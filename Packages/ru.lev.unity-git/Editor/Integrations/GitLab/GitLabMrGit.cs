using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;

namespace Lev.Git.GitLab
{
    /// <summary>
    /// Та часть работы с merge request'ами, что происходит в локальном git:
    /// забрать коммиты MR, переключиться на его ветку, найти родительскую
    /// ветку, прочитать шаблоны описания, прибраться после слияния.
    /// </summary>
    public static class GitLabMrGit
    {
        private static string Remote => GitRepository.SelectedRemoteName;

        /// <summary>Коммиты MR в локальном репозитории — если их нет, забираются с сервера. null — всё на месте.</summary>
        public static async Task<string> EnsureCommitsAsync(GlMr mr)
        {
            var head = mr.HeadSha ?? mr.Sha;
            var baseSha = mr.BaseSha ?? mr.StartSha;
            if (string.IsNullOrEmpty(head) || string.IsNullOrEmpty(baseSha)) return L.T("GitLab did not report the commits of this MR.");

            if (await GitHistory.HasCommitAsync(head) && await GitHistory.HasCommitAsync(baseSha)) return null;

            // refs/merge-requests/N/head GitLab держит у каждого MR — в том числе
            // из форков и после удаления исходной ветки.
            var fetch = await GitOperations.LongGit(L.F("Fetching MR !{0}", mr.Iid),
                "fetch --no-tags --quiet " + GitOperations.Q(Remote) + " " +
                GitOperations.Q("refs/merge-requests/" + mr.Iid + "/head") + " " + GitOperations.Q(mr.TargetBranch), 300000);

            if (fetch.Ok && await GitHistory.HasCommitAsync(head) && await GitHistory.HasCommitAsync(baseSha)) return null;
            return fetch.Ok
                ? L.F("Could not fetch the MR commits from remote “{0}”.", Remote)
                : L.F("Could not fetch the MR commits from remote “{0}”: {1}", Remote, fetch.Message);
        }

        /// <summary>Переключение на ветку MR: забрать её, проверить безопасность, создать локальную с upstream или перейти на существующую.</summary>
        public static async Task<ProcessResult> CheckoutSourceAsync(GlMr mr)
        {
            var branch = mr.SourceBranch;
            if (GitStatusCache.Branch == branch) return Ok(L.F("Already on branch “{0}”.", branch));

            var fetch = await GitOperations.LongGit("Fetch " + branch,
                "fetch --no-tags --quiet " + GitOperations.Q(Remote) + " " + GitOperations.Q(branch), 300000);
            if (!fetch.Ok) return fetch;

            var remoteRef = Remote + "/" + branch;
            if (!await SafeSwitch.AskAsync(remoteRef, L.T("Switch to MR Branch"), L.F("Switch to branch “{0}” of merge request !{1}?", branch, mr.Iid)))
                return Fail(L.T("Switch canceled."));

            bool local = (await GitOperations.Git("rev-parse --verify --quiet " + GitOperations.Q("refs/heads/" + branch), 15000)).Ok;
            if (local) return await GitOperations.CheckoutAsync(branch);

            return await GitOperations.WithAssetGuard(() => GitOperations.LongGit("Checkout " + branch,
                "checkout --progress -b " + GitOperations.Q(branch) + " --track " + GitOperations.Q(remoteRef), 300000));
        }

        /// <summary>Заголовки коммитов текущей ветки, которых нет в целевой, — от старых к новым.</summary>
        public static async Task<List<string>> CommitSubjectsAsync(string targetBranch)
        {
            var list = new List<string>();
            var target = Remote + "/" + targetBranch;
            var exists = await GitOperations.Git("rev-parse --verify --quiet " + GitOperations.Q("refs/remotes/" + target), 15000);
            var range = exists.Ok ? GitOperations.Q(target) + "..HEAD" : "HEAD --not --remotes";

            var r = await GitOperations.Git("log --reverse --no-merges -n 100 --format=%s " + range, 30000);
            if (!r.Ok) return list;
            foreach (var line in r.StdOut.Split('\n'))
                if (line.Trim().Length > 0) list.Add(line.Trim());
            return list;
        }

