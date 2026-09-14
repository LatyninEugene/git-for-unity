using System;
using System.Collections.Generic;
using System.IO;

namespace Lev.Git
{
    public enum SyntaxLanguage { None, CSharp, Json, Yaml, Shader, Xml, Css, Markdown, Ignore }

    public enum TokenKind { Plain, Keyword, Type, Method, String, Number, Comment, Preprocessor, Key, Tag, Attribute, Constant, Heading }

    public struct SyntaxSpan
    {
        public int Start, Length;
        public TokenKind Kind;
        public int End { get { return Start + Length; } }
    }

    /// <summary>Что тянется с прошлой строки: незакрытый комментарий, строка, блок кода.</summary>
    public enum SyntaxState { None, BlockComment, VerbatimString, RawString, XmlComment, CssBlock, CodeFence }

    /// <summary>
    /// Подсветка синтаксиса по строкам — для diff текстовых файлов.
    ///
    /// Не полноценный разбор языка, а лексер: ключевые слова, строки, числа,
    /// комментарии, ключи. Этого хватает, чтобы код читался как в редакторе.
    /// Строки разбираются по одной с состоянием: diff показывает фрагменты, и
    /// файл целиком тут не нужен. Незакрытый комментарий или строка переходит
    /// на следующую строку через <see cref="SyntaxState"/>.
    /// </summary>
    public static class SyntaxHighlight
    {
        public static SyntaxLanguage Detect(string path)
        {
            if (string.IsNullOrEmpty(path)) return SyntaxLanguage.None;

            var name = Path.GetFileName(path).ToLowerInvariant();
            if (name == ".gitignore" || name == ".gitattributes" || name == ".gitmodules" || name == ".lfsconfig") return SyntaxLanguage.Ignore;

            switch (Path.GetExtension(name))
            {
                case ".cs": return SyntaxLanguage.CSharp;

                case ".json": case ".asmdef": case ".asmref": case ".inputactions": case ".shadergraph":
                case ".shadersubgraph": case ".vfx": case ".index": case ".jsonc":
                    return SyntaxLanguage.Json;

                case ".yaml": case ".yml": case ".meta": case ".unity": case ".prefab": case ".asset": case ".mat":
                case ".anim": case ".controller": case ".overridecontroller": case ".mask": case ".physicmaterial":
                case ".playable": case ".lighting": case ".spriteatlas": case ".spriteatlasv2": case ".preset":
                case ".terrainlayer": case ".signal": case ".mixer": case ".renderTexture": case ".rendertexture":
                    return SyntaxLanguage.Yaml;

                case ".shader": case ".hlsl": case ".hlslinc": case ".cginc": case ".compute": case ".glsl": case ".raytrace":
                    return SyntaxLanguage.Shader;

                case ".xml": case ".uxml": case ".csproj": case ".props": case ".targets": case ".config": case ".plist":
                    return SyntaxLanguage.Xml;

                case ".uss": case ".tss": case ".css":
                    return SyntaxLanguage.Css;

                case ".md": case ".markdown":
                    return SyntaxLanguage.Markdown;

                default:
                    return SyntaxLanguage.None;
            }
        }

        /// <summary>
        /// Состояние в начале фрагмента diff. Что было выше, неизвестно — угадываем
        /// по самой строке: «* …» в C-подобных языках почти всегда середина комментария.
        /// </summary>
        public static SyntaxState GuessState(SyntaxLanguage language, string line)
        {
            if (language != SyntaxLanguage.CSharp && language != SyntaxLanguage.Shader && language != SyntaxLanguage.Css)
                return SyntaxState.None;

            var t = (line ?? string.Empty).TrimStart();
            if (t == "*" || t.StartsWith("* ", StringComparison.Ordinal) || t.StartsWith("*/", StringComparison.Ordinal))
                return SyntaxState.BlockComment;

            return SyntaxState.None;
        }

        public static List<SyntaxSpan> Tokenize(SyntaxLanguage language, string line, ref SyntaxState state)
        {
            var spans = new List<SyntaxSpan>();
            if (line == null) return spans;

            switch (language)
            {
                case SyntaxLanguage.CSharp: CLike(line, ref state, spans, true); break;
                case SyntaxLanguage.Shader: CLike(line, ref state, spans, false); break;
                case SyntaxLanguage.Json: Json(line, spans); break;
                case SyntaxLanguage.Yaml: Yaml(line, spans); break;
                case SyntaxLanguage.Xml: Xml(line, ref state, spans); break;
                case SyntaxLanguage.Css: Css(line, ref state, spans); break;
                case SyntaxLanguage.Markdown: Markdown(line, ref state, spans); break;
                case SyntaxLanguage.Ignore: Ignore(line, spans); break;
            }

            return Merge(spans);
        }

