using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Lev.Git.UI;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git.GitLab
{
    /// <summary>
    /// Задачи проекта: список с отбором, карточка, ветка из задачи по шаблону
    /// проекта, создание задачи со снимком окна Game или Scene.
    ///
    /// Настроек здесь нет — они в Project Settings → Git → GitLab.
    /// </summary>
    public sealed class IssuesView : VisualElement
    {
        private const int FilterCount = 4;

        private static string FilterName(IssueListFilter filter)
        {
            switch (filter)
            {
                case IssueListFilter.AssignedToMe: return L.T("Assigned to Me");
                case IssueListFilter.CreatedByMe: return L.T("Created by Me");
                case IssueListFilter.AllOpen: return L.T("All Open");
                default: return L.Tc("issue filter", "Closed");
            }
        }

        private readonly IGitHost _host;
        private readonly ListView _list = new ListView();
        private readonly List<GlIssue> _items = new List<GlIssue>();
        private readonly VisualElement _banner = new VisualElement();
        private readonly VisualElement _card = new VisualElement();
        private readonly Label _footer;
        private readonly ToolbarMenu _filterMenu;

        private IssueListFilter _filter;
        private string _search = string.Empty;
        private GlUserRef _me;
        private string _error;
        private bool _needsToken, _loaded, _loading;

        private GlIssue _selected;
        private List<GlMr> _related = new List<GlMr>();
        private string _existingBranch;
        private string _renderedBranch;
        private int _request;
        private int _pendingIid;

        /// <summary>Задачу открыли из MR: к нему можно вернуться кнопкой, пока выбрана эта задача.</summary>
        private int _returnMr, _returnIssue;

        /// <summary>Просили показать задачу, которой нет под текущим отбором, — подгрузить её отдельно.</summary>
        private bool _forceFind;

        private readonly DiscussionsView _discussions;
        private readonly ScrollView _cardScroll;
        private readonly Button _modeCard, _modeDiscussions;

        public IssuesView(IGitHost host)
        {
            _host = host;
            style.flexGrow = 1f;
            style.minHeight = 0f;
            _filter = (IssueListFilter)Mathf.Clamp(GitLabSettings.instance.defaultIssueFilter, 0, FilterCount - 1);

            // ---- панель ----
            var bar = Ui.Box("subbar");
            _filterMenu = new ToolbarMenu { text = FilterName(_filter) };
            for (int i = 0; i < FilterCount; i++)
            {
                var f = (IssueListFilter)i;
                _filterMenu.menu.AppendAction(FilterName(f), _ =>
                {
                    _filter = f;
                    _filterMenu.text = FilterName(f);
                    Load();
                }, _ => _filter == f ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }
            bar.Add(_filterMenu);

            var search = new ToolbarSearchField();
            search.style.flexGrow = 1f;
            search.tooltip = L.T("Search in title and description — Enter");
            search.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                _search = search.value ?? string.Empty;
                Load();
            });
            search.RegisterValueChangedCallback(e =>
            {
                if (string.IsNullOrEmpty(e.newValue) && _search.Length > 0) { _search = string.Empty; Load(); }
            });
            bar.Add(search);

            bar.Add(Ui.Action(L.T("Refresh"), Load));
            bar.Add(Ui.Action(L.T("New Issue…"), CreateIssueWindow.Open, L.T("Issue with a screenshot of the Game or Scene view")));
            bar.Add(Ui.Action("⚙", () => SettingsService.OpenProjectSettings(GitLabIntegration.SettingsPagePath), "Project Settings → Git → GitLab"));
            Add(bar);

            _banner.style.flexShrink = 0f;
            Add(_banner);

            // ---- список и карточка ----
            var split = new TwoPaneSplitView(0, 320f, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1f;
            split.style.minHeight = 0f;
            Add(split);

            var left = Ui.Box("pane");
            left.style.minWidth = 220f;
            _list.fixedItemHeight = 42f;
            _list.selectionType = SelectionType.Single;
            _list.makeItem = MakeRow;
            _list.bindItem = BindRow;
            _list.itemsSource = _items;
            _list.AddToClassList("list");
            _list.selectionChanged += sel =>
            {
                foreach (var o in sel) if (o is GlIssue issue) { Select(issue); return; }
            };
            left.Add(_list);

            var foot = Ui.Box("status-line");
            _footer = Ui.Text(string.Empty, "status-line__text");
            foot.Add(_footer);
            left.Add(foot);
            split.Add(left);

            var right = Ui.Box("pane", "pane--detail");
            right.style.minWidth = 260f;
            var modes = Ui.Box("modebar");
            _modeCard = Ui.Action(L.T("Issue"), () => SetRightMode(false), L.T("Description, assignee, branch, related MRs"));
            _modeDiscussions = Ui.Action(L.T("Threads"), () => SetRightMode(true), L.T("Issue comments and replies to them"));
            modes.Add(_modeCard);
            modes.Add(_modeDiscussions);
            right.Add(modes);

            _cardScroll = new ScrollView(ScrollViewMode.Vertical);
            _cardScroll.style.flexGrow = 1f;
            _card.AddToClassList("detail");
            _cardScroll.Add(_card);
            right.Add(_cardScroll);

            _discussions = new DiscussionsView(host, () => Client(out _));
            _discussions.Changed += UpdateModeLabels;
            right.Add(_discussions);
            split.Add(right);
            SetRightMode(false);

            RegisterCallback<AttachToPanelEvent>(_ => GitLabEvents.IssuesChanged += OnIssuesChanged);
            RegisterCallback<DetachFromPanelEvent>(_ => GitLabEvents.IssuesChanged -= OnIssuesChanged);

            RenderCard();
        }

        public void Refresh()
        {
            if (GitLabEvents.PendingOpenIssueIid > 0)
            {
                OnOpenRequested(GitLabEvents.PendingOpenIssueIid, GitLabEvents.PendingOpenIssueFromMr);
                return;
            }

            if (!_loaded && !_loading) { Load(); return; }

            RenderBanner();
            // Переключились на ветку задачи или ушли с неё — кнопки в карточке другие.
            if (_renderedBranch != GitStatusCache.Branch) { RenderCard(); _list.RefreshItems(); }
        }

        /// <summary>Показать задачу по номеру — выбрать в списке или подгрузить, не сбивая отбор.</summary>
        private void OnOpenRequested(int iid, int fromMr)
        {
            GitLabEvents.PendingOpenIssueIid = 0;
            GitLabEvents.PendingOpenIssueFromMr = 0;

            _returnMr = fromMr;
            _returnIssue = iid;
            SetRightMode(false);
            RenderBanner();

            if (_loading)
            {
                schedule.Execute(() => OnOpenRequested(iid, fromMr)).StartingIn(250);
                return;
            }

            int index = _items.FindIndex(i => i.Iid == iid);
            if (index >= 0)
            {
                SelectIndex(index);
                return;
            }

            _pendingIid = iid;
            _forceFind = true;
            Load();
        }

        /// <summary>
        /// Выбрать строку так, чтобы карточка обновилась наверняка. ListView не
        /// сообщает о выборе, если индекс не изменился, — а после смены отбора под
        /// тем же индексом стоит уже другая задача, и карточка оставалась пустой.
        /// </summary>
        private void SelectIndex(int index)
        {
            _list.SetSelectionWithoutNotify(Array.Empty<int>());
            _list.SetSelection(index);
            _list.ScrollToItem(index);
        }

        private void SetRightMode(bool discussions)
        {
            _cardScroll.style.display = discussions ? DisplayStyle.None : DisplayStyle.Flex;
            _discussions.style.display = discussions ? DisplayStyle.Flex : DisplayStyle.None;
            _modeCard.EnableInClassList("act--primary", !discussions);
            _modeDiscussions.EnableInClassList("act--primary", discussions);
            UpdateModeLabels();
        }

        private void UpdateModeLabels()
        {
            int total = _discussions.ThreadCount;
            _modeDiscussions.text = total > 0 ? L.F("Threads · {0}", total) : L.T("Threads");
        }

        private void OnIssuesChanged(int iid)
        {
            if (panel == null) return;
            _pendingIid = iid;
            Load();
        }

        private GitLabClient Client(out string problem)
        {
            problem = null;
            var baseUrl = GitLabInstance.EffectiveBaseUrl;
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(GitLabInstance.EffectiveEncodedId))
            {
                problem = L.T("The instance address or project path is not set — set them in Project Settings → Git → GitLab.");
                return null;
            }

            var token = GitLabToken.Read(baseUrl);
            if (string.IsNullOrEmpty(token)) { _needsToken = true; return null; }
            return new GitLabClient(baseUrl, token);
        }

        // ------------------------------------------------------------ загрузка ---

        private async void Load()
        {
            if (_loading) return;
            _loading = true;
            _loaded = true;
            _error = null;
            _needsToken = false;
            _footer.text = L.T("Loading…");

            try
            {
                var client = Client(out var problem);
                if (client == null) { _error = problem; return; }

                if (_me == null)
                {
                    var user = await client.GetAsync("/user");
                    if (user.Code == 401) { _needsToken = true; return; }
                    if (!user.Ok) { _error = user.Error; return; }
                    _me = GitLabModels.ParseUser(user.Body);
                }

                var (items, error) = await client.ListIssuesAsync(GitLabInstance.EffectiveEncodedId, _filter, _search);
                if (error != null) { _error = error; return; }

                var keep = _pendingIid > 0 ? _pendingIid : (_selected != null ? _selected.Iid : 0);
                _pendingIid = 0;

                // Задачи нет под этим отбором (закрыта, назначена не мне) — ставим её первой,
                // а отбор не трогаем: вернувшись к списку, человек увидит тот же.
                if (_forceFind && keep > 0 && !items.Exists(i => i.Iid == keep))
                {
                    var (extra, _) = await client.GetIssueAsync(GitLabInstance.EffectiveEncodedId, keep);
                    if (extra != null) items.Insert(0, extra);
                }
                _forceFind = false;

                _items.Clear();
                _items.AddRange(items);
                _list.itemsSource = _items;
                _list.Rebuild();

                // Первой — задача текущей ветки: над ней, скорее всего, и работают.
                int index = _items.FindIndex(i => i.Iid == keep);
                var branchIssue = GitLabWorkflow.IssueFromBranch(GitStatusCache.Branch);
                if (index < 0 && branchIssue > 0) index = _items.FindIndex(i => i.Iid == branchIssue);
                if (index < 0 && _items.Count > 0) index = 0;

                if (index >= 0)
                {
                    SelectIndex(index);
                }
                else
                {
                    _list.SetSelectionWithoutNotify(Array.Empty<int>());
                    _selected = null;
                    RenderCard();
                    _discussions.SetIssue(null, false);
                }
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
            finally
            {
                _loading = false;
                _footer.text = _error != null || _needsToken ? string.Empty
                    : FilterName(_filter) + ": " + _items.Count + (_items.Count >= 50 ? " " + L.T("(first 50 shown)") : string.Empty);
                RenderBanner();
            }
        }

        private void RenderBanner()
        {
            _banner.Clear();

            if (_needsToken)
            {
                var b = Ui.Banner(L.T("The API token for this instance is not set or was not accepted. It is separate from the git " +
                                      "credentials for push and is stored only on this computer."), "warn");
                var set = Ui.Action(L.T("Set Token…"),() => SettingsService.OpenProjectSettings(GitLabIntegration.SettingsPagePath), null, true);
                set.style.marginLeft = 8f;
                b.Add(set);
                _banner.Add(b);
                return;
            }

            if (!string.IsNullOrEmpty(_error)) _banner.Add(Ui.Banner(_error, "error"));

            if (_returnMr > 0)
            {
                var mr = _returnMr;
                var back = Ui.Banner(L.F("Issue #{0} was opened from merge request !{1}.", _returnIssue, mr), "info");
                var button = Ui.Action(L.F("← Back to !{0}", mr), () =>
                {
                    _returnMr = 0;
                    RenderBanner();
                    GitLabEvents.OpenMergeRequest(mr);
                }, L.T("The MR tab will open on the same merge request"), true);
                button.style.marginLeft = 8f;
                back.Add(button);
                _banner.Add(back);
            }
        }

        private async void Select(GlIssue issue)
        {
            int request = ++_request;
            _selected = issue;

            // Выбрали другую задачу — возврат к MR больше не про неё.
            if (_returnMr > 0 && issue.Iid != _returnIssue)
            {
                _returnMr = 0;
                RenderBanner();
            }
            _related = new List<GlMr>();
            _existingBranch = null;
            RenderCard();
            _discussions.SetIssue(issue, true);

            var client = Client(out _);
            if (client == null) return;

            var encoded = GitLabInstance.EffectiveEncodedId;
            var detailTask = client.GetIssueAsync(encoded, issue.Iid);
            var relatedTask = client.RelatedMergeRequestsAsync(encoded, issue.Iid);
            var branchTask = GitLabIssueGit.ExistingBranchAsync(issue.Iid);

            var (detail, _) = await detailTask;
            var related = await relatedTask;
            var branch = await branchTask;
            if (request != _request) return;

            if (detail != null) _selected = detail;
            _related = related;
            _existingBranch = branch;
            RenderCard();
        }

        // -------------------------------------------------------------- список ---

        private static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.style.paddingLeft = 8f;
            row.style.paddingRight = 6f;
            row.style.paddingTop = 3f;
            row.style.justifyContent = Justify.Center;

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;

            var dot = Ui.Text("●");
            dot.style.fontSize = 10f;
            dot.style.marginRight = 4f;
            top.Add(dot);

            // Длинное название обрезается многоточием, а значки справа остаются
            // видны целиком: иначе значок ложился поверх текста.
            var title = Ui.Text(string.Empty, "row__name");
            title.style.flexGrow = 1f;
            title.style.flexShrink = 1f;
            title.style.minWidth = 0f;
            title.style.maxWidth = new StyleLength(StyleKeyword.None);
            title.style.overflow = Overflow.Hidden;
            title.style.whiteSpace = WhiteSpace.NoWrap;
            title.style.textOverflow = TextOverflow.Ellipsis;
            top.Add(title);

            var chips = new VisualElement();
            chips.style.flexDirection = FlexDirection.Row;
            chips.style.flexShrink = 0f;
            chips.style.marginLeft = 4f;
            top.Add(chips);

            var meta = Ui.Text(string.Empty, "row__dir");
            meta.style.marginLeft = 14f;

            row.Add(top);
            row.Add(meta);
            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            var issue = _items[index];
            var top = element.ElementAt(0);
            var dot = (Label)top.ElementAt(0);
            var title = (Label)top.ElementAt(1);
            var chips = top.ElementAt(2);
            var meta = (Label)element.ElementAt(1);

            bool current = issue.Iid == GitLabWorkflow.IssueFromBranch(GitStatusCache.Branch);
            dot.style.color = current ? GitPalette.Text(GitFileStatus.Added) : new Color(0.5f, 0.5f, 0.5f, 0.6f);
            dot.tooltip = current ? L.T("Issue of the current branch") : null;

            title.text = issue.Title;
            element.tooltip = issue.Title;

            chips.Clear();
            if (!issue.IsOpen) chips.Add(Chip(L.Tc("issue state", "closed"), false));
            if (issue.Confidential) chips.Add(Chip(L.T("confidential"), false));
            if (issue.Labels.Count > 0) chips.Add(Chip(issue.Labels[0] + (issue.Labels.Count > 1 ? " +" + (issue.Labels.Count - 1) : string.Empty), false));
            if (issue.UserNotesCount > 0) chips.Add(Chip("💬 " + issue.UserNotesCount, false));

            var who = issue.Assignees.Count > 0 ? Join(issue.Assignees) : L.Tc("issue", "unassigned");
            var when = LockAdvice.Ago(issue.IsOpen ? issue.UpdatedAt : issue.ClosedAt ?? issue.UpdatedAt, DateTime.UtcNow);
            meta.text = "#" + issue.Iid + " · " + who + (string.IsNullOrEmpty(when) ? string.Empty : " · " + when);
        }

        private static Label Chip(string text, bool alert)
        {
            var chip = Ui.Text(text, "tab__badge");
            if (alert) chip.AddToClassList("tab__badge--alert");
            chip.style.marginLeft = 3f;
            return chip;
        }

        // ------------------------------------------------------------ карточка ---

        private void RenderCard()
        {
            _card.Clear();
            _renderedBranch = GitStatusCache.Branch;
            var issue = _selected;

            if (issue == null)
            {
                _card.Add(Ui.Empty(L.T("No Issue Selected"), _loaded && _items.Count == 0 ? L.T("No issues match this filter.") : L.T("Select an issue in the list on the left.")));
                return;
            }

            _card.Add(Ui.Text(issue.Title, "detail__name"));
            _card.Add(Ui.Text("#" + issue.Iid + (issue.Author != null ? " · " + issue.Author : string.Empty) +
                              (issue.CreatedAt.HasValue ? " · " + L.Fc("issue", "created {0}", LockAdvice.Ago(issue.CreatedAt, DateTime.UtcNow)) : string.Empty),
                              "detail__path"));

            var facts = Ui.Facts();
            facts.Add(issue.IsOpen
                ? Ui.Fact(L.T("State:"), L.Tc("issue state", "open"), "t-ok")
                : Ui.Fact(L.T("State:"), L.Tc("issue state", "closed") + (issue.ClosedAt.HasValue ? " " + LockAdvice.Ago(issue.ClosedAt, DateTime.UtcNow) : string.Empty), "t-dim"));
            facts.Add(Ui.Fact(L.T("Assignee:"), issue.Assignees.Count > 0 ? Join(issue.Assignees) : L.Tc("issue", "unassigned"),
                              issue.Assignees.Count > 0 ? null : "t-warn"));
            if (issue.Labels.Count > 0) facts.Add(Ui.Fact(L.T("Labels:"), string.Join(", ", issue.Labels)));
            if (!string.IsNullOrEmpty(issue.Milestone)) facts.Add(Ui.Fact(L.T("Milestone:"), issue.Milestone));
            if (!string.IsNullOrEmpty(issue.DueDate)) facts.Add(Ui.Fact(L.T("Due Date:"), issue.DueDate));
            if (issue.UserNotesCount > 0) facts.Add(Ui.Fact(L.T("Threads:"), issue.UserNotesCount.ToString()));
            if (issue.Confidential) facts.Add(Ui.Fact(L.T("Visibility:"), L.T("confidential issue"), "t-warn"));

            bool onBranch = issue.Iid == GitLabWorkflow.IssueFromBranch(GitStatusCache.Branch);
            if (onBranch) facts.Add(Ui.Fact(L.T("Branch:"), L.F("{0} (current)", GitStatusCache.Branch), "t-ok"));
            else if (_existingBranch != null) facts.Add(Ui.Fact(L.T("Branch:"), _existingBranch));
            _card.Add(facts);

            // ---- действия ----
            var actions = Ui.Box("commit__bar");
            actions.style.marginTop = 4f;
            actions.style.flexWrap = Wrap.Wrap;

            if (issue.IsOpen)
            {
                if (!onBranch)
                {
                    var start = Ui.Action(_existingBranch != null ? L.T("Switch to Issue Branch") : L.T("Start Work"), StartWork,
                        _existingBranch != null
                            ? L.F("Switch to “{0}”", _existingBranch)
                            : L.T("Create a branch from the project template and switch to it"), true);
                    start.SetEnabled(!_host.Busy);
                    actions.Add(start);
                }
                else
                {
                    actions.Add(Ui.Action(L.T("Create MR…"), CreateMergeRequestWindow.Open,
                        GitLabSettings.instance.closesIssueInDescription
                            ? L.F("Merge request from the current branch — with “Closes #{0}”", issue.Iid)
                            : L.T("Merge request from the current branch"), true));
                }

                if (_me != null && !issue.Assignees.Exists(u => u.Id == _me.Id))
                    actions.Add(Ui.Action(L.T("Assign to Me"), AssignToMe));

                actions.Add(Ui.Action(L.T("Close…"), () => SetState(true), L.T("Close without a merge request")));
            }
            else
            {
                actions.Add(Ui.Action(L.T("Reopen"), () => SetState(false)));
            }

            actions.Add(Ui.Action(L.F("Copy #{0}", issue.Iid), () =>
            {
                EditorGUIUtility.systemCopyBuffer = "#" + issue.Iid;
                _host.SetStatus(L.F("Copied “#{0}” — GitLab will link the commit to the issue.", issue.Iid), false);
            }, L.T("For the commit message")));
            actions.Add(Ui.Action(L.T("Open in GitLab"), () => Application.OpenURL(issue.WebUrl)));
            _card.Add(actions);

            // ---- связанные MR ----
            if (_related.Count > 0)
            {
                var head = Ui.Text(L.T("Merge Requests"), "detail__path");
                head.style.marginTop = 8f;
                _card.Add(head);

                foreach (var mr in _related)
                {
                    var iid = mr.Iid;
                    var row = Ui.Box("commit__bar");
                    row.style.alignItems = Align.Center;
                    row.style.justifyContent = Justify.FlexStart;
                    row.Add(Ui.Action("!" + mr.Iid, () => GitLabEvents.OpenMergeRequest(iid), L.T("Open in the MR tab")));
                    var label = Ui.Text(mr.Title + " · " + MrState(mr) + " · " + mr.SourceBranch, "banner__text");
                    label.style.marginLeft = 6f;
                    row.Add(label);
                    _card.Add(row);
                }
            }

            // ---- описание ----
            if (!string.IsNullOrWhiteSpace(issue.Description))
            {
                var head = Ui.Text(L.T("Description"), "detail__path");
                head.style.marginTop = 8f;
                _card.Add(head);

                _card.Add(new GitLabMarkdownView(issue.Description, () => Client(out _), "banner__text", 8000));
            }
        }

        private static string MrState(GlMr mr)
        {
            switch (mr.State)
            {
                case "merged": return L.Tc("mr state", "merged");
                case "closed": return L.Tc("mr state", "closed");
                default: return mr.Draft ? "draft" : L.Tc("mr state", "open");
            }
        }

        // ------------------------------------------------------------ действия ---

        private void StartWork()
        {
            var issue = _selected;
            if (issue == null) return;
            var s = GitLabSettings.instance;

            _host.Run(L.F("Branch for Issue #{0}", issue.Iid), async () =>
            {
                var client = Client(out var problem);
                if (client == null) { _host.SetStatus(problem ?? L.T("The API token is not set."), true); return; }
                var encoded = GitLabInstance.EffectiveEncodedId;

                var existing = await GitLabIssueGit.ExistingBranchAsync(issue.Iid);
                var name = existing;
                if (name == null)
                {
                    var suggested = GitLabIssueGit.BranchNameFor(issue, _me != null ? _me.Username : "user");
                    name = TextPromptWindow.Ask(L.F("Branch for Issue #{0}", issue.Iid),
                        L.T("The name follows the project template — you can edit it. The template is set in Project Settings → Git → GitLab."),
                        L.T("Branch Name"), suggested);
                    if (string.IsNullOrEmpty(name)) { _host.SetStatus(L.T("Branch creation canceled."), false); return; }
                }

                string baseBranch = null;
                if (existing == null && (IssueBranchBase)s.issueBranchBase == IssueBranchBase.ProjectDefault)
                {
                    var (project, error) = await client.GetProjectInfoAsync(encoded);
                    if (project == null || string.IsNullOrEmpty(project.DefaultBranch))
                    {
                        _host.SetStatus(error ?? L.T("The project's default branch is unknown."), true);
                        return;
                    }
                    baseBranch = project.DefaultBranch;
                }

                var r = await GitLabIssueGit.StartBranchAsync(name, baseBranch, existing == null && s.issueBranchPush);
                await GitStatusCache.RefreshAsync();
                if (!r.Ok) { _host.SetStatus(r.Message, true); RenderCard(); return; }

                var note = string.Empty;
                if (s.issueAssignSelf && issue.Assignees.Count == 0 && _me != null)
                {
                    var (_, assignError) = await client.AssignIssueAsync(encoded, issue.Iid, new List<long> { _me.Id });
                    note = " " + (assignError ?? L.T("The issue is assigned to you."));
                }

                _host.SetStatus(L.F("Current branch: {0}.", name) + note, false);
                _pendingIid = issue.Iid;
                Load();
            });
        }

        private void AssignToMe()
        {
            var issue = _selected;
            if (issue == null || _me == null) return;

            var ids = new List<long> { _me.Id };
            foreach (var u in issue.Assignees) if (u.Id != _me.Id) ids.Add(u.Id);

            RunOnIssue(L.T("Assigning Issue"), async client =>
            {
                var (_, error) = await client.AssignIssueAsync(GitLabInstance.EffectiveEncodedId, issue.Iid, ids);
                return (error ?? L.F("#{0} is assigned to you.", issue.Iid), error != null);
            }, issue.Iid);
        }

        private void SetState(bool close)
        {
            var issue = _selected;
            if (issue == null) return;

            if (close && !EditorUtility.DisplayDialog(L.T("Close Issue"),
                    L.F("Close #{0} “{1}”? It can be reopened later.", issue.Iid, issue.Title), L.T("Close"), L.T("Cancel")))
                return;

            RunOnIssue(close ? L.T("Closing Issue") : L.T("Reopening Issue"), async client =>
            {
                var (_, error) = await client.SetIssueStateAsync(GitLabInstance.EffectiveEncodedId, issue.Iid, close);
                return (error ?? (close ? L.F("#{0} is closed.", issue.Iid) : L.F("#{0} is reopened.", issue.Iid)), error != null);
            }, close ? 0 : issue.Iid);
        }

        private void RunOnIssue(string title, Func<GitLabClient, Task<(string message, bool error)>> action, int selectAfter)
        {
            _host.Run(title, async () =>
            {
                var client = Client(out var problem);
                if (client == null) { _host.SetStatus(problem ?? L.T("The API token is not set."), true); return; }

                var (message, error) = await action(client);
                _host.SetStatus(message, error);

                _pendingIid = selectAfter;
                Load();
            });
        }

        private static string Join(List<GlUserRef> users)
        {
            var sb = new StringBuilder();
            foreach (var u in users)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(u);
            }
            return sb.ToString();
        }
    }
}
