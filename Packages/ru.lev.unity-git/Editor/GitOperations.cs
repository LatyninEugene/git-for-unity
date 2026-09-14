using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Lev.Git
{
    /// <summary>Операции git, вызываемые из UI. Все асинхронные, ни одна не блокирует редактор.</summary>
    public static class GitOperations
    {
        public static string Q(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        public static Task<ProcessResult> Git(string args, int timeoutMs = GitProcess.DefaultTimeoutMs, string stdin = null)
        {
            return GitProcess.RunAsync(GitRepository.GitExe, args, GitRepository.RepoRoot, stdin, timeoutMs);
        }

        /// <summary>
        /// Долгая сетевая операция: с индикатором, живым прогрессом и отменой.
        /// Прогресс git печатает в stderr через \r, поэтому строка приходит сюда
        /// из фонового потока — <see cref="GitJob.Progress"/> для того и volatile.
        /// </summary>
        public static async Task<ProcessResult> LongGit(string title, string args, int timeoutMs)
        {
            var job = GitJobs.Begin(title);
            try
            {
                return await GitProcess.RunAsync(
                    GitRepository.GitExe, args, GitRepository.RepoRoot,
                    null, timeoutMs, job.Token, line => job.Progress = line);
            }
            finally
            {
                GitJobs.End(job);
            }
        }

        /// <summary>
        /// Текущая ветка. Из кэша статуса, если он свежий: `status --branch`
        /// уже сообщил её, и повторный запуск процесса ничего не добавит.
        /// </summary>
        public static async Task<string> CurrentBranchAsync()
        {
            if (GitStatusCache.HasData && !GitStatusCache.IsDetached)
                return GitStatusCache.Branch;

            var r = await Git("rev-parse --abbrev-ref HEAD", 15000);
            return r.Ok ? r.StdOut.Trim() : null;
        }

        public static async Task<List<string>> LocalBranchesAsync()
        {
            var r = await Git("branch --format=%(refname:short)", 20000);
            var list = new List<string>();
            if (!r.Ok) return list;
            foreach (var line in r.StdOut.Split('\n'))
            {
                var b = line.Trim();
                if (b.Length > 0) list.Add(b);
            }
            return list;
        }

        /// <summary>
        /// Стадирует выбранные пути. Для каждого ассета добавляется и его .meta —
        /// коммит ассета без .meta ломает ссылки у всех остальных.
        /// </summary>
        public static Task<ProcessResult> StageAsync(IEnumerable<GitChange> changes)
        {
            return RunBatched("add --all --", PathsOf(changes));
        }

        /// <summary>
        /// Убирает изменения из индекса, не трогая рабочую копию.
        ///
        /// На ветке без коммитов `restore --staged` не работает: ему не от чего
        /// отсчитывать, HEAD ещё не существует. Свежий репозиторий, созданный
        /// Unity Hub, — ровно этот случай, поэтому там снимаем через `rm --cached`.
        /// </summary>
        public static Task<ProcessResult> UnstageAsync(IEnumerable<GitChange> changes)
        {
            var prefix = GitStatusCache.IsInitialCommit
                ? "rm --cached -r --quiet --"
                : "restore --staged --";
            return RunBatched(prefix, IndexedPathsOf(changes));
        }

        /// <summary>
        /// Пути, которые действительно лежат в индексе. Не пара «ассет + мета»
        /// целиком: мета, которой git ещё не знает, в `restore --staged` даёт
        /// «pathspec did not match» и срывает всю команду.
        /// </summary>
        /// <summary>
        /// Файлы — к последнему коммиту. Что было в коммите, возвращается оттуда
        /// (и в рабочей копии, и в индексе); чего не было — снимается из индекса и
        /// удаляется с диска. Ассет и его .meta — парой; у переименованного
        /// возвращается и прежний путь.
        /// </summary>
        public static Task<ProcessResult> DiscardFilesAsync(IEnumerable<GitChange> changes)
        {
            return WithAssetGuard(async () =>
            {
                var paths = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                void Add(string projectPath)
                {
                    if (string.IsNullOrEmpty(projectPath)) return;
                    var gitPath = GitRepository.ToGitPath(projectPath);
                    if (seen.Add(gitPath)) paths.Add(gitPath);
                }

                foreach (var c in changes)
                {
                    if (c.IsConflicted) continue;
                    if (c.HasAsset) Add(c.ProjectPath);
                    if (c.HasMeta) Add(c.ProjectPath + ".meta");
                    if (!string.IsNullOrEmpty(c.OriginalPath))
                    {
                        Add(c.OriginalPath);
                        Add(c.OriginalPath + ".meta");
                    }
                }

                if (paths.Count == 0)
                    return new ProcessResult { ExitCode = 1, StdErr = L.T("Nothing to discard.") };

                // Что есть в последнем коммите. Порциями: путей бывает много.
                var inHead = new HashSet<string>(StringComparer.Ordinal);
                if (!GitStatusCache.IsInitialCommit)
                {
                    for (int i = 0; i < paths.Count; i += 200)
                    {
                        var batch = new StringBuilder("-c core.quotepath=false ls-tree -r --name-only HEAD --");
                        for (int j = i; j < Math.Min(paths.Count, i + 200); j++) batch.Append(' ').Append(Q(paths[j]));

                        var listed = await Git(batch.ToString(), 60000);
                        if (!listed.Ok) return listed;
                        foreach (var line in listed.StdOut.Split('\n'))
                            if (line.Trim().Length > 0) inHead.Add(line.Trim());
                    }
                }

                var restore = new List<string>();
                var remove = new List<string>();
                foreach (var p in paths)
                {
                    if (inHead.Contains(p)) restore.Add(Q(p));
                    else remove.Add(p);
                }

                if (restore.Count > 0)
                {
                    var r = await RunBatched("restore --source=HEAD --staged --worktree --", restore);
                    if (!r.Ok) return r;
                }

                if (remove.Count > 0)
                {
                    var quoted = remove.ConvertAll(Q);
                    var r = await RunBatched("rm --cached --quiet --ignore-unmatch --", quoted);
                    if (!r.Ok) return r;

                    foreach (var p in remove)
                    {
                        var full = System.IO.Path.Combine(GitRepository.RepoRoot, p);
                        try
                        {
                            if (!System.IO.File.Exists(full)) continue;
                            System.IO.File.SetAttributes(full, System.IO.FileAttributes.Normal);
                            System.IO.File.Delete(full);
                        }
                        catch (Exception e)
                        {
                            return new ProcessResult { ExitCode = 1, StdErr = L.F("Failed to delete {0}: {1}", p, e.Message) };
                        }
                    }
                }

                return new ProcessResult { ExitCode = 0 };
            });
        }

        private static List<string> IndexedPathsOf(IEnumerable<GitChange> changes)
        {
            var paths = new List<string>();
            foreach (var c in changes)
            {
                if (c.AssetInIndex) paths.Add(Q(GitRepository.ToGitPath(c.ProjectPath)));
                if (c.MetaInIndex) paths.Add(Q(GitRepository.ToGitPath(c.ProjectPath + ".meta")));
            }
            return paths;
        }

        private static List<string> PathsOf(IEnumerable<GitChange> changes)
        {
            var paths = new List<string>();
            foreach (var c in changes)
            {
                // Сам путь — только если менялся сам ассет. У меты папки «ассетом»
                // оказывается каталог, и `git add -- каталог` забрал бы в индекс
                // всё его содержимое, включая файлы, с которых сняли отметку.
                if (c.HasAsset) paths.Add(Q(GitRepository.ToGitPath(c.ProjectPath)));
                // Ассет без своей .meta ломает ссылки у всех остальных, поэтому
                // они всегда ходят парой — и в индекс, и из него.
                if (c.HasMeta) paths.Add(Q(GitRepository.ToGitPath(c.ProjectPath + ".meta")));
            }
            return paths;
        }

        /// <summary>
        /// Windows режет командную строку примерно на 32 000 символов, а в свежем
        /// Unity-проекте под тысячу untracked-файлов — выполняем порциями.
        /// </summary>
        private static async Task<ProcessResult> RunBatched(string prefix, List<string> paths)
        {
            if (paths.Count == 0)
                return new ProcessResult { ExitCode = 1, StdErr = L.T("No changes selected.") };

            const int MaxArgsLength = 24000;

            var sb = new StringBuilder(prefix);
            for (int i = 0; i < paths.Count; i++)
            {
                if (sb.Length + paths[i].Length + 1 > MaxArgsLength && sb.Length > prefix.Length)
                {
                    var batch = await Git(sb.ToString());
                    if (!batch.Ok) return batch;
                    sb.Length = 0;
                    sb.Append(prefix);
                }
                sb.Append(' ').Append(paths[i]);
            }

            return await Git(sb.ToString());
        }

        /// <summary>Сообщение передаётся через stdin — так его не портит экранирование в командной строке.</summary>
        public static Task<ProcessResult> CommitAsync(string message)
        {
            return Git("commit -F -", GitProcess.DefaultTimeoutMs, message);
        }

        /// <summary>Сообщение последнего коммита — чтобы при amend его не набирать заново.</summary>
        public static async Task<string> LastCommitMessageAsync()
        {
            var r = await Git("log -1 --pretty=%B", 15000);
            return r.Ok ? r.StdOut.TrimEnd('\n', '\r') : null;
        }

        /// <summary>Недавние заголовки коммитов, без повторов — для быстрой вставки.</summary>
        public static async Task<List<string>> RecentMessagesAsync(int count)
        {
            var list = new List<string>();
            var r = await Git("log -" + count + " --pretty=%s", 20000);
            if (!r.Ok) return list;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in r.StdOut.Split('\n'))
            {
                var s = line.Trim();
                if (s.Length == 0 || !seen.Add(s)) continue;
                list.Add(s);
            }
            return list;
        }

        /// <summary>
        /// Шаблон сообщения из commit.template.
        ///
        /// Строки-комментарии вырезаются здесь: `commit -F -` их не убирает,
        /// и они уехали бы прямо в текст коммита.
        /// </summary>
        public static async Task<string> CommitTemplateAsync()
        {
            var cfg = await Git("config --get commit.template", 15000);
            if (!cfg.Ok) return null;

            var path = cfg.StdOut.Trim();
            if (path.Length == 0) return null;

            if (path.StartsWith("~/", StringComparison.Ordinal))
                path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Substring(2));

            if (!System.IO.Path.IsPathRooted(path))
                path = System.IO.Path.Combine(GitRepository.RepoRoot, path);

            try
            {
                var raw = System.IO.File.ReadAllText(path, new UTF8Encoding(false));
                var sb = new StringBuilder();
                foreach (var line in raw.Split('\n'))
                {
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                    sb.Append(line).Append('\n');
                }
                return sb.ToString().TrimEnd('\n', '\r');
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Upstream текущей ветки ("origin/main") или null.
        /// Берётся из кэша статуса — его отдаёт тот же `status --branch`,
        /// поэтому отдельный запуск процесса нужен только пока кэш пуст.
        /// </summary>
        public static async Task<string> UpstreamAsync()
        {
            if (GitStatusCache.HasData) return GitStatusCache.Upstream;

            var r = await Git("rev-parse --abbrev-ref --symbolic-full-name @{u}", 15000);
            return r.Ok ? r.StdOut.Trim() : null;
        }

        /// <summary>
        /// Push с проверкой локов: до отправки — чужие локи на отправляемых
        /// файлах, после отправки в основную ветку — предложение снять свои.
        /// </summary>
        public static async Task<ProcessResult> PushAsync()
        {
            var outgoing = await LfsLockOps.OutgoingFilesAsync();
            if (!await LfsLockOps.ConfirmPushAsync(outgoing))
                return new ProcessResult { ExitCode = 1, StdErr = L.T("Push canceled: pushing files locked by others.") };

            var r = await PushCoreAsync();
            if (r.Ok) await LfsLockOps.OfferUnlockAfterPushAsync(outgoing);
            return r;
        }

        private static async Task<ProcessResult> PushCoreAsync()
        {
            if (await UpstreamAsync() != null)
                return await LongGit("Push", "push --progress", 300000);

            var branch = await CurrentBranchAsync();
            if (string.IsNullOrEmpty(branch))
                return new ProcessResult { ExitCode = 1, StdErr = L.T("Failed to determine the current branch.") };

            // Upstream ещё не задан — заводим его на выбранном remote.
            return await LongGit("Push",
                "push --progress -u " + Q(GitRepository.SelectedRemoteName) + " " + Q(branch), 300000);
        }

        /// <summary>
        /// Push текущей ветки в выбранный remote — не обязательно тот, за
        /// которым она следит. setUpstream делает этот remote её upstream:
        /// дальше обычный Push и Pull пойдут туда.
        /// </summary>
        public static async Task<ProcessResult> PushToAsync(string remote, string branch, bool setUpstream)
        {
            if (string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(branch))
                return new ProcessResult { ExitCode = 1, StdErr = L.T("No remote selected or no current branch.") };

            var outgoing = await LfsLockOps.OutgoingFilesAsync(remote + "/" + branch, remote);
            if (!await LfsLockOps.ConfirmPushAsync(outgoing))
                return new ProcessResult { ExitCode = 1, StdErr = L.T("Push canceled: pushing files locked by others.") };

            var r = await LongGit("Push → " + remote,
                "push --progress " + (setUpstream ? "-u " : string.Empty) + Q(remote) + " " + Q(branch), 300000);

            if (r.Ok) await LfsLockOps.OfferUnlockAfterPushAsync(outgoing);
            return r;
        }

        /// <summary>Сервер отклонил push: в ветке на нём есть коммиты, которых нет локально.</summary>
        public static bool IsRejectedByServer(ProcessResult r)
        {
            if (r == null || r.Ok) return false;
            var text = (r.StdErr ?? string.Empty) + "\n" + (r.StdOut ?? string.Empty);
            return text.IndexOf("[rejected]", StringComparison.Ordinal) >= 0 &&
                   (text.IndexOf("non-fast-forward", StringComparison.Ordinal) >= 0 ||
                    text.IndexOf("fetch first", StringComparison.Ordinal) >= 0);
        }

        /// <summary>
        /// Последний коммит уже лежит хоть в одной ветке remote — по данным
        /// последнего fetch. Upstream для этого не нужен: ветку могли отправить
        /// без -u или другим клиентом.
        /// </summary>
        public static async Task<bool> HeadOnRemoteAsync()
        {
            if (GitStatusCache.IsInitialCommit) return false;
            var r = await Git("branch -r --contains HEAD", 15000);
            return r.Ok && r.StdOut.Trim().Length > 0;
        }

        /// <summary>
        /// Похоже ли расхождение с сервером на amend уже отправленного коммита:
        /// последние коммиты локально и на сервере разные, а родитель у них один
        /// (или оба — первые в истории).
        /// </summary>
        public static async Task<bool> LooksAmendedAsync(string tracking)
        {
            var local = await Git("rev-list --parents -n 1 HEAD", 15000);
            var server = await Git("rev-list --parents -n 1 " + Q(tracking), 15000);
            if (!local.Ok || !server.Ok) return false;

            var l = local.StdOut.Trim().Split(' ');
            var s = server.StdOut.Trim().Split(' ');
            if (l.Length == 0 || s.Length == 0 || l[0] == s[0]) return false;

            return string.Join(" ", l, 1, l.Length - 1) == string.Join(" ", s, 1, s.Length - 1);
        }

        /// <summary>
        /// Push с заменой ветки на сервере (--force-with-lease).
        ///
        /// Замена проходит, только если ветка на сервере всё ещё та, что была при
        /// fetch: чужие коммиты, отправленные за это время, так не затереть. Поэтому
        /// перед push — свежий fetch, и ожидаемое состояние задаётся точным хэшем,
        /// а не «каким помнится».
        /// </summary>
        public static async Task<ProcessResult> ForcePushAsync(string remote, string branch, bool setUpstream, bool fetchFirst = true)
        {
            if (string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(branch))
                return new ProcessResult { ExitCode = 1, StdErr = L.T("No remote selected or no current branch.") };

            if (fetchFirst)
            {
                var fetch = await FetchRemoteAsync(remote);
                if (!fetch.Ok) return fetch;
            }

            var tracking = remote + "/" + branch;
            var known = await Git("rev-parse --verify --quiet " + Q("refs/remotes/" + tracking), 15000);

            // Ветки на сервере нет — заменять нечего, это обычный push.
            if (!known.Ok) return await PushToAsync(remote, branch, setUpstream);

            var outgoing = await LfsLockOps.OutgoingFilesAsync(tracking, remote);
            if (!await LfsLockOps.ConfirmPushAsync(outgoing))
                return new ProcessResult { ExitCode = 1, StdErr = L.T("Push canceled: pushing files locked by others.") };

            // --force-with-lease, а не голый --force: замена пройдёт, только если ветка
            // на сервере всё ещё на этом коммите.
            var lease = "--force-with-lease=" + Q(branch + ":" + known.StdOut.Trim());
            var r = await LongGit(L.F("Push --force → {0}", remote),
                "push --progress " + lease + " " + (setUpstream ? "-u " : string.Empty) + Q(remote) + " " + Q(branch), 300000);

            if (r.Ok)
            {
                await LfsLockOps.OfferUnlockAfterPushAsync(outgoing);
                return r;
            }

            // Самый частый отказ: ветка защищена, и сервер запрещает переписывать её
            // историю. По тексту git это не очевидно — объясняем, где это разрешить.
            if (GitOutput.IsProtectedBranchRefusal(r.StdErr))
            {
                return new ProcessResult
                {
                    ExitCode = r.ExitCode,
                    DurationMs = r.DurationMs,
                    StdOut = r.StdOut,
                    StdErr = L.F("The server does not allow push --force to the protected branch {0}. " +
                                 "Allow force push for this branch in the project settings on the server " +
                                 "(GitLab: Settings → Repository → Protected branches) or push to another branch.", branch) +
                             "\n\n" + GitOutput.WithoutProgress(r.StdErr)
                };
            }
            return r;
        }

        /// <summary>Забрать ветку с сервера и положить свои коммиты поверх неё.</summary>
        public static Task<ProcessResult> PullRebaseAsync(string remote, string branch)
        {
            return WithAssetGuard(() => LongGit(L.F("Pull with rebase from {0}", remote),
                "pull --rebase --progress " + Q(remote) + " " + Q(branch), 300000));
        }

        public static Task<ProcessResult> FetchAsync()
        {
            return FetchRemoteAsync(GitRepository.SelectedRemoteName);
        }

        public static Task<ProcessResult> FetchRemoteAsync(string remote)
        {
            return LongGit("Fetch " + remote, "fetch --progress --prune " + Q(remote), 300000);
        }

        public static Task<ProcessResult> FetchAllAsync()
        {
            return LongGit(L.T("Fetch all remotes"), "fetch --progress --prune --all", 600000);
        }

        // ---------------------------------------------------------- remotes ---

        public static Task<ProcessResult> RemoteAddAsync(string name, string url)
        {
            return Git("remote add " + Q(name) + " " + Q(url), 30000);
        }

        /// <summary>Удаляет remote вместе с его ветками отслеживания. Ветки на сервере не трогаются.</summary>
        public static Task<ProcessResult> RemoteRemoveAsync(string name)
        {
            return Git("remote remove " + Q(name), 60000);
        }

        /// <summary>Переименование переносит и ветки отслеживания, и upstream локальных веток.</summary>
        public static Task<ProcessResult> RemoteRenameAsync(string oldName, string newName)
        {
            return Git("remote rename " + Q(oldName) + " " + Q(newName), 60000);
        }

        public static Task<ProcessResult> RemoteSetUrlAsync(string name, string url, bool push)
        {
            return Git("remote set-url " + (push ? "--push " : string.Empty) + Q(name) + " " + Q(url), 30000);
        }

        /// <summary>Отдельный адрес для push больше не нужен — push снова идёт по адресу fetch.</summary>
        public static async Task<ProcessResult> RemoteClearPushUrlAsync(string name)
        {
            var r = await Git("config --unset-all " + Q("remote." + name + ".pushurl"), 30000);
            // Код 5 — ключа не было. Цель достигнута и так.
            return r.ExitCode == 5 ? new ProcessResult { ExitCode = 0 } : r;
        }

        /// <summary>Доступен ли remote: список веток без скачивания объектов.</summary>
        public static Task<ProcessResult> RemoteTestAsync(string name)
        {
            return LongGit(L.F("Testing {0}", name), "ls-remote --heads " + Q(name), 60000);
        }

        public static Task<ProcessResult> PullAsync()
        {
            return WithAssetGuard(() => LongGit("Pull", "pull --progress", 300000));
        }

        public static Task<ProcessResult> CheckoutAsync(string branch)
        {
            return WithAssetGuard(() => LongGit("Checkout " + branch, "checkout --progress " + Q(branch), 300000));
        }

        /// <summary>
        /// Оборачивает операцию, меняющую файлы на диске.
        ///
        /// Без этого Unity замечает изменения на полпути: начинает импорт и
        /// рекомпиляцию скриптов прямо посреди checkout, домен перезагружается,
        /// и незавершённая операция теряется вместе со стеком await.
        /// </summary>
        public static async Task<ProcessResult> WithAssetGuard(Func<Task<ProcessResult>> body)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return new ProcessResult { ExitCode = 1, StdErr = L.T("Canceled: there are unsaved scenes.") };

            AssetDatabase.SaveAssets();
            AssetDatabase.DisallowAutoRefresh();
            try
            {
                return await body();
            }
            finally
            {
                AssetDatabase.AllowAutoRefresh();
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
        }

        // ------------------------------------------------------------ diff ---

        /// <summary>
        /// Полный diff файла: рабочая копия против последнего коммита.
        ///
        /// Индекс в сравнении намеренно не участвует. Что войдёт в коммит,
        /// решают галочки, а индекс собирается из них в момент коммита — значит
        /// показывать нужно ВСЕ изменения файла, а не остаток относительно
        /// индекса. Иначе фрагмент, однажды отложенный, исчезал бы из списка.
        ///
        /// Неотслеживаемый файл сравнивать не с чем: его нет ни в индексе, ни в
        /// HEAD. Для него берём `--no-index` против пустоты.
        /// </summary>
        public static async Task<FileDiff> DiffAsync(GitChange change)
        {
            // У записи из одной меты сравнивать нужно саму мету: путь записи —
            // каталог, и diff по нему показал бы всю папку разом.
            var gitPath = GitRepository.ToGitPath(
                change.MetaOnly ? change.ProjectPath + ".meta" : change.ProjectPath);
            if (string.IsNullOrEmpty(gitPath)) return null;

            bool untracked = change.WorkStatus == GitFileStatus.Untracked
                             || (GitStatusCache.IsInitialCommit && change.Status != GitFileStatus.Deleted);

            var args = untracked
                ? "-c core.quotepath=false diff -U3 --no-index -- /dev/null " + Q(gitPath)
                : "-c core.quotepath=false diff -U3 HEAD -- " + Q(gitPath);

            var r = await Git(args, 60000);

            // `--no-index` возвращает 1, когда файлы различаются, — это не ошибка.
            if (!r.Ok && r.ExitCode != 1) return null;

            var diff = GitDiffParser.Parse(r.StdOut);
            GitPatchBuilder.MarkIntraLineChanges(diff);
            return diff;
        }

        /// <summary>Один файл в предстоящем коммите.</summary>
        public sealed class CommitItem
        {
            public GitChange Change;

            /// <summary>
            /// Содержимое, которое должно попасть в индекс, когда часть правок
            /// в коммит не идёт. null — файл идёт целиком.
            /// </summary>
            public string PartialContent;
        }

        /// <summary>
        /// Кладёт готовое содержимое в индекс, минуя рабочую копию и `git apply`.
        ///
        /// hash-object с --path обязателен: без него не применятся фильтры
        /// .gitattributes и core.autocrlf, и в индекс лягут не те переводы строк,
        /// что положил бы `git add`. Файл на диске при этом не трогается — там
        /// остаются все правки, включая не отмеченные.
        /// </summary>
        public static async Task<ProcessResult> StageContentAsync(string gitPath, string content)
        {
            var hashed = await Git("hash-object -w --path=" + Q(gitPath) + " --stdin",
                                   60000, content);
            if (!hashed.Ok) return hashed;

            var sha = hashed.StdOut.Trim();
            if (sha.Length < 40)
                return new ProcessResult { ExitCode = 1, StdErr = L.T("git did not return an object ID.") };

            var mode = await FileModeAsync(gitPath);
            return await Git(string.Format("update-index --add --cacheinfo {0},{1},{2}",
                                           mode, sha, gitPath.Replace("\"", "\\\"")), 30000);
        }

        /// <summary>Права файла из индекса. Для нового файла берём обычные 100644.</summary>
        private static async Task<string> FileModeAsync(string gitPath)
        {
            var r = await Git("ls-files -s -- " + Q(gitPath), 20000);
            if (r.Ok)
            {
                var s = r.StdOut.Trim();
                int sp = s.IndexOf(' ');
                if (sp == 6) return s.Substring(0, 6);
            }
            return "100644";
        }

        /// <summary>
        /// Содержимое файла из последнего коммита. null — файла там не было.
        /// Нужно семантическому сравнению: ему требуется исходный текст целиком,
        /// а не патч.
        /// </summary>
        public static async Task<string> ShowHeadTextAsync(string gitPath)
        {
            if (GitStatusCache.IsInitialCommit) return null;

            var r = await Git("show " + Q("HEAD:" + gitPath), 60000);
            return r.Ok ? r.StdOut : null;
        }

        /// <summary>
        /// Файл из последнего коммита как есть, байтами. null — файла там не было
        /// или прочитать не удалось.
        ///
        /// Через временный файл, а не через стандартный вывод: тот декодируется
        /// как текст, и картинка после такого чтения перестаёт быть картинкой.
        /// </summary>
        public static async Task<byte[]> ShowHeadBytesAsync(string gitPath)
        {
            if (GitStatusCache.IsInitialCommit) return null;

            var temp = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "levgitlab_" + Guid.NewGuid().ToString("N"));

            try
            {
                var ok = await GitProcess.RunToFileAsync(
                    GitRepository.GitExe, "show " + Q("HEAD:" + gitPath),
                    GitRepository.RepoRoot, temp, 60000);

                return ok ? System.IO.File.ReadAllBytes(temp) : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch { }
            }
        }

        /// <summary>
        /// Указатель LFS вместо содержимого?
        ///
        /// `git show` отдаёт blob как он лежит в репозитории, а для файла под LFS
        /// там лежит не картинка, а текстовая записка на полторы сотни байт:
        /// версия спецификации, хеш и размер. Фильтры при этом не применяются —
        /// они срабатывают только при выгрузке в рабочую копию.
        /// </summary>
        public static bool IsLfsPointer(byte[] bytes)
        {
            const string Marker = "version https://git-lfs.github.com/spec/";

            if (bytes == null || bytes.Length < Marker.Length || bytes.Length > 4096) return false;

            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, Marker.Length));
            return head == Marker;
        }

        /// <summary>
        /// Превращает указатель LFS в настоящее содержимое.
        ///
        /// Если объекта нет в локальном хранилище, git-lfs сходит за ним на
        /// сервер — поэтому таймаут щедрее обычного, а неудача не считается
        /// ошибкой: превью просто не покажем.
        ///
        /// Путь обязателен: по нему git-lfs выбирает настройки фильтра из
        /// .gitattributes, а без него любая жалоба в stderr указывает на
        /// «&lt;unknown file&gt;» и диагностировать её нечем.
        /// </summary>
        public static async Task<byte[]> SmudgeLfsAsync(byte[] pointer, string gitPath)
        {
            if (pointer == null) return null;

            var temp = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "levgitlab_lfs_" + Guid.NewGuid().ToString("N"));

            try
            {
                var ok = await GitProcess.RunToFileAsync(
                    GitRepository.GitExe, "lfs smudge -- " + Q(gitPath), GitRepository.RepoRoot,
                    temp, 120000, pointer);

                return ok ? System.IO.File.ReadAllBytes(temp) : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch { }
            }
        }

        /// <summary>Содержимое файла из рабочей копии как есть, без нормализации.</summary>
        public static string ReadWorktreeText(string gitPath)
        {
            try
            {
                var full = System.IO.Path.Combine(GitRepository.RepoRoot, gitPath);
                return System.IO.File.ReadAllText(full, new UTF8Encoding(false));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Записывает файл обратно в рабочую копию — для отката фрагмента.</summary>
        public static bool WriteWorktreeText(string gitPath, string content)
        {
            try
            {
                var full = System.IO.Path.Combine(GitRepository.RepoRoot, gitPath);
                System.IO.File.WriteAllText(full, content, new UTF8Encoding(false));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Собирает индекс ровно из отметок и коммитит.
        ///
        /// Порядок принципиален. Сначала индекс сбрасывается к HEAD по всем
        /// затронутым файлам — только после этого патч, снятый относительно HEAD,
        /// вообще на него ложится, и только так в коммит не попадает то, что
        /// застадировали снаружи, из Rider или из консоли.
        /// </summary>
        public static async Task<ProcessResult> CommitSelectionAsync(
            List<CommitItem> items, IEnumerable<GitChange> allChanges,
            string message, bool amend, bool signoff = false)
        {
            if (!amend && (items == null || items.Count == 0))
                return new ProcessResult { ExitCode = 1, StdErr = L.T("No files included.") };

            var staged = new List<GitChange>();
            foreach (var c in allChanges) if (c.IsStaged) staged.Add(c);

            if (staged.Count > 0)
            {
                var cleared = await UnstageAsync(staged);
                if (!cleared.Ok) return cleared;
            }

            var whole = new List<GitChange>();
            foreach (var it in items) if (it.PartialContent == null) whole.Add(it.Change);

            if (whole.Count > 0)
            {
                var added = await StageAsync(whole);
                if (!added.Ok) return added;
            }

            foreach (var it in items)
            {
                if (it.PartialContent == null) continue;

                var gitPath = GitRepository.ToGitPath(it.Change.ProjectPath);
                var written = await StageContentAsync(gitPath, it.PartialContent);
                if (!written.Ok)
                    return new ProcessResult
                    {
                        ExitCode = written.ExitCode,
                        StdErr = it.Change.ProjectPath + ": " + written.Message
                    };

                // .meta ходит с ассетом всегда: ассет без неё ломает ссылки
                // у всех, кто на него ссылается.
                if (it.Change.HasMeta)
                {
                    var metaPath = Q(GitRepository.ToGitPath(it.Change.ProjectPath + ".meta"));
                    var addedMeta = await RunBatched("add --all --", new List<string> { metaPath });
                    if (!addedMeta.Ok) return addedMeta;
                }
            }

            var args = "commit -F -";
            if (amend) args += " --amend";
            if (signoff) args += " -s";

            return await Git(args, GitProcess.DefaultTimeoutMs, message);
        }
    }
}
