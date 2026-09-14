using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;

namespace Lev.Git
{
    /// <summary>
    /// Локи и состояние LFS, известные редактору прямо сейчас.
    ///
    /// Локи живут на сервере, и спрашивать о них на каждую перерисовку окна
    /// Project нельзя. Поэтому здесь кэш: сервер опрашивается раз в минуту,
    /// пока редактор активен, и сразу после pull, push и операций с локами.
    /// Метки и проверки читают только кэш.
    ///
    /// Какие файлы из LFS не скачаны, узнаётся локально, без сети: по ответу
    /// `git lfs ls-files`, где у такого файла вместо «*» стоит «-».
    /// </summary>
    [InitializeOnLoad]
    public static class LfsLockCache
    {
        private const double LockIntervalSec = 60.0;
        private const double PointerIntervalSec = 30.0;

        private static LfsLockSet _locks = new LfsLockSet();
        private static HashSet<string> _notDownloaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static double _nextLocks, _nextPointers;
        private static bool _lockRunning, _pointerRunning;
        private static string _lastHead;

        public static LfsLockSet Locks => _locks;
        public static bool HasData { get; private set; }
        public static string LastError { get; private set; }
        public static DateTime? LastRefreshUtc { get; private set; }

        public static IEnumerable<string> NotDownloaded => _notDownloaded;
        public static int NotDownloadedCount => _notDownloaded.Count;

        public static event Action Updated;

        static LfsLockCache()
        {
            EditorApplication.update += OnUpdate;
        }

        private static void OnUpdate()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || !GitRepository.IsRepo) return;

            double now = EditorApplication.timeSinceStartup;

            // Сервер спрашиваем, только пока редактором пользуются: свёрнутый
            // Unity на ночь не должен дёргать GitLab раз в минуту.
            if (now >= _nextLocks && InternalEditorUtility.isApplicationActive)
            {
                _nextLocks = now + LockIntervalSec;
                _ = RefreshAsync();
            }

