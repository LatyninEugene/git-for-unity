using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Переносит значения из разобранного YAML в живой объект через сериализацию.
    ///
    /// Работает потому, что имена свойств в YAML и имена сериализованных полей —
    /// одно и то же: m_LocalPosition в файле сцены и SerializedProperty
    /// «m_LocalPosition» это буквально одно поле. Разъезжается только запись
    /// массивов, её и правим.
    ///
    /// Нужен в двух местах сразу: показать компонент таким, каким он был в
    /// коммите, и вернуть в него старые значения.
    /// </summary>
    public static class UnityPropertyApplier
    {
        public sealed class Result
        {
            public int Applied;
            public int Skipped;

            public override string ToString()
            {
                return Skipped == 0
                    ? L.F("properties transferred: {0}", Applied)
                    : L.F("transferred {0}, skipped {1}", Applied, Skipped);
            }
        }

        /// <summary>
        /// Возвращает живому компоненту значения из сохранённого документа.
        /// Это обычная правка сцены: с записью в историю отмены и пометкой
        /// сцены изменённой. Структурные поля (скрипт, родитель, состав
        /// компонентов) не трогаются — см. Structural.
        /// </summary>
        public static Result RestoreLive(UnityEngine.Object live, UnityDocument source, string undoName)
        {
            if (live == null || source == null) return new Result();

            UnityEditor.Undo.RecordObject(live, undoName);

            var so = new SerializedObject(live);
            var result = Apply(so, source);
            so.ApplyModifiedProperties();

            UnityEditor.EditorUtility.SetDirty(live);
            return result;
        }

        /// <summary>
        /// Одно свойство живого объекта — к значению из YAML. false — свойство
        /// не найдено, структурное или его тип так не переносится.
        /// </summary>
        public static bool RestoreProperty(UnityEngine.Object live, string yamlPath, string value, string undoName)
        {
            if (live == null || value == null || string.IsNullOrEmpty(yamlPath) || IsStructural(yamlPath)) return false;

            var so = new SerializedObject(live);
            var property = so.FindProperty(ToSerializedPath(yamlPath));
            if (property == null) return false;

            UnityEditor.Undo.RecordObject(live, undoName);
            if (!ApplyOne(property, value)) return false;

            so.ApplyModifiedProperties();
            UnityEditor.EditorUtility.SetDirty(live);
            return true;
        }

        /// <summary>Значение из YAML — в найденное сериализованное свойство. Для переопределений префаба.</summary>
        public static bool ApplyValue(SerializedProperty property, string value)
        {
            return property != null && ApplyOne(property, value);
        }

        /// <summary>
        /// Поля, которые задают, чем объект является и где он находится.
        /// Переносить их нельзя: восстановление m_Father перевесило бы объект
        /// в иерархии, а m_Script превратил бы компонент в другой скрипт.
        /// </summary>
        private static readonly HashSet<string> Structural = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_GameObject", "m_Script", "m_ObjectHideFlags",
            "m_PrefabInstance", "m_PrefabAsset", "m_CorrespondingSourceObject",
            "m_Father", "m_Children", "m_Component", "serializedVersion"
        };

        private static readonly Regex ArrayIndex = new Regex(@"\[(\d+)\]");

        public static Result Apply(SerializedObject target, UnityDocument source)
        {
            var result = new Result();
            if (target == null || source == null) return result;

            foreach (var pair in source.Props)
            {
                if (IsStructural(pair.Key)) continue;

                var property = target.FindProperty(ToSerializedPath(pair.Key));
                if (property == null) { result.Skipped++; continue; }

                if (ApplyOne(property, pair.Value)) result.Applied++;
                else result.Skipped++;
            }

            return result;
        }

        internal static bool IsStructural(string key)
        {
            int dot = key.IndexOf('.');
            int bracket = key.IndexOf('[');
            int cut = dot < 0 ? bracket : (bracket < 0 ? dot : Math.Min(dot, bracket));
            var head = cut < 0 ? key : key.Substring(0, cut);
            return Structural.Contains(head);
        }

        /// <summary>
        /// m_Component[0].component → m_Component.Array.data[0].component.
        /// Это единственное место, где имена в YAML и в сериализации расходятся.
        /// </summary>
        private static string ToSerializedPath(string yamlPath)
        {
            return ArrayIndex.Replace(yamlPath, ".Array.data[$1]");
        }

        private static bool ApplyOne(SerializedProperty p, string value)
        {
            if (value == null) return false;

            try
            {
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.LayerMask:
                    case SerializedPropertyType.Enum:
                    case SerializedPropertyType.ArraySize:
                    {
                        long n;
                        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                            return false;
                        p.intValue = (int)n;
                        return true;
                    }

                    case SerializedPropertyType.Boolean:
                        p.boolValue = value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
                        return true;

                    case SerializedPropertyType.Float:
                    {
                        float f;
                        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                            return false;
                        p.floatValue = f;
                        return true;
                    }

                    case SerializedPropertyType.String:
                        p.stringValue = Unquote(value);
                        return true;

                    case SerializedPropertyType.Vector2:
                    {
                        var m = Map(value); if (m == null) return false;
                        p.vector2Value = new Vector2(F(m, "x"), F(m, "y"));
                        return true;
                    }

                    case SerializedPropertyType.Vector3:
                    {
                        var m = Map(value); if (m == null) return false;
                        p.vector3Value = new Vector3(F(m, "x"), F(m, "y"), F(m, "z"));
                        return true;
                    }

                    case SerializedPropertyType.Vector4:
                    {
                        var m = Map(value); if (m == null) return false;
                        p.vector4Value = new Vector4(F(m, "x"), F(m, "y"), F(m, "z"), F(m, "w"));
                        return true;
                    }

                    case SerializedPropertyType.Quaternion:
                    {
                        var m = Map(value); if (m == null) return false;
                        p.quaternionValue = new Quaternion(F(m, "x"), F(m, "y"), F(m, "z"), F(m, "w"));
                        return true;
                    }

                    case SerializedPropertyType.Color:
                    {
                        var m = Map(value); if (m == null) return false;
                        p.colorValue = new Color(F(m, "r"), F(m, "g"), F(m, "b"), F(m, "a", 1f));
                        return true;
                    }

                    case SerializedPropertyType.Rect:
                    {
                        var m = Map(value); if (m == null) return false;
                        p.rectValue = new Rect(F(m, "x"), F(m, "y"), F(m, "width"), F(m, "height"));
                        return true;
                    }

                    case SerializedPropertyType.ObjectReference:
                    {
                        var map = Map(value);
                        if (map == null) return false;

                        // Ссылка внутри той же сцены не разрешается: у нас есть
                        // только её числовой идентификатор из файла, а объект в
                        // памяти живёт своей жизнью.
                        if (!map.ContainsKey("guid"))
                        {
                            string fid;
                            if (map.TryGetValue("fileID", out fid) && fid == "0")
                            {
                                p.objectReferenceValue = null;
                                return true;
                            }
                            return false;
                        }

                        var resolved = ResolveAsset(map);
                        if (resolved == null) return false;
                        p.objectReferenceValue = resolved;
                        return true;
                    }

                    default:
                        // Кривые, градиенты, вложенные структуры и всё, чего мы
                        // не понимаем: молча считаем пропущенным и сообщаем числом.
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static UnityEngine.Object ResolveAsset(Dictionary<string, string> map)
        {
            string guid;
            if (!map.TryGetValue("guid", out guid) || string.IsNullOrEmpty(guid)) return null;

            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return null;

            long localId = 0;
            string fid;
            if (map.TryGetValue("fileID", out fid))
                long.TryParse(fid, NumberStyles.Integer, CultureInfo.InvariantCulture, out localId);

            // В одном файле бывает несколько объектов — берём тот, чей локальный
            // идентификатор совпал, и только иначе главный ассет файла.
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (asset == null) continue;

                string g;
                long id;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out g, out id) && id == localId)
                    return asset;
            }

            return AssetDatabase.LoadMainAssetAtPath(path);
        }

        private static Dictionary<string, string> Map(string value)
        {
            return UnityYamlParser.ParseFlowMap(value);
        }

        private static float F(Dictionary<string, string> map, string key, float fallback = 0f)
        {
            string raw;
            float f;
            if (map.TryGetValue(key, out raw) &&
                float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                return f;
            return fallback;
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && (s[0] == '\'' || s[0] == '"') && s[s.Length - 1] == s[0])
                return s.Substring(1, s.Length - 2);
            return s;
        }
    }
}
