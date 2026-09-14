using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Lev.Git
{
    public enum DiffLineKind
    {
        Context = 0,
        Added,
        Removed,
        /// <summary>«\ No newline at end of file» — часть патча, но не строка файла.</summary>
        NoNewline
    }

    public sealed class DiffLine
    {
        public DiffLineKind Kind;
        public string Raw;      // с ведущим символом ' ', '+', '-' или '\'
        public string Text;     // без ведущего символа и без хвостового \r
        public int OldNumber;   // -1, если строки нет на этой стороне
        public int NewNumber;

        /// <summary>
        /// Диапазон в <see cref="Text"/>, который отличается от парной строки.
        /// -1, когда пары нет или различается вся строка целиком.
        /// </summary>
        public int MarkStart = -1;
        public int MarkEnd = -1;

        public bool HasMark => MarkStart >= 0 && MarkEnd > MarkStart;
    }

    public sealed class DiffHunk
    {
        public int OldStart, OldCount, NewStart, NewCount;
        public string Heading;              // текст после второго @@, обычно имя функции
        public List<DiffLine> Lines = new List<DiffLine>();

        /// <summary>Точный текст хунка вместе с заголовком @@ — из него собирается патч.</summary>
        public string RawText;

        private string _key;

        /// <summary>
        /// Устойчивый ключ фрагмента для запоминания отметок.
        ///
        /// Считается от содержимого, а не от позиции: правка выше по файлу
        /// сдвигает индексы всех последующих хунков, и отметки «поехали бы».
        /// Изменившийся фрагмент получает новый ключ и считается новым — то есть
        /// по умолчанию снова входит в коммит, что честнее, чем унаследовать
        /// снятую галочку от другого текста.
        /// </summary>
        public string Key
        {
            get
            {
                if (_key != null) return _key;

                // FNV-1a: детерминирован между запусками, в отличие от GetHashCode.
                unchecked
                {
                    uint h = 2166136261;
                    var s = RawText ?? string.Empty;
                    for (int i = 0; i < s.Length; i++)
                    {
                        h ^= s[i];
                        h *= 16777619;
                    }
                    _key = h.ToString("x8");
                }
                return _key;
            }
        }

        public int Added
        {
            get { int n = 0; foreach (var l in Lines) if (l.Kind == DiffLineKind.Added) n++; return n; }
        }

        public int Removed
        {
            get { int n = 0; foreach (var l in Lines) if (l.Kind == DiffLineKind.Removed) n++; return n; }
        }

        public string Header
        {
            get
            {
                return string.Format("@@ -{0},{1} +{2},{3} @@", OldStart, OldCount, NewStart, NewCount);
            }
        }
    }

    public sealed class FileDiff
    {
        public string Path;
        public string OldPath;
        public bool IsBinary;
        public bool IsEmpty => Hunks.Count == 0 && !IsBinary;

        /// <summary>Шапка патча: «diff --git», «index», «--- a/…», «+++ b/…».</summary>
        public string Preamble = string.Empty;

        public List<DiffHunk> Hunks = new List<DiffHunk>();

        public int Added { get { int n = 0; foreach (var h in Hunks) n += h.Added; return n; } }
        public int Removed { get { int n = 0; foreach (var h in Hunks) n += h.Removed; return n; } }

    }

    /// <summary>
    /// Сборка содержимого файла из выбранных фрагментов — без `git apply`.
    ///
    /// Почему не `git apply --cached`: на Windows он не работает. При
    /// core.autocrlf=true (настройка по умолчанию у Git for Windows) `git diff`
    /// печатает строки контекста с CRLF, а в индексе они лежат с LF, и apply
    /// не находит совпадения: «patch does not apply». Проверено на всех трёх
    /// значениях autocrlf — проходит только false.
    ///
    /// Поэтому нужное содержимое собирается здесь, в памяти, и обе стороны
    /// берутся из ОДНОГО источника — рабочей копии. Расхождению переводов строк
    /// взяться неоткуда, а нормализацию потом делает `git hash-object --path`,
    /// ровно так же, как её сделал бы `git add`.
    /// </summary>
    public static class GitPatchBuilder
    {
        /// <summary>
        /// Собирает содержимое, которое должно оказаться в индексе, из рабочей
        /// копии и решения по каждой изменённой строке.
        ///
        /// Правило простое и симметричное:
        ///  * строка контекста остаётся всегда;
        ///  * добавленная строка попадает, если отмечена;
        ///  * удалённая строка ВОЗВРАЩАЕТСЯ, если НЕ отмечена, — снятая отметка
        ///    на удалении означает «удаление в коммит не идёт».
        ///
        /// Этой же функцией делается откат: достаточно снять всё в нужном
        /// фрагменте и записать результат обратно в рабочую копию.
        /// </summary>
        public static string BuildStagedText(
            string worktreeText, FileDiff diff, Func<DiffHunk, int, bool> isSelected)
        {
            if (worktreeText == null || diff == null) return null;

            var lines = new List<string>(worktreeText.Split('\n'));

            // Фрагменты обрабатываются снизу вверх: правка выше по файлу сдвинула
            // бы номера всех последующих.
            var ordered = new List<DiffHunk>(diff.Hunks);
            ordered.Sort((a, b) => b.NewStart.CompareTo(a.NewStart));

            foreach (var h in ordered)
            {
                // При пустой новой стороне (@@ -5,3 +4,0 @@) git указывает номер
                // строки ПЕРЕД вставкой, а не первой строки диапазона.
                int start = h.NewCount == 0 ? h.NewStart : h.NewStart - 1;
                if (start < 0 || start > lines.Count) return null;

                int count = Math.Min(h.NewCount, lines.Count - start);

                var staged = new List<string>();
                for (int i = 0; i < h.Lines.Count; i++)
                {
                    var l = h.Lines[i];
                    if (l.Kind == DiffLineKind.NoNewline) continue;

                    var text = l.Raw.Length > 0 ? l.Raw.Substring(1) : string.Empty;
                    bool selected = isSelected(h, i);

                    if (l.Kind == DiffLineKind.Context) staged.Add(text);
                    else if (l.Kind == DiffLineKind.Added) { if (selected) staged.Add(text); }
                    else if (l.Kind == DiffLineKind.Removed) { if (!selected) staged.Add(text); }
                }

                lines.RemoveRange(start, count);
                lines.InsertRange(start, staged);
            }

            return string.Join("\n", lines.ToArray());
        }

        /// <summary>Изменяемая ли это строка — только такие можно отмечать.</summary>
        public static bool IsSelectable(DiffLine line)
        {
            return line.Kind == DiffLineKind.Added || line.Kind == DiffLineKind.Removed;
        }

        /// <summary>
        /// Отмечает в каждой паре «удалено / добавлено» ту часть строки, которая
        /// на самом деле изменилась.
        ///
        /// В коде и в YAML правка обычно затрагивает несколько символов посреди
        /// длинной строки, и без этого глаз ищет отличие вручную. Пары берутся
        /// позиционно внутри соседних серий: git выдаёт сначала все удалённые
        /// строки фрагмента, потом все добавленные, и n-я удалённая почти всегда
        /// соответствует n-й добавленной.
        /// </summary>
        public static void MarkIntraLineChanges(FileDiff diff)
        {
            if (diff == null) return;

            foreach (var hunk in diff.Hunks)
            {
                int i = 0;
                while (i < hunk.Lines.Count)
                {
                    if (hunk.Lines[i].Kind != DiffLineKind.Removed) { i++; continue; }

                    int removedStart = i;
                    while (i < hunk.Lines.Count && hunk.Lines[i].Kind == DiffLineKind.Removed) i++;
                    int removedCount = i - removedStart;

                    int addedStart = i;
                    while (i < hunk.Lines.Count && hunk.Lines[i].Kind == DiffLineKind.Added) i++;
                    int addedCount = i - addedStart;

                    int pairs = Math.Min(removedCount, addedCount);
                    for (int k = 0; k < pairs; k++)
                        MarkPair(hunk.Lines[removedStart + k], hunk.Lines[addedStart + k]);
                }
            }
        }

        private static void MarkPair(DiffLine oldLine, DiffLine newLine)
        {
            var a = oldLine.Text ?? string.Empty;
            var b = newLine.Text ?? string.Empty;

            int prefix = 0;
            int max = Math.Min(a.Length, b.Length);
            while (prefix < max && a[prefix] == b[prefix]) prefix++;

            int suffix = 0;
            while (suffix < max - prefix &&
                   a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

            // Совпадающих краёв нет — строки различаются целиком, и подсветка
            // середины ничего бы не сказала.
            if (prefix == 0 && suffix == 0) return;

            oldLine.MarkStart = prefix;
            oldLine.MarkEnd = a.Length - suffix;
            newLine.MarkStart = prefix;
            newLine.MarkEnd = b.Length - suffix;
        }
    }

    /// <summary>Разбор вывода `git diff` в формате unified.</summary>
    public static class GitDiffParser
    {
        private static readonly Regex HunkHeader = new Regex(
            @"^@@ -(?<os>\d+)(?:,(?<oc>\d+))? \+(?<ns>\d+)(?:,(?<nc>\d+))? @@(?<head>.*)$");

        /// <summary>
        /// Разбирает вывод git diff для ОДНОГО файла. Возвращает null, если
        /// изменений нет.
        /// </summary>
        public static FileDiff Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            // Разделяем по '\n', сохраняя '\r' внутри строк: содержимое файла может
            // быть с CRLF, и патч обязан вернуться байт в байт таким же.
            var lines = raw.Split('\n');

            var diff = new FileDiff();
            var preamble = new StringBuilder();
            DiffHunk current = null;
            StringBuilder currentRaw = null;
            int oldNo = 0, newNo = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Последний элемент после финального '\n' — пустая строка, не часть патча.
                if (i == lines.Length - 1 && line.Length == 0) break;

                var m = HunkHeader.Match(line);
                if (m.Success)
                {
                    FinishHunk(diff, current, currentRaw);

                    current = new DiffHunk
                    {
                        OldStart = int.Parse(m.Groups["os"].Value),
                        OldCount = m.Groups["oc"].Success ? int.Parse(m.Groups["oc"].Value) : 1,
                        NewStart = int.Parse(m.Groups["ns"].Value),
                        NewCount = m.Groups["nc"].Success ? int.Parse(m.Groups["nc"].Value) : 1,
                        Heading = m.Groups["head"].Value.Trim()
                    };
                    currentRaw = new StringBuilder();
                    currentRaw.Append(line).Append('\n');

                    oldNo = current.OldStart;
                    newNo = current.NewStart;
                    continue;
                }

                if (current == null)
                {
                    if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                        line.StartsWith("GIT binary patch", StringComparison.Ordinal))
                        diff.IsBinary = true;

                    if (line.StartsWith("--- ", StringComparison.Ordinal))
                        diff.OldPath = StripPrefix(line.Substring(4));
                    else if (line.StartsWith("+++ ", StringComparison.Ordinal))
                        diff.Path = StripPrefix(line.Substring(4));

                    preamble.Append(line).Append('\n');
                    continue;
                }

                currentRaw.Append(line).Append('\n');

                var dl = new DiffLine { Raw = line, OldNumber = -1, NewNumber = -1 };
                char c = line.Length > 0 ? line[0] : ' ';

                switch (c)
                {
                    case '+':
                        dl.Kind = DiffLineKind.Added;
                        dl.NewNumber = newNo++;
                        break;
                    case '-':
                        dl.Kind = DiffLineKind.Removed;
                        dl.OldNumber = oldNo++;
                        break;
                    case '\\':
                        dl.Kind = DiffLineKind.NoNewline;
                        break;
                    default:
                        dl.Kind = DiffLineKind.Context;
                        dl.OldNumber = oldNo++;
                        dl.NewNumber = newNo++;
                        break;
                }

                dl.Text = line.Length > 0 ? line.Substring(1) : string.Empty;
                if (dl.Text.EndsWith("\r", StringComparison.Ordinal))
                    dl.Text = dl.Text.Substring(0, dl.Text.Length - 1);

                current.Lines.Add(dl);
            }

            FinishHunk(diff, current, currentRaw);
            diff.Preamble = preamble.ToString();

            return diff.Hunks.Count == 0 && !diff.IsBinary ? null : diff;
        }

        private static void FinishHunk(FileDiff diff, DiffHunk hunk, StringBuilder raw)
        {
            if (hunk == null) return;
            hunk.RawText = raw.ToString();
            diff.Hunks.Add(hunk);
        }

        private static string StripPrefix(string p)
        {
            p = p.Trim();
            if (p == "/dev/null") return null;
            if (p.Length > 2 && (p.StartsWith("a/", StringComparison.Ordinal) ||
                                 p.StartsWith("b/", StringComparison.Ordinal)))
                return p.Substring(2);
            return p;
        }
    }
}
