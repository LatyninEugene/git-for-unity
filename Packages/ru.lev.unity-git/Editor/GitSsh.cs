using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;

namespace Lev.Git
{
    internal enum SshDiagnosisKind
    {
        /// <summary>Проверить нечем: нет утилит OpenSSH, или git запускает свой ssh (core.sshCommand, GIT_SSH).</summary>
        Unavailable,
        /// <summary>До сервера не достучаться.</summary>
        Network,
        /// <summary>Ключ сервера не совпал с known_hosts или сервера там ещё нет.</summary>
        HostKey,
        /// <summary>ssh с этими ключами входит — отказ не про ключ.</summary>
        AlreadyWorks,
        /// <summary>Сервер принимает ключ, но ssh сам его не предлагает: имя файла нестандартное.</summary>
        KeyNotOffered,
        /// <summary>Сервер принимает ключ, но он под паролем, а спросить пароль ssh негде.</summary>
        NeedsPassphrase,
        /// <summary>Ни один ключ из ~/.ssh сервер не принял.</summary>
        NoKeyAccepted
    }

    internal sealed class SshDiagnosis
    {
        public SshDiagnosisKind Kind;
        public string KeyPath;
        /// <summary>Путь ключа для показа: ~/.ssh/github.</summary>
        public string KeyName;
        /// <summary>Что показать человеку, если починить самим не вышло.</summary>
        public string Details;
    }

    /// <summary>
    /// Диагностика отказа SSH и свой ssh-agent на время сессии редактора.
    ///
    /// Почему git не входит, хотя ключ добавлен на сервер: ssh без настройки
    /// предлагает только ключи со стандартными именами (id_rsa, id_ed25519…),
    /// а ключ под паролем открыть не может — консоли, где спросить, у процесса
    /// из-под Unity нет. Оба случая решает агент: ключ добавляется в него один
    /// раз (пароль уходит в ssh-add через SSH_ASKPASS и переменную окружения
    /// дочернего процесса), а git находит агент по SSH_AUTH_SOCK.
    ///
    /// Пароль нигде не сохраняется и не пишется в журнал. Агент живёт, пока
    /// открыт редактор: при выходе он останавливается вместе с ключами.
    /// </summary>
    [InitializeOnLoad]
    internal static class GitSsh
    {
        /// <summary>Агент запущен плагином — его и надо остановить при выходе, чужой не трогаем.</summary>
        private const string OwnAgentVariable = "LEV_GIT_SSH_AGENT";

        private const int MaxProbedKeys = 12;

        private static string _toolsDir;
        private static bool _toolsResolved;

        static GitSsh()
        {
            EditorApplication.quitting += StopOwnAgent;
        }

        private static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>
        /// Передаёт процессу адрес агента. Переменные окружения редактора и так
        /// наследуются, но явная передача не зависит от того, когда рантайм
        /// снимает копию окружения.
        /// </summary>
        internal static void ApplyAgent(ProcessStartInfo psi)
        {
            var sock = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
            if (string.IsNullOrEmpty(sock)) return;
            psi.EnvironmentVariables["SSH_AUTH_SOCK"] = sock;

            var pid = Environment.GetEnvironmentVariable("SSH_AGENT_PID");
            if (!string.IsNullOrEmpty(pid)) psi.EnvironmentVariables["SSH_AGENT_PID"] = pid;
        }

        // ------------------------------------------------------ диагностика ---

