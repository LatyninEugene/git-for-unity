using System.Threading.Tasks;
using UnityEditor;

namespace Lev.Git.UI
{
    /// <summary>
    /// Что делать, когда сервер отклонил push: в ветке на нём есть коммиты,
    /// которых нет локально.
    ///
    /// Бывает по двум причинам, и выход у них разный. Последний коммит изменили
    /// после того, как отправили (amend), — тогда серверную версию нужно
    /// заменить. Кто-то другой отправил свои коммиты — тогда их нужно забрать и
    /// положить свои сверху. Первое узнаётся по тому, что у локального и
    /// серверного коммита общий родитель, — его и предлагаем первым.
    /// </summary>
    internal static class PushRecovery
    {
        /// <summary>Предлагает выход и выполняет выбранное. Возвращает итог — или исходную ошибку, если человек отказался.</summary>
        public static async Task<ProcessResult> OfferAsync(ProcessResult rejected)
        {
            if (rejected == null || !GitOperations.IsRejectedByServer(rejected)) return rejected;

            var branch = await GitOperations.CurrentBranchAsync();
            if (string.IsNullOrEmpty(branch)) return rejected;

            var upstream = GitStatusCache.Upstream;
            int slash = string.IsNullOrEmpty(upstream) ? -1 : upstream.IndexOf('/');
            var remote = slash > 0 ? upstream.Substring(0, slash) : GitRepository.SelectedRemoteName;
            bool setUpstream = slash <= 0;
            var tracking = remote + "/" + branch;

            // Решать по свежему состоянию сервера, а не по последнему fetch.
            var fetch = await GitOperations.FetchRemoteAsync(remote);
            if (!fetch.Ok) return rejected;

            bool amended = await GitOperations.LooksAmendedAsync(tracking);
            var title = L.T("Push Rejected");

            // Дальше: true — заменить на сервере, false — забрать и перенести свои коммиты.
            bool replace;
            if (amended)
            {
                int choice = EditorUtility.DisplayDialogComplex(title,
                    L.F("{0} on the server has the previous version of your last commit: the commit was changed after it had been pushed (amend).\n\n" +
                        "Replace the server version with yours (push --force)? If someone pushed to this branch in the meantime, the server refuses the replacement and nothing is lost.",
                        tracking),
                    L.T("Push --force"), L.T("Cancel"), L.T("Pull and Rebase"));
                if (choice == 1) return rejected;
                replace = choice == 0;
            }
            else
            {
                int choice = EditorUtility.DisplayDialogComplex(title,
                    L.F("The server has commits in {0} that you don't have.\n\n" +
                        "Pull them and put your commits on top (pull --rebase), or replace the server version with yours (force push)?",
                        tracking),
                    L.T("Pull and Rebase"), L.T("Cancel"), L.T("Push --force…"));
                if (choice == 1) return rejected;
                replace = choice == 2;

                if (replace && !EditorUtility.DisplayDialog(L.T("Push --force"),
                        L.F("Commits in {0} that you don't have will disappear from the server. Continue?", tracking),
                        L.T("Push --force"), L.T("Cancel")))
                    return rejected;
            }

            if (replace)
                return await GitOperations.ForcePushAsync(remote, branch, setUpstream, false);

            var pull = await GitOperations.PullRebaseAsync(remote, branch);
            if (!pull.Ok) return pull;
            return await GitOperations.PushToAsync(remote, branch, setUpstream);
        }
    }
}
