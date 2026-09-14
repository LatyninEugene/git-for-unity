using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>
    /// Иконки и типы компонентов по имени из YAML.
    ///
    /// В файле сцены у компонента есть только имя типа — «Rigidbody»,
    /// «BoxCollider». Чтобы нарисовать привычную иконку и собрать инспектор
    /// прошлого состояния, нужен настоящий System.Type, а его приходится искать
    /// среди загруженных сборок. Поиск не бесплатный, поэтому результат
    /// запоминается — в том числе отрицательный.
    /// </summary>
    internal static class SceneHistoryIcons
    {
        // ------------------------------------------------- настройки сцены ---
        // Общие для истории сцены и дерева объектов в diff: служебные документы
        // сцены должны выглядеть одинаково везде и не походить на объекты иерархии.

        /// <summary>Значок служебных данных сцены — RenderSettings, SceneRoots и прочих.</summary>
        public static Texture SettingsIcon
        {
            get
            {
                // FindTexture, а не IconContent: тот пишет в консоль, если значка
                // нет, а у тёмного и светлого скина имена разные.
                var icon = EditorGUIUtility.FindTexture(EditorGUIUtility.isProSkin ? "d_Settings" : "Settings")
                           ?? EditorGUIUtility.FindTexture("Settings");
                return icon != null ? icon : GameObjectIcon;
            }
        }

        /// <summary>Что это за данные — словами, а не именем класса из YAML.</summary>
        public static string SettingsTooltip(string type)
        {
            switch (type)
            {
                case "RenderSettings":
                    return L.T("Scene environment: skybox, ambient lighting, fog. Edited in the Lighting window.");
                case "LightmapSettings":
                    return L.T("Light baking parameters. Edited in the Lighting window.");
                case "OcclusionCullingSettings":
                    return L.T("Culling data for hidden objects. Edited in the Occlusion Culling window.");
                case "NavMeshSettings":
                    return L.T("Legacy navigation settings built into the scene.");
                case "SceneRoots":
                    return L.T("Order of root objects in the hierarchy. Changes when root objects are added, " +
                               "removed or reordered.");
                default:
                    return L.T("Scene service data, not a hierarchy object.");
            }
        }

        /// <summary>Пункт меню окна редактора, в котором правятся эти настройки, или null.</summary>
        public static string SettingsWindow(string type)
        {
            switch (type)
            {
                case "RenderSettings":
                case "LightmapSettings": return "Window/Rendering/Lighting";
                case "OcclusionCullingSettings": return "Window/Rendering/Occlusion Culling";
                default: return null;
            }
        }

        // ------------------------------------------------------ типы и значки ---

        private static readonly Dictionary<string, Type> _types =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        private static readonly Dictionary<string, Texture> _icons =
            new Dictionary<string, Texture>(StringComparer.Ordinal);

        /// <summary>
        /// Тип компонента по имени из YAML. null — не нашли: так бывает у
        /// MonoBehaviour, где настоящий класс прячется за GUID скрипта.
        /// </summary>
        public static Type TypeOf(string yamlType)
        {
            if (string.IsNullOrEmpty(yamlType)) return null;

            Type cached;
            if (_types.TryGetValue(yamlType, out cached)) return cached;

            Type found = null;

            // Сначала обычные места: движок и редактор. Это закрывает почти всё,
            // что встречается в сценах, без обхода всех сборок домена.
            foreach (var qualified in new[]
                     {
                         "UnityEngine." + yamlType + ", UnityEngine.CoreModule",
                         "UnityEngine." + yamlType + ", UnityEngine.PhysicsModule",
                         "UnityEngine." + yamlType + ", UnityEngine.AudioModule",
                         "UnityEngine.UI." + yamlType + ", UnityEngine.UI"
                     })
            {
                found = Type.GetType(qualified, false);
                if (found != null) break;
            }

            if (found == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch { continue; }   // сборка с неразрешёнными ссылками — пропускаем

                    foreach (var t in types)
                    {
                        if (t.Name != yamlType) continue;
                        if (!typeof(Component).IsAssignableFrom(t)) continue;
                        found = t;
                        break;
                    }

                    if (found != null) break;
                }
            }

            // Базовые и абстрактные классы на временный объект не добавить: Unity
            // отказывается и пишет ошибку в консоль сама, без исключения. Для
            // «MonoBehaviour» имя находит именно базовый класс — настоящий скрипт
            // прячется за GUID, см. ResolveComponent.
            if (found != null && !CanHost(found)) found = null;

            _types[yamlType] = found;
            return found;
        }

        /// <summary>
        /// Тип, который можно повесить на временный объект: настоящий, не
        /// абстрактный и не обобщённый наследник Component.
        /// </summary>
        public static bool CanHost(Type type)
        {
            return type != null &&
                   typeof(Component).IsAssignableFrom(type) &&
                   !type.IsAbstract &&
                   !type.IsGenericTypeDefinition &&
                   type != typeof(MonoBehaviour) &&
                   type != typeof(Behaviour) &&
                   type != typeof(Component);
        }

        /// <summary>
        /// Настоящий тип компонента. У скрипта в YAML записано лишь
        /// «MonoBehaviour», а класс определяется ссылкой m_Script на файл .cs —
        /// по ней и находим его, как это делает сам Unity.
        /// </summary>
        public static Type ResolveComponent(string yamlType, UnityDocument doc)
        {
            if (yamlType != "MonoBehaviour") return TypeOf(yamlType);

            var script = ScriptOf(doc);
            var type = script != null ? script.GetClass() : null;
            return CanHost(type) ? type : null;
        }

        /// <summary>Имя для показа: у скрипта — его класс, у остальных — тип из YAML.</summary>
        public static string DisplayName(string yamlType, UnityDocument doc)
        {
            if (yamlType != "MonoBehaviour") return yamlType ?? L.T("Component");

            var script = ScriptOf(doc);
            if (script == null) return L.T("Script (file not found)");

            var type = script.GetClass();
            return type != null ? ObjectNames.NicifyVariableName(type.Name) : script.name;
        }

        /// <summary>Иконка компонента с учётом скриптов: у них своя, из файла .cs.</summary>
        public static Texture ComponentIcon(string yamlType, UnityDocument doc)
        {
            if (yamlType != "MonoBehaviour") return ComponentIcon(yamlType);

            var script = ScriptOf(doc);
            if (script == null) return ScriptIcon;

            var type = script.GetClass();
            var content = type != null
                ? EditorGUIUtility.ObjectContent(null, type)
                : EditorGUIUtility.ObjectContent(script, typeof(MonoScript));

            return content != null && content.image != null ? content.image : ScriptIcon;
        }

        private static readonly Dictionary<string, MonoScript> _scripts =
            new Dictionary<string, MonoScript>(StringComparer.Ordinal);

        /// <summary>
        /// Файл скрипта по ссылке m_Script. Разборщик хранит ссылку одной
        /// строкой — «{fileID: 11500000, guid: …, type: 3}», — и GUID достаётся
        /// из неё.
        /// </summary>
        private static MonoScript ScriptOf(UnityDocument doc)
        {
            if (doc == null) return null;

            var raw = doc.Get("m_Script");
            if (string.IsNullOrEmpty(raw)) return null;

            var m = System.Text.RegularExpressions.Regex.Match(raw, @"guid:\s*([0-9a-fA-F]{32})");
            if (!m.Success) return null;

            var guid = m.Groups[1].Value;

            MonoScript cached;
            if (_scripts.TryGetValue(guid, out cached)) return cached;

            var path = AssetDatabase.GUIDToAssetPath(guid);
            var script = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<MonoScript>(path);

            _scripts[guid] = script;
            return script;
        }

        /// <summary>Иконка компонента — та же, что рисует инспектор.</summary>
        public static Texture ComponentIcon(string yamlType)
        {
            if (string.IsNullOrEmpty(yamlType)) return ScriptIcon;

            Texture cached;
            if (_icons.TryGetValue(yamlType, out cached)) return cached;

            Texture icon = null;
            var type = TypeOf(yamlType);

            if (type != null)
            {
                var content = EditorGUIUtility.ObjectContent(null, type);
                if (content != null) icon = content.image;
            }

            if (icon == null) icon = ScriptIcon;

            _icons[yamlType] = icon;
            return icon;
        }

        public static Texture GameObjectIcon
        {
            get { return EditorGUIUtility.IconContent("GameObject Icon").image; }
        }

        public static Texture PrefabIcon
        {
            get { return EditorGUIUtility.IconContent("Prefab Icon").image; }
        }

        private static Texture ScriptIcon
        {
            get { return EditorGUIUtility.IconContent("cs Script Icon").image; }
        }
    }
}
