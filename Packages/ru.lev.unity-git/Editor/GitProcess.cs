using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lev.Git
{
    /// <summary>Результат запуска внешнего процесса.</summary>
    public sealed class ProcessResult
    {
        public int ExitCode;
        public string StdOut = string.Empty;
        public string StdErr = string.Empty;
        public bool Canceled;
        public bool TimedOut;
        public long DurationMs;

        public bool Ok => ExitCode == 0;

        /// <summary>Текст для показа пользователю: stderr, если он есть, иначе stdout. Без строк прогресса git.</summary>
        public string Message
        {
            get
            {
                if (Canceled) return L.T("Operation canceled.");
                var s = GitOutput.WithoutProgress(string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdErr);
                return string.IsNullOrWhiteSpace(s) ? L.F("exit code {0}", ExitCode) : s.Trim();
            }
        }
    }

    /// <summary>
    /// Запуск git как внешнего процесса.
    ///
    /// Тонкости, из-за которых нельзя написать это «в лоб»:
    ///  * stdout и stderr читаются параллельно с ожиданием выхода, иначе процесс
    ///    заблокируется на заполненном буфере пайпа;
    ///  * stdout читается целиком, а не построчно — иначе ломается формат -z (NUL);
    ///  * stderr читается посимвольно: git печатает прогресс через \r без перевода
    ///    строки, и построчное чтение не выдаст ни одного события до самого конца;
    ///  * кодировка принудительно UTF-8, иначе кириллица в путях превращается в мусор;
    ///  * интерактивные запросы пароля запрещены, иначе редактор повиснет на
    ///    невидимом окне ввода.
    /// </summary>
    public static class GitProcess
    {
        public const int DefaultTimeoutMs = 120000;

        public static Task<ProcessResult> RunAsync(
            string exe, string arguments, string workingDirectory,
            string stdin = null, int timeoutMs = DefaultTimeoutMs,
            CancellationToken cancellation = default(CancellationToken),
            Action<string> onProgress = null, bool background = false, bool secret = false,
            IDictionary<string, string> environment = null, bool combinedOutput = false)
        {
            return Task.Run(() => Run(exe, arguments, workingDirectory, stdin, timeoutMs,
                                      cancellation, onProgress, background, secret, environment, combinedOutput));
        }

        /// <summary>
        /// Синхронный запуск. <paramref name="onProgress"/> вызывается из фонового
        /// потока — всё, что трогает API редактора, вызывающая сторона обязана
        /// перекладывать в главный поток сама.
        /// </summary>
        /// <param name="secret">
        /// Команда работает с учётными данными: её stdout в журнал не попадает.
        /// `git credential fill` печатает туда пароль открытым текстом, и без
        /// этого флага PAT оказался бы виден во вкладке «Консоль».
        /// </param>
        public static ProcessResult Run(
            string exe, string arguments, string workingDirectory,
            string stdin = null, int timeoutMs = DefaultTimeoutMs,
            CancellationToken cancellation = default(CancellationToken),
            Action<string> onProgress = null, bool background = false, bool secret = false,
            IDictionary<string, string> environment = null, bool combinedOutput = false)
        {
            // environment — дополнительные переменные окружения процесса.
            // combinedOutput — в журнал идут и stdout, и stderr: для команд, введённых
            // человеком, важно всё, что git напечатал.
            var result = new ProcessResult();
            var sw = Stopwatch.StartNew();

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            // Кодировку stdin обязательно задавать ДО Start(). Process.Start()
            // создаёт StandardInput и сразу ставит AutoFlush = true, а сеттер
            // AutoFlush вызывает Flush() — который записывает преамбулу кодировки
            // в трубу немедленно. Кодировка по умолчанию преамбулу имеет, и git
            // получает UTF-8 BOM перед первым же байтом: `credential fill` и
            // `credential approve` отвечают «refusing to work with credential
            // missing protocol field», а в сообщение коммита BOM попадает молча.
            // Писать потом в BaseStream уже поздно — BOM в потоке с момента старта.
            if (stdin != null) psi.StandardInputEncoding = new UTF8Encoding(false);

            // Никаких модальных запросов учётных данных из-под редактора.
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            // Сообщения git разбираются кодом, поэтому язык фиксируем.
            psi.EnvironmentVariables["LC_ALL"] = "C";
            // Ключи SSH, добавленные плагином в ssh-agent.
            GitSsh.ApplyAgent(psi);

            // Сетевые команды пишут разбивку по этапам (trace2): по одному итогу
            // «упало через 51 с» не понять, где ушло время — в хуке, в ssh или в передаче.
            // Трассировку, включённую самим пользователем, не перебиваем.
            string tracePath = null;
            if (IsGit(exe) && GitTimeline.ShouldTrace(arguments) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_TRACE2_EVENT")))
            {
                tracePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                   "levgit-trace2-" + Guid.NewGuid().ToString("N") + ".json");
                psi.EnvironmentVariables["GIT_TRACE2_EVENT"] = tracePath;
            }

            if (environment != null)
                foreach (var pair in environment)
                    psi.EnvironmentVariables[pair.Key] = pair.Value;

            Interlocked.Increment(ref GitCommandLog.Running);
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    p.Start();

                    // Чтение стартует ДО ожидания выхода — иначе дедлок на большом выводе.
                    Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                    Task<string> errTask = PumpStderrAsync(p.StandardError, onProgress);

                    if (stdin != null)
                    {
                        // Пишем готовыми байтами в поток, минуя кодировщик:
                        // кодировка уже задана без преамбулы, и здесь не остаётся
                        // ни одного места, где мог бы появиться лишний байт.
                        var payload = new UTF8Encoding(false).GetBytes(stdin);
                        var input = p.StandardInput.BaseStream;
                        input.Write(payload, 0, payload.Length);
                        input.Flush();
                        p.StandardInput.Close();
                    }

                    while (!p.WaitForExit(50))
                    {
                        if (cancellation.IsCancellationRequested)
                        {
                            TryKill(p);
                            result.Canceled = true;
                            break;
                        }
                        if (sw.ElapsedMilliseconds > timeoutMs)
                        {
                            TryKill(p);
                            result.TimedOut = true;
                            break;
                        }
                    }

                    // Убитый процесс закрывает пайпы, поэтому чтение здесь всегда завершается.
                    result.StdOut = SafeResult(outTask);
                    result.StdErr = SafeResult(errTask);

                    if (result.Canceled)
                    {
                        result.ExitCode = -1;
                        result.StdErr = L.T("Canceled by user.");
                    }
                    else if (result.TimedOut)
                    {
                        result.ExitCode = -1;
                        result.StdErr = L.F(
                            "The command did not finish in {0} ms: git {1}", timeoutMs, arguments);
                    }
                    else
                    {
                        result.ExitCode = p.ExitCode;
                    }
                }
            }
            catch (Exception e)
            {
                result.ExitCode = -1;
                result.StdErr = L.F(
                    "Failed to start '{0}': {1}. Make sure git is in PATH.", exe, e.Message);
            }
            finally
            {
                Interlocked.Decrement(ref GitCommandLog.Running);
            }

            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;

            var timeline = tracePath != null ? ReadTimeline(tracePath) : null;

            GitCommandLog.Add(new GitCommandRecord
            {
                Stamp = DateTime.Now.ToString("HH:mm:ss"),
                Args = arguments,
                Cwd = workingDirectory,
                ExitCode = result.ExitCode,
                DurationMs = result.DurationMs,
                // У секретных команд stdout не логируется никогда — он и содержит
                // учётные данные. Диагностика остаётся: ошибки идут в stderr.
                Output = secret
                    ? (string.IsNullOrWhiteSpace(result.StdErr)
                        ? L.T("‹output hidden: contains credentials›")
                        : result.StdErr)
                    : combinedOutput
                        ? Combine(result.StdOut, result.StdErr)
                        : (string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr),
                Background = background,
                Timeline = timeline
            });

            return result;
        }

        private static bool IsGit(string exe)
        {
            return string.Equals(System.IO.Path.GetFileNameWithoutExtension(exe ?? string.Empty), "git",
                                 StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Читает события trace2, сводит их в этапы и удаляет файл. Сбой чтения — тоже ответ, а не молчание.</summary>
        private static string ReadTimeline(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return null;

                string text;
                // Потомок, переживший git (ssh после обрыва), может ещё держать файл открытым.
                using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                                                             System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
                using (var reader = new System.IO.StreamReader(stream, new UTF8Encoding(false)))
                    text = reader.ReadToEnd();

                return GitTimeline.Summarize(text);
            }
            catch (Exception e)
            {
                return "stages unavailable: " + e.Message;
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch { /* временный файл, удалит система */ }
            }
        }

        /// <summary>
        /// Сохраняет вывод команды в файл БЕЗ декодирования.
        ///
        /// Обычный <see cref="Run"/> читает stdout как UTF-8 — для текста это
        /// правильно, а картинку или любой другой бинарник такое чтение портит
        /// безвозвратно. Здесь байты копируются из потока как есть.
        /// </summary>
        public static Task<bool> RunToFileAsync(
            string exe, string arguments, string workingDirectory, string destination,
            int timeoutMs = DefaultTimeoutMs, byte[] stdin = null)
        {
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory ?? string.Empty,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = stdin != null
                };

                psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
                psi.EnvironmentVariables["LC_ALL"] = "C";
                GitSsh.ApplyAgent(psi);

                // Та же ловушка, что и в Run: Process.Start создаёт StandardInput
                // с AutoFlush, а тот немедленно пишет преамбулу кодировки в трубу.
                // Указатель LFS с BOM перед «version» git-lfs не разбирает.
                if (stdin != null) psi.StandardInputEncoding = new UTF8Encoding(false);

                var sw = Stopwatch.StartNew();
                bool ok = false;
                string error = null;

                Interlocked.Increment(ref GitCommandLog.Running);
                try
                {
                    using (var p = new Process { StartInfo = psi })
                    {
                        p.Start();

                        using (var file = new System.IO.FileStream(
                            destination, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        {
                            var copy = p.StandardOutput.BaseStream.CopyToAsync(file);
                            var err = p.StandardError.ReadToEndAsync();

                            if (stdin != null)
                            {
                                var input = p.StandardInput.BaseStream;
                                input.Write(stdin, 0, stdin.Length);
                                input.Flush();
                                p.StandardInput.Close();
                            }

                            if (!p.WaitForExit(timeoutMs)) { TryKill(p); }
                            else
                            {
                                copy.GetAwaiter().GetResult();
                                error = SafeResult(err);
                                ok = p.ExitCode == 0;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    error = e.Message;
                }
                finally
                {
                    Interlocked.Decrement(ref GitCommandLog.Running);
                }

                sw.Stop();

                GitCommandLog.Add(new GitCommandRecord
                {
                    Stamp = DateTime.Now.ToString("HH:mm:ss"),
                    Args = arguments,
                    Cwd = workingDirectory,
                    ExitCode = ok ? 0 : -1,
                    DurationMs = sw.ElapsedMilliseconds,
                    Output = ok ? L.T("‹binary output saved to a file›") : error,
                    Background = true
                });

                return ok;
            });
        }

        /// <summary>
        /// Читает stderr посимвольно, отдавая наружу законченные строки.
        /// Разделителем считается и \n, и \r: прогресс git печатает вторым.
        /// </summary>
        private static Task<string> PumpStderrAsync(System.IO.StreamReader reader, Action<string> onProgress)
        {
            return Task.Run(() =>
            {
                var all = new StringBuilder();
                var line = new StringBuilder();
                var buf = new char[512];

                try
                {
                    int n;
                    while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            char c = buf[i];
                            all.Append(c);

                            if (c == '\r' || c == '\n')
                            {
                                if (line.Length > 0 && onProgress != null) onProgress(line.ToString());
                                line.Length = 0;
                            }
                            else
                            {
                                line.Append(c);
                            }
                        }
                    }
                }
                catch
                {
                    // Пайп закрыт убитым процессом — это штатное завершение чтения.
                }

                if (line.Length > 0 && onProgress != null) onProgress(line.ToString());
                return all.ToString();
            });
        }

        private static string Combine(string stdout, string stderr)
        {
            bool hasOut = !string.IsNullOrWhiteSpace(stdout);
            bool hasErr = !string.IsNullOrWhiteSpace(stderr);
            if (hasOut && hasErr) return stdout.TrimEnd() + "\n" + stderr.TrimEnd();
            return hasOut ? stdout : hasErr ? stderr : string.Empty;
        }

        private static string SafeResult(Task<string> t)
        {
            try { return t.GetAwaiter().GetResult() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static void TryKill(Process p)
        {
            try { if (!p.HasExited) p.Kill(); }
            catch { /* уже умер — ровно то, чего мы хотели */ }
        }
    }
}
