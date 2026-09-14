using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>
    /// Конфликт в коде и прочем тексте. Git сводит сам всё, что правила одна
    /// сторона; здесь — только места, которые правили обе: у каждого три версии
    /// с подсветкой синтаксиса и выбор, что взять. Итог пишется, когда решено всё.
    /// </summary>
    public sealed partial class ConflictResolverWindow
    {
        private const float CodeLine = 16f;
        private const int StableContext = 3;

        private static bool _textShowAll;
        private static bool _textMoreOpen;
        private static GUIStyle _code, _codeDim;

        private async void LoadText(FileState s)
        {
            s.Loading = true;
            Repaint();

            try
            {
                s.Text = await GitConflicts.TextMergeAsync(s.File);
                if (s.Text == null) s.Error = L.T("Couldn't read the file versions — resolve the conflict with an external tool or pick a whole side.");
            }
            catch (Exception e)
            {
                s.Error = L.F("Merge failed: {0}", e.Message);
            }
            finally
            {
                s.Loading = false;
                Repaint();
            }
        }

        private static void EnsureCodeStyles()
        {
            if (_code != null) return;

            _code = new GUIStyle(EditorStyles.label)
            {
                richText = true,
                font = Ui.Mono,
                fontSize = 11,
                clipping = TextClipping.Clip,
                wordWrap = false,
                padding = new RectOffset(4, 4, 0, 0),
                alignment = TextAnchor.MiddleLeft
            };

            _codeDim = new GUIStyle(_code) { fontStyle = FontStyle.Italic };
            _codeDim.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.55f, 0.55f, 0.55f) : new Color(0.45f, 0.45f, 0.45f);
        }

        private void DrawTextMerge(FileState s)
        {
            EnsureCodeStyles();
            var f = s.File;
            var state = GitConflicts.State;

            if (s.Loading && s.Text == null)
            {
                EditorGUILayout.LabelField(L.T("Merging three versions…"), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            if (s.Error != null || s.Text == null)
            {
                if (s.Error != null) EditorGUILayout.HelpBox(s.Error, MessageType.Warning);
                DrawTextAlternatives(f, true);
                return;
            }

            var m = s.Text;
            int conflicts = m.Conflicts, unresolved = m.Unresolved;

            EditorGUILayout.LabelField(conflicts == 0
                    ? L.T("Git merged the file itself: the sides changed different places. Review the result and write it.")
                    : L.F("Places changed by both sides: {0}, unresolved: {1}. Git merged the other changes itself.", conflicts, unresolved),
                EditorStyles.wordWrappedMiniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(conflicts == 0))
                {
                    if (GUILayout.Button(new GUIContent(L.F("« All: {0}", state.MineLabel), L.T("Resolve all unresolved places with your version")), GUILayout.Width(150f)))
                        m.ResolveAll(TextMergeChoice.Mine);
                    if (GUILayout.Button(new GUIContent(L.F("All: {0} »", state.TheirsLabel), L.T("Resolve all unresolved places with their version")), GUILayout.Width(150f)))
                        m.ResolveAll(TextMergeChoice.Theirs);
                    if (GUILayout.Button(new GUIContent(L.T("Reset"), L.T("Make all places unresolved again")), GUILayout.Width(80f)))
                        m.Reset();
                }

                GUILayout.Space(8f);
                _textShowAll = GUILayout.Toggle(_textShowAll, new GUIContent(L.T("Whole File"), L.T("Also show the parts git merged itself, in full")), GUILayout.Width(80f));
                GUILayout.FlexibleSpace();

                var label = unresolved > 0 ? L.F("Write (unresolved: {0})", unresolved) : L.T("Write Result");
                using (new EditorGUI.DisabledScope(unresolved > 0))
                    if (GUILayout.Button(new GUIContent(label, L.T("Write the result and mark the conflict resolved")), GUILayout.Width(190f)))
                        WriteText(s);
            }

            _textMoreOpen = EditorGUILayout.Foldout(_textMoreOpen, L.T("Other Ways"), true);
            if (_textMoreOpen) DrawTextAlternatives(f, false);

            EditorGUILayout.Space(4f);
            _bodyScroll = EditorGUILayout.BeginScrollView(_bodyScroll);

            var language = SyntaxHighlight.Detect(f.ProjectPath ?? f.GitPath);
            var syntax = SyntaxState.None;
            float width = position.width - ListWidth - 40f;
            int number = 0;

            for (int i = 0; i < m.Chunks.Count; i++)
            {
                var chunk = m.Chunks[i];
                if (!chunk.Conflict)
                {
                    DrawStable(chunk, i > 0, i < m.Chunks.Count - 1, language, ref syntax, width);
                    continue;
                }

                number++;
                DrawConflictChunk(chunk, number, state, language, ref syntax, width);
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.EndScrollView();
        }

        /// <summary>Участок, который git свёл сам. Длинный сворачивается до строк рядом с конфликтами.</summary>
        private static void DrawStable(TextMergeChunk chunk, bool conflictBefore, bool conflictAfter, SyntaxLanguage language,
                                       ref SyntaxState syntax, float width)
        {
            var lines = chunk.Lines;
            bool fold = !_textShowAll && lines.Count > StableContext * 2 + 2;

            int head = fold ? (conflictBefore ? StableContext : 0) : lines.Count;
            int tail = fold ? (conflictAfter ? StableContext : 0) : 0;

            for (int i = 0; i < lines.Count; i++)
            {
                // Состояние синтаксиса считается и по скрытым строкам: иначе
                // комментарий, открытый выше, не окрасил бы строки ниже.
                var rich = SyntaxRich.Line(lines[i], language, ref syntax);

                bool visible = !fold || i < head || i >= lines.Count - tail;
                if (visible)
                {
                    var rect = GUILayoutUtility.GetRect(width, CodeLine, GUILayout.ExpandWidth(true));
                    GUI.Label(rect, rich, _code);
                }
                else if (i == head)
                {
                    var rect = GUILayoutUtility.GetRect(width, CodeLine, GUILayout.ExpandWidth(true));
                    GUI.Label(rect, L.F("⋯ lines without conflicts: {0}", lines.Count - head - tail), _codeDim);
                }
            }
        }

        private void DrawConflictChunk(TextMergeChunk chunk, int number, GitOperationState state, SyntaxLanguage language,
                                       ref SyntaxState syntax, float width)
        {
            var resolved = chunk.Resolved();
            var mineColor = new Color(0.35f, 0.6f, 0.95f, 0.14f);
            var baseColor = new Color(0.5f, 0.5f, 0.5f, 0.10f);
            var theirsColor = new Color(0.95f, 0.6f, 0.3f, 0.14f);
            var resultColor = new Color(0.4f, 0.8f, 0.5f, 0.14f);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(L.F("Conflict {0}", number), EditorStyles.boldLabel, GUILayout.ExpandWidth(false));

                    var status = new GUIStyle(EditorStyles.miniLabel);
                    status.normal.textColor = resolved == null ? GitPalette.Text(GitFileStatus.Conflicted) : GitPalette.Text(GitFileStatus.Added);
                    GUILayout.Label(resolved == null ? L.T("unresolved") : ChoiceName(chunk.Choice, state), status, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();

                    ChoiceButton(chunk, TextMergeChoice.Mine, state.MineLabel, EditorStyles.miniButtonLeft);
                    ChoiceButton(chunk, TextMergeChoice.Theirs, state.TheirsLabel, EditorStyles.miniButtonMid);
                    ChoiceButton(chunk, TextMergeChoice.MineThenTheirs, L.T("Both: Mine, Theirs"), EditorStyles.miniButtonMid);
                    ChoiceButton(chunk, TextMergeChoice.TheirsThenMine, L.T("Both: Theirs, Mine"), EditorStyles.miniButtonMid);
                    ChoiceButton(chunk, TextMergeChoice.Base, L.T("Base"), EditorStyles.miniButtonMid);

                    bool manual = chunk.Choice == TextMergeChoice.Manual;
                    if (GUILayout.Toggle(manual, L.T("Manual"), EditorStyles.miniButtonRight, GUILayout.Width(70f)) && !manual)
                    {
                        var start = resolved ?? chunk.Mine;
                        chunk.Manual = string.Join("\n", start.ToArray());
                        chunk.Choice = TextMergeChoice.Manual;
                    }
                }

                float column = Mathf.Max(120f, (width - 16f) / 3f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var mineState = syntax;
                    var baseState = syntax;
                    var theirsState = syntax;
                    DrawSide(state.MineLabel, chunk.Mine, mineColor, column, language, ref mineState);
                    DrawSide(L.T("Base"), chunk.Base, baseColor, column, language, ref baseState);
                    DrawSide(state.TheirsLabel, chunk.Theirs, theirsColor, column, language, ref theirsState);
                }

                if (chunk.Choice == TextMergeChoice.Manual)
                {
                    EditorGUILayout.LabelField(L.T("Result — edit as needed:"), EditorStyles.miniBoldLabel);
                    int lines = Mathf.Max(3, TextMergeChunk.SplitLines(chunk.Manual ?? string.Empty).Count + 1);
                    var area = new GUIStyle(EditorStyles.textArea) { font = Ui.Mono, fontSize = 11, wordWrap = false };
                    chunk.Manual = EditorGUILayout.TextArea(chunk.Manual ?? string.Empty, area, GUILayout.Height(lines * CodeLine + 6f));
                }
                else if (resolved != null)
                {
                    EditorGUILayout.LabelField(L.T("Result:"), EditorStyles.miniBoldLabel);
                    var resultState = syntax;
                    foreach (var line in resolved)
                    {
                        var rect = GUILayoutUtility.GetRect(width, CodeLine, GUILayout.ExpandWidth(true));
                        if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(rect, resultColor);
                        GUI.Label(rect, SyntaxRich.Line(line, language, ref resultState), _code);
                    }
                    if (resolved.Count == 0) EditorGUILayout.LabelField(L.T("empty — the place is removed"), _codeDim);
                }
            }

            // Дальше файл продолжается той версией, что вошла в итог.
            foreach (var line in resolved ?? chunk.Mine)
                SyntaxHighlight.Tokenize(language, line ?? string.Empty, ref syntax);
        }

        private static void DrawSide(string caption, List<string> lines, Color tint, float width, SyntaxLanguage language, ref SyntaxState syntax)
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(width)))
            {
                GUILayout.Label(caption, EditorStyles.miniBoldLabel);

                if (lines.Count == 0)
                {
                    GUILayout.Label(L.T("— empty —"), _codeDim, GUILayout.Width(width));
                    return;
                }

                foreach (var line in lines)
                {
                    var rect = GUILayoutUtility.GetRect(width, CodeLine, GUILayout.Width(width));
                    if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(rect, tint);
                    GUI.Label(rect, new GUIContent(SyntaxRich.Line(line, language, ref syntax), line), _code);
                }
            }
        }

        private static void ChoiceButton(TextMergeChunk chunk, TextMergeChoice choice, string label, GUIStyle style)
        {
            bool on = chunk.Choice == choice;
            if (GUILayout.Toggle(on, label, style, GUILayout.ExpandWidth(false)) && !on)
            {
                chunk.Choice = choice;
                chunk.Manual = null;
            }
        }

        private static string ChoiceName(TextMergeChoice choice, GitOperationState state)
        {
            switch (choice)
            {
                case TextMergeChoice.Mine: return L.F("taken: {0}", state.MineLabel);
                case TextMergeChoice.Theirs: return L.F("taken: {0}", state.TheirsLabel);
                case TextMergeChoice.MineThenTheirs: return L.T("both: mine first");
                case TextMergeChoice.TheirsThenMine: return L.T("both: theirs first");
                case TextMergeChoice.Base: return L.T("base kept");
                default: return L.T("manual edit");
            }
        }

        /// <summary>Что делать, если слияние по местам не подходит: внешний инструмент или сторона целиком.</summary>
        private void DrawTextAlternatives(ConflictFile f, bool prominent)
        {
            if (prominent)
                EditorGUILayout.HelpBox(L.T("You can resolve the conflict in an external tool: the configured git mergetool, or the IDE if there is none. Once the markers are removed, mark the file resolved."), MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent(L.T("Open in mergetool"), L.T("The window waits until the tool closes"))))
                    RunFileOp("mergetool", () => GitConflicts.OpenExternalAsync(f));
                if (GUILayout.Button(new GUIContent(L.T("Mark Resolved"), L.T("The file on disk has no markers anymore — mark it resolved as is"))))
                    RunFileOp(L.T("Marking"), () => GitConflicts.MarkResolvedAsync(f));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L.F("« {0} — Whole File", GitConflicts.State.MineLabel))) Take(f, MergeSide.Mine);
                if (GUILayout.Button(L.F("{0} — Whole File »", GitConflicts.State.TheirsLabel))) Take(f, MergeSide.Theirs);
            }
        }

        private void WriteText(FileState s)
        {
            var f = s.File;
            var text = s.Text.Build();
            bool bom = s.Text.Bom;

            RunFileOp(L.T("Writing result"), async () =>
            {
                var r = await GitConflicts.WriteResolvedAsync(f, text, bom);
                if (r.Ok && !string.IsNullOrEmpty(f.ProjectPath) && f.ProjectPath.StartsWith("Assets/", StringComparison.Ordinal))
                    AssetDatabase.ImportAsset(f.ProjectPath, ImportAssetOptions.ForceUpdate);
                return r;
            });
        }
    }
}