        public static async Task<SshDiagnosis> DiagnoseAsync(GitRemote remote)
        {
            var d = new SshDiagnosis { Kind = SshDiagnosisKind.Unavailable };
            if (remote == null || !remote.IsSsh || string.IsNullOrEmpty(remote.Host)) return d;
            if (Tool("ssh") == null || await HasCustomSshAsync()) return d;

            var first = await ProbeAsync(remote, null);
            var probe = SshOutput.ParseProbe(first.StdErr);
            if (!probe.Authenticated && (probe.ConnectionFailed || first.TimedOut))
            {
                // git только что достучался до этого сервера — сбой соединения скорее случайный.
                await Task.Delay(1500);
                first = await ProbeAsync(remote, null);
                probe = SshOutput.ParseProbe(first.StdErr);
            }

            if (probe.Authenticated)
            {
                d.Kind = SshDiagnosisKind.AlreadyWorks;
                return d;
            }

            if (probe.HostKeyFailed)
            {
                d.Kind = SshDiagnosisKind.HostKey;
                d.Details = L.F("The host key of {0} is not in known_hosts or does not match it. Connect once from a terminal and check the key: ssh -T {1}",
                                remote.Host, Target(remote));
                return d;
            }

            if (probe.ConnectionFailed || first.TimedOut)
            {
                d.Kind = SshDiagnosisKind.Network;
                d.Details = L.F("Couldn't connect to {0} over SSH: {1}", remote.Host,
                                string.Join("\n", GitOutput.LastLines(first.StdErr, 2)));
                return d;
            }

            var home = Home();

            // Ключ со стандартным именем сервер знает, но войти не вышло — он под паролем.
            if (probe.AcceptedKey != null && !probe.AcceptedFromAgent)
                return await FoundAsync(d, probe.AcceptedKey, home);

            var keys = LocalKeys(home);
            var listing = new List<string>();
            int probed = 0;

            foreach (var key in keys)
            {
                bool offered = probe.Offered.Exists(o => SshOutput.SamePath(o, key));
                if (!offered && probed < MaxProbedKeys)
                {
                    probed++;
                    // Проверка только открытой частью ключа: пароль для этого не нужен.
                    var r = await ProbeAsync(remote, key);
                    var kp = SshOutput.ParseProbe(r.StdErr);
                    if (!kp.Authenticated && (kp.ConnectionFailed || r.TimedOut))
                    {
                        await Task.Delay(1500);
                        r = await ProbeAsync(remote, key);
                        kp = SshOutput.ParseProbe(r.StdErr);

                        // Без соединения «ключ не принят» было бы неправдой.
                        if (!kp.Authenticated && (kp.ConnectionFailed || r.TimedOut))
                        {
                            d.Kind = SshDiagnosisKind.Network;
                            d.Details = L.F("Couldn't connect to {0} over SSH: {1}", remote.Host,
                                            string.Join("\n", GitOutput.LastLines(r.StdErr, 2)));
                            return d;
                        }
                    }

                    if (kp.Authenticated)
                    {
                        d.Kind = SshDiagnosisKind.KeyNotOffered;
                        d.KeyPath = key;
                        d.KeyName = SshOutput.DisplayPath(key, home);
                        return d;
                    }

                    if (kp.AcceptedKey != null)
                        return await FoundAsync(d, kp.AcceptedFromAgent ? key : kp.AcceptedKey, home);
                }

                var fp = await RunToolAsync(Tool("ssh-keygen"), "-lf " + Quote(key + ".pub"), null, 10000);
                listing.Add(SshOutput.DisplayPath(key, home) + "   " + SshOutput.Fingerprint(fp.StdOut));
            }

            d.Kind = SshDiagnosisKind.NoKeyAccepted;
            d.Details = keys.Count == 0
                ? L.F("There are no SSH keys in {0} (a key file with a matching .pub next to it).",
                      SshOutput.DisplayPath(Path.Combine(home, ".ssh"), home))
                : L.F("{0} accepted none of the local keys:", remote.Host) + "\n" + string.Join("\n", listing) + "\n\n" +
                  L.T("Compare the fingerprints with the keys on the server's SSH keys page.");
            return d;
        }

        private static async Task<SshDiagnosis> FoundAsync(SshDiagnosis d, string key, string home)
        {
            d.KeyPath = key;
            d.KeyName = SshOutput.DisplayPath(key, home);
            d.Kind = await IsEncryptedAsync(key) ? SshDiagnosisKind.NeedsPassphrase : SshDiagnosisKind.KeyNotOffered;
            return d;
        }

        private static async Task<ProcessResult> ProbeAsync(GitRemote remote, string keyPath)
        {
            // BatchMode: ssh не ждёт ввода — ни пароля, ни подтверждения ключа сервера.
            // Остальное — как у git, без своих таймаутов соединения: проверка должна
            // вести себя так же, как настоящий push.
            var args = "-v -o BatchMode=yes";
            if (keyPath != null) args += " -o IdentitiesOnly=yes -i " + Quote(keyPath);
            args += " -T " + Target(remote);

            var r = await RunToolAsync(Tool("ssh"), args, null, 45000);

            // Проверка идёт мимо журнала команд git, поэтому итог пишется сюда — иначе
            // по отчёту не понять, почему диагностика пришла к своему выводу.
            var lines = SshOutput.DiagnosticLines(r.StdErr);
            Diagnostics.Journal.Info("SSH check (" + (keyPath == null ? "default keys" : Path.GetFileName(keyPath)) + "): exit " +
                                     r.ExitCode + ", " + r.DurationMs + " ms" + (r.TimedOut ? ", timed out" : string.Empty) +
                                     (lines.Count > 0 ? "\n  " + string.Join("\n  ", lines) : string.Empty));
            return r;
        }

