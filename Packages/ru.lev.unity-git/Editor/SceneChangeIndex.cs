using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lev.Git
{
    /// <summary>Что изменилось у конкретного объекта открытой сцены.</summary>
    public sealed class SceneObjectChange
    {
        public SceneChangeKind Kind;
        public SceneNode Node;
        public string ScenePath;
        public int InstanceId;

        /// <summary>Короткая сводка для инспектора: «+ BoxCollider · ~ Transform».</summary>
        public string Summary;
    }

    /// <summary>
    /// Связывает разницу сцены с живыми объектами открытой сцены.
    ///
    /// Нужен затем, что изменения полезнее видеть там, где работают: в
    /// иерархии и в инспекторе, а не только в отдельном окне. Внешний git-клиент
    /// этого не сделает в принципе — у него нет доступа к открытой сцене.
    ///
    /// Сопоставление идёт по fileID через GlobalObjectId, а не по пути в
    /// иерархии. Путь не различает соседей с одинаковыми именами — два «Cube»
    /// под одним родителем получали бы отметки друг друга. fileID в файле сцены
    /// и в GlobalObjectId живого объекта — одно и то же число. Объектов, которые
    /// созданы после последнего сохранения, в файле нет, и сопоставлять их не с
    /// чем: индекс сравнивает сохранённый файл с HEAD.
    /// </summary>
    [InitializeOnLoad]
    public static class SceneChangeIndex
    {
        /// <summary>Не чаще этого — пересчёт читает файл сцены и разбирает YAML целиком.</summary>
        private const double MinIntervalSeconds = 2.0;

        private static Dictionary<int, SceneObjectChange> _byInstance =
            new Dictionary<int, SceneObjectChange>();

        /// <summary>
        /// Узлы разницы по живым компонентам. Считаются при пересчёте, а не при
        /// отрисовке: шапка компонента рисуется на каждый кадр, и спрашивать
        /// там идентификатор объекта у редактора было бы слишком дорого.
        /// </summary>
        private static Dictionary<int, SceneNode> _byComponent =
            new Dictionary<int, SceneNode>();

        private static bool _dirty = true;
        private static bool _running;
        private static double _lastRun;

        public static bool HasData { get; private set; }

        /// <summary>
        /// В открытой сцене есть несохранённые правки. Тогда индикаторы
        /// показывают разницу с ПОСЛЕДНИМ СОХРАНЕНИЕМ, а не с тем, что на экране.
        /// </summary>
        public static bool HasUnsavedScene { get; private set; }

        public static event Action Updated;

        static SceneChangeIndex()
        {
            GitStatusCache.Updated += MarkDirty;
            EditorSceneManager.sceneOpened += (s, m) => MarkDirty();
            EditorSceneManager.sceneSaved += s => MarkDirty();
            EditorApplication.update += Pump;
        }

        public static void MarkDirty()
        {
            _dirty = true;
        }

        public static SceneObjectChange For(int instanceId)
        {
            SceneObjectChange info;
            return _byInstance.TryGetValue(instanceId, out info) ? info : null;
        }

        /// <summary>
        /// Имя типа компонента так, как его пишет Unity в YAML.
        ///
        /// Для скриптов там всегда MonoBehaviour, а не имя класса: сам класс
        /// указан отдельной ссылкой m_Script.
        /// </summary>
        public static string YamlTypeName(Component c)
        {
            if (c == null) return null;
            if (c is MonoBehaviour) return "MonoBehaviour";
            return c.GetType().Name;
        }

        /// <summary>Узел разницы для конкретного живого компонента. null — не менялся.</summary>
        public static SceneNode FindComponentNode(Component c)
        {
            if (c == null) return null;

            SceneNode node;
            return _byComponent.TryGetValue(c.GetInstanceID(), out node) ? node : null;
        }

        // -------------------------------------------------------- пересчёт ---

        private static void Pump()
        {
            if (!_dirty || _running) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup - _lastRun < MinIntervalSeconds) return;
            if (GitCommandLog.Running > 0) return;
            if (!GitSettings.instance.sceneChangeIndicators) { Clear(); return; }
            if (!GitRepository.IsRepo || !GitStatusCache.HasData) return;

            _dirty = false;
            _lastRun = EditorApplication.timeSinceStartup;
            var _ = RebuildAsync();
        }

        private static void Clear()
        {
            if (_byInstance.Count == 0 && !HasData) return;

            _byInstance = new Dictionary<int, SceneObjectChange>();
            _byComponent = new Dictionary<int, SceneNode>();
            HasData = false;
            Raise();
        }

        private static async Task RebuildAsync()
        {
            _running = true;
            try
            {
                var objects = new Dictionary<int, SceneObjectChange>();
                var components = new Dictionary<int, SceneNode>();
                bool unsaved = false;

                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded || string.IsNullOrEmpty(scene.path)) continue;
                    if (scene.isDirty) unsaved = true;

                    // Неизменённую сцену разбирать незачем — это самая дорогая часть.
                    if (GitStatusCache.GetStatus(scene.path) == GitFileStatus.None) continue;

                    await IndexSceneAsync(scene, objects, components);
                }

                _byInstance = objects;
                _byComponent = components;
                HasUnsavedScene = unsaved;
                HasData = true;
                Raise();
            }
            catch (Exception e)
            {
                Diagnostics.Journal.Warn(L.F("Couldn't match scene changes: {0}", e.Message));
            }
            finally
            {
                _running = false;
            }
        }

        private static async Task IndexSceneAsync(Scene scene,
                                                  Dictionary<int, SceneObjectChange> objects,
                                                  Dictionary<int, SceneNode> components)
        {
            var gitPath = GitRepository.ToGitPath(scene.path);
            if (string.IsNullOrEmpty(gitPath)) return;

            var newText = GitOperations.ReadWorktreeText(gitPath);
            if (newText == null || !UnityYamlParser.LooksLikeUnityYaml(newText)) return;

            var oldText = await GitOperations.ShowHeadTextAsync(gitPath) ?? string.Empty;
            var roots = SceneDiffBuilder.Build(oldText, newText);
            if (roots.Count == 0) return;

            // Пока читали HEAD, сцену могли закрыть.
            if (!scene.IsValid() || !scene.isLoaded) return;

            var byId = new Dictionary<long, SceneNode>();
            foreach (var n in roots) byId[n.FileId] = n;

            var all = new List<GameObject>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    all.Add(t.gameObject);

            // Одним пакетным запросом на всю сцену: поштучный
            // GetGlobalObjectIdSlow на тысячах объектов заметно тормозит.
            var ids = IdsOf(all);

            for (int i = 0; i < all.Count; i++)
            {
                var go = all[i];
                long key;

                if (ids[i].targetPrefabId == 0)
                    key = unchecked((long)ids[i].targetObjectId);
                else if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
                    // Объектов экземпляра префаба в файле сцены нет, есть документ
                    // PrefabInstance. Отметку получает корень экземпляра — по нему.
                    key = unchecked((long)ids[i].targetPrefabId);
                else
                    continue;

                SceneNode node;
                if (key == 0 || !byId.TryGetValue(key, out node)) continue;

                bool prefab = PrefabOverrides.Is(node.NewDoc) || PrefabOverrides.Is(node.OldDoc);

                int instanceId = go.GetInstanceID();
                objects[instanceId] = new SceneObjectChange
                {
                    Kind = node.Kind,
                    Node = node,
                    ScenePath = scene.path,
                    InstanceId = instanceId,
                    Summary = prefab ? SummarizePrefab(node) : Summarize(node)
                };

                if (node.Children.Count > 0) IndexComponents(go, node, components);
            }
        }

        /// <summary>Сопоставляет изменившиеся компоненты объекта с живыми — тоже по fileID.</summary>
        private static void IndexComponents(GameObject go, SceneNode node, Dictionary<int, SceneNode> into)
        {
            var live = new List<Component>();

            // null — компонент со скриптом, которого больше нет в проекте.
            foreach (var c in go.GetComponents<Component>()) if (c != null) live.Add(c);
            if (live.Count == 0) return;

            var ids = IdsOf(live);

            for (int i = 0; i < live.Count; i++)
            {
                long id = unchecked((long)ids[i].targetObjectId);

                foreach (var child in node.Children)
                {
                    if (child.FileId != id) continue;
                    into[live[i].GetInstanceID()] = child;
                    break;
                }
            }
        }

        private static GlobalObjectId[] IdsOf<T>(List<T> items) where T : UnityEngine.Object
        {
            var objects = new UnityEngine.Object[items.Count];
            for (int i = 0; i < items.Count; i++) objects[i] = items[i];

            var ids = new GlobalObjectId[objects.Length];
            if (objects.Length > 0) GlobalObjectId.GetGlobalObjectIdsSlow(objects, ids);
            return ids;
        }

        /// <summary>
        /// Сводка для инспектора: перечисляет компоненты и первые свойства.
        /// Длинный список тут не нужен — за подробностями человек идёт в окно.
        /// </summary>
        private static string Summarize(SceneNode node)
        {
            var parts = new List<string>();

            foreach (var p in node.Props)
            {
                parts.Add(p.Path + ": " + Short(p.Old) + " → " + Short(p.New));
                if (parts.Count >= 3) break;
            }

            foreach (var c in node.Children)
            {
                var sign = c.Kind == SceneChangeKind.Added ? "+ "
                         : c.Kind == SceneChangeKind.Removed ? "− " : "~ ";

                var text = sign + c.Title;
                if (c.Kind == SceneChangeKind.Modified && c.Props.Count > 0)
                    text += " (" + c.Props[0].Path + ")";

                parts.Add(text);
                if (parts.Count >= 6) break;
            }

            return parts.Count == 0 ? L.Tc("scene object", "modified") : string.Join("\n", parts.ToArray());
        }

        /// <summary>
        /// Сводка для экземпляра префаба — по переопределениям, а не по сырым
        /// путям вида m_Modification.m_Modifications[3].value.
        /// </summary>
        private static string SummarizePrefab(SceneNode node)
        {
            if (node.Kind == SceneChangeKind.Added) return L.T("instance added");

            var parts = new List<string>();
            foreach (var p in PrefabOverrides.Diff(node.OldDoc, node.NewDoc))
            {
                parts.Add(p.Path + ": " + Short(p.Old) + " → " + Short(p.New));
                if (parts.Count >= 4) break;
            }

            return parts.Count == 0 ? L.Tc("scene object", "modified") : string.Join("\n", parts.ToArray());
        }

        private static string Short(string value)
        {
            if (string.IsNullOrEmpty(value)) return "—";
            return value.Length > 28 ? value.Substring(0, 28) + "…" : value;
        }

        private static void Raise()
        {
            var h = Updated;
            if (h != null) h();

            EditorApplication.RepaintHierarchyWindow();
        }
    }
}
