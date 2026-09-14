using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>Разобранный адрес git-remote.</summary>
    public sealed class GitRemote
    {
        public string Raw;
        public string Scheme;    // http / https / ssh
        public string Host;      // gitlab.lev.ru
        public int Port;         // 0, если в адресе не указан
        public string FullPath;  // Latynin/TestGitLab

        public bool IsSsh => Scheme == "ssh";

        public string HostPort => Port > 0 ? Host + ":" + Port : Host;

        /// <summary>
        /// Веб-адрес инстанса, выведенный из remote.
        /// Для http(s) это точный адрес. Для ssh — догадка: порт SSH (например 2424)
        /// к веб-интерфейсу отношения не имеет, поэтому берётся https на том же
        /// хосте без порта. Если инстанс живёт иначе — задай адрес вручную.
        /// </summary>
        public string GuessedBaseUrl;

        public bool BaseUrlIsGuess;
    }

    /// <summary>Строка из `git remote -v`.</summary>
    public sealed class GitRemoteEntry
    {
        public string Name;
        public string FetchUrl;
        public string PushUrl;

        public bool SameBothWays =>
            string.Equals(FetchUrl, PushUrl, StringComparison.Ordinal);
    }

    public sealed class GitCredential
    {
        public string Username;
        public string Token;
        public string Protocol;
        public string Host;
    }

    /// <summary>
    /// Обнаружение git-репозитория вокруг Unity-проекта, перевод путей и
    /// разбор remote'ов.
    ///
    /// Важное разделение: «куда пушит git» и «где живёт веб/API GitLab» — это
    /// две разные вещи. При ssh-remote вторая из первой не выводится, поэтому
    /// адрес инстанса можно задать руками в настройках.
    /// </summary>
    public static class GitRepository
    {
        public const string GitExe = "git";

        public static string RepoRoot { get; private set; }

        /// <summary>Префикс каталога проекта относительно корня репозитория ("" или "sub/dir/").</summary>
        public static string PathPrefix { get; private set; } = string.Empty;

        public static List<GitRemoteEntry> Remotes { get; private set; } = new List<GitRemoteEntry>();

        /// <summary>Разобранный push-адрес выбранного remote.</summary>
        public static GitRemote Remote { get; private set; }

        /// <summary>Upstream текущей ветки, например "origin/main". null, если не задан.</summary>
        public static string Upstream { get; set; }

        public static bool IsRepo => !string.IsNullOrEmpty(RepoRoot);

        public static string ProjectRoot
        {
            get
            {
                var parent = Directory.GetParent(Application.dataPath);
                return parent == null ? Application.dataPath : Norm(parent.FullName);
            }
        }

        private static string Norm(string p)
        {
            if (string.IsNullOrEmpty(p)) return string.Empty;
            return p.Replace('\\', '/').TrimEnd('/');
        }

        // ------------------------------------------------- effective config ---

        public static string SelectedRemoteName
        {
            get
            {
                var n = GitSettings.instance.remoteName;
                return string.IsNullOrWhiteSpace(n) ? "origin" : n.Trim();
            }
        }

        // --------------------------------------------------------- discovery ---

        public static bool Locate()
        {
            RepoRoot = null;
            PathPrefix = string.Empty;
            Remote = null;
            Remotes = new List<GitRemoteEntry>();

            var projectRoot = ProjectRoot;

            var top = GitProcess.Run(GitExe, "rev-parse --show-toplevel", projectRoot, null, 15000);
            if (!top.Ok) return false;

            var root = Norm(top.StdOut.Trim());
            if (string.IsNullOrEmpty(root)) return false;
            RepoRoot = root;

            if (string.Equals(projectRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                PathPrefix = string.Empty;
            }
            else if (projectRoot.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                PathPrefix = projectRoot.Substring(root.Length + 1) + "/";
            }
            else
            {
                RepoRoot = null;
                return false;
            }

            LoadRemotes();
            return true;
        }

        public static void LoadRemotes()
        {
            var list = new List<GitRemoteEntry>();
            var r = GitProcess.Run(GitExe, "remote -v", RepoRoot, null, 15000);

            if (r.Ok)
            {
                var byName = new Dictionary<string, GitRemoteEntry>(StringComparer.Ordinal);
                foreach (var line in r.StdOut.Split('\n'))
                {
                    var l = line.Trim();
                    if (l.Length == 0) continue;

                    // "origin\tssh://git@host:2424/ns/repo.git (fetch)"
                    var m = Regex.Match(l, @"^(?<name>\S+)\s+(?<url>.+?)\s+\((?<kind>fetch|push)\)$");
                    if (!m.Success) continue;

                    var name = m.Groups["name"].Value;
                    GitRemoteEntry e;
                    if (!byName.TryGetValue(name, out e))
                    {
                        e = new GitRemoteEntry { Name = name };
                        byName[name] = e;
                        list.Add(e);
                    }

                    if (m.Groups["kind"].Value == "fetch") e.FetchUrl = m.Groups["url"].Value;
                    else e.PushUrl = m.Groups["url"].Value;
                }
            }

            Remotes = list;

            // Разбираем push-адрес выбранного remote — именно туда уедет код.
            GitRemoteEntry selected = null;
            foreach (var e in list)
                if (e.Name == SelectedRemoteName) { selected = e; break; }
            if (selected == null && list.Count > 0) selected = list[0];

            Remote = selected != null ? ParseRemote(selected.PushUrl ?? selected.FetchUrl) : null;
        }

        /// <summary>Разбирает http(s)://, ssh:// и scp-подобный (git@host:ns/repo.git) адрес.</summary>
        public static GitRemote ParseRemote(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();

            // http(s)://[user@]host[:port]/ns/repo[.git]
            var m = Regex.Match(raw,
                @"^(?<scheme>https?)://(?:[^@/]+@)?(?<host>[^:/]+)(?::(?<port>\d+))?/(?<path>.+?)(?:\.git)?/?$");
            if (m.Success)
            {
                var port = ParsePort(m.Groups["port"]);
                var hostPort = port > 0 ? m.Groups["host"].Value + ":" + port : m.Groups["host"].Value;
                return new GitRemote
                {
                    Raw = raw,
                    Scheme = m.Groups["scheme"].Value,
                    Host = m.Groups["host"].Value,
                    Port = port,
                    FullPath = m.Groups["path"].Value.Trim('/'),
                    GuessedBaseUrl = m.Groups["scheme"].Value + "://" + hostPort,
                    BaseUrlIsGuess = false
                };
            }

            // ssh://[user@]host[:port]/ns/repo[.git]
            // Порт здесь настоящий, и его нельзя утащить в путь проекта.
            m = Regex.Match(raw,
                @"^ssh://(?:[^@/]+@)?(?<host>[^:/]+)(?::(?<port>\d+))?/(?<path>.+?)(?:\.git)?/?$");
            if (m.Success)
            {
                return new GitRemote
                {
                    Raw = raw,
                    Scheme = "ssh",
                    Host = m.Groups["host"].Value,
                    Port = ParsePort(m.Groups["port"]),
                    FullPath = m.Groups["path"].Value.Trim('/'),
                    // SSH-порт не имеет отношения к вебу — угадываем https без порта.
                    GuessedBaseUrl = "https://" + m.Groups["host"].Value,
                    BaseUrlIsGuess = true
                };
            }

            // scp-подобный: [user@]host:ns/repo[.git] — порта в этом синтаксисе нет,
            // всё после двоеточия является путём (так это трактует и сам git).
            m = Regex.Match(raw,
                @"^(?:[^@/:]+@)?(?<host>[^:/]{2,}):(?<path>[^:]+?)(?:\.git)?/?$");
            if (m.Success)
            {
                return new GitRemote
                {
                    Raw = raw,
                    Scheme = "ssh",
                    Host = m.Groups["host"].Value,
                    Port = 0,
                    FullPath = m.Groups["path"].Value.Trim('/'),
                    GuessedBaseUrl = "https://" + m.Groups["host"].Value,
                    BaseUrlIsGuess = true
                };
            }

            return null;
        }

        private static int ParsePort(Group g)
        {
            int port;
            return g.Success && int.TryParse(g.Value, out port) ? port : 0;
        }

        // ------------------------------------------------------------ paths ---

        public static string ToGitPath(string projectRelativePath)
        {
            if (string.IsNullOrEmpty(projectRelativePath)) return null;
            return PathPrefix + projectRelativePath.Replace('\\', '/');
        }

        /// <summary>
        /// Отпечаток файла в рабочей копии: размер и время последней записи.
        ///
        /// Нужен там, где показанное надо пересобирать не по расписанию, а по
        /// факту изменения: статус файла остаётся «M» и после того, как его
        /// переписали, поэтому одного статуса для сравнения не хватает.
        /// </summary>
        public static string WorktreeStamp(string projectRelativePath)
        {
            try
            {
                var full = System.IO.Path.Combine(ProjectRoot, projectRelativePath);
                var info = new System.IO.FileInfo(full);
                return info.Exists
                    ? info.Length.ToString() + ":" + info.LastWriteTimeUtc.Ticks.ToString()
                    : "0";
            }
            catch { return "0"; }
        }

        public static string ToProjectPath(string gitPath)
        {
            if (string.IsNullOrEmpty(gitPath)) return null;
            gitPath = gitPath.Replace('\\', '/');
            if (PathPrefix.Length == 0) return gitPath;
            return gitPath.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase)
                ? gitPath.Substring(PathPrefix.Length)
                : null;
        }
    }
}