        private static string Target(GitRemote remote)
        {
            return (remote.Port > 0 ? "-p " + remote.Port + " " : string.Empty) +
                   SshOutput.UserFromUrl(remote.Raw) + "@" + remote.Host;
        }

        private static async Task<bool> IsEncryptedAsync(string keyPath)
        {
            // С пустым паролем ключ открывается — значит, пароля нет. Вывод (открытый ключ) не нужен.
            var r = await RunToolAsync(Tool("ssh-keygen"), "-y -P \"\" -f " + Quote(keyPath), null, 10000);
            return !r.Ok;
        }

        /// <summary>Свой ssh у git — наш агент и наша проверка к нему отношения не имеют.</summary>
        private static async Task<bool> HasCustomSshAsync()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH_COMMAND")))
                return true;

            var r = await GitProcess.RunAsync(GitRepository.GitExe, "config --get core.sshCommand",
                                              GitRepository.RepoRoot, null, 15000, default, null, true);
            return r.Ok && !string.IsNullOrWhiteSpace(r.StdOut);
        }

        private static string Home()
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home) || !Directory.Exists(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home;
        }

        /// <summary>Закрытые ключи в ~/.ssh: файлы, рядом с которыми лежит .pub.</summary>
        private static List<string> LocalKeys(string home)
        {
            var keys = new List<string>();
            var dir = Path.Combine(home, ".ssh");
            try
            {
                if (!Directory.Exists(dir)) return keys;
                foreach (var file in Directory.GetFiles(dir))
                    if (!file.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) && File.Exists(file + ".pub"))
                        keys.Add(file);
            }
            catch (Exception e)
            {
                Diagnostics.Journal.Warn("SSH keys folder was not read: " + e.Message);
            }

            keys.Sort(StringComparer.OrdinalIgnoreCase);
            return keys;
        }

        // ------------------------------------------------------------ агент ---

        /// <summary>
        /// Добавляет ключ в ssh-agent, при необходимости запустив его.
        /// <paramref name="passphrase"/> null — ключ без пароля.
        /// </summary>
        public static async Task<ProcessResult> AddKeyAsync(string keyPath, string passphrase)
        {
            if (!await EnsureAgentAsync())
                return new ProcessResult { ExitCode = -1, StdErr = L.T("Couldn't start ssh-agent.") };

            var env = new Dictionary<string, string>();
            var askpass = WriteAskpass();
            if (askpass != null)
            {
                env["SSH_ASKPASS"] = askpass;
                env["SSH_ASKPASS_REQUIRE"] = "force";
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) env["DISPLAY"] = "none";
                // Пароль — только в окружении дочернего ssh-add: не в аргументах, не в файле, не в журнале.
                env[SshOutput.AskpassVariable] = passphrase ?? string.Empty;
            }

            var r = await RunToolAsync(Tool("ssh-add"), Quote(keyPath), env, 20000);
            env.Clear();

            if (r.Ok) Diagnostics.Journal.Info("SSH key added to ssh-agent: " + Path.GetFileName(keyPath));
            return r;
        }

        private static async Task<bool> EnsureAgentAsync()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_AUTH_SOCK")))
            {
                // 0 — в агенте есть ключи, 1 — агент пуст, 2 — до агента не достучаться.
                var list = await RunToolAsync(Tool("ssh-add"), "-l", null, 10000);
                if (list.ExitCode == 0 || list.ExitCode == 1) return true;
            }

            var start = await RunToolAsync(Tool("ssh-agent"), "-s", null, 10000);
            string socket, pid;
            if (!start.Ok || !SshOutput.ParseAgent(start.StdOut, out socket, out pid))
            {
                Diagnostics.Journal.Warn("ssh-agent did not start: " + start.Message);
                return false;
            }

            // Окружение процесса редактора переживает перезагрузку домена, а все
            // запуски git наследуют его — так ключ виден и терминалу, и LFS.
            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", socket);
            Environment.SetEnvironmentVariable("SSH_AGENT_PID", pid);
            Environment.SetEnvironmentVariable(OwnAgentVariable, pid);
            Diagnostics.Journal.Info("Started ssh-agent for this editor session");
            return true;
        }

        private static void StopOwnAgent()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OwnAgentVariable))) return;

            var agent = Tool("ssh-agent");
            if (agent != null) RunTool(agent, "-k", null, 5000);

            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", null);
            Environment.SetEnvironmentVariable("SSH_AGENT_PID", null);
            Environment.SetEnvironmentVariable(OwnAgentVariable, null);
        }

        /// <summary>Скрипт для SSH_ASKPASS в папке данных пользователя (не в общем /tmp). Секрета в нём нет.</summary>
        private static string WriteAskpass()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LevGit");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "ssh-askpass.sh");
                File.WriteAllText(path, SshOutput.AskpassScript, new UTF8Encoding(false));
                if (!IsWindows) RunTool("chmod", "700 " + Quote(path), null, 5000);
                return path;
            }
            catch (Exception e)
            {
                Diagnostics.Journal.Warn("SSH askpass script was not written: " + e.Message);
                return null;
            }
        }

        // ------------------------------------------------------- процессы ---

        /// <summary>
        /// Утилита OpenSSH. Под Windows — из поставки Git for Windows: git запускает
        /// именно её, и агент у неё свой (системная служба OpenSSH ей не видна).
        /// null — утилит нет.
        /// </summary>
        private static string Tool(string name)
        {
            if (!IsWindows) return name;

            if (!_toolsResolved)
            {
                _toolsResolved = true;
                var r = GitProcess.Run(GitRepository.GitExe, "--exec-path", null, null, 15000, default, null, true);
                if (r.Ok)
                {
                    try
                    {
                        // <Git>/mingw64/libexec/git-core → <Git>/usr/bin
                        var root = new DirectoryInfo(r.StdOut.Trim()).Parent?.Parent?.Parent;
                        var bin = root == null ? null : Path.Combine(root.FullName, "usr", "bin");
                        if (bin != null && File.Exists(Path.Combine(bin, "ssh.exe"))) _toolsDir = bin;
                    }
                    catch (Exception e)
                    {
                        Diagnostics.Journal.Warn("OpenSSH tools were not found next to git: " + e.Message);
                    }
                }
            }

            return _toolsDir == null ? null : Path.Combine(_toolsDir, name + ".exe");
        }

        private static string Quote(string s)
        {
            return "\"" + s + "\"";
        }

        private static Task<ProcessResult> RunToolAsync(string exe, string arguments, IDictionary<string, string> env, int timeoutMs)
        {
            return Task.Run(() => RunTool(exe, arguments, env, timeoutMs));
        }

        /// <summary>
        /// Запуск утилиты ssh мимо журнала команд git: там ей не место, а окружение
        /// ssh-add несёт пароль ключа. stdin закрыт сразу — ждать ввода некому.
        /// </summary>
        private static ProcessResult RunTool(string exe, string arguments, IDictionary<string, string> env, int timeoutMs)
        {
            var result = new ProcessResult();
            if (exe == null)
            {
                result.ExitCode = -1;
                result.StdErr = L.T("OpenSSH tools were not found.");
                return result;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            psi.EnvironmentVariables["LC_ALL"] = "C";
            ApplyAgent(psi);
            if (env != null)
                foreach (var pair in env)
                    psi.EnvironmentVariables[pair.Key] = pair.Value;

            var sw = Stopwatch.StartNew();
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    p.Start();
                    p.StandardInput.Close();

                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();

                    if (p.WaitForExit(timeoutMs))
                    {
                        result.ExitCode = p.ExitCode;
                    }
                    else
                    {
                        try { p.Kill(); } catch { /* уже завершился */ }
                        result.TimedOut = true;
                        result.ExitCode = -1;
                    }

                    // ssh-agent уходит в фон; ждать закрытия его труб бесконечно нельзя.
                    result.StdOut = Wait(outTask);
                    result.StdErr = Wait(errTask);
                    if (result.TimedOut)
                        result.StdErr += "\n" + L.F("{0} did not finish in {1} s.", Path.GetFileName(exe), timeoutMs / 1000);
                }
            }
            catch (Exception e)
            {
                result.ExitCode = -1;
                result.StdErr = L.F("Failed to start '{0}': {1}", Path.GetFileName(exe), e.Message);
            }

            result.DurationMs = sw.ElapsedMilliseconds;
            return result;
        }

        private static string Wait(Task<string> task)
        {
            try { return task.Wait(3000) ? task.Result ?? string.Empty : string.Empty; }
            catch { return string.Empty; }
        }
    }
}
