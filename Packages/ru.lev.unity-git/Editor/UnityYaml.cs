using System;
using System.Collections.Generic;
using System.Text;

namespace Lev.Git
{
    /// <summary>Один сериализованный объект из сцены или префаба.</summary>
    public sealed class UnityDocument
    {
        /// <summary>Локальный идентификатор — число после &amp; в заголовке документа.</summary>
        public long FileId;

        /// <summary>Класс Unity: 1 — GameObject, 4 — Transform, 114 — MonoBehaviour и так далее.</summary>
        public int ClassId;

        /// <summary>Имя типа: GameObject, Transform, BoxCollider, PrefabInstance…</summary>
        public string TypeName;

        /// <summary>
        /// Свойства, разложенные в плоскую карту: «m_LocalPosition.x», «m_Component[0].component».
        ///
        /// Плоско, а не деревом, намеренно: сравнение двух версий объекта
        /// превращается в сравнение двух словарей, а путь свойства сразу читается
        /// человеком и годится прямо в текст изменения.
        /// </summary>
        public readonly Dictionary<string, string> Props = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Get(string key)
        {
            string v;
            return Props.TryGetValue(key, out v) ? v : null;
        }
    }

    /// <summary>
    /// Разбор Unity-YAML.
    ///
    /// Своя реализация вместо полноценной библиотеки YAML — сознательно.
    /// Формат Unity ограничен: отступы по два пробела, скаляры в одну строку,
    /// последовательности из простых элементов. Нужен же от него не полный
    /// объектный граф, а плоская карта «путь свойства → значение», по которой
    /// считается разница. Библиотека дала бы дерево, которое всё равно пришлось
    /// бы разворачивать, и заметно более медленный разбор на файлах в десятки
    /// мегабайт.
    ///
    /// Всё, чего парсер не понимает, он пропускает: сцена не обязана
    /// разбираться целиком, чтобы показать понятную разницу, а падать на
    /// незнакомом поле нельзя — формат меняется от версии Unity к версии.
    /// </summary>
    public static class UnityYamlParser
    {
        private struct Frame
        {
            public int Indent;
            public string Key;
        }

        /// <summary>
        /// Разбирает значение-карту, записанное в одну строку:
        /// {x: 0, y: 1, z: -10} или {fileID: 10304, guid: 0000…, type: 0}.
        /// Возвращает null, если значение картой не является.
        ///
        /// Парсер хранит такие значения целиком и не разворачивает: так он
        /// остаётся простым и ничего не теряет. Разбирать их — задача того, кто
        /// показывает разницу: «m_LocalPosition.y: 1 → 5» читается лучше, чем
        /// вектор целиком с обеих сторон.
        /// </summary>
        public static Dictionary<string, string> ParseFlowMap(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            value = value.Trim();
            if (value.Length < 2 || value[0] != '{' || value[value.Length - 1] != '}') return null;

            var inner = value.Substring(1, value.Length - 2);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

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

                var key = pair.Substring(0, colon).Trim();
                if (key.Length == 0) return null;

                result[key] = pair.Substring(colon + 1).Trim();
            }

            return result.Count > 0 ? result : null;
        }

        /// <summary>Похож ли файл на сцену или префаб Unity.</summary>
        public static bool LooksLikeUnityYaml(string text)
        {
            return !string.IsNullOrEmpty(text) &&
                   text.StartsWith("%YAML", StringComparison.Ordinal) &&
                   text.IndexOf("!u!", StringComparison.Ordinal) > 0;
        }

        public static List<UnityDocument> Parse(string text)
        {
            var docs = new List<UnityDocument>();
            if (string.IsNullOrEmpty(text)) return docs;

            UnityDocument current = null;
            bool awaitingTypeName = false;

            var stack = new List<Frame>();
            var counters = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0) continue;

                if (line.StartsWith("%", StringComparison.Ordinal)) continue;

                if (line.StartsWith("---", StringComparison.Ordinal))
                {
                    current = StartDocument(line);
                    if (current != null) docs.Add(current);
                    awaitingTypeName = current != null;
                    stack.Clear();
                    counters.Clear();
                    continue;
                }

                if (current == null) continue;

                int indent = Indent(line);
                var content = line.Substring(indent);

                // Имя типа — единственная строка документа с нулевым отступом.
                if (awaitingTypeName)
                {
                    if (indent == 0 && content.EndsWith(":", StringComparison.Ordinal))
                    {
                        current.TypeName = content.Substring(0, content.Length - 1).Trim();
                        awaitingTypeName = false;
                    }
                    continue;
                }

