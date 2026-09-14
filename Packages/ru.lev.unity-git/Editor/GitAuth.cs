using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lev.Git
{
    public enum GitAuthProblem
    {
        None = 0,
        /// <summary>Remote по SSH, ключ не принят. Пароль тут спросить не у кого.</summary>
        SshKey,
        /// <summary>Remote по http(s), учётных данных нет или они отвергнуты.</summary>
        HttpCredentials
    }

    /// <summary>
    /// Разбор отказов аутентификации и запись учётных данных в системный
    /// credential helper.
    ///
    /// Почему git молчит вместо того, чтобы спросить пароль: в
    /// <see cref="GitProcess"/> выставлен GIT_TERMINAL_PROMPT=0. Без него git,
    /// запущенный без консоли, повис бы на невидимом приглашении ввода, и
    /// редактор пришлось бы снимать через диспетчер задач. Поэтому спрашивать
    /// должен сам плагин — а git получает готовый ответ.
    /// </summary>
    public static class GitAuth
    {
        public static GitAuthProblem Classify(ProcessResult r, GitRemote remote)
        {
            if (r == null || r.Ok || r.Canceled) return GitAuthProblem.None;

            var text = ((r.StdErr ?? string.Empty) + "\n" + (r.StdOut ?? string.Empty));

            if (Has(text, "Permission denied (publickey")
                || Has(text, "Host key verification failed")
                || Has(text, "no matching host key")
                || Has(text, "Permission denied (password,publickey")
                || (Has(text, "Could not read from remote repository") && remote != null && remote.IsSsh))
                return GitAuthProblem.SshKey;

            if (Has(text, "Authentication failed")
                || Has(text, "could not read Username")
                || Has(text, "could not read Password")
                || Has(text, "HTTP Basic: Access denied")
                || Has(text, "terminal prompts disabled")
                || Has(text, "The requested URL returned error: 401")
                || Has(text, "The requested URL returned error: 403"))
                return GitAuthProblem.HttpCredentials;

            return GitAuthProblem.None;
        }

        private static bool Has(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Кладёт учётные данные в системный credential helper — тот же, куда их
        /// пишет Unity Hub. Сам плагин ничего не хранит: в проекте секретов нет,
        /// и удалить их можно штатными средствами git.
        ///
        /// Токен уходит через stdin, а не в аргументах: аргументы попадают и в
        /// журнал команд, и в список процессов операционной системы.
        /// </summary>
        public static async Task<ProcessResult> ApproveAsync(
            string protocol, string host, string username, string secret)
        {
            var input = string.Format(
                "protocol={0}\nhost={1}\nusername={2}\npassword={3}\n\n",
                protocol, host, username, secret);

            return await GitProcess.RunAsync(
                GitRepository.GitExe, "credential approve", GitRepository.RepoRoot,
                input, 20000, default(CancellationToken), null, false, true);
        }

        /// <summary>Забывает сохранённую запись — нужно, когда токен отозван или протух.</summary>
        public static async Task<ProcessResult> RejectAsync(string protocol, string host)
        {
            var input = string.Format("protocol={0}\nhost={1}\n\n", protocol, host);

            return await GitProcess.RunAsync(
                GitRepository.GitExe, "credential reject", GitRepository.RepoRoot,
                input, 20000, default(CancellationToken), null, false, true);
        }

        /// <summary>Переключает адрес remote — например с ssh на https.</summary>
        public static Task<ProcessResult> SetRemoteUrlAsync(string remoteName, string url)
        {
            return GitProcess.RunAsync(
                GitRepository.GitExe,
                "remote set-url \"" + remoteName + "\" \"" + url + "\"",
                GitRepository.RepoRoot, null, 20000);
        }

        /// <summary>Адрес основного remote по http(s). См. <see cref="HttpUrlFor"/>.</summary>
        public static string HttpUrlForCurrentProject()
        {
            return HttpUrlFor(GitRepository.Remote);
        }

        /// <summary>
        /// Адрес того же репозитория по http(s) для ssh-remote: тот же хост и путь.
        /// Адрес от интеграции берётся, только если она про этот же сервер, —
        /// иначе для remote на GitHub предлагался адрес GitLab. null — собрать не из чего.
        /// </summary>
        public static string HttpUrlFor(GitRemote remote)
        {
            if (remote == null || !remote.IsSsh || string.IsNullOrEmpty(remote.Host) || string.IsNullOrEmpty(remote.FullPath))
                return null;

            foreach (var integration in GitIntegrations.Enabled)
            {
                string url;
                try { url = integration.HttpCloneUrl; }
                catch { continue; }

                Uri uri;
                if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                    string.Equals(uri.Host, remote.Host, StringComparison.OrdinalIgnoreCase))
                    return url;
            }

            return "https://" + remote.Host + "/" + remote.FullPath + ".git";
        }

        /// <summary>
        /// Какой remote отказал — по тексту ошибки git: «git@github.com: Permission denied»
        /// или «unable to access 'https://host/…'». Операция могла идти не в основной
        /// remote, и чинить нужно именно тот, куда она шла. null — не узнать.
        /// </summary>
        public static GitRemote RemoteFromError(ProcessResult r, out string remoteName)
        {
            remoteName = null;
            if (r == null) return null;

            var text = (r.StdErr ?? string.Empty) + "\n" + (r.StdOut ?? string.Empty);
            string host = null;

            var ssh = System.Text.RegularExpressions.Regex.Match(text,
                @"(?:[\w.\-]+@)?(?<host>[\w\-]+(?:\.[\w\-]+)+):\s*Permission denied");
            if (ssh.Success) host = ssh.Groups["host"].Value;

            if (host == null)
            {
                var http = System.Text.RegularExpressions.Regex.Match(text, @"unable to access '(?<url>[^']+)'");
                Uri uri;
                if (http.Success && Uri.TryCreate(http.Groups["url"].Value, UriKind.Absolute, out uri)) host = uri.Host;
            }

            if (host == null) return null;

            // Основной remote — первым: если на тот же сервер смотрят два remote, вероятнее он.
            GitRemote found = null;
            foreach (var entry in GitRepository.Remotes)
            {
                var parsed = GitRepository.ParseRemote(entry.PushUrl ?? entry.FetchUrl);
                if (parsed == null || !string.Equals(parsed.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
                if (found == null || entry.Name == GitRepository.SelectedRemoteName)
                {
                    found = parsed;
                    remoteName = entry.Name;
                }
            }
            return found;
        }
    }
}
