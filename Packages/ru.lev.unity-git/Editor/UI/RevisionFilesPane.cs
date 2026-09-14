using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.UIElements;

namespace Lev.Git.UI
{
    /// <summary>
    /// Файлы между двумя коммитами: дерево слева, превью и diff справа — та же
    /// раскладка, что во вкладке «Изменения» и в журнале.
    ///
    /// Общий кирпич для интеграций: merge request, сравнение веток — везде, где
    /// нужно показать «что изменилось от сих до сих», вид один и тот же.
    /// Мета, изменённая вместе со своим ассетом, свёрнута в его строку.
    /// </summary>
    public sealed class RevisionFilesPane : VisualElement
    {
        private sealed class Row
        {
            public PathNode<GitCommitFile> Folder;
            public GitCommitFile File;
            public int Depth;
        }

        private readonly List<GitCommitFile> _files = new List<GitCommitFile>();
        private readonly List<GitCommitFile> _visible = new List<GitCommitFile>();
        private readonly HashSet<GitCommitFile> _withMeta = new HashSet<GitCommitFile>();
        private readonly List<Row> _rows = new List<Row>();
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly ListView _list = new ListView();
        private readonly Label _label;
        private readonly AssetPreviewPane _preview;
        private readonly DiffView _diff;

        private string _base, _head;
        private string _lastPath;

