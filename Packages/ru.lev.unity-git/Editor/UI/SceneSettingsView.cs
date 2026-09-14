using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lev.Git.UI
{
    /// <summary>
    /// Текст версий сцены по объектам git. Одна версия сцены нужна нескольким
    /// строкам ленты сразу — читается один раз.
    /// </summary>
    internal static class SceneBlobText
    {
        private const string NoBlob = "0000000000000000000000000000000000000000";
        private static readonly Dictionary<string, Task<string>> Cache = new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        private static readonly Queue<string> Order = new Queue<string>();

        public static Task<string> Read(string blob)
        {
            if (string.IsNullOrEmpty(blob) || blob == NoBlob) return Task.FromResult<string>(null);

            Task<string> task;
            if (Cache.TryGetValue(blob, out task)) return task;

            while (Order.Count >= 6) Cache.Remove(Order.Dequeue());

            task = GitConflicts.BlobTextAsync(blob);
            Cache[blob] = task;
            Order.Enqueue(blob);
            return task;
        }
    }

    /// <summary>
    /// Настройки сцены — RenderSettings, LightmapSettings, NavMeshSettings,
    /// OcclusionCullingSettings — настоящим инспектором, строка к строке.
    ///
    /// Документ настроек вырезается из версии сцены и загружается сам по себе,
    /// как прошлые версии материалов: получается временный объект с полями и
    /// подписями Unity. Ссылки на объекты этой же сцены (солнце, карты
    /// освещения) из одного документа не восстановить — они показываются None.
    /// Текущее состояние — настройки активной сцены, их можно править.
    /// </summary>
    internal sealed class SceneSettingsView : IDisposable
    {
        private static readonly HashSet<SceneSettingsView> Alive = new HashSet<SceneSettingsView>();

        static SceneSettingsView()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                foreach (var view in Alive.ToList()) view.Dispose();
            };
        }

        private readonly List<Object> _owned = new List<Object>();
        private readonly Action _repaint;
        private readonly string _typeName;
        private readonly bool _localReferences;

        private SerializedObject _oldSo, _newSo;
        private bool _loading = true, _disposed;
        private string _problem;

        public SceneSettingsView(SceneEvent e, Action repaint)
        {
            Alive.Add(this);
            _repaint = repaint;

            var oldDoc = e.Node != null ? e.Node.OldDoc : null;
            var newDoc = e.Node != null ? e.Node.NewDoc : null;
            _typeName = (newDoc ?? oldDoc) != null ? (newDoc ?? oldDoc).TypeName : null;
            _localReferences = SceneDocText.HasLocalReferences(newDoc) || SceneDocText.HasLocalReferences(oldDoc);

            LoadAsync(e.Revision, oldDoc, newDoc);
        }

        private async void LoadAsync(SceneRevision rev, UnityDocument oldDoc, UnityDocument newDoc)
        {
            try
            {
                var oldText = oldDoc != null ? await SceneBlobText.Read(rev.OldBlob) : null;
                var newText = newDoc != null ? await SceneBlobText.Read(rev.NewBlob) : null;
                if (_disposed) return;

                if (oldDoc != null) _oldSo = Load(oldText, oldDoc);
                if (newDoc != null) _newSo = Load(newText, newDoc);

                if ((oldDoc != null && _oldSo == null) || (newDoc != null && _newSo == null))
                    _problem = L.F("Unity couldn't build {0} from this version — showing values from the file.",
                                   _typeName ?? L.Tc("scene settings", "settings"));
            }
            catch (Exception e)
            {
                _problem = L.F("Settings failed to load: {0}", e.Message);
            }
            finally
            {
                _loading = false;
                if (!_disposed) _repaint();
            }
        }

        private SerializedObject Load(string text, UnityDocument doc)
        {
            var yaml = SceneDocText.Standalone(SceneDocText.Extract(text, doc.FileId));
            if (yaml == null) return null;

            var dir = Path.Combine(GitRepository.ProjectRoot, "Temp/LevGit/rev");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".asset");

            try
            {
                File.WriteAllText(file, yaml);
                var objects = InternalEditorUtility.LoadSerializedFileAndForget(file) ?? new Object[0];

                Object found = null;
                foreach (var o in objects)
                {
                    if (o == null) continue;
                    o.hideFlags = HideFlags.HideAndDontSave;
                    _owned.Add(o);
                    if (found == null) found = o;
                }

                return found != null ? new SerializedObject(found) : null;
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        public bool Failed
        {
            get { return !_loading && _problem != null; }
        }

        public string Problem
        {
            get { return _problem; }
        }

        // ------------------------------------------------------------ показ ---

        /// <returns>false — показать инспектором нельзя, вызывающий рисует таблицу значений.</returns>
        public bool Draw(float width, bool onlyChanged, bool compareLive, string scenePath)
        {
            if (_loading)
            {
                EditorGUILayout.LabelField(L.T("Reading scene version…"), EditorStyles.miniLabel);
                return true;
            }

            if (_problem != null) return false;

            SerializedObject live = null;
            string readOnly = null;

            if (compareLive)
            {
                var active = SceneManager.GetActiveScene();
                if (!active.IsValid() || !string.Equals(active.path, scenePath, StringComparison.OrdinalIgnoreCase))
                {
                    EditorGUILayout.HelpBox(L.F("Settings are taken from the active scene, but {0} isn't active — " +
                                                "there's nothing to compare with the current state.", Ui.NameOf(scenePath)), MessageType.None);
                    return true;
                }

                var target = LiveSettings(_typeName);
                if (target == null)
                {
                    EditorGUILayout.HelpBox(L.F("Current {0} can't be found in the editor.", _typeName), MessageType.None);
                    return true;
                }

                live = new SerializedObject(target);
                if (EditorApplication.isPlayingOrWillChangePlaymode) readOnly = L.T("Play mode is on — view only.");
            }

            var left = compareLive ? _newSo : _oldSo;
            var right = compareLive ? live : _newSo;

            const float marker = 16f;
            float column = Mathf.Max(110f, (width - marker - 30f) * 0.5f);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(compareLive ? L.T("After Commit") : left == null ? L.T("No Settings Before") : L.T("Before Commit"),
                                           EditorStyles.miniBoldLabel, GUILayout.Width(column));
                GUILayout.Space(marker);
                EditorGUILayout.LabelField(compareLive ? (readOnly != null ? L.T("Now") : L.T("Now — Editable"))
                                                       : right == null ? L.T("Settings Removed") : L.T("After Commit"),
                                           EditorStyles.miniBoldLabel, GUILayout.Width(column));
            }

            if (readOnly != null) EditorGUILayout.LabelField(readOnly, EditorStyles.miniLabel);
            if (_localReferences)
                EditorGUILayout.LabelField(L.T("References to scene objects in versions from history are shown as None — they can't be restored from a single document."),
                                           EditorStyles.wordWrappedMiniLabel);

            if (left != null) left.Update();
            if (right != null) right.Update();

            var walker = (right ?? left).GetIterator();
            var tint = GitPalette.Fill(GitFileStatus.Modified);
            var dot = tint;
            tint.a = 0.12f;

            float labelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Max(80f, column * 0.45f);

            bool enter = true;
            int shown = 0;

            try
            {
                while (walker.NextVisible(enter))
                {
                    enter = false;
                    var path = walker.propertyPath;
                    if (path == "m_Script") continue;

                    var l = left != null ? left.FindProperty(path) : null;
                    var r = right != null ? right.FindProperty(path) : null;
                    if (l == null && r == null) continue;

                    bool same = l != null && r != null && SerializedProperty.DataEquals(l, r);
                    if (onlyChanged && same) continue;
                    shown++;

                    if (l != null && r != null) l.isExpanded = r.isExpanded;

                    var row = EditorGUILayout.BeginHorizontal();
                    if (!same && Event.current.type == EventType.Repaint) EditorGUI.DrawRect(row, tint);

                    using (new EditorGUI.DisabledScope(true))
                    {
                        if (l != null) EditorGUILayout.PropertyField(l, true, GUILayout.Width(column));
                        else GUILayout.Space(column);
                    }

                    float height = EditorGUI.GetPropertyHeight(r ?? l, true);
                    var mark = GUILayoutUtility.GetRect(marker, height, GUILayout.Width(marker));
                    if (!same && Event.current.type == EventType.Repaint)
                        EditorGUI.DrawRect(new Rect(mark.x + 4f, mark.y + 5f, 7f, 7f), dot);

                    using (new EditorGUI.DisabledScope(live == null || readOnly != null))
                    {
                        if (r != null) EditorGUILayout.PropertyField(r, true, GUILayout.Width(column));
                        else GUILayout.Space(column);
                    }

                    EditorGUILayout.EndHorizontal();
                }
            }
            finally
            {
                EditorGUIUtility.labelWidth = labelWidth;
            }

            if (shown == 0)
                EditorGUILayout.LabelField(compareLive ? L.T("Settings are now the same as after this commit.") : L.T("No differences in fields."),
                                           EditorStyles.miniLabel);

            // Правка живых настроек — обычная правка сцены, с записью в Undo.
            if (live != null && readOnly == null && live.ApplyModifiedProperties())
            {
                var active = SceneManager.GetActiveScene();
                if (active.IsValid()) EditorSceneManager.MarkSceneDirty(active);
                SceneChangeIndex.MarkDirty();
            }

            return true;
        }

        /// <summary>
        /// Настройки активной сцены. Публичного доступа к этим объектам у Unity
        /// нет — берутся теми же внутренними методами, что у окна Lighting; не
        /// нашлись — ищутся среди загруженных объектов по имени типа.
        /// </summary>
        private static Object LiveSettings(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            Object found = null;
            switch (typeName)
            {
                case "RenderSettings":
                    found = CallStatic("UnityEngine.RenderSettings", "GetRenderSettings");
                    break;
                case "LightmapSettings":
                    found = CallStatic("UnityEditor.LightmapEditorSettings", "GetLightmapSettings");
                    break;
                case "NavMeshSettings":
                    found = CallStatic("UnityEditor.AI.NavMeshBuilder", "get_navMeshSettingsObject");
                    break;
                case "OcclusionCullingSettings":
                    found = CallStatic("UnityEditor.StaticOcclusionCulling", "GetOcclusionCullingSettings");
                    break;
            }

            if (found != null) return found;

            var candidates = Resources.FindObjectsOfTypeAll<Object>()
                .Where(o => o != null && o.GetType().Name == typeName && (o.hideFlags & HideFlags.DontSave) == 0)
                .ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static Object CallStatic(string typeName, string method)
        {
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var type = assembly.GetType(typeName, false);
                    if (type == null) continue;

                    var m = type.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    return m != null ? m.Invoke(null, null) as Object : null;
                }
            }
            catch { }

            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Alive.Remove(this);

            foreach (var o in _owned)
                if (o != null) Object.DestroyImmediate(o);
            _owned.Clear();
            _oldSo = _newSo = null;
        }
    }

    /// <summary>
    /// Порядок корневых объектов в Hierarchy — документ SceneRoots. Вместо
    /// номеров Transform — имена объектов: добавленные, удалённые и сдвинутые
    /// отмечены. Текущее состояние — корни открытой сцены по порядку.
    /// </summary>
    internal sealed class SceneRootsView
    {
        private readonly Action _repaint;
        private List<string> _before, _after;
        private bool _loading = true;

        public SceneRootsView(SceneEvent e, Action repaint)
        {
            _repaint = repaint;
            LoadAsync(e);
        }

        private async void LoadAsync(SceneEvent e)
        {
            try
            {
                var oldDoc = e.Node != null ? e.Node.OldDoc : null;
                var newDoc = e.Node != null ? e.Node.NewDoc : null;

                var oldText = oldDoc != null ? await SceneBlobText.Read(e.Revision.OldBlob) : null;
                var newText = newDoc != null ? await SceneBlobText.Read(e.Revision.NewBlob) : null;

                _before = oldDoc != null ? Names(oldText, SceneDocText.Roots(oldDoc)) : new List<string>();
                _after = newDoc != null ? Names(newText, SceneDocText.Roots(newDoc)) : new List<string>();
            }
            finally
            {
                _loading = false;
                _repaint();
            }
        }

        private static List<string> Names(string text, List<long> roots)
        {
            var names = new List<string>();
            foreach (var id in roots)
                names.Add(SceneDocText.NameOfTransform(text, id) ?? L.F("prefab instance ({0})", id));
            return names;
        }

        public void Draw(float width, bool compareLive, string scenePath)
        {
            if (_loading)
            {
                EditorGUILayout.LabelField(L.T("Reading scene version…"), EditorStyles.miniLabel);
                return;
            }

            var left = compareLive ? _after : _before;
            List<string> right;

            if (compareLive)
            {
                var scene = SceneManager.GetSceneByPath(scenePath);
                if (!scene.isLoaded)
                {
                    EditorGUILayout.HelpBox(L.F("Scene {0} isn't open — there's nowhere to get the current order from.", Ui.NameOf(scenePath)), MessageType.None);
                    return;
                }
                right = scene.GetRootGameObjects().Select(g => g.name).ToList();
            }
            else
            {
                right = _after;
            }

            EditorGUILayout.LabelField(L.T("Order of root objects in the Hierarchy"), EditorStyles.miniLabel);

            float column = Mathf.Max(110f, (width - 30f) * 0.5f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(compareLive ? L.T("After Commit") : L.T("Before Commit"), EditorStyles.miniBoldLabel, GUILayout.Width(column));
                EditorGUILayout.LabelField(compareLive ? L.T("Now") : L.T("After Commit"), EditorStyles.miniBoldLabel, GUILayout.Width(column));
            }

            int rows = Mathf.Max(left.Count, right.Count);
            var added = new Color(0.42f, 0.8f, 0.5f);
            var removed = new Color(0.93f, 0.42f, 0.38f);
            var moved = new Color(0.9f, 0.72f, 0.3f);

            for (int i = 0; i < rows; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    Cell(left, right, i, column, removed, moved);
                    Cell(right, left, i, column, added, moved);
                }
            }
        }

        private static void Cell(List<string> mine, List<string> other, int i, float width, Color missing, Color moved)
        {
            if (i >= mine.Count)
            {
                GUILayout.Space(width);
                return;
            }

            var name = mine[i];
            var style = new GUIStyle(EditorStyles.label);
            string suffix = string.Empty;

            if (!other.Contains(name))
            {
                style.normal.textColor = missing;
                suffix = other == null ? string.Empty : "  ●";
            }
            else if (other.IndexOf(name) != i)
            {
                style.normal.textColor = moved;
                suffix = "  ↕";
            }

            GUILayout.Label((i + 1) + ". " + name + suffix, style, GUILayout.Width(width));
        }
    }
}
