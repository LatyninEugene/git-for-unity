using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lev.Git
{
    /// <summary>
    /// Перевод интерфейса.
    ///
    /// В коде пишется английский текст — он же ключ и он же запасной вариант:
    /// <c>L.T("Commit")</c>. Переводы лежат в Editor/Localization/&lt;язык&gt;.po,
    /// в обычном формате gettext, который открывает любой редактор переводов.
    ///
    /// Этот файл от Unity не зависит (его собирают чистые тесты). Выбор языка,
    /// поиск файлов и перерисовка окон — в L.Editor.cs.
    ///
    /// Вызывать можно из любого потока: каталог подменяется целиком одной
    /// ссылкой, а читается без блокировок.
    /// </summary>
    internal static partial class L
    {
        public const string English = "en";

        private static volatile Catalog _catalog;
        private static volatile string _language = English;
        private static CultureInfo _culture = CultureInfo.InvariantCulture;

        /// <summary>Код текущего языка: "en", "ru".</summary>
        public static string Language => _language;

        /// <summary>Культура для дат и чисел в тексте.</summary>
        public static CultureInfo Culture => _culture;

        /// <summary>Растёт при каждой смене языка — по нему сбрасываются кэши подписей.</summary>
        public static int Revision { get; private set; }

        /// <summary>Язык сменился. Поднимается в главном потоке редактора.</summary>
        public static event Action Changed;

        // ---------------------------------------------------------- перевод ---

        /// <summary>Перевод строки.</summary>
        public static string T(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var c = _catalog;
            string s;
            return c != null && c.Strings.TryGetValue(text, out s) ? s : text;
        }

        /// <summary>
        /// Помечает строку для сбора переводов и возвращает её без перевода.
        /// Для таблиц, которые переводятся позже, при показе: <c>L.T(names[i])</c>.
        /// </summary>
        public static string M(string text)
        {
            return text;
        }

        /// <summary>Перевод строки с контекстом — когда одно английское слово переводится по-разному.</summary>
        public static string Tc(string context, string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var c = _catalog;
            string s;
            return c != null && c.Strings.TryGetValue(Catalog.Key(context, text), out s) ? s : text;
        }

        /// <summary>Перевод строки формата и подстановка: <c>L.F("Branch {0} not found", name)</c>.</summary>
        public static string F(string format, params object[] args)
        {
            return Format(T(format), format, args);
        }

        public static string Fc(string context, string format, params object[] args)
        {
            return Format(Tc(context, format), format, args);
        }

        /// <summary>
        /// Число с согласованием: <c>L.N("{0} file", "{0} files", n)</c>.
        /// {0} — само число, дополнительные аргументы — {1}, {2}…
        /// </summary>
        public static string N(string singular, string plural, long n, params object[] args)
        {
            var all = new object[(args != null ? args.Length : 0) + 1];
            all[0] = n;
            if (args != null) Array.Copy(args, 0, all, 1, args.Length);

            var fallback = n == 1 ? singular : plural;
            var c = _catalog;
            string[] forms;
            if (c != null && c.Plurals.TryGetValue(singular, out forms) && forms.Length > 0)
            {
                int i = Math.Max(0, Math.Min(forms.Length - 1, c.PluralIndex(n)));
                if (!string.IsNullOrEmpty(forms[i])) return Format(forms[i], fallback, all);
            }
            return Format(fallback, fallback, all);
        }

        private static string Format(string translated, string original, object[] args)
        {
            if (args == null || args.Length == 0) return translated;
            try { return string.Format(_culture, translated, args); }
            catch (FormatException)
            {
                // Сломанный перевод не должен ронять окно: показываем оригинал.
                try { return string.Format(CultureInfo.InvariantCulture, original, args); }
                catch (FormatException) { return original; }
            }
        }

        // ---------------------------------------------------- смена каталога ---

        /// <summary>Ставит язык и каталог. null — английский без перевода.</summary>
        internal static void Apply(string language, Catalog catalog)
        {
            language = string.IsNullOrEmpty(language) ? English : language;
            CultureInfo culture;
            try { culture = CultureInfo.GetCultureInfo(language); }
            catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }

            _culture = culture;
            _catalog = language == English ? null : catalog;
            _language = language;
            Revision++;
        }

        internal static void RaiseChanged()
        {
            var h = Changed;
            if (h != null) h();
        }
    }

    /// <summary>Разобранный .po: строки, множественные формы и правило выбора формы.</summary>
    internal sealed class Catalog
    {
        public readonly Dictionary<string, string> Strings = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string[]> Plurals = new Dictionary<string, string[]>(StringComparer.Ordinal);
        public Func<long, int> PluralIndex = PluralRules.English;
        public string Language;

        /// <summary>Ключ строки с контекстом — как в gettext: контекст, EOT, текст.</summary>
        public static string Key(string context, string text)
        {
            return string.IsNullOrEmpty(context) ? text : context + "\u0004" + text;
        }

        /// <summary>
        /// Разбор .po. Записи с пометкой fuzzy и пустым переводом пропускаются —
        /// вместо них остаётся английский текст.
        /// </summary>
        public static Catalog Parse(string text, string language)
        {
            var catalog = new Catalog { Language = language, PluralIndex = PluralRules.For(language) };
            if (string.IsNullOrEmpty(text)) return catalog;

            string ctx = null, id = null, idPlural = null;
            var strs = new SortedDictionary<int, string>();
            bool fuzzy = false, pendingFuzzy = false;

            // Куда дописывать строки-продолжения "…".
            string field = null;
            int fieldIndex = 0;

            Action flush = () =>
            {
                if (id != null && id.Length > 0 && !fuzzy)
                {
                    var key = Key(ctx, id);
                    if (idPlural == null)
                    {
                        string s;
                        if (strs.TryGetValue(0, out s) && !string.IsNullOrEmpty(s)) catalog.Strings[key] = s;
                    }
                    else if (strs.Count > 0)
                    {
                        var forms = new string[strs.Count];
                        int i = 0;
                        foreach (var kv in strs) forms[i++] = kv.Value;
                        if (Array.TrueForAll(forms, f => !string.IsNullOrEmpty(f))) catalog.Plurals[key] = forms;
                    }
                }
                ctx = null; id = null; idPlural = null;
                strs.Clear();
                fuzzy = false;
                field = null;
            };

            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (line[0] == '#')
                {
                    if (line.StartsWith("#,", StringComparison.Ordinal) && line.IndexOf("fuzzy", StringComparison.Ordinal) > 0)
                        pendingFuzzy = true;
                    continue;
                }

                if (line[0] == '"')
                {
                    var more = Unquote(line);
                    switch (field)
                    {
                        case "msgctxt": ctx += more; break;
                        case "msgid": id += more; break;
                        case "msgid_plural": idPlural += more; break;
                        case "msgstr": strs[fieldIndex] = (strs.ContainsKey(fieldIndex) ? strs[fieldIndex] : string.Empty) + more; break;
                    }
                    continue;
                }

                int space = line.IndexOf(' ');
                if (space < 0) continue;
                var keyword = line.Substring(0, space);
                var value = Unquote(line.Substring(space + 1).Trim());

                if (keyword == "msgctxt" || (keyword == "msgid" && field != "msgctxt"))
                {
                    flush();
                    fuzzy = pendingFuzzy;
                    pendingFuzzy = false;
                }

                if (keyword == "msgctxt") { ctx = value; field = keyword; }
                else if (keyword == "msgid") { id = value; field = keyword; }
                else if (keyword == "msgid_plural") { idPlural = value; field = keyword; }
                else if (keyword == "msgstr") { strs[0] = value; field = "msgstr"; fieldIndex = 0; }
                else if (keyword.StartsWith("msgstr[", StringComparison.Ordinal))
                {
                    int n;
                    int.TryParse(keyword.Substring(7).TrimEnd(']'), out n);
                    strs[n] = value;
                    field = "msgstr";
                    fieldIndex = n;
                }
            }
            flush();

            return catalog;
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2);

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }

                char e = s[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append('\\').Append(e); break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Правила выбора множественной формы — те же, что в заголовке Plural-Forms
    /// у gettext. Заданы таблицей: разбирать выражение из заголовка ради
    /// нескольких языков незачем.
    /// </summary>
    internal static class PluralRules
    {
        public static int English(long n) { return n == 1 ? 0 : 1; }

        /// <summary>1 файл, 2 файла, 5 файлов; 21 файл, 11 файлов.</summary>
        public static int Slavic(long n)
        {
            n = Math.Abs(n);
            return n % 10 == 1 && n % 100 != 11 ? 0
                : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 10 || n % 100 >= 20) ? 1 : 2;
        }

        public static int Polish(long n)
        {
            n = Math.Abs(n);
            return n == 1 ? 0 : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 10 || n % 100 >= 20) ? 1 : 2;
        }

        public static int French(long n) { return Math.Abs(n) > 1 ? 1 : 0; }
        public static int Single(long n) { return 0; }

        public static Func<long, int> For(string language)
        {
            switch (language)
            {
                case "ru": case "uk": case "be": case "sr": case "hr": case "bs": return Slavic;
                case "pl": return Polish;
                case "fr": case "pt-BR": return French;
                case "ja": case "zh": case "ko": case "vi": case "th": return Single;
                default: return English;
            }
        }
    }
}
