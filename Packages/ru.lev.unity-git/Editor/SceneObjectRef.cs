using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lev.Git
{
    /// <summary>Адрес объекта сцены: файл плюс fileID внутри него.</summary>
    public struct SceneObjectAddress
    {
        public string ScenePath;      // Assets/Scenes/Main.unity
        public long FileId;           // fileID объекта или компонента в этом файле
        public ulong PrefabId;        // не 0 — объект внутри экземпляра префаба
        public string Name;

        public bool IsValid { get { return !string.IsNullOrEmpty(ScenePath) && FileId != 0; } }
        public bool InsidePrefab { get { return PrefabId != 0; } }
    }

    /// <summary>
    /// Перевод между живым объектом в редакторе и записью в файле сцены.
    ///
    /// Ключ — fileID, а не путь в иерархии. Путь меняется при переименовании и
    /// переносе, и двух объектов с одинаковыми именами рядом достаточно, чтобы
    /// поиск по пути привёл не туда. fileID Unity держит неизменным — ровно для
    /// этого он и существует.
    ///
    /// Адрес берётся из GlobalObjectId: там тот же идентификатор, который
    /// сериализатор пишет в YAML, и добывать его разбором сцены не нужно.
    /// </summary>
    public static class SceneObjectRef
    {
        /// <summary>Тип адреса «объект сцены» в строковом виде GlobalObjectId.</summary>
        private const int SceneObjectType = 2;

        public static SceneObjectAddress Of(UnityEngine.Object obj)
        {
            var address = new SceneObjectAddress();
            if (obj == null) return address;

            var scene = SceneOf(obj);
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) return address;

            var id = GlobalObjectId.GetGlobalObjectIdSlow(obj);

            address.ScenePath = scene.path;
            address.FileId = unchecked((long)id.targetObjectId);
            address.PrefabId = id.targetPrefabId;
            address.Name = obj.name;
            return address;
        }

        private static Scene SceneOf(UnityEngine.Object obj)
        {
            var go = obj as GameObject;
            if (go != null) return go.scene;

            var component = obj as Component;
            if (component != null) return component.gameObject.scene;

            return default(Scene);
        }

        /// <summary>
        /// Обратный поиск: объект по адресу. Работает только для открытых сцен —
        /// в закрытой искать нечего, там объектов ещё не существует.
        /// </summary>
        public static UnityEngine.Object Find(SceneObjectAddress address)
        {
            if (!address.IsValid) return null;

            var guid = AssetDatabase.AssetPathToGUID(address.ScenePath);
            if (string.IsNullOrEmpty(guid)) return null;

            // Префаб — не сцена: его объекты живут в самом файле ассета, и открывать
            // его для этого не нужно. Тип адреса у них «исходный ассет» (3), у
            // некоторых версий — «импортированный» (1).
            if (address.ScenePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var assetType in new[] { 3, 1 })
                {
                    var assetText = string.Format("GlobalObjectId_V1-{0}-{1}-{2}-{3}",
                        assetType, guid, unchecked((ulong)address.FileId), address.PrefabId);
                    if (GlobalObjectId.TryParse(assetText, out var assetId))
                    {
                        var inAsset = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(assetId);
                        if (inAsset != null) return inAsset;
                    }
                }
                return null;
            }

            // Строка GlobalObjectId собирается из тех же частей, на которые он
            // раскладывается: тип, GUID файла, fileID, идентификатор префаба.
            var text = string.Format("GlobalObjectId_V1-{0}-{1}-{2}-{3}",
                SceneObjectType, guid, unchecked((ulong)address.FileId), address.PrefabId);

            GlobalObjectId id;
            if (!GlobalObjectId.TryParse(text, out id)) return null;

            // Нужны только объекты иерархии. На fileID документа PrefabInstance Unity
            // отдаёт служебную запись экземпляра — её принимать нельзя, иначе переход
            // «находил» не объект, и выделять было нечего.
            var found = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id);
            if (found is GameObject || found is Component) return found;

            if (address.PrefabId == 0)
            {
                var instanceRoot = FindInstanceRoot(address.ScenePath, address.FileId);
                if (instanceRoot != null) return instanceRoot;
            }
            if (found != null) return found;

            // Экземпляр префаба в файле сцены — документ PrefabInstance, а не
            // игровой объект: его fileID не адресует ни один живой объект.
            // Живой корень экземпляра узнаётся по targetPrefabId.
            return address.PrefabId == 0 ? FindInstanceRoot(address.ScenePath, address.FileId) : null;
        }

        /// <summary>
        /// Корень экземпляра префаба на открытой сцене.
        ///
        /// Не через <see cref="Find"/>: GlobalObjectId на fileID документа
        /// PrefabInstance отдаёт не игровой объект, а служебную запись экземпляра —
        /// поиск считал её найденной, и до корня дело не доходило. Корень ищется по
        /// идентификатору экземпляра, а если так не нашёлся — по исходному префабу
        /// и имени.
        /// </summary>
        public static GameObject FindInstance(string scenePath, long instanceFileId, string sourceGuid, string name)
        {
            var root = FindInstanceRoot(scenePath, instanceFileId);
            if (root != null) return root;

            var scene = SceneManager.GetSceneByPath(scenePath);
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(sourceGuid)) return null;

            var candidates = new List<GameObject>();
            foreach (var top in scene.GetRootGameObjects())
                foreach (var t in top.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;
                    if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go)) continue;
                    var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                    if (AssetDatabase.AssetPathToGUID(assetPath) == sourceGuid) candidates.Add(go);
                }

            if (candidates.Count == 1) return candidates[0];
            foreach (var c in candidates) if (c.name == name) return c;
            return null;
        }

        /// <summary>
        /// Корень экземпляра префаба по fileID его документа PrefabInstance.
        /// Идентификаторы корней запрашиваются одним пакетным вызовом.
        /// </summary>
        public static GameObject FindInstanceRoot(string scenePath, long instanceFileId)
        {
            var scene = SceneManager.GetSceneByPath(scenePath);
            if (!scene.IsValid() || !scene.isLoaded) return null;

            var roots = new List<GameObject>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject)) roots.Add(t.gameObject);

            if (roots.Count == 0) return null;

            var objects = new UnityEngine.Object[roots.Count];
            for (int i = 0; i < roots.Count; i++) objects[i] = roots[i];

            var ids = new GlobalObjectId[objects.Length];
            GlobalObjectId.GetGlobalObjectIdsSlow(objects, ids);

            for (int i = 0; i < ids.Length; i++)
                if (unchecked((long)ids[i].targetPrefabId) == instanceFileId) return roots[i];

            return null;
        }

        /// <summary>
        /// Переходит к объекту так, как это сделал бы человек: выделяет его и
        /// подсвечивает в иерархии, а для компонента ещё и раскрывает в
        /// инспекторе только его, свернув остальные. Прямого «показать этот
        /// компонент» у инспектора нет, поэтому так.
        /// </summary>
        public static bool Focus(SceneObjectAddress address)
        {
            return FocusObject(Find(address));
        }

        /// <summary>
        /// Объект экземпляра префаба, чей исходный объект в префабе — цель
        /// переопределения «guid|fileId». Так находится конкретный объект или
        /// компонент, у которого поменяли переопределённое свойство.
        /// </summary>
        public static UnityEngine.Object InstanceObject(GameObject root, string targetKey)
        {
            if (root == null || string.IsNullOrEmpty(targetKey)) return null;

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (IsSourceOf(t.gameObject, targetKey)) return t.gameObject;
                foreach (var c in t.GetComponents<Component>())
                    if (c != null && IsSourceOf(c, targetKey)) return c;
            }
            return null;
        }

        private static bool IsSourceOf(UnityEngine.Object o, string targetKey)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(o);
            return source != null &&
                   AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long id) &&
                   guid + "|" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) == targetKey;
        }

        /// <summary>Выделить объект или компонент, как <see cref="Focus"/>, но уже найденный.</summary>
        public static bool FocusObject(UnityEngine.Object target)
        {
            if (target == null) return false;

            var component = target as UnityEngine.Component;
            var go = component != null ? component.gameObject : target as UnityEngine.GameObject;
            if (go == null) return false;

            UnityEditor.Selection.activeGameObject = go;
            UnityEditor.EditorGUIUtility.PingObject(go);

            if (component != null)
            {
                foreach (var c in go.GetComponents<UnityEngine.Component>())
                {
                    if (c == null) continue;
                    UnityEditorInternal.InternalEditorUtility.SetIsInspectorExpanded(
                        c, ReferenceEquals(c, component));
                }

                // Без пересборки уже открытый инспектор настройку раскрытия не
                // перечитает — компонент останется свёрнутым.
                UnityEditor.ActiveEditorTracker.sharedTracker.ForceRebuild();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }

            return true;
        }

        /// <summary>Путь сцены, которой принадлежит объект. Пусто — объект не из сцены.</summary>
        public static string ScenePathOf(UnityEngine.Object obj)
        {
            var scene = SceneOf(obj);
            return scene.IsValid() ? scene.path : null;
        }

        /// <summary>
        /// Номер компонента среди однотипных на объекте. История сравнивает
        /// компоненты по fileID, но для заголовка окна нужен тот же номер,
        /// каким их различает семантический diff.
        /// </summary>
        public static int TypeIndexOf(Component component)
        {
            if (component == null) return 0;

            var siblings = component.gameObject.GetComponents<Component>();
            int index = 0;

            foreach (var c in siblings)
            {
                if (ReferenceEquals(c, component)) break;
                if (c != null && c.GetType() == component.GetType()) index++;
            }

            return index;
        }
    }
}
