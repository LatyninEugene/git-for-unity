using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Lev.Git
{
    /// <summary>
    /// Проверки имени и адреса remote до того, как их увидит git.
    ///
    /// Git и сам откажет, но его сообщение сформулировано для консоли. Здесь
    /// ошибка называется до нажатия «Сохранить», и ловится то, что git
    /// молча примет: токен, вписанный прямо в адрес. Такой адрес ляжет в
    /// .git/config открытым текстом и попадёт в журнал команд.
    /// </summary>
    public static class GitRemoteRules
    {
        private static readonly Regex ScpLike = new Regex(@"^(?:[^@/\s]+@)?[^:/\s]{2,}:[^\s]+$");

        /// <returns>Текст ошибки или null, если имя годится.</returns>
        public static string ValidateName(string name, IEnumerable<string> existing, string current = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return L.T("Name is not set.");
            name = name.Trim();

            if (name.StartsWith("-", StringComparison.Ordinal) || name.StartsWith("/", StringComparison.Ordinal) ||
                name.EndsWith("/", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal) ||
                name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) || name.Contains("..") || name.Contains("//") ||
                name.Contains("@{"))
                return L.T("git will not accept this name. Usually it is a single word: origin, upstream, backup.");

            foreach (char c in name)
                if (char.IsWhiteSpace(c) || c < 32 || "~^:?*[\\\"'".IndexOf(c) >= 0)
                    return L.F("The name contains an invalid character “{0}”.", char.IsWhiteSpace(c) ? L.Tc("character", "space") : c.ToString());

            if (existing != null)
                foreach (var e in existing)
                {
                    if (current != null && string.Equals(e, current, StringComparison.Ordinal)) continue;
                    // На Windows ссылки git нечувствительны к регистру: origin и Origin — одно и то же.
                    if (string.Equals(e, name, StringComparison.OrdinalIgnoreCase))
                        return L.F("A remote named “{0}” already exists.", e);
                }

            return null;
        }

        /// <returns>Текст ошибки или null, если адрес годится.</returns>
        public static string ValidateUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return L.T("URL is not set.");
            url = url.Trim();

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || string.IsNullOrEmpty(uri.Host))
                    return L.T("Cannot parse the URL. Example: http://gitlab.lev.ru/Latynin/TestGitLab.git");

                if (!string.IsNullOrEmpty(uri.UserInfo) && uri.UserInfo.Contains(":"))
                    return L.T("The URL contains a password or token. Remove it: it would be stored in .git/config in plain text and end up " +
                               "in the command log. git will ask for credentials itself on first access.");

                return url.IndexOf(' ') >= 0 ? L.T("The URL contains a space.") : null;
            }

            if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("git://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri;
                return Uri.TryCreate(url, UriKind.Absolute, out uri) ? null : L.T("Cannot parse the URL.");
            }

            // Локальный путь: C:\repos\x.git, /srv/git/x.git, ..\x.git
            if (IsLocalPath(url)) return null;

            if (ScpLike.IsMatch(url)) return null;

            return L.T("This does not look like a repository URL. Examples: http://host/group/repo.git, git@host:group/repo.git, ssh://git@host:2424/group/repo.git");
        }

        public static bool IsLocalPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (url.Length >= 3 && char.IsLetter(url[0]) && url[1] == ':' && (url[2] == '\\' || url[2] == '/')) return true;
            return url.StartsWith("/", StringComparison.Ordinal) || url.StartsWith("./", StringComparison.Ordinal) ||
                   url.StartsWith("../", StringComparison.Ordinal) || url.StartsWith(".\\", StringComparison.Ordinal) ||
                   url.StartsWith("..\\", StringComparison.Ordinal) || url.StartsWith("\\\\", StringComparison.Ordinal);
        }
    }
}
