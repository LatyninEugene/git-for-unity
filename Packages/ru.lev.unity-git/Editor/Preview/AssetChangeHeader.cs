using System;
using Lev.Git.UI;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.Preview
{
    /// <summary>
    /// Строка в шапке инспектора изменённого ассета: «Изменён с последнего
    /// коммита · Сравнить · История». Вопрос «что я тут поменял» возникает,
    /// когда смотришь на сам ассет, — ответ должен быть там же.
    ///
    /// Сцены сюда не входят: у них своя сводка по объектам. Скрипты тоже —
    /// их сравнивают построчно, и в окне Git для этого всё есть.
    /// </summary>
    [InitializeOnLoad]
    internal static class AssetChangeHeader
    {
        static AssetChangeHeader()
        {
            Editor.finishedDefaultHeaderGUI += OnHeader;
        }

        private static void OnHeader(Editor editor)
        {
            if (editor == null || editor.targets.Length != 1 || !GitRepository.IsRepo) return;

            var target = editor.target;
            if (target == null || !EditorUtility.IsPersistent(target)) return;
            if (!AssetDatabase.IsMainAsset(target) && !(target is AssetImporter)) return;

            var path = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
            if (AssetLoaders.Find(path) == null && !ThumbnailPresenter.Supports(path)) return;

            var status = GitStatusCache.GetStatus(path);
            if (status != GitFileStatus.Modified && status != GitFileStatus.Renamed) return;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(L.C("Modified since last commit",
                    "In the working tree this asset or its .meta differs from the last commit"), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                if (GUILayout.Button(L.C("Compare", "Last commit versus the current state — as a view, an inspector and a list of changes"),
                        EditorStyles.miniButtonLeft, GUILayout.Width(72f)))
                    AssetHistoryWindow.Open(path, true);

                if (GUILayout.Button(L.C("History", "All versions of the asset by commit"), EditorStyles.miniButtonRight, GUILayout.Width(64f)))
                    AssetHistoryWindow.Open(path, false);
            }
        }
    }
}
