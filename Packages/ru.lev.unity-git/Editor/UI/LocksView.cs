using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Lev.Git.UI
{
    /// <summary>
    /// Локи и LFS.
    ///
    /// Сверху — состояние: настроена ли блокировка файлов, проверяются ли локи
    /// при push, есть ли нескачанные файлы. Ниже — список: мои, чужие, не
    /// скачанные, с поиском и снятием нескольких сразу.
    /// </summary>
    public sealed class LocksView : VisualElement
    {
        private enum Filter { All = 0, Mine, Theirs, NotDownloaded }

        private static string[] FilterNames => new[]
        {
            L.T("All Locks"), L.Tc("locks", "Mine"), L.Tc("locks", "Theirs"), L.T("Not Downloaded from LFS")
        };

        private sealed class Row
        {
            public LfsLockInfo Lock;
            public string PointerGitPath;

            public string ProjectPath => GitRepository.ToProjectPath(Lock != null ? Lock.GitPath : PointerGitPath)
                                         ?? (Lock != null ? Lock.GitPath : PointerGitPath);
        }

        private readonly IGitHost _host;
        private readonly ListView _list = new ListView();
        private readonly List<Row> _rows = new List<Row>();
        private readonly VisualElement _detail = new VisualElement();
        private readonly VisualElement _state = new VisualElement();
        private readonly Button _lockSelected, _unlockSelected, _pullSelected;
        private readonly Label _footer;

        private Filter _filter;
        private string _search = string.Empty;
        private string _attributesState, _verifyState;
        private bool _attributesConfigured;

        public LocksView(IGitHost host)
        {
            _host = host;
            style.flexGrow = 1f;

            _state.AddToClassList("detail");
            _state.style.flexShrink = 0f;
            Add(_state);

            var split = new TwoPaneSplitView(0, 380f, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1f;
            split.style.minHeight = 0f;
            Add(split);

            var left = Ui.Box("pane");
            split.Add(left);

            var bar = Ui.Box("subbar");
            var filter = new ToolbarMenu { text = FilterNames[0] };
            for (int i = 0; i < FilterNames.Length; i++)
            {
                var f = (Filter)i;
                filter.menu.AppendAction(FilterNames[i], _ =>
                {
                    _filter = f;
                    filter.text = FilterNames[(int)f];
                    Rebuild();
                }, _ => _filter == f ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
            bar.Add(filter);

            var search = new ToolbarSearchField();
            search.style.flexGrow = 1f;
            search.RegisterValueChangedCallback(e => { _search = e.newValue ?? string.Empty; Rebuild(); });
            bar.Add(search);
            bar.Add(Ui.Action(L.T("Refresh"), Reload, L.T("Request locks from the server and re-read the LFS state")));
            left.Add(bar);

            var actions = Ui.Box("subbar");
            _lockSelected = Ui.Action(L.T("Lock Selected in Project"), LockProjectSelection,
                L.T("Lock the assets selected in the Project window"));
            _unlockSelected = Ui.Action(L.T("Unlock Selected"), UnlockSelected);
            _pullSelected = Ui.Action(L.T("Download Selected"), PullSelected);
            actions.Add(_lockSelected);
            actions.Add(_unlockSelected);
            actions.Add(_pullSelected);
            actions.Add(Ui.Spacer());
            left.Add(actions);

            _list.fixedItemHeight = 22f;
            _list.selectionType = SelectionType.Multiple;
            _list.makeItem = MakeRow;
            _list.bindItem = BindRow;
            _list.itemsSource = _rows;
            _list.AddToClassList("list");
            _list.selectionChanged += _ => { RenderDetail(); UpdateButtons(); };
            left.Add(_list);

            var foot = Ui.Box("status-line");
            _footer = Ui.Text(string.Empty, "status-line__text");
            foot.Add(_footer);
            left.Add(foot);

            var right = Ui.Box("pane", "pane--detail");
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.minHeight = 0f;
            _detail.AddToClassList("detail");
            scroll.Add(_detail);
            right.Add(scroll);
            split.Add(right);

            RegisterCallback<AttachToPanelEvent>(_ => LfsLockCache.Updated += OnCacheUpdated);
            RegisterCallback<DetachFromPanelEvent>(_ => LfsLockCache.Updated -= OnCacheUpdated);
        }

        public void Refresh()
        {
            if (!LfsLockCache.HasData) LfsLockCache.RequestRefresh();
            LoadStateAsync();
            Rebuild();
        }

        private void OnCacheUpdated()
        {
            if (panel == null) return;
            Rebuild();
            RenderState();
        }

        private void Reload()
        {
            _host.Run(L.T("Requesting locks"), async () =>
            {
                await LfsLockCache.RefreshAsync();
                await LfsLockCache.RefreshPointersAsync();
                LoadStateAsync();
                _host.SetStatus(LfsLockCache.LastError ?? L.F("Locks: {0}", LfsLockCache.Locks.All.Count), LfsLockCache.LastError != null);
            });
        }

        // --------------------------------------------------------- состояние ---

        private async void LoadStateAsync()
        {
            var text = LfsLockOps.ReadAttributes();
            _attributesConfigured = text != null &&
                (GitAttributesPlan.IsConfigured(text) || !GitAttributesPlan.Build(text, LfsLockOps.SceneLockPatterns).Changed);
            _attributesState = text == null ? L.T("not read")
                : _attributesConfigured ? L.Tc("lock setup", "configured") : L.Tc("lock setup", "not configured");
            _verifyState = await LfsLockOps.LocksVerifyAsync();
            RenderState();
        }

        private void RenderState()
        {
            _state.Clear();

            var facts = Ui.Facts();
            var locks = LfsLockCache.Locks;

            facts.Add(Ui.Fact(L.T("Locks:"), !LfsLockCache.HasData ? L.T("requesting…")
                : locks.Verified ? L.F("{0} (mine {1}, theirs {2})", locks.All.Count, locks.MineCount, locks.TheirsCount)
                : locks.All.Count.ToString()));

            if (LfsLockCache.LastRefreshUtc.HasValue)
                facts.Add(Ui.Fact(L.T("Updated:"), LockAdvice.Ago(LfsLockCache.LastRefreshUtc, DateTime.UtcNow)));

            facts.Add(Ui.Fact(L.T("File Locking:"), _attributesState ?? "…",
                _attributesConfigured ? "t-ok" : "t-warn"));
            facts.Add(Ui.Fact(L.T("Push Check:"), _verifyState == "true" ? L.Tc("push check", "enabled") : L.Tc("push check", "not enabled"),
                _verifyState == "true" ? "t-ok" : "t-warn"));
            facts.Add(Ui.Fact(L.T("Not Downloaded from LFS:"), LfsLockCache.NotDownloadedCount.ToString(),
                LfsLockCache.NotDownloadedCount > 0 ? "t-warn" : "t-dim"));
            _state.Add(facts);

            if (LfsLockCache.LastError != null) _state.Add(Ui.Banner(LfsLockCache.LastError, "warn"));

            var bar = Ui.Box("commit__bar");
            if (!_attributesConfigured || _verifyState != "true")
            {
                bar.Add(Ui.Action(L.T("Set Up Locking…"), () => _host.Run(L.T("Setting up locking"), async () =>
                {
                    var r = await LfsLockOps.SetupAsync();
                    _host.SetStatus(r.Ok ? r.StdOut : r.Message, !r.Ok);
                    LoadStateAsync();
                }), L.T("Mark LFS types, scenes and prefabs as lockable and enable lock verification on push"), true));
            }

            if (LfsLockCache.NotDownloadedCount > 0)
            {
                bar.Add(Ui.Action(L.T("Download All from LFS"), () => _host.Run(L.T("Downloading from LFS"), async () =>
                {
                    var r = await LfsLockOps.PullContentAsync(null);
                    _host.SetStatus(r.Ok ? L.T("LFS content downloaded") : r.Message, !r.Ok);
                })));
            }

            if (bar.childCount > 0) _state.Add(bar);
        }

        // ------------------------------------------------------------ список ---

        private void Rebuild()
        {
            _rows.Clear();

            if (_filter == Filter.NotDownloaded)
            {
                foreach (var p in LfsLockCache.NotDownloaded)
                    if (Matches(p)) _rows.Add(new Row { PointerGitPath = p });
                _rows.Sort((a, b) => string.Compare(a.PointerGitPath, b.PointerGitPath, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                foreach (var l in LfsLockCache.Locks.All)
                {
                    if (_filter == Filter.Mine && !l.Mine) continue;
                    if (_filter == Filter.Theirs && l.Mine) continue;
                    if (!Matches(l.GitPath) && (l.Owner ?? string.Empty).IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    _rows.Add(new Row { Lock = l });
                }
            }

            _list.itemsSource = _rows;
            _list.Rebuild();
            _footer.text = _rows.Count == 0 ? L.T("Empty") : L.F("Rows: {0}", _rows.Count);
            RenderDetail();
            UpdateButtons();
        }

        private bool Matches(string path)
        {
            return _search.Length == 0 || path.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private List<Row> Selected()
        {
            var list = new List<Row>();
            foreach (var o in _list.selectedItems) if (o is Row r) list.Add(r);
            return list;
        }

        private void UpdateButtons()
        {
            var sel = Selected();
            bool busy = _host.Busy;
            int locks = 0, pointers = 0;
            foreach (var r in sel) { if (r.Lock != null) locks++; else pointers++; }

            _lockSelected.SetEnabled(!busy && Selection.assetGUIDs != null && Selection.assetGUIDs.Length > 0);
            _unlockSelected.SetEnabled(!busy && locks > 0);
            _unlockSelected.style.display = _filter == Filter.NotDownloaded ? DisplayStyle.None : DisplayStyle.Flex;
            _pullSelected.SetEnabled(!busy && pointers > 0);
            _pullSelected.style.display = _filter == Filter.NotDownloaded ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private VisualElement MakeRow()
        {
            var row = Ui.Box("row");

            var icon = new Image();
            icon.AddToClassList("row__icon");
            icon.style.marginLeft = 6f;
            row.Add(icon);

            var dot = Ui.Box("group__dot");
            row.Add(dot);

            row.Add(Ui.Text(string.Empty, "row__name"));
            row.Add(Ui.Text(string.Empty, "row__dir"));
            row.Add(Ui.Text(string.Empty, "row__meta"));
            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            var r = _rows[index];
            var path = r.ProjectPath;

            ((Image)element.ElementAt(0)).image = Ui.AssetIcon(path);
            var dot = element.ElementAt(1);
            ((Label)element.ElementAt(2)).text = Ui.NameOf(path);

            if (r.Lock != null)
            {
                dot.style.backgroundColor = LockOverlay.ColorOf(r.Lock);
                ((Label)element.ElementAt(3)).text = r.Lock.Mine && LfsLockCache.Locks.Verified ? L.T("me") : r.Lock.Owner;
                ((Label)element.ElementAt(4)).text = LockAdvice.Ago(r.Lock.LockedAtUtc, DateTime.UtcNow);
            }
            else
            {
                dot.style.backgroundColor = new UnityEngine.Color(0.5f, 0.5f, 0.5f);
                ((Label)element.ElementAt(3)).text = Ui.DirOf(path);
                ((Label)element.ElementAt(4)).text = L.T("pointer");
            }

            element.tooltip = path;
        }

        // ------------------------------------------------------------ детали ---

        private void RenderDetail()
        {
            _detail.Clear();
            var sel = Selected();

            if (sel.Count == 0)
            {
                if (_rows.Count == 0)
                    _detail.Add(_filter == Filter.NotDownloaded
                        ? Ui.Empty(L.T("Everything is downloaded"), L.T("The working tree has no files with a pointer instead of LFS content."))
                        : Ui.Empty(L.T("No locks"),
                            L.T("A lock is taken before editing a file that git does not merge: an image, a model, a sound, the main scene. " +
                                "Right-click an asset in the Project window → Git → Lock.")));
                else
                    _detail.Add(Ui.Empty(L.T("Nothing selected"), L.T("Select a row on the left.")));
                return;
            }

            if (sel.Count > 1)
            {
                _detail.Add(Ui.Text(L.F("Selected: {0}", sel.Count), "detail__name"));
                return;
            }

            var r = sel[0];
            var path = r.ProjectPath;

            var title = Ui.Box("detail__title");
            var icon = new Image { image = Ui.AssetIcon(path) };
            icon.style.width = 20f;
            icon.style.height = 20f;
            title.Add(icon);
            title.Add(Ui.Text(Ui.NameOf(path), "detail__name"));
            _detail.Add(title);
            _detail.Add(Ui.Text(path, "detail__path"));

            var tools = Ui.Box("commit__bar");
            tools.style.marginTop = 4f;
            tools.Add(Ui.Action(L.T("Show in Project"), () =>
            {
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (obj != null) EditorGUIUtility.PingObject(obj);
            }));
            tools.Add(Ui.Action(L.T("File History"), () => GitWindow.ShowFileHistory(path)));

            if (r.Lock == null)
            {
                _detail.Add(Ui.Banner(L.T("The file is under LFS, but the working tree has a pointer instead of the content. " +
                                          "Unity sees it as a broken asset. This usually happens after a clone without LFS " +
                                          "or an interrupted pull."), "warn"));
                tools.Add(Ui.Action(L.T("Download"), () => Pull(new List<string> { r.PointerGitPath }), null, true));
                _detail.Add(tools);
                return;
            }

            var l = r.Lock;
            var card = Ui.Card(L.Tc("noun", "Lock"));
            card.Add(Ui.KeyValue(L.T("Owner"), l.Mine && LfsLockCache.Locks.Verified ? L.F("{0} (me)", l.Owner) : l.Owner,
                                 LfsLockCache.IsTheirs(l) ? "t-warn" : "t-ok"));
            card.Add(Ui.KeyValue(L.T("Locked At"), LockAdvice.Stamp(l.LockedAtUtc, DateTime.UtcNow)));
            card.Add(Ui.KeyValue(L.T("ID"), l.Id));

            var local = GitStatusCache.GetStatus(path);
            if (local != GitFileStatus.None) card.Add(Ui.KeyValueStatus(L.Tc("lock card", "Local"), local));
            _detail.Add(card);

            if (LfsLockCache.IsTheirs(l) && local != GitFileStatus.None)
                _detail.Add(Ui.Banner(L.T("The file is changed on your side, but someone else holds the lock. Agree with the owner before pushing."), "error"));

            tools.Add(Ui.Action(LfsLockCache.IsTheirs(l) ? L.T("Unlock Someone Else's Lock…") : L.T("Unlock"),
                                () => Unlock(new List<LfsLockInfo> { l })));
            _detail.Add(tools);
        }

        // ---------------------------------------------------------- действия ---

        private void LockProjectSelection()
        {
            var paths = new List<string>();
            foreach (var guid in Selection.assetGUIDs)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(p)) paths.Add(p);
            }

            _host.Run(L.T("Locking"), async () =>
            {
                var r = await LfsLockOps.LockAsync(paths);
                _host.SetStatus(r.Ok ? r.StdOut : r.Message, !r.Ok);
            });
        }

        private void UnlockSelected()
        {
            var locks = new List<LfsLockInfo>();
            foreach (var r in Selected()) if (r.Lock != null) locks.Add(r.Lock);
            Unlock(locks);
        }

        private void Unlock(List<LfsLockInfo> locks)
        {
            if (locks.Count == 0) return;
            _host.Run(L.T("Unlocking"), async () =>
            {
                var r = await LfsLockOps.UnlockLocksAsync(locks);
                _host.SetStatus(r.Ok ? r.StdOut : r.Message, !r.Ok);
            });
        }

        private void PullSelected()
        {
            var paths = new List<string>();
            foreach (var r in Selected()) if (r.PointerGitPath != null) paths.Add(r.PointerGitPath);
            Pull(paths);
        }

        private void Pull(List<string> gitPaths)
        {
            _host.Run(L.T("Downloading from LFS"), async () =>
            {
                var r = await LfsLockOps.PullContentAsync(gitPaths);
                _host.SetStatus(r.Ok ? L.T("Downloaded") : r.Message, !r.Ok);
            });
        }
    }
}