        public RevisionFilesPane(IGitHost host)
        {
            style.flexGrow = 1f;
            style.minHeight = 0f;

            var split = new TwoPaneSplitView(0, 260f, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1f;
            split.style.minHeight = 0f;
            Add(split);

            var filesPane = Ui.Box("pane");
            filesPane.style.minWidth = 150f;

            var bar = Ui.Box("subbar");
            _label = Ui.Text(string.Empty, "subbar__label");
            bar.Add(_label);
            filesPane.Add(bar);

            _list.fixedItemHeight = 22f;
            _list.selectionType = SelectionType.Single;
            _list.makeItem = MakeRow;
            _list.bindItem = BindRow;
            _list.itemsSource = _rows;
            _list.AddToClassList("list");
            Lev.Git.Preview.PreviewThumbnails.Watch(_list);
            _list.selectionChanged += selection =>
            {
                foreach (var o in selection)
                    if (o is Row r && r.File != null) { ShowFile(r.File); return; }
            };
            filesPane.Add(_list);

            var view = Ui.Box("pane", "pane--detail");
            view.style.minWidth = 200f;

            _preview = new AssetPreviewPane();
            _preview.style.display = DisplayStyle.None;
            view.Add(_preview);

            _diff = new DiffView(host);
            view.Add(_diff);
            _preview.AttachDiff(_diff);

            split.Add(filesPane);
            split.Add(view);
        }

        /// <summary>Diff справа — чтобы интеграция повесила на него комментарии.</summary>
        public DiffView Diff => _diff;

        public string BaseSha => _base;
        public string HeadSha => _head;

        /// <summary>Выбрать файл по пути в репозитории. false — в этом наборе такого файла нет.</summary>
        public bool ShowPath(string gitPath)
        {
            if (string.IsNullOrEmpty(gitPath)) return false;

            GitCommitFile file = null;
            foreach (var f in _files)
                if (string.Equals(f.GitPath, gitPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(f.OriginalGitPath, gitPath, StringComparison.OrdinalIgnoreCase)) { file = f; break; }
            if (file == null) return false;

            // Файл в свёрнутой папке — раскрываем путь к нему.
            var path = PathOf(file);
            bool expanded = false;
            foreach (var key in new List<string>(_collapsed))
            {
                if (!path.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase)) continue;
                _collapsed.Remove(key);
                expanded = true;
            }
            if (expanded) Rebuild();

            Select(file);
            ShowFile(file);
            return true;
        }

        public void ShowMessage(string title, string hint)
        {
            _base = _head = null;
            _files.Clear();
            Rebuild();
            _preview.Release();
            _preview.style.display = DisplayStyle.None;
            _diff.ShowMessage(title, hint);
        }

        public void SetRange(string baseSha, string headSha, List<GitCommitFile> files)
        {
            bool sameRange = baseSha == _base && headSha == _head;
            _base = baseSha;
            _head = headSha;

            _files.Clear();
            if (files != null) _files.AddRange(files);
            Rebuild();

            if (_files.Count == 0)
            {
                _preview.Release();
                _preview.style.display = DisplayStyle.None;
                _diff.ShowMessage(L.T("No Changes"), L.T("Files do not differ between these commits."));
                return;
            }

            var target = (sameRange ? FindByPath(_lastPath) : null) ?? FirstFile();
            Select(target);
            ShowFile(target);
        }

        // ------------------------------------------------------------ строки ---

        private static string PathOf(GitCommitFile f)
        {
            return f.ProjectPath ?? f.GitPath;
        }

        private void Rebuild()
        {
            _rows.Clear();
            ComputeVisible();

            int hidden = _files.Count - _visible.Count;
            _label.text = _files.Count == 0 ? string.Empty
                : hidden > 0 ? L.F("Files: {0}  (+{1} .meta)", _visible.Count, hidden)
                : L.F("Files: {0}", _files.Count);

            AddNode(PathTree.Build(_visible, PathOf), 0);
            _list.itemsSource = _rows;
            _list.Rebuild();
        }

        private void AddNode(PathNode<GitCommitFile> node, int depth)
        {
            foreach (var f in node.Folders)
            {
                _rows.Add(new Row { Folder = f, Depth = depth });
                if (!_collapsed.Contains(f.Path)) AddNode(f, depth + 1);
            }

            var files = new List<GitCommitFile>(node.Files);
            files.Sort((a, b) => string.Compare(PathOf(a), PathOf(b), StringComparison.OrdinalIgnoreCase));
            foreach (var f in files) _rows.Add(new Row { File = f, Depth = depth });
        }

        /// <summary>Мета, у которой в том же наборе есть пара — ассет или файлы папки, — прячется в строку пары.</summary>
        private void ComputeVisible()
        {
            _visible.Clear();
            _withMeta.Clear();

            var byPath = new Dictionary<string, GitCommitFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in _files) byPath[PathOf(f)] = f;

            foreach (var f in _files)
            {
                var path = PathOf(f);
                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    var owner = path.Substring(0, path.Length - ".meta".Length);
                    if (byPath.TryGetValue(owner, out var asset)) { _withMeta.Add(asset); continue; }
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

        private GitCommitFile FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var f in _visible)
                if (string.Equals(PathOf(f), path, StringComparison.OrdinalIgnoreCase)) return f;
            return null;
        }

        private GitCommitFile FirstFile()
        {
            foreach (var r in _rows) if (r.File != null) return r.File;
            return _visible.Count > 0 ? _visible[0] : null;
        }

