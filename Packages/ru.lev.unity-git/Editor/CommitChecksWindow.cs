using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Список находок перед коммитом.
    ///
    /// Ничего не запрещает: называет последствие и показывает, где смотреть.
    /// Решение остаётся за человеком — он может знать про свой случай больше,
    /// чем набор эвристик.
    /// </summary>
    public sealed class CommitChecksWindow : EditorWindow, ILocalizedWindow
    {
        private List<CommitCheck> _checks = new List<CommitCheck>();
        private Vector2 _scroll;

        public static void Open(List<CommitCheck> checks)
        {
            var w = GetWindow<CommitChecksWindow>(true, L.T("Pre-Commit Checks"), true);
            w.minSize = new Vector2(560f, 300f);
            w._checks = checks ?? new List<CommitCheck>();
            w.Show();
        }

        /// <summary>Заголовок задаётся один раз при открытии — при смене языка ставим заново.</summary>
        void ILocalizedWindow.OnLanguageChanged()
        {
            titleContent = new GUIContent(L.T("Pre-Commit Checks"));
            Repaint();
        }

        private void OnGUI()
        {
            if (_checks.Count == 0)
            {
                EditorGUILayout.Space(10f);
                EditorGUILayout.LabelField(L.T("No issues."), EditorStyles.boldLabel);
                return;
            }

            int errors = 0;
            foreach (var c in _checks) if (c.Severity == CheckSeverity.Error) errors++;

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(
                L.F("Errors: {0}, warnings: {1}", errors, _checks.Count - errors),
                EditorStyles.boldLabel);

            EditorGUILayout.LabelField(
                L.T("All of this breaks not for you, but for whoever pulls."),
                EditorStyles.miniLabel);

            EditorGUILayout.Space(4f);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            foreach (var check in _checks)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var badge = GUILayoutUtility.GetRect(14f, 14f, GUILayout.Width(14f), GUILayout.Height(14f));
                        if (Event.current.type == EventType.Repaint)
                            EditorGUI.DrawRect(badge, GitPalette.Fill(
                                check.Severity == CheckSeverity.Error
                                    ? GitFileStatus.Deleted
                                    : GitFileStatus.Modified));

                        EditorGUILayout.LabelField(check.Title, EditorStyles.boldLabel);
                    }

                    EditorGUILayout.LabelField(check.Detail, EditorStyles.wordWrappedMiniLabel);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(check.ProjectPath, EditorStyles.miniLabel);
                        GUILayout.FlexibleSpace();

                        var path = check.ProjectPath;
                        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(path)))
                        {
                            if (GUILayout.Button(L.T("Show"), EditorStyles.miniButton, GUILayout.Width(90f)))
                            {
                                var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                                if (obj != null) EditorGUIUtility.PingObject(obj);
                                else EditorUtility.RevealInFinder(
                                    System.IO.Path.Combine(GitRepository.ProjectRoot, path));
                            }
                        }
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }
    }
}
