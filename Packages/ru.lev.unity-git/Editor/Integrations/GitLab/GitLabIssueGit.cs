using System.Threading.Tasks;

namespace Lev.Git.GitLab
{
    /// <summary>
    /// Ветка из задачи в локальном git: имя по шаблону проекта, основа — по
    /// настройке, существующая ветка задачи переиспользуется, а не плодится.
    /// </summary>
    public static class GitLabIssueGit
    {
        private static string Remote => GitRepository.SelectedRemoteName;

        public static string BranchNameFor(GlIssue issue, string username)
        {
            var s = GitLabSettings.instance;
            return GitLabWorkflow.BranchName(s.branchTemplate, issue.Iid, issue.Title, issue.Labels, username,
                                             s.branchTransliterate, s.branchTitleMaxLength, s.branchSeparator);
        }

        /// <summary>
        /// Ветка, которую для задачи уже завели: локальная или на сервере, с
        /// номером задачи в имени. Шаблон могли поменять — поэтому ищем по номеру.
        /// </summary>
        public static async Task<string> ExistingBranchAsync(int iid)
        {
            foreach (var b in await GitOperations.LocalBranchesAsync())
                if (GitLabWorkflow.IssueFromBranch(b) == iid) return b;

            foreach (var b in await GitLabMrGit.RemoteBranchesAsync())
                if (GitLabWorkflow.IssueFromBranch(b) == iid) return b;

            return null;
        }

        /// <summary>
        /// Перейти на ветку задачи, создав её при необходимости.
        /// </summary>
        /// <param name="baseBranch">Ветка на сервере, от которой отвести; null — от текущего состояния.</param>
        public static async Task<ProcessResult> StartBranchAsync(string name, string baseBranch, bool push)
        {
            var remote = Remote;

            if (await RefExists("refs/heads/" + name))
            {
                if (GitStatusCache.Branch == name) return Ok(L.F("Already on branch “{0}”.", name));
                if (!await SafeSwitch.AskAsync(name, L.T("Issue Branch"), L.F("Switch to branch “{0}”?", name)))
                    return Fail(L.T("Switch canceled."));
                return await GitOperations.CheckoutAsync(name);
            }

            // Ветку задачи уже завёл кто-то другой — продолжаем её, а не начинаем параллельную.
            await GitOperations.Git("fetch --no-tags --quiet " + GitOperations.Q(remote) + " " + GitOperations.Q(name), 120000);
            if (await RefExists("refs/remotes/" + remote + "/" + name))
            {
                if (!await SafeSwitch.AskAsync(remote + "/" + name, L.T("Issue Branch"),
                        L.F("Branch “{0}” already exists on the server. Fetch it and switch to it?", name)))
                    return Fail(L.T("Switch canceled."));

                return await GitOperations.WithAssetGuard(() => GitOperations.LongGit("Checkout " + name,
                    "checkout --progress -b " + GitOperations.Q(name) + " --track " + GitOperations.Q(remote + "/" + name), 300000));
            }

            string start = "HEAD";
            string startLabel = GitStatusCache.Branch ?? L.T("the current commit");
            if (!string.IsNullOrEmpty(baseBranch))
            {
                var fetch = await GitOperations.LongGit("Fetch " + baseBranch,
                    "fetch --no-tags --quiet " + GitOperations.Q(remote) + " " + GitOperations.Q(baseBranch), 300000);

                if (fetch.Ok && await RefExists("refs/remotes/" + remote + "/" + baseBranch)) start = remote + "/" + baseBranch;
                else if (await RefExists("refs/heads/" + baseBranch)) start = baseBranch;
                else return Fail(fetch.Ok
                    ? L.F("Branch “{0}” was not found on the server or locally.", baseBranch)
                    : L.F("Branch “{0}” was not found on the server or locally: {1}", baseBranch, fetch.Message));
                startLabel = start;
            }

            if (!await SafeSwitch.AskAsync(start, L.T("Issue Branch"), L.F("Create branch “{0}” from “{1}” and switch to it?", name, startLabel)))
                return Fail(L.T("Branch creation canceled."));

            // --no-track: иначе upstream новой ветки стал бы origin/main, и push ушёл бы не туда.
            var created = await GitOperations.WithAssetGuard(() => GitOperations.LongGit(L.F("Creating branch {0}", name),
                "checkout --progress --no-track -b " + GitOperations.Q(name) + " " + GitOperations.Q(start), 300000));
            if (!created.Ok || !push) return created;

            var pushed = await GitHistory.PushBranchAsync(name, true);
            return pushed.Ok ? Ok(L.F("Branch “{0}” created and pushed.", name)) : Fail(L.F("Branch created, but push failed: {0}", pushed.Message));
        }

        private static async Task<bool> RefExists(string refName)
        {
            return (await GitOperations.Git("rev-parse --verify --quiet " + GitOperations.Q(refName), 15000)).Ok;
        }

        private static ProcessResult Ok(string message) { return new ProcessResult { ExitCode = 0, StdOut = message }; }
        private static ProcessResult Fail(string message) { return new ProcessResult { ExitCode = 1, StdErr = message }; }
    }
}
