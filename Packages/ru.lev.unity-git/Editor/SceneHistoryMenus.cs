using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Lev.Git.UI;

namespace Lev.Git
{
    /// <summary>
    /// Где история объектов вызывается: ПКМ по сцене и по объекту в иерархии,
    /// меню ⋮ компонента в инспекторе, контекстное меню сцены и префаба в Project.
    ///
    /// Всё это — места, где человек уже смотрит на объект и задаётся вопросом
    /// «кто это трогал». Идти за ответом в отдельное окно и там что-то искать
    /// он не станет.
    /// </summary>
    internal static class SceneHistoryMenus
    {
        [InitializeOnLoadMethod]
        private static void Hook()
        {
            SceneHierarchyHooks.addItemsToSceneHeaderContextMenu += OnSceneHeader;
            SceneHierarchyHooks.addItemsToGameObjectContextMenu += OnGameObject;
        }

        // ------------------------------------------------------- иерархия ---

        private static void OnSceneHeader(GenericMenu menu, Scene scene)
        {
            if (!GitRepository.IsRepo) return;

            menu.AddSeparator(string.Empty);

            if (string.IsNullOrEmpty(scene.path))
            {
                menu.AddDisabledItem(new GUIContent("Git/" + L.T("Scene History")));
                return;
            }

            menu.AddItem(new GUIContent("Git/" + L.T("Scene History")), false,
                         () => SceneHistoryWindow.ShowScene(scene.path));

            AddLockItems(menu, scene.path);
        }

        /// <summary>
        /// Лок сцены прямо из иерархии: сцену правят, глядя на неё здесь, а не
        /// в окне Project. Пункты зависят от того, чей лок сейчас на сцене.
        /// </summary>
        private static void AddLockItems(GenericMenu menu, string scenePath)
        {
            menu.AddSeparator("Git/");

            var path = scenePath;
            var l = LfsLockCache.LockOf(path);

            if (l == null)
            {
                menu.AddItem(new GUIContent("Git/" + L.T("Lock Scene")), false,
                    () => ProjectContextMenu.Run(L.Tc("scene lock operation", "Locking"), () => LfsLockOps.LockAsync(new[] { path })));
                return;
            }

            if (!LfsLockCache.IsTheirs(l))
            {
                menu.AddDisabledItem(new GUIContent("Git/" + LfsLockCache.Describe(l).Replace('/', '∕')));
                menu.AddItem(new GUIContent("Git/" + L.T("Unlock Scene")), false,
                    () => ProjectContextMenu.Run(L.T("Unlocking"), () => LfsLockOps.UnlockAsync(new[] { path })));
                return;
            }

            menu.AddDisabledItem(new GUIContent("Git/" + LfsLockCache.Describe(l).Replace('/', '∕')));
            menu.AddItem(new GUIContent("Git/" + L.T("Force Unlock…")), false,
                () => ProjectContextMenu.Run(L.T("Force unlocking"), () => LfsLockOps.UnlockAsync(new[] { path })));
        }

        private static void OnGameObject(GenericMenu menu, GameObject go)
        {
            if (!GitRepository.IsRepo || go == null) return;

            menu.AddSeparator(string.Empty);

            var address = SceneObjectRef.Of(go);
            if (!address.IsValid)
            {
                // Сцену ещё ни разу не сохраняли: в файле объекта нет, и
                // истории у него быть не может.
                menu.AddDisabledItem(new GUIContent("Git/" + L.Tc("one object", "Object History")));
                return;
            }

            menu.AddItem(new GUIContent("Git/" + L.Tc("one object", "Object History")), false, () => OpenObject(go, address));

            // У экземпляра префаба две истории: его переопределения в этой сцене
            // и сам файл префаба. Вторая — отдельным пунктом, без лишних вопросов.
            var prefabPath = PrefabPathOf(go);
            if (!string.IsNullOrEmpty(prefabPath))
                menu.AddItem(new GUIContent("Git/" + L.F("Prefab History “{0}”",
                                                         System.IO.Path.GetFileName(prefabPath))),
                             false, () => SceneHistoryWindow.ShowScene(prefabPath));
        }

        private static void OpenObject(GameObject go, SceneObjectAddress address)
        {
            if (!address.InsidePrefab)
            {
                SceneHistoryWindow.ShowObject(address);
                return;
            }

            // Корень экземпляра в файле сцены есть — это документ PrefabInstance
            // с переопределениями. Его история и есть история этого объекта.
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                SceneHistoryWindow.ShowObject(new SceneObjectAddress
                {
                    ScenePath = address.ScenePath,
                    FileId = unchecked((long)address.PrefabId),
                    Name = go.name
                });
                return;
            }

            // Объект принадлежит экземпляру префаба: в YAML сцены его нет,
            // там лежат только переопределения. Историю такого объекта надо
            // смотреть в самом префабе.
            var prefabPath = PrefabPathOf(go);

            if (string.IsNullOrEmpty(prefabPath))
            {
                EditorUtility.DisplayDialog(L.Tc("one object", "Object History"),
                    L.T("The object lives inside a prefab instance and isn't in the scene file — " +
                        "only overrides are stored there. The source prefab couldn't be found."),
                    L.T("Got It"));
                return;
            }

            if (EditorUtility.DisplayDialog(L.Tc("one object", "Object History"),
                    L.F("Object “{0}” comes from a prefab and isn't in the scene file: " +
                        "the scene stores only overrides.\n\nOpen the history of the prefab itself?\n{1}",
                        go.name, prefabPath),
                    L.T("Open Prefab History"), L.T("Cancel")))
                SceneHistoryWindow.ShowScene(prefabPath);
        }

        private static string PrefabPathOf(GameObject go)
        {
            if (go == null || !PrefabUtility.IsPartOfPrefabInstance(go)) return null;

            // Ближайший экземпляр, а не самый внешний: у вложенного префаба
            // объект принадлежит своему файлу, а не файлу обёртки.
            var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        // ------------------------------------------------------- инспектор ---

        private const string ComponentItem = "CONTEXT/Component/Component History";

        [MenuItem(ComponentItem, false, 1500)]
        private static void ComponentHistory(MenuCommand command)
        {
            var component = command.context as Component;
            if (component == null) return;

            var address = SceneObjectRef.Of(component);
            if (!address.IsValid) return;

            if (address.InsidePrefab)
            {
                OpenObject(component.gameObject, address);
                return;
            }

            SceneHistoryWindow.ShowComponent(SceneObjectRef.Of(component.gameObject), address,
                                             SceneChangeIndex.YamlTypeName(component));
        }

        [MenuItem(ComponentItem, true)]
        private static bool ComponentHistoryValidate(MenuCommand command)
        {
            var component = command.context as Component;
            return GitRepository.IsRepo && component != null && SceneObjectRef.Of(component).IsValid;
        }

        // --------------------------------------------------------- Project ---

        private const string AssetItem = "Assets/Git/Object History";

        [MenuItem(AssetItem, false, 1101)]
        private static void AssetHistory()
        {
            var path = SelectedScenePath();
            if (path != null) SceneHistoryWindow.ShowScene(path);
        }

        [MenuItem(AssetItem, true)]
        private static bool AssetHistoryValidate()
        {
            return GitRepository.IsRepo && SelectedScenePath() != null;
        }

        /// <summary>Путь выделенной сцены или префаба — только для них история по объектам осмысленна.</summary>
        private static string SelectedScenePath()
        {
            var guids = Selection.assetGUIDs;
            if (guids == null || guids.Length != 1) return null;

            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            if (string.IsNullOrEmpty(path)) return null;

            return path.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
    }
}