        /// <summary>Ветки на основном remote — для выбора целевой.</summary>
        public static async Task<List<string>> RemoteBranchesAsync()
        {
            var list = new List<string>();
            var r = await GitOperations.Git("for-each-ref --format=%(refname:strip=3) " + GitOperations.Q("refs/remotes/" + Remote), 30000);
            if (!r.Ok) return list;
            foreach (var line in r.StdOut.Split('\n'))
            {
                var b = line.Trim();
                if (b.Length > 0 && b != "HEAD") list.Add(b);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>
        /// Ветка, от которой отведена текущая: та ветка на remote, до точки
        /// ответвления от которой меньше всего своих коммитов. null — не нашлась.
        /// </summary>
        public static async Task<string> ParentBranchAsync(string current)
        {
            string best = null;
            int bestDistance = int.MaxValue;

            var branches = await RemoteBranchesAsync();
            int checkedCount = 0;
            foreach (var b in branches)
            {
                if (b == current || checkedCount++ > 60) continue;

                var mb = await GitOperations.Git("merge-base HEAD " + GitOperations.Q(Remote + "/" + b), 15000);
                if (!mb.Ok) continue;

                var count = await GitOperations.Git("rev-list --count " + GitOperations.Q(mb.StdOut.Trim()) + "..HEAD", 15000);
                if (!count.Ok || !int.TryParse(count.StdOut.Trim(), out var distance)) continue;

                if (distance < bestDistance) { bestDistance = distance; best = b; }
            }
            return best;
        }

        /// <summary>Шаблоны описания MR из .gitlab/merge_request_templates рабочей копии: имя → текст.</summary>
        public static List<KeyValuePair<string, string>> LocalTemplates()
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                var dir = Path.Combine(GitRepository.RepoRoot, ".gitlab", "merge_request_templates");
                if (!Directory.Exists(dir)) return list;
                foreach (var file in Directory.GetFiles(dir, "*.md"))
                    list.Add(new KeyValuePair<string, string>(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)));

                // «Default» — шаблон по умолчанию и в самом GitLab: он первый.
                list.Sort((a, b) => a.Key.Equals("Default", StringComparison.OrdinalIgnoreCase) ? -1
                                  : b.Key.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 1
                                  : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
            return list;
        }

        // ---------------------------------------------------------- после слияния ---

        /// <summary>
        /// Уборка после слияния — каждое действие по настройке проекта:
        /// спросить, сделать сразу или никогда.
        /// </summary>
        /// <param name="changedGitPaths">Файлы MR — для снятия своих локов.</param>
        public static async Task<string> AfterMergeAsync(GlMr mr, List<string> changedGitPaths)
        {
            var s = GitLabSettings.instance;
            var notes = new List<string>();

            // Локи — до переключения ветки: список файлов MR ещё под рукой.
            if (changedGitPaths != null && changedGitPaths.Count > 0 && (AfterMergeAction)s.afterMergeUnlock != AfterMergeAction.Never)
            {
                await LfsLockCache.RefreshAsync();
                var mine = new List<LfsLockInfo>();
                foreach (var p in changedGitPaths)
                {
                    var l = LfsLockCache.Locks.Find(p);
                    if (l != null && l.Mine && !mine.Contains(l)) mine.Add(l);
                }

                if (mine.Count > 0 && Decide((AfterMergeAction)s.afterMergeUnlock, L.T("Unlock Files"),
                        L.F("The merge request is merged. You still hold locks on its files: {0}. Unlock them?", mine.Count),
                        L.Tc("after merge", "Unlock")))
                {
                    var r = await LfsLockOps.UnlockLocksAsync(mine, false);
                    notes.Add(r.Ok ? L.T("locks released") : L.F("locks not released: {0}", r.Message));
                }
            }

            bool onSource = GitStatusCache.Branch == mr.SourceBranch;

            if (onSource && Decide((AfterMergeAction)s.afterMergeCheckoutTarget, L.Fc("checkout", "Switch to “{0}”", mr.TargetBranch),
                    L.F("Switch to “{0}” and pull the merged changes into it?", mr.TargetBranch), L.Tc("after merge", "Switch")))
            {
                var fetch = await GitOperations.FetchAsync();
                if (fetch.Ok && await SafeSwitch.AskAsync(mr.TargetBranch, L.T("Switch Branch"), L.F("Switch to “{0}”?", mr.TargetBranch)))
                {
                    var checkout = await GitOperations.CheckoutAsync(mr.TargetBranch);
                    if (checkout.Ok)
                    {
                        await GitStatusCache.RefreshAsync();
                        var pull = await GitOperations.PullAsync();
                        notes.Add(pull.Ok ? L.F("switched to “{0}” and pulled", mr.TargetBranch) : L.F("pull failed: {0}", pull.Message));
                        onSource = false;
                    }
                    else notes.Add(L.F("switch failed: {0}", checkout.Message));
                }
            }

            if (!onSource && (AfterMergeAction)s.afterMergeDeleteLocalBranch != AfterMergeAction.Never)
            {
                bool exists = (await GitOperations.Git("rev-parse --verify --quiet " + GitOperations.Q("refs/heads/" + mr.SourceBranch), 15000)).Ok;
                if (exists && Decide((AfterMergeAction)s.afterMergeDeleteLocalBranch, L.T("Delete Local Branch"),
                        L.F("Delete local branch “{0}”? The merge request is merged on the server.", mr.SourceBranch), L.T("Delete")))
                    notes.Add(await DeleteLocalAsync(mr));
            }

            await GitStatusCache.RefreshAsync();
            return notes.Count == 0 ? null : string.Join("; ", notes);
        }

        /// <summary>
        /// Удаление слитой ветки. После squash git не считает её слитой, поэтому
        /// обычное -d откажет. Принудительно удаляем, только если в локальной
        /// ветке нет коммитов, которых не было в MR, — иначе пропала бы работа.
        /// </summary>
        private static async Task<string> DeleteLocalAsync(GlMr mr)
        {
            var soft = await GitHistory.DeleteBranchAsync(mr.SourceBranch, false);
            if (soft.Ok) return L.T("local branch deleted");

            var head = mr.HeadSha ?? mr.Sha;
            var extra = await GitOperations.Git("rev-list --count " + GitOperations.Q(head) + ".." + GitOperations.Q("refs/heads/" + mr.SourceBranch), 15000);
            if (extra.Ok && extra.StdOut.Trim() == "0")
            {
                var force = await GitHistory.DeleteBranchAsync(mr.SourceBranch, true);
                return force.Ok ? L.T("local branch deleted") : L.F("branch not deleted: {0}", force.Message);
            }

            return L.F("local branch “{0}” kept: it has commits that were not in the MR", mr.SourceBranch);
        }

        private static bool Decide(AfterMergeAction action, string title, string question, string yes)
        {
            switch (action)
            {
                case AfterMergeAction.Always: return true;
                case AfterMergeAction.Never: return false;
                default: return EditorUtility.DisplayDialog(title, question, yes, L.T("Not Now"));
            }
        }

        private static ProcessResult Ok(string message) { return new ProcessResult { ExitCode = 0, StdOut = message }; }
        private static ProcessResult Fail(string message) { return new ProcessResult { ExitCode = 1, StdErr = message }; }
    }
}
