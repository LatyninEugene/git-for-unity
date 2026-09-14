using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>Что показывает окно: всю сцену, один объект или один компонент.</summary>
    public enum SceneHistoryScope { Scene = 0, Object, Component }

    /// <summary>
    /// История объектов сцены: кто, когда и что поменял.
    ///
    /// Каждый уровень показан в привычном для него виде: сцена — деревом
    /// объектов, как иерархия; объект — списком компонентов, как инспектор;
    /// компонент — настоящим инспектором на каждый коммит. Список строк «путь,
    /// свойство, значение» формально содержит то же самое, но читать его надо
    /// переводя в голове на язык редактора, а это и есть работа, которую должен
    /// делать инструмент.
    ///
    /// Отсюда IMGUI: инспектор компонента рисуется только им. Остальной пакет
    /// живёт на UI Toolkit, и это осознанное исключение ради одного окна.
    ///
    /// Коммиты рисуются порциями: собрать инспектор — это два временных объекта
    /// на каждый коммит, и делать их сразу на всю историю нельзя.
    /// </summary>
    public sealed class SceneHistoryWindow : EditorWindow, ILocalizedWindow
    {
        /// <summary>Сколько версий читать за раз. Дочитка глубже прибавляет столько же.</summary>
        private const int DepthStep = 100;
        private const int ScenePage = 15;
        private const int ComponentPage = 4;

        /// <summary>Узел дерева объектов внутри одного коммита.</summary>
        private sealed class TreeNode
        {
            public string Name;
            public string Path;
            public SceneEvent Own;                       // событие самого объекта
            public readonly List<SceneEvent> Components = new List<SceneEvent>();
            public readonly List<TreeNode> Children = new List<TreeNode>();
        }

        // ---- что показываем ----
        private string _scenePath;
        private SceneHistoryScope _scope;
        private long _objectId;
        private long _componentId;
        private string _objectTitle = string.Empty;
        private string _componentTitle = string.Empty;

        // ---- данные ----
        private SceneHistoryResult _result;
        private CancellationTokenSource _cancel;
        private string _status = string.Empty;
        private bool _loading;

        // ---- вид ----
        private Vector2 _scroll;
        private string _filter = string.Empty;
        private bool _onlyStructure;
        private bool _onlyChanged = true;
        private int _shown;

        /// <summary>У компонента: справа текущее состояние на сцене, а не состояние после коммита.</summary>
        private bool _compareLive;

        /// <summary>У объекта: компоненты инспекторами, а не списком правок.</summary>
        private bool _inspector;

        /// <summary>
        /// Окно открыли ради одного коммита — из меню «показать изменение» или
        /// «сравнить с текущим»: раскрыт только он, остальные свёрнуты.
        /// </summary>
        private bool _focusOnly;

        /// <summary>Лента из инспекторов: собирать их дорого, порции меньше.</summary>
        private bool InspectorView => _scope == SceneHistoryScope.Component || (_scope == SceneHistoryScope.Object && _inspector);

        private int _depth = DepthStep;

        /// <summary>Коммит, с которым окно открыли из журнала: к нему прокручиваем и его подсвечиваем.</summary>
        private string _focusSha;

        /// <summary>С какой ревизии читается лента; null — с текущей ветки.</summary>
        private string _fromRev;
        private string _fromLabel;

        /// <summary>Постоянная плашка о коммите, с которым окно открыли: почему его нет в ленте и что сделать.</summary>
        /// <remarks>Живёт только вместе с действием: без него после перезагрузки домена осталась бы пустая плашка.</remarks>
        [NonSerialized] private string _focusNote;
        [NonSerialized] private string _focusActionLabel;
        private Action _focusAction;
        private bool _scrollToFocus;

        private bool _reloadQueued;
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Версии, которые уже были в ленте. Новые приходят свёрнутыми — и при
        /// первом чтении, и после «Загрузить ещё», — а раскрытое человеком не трогается.
        /// </summary>
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Свёрнутые объекты и компоненты. Ключ — «коммит|путь»: у каждого коммита своё дерево.</summary>
        private readonly HashSet<string> _folded = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Коммиты, в которых раскрыта группа «Настройки сцены». По умолчанию она свёрнута.</summary>
        private readonly HashSet<string> _settingsOpen = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Переход, отложенный до следующего прохода раскладки. Менять уровень
        /// истории посреди обработки щелчка нельзя: остаток кадра рисовался бы
        /// уже по другим данным, и IMGUI ругается на расхождение раскладки.
        /// </summary>
        private Action _pending;

        /// <summary>Собранные инспекторы по коммитам. Живут, пока коммит на экране.</summary>
        private readonly Dictionary<string, ComponentDiffView> _diffs =
            new Dictionary<string, ComponentDiffView>(StringComparer.Ordinal);

        private static GUIStyle _shaStyle, _subjectStyle, _badgeStyle, _dimStyle, _removedStyle;

        // ------------------------------------------------------------ вход ---

        /// <param name="focusSha">Коммит, к которому прокрутить ленту; null — с начала.</param>
        public static void ShowScene(string scenePath, string focusSha = null)
        {
            var w = Open();
            w.SetFile(scenePath);
            w._focusSha = focusSha;
            w.ResetFocusContext();
            w.Go(SceneHistoryScope.Scene, 0, 0, null, null);
            w.Reload();
        }

        /// <param name="inspector">Компоненты объекта — инспекторами, а не списком правок.</param>
        /// <param name="compareLive">Сравнивать с текущим состоянием, а не с прошлой версией.</param>
        public static void ShowObject(SceneObjectAddress address, string focusSha = null,
                                      bool inspector = false, bool compareLive = false)
        {
            var w = Open();
            w.SetFile(address.ScenePath);
            w._focusSha = focusSha;
            w.ResetFocusContext();
            w.Go(SceneHistoryScope.Object, address.FileId, 0, address.Name, null);
            w._inspector = inspector;
            w._compareLive = compareLive;
            w._focusOnly = focusSha != null;
            w.Reload();
        }

        /// <param name="focusSha">Коммит, к которому прокрутить ленту; null — с начала.</param>
        /// <param name="compareLive">Сравнивать с текущим состоянием, а не с прошлой версией.</param>
        public static void ShowComponent(SceneObjectAddress owner, SceneObjectAddress component,
                                         string typeName, string focusSha = null, bool compareLive = false)
        {
            var w = Open();
            w.SetFile(component.ScenePath);
            w._focusSha = focusSha;
            w.ResetFocusContext();

            // Объект передаётся вместе с компонентом: без него «хлебная крошка»
            // вела бы в никуда, а шаг назад показал бы историю пустого адреса.
            w.Go(SceneHistoryScope.Component, owner.FileId, component.FileId, owner.Name, typeName);
            w._compareLive = compareLive;
            w._focusOnly = focusSha != null;
            w.Reload();
        }

        private bool IsPrefab => _scenePath != null && _scenePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

        private static SceneHistoryWindow Open()
        {
            var w = GetWindow<SceneHistoryWindow>(false, L.T("Scene History"), true);
            w.minSize = new Vector2(520f, 300f);
            w.Show();
            w.Focus();
            return w;
        }

        private void Go(SceneHistoryScope scope, long objectId, long componentId,
                        string objectTitle, string componentTitle)
        {
            _scope = scope;
            _objectId = objectId;
            _componentId = componentId;
            if (objectTitle != null) _objectTitle = objectTitle;
            if (componentTitle != null) _componentTitle = componentTitle;

            UpdateTitle();

            DropDiffs();
            _shown = PageSize;
            _scroll = Vector2.zero;
            Repaint();
        }

        private void UpdateTitle()
        {
            titleContent = new GUIContent(
                _scope == SceneHistoryScope.Scene ? L.T("Scene History")
                : _scope == SceneHistoryScope.Object ? L.Tc("one object", "Object History")
                : L.T("Component History"));
        }

        /// <summary>Заголовок задаётся при переходе между уровнями — при смене языка его надо переставить.</summary>
        void ILocalizedWindow.OnLanguageChanged()
        {
            UpdateTitle();
            Repaint();
        }

        private int PageSize
        {
            get { return InspectorView ? ComponentPage : ScenePage; }
        }

        private void OnEnable()
        {
            ComponentDiffView.SweepStrays();

            // Unity переносит приватные поля окна через перезагрузку домена, но null
            // в строке возвращает как "". Здесь null значит «нет»: без этого окно
            // показало бы баннер «история от коммита » и читало бы ленту от пустой ревизии.
            if (_fromRev == string.Empty) _fromRev = null;
            if (_fromLabel == string.Empty) _fromLabel = null;
            if (_focusSha == string.Empty) _focusSha = null;
            if (_scenePath == string.Empty) _scenePath = null;

            // Подсветка ссылок при наведении работает только с событиями движения.
            wantsMouseMove = true;
        }

        private void OnDisable()
        {
            if (_cancel != null) _cancel.Cancel();
            DropDiffs();
        }

        /// <summary>Сравнение с текущим состоянием должно видеть правки, сделанные в инспекторе или на сцене.</summary>
        private void OnInspectorUpdate()
        {
            if (_compareLive && InspectorView) Repaint();
        }

        private void DropDiffs()
        {
            foreach (var d in _diffs.Values) d.Dispose();
            _diffs.Clear();
            _parts.Clear();
        }

        // ------------------------------------------------ экземпляр префаба ---

        /// <summary>Объекты и компоненты префаба внутри экземпляра — по событию, чтобы не разбирать каждый кадр.</summary>
        private readonly Dictionary<SceneEvent, List<InstancePart>> _parts = new Dictionary<SceneEvent, List<InstancePart>>();

        private List<InstancePart> PartsOf(SceneEvent e)
        {
            if (e == null || !e.IsPrefabInstance || e.Node == null) return null;
            if (!_parts.TryGetValue(e, out var parts))
            {
                parts = PrefabInstanceParts.Build(e.Node.OldDoc, e.Node.NewDoc);
                _parts[e] = parts;
            }
            return parts;
        }

        private GameObject FindInstanceFor(SceneEvent e)
        {
            if (e == null || IsPrefab || e.Node == null) return null;
            var doc = e.Node.NewDoc ?? e.Node.OldDoc;
            return SceneObjectRef.FindInstance(_scenePath, e.ObjectId, e.SourcePrefabGuid, PrefabOverrides.NameOf(doc));
        }

        /// <summary>
        /// Строки объектов и компонентов внутри экземпляра. В дереве сцены — только
        /// названия с сутью правки; в списке объекта — ещё и сами правки под ними.
        /// </summary>
        private void DrawParts(SceneRevision rev, SceneEvent e, List<InstancePart> parts, int depth, bool withProps)
        {
            // Как в иерархии: правки корня экземпляра — прямо под его строкой, дети —
            // вложенными строками по имени; объект без своих правок — ради детей.
            var rootName = PrefabInstanceParts.InstanceName(e.Node.NewDoc ?? e.Node.OldDoc);
            int skipBelow = int.MaxValue;

            foreach (var entry in PrefabInstanceParts.Layout(parts, rootName))
            {
                if (entry.Level > skipBelow) continue;
                skipBelow = int.MaxValue;

                var part = entry.Part;
                int rowDepth = depth + Math.Max(0, entry.Level - 1);
                int inner = entry.Level == 0 ? depth : rowDepth + 1;

                if (entry.Level > 0)
                {
                    var key = rev.Sha + "|part|" + e.ObjectId + "|" + entry.Key;
                    bool expanded = !_folded.Contains(key);
                    var row = entry;

                    var action = DrawTreeRow(rowDepth, true, ref expanded, SceneHistoryIcons.GameObjectIcon, entry.Name,
                        expanded || part == null ? string.Empty : PrefabInstanceParts.Summary(part.AllChanges),
                        part != null ? e : null, true,
                        part != null
                            ? L.T("Object inside a prefab instance. Click — changes as an inspector. Right-click — actions.")
                            : L.T("The object itself didn't change — its changed children are here. Right-click — actions."));
                    SetFolded(key, expanded);
                    HandlePartAction(action, e, row.Key, part != null ? part.AllKeys : null, null, part != null ? part.Asset : null);

                    if (!expanded) { skipBelow = entry.Level; continue; }
                }

                if (part == null) continue;
                if (withProps) DrawChangesInline(part.Own, inner);

                foreach (var component in part.Components)
                {
                    var c = component;
                    var p = part;
                    var componentKey = rev.Sha + "|part|" + e.ObjectId + "|" + c.Key;
                    bool open = withProps && !_folded.Contains(componentKey);

                    var a = DrawTreeRow(inner, withProps, ref open, PrefabInstanceParts.ComponentIcon(c.Asset), c.Title,
                        open ? string.Empty : PrefabInstanceParts.Summary(c.Changes), e, true,
                        L.T("Component inside a prefab instance. Click — changes as an inspector. Right-click — actions."));
                    if (withProps) SetFolded(componentKey, open);
                    HandlePartAction(a, e, p.Key, p.AllKeys, c, c.Asset);

                    if (open) DrawChangesInline(c.Changes, inner + 1);
                }
            }
        }

        private void HandlePartAction(RowAction action, SceneEvent e, string objectKey, List<string> allKeys,
                                      InstanceComponentPart component, UnityEngine.Object prefabTarget)
        {
            if (action == RowAction.Open) Defer(() => DrillInspector(e, true, false));
            else if (action == RowAction.Menu) ShowPartMenu(e, objectKey, allKeys, component, prefabTarget);
        }

        private void ShowPartMenu(SceneEvent e, string objectKey, List<string> allKeys, InstanceComponentPart component,
                                  UnityEngine.Object prefabTarget)
        {
            var menu = new GenericMenu();
            bool gone = e.Kind == SceneEventKind.Removed;

            if (gone) menu.AddDisabledItem(new GUIContent(L.T("Select in Scene") + " — " + L.T("instance removed in this commit")));
            else menu.AddItem(new GUIContent(L.T("Select in Scene")), false, () => GoToPart(e, objectKey, allKeys, component));

            menu.AddItem(new GUIContent(L.T("Show Change as Inspector")), false, () => Defer(() => DrillInspector(e, true, false)));
            if (gone) menu.AddDisabledItem(new GUIContent(L.T("Compare with Current State") + " — " + L.T("removed in this commit")));
            else menu.AddItem(new GUIContent(L.T("Compare with Current State")), false, () => Defer(() => DrillInspector(e, true, true)));

            AddPrefabSourceItems(menu, e, prefabTarget);

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.T("Open Commit in Log")), false, () => OpenCommit(e.Revision));
            menu.ShowAsContext();
        }

        /// <summary>Пункты про исходный префаб: показать в Project, открыть на этом объекте, история файла.</summary>
        private void AddPrefabSourceItems(GenericMenu menu, SceneEvent e, UnityEngine.Object prefabTarget)
        {
            var prefabPath = PrefabPathOf(e);
            if (prefabPath == null)
            {
                menu.AddDisabledItem(new GUIContent(L.T("Prefab — prefab file not found")));
                return;
            }

            var file = System.IO.Path.GetFileName(prefabPath);
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.F("Show Prefab “{0}” in Project", file)), false,
                         () => PrefabInstanceParts.PingPrefab(e.SourcePrefabGuid));
            menu.AddItem(new GUIContent(prefabTarget != null ? L.T("Open Prefab at This Object") : L.T("Open Prefab")), false,
                         () => Defer(() => PrefabInstanceParts.OpenInPrefab(e.SourcePrefabGuid, prefabTarget)));
            menu.AddItem(new GUIContent(L.F("Prefab History “{0}”", file)), false, () => ShowScene(prefabPath));
        }

        /// <summary>Выделить объект или компонент экземпляра на сцене; закрытую сцену — предложить открыть.</summary>
        private void GoToPart(SceneEvent e, string objectKey, List<string> allKeys, InstanceComponentPart component)
        {
            var root = FindInstanceFor(e);
            if (root == null && !IsPrefab)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(_scenePath);
                if (!scene.isLoaded &&
                    EditorUtility.DisplayDialog(L.T("Scene Not Open"), L.F("To go to the object, open {0}.", Ui.NameOf(_scenePath)),
                        L.T("Open Scene"), L.T("Cancel")))
                {
                    OpenScene();
                    root = FindInstanceFor(e);
                }
            }

            if (root == null)
            {
                ShowNotification(new GUIContent(IsPrefab ? L.T("A nested prefab object can't be selected — open the prefab") : L.T("No instance in the scene")));
                return;
            }

            UnityEngine.Object target = component != null ? SceneObjectRef.InstanceObject(root, component.Key) : null;
            if (target == null) target = SceneObjectRef.InstanceObject(root, objectKey);
            if (target == null && allKeys != null)
                foreach (var key in allKeys)
                    if ((target = SceneObjectRef.InstanceObject(root, key)) != null) break;

            if (!SceneObjectRef.FocusObject(target ?? root))
                ShowNotification(new GUIContent(L.T("This object is no longer in the instance")));
        }

        private void DrawChangesInline(List<PrefabModificationChange> changes, int depth)
        {
            float offset = 4f + depth * IndentWidth + 33f;
            foreach (var change in changes)
            {
                var rect = GUILayoutUtility.GetRect(0f, 16f, GUILayout.ExpandWidth(true));
                float x = rect.x + offset;
                float width = Mathf.Max(0f, rect.xMax - x - 8f);
                float keyWidth = Mathf.Min(width * 0.45f, 260f);

                GUI.Label(new Rect(x, rect.y, keyWidth, 16f), change.PropertyPath, _dimStyle);
                GUI.Label(new Rect(x + keyWidth, rect.y, width - keyWidth, 16f),
                          Short(change.OldText) + "  →  " + Short(change.NewText), EditorStyles.miniLabel);
            }
        }

        /// <summary>Экземпляр инспекторами: каждый компонент префаба с изменившимися переопределениями.</summary>
        private void DrawInstanceInspector(SceneRevision rev, SceneEvent e)
        {
            var parts = PartsOf(e);
            if (parts == null || parts.Count == 0)
            {
                EditorGUILayout.LabelField(L.T("Instance overrides didn't change."), EditorStyles.miniLabel);
                return;
            }

            GameObject root = null;
            bool rootLooked = false;

            foreach (var part in parts)
            {
                if (part.Own.Count > 0)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label(new GUIContent(SceneHistoryIcons.GameObjectIcon), GUILayout.Width(16f), GUILayout.Height(16f));
                            var p = part;
                            if (GUILayout.Button(new GUIContent(part.Title, L.T("Select the object in the scene")), _subjectStyle, GUILayout.ExpandWidth(false)))
                                Defer(() => GoToPart(e, p.Key, p.AllKeys, null));
                            Link();
                            GUILayout.Label(L.Tc("row kind", "object"), _dimStyle);
                            GUILayout.FlexibleSpace();
                        }
                        DrawChangesTable(part.Own);
                    }
                }

                foreach (var component in part.Components)
                {
                    var c = component;
                    var p = part;
                    var diffKey = rev.Sha + "|i" + e.ObjectId + "|" + c.Key;
                    if (!_diffs.TryGetValue(diffKey, out var view))
                    {
                        var before = e.Node.OldDoc != null ? PrefabInstanceParts.EntriesFor(e.Node.OldDoc, c.Key) : null;
                        var after = e.Node.NewDoc != null ? PrefabInstanceParts.EntriesFor(e.Node.NewDoc, c.Key) : null;
                        view = new ComponentDiffView(c.Asset, before, after);
                        _diffs[diffKey] = view;
                    }

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label(new GUIContent(PrefabInstanceParts.ComponentIcon(c.Asset)), GUILayout.Width(16f), GUILayout.Height(16f));
                            if (GUILayout.Button(new GUIContent(c.Title, L.T("Select the instance object and expand this component")),
                                    _subjectStyle, GUILayout.ExpandWidth(false)))
                                Defer(() => GoToPart(e, p.Key, p.AllKeys, c));
                            Link();
                            GUILayout.Label(part.Title + " · " + PrefabInstanceParts.Summary(c.Changes), _dimStyle);
                            GUILayout.FlexibleSpace();
                        }

                        if (_compareLive && view.Ready)
                        {
                            if (!rootLooked) { root = FindInstanceFor(e); rootLooked = true; }
                            var live = root != null ? SceneObjectRef.InstanceObject(root, c.Key) as Component : null;
                            if (live == null)
                            {
                                DrawNoLive(e);
                                continue;
                            }
                            view.SetLive(live, LiveReadOnlyReason());
                        }

                        if (view.Ready) view.Draw(position.width - 40f, _onlyChanged, _compareLive);
                        else
                        {
                            if (!string.IsNullOrEmpty(view.Problem)) EditorGUILayout.LabelField(view.Problem, EditorStyles.miniLabel);
                            DrawChangesTable(c.Changes);
                        }
                    }
                }
            }
        }

        private static void DrawChangesTable(List<PrefabModificationChange> changes)
        {
            foreach (var change in changes)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(new GUIContent(change.PropertyPath, change.PropertyPath), EditorStyles.label,
                                    GUILayout.MinWidth(80f), GUILayout.MaxWidth(280f));
                    GUILayout.Label(change.OldText, EditorStyles.miniLabel, GUILayout.MinWidth(30f));
                    GUILayout.Label("→", EditorStyles.miniLabel, GUILayout.Width(16f));
                    GUILayout.Label(change.NewText, EditorStyles.miniLabel, GUILayout.MinWidth(30f));
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // --------------------------------------------------------- загрузка ---

        private async void Reload()
        {
            if (string.IsNullOrEmpty(_scenePath)) return;

            // Новый запрос во время чтения не теряется: текущее чтение
            // отменяется, а новое стартует, как только оно остановится.
            if (_loading)
            {
                _reloadQueued = true;
                if (_cancel != null) _cancel.Cancel();
                return;
            }

            _loading = true;
            _cancel = new CancellationTokenSource();
            DropDiffs();

            var path = _scenePath;

            try
            {
                var fromRev = _fromRev;
                var result = await SceneHistory.BuildAsync(
                    path, _depth,
                    message => { _status = message; Repaint(); },
                    _cancel.Token, fromRev);

                // Пока читали, окно могли переключить на другой файл или ветку.
                if (path != _scenePath || fromRev != _fromRev) return;

                if (result.Canceled)
                {
                    if (!_reloadQueued) _status = L.T("History reading canceled.");
                    return;
                }

                if (result.Error != null)
                {
                    _status = result.Error;
                    _result = null;
                    return;
                }

                _result = result;
                _status = string.Empty;

                // Лента по умолчанию свёрнута целиком: раскрывается только коммит,
                // ради которого открыли окно, — даже если его в ленте не нашлось.
                foreach (var rev in result.Revisions)
                {
                    if (!_seen.Add(rev.Sha)) continue;
                    if (rev.Sha != _focusSha) _collapsed.Add(rev.Sha);
                }

                ApplyFocus();
            }
            finally
            {
                _loading = false;
                if (_cancel != null) { _cancel.Dispose(); _cancel = null; }

                if (_reloadQueued)
                {
                    _reloadQueued = false;
                    Reload();
                }

                Repaint();
            }
        }

        // ------------------------------------------------------------- UI ---

        private void OnGUI()
        {
            EnsureStyles();

            if (_pending != null && Event.current.type == EventType.Layout)
            {
                var action = _pending;
                _pending = null;
                action();
            }

            if (Event.current.type == EventType.MouseMove) Repaint();
            DrawToolbar();

            if (_result == null)
            {
                EditorGUILayout.Space(20f);
                EditorGUILayout.LabelField(
                    _loading ? (_status.Length > 0 ? _status : L.T("Reading history…"))
                             : (_status.Length > 0 ? _status : L.T("History not loaded.")),
                    EditorStyles.centeredGreyMiniLabel);
                DrawFooter();
                return;
            }

            var commits = Selected();

            if (commits.Count == 0)
            {
                EditorGUILayout.Space(20f);
                EditorGUILayout.LabelField(
                    _result.Revisions.Count == 0
                        ? L.T("The scene file has never been committed.")
                        : L.T("Nothing found — clear the filter or look deeper."),
                    EditorStyles.centeredGreyMiniLabel);
                DrawFooter();
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            int limit = Mathf.Min(_shown, commits.Count);
            for (int i = 0; i < limit; i++) DrawCommit(commits[i]);

            if (commits.Count > limit)
            {
                EditorGUILayout.Space(6f);
                if (GUILayout.Button(L.F("Show {0} More of {1}",
                        Mathf.Min(PageSize, commits.Count - limit), commits.Count - limit)))
                    _shown += PageSize;
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        private static void EnsureStyles()
        {
            if (_shaStyle != null) return;

            _shaStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                fixedWidth = 0f
            };

            _subjectStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };

            _badgeStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };

            _dimStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = false };

            // Ссылка — обычная подпись, которая подсвечивается при наведении:
            // синий текст на каждой строке превратил бы дерево в пёстрый список.
            var accent = GitPalette.Fill(GitFileStatus.Modified);

            // Удалённое — приглушённым, как неактивный объект в иерархии.
            _removedStyle = new GUIStyle(EditorStyles.label);
            _removedStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.55f, 0.55f, 0.55f)
                : new Color(0.45f, 0.45f, 0.45f);

            _subjectStyle.hover.textColor = accent;
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                DrawCrumbs();

                GUILayout.FlexibleSpace();

                _filter = GUILayout.TextField(_filter, EditorStyles.toolbarSearchField,
                                              GUILayout.Width(140f));

                using (new EditorGUI.DisabledScope(_loading))
                {
                    if (GUILayout.Button(L.T("Refresh"), EditorStyles.toolbarButton, GUILayout.Width(70f)))
                        Reload();
                }
            }

            DrawViewOptions();
            DrawContextBanners();
        }

        /// <summary>
        /// Что сейчас показано на самом деле. Лента выглядит одинаково и для
        /// текущей ветки, и для чужой, и когда нужного коммита в ней нет, — без
        /// этой строки легко решить, что искомый коммит где-то ниже.
        /// </summary>
        private void DrawContextBanners()
        {
            if (_fromRev != null)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(EditorGUIUtility.IconContent("console.infoicon.sml"), GUILayout.Width(18f), GUILayout.Height(18f));
                    GUILayout.Label(L.F("Showing history from commit {0}, not the current branch “{1}”. " +
                                        "“Compare with Current State” still compares with the open scene.",
                                        _fromLabel, GitStatusCache.Branch ?? "HEAD"),
                                    EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button(L.T("To Current Branch"), GUILayout.Width(120f)))
                        Defer(() => ReadFrom(null, null, _focusSha));
                }
            }

            if (_focusNote == null) return;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(18f), GUILayout.Height(18f));
                GUILayout.Label(_focusNote, EditorStyles.wordWrappedMiniLabel);

                if (_focusAction != null && GUILayout.Button(_focusActionLabel, GUILayout.Width(Mathf.Min(260f, EditorStyles.miniButton.CalcSize(new GUIContent(_focusActionLabel)).x + 24f))))
                {
                    var action = _focusAction;
                    Defer(action);
                }

                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20f)))
                    Defer(() => { _focusNote = null; _focusAction = null; });
            }
        }

        /// <summary>Окно открыли с новым коммитом — прежние плашки и чужая ветка к нему не относятся.</summary>
        private void ResetFocusContext()
        {
            _focusNote = null;
            _focusAction = null;

            if (_fromRev == null) return;
            _fromRev = null;
            _fromLabel = null;
            _result = null;
            Reload();
        }

        /// <summary>Читает ленту от ревизии (null — от текущей ветки) и снова ищет в ней коммит.</summary>
        private void ReadFrom(string rev, string label, string focus)
        {
            _fromRev = rev;
            _fromLabel = label;
            _focusSha = focus;
            _focusNote = null;
            _focusAction = null;
            _focusOnly = true;
            _depth = DepthStep;
            _collapsed.Clear();
            _seen.Clear();
            _result = null;
            Reload();
        }

        private void SetFocusNote(string text, string actionLabel, Action action)
        {
            _focusNote = text;
            _focusActionLabel = actionLabel;
            _focusAction = action;
            Repaint();
        }

        /// <summary>
        /// Коммита нет в ленте. Лента идёт по первым родителям, поэтому причин три:
        /// его правки принесло слияние; он в другой ветке; до него ещё не дочитали.
        /// </summary>
        private async void ResolveMissingFocusAsync(string sha)
        {
            var start = _fromRev ?? "HEAD";
            var shortSha = sha.Length > 7 ? sha.Substring(0, 7) : sha;

            var subjectResult = await GitOperations.Git("log -1 --format=%s " + GitOperations.Q(sha));
            var subject = subjectResult.Ok ? subjectResult.StdOut.Trim() : string.Empty;
            var title = shortSha + (subject.Length > 0 ? " " + L.F("“{0}”", subject) : string.Empty);

            var ancestor = await GitOperations.Git("merge-base --is-ancestor " + GitOperations.Q(sha) + " " + GitOperations.Q(start));
            if (sha != _focusSha || _result == null) return;

            if (ancestor.ExitCode == 0)
            {
                // Самый старый коммит главной линии, в который входит искомый, — слияние, что его принесло.
                var chain = await GitOperations.Git("rev-list --first-parent --ancestry-path " + GitOperations.Q(sha + ".." + start));
                if (sha != _focusSha || _result == null) return;

                var lines = chain.Ok ? chain.StdOut.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries) : new string[0];
                var merge = lines.Length > 0 ? lines[lines.Length - 1].Trim() : null;

                if (merge != null && _result.Revisions.Exists(r => r.Sha == merge))
                {
                    var mergeShort = merge.Substring(0, 7);
                    SetFocusNote(L.F("Commit {0} is from a branch that was merged into this one. The timeline shows the branch history by merges, " +
                                     "so its changes are visible in merge {1} — it's expanded. The commit itself is visible in its branch's history.",
                                     title, mergeShort),
                                 L.T("Commit's Branch History"), () => ReadFrom(sha, title, sha));

                    _focusSha = merge;
                    ApplyFocus();
                    return;
                }

                if (_result.More)
                {
                    SetFocusNote(L.F("Commit {0} is in the branch, but deeper than the loaded history.", title),
                                 L.T("Load More"), LoadDeeper);
                    return;
                }

                SetFocusNote(L.F("Commit {0} is in the branch, but the merge that brought it didn't change the file: " +
                                 "these changes were later overwritten or reverted.", title),
                             L.T("History from This Commit"), () => ReadFrom(sha, title, sha));
                return;
            }

            var branchesResult = await GitOperations.Git("branch -a --contains " + GitOperations.Q(sha) + " --format=%(refname:short)");
            if (sha != _focusSha || _result == null) return;

            var branches = branchesResult.Ok
                ? branchesResult.StdOut.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(b => b.Trim()).ToList()
                : new List<string>();

            var where = branches.Count == 0 ? L.T("in no branch — only in the reflog")
                : L.F("only in {0}", string.Join(", ", branches.Take(3).ToArray())) +
                  (branches.Count > 3 ? " " + L.F("and {0} more", branches.Count - 3) : string.Empty);

            SetFocusNote(L.F("Commit {0} isn't in the current branch “{1}” — it's {2}. " +
                             "The timeline above is the current branch's history; this commit isn't in it.",
                             title, GitStatusCache.Branch ?? "HEAD", where),
                         L.T("History from This Commit"), () => ReadFrom(sha, title, sha));
        }

        /// <summary>
        /// Настройки вида — отдельной строкой и разными элементами по смыслу:
        /// выбор из вариантов — переключателем или списком с подписью, вкл/выкл —
        /// флажком. Кнопки одного вида подряд читались как одна группа.
        /// </summary>
        private void DrawViewOptions()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (_scope == SceneHistoryScope.Object)
                {
                    GUILayout.Label(L.T("View"), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    bool inspector = GUILayout.Toolbar(_inspector ? 1 : 0, new[]
                    {
                        L.C("List", "Component changes as rows"),
                        L.C("Inspector", "Each component as an inspector, like on the object")
                    }, EditorStyles.toolbarButton, GUILayout.Width(150f)) == 1;

                    if (inspector != _inspector)
                    {
                        _inspector = inspector;
                        _shown = PageSize;
                    }

                    OptionsSeparator();
                }

                if (InspectorView)
                {
                    GUILayout.Label(L.T("Compare With"), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    _compareLive = EditorGUILayout.Popup(_compareLive ? 1 : 0, new[]
                    {
                        L.C("previous version", "Left — before the commit, right — after"),
                        L.C("current state", "Left — after the commit, right — now; the current state is editable")
                    }, EditorStyles.toolbarPopup, GUILayout.Width(140f)) == 1;

                    OptionsSeparator();
                    _onlyChanged = EditorGUILayout.ToggleLeft(
                        L.C("Only Changed Fields", "Hide fields that don't differ"),
                        _onlyChanged, GUILayout.Width(170f));
                }
                else
                {
                    _onlyStructure = EditorGUILayout.ToggleLeft(
                        L.C("Only Added and Removed", "Hide property changes"),
                        _onlyStructure, GUILayout.Width(210f));
                }

                GUILayout.FlexibleSpace();
            }
        }

        private static void OptionsSeparator()
        {
            GUILayout.Space(6f);
            var rect = GUILayoutUtility.GetRect(1f, 14f, GUILayout.Width(1f));
            rect.y += 3f;
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.4f));
            GUILayout.Space(8f);
        }

        private void DrawCrumbs()
        {
            Crumb(Ui.NameOf(_scenePath ?? string.Empty), SceneHistoryScope.Scene);

            if (_scope != SceneHistoryScope.Scene && !string.IsNullOrEmpty(_objectTitle))
            {
                GUILayout.Label("›", EditorStyles.miniLabel, GUILayout.Width(10f));
                Crumb(_objectTitle, SceneHistoryScope.Object);
            }

            if (_scope == SceneHistoryScope.Component && !string.IsNullOrEmpty(_componentTitle))
            {
                GUILayout.Label("›", EditorStyles.miniLabel, GUILayout.Width(10f));
                Crumb(_componentTitle, SceneHistoryScope.Component);
            }
        }

        private void Crumb(string text, SceneHistoryScope scope)
        {
            bool current = scope == _scope;
            var style = current ? EditorStyles.boldLabel : EditorStyles.label;
            var content = new GUIContent(text);
            float width = Mathf.Min(style.CalcSize(content).x + 6f, 180f);

            // Назад ведут только уровни выше текущего: вперёд идут щелчком
            // по объекту или компоненту в самой ленте.
            if (current || scope > _scope)
            {
                GUILayout.Label(content, style, GUILayout.Width(width));
                return;
            }

            if (GUILayout.Button(content, EditorStyles.toolbarButton, GUILayout.Width(width)))
            {
                Go(scope, _objectId, scope == SceneHistoryScope.Object ? 0 : _componentId, null, null);
            }
        }

        private void DrawFooter()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(_loading ? _status : Summary(), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();

                // Дочитывать глубже дёшево: уже разобранные версии берутся из
                // кэша на диске, заново разбираются только новые. Сам запуск —
                // на следующем кадре: чтение сразу меняет состав этой полосы.
                if (!_loading && _result != null && _result.More &&
                    GUILayout.Button(L.F("Load {0} More Versions", DepthStep),
                                     EditorStyles.toolbarButton, GUILayout.Width(170f)))
                    Defer(LoadDeeper);

                if (_loading && GUILayout.Button(L.Tc("cancel reading", "Stop"), EditorStyles.toolbarButton, GUILayout.Width(70f)))
                {
                    if (_cancel != null) _cancel.Cancel();
                }
            }
        }

        private string Summary()
        {
            if (_result == null) return string.Empty;

            return L.F("Versions read: {0}{1}{2}",
                _result.Revisions.Count,
                _result.FromCache > 0 ? " · " + L.F("from cache {0}", _result.FromCache) : string.Empty,
                _result.More ? " · " + L.T("history is longer") : string.Empty);
        }

        // ------------------------------------------------------- отбор ---

        /// <summary>Коммиты с их событиями, в порядке журнала и без пустых.</summary>
        private List<KeyValuePair<SceneRevision, List<SceneEvent>>> Selected()
        {
            var result = new List<KeyValuePair<SceneRevision, List<SceneEvent>>>();

            foreach (var rev in _result.Revisions)
            {
                List<SceneEvent> own = null;

                foreach (var e in _result.Events)
                {
                    if (!ReferenceEquals(e.Revision, rev) || !Matches(e)) continue;
                    if (own == null) own = new List<SceneEvent>();
                    own.Add(e);
                }

                if (own != null)
                    result.Add(new KeyValuePair<SceneRevision, List<SceneEvent>>(rev, own));
            }

            return result;
        }

        private bool Matches(SceneEvent e)
        {
            if (_scope == SceneHistoryScope.Object && e.ObjectId != _objectId) return false;
            if (_scope == SceneHistoryScope.Component && e.ComponentId != _componentId) return false;

            if (_onlyStructure && _scope != SceneHistoryScope.Component &&
                e.Kind != SceneEventKind.Added && e.Kind != SceneEventKind.Removed) return false;

            if (_filter.Length == 0) return true;

            if (Has(e.ObjectPath) || Has(e.ComponentType) || Has(TypeTitle(e)) || Has(e.Revision.Subject) ||
                Has(e.Revision.Author) || Has(e.OldName)) return true;

            foreach (var p in e.Props) if (Has(p.Path)) return true;
            return false;
        }

        private bool Has(string text)
        {
            return !string.IsNullOrEmpty(text) &&
                   text.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------ коммит ---

        private void DrawCommit(KeyValuePair<SceneRevision, List<SceneEvent>> entry)
        {
            var rev = entry.Key;
            bool open = !_collapsed.Contains(rev.Sha);

            // Отступ перед коммитом — граница, которую глаз видит сразу, даже когда
            // внутри много похожих блоков компонентов.
            EditorGUILayout.Space(8f);

            var header = EditorGUILayout.BeginHorizontal(EditorStyles.helpBox, GUILayout.Height(22f));
            bool focused = rev.Sha == _focusSha;

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(header, HeaderTint);

                // Коммит, с которым окно открыли из журнала, — полоской слева:
                // глазу нужно за что-то зацепиться в длинной ленте.
                if (focused)
                    EditorGUI.DrawRect(new Rect(header.x, header.y, 3f, header.height),
                                       GitPalette.Fill(GitFileStatus.Modified));

                // Прокрутка — на перерисовке: только тогда известно, где в ленте
                // оказался заголовок этого коммита.
                if (focused && _scrollToFocus)
                {
                    _scrollToFocus = false;
                    _scroll.y = Mathf.Max(0f, header.y - 6f);
                    Repaint();
                }
            }

            if (GUILayout.Button(open ? "▾" : "▸", _dimStyle, GUILayout.Width(12f)))
            {
                if (!_collapsed.Remove(rev.Sha)) _collapsed.Add(rev.Sha);
                Repaint();
            }

            // Хеш и заголовок — ссылка в журнал: там у коммита полный разбор,
            // файлы, превью и действия.
            var tip = L.T("Open Commit in Log");
            if (GUILayout.Button(new GUIContent(rev.ShortSha, tip), _shaStyle, GUILayout.Width(56f)))
                OpenCommit(rev);
            Link();

            if (GUILayout.Button(new GUIContent(rev.Subject, tip), _subjectStyle))
                OpenCommit(rev);
            Link();

            GUILayout.FlexibleSpace();
            GUILayout.Label(rev.Author, _dimStyle);
            GUILayout.Label(Ago(rev.Date), _dimStyle, GUILayout.Width(84f));

            EditorGUILayout.EndHorizontal();

            if (Event.current.type == EventType.MouseDown && Event.current.button == 1 &&
                header.Contains(Event.current.mousePosition))
            {
                ShowCommitMenu(rev);
                Event.current.Use();
            }

            if (!open) return;

            // Содержимое коммита — с отступом и полосой слева: сразу видно, какие
            // блоки к какому коммиту относятся и где начинается следующий.
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12f);
            var body = EditorGUILayout.BeginVertical();

            switch (_scope)
            {
                case SceneHistoryScope.Scene: DrawTree(rev, entry.Value); break;
                case SceneHistoryScope.Object:
                    if (_inspector) DrawInspector(rev, entry.Value);
                    else DrawComponentList(rev, entry.Value);
                    break;
                default: DrawInspector(rev, entry.Value); break;
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            if (Event.current.type == EventType.Repaint)
            {
                var rail = focused ? GitPalette.Fill(GitFileStatus.Modified) : new Color(0.5f, 0.5f, 0.5f, 0.35f);
                EditorGUI.DrawRect(new Rect(body.x - 8f, body.y, 2f, body.height), rail);
            }
        }

        private static Color HeaderTint
        {
            get
            {
                return EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.09f)
                    : new Color(0f, 0f, 0f, 0.08f);
            }
        }

        private void ShowCommitMenu(SceneRevision rev)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(L.T("Open in Log")), false, () => OpenCommit(rev));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.T("Copy SHA")), false,
                         () => EditorGUIUtility.systemCopyBuffer = rev.Sha);
            menu.AddItem(new GUIContent(L.T("Show File in Log")), false,
                         () => GitWindow.ShowFileHistory(_scenePath));
            menu.ShowAsContext();
        }

        // -------------------------------------------- сцена: дерево объектов ---

        private enum RowAction { None, Open, Menu }

        private const float RowHeight = 18f;
        private const float IndentWidth = 14f;

        private void DrawTree(SceneRevision rev, List<SceneEvent> events)
        {
            var objects = new List<SceneEvent>();
            var settings = new List<SceneEvent>();

            foreach (var e in events)
            {
                if (e.IsSceneSettings) settings.Add(e);
                else objects.Add(e);
            }

            foreach (var node in BuildTree(objects)) DrawNode(rev, node, 0);

            if (settings.Count > 0) DrawSettingsGroup(rev, settings);
        }

        /// <summary>
        /// Собирает дерево так, как его показала бы иерархия: по пути объекта.
        /// Промежуточные узлы могут не иметь собственного события — их в этом
        /// коммите не меняли, но без них ребёнок повис бы в воздухе.
        /// </summary>
        private static List<TreeNode> BuildTree(List<SceneEvent> events)
        {
            var roots = new List<TreeNode>();
            var byPath = new Dictionary<string, TreeNode>(StringComparer.Ordinal);

            foreach (var e in events)
            {
                var path = string.IsNullOrEmpty(e.ObjectPath) ? L.T("Scene") : e.ObjectPath;
                var node = Ensure(roots, byPath, path);

                if (e.IsObjectLevel) node.Own = e;
                else node.Components.Add(e);
            }

            return roots;
        }

        private static TreeNode Ensure(List<TreeNode> roots, Dictionary<string, TreeNode> byPath, string path)
        {
            TreeNode existing;
            if (byPath.TryGetValue(path, out existing)) return existing;

            int slash = path.LastIndexOf('/');
            var name = slash < 0 ? path : path.Substring(slash + 1);

            var node = new TreeNode { Name = name, Path = path };
            byPath[path] = node;

            if (slash < 0) roots.Add(node);
            else Ensure(roots, byPath, path.Substring(0, slash)).Children.Add(node);

            return node;
        }

        private void DrawNode(SceneRevision rev, TreeNode node, int depth)
        {
            var key = rev.Sha + "|" + node.Path;
            var parts = node.Own != null ? PartsOf(node.Own) : null;
            bool expandable = node.Children.Count > 0 || node.Components.Count > 0 || (parts != null && parts.Count > 0);
            bool expanded = !_folded.Contains(key);

            var icon = node.Own != null && node.Own.IsPrefabInstance
                ? SceneHistoryIcons.PrefabIcon
                : SceneHistoryIcons.GameObjectIcon;

            // Объект, у которого в этом коммите менялись только компоненты, своего
            // события не имеет, но его адрес известен из событий компонентов.
            var anchor = node.Own ?? (node.Components.Count > 0 ? node.Components[0] : null);

            var action = DrawTreeRow(depth, expandable, ref expanded, icon, node.Name,
                                     Summary(node.Own), node.Own, anchor != null,
                                     anchor != null ? L.T("Click — object history. Right-click — select in scene.") : null);

            if (expandable) SetFolded(key, expanded);

            if (anchor != null)
            {
                if (action == RowAction.Open) Defer(() => Drill(anchor, true));
                else if (action == RowAction.Menu) ShowEventMenu(anchor, true);
            }

            if (!expanded) return;

            foreach (var component in node.Components)
            {
                var c = component;
                bool leaf = false;

                var a = DrawTreeRow(depth + 1, false, ref leaf,
                                    TypeIcon(c), TypeTitle(c),
                                    Summary(c), c, true,
                                    L.T("Click — component history. Right-click — go to it."));

                if (a == RowAction.Open) Defer(() => Drill(c, false));
                else if (a == RowAction.Menu) ShowEventMenu(c, false);
            }

            // Экземпляр префаба — объектами и компонентами префаба внутри него.
            if (parts != null && parts.Count > 0) DrawParts(rev, node.Own, parts, depth + 1, false);

            foreach (var child in node.Children) DrawNode(rev, child, depth + 1);
        }

        /// <summary>
        /// Служебные документы сцены — отдельной группой в конце.
        ///
        /// Это не объекты иерархии: RenderSettings, LightmapSettings и прочие
        /// есть в каждой сцене, в иерархии не видны и удалить их нельзя. Среди
        /// игровых объектов они выглядели бы как объекты, к которым почему-то
        /// не перейти. Группа свёрнута по умолчанию: SceneRoots, например,
        /// меняется при каждом добавлении корневого объекта и засоряла бы дерево.
        /// </summary>
        private void DrawSettingsGroup(SceneRevision rev, List<SceneEvent> settings)
        {
            bool expanded = _settingsOpen.Contains(rev.Sha);

            DrawTreeRow(0, true, ref expanded, SettingsIcon, L.T("Scene Settings"),
                        L.F("{0} · not hierarchy objects", settings.Count), null, false,
                        L.T("Service data that every scene has and that isn't visible in the hierarchy: " +
                            "environment and lighting, baking, occlusion, navigation, root object order."));

            if (expanded) _settingsOpen.Add(rev.Sha);
            else _settingsOpen.Remove(rev.Sha);

            if (!expanded) return;

            foreach (var item in settings)
            {
                var e = item;
                bool leaf = false;

                var a = DrawTreeRow(1, false, ref leaf, SettingsIcon, e.ObjectPath, Summary(e), e, true,
                                    SettingsTooltip(e.ObjectPath) + "\n" + L.T("Click — history. Right-click — actions."));

                if (a == RowAction.Open) Defer(() => Drill(e, true));
                else if (a == RowAction.Menu) ShowSettingsMenu(e);
            }
        }

        // ------------------------------------ объект: список компонентов ---

        /// <summary>
        /// Объект — как в инспекторе: строка самого объекта, под ней компоненты.
        /// Правки свойств раскрываются прямо под компонентом, а щелчок по нему
        /// ведёт в историю компонента с настоящим инспектором.
        /// </summary>
        private void DrawComponentList(SceneRevision rev, List<SceneEvent> events)
        {
            foreach (var item in events)
            {
                if (!item.IsObjectLevel) continue;

                var e = item;
                var key = rev.Sha + "|object";
                bool expandable = e.Props.Count > 0;
                bool expanded = expandable && !_folded.Contains(key);

                var icon = e.IsSceneSettings ? SettingsIcon
                         : e.IsPrefabInstance ? SceneHistoryIcons.PrefabIcon
                         : SceneHistoryIcons.GameObjectIcon;

                var tip = e.IsSceneSettings ? SettingsTooltip(e.ObjectPath) : L.T("Right-click — select in scene.");

                var a = DrawTreeRow(0, expandable, ref expanded, icon, e.ObjectPath,
                                    expanded ? string.Empty : Summary(e), e, false, tip);

                if (expandable) SetFolded(key, expanded);

                if (a == RowAction.Menu)
                {
                    if (e.IsSceneSettings) ShowSettingsMenu(e);
                    else ShowEventMenu(e, true);
                }

                if (expanded)
                {
                    // У экземпляра правки — не список путей, а объекты и компоненты префаба.
                    var parts = PartsOf(e);
                    if (parts != null) DrawParts(rev, e, parts, 1, true);
                    else DrawPropsInline(e, 1);
                }
            }

            foreach (var item in events)
            {
                if (item.IsObjectLevel) continue;

                var e = item;
                var key = rev.Sha + "|" + e.ComponentId;
                bool expandable = e.Props.Count > 0;
                bool expanded = expandable && !_folded.Contains(key);

                var a = DrawTreeRow(0, expandable, ref expanded,
                                    TypeIcon(e), TypeTitle(e),
                                    expanded ? string.Empty : Summary(e), e, true,
                                    L.T("Click — component history. Right-click — go to it."));

                if (expandable) SetFolded(key, expanded);

                if (a == RowAction.Open) Defer(() => Drill(e, false));
                else if (a == RowAction.Menu) ShowEventMenu(e, false);

                if (expanded) DrawPropsInline(e, 1);
            }
        }

        /// <summary>Правки свойств под строкой компонента: «свойство   было → стало».</summary>
        private void DrawPropsInline(SceneEvent e, int depth)
        {
            const int Max = 12;

            // Отступ выровнен по именам строк: треугольник и иконка — 33 пикселя.
            float offset = 4f + depth * IndentWidth + 33f;

            for (int i = 0; i < e.Props.Count; i++)
            {
                var rect = GUILayoutUtility.GetRect(0f, 16f, GUILayout.ExpandWidth(true));
                float x = rect.x + offset;
                float width = Mathf.Max(0f, rect.xMax - x - 8f);

                if (i == Max)
                {
                    GUI.Label(new Rect(x, rect.y, width, 16f),
                              L.F("{0} more — the full list is in the component history", e.Props.Count - Max), _dimStyle);
                    break;
                }

                var p = e.Props[i];
                float keyWidth = Mathf.Min(width * 0.45f, 260f);

                GUI.Label(new Rect(x, rect.y, keyWidth, 16f), p.Path, _dimStyle);
                GUI.Label(new Rect(x + keyWidth, rect.y, width - keyWidth, 16f),
                          Short(p.Old) + "  →  " + Short(p.New), EditorStyles.miniLabel);
            }

            if (e.HiddenProps > 0)
            {
                var rect = GUILayoutUtility.GetRect(0f, 16f, GUILayout.ExpandWidth(true));
                GUI.Label(new Rect(rect.x + offset, rect.y, rect.width - offset, 16f),
                          L.F("+{0} service properties", e.HiddenProps), _dimStyle);
            }
        }

        /// <summary>
        /// Строка в стиле иерархии: треугольник раскрытия, иконка, имя, суть
        /// правки и знак состояния у правого края.
        ///
        /// Рисуется по прямоугольникам, а не цепочкой GUILayout: только так
        /// треугольник, иконка и имя встают ровно так же, как в иерархии, и
        /// вся строка реагирует на наведение и щелчок целиком.
        /// </summary>
        /// <param name="clickable">Щелчок по строке открывает историю. Иначе он сворачивает строку.</param>
        private RowAction DrawTreeRow(int depth, bool expandable, ref bool expanded, Texture icon,
                                      string title, string detail, SceneEvent e, bool clickable,
                                      string tooltip)
        {
            var rect = GUILayoutUtility.GetRect(0f, RowHeight, GUILayout.ExpandWidth(true));
            var ev = Event.current;
            bool hover = rect.Contains(ev.mousePosition);

            if (hover && ev.type == EventType.Repaint)
                EditorGUI.DrawRect(rect, HoverTint);

            float x = rect.x + 4f + depth * IndentWidth;

            var arrow = new Rect(x, rect.y, 14f, RowHeight);
            if (expandable) expanded = EditorGUI.Foldout(arrow, expanded, GUIContent.none, false);
            x += 14f;

            if (icon != null)
                GUI.DrawTexture(new Rect(x, rect.y + 1f, 16f, 16f), icon, ScaleMode.ScaleToFit);
            x += 19f;

            const float SignWidth = 24f;
            float right = rect.xMax - SignWidth - 6f;

            bool gone = e != null && e.Kind == SceneEventKind.Removed;
            var style = gone ? _removedStyle : EditorStyles.label;

            var name = new GUIContent(title ?? string.Empty, tooltip);
            float nameWidth = Mathf.Min(style.CalcSize(name).x, Mathf.Max(0f, right - x));
            GUI.Label(new Rect(x, rect.y, nameWidth, RowHeight), name, style);
            x += nameWidth + 10f;

            if (!string.IsNullOrEmpty(detail) && right - x > 24f)
                GUI.Label(new Rect(x, rect.y, right - x, RowHeight), detail, _dimStyle);

            // Знак — у правого края, как отметки изменений в самой иерархии:
            // слева он спорил бы с треугольником и сдвигал имена.
            if (e != null)
            {
                var old = GUI.color;
                GUI.color = GitPalette.Text(StatusOf(e.Kind));
                GUI.Label(new Rect(rect.xMax - SignWidth - 4f, rect.y, SignWidth, RowHeight),
                          Sign(e.Kind), _badgeStyle);
                GUI.color = old;
            }

            var body = new Rect(arrow.xMax, rect.y, rect.xMax - arrow.xMax, rect.height);
            if (clickable) EditorGUIUtility.AddCursorRect(body, MouseCursor.Link);

            // Треугольник Foldout обработал сам — щелчок по нему здесь уже «использован».
            if (ev.type != EventType.MouseDown || !hover) return RowAction.None;

            if (ev.button == 1)
            {
                ev.Use();
                return RowAction.Menu;
            }

            if (ev.button != 0) return RowAction.None;

            if (clickable)
            {
                ev.Use();
                return RowAction.Open;
            }

            if (expandable)
            {
                expanded = !expanded;
                ev.Use();
            }

            return RowAction.None;
        }

        private void SetFolded(string key, bool expanded)
        {
            if (expanded) _folded.Remove(key);
            else _folded.Add(key);
        }

        private void Defer(Action action)
        {
            _pending = action;
            Repaint();
        }

        private static Color HoverTint
        {
            get
            {
                return EditorGUIUtility.isProSkin
                    ? new Color(1f, 1f, 1f, 0.06f)
                    : new Color(0f, 0f, 0f, 0.06f);
            }
        }

        // Значок, подсказка и окно настроек сцены — общие с деревом объектов в diff.
        private static Texture SettingsIcon => SceneHistoryIcons.SettingsIcon;
        private static string SettingsTooltip(string type) => SceneHistoryIcons.SettingsTooltip(type);
        private static string SettingsWindow(string type) => SceneHistoryIcons.SettingsWindow(type);

        /// <summary>Курсор-«рука» над последним нарисованным элементом.</summary>
        private static void Link()
        {
            EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
        }

        // ---------------------------- компонент: инспектор на каждый коммит ---

        private void DrawInspector(SceneRevision rev, List<SceneEvent> events)
        {
            foreach (var e in events)
            {
                if (e.IsPrefabInstance && e.Node != null)
                {
                    DrawInstanceInspector(rev, e);
                    continue;
                }

                // Сам объект — не компонент: у него шапка GameObject, а не инспектор типа.
                if (e.IsObjectLevel)
                {
                    DrawObjectInspector(e);
                    continue;
                }

                var type = SceneHistoryIcons.ResolveComponent(e.ComponentType, DocOf(e));

                // Ключ — коммит и компонент: в инспекторе объекта у одного коммита их несколько.
                var diffKey = rev.Sha + "|" + (e.IsObjectLevel ? "o" + e.ObjectId : "c" + e.ComponentId);
                ComponentDiffView view;
                if (!_diffs.TryGetValue(diffKey, out view))
                {
                    view = new ComponentDiffView(type, e.Node != null ? e.Node.OldDoc : null,
                                                        e.Node != null ? e.Node.NewDoc : null);
                    _diffs[diffKey] = view;
                }

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(new GUIContent(TypeIcon(e)),
                                        GUILayout.Width(16f), GUILayout.Height(16f));
                        if (e.Kind != SceneEventKind.Removed)
                        {
                            if (GUILayout.Button(new GUIContent(TypeTitle(e),
                                        L.T("Select the object and expand this component")),
                                    _subjectStyle, GUILayout.ExpandWidth(false)))
                                Defer(() => GoTo(e, false));
                            Link();
                        }
                        else
                        {
                            GUILayout.Label(TypeTitle(e), _subjectStyle, GUILayout.ExpandWidth(false));
                        }

                        GUILayout.Label(Sign(e.Kind) + " " + Summary(e), _dimStyle);
                        GUILayout.FlexibleSpace();

                        // Возврат — выпадающим списком: вернуть можно и к состоянию
                        // после коммита, и к состоянию до него, и это разные решения.
                        var restoreContent = L.C("Restore", "Restore the live component's values");
                        var restoreRect = GUILayoutUtility.GetRect(restoreContent, EditorStyles.miniPullDown,
                                                                   GUILayout.Width(80f));
                        if (EditorGUI.DropdownButton(restoreRect, restoreContent, FocusType.Passive,
                                                     EditorStyles.miniPullDown))
                            ShowRestoreMenu(e, restoreRect);

                        if (GUILayout.Button(L.T("Before and After…"), EditorStyles.miniButton, GUILayout.Width(110f)))
                            OpenBeforeAfter(e);
                    }

                    if (_compareLive && view.Ready)
                    {
                        var live = e.Kind == SceneEventKind.Removed ? null : LiveComponent(e);
                        if (live == null)
                        {
                            DrawNoLive(e);
                            continue;
                        }
                        view.SetLive(live, LiveReadOnlyReason());
                    }

                    if (view.Ready) view.Draw(position.width - 40f, _onlyChanged, _compareLive);
                    else DrawPropertyTable(e, view.Problem);
                }
            }
        }

        // ------------------------------------------ объект: шапка GameObject ---

        /// <summary>Поля шапки GameObject в инспекторе — то, что хранит сам документ объекта.</summary>
        private static readonly string[] ObjectFields = { "m_IsActive", "m_Name", "m_TagString", "m_Layer", "m_StaticEditorFlags" };

        /// <summary>
        /// Объект целиком — как шапка GameObject в инспекторе: активность, имя, тег,
        /// слой, Static. Слева до коммита, справа после — или сейчас, и тогда
        /// правая колонка правится. Настройки сцены — таблицей значений.
        /// </summary>
        private void DrawObjectInspector(SceneEvent e)
        {
            var oldDoc = e.Node != null ? e.Node.OldDoc : null;
            var newDoc = e.Node != null ? e.Node.NewDoc : null;
            var doc = newDoc ?? oldDoc;
            bool gameObject = !e.IsSceneSettings && doc != null && doc.TypeName == "GameObject";

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var icon = gameObject ? SceneHistoryIcons.GameObjectIcon : EditorGUIUtility.IconContent("SceneAsset Icon").image;
                    GUILayout.Label(new GUIContent(icon), GUILayout.Width(16f), GUILayout.Height(16f));

                    var title = gameObject ? ObjectTitle(e, doc) : (doc != null && doc.TypeName != null ? doc.TypeName : L.T("Scene Settings"));
                    if (gameObject && e.Kind != SceneEventKind.Removed)
                    {
                        if (GUILayout.Button(new GUIContent(title, L.T("Select the object in the scene")), _subjectStyle, GUILayout.ExpandWidth(false)))
                            Defer(() => GoTo(e, true));
                        Link();
                    }
                    else
                    {
                        GUILayout.Label(title, _subjectStyle, GUILayout.ExpandWidth(false));
                    }

                    GUILayout.Label(Sign(e.Kind) + " " + Summary(e), _dimStyle);
                    GUILayout.FlexibleSpace();
                }

                if (!gameObject)
                {
                    DrawSceneSettings(e, doc);
                    return;
                }

                GameObject live = null;
                string readOnly = null;

                if (_compareLive)
                {
                    if (e.Kind == SceneEventKind.Removed)
                    {
                        EditorGUILayout.HelpBox(L.T("The object was removed in this commit — there's nothing to compare with the current state."), MessageType.None);
                        return;
                    }

                    live = SceneObjectRef.Find(new SceneObjectAddress { ScenePath = _scenePath, FileId = e.ObjectId }) as GameObject;
                    if (live == null)
                    {
                        DrawNoLiveObject();
                        return;
                    }

                    readOnly = LiveReadOnlyReason();
                }

                var left = _compareLive ? newDoc : oldDoc;
                float column = Mathf.Max(110f, (position.width - 40f - 16f - 30f) * 0.5f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(_compareLive ? L.T("After Commit") : oldDoc == null ? L.T("Object Didn't Exist") : L.T("Before Commit"),
                                               EditorStyles.miniBoldLabel, GUILayout.Width(column));
                    GUILayout.Space(16f);
                    EditorGUILayout.LabelField(_compareLive ? (readOnly != null ? L.T("Now") : L.T("Now — Editable"))
                                                            : newDoc == null ? L.T("Object Removed") : L.T("After Commit"),
                                               EditorStyles.miniBoldLabel, GUILayout.Width(column));
                }

                if (readOnly != null) EditorGUILayout.LabelField(readOnly, EditorStyles.miniLabel);

                var tint = GitPalette.Fill(GitFileStatus.Modified);
                var dot = tint;
                tint.a = 0.12f;

                float labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = Mathf.Max(60f, column * 0.35f);

                int shown = 0;
                try
                {
                    foreach (var field in ObjectFields)
                    {
                        var leftValue = left != null ? left.Get(field) : null;
                        var rightValue = _compareLive ? LiveObjectValue(live, field) : newDoc != null ? newDoc.Get(field) : null;

                        bool same = Unquote(leftValue) == Unquote(rightValue);
                        if (_onlyChanged && same) continue;
                        shown++;

                        var row = EditorGUILayout.BeginHorizontal();
                        if (!same && Event.current.type == EventType.Repaint) EditorGUI.DrawRect(row, tint);

                        using (new EditorGUI.DisabledScope(true))
                        {
                            if (left != null) DrawObjectField(field, leftValue, column);
                            else GUILayout.Space(column);
                        }

                        var marker = GUILayoutUtility.GetRect(16f, EditorGUIUtility.singleLineHeight, GUILayout.Width(16f));
                        if (!same && Event.current.type == EventType.Repaint)
                            EditorGUI.DrawRect(new Rect(marker.x + 4f, marker.y + 5f, 7f, 7f), dot);

                        if (_compareLive && readOnly == null)
                        {
                            DrawLiveObjectField(live, field, column);
                        }
                        else
                        {
                            using (new EditorGUI.DisabledScope(true))
                            {
                                if (_compareLive || newDoc != null) DrawObjectField(field, rightValue, column);
                                else GUILayout.Space(column);
                            }
                        }

                        EditorGUILayout.EndHorizontal();
                    }
                }
                finally
                {
                    EditorGUIUtility.labelWidth = labelWidth;
                }

                if (shown == 0)
                    EditorGUILayout.LabelField(_compareLive ? L.T("The object is now the same as after this commit.")
                                                            : L.T("The object's fields didn't change — its components did."),
                                               EditorStyles.miniLabel);
            }
        }

        /// <summary>Виды настроек сцены по строкам ленты. Держим немного: у каждого свои временные объекты.</summary>
        private readonly Dictionary<string, SceneSettingsView> _settingsViews = new Dictionary<string, SceneSettingsView>(StringComparer.Ordinal);
        private readonly Dictionary<string, SceneRootsView> _rootsViews = new Dictionary<string, SceneRootsView>(StringComparer.Ordinal);
        private readonly Queue<string> _settingsOrder = new Queue<string>();

        /// <summary>Настройки сцены — инспектором; порядок корней — списком имён; не собралось — таблицей значений.</summary>
        private void DrawSceneSettings(SceneEvent e, UnityDocument doc)
        {
            var key = e.Revision.Sha + "|s" + e.ObjectId;
            float width = position.width - 40f;

            if (doc != null && doc.TypeName == "SceneRoots")
            {
                SceneRootsView roots;
                if (!_rootsViews.TryGetValue(key, out roots))
                {
                    if (_rootsViews.Count > 32) _rootsViews.Clear();
                    roots = new SceneRootsView(e, Repaint);
                    _rootsViews[key] = roots;
                }

                roots.Draw(width, _compareLive, _scenePath);
                return;
            }

            SceneSettingsView view;
            if (!_settingsViews.TryGetValue(key, out view))
            {
                while (_settingsOrder.Count >= 16)
                {
                    var old = _settingsOrder.Dequeue();
                    SceneSettingsView evicted;
                    if (_settingsViews.TryGetValue(old, out evicted)) evicted.Dispose();
                    _settingsViews.Remove(old);
                }

                view = new SceneSettingsView(e, Repaint);
                _settingsViews[key] = view;
                _settingsOrder.Enqueue(key);
            }

            if (!view.Draw(width, _onlyChanged, _compareLive, _scenePath))
                DrawPropertyTable(e, view.Problem);
        }

        private static string ObjectTitle(SceneEvent e, UnityDocument doc)
        {
            var name = Unquote(doc.Get("m_Name"));
            return string.IsNullOrEmpty(name) ? (string.IsNullOrEmpty(e.ObjectPath) ? "GameObject" : e.ObjectPath) : name;
        }

        private static string Unquote(string raw)
        {
            if (raw == null) return null;
            raw = raw.Trim();
            if (raw.Length >= 2 && (raw[0] == '\'' || raw[0] == '"') && raw[raw.Length - 1] == raw[0])
                raw = raw.Substring(1, raw.Length - 2);
            return raw;
        }

        private static void DrawObjectField(string field, string raw, float width)
        {
            var value = Unquote(raw);
            int number;
            bool isNumber = int.TryParse(value, out number);

            switch (field)
            {
                case "m_IsActive":
                    if (value == null) EditorGUILayout.LabelField("Active", "—", GUILayout.Width(width));
                    else EditorGUILayout.Toggle("Active", value == "1", GUILayout.Width(width));
                    break;
                case "m_Name":
                    EditorGUILayout.TextField("Name", value ?? "—", GUILayout.Width(width));
                    break;
                case "m_TagString":
                    EditorGUILayout.TextField("Tag", value ?? "—", GUILayout.Width(width));
                    break;
                case "m_Layer":
                    EditorGUILayout.TextField("Layer", !isNumber ? value ?? "—" : LayerTitle(number), GUILayout.Width(width));
                    break;
                default:
                    EditorGUILayout.TextField("Static", !isNumber ? value ?? "—" : ((StaticEditorFlags)number).ToString(), GUILayout.Width(width));
                    break;
            }
        }

        private static string LayerTitle(int layer)
        {
            var name = LayerMask.LayerToName(layer);
            return string.IsNullOrEmpty(name) ? "Layer " + layer : layer + ": " + name;
        }

        private static string LiveObjectValue(GameObject live, string field)
        {
            switch (field)
            {
                case "m_IsActive": return live.activeSelf ? "1" : "0";
                case "m_Name": return live.name;
                case "m_TagString": return live.tag;
                case "m_Layer": return live.layer.ToString();
                default: return ((int)GameObjectUtility.GetStaticEditorFlags(live)).ToString();
            }
        }

        /// <summary>Поле живого объекта — обычная правка сцены, с записью в Undo.</summary>
        private static void DrawLiveObjectField(GameObject live, string field, float width)
        {
            EditorGUI.BeginChangeCheck();

            switch (field)
            {
                case "m_IsActive":
                {
                    bool active = EditorGUILayout.Toggle("Active", live.activeSelf, GUILayout.Width(width));
                    if (!EditorGUI.EndChangeCheck()) return;
                    Undo.RecordObject(live, L.T("Object Active State"));
                    live.SetActive(active);
                    break;
                }
                case "m_Name":
                {
                    var name = EditorGUILayout.DelayedTextField("Name", live.name, GUILayout.Width(width));
                    if (!EditorGUI.EndChangeCheck()) return;
                    Undo.RecordObject(live, L.T("Object Name"));
                    live.name = name;
                    break;
                }
                case "m_TagString":
                {
                    var tag = EditorGUILayout.TagField("Tag", live.tag, GUILayout.Width(width));
                    if (!EditorGUI.EndChangeCheck()) return;
                    Undo.RecordObject(live, L.T("Object Tag"));
                    live.tag = tag;
                    break;
                }
                case "m_Layer":
                {
                    int layer = EditorGUILayout.LayerField("Layer", live.layer, GUILayout.Width(width));
                    if (!EditorGUI.EndChangeCheck()) return;
                    Undo.RecordObject(live, L.T("Object Layer"));
                    live.layer = layer;
                    break;
                }
                default:
                {
                    var flags = (StaticEditorFlags)EditorGUILayout.EnumFlagsField("Static", GameObjectUtility.GetStaticEditorFlags(live), GUILayout.Width(width));
                    if (!EditorGUI.EndChangeCheck()) return;
                    Undo.RecordObject(live, L.T("Object Static Flags"));
                    GameObjectUtility.SetStaticEditorFlags(live, flags);
                    break;
                }
            }

            EditorUtility.SetDirty(live);
            if (!EditorUtility.IsPersistent(live) && live.scene.IsValid())
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(live.scene);
            SceneChangeIndex.MarkDirty();
        }

        /// <summary>Текущего объекта нет: сцена закрыта или объект удалили позже.</summary>
        private void DrawNoLiveObject()
        {
            if (IsPrefab)
            {
                EditorGUILayout.HelpBox(L.T("This object is no longer in the prefab — it was removed later."), MessageType.None);
                return;
            }

            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(_scenePath);
            if (scene.isLoaded)
            {
                EditorGUILayout.HelpBox(L.T("This object is no longer in the scene — it was removed later."), MessageType.None);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox(L.F("Scene {0} isn't open — there's nowhere to get the current state from.", Ui.NameOf(_scenePath)), MessageType.None);
                if (GUILayout.Button(L.T("Open Scene"), GUILayout.Width(110f), GUILayout.Height(38f)))
                    Defer(OpenScene);
            }
        }

        /// <summary>Текущего состояния нет: сцена закрыта, компонент удалён в коммите или позже.</summary>
        private void DrawNoLive(SceneEvent e)
        {
            if (e.Kind == SceneEventKind.Removed)
            {
                EditorGUILayout.HelpBox(L.T("The component was removed in this commit — there's nothing to compare with the current state."), MessageType.None);
                return;
            }

            // Префаб открывать не нужно: его текущее состояние — сам файл, и компонент
            // находится в нём всегда, пока его не удалили.
            if (IsPrefab)
            {
                EditorGUILayout.HelpBox(L.T("This component is no longer in the prefab — it was removed later."), MessageType.None);
                return;
            }

            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(_scenePath);
            if (scene.isLoaded)
            {
                EditorGUILayout.HelpBox(L.T("This component is no longer in the scene — it was removed later."), MessageType.None);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox(L.F("Scene {0} isn't open — there's nowhere to get the current state from.", Ui.NameOf(_scenePath)), MessageType.None);
                if (GUILayout.Button(L.T("Open Scene"), GUILayout.Width(110f), GUILayout.Height(38f)))
                    Defer(OpenScene);
            }
        }

        private void OpenScene()
        {
            // OpenScene принимает только .unity; префаб сюда не попадает, но проверка —
            // на случай нового места вызова.
            if (IsPrefab || string.IsNullOrEmpty(_scenePath)) return;
            if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(_scenePath);
            Repaint();
        }

        /// <summary>
        /// Префаб открыт на редактирование — его объекты в режиме префаба и в файле
        /// разные: правка файла здесь потерялась бы при сохранении префаба там.
        /// </summary>
        private string LiveReadOnlyReason()
        {
            if (!IsPrefab) return null;
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null && string.Equals(stage.assetPath, _scenePath, StringComparison.OrdinalIgnoreCase)
                ? L.T("The prefab is open for editing — edit it there; this is view only.")
                : null;
        }

        /// <summary>
        /// Изменение объекта или компонента в виде инспектора — на том же коммите
        /// ленты, с прошлой версией или с текущим состоянием.
        /// </summary>
        private void DrillInspector(SceneEvent e, bool objectRow, bool compareLive)
        {
            if (objectRow) Go(SceneHistoryScope.Object, e.ObjectId, 0, e.ObjectPath, null);
            else Go(SceneHistoryScope.Component, e.ObjectId, e.ComponentId, e.ObjectPath, TypeTitle(e));

            _inspector = objectRow || _inspector;
            _compareLive = compareLive;
            _shown = PageSize;
            _focusSha = e.Revision.Sha;
            _focusOnly = true;
            ApplyFocus();
        }

        /// <summary>
        /// Запасной вид, когда инспектор не собрать: у MonoBehaviour настоящий
        /// класс прячется за GUID скрипта, и восстановить его из YAML нечем.
        /// </summary>
        private static void DrawPropertyTable(SceneEvent e, string problem)
        {
            if (!string.IsNullOrEmpty(problem))
                EditorGUILayout.LabelField(problem, EditorStyles.miniLabel);

            foreach (var p in e.Props)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    // Гибкие ширины, а не фиксированные: у LabelField своя
                    // минимальная ширина поля, и строка вылезала за окно,
                    // добавляя горизонтальную прокрутку.
                    GUILayout.Label(new GUIContent(p.Path, p.Path), EditorStyles.label,
                                    GUILayout.MinWidth(80f), GUILayout.MaxWidth(280f));
                    GUILayout.Label(new GUIContent(p.Old ?? "—", p.Old), EditorStyles.miniLabel,
                                    GUILayout.MinWidth(30f));
                    GUILayout.Label("→", EditorStyles.miniLabel, GUILayout.Width(16f));
                    GUILayout.Label(new GUIContent(p.New ?? "—", p.New), EditorStyles.miniLabel,
                                    GUILayout.MinWidth(30f));
                    GUILayout.FlexibleSpace();
                }
            }

            if (e.Props.Count == 0)
                EditorGUILayout.LabelField(
                    e.IsObjectLevel
                        ? (e.Kind == SceneEventKind.Added ? L.T("Object added.") : e.Kind == SceneEventKind.Removed ? L.T("Object removed.") : L.T("Values didn't change."))
                        : (e.Kind == SceneEventKind.Added ? L.T("Component added.") : L.T("Component removed.")),
                    EditorStyles.miniLabel);
        }

        // --------------------------------------------------------- действия ---

        private void Drill(SceneEvent e, bool objectRow)
        {
            if (objectRow)
            {
                Go(SceneHistoryScope.Object, e.ObjectId, 0, e.ObjectPath, null);
                return;
            }

            Go(SceneHistoryScope.Component, e.ObjectId, e.ComponentId, e.ObjectPath, TypeTitle(e));
        }

        private void OpenCommit(SceneRevision rev)
        {
            GitWindow.ShowCommit(rev.Sha, _scenePath);
        }

        /// <summary>
        /// Переход к живому объекту. Сцена может быть не открыта — тогда
        /// предлагаем открыть её, а не молча сообщаем «не найдено».
        /// </summary>
        private void GoTo(SceneEvent e, bool objectRow)
        {
            var address = new SceneObjectAddress
            {
                ScenePath = _scenePath,
                FileId = objectRow ? e.ObjectId : e.ComponentId
            };

            // Экземпляр префаба ищется по-своему: по его fileID находится служебная
            // запись экземпляра, а не корневой объект.
            if (objectRow && e.IsPrefabInstance)
            {
                var instanceRoot = FindInstanceFor(e);
                if (instanceRoot != null && SceneObjectRef.FocusObject(instanceRoot)) return;
            }
            else if (SceneObjectRef.Focus(address)) return;

            // У префаба объекты ищутся в самом файле — раз не нашлось, их там уже нет.
            if (IsPrefab)
            {
                ShowNotification(new GUIContent(objectRow ? L.T("This object is no longer in the prefab") : L.T("This component is no longer in the prefab")));
                return;
            }

            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(_scenePath);

            if (!scene.isLoaded)
            {
                if (!EditorUtility.DisplayDialog(L.T("Scene Not Open"),
                        L.F("To go to the object, open {0}.", Ui.NameOf(_scenePath)),
                        L.T("Open Scene"), L.T("Cancel")))
                    return;

                if (!UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return;

                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(_scenePath);
                if (SceneObjectRef.Focus(address)) return;
            }

            // Сцена открыта, а объекта нет: его удалили позже этого коммита.
            ShowNotification(new GUIContent(objectRow
                ? L.T("This object is no longer in the scene")
                : L.T("This component is no longer on the object")));
        }

        private void ShowEventMenu(SceneEvent e, bool objectRow)
        {
            var menu = new GenericMenu();

            // Переход на сцену — первым: левая кнопка ведёт в историю, а правую
            // нажимают как раз затем, чтобы увидеть объект вживую.
            bool gone = objectRow
                ? e.IsObjectLevel && e.Kind == SceneEventKind.Removed
                : e.Kind == SceneEventKind.Removed;

            var goTo = objectRow ? L.T("Select in Scene") : L.T("Go to Component");
            if (gone) menu.AddDisabledItem(new GUIContent(goTo + " — " + L.T("removed in this commit")));
            else menu.AddItem(new GUIContent(goTo), false, () => GoTo(e, objectRow));

            if (_scope == SceneHistoryScope.Scene || (_scope == SceneHistoryScope.Object && !objectRow))
                menu.AddItem(new GUIContent(objectRow ? L.Tc("one object", "Object History") : L.T("Component History")),
                             false, () => Drill(e, objectRow));

            // У объекта, собранного из событий его компонентов, собственной
            // разницы нет — показывать «было и стало» было бы нечего.
            // Экземпляр префаба — ссылка на историю самого файла .prefab: объекты
            // внутри экземпляра живут там, а в сцене лежат только переопределения.
            if (objectRow && e.IsPrefabInstance) AddPrefabSourceItems(menu, e, null);

            menu.AddItem(new GUIContent(L.T("Show Change as Inspector")), false, () => Defer(() => DrillInspector(e, objectRow, false)));
            if (gone) menu.AddDisabledItem(new GUIContent(L.T("Compare with Current State") + " — " + L.T("removed in this commit")));
            else menu.AddItem(new GUIContent(L.T("Compare with Current State")), false, () => Defer(() => DrillInspector(e, objectRow, true)));

            if (e.Node != null && (!objectRow || e.IsObjectLevel))
                menu.AddItem(new GUIContent(L.T("Before and After…")), false, () => OpenBeforeAfter(e));
            else
                menu.AddDisabledItem(new GUIContent(L.T("Before and After…")));

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.T("Open Commit in Log")), false, () => OpenCommit(e.Revision));
            menu.AddItem(new GUIContent(L.T("Copy Commit SHA")), false,
                         () => EditorGUIUtility.systemCopyBuffer = e.Revision.Sha);

            menu.ShowAsContext();
        }

        private void ShowSettingsMenu(SceneEvent e)
        {
            var menu = new GenericMenu();

            var window = SettingsWindow(e.ObjectPath);
            if (window != null)
                menu.AddItem(new GUIContent(L.T("Open Settings Window")), false,
                             () => EditorApplication.ExecuteMenuItem(window));
            else
                menu.AddDisabledItem(new GUIContent(L.T("Open Settings Window") + " — " + L.T("this data has none")));

            if (_scope == SceneHistoryScope.Scene)
                menu.AddItem(new GUIContent(L.T("History of These Settings")), false, () => Drill(e, true));

            if (e.Node != null)
                menu.AddItem(new GUIContent(L.T("Before and After…")), false, () => OpenBeforeAfter(e));

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.T("Open Commit in Log")), false, () => OpenCommit(e.Revision));
            menu.AddItem(new GUIContent(L.T("Copy Commit SHA")), false,
                         () => EditorGUIUtility.systemCopyBuffer = e.Revision.Sha);

            menu.ShowAsContext();
        }

        private void OpenBeforeAfter(SceneEvent e)
        {
            if (e.Node == null) return;

            var live = e.IsObjectLevel ? null : LiveComponent(e);

            var subtitle = (e.IsObjectLevel
                ? e.ObjectPath
                : L.F("{0} on “{1}”", TypeTitle(e), e.ObjectPath)) + " · " + e.Revision.ShortSha;

            ComponentHistoryWindow.Show(e.Node, subtitle, live);
        }

        // ------------------------------------------- переходы и возврат ---

        /// <summary>Переключает окно на другой файл: прежняя лента к нему отношения не имеет.</summary>
        private void SetFile(string path)
        {
            if (path == _scenePath) return;

            _scenePath = path;
            _result = null;
            _depth = DepthStep;
            _collapsed.Clear();
            _seen.Clear();
            _folded.Clear();
            _settingsOpen.Clear();
            DropDiffs();
        }

        private void LoadDeeper()
        {
            _depth += DepthStep;
            Reload();
        }

        /// <summary>
        /// Показывает коммит, с которым окно открыли из журнала: догружает
        /// порции ленты до него и прокручивает к нему на ближайшей перерисовке.
        /// Если коммита в ленте нет — говорит почему, а не молчит.
        /// </summary>
        private void ApplyFocus()
        {
            if (string.IsNullOrEmpty(_focusSha) || _result == null) return;

            var commits = Selected();
            for (int i = 0; i < commits.Count; i++)
            {
                if (commits[i].Key.Sha != _focusSha) continue;

                _shown = Mathf.Max(_shown, (i / PageSize + 1) * PageSize);

                // Пришли ради одного коммита — остальные сворачиваем, чтобы он не
                // терялся среди соседних. Развернуть их можно треугольником.
                if (_focusOnly)
                {
                    _focusOnly = false;
                    _collapsed.Clear();
                    foreach (var rev in _result.Revisions)
                        if (rev.Sha != _focusSha) _collapsed.Add(rev.Sha);
                }

                _collapsed.Remove(_focusSha);
                _scrollToFocus = true;
                return;
            }

            var sha = _focusSha.Length > 7 ? _focusSha.Substring(0, 7) : _focusSha;

            if (_result.Revisions.Exists(r => r.Sha == _focusSha))
            {
                SetFocusNote(L.F("Objects didn't change in commit {0} — only the file text. See the “Text” tab in the log.", sha), null, null);
                return;
            }

            ResolveMissingFocusAsync(_focusSha);
        }

        private Component LiveComponent(SceneEvent e)
        {
            if (e == null || e.IsObjectLevel) return null;

            return SceneObjectRef.Find(new SceneObjectAddress
            {
                ScenePath = _scenePath,
                FileId = e.ComponentId
            }) as Component;
        }

        private void ShowRestoreMenu(SceneEvent e, Rect anchor)
        {
            var menu = new GenericMenu();
            var live = LiveComponent(e);
            var at = e.Revision.ShortSha;

            if (live == null)
            {
                menu.AddDisabledItem(new GUIContent(L.T("The component isn't in the open scene — nowhere to restore to")));
            }
            else
            {
                var after = e.Node != null ? e.Node.NewDoc : null;
                var before = e.Node != null ? e.Node.OldDoc : null;

                if (after != null)
                    menu.AddItem(new GUIContent(L.F("As After Commit {0}", at)), false,
                                 () => RestoreTo(e, live, after, L.F("after commit {0}", at)));
                else
                    menu.AddDisabledItem(new GUIContent(L.F("As After Commit {0}", at) + " — " + L.T("the component was removed in it")));

                if (before != null)
                    menu.AddItem(new GUIContent(L.F("As Before Commit {0}", at)), false,
                                 () => RestoreTo(e, live, before, L.F("before commit {0}", at)));
                else
                    menu.AddDisabledItem(new GUIContent(L.F("As Before Commit {0}", at) + " — " + L.T("the component didn't exist yet")));
            }

            menu.DropDown(anchor);
        }

        private void RestoreTo(SceneEvent e, Component live, UnityDocument doc, string when)
        {
            if (live == null || doc == null) return;

            if (!EditorUtility.DisplayDialog(L.T("Restore Values"),
                    L.F("{0} on “{1}”", TypeTitle(e), e.ObjectPath) + "\n\n" +
                    L.F("Values will be replaced with the state {0} (“{1}”).", when, e.Revision.Subject) + "\n\n" +
                    L.T("Unchanged: the script reference, the parent in the hierarchy and the component list.") + "\n" +
                    L.T("You can undo with Ctrl+Z."),
                    L.T("Restore"), L.T("Cancel")))
                return;

            var result = UnityPropertyApplier.RestoreLive(live, doc, L.T("Restore Values from History"));
            SceneChangeIndex.MarkDirty();
            ShowNotification(new GUIContent(L.F("Done: {0}", result)));
        }

        private static string PrefabPathOf(SceneEvent e)
        {
            if (e == null || string.IsNullOrEmpty(e.SourcePrefabGuid)) return null;

            var path = AssetDatabase.GUIDToAssetPath(e.SourcePrefabGuid);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        // ---------------------------------------------------------- мелочи ---

        private static UnityDocument DocOf(SceneEvent e)
        {
            if (e == null || e.Node == null) return null;
            return e.Node.NewDoc ?? e.Node.OldDoc;
        }

        /// <summary>Имя компонента для показа: у скрипта — его класс, а не «MonoBehaviour».</summary>
        private static string TypeTitle(SceneEvent e)
        {
            // У события самого объекта типа компонента нет — это GameObject или настройки сцены.
            if (e.IsObjectLevel)
            {
                var doc = DocOf(e);
                return e.IsSceneSettings && doc != null && doc.TypeName != null ? doc.TypeName : "GameObject";
            }

            return SceneHistoryIcons.DisplayName(e.ComponentType, DocOf(e));
        }

        private static Texture TypeIcon(SceneEvent e)
        {
            if (e.IsObjectLevel)
                return e.IsSceneSettings ? EditorGUIUtility.IconContent("SceneAsset Icon").image : SceneHistoryIcons.GameObjectIcon;

            return SceneHistoryIcons.ComponentIcon(e.ComponentType, DocOf(e));
        }

        private static string Summary(SceneEvent e)
        {
            if (e == null) return string.Empty;

            if (e.Kind == SceneEventKind.Renamed) return e.OldName + " → " + e.NewName;
            if (e.Kind == SceneEventKind.Reparented) return L.T("moved in the hierarchy");
            if (e.Kind == SceneEventKind.Added) return string.Empty;
            if (e.Kind == SceneEventKind.Removed) return string.Empty;

            if (e.IsPrefabInstance && e.Node != null)
            {
                var changes = PrefabOverrides.Changes(e.Node.OldDoc, e.Node.NewDoc);
                return PrefabInstanceParts.Summary(changes);
            }

            if (e.Props.Count == 0) return string.Empty;

            var first = e.Props[0];
            var text = first.Path + ": " + Short(first.Old) + " → " + Short(first.New);
            return e.Props.Count > 1 ? text + "  +" + (e.Props.Count - 1) : text;
        }

        private static string Sign(SceneEventKind kind)
        {
            switch (kind)
            {
                case SceneEventKind.Added: return "+";
                case SceneEventKind.Removed: return "−";
                case SceneEventKind.Renamed: return L.Tc("rename sign", "ab");
                case SceneEventKind.Reparented: return "⇱";
                default: return "~";
            }
        }

        private static GitFileStatus StatusOf(SceneEventKind kind)
        {
            switch (kind)
            {
                case SceneEventKind.Added: return GitFileStatus.Added;
                case SceneEventKind.Removed: return GitFileStatus.Deleted;
                case SceneEventKind.Renamed:
                case SceneEventKind.Reparented: return GitFileStatus.Renamed;
                default: return GitFileStatus.Modified;
            }
        }

        private static string Short(string value)
        {
            if (string.IsNullOrEmpty(value)) return "—";
            return value.Length <= 24 ? value : value.Substring(0, 24) + "…";
        }

        private static string Ago(DateTime when)
        {
            if (when == DateTime.MinValue) return string.Empty;

            var span = DateTime.Now - when;
            if (span.TotalHours < 1) return L.F("{0} min ago", (int)span.TotalMinutes);
            if (span.TotalDays < 1) return L.F("{0} h ago", (int)span.TotalHours);
            if (span.TotalDays < 7) return L.Fc("scene history", "{0} d ago", (int)span.TotalDays);
            if (span.TotalDays < 365) return when.ToString("d MMM");
            return when.ToString("MM.yyyy");
        }
    }
}