        /// <summary>Соседние участки одного вида — один: меньше тегов в rich text.</summary>
        private static List<SyntaxSpan> Merge(List<SyntaxSpan> spans)
        {
            if (spans.Count < 2) return spans;

            var merged = new List<SyntaxSpan>(spans.Count) { spans[0] };
            for (int i = 1; i < spans.Count; i++)
            {
                var last = merged[merged.Count - 1];
                var span = spans[i];

                if (span.Kind == last.Kind && span.Start == last.End)
                {
                    last.Length += span.Length;
                    merged[merged.Count - 1] = last;
                }
                else
                {
                    merged.Add(span);
                }
            }

            return merged;
        }

        private static void Add(List<SyntaxSpan> spans, int start, int end, TokenKind kind)
        {
            if (end > start) spans.Add(new SyntaxSpan { Start = start, Length = end - start, Kind = kind });
        }

        private static bool IsIdentStart(char c) { return char.IsLetter(c) || c == '_'; }
        private static bool IsIdent(char c) { return char.IsLetterOrDigit(c) || c == '_'; }

        private static int SkipSpaces(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        private static int Number(string s, int i)
        {
            int j = i;
            if (j + 1 < s.Length && s[j] == '0' && (s[j + 1] == 'x' || s[j + 1] == 'X')) j += 2;
            while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '.' || s[j] == '_'))
            {
                if (s[j] == '.' && (j + 1 >= s.Length || !char.IsDigit(s[j + 1]))) break;
                j++;
            }
            return j;
        }

        /// <summary>Конец строкового литерала в кавычках с экранированием «\». -1 — не закрыт на этой строке.</summary>
        private static int Quoted(string s, int i, char quote)
        {
            for (int j = i + 1; j < s.Length; j++)
            {
                if (s[j] == '\\') { j++; continue; }
                if (s[j] == quote) return j + 1;
            }
            return -1;
        }

