using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Lev.Git.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lev.Git
{
    /// <summary>
    /// Откат объекта, компонента или одного свойства к последнему коммиту.
    ///
    /// Правится живой объект в редакторе, а не текст файла: откат попадает в
    /// историю отмены одной операцией, не теряет других несохранённых правок
    /// сцены и не требует перезагружать её. Цена — нужна открытая сцена, а
    /// удалённый объект целиком вернуть нельзя: его место в иерархии и ссылки на
    /// него не восстановить из одного документа.
    /// </summary>
    public static class SceneRevert
    {
        private static string UndoName => L.Tc("undo", "Revert to Last Commit");

        public sealed class Outcome
        {
            public bool Ok;
            public string Message;

            public static Outcome Fail(string message) { return new Outcome { Message = message }; }
        }

        private sealed class Tally
        {
            public int Done, Skipped;
            public readonly List<string> Problems = new List<string>();

            public Outcome ToOutcome()
            {
                if (Done == 0)
                    return Outcome.Fail(Problems.Count > 0 ? string.Join("; ", Problems.ToArray())
                                                          : L.T("Nothing to revert: the values already match the last commit."));

                var message = L.T("Reverted to the last commit.");
                if (Skipped > 0) message += " " + L.F("Properties not transferred: {0} — curves, nested structures and references within the scene can't be restored this way.", Skipped);
                if (Problems.Count > 0) message += " " + string.Join("; ", Problems.ToArray()) + ".";
                return new Outcome { Ok = true, Message = message };
            }
        }

        public static bool IsPrefabFile(string path)
        {
            return path != null && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Готов ли файл к откату. null — готов; иначе — что мешает.</summary>
        public static string NotReady(string path)
        {
            if (string.IsNullOrEmpty(path)) return L.T("No file selected.");

            if (IsPrefabFile(path))
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                return stage != null && string.Equals(stage.assetPath, path, StringComparison.OrdinalIgnoreCase)
                    ? L.T("The prefab is open for editing — close it and revert again: otherwise the change will be lost when it's saved.")
                    : null;
            }

            return null;
        }

        /// <summary>Сцена открыта — откат идёт по живым объектам. Во всех остальных файлах — правкой текста.</summary>
        public static bool IsLive(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return false;
            var scene = SceneManager.GetSceneByPath(path);
            return scene.IsValid() && scene.isLoaded;
        }

        /// <summary>Почему узел не откатить; null — можно. Коротко: текст идёт в пункт меню.</summary>
        public static string CannotReason(string path, SceneNode node, SceneNode owner)
        {
            if (node == null) return L.T("nothing to revert");
            bool live = IsLive(path);

            // Объект целиком — это ещё и место в иерархии и ссылки на него у других:
            // из одного документа их не восстановить.
            if (owner == null && node.Kind == SceneChangeKind.Removed) return L.T("removed object — revert the whole file");
            if (owner == null && node.Kind == SceneChangeKind.Added && !live) return L.T("added object — revert the whole file");

            if (live && owner != null && node.Kind == SceneChangeKind.Removed)
            {
                var componentType = SceneHistoryIcons.ResolveComponent(TypeOf(node), node.OldDoc);
                if (componentType == null) return L.T("component type can't be determined");
                if (componentType == typeof(Transform)) return L.T("Transform can't be restored separately");
            }

            return null;
        }

        // ---------------------------------------------------------- узел ---

        /// <summary>Объект (вместе с его изменившимися компонентами) или компонент — к последнему коммиту.</summary>
        public static Outcome Node(string path, SceneNode node, SceneNode owner)
        {
            var reason = NotReady(path);
            if (reason != null) return Outcome.Fail(reason);
            if (!IsLive(path)) return Outcome.Fail(L.T("The scene is not open."));

            var cannot = CannotReason(path, node, owner);
            if (cannot != null) return Outcome.Fail(L.F("Can't revert: {0}.", cannot));

            var tally = new Tally();
            int group = BeginUndo();

            bool destroyed = RevertOne(path, node, owner, tally);

            // У удалённого откатом объекта компоненты ушли вместе с ним.
            if (owner == null && !destroyed)
            {
                foreach (var child in node.Children)
                {
                    if (CannotReason(path, child, node) != null) { tally.Skipped++; continue; }
                    RevertOne(path, child, node, tally);
                }
            }

            Undo.CollapseUndoOperations(group);
            Finish(path);
            return tally.ToOutcome();
        }

        /// <returns>true — объект удалён целиком (он был добавлен после коммита).</returns>
        private static bool RevertOne(string path, SceneNode node, SceneNode owner, Tally tally)
        {
            var live = SceneObjectRef.Find(new SceneObjectAddress { ScenePath = path, FileId = node.FileId });

            switch (node.Kind)
            {
                case SceneChangeKind.Added:
                {
                    if (live == null) { tally.Problems.Add(L.F("“{0}” not found", node.Title)); return false; }
                    // Transform не удалить отдельно — он уходит вместе со своим объектом.
                    if (live is Transform) { tally.Skipped++; return false; }

                    Undo.DestroyObjectImmediate(live);
                    tally.Done++;
                    return !(live is Component);
                }

                case SceneChangeKind.Removed:
                {
                    var go = SceneObjectRef.Find(new SceneObjectAddress { ScenePath = path, FileId = owner.FileId }) as GameObject;
                    if (go == null) { tally.Problems.Add(L.F("object “{0}” not found", owner.Title)); return false; }

                    var type = SceneHistoryIcons.ResolveComponent(TypeOf(node), node.OldDoc);
                    var added = Undo.AddComponent(go, type);
                    if (added == null) { tally.Problems.Add(L.F("component {0} wasn't added", TypeOf(node))); return false; }

                    var restored = UnityPropertyApplier.RestoreLive(added, node.OldDoc, UndoName);
                    tally.Done++;
                    tally.Skipped += restored.Skipped;
                    return false;
                }

                default:
                {
                    if (TypeOf(node) == PrefabOverrides.TypeName)
                    {
                        var doc = node.NewDoc ?? node.OldDoc;
                        RevertInstance(SceneObjectRef.FindInstance(path, node.FileId, PrefabOverrides.SourceGuid(doc), PrefabOverrides.NameOf(doc)),
                                       node, null, tally);
                        return false;
                    }

                    if (live == null) { tally.Problems.Add(L.F("“{0}” not found", node.Title)); return false; }

                    var restored = UnityPropertyApplier.RestoreLive(live, node.OldDoc, UndoName);
                    if (restored.Applied > 0) tally.Done++;
                    tally.Skipped += restored.Skipped;
                    return false;
                }
            }
        }

        // ------------------------------------------------------- свойство ---

        /// <summary>Одно свойство — к значению из последнего коммита.</summary>
        public static Outcome Property(string path, SceneNode node, SceneNode owner, ScenePropertyChange prop)
        {
            var reason = NotReady(path);
            if (reason != null) return Outcome.Fail(reason);
            if (!IsLive(path)) return Outcome.Fail(L.T("The scene is not open."));
            if (node == null || prop == null) return Outcome.Fail(L.T("Nothing to revert."));

            if (node.Kind != SceneChangeKind.Modified)
                return Outcome.Fail(L.T("The object was added or removed after the commit — revert it as a whole."));

            var live = SceneObjectRef.Find(new SceneObjectAddress { ScenePath = path, FileId = node.FileId });
            int group = BeginUndo();

            if (TypeOf(node) == PrefabOverrides.TypeName)
                return Outcome.Fail(L.T("An instance override is reverted from the row of the object inside the instance."));

            if (live == null) return Outcome.Fail(L.F("“{0}” not found in the scene.", node.Title));
            if (prop.Old == null) return Outcome.Fail(L.T("The property wasn't in the last commit — nothing to revert."));

            bool ok = UnityPropertyApplier.RestoreProperty(live, prop.Path, prop.Old, UndoName);
            Undo.CollapseUndoOperations(group);
            Finish(path);

            return ok
                ? new Outcome { Ok = true, Message = L.F("“{0}” reverted to the last commit.", prop.Path) }
                : Outcome.Fail(L.F("Property “{0}” can't be restored this way: this value type isn't transferred. Revert the whole component or the file.", prop.Path));
        }

        // ------------------------------------------------ экземпляр префаба ---

        /// <summary>
        /// Выбранные переопределения экземпляра — к последнему коммиту.
        /// <paramref name="filter"/> отбирает, какие именно: одно свойство,
        /// все правки объекта или компонента внутри экземпляра.
        /// </summary>
        public static Outcome InstanceOverrides(string path, SceneNode instance, Func<PrefabModificationChange, bool> filter)
        {
            var reason = NotReady(path);
            if (reason != null) return Outcome.Fail(reason);
            if (!IsLive(path)) return Outcome.Fail(L.T("The scene is not open."));
            if (IsPrefabFile(path)) return Outcome.Fail(L.T("A nested prefab is reverted by reverting the file."));
            if (instance == null || instance.Kind != SceneChangeKind.Modified)
                return Outcome.Fail(L.T("The instance was added or removed after the commit — revert it as a whole."));

            var doc = instance.NewDoc ?? instance.OldDoc;
            var root = SceneObjectRef.FindInstance(path, instance.FileId, PrefabOverrides.SourceGuid(doc), PrefabOverrides.NameOf(doc));
            var tally = new Tally();
            int group = BeginUndo();
            RevertInstance(root, instance, filter, tally);
            Undo.CollapseUndoOperations(group);
            Finish(path);
            return tally.ToOutcome();
        }

        /// <summary>
        /// Переопределения экземпляра — к состоянию коммита. Значение переносится
        /// на объект экземпляра, чей исходный объект указан в target. Переопределение,
        /// которого в коммите не было, снимается штатным «Revert» Unity.
        /// </summary>
        private static void RevertInstance(GameObject root, SceneNode node, Func<PrefabModificationChange, bool> filter, Tally tally)
        {
            if (root == null) { tally.Problems.Add(L.T("prefab instance not found in the scene")); return; }

            foreach (var change in PrefabOverrides.Changes(node.OldDoc, node.NewDoc))
            {
                if (filter != null && !filter(change)) continue;

                var target = SceneObjectRef.InstanceObject(root, change.Target);
                if (target == null) { tally.Skipped++; continue; }

                var so = new SerializedObject(target);
                var property = so.FindProperty(change.PropertyPath);
                if (property == null) { tally.Skipped++; continue; }

                if (change.Before == null)
                {
                    PrefabUtility.RevertPropertyOverride(property, InteractionMode.UserAction);
                    tally.Done++;
                    continue;
                }

                Undo.RecordObject(target, UndoName);
                var value = property.propertyType == SerializedPropertyType.ObjectReference ? change.Before.Reference : change.Before.Value;
                if (UnityPropertyApplier.ApplyValue(property, value))
                {
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(target);
                    tally.Done++;
                }
                else
                {
                    tally.Skipped++;
                }
            }
        }

        // ------------------------------------------------ откат правкой файла ---

        /// <summary>
        /// Объект (со своими компонентами) или компонент — к коммиту правкой
        /// текста файла: закрытая сцена, префаб, материал, любой YAML-ассет.
        /// </summary>
        public static async Task<Outcome> NodeInFileAsync(string path, SceneNode node, SceneNode owner)
        {
            var reason = NotReady(path);
            if (reason != null) return Outcome.Fail(reason);

            var cannot = CannotReason(path, node, owner);
            if (cannot != null) return Outcome.Fail(L.F("Can't revert: {0}.", cannot));

            var texts = await TextsAsync(path);
            if (texts.error != null) return Outcome.Fail(texts.error);

            var text = RevertNodeText(texts.current, texts.head, node, owner);
            if (owner == null && node.Kind == SceneChangeKind.Modified)
                foreach (var child in node.Children)
                    if (CannotReason(path, child, node) == null)
                        text = RevertNodeText(text, texts.head, child, node);

            return WriteText(path, texts.current, text);
        }

        /// <summary>Одно свойство — к коммиту правкой текста файла.</summary>
        public static async Task<Outcome> PropertyInFileAsync(string path, SceneNode node, ScenePropertyChange prop)
        {
            var reason = NotReady(path);
            if (reason != null) return Outcome.Fail(reason);
            if (node == null || prop == null) return Outcome.Fail(L.T("Nothing to revert."));
            if (node.Kind != SceneChangeKind.Modified)
                return Outcome.Fail(L.T("The object was added or removed after the commit — revert it as a whole."));

            var texts = await TextsAsync(path);
            if (texts.error != null) return Outcome.Fail(texts.error);

            return WriteText(path, texts.current, YamlRevert.Property(texts.current, texts.head, node.FileId, prop.Path));
        }

        /// <summary>Переопределения экземпляра префаба — к коммиту правкой текста файла.</summary>
        public static async Task<Outcome> InstanceOverridesInFileAsync(string path, SceneNode instance, Func<PrefabModificationChange, bool> filter)
        {
            var reason = NotReady(path);
            if (reason != null) return Outcome.Fail(reason);
            if (instance == null || instance.Kind != SceneChangeKind.Modified)
                return Outcome.Fail(L.T("The instance was added or removed after the commit — revert it as a whole."));

            var texts = await TextsAsync(path);
            if (texts.error != null) return Outcome.Fail(texts.error);

            return WriteText(path, texts.current, YamlRevert.InstanceOverrides(texts.current, texts.head, instance.FileId, filter));
        }

        private static string RevertNodeText(string text, string head, SceneNode node, SceneNode owner)
        {
            if (node.Kind == SceneChangeKind.Modified)
            {
                // Заголовок объекта, заведённый ради изменившихся компонентов, своего документа не менял.
                if (node.OldDoc == null || node.NewDoc == null) return text;

                if (TypeOf(node) == PrefabOverrides.TypeName)
                    return YamlRevert.InstanceOverrides(text, head, node.FileId, null) ?? text;

                // По свойствам, а не документом целиком: родитель, дети и список компонентов
                // остаются как сейчас — иначе откат одного объекта порвал бы иерархию.
                foreach (var p in node.Props)
                {
                    if (UnityPropertyApplier.IsStructural(p.Path)) continue;
                    text = YamlRevert.Property(text, head, node.FileId, p.Path) ?? text;
                }
                return text;
            }

            // Добавленный или удалённый компонент — документом и ссылкой в списке компонентов объекта.
            var next = YamlRevert.Document(text, head, node.FileId) ?? text;
            if (owner != null) next = YamlRevert.ComponentLink(next, head, owner.FileId, node.FileId) ?? next;
            return next;
        }

        private static async Task<(string current, string head, string error)> TextsAsync(string path)
        {
            var gitPath = GitRepository.ToGitPath(path);
            var current = GitOperations.ReadWorktreeText(gitPath);
            if (current == null) return (null, null, L.F("File not read: {0}", path));

            var head = await GitOperations.ShowHeadTextAsync(gitPath);
            if (head == null) return (null, null, L.T("The file isn't in the last commit — it's reverted by deleting the file."));

            return (current, head, null);
        }

        private static Outcome WriteText(string path, string current, string text)
        {
            if (text == null || text == current)
                return Outcome.Fail(L.T("Nothing to revert: the file already has the values of the last commit."));

            if (!GitOperations.WriteWorktreeText(GitRepository.ToGitPath(path), text))
                return Outcome.Fail(L.T("The file wasn't written — it may be read-only (lockable without a lock)."));

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return new Outcome { Ok = true, Message = L.F("Reverted to the last commit — {0} updated.", System.IO.Path.GetFileName(path)) };
        }

        // ---------------------------------------------------------- мелочи ---

        private static string TypeOf(SceneNode node)
        {
            var doc = node.NewDoc ?? node.OldDoc;
            return doc != null ? doc.TypeName : node.Title;
        }

        private static int BeginUndo()
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            return Undo.GetCurrentGroup();
        }

        /// <summary>Сцена помечается изменённой; префаб — сохраняется: правка ассета иначе потерялась бы.</summary>
        private static void Finish(string path)
        {
            if (IsPrefabFile(path))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset != null) AssetDatabase.SaveAssetIfDirty(asset);
                return;
            }

            var scene = SceneManager.GetSceneByPath(path);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
            SceneChangeIndex.MarkDirty();
        }
    }
}
