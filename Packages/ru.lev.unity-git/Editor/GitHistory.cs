using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Lev.Git
{
    /// <summary>Условия отбора коммитов. Пустые поля не попадают в командную строку.</summary>
    public sealed class GitLogFilter
    {
        public string Author;
        public string Text;
        /// <summary>Путь относительно проекта (Assets/…), не относительно репозитория.</summary>
        public string ProjectPath;
        /// <summary>Всё, что понимает --since: «2 weeks ago», «2026-01-01».</summary>
        public string Since;
        public string Until;
        /// <summary>Показывать все ветки, а не только текущую.</summary>
        public bool AllRefs = true;

        /// <summary>
        /// Одна ветка вместо всех. Выборка при этом остаётся непрерывной — это
        /// вся история от вершины ветки вниз, — поэтому граф рисуется.
        /// </summary>
        public string Ref;

        public int Limit = 300;
        public int Skip;

        /// <summary>
        /// Отбор, при котором часть коммитов выпадает из выборки. Тогда связи
        /// «родитель — потомок» рвутся, и рисовать граф нельзя: линии пошли бы
        /// мимо, а пустые места читались бы как настоящие ветвления.
        /// </summary>
        public bool DropsCommits
        {
            get
            {
                return !string.IsNullOrEmpty(Author) || !string.IsNullOrEmpty(Text) ||
                       !string.IsNullOrEmpty(ProjectPath) ||
                       !string.IsNullOrEmpty(Since) || !string.IsNullOrEmpty(Until);
            }
        }
    }

    /// <summary>Страница журнала вместе с готовой раскладкой графа.</summary>
    public sealed class GitLogPage
    {
        public List<GitCommit> Commits = new List<GitCommit>();
        public int Lanes = 1;
        /// <summary>Граф осмыслен: выборка непрерывна.</summary>
        public bool HasGraph;
        /// <summary>Есть что дозагрузить: пришло ровно столько, сколько просили.</summary>
        public bool More;
        public string Error;
    }

    public enum GitCommitFileStatus { Modified, Added, Deleted, Renamed, Copied, TypeChanged }

    /// <summary>Файл, затронутый одним коммитом.</summary>
    public sealed class GitCommitFile
    {
        public GitCommitFileStatus Status;
        public string GitPath;
        public string ProjectPath;
        public string OriginalPath;
        /// <summary>Прежний путь в репозитории — по нему достаётся версия до переименования.</summary>
        public string OriginalGitPath;
        /// <summary>Файл вне папки проекта: ProjectSettings, Packages и прочее.</summary>
        public bool Outside { get { return ProjectPath == null; } }
    }

    public sealed class GitBranchInfo
    {
        public string Name;
        public string Sha;
        public string Upstream;
        public int Ahead, Behind;
        public bool IsRemote;
        public bool IsCurrent;
        public bool Gone;
        public DateTime Date;
        public string Subject = string.Empty;
    }

    /// <summary>
    /// Журнал, ветки и действия над коммитами.
    ///
    /// Отдельно от <see cref="GitOperations"/> намеренно: там операции над
    /// рабочей копией, здесь — чтение истории, и общего у них только вызов git.
    /// </summary>
    public static class GitHistory
    {
        // ------------------------------------------------------------ журнал ---

        public static async Task<GitLogPage> LogAsync(GitLogFilter filter)
        {
            var page = new GitLogPage();
            if (filter == null) filter = new GitLogFilter();

            if (GitStatusCache.IsInitialCommit)
            {
                page.Error = L.T("The repository has no commits yet.");
                return page;
            }

            var args = new StringBuilder("-c core.quotepath=false --no-optional-locks log");

            // Топологический порядок обязателен: раскладка графа идёт одним
            // проходом сверху вниз и требует, чтобы родитель встретился ПОСЛЕ
            // потомка. При сортировке по дате это не гарантировано — часы на
            // машине автора могут отставать.
            args.Append(" --topo-order");
            args.Append(" --format=").Append(GitLogParser.Format);
            args.Append(" -n ").Append(Math.Max(1, filter.Limit));

            if (filter.Skip > 0) args.Append(" --skip=").Append(filter.Skip);
            // Ветка передаётся ревизией до «--»: после него git счёл бы её путём.
            if (!string.IsNullOrEmpty(filter.Ref)) args.Append(' ').Append(GitOperations.Q(filter.Ref));
            else if (filter.AllRefs) args.Append(" --all");

            if (!string.IsNullOrEmpty(filter.Author))
                args.Append(" --author=").Append(GitOperations.Q(filter.Author));

            if (!string.IsNullOrEmpty(filter.Text))
                args.Append(" --regexp-ignore-case --fixed-strings --grep=")
                    .Append(GitOperations.Q(filter.Text));

            if (!string.IsNullOrEmpty(filter.Since))
                args.Append(" --since=").Append(GitOperations.Q(filter.Since));

            if (!string.IsNullOrEmpty(filter.Until))
                args.Append(" --until=").Append(GitOperations.Q(filter.Until));

            if (!string.IsNullOrEmpty(filter.ProjectPath))
            {
                var gitPath = GitRepository.ToGitPath(filter.ProjectPath);
                // --full-history: без него git прячет слияния, через которые правки
                // файла пришли из другой ветки, — а «История сцены» показывает именно
                // их, и переход оттуда к коммиту не находил его в журнале.
                // --follow работает только с одним путём и только без --all.
                args.Append(" --full-history -- ").Append(GitOperations.Q(gitPath));
            }

            var r = await GitOperations.Git(args.ToString(), 60000);
            if (!r.Ok) { page.Error = r.Message; return page; }

            page.Commits = GitLogParser.Parse(r.StdOut);
            page.More = page.Commits.Count >= filter.Limit;
            page.HasGraph = !filter.DropsCommits;

            if (page.HasGraph) page.Lanes = GitGraphBuilder.Layout(page.Commits);

            return page;
        }

        /// <summary>Полный текст сообщения коммита, включая тело после заголовка.</summary>
        public static async Task<string> MessageAsync(string sha)
        {
            var r = await GitOperations.Git("log -1 --format=%B " + GitOperations.Q(sha), 20000);
            return r.Ok ? r.StdOut.TrimEnd('\n', '\r').TrimStart('\uFEFF') : null;
        }

        /// <summary>Авторы из журнала, по убыванию числа коммитов — для фильтра.</summary>
        public static async Task<List<string>> AuthorsAsync(int scan)
        {
            var list = new List<string>();
            var r = await GitOperations.Git("log --all -n " + scan + " --format=%an", 30000);
            if (!r.Ok) return list;

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in r.StdOut.Split('\n'))
            {
                var s = line.Trim();
                if (s.Length == 0) continue;
                int n;
                seen[s] = seen.TryGetValue(s, out n) ? n + 1 : 1;
            }

            list.AddRange(seen.Keys);
            list.Sort((a, b) => seen[b].CompareTo(seen[a]));
            return list;
        }

        // ------------------------------------------------- содержимое коммита ---

        /// <summary>
        /// Файлы, затронутые коммитом.
        ///
        /// diff-tree, а не show: у show для слияния по умолчанию сводный diff,
        /// и --name-status по нему не печатает ничего. --first-parent даёт
        /// понятную картину «что принесло это слияние в текущую ветку»,
        /// --root — работающий разбор самого первого коммита.
        /// </summary>
        public static async Task<List<GitCommitFile>> FilesAsync(string sha)
        {
            var files = new List<GitCommitFile>();

            var r = await GitOperations.Git(
                "-c core.quotepath=false diff-tree --no-commit-id --name-status -r -z -M --root " +
                "--first-parent " + GitOperations.Q(sha), 60000);

            if (!r.Ok) return files;

            // Поля идут подряд через NUL: «M path», а для переименования —
            // «R100 старый новый», то есть на одну запись приходится три поля.
            var f = r.StdOut.Split('\0');
            for (int i = 0; i + 1 < f.Length; )
            {
                var code = f[i];
                if (code.Length == 0) { i++; continue; }

                bool renamed = code[0] == 'R' || code[0] == 'C';
                if (renamed && i + 2 >= f.Length) break;

                var file = new GitCommitFile { Status = StatusOf(code[0]) };

                if (renamed)
                {
                    file.OriginalGitPath = f[i + 1];
                    file.OriginalPath = GitRepository.ToProjectPath(f[i + 1]);
                    file.GitPath = f[i + 2];
                    i += 3;
                }
                else
                {
                    file.GitPath = f[i + 1];
                    i += 2;
                }

                file.ProjectPath = GitRepository.ToProjectPath(file.GitPath);
                files.Add(file);
            }

            return files;
        }

        private static GitCommitFileStatus StatusOf(char c)
        {
            switch (c)
            {
                case 'A': return GitCommitFileStatus.Added;
                case 'D': return GitCommitFileStatus.Deleted;
                case 'R': return GitCommitFileStatus.Renamed;
                case 'C': return GitCommitFileStatus.Copied;
                case 'T': return GitCommitFileStatus.TypeChanged;
                default: return GitCommitFileStatus.Modified;
            }
        }

        /// <summary>
        /// Патч одного файла внутри коммита.
        ///
        /// Для переименованного файла в отбор идут оба пути: с одним новым git
        /// не видит, откуда файл пришёл, и показывает его целиком добавленным.
        /// </summary>
        public static async Task<FileDiff> DiffAsync(string sha, string gitPath, string originalGitPath = null)
        {
            if (string.IsNullOrEmpty(gitPath)) return null;

            var paths = GitOperations.Q(gitPath);
            if (!string.IsNullOrEmpty(originalGitPath) && originalGitPath != gitPath)
                paths = GitOperations.Q(originalGitPath) + " " + paths;

            var r = await GitOperations.Git(
                "-c core.quotepath=false diff-tree -p -U3 -M --root --first-parent --no-commit-id " +
                GitOperations.Q(sha) + " -- " + paths, 60000);

            return r.Ok ? GitDiffParser.Parse(r.StdOut) : null;
        }

        /// <summary>
        /// Содержимое файла в ревизии байтами — для картинок и указателей LFS.
        /// Через файл, а не через текстовый stdout: чтение как UTF-8 портит
        /// двоичные данные безвозвратно.
        /// </summary>
        public static async Task<byte[]> ShowBytesAsync(string rev, string gitPath)
        {
            if (string.IsNullOrEmpty(rev) || string.IsNullOrEmpty(gitPath)) return null;

            var temp = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "levgitlab_" + Guid.NewGuid().ToString("N"));

            try
            {
                var ok = await GitProcess.RunToFileAsync(
                    GitRepository.GitExe, "show " + GitOperations.Q(rev + ":" + gitPath),
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

        // ------------------------------------------------------------ диапазон ---

        /// <summary>
        /// Файлы, изменённые между двумя коммитами — например, всё, что принёс
        /// merge request от точки ответвления до своей вершины.
        /// </summary>
        public static async Task<List<GitCommitFile>> RangeFilesAsync(string baseSha, string headSha)
        {
            var files = new List<GitCommitFile>();

            var r = await GitOperations.Git(
                "-c core.quotepath=false diff --name-status -z -M " +
                GitOperations.Q(baseSha) + " " + GitOperations.Q(headSha), 60000);
            if (!r.Ok) return files;

            var f = r.StdOut.Split('\0');
            for (int i = 0; i + 1 < f.Length; )
            {
                var code = f[i];
                if (code.Length == 0) { i++; continue; }

                bool renamed = code[0] == 'R' || code[0] == 'C';
                if (renamed && i + 2 >= f.Length) break;

                var file = new GitCommitFile { Status = StatusOf(code[0]) };
                if (renamed)
                {
                    file.OriginalGitPath = f[i + 1];
                    file.OriginalPath = GitRepository.ToProjectPath(f[i + 1]);
                    file.GitPath = f[i + 2];
                    i += 3;
                }
                else
                {
                    file.GitPath = f[i + 1];
                    i += 2;
                }

                file.ProjectPath = GitRepository.ToProjectPath(file.GitPath);
                files.Add(file);
            }

            return files;
        }

        /// <summary>Патч одного файла между двумя коммитами.</summary>
        public static async Task<FileDiff> RangeDiffAsync(string baseSha, string headSha, string gitPath, string originalGitPath = null)
        {
            if (string.IsNullOrEmpty(gitPath)) return null;

            var paths = GitOperations.Q(gitPath);
            if (!string.IsNullOrEmpty(originalGitPath) && originalGitPath != gitPath)
                paths = GitOperations.Q(originalGitPath) + " " + paths;

            var r = await GitOperations.Git(
                "-c core.quotepath=false diff -p -U3 -M " + GitOperations.Q(baseSha) + " " + GitOperations.Q(headSha) +
                " -- " + paths, 60000);

            return r.Ok ? GitDiffParser.Parse(r.StdOut) : null;
        }

        /// <summary>Пустое дерево git: сторона сравнения, на которой файла ещё или уже нет.</summary>
        public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

        /// <summary>Патч одного файла между ревизией и рабочей копией.</summary>
        public static async Task<FileDiff> WorktreeDiffAsync(string rev, string gitPath)
        {
            if (string.IsNullOrEmpty(gitPath) || string.IsNullOrEmpty(rev)) return null;

            var r = await GitOperations.Git(
                "-c core.quotepath=false diff -p -U3 -M " + GitOperations.Q(rev) + " -- " + GitOperations.Q(gitPath), 60000);

            return r.Ok ? GitDiffParser.Parse(r.StdOut) : null;
        }

        /// <summary>Есть ли коммит в локальном репозитории — или его сначала надо забрать с сервера.</summary>
        public static async Task<bool> HasCommitAsync(string sha)
        {
            if (string.IsNullOrEmpty(sha)) return false;
            var r = await GitOperations.Git("cat-file -e " + GitOperations.Q(sha + "^{commit}"), 15000);
            return r.Ok;
        }

        /// <summary>Содержимое файла на момент коммита — для семантического сравнения сцен.</summary>
        public static async Task<string> ShowTextAsync(string sha, string gitPath)
        {
            var r = await GitOperations.Git(
                "show " + GitOperations.Q(sha + ":" + gitPath), 60000);
            return r.Ok ? r.StdOut : null;
        }

        // ------------------------------------------------------------- ветки ---

        public static async Task<List<GitBranchInfo>> BranchesAsync()
        {
            var list = new List<GitBranchInfo>();

            // Разделитель — управляющий символ, а не '|': вертикальная черта
            // встречается и в заголовках коммитов, и в именах веток она законна.
            const string Sep = "\u001F";
            var format = string.Join(Sep, new[]
            {
                "%(refname:short)", "%(objectname)", "%(upstream:short)",
                "%(upstream:track,nobracket)", "%(committerdate:iso8601-strict)",
                "%(HEAD)", "%(refname)", "%(contents:subject)"
            });

            var r = await GitOperations.Git(
                "for-each-ref --sort=-committerdate --format=" + GitOperations.Q(format) +
                " refs/heads refs/remotes", 30000);

            if (!r.Ok) return list;

            foreach (var line in r.StdOut.Split('\n'))
            {
                var s = line.TrimEnd('\r');
                if (s.Length == 0) continue;

                var f = s.Split(new[] { Sep }, StringSplitOptions.None);
                if (f.Length < 8) continue;

                // origin/HEAD — это указатель на ветку по умолчанию, а не ветка.
                if (f[0].EndsWith("/HEAD", StringComparison.Ordinal)) continue;

                var b = new GitBranchInfo
                {
                    Name = f[0],
                    Sha = f[1],
                    Upstream = string.IsNullOrEmpty(f[2]) ? null : f[2],
                    IsCurrent = f[5].Trim() == "*",
                    // \u041F\u043E \u043F\u043E\u043B\u043D\u043E\u043C\u0443 \u0438\u043C\u0435\u043D\u0438, \u0430 \u043D\u0435 \u043F\u043E \u0441\u043B\u044D\u0448\u0443 \u0432 \u043A\u043E\u0440\u043E\u0442\u043A\u043E\u043C: \u043B\u043E\u043A\u0430\u043B\u044C\u043D\u0430\u044F \u0432\u0435\u0442\u043A\u0430
                    // \u0432\u043F\u043E\u043B\u043D\u0435 \u043C\u043E\u0436\u0435\u0442 \u043D\u0430\u0437\u044B\u0432\u0430\u0442\u044C\u0441\u044F feature/foo, \u0438 \u043F\u0440\u043E\u0432\u0435\u0440\u043A\u0430 \u043D\u0430 \u0441\u043B\u044D\u0448
                    // \u043E\u0431\u044A\u044F\u0432\u0438\u043B\u0430 \u0431\u044B \u0435\u0451 \u0443\u0434\u0430\u043B\u0451\u043D\u043D\u043E\u0439.
                    IsRemote = f[6].StartsWith("refs/remotes/", StringComparison.Ordinal),
                    Subject = f[7].TrimStart('\uFEFF')
                };

                DateTime d;
                if (DateTime.TryParse(f[4], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal |
                        System.Globalization.DateTimeStyles.AssumeUniversal, out d))
                    b.Date = d.ToLocalTime();

                ParseTrack(f[3], b);
                list.Add(b);
            }

            return list;
        }

        /// <summary>Разбирает «ahead 2, behind 1» или «gone».</summary>
        private static void ParseTrack(string track, GitBranchInfo b)
        {
            if (string.IsNullOrEmpty(track)) return;

            if (track.Contains("gone")) { b.Gone = true; return; }

            foreach (var part in track.Split(','))
            {
                var s = part.Trim();
                int value;

                if (s.StartsWith("ahead ", StringComparison.Ordinal) &&
                    int.TryParse(s.Substring(6), out value)) b.Ahead = value;

                else if (s.StartsWith("behind ", StringComparison.Ordinal) &&
                         int.TryParse(s.Substring(7), out value)) b.Behind = value;
            }
        }

        // ---------------------------------------------------------- действия ---

        /// <summary>
        /// Создаёт ветку от указанного коммита. Без checkout: переключение —
        /// отдельное решение, и делать его молча за пользователя нельзя.
        /// </summary>
        public static Task<ProcessResult> CreateBranchAsync(string name, string startPoint)
        {
            return GitOperations.Git(
                "branch " + GitOperations.Q(name) + " " + GitOperations.Q(startPoint), 30000);
        }

        public static Task<ProcessResult> CreateBranchAndCheckoutAsync(string name, string startPoint)
        {
            return GitOperations.WithAssetGuard(() => GitOperations.LongGit(
                L.F("Creating branch {0}", name),
                "checkout --progress -b " + GitOperations.Q(name) + " " + GitOperations.Q(startPoint),
                300000));
        }

        /// <summary>Переход на конкретный коммит: рабочая копия окажется в detached HEAD.</summary>
        public static Task<ProcessResult> CheckoutCommitAsync(string sha)
        {
            return GitOperations.WithAssetGuard(() => GitOperations.LongGit(
                L.F("Checking out {0}", Short(sha)), "checkout --progress " + GitOperations.Q(sha), 300000));
        }

        /// <summary>
        /// Переносит коммит в текущую ветку. Для слияния нужен -m 1: у него два
        /// родителя, и git сам не выберет, относительно какого считать разницу.
        /// </summary>
        public static Task<ProcessResult> CherryPickAsync(string sha, bool isMerge, bool noCommit)
        {
            var args = "cherry-pick" + (isMerge ? " -m 1" : "") + (noCommit ? " --no-commit" : "") +
                       " " + GitOperations.Q(sha);
            return GitOperations.WithAssetGuard(() => GitOperations.Git(args, 120000));
        }

        public static Task<ProcessResult> RevertAsync(string sha, bool isMerge, bool noCommit)
        {
            var args = "revert" + (isMerge ? " -m 1" : "") + (noCommit ? " --no-commit" : "") +
                       " " + GitOperations.Q(sha);
            return GitOperations.WithAssetGuard(() => GitOperations.Git(args, 120000));
        }

        /// <summary>
        /// Коммиты текущей ветки, которых нет ни на одном remote (по данным последнего
        /// fetch). Их историю можно переписать, никому не помешав.
        /// </summary>
        public static async Task<System.Collections.Generic.HashSet<string>> LocalCommitsAsync(int limit = 500)
        {
            var set = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            if (GitStatusCache.IsInitialCommit) return set;

            var r = await GitOperations.Git("rev-list -n " + limit + " HEAD --not --remotes", 20000);
            if (!r.Ok) return set;

            foreach (var line in r.StdOut.Split('\n'))
            {
                var sha = line.Trim();
                if (sha.Length > 0) set.Add(sha);
            }
            return set;
        }

        /// <summary>
        /// Меняет сообщение локального коммита.
        ///
        /// Последний коммит — через <c>commit --amend --only</c>: изменения, уже
        /// отмеченные к коммиту, в него не попадут. Более ранний — новым объектом
        /// коммита с тем же деревом, родителями, автором и датой; следующие за ним
        /// коммиты переносятся на него, и содержимое у них не меняется. Слияния git
        /// при таком переносе не сохраняет, поэтому отрезок со слияниями отклоняется.
        /// </summary>
        public static async Task<ProcessResult> RewordAsync(string sha, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return new ProcessResult { ExitCode = 1, StdErr = L.T("The message can't be empty.") };

            var head = await GitOperations.Git("rev-parse HEAD", 15000);
            if (!head.Ok) return head;

            if (head.StdOut.Trim() == sha)
                return await GitOperations.Git("commit --amend --only --allow-empty -F -", 60000, message);

            var info = await GitOperations.Git(
                "log -1 --date=raw --format=%P%x00%an%x00%ae%x00%ad " + GitOperations.Q(sha), 15000);
            if (!info.Ok) return info;

            var parts = info.StdOut.TrimEnd('\n', '\r').Split('\0');
            if (parts.Length < 4)
                return new ProcessResult { ExitCode = 1, StdErr = L.F("Failed to read commit {0}.", Short(sha)) };

            var parents = parts[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var merges = await GitOperations.Git("rev-list --merges " + GitOperations.Q(sha + "..HEAD"), 20000);
            if (parents.Length > 1 || (merges.Ok && merges.StdOut.Trim().Length > 0))
                return new ProcessResult
                {
                    ExitCode = 1,
                    StdErr = L.T("There are merge commits between this commit and the last one: rewriting would lose them. " +
                                 "Only commits on a straight line can be edited here.")
                };

            // Новый коммит: то же содержимое и те же родители, другое сообщение.
            var args = new System.Text.StringBuilder("commit-tree " + GitOperations.Q(sha + "^{tree}"));
            foreach (var p in parents) args.Append(" -p ").Append(GitOperations.Q(p));
            args.Append(" -F -");

            var author = new System.Collections.Generic.Dictionary<string, string>
            {
                { "GIT_AUTHOR_NAME", parts[1] },
                { "GIT_AUTHOR_EMAIL", parts[2] },
                { "GIT_AUTHOR_DATE", parts[3] }
            };

            var created = await GitProcess.RunAsync(GitRepository.GitExe, args.ToString(), GitRepository.RepoRoot,
                message, 30000, default(System.Threading.CancellationToken), null, false, false, author);
            if (!created.Ok) return created;

            // Следующие коммиты — поверх нового. Деревья те же, поэтому конфликтов не бывает;
            // незакоммиченные правки на время переноса убираются и возвращаются (--autostash).
            return await GitOperations.WithAssetGuard(() => GitOperations.LongGit(
                L.F("Rewriting history after {0}", Short(sha)),
                "rebase --autostash --onto " + GitOperations.Q(created.StdOut.Trim()) + " " + GitOperations.Q(sha), 300000));
        }

        public static Task<ProcessResult> ResetAsync(string sha, string mode)
        {
            return GitOperations.WithAssetGuard(() => GitOperations.LongGit(
                L.F("Resetting to {0}", Short(sha)), "reset --" + mode + " " + GitOperations.Q(sha), 120000));
        }

        /// <summary>
        /// Переход на ветку. Для удалённой git сам заводит локальную с тем же
        /// именем и настроенным upstream — это его штатное поведение, и
        /// воспроизводить его вручную через -b --track значит спотыкаться там,
        /// где локальная ветка уже есть.
        /// </summary>
        public static Task<ProcessResult> CheckoutBranchAsync(GitBranchInfo branch)
        {
            var name = branch.IsRemote ? StripRemote(branch.Name) : branch.Name;

            return GitOperations.WithAssetGuard(() => GitOperations.LongGit(
                L.F("Checking out {0}", name), "checkout --progress " + GitOperations.Q(name), 300000));
        }

        /// <summary>Сливает ветку в текущую.</summary>
        public static Task<ProcessResult> MergeAsync(string name, bool noFastForward)
        {
            var args = "merge" + (noFastForward ? " --no-ff" : "") + " " + GitOperations.Q(name);
            return GitOperations.WithAssetGuard(() => GitOperations.LongGit(
                L.F("Merging {0}", name), args, 300000));
        }

        /// <summary>
        /// Удаляет локальную ветку. Без force git откажется удалять ветку,
        /// изменения которой никуда не влиты, — и правильно сделает.
        /// </summary>
        public static Task<ProcessResult> DeleteBranchAsync(string name, bool force)
        {
            return GitOperations.Git("branch " + (force ? "-D " : "-d ") + GitOperations.Q(name), 30000);
        }

        /// <summary>Удаляет ветку на сервере: push с пустым источником.</summary>
        public static Task<ProcessResult> DeleteRemoteBranchAsync(string remoteBranch)
        {
            int slash = remoteBranch.IndexOf('/');
            if (slash <= 0)
                return Task.FromResult(new ProcessResult
                {
                    ExitCode = 1,
                    StdErr = L.F("Cannot parse the remote branch name: {0}", remoteBranch)
                });

            var remote = remoteBranch.Substring(0, slash);
            var name = remoteBranch.Substring(slash + 1);

            return GitOperations.LongGit(L.F("Deleting {0}", remoteBranch),
                "push --progress " + GitOperations.Q(remote) + " --delete " + GitOperations.Q(name),
                300000);
        }

        /// <summary>Отправляет ветку, при необходимости заводя ей upstream.</summary>
        public static Task<ProcessResult> PushBranchAsync(string name, bool setUpstream)
        {
            var remote = GitRepository.SelectedRemoteName;
            var args = "push --progress " + (setUpstream ? "-u " : "") +
                       GitOperations.Q(remote) + " " + GitOperations.Q(name);

            return GitOperations.LongGit("Push " + name, args, 600000);
        }

        /// <summary>«origin/feature» → «feature».</summary>
        public static string StripRemote(string remoteBranch)
        {
            if (string.IsNullOrEmpty(remoteBranch)) return remoteBranch;
            int slash = remoteBranch.IndexOf('/');
            return slash > 0 ? remoteBranch.Substring(slash + 1) : remoteBranch;
        }

        public static Task<ProcessResult> TagAsync(string name, string sha, string message)
        {
            // Аннотированный тег, если есть текст: у него остаются автор и дата,
            // и он попадает в описание версии. Иначе лёгкий.
            var args = string.IsNullOrEmpty(message)
                ? "tag " + GitOperations.Q(name) + " " + GitOperations.Q(sha)
                : "tag -a " + GitOperations.Q(name) + " " + GitOperations.Q(sha) + " -F -";

            return GitOperations.Git(args, 30000, string.IsNullOrEmpty(message) ? null : message);
        }

        public static string Short(string sha)
        {
            return string.IsNullOrEmpty(sha) ? string.Empty
                 : (sha.Length <= 7 ? sha : sha.Substring(0, 7));
        }
    }
}
