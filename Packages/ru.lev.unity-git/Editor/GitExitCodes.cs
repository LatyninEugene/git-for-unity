using System;

namespace Lev.Git
{
    /// <summary>
    /// Коды выхода git, которые означают ответ, а не сбой.
    ///
    /// `git config --get` возвращает 1, когда настройки нет; `diff --no-index` —
    /// когда файлы различаются; `check-ignore` — когда ничего не игнорируется.
    /// Считать это ошибками значит засорять журнал и отчёт о проблеме ложными
    /// тревогами, за которыми не видно настоящей.
    ///
    /// От Unity не зависит: проверяется чистыми тестами.
    /// </summary>
    internal static class GitExitCodes
    {
        /// <summary>Сбой ли это, с учётом того, что значит код у этой команды.</summary>
        public static bool IsFailure(string args, int exitCode)
        {
            if (exitCode == 0) return false;
            if (exitCode < 0 || string.IsNullOrEmpty(args)) return true;

            string rest;
            switch (Verb(args, out rest))
            {
                case "config":
                    return !(exitCode == 1 && rest.IndexOf("--get", StringComparison.Ordinal) >= 0);
                case "diff":
                    return !(exitCode == 1 && (Has(rest, "--no-index") || Has(rest, "--exit-code") || Has(rest, "--quiet")));
                case "check-ignore":
                    return exitCode != 1;
                case "merge-base":
                    return !(exitCode == 1 && Has(rest, "--is-ancestor"));
                case "merge-file":
                    // Код — число конфликтов; ошибки у merge-file — от 127 и выше.
                    return exitCode >= 127;
                case "rev-parse":
                    return !(exitCode == 1 && Has(rest, "--verify") && (Has(rest, "--quiet") || Has(rest, "-q")));
                case "ls-remote":
                    return !(exitCode == 2 && Has(rest, "--exit-code"));
                default:
                    return true;
            }
        }

        /// <summary>Подкоманда после глобальных опций вроде «-c ключ=значение» и «--no-optional-locks».</summary>
        public static string Verb(string args, out string rest)
        {
            var parts = (args ?? string.Empty).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            while (i < parts.Length && parts[i].StartsWith("-", StringComparison.Ordinal))
            {
                if (parts[i] == "-c" || parts[i] == "-C") i++;
                i++;
            }

            if (i >= parts.Length)
            {
                rest = string.Empty;
                return string.Empty;
            }

            rest = string.Join(" ", parts, i + 1, parts.Length - i - 1);
            return parts[i];
        }

        private static bool Has(string args, string option)
        {
            foreach (var part in args.Split(' '))
                if (part == option || part.StartsWith(option + "=", StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
