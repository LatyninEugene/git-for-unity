using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>
    /// Сцена и префаб в окне слияния — так, как их видит Unity.
    ///
    /// Сверху иерархия в три колонки: моя версия, итог, их. Строки выровнены,
    /// и объект стоит в одной строке во всех колонках, даже если на какой-то
    /// стороне его нет. Снизу — инспектор выбранного объекта в тех же
    /// колонках: компоненты, их свойства и стрелки «взять» на каждом уровне.
    /// Итоговая колонка — это и есть сцена, которую человек собирает.
    /// </summary>
    public sealed partial class ConflictResolverWindow
    {
        private const long SettingsRow = long.MinValue;

        private float _treeHeight = 230f;
        private bool _draggingSplit;
        private bool _onlyChangedTree = true;
        private Vector2 _treeScroll, _inspectorScroll;

        private readonly HashSet<string> _treeCollapsed = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _componentFolded = new HashSet<string>(StringComparer.Ordinal);

        private static bool IsScene(FileState s)
        {
            return s.MineOutline != null && (s.MineOutline.Nodes.Count > 0 || s.TheirsOutline.Nodes.Count > 0);
        }

        private void DrawSceneMerge(FileState s, float width)
        {
            var o = s.Outline;
            if (o == null) return;

            var state = GitConflicts.State;
            float column = Mathf.Max(120f, (width - Gutter * 2f) / 3f);

            if (!s.HasSelection) SelectDefault(s);

            DrawColumnHeads(state, column);

            // ---- иерархия ----
            _treeScroll = EditorGUILayout.BeginScrollView(_treeScroll, GUILayout.Height(_treeHeight));

            var rows = o.Flatten(id => !_treeCollapsed.Contains(s.File.GitPath + "|" + id), _onlyChangedTree);
            if (rows.Count == 0 && o.Loose.Count == 0)
                EditorGUILayout.LabelField(L.T("The sides didn't change anything in objects."), EditorStyles.centeredGreyMiniLabel);

            foreach (var row in rows) DrawTreeRow(s, row, column);
            if (o.Loose.Count > 0) DrawSettingsRow(s, column);

            EditorGUILayout.EndScrollView();

            DrawSplitter();

            // ---- инспектор ----
            _inspectorScroll = EditorGUILayout.BeginScrollView(_inspectorScroll);
            DrawObjectInspector(s, column);
            DrawIssues(s);
            EditorGUILayout.Space(10f);
            EditorGUILayout.EndScrollView();
        }

        private void SelectDefault(FileState s)
        {
            var rows = s.Outline.Flatten(_ => true, true);
            foreach (var r in rows)
                foreach (var c in s.Outline.ByNode.ContainsKey(r.Id) ? s.Outline.ByNode[r.Id] : new List<YamlMergeConflict>())
                    if (!c.IsResolved) { Select(s, r.Id); return; }

            foreach (var r in rows)
                if (s.Outline.ByNode.ContainsKey(r.Id)) { Select(s, r.Id); return; }

            if (s.Outline.Loose.Count > 0) Select(s, SettingsRow);
        }

        private void Select(FileState s, long row)
        {
            s.SelectedRow = row;
            s.HasSelection = true;
            _inspectorScroll = Vector2.zero;
            DropViews();
            Repaint();
        }

        private void DrawSplitter()
        {
            var rect = GUILayoutUtility.GetRect(0f, 7f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(rect.x, rect.y + 3f, rect.width, 1f), new Color(0f, 0f, 0f, 0.35f));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);

            var ev = Event.current;
            if (ev.type == EventType.MouseDown && rect.Contains(ev.mousePosition)) { _draggingSplit = true; ev.Use(); }
            else if (ev.type == EventType.MouseDrag && _draggingSplit)
            {
                _treeHeight = Mathf.Clamp(_treeHeight + ev.delta.y, 80f, Mathf.Max(120f, position.height - 220f));
                ev.Use();
                Repaint();
            }
            else if (ev.rawType == EventType.MouseUp) _draggingSplit = false;
        }

        // --------------------------------------------------------- иерархия ---

        private void DrawTreeRow(FileState s, MergeOutlineRow row, float column)
        {
            var o = s.Outline;
            var changes = o.Subtree(row.Id);

            var rect = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));
            var mine = new Rect(rect.x, rect.y, column, rect.height);
            var result = new Rect(mine.xMax + Gutter, rect.y, column, rect.height);
            var theirs = new Rect(result.xMax + Gutter, rect.y, column, rect.height);

            bool mineChanged = false, theirsChanged = false;
            int open = 0;
            foreach (var c in changes)
            {
                if (c.MineChanged) mineChanged = true;
                if (c.TheirsChanged) theirsChanged = true;
                if (!c.IsResolved) open++;
            }

            bool selected = s.HasSelection && s.SelectedRow == row.Id;

            if (Event.current.type == EventType.Repaint)
            {
                if (selected) EditorGUI.DrawRect(rect, new Color(0.24f, 0.48f, 0.9f, 0.30f));
                if (mineChanged) EditorGUI.DrawRect(mine, MineTint(0.14f));
                if (theirsChanged) EditorGUI.DrawRect(theirs, TheirsTint(0.14f));
                if (open > 0) EditorGUI.DrawRect(result, new Color(0.9f, 0.25f, 0.25f, 0.22f));
                else if (changes.Count > 0) EditorGUI.DrawRect(result, ResultTint(changes, 0.12f));
            }

            var key = s.File.GitPath + "|" + row.Id;
            bool expanded = !_treeCollapsed.Contains(key);
            bool toggled = false;

            toggled |= NodeCell(mine, o, s.MineOutline, row, ref expanded);
            toggled |= NodeCell(result, o, s.ResultOutline, row, ref expanded);
            toggled |= NodeCell(theirs, o, s.TheirsOutline, row, ref expanded);

            if (toggled)
            {
                if (expanded) _treeCollapsed.Remove(key); else _treeCollapsed.Add(key);
            }

            if (open > 0)
            {
                var old = GUI.color;
                GUI.color = GitPalette.Text(GitFileStatus.Conflicted);
                GUI.Label(new Rect(result.xMax - 22f, result.y, 20f, result.height), "!", EditorStyles.boldLabel);
                GUI.color = old;
            }

            if (changes.Count > 0)
            {
                var takeMine = new Rect(mine.xMax + 3f, rect.y + 1f, Gutter - 6f, 18f);
                var takeTheirs = new Rect(result.xMax + 3f, rect.y + 1f, Gutter - 6f, 18f);
                if (GUI.Button(takeMine, new GUIContent("»", L.T("The object with all descendants — as on my side")), _arrow)) Take(changes, MergeSide.Mine);
                if (GUI.Button(takeTheirs, new GUIContent("«", L.T("The object with all descendants — as on their side")), _arrow)) Take(changes, MergeSide.Theirs);
            }

            var ev = Event.current;
            if (rect.Contains(ev.mousePosition))
            {
                if (ev.type == EventType.MouseDown && ev.button == 0)
                {
                    var id = row.Id;
                    _pending = () => Select(s, id);
                    ev.Use();
                }
                else if (ev.type == EventType.ContextClick && changes.Count > 0)
                {
                    ShowTakeMenu(changes, L.T("Whole Object"));
                    ev.Use();
                }
            }
        }

        /// <summary>Ячейка иерархии. Возвращает true, если щёлкнули по треугольнику.</summary>
        private bool NodeCell(Rect cell, MergeOutline o, SceneOutline side, MergeOutlineRow row, ref bool expanded)
        {
            bool unpacked;
            var node = o.NodeOn(side, row.Id, out unpacked);

            float x = cell.x + 4f + row.Depth * 14f;
            bool toggled = false;

            if (row.HasChildren)
            {
                bool now = EditorGUI.Foldout(new Rect(x, cell.y + 1f, 14f, cell.height - 2f), expanded, GUIContent.none, false);
                if (now != expanded) { expanded = now; toggled = true; }
            }
            x += 14f;

            if (node == null)
            {
                GUI.Label(new Rect(x, cell.y, cell.xMax - x, cell.height), "—", _cellDim);
                return toggled;
            }

            var icon = node.IsPrefabInstance ? PrefabIcon : SceneHistoryIcons.GameObjectIcon;
            if (icon != null) GUI.DrawTexture(new Rect(x, cell.y + 2f, 16f, 16f), icon, ScaleMode.ScaleToFit);
            x += 19f;

            var name = NodeName(node);
            if (unpacked) name += "   · " + L.Tc("prefab instance", "unpacked");

            var style = node.Active ? EditorStyles.label : _cellDim;
            GUI.Label(new Rect(x, cell.y + 1f, cell.xMax - x - 20f, cell.height - 2f),
                      new GUIContent(name, node.IsPrefabInstance ? L.F("Prefab instance {0}", PrefabPath(node)) : name), style);
            return toggled;
        }

        private void DrawSettingsRow(FileState s, float column)
        {
            var o = s.Outline;
            var rect = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));
            bool selected = s.HasSelection && s.SelectedRow == SettingsRow;

            int open = 0;
            foreach (var c in o.Loose) if (!c.IsResolved) open++;

            if (Event.current.type == EventType.Repaint)
            {
                if (selected) EditorGUI.DrawRect(rect, new Color(0.24f, 0.48f, 0.9f, 0.30f));
                if (open > 0) EditorGUI.DrawRect(new Rect(rect.x + column + Gutter, rect.y, column, rect.height), new Color(0.9f, 0.25f, 0.25f, 0.22f));
            }

            GUI.Label(new Rect(rect.x + 22f, rect.y + 1f, rect.width - 30f, 18f),
                      L.F("Scene settings and service documents · changes: {0}", o.Loose.Count) + (open > 0 ? " · " + L.F("unresolved: {0}", open) : string.Empty),
                      _cellDim);

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                _pending = () => Select(s, SettingsRow);
                Event.current.Use();
            }
        }

        // -------------------------------------------------------- инспектор ---

        private void DrawObjectInspector(FileState s, float column)
        {
            var o = s.Outline;
            if (!s.HasSelection)
            {
                EditorGUILayout.LabelField(L.T("Select an object in the Hierarchy."), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            if (s.SelectedRow == SettingsRow)
            {
                EditorGUILayout.LabelField(L.T("Scene Settings"), EditorStyles.boldLabel);
                foreach (var c in o.Loose) DrawChange(s, c, column);
                return;
            }

            long row = s.SelectedRow;
            bool u1, u2, u3;
            var mineNode = o.NodeOn(s.MineOutline, row, out u1);
            var resultNode = o.NodeOn(s.ResultOutline, row, out u2);
            var theirsNode = o.NodeOn(s.TheirsOutline, row, out u3);
            var any = resultNode ?? mineNode ?? theirsNode ?? o.NodeOn(s.BaseOutline, row, out u1);

            if (any == null)
            {
                EditorGUILayout.LabelField(L.T("Object not found."), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            EditorGUILayout.LabelField(NodeName(any), EditorStyles.largeLabel);

            var pair = o.UnpackOf(row);
            if (pair != null) DrawUnpackBanner(s, pair, any);

            // ---- сам объект ----
            var own = o.ChangesOf(row);
            DrawSectionHeader(s, column, "obj:" + row,
                mineNode, resultNode, theirsNode,
                n => n.IsPrefabInstance ? L.T("Prefab Instance") : "GameObject",
                n => n.IsPrefabInstance ? PrefabIcon : SceneHistoryIcons.GameObjectIcon,
                own, null);

            if (!_componentFolded.Contains(s.File.GitPath + "|obj:" + row))
                foreach (var c in own) DrawChange(s, c, column);

            // ---- компоненты ----
            foreach (var comp in o.ComponentsOf(row))
            {
                var changes = o.ChangesOf(comp);
                if (_onlyChangedTree && changes.Count == 0) continue;
                DrawComponent(s, row, comp, changes, column);
            }
        }

        private void DrawUnpackBanner(FileState s, UnpackPair pair, OutlineNode node)
        {
            var state = GitConflicts.State;
            bool theirsUnpacked = pair.Side == MergeSide.Theirs;

            EditorGUILayout.HelpBox(theirsUnpacked
                ? L.F("On their side, the prefab instance “{0}” was unpacked into regular objects, while on my side it stayed an instance. These are two different ways to store an object, and they can't be merged property by property: the result is either the prefab instance with its overrides or the unpacked objects.", NodeName(node))
                : L.F("On my side, the prefab instance “{0}” was unpacked into regular objects, while on their side it stayed an instance. These are two different ways to store an object, and they can't be merged property by property: the result is either the prefab instance with its overrides or the unpacked objects.", NodeName(node)),
                MessageType.Info);

            var changes = s.Outline.Subtree(pair.PrefabId);
            using (new EditorGUILayout.HorizontalScope())
            {
                var prefabSide = theirsUnpacked ? MergeSide.Mine : MergeSide.Theirs;
                if (GUILayout.Button((prefabSide == MergeSide.Mine ? "» " : "") + L.F("Keep Prefab Instance ({0})", prefabSide == MergeSide.Mine ? state.MineLabel : state.TheirsLabel) +
                                     (prefabSide == MergeSide.Theirs ? " «" : "")))
                    Take(changes, prefabSide);

                if (GUILayout.Button((pair.Side == MergeSide.Mine ? "» " : "") + L.F("Take Unpacked Objects ({0})", pair.Side == MergeSide.Mine ? state.MineLabel : state.TheirsLabel) +
                                     (pair.Side == MergeSide.Theirs ? " «" : "")))
                    Take(changes, pair.Side);
            }
            EditorGUILayout.Space(4f);
        }

        private void DrawComponent(FileState s, long row, long comp, List<YamlMergeConflict> changes, float column)
        {
            var mineDoc = DocIn(s.MineScene, comp);
            var resultDoc = DocIn(s.ResultScene, comp);
            var theirsDoc = DocIn(s.TheirsScene, comp);
            var any = resultDoc ?? mineDoc ?? theirsDoc ?? DocIn(s.BaseScene, comp);
            if (any == null) return;

            var foldKey = "comp:" + comp;
            DrawSectionHeader(s, column, foldKey, mineDoc, resultDoc, theirsDoc,
                d => SceneHistoryIcons.DisplayName(d.TypeName, d),
                d => SceneHistoryIcons.ComponentIcon(d.TypeName, d),
                changes, any);

            if (_componentFolded.Contains(s.File.GitPath + "|" + foldKey)) return;

            // Итоговая колонка инспектора собирается из итогового документа.
            // Если компонента нет нигде, кроме базы, показывать нечего.
            if (mineDoc == null && resultDoc == null && theirsDoc == null) { foreach (var c in changes) DrawChange(s, c, column); return; }

            var viewKey = s.File.GitPath + "|view|" + comp;
            ComponentMergeView view;
            if (!_views.TryGetValue(viewKey, out view))
            {
                var type = SceneHistoryIcons.ResolveComponent(any.TypeName, any);
                view = new ComponentMergeView(type, new[] { mineDoc, resultDoc, theirsDoc });
                _views[viewKey] = view;
            }

            HashSet<string> drawn = null;
            if (view.Ready)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        drawn = view.Draw(column, Gutter, _onlyDifferent,
                            path => ChangesForProperty(changes, path),
                            (list, side) => Take(list, side));
                    }
                }
            }

            // Правки, которым в инспекторе строки не нашлось, — отдельными строками:
            // скрытое поле всё равно попадёт в файл, и выбирать его тоже нужно.
            foreach (var c in changes)
            {
                if (c.IsDocument) continue;
                if (drawn != null && drawn.Contains(RootOf(c.Key))) continue;
                DrawChange(s, c, column);
            }
        }

        private void DrawSectionHeader<T>(FileState s, float column, string foldKey, T mine, T result, T theirs,
                                          Func<T, string> title, Func<T, Texture> icon,
                                          List<YamlMergeConflict> changes, UnityDocument doc) where T : class
        {
            var rect = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            var mineRect = new Rect(rect.x, rect.y, column, rect.height);
            var resultRect = new Rect(mineRect.xMax + Gutter, rect.y, column, rect.height);
            var theirsRect = new Rect(resultRect.xMax + Gutter, rect.y, column, rect.height);

            bool mineChanged = false, theirsChanged = false;
            int open = 0;
            foreach (var c in changes)
            {
                if (c.MineChanged) mineChanged = true;
                if (c.TheirsChanged) theirsChanged = true;
                if (!c.IsResolved) open++;
            }

            if (Event.current.type == EventType.Repaint)
            {
                var head = new Color(0.5f, 0.5f, 0.5f, 0.14f);
                EditorGUI.DrawRect(mineRect, mineChanged ? MineTint(0.2f) : head);
                EditorGUI.DrawRect(theirsRect, theirsChanged ? TheirsTint(0.2f) : head);
                EditorGUI.DrawRect(resultRect, open > 0 ? new Color(0.9f, 0.25f, 0.25f, 0.25f) : changes.Count > 0 ? ResultTint(changes, 0.16f) : head);
            }

            var full = s.File.GitPath + "|" + foldKey;
            bool expanded = !_componentFolded.Contains(full);
            bool now = EditorGUI.Foldout(new Rect(mineRect.x + 4f, rect.y + 2f, 14f, 18f), expanded, GUIContent.none, false);
            if (now != expanded) { if (now) _componentFolded.Remove(full); else _componentFolded.Add(full); }

            HeaderCell(mineRect, mine, title, icon, 18f);
            HeaderCell(resultRect, result, title, icon, 4f);
            HeaderCell(theirsRect, theirs, title, icon, 4f);

            if (open > 0)
            {
                var old = GUI.color;
                GUI.color = GitPalette.Text(GitFileStatus.Conflicted);
                GUI.Label(new Rect(resultRect.xMax - 120f, rect.y + 2f, 116f, 18f), L.F("unresolved: {0}", open), _badge);
                GUI.color = old;
            }

            if (changes.Count > 0)
            {
                if (GUI.Button(new Rect(mineRect.xMax + 3f, rect.y + 2f, Gutter - 6f, 18f), new GUIContent("»", L.T("Whole, as on my side")), _arrow))
                    Take(changes, MergeSide.Mine);
                if (GUI.Button(new Rect(resultRect.xMax + 3f, rect.y + 2f, Gutter - 6f, 18f), new GUIContent("«", L.T("Whole, as on their side")), _arrow))
                    Take(changes, MergeSide.Theirs);

                var ev = Event.current;
                if (ev.type == EventType.ContextClick && rect.Contains(ev.mousePosition))
                {
                    ShowTakeMenu(changes, L.Tc("take menu", "Whole"));
                    ev.Use();
                }
            }

            GUILayout.Space(2f);
        }

        private void HeaderCell<T>(Rect cell, T item, Func<T, string> title, Func<T, Texture> icon, float indent) where T : class
        {
            float x = cell.x + indent;
            if (item == null)
            {
                GUI.Label(new Rect(x, cell.y + 2f, cell.width - indent, 18f), L.T("— none —"), _cellDim);
                return;
            }

            var tex = icon(item);
            if (tex != null) GUI.DrawTexture(new Rect(x, cell.y + 3f, 16f, 16f), tex, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(x + 19f, cell.y + 2f, cell.xMax - x - 19f, 18f), title(item), EditorStyles.boldLabel);
        }

        // ------------------------------------------------------------ общее ---

        private void Take(List<YamlMergeConflict> changes, MergeSide side)
        {
            foreach (var c in changes) c.Choice = side;
            Changed();
        }

        private void ShowTakeMenu(List<YamlMergeConflict> changes, string what)
        {
            var state = GitConflicts.State;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(what + ": " + state.MineLabel.Replace('/', '∕')), false, () => Take(changes, MergeSide.Mine));
            menu.AddItem(new GUIContent(what + ": " + state.TheirsLabel.Replace('/', '∕')), false, () => Take(changes, MergeSide.Theirs));
            menu.AddItem(new GUIContent(L.F("{0}: As Before Both Changes", what)), false, () => Take(changes, MergeSide.Base));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.T("Reset Choice")), false, () => Take(changes, MergeSide.None));
            menu.ShowAsContext();
        }

        private static List<YamlMergeConflict> ChangesForProperty(List<YamlMergeConflict> changes, string path)
        {
            var list = new List<YamlMergeConflict>();
            foreach (var c in changes)
                if (!c.IsDocument && RootOf(c.Key) == path) list.Add(c);
            return list;
        }

        /// <summary>«m_LocalPosition», «m_Materials[]», «m_Foo.bar» → имя свойства верхнего уровня.</summary>
        private static string RootOf(string key)
        {
            if (string.IsNullOrEmpty(key) || key[0] == '@') return string.Empty;
            int end = key.IndexOfAny(new[] { '.', '[', ':', '#' });
            return end < 0 ? key : key.Substring(0, end);
        }

        private static UnityDocument DocIn(UnityScene scene, long id)
        {
            UnityDocument d;
            return scene != null && scene.ById.TryGetValue(id, out d) ? d : null;
        }

        private static Color ResultTint(List<YamlMergeConflict> changes, float a)
        {
            bool mine = false, theirs = false;
            foreach (var c in changes)
            {
                var e = c.Effective;
                if (e == MergeSide.Mine) mine = true;
                else if (e == MergeSide.Theirs) theirs = true;
                else if (e == MergeSide.None) { mine = true; theirs = true; }
            }

            if (mine && theirs) return new Color(0.55f, 0.45f, 0.9f, a);
            if (theirs) return TheirsTint(a);
            if (mine) return MineTint(a);
            return new Color(0.5f, 0.5f, 0.5f, a);
        }

        private static string NodeName(OutlineNode node)
        {
            if (!string.IsNullOrEmpty(node.Name)) return node.Name;
            if (node.IsPrefabInstance)
            {
                var path = PrefabPath(node);
                return string.IsNullOrEmpty(path) ? L.T("Prefab Instance") : System.IO.Path.GetFileNameWithoutExtension(path);
            }
            return L.T("(unnamed)");
        }

        private static string PrefabPath(OutlineNode node)
        {
            return string.IsNullOrEmpty(node.PrefabGuid) ? string.Empty : AssetDatabase.GUIDToAssetPath(node.PrefabGuid);
        }

        private static Texture PrefabIcon
        {
            get
            {
                var icon = EditorGUIUtility.FindTexture("Prefab Icon");
                return icon != null ? icon : SceneHistoryIcons.GameObjectIcon;
            }
        }
    }
}
