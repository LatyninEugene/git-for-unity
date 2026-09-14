using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lev.Git
{
    /// <summary>Чья версия берётся при разрешении.</summary>
    public enum MergeSide
    {
        /// <summary>Не решено. В предпросмотре такой конфликт показывает мою сторону.</summary>
        None = 0,
        Mine,
        Theirs,
        Base
    }

    public enum MergeConflictKind
    {
        /// <summary>Обе стороны изменили одно свойство по-разному.</summary>
        Property,

        /// <summary>Обе стороны переставили элементы списков документа, и по-разному.</summary>
        Order,

        /// <summary>Я удалил документ, а они его изменили.</summary>
        RemovedByMine,

        /// <summary>Они удалили документ, а я его изменил.</summary>
        RemovedByTheirs
    }

    /// <summary>
    /// Правка, пришедшая с одной или обеих сторон, — строка в окне слияния.
    ///
    /// Конфликт (<see cref="IsConflict"/>) — место, где автомат не может
    /// решить за человека. Остальные правки решены автоматически, но тоже
    /// выбираемы: человек видит, что пришло с какой стороны, и может взять
    /// другую версию, как в трёхпанельном слиянии IDEA.
    /// </summary>
    public sealed class YamlMergeConflict
    {
        /// <summary>Настоящий конфликт. false — правка, решённая автоматически.</summary>
        public bool IsConflict = true;

        /// <summary>Что выбрал автомат у неконфликтной правки: Mine, Theirs или None — слито из обеих.</summary>
        public MergeSide Auto;

        /// <summary>Текст, собранный из обеих сторон, если автомат слил правку по частям.</summary>
        public string AutoText;

        /// <summary>Обе стороны сделали одну и ту же правку.</summary>
        public bool Same;

        public bool IsDocument => Key == null;

        public bool MineChanged => Mine != Base;
        public bool TheirsChanged => Theirs != Base;

        /// <summary>Какая версия пойдёт в результат: выбор человека или решение автомата.</summary>
        public MergeSide Effective
        {
            get
            {
                if (Choice != MergeSide.None) return Choice;
                return IsConflict ? MergeSide.Mine : Auto;
            }
        }

        /// <summary>Текст в результате. null — в результате этого нет.</summary>
        public string ResultText
        {
            get
            {
                if (Choice == MergeSide.None && !IsConflict && AutoText != null) return AutoText;
                return TextOf(Effective == MergeSide.None ? MergeSide.Mine : Effective);
            }
        }

        public long FileId;
        public MergeConflictKind Kind;

        /// <summary>
        /// Ключ единицы внутри документа: «m_LocalPosition», «m_Children[#{fileID: 5}]».
        /// null — конфликт документа целиком.
        /// </summary>
        public string Key;

        /// <summary>Текст версий. null — на этой стороне единицы нет.</summary>
        public string Base, Mine, Theirs;

        public MergeSide Choice;

        public bool IsResolved => !IsConflict || Choice != MergeSide.None;

        public string TextOf(MergeSide side)
        {
            return side == MergeSide.Theirs ? Theirs : side == MergeSide.Base ? Base : Mine;
        }
    }

    /// <summary>
    /// Файл Unity-YAML, разрезанный на документы.
    ///
    /// Документ хранится ТЕКСТОМ, а не разобранным: слияние переносит в
    /// результат строки как есть, и файл, в котором ничего не решалось,
    /// выходит байт в байт. Разбирать приходится только документы, которые
    /// правили обе стороны.
    /// </summary>
    public sealed class YamlFile
    {
        public string Newline = "\n";
        public bool TrailingNewline;

        /// <summary>Строки до первого документа: %YAML и %TAG.</summary>
        public string Preamble = string.Empty;

        public readonly List<YamlBlock> Blocks = new List<YamlBlock>();

        /// <summary>Документ по ключу — fileID, при повторе с номером.</summary>
        public readonly Dictionary<string, YamlBlock> ByKey = new Dictionary<string, YamlBlock>(StringComparer.Ordinal);

        public static YamlFile Split(string text)
        {
            var file = new YamlFile();
            if (string.IsNullOrEmpty(text)) return file;

            var lines = text.Split('\n');
            int count = lines.Length;

            if (text[text.Length - 1] == '\n') { file.TrailingNewline = true; count--; }
            if (text.IndexOf("\r\n", StringComparison.Ordinal) >= 0) file.Newline = "\r\n";

            for (int i = 0; i < count; i++)
                if (lines[i].Length > 0 && lines[i][lines[i].Length - 1] == '\r')
                    lines[i] = lines[i].Substring(0, lines[i].Length - 1);

            int first = -1;
            for (int i = 0; i < count; i++)
                if (IsDocumentStart(lines[i])) { first = i; break; }

            // Мета и прочий обычный YAML: разделителей документов нет вовсе,
            // и весь файл — один документ без заголовка.
            if (first < 0)
            {
                file.Add(new YamlBlock { FileId = 0, Header = null, Lines = Slice(lines, 0, count) });
                return file;
            }

            file.Preamble = string.Join("\n", lines, 0, first);

            int start = first;
            for (int i = first + 1; i <= count; i++)
            {
                if (i < count && !IsDocumentStart(lines[i])) continue;

                file.Add(new YamlBlock
                {
                    FileId = ParseFileId(lines[start]),
                    Header = lines[start],
                    Lines = Slice(lines, start + 1, i - start - 1)
                });
                start = i;
            }

            return file;
        }

        private void Add(YamlBlock block)
        {
            var key = block.FileId.ToString(CultureInfo.InvariantCulture);

            // Повтор fileID — файл уже испорчен. Слить его всё равно нужно, а
            // потерять второй документ нельзя, поэтому ключ получает номер.
            if (ByKey.ContainsKey(key))
            {
                int n = 2;
                while (ByKey.ContainsKey(key + "#" + n)) n++;
                key = key + "#" + n;
            }

            block.Key = key;
            Blocks.Add(block);
            ByKey[key] = block;
        }

        private static string[] Slice(string[] lines, int start, int count)
        {
            var result = new string[count];
            Array.Copy(lines, start, result, 0, count);
            return result;
        }

        public static bool IsDocumentStart(string line)
        {
            return line.StartsWith("--- ", StringComparison.Ordinal) || line == "---";
        }

        private static long ParseFileId(string header)
        {
            int amp = header.IndexOf('&');
            if (amp < 0) return 0;

            int end = amp + 1;
            while (end < header.Length && (char.IsDigit(header[end]) || header[end] == '-')) end++;

            long id;
            return long.TryParse(header.Substring(amp + 1, end - amp - 1), NumberStyles.AllowLeadingSign,
                                 CultureInfo.InvariantCulture, out id) ? id : 0;
        }
    }

    public sealed class YamlBlock
    {
        public string Key;
        public long FileId;

        /// <summary>«--- !u!1 &amp;123» или null у файла без разделителей.</summary>
        public string Header;

        public string[] Lines;

        private string _text;

        /// <summary>Документ целиком, строки через \n.</summary>
        public string Text
        {
            get
            {
                if (_text != null) return _text;
                var body = string.Join("\n", Lines);
                _text = Header == null ? body : (Lines.Length == 0 ? Header : Header + "\n" + body);
                return _text;
            }
        }

        /// <summary>Имя типа — первая строка тела: «GameObject:».</summary>
        public string TypeName
        {
            get
            {
                if (Header == null) return null;
                foreach (var l in Lines)
                    if (l.Length > 1 && l[0] != ' ' && l[l.Length - 1] == ':') return l.Substring(0, l.Length - 1);
                return null;
            }
        }
    }

    /// <summary>
    /// Смысловая единица внутри документа: строка свойства, заголовок
    /// вложенной карты, элемент списка с ключом или список целиком.
    /// </summary>
    internal sealed class YamlUnit
    {
        public string Key;

        /// <summary>Строки единицы через \n. У заголовка списка с ключами — null.</summary>
        public string Text;

        /// <summary>
        /// У заголовка списка с ключами: отступ и имя. Сам заголовок не
        /// хранится, а пишется при сборке: «m_Children:» или «m_Children: []» —
        /// смотря, остались ли в списке элементы.
        /// </summary>
        public string ListHeader;

        public bool IsListHeader => ListHeader != null;

        /// <summary>Значение для сравнения сторон.</summary>
        public string Value => IsListHeader ? "\u0001" + ListHeader : Text;
    }

    /// <summary>
    /// Разбиение тела документа на единицы.
    ///
    /// Разбор тот же, что у <see cref="UnityYamlParser"/>, но результат другой:
    /// не карта значений, а упорядоченные куски исходного текста с ключами.
    /// Слияние решает по ключам, а в результат кладёт текст — поэтому
    /// форматирование Unity не пересобирается и не «плывёт».
    /// </summary>
    internal static class YamlUnits
    {
        /// <summary>
        /// Списки, элементы которых различимы сами по себе: ссылками или
        /// парой «цель + путь свойства». Их можно сливать поэлементно — один
        /// добавил ребёнка, другой добавил другого, в результате оба.
        ///
        /// Все прочие списки неделимы. У m_Materials, например, важен номер
        /// слота, и один материал бывает в двух слотах: поэлементное слияние
        /// там молча перепутало бы материалы.
        /// </summary>
        private static readonly HashSet<string> KeyedLists = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_Component",
            "m_Children",
            "m_Modifications",
            "m_RemovedComponents",
            "m_RemovedGameObjects",
            "m_AddedGameObjects",
            "m_AddedComponents",
            "m_Roots"
        };

        private struct Frame
        {
            public int Indent;
            public string Key;
        }

        public static List<YamlUnit> Build(YamlBlock block)
        {
            return Build(block, null);
        }

        /// <param name="atomic">
        /// Пути списков, которые нужно держать неделимыми, даже если их можно
        /// разложить поэлементно. Нужно, когда на одной из сторон список
        /// разложить не вышло: иначе одна сторона дала бы единицу «весь список»,
        /// другая — «заголовок и элементы», и в результат попали бы обе.
        /// </param>
        public static List<YamlUnit> Build(YamlBlock block, HashSet<string> atomic)
        {
            var units = new List<YamlUnit>();
            var used = new HashSet<string>(StringComparer.Ordinal);

            if (block.Header != null) Add(units, used, "@header", block.Header, null);

            var lines = block.Lines;
            var stack = new List<Frame>();
            bool typeSeen = block.Header == null;
            int i = 0;

            while (i < lines.Length)
            {
                var line = lines[i];
                int indent = Indent(line);
                var content = line.Substring(indent);

                if (!typeSeen && indent == 0 && content.EndsWith(":", StringComparison.Ordinal))
                {
                    Add(units, used, "@type", line, null);
                    typeSeen = true;
                    i++;
                    continue;
                }

                // Пустая строка, продолжение длинного значения, элемент списка
                // без распознанного заголовка — всё это держится за предыдущей
                // единицей. Грубее, но текст не теряется никогда.
                int colon = IsItem(content) ? -1 : FindKeyColon(content);
                if (content.Length == 0 || colon < 0)
                {
                    AppendToLast(units, used, line);
                    i++;
                    continue;
                }

                PopTo(stack, indent);

                var key = content.Substring(0, colon).Trim();
                var value = content.Substring(colon + 1).Trim();
                var path = Prefix(stack) + key;

                if (value.Length > 0)
                {
                    if (value == "[]" && KeyedLists.Contains(key) && (atomic == null || !atomic.Contains(path)))
                        Add(units, used, path + "[]", null, line.Substring(0, indent) + key);
                    else
                        Add(units, used, path, line, null);
                    i++;
                    continue;
                }

                // Ключ без значения: список (элементы Unity пишет на том же
                // отступе, что и ключ) или вложенная карта.
                if (i + 1 < lines.Length && IsItem(lines[i + 1].TrimStart(' ')) && Indent(lines[i + 1]) >= indent)
                {
                    i = AddSequence(units, used, lines, i, indent, key, path, atomic);
                    continue;
                }

                Add(units, used, path + ":", line, null);
                stack.Add(new Frame { Indent = indent, Key = key });
                i++;
            }

            return units;
        }

        private static int AddSequence(List<YamlUnit> units, HashSet<string> used, string[] lines,
                                       int headerIndex, int indent, string key, string path,
                                       HashSet<string> atomic)
        {
            int itemIndent = Indent(lines[headerIndex + 1]);
            var starts = new List<int>();

            int k = headerIndex + 1;
            for (; k < lines.Length; k++)
            {
                int ind = Indent(lines[k]);
                var c = lines[k].Substring(ind);

                if (ind == itemIndent && IsItem(c)) starts.Add(k);
                else if (ind > itemIndent || c.Length == 0) continue;
                else break;
            }

            List<string> ids;
            if (KeyedLists.Contains(key) && (atomic == null || !atomic.Contains(path)) &&
                TryIdentities(lines, starts, k, itemIndent, out ids))
            {
                Add(units, used, path + "[]", null, lines[headerIndex].Substring(0, indent) + key);
                for (int n = 0; n < starts.Count; n++)
                {
                    int end = n + 1 < starts.Count ? starts[n + 1] : k;
                    Add(units, used, path + "[#" + ids[n] + "]", string.Join("\n", lines, starts[n], end - starts[n]), null);
                }
            }
            else
            {
                Add(units, used, path, string.Join("\n", lines, headerIndex, k - headerIndex), null);
            }

            return k;
        }

        /// <summary>
        /// Опознавательный знак элемента: первая строка без «- » и путь
        /// свойства, если он есть. У ссылки это сама ссылка, у переопределения
        /// префаба — цель вместе с путём. Хоть один элемент без знака или два
        /// одинаковых — и список считается неделимым.
        /// </summary>
        private static bool TryIdentities(string[] lines, List<int> starts, int end, int itemIndent, out List<string> ids)
        {
            ids = new List<string>(starts.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int n = 0; n < starts.Count; n++)
            {
                int stop = n + 1 < starts.Count ? starts[n + 1] : end;
                var first = lines[starts[n]].Substring(itemIndent);
                var id = first.Length > 1 ? first.Substring(2).Trim() : string.Empty;

                for (int j = starts[n] + 1; j < stop; j++)
                {
                    var c = lines[j].TrimStart(' ');
                    if (c.StartsWith("propertyPath:", StringComparison.Ordinal))
                        id += "|" + c.Substring("propertyPath:".Length).Trim();
                }

                if (id.Length == 0 || !seen.Add(id)) return false;
                ids.Add(id);
            }

            return true;
        }

        private static void Add(List<YamlUnit> units, HashSet<string> used, string key, string text, string listHeader)
        {
            // Одинаковый путь у двух строк бывает только в испорченном файле.
            // Номер по порядку совпадёт у сторон, пока файл портили одинаково.
            if (!used.Add(key))
            {
                int n = 2;
                while (!used.Add(key + "#" + n)) n++;
                key = key + "#" + n;
            }

            units.Add(new YamlUnit { Key = key, Text = text, ListHeader = listHeader });
        }

        private static void AppendToLast(List<YamlUnit> units, HashSet<string> used, string line)
        {
            if (units.Count == 0 || units[units.Count - 1].IsListHeader)
            {
                Add(units, used, "@text", line, null);
                return;
            }

            var last = units[units.Count - 1];
            last.Text = last.Text + "\n" + line;
        }

        internal static bool IsItem(string content)
        {
            return content.StartsWith("- ", StringComparison.Ordinal) || content == "-";
        }

        internal static int FindKeyColon(string s)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == ':' && depth == 0 && (i + 1 >= s.Length || s[i + 1] == ' ')) return i;
            }
            return -1;
        }

        private static void PopTo(List<Frame> stack, int indent)
        {
            while (stack.Count > 0 && stack[stack.Count - 1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
        }

        private static string Prefix(List<Frame> stack)
        {
            if (stack.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var f in stack) sb.Append(f.Key).Append('.');
            return sb.ToString();
        }

        internal static int Indent(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            return i;
        }
    }

    /// <summary>
    /// Слияние одного документа, правленного обеими сторонами.
    /// Решения по единицам принимаются один раз, а собирается текст заново
    /// при каждом выборе человека — это дёшево, документ короткий.
    /// </summary>
    internal sealed class YamlDocumentMerge
    {
        private enum Pick { Mine, Theirs, Merged, Conflict }

        private sealed class Decision
        {
            public Pick Pick;
            public string Merged;

            /// <summary>Строка окна слияния. null — стороны не расходятся с базой.</summary>
            public YamlMergeConflict Item;
        }

        private readonly List<YamlUnit> _base, _mine, _theirs;
        private readonly Dictionary<string, YamlUnit> _b, _m, _t;
        private readonly Dictionary<string, Decision> _decisions = new Dictionary<string, Decision>(StringComparer.Ordinal);

        public readonly List<YamlMergeConflict> Conflicts = new List<YamlMergeConflict>();

        /// <summary>Все правки документа, включая конфликты.</summary>
        public readonly List<YamlMergeConflict> Changes = new List<YamlMergeConflict>();

        /// <summary>Конфликт порядка, если обе стороны переставили элементы по-разному.</summary>
        public YamlMergeConflict OrderConflict;

        public YamlDocumentMerge(long fileId, YamlBlock baseBlock, YamlBlock mineBlock, YamlBlock theirsBlock)
        {
            _base = baseBlock != null ? YamlUnits.Build(baseBlock) : new List<YamlUnit>();
            _mine = YamlUnits.Build(mineBlock);
            _theirs = YamlUnits.Build(theirsBlock);

            var atomic = MixedLists(_base, _mine, _theirs);
            if (atomic.Count > 0)
            {
                _base = baseBlock != null ? YamlUnits.Build(baseBlock, atomic) : new List<YamlUnit>();
                _mine = YamlUnits.Build(mineBlock, atomic);
                _theirs = YamlUnits.Build(theirsBlock, atomic);
            }
            _b = Index(_base);
            _m = Index(_mine);
            _t = Index(_theirs);

            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in _mine) if (seen.Add(u.Key)) keys.Add(u.Key);
            foreach (var u in _theirs) if (seen.Add(u.Key)) keys.Add(u.Key);
            foreach (var u in _base) if (seen.Add(u.Key)) keys.Add(u.Key);

            foreach (var key in keys)
            {
                var bv = ValueOf(_b, key);
                var mv = ValueOf(_m, key);
                var tv = ValueOf(_t, key);

                var d = new Decision();

                if (mv == tv || tv == bv) d.Pick = Pick.Mine;
                else if (mv == bv) d.Pick = Pick.Theirs;
                else
                {
                    var merged = YamlLineMerge.TryMerge(bv, mv, tv);
                    if (merged != null)
                    {
                        d.Pick = Pick.Merged;
                        d.Merged = merged;
                    }
                    else
                    {
                        d.Pick = Pick.Conflict;
                    }
                }

                // Заголовки списков — служебные единицы: их текст пишет сборка,
                // выбирать там нечего.
                bool header = (Get(_m, key) ?? Get(_t, key) ?? Get(_b, key)).IsListHeader;

                if (!header && (mv != bv || tv != bv))
                {
                    d.Item = new YamlMergeConflict
                    {
                        FileId = fileId,
                        Kind = MergeConflictKind.Property,
                        Key = key,
                        Base = TextOf(_b, key),
                        Mine = TextOf(_m, key),
                        Theirs = TextOf(_t, key),
                        IsConflict = d.Pick == Pick.Conflict,
                        Auto = d.Pick == Pick.Theirs ? MergeSide.Theirs : d.Pick == Pick.Merged ? MergeSide.None : MergeSide.Mine,
                        AutoText = d.Merged,
                        Same = mv == tv
                    };
                    Changes.Add(d.Item);
                    if (d.Item.IsConflict) Conflicts.Add(d.Item);
                }

                _decisions[key] = d;
            }

            bool orderConflict;
            OrderMerge.Merge(Keys(_base), Keys(_mine), Keys(_theirs), AllPresent(), MergeSide.None, out orderConflict);

            if (orderConflict)
            {
                OrderConflict = new YamlMergeConflict
                {
                    FileId = fileId,
                    Kind = MergeConflictKind.Order,
                    Key = "@order",
                    Base = baseBlock != null ? baseBlock.Text : null,
                    Mine = mineBlock.Text,
                    Theirs = theirsBlock.Text
                };
                Conflicts.Add(OrderConflict);
                Changes.Add(OrderConflict);
            }
        }

        /// <summary>Списки, разложенные поэлементно не на всех сторонах.</summary>
        private static HashSet<string> MixedLists(params List<YamlUnit>[] sides)
        {
            var keyed = new HashSet<string>(StringComparer.Ordinal);
            var whole = new HashSet<string>(StringComparer.Ordinal);

            foreach (var side in sides)
                foreach (var u in side)
                {
                    if (u.IsListHeader) keyed.Add(u.Key.Substring(0, u.Key.Length - 2));
                    else whole.Add(u.Key);
                }

            keyed.IntersectWith(whole);
            return keyed;
        }

        /// <summary>Текст документа с учётом решений человека.</summary>
        public string Build()
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            var texts = new Dictionary<string, YamlUnit>(StringComparer.Ordinal);

            foreach (var pair in _decisions)
            {
                var unit = Resolve(pair.Key, pair.Value);
                if (unit == null) continue;
                present.Add(pair.Key);
                texts[pair.Key] = unit;
            }

            bool unused;
            var order = OrderMerge.Merge(Keys(_base), Keys(_mine), Keys(_theirs), present,
                                         OrderConflict != null ? OrderConflict.Choice : MergeSide.None, out unused);

            var sb = new StringBuilder();
            for (int i = 0; i < order.Count; i++)
            {
                var unit = texts[order[i]];
                string text;

                if (unit.IsListHeader)
                {
                    // Пустой список Unity пишет в строку, непустой — заголовком.
                    var prefix = unit.Key.Substring(0, unit.Key.Length - 2) + "[#";
                    bool hasItems = i + 1 < order.Count && order[i + 1].StartsWith(prefix, StringComparison.Ordinal);
                    text = unit.ListHeader + (hasItems ? ":" : ": []");
                }
                else
                {
                    text = unit.Text;
                }

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(text);
            }

            return sb.ToString();
        }

        private YamlUnit Resolve(string key, Decision d)
        {
            var choice = d.Item != null ? d.Item.Choice : MergeSide.None;

            if (choice == MergeSide.None)
            {
                switch (d.Pick)
                {
                    case Pick.Theirs: return Get(_t, key);
                    case Pick.Merged: return new YamlUnit { Key = key, Text = d.Merged };
                    default: return Get(_m, key);
                }
            }

            switch (choice)
            {
                case MergeSide.Theirs: return Get(_t, key);
                case MergeSide.Base: return Get(_b, key);
                default: return Get(_m, key);
            }
        }

        private HashSet<string> AllPresent()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var u in _mine) set.Add(u.Key);
            foreach (var u in _theirs) set.Add(u.Key);
            return set;
        }

        private static Dictionary<string, YamlUnit> Index(List<YamlUnit> units)
        {
            var d = new Dictionary<string, YamlUnit>(StringComparer.Ordinal);
            foreach (var u in units) d[u.Key] = u;
            return d;
        }

        private static List<string> Keys(List<YamlUnit> units)
        {
            var list = new List<string>(units.Count);
            foreach (var u in units) list.Add(u.Key);
            return list;
        }

        private static YamlUnit Get(Dictionary<string, YamlUnit> map, string key)
        {
            YamlUnit u;
            return map.TryGetValue(key, out u) ? u : null;
        }

        private static string ValueOf(Dictionary<string, YamlUnit> map, string key)
        {
            var u = Get(map, key);
            return u != null ? u.Value : null;
        }

        private static string TextOf(Dictionary<string, YamlUnit> map, string key)
        {
            var u = Get(map, key);
            if (u == null) return null;
            return u.IsListHeader ? u.ListHeader + ":" : u.Text;
        }
    }

    /// <summary>
    /// Слияние порядка ключей.
    ///
    /// Шаблоном берётся сторона, которая порядок меняла, — иначе перестановка
    /// детей в иерархии терялась бы молча. Появившееся на другой стороне
    /// вплетается следом за своим соседом слева. Если переставляли обе
    /// стороны, и по-разному, это конфликт: какой порядок верный, знает человек.
    /// </summary>
    internal static class OrderMerge
    {
        public static List<string> Merge(IList<string> b, IList<string> m, IList<string> t,
                                         HashSet<string> present, MergeSide choice, out bool conflict)
        {
            var inB = new HashSet<string>(b, StringComparer.Ordinal);
            var inM = new HashSet<string>(m, StringComparer.Ordinal);
            var inT = new HashSet<string>(t, StringComparer.Ordinal);

            var bc = Common(b, inM, inT);
            var mc = Common(m, inB, inT);
            var tc = Common(t, inB, inM);

            bool mineMoved = !Same(mc, bc);
            bool theirsMoved = !Same(tc, bc);
            conflict = mineMoved && theirsMoved && !Same(mc, tc);

            bool theirsFirst = conflict ? choice == MergeSide.Theirs : (theirsMoved && !mineMoved);
            if (conflict && choice == MergeSide.Base) return Weave(Weave(b, m, present), t, present);

            return theirsFirst ? Weave(t, m, present) : Weave(m, t, present);
        }

        private static List<string> Weave(IList<string> primary, IList<string> secondary, HashSet<string> present)
        {
            var result = new List<string>(primary.Count + 8);
            var position = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var k in primary)
            {
                if (!present.Contains(k) || position.ContainsKey(k)) continue;
                position[k] = result.Count;
                result.Add(k);
            }

            for (int i = 0; i < secondary.Count; i++)
            {
                var k = secondary[i];
                if (!present.Contains(k) || position.ContainsKey(k)) continue;

                // Новое встаёт перед своим соседом справа, а если его нет —
                // после соседа слева. Именно в таком порядке: элемент, дописанный
                // в конец списка, иначе встал бы после последнего общего
                // элемента, а тот мог уехать у другой стороны в начало.
                int at = -1;
                for (int j = i + 1; j < secondary.Count && at < 0; j++)
                {
                    int p;
                    if (position.TryGetValue(secondary[j], out p)) at = p;
                }
                for (int j = i - 1; j >= 0 && at < 0; j--)
                {
                    int p;
                    if (position.TryGetValue(secondary[j], out p)) at = p + 1;
                }
                if (at < 0) at = result.Count;

                result.Insert(at, k);

                // Вставка сдвигает позиции всего, что правее. Пересчёт полный,
                // но только после вставок: в обычном файле их единицы.
                position.Clear();
                for (int n = 0; n < result.Count; n++) position[result[n]] = n;
            }

            return result;
        }

        private static List<string> Common(IList<string> keys, HashSet<string> a, HashSet<string> b)
        {
            var list = new List<string>();
            foreach (var k in keys) if (a.Contains(k) && b.Contains(k)) list.Add(k);
            return list;
        }

        private static bool Same(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>
    /// Слияние внутри единицы, когда её меняли обе стороны.
    ///
    /// Два частых случая, которые на уровне строки выглядят конфликтом, а
    /// по смыслу им не являются: позиция, у которой один поменял x, а другой
    /// y, — и переопределение префаба, у которого один поменял значение, а
    /// другой ссылку. И ещё числа, разошедшиеся в последнем знаке после
    /// пересохранения, — их Unity тоже считает равными.
    /// </summary>
    internal static class YamlLineMerge
    {
        public static string TryMerge(string b, string m, string t)
        {
            if (b == null || m == null || t == null) return null;

            var bl = b.Split('\n');
            var ml = m.Split('\n');
            var tl = t.Split('\n');
            if (bl.Length != ml.Length || ml.Length != tl.Length) return null;

            var result = new string[ml.Length];
            for (int i = 0; i < ml.Length; i++)
            {
                var line = MergeLine(bl[i], ml[i], tl[i]);
                if (line == null) return null;
                result[i] = line;
            }

            return string.Join("\n", result);
        }

        private static string MergeLine(string b, string m, string t)
        {
            if (m == t || t == b) return m;
            if (m == b) return t;

            string mp, mv, tp, tv, bp, bv;
            if (!SplitLine(m, out mp, out mv) || !SplitLine(t, out tp, out tv) || !SplitLine(b, out bp, out bv))
                return null;
            if (mp != tp || mp != bp) return null;

            if (NumbersClose(mv, tv)) return m;

            var bm = OrderedFlow(bv);
            var mm = OrderedFlow(mv);
            var tm = OrderedFlow(tv);
            if (bm == null || mm == null || tm == null) return null;
            if (bm.Count != mm.Count || mm.Count != tm.Count) return null;

            var sb = new StringBuilder(mp).Append('{');
            for (int i = 0; i < mm.Count; i++)
            {
                if (bm[i].Key != mm[i].Key || mm[i].Key != tm[i].Key) return null;

                string x = bm[i].Value, y = mm[i].Value, z = tm[i].Value, v;
                if (y == z || z == x) v = y;
                else if (y == x) v = z;
                else if (NumbersClose(y, z)) v = y;
                else return null;

                if (i > 0) sb.Append(", ");
                sb.Append(mm[i].Key).Append(": ").Append(v);
            }

            return sb.Append('}').ToString();
        }

        /// <summary>«  m_Mass: 5» → «  m_Mass: » и «5». Элемент списка — после «- ».</summary>
        private static bool SplitLine(string line, out string prefix, out string value)
        {
            prefix = value = null;

            int indent = YamlUnits.Indent(line);
            var content = line.Substring(indent);
            int shift = 0;
            if (YamlUnits.IsItem(content)) { shift = 2; content = content.Length > 1 ? content.Substring(2) : string.Empty; }

            int colon = YamlUnits.FindKeyColon(content);
            if (colon < 0 || colon + 2 > content.Length) return false;

            prefix = line.Substring(0, indent + shift + colon + 2);
            value = content.Substring(colon + 2);
            return true;
        }

        private static List<KeyValuePair<string, string>> OrderedFlow(string value)
        {
            if (string.IsNullOrEmpty(value) || value[0] != '{' || value[value.Length - 1] != '}') return null;

            var inner = value.Substring(1, value.Length - 2);
            var list = new List<KeyValuePair<string, string>>();

            int depth = 0, start = 0;
            for (int i = 0; i <= inner.Length; i++)
            {
                if (i < inner.Length)
                {
                    char c = inner[i];
                    if (c == '{' || c == '[') { depth++; continue; }
                    if (c == '}' || c == ']') { depth--; continue; }
                    if (c != ',' || depth != 0) continue;
                }

                var pair = inner.Substring(start, i - start);
                start = i + 1;

                int colon = pair.IndexOf(':');
                if (colon < 0) return null;
                list.Add(new KeyValuePair<string, string>(pair.Substring(0, colon).Trim(), pair.Substring(colon + 1).Trim()));
            }

            return list.Count > 0 ? list : null;
        }

        /// <summary>Порог тот же по смыслу, что в правилах UnityYAMLMerge: шум округления, а не правка.</summary>
        internal static bool NumbersClose(string a, string b)
        {
            double x, y;
            if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;

            double scale = Math.Max(1.0, Math.Max(Math.Abs(x), Math.Abs(y)));
            return Math.Abs(x - y) <= 1e-6 * scale;
        }
    }

    /// <summary>Как документ попал в результат — для сводки и для окна разрешения.</summary>
    public enum YamlDocumentOutcome
    {
        Unchanged,
        FromMine,
        FromTheirs,
        Merged,
        Conflicted
    }

    /// <summary>Документ в плане слияния.</summary>
    public sealed class YamlDocumentPlan
    {
        public string Key;
        public long FileId;
        public YamlBlock Base, Mine, Theirs;
        public YamlDocumentOutcome Outcome;

        /// <summary>Решение человека о документе целиком. Сильнее решений по его единицам.</summary>
        public MergeSide Override;

        public readonly List<YamlMergeConflict> Conflicts = new List<YamlMergeConflict>();

        /// <summary>Все правки документа: конфликты и решённые автоматически.</summary>
        public readonly List<YamlMergeConflict> Changes = new List<YamlMergeConflict>();

        /// <summary>Правка документа целиком, которую автомат взял с одной стороны.</summary>
        internal YamlMergeConflict DocumentChange;

        internal YamlDocumentMerge Merge;
        internal MergeSide AutoSide;
        internal YamlMergeConflict DocumentConflict;

        public string TypeName
        {
            get
            {
                var block = Mine ?? Theirs ?? Base;
                return block != null ? block.TypeName : null;
            }
        }

        public YamlBlock BlockOf(MergeSide side)
        {
            return side == MergeSide.Theirs ? Theirs : side == MergeSide.Base ? Base : Mine;
        }

        /// <summary>Текст документа в результате. null — документа в результате нет.</summary>
        public string Build()
        {
            if (Override != MergeSide.None)
            {
                var chosen = BlockOf(Override);
                return chosen != null ? chosen.Text : null;
            }

            if (DocumentConflict != null)
            {
                var chosen = BlockOf(DocumentConflict.Choice == MergeSide.None ? MergeSide.Mine : DocumentConflict.Choice);
                return chosen != null ? chosen.Text : null;
            }

            if (DocumentChange != null && DocumentChange.Choice != MergeSide.None)
            {
                var chosen = BlockOf(DocumentChange.Choice);
                return chosen != null ? chosen.Text : null;
            }

            if (Merge != null)
            {
                // Документ одной стороны без выборов человека — её текст как есть,
                // без пересборки из единиц.
                bool chosen = false;
                foreach (var c in Changes) if (c.Choice != MergeSide.None) { chosen = true; break; }

                if (!chosen && (Outcome == YamlDocumentOutcome.FromMine || Outcome == YamlDocumentOutcome.FromTheirs))
                {
                    var auto = BlockOf(AutoSide);
                    return auto != null ? auto.Text : null;
                }

                return Merge.Build();
            }

            var block = BlockOf(AutoSide);
            return block != null ? block.Text : null;
        }
    }

    /// <summary>
    /// Трёхстороннее слияние файла Unity-YAML: сцены, префаба, ассета, меты.
    ///
    /// Сопоставление по fileID, как в семантическом diff: документ, который
    /// поменяла одна сторона, берётся с этой стороны целиком и текстом как
    /// есть. Разбирать на свойства приходится только то, что трогали обе.
    /// </summary>
    public sealed class YamlMergeResult
    {
        public YamlFile BaseFile, MineFile, TheirsFile;

        public readonly List<YamlDocumentPlan> Documents = new List<YamlDocumentPlan>();
        public readonly Dictionary<string, YamlDocumentPlan> ByKey = new Dictionary<string, YamlDocumentPlan>(StringComparer.Ordinal);

        /// <summary>Все конфликты файла в порядке документов.</summary>
        public readonly List<YamlMergeConflict> Conflicts = new List<YamlMergeConflict>();

        /// <summary>Все правки файла — конфликты и решённые автоматически.</summary>
        public readonly List<YamlMergeConflict> Changes = new List<YamlMergeConflict>();

        /// <summary>Сбросить все выборы человека: вернуть решения автомата и нерешённые конфликты.</summary>
        public void ResetChoices()
        {
            foreach (var c in Changes) c.Choice = MergeSide.None;
            foreach (var d in Documents) d.Override = MergeSide.None;
        }

        public int Unresolved
        {
            get
            {
                int n = 0;
                foreach (var d in Documents)
                {
                    if (d.Override != MergeSide.None) continue;
                    foreach (var c in d.Conflicts) if (!c.IsResolved) n++;
                }
                return n;
            }
        }

        public int Count(YamlDocumentOutcome outcome)
        {
            int n = 0;
            foreach (var d in Documents) if (d.Outcome == outcome) n++;
            return n;
        }

        /// <summary>Все документы с этим fileID. Обычно один.</summary>
        public IEnumerable<YamlDocumentPlan> Find(long fileId)
        {
            foreach (var d in Documents) if (d.FileId == fileId) yield return d;
        }

        public void SetDocumentSide(long fileId, MergeSide side)
        {
            foreach (var d in Find(fileId)) d.Override = side;
        }

        /// <summary>Решить все нерешённые конфликты одной стороной.</summary>
        public void ResolveAll(MergeSide side, bool overwrite = false)
        {
            foreach (var c in Conflicts)
                if (overwrite || !c.IsResolved) c.Choice = side;
        }

        public string Build()
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var d in Documents)
            {
                var text = d.Build();
                if (text == null) continue;
                present.Add(d.Key);
                texts[d.Key] = text;
            }

            bool unused;
            var order = OrderMerge.Merge(KeysOf(BaseFile), KeysOf(MineFile), KeysOf(TheirsFile),
                                         present, MergeSide.None, out unused);

            var preamble = MineFile.Preamble.Length > 0 ? MineFile.Preamble : TheirsFile.Preamble;

            var sb = new StringBuilder();
            if (preamble.Length > 0) sb.Append(preamble);

            foreach (var key in order)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(texts[key]);
            }

            var newline = MineFile.Blocks.Count > 0 || MineFile.Preamble.Length > 0 ? MineFile.Newline : TheirsFile.Newline;
            bool trailing = MineFile.Blocks.Count > 0 ? MineFile.TrailingNewline : TheirsFile.TrailingNewline;

            if (sb.Length > 0 && trailing) sb.Append('\n');

            var result = sb.ToString();
            return newline == "\n" ? result : result.Replace("\n", newline);
        }

        private static List<string> KeysOf(YamlFile file)
        {
            var keys = new List<string>(file.Blocks.Count);
            foreach (var b in file.Blocks) keys.Add(b.Key);
            return keys;
        }
    }

    public static class YamlMerge
    {
        public static YamlMergeResult Merge(string baseText, string mineText, string theirsText)
        {
            var r = new YamlMergeResult
            {
                BaseFile = YamlFile.Split(baseText),
                MineFile = YamlFile.Split(mineText),
                TheirsFile = YamlFile.Split(theirsText)
            };

            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in r.MineFile.Blocks) if (seen.Add(b.Key)) keys.Add(b.Key);
            foreach (var b in r.TheirsFile.Blocks) if (seen.Add(b.Key)) keys.Add(b.Key);
            foreach (var b in r.BaseFile.Blocks) if (seen.Add(b.Key)) keys.Add(b.Key);

            foreach (var key in keys)
            {
                var plan = new YamlDocumentPlan
                {
                    Key = key,
                    Base = Block(r.BaseFile, key),
                    Mine = Block(r.MineFile, key),
                    Theirs = Block(r.TheirsFile, key)
                };
                plan.FileId = (plan.Mine ?? plan.Theirs ?? plan.Base).FileId;

                Decide(plan);

                r.Documents.Add(plan);
                r.ByKey[key] = plan;
                r.Conflicts.AddRange(plan.Conflicts);
                r.Changes.AddRange(plan.Changes);
            }

            return r;
        }

        private static void Decide(YamlDocumentPlan plan)
        {
            var b = plan.Base != null ? plan.Base.Text : null;
            var m = plan.Mine != null ? plan.Mine.Text : null;
            var t = plan.Theirs != null ? plan.Theirs.Text : null;

            if (m == t || t == b || m == b)
            {
                plan.AutoSide = m == b && m != t ? MergeSide.Theirs : MergeSide.Mine;
                plan.Outcome = m == t && m == b ? YamlDocumentOutcome.Unchanged
                             : plan.AutoSide == MergeSide.Theirs ? YamlDocumentOutcome.FromTheirs
                             : YamlDocumentOutcome.FromMine;

                if (plan.Outcome == YamlDocumentOutcome.Unchanged) return;

                // Документ, который есть на всех сторонах, раскладывается на
                // свойства и тогда, когда его меняла одна сторона: иначе
                // человек мог бы выбрать только весь документ разом. Для
                // иерархии это важно — место ребёнка в родителе — элемент
                // m_Children, и без разбора его нельзя взять вместе с ребёнком.
                if (plan.Base != null && plan.Mine != null && plan.Theirs != null)
                {
                    plan.Merge = new YamlDocumentMerge(plan.FileId, plan.Base, plan.Mine, plan.Theirs);
                    plan.Changes.AddRange(plan.Merge.Changes);
                }
                else
                {
                    AddDocumentChange(plan, b, m, t, plan.AutoSide);
                }
                return;
            }

            // Удалён одной стороной и изменён другой. Кусками тут сливать
            // нечего: документ либо есть, либо его нет.
            if (m == null || t == null)
            {
                plan.DocumentConflict = new YamlMergeConflict
                {
                    FileId = plan.FileId,
                    Kind = m == null ? MergeConflictKind.RemovedByMine : MergeConflictKind.RemovedByTheirs,
                    Base = b,
                    Mine = m,
                    Theirs = t
                };
                plan.Conflicts.Add(plan.DocumentConflict);
                plan.Changes.Add(plan.DocumentConflict);
                plan.Outcome = YamlDocumentOutcome.Conflicted;
                return;
            }

            // Правили обе стороны (или обе добавили документ с одним fileID) —
            // сливаем по свойствам. У добавленного обеими база пустая.
            plan.Merge = new YamlDocumentMerge(plan.FileId, plan.Base, plan.Mine, plan.Theirs);
            plan.Conflicts.AddRange(plan.Merge.Conflicts);
            plan.Changes.AddRange(plan.Merge.Changes);
            plan.Outcome = plan.Conflicts.Count > 0 ? YamlDocumentOutcome.Conflicted : YamlDocumentOutcome.Merged;
        }

        private static void AddDocumentChange(YamlDocumentPlan plan, string b, string m, string t, MergeSide auto)
        {
            plan.DocumentChange = new YamlMergeConflict
            {
                FileId = plan.FileId,
                Kind = MergeConflictKind.Property,
                Key = null,
                Base = b,
                Mine = m,
                Theirs = t,
                IsConflict = false,
                Auto = auto,
                Same = m == t
            };
            plan.Changes.Add(plan.DocumentChange);
        }

        private static YamlBlock Block(YamlFile file, string key)
        {
            YamlBlock b;
            return file.ByKey.TryGetValue(key, out b) ? b : null;
        }

        /// <summary>
        /// Человеческое имя ключа единицы: «m_Children: {fileID: 5}»,
        /// «переопределение m_Name», «порядок элементов».
        /// </summary>
        public static string DescribeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return L.T("whole document");
            if (key == "@header") return L.T("document header");
            if (key == "@type") return L.T("document type");
            if (key == "@order") return L.T("element order in lists");
            if (key.StartsWith("@text", StringComparison.Ordinal)) return L.T("text without a key");

            int item = key.IndexOf("[#", StringComparison.Ordinal);
            if (item > 0 && key.EndsWith("]", StringComparison.Ordinal))
            {
                var list = key.Substring(0, item);
                var id = key.Substring(item + 2, key.Length - item - 3);
                int bar = id.LastIndexOf('|');
                return bar >= 0 ? L.F("override {0}", id.Substring(bar + 1)) : list + ": " + id;
            }

            if (key.EndsWith("[]", StringComparison.Ordinal)) return L.F("list {0}", key.Substring(0, key.Length - 2));
            if (key.EndsWith(":", StringComparison.Ordinal)) return key.Substring(0, key.Length - 1);
            return key;
        }
    }
}