        // ---------------------------------------------------------- C# и шейдеры ---

        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "break", "case", "catch", "checked", "class", "const", "continue", "default",
            "delegate", "do", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "for",
            "foreach", "goto", "if", "implicit", "in", "interface", "internal", "is", "lock", "namespace", "new",
            "null", "operator", "out", "override", "params", "private", "protected", "public", "readonly", "ref",
            "return", "sealed", "sizeof", "stackalloc", "static", "struct", "switch", "this", "throw", "true", "try",
            "typeof", "unchecked", "unsafe", "using", "virtual", "volatile", "while", "async", "await", "var", "get",
            "set", "init", "value", "yield", "partial", "where", "when", "nameof", "record", "with", "and", "or", "not",
            "global", "required", "file", "scoped",
            "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "object",
            "short", "ushort", "string", "void", "dynamic", "nint", "nuint"
        };

        private static readonly HashSet<string> ShaderKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "Shader", "SubShader", "Pass", "Properties", "Tags", "Category", "Fallback", "FallBack", "CustomEditor",
            "Cull", "ZWrite", "ZTest", "ZClip", "Blend", "BlendOp", "ColorMask", "Offset", "Stencil", "LOD", "Name",
            "UsePass", "GrabPass", "HLSLPROGRAM", "ENDHLSL", "CGPROGRAM", "ENDCG", "HLSLINCLUDE", "CGINCLUDE",
            "PackageRequirements", "AlphaToMask", "Conservative",
            "if", "else", "for", "while", "do", "return", "break", "continue", "discard", "switch", "case", "default",
            "struct", "cbuffer", "tbuffer", "static", "const", "uniform", "inline", "in", "out", "inout", "true", "false",
            "void", "register", "packoffset", "typedef", "namespace", "class", "interface"
        };

        private static readonly HashSet<string> ShaderTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "bool", "int", "uint", "float", "half", "double", "fixed", "min16float", "min10float", "real",
            "sampler", "sampler1D", "sampler2D", "sampler3D", "samplerCUBE", "SamplerState", "SamplerComparisonState",
            "Texture2D", "Texture3D", "TextureCube", "Texture2DArray", "RWTexture2D", "StructuredBuffer",
            "RWStructuredBuffer", "Buffer", "matrix", "vector"
        };

        private static bool IsShaderType(string word)
        {
            if (ShaderTypes.Contains(word)) return true;

            // float4, half3x3, int2 и подобные.
            foreach (var basis in new[] { "float", "half", "int", "uint", "bool", "fixed", "double", "real", "min16float" })
            {
                if (!word.StartsWith(basis, StringComparison.Ordinal) || word.Length == basis.Length) continue;
                var tail = word.Substring(basis.Length);
                if (tail.Length == 1 && char.IsDigit(tail[0])) return true;
                if (tail.Length == 3 && char.IsDigit(tail[0]) && tail[1] == 'x' && char.IsDigit(tail[2])) return true;
            }
            return false;
        }

        private static void CLike(string s, ref SyntaxState state, List<SyntaxSpan> spans, bool csharp)
        {
            int i = 0;

            while (i < s.Length)
            {
                if (state == SyntaxState.BlockComment)
                {
                    int close = s.IndexOf("*/", i, StringComparison.Ordinal);
                    int end = close < 0 ? s.Length : close + 2;
                    Add(spans, i, end, TokenKind.Comment);
                    if (close >= 0) state = SyntaxState.None;
                    i = end;
                    continue;
                }

                if (state == SyntaxState.VerbatimString)
                {
                    int j = i;
                    while (j < s.Length)
                    {
                        if (s[j] == '"')
                        {
                            if (j + 1 < s.Length && s[j + 1] == '"') { j += 2; continue; }
                            j++;
                            state = SyntaxState.None;
                            break;
                        }
                        j++;
                    }
                    Add(spans, i, j, TokenKind.String);
                    i = j;
                    continue;
                }

                if (state == SyntaxState.RawString)
                {
                    int close = s.IndexOf("\"\"\"", i, StringComparison.Ordinal);
                    int end = close < 0 ? s.Length : close + 3;
                    Add(spans, i, end, TokenKind.String);
                    if (close >= 0) state = SyntaxState.None;
                    i = end;
                    continue;
                }

                char c = s[i];

                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    Add(spans, i, s.Length, TokenKind.Comment);
                    return;
                }

                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    state = SyntaxState.BlockComment;
                    continue;
                }

                if (c == '#' && s.Substring(0, i).Trim().Length == 0)
                {
                    Add(spans, i, s.Length, TokenKind.Preprocessor);
                    return;
                }

                // C#: $"…", @"…", $@"…", @$"…", """…""".
                if (csharp && (c == '$' || c == '@'))
                {
                    int j = i;
                    bool verbatim = false;
                    while (j < s.Length && (s[j] == '$' || s[j] == '@'))
                    {
                        if (s[j] == '@') verbatim = true;
                        j++;
                    }

                    if (j < s.Length && s[j] == '"')
                    {
                        if (verbatim)
                        {
                            Add(spans, i, j + 1, TokenKind.String);
                            state = SyntaxState.VerbatimString;
                            i = j + 1;
                            continue;
                        }

                        c = '"';
                        int endq = Quoted(s, j, '"');
                        int stop = endq < 0 ? s.Length : endq;
                        Add(spans, i, stop, TokenKind.String);
                        i = stop;
                        continue;
                    }
                }

                if (c == '"')
                {
                    if (csharp && string.CompareOrdinal(s, i, "\"\"\"", 0, 3) == 0)
                    {
                        int close = s.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                        int end = close < 0 ? s.Length : close + 3;
                        Add(spans, i, end, TokenKind.String);
                        if (close < 0) state = SyntaxState.RawString;
                        i = end;
                        continue;
                    }

                    int q = Quoted(s, i, '"');
                    int stopq = q < 0 ? s.Length : q;
                    Add(spans, i, stopq, TokenKind.String);
                    i = stopq;
                    continue;
                }

                if (c == '\'')
                {
                    int q = Quoted(s, i, '\'');
                    if (q > 0 && q - i <= 12)
                    {
                        Add(spans, i, q, TokenKind.String);
                        i = q;
                        continue;
                    }
                    i++;
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]) && (i == 0 || !IsIdent(s[i - 1]))))
                {
                    int end = Number(s, i);
                    Add(spans, i, end, TokenKind.Number);
                    i = end;
                    continue;
                }

                if (IsIdentStart(c))
                {
                    int end = i + 1;
                    while (end < s.Length && IsIdent(s[end])) end++;
                    var word = s.Substring(i, end - i);

                    TokenKind kind;
                    if (csharp ? CSharpKeywords.Contains(word) : ShaderKeywords.Contains(word)) kind = TokenKind.Keyword;
                    else if (!csharp && IsShaderType(word)) kind = TokenKind.Type;
                    else
                    {
                        int next = SkipSpaces(s, end);
                        if (next < s.Length && s[next] == '(') kind = TokenKind.Method;
                        else if (csharp && char.IsUpper(word[0])) kind = TokenKind.Type;
                        else kind = TokenKind.Plain;
                    }

                    if (kind != TokenKind.Plain) Add(spans, i, end, kind);
                    i = end;
                    continue;
                }

                i++;
            }
        }

        // ------------------------------------------------------------ JSON ---

        private static readonly HashSet<string> JsonConstants = new HashSet<string>(StringComparer.Ordinal) { "true", "false", "null" };

        private static void Json(string s, List<SyntaxSpan> spans)
        {
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];

                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    Add(spans, i, s.Length, TokenKind.Comment);
                    return;
                }

                if (c == '"')
                {
                    int q = Quoted(s, i, '"');
                    int end = q < 0 ? s.Length : q;
                    int next = SkipSpaces(s, end);
                    Add(spans, i, end, next < s.Length && s[next] == ':' ? TokenKind.Key : TokenKind.String);
                    i = end;
                    continue;
                }

                if (char.IsDigit(c) || (c == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    int end = Number(s, i + (c == '-' ? 1 : 0));
                    Add(spans, i, end, TokenKind.Number);
                    i = end;
                    continue;
                }

                if (char.IsLetter(c))
                {
                    int end = i;
                    while (end < s.Length && char.IsLetter(s[end])) end++;
                    if (JsonConstants.Contains(s.Substring(i, end - i))) Add(spans, i, end, TokenKind.Constant);
                    i = end;
                    continue;
                }

                i++;
            }
        }

        // ------------------------------------------------------------ YAML ---

        private static void Yaml(string s, List<SyntaxSpan> spans)
        {
            if (s.StartsWith("%", StringComparison.Ordinal) || s.StartsWith("---", StringComparison.Ordinal))
            {
                Add(spans, 0, s.Length, TokenKind.Preprocessor);
                return;
            }

            int i = SkipSpaces(s, 0);
            if (i < s.Length && s[i] == '#')
            {
                Add(spans, i, s.Length, TokenKind.Comment);
                return;
            }

            if (i + 1 < s.Length && s[i] == '-' && s[i + 1] == ' ') i = SkipSpaces(s, i + 1);

            // Ключ: всё до «: » или до двоеточия в конце строки.
            int colon = KeyColon(s, i);
            if (colon > i)
            {
                Add(spans, i, colon, TokenKind.Key);
                i = colon + 1;
            }

            YamlValue(s, i, s.Length, spans);
        }

        private static int KeyColon(string s, int from)
        {
            if (from >= s.Length || s[from] == '{' || s[from] == '[' || s[from] == '"' || s[from] == '\'') return -1;

            for (int j = from; j < s.Length; j++)
            {
                char c = s[j];
                if (c == ':' && (j + 1 == s.Length || s[j + 1] == ' ')) return j;
                if (c == '#' && j > from && s[j - 1] == ' ') return -1;
            }
            return -1;
        }

        private static void YamlValue(string s, int i, int end, List<SyntaxSpan> spans)
        {
            while (i < end)
            {
                char c = s[i];

                if (c == ' ' || c == '{' || c == '}' || c == '[' || c == ']' || c == ',') { i++; continue; }

                if (c == '#' && (i == 0 || s[i - 1] == ' '))
                {
                    Add(spans, i, end, TokenKind.Comment);
                    return;
                }

                if (c == '"' || c == '\'')
                {
                    int q = Quoted(s, i, c);
                    int stop = q < 0 ? end : q;
                    Add(spans, i, stop, TokenKind.String);
                    i = stop;
                    continue;
                }

                // Слово до разделителя: ключ во flow-карте, число, константа или текст.
                int j = i;
                while (j < end && s[j] != ',' && s[j] != '}' && s[j] != ']' && !(s[j] == ':' && (j + 1 == end || s[j + 1] == ' '))) j++;

                var word = s.Substring(i, j - i).TrimEnd();
                int wordEnd = i + word.Length;

                if (j < end && s[j] == ':')
                    Add(spans, i, wordEnd, TokenKind.Key);
                else if (IsYamlNumber(word))
                    Add(spans, i, wordEnd, TokenKind.Number);
                else if (word == "true" || word == "false" || word == "null" || word == "~")
                    Add(spans, i, wordEnd, TokenKind.Constant);
                else if (word.StartsWith("!u!", StringComparison.Ordinal) || word.StartsWith("&", StringComparison.Ordinal) || word.StartsWith("*", StringComparison.Ordinal))
                    Add(spans, i, wordEnd, TokenKind.Preprocessor);

                i = j < end && s[j] == ':' ? j + 1 : Math.Max(j, i + 1);
            }
        }

        private static bool IsYamlNumber(string word)
        {
            if (word.Length == 0) return false;
            int start = word[0] == '-' || word[0] == '+' ? 1 : 0;
            if (start >= word.Length) return false;

            bool digit = false;
            for (int k = start; k < word.Length; k++)
            {
                char c = word[k];
                if (char.IsDigit(c)) digit = true;
                else if (c != '.' && c != 'e' && c != 'E' && c != '-' && c != '+') return false;
            }
            return digit;
        }

        // ------------------------------------------------------------- XML ---

        private static void Xml(string s, ref SyntaxState state, List<SyntaxSpan> spans)
        {
            int i = 0;
            while (i < s.Length)
            {
                if (state == SyntaxState.XmlComment)
                {
                    int close = s.IndexOf("-->", i, StringComparison.Ordinal);
                    int end = close < 0 ? s.Length : close + 3;
                    Add(spans, i, end, TokenKind.Comment);
                    if (close >= 0) state = SyntaxState.None;
                    i = end;
                    continue;
                }

                if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
                {
                    state = SyntaxState.XmlComment;
                    continue;
                }

                if (s[i] != '<')
                {
                    i++;
                    continue;
                }

                // Тег: <имя атрибут="значение" … > или </имя>.
                int j = i + 1;
                if (j < s.Length && (s[j] == '/' || s[j] == '?' || s[j] == '!')) j++;
                int nameStart = j;
                while (j < s.Length && (IsIdent(s[j]) || s[j] == ':' || s[j] == '.' || s[j] == '-')) j++;
                Add(spans, i, j, TokenKind.Tag);
                if (j == nameStart) { i = j; continue; }

                while (j < s.Length && s[j] != '>')
                {
                    if (s[j] == '"' || s[j] == '\'')
                    {
                        int q = Quoted(s, j, s[j]);
                        int stop = q < 0 ? s.Length : q;
                        Add(spans, j, stop, TokenKind.String);
                        j = stop;
                        continue;
                    }

                    if (IsIdentStart(s[j]))
                    {
                        int end = j;
                        while (end < s.Length && (IsIdent(s[end]) || s[end] == ':' || s[end] == '-' || s[end] == '.')) end++;
                        Add(spans, j, end, TokenKind.Attribute);
                        j = end;
                        continue;
                    }

                    j++;
                }

                if (j < s.Length)
                {
                    int start = j > 0 && (s[j - 1] == '/' || s[j - 1] == '?') ? j - 1 : j;
                    Add(spans, start, j + 1, TokenKind.Tag);
                    j++;
                }

                i = j;
            }
        }

        // ------------------------------------------------------------- CSS ---

        private static void Css(string s, ref SyntaxState state, List<SyntaxSpan> spans)
        {
            int i = 0;
            bool inBlock = state == SyntaxState.CssBlock;

            while (i < s.Length)
            {
                if (string.CompareOrdinal(s, i, "/*", 0, 2) == 0 || (i == 0 && spans.Count == 0 && CommentCarry(ref state)))
                {
                    int from = string.CompareOrdinal(s, i, "/*", 0, 2) == 0 ? i + 2 : i;
                    int close = s.IndexOf("*/", from, StringComparison.Ordinal);
                    int end = close < 0 ? s.Length : close + 2;
                    Add(spans, i, end, TokenKind.Comment);
                    if (close < 0) { state = SyntaxState.BlockComment; return; }
                    state = inBlock ? SyntaxState.CssBlock : SyntaxState.None;
                    i = end;
                    continue;
                }

                char c = s[i];

                if (c == '{') { inBlock = true; state = SyntaxState.CssBlock; i++; continue; }
                if (c == '}') { inBlock = false; state = SyntaxState.None; i++; continue; }
                if (char.IsWhiteSpace(c) || c == ';' || c == ',') { i++; continue; }

                if (!inBlock)
                {
                    // Селектор: .класс, #имя, тип, :псевдокласс.
                    int end = i + 1;
                    while (end < s.Length && s[end] != '{' && s[end] != ',' && !char.IsWhiteSpace(s[end])) end++;
                    Add(spans, i, end, c == '.' ? TokenKind.Type : c == '#' ? TokenKind.Constant : c == ':' ? TokenKind.Attribute : TokenKind.Tag);
                    i = end;
                    continue;
                }

                // Внутри блока: «свойство: значение;».
                int colon = s.IndexOf(':', i);
                int semi = s.IndexOf(';', i);
                if (colon > i && (semi < 0 || colon < semi) && IsIdentStart(c) || (c == '-' && colon > i))
                {
                    Add(spans, i, colon, TokenKind.Key);
                    i = colon + 1;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    int q = Quoted(s, i, c);
                    int stop = q < 0 ? s.Length : q;
                    Add(spans, i, stop, TokenKind.String);
                    i = stop;
                    continue;
                }

                if (c == '#' || char.IsDigit(c) || (c == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    int end = i + 1;
                    while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] == '.' || s[end] == '%')) end++;
                    Add(spans, i, end, TokenKind.Number);
                    i = end;
                    continue;
                }

                if (IsIdentStart(c))
                {
                    int end = i + 1;
                    while (end < s.Length && (IsIdent(s[end]) || s[end] == '-')) end++;
                    int next = SkipSpaces(s, end);
                    if (next < s.Length && s[next] == '(') Add(spans, i, end, TokenKind.Method);
                    else Add(spans, i, end, TokenKind.Constant);
                    i = end;
                    continue;
                }

                i++;
            }
        }

        private static bool CommentCarry(ref SyntaxState state)
        {
            return state == SyntaxState.BlockComment;
        }

        // -------------------------------------------------------- Markdown ---

        private static void Markdown(string s, ref SyntaxState state, List<SyntaxSpan> spans)
        {
            var trimmed = s.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Add(spans, 0, s.Length, TokenKind.Preprocessor);
                state = state == SyntaxState.CodeFence ? SyntaxState.None : SyntaxState.CodeFence;
                return;
            }

            if (state == SyntaxState.CodeFence)
            {
                Add(spans, 0, s.Length, TokenKind.String);
                return;
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                Add(spans, 0, s.Length, TokenKind.Heading);
                return;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                Add(spans, 0, s.Length, TokenKind.Comment);
                return;
            }

            int lead = s.Length - trimmed.Length;
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("+ ", StringComparison.Ordinal))
                Add(spans, lead, lead + 1, TokenKind.Keyword);

            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '`')
                {
                    int close = s.IndexOf('`', i + 1);
                    if (close < 0) break;
                    Add(spans, i, close + 1, TokenKind.String);
                    i = close;
                }
                else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int close = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (close < 0) break;
                    Add(spans, i, close + 2, TokenKind.Keyword);
                    i = close + 1;
                }
                else if (s[i] == '[')
                {
                    int mid = s.IndexOf("](", i, StringComparison.Ordinal);
                    int close = mid < 0 ? -1 : s.IndexOf(')', mid);
                    if (close < 0) continue;
                    Add(spans, i, mid + 1, TokenKind.Type);
                    Add(spans, mid + 1, close + 1, TokenKind.Comment);
                    i = close;
                }
            }
        }

        // ------------------------------------------------------- .gitignore ---

        private static void Ignore(string s, List<SyntaxSpan> spans)
        {
            var t = s.TrimStart();
            int lead = s.Length - t.Length;

            if (t.StartsWith("#", StringComparison.Ordinal)) { Add(spans, lead, s.Length, TokenKind.Comment); return; }
            if (t.StartsWith("!", StringComparison.Ordinal)) Add(spans, lead, lead + 1, TokenKind.Keyword);

            // .gitattributes: «шаблон атрибут=значение -атрибут».
            int space = s.IndexOf(' ', lead);
            if (space > 0)
            {
                Add(spans, lead, space, TokenKind.Type);
                for (int i = space; i < s.Length;)
                {
                    int start = SkipSpaces(s, i);
                    int end = start;
                    while (end < s.Length && !char.IsWhiteSpace(s[end])) end++;
                    int eq = s.IndexOf('=', start);
                    if (eq > start && eq < end)
                    {
                        Add(spans, start, eq, TokenKind.Key);
                        Add(spans, eq + 1, end, TokenKind.String);
                    }
                    else
                    {
                        Add(spans, start, end, TokenKind.Attribute);
                    }
                    i = end;
                }
            }
        }
    }
}
