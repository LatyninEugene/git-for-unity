using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEditor;

namespace Lev.Git
{
    public enum GitFileStatus
    {
        None = 0,
        Untracked,
        Modified,
        Added,
        Renamed,
        Deleted,
        Conflicted
    }

    /// <summary>Одна строка в списке изменений. Ассет и его .meta схлопнуты в одну запись.</summary>
    public sealed class GitChange
    {
        public string ProjectPath;    // "Assets/Foo.cs" или "ProjectSettings/Bar.asset"
        public string OriginalPath;   // откуда переименовано, иначе null

        /// <summary>Худшее из индекса и рабочей копии — для бейджей и сортировки.</summary>
        public GitFileStatus Status;

        /// <summary>Состояние в индексе: что уйдёт в коммит прямо сейчас.</summary>
        public GitFileStatus IndexStatus;

        /// <summary>Состояние в рабочей копии: что в коммит пока не попадёт.</summary>
        public GitFileStatus WorkStatus;

        public bool HasMeta;          // рядом менялась ещё и .meta

        /// <summary>
        /// Менялся сам ассет, а не только его .meta. У меты папки — false:
        /// «ассетом» там оказывается каталог, которого git не хранит.
        /// </summary>
        public bool HasAsset;

        /// <summary>
        /// Что из пары лежит в индексе. Ассет и мета бывают в разном состоянии:
        /// файл добавлен в индекс, а мету Unity создал позже, и git её ещё не
        /// знает. Снимать из индекса можно только то, что там есть, — иначе
        /// `git restore --staged` откажет целиком на незнакомом пути.
        /// </summary>
        public bool AssetInIndex, MetaInIndex;

        /// <summary>Запись собрана из одной меты — у папки или у ассета, чей файл не менялся.</summary>
        public bool MetaOnly => HasMeta && !HasAsset;
        public bool Selected = true;

        public bool IsStaged => IndexStatus != GitFileStatus.None;
        public bool IsUnstaged => WorkStatus != GitFileStatus.None;

        /// <summary>Часть изменений в индексе, часть — нет. Для UI это отдельное состояние.</summary>
        public bool IsPartiallyStaged => IsStaged && IsUnstaged;

        public bool IsConflicted => Status == GitFileStatus.Conflicted;
    }

    /// <summary>
    /// Кэш git-статусов. Обновляется в фоне и никогда не дёргает git из OnGUI —
    /// иначе окно Project встанет колом на первом же большом проекте.
    ///
    /// Всё состояние снимается ОДНИМ вызовом git: `--porcelain=v2 --branch` отдаёт
    /// и состояние файлов, и текущую ветку, и upstream, и ahead/behind. Раньше на
    /// то же самое уходило три отдельных запуска процесса.
    /// </summary>
    public static class GitStatusCache
    {
        // Статусы отдельных файлов, ключ — путь относительно каталога проекта.
        private static Dictionary<string, GitFileStatus> _files = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
        // Агрегированные статусы папок (худший среди потомков).
        private static Dictionary<string, GitFileStatus> _folders = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
        private static List<GitChange> _changes = new List<GitChange>();

        private static bool _running;

        public static bool HasData { get; private set; }
        public static double LastRefreshTime { get; private set; }
        public static long LastRefreshMs { get; private set; }
        public static string LastError { get; private set; }

        // --- сведения о ветке, снятые тем же вызовом --------------------------
        public static string Branch { get; private set; }
        public static string Upstream { get; private set; }
        public static int Ahead { get; private set; }
        public static int Behind { get; private set; }
        public static bool IsDetached { get; private set; }
        public static bool IsInitialCommit { get; private set; }

        /// <summary>
        /// Коммит, на котором стоит HEAD. Достаётся тем же вызовом status и нужен
        /// как признак «прежняя версия файлов могла смениться»: любой коммит,
        /// checkout или pull его меняют, а обычная правка на диске — нет.
        /// </summary>
        public static string HeadOid { get; private set; }

        public static IReadOnlyList<GitChange> Changes => _changes;

        public static int StagedCount
        {
            get { int n = 0; foreach (var c in _changes) if (c.IsStaged) n++; return n; }
        }

        public static int ConflictCount
        {
            get { int n = 0; foreach (var c in _changes) if (c.IsConflicted) n++; return n; }
        }

        public static event Action Updated;

