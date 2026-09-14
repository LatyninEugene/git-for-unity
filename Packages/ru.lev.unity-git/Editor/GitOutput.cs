using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Lev.Git
{
    /// <summary>
    /// Вывод git для человека: без строк прогресса.
    ///
    /// Push и fetch печатают в stderr сотни строк «Counting objects: 17% (13/79)»,
    /// перезаписывая их через \r. Настоящая причина ошибки — одна-две строки в
    /// самом конце. Если хранить вывод как есть и обрезать с начала, до причины
    /// дело не доходит: так и было, когда сервер отклонил push --force.
    ///
    /// От Unity не зависит: проверяется чистыми тестами.
    /// </summary>
    internal static class GitOutput
    {
        private static readonly Regex Progress = new Regex(
            @"^(remote:\s*)?(Enumerating objects|Counting objects|Compressing objects|Writing objects|Receiving objects|" +
            @"Resolving deltas|Delta compression using|Total \d+ \(delta|Uploading LFS objects|Downloading LFS objects|" +
            @"Filtering content|Updating files|Checking out files|Unpacking objects)\b",
            RegexOptions.CultureInvariant);

        /// <summary>Убирает строки прогресса; перезаписанные через \r оставляет последним состоянием.</summary>
        public static string WithoutProgress(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new StringBuilder(text.Length);
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                // «a\rb\rc» на экране — это «c»: всё до последнего \r затёрто.
                var parts = raw.Split('\r');
                var line = parts[parts.Length - 1];
                for (int i = parts.Length - 1; i >= 0 && line.Trim().Length == 0; i--) line = parts[i];

                if (Progress.IsMatch(line.Trim())) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line.TrimEnd());
            }
            return sb.ToString().Trim('\n');
        }

        /// <summary>
        /// Укорачивает длинный текст, сохраняя начало и — главное — конец: у git
        /// причина ошибки печатается последней.
        /// </summary>
        public static string Shorten(string text, int maxChars, string marker)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;

            int head = maxChars / 4;
            int tail = maxChars - head;
            return text.Substring(0, head).TrimEnd() + "\n" + marker + "\n" + text.Substring(text.Length - tail).TrimStart();
        }

        /// <summary>Последние непустые строки — для журнала, где важен конец вывода.</summary>
        public static List<string> LastLines(string text, int count)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            foreach (var line in text.Split('\n'))
                if (line.Trim().Length > 0) lines.Add(line.TrimEnd());

            if (lines.Count > count) lines.RemoveRange(0, lines.Count - count);
            return lines;
        }

        /// <summary>Сервер отказал из-за защиты ветки — GitLab, GitHub и Gitea пишут об этом по-разному, но со словом «protected».</summary>
        public static bool IsProtectedBranchRefusal(string text)
        {
            return !string.IsNullOrEmpty(text) &&
                   text.IndexOf("protected branch", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
