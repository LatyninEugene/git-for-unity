using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>Компонент префаба, у которого в экземпляре менялись переопределения.</summary>
    internal sealed class InstanceComponentPart
    {
        public string Key;
        public Component Asset;
        public readonly List<PrefabModificationChange> Changes = new List<PrefabModificationChange>();

        public string Title => Asset != null ? ObjectNames.NicifyVariableName(Asset.GetType().Name) : L.T("Component");
    }

    /// <summary>Объект префаба внутри экземпляра: его собственные правки и правки его компонентов.</summary>
    internal sealed class InstancePart
    {
        public string Key;
        public string Title;
        public GameObject Asset;
        public readonly List<string> AllKeys = new List<string>();
        public readonly List<PrefabModificationChange> Own = new List<PrefabModificationChange>();
        public readonly List<InstanceComponentPart> Components = new List<InstanceComponentPart>();

        public List<PrefabModificationChange> AllChanges
        {
            get
            {
                var all = new List<PrefabModificationChange>(Own);
                foreach (var c in Components) all.AddRange(c.Changes);
                return all;
            }
        }
    }

    /// <summary>Строка дерева экземпляра: объект префаба на своём уровне иерархии.</summary>
    internal sealed class InstanceLayoutRow
    {
        /// <summary>null — объект сам не менялся, строка нужна ради его изменившихся детей.</summary>
        public InstancePart Part;

        /// <summary>«guid|fileId» объекта в файле префаба.</summary>
        public string Key;
        public string Name;

        /// <summary>0 — корень экземпляра: его правки идут прямо под строкой экземпляра.</summary>
        public int Level;
    }

    /// <summary>
    /// Экземпляр префаба — объектами и компонентами префаба, а не документом
    /// PrefabInstance с номерами переопределений. Цель каждого переопределения —
    /// ссылка на объект в файле префаба: по ней находятся имя, путь и тип.
    /// Общий разбор для diff и для окна истории сцены.
    /// </summary>
    internal static class PrefabInstanceParts
    {
        public static List<InstancePart> Build(UnityDocument oldDoc, UnityDocument newDoc)
        {
            var parts = new List<InstancePart>();
            var byKey = new Dictionary<string, InstancePart>(StringComparer.Ordinal);
            var rootName = InstanceName(newDoc ?? oldDoc);

            foreach (var change in PrefabOverrides.Changes(oldDoc, newDoc))
            {
                var asset = PrefabTargets.Resolve(change.TargetGuid, change.TargetFileId);
                var component = asset as Component;
                var go = component != null ? component.gameObject : asset as GameObject;
                var objectKey = (go != null ? PrefabTargets.KeyOf(go) : null) ?? change.Target;

                // Где переопределения нет, действует значение префаба — его и покажем.
                if (change.Before == null || change.After == null)
                    change.PrefabValue = ReadValue(asset, change.PropertyPath);

                if (!byKey.TryGetValue(objectKey, out var part))
                {
                    part = new InstancePart
                    {
                        Key = objectKey,
                        Asset = go,
                        Title = go != null ? PrefabTargets.PathInPrefab(go, rootName) : L.F("prefab object · fileID {0}", change.TargetFileId)
                    };
                    byKey[objectKey] = part;
                    parts.Add(part);
                }

                if (!part.AllKeys.Contains(change.Target)) part.AllKeys.Add(change.Target);

                if (component == null)
                {
                    part.Own.Add(change);
                    continue;
                }

                var entry = part.Components.Find(c => c.Key == change.Target);
                if (entry == null)
                {
                    entry = new InstanceComponentPart { Key = change.Target, Asset = component };
                    part.Components.Add(entry);
                }
                entry.Changes.Add(change);
            }

            return parts;
        }

        private sealed class LayoutNode
        {
            public string Key, Name;
            public int Order;
            public InstancePart Part;
            public readonly List<LayoutNode> Children = new List<LayoutNode>();
        }

        /// <summary>
        /// Объекты экземпляра так, как их показывает иерархия: корень, под ним
        /// дети по порядку. Корень называется как экземпляр на сцене; объекты,
        /// которые сами не менялись, попадают строкой без правок — иначе
        /// изменившийся ребёнок висел бы без родителя.
        /// </summary>
        public static List<InstanceLayoutRow> Layout(List<InstancePart> parts, string rootName)
        {
            var nodes = new Dictionary<string, LayoutNode>(StringComparer.Ordinal);
            var roots = new List<LayoutNode>();
            var loose = new List<InstancePart>();

            foreach (var part in parts)
            {
                if (part.Asset == null) { loose.Add(part); continue; }

                LayoutNode child = null;
                for (var t = part.Asset.transform; t != null; t = t.parent)
                {
                    var key = PrefabTargets.KeyOf(t.gameObject) ?? "id" + t.GetInstanceID();
                    bool existed = nodes.TryGetValue(key, out var node);
                    if (!existed)
                    {
                        node = new LayoutNode
                        {
                            Key = key,
                            Name = t.parent == null && !string.IsNullOrEmpty(rootName) ? rootName : t.name,
                            Order = t.GetSiblingIndex()
                        };
                        nodes[key] = node;
                        if (t.parent == null) roots.Add(node);
                    }

                    if (t == part.Asset.transform) node.Part = part;
                    if (child != null && !node.Children.Contains(child)) node.Children.Add(child);
                    if (existed) break;
                    child = node;
                }
            }

            var rows = new List<InstanceLayoutRow>();
            roots.Sort((a, b) => a.Order.CompareTo(b.Order));
            foreach (var root in roots) Emit(root, 0, rows);
            foreach (var part in loose) rows.Add(new InstanceLayoutRow { Part = part, Key = part.Key, Name = part.Title, Level = 1 });
            return rows;
        }

        private static void Emit(LayoutNode node, int level, List<InstanceLayoutRow> rows)
        {
            rows.Add(new InstanceLayoutRow { Part = node.Part, Key = node.Key, Name = node.Name, Level = level });
            node.Children.Sort((a, b) => a.Order.CompareTo(b.Order));
            foreach (var child in node.Children) Emit(child, level + 1, rows);
        }

        // ------------------------------------------------------- префаб-источник ---

        /// <summary>Выделить файл префаба в окне Project.</summary>
        public static void PingPrefab(string guid)
        {
            var asset = string.IsNullOrEmpty(guid) ? null : AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));
            if (asset == null) return;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        /// <summary>
        /// Открыть префаб на редактирование и выделить в нём тот же объект: в режиме
        /// префаба объекты — копии из файла, и находятся они по пути от корня.
        /// </summary>
        public static bool OpenInPrefab(string guid, UnityEngine.Object target)
        {
            var path = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return false;

            var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(path);
            if (stage == null || stage.prefabContentsRoot == null) return false;

            var component = target as Component;
            var go = component != null ? component.gameObject : target as GameObject;
            var root = stage.prefabContentsRoot.transform;

            Transform found = root;
            if (go != null)
            {
                var relative = RelativePath(go);
                if (!string.IsNullOrEmpty(relative)) found = root.Find(relative) ?? root;
            }

            // У компонента — выделить объект и раскрыть в инспекторе именно его.
            UnityEngine.Object focus = found.gameObject;
            if (component != null)
            {
                var sameType = found.GetComponent(component.GetType());
                if (sameType != null) focus = sameType;
            }

            return SceneObjectRef.FocusObject(focus);
        }

        private static string RelativePath(GameObject go)
        {
            var names = new List<string>();
            for (var t = go.transform; t != null && t.parent != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        /// <summary>Имя экземпляра: как его назвали на сцене, иначе — имя файла префаба.</summary>
        public static string InstanceName(UnityDocument doc)
        {
            var name = PrefabOverrides.NameOf(doc);
            if (!string.IsNullOrEmpty(name)) return name;

            var guid = PrefabOverrides.SourceGuid(doc);
            var path = guid != null ? AssetDatabase.GUIDToAssetPath(guid) : null;
            return !string.IsNullOrEmpty(path) ? System.IO.Path.GetFileNameWithoutExtension(path) : L.T("Prefab Instance");
        }

        /// <summary>Суть правок одной строкой: «m_LocalPosition.x: 6.652 → 6.5  +2».</summary>
        public static string Summary(List<PrefabModificationChange> changes)
        {
            if (changes == null || changes.Count == 0) return string.Empty;
            var first = changes[0];
            var text = first.PropertyPath + ": " + Short(first.OldText) + " → " + Short(first.NewText);
            return changes.Count > 1 ? text + "  +" + (changes.Count - 1) : text;
        }

        /// <summary>Все переопределения одной цели в состоянии документа — чтобы собрать компонент целиком.</summary>
        public static List<PrefabModification> EntriesFor(UnityDocument doc, string targetKey)
        {
            var list = new List<PrefabModification>();
            foreach (var m in PrefabOverrides.Entries(doc).Values)
                if (m.Target == targetKey) list.Add(m);
            return list;
        }

        public static Texture ComponentIcon(UnityEngine.Object asset)
        {
            if (asset == null) return EditorGUIUtility.IconContent("cs Script Icon").image;
            var content = EditorGUIUtility.ObjectContent(asset, asset.GetType());
            return content != null && content.image != null ? content.image : EditorGUIUtility.IconContent("cs Script Icon").image;
        }

        /// <summary>
        /// Значение свойства объекта в файле префаба — в том же виде, в каком оно
        /// записано в YAML переопределения: флаг 0/1, число, имя объекта по ссылке.
        /// null — свойство не найдено или его тип так не показать.
        /// </summary>
        private static string ReadValue(UnityEngine.Object asset, string propertyPath)
        {
            if (asset == null || string.IsNullOrEmpty(propertyPath)) return null;

            try
            {
                using (var so = new SerializedObject(asset))
                {
                    var p = so.FindProperty(propertyPath);
                    if (p == null) return null;

                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    switch (p.propertyType)
                    {
                        case SerializedPropertyType.Integer:
                        case SerializedPropertyType.LayerMask:
                        case SerializedPropertyType.ArraySize:
                            return p.longValue.ToString(inv);
                        case SerializedPropertyType.Enum:
                            return p.intValue.ToString(inv);
                        case SerializedPropertyType.Boolean:
                            return p.boolValue ? "1" : "0";
                        case SerializedPropertyType.Float:
                            return p.floatValue.ToString(inv);
                        case SerializedPropertyType.String:
                            return p.stringValue;
                        case SerializedPropertyType.ObjectReference:
                            return p.objectReferenceValue != null ? p.objectReferenceValue.name : "None";
                        case SerializedPropertyType.Color:
                            var c = p.colorValue;
                            return string.Format(inv, "{{r: {0}, g: {1}, b: {2}, a: {3}}}", c.r, c.g, c.b, c.a);
                        case SerializedPropertyType.Vector2:
                            return string.Format(inv, "{{x: {0}, y: {1}}}", p.vector2Value.x, p.vector2Value.y);
                        case SerializedPropertyType.Vector3:
                            return string.Format(inv, "{{x: {0}, y: {1}, z: {2}}}", p.vector3Value.x, p.vector3Value.y, p.vector3Value.z);
                        default:
                            return null;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static string Short(string value)
        {
            if (string.IsNullOrEmpty(value)) return "—";
            return value.Length <= 24 ? value : value.Substring(0, 24) + "…";
        }
    }
}
