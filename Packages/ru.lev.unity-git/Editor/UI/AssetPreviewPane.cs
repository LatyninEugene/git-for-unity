using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lev.Git.Preview;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git.UI
{
    /// <summary>
    /// Панель изменений ассета — одна на «Изменения», «Историю» и файлы merge
    /// request. Над ней одна строка вкладок: «Вид», «Инспектор», «Объекты»,
    /// «Текст». Первые две рисует сама панель, вторые две — diff файла под ней:
    /// на своих вкладках панель прячет его и занимает всю высоту, на его — сама
    /// сжимается до строки вкладок.
    ///
    /// О типах панель ничего не знает. Стороны сравнения достаёт источник версий,
    /// в объекты Unity их превращает загрузчик, рисует показ, а список изменений
    /// составляет описатель — всё выбирается по типу ассета. Раскладку сторон,
    /// увеличение, каналы и маску разницы панель делает сама для любого показа.
    ///
    /// Сторона рабочей копии — сам ассет проекта: его можно править, откат пишет
    /// прямо в него, а список изменений пересчитывается после правки. Картинки
    /// и звук — исключение: у них обе стороны читаются из файлов, чтобы
    /// сравнивались исходные данные, а не результат импорта.
    /// </summary>
    public sealed class AssetPreviewPane : VisualElement
    {
        private enum Tab { View, Inspector, Changes, Objects, Text }

        // Выбор человека переживает смену файла и живёт до перезагрузки домена.
        private static Tab _tab = Tab.View;
        private static CompareModes _mode = CompareModes.SideBySide;
        private static bool _unityInspector;

        private static readonly string[] ChannelNames = { "RGBA", "R", "G", "B", "A" };

        private readonly Button _tabView, _tabInspector, _tabChanges, _tabObjects, _tabText;
        private readonly Button _modeSide, _modeSwipe, _modeOverlay, _modeDifference, _modeToggle, _pickBefore, _pickAfter;
        private readonly Button _inspectAligned, _inspectUnity, _hiddenToggle, _slicesToggle, _zoomReset;
        private readonly Button _playBefore, _playAfter, _stop, _importButton;
        private readonly DropdownField _choice;
        private readonly Button[] _channelButtons = new Button[ChannelNames.Length];
        private readonly VisualElement _tools, _modes, _pick, _shapes, _channels, _playback, _inspectModes, _body, _changesBox;
        private readonly Slider _opacity, _threshold;
        private readonly ScrollView _changeRows;
        private readonly Label _note, _changesTitle, _mainNote, _playTime;

        private readonly IMGUIContainer _view, _aligned;
        private readonly VisualElement _inspector;
        private readonly IMGUIContainer[] _columns = new IMGUIContainer[2];

        private readonly PreviewSync _sync = new PreviewSync();
        private readonly Editor[] _editors = new Editor[2];

        /// <summary>Состояние версии из git до отрисовки: случайная правка в её инспекторе сразу откатывается.</summary>
        private readonly string[] _snapshots = new string[2];

        private readonly AlignedInspector _alignedInspector = new AlignedInspector();

        /// <summary>Где на экране нарисована картинка стороны и какая её часть видна — для лупы и перемотки.</summary>
        private readonly Rect[] _fits = new Rect[2];
        private readonly Rect[] _sources = new Rect[2];

        /// <summary>Diff файла под панелью: его вкладки «Объекты» и «Текст» живут в нашей строке.</summary>
        private DiffView _diff;

        /// <summary>Diff спрятан нами — и показать его обратно должны мы.</summary>
        private bool _hidDiff;

        private string _key, _path, _stamp, _message;
        private int _gen;
        private bool _loading, _showHidden, _describeScheduled, _dragSwipe, _dragPan;
        private int _dragSlot;

        /// <summary>Человек разрешил временный импорт для этого файла.</summary>
        private bool _importConsent;

        /// <summary>Тянут линию воспроизведения или полосу времени; сдвигают камеру 3D-превью.</summary>
        private bool _dragScrub, _dragShift;

        /// <summary>Полоса времени под превью и область, по ширине которой сейчас идёт перемотка.</summary>
        private Rect _stripRect, _scrubArea;

        private AssetLoader _loader;
        private RevisionSide _beforeSide, _afterSide;
        private LoadedAsset _before, _after;
        private AssetPresenter _presenter;
        private List<ChangeItem> _items;
        private Vector2 _inspectorScroll;

        // Живые панели. Показы 3D держат PreviewRenderUtility со своей сценой:
        // если панель не освободить до перезагрузки домена, Unity пишет об утечке
        // сцены превью. Панели во вкладках окна Git не получают OnDisable окна,
        // поэтому их освобождаем отсюда — перед перезагрузкой и по просьбе окна.
        private static readonly List<WeakReference<AssetPreviewPane>> Live = new List<WeakReference<AssetPreviewPane>>();

        static AssetPreviewPane()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => ReleaseAll(null);
            EditorApplication.quitting += () => ReleaseAll(null);
        }

        /// <summary>Освобождает ресурсы панелей, подходящих под условие (null — всех).</summary>
        internal static void ReleaseAll(Func<AssetPreviewPane, bool> which)
        {
            Live.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in Live.ToArray())
            {
                AssetPreviewPane pane;
                if (!weak.TryGetTarget(out pane) || (which != null && !which(pane))) continue;
                try { pane.Release(); }
                catch (Exception e) { Diagnostics.Journal.Exception(e, "Preview pane release"); }
            }
        }

        public AssetPreviewPane()
        {
            Live.RemoveAll(w => !w.TryGetTarget(out _));
            Live.Add(new WeakReference<AssetPreviewPane>(this));

            AddToClassList("preview");

            // ---- вкладки ----
            var bar = Ui.Box("preview__bar");

            var tabs = Ui.ButtonGroup("preview__group");
            _tabView = Ui.Action(L.T("View"), () => SetTab(Tab.View), L.T("How the asset looked and how it looks now"));
            _tabInspector = Ui.Action(L.T("Inspector"), () => SetTab(Tab.Inspector), L.T("Version fields side by side; the current version can be edited"));
            _tabChanges = Ui.Action(L.T("Changes"), () => SetTab(Tab.Changes),
                L.T("What changed in Inspector terms: properties, file info, import settings, sprite slicing"));
            _tabObjects = Ui.Action(L.T("Objects"), () => SetTab(Tab.Objects), L.T("What changed in the file: objects, components, properties"));
            _tabText = Ui.Action(L.T("Text"), () => SetTab(Tab.Text), L.T("Plain line-by-line comparison"));
            tabs.Add(_tabView);
            tabs.Add(_tabInspector);
            tabs.Add(_tabChanges);
            tabs.Add(_tabObjects);
            tabs.Add(_tabText);
            bar.Add(tabs);

            _inspectModes = Ui.ButtonGroup("preview__group");
            _inspectAligned = Ui.Action(L.T("Fields"), () => SetUnityInspector(false), L.T("Version fields line by line, changed ones marked"));
            _inspectUnity = Ui.Action(L.T("As in Unity"), () => SetUnityInspector(true),
                L.T("Real Unity inspectors side by side — with their sections and styling, but without marks"));
            _inspectModes.Add(_inspectAligned);
            _inspectModes.Add(_inspectUnity);
            bar.Add(_inspectModes);

            _importButton = Ui.Action(L.T("Import Previous Version"), ImportForPreview,
                L.T("The previous version will be imported into the temporary folder Assets/GitPreview and deleted when no longer needed. " +
                    "Git does not see this folder."));
            _importButton.style.display = DisplayStyle.None;
            bar.Add(_importButton);

            bar.Add(Ui.Spacer());
            Add(bar);

            // ---- инструменты вкладки «Вид» ----
            _tools = Ui.Box("preview__tools");

            _modes = Ui.ButtonGroup("preview__group");
            _modeSide = Ui.Action(L.T("Side by Side"), () => SetMode(CompareModes.SideBySide), L.T("Versions side by side"));
            _modeSwipe = Ui.Action(L.T("Swipe"), () => SetMode(CompareModes.Swipe), L.T("One image; the mouse moves the border between versions"));
            _modeOverlay = Ui.Action(L.T("Overlay"), () => SetMode(CompareModes.Overlay), L.T("The new version over the previous one with transparency"));
            _modeDifference = Ui.Action(L.T("Difference"), () => SetMode(CompareModes.Difference), L.T("Differing areas highlighted, the rest dimmed"));
            _modeToggle = Ui.Action(L.T("Toggle"), () => SetMode(CompareModes.Toggle), L.T("One version in place of the other — small shifts are easier to notice"));
            _modes.Add(_modeSide);
            _modes.Add(_modeSwipe);
            _modes.Add(_modeOverlay);
            _modes.Add(_modeDifference);
            _modes.Add(_modeToggle);
            _tools.Add(_modes);

            _opacity = new Slider(0f, 1f) { value = _sync.Opacity, tooltip = L.T("Opacity of the new version") };
            _opacity.AddToClassList("preview__slider");
            _opacity.RegisterValueChangedCallback(e => { _sync.Opacity = e.newValue; _view.MarkDirtyRepaint(); });
            _tools.Add(_opacity);

            _threshold = new Slider(0f, 0.5f) { value = _sync.Threshold, tooltip = L.T("Threshold: differences smaller than this are not highlighted") };
            _threshold.AddToClassList("preview__slider");
            _threshold.RegisterValueChangedCallback(e => { _sync.Threshold = e.newValue; _view.MarkDirtyRepaint(); });
            _tools.Add(_threshold);

            _pick = Ui.ButtonGroup("preview__group");
            _pickBefore = Ui.Action(L.T("Before"), () => PickSide(true));
            _pickAfter = Ui.Action(L.T("After"), () => PickSide(false));
            _pick.Add(_pickBefore);
            _pick.Add(_pickAfter);
            _tools.Add(_pick);

            _shapes = Ui.ButtonGroup("preview__group");
            _tools.Add(_shapes);

            _choice = new DropdownField();
            _choice.AddToClassList("preview__choice");
            _choice.RegisterValueChangedCallback(_ =>
            {
                _sync.Choice = Mathf.Max(0, _choice.index);
                _view.MarkDirtyRepaint();
            });
            _tools.Add(_choice);

            _channels = Ui.ButtonGroup("preview__group");
            for (int i = 0; i < ChannelNames.Length; i++)
            {
                int channel = i;
                _channelButtons[i] = Ui.Action(ChannelNames[i], () => SetChannel(channel),
                    i == 0 ? L.T("Color with alpha") : i == 4 ? L.T("Alpha channel only") : L.F("Channel {0} only", ChannelNames[i]));
                _channels.Add(_channelButtons[i]);
            }
            _tools.Add(_channels);

            _slicesToggle = Ui.Action(L.T("Slices"), ToggleSlices, L.T("Sprite rects from .meta: yellow — changed, green — added, red — removed"));
            _tools.Add(_slicesToggle);

            _zoomReset = Ui.Action("1:1", ResetZoom, L.T("Wheel — zoom in under the cursor, drag — pan, double-click — fit"));
            _tools.Add(_zoomReset);

            _playback = Ui.ButtonGroup("preview__group");
            _playBefore = Ui.Action("▶ " + L.T("Before"), () => TogglePlay(0), L.T("Listen to the previous version; while playing — switch to it from the same position"));
            _playAfter = Ui.Action("▶ " + L.T("After"), () => TogglePlay(1), L.T("Listen to the new version; while playing — switch to it from the same position"));
            _stop = Ui.Action("■", StopPlayback, L.T("Stop"));
            _playback.Add(_playBefore);
            _playback.Add(_playAfter);
            _playback.Add(_stop);
            _tools.Add(_playback);

            _playTime = Ui.Text(string.Empty, "preview__time");
            _tools.Add(_playTime);

            _tools.Add(Ui.Spacer());
            Add(_tools);

            _note = Ui.Text(string.Empty, "preview__note");
            Add(_note);

            // ---- содержимое ----
            _body = Ui.Box("preview__body");

            _view = new IMGUIContainer(DrawView);
            _view.AddToClassList("preview__view");
            _body.Add(_view);

            _aligned = new IMGUIContainer(DrawAligned);
            _aligned.AddToClassList("preview__aligned");
            _body.Add(_aligned);

            // Каждая колонка — свой IMGUIContainer, а не половина общего.
            // Инспекторы вроде URP рисуют заголовки разделов от левого края
            // области и тянут поля на ширину окна: в общей области правая
            // колонка наезжала на левую. Своя область — свой левый край.
            _inspector = Ui.Box("preview__inspector");
            for (int i = 0; i < _columns.Length; i++)
            {
                int slot = i;
                _columns[i] = new IMGUIContainer(() => DrawInspectorColumn(slot));
                _columns[i].AddToClassList("preview__column");
                if (i > 0) _columns[i].AddToClassList("preview__column--right");
                _inspector.Add(_columns[i]);
            }
            _body.Add(_inspector);
            Add(_body);

            // ---- список изменений ----
            // Список — своя вкладка: под картинкой он отнимал место у самого
            // превью, а размер файла и так написан строкой над ним.
            _changesBox = Ui.Box("pchanges", "pchanges--fill");
            var head = Ui.Box("pchanges__head");
            _changesTitle = Ui.Text(string.Empty, "pchanges__title");
            head.Add(_changesTitle);
            _mainNote = Ui.Text(string.Empty, "pchanges__comments");
            _mainNote.tooltip = L.T("Threads on this asset in the merge request");
            _mainNote.RegisterCallback<ClickEvent>(_ => OpenNotes(MainRef()));
            head.Add(_mainNote);
            head.Add(Ui.Spacer());
            _hiddenToggle = Ui.Action(L.T("show hidden"), ToggleHidden, L.T("Hidden shader properties, keywords and passes"));
            head.Add(_hiddenToggle);
            _changesBox.Add(head);

            _changeRows = new ScrollView(ScrollViewMode.Vertical);
            _changeRows.AddToClassList("pchanges__scroll");
            _changesBox.Add(_changeRows);
            Add(_changesBox);

            _alignedInspector.LiveEdited += () =>
            {
                ScheduleDescribe();
                _view.MarkDirtyRepaint();
            };

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                Undo.undoRedoPerformed += OnUndo;
                PreviewBackground.Changed += RepaintView;
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                Undo.undoRedoPerformed -= OnUndo;
                PreviewBackground.Changed -= RepaintView;
                if (_presenter != null) _presenter.Stop();
            });

            // Время воспроизведения и кнопки идут вслед за звуком, а не за мышью.
            schedule.Execute(UpdatePlayback).Every(100);

            style.display = DisplayStyle.None;
        }

        /// <summary>
        /// Связывает панель с diff под ней. Пока панель видна, вкладки «Объекты»
        /// и «Текст» показываются в её строке; спряталась — у diff снова свои.
        /// </summary>
        public void AttachDiff(DiffView diff)
        {
            _diff = diff;
            if (_diff == null) return;

            _diff.ModesChanged += () => { if (style.display == DisplayStyle.Flex) UpdateChrome(); };
            _diff.NotesRefreshed += RefreshNotes;
            _diff.ModeRequested += text =>
            {
                // У материала или ScriptableObject переход к объекту понятнее в
                // списке изменений словами инспектора, чем в дереве YAML.
                _tab = text ? Tab.Text : _loader is YamlAssetLoader ? Tab.Changes : Tab.Objects;
                if (style.display == DisplayStyle.Flex) UpdateChrome();
            };
        }

        // ------------------------------------------------------------ показ ---

        /// <summary>Файл из списка изменений: последний коммит против рабочей копии.</summary>
        public bool Show(GitChange change)
        {
            if (change == null) { Hide(); return false; }

            var path = change.ProjectPath;
            bool wantOld = change.Status != GitFileStatus.Added && change.WorkStatus != GitFileStatus.Untracked;
            bool hasNew = change.Status != GitFileStatus.Deleted &&
                          System.IO.File.Exists(System.IO.Path.Combine(GitRepository.ProjectRoot, path));

            var oldGitPath = GitRepository.ToGitPath(change.OriginalPath ?? path);

            return Present(path,
                wantOld ? RevisionSide.Head(oldGitPath, L.T("Before")) : null,
                hasNew ? RevisionSide.Worktree(path, L.T("Now")) : null);
        }

        /// <summary>
        /// Файл внутри коммита: прежняя версия — из первого родителя, новая — из
        /// самого коммита. Рабочая копия здесь не участвует.
        /// </summary>
        public bool ShowRevision(GitCommit commit, GitCommitFile file)
        {
            if (commit == null || file == null) { Hide(); return false; }

            var parent = commit.Parents.Length > 0 ? commit.Parents[0] : null;
            bool wantOld = parent != null && file.Status != GitCommitFileStatus.Added;
            bool wantNew = file.Status != GitCommitFileStatus.Deleted;

            return Present(file.ProjectPath ?? file.GitPath,
                wantOld ? RevisionSide.Commit(parent, file.OriginalGitPath ?? file.GitPath, L.T("Before")) : null,
                wantNew ? RevisionSide.Commit(commit.Sha, file.GitPath, L.T("After")) : null);
        }

        /// <summary>Любые две версии одного файла — для «Истории ассета». null у стороны — её нет.</summary>
        internal bool ShowVersions(string projectPath, RevisionSide before, RevisionSide after)
        {
            return Present(projectPath, before, after);
        }

        /// <summary>Файл между двумя коммитами — например, всё, что изменил merge request.</summary>
        public bool ShowRange(string baseSha, string headSha, GitCommitFile file)
        {
            if (string.IsNullOrEmpty(baseSha) || string.IsNullOrEmpty(headSha) || file == null) { Hide(); return false; }

            bool wantOld = file.Status != GitCommitFileStatus.Added;
            bool wantNew = file.Status != GitCommitFileStatus.Deleted;

            return Present(file.ProjectPath ?? file.GitPath,
                wantOld ? RevisionSide.Commit(baseSha, file.OriginalGitPath ?? file.GitPath, L.T("Before")) : null,
                wantNew ? RevisionSide.Commit(headSha, file.GitPath, L.T("After")) : null);
        }

        public void Release()
        {
            _gen++;
            _key = null;
            _loading = false;
            _message = null;
            _items = null;

            for (int i = 0; i < _editors.Length; i++)
            {
                if (_editors[i] != null) UnityEngine.Object.DestroyImmediate(_editors[i]);
                _editors[i] = null;
                _snapshots[i] = null;
            }

            _alignedInspector.Dispose();

            if (_presenter != null)
            {
                _presenter.Stop();
                _presenter.Dispose();
            }
            _presenter = null;
            _presenterError = null;

            RevisionCache.Release(_before);
            RevisionCache.Release(_after);
            _before = _after = null;

            // Увеличение — свойство картинки, которую смотрели; у следующего файла оно не к месту.
            _sync.Zoom = 1f;
            _sync.Pan = new Vector2(0.5f, 0.5f);
            _sync.Dolly = 1f;
            _sync.Offset = Vector2.zero;

            _changeRows.Clear();
            _shapes.Clear();
            _note.text = string.Empty;
        }

        private void Hide()
        {
            Release();
            style.display = DisplayStyle.None;
            style.flexGrow = 0f;

            if (_diff == null) return;
            _diff.ExternalModeBar = false;
            RestoreDiff();
        }

        private void RestoreDiff()
        {
            if (!_hidDiff || _diff == null) return;
            _diff.style.display = DisplayStyle.Flex;
            _hidDiff = false;
        }

        /// <summary>Отпечаток файла рабочей копии вместе с его метой: мета меняется при правке настроек импорта.</summary>
        private static string StampOf(string projectPath)
        {
            return GitRepository.WorktreeStamp(projectPath) + "|" + GitRepository.WorktreeStamp(projectPath + ".meta");
        }

        private bool Present(string path, RevisionSide before, RevisionSide after)
        {
            if (string.IsNullOrEmpty(path) || (before == null && after == null)) { Hide(); return false; }

            var loader = AssetLoaders.Find(path);
            if (loader == null && !ThumbnailPresenter.Supports(path)) { Hide(); return false; }

            bool fromFile = loader != null && loader.PreferFileForWorktree;
            bool worktree = after != null && after.Kind == SideKind.Worktree;
            var stamp = worktree ? StampOf(after.ProjectPath) : null;

            // Сторона, прочитанная из файла, устаревает вместе с файлом, поэтому
            // отпечаток — часть ключа. Живой ассет перечитывать не нужно.
            var key = path + "#" + (before != null ? before.Key : "-") + "#" +
                      (after != null ? after.Key + (worktree && fromFile ? "|" + stamp : string.Empty) : "-");

            style.display = DisplayStyle.Flex;

            if (key == _key)
            {
                if (worktree && stamp != _stamp)
                {
                    _stamp = stamp;
                    RefreshLiveMeta();
                    Describe();
                }

                // Хозяин мог показать diff заново при перерисовке — прячем снова.
                UpdateChrome();
                return true;
            }

            if (path != _path) _importConsent = false;

            Release();
            _key = key;
            _path = path;
            _loader = loader;
            _beforeSide = before;
            _afterSide = after;
            _stamp = stamp;
            _loading = true;
            _note.text = L.T("Reading versions from git…");

            UpdateChrome();
            LoadAsync(_gen);
            return true;
        }

        /// <summary>Настройки импорта у живого ассета поменяли и сохранили — перечитываем мету.</summary>
        private void RefreshLiveMeta()
        {
            if (_after == null || _afterSide == null || _afterSide.Kind != SideKind.Worktree) return;

            var bytes = RevisionSide.ReadWorktreeMeta(_afterSide.ProjectPath);
            _after.MetaText = bytes != null ? System.Text.Encoding.UTF8.GetString(bytes) : null;
        }

        private async void LoadAsync(int gen)
        {
            var before = await LoadSide(_beforeSide, gen);
            if (gen != _gen) { RevisionCache.Release(before); return; }

            var after = await LoadSide(_afterSide, gen);
            if (gen != _gen)
            {
                RevisionCache.Release(before);
                RevisionCache.Release(after);
                return;
            }

            _before = before;
            _after = after;
            _loading = false;

            _presenter = AssetPresenters.Create(After() ?? Before());
            if (_presenter != null)
            {
                try
                {
                    _presenter.Prepare(Before(), After());
                }
                catch (Exception e)
                {
                    Diagnostics.Journal.Warn(L.F("Preview presenter was not prepared: {0}", e.Message));
                }
                _sync.Choice = _presenter.DefaultChoice;
            }

            RebuildShapes();
            RebuildChoices();

            _alignedInspector.Set(InspectorSide(0), _beforeSide != null ? _beforeSide.Caption : L.T("Before"),
                                  InspectorSide(1), _afterSide != null ? _afterSide.Caption : L.T("After"));

            Describe();
            UpdateChrome();
            RepaintAll();
        }

        private async Task<LoadedAsset> LoadSide(RevisionSide side, int gen)
        {
            if (side == null) return null;

            LoadedAsset loaded;

            if (side.Kind == SideKind.Worktree && (_loader == null || !_loader.PreferFileForWorktree))
            {
                loaded = LoadedAsset.FromProject(side.ProjectPath);
            }
            else if (_loader == null)
            {
                loaded = LoadedAsset.Failed(L.T("A previous version of this asset type cannot be shown yet — " +
                                                "Unity would have to import it into the project for that."));
            }
            else if (_loader.NeedsConsent && !_importConsent)
            {
                loaded = LoadedAsset.Failed(L.T("the previous version is built by a temporary import — press “Import Previous Version”"));
                loaded.NeedsImport = true;
            }
            else
            {
                // Одна и та же версия файла в разных коммитах — один объект git. По
                // нему и кэшируем: листая историю, ассет не грузится заново.
                var oid = await side.ResolveOidAsync();
                if (gen != _gen) return null;

                bool dependencies = GitSettings.instance.previewDependencies == 1;
                var cacheKey = oid != null ? oid + "|" + _path + (dependencies ? "|deps" : string.Empty) : null;
                var cached = RevisionCache.Acquire(cacheKey);
                if (cached != null) return cached;

                var bytes = await side.ReadAsync(() => { if (gen == _gen) _note.text = L.T("Fetching the version from LFS…"); });
                if (gen != _gen) return null;

                // Мета — до загрузки: временному импорту она и есть настройки импорта.
                var meta = await side.ReadMetaAsync();
                if (gen != _gen) return null;

                loaded = bytes == null
                    ? LoadedAsset.Failed(L.T("the version was not read: it is not in git or LFS is unavailable"))
                    : await AssetLoaders.SafeLoadAsync(_loader, _path, bytes, meta);

                if (gen != _gen) { RevisionCache.Release(loaded); return null; }
                loaded.MetaText = meta;

                if (dependencies && loaded.Main != null)
                {
                    _note.text = L.T("Picking up dependencies from the same commit…");
                    await DependencySnapshot.ApplyAsync(loaded, side);
                    if (gen != _gen) { RevisionCache.Release(loaded); return null; }
                }

                RevisionCache.Add(cacheKey, loaded);
                return loaded;
            }

            // У стороны без объекта мета всё равно читается: у модели изменения
            // настроек импорта видны и без самой модели.
            loaded.MetaText = await side.ReadMetaAsync();
            if (gen != _gen) { RevisionCache.Release(loaded); return null; }
            return loaded;
        }

        private LoadedAsset Before() { return _before != null && _before.Main != null ? _before : null; }
        private LoadedAsset After() { return _after != null && _after.Main != null ? _after : null; }

        /// <summary>Сторона, у которой есть что показать в инспекторе.</summary>
        private LoadedAsset InspectorSide(int slot)
        {
            var side = slot == 0 ? Before() : After();
            return side != null && side.Describable ? side : null;
        }

        private LoadedAsset SideOfSlot(int slot)
        {
            return slot == 0 ? _before : _after;
        }

        private void RepaintAll()
        {
            _view.MarkDirtyRepaint();
            _aligned.MarkDirtyRepaint();
            foreach (var column in _columns) column.MarkDirtyRepaint();
        }

        // ------------------------------------------------------- изменения ---

        private void Describe()
        {
            _items = null;

            try
            {
                _items = ChangeDescribers.DescribeAll(_before, _after);
            }
            catch (Exception e)
            {
                Diagnostics.Journal.Warn(L.F("Asset changes were not described: {0}", e.Message));
            }

            _alignedInspector.SetChanges(_items);
            _alignedInspector.ShowHidden = _showHidden;

            // Набор вкладок зависит от списка: появились изменения — появилась и их вкладка.
            UpdateChrome();
            _aligned.MarkDirtyRepaint();
        }

        private void ScheduleDescribe()
        {
            if (_describeScheduled) return;
            _describeScheduled = true;

            int gen = _gen;
            schedule.Execute(() =>
            {
                _describeScheduled = false;
                if (gen == _gen) Describe();
            }).StartingIn(300);
        }

        private void OnUndo()
        {
            if (_after == null || !_after.Live) return;
            ScheduleDescribe();
            RepaintAll();
        }

        private void RebuildChanges()
        {
            _changeRows.Clear();

            if (_items == null || CurrentTab() != Tab.Changes)
            {
                _changesBox.style.display = DisplayStyle.None;
                return;
            }

            _changesBox.style.display = DisplayStyle.Flex;

            int hidden = _items.Count(i => i.Folded);
            int shown = _items.Count - hidden;

            _changesTitle.text = _items.Count == 0
                ? L.T("No differences")
                : L.F("Changes: {0}", shown) + (hidden > 0 ? " · " + L.F("hidden {0}", hidden) : string.Empty) +
                  (_items.Count >= DescribeContext.Limit ? " · " + L.F("showing the first {0}", DescribeContext.Limit) : string.Empty);

            _hiddenToggle.style.display = hidden > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _hiddenToggle.text = _showHidden ? L.T("hide hidden") : L.T("show hidden");

            SetNote(_mainNote, MainRef());

            string group = null;
            bool first = true;

            foreach (var item in _items)
            {
                if (item.Folded && !_showHidden) continue;

                if (first || item.Group != group)
                {
                    first = false;
                    group = item.Group;
                    if (group != null) _changeRows.Add(GroupHeader(item));
                }

                _changeRows.Add(MakeChangeRow(item));
            }
        }

        private VisualElement MakeChangeRow(ChangeItem item)
        {
            var row = Ui.Box("pchange");
            if (item.Folded) row.AddToClassList("pchange--hidden");
            if (item.Group != null) row.AddToClassList("pchange--nested");

            var label = Ui.Text(item.Label, "pchange__label");
            label.tooltip = string.Join("\n", new[] { item.Label, item.Tooltip, item.Note }.Where(s => !string.IsNullOrEmpty(s)).ToArray());
            row.Add(label);

            row.Add(ValueElement(item.Before, "pchange__old"));
            row.Add(Ui.Text("→", "pchange__arrow"));
            row.Add(ValueElement(item.After, "pchange__new"));

            if (item.Note != null) row.Add(Ui.Text(item.Note, "pchange__note"));

            row.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                if (_after != null && _after.Live)
                {
                    var caption = _beforeSide != null ? _beforeSide.Caption : L.T("Before");
                    evt.menu.AppendAction(L.F("Revert to “{0}”", caption), _ => RevertItem(item),
                        ChangeReverter.CanRevert(item, _after) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                    evt.menu.AppendSeparator();
                }

                evt.menu.AppendAction(L.T("Copy Previous Value"), _ => EditorGUIUtility.systemCopyBuffer = item.Before.Text);
                evt.menu.AppendAction(L.T("Copy New Value"), _ => EditorGUIUtility.systemCopyBuffer = item.After.Text);

                // Комментарий цепляется к объекту файла, а не к одному свойству:
                // так его видно и в GitLab — у строки заголовка объекта в YAML.
                var target = RefFor(item);
                if (target != null && _diff != null && _diff.ObjectMenu != null) _diff.ObjectMenu(evt.menu, target);
            }));

            return row;
        }

        private static VisualElement ValueElement(ChangeValue value, string side)
        {
            var box = Ui.Box("pchange__value", side);

            if (value.Kind == ValueKind.Color)
            {
                var swatch = new VisualElement();
                swatch.AddToClassList("pchange__swatch");
                swatch.style.backgroundColor = new Color(value.Color.r, value.Color.g, value.Color.b, 1f);
                box.Add(swatch);
            }
            else if (value.Kind == ValueKind.Object && value.Ref != null)
            {
                var icon = new Image { image = AssetPreview.GetMiniThumbnail(value.Ref) };
                icon.AddToClassList("pchange__icon");
                box.Add(icon);
            }

            var text = Ui.Text(value.Text ?? string.Empty, "pchange__text");
            text.tooltip = value.Text;
            box.Add(text);
            return box;
        }

        private void RevertItem(ChangeItem item)
        {
            _message = ChangeReverter.Revert(item);

            Describe();
            RepaintAll();
        }

        // ------------------------------------------------------- комментарии ---

        /// <summary>Пересчитать пометки обсуждений — их набор дочитался или сменился.</summary>
        public void RefreshNotes()
        {
            if (style.display == DisplayStyle.Flex) RebuildChanges();
        }

        private VisualElement GroupHeader(ChangeItem item)
        {
            var row = Ui.Box("pchange__grouprow");
            row.Add(Ui.Text(item.Group, "pchange__group"));

            var target = RefFor(item);
            var note = Ui.Text(string.Empty, "pchanges__comments");
            SetNote(note, target);
            note.RegisterCallback<ClickEvent>(_ => OpenNotes(target));
            row.Add(note);

            row.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                if (target != null && _diff != null && _diff.ObjectMenu != null) _diff.ObjectMenu(evt.menu, target);
            }));

            return row;
        }

        /// <summary>Сам ассет — главный объект файла.</summary>
        private DiffObjectRef MainRef()
        {
            var after = After();
            var before = Before();
            var side = after ?? before;
            if (side == null) return null;

            return MakeRef(side.FileIdOf(side.Main), MainTitle(), null, before == null, after == null);
        }

        /// <summary>Объект, к которому относится изменение: сам ассет или его подобъект.</summary>
        private DiffObjectRef RefFor(ChangeItem item)
        {
            if (item.Group == null) return MainRef();

            bool fromAfter = item.AfterTarget != null && _after != null;
            var target = fromAfter ? item.AfterTarget : item.BeforeTarget;
            var side = fromAfter ? _after : _before;
            if (target == null || side == null) return null;

            var title = string.IsNullOrEmpty(target.name) ? target.GetType().Name : target.name;
            return MakeRef(side.FileIdOf(target), title, MainTitle(),
                           item.Kind == ChangeKind.Added, item.Kind == ChangeKind.Removed);
        }

        private string MainTitle()
        {
            var side = After() ?? Before();
            var name = side != null ? side.Main.name : null;
            return string.IsNullOrEmpty(name) ? System.IO.Path.GetFileNameWithoutExtension(_path ?? string.Empty) : name;
        }

        private DiffObjectRef MakeRef(long fileId, string title, string owner, bool added, bool removed)
        {
            if (fileId == 0) return null;

            var gitPath = _afterSide != null ? _afterSide.GitPath : _beforeSide != null ? _beforeSide.GitPath : null;
            if (gitPath == null) return null;

            return new DiffObjectRef
            {
                GitPath = gitPath,
                OldGitPath = _beforeSide != null ? _beforeSide.GitPath : gitPath,
                ProjectPath = _path,
                FileId = fileId,
                Title = title,
                OwnerTitle = owner,
                Added = added,
                Removed = removed
            };
        }

        private void SetNote(Label label, DiffObjectRef target)
        {
            var text = target != null && _diff != null && _diff.ObjectNote != null ? _diff.ObjectNote(target) : null;
            label.text = text ?? string.Empty;
            Show(label, !string.IsNullOrEmpty(text));
        }

        private void OpenNotes(DiffObjectRef target)
        {
            if (target != null && _diff != null && _diff.ObjectNoteClicked != null) _diff.ObjectNoteClicked(target);
        }

        private void ToggleHidden()
        {
            _showHidden = !_showHidden;
            _alignedInspector.ShowHidden = _showHidden;
            UpdateChrome();
            _aligned.MarkDirtyRepaint();
        }

        // ------------------------------------------------------------ вкладки ---

        private static bool IsPreviewTab(Tab tab)
        {
            return tab == Tab.View || tab == Tab.Inspector || tab == Tab.Changes;
        }

        /// <summary>Есть ли картинка, которую рисует показ.</summary>
        private bool Drawable()
        {
            return !_loading && _presenter != null && (Presents(Before()) || Presents(After()));
        }

        private bool HasView()
        {
            if (_loading) return _loader != null || ThumbnailPresenter.Supports(_path);
            return Drawable();
        }

        /// <summary>
        /// Вкладка «Изменения» есть, когда есть что перечислить. Пока версии
        /// грузятся, выбранная вкладка не пропадает — иначе она мигала бы.
        /// </summary>
        private bool HasChanges()
        {
            if (_loading) return _tab == Tab.Changes;
            return _items != null && _items.Count > 0;
        }

        private bool HasInspector()
        {
            if (_loading) return _loader != null && !_loader.PreferFileForWorktree;
            return InspectorSide(0) != null || InspectorSide(1) != null;
        }

        /// <summary>
        /// Вкладка, которая показывается на самом деле. Выбор человека не
        /// переписывается: у скрипта нет «Вида», но следующий материал снова
        /// откроется на «Виде».
        /// </summary>
        private Tab CurrentTab()
        {
            bool view = HasView(), inspector = HasInspector(), changes = HasChanges();
            bool objects = _diff != null && _diff.SemanticAvailable;
            bool text = _diff != null;

            Func<Tab, bool> available = t =>
                t == Tab.View ? view : t == Tab.Inspector ? inspector : t == Tab.Changes ? changes :
                t == Tab.Objects ? objects : text;

            if (available(_tab)) return _tab;

            // У модели из истории картинки нет — сразу к списку изменений импорта.
            var order = IsPreviewTab(_tab)
                ? new[] { Tab.View, Tab.Inspector, Tab.Changes, Tab.Objects, Tab.Text }
                : new[] { Tab.Objects, Tab.Text, Tab.View, Tab.Inspector, Tab.Changes };

            foreach (var t in order)
                if (available(t)) return t;

            return Tab.View;
        }

        private void UpdateChrome()
        {
            var tab = CurrentTab();
            bool previewTab = IsPreviewTab(tab);
            bool drawable = Drawable();

            Show(_tabView, HasView());
            Show(_tabInspector, HasInspector());
            Show(_tabChanges, HasChanges());
            Show(_tabObjects, _diff != null && _diff.SemanticAvailable);
            Show(_tabText, _diff != null);
            _tabChanges.text = _items != null && _items.Count > 0
                ? L.F("Changes · {0}", _items.Count(i => !i.Folded || _showHidden))
                : L.T("Changes");
            _tabView.EnableInClassList("act--primary", tab == Tab.View);
            _tabInspector.EnableInClassList("act--primary", tab == Tab.Inspector);
            _tabChanges.EnableInClassList("act--primary", tab == Tab.Changes);
            _tabObjects.EnableInClassList("act--primary", tab == Tab.Objects);
            _tabText.EnableInClassList("act--primary", tab == Tab.Text);

            ApplyDiff(tab);

            bool inspectorTab = tab == Tab.Inspector;
            Show(_inspectModes, inspectorTab && !_loading);
            Show(_importButton, !_loading && ((_before != null && _before.NeedsImport) || (_after != null && _after.NeedsImport)));
            _inspectAligned.EnableInClassList("act--primary", !_unityInspector);
            _inspectUnity.EnableInClassList("act--primary", _unityInspector);

            UpdateTools(tab == Tab.View && drawable);

            // На своих вкладках панель занимает всю высоту, на вкладках diff —
            // только строку вкладок.
            style.flexGrow = previewTab ? 1f : 0f;
            style.flexShrink = previewTab ? 1f : 0f;

            bool showView = tab == Tab.View && (drawable || _loading);
            Show(_body, previewTab && (showView || inspectorTab));
            Show(_view, showView);
            Show(_aligned, inspectorTab && !_unityInspector);
            Show(_inspector, inspectorTab && _unityInspector);
            Show(_columns[0], InspectorSide(0) != null);
            Show(_columns[1], InspectorSide(1) != null);
            _columns[1].EnableInClassList("preview__column--right", InspectorSide(0) != null);

            RebuildChanges();
            UpdateNote();
        }

        /// <summary>Инструменты вида — только те, что умеет показ и подходят к режиму.</summary>
        private void UpdateTools(bool visible)
        {
            Show(_tools, visible);
            if (!visible || _presenter == null) return;

            var features = _presenter.Features;
            bool both = Presents(Before()) && Presents(After());
            var modes = _presenter.Modes;
            var mode = EffectiveMode();

            Show(_modes, both && modes != CompareModes.SideBySide);
            Show(_modeSide, (modes & CompareModes.SideBySide) != 0);
            Show(_modeSwipe, (modes & CompareModes.Swipe) != 0);
            Show(_modeOverlay, (modes & CompareModes.Overlay) != 0);
            Show(_modeDifference, (modes & CompareModes.Difference) != 0 && PreviewCanvas.Advanced);
            Show(_modeToggle, (modes & CompareModes.Toggle) != 0);
            _modeSide.EnableInClassList("act--primary", mode == CompareModes.SideBySide);
            _modeSwipe.EnableInClassList("act--primary", mode == CompareModes.Swipe);
            _modeOverlay.EnableInClassList("act--primary", mode == CompareModes.Overlay);
            _modeDifference.EnableInClassList("act--primary", mode == CompareModes.Difference);
            _modeToggle.EnableInClassList("act--primary", mode == CompareModes.Toggle);

            Show(_opacity, both && mode == CompareModes.Overlay);
            Show(_threshold, both && mode == CompareModes.Difference);

            Show(_pick, both && mode == CompareModes.Toggle);
            _pickBefore.text = _beforeSide != null ? _beforeSide.Caption : L.T("Before");
            _pickAfter.text = _afterSide != null ? _afterSide.Caption : L.T("After");
            _pickBefore.EnableInClassList("act--primary", _sync.ShowBefore);
            _pickAfter.EnableInClassList("act--primary", !_sync.ShowBefore);

            Show(_shapes, _presenter.Shapes != null);
            int index = 0;
            foreach (var child in _shapes.Children())
                child.EnableInClassList("act--primary", index++ == _sync.Shape);

            bool channels = (features & PresenterFeatures.Channels) != 0 && PreviewCanvas.Advanced && mode != CompareModes.Difference;
            Show(_channels, channels);
            for (int i = 0; i < _channelButtons.Length; i++)
                _channelButtons[i].EnableInClassList("act--primary", i == _sync.Channel);

            bool slices = (features & PresenterFeatures.Slices) != 0 &&
                          (_presenter.HasOverlay(Before()) || _presenter.HasOverlay(After()));
            Show(_slicesToggle, slices);
            _slicesToggle.EnableInClassList("act--primary", _sync.ShowSlices);

            bool zoom = (features & PresenterFeatures.Zoom) != 0;
            Show(_zoomReset, zoom);
            _zoomReset.text = _sync.Zoom > 1.01f ? "×" + _sync.Zoom.ToString("0.#") + " · " + L.T("fit") : L.T("magnifier: wheel");
            _zoomReset.SetEnabled(_sync.Zoom > 1.01f);

            bool playback = (features & PresenterFeatures.Playback) != 0 && _presenter.PlaybackAvailable;
            bool perSide = _presenter.PlaybackPerSide;
            Show(_playback, playback);
            Show(_playTime, playback);
            Show(_playBefore, !perSide || Presents(Before()));
            Show(_playAfter, perSide && Presents(After()));
            _playBefore.text = "▶ " + (perSide ? (_beforeSide != null ? _beforeSide.Caption : L.T("Before")) : L.T("Play"));
            _playAfter.text = "▶ " + (_afterSide != null ? _afterSide.Caption : L.T("After"));

            Show(_choice, _presenter.ChoicesVisible(_sync));
            UpdatePlayback();
        }

        /// <summary>Diff виден только на своих вкладках, и в нужном режиме.</summary>
        private void ApplyDiff(Tab tab)
        {
            if (_diff == null) return;

            _diff.ExternalModeBar = true;

            if (IsPreviewTab(tab))
            {
                if (_diff.style.display != DisplayStyle.None)
                {
                    _diff.style.display = DisplayStyle.None;
                    _hidDiff = true;
                }
                return;
            }

            RestoreDiff();
            _diff.SetTextMode(tab == Tab.Text);
        }

        private static void Show(VisualElement e, bool visible)
        {
            e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void UpdateNote()
        {
            if (_loading) return;

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(_message)) parts.Add(_message);

            AddSideNote(parts, _beforeSide, _before);
            AddSideNote(parts, _afterSide, _after);

            _note.text = string.Join(" · ", parts.Distinct().ToArray());
        }

        private static void AddSideNote(List<string> parts, RevisionSide side, LoadedAsset loaded)
        {
            if (side == null || loaded == null) return;

            var caption = side.Caption.ToLowerInvariant();
            if (loaded.Error != null) { parts.Add(caption + ": " + loaded.Error); return; }
            if (!string.IsNullOrEmpty(loaded.Info)) parts.Add(caption + " " + loaded.Info);
            foreach (var n in loaded.Notes) parts.Add(caption + ": " + n);
        }

        private void RebuildShapes()
        {
            _shapes.Clear();
            if (_presenter == null || _presenter.Shapes == null) return;

            var shapes = _presenter.Shapes;
            for (int i = 0; i < shapes.Length; i++)
            {
                int index = i;
                _shapes.Add(Ui.Action(shapes[i], () =>
                {
                    _sync.Shape = index;
                    UpdateChrome();
                    _view.MarkDirtyRepaint();
                }));
            }
        }

        private void RebuildChoices()
        {
            var choices = _presenter != null ? _presenter.Choices : null;
            if (choices == null || choices.Length == 0)
            {
                _choice.choices = new List<string>();
                return;
            }

            _choice.choices = new List<string>(choices);
            _choice.SetValueWithoutNotify(choices[Mathf.Clamp(_sync.Choice, 0, choices.Length - 1)]);
        }

        private CompareModes EffectiveMode()
        {
            if (_presenter == null) return CompareModes.SideBySide;
            if ((_presenter.Modes & _mode) == 0) return CompareModes.SideBySide;
            if (_mode == CompareModes.Difference && !PreviewCanvas.Advanced) return CompareModes.SideBySide;
            return _mode;
        }

        private void SetTab(Tab tab)
        {
            _tab = tab;
            UpdateChrome();
            RepaintAll();
        }

        private void SetMode(CompareModes mode)
        {
            _mode = mode;
            UpdateChrome();
            _view.MarkDirtyRepaint();
        }

        private void SetUnityInspector(bool unity)
        {
            _unityInspector = unity;
            UpdateChrome();
            RepaintAll();
        }

        private void PickSide(bool before)
        {
            _sync.ShowBefore = before;

            // Переключили версию во время звучания — продолжаем её с того же места.
            if (_presenter != null && _presenter.Playing)
                _presenter.Play(before ? _before : _after, before ? 0 : 1, _presenter.PlayTime);

            UpdateChrome();
            _view.MarkDirtyRepaint();
        }

        private void SetChannel(int channel)
        {
            _sync.Channel = channel;
            UpdateChrome();
            _view.MarkDirtyRepaint();
        }

        private void ToggleSlices()
        {
            _sync.ShowSlices = !_sync.ShowSlices;
            UpdateChrome();
            _view.MarkDirtyRepaint();
        }

        private void ResetZoom()
        {
            _sync.Zoom = 1f;
            _sync.Pan = new Vector2(0.5f, 0.5f);
            _sync.Dolly = 1f;
            _sync.Offset = Vector2.zero;
            UpdateChrome();
            _view.MarkDirtyRepaint();
        }

        /// <summary>Человек разрешил временный импорт — перечитываем версии уже с ним.</summary>
        private void ImportForPreview()
        {
            _importConsent = true;

            var path = _path;
            var before = _beforeSide;
            var after = _afterSide;

            Release();
            Present(path, before, after);
        }

        // ------------------------------------------------------ воспроизведение ---

        /// <summary>
        /// ▶ у стороны: не играет — играть с того места, где остановились;
        /// играет другая — переключиться на эту с того же места; играет эта — стоп.
        /// </summary>
        private void TogglePlay(int slot)
        {
            if (_presenter == null) return;

            if (_presenter.Playing && _presenter.PlayingSlot == slot)
            {
                _presenter.Stop();
            }
            else
            {
                var side = SideOfSlot(slot);
                float time = _presenter.PlayTime;
                if (time >= _presenter.Length(side) - 0.05f) time = 0f;
                _presenter.Play(side, slot, time);
            }

            UpdatePlayback();
            _view.MarkDirtyRepaint();
        }

        private void StopPlayback()
        {
            if (_presenter != null) _presenter.Stop();
            UpdatePlayback();
            _view.MarkDirtyRepaint();
        }

        private void UpdatePlayback()
        {
            if (_presenter == null || (_presenter.Features & PresenterFeatures.Playback) == 0) return;

            int playing = _presenter.PlayingSlot;
            _playBefore.EnableInClassList("act--primary", playing == 0);
            _playAfter.EnableInClassList("act--primary", playing == 1);
            _stop.SetEnabled(playing >= 0);

            var side = After() ?? Before();
            float length = _presenter.Length(side);
            _playTime.text = length > 0f
                ? AudioAssetLoader.Duration(_presenter.PlayTime) + " / " + AudioAssetLoader.Duration(length)
                : string.Empty;

            if (playing >= 0) _view.MarkDirtyRepaint();
        }

        // --------------------------------------------------------------- вид ---

        private struct SideRef
        {
            public LoadedAsset Side;
            public string Caption;
            public int Slot;
            public bool Presentable;
        }

        private List<SideRef> Sides()
        {
            var sides = new List<SideRef>();

            if (_beforeSide != null && _before != null)
                sides.Add(new SideRef { Side = _before, Caption = _beforeSide.Caption, Slot = 0, Presentable = _presenter != null && Presents(_before) });

            if (_afterSide != null && _after != null)
                sides.Add(new SideRef { Side = _after, Caption = _afterSide.Caption, Slot = 1, Presentable = _presenter != null && Presents(_after) });

            return sides;
        }

        /// <summary>Фон ячейки — из настроек проекта: тёмный, светлый, шахматка или свой цвет.</summary>
        private static void FillBackground(Rect rect)
        {
            PreviewBackground.Fill(rect);
        }

        private void RepaintView()
        {
            _view.MarkDirtyRepaint();
        }

        /// <summary>Показ упал — текст для панели вместо картинки. Сбрасывается при смене файла.</summary>
        private string _presenterError;

        /// <summary>
        /// Может ли показ нарисовать сторону. Показ может быть из стороннего пакета —
        /// его исключение не должно ломать панель.
        /// </summary>
        private bool Presents(LoadedAsset side)
        {
            var presenter = _presenter;
            if (presenter == null || side == null || _presenterError != null) return false;

            try
            {
                return presenter.CanPresent(side);
            }
            catch (Exception e)
            {
                ReportPresenter(e);
                return false;
            }
        }

        private void ReportPresenter(Exception e)
        {
            if (_presenterError != null) return;

            var name = _presenter != null ? _presenter.GetType().Name : L.T("presenter");
            _presenterError = L.F("Presenter {0} could not draw this version: {1}", name, e.Message);
            Diagnostics.Journal.Warn(_presenterError + "\n" + e);
            schedule.Execute(UpdateChrome);
        }

        private void DrawView()
        {
            if (_presenterError != null)
            {
                var area = new Rect(0f, 0f, _view.contentRect.width, _view.contentRect.height);
                GUI.Label(new RectOffset(12, 12, 12, 12).Remove(area), _presenterError,
                          new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true });
                return;
            }

            try
            {
                DrawViewUnsafe();
            }
            catch (ExitGUIException)
            {
                throw;
            }
            catch (Exception e)
            {
                ReportPresenter(e);
                _view.MarkDirtyRepaint();
            }
        }

        private void DrawViewUnsafe()
        {
            var rect = new Rect(0f, 0f, _view.contentRect.width, _view.contentRect.height);
            if (rect.width < 10f || rect.height < 10f) return;

            if (_loading || _presenter == null)
            {
                GUI.Label(rect, _loading ? L.Tc("preview", "Loading…") : string.Empty, EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var sides = Sides();
            if (sides.Count == 0) return;

            bool both = sides.Count == 2 && sides[0].Presentable && sides[1].Presentable;
            var mode = both ? EffectiveMode() : CompareModes.SideBySide;

            // У показов с воспроизведением под картинкой — полоса времени: по ней
            // перематывают, а щелчок по самой картинке запуск не трогает.
            _stripRect = default(Rect);
            if ((_presenter.Features & PresenterFeatures.Playback) != 0 && _presenter.PlaybackAvailable && rect.height > 60f)
            {
                _stripRect = new Rect(rect.x, rect.yMax - 18f, rect.width, 18f);
                rect.height -= 20f;
            }

            HandleInput(rect, mode);

            if (Event.current.type != EventType.Repaint) return;

            FillBackground(rect);

            switch (mode)
            {
                case CompareModes.Swipe:
                {
                    DrawContent(rect, sides[0], sides[1], 1f, true);

                    float split = rect.x + rect.width * _sync.Swipe;
                    GUI.BeginClip(new Rect(split, rect.y, rect.xMax - split, rect.height));
                    DrawContent(new Rect(rect.x - split, 0f, rect.width, rect.height), sides[1], sides[0], 1f, false);
                    GUI.EndClip();

                    _fits[1] = _fits[0];
                    _sources[1] = _sources[0];

                    EditorGUI.DrawRect(new Rect(split - 1f, rect.y, 2f, rect.height), new Color(0.24f, 0.42f, 0.66f));
                    Caption(rect, sides[0].Caption, false);
                    Caption(rect, sides[1].Caption, true);
                    break;
                }

                case CompareModes.Overlay:
                {
                    DrawContent(rect, sides[0], sides[1], 1f, true);
                    DrawContent(rect, sides[1], sides[0], _sync.Opacity, true);
                    Caption(rect, sides[0].Caption + " + " + sides[1].Caption + " " + Mathf.RoundToInt(_sync.Opacity * 100f) + "%", false);
                    break;
                }

                case CompareModes.Difference:
                {
                    DrawDifference(rect, sides[0], sides[1]);
                    Caption(rect, L.F("Difference · threshold {0}%", Mathf.RoundToInt(_sync.Threshold * 100f)), false);
                    break;
                }

                case CompareModes.Toggle:
                {
                    var shown = _sync.ShowBefore ? sides[0] : sides[1];
                    var other = _sync.ShowBefore ? sides[1] : sides[0];
                    DrawContent(rect, shown, other, 1f, true);
                    Caption(rect, shown.Caption, false);
                    break;
                }

                default:
                {
                    const float gap = 4f;
                    bool stack = _presenter.Stack(_sync);
                    float size = ((stack ? rect.height : rect.width) - gap * (sides.Count - 1)) / sides.Count;

                    for (int i = 0; i < sides.Count; i++)
                    {
                        var cell = stack
                            ? new Rect(rect.x, rect.y + i * (size + gap), rect.width, size)
                            : new Rect(rect.x + i * (size + gap), rect.y, size, rect.height);

                        FillBackground(cell);
                        DrawContent(cell, sides[i], sides.Count > 1 ? sides[1 - i] : default(SideRef), 1f, true);
                        Caption(cell, sides[i].Caption, false);
                    }
                    break;
                }
            }

            if (_stripRect.width > 0f) DrawStrip(_stripRect);

            if (_presenter.NeedsRepaint)
                schedule.Execute(() => _view.MarkDirtyRepaint()).StartingIn(60);
        }

        private void DrawContent(Rect cell, SideRef side, SideRef other, float opacity, bool record)
        {
            if (!side.Presentable)
            {
                var text = side.Side != null ? side.Side.Error ?? L.T("nothing to show it with") : string.Empty;
                var style = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true };
                GUI.Label(new RectOffset(8, 8, 8, 8).Remove(cell), text, style);
                return;
            }

            var texture = _presenter.Render(cell, side.Side, side.Slot, _sync);
            if (texture == null) return;

            var features = _presenter.Features;
            var fit = PreviewCanvas.Fit(cell, _presenter.Aspect(side.Side));
            var source = PreviewCanvas.Source(_sync, (features & PresenterFeatures.Zoom) != 0);
            bool channels = (features & PresenterFeatures.Channels) != 0;

            if (channels && opacity >= 1f && _sync.Channel == 0 && PreviewBackground.CheckerUnderImages) PreviewCanvas.Checker(fit);
            PreviewCanvas.Draw(fit, texture, source, _sync, channels, null, false, opacity);
            _presenter.DrawOverlay(fit, source, side.Side, other.Side, side.Slot == 1, _sync);

            if (record)
            {
                _fits[side.Slot] = fit;
                _sources[side.Slot] = source;
            }
        }

        private void DrawDifference(Rect rect, SideRef before, SideRef after)
        {
            var beforeTexture = _presenter.Render(rect, before.Side, before.Slot, _sync);
            var afterTexture = _presenter.Render(rect, after.Side, after.Slot, _sync);
            if (beforeTexture == null || afterTexture == null) return;

            var fit = PreviewCanvas.Fit(rect, _presenter.Aspect(after.Side));
            var source = PreviewCanvas.Source(_sync, (_presenter.Features & PresenterFeatures.Zoom) != 0);
            PreviewCanvas.Draw(fit, afterTexture, source, _sync, false, beforeTexture, true, 1f);

            _fits[0] = _fits[1] = fit;
            _sources[0] = _sources[1] = source;
        }

        private static void Caption(Rect rect, string text, bool right)
        {
            var style = EditorStyles.miniBoldLabel;
            var size = style.CalcSize(new GUIContent(text));
            var box = new Rect(right ? rect.xMax - size.x - 8f : rect.x + 4f, rect.y + 3f, size.x + 4f, size.y);
            EditorGUI.DrawRect(box, new Color(0f, 0f, 0f, 0.35f));
            GUI.Label(new Rect(box.x + 2f, box.y, size.x, size.y), text, style);
        }

        /// <summary>Сторона под курсором по последней отрисовке; -1 — мимо картинок.</summary>
        private int SlotAt(Vector2 mouse)
        {
            if (_fits[1].width > 0f && _fits[1].Contains(mouse) && _after != null) return 1;
            if (_fits[0].width > 0f && _fits[0].Contains(mouse) && _before != null) return 0;
            return -1;
        }

        private void HandleInput(Rect rect, CompareModes mode)
        {
            var e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);

            var features = _presenter.Features;
            bool zoom = (features & PresenterFeatures.Zoom) != 0;
            bool playback = (features & PresenterFeatures.Playback) != 0;

            switch (e.GetTypeForControl(id))
            {
                case EventType.ScrollWheel:
                {
                    // 3D-превью: колесо приближает и отдаляет камеру.
                    if (!zoom && _presenter.Interactive && rect.Contains(e.mousePosition))
                    {
                        _sync.Dolly = Mathf.Clamp(_sync.Dolly * (e.delta.y < 0f ? 0.9f : 1.1f), 0.15f, 6f);
                        e.Use();
                        _view.MarkDirtyRepaint();
                        break;
                    }

                    if (!zoom || !rect.Contains(e.mousePosition)) break;
                    int slot = SlotAt(e.mousePosition);
                    if (slot < 0) break;

                    ZoomAt(_fits[slot], _sources[slot], e.mousePosition, e.delta.y < 0f ? 1.25f : 0.8f);
                    e.Use();
                    UpdateChrome();
                    _view.MarkDirtyRepaint();
                    break;
                }

                case EventType.MouseDown:
                {
                    // Полоса времени или сама линия воспроизведения — перемотка.
                    Rect area = default(Rect);
                    if (playback && e.button == 0 &&
                        (_stripRect.Contains(e.mousePosition) || NearPlayhead(e.mousePosition, out area)))
                    {
                        _scrubArea = _stripRect.Contains(e.mousePosition) ? _presenter.TimeArea(_stripRect) : area;
                        _dragScrub = true;
                        ScrubTo(e.mousePosition.x);
                        GUIUtility.hotControl = id;
                        e.Use();
                        break;
                    }

                    if (!rect.Contains(e.mousePosition)) break;

                    if ((zoom || _presenter.Interactive) && e.button == 0 && e.clickCount == 2)
                    {
                        ResetZoom();
                        e.Use();
                        break;
                    }

                    float split = rect.x + rect.width * _sync.Swipe;
                    bool nearSplit = mode == CompareModes.Swipe && Mathf.Abs(e.mousePosition.x - split) < 8f;
                    bool zoomed = zoom && _sync.Zoom > 1.01f;

                    _dragSwipe = _dragPan = _dragScrub = _dragShift = false;

                    if (e.button == 0 && mode == CompareModes.Swipe && (nearSplit || (!_presenter.Interactive && !zoomed)))
                    {
                        _dragSwipe = true;
                        _sync.Swipe = Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width);
                    }
                    else if (zoom && (e.button == 1 || e.button == 2 || (e.button == 0 && zoomed)))
                    {
                        _dragPan = true;
                        _dragSlot = Mathf.Max(0, SlotAt(e.mousePosition));
                    }
                    else if (_presenter.Interactive && (e.button == 1 || e.button == 2))
                    {
                        _dragShift = true;
                        _dragSlot = Mathf.Max(0, SlotAt(e.mousePosition));
                    }
                    else if (e.button == 0 && _presenter.Interactive)
                    {
                        // Вращение — ниже, в перетаскивании.
                    }
                    else
                    {
                        if (e.button == 0 && mode == CompareModes.Toggle) PickSide(!_sync.ShowBefore);
                        break;
                    }

                    GUIUtility.hotControl = id;
                    e.Use();
                    _view.MarkDirtyRepaint();
                    break;
                }

                case EventType.MouseDrag:
                {
                    if (GUIUtility.hotControl != id) break;

                    if (_dragScrub)
                    {
                        ScrubTo(e.mousePosition.x);
                    }
                    else if (_dragShift)
                    {
                        var fit = _fits[_dragSlot];
                        float height = fit.height > 0f ? fit.height : rect.height;
                        _sync.Offset += new Vector2(e.delta.x, -e.delta.y) / Mathf.Max(20f, height);
                    }
                    else if (_dragSwipe)
                    {
                        _sync.Swipe = Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width);
                    }
                    else if (_dragPan)
                    {
                        var fit = _fits[_dragSlot];
                        float size = 1f / Mathf.Max(1f, _sync.Zoom);
                        if (fit.width > 0f && fit.height > 0f)
                            _sync.Pan = ClampPan(new Vector2(
                                _sync.Pan.x - e.delta.x / fit.width * size,
                                _sync.Pan.y + e.delta.y / fit.height * size), size);
                    }
                    else
                    {
                        _sync.Orbit += e.delta * 0.6f;
                        _sync.Orbit.y = Mathf.Clamp(_sync.Orbit.y, -89f, 89f);
                    }

                    e.Use();
                    _view.MarkDirtyRepaint();
                    break;
                }

                case EventType.MouseUp:
                {
                    if (GUIUtility.hotControl != id) break;
                    GUIUtility.hotControl = 0;
                    _dragSwipe = _dragPan = _dragScrub = _dragShift = false;
                    e.Use();
                    break;
                }
            }
        }

        /// <summary>Мышь у линии воспроизведения в одной из сторон — её можно тянуть.</summary>
        private bool NearPlayhead(Vector2 mouse, out Rect area)
        {
            area = default(Rect);
            if (_presenter == null) return false;

            float time = _presenter.PlayTime;
            for (int slot = 0; slot < 2; slot++)
            {
                var fit = _fits[slot];
                if (fit.width <= 0f || !fit.Contains(mouse)) continue;

                float length = _presenter.Length(SideOfSlot(slot));
                if (length <= 0f) continue;

                var candidate = _presenter.TimeArea(fit);
                float x = candidate.x + candidate.width * Mathf.Clamp01(time / length);
                if (Mathf.Abs(mouse.x - x) > 6f) continue;

                area = candidate;
                return true;
            }

            return false;
        }

        private void ScrubTo(float x)
        {
            if (_presenter == null || _scrubArea.width <= 0f) return;

            var side = After() ?? Before();
            float length = _presenter.Length(side);
            if (length <= 0f) return;

            float time = Mathf.Clamp01((x - _scrubArea.x) / _scrubArea.width) * length;
            int slot = _presenter.PlayingSlot >= 0 ? _presenter.PlayingSlot : After() != null ? 1 : 0;

            _presenter.Seek(SideOfSlot(slot), slot, time);
            UpdatePlayback();
            _view.MarkDirtyRepaint();
        }

        /// <summary>Полоса времени: пройденная часть, деления, ручка и время справа.</summary>
        private void DrawStrip(Rect strip)
        {
            float length = _presenter.Length(After() ?? Before());
            if (length <= 0f) return;

            EditorGUI.DrawRect(strip, new Color(0f, 0f, 0f, 0.35f));

            var area = _presenter.TimeArea(strip);
            float time = Mathf.Clamp(_presenter.PlayTime, 0f, length);
            float x = area.x + area.width * (time / length);

            EditorGUI.DrawRect(new Rect(area.x, strip.y + strip.height * 0.5f - 1f, area.width, 2f), new Color(1f, 1f, 1f, 0.15f));
            EditorGUI.DrawRect(new Rect(area.x, strip.y + strip.height * 0.5f - 1f, x - area.x, 2f), new Color(0.35f, 0.55f, 0.85f));

            float step = TickStep(length, area.width);
            for (int i = 0; i * step <= length + 1e-4f && i < 1000; i++)
            {
                float tx = area.x + area.width * (i * step / length);
                EditorGUI.DrawRect(new Rect(tx, strip.yMax - 5f, 1f, 5f), new Color(1f, 1f, 1f, 0.35f));
            }

            EditorGUI.DrawRect(new Rect(x - 4f, strip.y + 2f, 8f, strip.height - 4f), Color.white);

            var label = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            GUI.Label(new Rect(strip.xMax - 120f, strip.y, 116f, strip.height),
                      AudioAssetLoader.Duration(time) + " / " + AudioAssetLoader.Duration(length), label);
        }

        private static float TickStep(float length, float width)
        {
            foreach (var step in new[] { 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 30f, 60f })
                if (width * step / length >= 40f) return step;
            return 120f;
        }

        /// <summary>Увеличение под курсором: точка картинки под мышью остаётся на месте.</summary>
        private void ZoomAt(Rect fit, Rect source, Vector2 mouse, float factor)
        {
            if (fit.width <= 0f || fit.height <= 0f) return;

            float fx = (mouse.x - fit.x) / fit.width;
            float fy = (fit.yMax - mouse.y) / fit.height;
            float u = source.x + fx * source.width;
            float v = source.y + fy * source.height;

            float zoom = Mathf.Clamp(_sync.Zoom * factor, 1f, 64f);
            float size = 1f / zoom;

            _sync.Zoom = zoom;
            _sync.Pan = ClampPan(new Vector2(u - (fx - 0.5f) * size, v - (fy - 0.5f) * size), size);
        }

        private static Vector2 ClampPan(Vector2 pan, float size)
        {
            return new Vector2(Mathf.Clamp(pan.x, size * 0.5f, 1f - size * 0.5f),
                               Mathf.Clamp(pan.y, size * 0.5f, 1f - size * 0.5f));
        }

        // ------------------------------------------------- инспектор: поля ---

        private void DrawAligned()
        {
            float width = _aligned.contentRect.width;
            float height = _aligned.contentRect.height;
            if (width < 60f || height < 30f) return;

            _alignedInspector.Draw(width, height);
        }

        // -------------------------------------------- инспектор: как в Unity ---

        /// <summary>
        /// Ширина окна, которую видят инспекторы. Публично её только читают, а
        /// инспекторы по ней решают, где кончается строка: без подмены поля
        /// колонки растягивались бы на всё окно. Поле внутреннее — не нашлось,
        /// значит колонки просто будут шире, чем надо, но рисоваться будут.
        /// </summary>
        private static readonly System.Reflection.FieldInfo OverriddenViewWidth = FindViewWidthField();

        private static System.Reflection.FieldInfo FindViewWidthField()
        {
            var field = typeof(EditorGUIUtility).GetField("s_OverriddenViewWidth",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return field != null && field.FieldType == typeof(float) ? field : null;
        }

        /// <summary>Нашлись ли внутренние члены Unity, на которые опирается инспектор по колонкам. Для отчёта о проблеме.</summary>
        internal static bool ViewWidthHookFound => OverriddenViewWidth != null;
        internal static bool FirstInspectedEditorFound => FirstInspectedEditor != null;

        private void DrawInspectorColumn(int slot)
        {
            var side = InspectorSide(slot);
            var info = slot == 0 ? _beforeSide : _afterSide;
            if (side == null || info == null) return;

            const float scrollbar = 14f;
            float width = _columns[slot].contentRect.width;
            if (width < 40f) return;

            GUILayout.Label(info.Caption + " · " + (side.Live ? L.T("editable") : L.T("read-only")), EditorStyles.miniBoldLabel);

            var editor = EditorFor(slot, side.Main, side.Live);
            if (editor == null) return;

            object savedViewWidth = OverriddenViewWidth != null ? OverriddenViewWidth.GetValue(null) : null;
            float savedLabelWidth = EditorGUIUtility.labelWidth;
            bool savedWideMode = EditorGUIUtility.wideMode;
            var savedColor = GUI.color;

            try
            {
                float content = width - scrollbar;
                if (OverriddenViewWidth != null) OverriddenViewWidth.SetValue(null, content);
                EditorGUIUtility.labelWidth = Mathf.Max(80f, content * 0.4f);
                EditorGUIUtility.wideMode = content > 330f;

                // Высота задаётся явно: в отдельной области IMGUI прокрутка без
                // неё сжимается до пары строк и прячет всё, что ниже шапки.
                float height = Mathf.Max(40f, _columns[slot].contentRect.height - EditorGUIUtility.singleLineHeight - 4f);
                var scroll = GUILayout.BeginScrollView(_inspectorScroll, false, false,
                    GUIStyle.none, GUI.skin.verticalScrollbar, GUIStyle.none, GUILayout.Height(height));

                // Прокрутка общая: обе колонки показывают одно и то же место
                // инспектора, иначе сравнивать нечего.
                if (scroll != _inspectorScroll)
                {
                    _inspectorScroll = scroll;
                    _columns[1 - slot].MarkDirtyRepaint();
                }

                // Версию из git не блокируем: в заблокированном инспекторе не
                // раскрываются разделы. Она затемнена, а любая правка значения
                // тут же откатывается к снимку — версия остаётся как в git.
                if (!side.Live) GUI.color = new Color(savedColor.r, savedColor.g, savedColor.b, savedColor.a * 0.6f);

                EditorGUI.BeginChangeCheck();

                editor.DrawHeader();
                editor.OnInspectorGUI();

                if (EditorGUI.EndChangeCheck())
                {
                    if (side.Live)
                    {
                        ScheduleDescribe();
                    }
                    else if (_snapshots[slot] != null)
                    {
                        EditorJsonUtility.FromJsonOverwrite(_snapshots[slot], side.Main);
                        _columns[slot].MarkDirtyRepaint();
                    }

                    _view.MarkDirtyRepaint();
                }

                GUI.color = savedColor;
                GUILayout.EndScrollView();
            }
            finally
            {
                GUI.color = savedColor;
                if (OverriddenViewWidth != null) OverriddenViewWidth.SetValue(null, savedViewWidth);
                EditorGUIUtility.labelWidth = savedLabelWidth;
                EditorGUIUtility.wideMode = savedWideMode;
            }
        }

        private static readonly System.Reflection.PropertyInfo FirstInspectedEditor =
            typeof(Editor).GetProperty("firstInspectedEditor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private Editor EditorFor(int slot, UnityEngine.Object target, bool live)
        {
            if (_editors[slot] != null && _editors[slot].target == target) return _editors[slot];

            if (_editors[slot] != null) UnityEngine.Object.DestroyImmediate(_editors[slot]);
            _editors[slot] = null;
            _snapshots[slot] = null;

            if (target == null) return null;

            // Инспектор материала рисует содержимое только у «развёрнутого»
            // объекта. У временного объекта из git этого состояния нет вовсе —
            // без явной отметки видна одна шапка.
            UnityEditorInternal.InternalEditorUtility.SetIsInspectorExpanded(target, true);

            var editor = Editor.CreateEditor(target);
            if (editor != null && FirstInspectedEditor != null && FirstInspectedEditor.CanWrite)
            {
                try { FirstInspectedEditor.SetValue(editor, true); } catch { }
            }

            if (!live)
            {
                try { _snapshots[slot] = EditorJsonUtility.ToJson(target); } catch { }
            }

            _editors[slot] = editor;
            return editor;
        }
    }
}
