using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Метки изменений в окне Hierarchy и подвал в инспекторе.
    ///
    /// Смысл в том, чтобы изменения были видны там, где работают, а не только
    /// в отдельном окне: человек смотрит на иерархию и сразу знает, какие
    /// объекты он трогал с последнего коммита.
    /// </summary>
    [InitializeOnLoad]
    public static class SceneChangeOverlay
    {
        private static GUIStyle _badge;

        static SceneChangeOverlay()
        {
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItem;
            Editor.finishedDefaultHeaderGUI += OnInspectorHeader;
        }

        private static GitFileStatus AsStatus(SceneChangeKind kind)
        {
            switch (kind)
            {
                case SceneChangeKind.Added: return GitFileStatus.Added;
                case SceneChangeKind.Removed: return GitFileStatus.Deleted;
                default: return GitFileStatus.Modified;
            }
        }

        private static string Letter(SceneChangeKind kind)
        {
            switch (kind)
            {
                case SceneChangeKind.Added: return "A";
                case SceneChangeKind.Removed: return "D";
                default: return "M";
            }
        }

        // ----------------------------------------------------- иерархия ---

        private static void OnHierarchyItem(int instanceId, Rect rect)
        {
            if (!SceneChangeIndex.HasData || Event.current.type != EventType.Repaint) return;

            var info = SceneChangeIndex.For(instanceId);
            if (info == null) return;

            // Метка прижата к правому краю строки: слева там имя объекта, а
            // правая часть строки в иерархии почти всегда пуста.
            var badge = new Rect(rect.xMax - 15f, rect.y + 1f, 13f, 13f);
            EditorGUI.DrawRect(badge, GitPalette.Fill(AsStatus(info.Kind)));
            GUI.Label(badge, Letter(info.Kind), BadgeStyle());
        }

        private static GUIStyle BadgeStyle()
        {
            if (_badge == null)
            {
                _badge = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = 9,
                    normal = { textColor = Color.black }
                };
            }
            return _badge;
        }

        // ---------------------------------------------------- инспектор ---

        private static void OnInspectorHeader(Editor editor)
        {
            if (!SceneChangeIndex.HasData) return;
            if (editor == null || editor.targets == null || editor.targets.Length != 1) return;

            // Для компонентов этот хук не срабатывает вовсе: он вызывается только
            // для шапки самого объекта. Метка компонента живёт в ComponentHeaderBadge.
            var go = editor.target as GameObject;
            if (go == null) return;

            var info = SceneChangeIndex.For(go.GetInstanceID());
            if (info == null) return;

            var title = info.Kind == SceneChangeKind.Added ? L.T("Object added since the last commit")
                      : info.Kind == SceneChangeKind.Removed ? L.T("Object removed in the last commit")
                      : L.T("Object modified since the last commit");

            var body = title + "\n" + info.Summary;

            // Сравнение идёт с файлом на диске, а не с тем, что в памяти:
            // умолчать об этом значило бы врать про несохранённую сцену.
            if (SceneChangeIndex.HasUnsavedScene)
                body += "\n\n" + L.T("The scene has unsaved changes — comparing with the last save.");

            EditorGUILayout.Space(2f);
            EditorGUILayout.HelpBox(body, MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(L.T("Open Git Window"), EditorStyles.miniButton, GUILayout.Width(140f)))
                    GitWindow.Open();
            }
        }
    }
}
