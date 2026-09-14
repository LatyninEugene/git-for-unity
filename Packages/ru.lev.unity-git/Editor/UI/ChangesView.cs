using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git.UI
{
    /// <summary>
    /// Список изменений.
    ///
    /// Что уйдёт в коммит, задают флажки — на файле и на отдельных фрагментах
    /// внутри него. Флажок это состояние самого списка, а не индекса git:
    /// щёлкать по файлу и каждый раз ждать `git add` было бы медленно и
    /// засоряло бы журнал команд. Индекс собирается из отметок один раз, в
    /// момент коммита, — см. <see cref="Commit"/>.
    ///
    /// Индекса пользователь не видит вовсе. Это сознательно: он не состояние,
    /// которое стоит держать в голове, а способ, которым git принимает коммит.
    ///
    /// Изменения разложены по группам, и пустые группы не рисуются вовсе:
    /// вместо двухсот однородных строк человек видит два-три заголовка с числами.
    /// </summary>
    public sealed class ChangesView : VisualElement
    {
        /// <summary>Что из файла отмечено к коммиту.</summary>
        private sealed class Selection
        {
            public bool Included = true;

            /// <summary>
            /// Ключи снятых СТРОК. Пусто — файл идёт целиком.
            ///
            /// Ключ строится от содержимого фрагмента, а не от его номера:
            /// правка выше по файлу сдвигает номера, и отметки бы «поехали».
            /// Изменившийся фрагмент получает новый ключ и считается новым — то
            /// есть снова входит в коммит целиком, что честнее, чем унаследовать
            /// снятую отметку от другого текста.
            /// </summary>
            public HashSet<string> ExcludedLines;

            public bool IsPartial => Included && ExcludedLines != null && ExcludedLines.Count > 0;
        }

        private sealed class Group
        {
            public string Key;
            public string Title;
            public GitFileStatus Status;
            public List<GitChange> Items;
            public bool Checkable;            // конфликты отметить нельзя
        }

        private sealed class Row
        {
            public Group Group;      // не null у заголовка группы
            public PathNode<GitChange> Folder; // не null у папки
            public GitChange Change; // не null у файла
            public int Depth;
            public string GroupKey;
        }

        private sealed class RowWidgets
        {
            public VisualElement GroupBox, RowBox, FolderBox, Dot;
            public TriCheck GroupCheck, Check, FolderCheck;
            public Label Arrow, GroupTitle, GroupCount;
            public Label FolderArrow, FolderName, FolderMeta, FolderCount;
            public Label Name, Dir, Meta;
            public StatusBadge Badge;
            public Image Icon, IconBefore, FolderIcon;
            public Row Bound;
        }

        private readonly IGitHost _host;
        private readonly ListView _list = new ListView();
        private readonly List<Row> _rows = new List<Row>();
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.Ordinal);
        private readonly VisualElement _detail = new VisualElement();
        private readonly DiffView _diff;
        private readonly AssetPreviewPane _preview;

        private TextField _message;
        private Label _summary;
        private Button _commit, _commitPush;
        private Button _options;
        private bool _amend;

        /// <summary>Отметки по файлам. Ключ — путь: список пересобирается на каждом обновлении статуса.</summary>
        private readonly Dictionary<string, Selection> _selection =
            new Dictionary<string, Selection>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Свёрнутые папки. Ключ — «группа/путь»: одна папка может быть в двух группах.</summary>
        private readonly HashSet<string> _collapsedFolders = new HashSet<string>(StringComparer.Ordinal);

        private string _filter = string.Empty;
        private string _amendedMessage;
        private bool _signoff;
        private bool _tree = true;

        // Содержимое меню параметров: подгружается заранее, см. LoadCommitMetaAsync.
        private List<string> _recentMessages = new List<string>();
        private string _commitTemplate;
        private bool _metaLoaded;

        public ChangesView(IGitHost host)
        {
            _host = host;
            style.flexGrow = 1f;

            var split = new TwoPaneSplitView(0, 340f, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1f;
            split.style.minHeight = 0f;
            Add(split);

            // ------------------------------------------------ левая панель ---
            var left = Ui.Box("pane");
            left.style.minWidth = 240f;
            split.Add(left);

            var bar = Ui.Box("subbar");
            var search = new ToolbarSearchField();
            search.style.flexGrow = 1f;
            search.RegisterValueChangedCallback(e =>
            {
                _filter = e.newValue ?? string.Empty;
                Rebuild();
            });
            bar.Add(search);

            var treeToggle = Ui.Action(_tree ? L.T("Tree") : L.T("List"), null, L.T("Show folders as a tree or a flat list"));
            treeToggle.clicked += () =>
            {
                _tree = !_tree;
                treeToggle.text = _tree ? L.T("Tree") : L.T("List");
                Rebuild();
            };
            bar.Add(treeToggle);
            left.Add(bar);

            _list.fixedItemHeight = 22f;
            _list.selectionType = SelectionType.Multiple;
            _list.makeItem = MakeRow;
            _list.bindItem = BindRow;
            _list.itemsSource = _rows;
            _list.AddToClassList("list");
            _list.selectionChanged += _ => RenderDetail();
            Lev.Git.Preview.PreviewThumbnails.Watch(_list);
            left.Add(_list);

            left.Add(BuildCommitPanel());

            // ------------------------------------------------ правая панель ---
            var right = Ui.Box("pane", "pane--detail");
            right.style.minWidth = 180f;

            // Сведения занимают столько, сколько нужно, но не больше 45% высоты:
            // ниже стоит diff, и его списку строк нужна определённая высота,
            // иначе виртуализация не работает.
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexShrink = 0f;
            scroll.style.maxHeight = Length.Percent(45f);
            _detail.AddToClassList("detail");
            scroll.Add(_detail);
            right.Add(scroll);

            _preview = new AssetPreviewPane();
            right.Add(_preview);

            _diff = new DiffView(host);
            _diff.IsLineSelected = IsLineSelected;
            _diff.SetLineSelected = SetLineSelected;
            _diff.GetHunkState = GetHunkState;
            _diff.SetHunkSelected = SetHunkSelected;
            _diff.FileChanged += OnFileChanged;
            right.Add(_diff);
            _preview.AttachDiff(_diff);

            split.Add(right);
        }

        private VisualElement BuildCommitPanel()
        {
            var panel = Ui.Box("commit");

            _message = new TextField { multiline = true };
            _message.AddToClassList("commit__field");
            _message.textEdition.placeholder = L.T("Commit message  (Ctrl+Enter to commit)");
            _message.RegisterValueChangedCallback(_ => UpdateButtons());

            // Ctrl+Enter прямо из поля: рука уже там, где набирают сообщение.
            _message.RegisterCallback<KeyDownEvent>(e =>
            {
                if ((e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) || !e.ctrlKey) return;
                if (_commit.enabledSelf) Commit();
                e.StopPropagation();
            });
            panel.Add(_message);

            // Подсказка делит строку с меню параметров, а кнопки коммита стоят
            // отдельной строкой и переносятся при нехватке ширины: раскладка в
            // один ряд на узкой панели прятала кнопки за край.
            var meta = Ui.Box("commit__meta");
            _summary = Ui.Text(string.Empty, "commit__summary");
            meta.Add(_summary);

            _options = Ui.Action(L.T("Options ▾"), ShowOptionsMenu,
                L.T("Amend the last commit, insert a recent message or the template, " +
                    "add a Signed-off-by line"));
            meta.Add(_options);
            panel.Add(meta);

            var row = Ui.Box("commit__bar");
            _commitPush = Ui.Action(L.T("Commit and Push"), CommitAndPush);
            _commit = Ui.Action(L.T("Commit"), Commit, null, true);
            row.Add(_commitPush);
            row.Add(_commit);

            panel.Add(row);
            return panel;
        }

        // ------------------------------------------------------- отметки ---

        private Selection Get(GitChange c)
        {
            Selection s;
            if (_selection.TryGetValue(c.ProjectPath, out s)) return s;

            // По умолчанию: правки отслеживаемых файлов идут в коммит, новые —
            // нет. Случайно закоммитить мусор из рабочей папки должно быть
            // трудно, а забыть свою же правку — легко заметно.
            s = new Selection
            {
                Included = !c.IsConflicted &&
                           (c.IsStaged || c.WorkStatus != GitFileStatus.Untracked)
            };
            _selection[c.ProjectPath] = s;
            return s;
        }

        private CheckState StateOf(GitChange c)
        {
            var s = Get(c);
            if (!s.Included) return CheckState.Off;
            return s.IsPartial ? CheckState.Partial : CheckState.On;
        }

        private void SetIncluded(GitChange c, bool on)
        {
            var s = Get(c);
            s.Included = on;
            // Щелчок по файлу — решение обо всём файле, частичные отметки сбрасываются.
            s.ExcludedLines = null;
        }

        private static string LineKey(DiffHunk hunk, int lineIndex)
        {
            return hunk.Key + ":" + lineIndex;
        }

        private bool IsLineSelected(GitChange change, DiffHunk hunk, int lineIndex)
        {
            var s = Get(change);
            if (!s.Included) return false;
            return s.ExcludedLines == null || !s.ExcludedLines.Contains(LineKey(hunk, lineIndex));
        }

        private CheckState GetHunkState(GitChange change, DiffHunk hunk)
        {
            var s = Get(change);
            if (!s.Included) return CheckState.Off;
            if (s.ExcludedLines == null) return CheckState.On;

            int total = 0, on = 0;
            for (int i = 0; i < hunk.Lines.Count; i++)
            {
                if (!GitPatchBuilder.IsSelectable(hunk.Lines[i])) continue;
                total++;
                if (!s.ExcludedLines.Contains(LineKey(hunk, i))) on++;
            }

            if (total == 0 || on == total) return CheckState.On;
            return on == 0 ? CheckState.Off : CheckState.Partial;
        }

        private void SetHunkSelected(GitChange change, DiffHunk hunk, bool on)
        {
            var s = Prepare(change);

            for (int i = 0; i < hunk.Lines.Count; i++)
            {
                if (!GitPatchBuilder.IsSelectable(hunk.Lines[i])) continue;
                if (on) s.ExcludedLines.Remove(LineKey(hunk, i));
                else s.ExcludedLines.Add(LineKey(hunk, i));
            }

            Normalize(change, s);
        }

        private void SetLineSelected(GitChange change, DiffHunk hunk, int lineIndex, bool on)
        {
            var s = Prepare(change);

            if (on) s.ExcludedLines.Remove(LineKey(hunk, lineIndex));
            else s.ExcludedLines.Add(LineKey(hunk, lineIndex));

            Normalize(change, s);
        }

        /// <summary>
        /// Готовит набор снятых строк к точечной правке.
        ///
        /// Если файл был снят целиком, точечная отметка означает «беру только
        /// это»: включаем файл и снимаем всё остальное, иначе один щелчок по
        /// строке протащил бы в коммит весь файл.
        /// </summary>
        private Selection Prepare(GitChange change)
        {
            var s = Get(change);

            if (!s.Included)
            {
                s.Included = true;
                s.ExcludedLines = new HashSet<string>(StringComparer.Ordinal);
                foreach (var h in _diff.Hunks)
                    for (int i = 0; i < h.Lines.Count; i++)
                        if (GitPatchBuilder.IsSelectable(h.Lines[i]))
                            s.ExcludedLines.Add(LineKey(h, i));
            }

            if (s.ExcludedLines == null)
                s.ExcludedLines = new HashSet<string>(StringComparer.Ordinal);

            return s;
        }

        /// <summary>Приводит крайние состояния к простым: «всё» и «ничего».</summary>
        private void Normalize(GitChange change, Selection s)
        {
            int total = 0, off = 0;
            foreach (var h in _diff.Hunks)
            {
                for (int i = 0; i < h.Lines.Count; i++)
                {
                    if (!GitPatchBuilder.IsSelectable(h.Lines[i])) continue;
                    total++;
                    if (s.ExcludedLines.Contains(LineKey(h, i))) off++;
                }
            }

            if (total > 0 && off == total)
            {
                // Сняли всё до последней строки — это то же самое, что снять файл.
                s.Included = false;
                s.ExcludedLines = null;
            }
            else if (off == 0)
            {
                s.ExcludedLines = null;
            }

            _list.RefreshItems();
            UpdateButtons();
            RenderDetail();
        }

        private int MarkedCount()
        {
            int n = 0;
            foreach (var c in GitStatusCache.Changes)
                if (!c.IsConflicted && Get(c).Included) n++;
            return n;
        }

        // ------------------------------------------------------- обновление ---

        public void Refresh()
        {
            if (!_metaLoaded) LoadCommitMetaAsync();

            Rebuild();
            UpdateButtons();
            RenderDetail();
        }

        private void OnFileChanged()
        {
            Rebuild();
            UpdateButtons();
        }

        private bool Matches(GitChange c)
        {
            return _filter.Length == 0 ||
                   c.ProjectPath.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Rebuild()
        {
            var conflicts = new List<GitChange>();
            var changed = new List<GitChange>();
            var untracked = new List<GitChange>();

            foreach (var c in GitStatusCache.Changes)
            {
                if (!Matches(c)) continue;

                if (c.IsConflicted) conflicts.Add(c);
                else if (!c.IsStaged && c.WorkStatus == GitFileStatus.Untracked) untracked.Add(c);
                else changed.Add(c);
            }

            ForgetVanished();

            _rows.Clear();
            AddGroup("conflict", L.T("Conflicts"), GitFileStatus.Conflicted, conflicts, false);
            AddGroup("changed", L.T("Changes"), GitFileStatus.Modified, changed, true);
            AddGroup("untracked", L.T("Untracked"), GitFileStatus.Untracked, untracked, true);

            _list.itemsSource = _rows;
            _list.Rebuild();
        }

        /// <summary>Убирает отметки закоммиченных и исчезнувших файлов, иначе словарь растёт вечно.</summary>
        private void ForgetVanished()
        {
            if (_selection.Count < 512) return;

            var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in GitStatusCache.Changes) alive.Add(c.ProjectPath);

            var dead = new List<string>();
            foreach (var key in _selection.Keys) if (!alive.Contains(key)) dead.Add(key);
            foreach (var key in dead) _selection.Remove(key);
        }

        private void AddGroup(string key, string title, GitFileStatus status,
                              List<GitChange> items, bool checkable)
        {
            // Пустая группа не рисуется: заголовок с нулём — тоже строка,
            // а их количество здесь и есть главная проблема читаемости.
            if (items.Count == 0) return;

            _rows.Add(new Row
            {
                GroupKey = key,
                Group = new Group
                {
                    Key = key, Title = title, Status = status,
                    Items = items, Checkable = checkable
                }
            });

            if (_collapsed.Contains(key)) return;

            // При активном поиске дерево мешает: человек ищет файл, а не место,
            // и промежуточные папки только отодвигают ответ.
            if (_tree && _filter.Length == 0)
                AddNode(PathTree.Build(items, c => c.ProjectPath, IsFolderMeta), 0, key);
            else
                foreach (var c in Sorted(items)) _rows.Add(new Row { Change = c, GroupKey = key });
        }

        /// <summary>
        /// Запись от меты папки. Файлом её показывать нельзя: её путь — сам
        /// каталог, и в дереве она встала бы строкой-двойником рядом с папкой.
        /// Диск проверяем только у записей без ассета — их единицы.
        /// </summary>
        private static bool IsFolderMeta(GitChange c)
        {
            if (!c.MetaOnly) return false;

            var full = System.IO.Path.Combine(GitRepository.ProjectRoot, c.ProjectPath);
            if (System.IO.Directory.Exists(full)) return true;

            // Удалённая папка: каталога уже нет, но нет и файла с таким именем.
            return c.Status == GitFileStatus.Deleted && !System.IO.File.Exists(full);
        }

        private static List<GitChange> Sorted(List<GitChange> items)
        {
            var copy = new List<GitChange>(items);
            copy.Sort((a, b) => string.Compare(a.ProjectPath, b.ProjectPath, StringComparison.OrdinalIgnoreCase));
            return copy;
        }

        // ----------------------------------------------------------- дерево ---

        private void AddNode(PathNode<GitChange> node, int depth, string groupKey)
        {
            foreach (var f in node.Folders)
            {
                _rows.Add(new Row { Folder = f, Depth = depth, GroupKey = groupKey });
                if (!_collapsedFolders.Contains(groupKey + "/" + f.Path))
                    AddNode(f, depth + 1, groupKey);
            }

            foreach (var c in Sorted(node.Files))
                _rows.Add(new Row { Change = c, Depth = depth, GroupKey = groupKey });
        }

        private static void CollectFiles(PathNode<GitChange> node, List<GitChange> into)
        {
            if (node.HasSelf) into.Add(node.Self);
            foreach (var f in node.Folders) CollectFiles(f, into);
            into.AddRange(node.Files);
        }

        private CheckState FolderState(PathNode<GitChange> node)
        {
            var files = new List<GitChange>();
            CollectFiles(node, files);

            int on = 0, partial = 0;
            foreach (var c in files)
            {
                var s = StateOf(c);
                if (s == CheckState.On) on++;
                else if (s == CheckState.Partial) partial++;
            }

            if (partial > 0 || (on > 0 && on < files.Count)) return CheckState.Partial;
            return on == 0 ? CheckState.Off : CheckState.On;
        }

        private CheckState GroupState(Group g)
        {
            int on = 0, partial = 0;
            foreach (var c in g.Items)
            {
                var s = StateOf(c);
                if (s == CheckState.On) on++;
                else if (s == CheckState.Partial) partial++;
            }

            if (partial > 0) return CheckState.Partial;
            if (on == 0) return CheckState.Off;
            return on == g.Items.Count ? CheckState.On : CheckState.Partial;
        }

        // ---------------------------------------------------------- строки ---

        private VisualElement MakeRow()
        {
            var wrap = new VisualElement();
            var w = new RowWidgets();

            // ---- заголовок группы ----
            w.GroupBox = Ui.Box("group");

            w.GroupCheck = new TriCheck();
            w.GroupCheck.Clicked += on =>
            {
                var g = w.Bound != null ? w.Bound.Group : null;
                if (g == null || !g.Checkable) return;
                foreach (var c in g.Items) SetIncluded(c, on);
                _list.RefreshItems();
                UpdateButtons();
                RenderDetail();
            };
            w.GroupBox.Add(w.GroupCheck);

            w.Arrow = Ui.Text(string.Empty, "group__arrow");
            w.Dot = Ui.Box("group__dot");
            w.GroupTitle = Ui.Text(string.Empty, "group__title");
            w.GroupCount = Ui.Text(string.Empty, "group__count");
            w.GroupBox.Add(w.Arrow);
            w.GroupBox.Add(w.Dot);
            w.GroupBox.Add(w.GroupTitle);
            w.GroupBox.Add(w.GroupCount);

            // Сворачивание — по клику на заголовок, но не на флажок.
            w.GroupBox.RegisterCallback<MouseDownEvent>(e =>
            {
                if (w.Bound == null || w.Bound.Group == null) return;
                var t = e.target as VisualElement;
                if (t == w.GroupCheck || (t != null && w.GroupCheck.Contains(t))) return;

                var key = w.Bound.Group.Key;
                if (!_collapsed.Remove(key)) _collapsed.Add(key);
                // Перестройка откладывается на кадр: сейчас мы внутри обработчика
                // на элементе, который эта перестройка и переиспользует.
                _list.schedule.Execute(Rebuild);
                e.StopPropagation();
            });
            wrap.Add(w.GroupBox);

            // ---- строка папки ----
            w.FolderBox = Ui.Box("row", "row--folder");

            w.FolderCheck = new TriCheck();
            w.FolderCheck.Clicked += on =>
            {
                if (w.Bound == null || w.Bound.Folder == null) return;
                var files = new List<GitChange>();
                CollectFiles(w.Bound.Folder, files);
                foreach (var c in files) if (!c.IsConflicted) SetIncluded(c, on);
                _list.RefreshItems();
                UpdateButtons();
                RenderDetail();
            };
            w.FolderBox.Add(w.FolderCheck);

            w.FolderArrow = Ui.Text(string.Empty, "group__arrow");
            w.FolderIcon = new Image();
            w.FolderIcon.AddToClassList("row__icon");
            w.FolderName = Ui.Text(string.Empty, "row__name");
            w.FolderMeta = Ui.Text(string.Empty, "row__meta");
            w.FolderCount = Ui.Text(string.Empty, "row__dir");
            w.FolderBox.Add(w.FolderArrow);
            w.FolderBox.Add(w.FolderIcon);
            w.FolderBox.Add(w.FolderName);
            w.FolderBox.Add(w.FolderMeta);
            w.FolderBox.Add(w.FolderCount);

            w.FolderBox.RegisterCallback<MouseDownEvent>(e =>
            {
                // Только левой кнопкой: правая открывает меню и папку сворачивать не должна.
                if (e.button != 0 || w.Bound == null || w.Bound.Folder == null) return;
                var t = e.target as VisualElement;
                if (t == w.FolderCheck || (t != null && w.FolderCheck.Contains(t))) return;

                var key = w.Bound.GroupKey + "/" + w.Bound.Folder.Path;
                if (!_collapsedFolders.Remove(key)) _collapsedFolders.Add(key);
                _list.schedule.Execute(Rebuild);
                e.StopPropagation();
            });

            w.FolderBox.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                var folder = w.Bound != null ? w.Bound.Folder : null;
                if (folder == null) return;

                var files = new List<GitChange>();
                CollectFiles(folder, files);
                if (folder.HasSelf && !files.Contains(folder.Self)) files.Add(folder.Self);

                var untracked = files.FindAll(IsUntracked);
                if (untracked.Count > 0)
                {
                    evt.menu.AppendAction(L.F("Add to Git (git add) ({0})", untracked.Count), _ => AddToGit(untracked),
                        _host.Busy ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
                    evt.menu.AppendSeparator();
                }

                var markable = files.FindAll(c => !c.IsConflicted);
                evt.menu.AppendAction(L.F("Include in Commit ({0})", markable.Count), _ => Mark(markable, true));
                evt.menu.AppendAction(L.F("Exclude from Commit ({0})", markable.Count), _ => Mark(markable, false));
                evt.menu.AppendSeparator();

                var path = folder.Path;
                evt.menu.AppendAction(L.T("Show in Project"), _ =>
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (obj != null) EditorGUIUtility.PingObject(obj);
                });
            }));

            wrap.Add(w.FolderBox);

            // ---- строка файла ----
            w.RowBox = Ui.Box("row");

            w.Check = new TriCheck();
            w.Check.Clicked += on =>
            {
                var change = w.Bound != null ? w.Bound.Change : null;
                if (change == null || change.IsConflicted) return;

                // Щёлкнули по строке, входящей в выделение, — меняем всё выделение:
                // отмечать полсотни файлов по одному никто не станет.
                var target = Selected();
                if (target.Count > 1 && target.Contains(change))
                    foreach (var c in target) { if (!c.IsConflicted) SetIncluded(c, on); }
                else
                    SetIncluded(change, on);

                _list.RefreshItems();
                UpdateButtons();
                RenderDetail();
            };
            w.RowBox.Add(w.Check);

            w.Badge = new StatusBadge();
            w.Icon = new Image();
            w.Icon.AddToClassList("row__icon");
            w.IconBefore = new Image();
            w.IconBefore.AddToClassList("row__icon");
            w.IconBefore.AddToClassList("row__icon--before");
            w.Name = Ui.Text(string.Empty, "row__name");
            w.Dir = Ui.Text(string.Empty, "row__dir");
            w.Meta = Ui.Text(string.Empty, "row__meta");
            w.RowBox.Add(w.Badge);
            w.RowBox.Add(w.IconBefore);
            w.RowBox.Add(w.Icon);
            w.RowBox.Add(w.Name);
            w.RowBox.Add(w.Dir);
            w.RowBox.Add(w.Meta);

            w.RowBox.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                var change = w.Bound != null ? w.Bound.Change : null;
                if (change == null) return;

                var target = Selected();
                if (!target.Contains(change)) target = new List<GitChange> { change };
                bool many = target.Count > 1;

                if (change.IsConflicted)
                {
                    evt.menu.AppendAction(L.T("Resolve Conflict…"), _ => ConflictResolverWindow.Open(change.ProjectPath));
                    evt.menu.AppendSeparator();
                }

                var untracked = target.FindAll(IsUntracked);
                if (untracked.Count > 0)
                {
                    evt.menu.AppendAction(untracked.Count > 1
                            ? L.F("Add to Git (git add) ({0})", untracked.Count)
                            : L.T("Add to Git (git add)"),
                        _ => AddToGit(untracked),
                        _host.Busy ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
                    evt.menu.AppendSeparator();
                }

                evt.menu.AppendAction(many ? L.F("Include in Commit ({0})", target.Count) : L.T("Include in Commit"),
                    _ => Mark(target, true));
                evt.menu.AppendAction(many ? L.F("Exclude from Commit ({0})", target.Count) : L.T("Exclude from Commit"),
                    _ => Mark(target, false));
                evt.menu.AppendSeparator();

                // У нового файла прошлой версии нет — сравнивать и листать историю нечего.
                if (target.Count == 1 && target[0].Status != GitFileStatus.Deleted &&
                    target[0].Status != GitFileStatus.Added && !IsUntracked(target[0]))
                {
                    var path = target[0].ProjectPath;
                    evt.menu.AppendAction(L.T("Compare with Last Commit"), _ => AssetHistoryWindow.Open(path, false));
                    evt.menu.AppendAction(L.T("Asset History"), _ => AssetHistoryWindow.Open(path, false));
                    evt.menu.AppendSeparator();
                }

                var revertable = target.FindAll(c => !c.IsConflicted);
                evt.menu.AppendAction(many ? L.F("Discard Changes… ({0})", target.Count) : L.T("Discard Changes…"),
                    _ => DiscardFiles(revertable),
                    revertable.Count > 0 && !_host.Busy ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                evt.menu.AppendSeparator();
                evt.menu.AppendAction(L.T("Show in Project"), _ =>
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(change.ProjectPath);
                    if (obj != null) EditorGUIUtility.PingObject(obj);
                });
            }));

            wrap.Add(w.RowBox);
            wrap.userData = w;
            return wrap;
        }

        private static bool IsUntracked(GitChange c)
        {
            return c != null && !c.IsStaged && c.WorkStatus == GitFileStatus.Untracked;
        }

        /// <summary>
        /// git add для неотслеживаемых файлов — вместе с их .meta. После этого
        /// файл виден git как новый и попадает в группу изменений.
        /// </summary>
        private void AddToGit(List<GitChange> items)
        {
            if (items.Count == 0) return;

            _host.Run("git add", async () =>
            {
                var r = await GitOperations.StageAsync(items);
                _host.SetStatus(r.Ok
                    ? (items.Count == 1 ? L.F("Added to Git: {0}", items[0].ProjectPath) : L.F("Files added to Git: {0}", items.Count))
                    : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                Rebuild();
                UpdateButtons();
            });
        }

        /// <summary>
        /// Файлы целиком — к последнему коммиту: изменённые и удалённые
        /// возвращаются, новые удаляются. Любой тип файла, вместе с .meta.
        /// </summary>
        private void DiscardFiles(List<GitChange> items)
        {
            if (items.Count == 0) return;

            var created = items.FindAll(c => c.Status == GitFileStatus.Untracked || c.Status == GitFileStatus.Added);
            int restored = items.Count - created.Count;

            var message = string.Empty;
            if (restored > 0) message += L.F("Will return to the last commit: {0}", restored) + "\n";
            if (created.Count > 0)
            {
                message += L.F("New files will be deleted from disk: {0}", created.Count) + "\n";
                for (int i = 0; i < created.Count && i < 8; i++) message += "• " + created[i].ProjectPath + "\n";
                if (created.Count > 8) message += "• " + L.F("…and {0} more", created.Count - 8) + "\n";
            }
            message += "\n" + L.T("Changes in these files will be lost permanently.");

            if (!EditorUtility.DisplayDialog(L.T("Discard Changes"), message, L.T("Discard"), L.T("Cancel")))
                return;

            _host.Run(L.T("Discarding Files"), async () =>
            {
                var r = await GitOperations.DiscardFilesAsync(items);
                _host.SetStatus(r.Ok ? L.F("Discarded to the last commit: {0}", items.Count) : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                Rebuild();
                UpdateButtons();
            });
        }

        private void Mark(List<GitChange> items, bool value)
        {
            foreach (var c in items) if (!c.IsConflicted) SetIncluded(c, value);
            _list.RefreshItems();
            UpdateButtons();
            RenderDetail();
        }

        private void BindRow(VisualElement element, int index)
        {
            var w = (RowWidgets)element.userData;
            var row = _rows[index];
            w.Bound = row;

            bool isGroup = row.Group != null;
            bool isFolder = row.Folder != null;

            w.GroupBox.style.display = isGroup ? DisplayStyle.Flex : DisplayStyle.None;
            w.FolderBox.style.display = isFolder ? DisplayStyle.Flex : DisplayStyle.None;
            w.RowBox.style.display = (!isGroup && !isFolder) ? DisplayStyle.Flex : DisplayStyle.None;

            if (isFolder)
            {
                var f = row.Folder;
                bool collapsed = _collapsedFolders.Contains(row.GroupKey + "/" + f.Path);

                w.FolderCheck.Set(FolderState(f));
                w.FolderArrow.text = collapsed ? "▸" : "▾";
                w.FolderIcon.image = EditorGUIUtility.IconContent(
                    collapsed ? "Folder Icon" : "FolderOpened Icon").image;
                w.FolderName.text = f.Name;

                // Мета самой папки свёрнута в её строку — так же, как мета файла
                // свёрнута в строку файла, — и отмечается флажком папки.
                w.FolderMeta.text = f.HasSelf ? "+meta" : string.Empty;
                w.FolderBox.tooltip = f.HasSelf
                    ? f.Path + "\n" + L.F("folder meta: {0}", Ui.StatusName(f.Self.Status).ToLowerInvariant())
                    : f.Path;

                var files = new List<GitChange>();
                CollectFiles(f, files);
                w.FolderCount.text = files.Count.ToString();

                // Отступ по глубине — единственное, что отличает вложенную папку
                // от корневой: рисовать направляющие линии в списке на 500 строк
                // дорого и без пользы.
                w.FolderBox.style.paddingLeft = row.Depth * 12f;
                return;
            }

            if (isGroup)
            {
                var g = row.Group;

                w.GroupCheck.style.visibility = g.Checkable ? Visibility.Visible : Visibility.Hidden;
                w.GroupCheck.Set(GroupState(g));

                w.Arrow.text = _collapsed.Contains(g.Key) ? "▸" : "▾";
                w.GroupTitle.text = g.Title.ToUpperInvariant();
                w.GroupCount.text = g.Items.Count.ToString();

                Ui.SetStatusFill(w.Dot, g.Status);
                return;
            }

            var change = row.Change;

            w.Check.style.visibility = change.IsConflicted ? Visibility.Hidden : Visibility.Visible;
            w.Check.Set(StateOf(change));

            w.Badge.Set(change.Status);

            // Миниатюры «было → стало» вместо иконки — по настройке проекта.
            Texture after = null, before = null;
            if (Lev.Git.Preview.PreviewThumbnails.Enabled(change.ProjectPath))
            {
                if (change.Status != GitFileStatus.Deleted)
                    after = Lev.Git.Preview.PreviewThumbnails.ForWorktree(change.ProjectPath);
                if (change.Status == GitFileStatus.Modified || change.Status == GitFileStatus.Renamed || change.Status == GitFileStatus.Deleted)
                    before = Lev.Git.Preview.PreviewThumbnails.ForHead(change.OriginalPath ?? change.ProjectPath);
            }

            w.Icon.image = after ?? (change.Status == GitFileStatus.Deleted ? before : null) ?? Ui.AssetIcon(change.ProjectPath);
            bool pair = after != null && before != null;
            w.IconBefore.image = pair ? before : null;
            w.IconBefore.style.display = pair ? DisplayStyle.Flex : DisplayStyle.None;
            w.Name.text = Ui.NameOf(change.ProjectPath);
            // В дереве папка уже названа выше по списку — повторять путь в каждой
            // строке значит писать одно и то же по двадцать раз подряд.
            w.Dir.text = row.Depth > 0 || (_tree && _filter.Length == 0)
                ? string.Empty
                : Ui.DirOf(change.ProjectPath);
            w.Meta.text = change.HasMeta ? "+meta" : string.Empty;
            w.RowBox.style.paddingLeft = row.Depth * 12f;

            w.RowBox.tooltip = change.OriginalPath != null
                ? change.ProjectPath + "\n" + L.F("renamed from {0}", change.OriginalPath)
                : change.ProjectPath;
        }

        // -------------------------------------------------------- действия ---

        private List<GitChange> Selected()
        {
            var list = new List<GitChange>();
            foreach (var o in _list.selectedItems)
            {
                var row = o as Row;
                if (row != null && row.Change != null) list.Add(row.Change);
            }
            return list;
        }

        private string _headOnRemoteKey;
        private bool _headOnRemote;

        /// <summary>
        /// Лежит ли последний коммит на сервере. Проверка асинхронная и одна на
        /// каждое обновление статуса; пока ответа нет — считаем, что не лежит.
        /// Вызывается только в режиме исправления, поэтому git зря не запускается.
        /// </summary>
        private bool HeadOnRemote()
        {
            var head = GitStatusCache.HeadOid;
            if (string.IsNullOrEmpty(head)) return false;

            var key = head + "|" + GitStatusCache.LastRefreshTime.ToString("R");
            if (_headOnRemoteKey != key)
            {
                _headOnRemoteKey = key;
                CheckHeadOnRemote(key);
            }
            return _headOnRemote;
        }

        private async void CheckHeadOnRemote(string key)
        {
            bool on = await GitOperations.HeadOnRemoteAsync();
            if (_headOnRemoteKey != key || _headOnRemote == on) return;
            _headOnRemote = on;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool busy = _host.Busy;
            int marked = MarkedCount();
            int conflicts = GitStatusCache.ConflictCount;
            bool hasMessage = !string.IsNullOrWhiteSpace(_message.value);

            // Исправлять нечего, пока нет ни одного коммита.
            bool canAmend = !GitStatusCache.IsInitialCommit;
            if (!canAmend) _amend = false;
            _options.SetEnabled(!busy);

            bool amend = _amend;
            bool can = !busy && conflicts == 0 && hasMessage && (marked > 0 || amend);

            _commit.SetEnabled(can);
            _commitPush.SetEnabled(can);

            var verb = amend ? L.T("Amend") : L.T("Commit");
            _commit.text = marked > 0 ? verb + " (" + marked + ")" : verb;

            // Последний коммит уже на сервере — по данным последнего fetch, а не по
            // upstream: ветку могли отправить без -u или другим клиентом.
            bool amendPushed = amend && HeadOnRemote();

            int partial = 0;
            foreach (var c in GitStatusCache.Changes) if (Get(c).IsPartial) partial++;

            if (conflicts > 0)
                _summary.text = L.F("Resolve conflicts first: {0}", conflicts);
            else if (amendPushed)
                _summary.text = L.T("The last commit is already on the server: amending rewrites history, " +
                                    "push will need --force");
            else if (amend && marked == 0)
                _summary.text = hasMessage ? L.T("Only the commit message changes") : L.T("A message is required");
            else if (marked == 0)
                _summary.text = GitStatusCache.Changes.Count == 0
                    ? L.T("No changes")
                    : L.T("Check the files that go into the commit");
            else if (!hasMessage)
                _summary.text = L.F("Files to commit: {0} · a message is required", marked);
            else
                _summary.text = partial > 0
                    ? L.F("Files to commit: {0}, partially: {1}", marked, partial)
                    : L.F("Files to commit: {0}", marked);
        }

        /// <summary>
        /// Превращает отметки в план коммита.
        ///
        /// Для файла с частичной отметкой diff перечитывается заново: между
        /// отметкой и коммитом файл могли править, и патч должен собираться из
        /// того, что в нём есть сейчас.
        /// </summary>
        private async Task<List<GitOperations.CommitItem>> BuildItemsAsync()
        {
            var items = new List<GitOperations.CommitItem>();

            foreach (var c in GitStatusCache.Changes)
            {
                if (c.IsConflicted) continue;

                var s = Get(c);
                if (!s.Included) continue;

                if (s.ExcludedLines == null || s.ExcludedLines.Count == 0)
                {
                    items.Add(new GitOperations.CommitItem { Change = c });
                    continue;
                }

                var diff = await GitOperations.DiffAsync(c);
                if (diff == null || diff.Hunks.Count == 0)
                {
                    items.Add(new GitOperations.CommitItem { Change = c });
                    continue;
                }

                // Сколько отмеченных строк осталось после того, как файл могли
                // править между отметкой и коммитом.
                int total = 0, off = 0;
                foreach (var h in diff.Hunks)
                    for (int i = 0; i < h.Lines.Count; i++)
                    {
                        if (!GitPatchBuilder.IsSelectable(h.Lines[i])) continue;
                        total++;
                        if (s.ExcludedLines.Contains(LineKey(h, i))) off++;
                    }

                if (total > 0 && off == total) continue;             // всё снято — файл не идёт
                if (off == 0)                                        // отметки устарели — файл целиком
                {
                    items.Add(new GitOperations.CommitItem { Change = c });
                    continue;
                }

                // В индекс кладём рабочую копию без снятых строк. Обе стороны берутся
                // из одного источника, поэтому переводам строк разойтись негде.
                var gitPath = GitRepository.ToGitPath(c.ProjectPath);
                var worktree = GitOperations.ReadWorktreeText(gitPath);

                var excluded = s.ExcludedLines;
                var content = GitPatchBuilder.BuildStagedText(
                    worktree, diff, (h, i) => !excluded.Contains(LineKey(h, i)));

                if (content == null)
                {
                    // Собрать не удалось — честнее взять файл целиком, чем потерять правки.
                    items.Add(new GitOperations.CommitItem { Change = c });
                    continue;
                }

                items.Add(new GitOperations.CommitItem { Change = c, PartialContent = content });
            }

            return items;
        }

        /// <summary>
        /// Прогоняет проверки целостности и, если что-то найдено, даёт решить.
        /// Возвращает false, если человек отказался коммитить.
        ///
        /// Проверки не запрещают: они называют последствие. Автор может знать
        /// про свой случай больше, чем набор эвристик.
        /// </summary>
        private async Task<bool> PassesChecksAsync(List<GitOperations.CommitItem> items)
        {
            var changes = new List<GitChange>();
            foreach (var it in items) changes.Add(it.Change);

            var checks = await CommitChecks.RunAsync(changes);
            if (checks.Count == 0) return true;

            int errors = 0;
            foreach (var c in checks) if (c.Severity == CheckSeverity.Error) errors++;

            // «Найдено: 1 ошибка, 2 предупреждения» — со склонением и без нулевых частей.
            int warnings = checks.Count - errors;
            var counts = new List<string>();
            if (errors > 0) counts.Add(L.N("{0} error", "{0} errors", errors));
            if (warnings > 0) counts.Add(L.N("{0} warning", "{0} warnings", warnings));

            var summary = new System.Text.StringBuilder();
            summary.Append(L.F("Found: {0}.", string.Join(", ", counts.ToArray())));
            summary.Append("\n\n");

            for (int i = 0; i < checks.Count && i < 4; i++)
                summary.Append("• ").Append(checks[i].Title).Append(": ").Append(checks[i].ProjectPath).Append('\n');

            if (checks.Count > 4) summary.Append(L.F("…and {0} more", checks.Count - 4)).Append('\n');
            summary.Append('\n').Append(L.T("This breaks not for you, but for whoever pulls."));

            int choice = EditorUtility.DisplayDialogComplex(
                L.T("Pre-Commit Checks"), summary.ToString(),
                L.T("Show Details"), L.T("Cancel"), L.T("Commit Anyway"));

            if (choice == 0) { CommitChecksWindow.Open(checks); return false; }
            return choice == 2;
        }

        private void Commit()
        {
            var message = _message.value;
            bool amend = _amend;

            _host.Run(amend ? L.T("Amending Commit") : L.T("Commit"), async () =>
            {
                var items = await BuildItemsAsync();
                if (!await PassesChecksAsync(items)) { _host.SetStatus(L.T("Commit cancelled"), false); return; }
                var r = await GitOperations.CommitSelectionAsync(
                    items, GitStatusCache.Changes, message, amend, _signoff);

                _host.SetStatus(
                    r.Ok ? (amend ? L.T("Commit amended") : L.F("Commit created: {0} files", items.Count))
                         : r.Message,
                    !r.Ok);

                if (r.Ok) ResetCommitPanel();
                await GitStatusCache.RefreshAsync();
            });
        }

        private void CommitAndPush()
        {
            var message = _message.value;
            bool amend = _amend;

            _host.Run(L.T("Commit and Push"), async () =>
            {
                var items = await BuildItemsAsync();
                if (!await PassesChecksAsync(items)) { _host.SetStatus(L.T("Commit cancelled"), false); return; }

                var c = await GitOperations.CommitSelectionAsync(
                    items, GitStatusCache.Changes, message, amend, _signoff);

                if (!c.Ok) { _host.SetStatus(c.Message, true); return; }

                ResetCommitPanel();
                var p = await GitOperations.PushAsync();
                if (!p.Ok) p = await PushRecovery.OfferAsync(p);
                _host.SetStatus(p.Ok ? L.T("Commit pushed") : p.Message, !p.Ok);
                await GitStatusCache.RefreshAsync();
            });
        }

        /// <summary>
        /// Всё, что настраивает предстоящий коммит: режим исправления, прошлые
        /// сообщения, шаблон, подпись.
        ///
        /// Меню, а не элементы в ряд: на панели шириной 240 px четыре контрола
        /// вместе с кнопками коммита не помещаются, и что-то обязательно уезжает
        /// за край. Состояние режима при этом видно и без меню — по надписи на
        /// кнопке коммита и по строке подсказки.
        /// </summary>
        /// <summary>
        /// Подгружает то, что нужно меню параметров, заранее.
        ///
        /// Обязательно заранее: <c>GenericMenu.ShowAsContext</c> позиционируется
        /// по <c>Event.current</c>, а после первого <c>await</c> обработчик уже
        /// вне GUI-события, и меню просто не открывается. Заодно оно открывается
        /// мгновенно, без запуска git по щелчку.
        /// </summary>
        private async void LoadCommitMetaAsync()
        {
            _metaLoaded = true;
            _recentMessages = await GitOperations.RecentMessagesAsync(15);
            _commitTemplate = await GitOperations.CommitTemplateAsync();
        }

        private void ShowOptionsMenu()
        {
            var menu = new GenericMenu();

            bool canAmend = !GitStatusCache.IsInitialCommit;
            if (canAmend)
                menu.AddItem(new GUIContent(L.T("Amend Last Commit")), _amend, () => ToggleAmend(!_amend));
            else
                menu.AddDisabledItem(new GUIContent(L.T("Amend Last Commit")));

            menu.AddItem(new GUIContent(L.T("Add Signed-off-by")), _signoff, () =>
            {
                _signoff = !_signoff;
                UpdateButtons();
            });

            menu.AddSeparator(string.Empty);

            var recent = _recentMessages;
            if (recent.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent(L.T("Recent Messages/Empty")));
            }
            else
            {
                foreach (var m in recent)
                {
                    var full = m;
                    // '/' в GenericMenu создаёт подменю — в тексте сообщения его быть не должно.
                    var label = full.Replace('/', '∕');
                    if (label.Length > 60) label = label.Substring(0, 60) + "…";

                    menu.AddItem(new GUIContent(L.T("Recent Messages") + "/" + label), false, () =>
                    {
                        _message.value = full;
                        UpdateButtons();
                    });
                }
            }

            var template = _commitTemplate;
            if (string.IsNullOrEmpty(template))
            {
                menu.AddDisabledItem(new GUIContent(L.T("No Template Configured (commit.template)")));
            }
            else
            {
                menu.AddItem(new GUIContent(L.T("Insert Template")), false, () =>
                {
                    _message.value = template;
                    UpdateButtons();
                });
            }

            // DropDown с явным прямоугольником, а не ShowAsContext: меню должно
            // раскрываться под своей кнопкой, а не там, где оказался курсор.
            menu.DropDown(_options.worldBound);
        }

        /// <summary>
        /// Переключает режим исправления. Включили — подставляем сообщение
        /// последнего коммита, чтобы его не набирать заново. Выключили — убираем
        /// подставленное, но не то, что человек написал сам.
        /// </summary>
        private async void ToggleAmend(bool on)
        {
            _amend = on;
            UpdateButtons();

            if (!on)
            {
                if (_message.value == _amendedMessage) _message.value = string.Empty;
                _amendedMessage = null;
                UpdateButtons();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_message.value)) return;

            var last = await GitOperations.LastCommitMessageAsync();
            if (string.IsNullOrEmpty(last) || !_amend) return;

            _amendedMessage = last;
            _message.value = last;
            UpdateButtons();
        }

        /// <summary>Коммит удался: чистим поле и снимаем amend, чтобы следующий не ушёл туда же.</summary>
        private void ResetCommitPanel()
        {
            _message.value = string.Empty;
            _amendedMessage = null;
            _amend = false;
            _selection.Clear();

            // Список прошлых сообщений устарел — только что появилось новое.
            _metaLoaded = false;

            UpdateButtons();
        }

        // ---------------------------------------------------------- детали ---

        private void RenderDetail()
        {
            _detail.Clear();

            var sel = Selected();

            // Diff показывается только для одного выбранного файла: для набора
            // он превратился бы в кашу, а показывать «ничего не выбрано» незачем.
            _diff.style.display = sel.Count == 1 ? DisplayStyle.Flex : DisplayStyle.None;
            if (sel.Count != 1) { _diff.Show(null); _preview.Release(); _preview.style.display = DisplayStyle.None; }

            if (sel.Count == 0)
            {
                _detail.Add(Ui.Empty(
                    GitStatusCache.Changes.Count == 0 ? L.T("No Changes") : L.T("Nothing Selected"),
                    GitStatusCache.Changes.Count == 0
                        ? L.T("The working tree matches the last commit.")
                        : L.T("Select a file on the left to see its changes.")));
                return;
            }

            if (sel.Count > 1)
            {
                _detail.Add(Ui.Text(L.F("Selected: {0}", sel.Count), "detail__name"));

                int marked = 0, partial = 0, conflicts = 0;
                foreach (var c in sel)
                {
                    if (c.IsConflicted) { conflicts++; continue; }
                    var st = StateOf(c);
                    if (st == CheckState.On) marked++;
                    else if (st == CheckState.Partial) partial++;
                }

                var card = Ui.Card(L.T("Summary"));
                card.Add(Ui.KeyValue(L.T("Fully Included"), L.Fc("count", "{0} of {1}", marked, sel.Count)));
                if (partial > 0) card.Add(Ui.KeyValue(L.T("Partially Included"), partial.ToString(), "t-warn"));
                if (conflicts > 0) card.Add(Ui.KeyValue(L.Tc("count", "Conflicts"), conflicts.ToString(), "t-error"));
                _detail.Add(card);

                var bar = Ui.Box("commit__bar");
                bar.style.marginTop = 4f;
                bar.Add(Ui.Action(L.T("Include All"), () => Mark(sel, true)));
                bar.Add(Ui.Action(L.T("Exclude All"), () => Mark(sel, false)));
                _detail.Add(bar);
                return;
            }

            var change = sel[0];

            var title = Ui.Box("detail__title");
            var icon = new Image { image = Ui.AssetIcon(change.ProjectPath) };
            icon.style.width = 20f;
            icon.style.height = 20f;
            title.Add(icon);
            title.Add(Ui.Text(Ui.NameOf(change.ProjectPath), "detail__name"));
            _detail.Add(title);
            _detail.Add(Ui.Text(change.ProjectPath, "detail__path"));

            if (change.IsConflicted)
            {
                var banner = Ui.Banner(L.T("Conflict: the file was changed on both sides. Scenes and prefabs are merged by objects " +
                                           "and properties, everything else as a whole or in an external tool."), "error");
                var resolve = Ui.Action(L.T("Resolve…"), () => ConflictResolverWindow.Open(change.ProjectPath), null, true);
                resolve.style.marginLeft = 8f;
                banner.Add(resolve);
                _detail.Add(banner);
            }

            // Факты в строку, а не столбцом: они занимают одну-две строки вместо
            // четырёх, и высота остаётся панели diff, ради которой сюда и смотрят.
            var facts = Ui.Facts();
            var st2 = StateOf(change);

            facts.Add(Ui.Fact(L.T("In Commit:"),
                change.IsConflicted ? L.T("no, conflict")
                    : st2 == CheckState.On ? L.T("fully")
                    : st2 == CheckState.Partial ? L.T("partially")
                    : L.T("no"),
                change.IsConflicted ? "t-error"
                    : st2 == CheckState.On ? "t-ok"
                    : st2 == CheckState.Partial ? "t-warn" : "t-dim"));

            facts.Add(Ui.FactStatus(L.T("Change:"), change.Status));
            // Мета меняется вместе с ассетом: удалили ассет — удалена и она,
            // добавили — добавлена. «Изменена» у удалённого файла сбивает с толку.
            facts.Add(Ui.Fact(".meta:", !change.HasMeta ? L.Tc("meta status", "unchanged")
                : change.Status == GitFileStatus.Deleted ? L.Tc("meta status", "deleted")
                : change.Status == GitFileStatus.Added || change.WorkStatus == GitFileStatus.Untracked ? L.Tc("meta status", "added")
                : L.Tc("meta status", "modified")));

            if (change.OriginalPath != null)
                facts.Add(Ui.Fact(L.T("Renamed From:"), change.OriginalPath));

            _detail.Add(facts);

            var tools = Ui.Box("commit__bar");
            tools.style.marginTop = 4f;
            tools.Add(Ui.Action(L.T("File History"), () => GitWindow.ShowFileHistory(change.ProjectPath),
                                L.T("Show the log of commits that touched this file")));
            _detail.Add(tools);

            // Превью показывается только там, где оно осмысленно, и само решает,
            // прятаться ли: для скрипта картинка не скажет ничего.
            _preview.Show(change);

            _diff.Show(change);
        }
    }
}