        private void Select(GitCommitFile file)
        {
            if (file == null) return;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].File != file) continue;
                _list.SetSelectionWithoutNotify(new[] { i });
                _list.ScrollToItem(i);
                return;
            }
        }

        private void ShowFile(GitCommitFile file)
        {
            if (file == null || _base == null || _head == null) return;
            _lastPath = PathOf(file);
            _preview.ShowRange(_base, _head, file);
            _diff.ShowRange(_base, _head, file);
        }

        private sealed class Widgets
        {
            public VisualElement FolderBox, FileBox;
            public Label FolderArrow, FolderName, FolderCount, Name, Meta;
            public Image FolderIcon, Icon, IconBefore;
            public StatusBadge Badge;
            public Row Bound;
        }

        private VisualElement MakeRow()
        {
            var wrap = new VisualElement();
            var w = new Widgets();

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
                if (!_collapsed.Add(key)) _collapsed.Remove(key);
                _list.schedule.Execute(Rebuild);
                e.StopPropagation();
            });
            wrap.Add(w.FolderBox);

            w.FileBox = Ui.Box("row");
            w.Badge = new StatusBadge();
            w.Icon = new Image();
            w.Icon.AddToClassList("row__icon");
            w.IconBefore = new Image();
            w.IconBefore.AddToClassList("row__icon");
            w.IconBefore.AddToClassList("row__icon--before");
            w.Name = Ui.Text(string.Empty, "row__name");
            w.Meta = Ui.Text(string.Empty, "row__meta");
            w.FileBox.Add(w.Badge);
            w.FileBox.Add(w.IconBefore);
            w.FileBox.Add(w.Icon);
            w.FileBox.Add(w.Name);
            w.FileBox.Add(w.Meta);
            w.FileBox.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                var f = w.Bound != null ? w.Bound.File : null;
                if (f == null) return;
                if (f.ProjectPath != null)
                {
                    evt.menu.AppendAction(L.T("File History"), _ => GitWindow.ShowFileHistory(f.ProjectPath));
                    evt.menu.AppendAction(L.T("Asset History"), _ => AssetHistoryWindow.Open(f.ProjectPath, false));
                    evt.menu.AppendAction(L.T("Show in Project"), _ =>
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(f.ProjectPath);
                        if (obj != null) EditorGUIUtility.PingObject(obj);
                    });
                    evt.menu.AppendSeparator();
                }
                evt.menu.AppendAction(L.T("Copy Path"), _ => EditorGUIUtility.systemCopyBuffer = PathOf(f));
            }));
            wrap.Add(w.FileBox);

            wrap.userData = w;
            return wrap;
        }

        private void BindRow(VisualElement element, int index)
        {
            var w = (Widgets)element.userData;
            var row = _rows[index];
            w.Bound = row;

            bool folder = row.Folder != null;
            w.FolderBox.style.display = folder ? DisplayStyle.Flex : DisplayStyle.None;
            w.FileBox.style.display = folder ? DisplayStyle.None : DisplayStyle.Flex;

            if (folder)
            {
                bool collapsed = _collapsed.Contains(row.Folder.Path);
                w.FolderArrow.text = collapsed ? "▸" : "▾";
                w.FolderIcon.image = EditorGUIUtility.IconContent(collapsed ? "Folder Icon" : "FolderOpened Icon").image;
                w.FolderName.text = row.Folder.Name;
                var inside = new List<GitCommitFile>();
                row.Folder.Collect(inside);
                w.FolderCount.text = inside.Count.ToString();
                w.FolderBox.style.paddingLeft = row.Depth * 12f;
                return;
            }

            var f = row.File;
            w.Badge.Set(StatusOf(f.Status));

            UnityEngine.Texture after = null, before = null;
            if (_base != null && _head != null && f.ProjectPath != null && Lev.Git.Preview.PreviewThumbnails.Enabled(f.ProjectPath))
            {
                if (f.Status != GitCommitFileStatus.Deleted)
                    after = Lev.Git.Preview.PreviewThumbnails.ForCommit(_head, f.ProjectPath, f.GitPath);
                if (f.Status != GitCommitFileStatus.Added)
                    before = Lev.Git.Preview.PreviewThumbnails.ForCommit(_base, f.ProjectPath, f.OriginalGitPath ?? f.GitPath);
            }

            w.Icon.image = after ?? (f.Status == GitCommitFileStatus.Deleted ? before : null) ?? Ui.AssetIcon(PathOf(f));
            bool pair = after != null && before != null;
            w.IconBefore.image = pair ? before : null;
            w.IconBefore.style.display = pair ? DisplayStyle.Flex : DisplayStyle.None;
            w.Name.text = Ui.NameOf(PathOf(f));
            w.Meta.text = _withMeta.Contains(f) ? "+meta" : string.Empty;
            w.FileBox.style.paddingLeft = row.Depth * 12f;
            w.FileBox.tooltip = f.OriginalPath != null ? PathOf(f) + "\n" + L.F("renamed from {0}", f.OriginalPath) : PathOf(f);
        }

        private static GitFileStatus StatusOf(GitCommitFileStatus s)
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
    }
}
