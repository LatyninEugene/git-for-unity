using System;
using System.Collections.Generic;
using Lev.Git.Preview;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>
    /// Вид конфликтующего ассета над списком правок: база, мои, их и итог.
    /// Сцены сюда не входят — у них иерархия и инспектор объектов.
    /// </summary>
    public sealed partial class ConflictResolverWindow
    {
        private const float AssetPreviewHeight = 200f;

        private static bool _showAssetPreview = true;
        private readonly Dictionary<string, ConflictPreview> _previews = new Dictionary<string, ConflictPreview>(StringComparer.Ordinal);

        private void DrawAssetPreview(FileState s)
        {
            var f = s.File;
            if (f.IsMeta || string.IsNullOrEmpty(f.ProjectPath)) return;
            if (f.ProjectPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return;
            if (!ConflictPreview.Supports(f.ProjectPath)) return;

            _showAssetPreview = EditorGUILayout.Foldout(_showAssetPreview,
                s.Merge != null ? L.T("View: Base, Mine, Theirs and Result") : L.T("View: Base, Mine, Theirs"), true);
            if (!_showAssetPreview) return;

            ConflictPreview preview;
            if (!_previews.TryGetValue(f.GitPath, out preview) || !preview.Matches(f))
            {
                if (preview != null) preview.Dispose();
                preview = new ConflictPreview(f, Repaint);
                _previews[f.GitPath] = preview;
            }

            if (s.Merge != null && s.Built != null) preview.SetResult(s.Built, s.BuiltVersion);

            var state = GitConflicts.State;
            var rect = GUILayoutUtility.GetRect(0f, AssetPreviewHeight, GUILayout.ExpandWidth(true));
            preview.Draw(rect, state.MineLabel, state.TheirsLabel);
            EditorGUILayout.Space(4f);
        }

        private void DropPreviews()
        {
            foreach (var p in _previews.Values) p.Dispose();
            _previews.Clear();
        }
    }
}
