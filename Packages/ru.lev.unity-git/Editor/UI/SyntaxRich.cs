using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;

namespace Lev.Git.UI
{
    /// <summary>
    /// Строка кода как rich text — одна для diff (UI Toolkit) и окна конфликтов
    /// (IMGUI): оба понимают &lt;color&gt;.
    ///
    /// Пробелы становятся неразрывными — иначе отступы схлопываются. После «&lt;»
    /// ставится невидимый пробел: без него дженерики и XML читались бы как теги.
    /// </summary>
    internal static class SyntaxRich
    {
        private static readonly string[] DarkColors =
        {
            null, "#569CD6", "#4EC9B0", "#DCDCAA", "#CE9178", "#B5CEA8", "#6A9955", "#9B9B9B", "#9CDCFE", "#569CD6", "#9CDCFE", "#569CD6", "#569CD6"
        };

        private static readonly string[] LightColors =
        {
            null, "#0000FF", "#267F99", "#795E26", "#A31515", "#098658", "#008000", "#7A7A7A", "#0451A5", "#800000", "#C50000", "#0000FF", "#0000FF"
        };

        private const char Nbsp = ' ';
        private const string TagBreak = "<​";

        /// <summary>Часть строки [from, to) с цветами по участкам синтаксиса. spans может быть null.</summary>
        public static string Rich(string text, List<SyntaxSpan> spans, int from, int to)
        {
            if (text == null || to <= from) return string.Empty;

            var sb = new StringBuilder(to - from + 16);
            var colors = EditorGUIUtility.isProSkin ? DarkColors : LightColors;
            int pos = from;

            if (spans != null)
            {
                foreach (var span in spans)
                {
                    int s = Math.Max(span.Start, from);
                    int e = Math.Min(span.End, to);
                    if (e <= s || s < pos) continue;

                    Escape(sb, text, pos, s);

                    var color = colors[(int)span.Kind];
                    if (color == null)
                    {
                        Escape(sb, text, s, e);
                    }
                    else
                    {
                        bool bold = span.Kind == TokenKind.Heading;
                        sb.Append("<color=").Append(color).Append('>');
                        if (bold) sb.Append("<b>");
                        Escape(sb, text, s, e);
                        if (bold) sb.Append("</b>");
                        sb.Append("</color>");
                    }

                    pos = e;
                }
            }

            Escape(sb, text, pos, to);
            return sb.ToString();
        }

        public static string Line(string text, SyntaxLanguage language, ref SyntaxState state)
        {
            text = text ?? string.Empty;
            var spans = language == SyntaxLanguage.None ? null : SyntaxHighlight.Tokenize(language, text, ref state);
            return Rich(text, spans, 0, text.Length);
        }

        private static void Escape(StringBuilder sb, string text, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                char c = text[i];
                if (c == ' ') sb.Append(Nbsp);
                else if (c == '\t') sb.Append(Nbsp, 4);
                else if (c == '<') sb.Append(TagBreak);
                else sb.Append(c);
            }
        }
    }
}
