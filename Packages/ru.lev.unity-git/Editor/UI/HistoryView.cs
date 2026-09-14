using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git.UI
{
    /// <summary>
    /// История одним окном: ветки слева, журнал с графом справа, разбор
    /// выбранного коммита снизу — файлы деревом, превью и diff в том же виде,
    /// что во вкладке «Изменения».
    ///
    /// Журнал виртуализован: в проекте с многолетней историей строк десятки
    /// тысяч, и собирать элементы на каждую — верный способ подвесить редактор
    /// на открытии вкладки.
    /// </summary>
    public sealed class HistoryView : VisualElement
    {
        private const int PageSize = 300;

        private sealed class CommitWidgets
        {
            public CommitGraph Graph;
            public VisualElement Refs;
            public Label Subject, Author, Date;

            /// <summary>Коммит, привязанный к строке сейчас. Нужен контекстному
            /// меню: ListView переиспользует строки, и брать выделение вместо
            /// содержимого строки — значит показать меню не того коммита.</summary>
            public GitCommit Commit;
        }

        private sealed class FileRow
        {
            public PathNode<GitCommitFile> Folder;   // не null у папки
            public GitCommitFile File;               // не null у файла
            public int Depth;
        }

        private sealed class FileWidgets
        {
            public VisualElement FolderBox, FileBox;
            public Label FolderArrow, FolderName, FolderCount;
            public Image FolderIcon, Icon, IconBefore;
            public StatusBadge Badge;
            public Label Name, Dir, Meta;
            public FileRow Bound;
        }

        private readonly IGitHost _host;

        private readonly GitLogFilter _filter = new GitLogFilter();
        private readonly List<GitCommit> _commits = new List<GitCommit>();
        private readonly List<GitCommitFile> _files = new List<GitCommitFile>();
        private readonly List<FileRow> _fileRows = new List<FileRow>();

        /// <summary>Файлы, которые реально стоят в списке: с учётом скрытых мет.</summary>
        private readonly List<GitCommitFile> _visible = new List<GitCommitFile>();

        /// <summary>Ассеты, чья мета изменилась вместе с ними и свёрнута в их строку.</summary>
        private readonly HashSet<GitCommitFile> _withMeta = new HashSet<GitCommitFile>();

        /// <summary>Свёрнутые папки файлов коммита. Ключ — путь: переживает смену коммита.</summary>
        private readonly HashSet<string> _collapsedFolders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly BranchesPane _branches;
        private ListView _list, _fileList;
        private DiffView _diff;
        private AssetPreviewPane _preview;

        private TextField _search;
        private Button _authorButton, _periodButton, _pathChip, _more, _treeToggle;
        private VisualElement _actionBar, _graphNote, _detailRefs;
        private Label _summary, _message, _detailTitle, _detailMeta, _filesLabel;

        private List<string> _authors = new List<string>();
        private GitCommit _selected;
        private int _lanes = 1;
        private bool _loading, _reloadQueued;
        private bool _tree = true;

        private const string ShowMetaPref = "LevGit.History.ShowMeta";

        /// <summary>
        /// Меты отдельными строками. По умолчанию выключено: в Unity-коммите их
        /// ровно столько же, сколько ассетов, и список вырастает вдвое, ничего
        /// не добавляя, — так же, как во вкладке «Изменения», где мета свёрнута
        /// в строку ассета.
        /// </summary>
        private bool _showMeta = EditorPrefs.GetBool(ShowMetaPref, false);

        private TriCheck _metaCheck;

        private string _pendingSelectSha;

        /// <summary>
        /// Последний показанный файл. При переходе к соседнему коммиту остаёмся
        /// на нём, если он там тоже менялся, — так историю одного ассета удобно
        /// листать стрелками по журналу.
        /// </summary>
        private string _lastFilePath;

        /// <summary>HEAD на момент последней загрузки: по нему видно, что
        /// журнал устарел — коммит, checkout или pull его меняют.</summary>
        private string _loadedHead;

        /// <summary>Коммиты, которых нет ни на одном remote: у них можно менять сообщение.</summary>
        private HashSet<string> _localShas = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>У последней загрузки есть продолжение.</summary>
        private bool _hasMore;

        /// <summary>Сколько страниц дочитано в поисках запрошенного коммита.</summary>
        private int _autoPages;
        private const int MaxAutoPages = 10;

        public HistoryView(IGitHost host)
        {
            _host = host;
            AddToClassList("history");
            style.flexGrow = 1f;
            style.minHeight = 0f;

            // Ветки и журнал рядом, без переключения разделов: выбор ветки
            // сразу отбирает журнал справа.
            var split = new TwoPaneSplitView(0, 200f, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1f;
            split.style.minHeight = 0f;
            Add(split);

            _branches = new BranchesPane(host, OnBranchesChanged);
            _branches.style.minWidth = 130f;
            _branches.RefSelected += OnRefSelected;
            split.Add(_branches);

            var main = Ui.Box("pane");
            main.style.minWidth = 300f;

            main.Add(BuildFilterBar());

            _graphNote = Ui.Banner(
                L.T("The filter hides some commits, so the branch graph is not drawn: " +
                    "its lines would lead past the skipped commits."), "info");
            _graphNote.style.display = DisplayStyle.None;
            _graphNote.style.flexShrink = 0f;
            main.Add(_graphNote);

            // Закреплён НИЖНИЙ раздел: при растягивании окна растёт журнал,
            // ради которого сюда и приходят, а разбор коммита сохраняет высоту.
            var vsplit = new TwoPaneSplitView(1, 340f, TwoPaneSplitViewOrientation.Vertical);
            vsplit.style.flexGrow = 1f;
            vsplit.style.minHeight = 0f;
            vsplit.Add(BuildList());
            vsplit.Add(BuildDetail());
            main.Add(vsplit);

            split.Add(main);

            RegisterCallback<AttachToPanelEvent>(_ => GitIntegrations.BadgesChanged += OnBadgesChanged);
            RegisterCallback<DetachFromPanelEvent>(_ => GitIntegrations.BadgesChanged -= OnBadgesChanged);
        }

        private void OnBadgesChanged()
        {
            _list.RefreshItems();
            FillRefs(_detailRefs, _selected);
        }

        // ------------------------------------------------------------ отбор ---

        private VisualElement BuildFilterBar()
        {
            // flex-wrap, а не фиксированный ряд: на узкой панели поля переходят
            // на вторую строку, и ни один элемент не исчезает за краем.
            var bar = Ui.Box("hfilter");

            _search = new TextField { value = string.Empty };
            _search.AddToClassList("hfilter__search");
            _search.tooltip = L.T("Search commit messages. Enter to apply");
            _search.textEdition.placeholder = L.T("Search messages…");
            _search.textEdition.hidePlaceholderOnFocus = true;
            _search.style.minWidth = 0f;
            _search.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                _filter.Text = string.IsNullOrWhiteSpace(_search.value) ? null : _search.value.Trim();
                e.StopPropagation();
                Reload();
            });
            bar.Add(_search);

            _authorButton = Ui.Action(L.T("Author: Any"), null, L.T("Show commits by one author"));
            _authorButton.clicked += () => ShowAuthorMenu(_authorButton.worldBound);
            bar.Add(_authorButton);

            _periodButton = Ui.Action(L.T("All Time"), null, L.T("Limit the period"));
            _periodButton.clicked += () => ShowPeriodMenu(_periodButton.worldBound);
            bar.Add(_periodButton);

            _pathChip = Ui.Action(string.Empty, () => FilterByPath(null), L.T("Clear the file filter"));
            _pathChip.AddToClassList("chip--clear");
            _pathChip.style.display = DisplayStyle.None;
            bar.Add(_pathChip);

            bar.Add(Ui.Spacer());

            _summary = Ui.Text(string.Empty, "hfilter__summary");
            bar.Add(_summary);

            bar.Add(Ui.Action(L.T("Refresh"), ReloadAll, L.T("Reload the log and branches")));
            return bar;
        }

        private void ShowAuthorMenu(Rect anchor)
        {
            // Данные подготовлены заранее: GenericMenu строится синхронно, и
            // await внутри обработчика обнулил бы Event.current.
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent(L.T("Any")), _filter.Author == null, () =>
            {
                _filter.Author = null;
                _authorButton.text = L.T("Author: Any");
                Reload();
            });

            if (_authors.Count > 0) menu.AddSeparator(string.Empty);

            foreach (var a in _authors)
            {
                var author = a;
                menu.AddItem(new GUIContent(author), _filter.Author == author, () =>
                {
                    _filter.Author = author;
                    _authorButton.text = L.F("Author: {0}", author);
                    Reload();
                });
            }

            menu.DropDown(anchor);
        }

        private void ShowPeriodMenu(Rect anchor)
        {
            var menu = new GenericMenu();

            Action<string, string> add = (title, since) =>
            {
                menu.AddItem(new GUIContent(title), _filter.Since == since, () =>
                {
                    _filter.Since = since;
                    _periodButton.text = title;
                    Reload();
                });
            };

            add(L.T("All Time"), null);
            menu.AddSeparator(string.Empty);
            add(L.T("Past Day"), "1 day ago");
            add(L.T("Past Week"), "1 week ago");
            add(L.T("Past Month"), "1 month ago");
            add(L.T("Past Year"), "1 year ago");

            menu.DropDown(anchor);
        }

        /// <summary>
        /// История одного файла или папки — вызывается из других панелей.
        /// null снимает отбор.
        /// </summary>
        public void FilterByPath(string projectPath, string selectSha = null)
        {
            // Пустая строка — не коммит: иначе поиск «не находит» его и показывает ошибку.
            if (!string.IsNullOrEmpty(selectSha))
            {
                // Коммит пришёл из истории сцены, а та читается по текущей ветке.
                // Отбор по другой ветке мог бы его спрятать — снимаем.
                _filter.Ref = null;
                _branches.Select(null, false);
                _pendingSelectSha = selectSha;
                _autoPages = 0;
            }

            _filter.ProjectPath = string.IsNullOrEmpty(projectPath) ? null : projectPath;

            // Без выбранной ветки историю файла смотрим вдоль текущей: с --all
            // в неё попали бы правки из всех веток вперемешку.
            _filter.AllRefs = _filter.ProjectPath == null;
            Reload();
        }

        private void OnRefSelected(string refName)
        {
            _filter.Ref = refName;
            Reload();
        }

        private void OnBranchesChanged()
        {
            // Ветку создали, удалили или переключились — ярлыки в журнале
            // устарели, даже если сами коммиты те же.
            _loadedHead = null;
            Reload();
        }

        // ------------------------------------------------------------ журнал ---

        private VisualElement BuildList()
        {
            var box = Ui.Box("history__list");
            box.style.minHeight = 0f;

            _list = new ListView(_commits, 22, MakeCommitRow, BindCommitRow)
            {
                selectionType = SelectionType.Single,
                showBorder = false,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight
            };
            _list.style.flexGrow = 1f;
            _list.style.minHeight = 0f;
            _list.selectionChanged += OnCommitSelectionChanged;
            box.Add(_list);

            _more = Ui.Action(L.F("Show {0} More", PageSize), LoadMore);
            _more.style.display = DisplayStyle.None;
            _more.style.flexShrink = 0f;
            box.Add(_more);

            return box;
        }

        private VisualElement MakeCommitRow()
        {
            var row = Ui.Box("crow");

            var w = new CommitWidgets
            {
                Graph = new CommitGraph(),
                Refs = Ui.Box("crow__refs"),
                Subject = Ui.Text(string.Empty, "crow__subject"),
                Author = Ui.Text(string.Empty, "crow__author"),
                Date = Ui.Text(string.Empty, "crow__date")
            };

            row.Add(w.Graph);
            row.Add(w.Refs);
            row.Add(w.Subject);
            row.Add(Ui.Spacer());
            row.Add(w.Author);
            row.Add(w.Date);

            row.userData = w;
            row.AddManipulator(new ContextualMenuManipulator(e =>
            {
                if (w.Commit != null) FillCommitMenu(e.menu, w.Commit);
            }));

            return row;
        }

        private void BindCommitRow(VisualElement element, int index)
        {
            var w = (CommitWidgets)element.userData;
            var c = _commits[index];
            w.Commit = c;

            bool graph = !_filter.DropsCommits;
            w.Graph.style.display = graph ? DisplayStyle.Flex : DisplayStyle.None;
            if (graph) w.Graph.Bind(c, _lanes, false);

            FillRefs(w.Refs, c);

            w.Subject.text = c.Subject;
            w.Author.text = c.Author;
            w.Date.text = Ago(c.Date);

            element.EnableInClassList("crow--merge", c.IsMerge);
        }

        private static void FillRefs(VisualElement box, GitCommit c)
        {
            box.Clear();

            if (c != null)
            {
                HashSet<string> badged = null;
                foreach (var r in c.Refs)
                {
                    var chip = Ui.Text(r.Name, "refchip", "refchip--" + KindClass(r.Kind));
                    if (r.IsCurrent) chip.AddToClassList("refchip--current");
                    box.Add(chip);

                    // Значок MR — один на ветку, даже если у неё здесь и локальная, и удалённая метка.
                    if (r.Kind != GitRefKind.LocalBranch && r.Kind != GitRefKind.RemoteBranch) continue;
                    var server = r.Kind == GitRefKind.RemoteBranch ? GitHistory.StripRemote(r.Name) : r.Name;
                    if (badged == null) badged = new HashSet<string>();
                    if (!badged.Add(server)) continue;

                    var badge = GitIntegrations.BranchBadge(server);
                    if (badge == null) continue;
                    var mr = Ui.Text(badge.Text, "refchip", "refchip--mr");
                    mr.tooltip = badge.Tooltip;
                    if (badge.Open != null)
                    {
                        var open = badge.Open;
                        mr.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); open(); });
                    }
                    box.Add(mr);
                }
            }

            box.style.display = c != null && c.Refs.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static string KindClass(GitRefKind kind)
        {
            switch (kind)
            {
                case GitRefKind.Tag: return "tag";
                case GitRefKind.RemoteBranch: return "remote";
                case GitRefKind.Head: return "head";
                default: return "local";
            }
        }

        /// <summary>Человеческая давность: точная дата в списке только мешает сравнивать.</summary>
        private static string Ago(DateTime when)
        {
            if (when == DateTime.MinValue) return string.Empty;

            var span = DateTime.Now - when;
            if (span.TotalMinutes < 1) return L.T("just now");
            if (span.TotalHours < 1) return L.F("{0} min ago", (int)span.TotalMinutes);
            if (span.TotalDays < 1) return L.F("{0} h ago", (int)span.TotalHours);
            if (span.TotalDays < 7) return L.F("{0} d ago", (int)span.TotalDays);
            if (span.TotalDays < 365) return when.ToString("d MMM", L.Culture);
            return when.ToString("MM.yyyy", L.Culture);
        }

        // ------------------------------------------------------- загрузка ---

        public void Refresh()
        {
            _branches.Refresh();

            if (_loading) return;

            // Перечитываем не по такту, а когда журнал действительно устарел:
            // коммит в соседней вкладке, checkout, pull. На обычном обновлении
            // статуса раз в пару секунд гонять git log незачем.
            if (_commits.Count == 0 || _loadedHead != GitStatusCache.HeadOid) Reload();
        }

        private void ReloadAll()
        {
            _branches.Reload();
            Reload();
        }

        private async void Reload()
        {
            // Просьба во время загрузки не теряется: выбор ветки или автора,
            // сделанный, пока журнал ещё читается, должен в итоге примениться.
            if (_loading) { _reloadQueued = true; return; }

            _filter.Skip = 0;
            _filter.Limit = PageSize;

            await LoadAsync(true);

            if (_authors.Count == 0) _authors = await GitHistory.AuthorsAsync(1000);
        }

        private async void LoadMore()
        {
            if (_loading) return;
            _filter.Skip = _commits.Count;
            await LoadAsync(false);
        }

        private async System.Threading.Tasks.Task LoadAsync(bool replace)
        {
            _loading = true;
            _more.SetEnabled(false);
            _summary.text = L.T("Reading the log…");

            try
            {
                var page = await GitHistory.LogAsync(_filter);

                if (page.Error != null)
                {
                    _summary.text = string.Empty;
                    _commits.Clear();
                    _list.Rebuild();
                    ShowCommit(null);
                    _host.SetStatus(page.Error, true);
                    return;
                }

                if (replace) _commits.Clear();
                _commits.AddRange(page.Commits);

                // Раскладка считается по ВСЕМУ накопленному списку, а не по
                // странице: колонки продолжаются через границу страниц, и
                // пересчёт только хвоста разорвал бы линии ровно на стыке.
                _lanes = page.HasGraph ? GitGraphBuilder.Layout(_commits) : 1;

                _graphNote.style.display = page.HasGraph ? DisplayStyle.None : DisplayStyle.Flex;
                _more.style.display = page.More ? DisplayStyle.Flex : DisplayStyle.None;
                _hasMore = page.More;

                // Какие коммиты ещё только здесь — для пункта «Изменить сообщение…».
                if (replace) _localShas = await GitHistory.LocalCommitsAsync();

                _loadedHead = GitStatusCache.HeadOid;
                _summary.text = L.F("Commits: {0}", _commits.Count + (page.More ? "+" : string.Empty));

                _pathChip.style.display = _filter.ProjectPath == null ? DisplayStyle.None : DisplayStyle.Flex;
                if (_filter.ProjectPath != null)
                    _pathChip.text = L.F("File: {0}", Ui.NameOf(_filter.ProjectPath)) + "  ×";

                _list.Rebuild();
                RestoreSelection();
            }
            finally
            {
                _loading = false;
                _more.SetEnabled(true);
            }

            if (_reloadQueued)
            {
                _reloadQueued = false;
                Reload();
            }
        }

        /// <summary>
        /// Возвращает выделение на тот же коммит после перечитывания.
        /// Индекс для этого не годится: с другим отбором он указывает на чужую
        /// строку, и разбор молча показал бы не тот коммит.
        /// </summary>
        private void RestoreSelection()
        {
            // Если за этой загрузкой уже стоит следующая — с новым отбором, —
            // просьбу выделить коммит оставляем ей: здесь его может не быть.
            var pending = _reloadQueued ? null : _pendingSelectSha;
            if (!_reloadQueued) _pendingSelectSha = null;

            var sha = pending ?? (_selected != null ? _selected.Sha : null);

            int index = -1;
            if (sha != null)
                for (int i = 0; i < _commits.Count; i++)
                    if (_commits[i].Sha == sha) { index = i; break; }

            if (pending != null && index < 0)
            {
                // Коммит глубже загруженного — дочитываем сами, а не отправляем искать кнопку.
                if (_hasMore && _autoPages < MaxAutoPages)
                {
                    _autoPages++;
                    _pendingSelectSha = pending;
                    _summary.text = L.F("Looking for commit {0}…", GitHistory.Short(pending));
                    schedule.Execute(LoadMore);
                    return;
                }

                // В истории этого файла коммита нет — открываем общий журнал на нём.
                if (_filter.ProjectPath != null)
                {
                    _host.SetStatus(L.F("Commit {0} was not found in the history of “{1}”: shown in the full log.",
                                        GitHistory.Short(pending), Ui.NameOf(_filter.ProjectPath)), false);
                    FilterByPath(null, pending);
                    return;
                }

                _host.SetStatus(L.F("Commit {0} is not in the log: its branch may have been deleted " +
                                    "or it has not been fetched from the server yet. Do a Fetch.", GitHistory.Short(pending)), true);
            }

            if (index < 0 && _commits.Count > 0) index = 0;

            if (index < 0) { ShowCommit(null); return; }

            _list.SetSelectionWithoutNotify(new[] { index });
            _list.ScrollToItem(index);
            ShowCommit(_commits[index]);
        }

        private void OnCommitSelectionChanged(IEnumerable<object> selection)
        {
            GitCommit picked = null;
            foreach (var o in selection) { picked = o as GitCommit; break; }
            ShowCommit(picked);
        }

        // ------------------------------------------------------------ разбор ---

        private VisualElement BuildDetail()
        {
            var box = Ui.Box("history__detail");
            box.style.minHeight = 0f;

            var head = Ui.Box("cdetail");

            _detailTitle = Ui.Text(string.Empty, "cdetail__title");
            head.Add(_detailTitle);

            _detailRefs = Ui.Box("crow__refs");
            head.Add(_detailRefs);

            _detailMeta = Ui.Text(string.Empty, "cdetail__meta");
            head.Add(_detailMeta);

            _message = Ui.Text(string.Empty, "cdetail__message");
            _message.style.display = DisplayStyle.None;
            head.Add(_message);

            _actionBar = Ui.Box("cdetail__actions");
            head.Add(_actionBar);

            box.Add(head);

            // Файлы коммита слева, превью и diff справа — та же раскладка, что
            // во вкладке «Изменения», чтобы глаз не переучивался между ними.
            var inner = new TwoPaneSplitView(0, 260f, TwoPaneSplitViewOrientation.Horizontal);
            inner.style.flexGrow = 1f;
            inner.style.minHeight = 0f;

            var filesPane = Ui.Box("pane");
            filesPane.style.minWidth = 150f;

            var bar = Ui.Box("subbar");
            _filesLabel = Ui.Text(string.Empty, "subbar__label");
            bar.Add(_filesLabel);
            bar.Add(Ui.Spacer());

            var metaBox = Ui.Box("subbar__toggle");
            metaBox.tooltip = L.T(
                "Show .meta files as separate rows.\n" +
                "When off, a meta changed together with its asset is marked on the asset " +
                "with “+meta”. A meta changed without its asset (for example, import settings) " +
                "is always shown: otherwise such a commit would look empty.");

            _metaCheck = new TriCheck();
            _metaCheck.Set(_showMeta ? CheckState.On : CheckState.Off);
            _metaCheck.Clicked += on => SetShowMeta(on);
            metaBox.Add(_metaCheck);

            // Подпись тоже переключает: попадать в квадратик флажка мышью неудобно.
            var metaLabel = Ui.Text(".meta", "subbar__check");
            metaLabel.RegisterCallback<ClickEvent>(_ => SetShowMeta(!_showMeta));
            metaBox.Add(metaLabel);


            bar.Add(metaBox);

            _treeToggle = Ui.Action(_tree ? L.T("Tree") : L.T("List"), null, L.T("Show folders as a tree or a flat list"));
            _treeToggle.clicked += () =>
            {
                var keep = CurrentFile();
                _tree = !_tree;
                _treeToggle.text = _tree ? L.T("Tree") : L.T("List");
                RebuildFileRows();
                SelectFileRow(keep);
            };
            bar.Add(_treeToggle);
            filesPane.Add(bar);

            _fileList = new ListView();
            _fileList.fixedItemHeight = 22f;
            _fileList.selectionType = SelectionType.Single;
            _fileList.makeItem = MakeFileRow;
            _fileList.bindItem = BindFileRow;
            _fileList.itemsSource = _fileRows;
            _fileList.AddToClassList("list");
            Lev.Git.Preview.PreviewThumbnails.Watch(_fileList);
            _fileList.selectionChanged += OnFileSelectionChanged;
            filesPane.Add(_fileList);

            var view = Ui.Box("pane", "pane--detail");
            view.style.minWidth = 200f;

            _preview = new AssetPreviewPane();
            _preview.style.display = DisplayStyle.None;
            view.Add(_preview);

            _diff = new DiffView(_host);
            view.Add(_diff);
            _preview.AttachDiff(_diff);

            inner.Add(filesPane);
            inner.Add(view);
            box.Add(inner);

            return box;
        }

        private async void ShowCommit(GitCommit c)
        {
            // Тот же коммит после перечитывания журнала — новый объект с тем же
            // sha. Файлы и выбранный в них файл остаются: сбрасывать разбор
            // из-за того, что сменили отбор по ветке, незачем.
            if (c != null && _selected != null && c.Sha == _selected.Sha && _files.Count > 0)
            {
                _selected = c;
                FillRefs(_detailRefs, c);
                BuildActions(c);
                return;
            }

            _selected = c;

            _files.Clear();
            RebuildFileRows();
            _filesLabel.text = string.Empty;
            _preview.ShowRevision(null, null);

            BuildActions(c);
            FillRefs(_detailRefs, c);

            if (c == null)
            {
                _detailTitle.text = L.T("No Commit Selected");
                _detailMeta.text = string.Empty;
                _message.style.display = DisplayStyle.None;
                _diff.ShowMessage(L.T("No Commit Selected"), L.T("Select a commit in the log above."));
                return;
            }

            _detailTitle.text = c.Subject;
            _detailMeta.text = string.Format("{0} · {1} · {2}",
                c.ShortSha, c.Author, c.Date.ToString("d MMMM yyyy, HH:mm", L.Culture));
            _message.style.display = DisplayStyle.None;
            _diff.ShowMessage(L.T("Reading the commit…"), null);

            var sha = c.Sha;

            // Сообщение и файлы — независимые запросы: запускаем оба сразу,
            // чтобы разбор не ждал их по очереди.
            var messageTask = GitHistory.MessageAsync(sha);
            var filesTask = GitHistory.FilesAsync(sha);

            var message = await messageTask;
            if (_selected == null || _selected.Sha != sha) return;

            // В заголовке уже показан первый абзац — в теле оставляем остальное.
            var body = BodyOf(message, c.Subject);
            _message.text = body;
            _message.style.display = string.IsNullOrEmpty(body) ? DisplayStyle.None : DisplayStyle.Flex;

            var files = await filesTask;
            if (_selected == null || _selected.Sha != sha) return;

            ShowFiles(files);
        }

        private static string BodyOf(string message, string subject)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;

            var text = message.Replace("\r\n", "\n");
            if (text.StartsWith(subject, StringComparison.Ordinal))
                text = text.Substring(subject.Length);

            return text.Trim('\n', ' ');
        }

        // ------------------------------------------------------- файлы коммита ---

        private void ShowFiles(List<GitCommitFile> files)
        {
            _files.Clear();
            _files.AddRange(files);
            RebuildFileRows();

            if (_files.Count == 0)
            {

                bool merge = _selected != null && _selected.IsMerge;
                _preview.ShowRevision(null, null);
                _diff.ShowMessage(
                    merge ? L.T("Merge without changes of its own") : L.T("The commit changed no files"),
                    merge ? L.T("Relative to the first parent, this merge changes nothing.") : null);
                return;
            }

            // Какой файл показать первым: тот, по которому отобран журнал, —
            // ради него человек сюда и пришёл; иначе тот, что смотрели в
            // предыдущем коммите; иначе первый в списке.
            var target = FindFilterTarget() ?? FindByPath(_lastFilePath) ?? FirstFile();

            SelectFileRow(target);
            ShowFile(target);
        }

        private GitCommitFile FindFilterTarget()
        {
            var p = _filter.ProjectPath;
            if (string.IsNullOrEmpty(p)) return null;

            var exact = FindByPath(p);
            if (exact != null) return exact;

            // Отбор по папке: первый по алфавиту файл внутри неё.
            var prefix = p.TrimEnd('/') + "/";
            GitCommitFile best = null;
            foreach (var f in _visible)
            {
                if (!PathOf(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || ComparePaths(f, best) < 0) best = f;
            }
            return best;
        }

        private GitCommitFile FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            foreach (var f in _visible)
            {
                if (string.Equals(PathOf(f), path, StringComparison.OrdinalIgnoreCase)) return f;

                // Файл могли переименовать в этом самом коммите — узнаём его и
                // по прежнему имени, иначе история файла теряла бы его на стыке.
                if (string.Equals(f.OriginalPath, path, StringComparison.OrdinalIgnoreCase)) return f;
            }

            return null;
        }

        private GitCommitFile FirstFile()
        {
            foreach (var r in _fileRows) if (r.File != null) return r.File;
            return _visible.Count > 0 ? _visible[0] : null;
        }

        private void ShowFile(GitCommitFile file)
        {
            if (_selected == null || file == null) return;

            _lastFilePath = PathOf(file);
            _preview.ShowRevision(_selected, file);
            _diff.ShowRevision(_selected, file);
        }

        /// <summary>Сцена или префаб — файлы, у которых есть история по объектам.</summary>
        private static bool IsObjectFile(GitCommitFile f)
        {
            if (f == null || f.ProjectPath == null) return false;

            return f.ProjectPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
                   f.ProjectPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        }


        private void OnFileSelectionChanged(IEnumerable<object> selection)
        {
            foreach (var o in selection)
            {
                var row = o as FileRow;
                // Папка — не файл: разбор остаётся на прежнем месте.
                if (row != null && row.File != null) ShowFile(row.File);
                return;
            }
        }

        private static string PathOf(GitCommitFile f)
        {
            return f.ProjectPath ?? f.GitPath;
        }

        private static int ComparePaths(GitCommitFile a, GitCommitFile b)
        {
            return string.Compare(PathOf(a), PathOf(b), StringComparison.OrdinalIgnoreCase);
        }

        private void RebuildFileRows()
        {
            _fileRows.Clear();
            ComputeVisible();

            int hidden = _files.Count - _visible.Count;
            _filesLabel.text = _files.Count == 0 ? string.Empty
                : hidden > 0 ? L.F("Files: {0}  (+{1} .meta)", _visible.Count, hidden)
                : L.F("Files: {0}", _files.Count);

            if (_tree)
            {
                AddFileNode(PathTree.Build(_visible, PathOf), 0);
            }
            else
            {
                var sorted = new List<GitCommitFile>(_visible);
                sorted.Sort(ComparePaths);
                foreach (var f in sorted) _fileRows.Add(new FileRow { File = f });
            }

            _fileList.itemsSource = _fileRows;
            _fileList.Rebuild();
        }

        private void AddFileNode(PathNode<GitCommitFile> node, int depth)
        {
            foreach (var f in node.Folders)
            {
                _fileRows.Add(new FileRow { Folder = f, Depth = depth });
                if (!_collapsedFolders.Contains(f.Path)) AddFileNode(f, depth + 1);
            }

            var files = new List<GitCommitFile>(node.Files);
            files.Sort(ComparePaths);
            foreach (var f in files) _fileRows.Add(new FileRow { File = f, Depth = depth });
        }

        /// <summary>
        /// Какие файлы стоят в списке. Прячется только мета, у которой в этом же
        /// коммите есть пара: ассет или содержимое папки. Мета без пары — это
        /// самостоятельное изменение (настройки импорта, смена GUID), и спрятать
        /// её значило бы спрятать сам коммит.
        /// </summary>
        private void ComputeVisible()
        {
            _visible.Clear();
            _withMeta.Clear();

            if (_showMeta) { _visible.AddRange(_files); return; }

            var byPath = new Dictionary<string, GitCommitFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in _files) byPath[PathOf(f)] = f;

            foreach (var f in _files)
            {
                var path = PathOf(f);

                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    var owner = path.Substring(0, path.Length - ".meta".Length);

                    GitCommitFile asset;
                    if (byPath.TryGetValue(owner, out asset)) { _withMeta.Add(asset); continue; }

                    // Мета папки: сама папка в git не хранится, её «пара» — файлы внутри.
                    if (HasFileUnder(owner)) continue;
                }

                _visible.Add(f);
            }
        }

        private bool HasFileUnder(string folder)
        {
            var prefix = folder + "/";
            foreach (var f in _files)
                if (PathOf(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void SetShowMeta(bool show)
        {
            if (_showMeta == show) return;

            _showMeta = show;
            _metaCheck.Set(show ? CheckState.On : CheckState.Off);
            EditorPrefs.SetBool(ShowMetaPref, show);

            var keep = CurrentFile();
            RebuildFileRows();

            if (keep == null || _visible.Contains(keep))
            {
                SelectFileRow(keep);
                return;
            }

            // Выбранная мета спряталась — переходим на её ассет, а если это была
            // мета папки, то на первый файл списка.
            var path = PathOf(keep);
            var next = FindByPath(path.Substring(0, path.Length - ".meta".Length)) ?? FirstFile();
            SelectFileRow(next);
            ShowFile(next);
        }

        private GitCommitFile CurrentFile()
        {
            foreach (var o in _fileList.selectedItems)
            {
                var row = o as FileRow;
                if (row != null && row.File != null) return row.File;
            }
            return null;
        }

        /// <summary>Выделяет файл в списке, раскрыв свёрнутые папки на пути к нему.</summary>
        private void SelectFileRow(GitCommitFile file)
        {
            if (file == null) return;

            var path = PathOf(file);
            bool expanded = false;

            foreach (var key in new List<string>(_collapsedFolders))
            {
                if (!path.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase)) continue;
                _collapsedFolders.Remove(key);
                expanded = true;
            }

            if (expanded) RebuildFileRows();

            for (int i = 0; i < _fileRows.Count; i++)
            {
                if (_fileRows[i].File != file) continue;
                _fileList.SetSelectionWithoutNotify(new[] { i });
                _fileList.ScrollToItem(i);
                return;
            }
        }

        private VisualElement MakeFileRow()
        {
            var wrap = new VisualElement();
            var w = new FileWidgets();

            // ---- папка ----
            w.FolderBox = Ui.Box("row", "row--folder");
            w.FolderArrow = Ui.Text(string.Empty, "group__arrow");
            w.FolderIcon = new Image();
            w.FolderIcon.AddToClassList("row__icon");
            w.FolderName = Ui.Text(string.Empty, "row__name");
            w.FolderCount = Ui.Text(string.Empty, "row__dir");
            w.FolderBox.Add(w.FolderArrow);
            w.FolderBox.Add(w.FolderIcon);
            w.FolderBox.Add(w.FolderName);
            w.FolderBox.Add(w.FolderCount);

            w.FolderBox.RegisterCallback<MouseDownEvent>(e =>
            {
                if (w.Bound == null || w.Bound.Folder == null) return;

                var key = w.Bound.Folder.Path;
                bool collapsing = _collapsedFolders.Add(key);
                if (!collapsing) _collapsedFolders.Remove(key);

                // Перестройка откладывается на кадр: сейчас мы внутри обработчика
                // на элементе, который эта перестройка и переиспользует.
                var keep = CurrentFile();
                _fileList.schedule.Execute(() =>
                {
                    RebuildFileRows();

                    // Свернули папку с выбранным файлом — снимаем выделение, а не
                    // раскрываем папку обратно: человек явно хотел её свернуть.
                    // Разбор при этом остаётся на экране.
                    if (keep != null && collapsing &&
                        PathOf(keep).StartsWith(key + "/", StringComparison.OrdinalIgnoreCase))
                        _fileList.SetSelectionWithoutNotify(new int[0]);
                    else
                        SelectFileRow(keep);
                });
                e.StopPropagation();
            });
            wrap.Add(w.FolderBox);

            // ---- файл ----
            w.FileBox = Ui.Box("row");
            w.Badge = new StatusBadge();
            w.Icon = new Image();
            w.Icon.AddToClassList("row__icon");
            w.IconBefore = new Image();
            w.IconBefore.AddToClassList("row__icon");
            w.IconBefore.AddToClassList("row__icon--before");
            w.Name = Ui.Text(string.Empty, "row__name");
            w.Dir = Ui.Text(string.Empty, "row__dir");
            w.FileBox.Add(w.Badge);
            w.FileBox.Add(w.IconBefore);
            w.FileBox.Add(w.Icon);
            w.FileBox.Add(w.Name);
            w.FileBox.Add(w.Dir);
            w.Meta = Ui.Text(string.Empty, "row__meta");
            w.FileBox.Add(w.Meta);

            w.FileBox.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                var f = w.Bound != null ? w.Bound.File : null;
                if (f == null) return;

                if (f.ProjectPath != null)
                {
                    evt.menu.AppendAction(L.T("File History"), _ => FilterByPath(f.ProjectPath));
                    evt.menu.AppendAction(L.T("Asset History"), _ => AssetHistoryWindow.Open(f.ProjectPath, false));

                    if (IsObjectFile(f))
                        evt.menu.AppendAction(L.Tc("objects of a scene or prefab", "Object History"), _ =>
                            SceneHistoryWindow.ShowScene(f.ProjectPath, _selected != null ? _selected.Sha : null));
                    evt.menu.AppendAction(L.T("Show in Project"), _ =>
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(f.ProjectPath);
                        if (obj != null) EditorGUIUtility.PingObject(obj);
                        else _host.SetStatus(L.F("The file is no longer in the project: {0}", f.ProjectPath), true);
                    });
                    evt.menu.AppendSeparator();
                }

                evt.menu.AppendAction(L.T("Copy Path"), _ => EditorGUIUtility.systemCopyBuffer = PathOf(f));
            }));
            wrap.Add(w.FileBox);

            wrap.userData = w;
            return wrap;
        }

        private void BindFileRow(VisualElement element, int index)
        {
            var w = (FileWidgets)element.userData;
            var row = _fileRows[index];
            w.Bound = row;

            bool isFolder = row.Folder != null;
            w.FolderBox.style.display = isFolder ? DisplayStyle.Flex : DisplayStyle.None;
            w.FileBox.style.display = isFolder ? DisplayStyle.None : DisplayStyle.Flex;

            if (isFolder)
            {
                var f = row.Folder;
                bool collapsed = _collapsedFolders.Contains(f.Path);

                w.FolderArrow.text = collapsed ? "▸" : "▾";
                w.FolderIcon.image = EditorGUIUtility.IconContent(
                    collapsed ? "Folder Icon" : "FolderOpened Icon").image;
                w.FolderName.text = f.Name;

                var inside = new List<GitCommitFile>();
                f.Collect(inside);
                w.FolderCount.text = inside.Count.ToString();

                w.FolderBox.style.paddingLeft = 4f + row.Depth * 12f;
                return;
            }

            var file = row.File;
            var path = PathOf(file);

            w.Badge.Set(ToFileStatus(file.Status));

            Texture after = null, before = null;
            if (_selected != null && file.ProjectPath != null && Lev.Git.Preview.PreviewThumbnails.Enabled(file.ProjectPath))
            {
                if (file.Status != GitCommitFileStatus.Deleted)
                    after = Lev.Git.Preview.PreviewThumbnails.ForCommit(_selected.Sha, file.ProjectPath, file.GitPath);
                if (file.Status != GitCommitFileStatus.Added && _selected.Parents.Length > 0)
                    before = Lev.Git.Preview.PreviewThumbnails.ForCommit(_selected.Parents[0], file.ProjectPath, file.OriginalGitPath ?? file.GitPath);
            }

            w.Icon.image = after ?? (file.Status == GitCommitFileStatus.Deleted ? before : null) ?? Ui.AssetIcon(path);
            bool pair = after != null && before != null;
            w.IconBefore.image = pair ? before : null;
            w.IconBefore.style.display = pair ? DisplayStyle.Flex : DisplayStyle.None;
            w.Name.text = Ui.NameOf(path);

            // В дереве папка уже названа выше по списку — повторять путь в каждой
            // строке значит писать одно и то же по двадцать раз подряд.
            w.Dir.text = _tree ? string.Empty : Ui.DirOf(path);
            w.Meta.text = _withMeta.Contains(file) ? "+meta" : string.Empty;
            w.FileBox.style.paddingLeft = 4f + row.Depth * 12f;

            var from = file.OriginalPath ?? file.OriginalGitPath;
            w.FileBox.tooltip = from != null ? path + "\n" + L.F("renamed from {0}", from) : path;
        }

        private static GitFileStatus ToFileStatus(GitCommitFileStatus s)
        {
            switch (s)
            {
                case GitCommitFileStatus.Added: return GitFileStatus.Added;
                case GitCommitFileStatus.Deleted: return GitFileStatus.Deleted;
                case GitCommitFileStatus.Renamed:
                case GitCommitFileStatus.Copied: return GitFileStatus.Renamed;
                default: return GitFileStatus.Modified;
            }
        }

        // --------------------------------------------------------- действия ---

        private void BuildActions(GitCommit c)
        {
            _actionBar.Clear();
            if (c == null) return;

            // Кнопки самых частых действий видны всегда, остальное — в меню:
            // на узкой панели ряд из восьми кнопок обрезался бы, и до половины
            // из них было бы не добраться.
            _actionBar.Add(Ui.Action(L.T("Checkout"), () => Checkout(c),
                L.T("Switch the working tree to this commit")));

            _actionBar.Add(Ui.Action(L.T("Branch from Here…"), () => CreateBranch(c),
                L.T("Create a branch starting at this commit")));

            _actionBar.Add(Ui.Action(L.T("Revert…"), () => Revert(c),
                L.T("Create a commit that undoes this one")));

            var more = Ui.Action("⋯", null, L.T("More actions"));
            more.clicked += () =>
            {
                var menu = new GenericMenu();
                FillCommitMenu(menu, c);
                menu.DropDown(more.worldBound);
            };
            _actionBar.Add(more);

            _actionBar.Add(Ui.Spacer());

            _actionBar.Add(Ui.Action(L.T("Copy SHA"), () =>
            {
                EditorGUIUtility.systemCopyBuffer = c.Sha;
                _host.SetStatus(L.F("SHA copied: {0}", c.ShortSha), false);
            }));
        }

        private void FillCommitMenu(GenericMenu menu, GitCommit c)
        {
            menu.AddItem(new GUIContent(L.T("Checkout Commit")), false, () => Checkout(c));
            menu.AddItem(new GUIContent(L.T("Create Branch from Here…")), false, () => CreateBranch(c));
            menu.AddItem(new GUIContent(L.T("Create Tag…")), false, () => CreateTag(c));
            if (_localShas.Contains(c.Sha))
                menu.AddItem(new GUIContent(L.T("Edit Message…")), false, () => EditMessage(c));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.Tc("with git command", "Cherry-Pick to Current Branch")), false,
                         () => CherryPick(c));
            menu.AddItem(new GUIContent(L.Tc("with git command", "Revert")), false, () => Revert(c));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.T("Reset/Soft — Keep Changes in the Index")), false,
                         () => Reset(c, "soft"));
            menu.AddItem(new GUIContent(L.T("Reset/Mixed — Keep Changes in Files")), false,
                         () => Reset(c, "mixed"));
            menu.AddItem(new GUIContent(L.T("Reset/Hard — Erase Everything After the Commit")), false,
                         () => Reset(c, "hard"));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.T("Copy SHA")), false,
                         () => EditorGUIUtility.systemCopyBuffer = c.Sha);
            GitIntegrations.AddOpenLinks((title, act) => menu.AddItem(new GUIContent(title), false, () => act()),
                                         i => i.CommitUrl(c.Sha));
        }

        private void FillCommitMenu(DropdownMenu menu, GitCommit c)
        {
            menu.AppendAction(L.T("Checkout Commit"), _ => Checkout(c));
            menu.AppendAction(L.T("Create Branch from Here…"), _ => CreateBranch(c));
            menu.AppendAction(L.T("Create Tag…"), _ => CreateTag(c));
            if (_localShas.Contains(c.Sha))
                menu.AppendAction(L.T("Edit Message…"), _ => EditMessage(c));
            menu.AppendSeparator();
            menu.AppendAction(L.T("Cherry-Pick to Current Branch"), _ => CherryPick(c));
            menu.AppendAction(L.T("Revert"), _ => Revert(c));
            menu.AppendSeparator();
            menu.AppendAction(L.T("Copy SHA"), _ => EditorGUIUtility.systemCopyBuffer = c.Sha);
            GitIntegrations.AddOpenLinks((title, act) => menu.AppendAction(title, _ => act()), i => i.CommitUrl(c.Sha));
        }

        private void Checkout(GitCommit c)
        {
            _host.Run(L.F("Switching to {0}", c.ShortSha), async () =>
            {
                if (!await SafeSwitch.AskAsync(c.Sha, L.T("Switch to Commit"),
                        L.F("The working tree will switch to {0} (“{1}”).\n\n" +
                            "HEAD will be detached: new commits will not belong to any branch " +
                            "until you create one.", c.ShortSha, c.Subject)))
                    return;

                var r = await GitHistory.CheckoutCommitAsync(c.Sha);
                _host.SetStatus(r.Ok ? L.F("Working tree at {0}", c.ShortSha) : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                _pendingSelectSha = c.Sha;
                ReloadAll();
            });
        }

        private void CreateBranch(GitCommit c)
        {
            var name = TextPromptWindow.Ask(
                L.T("New Branch"),
                L.F("The branch will start at commit {0} (“{1}”).", c.ShortSha, c.Subject),
                L.T("Branch Name"), SuggestBranchName(c));

            if (string.IsNullOrEmpty(name)) return;

            bool switchTo = EditorUtility.DisplayDialog(
                L.T("New Branch"),
                L.F("Branch “{0}” will be created from {1}.\n\nSwitch to it now?", name, c.ShortSha),
                L.T("Switch"), L.T("Stay Here"));

            _host.Run(L.F("Creating branch {0}", name), async () =>
            {
                if (switchTo && !await SafeSwitch.AskAsync(c.Sha, L.T("New Branch"),
                        L.F("The working tree will switch to branch “{0}”.", name)))
                    switchTo = false;

                var r = switchTo
                    ? await GitHistory.CreateBranchAndCheckoutAsync(name, c.Sha)
                    : await GitHistory.CreateBranchAsync(name, c.Sha);

                _host.SetStatus(r.Ok ? L.F("Branch “{0}” created", name) : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                ReloadAll();
            });
        }

        /// <summary>Заготовка имени: из заголовка коммита, но только безопасные символы.</summary>
        private static string SuggestBranchName(GitCommit c)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in c.Subject.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) && ch < 128) sb.Append(ch);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                if (sb.Length >= 30) break;
            }
            return sb.ToString().Trim('-');
        }

        private void CreateTag(GitCommit c)
        {
            var name = TextPromptWindow.Ask(
                L.T("New Tag"), L.F("The tag will point to commit {0}.", c.ShortSha), L.T("Tag Name"), string.Empty);

            if (string.IsNullOrEmpty(name)) return;

            _host.Run(L.F("Creating tag {0}", name), async () =>
            {
                var r = await GitHistory.TagAsync(name, c.Sha, null);
                _host.SetStatus(r.Ok ? L.F("Tag “{0}” created", name) : r.Message, !r.Ok);
                _loadedHead = null;
                Reload();
            });
        }

        private void CherryPick(GitCommit c)
        {
            _host.Run(L.F("Cherry-picking {0}", c.ShortSha), async () =>
            {
                if (!await SafeSwitch.AskAsync(null, L.T("Cherry-Pick Commit"),
                        L.F("Changes from {0} (“{1}”) will be applied " +
                            "to the current branch as a new commit.", c.ShortSha, c.Subject)))
                    return;

                var r = await GitHistory.CherryPickAsync(c.Sha, c.IsMerge, false);
                _host.SetStatus(r.Ok ? L.T("Commit cherry-picked") : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                ReloadAll();
            });
        }

        private void Revert(GitCommit c)
        {
            _host.Run(L.F("Reverting {0}", c.ShortSha), async () =>
            {
                if (!await SafeSwitch.AskAsync(null, L.T("Revert Commit"),
                        L.F("A new commit that undoes {0} (“{1}”) will be created." +
                            "\n\nThe commit itself stays in the history.", c.ShortSha, c.Subject)))
                    return;

                var r = await GitHistory.RevertAsync(c.Sha, c.IsMerge, false);
                _host.SetStatus(r.Ok ? L.T("Revert done") : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                ReloadAll();
            });
        }

        /// <summary>
        /// Правка сообщения коммита, которого ещё нет на сервере. Отправленную
        /// историю переписывать значило бы мешать тем, кто её уже забрал, поэтому
        /// у таких коммитов этого пункта в меню нет.
        /// </summary>
        private async void EditMessage(GitCommit c)
        {
            if (_host.Busy) return;

            var current = await GitHistory.MessageAsync(c.Sha) ?? c.Subject;
            bool isHead = c.Sha == GitStatusCache.HeadOid;

            var message = CommitMessageWindow.Ask(
                L.T("Edit Commit Message"),
                isHead
                    ? L.F("Commit {0} is only on this computer: its message is changed in place.", c.ShortSha)
                    : L.F("Commit {0} is only on this computer. The commits after it get new SHAs, their contents don't change.", c.ShortSha),
                current);

            if (message == null || message == current.Trim()) return;

            _host.Run(L.F("Editing message of {0}", c.ShortSha), async () =>
            {
                // Пока окно было открыто, коммит могли отправить.
                if (!(await GitHistory.LocalCommitsAsync()).Contains(c.Sha))
                {
                    _host.SetStatus(L.T("The commit is already on the server: its message can't be changed without push --force."), true);
                    return;
                }

                var r = await GitHistory.RewordAsync(c.Sha, message);
                _host.SetStatus(r.Ok ? L.T("Commit message changed") : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                ReloadAll();
            });
        }

        private void Reset(GitCommit c, string mode)
        {
            // Жёсткий сброс — единственное действие здесь, которое безвозвратно
            // уничтожает несохранённую работу, поэтому у него отдельный вопрос
            // с прямым названием последствий.
            string what;
            switch (mode)
            {
                case "soft":
                    what = L.F("Commits after {0} will be undone, " +
                               "and their changes will stay in the index.", c.ShortSha);
                    break;
                case "mixed":
                    what = L.F("Commits after {0} will be undone, " +
                               "and their changes will stay in the files, but not in the index.", c.ShortSha);
                    break;
                default:
                    what = L.F("Commits after {0} and ALL uncommitted " +
                               "changes will be deleted permanently.", c.ShortSha);
                    break;
            }

            _host.Run(L.F("Resetting to {0}", c.ShortSha), async () =>
            {
                var report = await SafeSwitch.InspectAsync(c.Sha);
                if (report.Blocked) { SafeSwitch.Confirm(report, L.T("Resetting"), what); return; }

                if (!EditorUtility.DisplayDialog(L.T("Reset Branch"), what + "\n\n" + L.T("Proceed?"),
                        mode == "hard" ? L.T("Yes, Erase") : L.T("Proceed"), L.T("Cancel")))
                    return;

                var r = await GitHistory.ResetAsync(c.Sha, mode);
                _host.SetStatus(r.Ok ? L.F("Branch reset to {0}", c.ShortSha) : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                _pendingSelectSha = c.Sha;
                ReloadAll();
            });
        }
    }
}
