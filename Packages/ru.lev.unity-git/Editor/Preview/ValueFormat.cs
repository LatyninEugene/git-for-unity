using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lev.Git.Preview
{
    /// <summary>
    /// Значения из YAML в виде, удобном человеку: цвет — образцом, ссылка на
    /// ассет — его именем, вектор — компактно. Без Unity, чтобы проверялось тестами.
    /// </summary>
    public static class ValueFormat
    {
        public static bool TryColor(string raw, out float r, out float g, out float b, out float a)
        {
            r = g = b = 0f;
            a = 1f;

            var map = UnityYamlParser.ParseFlowMap(raw);
            if (map == null || map.Count < 3 || map.Count > 4) return false;
            if (!F(map, "r", out r) || !F(map, "g", out g) || !F(map, "b", out b)) return false;
            if (map.Count == 4 && !F(map, "a", out a)) return false;
            return true;
        }

        /// <summary>Ссылка на ассет другого файла: {fileID: …, guid: …, type: …}.</summary>
        public static bool TryAssetRef(string raw, out string guid, out long fileId)
        {
            guid = null;
            fileId = 0;

            var map = UnityYamlParser.ParseFlowMap(raw);
            string g, id;
            if (map == null || !map.TryGetValue("guid", out g) || !map.TryGetValue("fileID", out id)) return false;

            g = g.Trim();
            if (g.Length != 32 || g.Trim('0').Length == 0) return false;
            foreach (var c in g)
                if (!Uri.IsHexDigit(c)) return false;

            if (!long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out fileId)) return false;
            guid = g;
            return true;
        }

        /// <summary>
        /// {x: 1, y: 2, z: 3} → (1, 2, 3); {fileID: 0} → None. Прочее — как есть.
        /// </summary>
        public static string Compact(string raw)
        {
            if (raw == null) return "—";

            var map = UnityYamlParser.ParseFlowMap(raw);
            if (map == null) return raw;

            string id;
            if (map.Count == 1 && map.TryGetValue("fileID", out id) && id.Trim() == "0") return "None";

            var order = new[] { "x", "y", "z", "w" };
            if (map.Count >= 2 && map.Count <= 4)
            {
                var parts = new List<string>();
                for (int i = 0; i < map.Count; i++)
                {
                    string v;
                    if (!map.TryGetValue(order[i], out v)) return raw;
                    parts.Add(v.Trim());
                }
                return "(" + string.Join(", ", parts.ToArray()) + ")";
            }

            return raw;
        }

        private static bool F(Dictionary<string, string> map, string key, out float value)
        {
            string raw;
            value = 0f;
            return map.TryGetValue(key, out raw) &&
                   float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
