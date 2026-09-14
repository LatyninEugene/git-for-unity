using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lev.Git.Diagnostics
{
    /// <summary>
    /// Вычистка секретов и приватных данных из текста журнала и отчёта.
    ///
    /// Секреты убираются всегда, без спроса: токены, пароли, заголовки
    /// авторизации, логин и пароль внутри адреса. Токен GitLab, кроме того,
    /// регистрируется здесь при чтении из хранилища — и вырезается по точному
    /// совпадению, как бы он ни оказался в тексте.
    ///
    /// Пути и адрес сервера скрываются по выбору человека — в отчёте.
    ///
    /// От Unity не зависит: проверяется чистыми тестами.
    /// </summary>
    internal static class Redactor
    {
        public const string Mask = "***";

        private static readonly object Sync = new object();
        private static string[] _secrets = new string[0];

        private static readonly Regex UserInfo = new Regex(
            @"(?<scheme>\b[a-z][a-z0-9+.\-]*://)[^/@\s""'<>]+@",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex AuthHeader = new Regex(
            @"\b(?<name>PRIVATE-TOKEN|JOB-TOKEN|DEPLOY-TOKEN|X-Gitlab-Token|Proxy-Authorization|Authorization)(?<sep>\s*[:=]\s*)(?:(?:Bearer|Basic|token)\s+)?[^\s""',;]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex KnownTokens = new Regex(
            @"\b(?:glpat-[A-Za-z0-9_\-]{16,}|gl(?:dt|rt|cbt|ptt|ft|imt|oas|soat)-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})",
            RegexOptions.CultureInvariant);

        private static readonly Regex QuerySecret = new Regex(
            @"(?<key>[?&](?:private_token|access_token|token|password|secret)=)[^&\s""']+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Assignment = new Regex(
            @"\b(?<key>password|passwd|pwd|secret|token)(?<sep>=|:\s*)(?<value>[^\s""',;]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Путь в кавычках может содержать пробелы («Git for Unity»): там границей
        // служит кавычка, а перед путём может стоять «HEAD:». Без кавычек граница — пробел.
        private static readonly Regex ProjectPath = new Regex(
            @"""(?<qpre>(?:[^""\r\n]*?[:=])?)(?<qroot>Assets|Packages|ProjectSettings)(?<qrest>(?:[/\\][^""\r\n/\\]+)+)""" +
            @"|(?<![\w.\-])(?<root>Assets|Packages|ProjectSettings)(?<rest>(?:[/\\][^\s""'<>|:*?/\\]+)+)",
            RegexOptions.CultureInvariant);

        private static readonly Regex UrlHost = new Regex(
            @"(?<scheme>\b(?:https?|ssh|git)://)(?<host>[^/\s:""'<>]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex ScpHost = new Regex(
            @"(?<user>\b[\w.\-]+@)(?<host>[\w.\-]+\.[a-z]{2,})(?<colon>:)(?=[\w.\-~/])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Запомнить секрет, чтобы вырезать его по точному совпадению. Короче 6 символов — не принимается.</summary>
        public static void RegisterSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret) || secret.Trim().Length < 6) return;
            secret = secret.Trim();

            lock (Sync)
            {
                if (Array.IndexOf(_secrets, secret) >= 0) return;
                var list = new List<string>(_secrets) { secret };
                // Длинные первыми: иначе секрет, содержащий другой, вырежется по частям.
                list.Sort((a, b) => b.Length.CompareTo(a.Length));
                _secrets = list.ToArray();
            }
        }

        /// <summary>Убирает секреты. Применяется ко всему, что попадает в журнал и отчёт.</summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            foreach (var s in _secrets)
                if (text.IndexOf(s, StringComparison.Ordinal) >= 0) text = text.Replace(s, Mask);

            text = UserInfo.Replace(text, m => m.Groups["scheme"].Value + Mask + "@");
            text = AuthHeader.Replace(text, m => m.Groups["name"].Value + m.Groups["sep"].Value + Mask);
            text = KnownTokens.Replace(text, Mask);
            text = QuerySecret.Replace(text, m => m.Groups["key"].Value + Mask);
            text = Assignment.Replace(text, m => m.Groups["value"].Value == Mask
                ? m.Value
                : m.Groups["key"].Value + m.Groups["sep"].Value + Mask);
            return text;
        }

        /// <summary>
        /// Скрывает пути: корень проекта и домашняя папка — метками, пути
        /// внутри Assets, Packages и ProjectSettings — хэшами каждой части.
        /// Хэш стабилен, поэтому один и тот же файл в разных местах отчёта
        /// узнаётся, а расширение остаётся — по нему видно тип ассета.
        /// </summary>
        public static string HidePaths(string text, string projectRoot, string home)
        {
            if (string.IsNullOrEmpty(text)) return text;

            text = ReplacePath(text, projectRoot, "<project>");
            text = ReplacePath(text, home, "<home>");

            return ProjectPath.Replace(text, m =>
            {
                if (m.Groups["qroot"].Success)
                    return "\"" + m.Groups["qpre"].Value + HashPath(m.Groups["qroot"].Value, m.Groups["qrest"].Value) + "\"";
                return HashPath(m.Groups["root"].Value, m.Groups["rest"].Value);
            });
        }

        private static string HashPath(string root, string rest)
        {
            var parts = rest.Split('/', '\\');
            var sb = new StringBuilder(root);
            for (int i = 1; i < parts.Length; i++)
            {
                var part = parts[i];
                var dot = i == parts.Length - 1 ? part.LastIndexOf('.') : -1;
                var ext = dot > 0 ? part.Substring(dot) : string.Empty;
                sb.Append('/').Append(Hash(dot > 0 ? part.Substring(0, dot) : part)).Append(ext);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Скрывает имена — ветки, их remote-версии. Каждое вхождение целиком
        /// заменяется на «#хэш», тот же, что у скрытых значений отчёта, поэтому
        /// одна и та же ветка узнаётся во всех местах. Части других слов не трогаются.
        /// </summary>
        public static string HideNames(string text, IEnumerable<string> names)
        {
            if (string.IsNullOrEmpty(text) || names == null) return text;

            var list = new List<string>();
            foreach (var n in names)
                if (!string.IsNullOrEmpty(n) && n.Length >= 2 && !list.Contains(n)) list.Add(n);
            // Длинные первыми: «origin/main» целиком, а не «origin/» и хэш «main».
            list.Sort((a, b) => b.Length.CompareTo(a.Length));

            foreach (var name in list)
                text = Regex.Replace(text, @"(?<![\w.\-/#])" + Regex.Escape(name) + @"(?![\w.\-/])",
                                     "#" + Hash(name), RegexOptions.CultureInvariant);
            return text;
        }

        /// <summary>Скрывает адрес сервера в ссылках http(s), ssh и в записи вида git@host:group/repo.</summary>
        public static string HideServers(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = UrlHost.Replace(text, m => m.Groups["scheme"].Value + "<server>");
            return ScpHost.Replace(text, m => m.Groups["user"].Value + "<server>" + m.Groups["colon"].Value);
        }

        public static string Hash(string value)
        {
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static string ReplacePath(string text, string path, string label)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 3) return text;
            path = path.TrimEnd('/', '\\');

            foreach (var variant in new[] { path.Replace('\\', '/'), path.Replace('/', '\\') })
            {
                int i;
                while ((i = text.IndexOf(variant, StringComparison.OrdinalIgnoreCase)) >= 0)
                    text = text.Substring(0, i) + label + text.Substring(i + variant.Length);
            }
            return text;
        }
    }
}
