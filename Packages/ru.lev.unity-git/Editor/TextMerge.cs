using System;
using System.Collections.Generic;

namespace Lev.Git
{
    public enum TextMergeChoice { None, Mine, Theirs, MineThenTheirs, TheirsThenMine, Base, Manual }

    /// <summary>Участок результата слияния: спокойный текст или место, которое правили обе стороны.</summary>
    public sealed class TextMergeChunk
    {
        public bool Conflict;

        /// <summary>Строки спокойного участка — git свёл их сам.</summary>
        public readonly List<string> Lines = new List<string>();

        public readonly List<string> Mine = new List<string>();
        public readonly List<string> Base = new List<string>();
        public readonly List<string> Theirs = new List<string>();

        public TextMergeChoice Choice;

        /// <summary>Текст ручной правки — для Choice = Manual.</summary>
        public string Manual;

        /// <summary>Строки, которыми участок войдёт в итог. null — конфликт не решён.</summary>
        public List<string> Resolved()
        {
            if (!Conflict) return Lines;

            switch (Choice)
            {
                case TextMergeChoice.Mine: return Mine;
                case TextMergeChoice.Theirs: return Theirs;
                case TextMergeChoice.Base: return Base;
                case TextMergeChoice.MineThenTheirs: return Concat(Mine, Theirs);
                case TextMergeChoice.TheirsThenMine: return Concat(Theirs, Mine);
                case TextMergeChoice.Manual: return SplitLines(Manual ?? string.Empty);
                default: return null;
            }
        }

        private static List<string> Concat(List<string> a, List<string> b)
        {
            var list = new List<string>(a.Count + b.Count);
            list.AddRange(a);
            list.AddRange(b);
            return list;
        }

        public static List<string> SplitLines(string text)
        {
            var list = new List<string>();
            if (text.Length == 0) return list;

            foreach (var line in text.Replace("\r\n", "\n").Split('\n')) list.Add(line);
            if (list.Count > 0 && list[list.Count - 1].Length == 0 && text.EndsWith("\n", StringComparison.Ordinal))
                list.RemoveAt(list.Count - 1);
            return list;
        }
    }

    /// <summary>Разобранный результат слияния текстового файла.</summary>
    public sealed class TextMergeResult
    {
        public readonly List<TextMergeChunk> Chunks = new List<TextMergeChunk>();

        /// <summary>Перевод строки файла: итог пишется тем же, что пришло из версий.</summary>
        public string Newline = "\n";
        public bool FinalNewline;

        /// <summary>У версии «моё» был BOM — итог сохранит его.</summary>
        public bool Bom;

        public int Conflicts
        {
            get
            {
                int n = 0;
                foreach (var c in Chunks) if (c.Conflict) n++;
                return n;
            }
        }

        public int Unresolved
        {
            get
            {
                int n = 0;
                foreach (var c in Chunks) if (c.Conflict && c.Resolved() == null) n++;
                return n;
            }
        }

        public void ResolveAll(TextMergeChoice choice)
        {
            foreach (var c in Chunks)
                if (c.Conflict && (c.Choice == TextMergeChoice.None || choice == TextMergeChoice.None)) c.Choice = choice;
        }

        public void Reset()
        {
            foreach (var c in Chunks)
            {
                c.Choice = TextMergeChoice.None;
                c.Manual = null;
            }
        }

        /// <summary>Итог. Нерешённые места остаются маркерами — такой текст запись не пропустит.</summary>
        public string Build()
        {
            var lines = new List<string>();

            foreach (var c in Chunks)
            {
                var resolved = c.Resolved();
                if (resolved != null)
                {
                    lines.AddRange(resolved);
                    continue;
                }

                lines.Add("<<<<<<< " + TextMerge.MineLabel);
                lines.AddRange(c.Mine);
                lines.Add("||||||| " + TextMerge.BaseLabel);
                lines.AddRange(c.Base);
                lines.Add("=======");
                lines.AddRange(c.Theirs);
                lines.Add(">>>>>>> " + TextMerge.TheirsLabel);
            }

            var text = string.Join(Newline, lines.ToArray());
            return FinalNewline && lines.Count > 0 ? text + Newline : text;
        }
    }

    /// <summary>
    /// Разбор вывода `git merge-file --diff3` с нашими метками. Git сливает
    /// сам всё, что правила одна сторона, а места, где правили обе, отмечает
    /// маркерами с тремя версиями: моя, база, их. Метки уникальные — строка
    /// «=======» внутри самого файла за маркер не примется.
    /// </summary>
    public static class TextMerge
    {
        public const string MineLabel = "LEVGIT-MINE";
        public const string BaseLabel = "LEVGIT-BASE";
        public const string TheirsLabel = "LEVGIT-THEIRS";

        private enum Part { Stable, Mine, Base, Theirs }

        public static TextMergeResult Parse(string merged)
        {
            var result = new TextMergeResult();
            merged = merged ?? string.Empty;

            result.Newline = merged.Contains("\r\n") ? "\r\n" : "\n";
            result.FinalNewline = merged.EndsWith("\n", StringComparison.Ordinal);

            var raw = merged.Split('\n');
            int count = result.FinalNewline ? raw.Length - 1 : raw.Length;
            if (merged.Length == 0) count = 0;

            var part = Part.Stable;
            TextMergeChunk current = null;

            for (int i = 0; i < count; i++)
            {
                var line = raw[i].EndsWith("\r", StringComparison.Ordinal) ? raw[i].Substring(0, raw[i].Length - 1) : raw[i];

                if (part == Part.Stable && line == "<<<<<<< " + MineLabel)
                {
                    current = new TextMergeChunk { Conflict = true };
                    result.Chunks.Add(current);
                    part = Part.Mine;
                    continue;
                }

                if (part == Part.Mine && line == "||||||| " + BaseLabel) { part = Part.Base; continue; }
                if ((part == Part.Base || part == Part.Mine) && line == "=======") { part = Part.Theirs; continue; }

                if (part == Part.Theirs && line == ">>>>>>> " + TheirsLabel)
                {
                    part = Part.Stable;
                    current = null;
                    continue;
                }

                switch (part)
                {
                    case Part.Mine: current.Mine.Add(line); break;
                    case Part.Base: current.Base.Add(line); break;
                    case Part.Theirs: current.Theirs.Add(line); break;
                    default:
                    {
                        var last = result.Chunks.Count > 0 ? result.Chunks[result.Chunks.Count - 1] : null;
                        if (last == null || last.Conflict)
                        {
                            last = new TextMergeChunk();
                            result.Chunks.Add(last);
                        }
                        last.Lines.Add(line);
                        break;
                    }
                }
            }

            // Обе стороны сделали одно и то же — выбирать нечего.
            foreach (var c in result.Chunks)
                if (c.Conflict && Same(c.Mine, c.Theirs)) c.Choice = TextMergeChoice.Mine;

            return result;
        }

        private static bool Same(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
