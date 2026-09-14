using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.Diagnostics
{
    /// <summary>
    /// Preferences → Git: личные настройки, которые не должны попадать в
    /// проект, — язык интерфейса и подробный журнал.
    /// </summary>
    internal static class GitPreferencesProvider
    {
        public const string PagePath = "Preferences/Git";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(PagePath, SettingsScope.User)
            {
                label = "Git",
                guiHandler = _ => Draw(),
                keywords = new HashSet<string>(new[] { "git", "language", "язык", "log", "report", "журнал", "отчёт" }) // loc-ignore
            };
        }

        private static void Draw()
        {
            EditorGUIUtility.labelWidth = 240f;

            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(10, 10, 10, 10) }))
            {
                L.LanguageField();
                EditorGUILayout.LabelField(L.T("By default the interface follows the system language."), EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(12f);
                EditorGUILayout.LabelField(L.T("Diagnostics"), EditorStyles.boldLabel);

                Journal.Verbose = EditorGUILayout.Toggle(
                    L.C("Detailed Log", "Also record successful background git commands. Stays on until the editor restarts."),
                    Journal.Verbose);

                EditorGUILayout.LabelField(L.F("Log folder: {0}", Journal.Directory), EditorStyles.wordWrappedMiniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L.T("Open Log Folder"), GUILayout.ExpandWidth(false)))
                        ProblemReportWindow.RevealLogs();
                    if (GUILayout.Button(L.T("Report a Problem…"), GUILayout.ExpandWidth(false)))
                        ProblemReportWindow.Open();
                    GUILayout.FlexibleSpace();
                }
            }
        }
    }
}
