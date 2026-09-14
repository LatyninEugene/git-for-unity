using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lev.Git
{
    /// <summary>
    /// Откат части Unity-YAML файла к версии из коммита — правкой текста.
    ///
    /// Нужен там, где живых объектов нет или они не главные: закрытая сцена,
    /// префаб, материал, ScriptableObject, контроллер аниматора. Меняются только
    /// строки нужного документа или свойства — остальной файл остаётся байт в
    /// байт, чтобы после отката в diff не появлялось лишнего.
    ///
    /// Чистый код без Unity: проверяется тестами на синтетических файлах.
    /// Каждый метод возвращает новый текст или null, если менять нечего.
    /// </summary>
    public static class YamlRevert
    {
        private sealed class Range
        {
            public int Start, Count;
        }

        private sealed class ModEntry
        {
            public string Key;
            public readonly List<string> Lines = new List<string>();
        }

        // ------------------------------------------------------------ документ ---

        /// <summary>
        /// Документ целиком — как в коммите. Добавленный после коммита убирается,
        /// удалённый встаёт после ближайшего предшественника по коммиту.
        /// </summary>
        public static string Document(string current, string head, long fileId)
        {
            var cur = YamlFile.Split(current ?? string.Empty);
            var old = YamlFile.Split(head ?? string.Empty);
            var key = Key(fileId);

            cur.ByKey.TryGetValue(key, out var c);
            old.ByKey.TryGetValue(key, out var o);
            if (c == null && o == null) return null;

            var texts = new List<string>();
            var keys = new List<string>();
            foreach (var b in cur.Blocks) { texts.Add(b.Text); keys.Add(b.Key); }

            if (c != null && o != null)
            {
                texts[keys.IndexOf(key)] = o.Text;
            }
            else if (c != null)
            {
                int i = keys.IndexOf(key);
                texts.RemoveAt(i);
                keys.RemoveAt(i);
            }
            else
            {
                int insertAt = 0;
                foreach (var b in old.Blocks)
                {
                    if (b.Key == key) break;
                    int at = keys.IndexOf(b.Key);
                    if (at >= 0) insertAt = at + 1;
                }
                texts.Insert(insertAt, o.Text);
                keys.Insert(insertAt, key);
            }

            return Join(cur, texts);
        }

        // ------------------------------------------------------------ свойство ---

        /// <summary>
        /// Одно свойство документа — как в коммите. «m_LocalPosition.x» в
        /// однострочной карте меняет одно значение; «m_Materials[0]» и вложенные
        /// карты возвращаются свойством целиком — со всеми строками.
        /// </summary>
        public static string Property(string current, string head, long fileId, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var top = TopKey(path);

            return EditDocument(current, head, fileId, (cur, old) =>
            {
                var curRange = FindProperty(cur, top);
                var oldRange = FindProperty(old, top);
                if (curRange == null && oldRange == null) return false;

                // Поле однострочной карты: {x: 1, y: 2, z: 3}.
                if (path.Length > top.Length && path[top.Length] == '.' &&
                    curRange != null && oldRange != null && curRange.Count == 1 && oldRange.Count == 1)
                {
                    var sub = path.Substring(top.Length + 1);
                    var curMap = FlowMap(cur[curRange.Start], out var prefix);
                    var oldMap = FlowMap(old[oldRange.Start], out _);
                    if (curMap != null && oldMap != null && curMap.ContainsKey(sub) && oldMap.TryGetValue(sub, out var value))
                    {
                        if (curMap[sub] == value) return false;
                        curMap[sub] = value;
                        cur[curRange.Start] = prefix + "{" + JoinMap(curMap) + "}";
                        return true;
                    }
                }

                var replacement = oldRange != null ? old.GetRange(oldRange.Start, oldRange.Count) : new List<string>();

                if (curRange != null)
                {
                    if (Same(cur, curRange, replacement)) return false;
                    cur.RemoveRange(curRange.Start, curRange.Count);
                    cur.InsertRange(curRange.Start, replacement);
                    return true;
                }

                // Свойства сейчас нет — ставим после ближайшего предыдущего по коммиту свойства.
                int insertAt = cur.Count;
                for (int i = oldRange.Start - 1; i >= 0; i--)
                {
                    var previous = KeyOfLine(old[i]);
                    if (previous == null) continue;
                    var at = FindProperty(cur, previous);
                    if (at == null) continue;
                    insertAt = at.Start + at.Count;
                    break;
                }
                cur.InsertRange(insertAt, replacement);
                return true;
            });
        }

        // ------------------------------------------------ ссылка на компонент ---

        /// <summary>
        /// Ссылка на компонент в списке m_Component объекта — как в коммите.
        /// Нужна вместе с откатом добавленного или удалённого компонента: документ
        /// без ссылки или ссылка без документа ломают объект.
        /// </summary>
        public static string ComponentLink(string current, string head, long ownerId, long componentId)
        {
            var needle = "{fileID: " + componentId.ToString(CultureInfo.InvariantCulture) + "}";

            return EditDocument(current, head, ownerId, (cur, old) =>
            {
                int curAt = EntryIndex(cur, "m_Component", needle);
                int oldAt = EntryIndex(old, "m_Component", needle);
                if ((curAt >= 0) == (oldAt >= 0)) return false;

                var list = FindProperty(cur, "m_Component");
                if (list == null) return false;

                if (curAt >= 0)
                {
                    cur.RemoveAt(curAt);
                    // Пустой список Unity пишет «m_Component: []».
                    if (list.Count == 2) cur[list.Start] = cur[list.Start].TrimEnd() + " []";
                    return true;
                }

                if (cur[list.Start].EndsWith(" []", StringComparison.Ordinal))
                    cur[list.Start] = cur[list.Start].Substring(0, cur[list.Start].Length - 3);

                int insertAt = list.Start + 1;
                var oldList = FindProperty(old, "m_Component");
                for (int i = oldList.Start + 1; i < oldAt; i++)
                {
                    int at = cur.IndexOf(old[i], list.Start + 1);
                    if (at >= 0 && at < list.Start + list.Count) insertAt = at + 1;
                }
                cur.Insert(insertAt, old[oldAt]);
                return true;
            });
        }

        // ------------------------------------------------ экземпляр префаба ---

        /// <summary>
        /// Переопределения экземпляра — как в коммите. <paramref name="filter"/>
        /// отбирает, какие именно; null — все изменившиеся. Переопределение,
        /// которого в коммите не было, снимается; пропавшее — возвращается.
        /// </summary>
        public static string InstanceOverrides(string current, string head, long instanceId, Func<PrefabModificationChange, bool> filter)
        {
            if (!UnityScene.Build(current ?? string.Empty).ById.TryGetValue(instanceId, out var curDoc) ||
                !UnityScene.Build(head ?? string.Empty).ById.TryGetValue(instanceId, out var oldDoc))
                return null;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in PrefabOverrides.Changes(oldDoc, curDoc))
                if (filter == null || filter(change)) keys.Add(change.Key);
            if (keys.Count == 0) return null;

            return EditDocument(current, head, instanceId, (cur, old) =>
            {
                var curList = ModificationList(cur, out var curEntries);
                ModificationList(old, out var oldEntries);
                if (curList == null) return false;

                var result = new List<string>();
                var present = new HashSet<string>(StringComparer.Ordinal);

                foreach (var entry in curEntries)
                {
                    present.Add(entry.Key);
                    if (!keys.Contains(entry.Key)) { result.AddRange(entry.Lines); continue; }

                    var committed = oldEntries.Find(e => e.Key == entry.Key);
                    if (committed != null) result.AddRange(committed.Lines);
                }

                foreach (var entry in oldEntries)
                    if (keys.Contains(entry.Key) && !present.Contains(entry.Key)) result.AddRange(entry.Lines);

                var header = cur[curList.Start];
                var bare = header.EndsWith(" []", StringComparison.Ordinal) ? header.Substring(0, header.Length - 3) : header;

                cur.RemoveRange(curList.Start + 1, curList.Count - 1);
                cur.InsertRange(curList.Start + 1, result);
                cur[curList.Start] = result.Count == 0 ? bare.TrimEnd() + " []" : bare;
                return true;
            });
        }

        // -------------------------------------------------------------- разбор ---

        private static string Key(long fileId)
        {
            return fileId.ToString(CultureInfo.InvariantCulture);
        }

        private static string EditDocument(string current, string head, long fileId, Func<List<string>, List<string>, bool> edit)
        {
            var cur = YamlFile.Split(current ?? string.Empty);
            var old = YamlFile.Split(head ?? string.Empty);
            var key = Key(fileId);
            if (!cur.ByKey.TryGetValue(key, out var c) || !old.ByKey.TryGetValue(key, out var o)) return null;

            var lines = new List<string>(c.Lines);
            if (!edit(lines, new List<string>(o.Lines))) return null;

            var texts = new List<string>();
            foreach (var b in cur.Blocks)
                texts.Add(ReferenceEquals(b, c) ? Compose(c.Header, lines) : b.Text);
            return Join(cur, texts);
        }

        private static string Compose(string header, List<string> lines)
        {
            if (header == null) return string.Join("\n", lines.ToArray());
            return lines.Count == 0 ? header : header + "\n" + string.Join("\n", lines.ToArray());
        }

        /// <summary>Файл обратно в текст — с его переводами строк и завершающим переводом.</summary>
        private static string Join(YamlFile file, List<string> blocks)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(file.Preamble)) sb.Append(file.Preamble);
            foreach (var text in blocks)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(text);
            }
            if (file.TrailingNewline) sb.Append('\n');

            var result = sb.ToString();
            return file.Newline == "\r\n" ? result.Replace("\n", "\r\n") : result;
        }

        private static string TopKey(string path)
        {
            int cut = path.Length;
            int dot = path.IndexOf('.');
            int bracket = path.IndexOf('[');
            if (dot >= 0) cut = Math.Min(cut, dot);
            if (bracket >= 0) cut = Math.Min(cut, bracket);
            return path.Substring(0, cut);
        }

        /// <summary>
        /// Свойство верхнего уровня документа: строка «  ключ:» и всё, что к ней
        /// относится, — строки глубже и элементы списка «  - », которые Unity пишет
        /// на том же отступе.
        /// </summary>
        private static Range FindProperty(List<string> lines, string key)
        {
            var head = "  " + key + ":";
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (!line.StartsWith(head, StringComparison.Ordinal)) continue;
                if (line.Length > head.Length && line[head.Length] != ' ') continue;

                int end = i + 1;
                while (end < lines.Count &&
                       (lines[end].StartsWith("   ", StringComparison.Ordinal) ||
                        lines[end].StartsWith("  - ", StringComparison.Ordinal) ||
                        lines[end] == "  -"))
                    end++;

                return new Range { Start = i, Count = end - i };
            }
            return null;
        }

        private static string KeyOfLine(string line)
        {
            if (line.Length < 4 || !line.StartsWith("  ", StringComparison.Ordinal) || line[2] == ' ' || line[2] == '-') return null;
            int colon = line.IndexOf(':');
            return colon > 2 ? line.Substring(2, colon - 2) : null;
        }

        private static Dictionary<string, string> FlowMap(string line, out string prefix)
        {
            prefix = null;
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) return null;

            var value = line.Substring(colon + 2).Trim();
            if (!value.StartsWith("{", StringComparison.Ordinal) || !value.EndsWith("}", StringComparison.Ordinal)) return null;

            prefix = line.Substring(0, colon + 2);
            return UnityYamlParser.ParseFlowMap(value);
        }

        private static string JoinMap(Dictionary<string, string> map)
        {
            var parts = new List<string>();
            foreach (var pair in map) parts.Add(pair.Key + ": " + pair.Value);
            return string.Join(", ", parts.ToArray());
        }

        private static bool Same(List<string> lines, Range range, List<string> replacement)
        {
            if (range.Count != replacement.Count) return false;
            for (int i = 0; i < range.Count; i++)
                if (lines[range.Start + i] != replacement[i]) return false;
            return true;
        }

        private static int EntryIndex(List<string> lines, string key, string needle)
        {
            var range = FindProperty(lines, key);
            if (range == null) return -1;
            for (int i = range.Start + 1; i < range.Start + range.Count; i++)
                if (lines[i].Contains(needle)) return i;
            return -1;
        }

        /// <summary>Список m_Modifications: его строки и записи «- target / propertyPath / value / objectReference».</summary>
        private static Range ModificationList(List<string> lines, out List<ModEntry> entries)
        {
            entries = new List<ModEntry>();

            int header = -1;
            string indent = null;
            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith("m_Modifications:", StringComparison.Ordinal)) continue;
                header = i;
                indent = lines[i].Substring(0, lines[i].Length - trimmed.Length);
                break;
            }
            if (header < 0) return null;

            var item = indent + "- ";
            var deeper = indent + "  ";
            int end = header + 1;
            ModEntry entry = null;

            while (end < lines.Count)
            {
                var line = lines[end];
                if (line.StartsWith(item, StringComparison.Ordinal))
                {
                    entry = new ModEntry();
                    entries.Add(entry);
                }
                else if (entry == null || !line.StartsWith(deeper, StringComparison.Ordinal))
                {
                    break;
                }
                entry.Lines.Add(line);
                end++;
            }

            foreach (var e in entries) e.Key = EntryKey(e.Lines);
            return new Range { Start = header, Count = end - header };
        }

        /// <summary>Ключ записи — тот же, что у <see cref="PrefabModification.Key"/>: «guid|fileId|propertyPath».</summary>
        private static string EntryKey(List<string> lines)
        {
            string guid = null, fileId = null, path = null;
            foreach (var raw in lines)
            {
                var t = raw.TrimStart();
                if (t.StartsWith("- ", StringComparison.Ordinal)) t = t.Substring(2);

                if (t.StartsWith("target:", StringComparison.Ordinal))
                {
                    var map = UnityYamlParser.ParseFlowMap(t.Substring(7).Trim());
                    if (map != null)
                    {
                        map.TryGetValue("fileID", out fileId);
                        map.TryGetValue("guid", out guid);
                    }
                }
                else if (t.StartsWith("propertyPath:", StringComparison.Ordinal))
                {
                    path = t.Substring(13).Trim();
                }
            }
            return guid + "|" + fileId + "|" + path;
        }
    }
}
