using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Lev.Git
{
    /// <summary>
    /// Работа с отдельными документами внутри текста сцены — без разбора всего
    /// файла. Нужна, чтобы показать настройки сцены настоящим инспектором:
    /// документ вырезается из версии сцены и загружается сам по себе.
    /// </summary>
    public static class SceneDocText
    {
        public const string Header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";

        /// <summary>Документ с этим fileID целиком, начиная со строки «--- !u!…». null — такого нет.</summary>
        public static string Extract(string text, long fileId)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var marker = new Regex(@"^--- !u!\d+ &" + fileId.ToString(CultureInfo.InvariantCulture) + @"(\s|$)",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            var m = marker.Match(text);
            if (!m.Success) return null;

            int end = text.IndexOf("\n--- ", m.Index + 1, StringComparison.Ordinal);
            var section = end < 0 ? text.Substring(m.Index) : text.Substring(m.Index, end - m.Index + 1);
            return section.Replace("\r\n", "\n");
        }

        /// <summary>Один документ как самостоятельный файл Unity-YAML.</summary>
        public static string Standalone(string section)
        {
            if (string.IsNullOrEmpty(section)) return null;
            return Header + (section.EndsWith("\n", StringComparison.Ordinal) ? section : section + "\n");
        }

        /// <summary>Имя объекта по fileID его Transform. null — объект не найден (например, внутри префаба).</summary>
        public static string NameOfTransform(string text, long transformId)
        {
            var transform = Extract(text, transformId);
            if (transform == null) return null;

            var owner = Regex.Match(transform, @"^\s*m_GameObject: \{fileID: (-?\d+)\}", RegexOptions.Multiline);
            long ownerId;
            if (!owner.Success || !long.TryParse(owner.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerId))
                return null;

            var go = Extract(text, ownerId);
            if (go == null) return null;

            var name = Regex.Match(go, @"^\s*m_Name: (.*)$", RegexOptions.Multiline);
            if (!name.Success) return null;

            var value = name.Groups[1].Value.Trim();
            if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[value.Length - 1] == value[0])
                value = value.Substring(1, value.Length - 2);
            return value;
        }

        /// <summary>fileID корневых Transform по порядку из документа SceneRoots.</summary>
        public static List<long> Roots(UnityDocument doc)
        {
            var result = new List<long>();
            if (doc == null) return result;

            for (int i = 0; i < 100000; i++)
            {
                var raw = doc.Get("m_Roots[" + i + "]");
                string id = null;

                if (raw != null)
                {
                    var map = UnityYamlParser.ParseFlowMap(raw);
                    if (map != null) map.TryGetValue("fileID", out id);
                }
                else
                {
                    id = doc.Get("m_Roots[" + i + "].fileID");
                    if (id == null) break;
                }

                long value;
                if (id != null && long.TryParse(id.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) result.Add(value);
            }

            return result;
        }

        /// <summary>Есть ли в документе ссылки на объекты этой же сцены: из вырезанного документа они не восстановятся.</summary>
        public static bool HasLocalReferences(UnityDocument doc)
        {
            if (doc == null) return false;

            foreach (var pair in doc.Props)
            {
                var map = UnityYamlParser.ParseFlowMap(pair.Value);
                string id;
                if (map == null || map.ContainsKey("guid") || !map.TryGetValue("fileID", out id)) continue;
                if (id.Trim() != "0") return true;
            }

            return false;
        }
    }
}
