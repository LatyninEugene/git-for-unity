using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lev.Git
{
    /// <summary>Ссылка, указывающая на коммит: ветка, удалённая ветка или тег.</summary>
    public enum GitRefKind { LocalBranch, RemoteBranch, Tag, Head }

    public sealed class GitRef
    {
        public GitRefKind Kind;
        public string Name;
        /// <summary>Ветка, на которой стоит HEAD.</summary>
        public bool IsCurrent;
    }

    /// <summary>Один коммит из журнала.</summary>
    public sealed class GitCommit
    {
        public string Sha = string.Empty;
        public string ShortSha = string.Empty;
        public string[] Parents = Array.Empty<string>();
        public string Author = string.Empty;
        public string AuthorEmail = string.Empty;
        public DateTime Date;
        public string Subject = string.Empty;
        public List<GitRef> Refs = new List<GitRef>();

        /// <summary>Колонка, в которой рисуется точка коммита. Заполняет раскладка.</summary>
        public int Lane;

        /// <summary>
        /// Отрезки, проходящие через строку этого коммита. Заполняет раскладка.
        /// </summary>
        public List<GitGraphEdge> Edges = new List<GitGraphEdge>();

        /// <summary>Сколько колонок занято на этой строке — ширина графа.</summary>
        public int LaneCount;

        public bool IsMerge { get { return Parents.Length > 1; } }
    }

    /// <summary>
    /// Отрезок линии графа на одной строке: из колонки сверху в колонку снизу.
    ///
    /// Хранится именно парой колонок, а не списком точек: рисовать строку нужно
    /// уметь по одной, не зная соседей, — иначе виртуализация списка невозможна.
    /// </summary>
    public struct GitGraphEdge
    {
        public int From;
        public int To;
        /// <summary>Отрезок начинается в точке этого коммита (ветвление вниз).</summary>
        public bool FromCommit;
    }

    /// <summary>
    /// Разбор вывода `git log` с фиксированным форматом.
    ///
    /// Поля разделены NUL, записи — символом RS (0x1E). Пробелы и переводы
    /// строк разделителями быть не могут: и в имени автора, и в заголовке
    /// коммита встречается что угодно, а %s к тому же уже обрезан до первой
    /// строки, так что RS гарантированно свободен.
    /// </summary>
    public static class GitLogParser
    {
        /// <summary>Формат для --format=. Порядок полей обязан совпадать с разбором.</summary>
        public const string Format =
            "%H%x00%h%x00%P%x00%an%x00%ae%x00%aI%x00%D%x00%s%x1e";

        public static List<GitCommit> Parse(string raw)
        {
            var result = new List<GitCommit>();
            if (string.IsNullOrEmpty(raw)) return result;

            var records = raw.Split('\u001e');
            foreach (var record in records)
            {
                // Между записями остаётся перевод строки от самого git.
                var text = record.Trim('\n', '\r');
                if (text.Length == 0) continue;

                var f = text.Split('\0');
                if (f.Length < 8) continue;

                var c = new GitCommit
                {
                    Sha = f[0],
                    ShortSha = f[1],
                    Parents = SplitParents(f[2]),
                    Author = f[3],
                    AuthorEmail = f[4],
                    Date = ParseDate(f[5]),
                    // Заголовки коммитов, сделанных до починки кодировки stdin,
                    // начинаются с BOM: он попадал в сообщение вместе с текстом.
                    // Переписывать историю ради невидимого символа незачем,
                    // а показывать его — тем более.
                    Subject = f[7].TrimStart('\uFEFF')
                };

                ParseRefs(f[6], c.Refs);
                result.Add(c);
            }

            return result;
        }

        private static string[] SplitParents(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<string>();
            return s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static DateTime ParseDate(string s)
        {
            DateTime d;
            // %aI — строгий ISO 8601 со смещением. Приводим к местному времени:
            // пользователь сравнивает записи со своими часами, а не с UTC.
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return d.ToLocalTime();

            return DateTime.MinValue;
        }

        /// <summary>
        /// Разбирает %D: «HEAD -&gt; main, origin/main, tag: v1.0».
        /// </summary>
        private static void ParseRefs(string decorations, List<GitRef> into)
        {
            if (string.IsNullOrEmpty(decorations)) return;

            foreach (var part in decorations.Split(','))
            {
                var s = part.Trim();
                if (s.Length == 0) continue;

                if (s.StartsWith("tag: ", StringComparison.Ordinal))
                {
                    into.Add(new GitRef { Kind = GitRefKind.Tag, Name = s.Substring(5).Trim() });
                    continue;
                }

                bool current = false;
                int arrow = s.IndexOf("-> ", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    // «HEAD -> main»: сама ветка и есть текущая, HEAD отдельной
                    // строкой не нужен — иначе на строке два одинаковых ярлыка.
                    s = s.Substring(arrow + 3).Trim();
                    current = true;
                }
                else if (s == "HEAD")
                {
                    into.Add(new GitRef { Kind = GitRefKind.Head, Name = "HEAD", IsCurrent = true });
                    continue;
                }

                var kind = s.StartsWith("origin/", StringComparison.Ordinal) || s.Contains("/")
                    ? GitRefKind.RemoteBranch
                    : GitRefKind.LocalBranch;

                into.Add(new GitRef { Kind = kind, Name = s, IsCurrent = current });
            }
        }
    }
}
