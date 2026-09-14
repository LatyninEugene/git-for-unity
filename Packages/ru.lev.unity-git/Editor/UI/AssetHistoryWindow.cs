using System;
using System.Collections.Generic;
using System.Linq;
using Lev.Git.Preview;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git.UI
{
    /// <summary>
    /// «История ассета» — все версии одного файла по коммитам, с миниатюрой
    /// каждой. То же, что «История сцены», но для любого ассета: материала,
    /// текстуры, звука, модели.
    ///
    /// Справа — та же панель превью, что в окне Git. Что с чем сравнивается:
    /// выбран коммит — его версия против предыдущей; включено «С текущим» — его
    /// версия против рабочей копии, которую можно тут же править; выбраны два
    /// коммита — старший против младшего.
    /// </summary>
    public sealed class AssetHistoryWindow : EditorWindow, IGitHost, ILocalizedWindow
    {
        private bool _busy;
        private DiffView _diff;

        public bool Busy
        {
            get { return _busy; }
        }

        public async void Run(string title, Func<System.Threading.Tasks.Task> body)
        {
            _busy = true;
            SetStatus(title + "…", false);
            try
            {
                await body();
            }
            catch (Exception e)
            {
                SetStatus(title + ": " + e.Message, true);
            }
            finally
            {
                _busy = false;
            }
        }

        public void SetStatus(string message, bool error)
        {
            if (_status == null) return;
            _status.text = message ?? string.Empty;
            _status.EnableInClassList("t-error", error);
        }

        private sealed class Entry
        {
            public GitCommit Commit;
            public bool Worktree;
        }

        [SerializeField] private string _path;
        [SerializeField] private bool _withCurrent;

        private readonly List<Entry> _entries = new List<Entry>();
        private ListView _list;
        private AssetPreviewPane _pane;
        private Label _title, _status;
        private Button _current, _restore;
        private int _gen;

        public static void Open(string projectPath, bool compareWithCurrent)
        {
            if (string.IsNullOrEmpty(projectPath)) return;

            var w = GetWindow<AssetHistoryWindow>(false, L.T("Asset History"), true);
            w.minSize = new Vector2(760f, 420f);
            w._path = projectPath;
            w._withCurrent = compareWithCurrent;
            w.Show();
            w.Focus();
            w.Reload();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            Ui.Attach(root);
            root.style.flexDirection = FlexDirection.Column;

            var bar = Ui.Box("subbar");
            _title = Ui.Text(string.Empty, "subbar__label");
            bar.Add(_title);
            bar.Add(Ui.Spacer());

            _current = Ui.Action(L.T("With Current"), ToggleCurrent,
                L.T("Compare the selected version not with the previous one but with what is in the project now. The current side can be edited"));
            bar.Add(_current);

            _restore = Ui.Action(L.T("Restore This Version…"), Restore, L.T("Write the file into the project as it was in the selected commit"));
            bar.Add(_restore);

            bar.Add(Ui.Action(L.T("To History"), () => GitWindow.ShowFileHistory(_path), L.T("Open the file history in the Git window")));
            root.Add(bar);

            var split = new TwoPaneSplitView(0, 300f, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1f;

            var left = Ui.Box("pane");
            left.style.minWidth = 200f;

            _list = new ListView
            {
                fixedItemHeight = 46f,
                selectionType = SelectionType.Multiple,
                makeItem = MakeRow,
                bindItem = BindRow,
                itemsSource = _entries
            };
            _list.AddToClassList("list");
            _list.style.flexGrow = 1f;
            _list.selectionChanged += _ => ShowSelection();
            PreviewThumbnails.Watch(_list);
            left.Add(_list);

            _status = Ui.Text(string.Empty, "preview__note");
            left.Add(_status);

            var right = Ui.Box("pane", "pane--detail");
            right.style.minWidth = 300f;
            // Под превью — сравнение текста: у скрипта, JSON или USS превью нет, и
            // без него окно было бы пустым. Для ассетов оно — вкладки «Объекты» и «Текст».
            _pane = new AssetPreviewPane();
            right.Add(_pane);

            _diff = new DiffView(this);
            right.Add(_diff);
            _pane.AttachDiff(_diff);

            split.Add(left);
            split.Add(right);
            root.Add(split);

            if (!string.IsNullOrEmpty(_path)) Reload();
        }

        private void OnDisable()
        {
            if (_pane != null) _pane.Release();
        }

        /// <summary>Язык сменился: подписи на UI Toolkit расставлены при сборке, поэтому окно собирается заново.</summary>
        void ILocalizedWindow.OnLanguageChanged()
        {
            SetTitle();
            if (_list == null) return;

            if (_pane != null) _pane.Release();
            rootVisualElement.Clear();
            rootVisualElement.styleSheets.Clear();
            CreateGUI();
        }

        private void SetTitle()
        {
            titleContent = string.IsNullOrEmpty(_path)
                ? new GUIContent(L.T("Asset History"))
                : new GUIContent(L.F("History: {0}", Ui.NameOf(_path)), Ui.AssetIcon(_path));
        }

        // ---------------------------------------------------------- журнал ---

        private async void Reload()
        {
            if (_list == null || string.IsNullOrEmpty(_path)) return;

            int gen = ++_gen;
            SetTitle();
            _title.text = _path;
            _status.text = L.T("Reading the file history…");
            UpdateButtons();

            var page = await GitHistory.LogAsync(new GitLogFilter { ProjectPath = _path, AllRefs = false, Limit = 500 });
            if (gen != _gen) return;

            _entries.Clear();

            bool exists = System.IO.File.Exists(System.IO.Path.Combine(GitRepository.ProjectRoot, _path));
            if (exists) _entries.Add(new Entry { Worktree = true });

            if (page.Commits != null)
                foreach (var commit in page.Commits) _entries.Add(new Entry { Commit = commit });

            _status.text = page.Error ?? (page.Commits == null || page.Commits.Count == 0
                ? L.T("No commits with this file.")
                : L.F("Versions: {0}", page.Commits.Count) + (page.More ? " " + L.T("(showing the latest)") : string.Empty) +
                  " · " + L.T("Ctrl+click — compare two versions"));

            _list.Rebuild();

            // Сразу — самое осмысленное: изменённый файл против последнего коммита,
            // иначе последний коммит против предыдущего.
            bool modified = GitStatusCache.GetStatus(_path) != GitFileStatus.None;
            int index = exists && modified && !_withCurrent ? 0 : exists ? 1 : 0;
            if (index < _entries.Count) _list.SetSelection(index);
            else ShowSelection();
        }

        private VisualElement MakeRow()
        {
            var row = Ui.Box("ahist__row");

            var thumb = new Image { scaleMode = ScaleMode.ScaleToFit };
            thumb.AddToClassList("ahist__thumb");
            row.Add(thumb);

            var text = Ui.Box("ahist__text");
            text.Add(Ui.Text(string.Empty, "ahist__title"));
            text.Add(Ui.Text(string.Empty, "ahist__meta"));
            row.Add(text);

            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            var entry = _entries[index];
            var thumb = (Image)element.ElementAt(0);
            var title = (Label)element.ElementAt(1).ElementAt(0);
            var meta = (Label)element.ElementAt(1).ElementAt(1);

            if (entry.Worktree)
            {
                var status = GitStatusCache.GetStatus(_path);
                thumb.image = PreviewThumbnails.ForWorktree(_path) ?? Ui.AssetIcon(_path);
                title.text = L.T("Now");
                meta.text = status == GitFileStatus.None ? L.T("working tree, same as in the last commit") : L.F("working tree · {0}", Ui.StatusName(status).ToLowerInvariant());
                return;
            }

            var c = entry.Commit;
            thumb.image = PreviewThumbnails.ForCommit(c.Sha, _path) ?? Ui.AssetIcon(_path);
            title.text = c.Subject;
            title.tooltip = c.Subject;
            meta.text = c.ShortSha + " · " + c.Author + " · " + c.Date.ToString("d MMM yyyy, HH:mm");
        }

        // --------------------------------------------------------- сравнение ---

        private List<Entry> Selected()
        {
            return _list.selectedIndices.OrderBy(i => i).Where(i => i < _entries.Count).Select(i => _entries[i]).ToList();
        }

        private void ShowSelection()
        {
            var selected = Selected();
            var gitPath = GitRepository.ToGitPath(_path);
            bool exists = System.IO.File.Exists(System.IO.Path.Combine(GitRepository.ProjectRoot, _path));

            RevisionSide before = null, after = null;

            if (selected.Count >= 2)
            {
                // Список идёт от новых к старым: первый выбранный — новее.
                after = SideOf(selected[0], gitPath);
                before = SideOf(selected[selected.Count - 1], gitPath);
            }
            else if (selected.Count == 1)
            {
                var e = selected[0];
                if (e.Worktree)
                {
                    // Нового файла в последнем коммите ещё нет — «было» у него пусто,
                    // и спрашивать git о HEAD-версии незачем.
                    var status = GitStatusCache.GetStatus(_path);
                    bool inHead = !GitStatusCache.IsInitialCommit &&
                                  status != GitFileStatus.Untracked && status != GitFileStatus.Added;
                    before = inHead ? RevisionSide.Head(gitPath, L.T("Last Commit")) : null;
                    after = RevisionSide.Worktree(_path, L.T("Now"));
                }
                else if (_withCurrent && exists)
                {
                    before = RevisionSide.Commit(e.Commit.Sha, gitPath, e.Commit.ShortSha);
                    after = RevisionSide.Worktree(_path, L.T("Now"));
                }
                else
                {
                    var parent = e.Commit.Parents.Length > 0 ? e.Commit.Parents[0] : null;
                    before = parent != null ? RevisionSide.Commit(parent, gitPath, L.F("Before {0}", e.Commit.ShortSha)) : null;
                    after = RevisionSide.Commit(e.Commit.Sha, gitPath, e.Commit.ShortSha);
                }
            }

            _diff.ShowCompare(_path, before, after);
            _pane.ShowVersions(_path, before, after);
            UpdateButtons();
        }

        private RevisionSide SideOf(Entry e, string gitPath)
        {
            return e.Worktree
                ? RevisionSide.Worktree(_path, L.T("Now"))
                : RevisionSide.Commit(e.Commit.Sha, gitPath, e.Commit.ShortSha);
        }

        private void ToggleCurrent()
        {
            _withCurrent = !_withCurrent;
            ShowSelection();
        }

        private void UpdateButtons()
        {
            if (_current == null) return;

            var selected = _list != null ? Selected() : new List<Entry>();
            _current.EnableInClassList("act--primary", _withCurrent);
            _restore.SetEnabled(selected.Count == 1 && !selected[0].Worktree);
        }

        // ----------------------------------------------------------- возврат ---

        private async void Restore()
        {
            var selected = Selected();
            if (selected.Count != 1 || selected[0].Worktree) return;

            var commit = selected[0].Commit;
            if (!EditorUtility.DisplayDialog(L.T("Restore Version"),
                    L.F("“{0}” will become as it was in commit {1} “{2}”.\n\n" +
                        "Current changes to this file will be lost. The result will appear in “Changes” — you can commit or discard it.",
                        _path, commit.ShortSha, commit.Subject),
                    L.T("Restore"), L.T("Cancel")))
                return;

            if (_path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) && !GitConflicts.ConfirmSceneWrite(_path)) return;

            var gitPath = GitRepository.ToGitPath(_path);
            _status.text = L.F("Restoring version {0}…", commit.ShortSha);

            var result = await GitOperations.Git("restore --source=" + GitOperations.Q(commit.Sha) + " --worktree -- " + GitOperations.Q(gitPath));
            if (!result.Ok)
            {
                _status.text = L.F("Not restored: {0}", result.Message);
                return;
            }

            // Меты в том коммите могло не быть — тогда остаётся текущая.
            await GitOperations.Git("restore --source=" + GitOperations.Q(commit.Sha) + " --worktree -- " + GitOperations.Q(gitPath + ".meta"));

            GitConflicts.ReloadAfterWrite(_path);
            AssetDatabase.ImportAsset(_path, ImportAssetOptions.ForceUpdate);
            await GitStatusCache.RefreshAsync();

            _withCurrent = true;
            _status.text = L.F("Version {0} was written to the project.", commit.ShortSha);
            Reload();
        }
    }
}
