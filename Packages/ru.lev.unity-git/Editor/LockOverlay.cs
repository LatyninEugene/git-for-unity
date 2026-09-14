using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lev.Git
{
    /// <summary>
    /// Замки там, где на ассет смотрят: иконка в окне Project, строка сцены в
    /// иерархии, шапка инспектора. Свой лок и чужой — разным цветом, в
    /// подсказке — кто и когда. Всё читается из <see cref="LfsLockCache"/>.
    /// </summary>
    [InitializeOnLoad]
    public static class LockOverlay
    {
        private static readonly Color MineColor = new Color(0.36f, 0.78f, 0.45f);
        private static readonly Color TheirsColor = new Color(0.96f, 0.42f, 0.30f);
        private static readonly Color UnknownColor = new Color(0.85f, 0.75f, 0.35f);

        private static Texture _lockIcon;
        private static GUIStyle _tiny;

        static LockOverlay()
        {
            EditorApplication.projectWindowItemOnGUI += OnProjectItem;
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItem;
            UnityEditor.Editor.finishedDefaultHeaderGUI += OnInspectorHeader;
        }

        private static Texture LockIcon
        {
            get
            {
                if (_lockIcon == null)
                    _lockIcon = EditorGUIUtility.FindTexture(EditorGUIUtility.isProSkin ? "d_InspectorLock" : "InspectorLock")
                                ?? EditorGUIUtility.FindTexture("InspectorLock")
                                ?? EditorGUIUtility.FindTexture("LockIcon-On");
                return _lockIcon;
            }
        }

        public static Color ColorOf(LfsLockInfo l)
        {
            if (!LfsLockCache.Locks.Verified) return UnknownColor;
            return l.Mine ? MineColor : TheirsColor;
        }

        // ---------------------------------------------------------- Project ---

        private static void OnProjectItem(string guid, Rect rect)
        {
            if (!GitRepository.IsRepo) return;

            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return;

            var l = LfsLockCache.LockOf(path);
            bool pointer = l == null && LfsLockCache.IsNotDownloaded(path);
            if (l == null && !pointer) return;

            bool grid = rect.height > 20f;
            var badge = grid
                ? new Rect(rect.xMax - 16f, rect.y + 2f, 14f, 14f)
                : new Rect(rect.xMax - 32f, rect.y + 1f, 14f, 14f);

            if (l != null) DrawLock(badge, l, LfsLockCache.Describe(l));
            else DrawPointer(badge);
        }

        public static void DrawLock(Rect badge, LfsLockInfo l, string tooltip)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(badge, new Color(0f, 0f, 0f, 0.55f));
                var old = GUI.color;
                GUI.color = ColorOf(l);
                if (LockIcon != null) GUI.DrawTexture(new RectOffset(1, 1, 1, 1).Remove(badge), LockIcon, ScaleMode.ScaleToFit);
                else GUI.Label(badge, "L", Tiny);
                GUI.color = old;
            }

            GUI.Label(badge, new GUIContent(string.Empty, tooltip));
        }

        private static void DrawPointer(Rect badge)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(badge, new Color(0.45f, 0.45f, 0.45f, 0.9f));
                GUI.Label(badge, "↓", Tiny);
            }

            GUI.Label(badge, new GUIContent(string.Empty,
                L.T("The LFS file is not downloaded: there is a pointer instead of the content. Right-click → Git → Download from LFS.")));
        }

        private static GUIStyle Tiny
        {
            get
            {
                if (_tiny == null)
                    _tiny = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontStyle = FontStyle.Bold,
                        fontSize = 9,
                        normal = { textColor = Color.white }
                    };
                return _tiny;
            }
        }

        // -------------------------------------------------------- Hierarchy ---

        private static void OnHierarchyItem(int instanceId, Rect rect)
        {
            if (!LfsLockCache.HasData) return;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.handle != instanceId) continue;
                if (string.IsNullOrEmpty(scene.path)) return;

                var l = LfsLockCache.LockOf(scene.path);
                if (l == null) return;

                DrawLock(new Rect(rect.xMax - 40f, rect.y + 1f, 14f, 14f), l, LfsLockCache.Describe(l));
                return;
            }
        }

        // -------------------------------------------------------- Inspector ---

        private static void OnInspectorHeader(UnityEditor.Editor editor)
        {
            if (!LfsLockCache.HasData || editor == null || editor.target == null) return;

            string path;
            bool sceneObject = false;

            var go = editor.target as GameObject;
            var comp = editor.target as Component;
            if (go == null && comp != null) go = comp.gameObject;

            if (go != null && !EditorUtility.IsPersistent(go))
            {
                path = go.scene.path;
                sceneObject = true;
            }
            else
            {
                path = AssetDatabase.GetAssetPath(editor.target);
            }

            if (string.IsNullOrEmpty(path)) return;

            var l = LfsLockCache.LockOf(path);
            if (l == null)
            {
                if (!sceneObject && LfsLockCache.IsNotDownloaded(path))
                    EditorGUILayout.HelpBox(L.T("The LFS file is not downloaded — the project has a pointer, not the content."), MessageType.Warning);
                return;
            }

            var rect = EditorGUILayout.GetControlRect(false, 18f);
            DrawLock(new Rect(rect.x, rect.y + 2f, 14f, 14f), l, null);

            var text = sceneObject ? L.F("Scene: {0}", LfsLockCache.Describe(l)) : LfsLockCache.Describe(l);
            if (LfsLockCache.IsTheirs(l)) text = L.F("{0} — your edits may be lost", text);

            var style = new GUIStyle(EditorStyles.miniLabel);
            if (LfsLockCache.IsTheirs(l)) style.normal.textColor = TheirsColor;
            GUI.Label(new Rect(rect.x + 18f, rect.y, rect.width - 18f, rect.height), text, style);
        }
    }

    /// <summary>
    /// Предупреждения до потери работы и автолок.
    ///
    /// Сохранение ассета с чужим локом спрашивает до записи на диск. Открытие
    /// такой сцены показывает уведомление. Правка чужого залоченного файла
    /// снаружи редактора замечается по статусу git. Автолок — по настройке:
    /// первая правка сама берёт лок.
    /// </summary>
    [InitializeOnLoad]
    public sealed class LockGuard : AssetModificationProcessor
    {
        private static readonly HashSet<string> _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _primed;

        static LockGuard()
        {
            EditorSceneManager.sceneOpened += OnSceneOpened;
            GitStatusCache.Updated += OnStatusUpdated;
        }

        public enum AutoLockMode { Off = 0, Scenes = 1, AllLockable = 2 }

        /// <summary>Сохранение запертого (lockable) файла без лока.</summary>
        public enum ReadOnlySaveMode { Ask = 0, TakeLock = 1, SaveWithoutLock = 2 }

        // ------------------------------------------------------- сохранение ---

        private static string[] OnWillSaveAssets(string[] paths)
        {
            paths = OfferLockForReadOnly(paths);

            if (paths == null || paths.Length == 0 || !LfsLockCache.HasData || LfsLockCache.Locks.All.Count == 0)
            {
                ScheduleAutoLock(paths);
                return paths;
            }

            var blocked = new List<string>();
            foreach (var p in paths)
            {
                var l = LfsLockCache.LockOf(p);
                if (LfsLockCache.IsTheirs(l)) blocked.Add(p);
            }

            if (blocked.Count == 0)
            {
                ScheduleAutoLock(paths);
                return paths;
            }

            var lines = new List<string>();
            foreach (var p in blocked) lines.Add(p + " — " + LfsLockCache.Describe(LfsLockCache.LockOf(p)));

            int choice = EditorUtility.DisplayDialogComplex(L.T("Files Locked by Others"),
                L.F("• {0}" +
                    "\n\nWhile someone else holds the lock, one of the versions of these files will be lost on merge. " +
                    "If a file is marked lockable, it is read-only, and Unity will not be able to write it.",
                    string.Join("\n• ", lines.ToArray())),
                L.T("Don't Save Them"), L.T("Cancel"), L.T("Save Anyway"));

            if (choice == 2)
            {
                ScheduleAutoLock(paths);
                return paths;
            }

            var allowed = new List<string>();
            foreach (var p in paths) if (!blocked.Contains(p)) allowed.Add(p);
            ScheduleAutoLock(allowed.ToArray());
            return allowed.ToArray();
        }

        /// <summary>
        /// Запираемый (lockable) файл без лока git-lfs держит только для чтения, и
        /// Unity его просто не запишет — правки останутся висеть несохранёнными.
        ///
        /// Лок при этом не обязателен: команды работают по-разному, поэтому что
        /// делать — настройка проекта. Взять лок (синхронно: сохранение ждёт
        /// секунду-другую) или сохранить без него, сняв «только для чтения».
        /// Автолок, если он включён для этого файла, берёт лок без вопроса.
        /// </summary>
        private static string[] OfferLockForReadOnly(string[] paths)
        {
            if (paths == null || paths.Length == 0 || !GitRepository.IsRepo) return paths;

            var readOnly = new List<string>();
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p) || LfsLockCache.IsTheirs(LfsLockCache.LockOf(p))) continue;
                try
                {
                    var info = new System.IO.FileInfo(System.IO.Path.Combine(GitRepository.ProjectRoot, p));
                    if (info.Exists && info.IsReadOnly) readOnly.Add(p);
                }
                catch { }
            }

            if (readOnly.Count == 0) return paths;

            var mode = (ReadOnlySaveMode)GitSettings.instance.readOnlySave;
            bool coveredByAutoLock = true;
            foreach (var p in readOnly) if (!AutoLockCovers(p)) coveredByAutoLock = false;

            // 0 — взять лок, 1 — не сохранять, 2 — сохранить без лока.
            int choice;
            if (coveredByAutoLock || mode == ReadOnlySaveMode.TakeLock) choice = 0;
            else if (mode == ReadOnlySaveMode.SaveWithoutLock) choice = 2;
            else
                // Строки разбиты вручную и короткие: системное окно переносит длинный
                // текст само и отрывает запятые и точки от слов.
                choice = EditorUtility.DisplayDialogComplex(L.T("Read-Only File"),
                    L.F("The file is marked lockable. Until it is locked,\n" +
                        "git-lfs keeps it read-only:\n\n• {0}" +
                        "\n\nA lock is not required.\n" +
                        "With a lock the team sees the file is taken.\n" +
                        "Without a lock parallel edits will have to be merged.\n\n" +
                        "The default behavior is configured\n" +
                        "in Project Settings, Git section, “Locks” block",
                        string.Join("\n• ", readOnly.ToArray())),
                    L.T("Lock and Save"), L.T("Don't Save"), L.T("Save Without a Lock"));

            var failed = new List<string>();
            foreach (var p in readOnly)
            {
                var full = System.IO.Path.Combine(GitRepository.ProjectRoot, p);

                if (choice == 1) { failed.Add(p); continue; }

                if (choice == 2)
                {
                    try
                    {
                        // git-lfs ставит «только для чтения» только при checkout: снять его
                        // можно, и до следующего checkout файл пишется без вопросов.
                        var info = new System.IO.FileInfo(full);
                        info.Attributes &= ~System.IO.FileAttributes.ReadOnly;
                        Diagnostics.Journal.Notice(L.F("Saved without a lock: {0}", p));
                    }
                    catch (Exception e)
                    {
                        failed.Add(p);
                        Diagnostics.Journal.Warn(L.F("Failed to clear “read-only” on {0}: {1}", p, e.Message));
                    }
                    continue;
                }

                var r = GitProcess.Run(GitRepository.GitExe, "lfs lock -- " + GitOperations.Q(GitRepository.ToGitPath(p)),
                                       GitRepository.RepoRoot, null, 30000);
                bool writable = false;
                try { writable = !new System.IO.FileInfo(full).IsReadOnly; }
                catch { }

                if (!r.Ok || !writable)
                {
                    failed.Add(p);
                    Diagnostics.Journal.Warn(L.F("Lock on {0} was not taken: {1}", p, r.Message));
                }
            }

            if (choice == 0) LfsLockCache.RequestRefresh();
            if (failed.Count == 0) return paths;

            var allowed = new List<string>();
            foreach (var p in paths) if (!failed.Contains(p)) allowed.Add(p);
            return allowed.ToArray();
        }

        // ---------------------------------------------------------- сцена ---

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            var l = LfsLockCache.LockOf(scene.path);
            if (!LfsLockCache.IsTheirs(l)) return;

            var message = L.F("“{0}”: {1}. Edits to this scene will be lost on merge.", scene.name, LfsLockCache.Describe(l));
            foreach (SceneView view in SceneView.sceneViews) view.ShowNotification(new GUIContent(message), 6.0);
            Diagnostics.Journal.Warn(message);
        }

        // ------------------------------------------------ правки снаружи ---

        private static void OnStatusUpdated()
        {
            var fresh = new List<string>();
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in GitStatusCache.Changes)
            {
                if (c.Status != GitFileStatus.Modified || !c.HasAsset) continue;
                current.Add(c.ProjectPath);
                if (!_known.Contains(c.ProjectPath)) fresh.Add(c.ProjectPath);
            }

            _known.Clear();
            _known.UnionWith(current);

            // Первое обновление после старта — не «первая правка», а то, что уже было.
            if (!_primed) { _primed = true; return; }
            if (fresh.Count == 0) return;

            var theirs = new List<string>();
            foreach (var p in fresh)
            {
                var l = LfsLockCache.LockOf(p);
                if (LfsLockCache.IsTheirs(l) && _warned.Add(p)) theirs.Add(p + " — " + LfsLockCache.Describe(l));
            }

            if (theirs.Count > 0)
            {
                EditorApplication.delayCall += () => EditorUtility.DisplayDialog(L.T("File Locked by Someone Else Was Changed"),
                    L.F("• {0}" +
                        "\n\nThe file was changed on your side, but someone else holds the lock. Agree with them before continuing: " +
                        "one of the versions will be lost on merge.",
                        string.Join("\n• ", theirs.ToArray())), L.T("Got It"));
            }

            ScheduleAutoLock(fresh.ToArray());
        }

        // ---------------------------------------------------------- автолок ---

        /// <summary>Автолок по настройке сам взял бы лок на этот файл.</summary>
        private static bool AutoLockCovers(string path)
        {
            switch ((AutoLockMode)GitSettings.instance.autoLock)
            {
                case AutoLockMode.Scenes: return path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
                // Файл только для чтения — значит, он и есть lockable.
                case AutoLockMode.AllLockable: return true;
                default: return false;
            }
        }

        private static void ScheduleAutoLock(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            var mode = (AutoLockMode)GitSettings.instance.autoLock;
            if (mode == AutoLockMode.Off || !GitRepository.IsRepo || !LfsLockCache.HasData || !LfsLockCache.Locks.Verified) return;

            var candidates = new List<string>();
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p) || p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (mode == AutoLockMode.Scenes && !p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) continue;
                if (LfsLockCache.LockOf(p) != null) continue;
                if (!p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(p);
            }
            if (candidates.Count == 0) return;

            EditorApplication.delayCall += () => { _ = AutoLockAsync(candidates, mode); };
        }

        private static async Task AutoLockAsync(List<string> candidates, AutoLockMode mode)
        {
            var targets = candidates;
            if (mode == AutoLockMode.AllLockable)
            {
                var lockable = await LfsLockOps.LockableAsync(candidates);
                targets = new List<string>();
                foreach (var p in candidates) if (lockable.Contains(p)) targets.Add(p);
            }
            if (targets.Count == 0) return;

            var r = await LfsLockOps.LockAsync(targets, true);
            if (r.Ok) Diagnostics.Journal.Notice(L.F("Auto-lock: {0}", string.Join(", ", targets.ToArray())));
            else Diagnostics.Journal.Warn(L.F("Auto-lock failed: {0}", r.Message));
        }
    }
}