        /// <summary>Статус ассета с учётом парного .meta. Для папок — агрегат по содержимому.</summary>
        public static GitFileStatus GetStatus(string projectPath)
        {
            if (!HasData || string.IsNullOrEmpty(projectPath)) return GitFileStatus.None;

            GitFileStatus folder;
            if (_folders.TryGetValue(projectPath, out folder)) return folder;

            GitFileStatus best = GitFileStatus.None;
            GitFileStatus f;
            if (_files.TryGetValue(projectPath, out f)) best = Worse(best, f);
            if (_files.TryGetValue(projectPath + ".meta", out f)) best = Worse(best, f);
            return best;
        }

        private static GitFileStatus Worse(GitFileStatus a, GitFileStatus b)
        {
            return (int)a >= (int)b ? a : b;
        }

        public static async Task RefreshAsync()
        {
            if (_running) return;
            if (!GitRepository.IsRepo && !GitRepository.Locate())
            {
                HasData = false;
                LastError = L.T("The project directory is not inside a git repository.");
                Updated?.Invoke();
                return;
            }

            _running = true;
            var sw = Stopwatch.StartNew();
            try
            {
                // --no-optional-locks: без него `git status` обновляет stat-кэш индекса,
                //   то есть пишет в .git/index — а за индексом следит наблюдатель, и
                //   получается бесконечный цикл «статус → событие → статус».
                // --porcelain=v2 --branch: состояние файлов и ветки за один запуск.
                // -z: разделитель NUL, без экранирования и кавычек — единственный
                //   формат, устойчивый к пробелам и кириллице в путях.
                var r = await GitProcess.RunAsync(
                    GitRepository.GitExe,
                    "--no-optional-locks status --porcelain=v2 --branch -z --untracked-files=all",
                    GitRepository.RepoRoot,
                    null, GitProcess.DefaultTimeoutMs,
                    default(System.Threading.CancellationToken), null,
                    background: true);

                if (!r.Ok)
                {
                    LastError = r.Message;
                    HasData = false;
                    return;
                }

                LastError = null;
                Parse(r.StdOut);
                HasData = true;
                LastRefreshTime = EditorApplication.timeSinceStartup;
            }
            finally
            {
                sw.Stop();
                LastRefreshMs = sw.ElapsedMilliseconds;
                _running = false;
                Updated?.Invoke();
                EditorApplication.RepaintProjectWindow();
            }
        }

        // ------------------------------------------------------------ parsing ---

        private static void Parse(string raw)
        {
            var files = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
            var folders = new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<Entry>();

            Branch = null; Upstream = null; Ahead = 0; Behind = 0;
            IsDetached = false; IsInitialCommit = false; HeadOid = null;

            var fields = raw.Split('\0');
            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f.Length == 0) continue;

                if (f[0] == '#') { ParseHeader(f); continue; }

                switch (f[0])
                {
                    case '1': // обычное изменение: 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
                    {
                        var path = TailAfter(f, 8);
                        if (path != null) entries.Add(MakeEntry(f[2], f[3], path, null));
                        break;
                    }

                    case '2': // переименование: та же голова плюс <X><score>, а исходный путь
                              // при -z приходит ОТДЕЛЬНЫМ полем — иначе его не отличить от
                              // пути с пробелом.
                    {
                        var path = TailAfter(f, 9);
                        string original = (i + 1 < fields.Length) ? fields[++i] : null;
                        if (path != null) entries.Add(MakeEntry(f[2], f[3], path, original));
                        break;
                    }

                    case 'u': // конфликт: u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
                    {
                        var path = TailAfter(f, 10);
                        if (path != null)
                            entries.Add(new Entry
                            {
                                Path = path,
                                Index = GitFileStatus.Conflicted,
                                Work = GitFileStatus.Conflicted
                            });
                        break;
                    }

                    case '?': // неотслеживаемый
                        if (f.Length > 2)
                            entries.Add(new Entry
                            {
                                Path = f.Substring(2),
                                Index = GitFileStatus.None,
                                Work = GitFileStatus.Untracked
                            });
                        break;

                    case '!': // игнорируемый — в списке изменений ему делать нечего
                        break;
                }
            }

            foreach (var e in entries)
            {
                Record(files, folders, e.Path, Worse(e.Index, e.Work));
                if (e.Original != null) Record(files, folders, e.Original, GitFileStatus.Renamed);
            }

            _files = files;
            _folders = folders;
            _changes = BuildChanges(entries);

