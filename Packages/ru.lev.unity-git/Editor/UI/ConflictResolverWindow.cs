using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>
    /// Разрешение конфликтов в три панели, как в IDEA: слева моя версия,
    /// справа их, в центре то, что будет записано.
    ///
    /// Строка — не только конфликт, а любая правка любой стороны: что движок
    /// свёл сам, тоже видно, и у каждой строки есть «»» и «««» — взять версию
    /// с этой стороны. Иначе автоматическое слияние — чёрный ящик, которому
    /// приходится верить на слово.
    ///
    /// Сцена и префаб сгруппированы по объектам, у компонента по щелчку
    /// раскрывается настоящий инспектор в тех же трёх колонках.
    ///
    /// IMGUI по той же причине, что и история объектов: инспектор компонента
    /// рисуется только им.
    /// </summary>
    public sealed partial class ConflictResolverWindow : EditorWindow, ILocalizedWindow
    {
        private sealed class FileState
        {
            public ConflictFile File;
            public string Base, Mine, Theirs;
            public YamlMergeResult Merge;
            public UnityScene BaseScene, MineScene, TheirsScene;
            public bool Loading;
            public string Error;

            /// <summary>Результат UnityYAMLMerge, если он свёл файл сам.</summary>
            public string UnityResult;
            public bool UnityTried;

            public int BuiltVersion = -1;
            public string Built;
            public UnityScene ResultScene;
            public List<MergeIssue> Issues;

            public SceneOutline BaseOutline, MineOutline, TheirsOutline, ResultOutline;
            public MergeOutline Outline;
            public long SelectedRow;
            public bool HasSelection;

            /// <summary>Слияние кода и прочего текста по местам.</summary>
            public TextMergeResult Text;
        }

        private sealed class Group
        {
            public string Key;
            public string Title;
            public long ObjectId;
            public int Open;
            public readonly List<YamlMergeConflict> Changes = new List<YamlMergeConflict>();
        }

        private const float ListWidth = 220f;
        private const float Gutter = 26f;

        private List<ConflictFile> _files = new List<ConflictFile>();
        private readonly Dictionary<string, FileState> _states = new Dictionary<string, FileState>(StringComparer.Ordinal);
        private string _selected;
        // Разовая просьба выделить файл: через перезагрузку домена Unity пронёс бы её как "".
        [NonSerialized] private string _pendingSelect;
        private bool _listing;
        private bool _busy;
        private string _status = string.Empty;
        private bool _statusError;

        private Vector2 _listScroll, _bodyScroll;
        private int _version;

        private bool _showSame;
        private bool _onlyConflicts;
        private bool _onlyDifferent = true;

        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _folded = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, ComponentMergeView> _views = new Dictionary<string, ComponentMergeView>(StringComparer.Ordinal);

        /// <summary>Подписи ячеек. Стороны не меняются, итог зависит от выбора — ключ включает его.</summary>
        private readonly Dictionary<string, string> _cells = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<YamlMergeConflict, int> _ids = new Dictionary<YamlMergeConflict, int>();

        private Action _pending;
        private static GUIStyle _cell, _cellDim, _title, _badge, _arrow, _column;

        // ------------------------------------------------------------ вход ---

        [MenuItem("Window/Git Conflicts")]
        public static void OpenMenu() { Open(null); }

        public static void Open(string projectPath)
        {
            var w = GetWindow<ConflictResolverWindow>(false, L.T("Conflicts"), true);
            w.minSize = new Vector2(820f, 380f);
            w.Show();
            w.Focus();
            w._pendingSelect = projectPath;
            w.Reload();
        }

        void ILocalizedWindow.OnLanguageChanged()
        {
            titleContent = new GUIContent(L.T("Conflicts"));
            _cells.Clear();
            Repaint();
        }

        private void OnEnable()
        {
            ComponentDiffView.SweepStrays();
            GitStatusCache.Updated += OnStatusUpdated;
            wantsMouseMove = true;
            if (_files.Count == 0) Reload();
        }

        private void OnDisable()
        {
            GitStatusCache.Updated -= OnStatusUpdated;
            DropViews();
        }

        private void OnStatusUpdated()
        {
            // Конфликт решили снаружи — из консоли или другого клиента.
            if (!_listing && !_busy && GitStatusCache.ConflictCount != _files.Count) Reload();
            else Repaint();
        }

        private void DropViews()
        {
            foreach (var v in _views.Values) v.Dispose();
            _views.Clear();
            DropPreviews();
        }

        // ---------------------------------------------------------- загрузка ---

        private async void Reload()
        {
            if (_listing || !GitRepository.IsRepo && !GitRepository.Locate()) return;
            _listing = true;

            try
            {
                _files = await GitConflicts.ListAsync();

                var alive = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in _files) alive.Add(f.GitPath);
                foreach (var key in new List<string>(_states.Keys))
                    if (!alive.Contains(key)) _states.Remove(key);

                if (_pendingSelect != null)
                {
                    foreach (var f in _files)
                        if (f.ProjectPath == _pendingSelect || f.ProjectPath == _pendingSelect + ".meta")
                        { Select(f); break; }
                    _pendingSelect = null;
                }

                if (_selected != null && !alive.Contains(_selected)) _selected = null;
                if (_selected == null && _files.Count > 0) Select(_files[0]);
            }
            finally
            {
                _listing = false;
                Repaint();
            }
        }

        private void Select(ConflictFile f)
        {
            if (_selected != f.GitPath)
            {
                DropViews();
                _bodyScroll = Vector2.zero;
            }
            _selected = f.GitPath;

            // Состояние пересоздаётся, если с тех пор поменялись сами версии:
            // например, rebase перешёл к следующему коммиту.
            FileState state;
            if (_states.TryGetValue(f.GitPath, out state) &&
                state.File.MineBlob == f.MineBlob && state.File.TheirsBlob == f.TheirsBlob && state.File.BaseBlob == f.BaseBlob)
                return;

            state = new FileState { File = f };
            _states[f.GitPath] = state;
            if (f.Content == ConflictContent.UnityYaml && f.BothPresent) Load(state);
            else if (f.Content == ConflictContent.Text && f.BothPresent) LoadText(state);
        }

        private async void Load(FileState s)
        {
            s.Loading = true;
            Repaint();

            try
            {
                s.Base = await GitConflicts.BlobTextAsync(s.File.BaseBlob);
                s.Mine = await GitConflicts.BlobTextAsync(s.File.MineBlob);
                s.Theirs = await GitConflicts.BlobTextAsync(s.File.TheirsBlob);

                if (s.Mine == null || s.Theirs == null)
                {
                    s.Error = L.T("Couldn't read the file versions from the repository.");
                    return;
                }

                string b = s.Base, m = s.Mine, t = s.Theirs;
                bool yaml = UnityYamlParser.LooksLikeUnityYaml(m) || UnityYamlParser.LooksLikeUnityYaml(t);

                // Разбор больших сцен — секунды: в фоне, чтобы редактор не вставал.
                await Task.Run(() =>
                {
                    s.Merge = YamlMerge.Merge(b, m, t);
                    if (yaml)
                    {
                        s.BaseScene = UnityScene.Build(b ?? string.Empty);
                        s.MineScene = UnityScene.Build(m);
                        s.TheirsScene = UnityScene.Build(t);
                        s.BaseOutline = SceneOutline.Build(s.BaseScene);
                        s.MineOutline = SceneOutline.Build(s.MineScene);
                        s.TheirsOutline = SceneOutline.Build(s.TheirsScene);
                    }
                });

                Changed();

                // Штатный инструмент — только для настоящего Unity-YAML: мету он не понимает.
                if (yaml)
                {
                    s.UnityResult = await GitConflicts.UnityYamlMergeAsync(s.File.GitPath, b, m, t);
                    s.UnityTried = true;

                    // Совпал с нашим результатом — второе предложение ничего не добавит.
                    if (s.UnityResult != null && s.UnityResult == s.Merge.Build()) s.UnityResult = null;
                }
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

        // -------------------------------------------------------------- UI ---

        private void OnGUI()
        {
            EnsureStyles();

            if (_pending != null && Event.current.type == EventType.Layout)
            {
                var a = _pending;
                _pending = null;
                a();
            }

            if (Event.current.type == EventType.MouseMove) Repaint();

            DrawToolbar();

            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(ListWidth)))
                    DrawList();

                var line = GUILayoutUtility.GetRect(1f, 1f, GUILayout.Width(1f), GUILayout.ExpandHeight(true));
                if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(line, new Color(0f, 0f, 0f, 0.25f));

                using (new EditorGUILayout.VerticalScope())
                    DrawBody();
            }

            if (_status.Length > 0)
            {
                var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                if (_statusError) style.normal.textColor = GitPalette.Text(GitFileStatus.Conflicted);
                EditorGUILayout.LabelField(_status, style);
            }
        }

        private static void EnsureStyles()
        {
            if (_cell != null) return;

            _cell = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = false,
                fontSize = 11,
                padding = new RectOffset(6, 6, 3, 3),
                alignment = TextAnchor.UpperLeft
            };

            _cellDim = new GUIStyle(_cell) { fontStyle = FontStyle.Italic };
            _cellDim.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.6f, 0.6f, 0.6f) : new Color(0.4f, 0.4f, 0.4f);

            _title = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleLeft };
            _badge = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            _arrow = new GUIStyle(EditorStyles.miniButton) { fontSize = 13, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 0, 2) };
            _column = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip };
        }

        private void DrawToolbar()
        {
            var state = GitConflicts.State;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(state.Title, EditorStyles.boldLabel);
                GUILayout.Label(_files.Count == 0 ? L.T("no conflicts") : L.F("files left: {0}", _files.Count), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(_busy || _listing))
                {
                    if (GUILayout.Button(L.T("Refresh"), EditorStyles.toolbarButton)) Reload();

                    if (state.CanContinue)
                    {
                        using (new EditorGUI.DisabledScope(_files.Count > 0))
                            if (GUILayout.Button(new GUIContent(L.Tc("git operation", "Continue"),
                                    _files.Count > 0 ? L.T("Resolve all conflicts first") : L.F("Continue {0}", state.Title.ToLowerInvariant())),
                                    EditorStyles.toolbarButton))
                                RunOp(L.T("Continuing"), GitConflicts.ContinueAsync);

                        if (state.CanSkip && GUILayout.Button(new GUIContent(L.T("Skip Commit"),
                                L.T("Drop the current commit from the rebase and move on")), EditorStyles.toolbarButton))
                            if (EditorUtility.DisplayDialog(L.T("Skip Commit"),
                                    L.T("Changes from this commit won't be in the result. Continue?"), L.T("Skip"), L.T("Cancel")))
                                RunOp(L.T("Skipping"), GitConflicts.SkipAsync);

                        if (GUILayout.Button(L.T("Abort"), EditorStyles.toolbarButton) &&
                            EditorUtility.DisplayDialog(L.T("Abort Operation"),
                                L.F("{0} will be aborted and the working tree will return to its state before it started. Choices already made here will be lost.", state.Title),
                                L.T("Abort Operation"), L.T("Back")))
                            RunOp(L.T("Aborting"), GitConflicts.AbortAsync);
                    }
                }
            }
        }

        private async void RunOp(string title, Func<Task<ProcessResult>> op)
        {
            _busy = true;
            SetStatus(title + "…", false);
            try
            {
                var r = await op();
                SetStatus(r.Ok ? L.F("{0}: done", title) : r.Message, !r.Ok);
                if (r.Ok) GitConflicts.EndBackupSession();
            }
            finally
            {
                _busy = false;
                _states.Clear();
                DropViews();
                await GitStatusCache.RefreshAsync();
                Reload();
            }
        }

        private void SetStatus(string text, bool error)
        {
            _status = text ?? string.Empty;
            _statusError = error;
            Repaint();
        }

        // ------------------------------------------------------------ список ---

        private void DrawList()
        {
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);

            if (_files.Count == 0)
            {
                EditorGUILayout.Space(10f);
                EditorGUILayout.LabelField(_listing ? L.T("Looking for conflicts…") : L.T("No conflicts."), EditorStyles.centeredGreyMiniLabel);
                if (!_listing && GitConflicts.State.CanContinue)
                    EditorGUILayout.LabelField(L.T("You can continue the operation."), EditorStyles.centeredGreyMiniLabel);
            }

            foreach (var f in _files)
            {
                var rect = GUILayoutUtility.GetRect(0f, 36f, GUILayout.ExpandWidth(true));
                bool selected = f.GitPath == _selected;

                if (Event.current.type == EventType.Repaint)
                {
                    if (selected) EditorGUI.DrawRect(rect, new Color(0.24f, 0.48f, 0.9f, 0.28f));
                    else if (rect.Contains(Event.current.mousePosition)) EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.1f));
                }

                GUI.DrawTexture(new Rect(rect.x + 6f, rect.y + 3f, 16f, 16f), Ui.AssetIcon(f.IsMeta
                    ? f.ProjectPath.Substring(0, f.ProjectPath.Length - 5) : f.ProjectPath), ScaleMode.ScaleToFit);

                GUI.Label(new Rect(rect.x + 26f, rect.y + 2f, rect.width - 30f, 16f),
                          new GUIContent(Ui.NameOf(f.ProjectPath), f.ProjectPath), EditorStyles.label);

                FileState st;
                var detail = GitConflictParser.ShapeName(f.Shape);
                if (_states.TryGetValue(f.GitPath, out st) && st.Merge != null)
                    detail = st.Merge.Unresolved == 0 ? L.T("nothing open — review and write") : L.F("unresolved: {0}", st.Merge.Unresolved);

                GUI.Label(new Rect(rect.x + 26f, rect.y + 18f, rect.width - 30f, 14f), detail, EditorStyles.miniLabel);

                if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
                {
                    var file = f;
                    _pending = () => Select(file);
                    Event.current.Use();
                    Repaint();
                }
            }

            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------ файл ---

        private void DrawBody()
        {
            FileState s;
            if (_selected == null || !_states.TryGetValue(_selected, out s))
            {
                EditorGUILayout.Space(20f);
                EditorGUILayout.LabelField(L.T("Select a file on the left."), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var f = s.File;
            EditorGUILayout.LabelField(f.ProjectPath, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(GitConflictParser.ShapeName(f.Shape) + " · " + ContentName(f.Content), EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(_busy))
            {
                if (!f.BothPresent || f.Content == ConflictContent.Binary) DrawWholeFile(s);
                else if (f.Content == ConflictContent.Text) DrawTextMerge(s);
                else DrawYaml(s);
            }
        }

        private static string ContentName(ConflictContent c)
        {
            return c == ConflictContent.UnityYaml ? "Unity-YAML" : c == ConflictContent.Binary ? L.Tc("file content", "binary") : L.Tc("file content", "text");
        }

        /// <summary>Файл, который сливать нечем: бинарник или удалённый одной стороной.</summary>
        private void DrawWholeFile(FileState s)
        {
            var f = s.File;
            var state = GitConflicts.State;

            DrawAssetPreview(s);

            EditorGUILayout.HelpBox(f.Content == ConflictContent.Binary && f.BothPresent
                ? L.T("A binary file can't be merged in parts — pick a whole version. Both versions will be saved to Library/LevGit/MergeBackups.")
                : L.T("One side deleted the file. Decide whether it stays."), MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(f.MineBlob != null ? L.F("« {0} — Take", state.MineLabel) : L.F("« {0} — Delete File", state.MineLabel)))
                    Take(f, MergeSide.Mine);
                if (GUILayout.Button(f.TheirsBlob != null ? L.F("{0} — Take »", state.TheirsLabel) : L.F("{0} — Delete File »", state.TheirsLabel)))
                    Take(f, MergeSide.Theirs);
            }
        }

        private void DrawText(FileState s)
        {
            var f = s.File;
            EditorGUILayout.HelpBox(L.T("Code and other text are merged by the configured git mergetool, or by the external editor if there is none. Once the conflict markers are removed, mark the file resolved."), MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L.T("Open in mergetool"))) RunFileOp("mergetool", () => GitConflicts.OpenExternalAsync(f));
                if (GUILayout.Button(L.T("Mark Resolved"))) RunFileOp(L.T("Marking"), () => GitConflicts.MarkResolvedAsync(f));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L.F("« {0} — Whole File", GitConflicts.State.MineLabel))) Take(f, MergeSide.Mine);
                if (GUILayout.Button(L.F("{0} — Whole File »", GitConflicts.State.TheirsLabel))) Take(f, MergeSide.Theirs);
            }
        }

        private void Take(ConflictFile f, MergeSide side)
        {
            if (f.ProjectPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && !GitConflicts.ConfirmSceneWrite(f.ProjectPath))
                return;

            RunFileOp(L.T("Choosing version"), async () =>
            {
                var r = await GitConflicts.TakeSideAsync(f, side);
                if (r.Ok) GitConflicts.ReloadAfterWrite(f.ProjectPath);
                return r;
            });
        }

        private async void RunFileOp(string title, Func<Task<ProcessResult>> op)
        {
            _busy = true;
            try
            {
                var r = await op();
                SetStatus(r.Ok ? (string.IsNullOrEmpty(r.StdOut) ? L.F("{0}: done", title) : r.StdOut.Trim()) : r.Message, !r.Ok);
            }
            catch (Exception e)
            {
                SetStatus(title + ": " + e.Message, true);
            }
            finally
            {
                _busy = false;
                await GitStatusCache.RefreshAsync();
                Reload();
            }
        }

        // ---------------------------------------------------- три панели ---

        private void DrawYaml(FileState s)
        {
            if (s.Loading && s.Merge == null)
            {
                EditorGUILayout.LabelField(L.T("Reading three versions and merging…"), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            if (s.Error != null)
            {
                EditorGUILayout.HelpBox(s.Error, MessageType.Error);
                DrawWholeFile(s);
                return;
            }

            var r = s.Merge;
            if (r == null) return;

            EnsureBuilt(s);
            var state = GitConflicts.State;

            DrawAssetPreview(s);

            int mine = 0, theirs = 0, both = 0, same = 0;
            foreach (var c in r.Changes)
            {
                if (c.Same) same++;
                else if (c.IsConflict || c.Auto == MergeSide.None) both++;
                else if (c.Auto == MergeSide.Mine) mine++;
                else theirs++;
            }

            EditorGUILayout.LabelField(L.F("Changes only on my side: {0}, only on theirs: {1}, on both: {2}, identical: {3}. Conflicts: {4}, unresolved: {5}.", mine, theirs, both, same, r.Conflicts.Count, r.Unresolved), EditorStyles.wordWrappedMiniLabel);

            if (s.UnityResult != null)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(L.T("UnityYAMLMerge merged the file using Unity's standard rules and got a different result."),
                                    EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button(L.T("Write Its Version"), GUILayout.Width(160f)))
                        WriteChecked(s, s.UnityResult, "UnityYAMLMerge");
                }
            }

            // Действия и запись — наверху: внизу кнопку уводит за край любая длинная сцена.
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent(L.T("« Conflicts: Mine"), L.T("Resolve all unresolved conflicts with my side")), GUILayout.Width(130f)))
                { r.ResolveAll(MergeSide.Mine); Changed(); }
                if (GUILayout.Button(new GUIContent(L.T("Conflicts: Theirs »"), L.T("Resolve all unresolved conflicts with their side")), GUILayout.Width(130f)))
                { r.ResolveAll(MergeSide.Theirs); Changed(); }
                if (GUILayout.Button(new GUIContent(L.T("Reset"), L.T("Restore the automatic decisions; conflicts become unresolved again")), GUILayout.Width(80f)))
                { r.ResetChoices(); Changed(); }

                GUILayout.Space(8f);
                if (!IsScene(s)) _onlyConflicts = GUILayout.Toggle(_onlyConflicts, L.T("Conflicts Only"), GUILayout.Width(118f));
                if (IsScene(s))
                    _onlyChangedTree = GUILayout.Toggle(_onlyChangedTree, new GUIContent(L.T("Changed Only"),
                        L.T("Show only objects and components changed by at least one side in the Hierarchy and Inspector")), GUILayout.Width(130f));
                else
                    _showSame = GUILayout.Toggle(_showSame, new GUIContent(L.T("Identical"), L.T("Show changes made identically by both sides")), GUILayout.Width(92f));

                GUILayout.FlexibleSpace();

                var label = r.Unresolved > 0 ? L.F("Write (unresolved: {0})", r.Unresolved) : L.T("Write Result");
                using (new EditorGUI.DisabledScope(r.Unresolved > 0))
                    if (GUILayout.Button(new GUIContent(label, L.T("Write the result and mark the conflict resolved")), GUILayout.Width(190f)))
                        WriteChecked(s, s.Built, L.Tc("merge result source", "merge"));
            }

            float width = position.width - ListWidth - 22f;

            if (IsScene(s))
            {
                DrawSceneMerge(s, width);
                return;
            }

            float column = Mathf.Max(120f, (width - Gutter * 2f) / 3f);

            DrawColumnHeads(state, column);

            _bodyScroll = EditorGUILayout.BeginScrollView(_bodyScroll);

            var groups = GroupsOf(s);
            if (groups.Count == 0)
                EditorGUILayout.LabelField(r.Changes.Count == 0 ? L.T("The sides didn't change anything.") : L.T("Nothing matches the filter."),
                                           EditorStyles.centeredGreyMiniLabel);

            foreach (var g in groups) DrawGroup(s, g, column);

            DrawIssues(s);
            EditorGUILayout.Space(8f);
            EditorGUILayout.EndScrollView();
        }

        private void DrawColumnHeads(GitOperationState state, float column)
        {
            var rect = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            var mine = new Rect(rect.x, rect.y, column, rect.height);
            var result = new Rect(mine.xMax + Gutter, rect.y, column, rect.height);
            var theirs = new Rect(result.xMax + Gutter, rect.y, column, rect.height);

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(mine, MineTint(0.22f));
                EditorGUI.DrawRect(result, new Color(0.5f, 0.5f, 0.5f, 0.16f));
                EditorGUI.DrawRect(theirs, TheirsTint(0.22f));
            }

            GUI.Label(mine, new GUIContent(state.MineLabel, L.T("My side's version")), _column);
            GUI.Label(result, new GUIContent(L.T("Result"), L.T("What will be written. Right-click a cell for the base version or reset")), _column);
            GUI.Label(theirs, new GUIContent(state.TheirsLabel, L.T("Their side's version")), _column);
        }

        // ------------------------------------------------------------ группы ---

        private List<Group> GroupsOf(FileState s)
        {
            var groups = new List<Group>();
            var byKey = new Dictionary<string, Group>(StringComparer.Ordinal);

            foreach (var c in s.Merge.Changes)
            {
                if (c.Same && !_showSame) continue;
                if (_onlyConflicts && !c.IsConflict) continue;

                long objectId;
                var title = OwnerTitle(s, c.FileId, out objectId);
                var key = objectId != 0 ? "go:" + objectId : "doc:" + c.FileId;

                Group g;
                if (!byKey.TryGetValue(key, out g))
                {
                    g = new Group { Key = key, Title = title, ObjectId = objectId };
                    byKey[key] = g;
                    groups.Add(g);
                }

                g.Changes.Add(c);
                if (!c.IsResolved) g.Open++;
            }

            // Нерешённое — наверх: ради него окно и открыли.
            groups.Sort((a, b) =>
            {
                if ((a.Open > 0) != (b.Open > 0)) return a.Open > 0 ? -1 : 1;
                return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });

            return groups;
        }

        /// <summary>Объект, к которому относится документ, — на любой стороне, где он есть.</summary>
        private static string OwnerTitle(FileState s, long fileId, out long objectId)
        {
            objectId = 0;
            if (s.MineScene == null) return s.File.IsMeta ? L.T("Meta File") : L.T("File");

            foreach (var scene in new[] { s.MineScene, s.TheirsScene, s.BaseScene })
            {
                UnityDocument doc;
                if (scene == null || !scene.ById.TryGetValue(fileId, out doc)) continue;

                if (doc.TypeName == "GameObject")
                {
                    objectId = fileId;
                    return scene.Describe(doc);
                }

                var owner = scene.OwnerOf(doc);
                if (owner != null)
                {
                    objectId = owner.FileId;
                    return owner.Path;
                }

                if (doc.TypeName == "PrefabInstance") return L.T("Prefab Instance");
                return doc.TypeName ?? L.F("document {0}", fileId);
            }

            return L.F("document {0}", fileId);
        }

        private void DrawGroup(FileState s, Group g, float column)
        {
            var state = GitConflicts.State;
            bool open = !_folded.Contains(s.File.GitPath + "|" + g.Key);

            var rect = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.12f));

            var mineRect = new Rect(rect.x, rect.y, column, rect.height);
            var arrowL = new Rect(mineRect.xMax + 2f, rect.y + 2f, Gutter - 4f, rect.height - 4f);
            var resultRect = new Rect(mineRect.xMax + Gutter, rect.y, column, rect.height);
            var arrowR = new Rect(resultRect.xMax + 2f, rect.y + 2f, Gutter - 4f, rect.height - 4f);

            bool now = EditorGUI.Foldout(new Rect(rect.x + 4f, rect.y, 14f, rect.height), open, GUIContent.none, false);
            if (now != open) { if (now) _folded.Remove(s.File.GitPath + "|" + g.Key); else _folded.Add(s.File.GitPath + "|" + g.Key); }

            var icon = g.ObjectId != 0 ? SceneHistoryIcons.GameObjectIcon : null;
            float x = rect.x + 20f;
            if (icon != null) { GUI.DrawTexture(new Rect(x, rect.y + 3f, 16f, 16f), icon, ScaleMode.ScaleToFit); x += 19f; }

            var head = "  · " + (g.Open > 0 ? L.F("unresolved: {0}", g.Open) : L.F("changes: {0}", g.Changes.Count));
            GUI.Label(new Rect(x, rect.y, resultRect.xMax - x, rect.height), new GUIContent(g.Title + head, g.Title), _title);

            if (GUI.Button(arrowL, new GUIContent("»", L.T("Take everything for the object from my side")), _arrow))
            { foreach (var c in g.Changes) c.Choice = MergeSide.Mine; Changed(); }
            if (GUI.Button(arrowR, new GUIContent("«", L.T("Take everything for the object from their side")), _arrow))
            { foreach (var c in g.Changes) c.Choice = MergeSide.Theirs; Changed(); }

            // Удалённый одной стороной объект: вернуть его можно только
            // целиком, иначе компонент останется без объекта.
            bool removal = false;
            foreach (var c in g.Changes)
                if (c.Kind == MergeConflictKind.RemovedByMine || c.Kind == MergeConflictKind.RemovedByTheirs) removal = true;

            if (removal && g.ObjectId != 0)
            {
                var whole = new Rect(arrowR.xMax + 4f, rect.y + 2f, 130f, rect.height - 4f);
                if (GUI.Button(whole, new GUIContent(L.T("Whole Object ▾"), L.T("Take the object with all its components from one side")), EditorStyles.miniButton))
                {
                    var menu = new GenericMenu();
                    var group = g;
                    menu.AddItem(new GUIContent(state.MineLabel), false, () => WholeObject(s, group, MergeSide.Mine));
                    menu.AddItem(new GUIContent(state.TheirsLabel), false, () => WholeObject(s, group, MergeSide.Theirs));
                    menu.DropDown(whole);
                }
            }

            if (!now) return;

            foreach (var c in g.Changes) DrawChange(s, c, column);
            EditorGUILayout.Space(4f);
        }

        private void WholeObject(FileState s, Group g, MergeSide side)
        {
            var ids = new HashSet<long> { g.ObjectId };
            foreach (var scene in new[] { s.MineScene, s.TheirsScene, s.BaseScene })
            {
                UnityGameObject go;
                if (scene == null || !scene.GameObjects.TryGetValue(g.ObjectId, out go)) continue;
                foreach (var comp in go.Components) ids.Add(comp.FileId);
            }

            foreach (var id in ids) s.Merge.SetDocumentSide(id, side);
            foreach (var c in g.Changes) c.Choice = side;
            Changed();
        }

        // ------------------------------------------------------------ строка ---

        private void DrawChange(FileState s, YamlMergeConflict c, float column)
        {
            var key = s.File.GitPath + "|" + c.FileId + "|" + (c.Key ?? "@doc");
            bool canInspect = CanInspect(s, c);

            // Заголовок: что это и как решено.
            var head = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));
            float x = head.x + 16f;

            bool expanded = _expanded.Contains(key);
            if (canInspect)
            {
                bool now = EditorGUI.Foldout(new Rect(x, head.y, 14f, head.height), expanded, GUIContent.none, false);
                if (now != expanded) { if (now) _expanded.Add(key); else _expanded.Remove(key); expanded = now; }
            }
            x += 16f;

            string badge;
            Color badgeColor;
            Verdict(s, c, out badge, out badgeColor);

            GUI.Label(new Rect(x, head.y, head.width - x - 190f, head.height), Title(s, c), EditorStyles.miniLabel);
            var old = GUI.color;
            GUI.color = badgeColor;
            GUI.Label(new Rect(head.xMax - 190f, head.y, 184f, head.height), badge, _badge);
            GUI.color = old;

            // Ячейки.
            var mineText = Cell(s, c, MergeSide.Mine);
            var theirsText = Cell(s, c, MergeSide.Theirs);
            var resultText = Cell(s, c, MergeSide.None);

            float height = Mathf.Max(Height(mineText, column), Height(resultText, column), Height(theirsText, column));
            height = Mathf.Clamp(height, 20f, 110f);

            var row = GUILayoutUtility.GetRect(0f, height, GUILayout.ExpandWidth(true));
            var mine = new Rect(row.x, row.y, column, height);
            var result = new Rect(mine.xMax + Gutter, row.y, column, height);
            var theirs = new Rect(result.xMax + Gutter, row.y, column, height);

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(mine, c.MineChanged ? MineTint(0.16f) : new Color(0.5f, 0.5f, 0.5f, 0.05f));
                EditorGUI.DrawRect(theirs, c.TheirsChanged ? TheirsTint(0.16f) : new Color(0.5f, 0.5f, 0.5f, 0.05f));

                Color center;
                if (!c.IsResolved) center = new Color(0.9f, 0.25f, 0.25f, 0.22f);
                else switch (c.Effective)
                {
                    case MergeSide.Mine: center = MineTint(0.12f); break;
                    case MergeSide.Theirs: center = TheirsTint(0.12f); break;
                    case MergeSide.Base: center = new Color(0.5f, 0.5f, 0.5f, 0.12f); break;
                    default: center = new Color(0.55f, 0.45f, 0.9f, 0.14f); break;
                }
                EditorGUI.DrawRect(result, center);
            }

            GUI.Label(mine, mineText, c.Mine == null ? _cellDim : _cell);
            GUI.Label(theirs, theirsText, c.Theirs == null ? _cellDim : _cell);
            GUI.Label(result, resultText, !c.IsResolved || c.ResultText == null ? _cellDim : _cell);

            // Стрелки: взять версию с этой стороны.
            var takeMine = new Rect(mine.xMax + 3f, row.y + (height - 20f) * 0.5f, Gutter - 6f, 20f);
            var takeTheirs = new Rect(result.xMax + 3f, row.y + (height - 20f) * 0.5f, Gutter - 6f, 20f);

            bool mineActive = c.Effective == MergeSide.Mine && c.IsResolved && (c.Choice != MergeSide.None || c.AutoText == null);
            bool theirsActive = c.Effective == MergeSide.Theirs && (c.Choice != MergeSide.None || c.AutoText == null);

            var tint = GUI.backgroundColor;
            if (mineActive) GUI.backgroundColor = MineTint(1f);
            if (GUI.Button(takeMine, new GUIContent("»", L.T("Take my version")), _arrow)) { c.Choice = MergeSide.Mine; Changed(); }
            GUI.backgroundColor = tint;

            if (theirsActive) GUI.backgroundColor = TheirsTint(1f);
            if (GUI.Button(takeTheirs, new GUIContent("«", L.T("Take their version")), _arrow)) { c.Choice = MergeSide.Theirs; Changed(); }
            GUI.backgroundColor = tint;

            var ev = Event.current;
            if (ev.type == EventType.ContextClick && result.Contains(ev.mousePosition))
            {
                ShowCellMenu(c);
                ev.Use();
            }

            if (expanded && canInspect) DrawInspector(s, c, key, column);
            EditorGUILayout.Space(3f);
        }

        private void ShowCellMenu(YamlMergeConflict c)
        {
            var state = GitConflicts.State;
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(L.F("Take: {0}", state.MineLabel.Replace('/', '∕'))), c.Choice == MergeSide.Mine, () => { c.Choice = MergeSide.Mine; Changed(); });
            menu.AddItem(new GUIContent(L.F("Take: {0}", state.TheirsLabel.Replace('/', '∕'))), c.Choice == MergeSide.Theirs, () => { c.Choice = MergeSide.Theirs; Changed(); });
            menu.AddItem(new GUIContent(L.T("Take Base Version (as it was before both changes)")), c.Choice == MergeSide.Base, () => { c.Choice = MergeSide.Base; Changed(); });
            menu.AddSeparator(string.Empty);

            if (c.IsConflict) menu.AddItem(new GUIContent(L.T("Clear Choice")), false, () => { c.Choice = MergeSide.None; Changed(); });
            else menu.AddItem(new GUIContent(L.T("As Resolved Automatically")), c.Choice == MergeSide.None, () => { c.Choice = MergeSide.None; Changed(); });

            menu.ShowAsContext();
        }

        private static void Verdict(FileState s, YamlMergeConflict c, out string text, out Color color)
        {
            var dim = EditorGUIUtility.isProSkin ? new Color(0.65f, 0.65f, 0.65f) : new Color(0.35f, 0.35f, 0.35f);

            if (c.IsConflict && c.Choice == MergeSide.None)
            {
                text = L.T("conflict — pick a side");
                color = GitPalette.Text(GitFileStatus.Conflicted);
                return;
            }

            if (c.Choice != MergeSide.None)
            {
                text = c.Choice == MergeSide.Mine ? L.T("chosen: mine") : c.Choice == MergeSide.Theirs ? L.T("chosen: theirs") : L.T("chosen: base");
                color = c.IsConflict ? GitPalette.Text(GitFileStatus.Added) : dim;
                return;
            }

            color = dim;
            if (c.Same) text = L.T("identical on both");
            else if (c.Auto == MergeSide.None) text = L.T("auto: merged from both");
            else text = c.Auto == MergeSide.Mine ? L.T("auto: only mine") : L.T("auto: only theirs");
        }

        private static Color MineTint(float a) { return new Color(0.28f, 0.55f, 1f, a); }
        private static Color TheirsTint(float a) { return new Color(0.3f, 0.78f, 0.42f, a); }

        private static float Height(string text, float width)
        {
            return _cell.CalcHeight(new GUIContent(text), width);
        }

        private void Changed()
        {
            _version++;
            DropViews();
            Repaint();
        }

        // ----------------------------------------------------------- подписи ---

        private static string Title(FileState s, YamlMergeConflict c)
        {
            var doc = DocOf(s, c.FileId);
            var type = doc != null ? doc.TypeName : null;

            switch (c.Kind)
            {
                case MergeConflictKind.RemovedByMine: return L.F("{0} · deleted by me, changed by them", type ?? L.T("Document"));
                case MergeConflictKind.RemovedByTheirs: return L.F("{0} · deleted by them, changed by me", type ?? L.T("Document"));
                case MergeConflictKind.Order: return L.F("{0} · element order", type ?? L.T("Document"));
            }

            if (c.IsDocument)
            {
                var what = c.Base == null ? " · " + L.Tc("document", "added") : (c.Mine == null || c.Theirs == null) ? " · " + L.Tc("document", "deleted") : string.Empty;
                return (type == "GameObject" ? L.T("Object") : type ?? L.T("File")) + what;
            }

            return (type != null && type != "GameObject" ? type + " · " : string.Empty) + YamlMerge.DescribeKey(c.Key);
        }

        private static UnityDocument DocOf(FileState s, long fileId)
        {
            foreach (var scene in new[] { s.MineScene, s.TheirsScene, s.BaseScene })
            {
                UnityDocument doc;
                if (scene != null && scene.ById.TryGetValue(fileId, out doc)) return doc;
            }
            return null;
        }

        /// <summary>
        /// Текст ячейки. side = None — центральная, итоговая. Правка
        /// документа целиком показывается списком изменённых свойств
        /// относительно базы, а не YAML-текстом на сорок строк.
        /// </summary>
        private string Cell(FileState s, YamlMergeConflict c, MergeSide side)
        {
            if (side == MergeSide.None && !c.IsResolved)
                return L.T("unresolved: » — mine, « — theirs");

            int id;
            if (!_ids.TryGetValue(c, out id)) { id = _ids.Count; _ids[c] = id; }

            string text;
            string variant;
            if (side == MergeSide.None)
            {
                text = c.ResultText;
                variant = "r" + (c.Choice == MergeSide.None && c.AutoText != null ? "auto" : c.Effective.ToString());
            }
            else
            {
                text = c.TextOf(side);
                variant = side.ToString();
            }

            var cacheKey = s.File.GitPath + "|" + id + "|" + variant;
            string cached;
            if (_cells.TryGetValue(cacheKey, out cached)) return cached;

            string result;
            if (c.Kind == MergeConflictKind.Order)
                result = text == null ? L.T("— none —") : side == MergeSide.None ? L.F("order: {0}", c.Effective == MergeSide.Theirs ? L.Tc("order", "theirs") : c.Effective == MergeSide.Base ? L.Tc("order", "base") : L.Tc("order", "mine")) : L.T("own element order");
            else if (text == null)
                result = c.Base == null ? L.T("— none —") : L.T("— deleted —");
            else if (c.IsDocument || c.Kind != MergeConflictKind.Property)
                result = DocumentSummary(c.Base, text);
            else
                result = UnitText(text);

            _cells[cacheKey] = result;
            return result;
        }

        private static string UnitText(string text)
        {
            var lines = text.Split('\n');
            var sb = new StringBuilder();

            for (int i = 0; i < lines.Length && i < 8; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("- ", StringComparison.Ordinal)) line = line.Substring(2);

                // Одна строка «ключ: значение» — ключ уже в заголовке, оставляем значение.
                if (lines.Length == 1)
                {
                    int colon = YamlUnits.FindKeyColon(line);
                    if (colon > 0 && colon + 2 <= line.Length) line = line.Substring(colon + 1).Trim();
                }

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
            }

            if (lines.Length > 8) sb.Append("\n" + L.F("…{0} more lines", lines.Length - 8));
            return sb.Length == 0 ? L.T("(empty)") : sb.ToString();
        }

        private static string DocumentSummary(string baseText, string sideText)
        {
            var side = FirstDoc(sideText);
            if (side == null) return UnitText(sideText);

            var name = side.Get("m_Name");
            if (baseText == null)
                return (name != null ? L.F("added “{0}”", name) : L.Tc("document", "added")) + (side.TypeName != null ? " · " + side.TypeName : string.Empty);

            var basis = FirstDoc(baseText);
            if (basis == null) return UnitText(sideText);

            var keys = new List<string>();
            foreach (var p in side.Props)
                if (basis.Get(p.Key) != p.Value && !IsNoise(p.Key)) keys.Add(p.Key);
            foreach (var p in basis.Props)
                if (!side.Props.ContainsKey(p.Key) && !IsNoise(p.Key)) keys.Add(p.Key);

            if (keys.Count == 0) return L.T("no changes relative to base");
            keys.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            for (int i = 0; i < keys.Count && i < 6; i++)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(keys[i]).Append(": ").Append(side.Get(keys[i]) ?? "—");
            }
            if (keys.Count > 6) sb.Append("\n" + L.F("…{0} more properties", keys.Count - 6));
            return sb.ToString();
        }

        private static bool IsNoise(string key)
        {
            return key == "serializedVersion" || key.EndsWith(".serializedVersion", StringComparison.Ordinal) ||
                   key.StartsWith("m_Component[", StringComparison.Ordinal);
        }

        private static UnityDocument FirstDoc(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            // Документ без заголовка «--- !u!» парсер не видит — достраиваем.
            var source = text.StartsWith("---", StringComparison.Ordinal) ? text : "--- !u!0 &0\nDocument:\n" + text;
            var docs = UnityYamlParser.Parse(source);
            return docs.Count > 0 ? docs[0] : null;
        }

        // --------------------------------------------------------- инспектор ---

        private static bool CanInspect(FileState s, YamlMergeConflict c)
        {
            if (s.MineScene == null || c.Kind == MergeConflictKind.Order) return false;
            var doc = DocOf(s, c.FileId);
            if (doc == null || doc.TypeName == null) return false;
            if (doc.TypeName == "MonoBehaviour") return true;
            return doc.TypeName != "GameObject" && doc.TypeName != "PrefabInstance" &&
                   SceneHistoryIcons.TypeOf(doc.TypeName) != null;
        }

        private void DrawInspector(FileState s, YamlMergeConflict c, string key, float column)
        {
            ComponentMergeView view;
            if (!_views.TryGetValue(key, out view))
            {
                UnityDocument mine = null, theirs = null, result = null;
                s.MineScene.ById.TryGetValue(c.FileId, out mine);
                s.TheirsScene.ById.TryGetValue(c.FileId, out theirs);
                if (s.ResultScene != null) s.ResultScene.ById.TryGetValue(c.FileId, out result);

                var any = mine ?? theirs ?? result;
                if (any == null) return;   // удалён обеими — показывать нечего
                var type = SceneHistoryIcons.ResolveComponent(any.TypeName, any);
                view = new ComponentMergeView(type, new[] { mine, result, theirs });
                _views[key] = view;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    _onlyDifferent = EditorGUILayout.ToggleLeft(L.T("Only Differing Properties"), _onlyDifferent, EditorStyles.miniLabel);
                    var docChanges = new List<YamlMergeConflict>();
                    foreach (var other in s.Merge.Changes) if (other.FileId == c.FileId) docChanges.Add(other);
                    view.Draw(column, Gutter, _onlyDifferent,
                              path => ChangesForProperty(docChanges, path),
                              (list, side) => Take(list, side));
                }
            }
        }

        // ----------------------------------------------------------- проверка ---

        private void EnsureBuilt(FileState s)
        {
            if (s.BuiltVersion == _version) return;

            s.Built = s.Merge.Build();
            s.Issues = MergeValidator.Check(s.Built, s.Merge);
            s.ResultScene = s.MineScene != null ? UnityScene.Build(s.Built) : null;
            s.BuiltVersion = _version;

            if (s.MineOutline != null)
            {
                s.ResultOutline = SceneOutline.Build(s.ResultScene);
                s.Outline = MergeOutline.Build(s.BaseOutline, s.MineOutline, s.TheirsOutline, s.ResultOutline, s.Merge);
            }

            // Итоговые ячейки зависят от выбора — старые подписи не годятся.
            var stale = new List<string>();
            foreach (var k in _cells.Keys) if (k.Contains("|r")) stale.Add(k);
            foreach (var k in stale) _cells.Remove(k);
        }

        private void DrawIssues(FileState s)
        {
            if (s.Issues == null || s.Issues.Count == 0) return;

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(L.T("Result Check"), EditorStyles.boldLabel);
            foreach (var i in s.Issues)
                EditorGUILayout.HelpBox(i.Title + "\n" + i.Detail,
                    i.Severity == MergeIssueSeverity.Error ? MessageType.Error : MessageType.Warning);
        }

        // ------------------------------------------------------------ запись ---

        private void WriteChecked(FileState s, string text, string source)
        {
            var issues = MergeValidator.Check(text, s.Merge);
            int errors = MergeValidator.Errors(issues);

            var diff = s.MineScene != null ? SceneDiffBuilder.Build(s.Mine, text) : null;
            int objects = 0, props = 0, settings = 0;
            if (diff != null) SceneDiffBuilder.Count(diff, out objects, out props, out settings);

            var message = L.F("Source: {0}.", source) + "\n" +
                          (diff != null ? L.F("Compared with my version, objects and components changed: {0}, properties: {1}.", objects, props) + "\n" : string.Empty) +
                          (diff != null && settings > 0 ? L.F("Scene settings changed: {0}.", settings) + "\n" : string.Empty) +
                          (issues.Count == 0 ? L.T("The result check is clean.") :
                              L.F("The check found: errors {0}, warnings {1}.", errors, issues.Count - errors) + "\n• " +
                              issues[0].Title + (issues.Count > 1 ? "\n" + L.F("…and {0} more", issues.Count - 1) : string.Empty)) +
                          "\n\n" + L.T("The original versions will be saved to Library/LevGit/MergeBackups.");

            if (!EditorUtility.DisplayDialog(L.F("Write {0}", Ui.NameOf(s.File.ProjectPath)), message,
                    errors > 0 ? L.T("Write with Errors") : L.T("Write"), L.T("Cancel")))
                return;

            if (s.File.ProjectPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) &&
                !GitConflicts.ConfirmSceneWrite(s.File.ProjectPath))
                return;

            var file = s.File;
            RunFileOp(L.T("Writing"), async () =>
            {
                var r = await GitConflicts.WriteResolvedAsync(file, text);
                if (r.Ok) GitConflicts.ReloadAfterWrite(file.ProjectPath);
                return r;
            });
        }
    }
}
