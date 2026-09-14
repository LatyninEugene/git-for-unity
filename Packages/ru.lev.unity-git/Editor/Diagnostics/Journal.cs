using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.Diagnostics
{
    internal enum JournalLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    internal sealed class JournalEntry
    {
        public DateTime Time;
        public JournalLevel Level;
        public string Message;
    }

    /// <summary>
    /// Журнал пакета на диске: Library/LevGit/Logs, файл на день.
    ///
    /// Нужен для отчёта о проблеме: когда человек пишет «не работает», по
    /// журналу видно, какие команды git шли, чем кончились и что упало.
    /// Консоль Unity для этого не годится — её чистят, и она не переживает
    /// перезапуск.
    ///
    /// Всё, что сюда попадает, проходит <see cref="Redactor.Clean"/>. Пишется
    /// из любого потока: строки копятся в памяти и сбрасываются на диск в
    /// главном цикле редактора, перед перезагрузкой домена и при выходе.
    /// </summary>
    [InitializeOnLoad]
    internal static class Journal
    {
        public const int KeepDays = 7;
        public const long MaxBytes = 20L * 1024 * 1024;

        private const string VerboseKey = "LevGit.Journal.Verbose";
        private const int RecentCapacity = 300;
        private const int FlushThreshold = 64 * 1024;

        private static readonly object Sync = new object();
        private static readonly StringBuilder Pending = new StringBuilder();
        private static readonly Queue<JournalEntry> Recent = new Queue<JournalEntry>();

        // Своё же сообщение в консоли не надо записывать второй раз.
        [ThreadStatic] private static bool _toConsole;

        private static volatile bool _verbose;

        static Journal()
        {
            Directory = Path.GetFullPath(Path.Combine("Library", "LevGit", "Logs"));
            _verbose = SessionState.GetBool(VerboseKey, false);

            Cleanup();

            Application.logMessageReceivedThreaded += OnConsoleMessage;
            EditorApplication.update += Flush;
            AssemblyReloadEvents.beforeAssemblyReload += Flush;
            EditorApplication.quitting += Flush;

            Write(JournalLevel.Info, "Editor loaded: Unity " + Application.unityVersion + ", " +
                                     SystemInfo.operatingSystem + ", Git for Unity " + PackageMeta.Version +
                                     ", language " + L.Language);
        }

        /// <summary>Папка журнала.</summary>
        public static string Directory { get; private set; }

        /// <summary>Подробный журнал: успешные фоновые команды git и отладочные записи. До перезапуска редактора.</summary>
        public static bool Verbose
        {
            get { return _verbose; }
            set
            {
                if (_verbose == value) return;
                _verbose = value;
                SessionState.SetBool(VerboseKey, value);
                Write(JournalLevel.Info, value ? "Verbose logging on" : "Verbose logging off");
            }
        }

        // ------------------------------------------------------------ запись ---

        /// <summary>Только в подробном журнале.</summary>
        public static void Debug(string message)
        {
            if (_verbose) Write(JournalLevel.Debug, message);
        }

        /// <summary>В журнал, без консоли.</summary>
        public static void Info(string message)
        {
            Write(JournalLevel.Info, message);
        }

        /// <summary>В журнал и в консоль обычным сообщением — то, что человек сделал и хочет увидеть.</summary>
        public static void Notice(string message)
        {
            Write(JournalLevel.Info, message);
            ToConsole(LogType.Log, message);
        }

        /// <summary>В журнал и в консоль предупреждением.</summary>
        public static void Warn(string message)
        {
            Write(JournalLevel.Warning, message);
            ToConsole(LogType.Warning, message);
        }

        /// <summary>В журнал и в консоль ошибкой.</summary>
        public static void Error(string message)
        {
            Write(JournalLevel.Error, message);
            ToConsole(LogType.Error, message);
        }

        /// <summary>Исключение со стеком — в журнал, и в консоль как исключение.</summary>
        public static void Exception(Exception e, string context = null)
        {
            if (e == null) return;
            Write(JournalLevel.Error, (string.IsNullOrEmpty(context) ? string.Empty : context + ": ") + e);

            _toConsole = true;
            try { UnityEngine.Debug.LogException(e); }
            finally { _toConsole = false; }
        }

        /// <summary>Выполненная команда git. Успешные фоновые — только в подробном журнале.</summary>
        internal static void Git(GitCommandRecord r)
        {
            if (r == null) return;
            bool failed = r.Failed;
            if (!failed && r.Background && !_verbose) return;

            var sb = new StringBuilder();
            sb.Append("git ").Append(r.Args).Append(" → ").Append(r.ExitCode).Append(", ").Append(r.DurationMs).Append(" ms");
            if (r.Background) sb.Append(" (background)");

            // Последние строки, а не первые: причину ошибки git печатает в конце.
            if (failed && !string.IsNullOrEmpty(r.Output))
                foreach (var line in GitOutput.LastLines(r.Output, 12))
                    sb.Append("\n    ").Append(line);

            // Этапы — у сбоя и у долгой команды: именно там встаёт вопрос, где ушло время.
            if (!string.IsNullOrEmpty(r.Timeline) && (failed || r.DurationMs >= GitTimeline.SlowMs))
            {
                sb.Append("\n    stages:");
                foreach (var line in r.Timeline.Split('\n'))
                    if (line.Trim().Length > 0) sb.Append("\n      ").Append(line.TrimEnd());
            }

            Write(failed ? JournalLevel.Warning : JournalLevel.Debug, sb.ToString());
        }

        // ------------------------------------------------------------ чтение ---

        /// <summary>Последние предупреждения и ошибки этой сессии — для отчёта.</summary>
        public static List<JournalEntry> RecentProblems()
        {
            lock (Sync)
            {
                var list = new List<JournalEntry>();
                foreach (var e in Recent)
                    if (e.Level >= JournalLevel.Warning) list.Add(e);
                return list;
            }
        }

        /// <summary>Файлы журнала за последние дни, от старых к новым.</summary>
        public static List<string> Files(int days)
        {
            Flush();
            var result = new List<string>();
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return result;
                var since = DateTime.Now.Date.AddDays(-Math.Max(0, days - 1));
                foreach (var f in System.IO.Directory.GetFiles(Directory, "levgit-*.log"))
                    if (File.GetLastWriteTime(f) >= since) result.Add(f);
                result.Sort(StringComparer.Ordinal);
            }
            catch { }
            return result;
        }

        public static void Flush()
        {
            string text;
            lock (Sync)
            {
                if (Pending.Length == 0) return;
                text = Pending.ToString();
                Pending.Length = 0;
            }

            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(Path.Combine(Directory, "levgit-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                                   text, new UTF8Encoding(false));
            }
            catch
            {
                // Журнал — помощник, а не причина ронять редактор: диск занят — пропускаем.
            }
        }

        // --------------------------------------------------------- служебное ---

        private static void Write(JournalLevel level, string message)
        {
            if (message == null) return;
            message = Redactor.Clean(message);
            var now = DateTime.Now;

            bool flushNow;
            lock (Sync)
            {
                Pending.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
                       .Append(Letter(level)).Append(' ')
                       .Append(message.Replace("\n", "\n    ")).Append('\n');

                if (level != JournalLevel.Debug)
                {
                    Recent.Enqueue(new JournalEntry { Time = now, Level = level, Message = message });
                    while (Recent.Count > RecentCapacity) Recent.Dequeue();
                }

                flushNow = Pending.Length > FlushThreshold;
            }

            if (flushNow) Flush();
        }

        private static void ToConsole(LogType type, string message)
        {
            _toConsole = true;
            try
            {
                var text = "[Git] " + message;
                if (type == LogType.Error) UnityEngine.Debug.LogError(text);
                else if (type == LogType.Warning) UnityEngine.Debug.LogWarning(text);
                else UnityEngine.Debug.Log(text);
            }
            finally
            {
                _toConsole = false;
            }
        }

        /// <summary>
        /// Ошибки из кода пакета, которые прошли мимо журнала: необработанные
        /// исключения в обработчиках интерфейса, ошибки чужого кода в наших
        /// окнах. Узнаются по стеку.
        /// </summary>
        private static void OnConsoleMessage(string condition, string stackTrace, LogType type)
        {
            if (_toConsole) return;
            if (type == LogType.Log) return;

            bool ours = (stackTrace != null && stackTrace.IndexOf("Lev.Git", StringComparison.Ordinal) >= 0) ||
                        (condition != null && condition.StartsWith("[Git", StringComparison.Ordinal));
            if (!ours) return;

            var sb = new StringBuilder("Console " + type + ": " + condition);
            if (type != LogType.Warning && !string.IsNullOrEmpty(stackTrace))
            {
                int lines = 0;
                foreach (var line in stackTrace.Split('\n'))
                {
                    if (line.Trim().Length == 0) continue;
                    sb.Append('\n').Append(line.TrimEnd());
                    if (++lines == 12) break;
                }
            }

            Write(type == LogType.Warning ? JournalLevel.Warning : JournalLevel.Error, sb.ToString());
        }

        private static void Cleanup()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return;

                var files = new List<FileInfo>();
                foreach (var f in new DirectoryInfo(Directory).GetFiles("levgit-*.log")) files.Add(f);
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

                var cutoff = DateTime.Now.AddDays(-KeepDays);
                long total = 0;
                foreach (var f in files)
                {
                    total += f.Length;
                    if (f.LastWriteTime < cutoff || total > MaxBytes) f.Delete();
                }
            }
            catch { }
        }

        private static char Letter(JournalLevel level)
        {
            switch (level)
            {
                case JournalLevel.Debug: return 'D';
                case JournalLevel.Warning: return 'W';
                case JournalLevel.Error: return 'E';
                default: return 'I';
            }
        }
    }

    /// <summary>Сведения о самом пакете.</summary>
    internal static class PackageMeta
    {
        private static string _version;

        public const string Name = "ru.lev.unity-git";

        public static string Version
        {
            get
            {
                if (_version != null) return _version;
                try
                {
                    // Неудачный ответ не запоминаем: во время импорта пакета Package
                    // Manager его ещё не знает, а через секунду уже знает.
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PackageMeta).Assembly);
                    _version = info != null ? info.version : FromPackageJson();
                }
                catch
                {
                    _version = null;
                }
                return _version ?? "unknown";
            }
        }

        /// <summary>Установлен из .unitypackage в Assets — не пакетом: версию берём из package.json рядом с папкой Editor.</summary>
        private static string FromPackageJson()
        {
            try
            {
                var asmdef = UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("Lev.Git.Editor");
                if (string.IsNullOrEmpty(asmdef)) return null;
                var json = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(asmdef))), "package.json");
                if (!File.Exists(json)) return null;
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(json), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value + " (in Assets)" : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