            // После checkout и pull набор скачанного меняется — HEAD подсказывает когда.
            if (now >= _nextPointers || (GitStatusCache.HeadOid != null && GitStatusCache.HeadOid != _lastHead))
            {
                _nextPointers = now + PointerIntervalSec;
                _lastHead = GitStatusCache.HeadOid;
                _ = RefreshPointersAsync();
            }
        }

        /// <summary>Попросить обновить всё при ближайшем такте.</summary>
        public static void RequestRefresh()
        {
            _nextLocks = 0;
            _nextPointers = 0;
        }

        public static async Task RefreshAsync()
        {
            if (_lockRunning || !GitRepository.IsRepo) return;
            _lockRunning = true;

            try
            {
                var r = await GitProcess.RunAsync(GitRepository.GitExe, "lfs locks --verify --json",
                    GitRepository.RepoRoot, null, 45000, default(System.Threading.CancellationToken), null, background: true);

                LfsLockSet set = r.Ok ? LfsLockSet.ParseVerified(r.StdOut) : null;

                // Сервер без проверки локов — берём простой список: владельцы
                // видны, но какие из них мои, уже не сказать наверняка.
                if (set == null)
                {
                    var plain = await GitProcess.RunAsync(GitRepository.GitExe, "lfs locks --json",
                        GitRepository.RepoRoot, null, 45000, default(System.Threading.CancellationToken), null, background: true);
                    if (plain.Ok) set = LfsLockSet.ParsePlain(plain.StdOut, null);
                    if (set == null)
                    {
                        LastError = L.F("Locks not received: {0}", r.Ok ? plain.Message : r.Message);
                        return;
                    }
                }

                _locks = set;
                HasData = true;
                LastError = set.Verified ? null : L.T("The server did not confirm which locks are yours: splitting into yours and others' is unavailable.");
                LastRefreshUtc = DateTime.UtcNow;
            }
            finally
            {
                _lockRunning = false;
                Notify();
            }
        }

        public static async Task RefreshPointersAsync()
        {
            if (_pointerRunning || !GitRepository.IsRepo) return;
            _pointerRunning = true;

            try
            {
                var r = await GitProcess.RunAsync(GitRepository.GitExe, "-c core.quotepath=false lfs ls-files",
                    GitRepository.RepoRoot, null, 60000, default(System.Threading.CancellationToken), null, background: true);
                if (!r.Ok) return;

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in LfsFiles.Parse(r.StdOut))
                    if (!e.Downloaded) set.Add(e.GitPath);

                _notDownloaded = set;
            }
            finally
            {
                _pointerRunning = false;
                Notify();
            }
        }

        private static void Notify()
        {
            Updated?.Invoke();
            EditorApplication.RepaintProjectWindow();
            EditorApplication.RepaintHierarchyWindow();
        }

        /// <summary>Лок ассета по пути относительно проекта. Лок самого ассета закрывает и его .meta.</summary>
        public static LfsLockInfo LockOf(string projectPath)
        {
            if (!HasData || string.IsNullOrEmpty(projectPath)) return null;
            return _locks.Find(GitRepository.ToGitPath(projectPath));
        }

        /// <summary>Лок чужой — и это известно наверняка.</summary>
        public static bool IsTheirs(LfsLockInfo l)
        {
            return l != null && _locks.Verified && !l.Mine;
        }

        public static bool IsNotDownloaded(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || _notDownloaded.Count == 0) return false;
            return _notDownloaded.Contains(GitRepository.ToGitPath(projectPath));
        }

        /// <summary>Подпись для меток и диалогов: «Лок у Анны · 12.09.2026 16:05 · 3 ч назад».</summary>
        public static string Describe(LfsLockInfo l)
        {
            if (l == null) return string.Empty;
            var who = l.Mine && _locks.Verified ? L.T("Your lock") : L.F("Locked by {0}", l.Owner);
            return who + " · " + LockAdvice.Stamp(l.LockedAtUtc, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Действия с локами вместе с проверками, которых нет в самом git:
    /// свежесть файла при взятии, незавершённая правка при снятии, чужие
    /// локи перед push.
    /// </summary>
    public static class LfsLockOps
    {
        // ------------------------------------------------------------ взять ---

        /// <param name="checkFreshness">
        /// Проверить до взятия, не изменён ли файл там, где у меня этой правки
        /// нет. Лок, взятый на устаревшем файле, конфликта не спасает: работа
        /// пойдёт поверх старой версии.
        /// </param>
        public static async Task<ProcessResult> LockAsync(IList<string> projectPaths, bool checkFreshness = true)
        {
            var paths = Files(projectPaths);
            if (paths.Count == 0) return Fail(L.T("No files selected."));

            var taken = new List<string>();
            var busy = new List<string>();
            foreach (var p in paths)
            {
                var l = LfsLockCache.LockOf(p);
                if (l == null) { taken.Add(p); continue; }
                if (!l.Mine) busy.Add(p + " — " + LfsLockCache.Describe(l));
            }

            if (busy.Count > 0)
                EditorUtility.DisplayDialog(L.T("File Already Locked"),
                    L.F("These files are already locked by others and cannot be locked:\n\n• {0}", string.Join("\n• ", busy.ToArray())), L.T("Got It"));

            if (taken.Count == 0) return busy.Count > 0 ? Fail(L.T("Files are busy.")) : Ok(L.T("The lock is already yours."));

            if (checkFreshness)
            {
                var stale = await StaleChangesAsync(taken);
                if (stale.Count > 0)
                {
                    var sb = new StringBuilder(L.T("These files have already been changed where you don't have these edits:\n"));
                    for (int i = 0; i < stale.Count && i < 8; i++)
                    {
                        var c = stale[i];
                        sb.Append("\n• ").Append(L.F("{0} — {1}, {2}: “{3}”", c.Ref, c.Author,
                            LockAdvice.Ago(c.When, DateTime.UtcNow), c.Subject));
                    }
                    if (stale.Count > 8) sb.Append(L.F("\n…and {0} more", stale.Count - 8));
                    sb.Append(L.T("\n\nA lock taken now will not help: the edit will go on top of the old version, and on merge " +
                                  "one of the versions will be lost. Pull these changes first."));

                    if (!EditorUtility.DisplayDialog(L.T("File Changed in Another Branch"), sb.ToString(), L.T("Lock Anyway"), L.T("Cancel")))
                        return Fail(L.T("Lock not taken: pull the changes first."));
                }
            }

            var errors = new List<string>();
            foreach (var p in taken)
            {
                var r = await GitOperations.Git("lfs lock --json -- " + GitOperations.Q(GitRepository.ToGitPath(p)), 60000);
                if (!r.Ok) errors.Add(p + ": " + r.Message);
            }

            await LfsLockCache.RefreshAsync();

            return errors.Count == 0
                ? Ok(taken.Count == 1 ? L.F("Locked: {0}", taken[0]) : L.F("Locks taken: {0}", taken.Count))
                : Fail(string.Join("\n", errors.ToArray()));
        }

        /// <summary>Коммиты, изменившие файлы в upstream или ветках на сервере, которых нет в HEAD.</summary>
        public static async Task<List<RemoteChange>> StaleChangesAsync(IList<string> projectPaths)
        {
            // Свежие сведения о ветках — без них проверка знает только то, что
            // было на момент прошлого fetch. Не вышло (нет сети) — проверяем по тому, что есть.
            await GitOperations.LongGit(L.T("Checking freshness"), "fetch --quiet --prune " + GitOperations.Q(GitRepository.SelectedRemoteName), 60000);

            var args = new StringBuilder("-c core.quotepath=false log --remotes --not HEAD --source -n 30 --format=")
                .Append(LockAdvice.LogFormat).Append(" --");
            foreach (var p in projectPaths) args.Append(' ').Append(GitOperations.Q(GitRepository.ToGitPath(p)));

            var r = await GitOperations.Git(args.ToString(), 60000);
            return r.Ok ? LockAdvice.ParseLog(r.StdOut) : new List<RemoteChange>();
        }

        // ------------------------------------------------------------ снять ---

        public static async Task<ProcessResult> UnlockAsync(IList<string> projectPaths, bool warnUnfinished = true)
        {
            var locks = new List<LfsLockInfo>();
            foreach (var p in projectPaths)
            {
                var l = LfsLockCache.LockOf(p);
                if (l != null && !locks.Contains(l)) locks.Add(l);
            }
            if (locks.Count == 0) return Fail(L.T("The selected files have no locks."));

            return await UnlockLocksAsync(locks, warnUnfinished);
        }

        public static async Task<ProcessResult> UnlockLocksAsync(IList<LfsLockInfo> locks, bool warnUnfinished = true)
        {
            var mine = new List<LfsLockInfo>();
            var theirs = new List<LfsLockInfo>();
            foreach (var l in locks) (LfsLockCache.IsTheirs(l) ? theirs : mine).Add(l);

            if (theirs.Count > 0)
            {
                var names = new List<string>();
                foreach (var l in theirs) names.Add(GitRepository.ToProjectPath(l.GitPath) + " — " + LfsLockCache.Describe(l));

                if (!EditorUtility.DisplayDialog(L.T("Unlock Someone Else's Lock"),
                        L.F("These locks belong to others:\n\n• {0}" +
                            "\n\nIf someone is editing the file right now, their work may be overwritten after the lock is removed. " +
                            "The server allows this only for those who have permission (in GitLab — project maintainers). It is better to message the owner first.",
                            string.Join("\n• ", names.ToArray())),
                        L.T("Force Unlock"), L.T("Cancel")))
                    theirs.Clear();
            }

            if (warnUnfinished && mine.Count > 0)
            {
                var problems = await UnfinishedAsync(mine);
                if (problems.Count > 0 &&
                    !EditorUtility.DisplayDialog(L.T("The Edit Has Not Reached the Main Branch Yet"),
                        L.F("• {0}" +
                            "\n\nAfter the lock is removed, the next person will take the file in the version that is in the main branch " +
                            "and start editing without this edit. One of the versions will be lost on merge.",
                            string.Join("\n• ", problems.ToArray())),
                        L.T("Unlock Anyway"), L.T("Cancel")))
                    mine.Clear();
            }

            if (mine.Count + theirs.Count == 0) return Fail(L.T("Locks not removed."));

            var errors = new List<string>();
            foreach (var l in mine) await UnlockOne(l, false, errors);
            foreach (var l in theirs) await UnlockOne(l, true, errors);

            await LfsLockCache.RefreshAsync();

            int done = mine.Count + theirs.Count - errors.Count;
            return errors.Count == 0 ? Ok(L.F("Locks removed: {0}", done)) : Fail(string.Join("\n", errors.ToArray()));
        }

        private static async Task UnlockOne(LfsLockInfo l, bool force, List<string> errors)
        {
            // По идентификатору, а не по пути: файл могли удалить или переименовать,
            // а лок на старом пути всё равно надо уметь снять.
            var args = "lfs unlock " + (force ? "--force " : string.Empty) +
                       (string.IsNullOrEmpty(l.Id) ? "-- " + GitOperations.Q(l.GitPath) : "--id=" + l.Id);

            var r = await GitOperations.Git(args, 60000);
            if (!r.Ok) errors.Add(l.GitPath + ": " + r.Message);
        }

        /// <summary>Что мешает снять свой лок без потерь: незакоммичено, не отправлено, не влито.</summary>
        private static async Task<List<string>> UnfinishedAsync(List<LfsLockInfo> locks)
        {
            var problems = new List<string>();
            var upstream = await GitOperations.UpstreamAsync();
            var main = await DefaultBranchAsync();
            var branch = await GitOperations.CurrentBranchAsync();
            bool onMain = main != null && branch != null && main.EndsWith("/" + branch, StringComparison.Ordinal);

            foreach (var l in locks)
            {
                var projectPath = GitRepository.ToProjectPath(l.GitPath);
                var q = GitOperations.Q(l.GitPath);

                if (GitStatusCache.GetStatus(projectPath) != GitFileStatus.None)
                {
                    problems.Add(L.F("{0}: the edit is not committed", projectPath));
                    continue;
                }

                var range = upstream != null ? GitOperations.Q(upstream) + "..HEAD" : "HEAD --not --remotes";
                var unpushed = await GitOperations.Git("log --format=%h " + range + " -- " + q, 30000);
                if (unpushed.Ok && unpushed.StdOut.Trim().Length > 0)
                {
                    problems.Add(L.F("{0}: commits with the edit are not pushed to the server", projectPath));
                    continue;
                }

                if (main != null && !onMain)
                {
                    var unmerged = await GitOperations.Git("log --format=%h " + GitOperations.Q(main) + "..HEAD -- " + q, 30000);
                    if (unmerged.Ok && unmerged.StdOut.Trim().Length > 0)
                        problems.Add(L.F("{0}: the edit is in branch “{1}” but not merged into {2}", projectPath, branch, main));
                }
            }

            return problems;
        }

        /// <summary>Основная ветка на сервере: «origin/main». null — определить не вышло.</summary>
        public static async Task<string> DefaultBranchAsync()
        {
            var remote = GitRepository.SelectedRemoteName;
            var head = await GitOperations.Git("symbolic-ref --quiet --short refs/remotes/" + remote + "/HEAD", 15000);
            if (head.Ok && head.StdOut.Trim().Length > 0) return head.StdOut.Trim();

            foreach (var name in new[] { "main", "master", "develop" })
            {
                var r = await GitOperations.Git("rev-parse --verify --quiet refs/remotes/" + remote + "/" + name, 15000);
                if (r.Ok) return remote + "/" + name;
            }

            return null;
        }

        // ------------------------------------------------------------- push ---

        /// <summary>Файлы, изменённые в коммитах, которые уйдут при push.</summary>
        /// <param name="target">Куда уйдёт push: «origin/main». null — в upstream текущей ветки.</param>
        /// <param name="remote">Remote цели — чтобы сравнить с его ветками, если такой ветки там ещё нет.</param>
        public static async Task<List<string>> OutgoingFilesAsync(string target = null, string remote = null)
        {
            string range;
            if (target != null)
            {
                var exists = await GitOperations.Git("rev-parse --verify --quiet " + GitOperations.Q("refs/remotes/" + target), 15000);
                range = exists.Ok
                    ? GitOperations.Q(target) + "..HEAD"
                    : "HEAD --not " + (remote != null ? "--remotes=" + GitOperations.Q(remote) : "--remotes");
            }
            else
            {
                var upstream = await GitOperations.UpstreamAsync();
                range = upstream != null ? GitOperations.Q(upstream) + "..HEAD" : "HEAD --not --remotes";
            }

            var r = await GitOperations.Git("-c core.quotepath=false log --format= --name-only " + range, 60000);
            var list = new List<string>();
            if (!r.Ok) return list;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in r.StdOut.Split('\n'))
            {
                var p = line.Trim();
                if (p.Length > 0 && seen.Add(p)) list.Add(p);
            }
            return list;
        }

        /// <summary>
        /// Перед push: чужие локи на отправляемых файлах. git-lfs и сам откажет
        /// по LFS-файлам, но сцену, которая лежит текстом, может пропустить —
        /// а сказать об этом лучше до сети, понятными словами.
        /// </summary>
        public static async Task<bool> ConfirmPushAsync(List<string> outgoingGitPaths)
        {
            if (outgoingGitPaths == null || outgoingGitPaths.Count == 0) return true;

            await LfsLockCache.RefreshAsync();
            if (!LfsLockCache.Locks.Verified) return true;

            var blocked = new List<string>();
            foreach (var p in outgoingGitPaths)
            {
                var l = LfsLockCache.Locks.Find(p);
                if (l != null && !l.Mine) blocked.Add(p + " — " + LfsLockCache.Describe(l));
            }

            if (blocked.Count == 0) return true;

            return EditorUtility.DisplayDialog(L.T("Pushing Files Locked by Others"),
                L.F("• {0}" +
                    "\n\nWhile someone else holds the lock, their work on these files will overwrite yours or vice versa. " +
                    "For files in LFS the server will reject the push anyway.",
                    string.Join("\n• ", blocked.ToArray())),
                L.T("Push Anyway"), L.T("Cancel"));
        }

        /// <summary>После push в основную ветку: предложить снять свои локи на отправленных файлах.</summary>
        public static async Task OfferUnlockAfterPushAsync(List<string> outgoingGitPaths)
        {
            if (outgoingGitPaths == null || outgoingGitPaths.Count == 0) return;

            var main = await DefaultBranchAsync();
            var branch = await GitOperations.CurrentBranchAsync();
            if (main == null || branch == null || !main.EndsWith("/" + branch, StringComparison.Ordinal)) return;

            await LfsLockCache.RefreshAsync();
            var mine = new List<LfsLockInfo>();
            foreach (var p in outgoingGitPaths)
            {
                var l = LfsLockCache.Locks.Find(p);
                if (l != null && l.Mine && !mine.Contains(l)) mine.Add(l);
            }
            if (mine.Count == 0) return;

            var names = new List<string>();
            foreach (var l in mine) names.Add(GitRepository.ToProjectPath(l.GitPath) ?? l.GitPath);

            if (EditorUtility.DisplayDialog(L.T("Unlock Files?"),
                    L.F("The edits went to {0}. Your locks remain on these files:\n\n• {1}\n\nUnlock them so others can edit them?",
                        main, string.Join("\n• ", names.ToArray())),
                    L.Tc("unlock after push", "Unlock"), L.T("Keep")))
                await UnlockLocksAsync(mine, false);
        }

        // ------------------------------------------------------------- LFS ---

        public static Task<ProcessResult> PullContentAsync(IList<string> gitPaths)
        {
            var include = new StringBuilder();
            if (gitPaths != null)
                foreach (var p in gitPaths)
                {
                    if (include.Length > 20000) break;
                    if (include.Length > 0) include.Append(',');
                    include.Append(p.Replace(",", "\\,"));
                }

            var args = include.Length > 0
                ? "lfs pull --include=" + GitOperations.Q(include.ToString())
                : "lfs pull";

            return GitOperations.WithAssetGuard(async () =>
            {
                var r = await GitOperations.LongGit(L.T("Downloading from LFS"), args, 3600000);
                await LfsLockCache.RefreshPointersAsync();
                return r;
            });
        }

        /// <summary>Какие из путей помечены lockable — по правилам .gitattributes, которые знает git.</summary>
        public static async Task<HashSet<string>> LockableAsync(IList<string> projectPaths)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (projectPaths == null || projectPaths.Count == 0) return result;

            var args = new StringBuilder("-c core.quotepath=false check-attr lockable --");
            foreach (var p in projectPaths)
            {
                args.Append(' ').Append(GitOperations.Q(GitRepository.ToGitPath(p)));
                if (args.Length > 24000) break;
            }

            var r = await GitOperations.Git(args.ToString(), 30000);
            if (!r.Ok) return result;

            foreach (var line in r.StdOut.Split('\n'))
            {
                int marker = line.LastIndexOf(": lockable: ", StringComparison.Ordinal);
                if (marker < 0 || line.Substring(marker + ": lockable: ".Length).Trim() != "set") continue;
                var projectPath = GitRepository.ToProjectPath(line.Substring(0, marker).Trim());
                if (projectPath != null) result.Add(projectPath);
            }

            return result;
        }

        // --------------------------------------------------------- настройка ---

        public static readonly string[] SceneLockPatterns = { "*.unity", "*.prefab" };

        public static string AttributesPath => System.IO.Path.Combine(GitRepository.RepoRoot, ".gitattributes");

        public static string ReadAttributes()
        {
            try { return System.IO.File.Exists(AttributesPath) ? System.IO.File.ReadAllText(AttributesPath) : string.Empty; }
            catch { return null; }
        }

        public static async Task<string> LocksVerifyAsync()
        {
            var r = await GitOperations.Git("config --get lfs.locksverify", 15000);
            return r.Ok ? r.StdOut.Trim() : null;
        }

        /// <summary>Показывает правку .gitattributes и применяет её после подтверждения.</summary>
        public static async Task<ProcessResult> SetupAsync()
        {
            var text = ReadAttributes();
            if (text == null) return Fail(L.T("Failed to read .gitattributes."));

            var plan = GitAttributesPlan.Build(text, SceneLockPatterns);
            var verify = await LocksVerifyAsync();

            if (!plan.Changed && verify == "true") return Ok(L.T("Locking is already set up."));

            var message = new StringBuilder();
            if (plan.Changed)
            {
                message.Append(L.F("The following will be added to .gitattributes:\n\n• {0}" +
                                   "\n\nMarked files will become read-only for everyone who pulls, until a lock is taken. " +
                                   "Scenes and prefabs stay as text — they are not moved to LFS. " +
                                   "The .gitattributes change must be committed to reach the team.\n\n",
                                   string.Join("\n• ", plan.Added.ToArray())));
            }
            if (verify != "true")
                message.Append(L.T("lfs.locksverify = true will be enabled for this working tree: push will fail " +
                                   "if locks could not be verified on the server."));

            if (!EditorUtility.DisplayDialog(L.T("Set Up File Locking"), message.ToString(), L.T("Apply"), L.T("Cancel")))
                return Fail(L.T("Setup canceled."));

            if (plan.Changed)
            {
                try { System.IO.File.WriteAllText(AttributesPath, plan.NewText, new UTF8Encoding(false)); }
                catch (Exception e) { return Fail(L.F("Failed to write .gitattributes: {0}", e.Message)); }
            }

            if (verify != "true")
            {
                var r = await GitOperations.Git("config lfs.locksverify true", 15000);
                if (!r.Ok) return r;
            }

            await GitStatusCache.RefreshAsync();
            return Ok(plan.Changed ? L.T("Locking is set up. Commit .gitattributes so it reaches the team.") : L.T("Lock verification on push is enabled."));
        }

        // ------------------------------------------------------------ мелочи ---

        /// <summary>Только файлы: у папки лока не бывает, а мета запирается вместе с ассетом.</summary>
        private static List<string> Files(IList<string> projectPaths)
        {
            var list = new List<string>();
            if (projectPaths == null) return list;

            foreach (var p in projectPaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                var path = p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? p.Substring(0, p.Length - 5) : p;
                if (AssetDatabase.IsValidFolder(path) || list.Contains(path)) continue;
                list.Add(path);
            }
            return list;
        }

        private static ProcessResult Ok(string message) { return new ProcessResult { ExitCode = 0, StdOut = message }; }
        private static ProcessResult Fail(string message) { return new ProcessResult { ExitCode = 1, StdErr = message }; }
    }
}