                if (content.StartsWith("- ", StringComparison.Ordinal) || content == "-")
                    HandleSequenceItem(current, stack, counters, indent, content);
                else
                    HandleMapping(current, stack, counters, indent, content);
            }

            return docs;
        }

        private static UnityDocument StartDocument(string line)
        {
            // Формат заголовка: --- !u!<classId> &<fileId>[ stripped]
            int bang = line.IndexOf("!u!", StringComparison.Ordinal);
            int amp = line.IndexOf('&');
            if (bang < 0 || amp < 0) return null;

            var classPart = line.Substring(bang + 3, amp - bang - 3).Trim();
            var idPart = line.Substring(amp + 1).Trim();

            int space = idPart.IndexOf(' ');
            if (space > 0) idPart = idPart.Substring(0, space);   // « stripped» и подобное

            int classId;
            long fileId;
            if (!int.TryParse(classPart, out classId)) classId = 0;
            if (!long.TryParse(idPart, out fileId)) return null;

            return new UnityDocument { ClassId = classId, FileId = fileId };
        }

        private static void HandleMapping(
            UnityDocument doc, List<Frame> stack, Dictionary<string, int> counters,
            int indent, string content)
        {
            // Ключ на отступе N закрывает все рамки на отступе N и глубже.
            PopTo(stack, indent, false);

            int colon = FindKeyColon(content);
            if (colon < 0) return;

            var key = content.Substring(0, colon).Trim();
            var value = content.Substring(colon + 1).Trim();
            if (key.Length == 0) return;

            var prefix = Prefix(stack);

            if (value.Length == 0)
            {
                // Ключ без значения — начало вложенной карты или последовательности.
                stack.Add(new Frame { Indent = indent, Key = key });
                counters.Remove(prefix + key);
                return;
            }

            doc.Props[prefix + key] = value;
        }

        private static void HandleSequenceItem(
            UnityDocument doc, List<Frame> stack, Dictionary<string, int> counters,
            int indent, string content)
        {
            // Элемент последовательности стоит на том же отступе, что и её ключ,
            // поэтому рамку ключа сохраняем, а всё, что глубже, закрываем.
            PopTo(stack, indent, true);

            var prefix = Prefix(stack);
            if (stack.Count == 0) return;

            var listKey = prefix;                       // уже включает имя ключа с точкой
            if (listKey.EndsWith(".", StringComparison.Ordinal))
                listKey = listKey.Substring(0, listKey.Length - 1);

            int index;
            counters.TryGetValue(listKey, out index);
            counters[listKey] = index + 1;

            // Рамка ключа списка НЕ меняется: номер элемента кладётся отдельной
            // рамкой поверх. Иначе второй элемент строился бы поверх ключа,
            // уже содержащего «[0]», и получалось бы m_Component[0][1].
            //
            // Отступ на полшага глубже: продолжение элемента идёт с отступом
            // indent+2 и не должно её закрывать, а следующий «- » на отступе
            // indent — должен.
            stack.Add(new Frame { Indent = indent + 1, Key = "[" + index + "]" });

            var body = content.Length > 1 ? content.Substring(2).Trim() : string.Empty;
            if (body.Length == 0) return;

            int colon = FindKeyColon(body);
            if (colon < 0)
            {
                // Простой элемент: «- {fileID: 123}».
                var flat = Prefix(stack);
                if (flat.EndsWith(".", StringComparison.Ordinal))
                    flat = flat.Substring(0, flat.Length - 1);
                doc.Props[flat] = body;
                return;
            }

            var key = body.Substring(0, colon).Trim();
            var value = body.Substring(colon + 1).Trim();

            if (value.Length == 0)
            {
                stack.Add(new Frame { Indent = indent + 2, Key = key });
                return;
            }

            doc.Props[Prefix(stack) + key] = value;
        }

        /// <summary>
        /// Двоеточие, отделяющее ключ. Не первое попавшееся: значение вида
        /// {fileID: 0} тоже содержит двоеточия, и по ним ключ резать нельзя.
        /// </summary>
        private static int FindKeyColon(string s)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == ':' && depth == 0)
                {
                    // Ключ заканчивается двоеточием, за которым пробел или конец строки.
                    if (i + 1 >= s.Length || s[i + 1] == ' ') return i;
                }
            }
            return -1;
        }

        private static void PopTo(List<Frame> stack, int indent, bool keepSameIndent)
        {
            while (stack.Count > 0)
            {
                var top = stack[stack.Count - 1];
                bool drop = keepSameIndent ? top.Indent > indent : top.Indent >= indent;
                if (!drop) break;
                stack.RemoveAt(stack.Count - 1);
            }
        }

        private static string Prefix(List<Frame> stack)
        {
            if (stack.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            foreach (var f in stack)
            {
                if (f.Key.Length == 0) continue;
                // Номер элемента прилипает к имени списка без точки:
                // m_Component[0], а не m_Component.[0].
                if (sb.Length > 0 && f.Key[0] != '[') sb.Append('.');
                sb.Append(f.Key);
            }
            if (sb.Length > 0) sb.Append('.');
            return sb.ToString();
        }

        private static int Indent(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            return i;
        }
    }
}