            // Ветку держим в одном месте, чтобы окно не запускало git ещё раз.
            GitRepository.Upstream = Upstream;
        }

        private struct Entry
        {
            public string Path;
            public string Original;
            public GitFileStatus Index;
            public GitFileStatus Work;
        }

        private static Entry MakeEntry(char x, char y, string path, string original)
        {
            return new Entry
            {
                Path = path,
                Original = original,
                Index = FromCode(x),
                Work = FromCode(y)
            };
        }

        private static GitFileStatus FromCode(char c)
        {
            switch (c)
            {
                case '.': return GitFileStatus.None;
                case 'M': return GitFileStatus.Modified;
                case 'T': return GitFileStatus.Modified;   // сменился тип записи — для нас просто правка
                case 'A': return GitFileStatus.Added;
                case 'D': return GitFileStatus.Deleted;
                case 'R': return GitFileStatus.Renamed;
                case 'C': return GitFileStatus.Renamed;    // копия — тот же случай для UI
                case 'U': return GitFileStatus.Conflicted;
                default: return GitFileStatus.None;
            }
        }

        private static void ParseHeader(string line)
        {
            // # branch.oid <oid> | (initial)
            // # branch.head <name> | (detached)
            // # branch.upstream <name>
            // # branch.ab +<ahead> -<behind>
            if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                IsInitialCommit = line.EndsWith("(initial)", StringComparison.Ordinal);
                if (!IsInitialCommit) HeadOid = line.Substring("# branch.oid ".Length);
            }
            else if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var v = line.Substring("# branch.head ".Length);
                IsDetached = v == "(detached)";
                Branch = IsDetached ? null : v;
            }
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                Upstream = line.Substring("# branch.upstream ".Length);
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = line.Substring("# branch.ab ".Length).Split(' ');
                if (parts.Length >= 2)
                {
                    int a, b;
                    if (int.TryParse(parts[0].TrimStart('+'), out a)) Ahead = a;
                    if (int.TryParse(parts[1].TrimStart('-'), out b)) Behind = b;
                }
            }
        }

        /// <summary>
        /// Возвращает всё, что идёт после <paramref name="fieldCount"/> полей,
        /// разделённых пробелом. Путь может содержать пробелы, поэтому его нельзя
        /// получить обычным Split — только отсчётом фиксированной головы записи.
        /// </summary>
        private static string TailAfter(string s, int fieldCount)
        {
            int pos = 0;
            for (int i = 0; i < fieldCount; i++)
            {
                int sp = s.IndexOf(' ', pos);
                if (sp < 0) return null;
                pos = sp + 1;
            }
            return pos < s.Length ? s.Substring(pos) : null;
        }

        private static void Record(
            Dictionary<string, GitFileStatus> files,
            Dictionary<string, GitFileStatus> folders,
            string gitPath, GitFileStatus status)
        {
            var projectPath = GitRepository.ToProjectPath(gitPath);
            if (string.IsNullOrEmpty(projectPath)) return;

            projectPath = projectPath.TrimEnd('/');

            GitFileStatus existing;
            files[projectPath] = files.TryGetValue(projectPath, out existing) ? Worse(existing, status) : status;

            // Поднимаем статус вверх по дереву каталогов.
            int slash = projectPath.LastIndexOf('/');
            while (slash > 0)
            {
                var dir = projectPath.Substring(0, slash);
                GitFileStatus cur;
                folders[dir] = folders.TryGetValue(dir, out cur) ? Worse(cur, status) : status;
                slash = dir.LastIndexOf('/');
            }
        }

        private static List<GitChange> BuildChanges(List<Entry> entries)
        {
            var byAsset = new Dictionary<string, GitChange>(StringComparer.OrdinalIgnoreCase);

            foreach (var e in entries)
            {
                var projectPath = GitRepository.ToProjectPath(e.Path);
                if (string.IsNullOrEmpty(projectPath)) continue;

                bool isMeta = projectPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
                var key = isMeta ? projectPath.Substring(0, projectPath.Length - ".meta".Length) : projectPath;

                GitChange change;
                if (!byAsset.TryGetValue(key, out change))
                {
                    change = new GitChange { ProjectPath = key };
                    byAsset[key] = change;
                }

                change.IndexStatus = Worse(change.IndexStatus, e.Index);
                change.WorkStatus = Worse(change.WorkStatus, e.Work);
                change.Status = Worse(change.IndexStatus, change.WorkStatus);

                if (isMeta) change.HasMeta = true;
                else change.HasAsset = true;

                if (e.Index != GitFileStatus.None)
                {
                    if (isMeta) change.MetaInIndex = true;
                    else change.AssetInIndex = true;
                }

                // Переименование меты сам ассет не описывает — путь берём только от ассета.
                if (!isMeta && e.Original != null)
                    change.OriginalPath = GitRepository.ToProjectPath(e.Original);
            }

            var list = new List<GitChange>(byAsset.Values);
            list.Sort((a, b) => string.Compare(a.ProjectPath, b.ProjectPath, StringComparison.OrdinalIgnoreCase));
            return list;
        }
    }
}
