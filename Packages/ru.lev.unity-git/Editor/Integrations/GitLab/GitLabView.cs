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
    /// Вкладка GitLab: merge request'ы проекта.
    ///
    /// Слева список с отбором «мои / ждут моего ревью / все», справа карточка:
    /// можно ли слить и почему нет, кто одобрил, и изменения MR в том же виде,
    /// что во вкладке «Изменения», — дерево, превью, diff, сцены объектами.
    /// Изменения считаются локальным git по коммитам MR: так работают
    /// семантический diff и превью, которых нет в веб-интерфейсе.
    ///
    /// Настроек здесь нет — они в Project Settings → Git → GitLab.
    /// </summary>
    public sealed class GitLabView : VisualElement
    {
        private const int FilterCount = 5;

        private static string FilterName(MrListFilter filter)
        {
            switch ((int)filter)
            {
                case 0: return L.Tc("mr list filter", "Mine");
                case 1: return L.Tc("mr list filter", "Awaiting My Review");
                case 2: return L.Tc("mr list filter", "All Open");
                case 3: return L.Tc("mr list filter", "Merged");
                default: return L.Tc("mr list filter", "Closed");
            }
        }

        private readonly IGitHost _host;
        private readonly ListView _list = new ListView();
        private readonly List<GlMr> _items = new List<GlMr>();
        private readonly VisualElement _banner = new VisualElement();
        private readonly VisualElement _card = new VisualElement();
        private readonly RevisionFilesPane _files;
        private readonly Label _footer;
        private readonly ToolbarMenu _filterMenu;

        private MrListFilter _filter;
        private string _search = string.Empty;
        private string _username;
        private string _error;
        private bool _needsToken;
        private bool _loaded;
        private bool _loading;

        private GlMr _selected;
        private GlApprovals _approvals;
        private string _filesProblem;
        private int _request;
        private int _pendingIid;

        /// <summary>Файлы MR — чтобы после слияния снять свои локи на них.</summary>
        private List<string> _rangePaths = new List<string>();

        private readonly DiscussionsView _discussions;

        /// <summary>Задачи MR: те, что он закроет, упомянутые в описании и задача ветки.</summary>
        private List<GlIssue> _linkedIssues = new List<GlIssue>();
        private readonly Button _modeFiles, _modeDiscussions;

        /// <summary>Просили показать конкретный MR: если его нет под текущим отбором — сменить отбор.</summary>
        private bool _forceFind;

        /// <summary>Обсуждения по привязке — чтобы diff быстро находил свои пометки.</summary>
        private readonly List<GlDiscussion> _lineThreads = new List<GlDiscussion>();
        private readonly Dictionary<string, List<GlDiscussion>> _objectThreads = new Dictionary<string, List<GlDiscussion>>();

        public GitLabView(IGitHost host)
        {
            _host = host;
            style.flexGrow = 1f;
            _filter = (MrListFilter)Mathf.Clamp(GitLabSettings.instance.defaultListFilter, 0, FilterCount - 1);

            // ---- панель ----
            var bar = Ui.Box("subbar");
            _filterMenu = new ToolbarMenu { text = FilterName(_filter) };
            for (int i = 0; i < FilterCount; i++)
            {
                var f = (MrListFilter)i;
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
            search.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                _search = search.value ?? string.Empty;
                Load();
            });
            search.RegisterValueChangedCallback(e =>
            {
                // Очистили поле крестиком — сразу возвращаем полный список.
                if (string.IsNullOrEmpty(e.newValue) && _search.Length > 0) { _search = string.Empty; Load(); }
            });
            search.tooltip = L.T("Search in title and description — press Enter");
            bar.Add(search);

            bar.Add(Ui.Action(L.T("Refresh"), Load));
            bar.Add(Ui.Action(L.T("Create MR…"), CreateMergeRequestWindow.Open, L.T("Merge request from the current branch")));
            bar.Add(Ui.Action("⚙", OpenSettings, "Project Settings → Git → GitLab"));
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
                foreach (var o in sel) if (o is GlMr mr) { Select(mr); return; }
            };
            left.Add(_list);

            var foot = Ui.Box("status-line");
            _footer = Ui.Text(string.Empty, "status-line__text");
            foot.Add(_footer);
            left.Add(foot);
            split.Add(left);

            var right = Ui.Box("pane", "pane--detail");
            right.style.minWidth = 260f;

            var cardScroll = new ScrollView(ScrollViewMode.Vertical);
            cardScroll.style.flexShrink = 0f;
            cardScroll.style.maxHeight = Length.Percent(42f);
            _card.AddToClassList("detail");
            cardScroll.Add(_card);
            right.Add(cardScroll);

            // Изменения и обсуждения — на одном месте, переключателем: из обсуждения
            // удобно перейти к строке, а из пометки у строки — к обсуждению.
            var modes = Ui.Box("modebar");
            _modeFiles = Ui.Action(L.T("Changes"), () => SetRightMode(false), L.T("MR files: tree, preview, diff. Right-click a line or object to comment"));
            _modeDiscussions = Ui.Action(L.T("Threads"), () => SetRightMode(true), L.T("Review comments, replies, resolving threads"));
            modes.Add(_modeFiles);
            modes.Add(_modeDiscussions);
            right.Add(modes);

            var host2 = new VisualElement();
            host2.style.flexGrow = 1f;
            host2.style.minHeight = 0f;

            _files = new RevisionFilesPane(host);
            host2.Add(_files);

            _discussions = new DiscussionsView(host, () => Client(out _));
            _discussions.Changed += OnDiscussionsChanged;
            _discussions.ShowInChanges = ShowThreadInChanges;
            host2.Add(_discussions);

            right.Add(host2);
            split.Add(right);

            HookDiff(_files.Diff);
            SetRightMode(false);

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                GitLabEvents.MergeRequestsChanged += OnMergeRequestsChanged;
                GitLabEvents.OpenMergeRequestRequested += OnOpenRequested;
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                GitLabEvents.MergeRequestsChanged -= OnMergeRequestsChanged;
                GitLabEvents.OpenMergeRequestRequested -= OnOpenRequested;
            });

            RenderCard();
        }

        public void Refresh()
        {
            if (GitLabEvents.PendingOpenIid > 0) { OnOpenRequested(GitLabEvents.PendingOpenIid); return; }
            if (!_loaded && !_loading) Load();
            else RenderBanner();
        }

        /// <summary>Показать MR по номеру: выбрать в списке, а если его там нет — найти, сменив отбор.</summary>
        private void OnOpenRequested(int iid)
        {
            GitLabEvents.PendingOpenIid = 0;
            if (_loading)
            {
                schedule.Execute(() => OnOpenRequested(iid)).StartingIn(250);
                return;
            }

            int index = _items.FindIndex(m => m.Iid == iid);
            if (index >= 0)
            {
                SelectIndex(index);
                return;
            }

            _pendingIid = iid;
            _forceFind = true;
            Load();
        }

        private static void OpenSettings()
        {
            SettingsService.OpenProjectSettings(GitLabIntegration.SettingsPagePath);
        }

        /// <summary>
        /// Выбрать строку так, чтобы карточка обновилась наверняка. ListView не
        /// сообщает о выборе, если индекс не изменился, — а после смены отбора под
        /// тем же индексом стоит уже другой MR, и карточка оставалась пустой.
        /// </summary>
        private void SelectIndex(int index)
        {
            _list.SetSelectionWithoutNotify(Array.Empty<int>());
            _list.SetSelection(index);
            _list.ScrollToItem(index);
        }

        /// <summary>MR создан или изменён где-то ещё — перечитываем список и выбираем его.</summary>
        private void OnMergeRequestsChanged(int iid)
        {
            if (panel == null) return;
            _pendingIid = iid;
            Load();
        }

        // ------------------------------------------------------------ загрузка ---

        private GitLabClient Client(out string problem)
        {
            problem = null;
            var baseUrl = GitLabInstance.EffectiveBaseUrl;
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(GitLabInstance.EffectiveEncodedId))
            {
                problem = L.T("Instance URL or project path is not set — set them in Project Settings → Git → GitLab.");
                return null;
            }

            var token = GitLabToken.Read(baseUrl);
            if (string.IsNullOrEmpty(token)) { _needsToken = true; return null; }
            return new GitLabClient(baseUrl, token);
        }

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

                if (_username == null)
                {
                    var user = await client.GetAsync("/user");
                    if (user.Code == 401) { _needsToken = true; return; }
                    if (!user.Ok) { _error = user.Error; return; }
                    _username = GitLabModels.ParseUser(user.Body)?.Username;
                }

                var (items, error) = await client.ListMergeRequestsAsync(GitLabInstance.EffectiveEncodedId, _filter, _search, _username);
                if (error != null) { _error = error; return; }

                var keep = _pendingIid > 0 ? _pendingIid : (_selected != null ? _selected.Iid : 0);
                _pendingIid = 0;

                // Просили показать конкретный MR, а под этим отбором его нет (слит, закрыт, чужой) —
                // ставим его первым, а отбор не трогаем: вернувшись к списку, человек увидит тот же.
                if (_forceFind && keep > 0 && !items.Exists(m => m.Iid == keep))
                {
                    var (extra, _) = await client.GetMergeRequestAsync(GitLabInstance.EffectiveEncodedId, keep);
                    if (extra != null) items.Insert(0, extra);
                }
                _forceFind = false;

                _items.Clear();
                _items.AddRange(items);
                _list.itemsSource = _items;
                _list.Rebuild();

                // Первым выбирается MR текущей ветки — ради него обычно и открывают вкладку.
                var branch = GitStatusCache.Branch;
                int index = _items.FindIndex(m => m.Iid == keep);
                if (index < 0 && !string.IsNullOrEmpty(branch)) index = _items.FindIndex(m => m.SourceBranch == branch);
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
                    _files.ShowMessage(L.T("No MR selected"), _items.Count == 0 ? L.T("No open MRs match this filter.") : null);
                    _discussions.SetMergeRequest(null, false);
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
                    : FilterName(_filter) + ": " + _items.Count + (_items.Count >= 50 ? " " + L.T("(showing first 50)") : string.Empty);
                RenderBanner();
            }
        }

        private void RenderBanner()
        {
            _banner.Clear();

            if (_needsToken)
            {
                var b = Ui.Banner(L.T("The API token for this instance is not set or was rejected. It is separate from the git credentials " +
                                      "for push and is stored only on this computer."), "warn");
                var set = Ui.Action(L.T("Set Token…"), OpenSettings, null, true);
                set.style.marginLeft = 8f;
                b.Add(set);
                _banner.Add(b);
                return;
            }

            if (!string.IsNullOrEmpty(_error))
            {
                _banner.Add(Ui.Banner(_error, "error"));
                return;
            }

            var branch = GitStatusCache.Branch;
            if ((_filter == MrListFilter.Mine || _filter == MrListFilter.AllOpen) && !string.IsNullOrEmpty(branch) && _loaded && !_loading &&
                !_items.Exists(m => m.SourceBranch == branch) && string.IsNullOrEmpty(_search))
            {
                _banner.Add(Ui.Banner(_filter == MrListFilter.Mine
                    ? L.F("The current branch “{0}” has no open merge request of yours.", branch)
                    : L.F("The current branch “{0}” has no open merge request.", branch), "info"));
            }
        }

        private async void Select(GlMr mr)
        {
            int request = ++_request;
            _selected = mr;
            _approvals = null;
            _filesProblem = null;
            _linkedIssues = new List<GlIssue>();
            RenderCard();
            _files.ShowMessage(L.T("Reading merge request…"), null);
            _discussions.SetMergeRequest(mr, true);

            var client = Client(out _);
            if (client == null) return;

            var encoded = GitLabInstance.EffectiveEncodedId;
            var detailTask = client.GetMergeRequestAsync(encoded, mr.Iid);
            var approvalsTask = client.GetApprovalsAsync(encoded, mr.Iid);

            var (detail, error) = await detailTask;
            var approvals = await approvalsTask;
            if (request != _request) return;

            if (detail == null)
            {
                _filesProblem = error;
                RenderCard();
                _files.ShowMessage(L.T("Could not read the MR"), error);
                return;
            }

            _selected = detail;
            _approvals = approvals;
            RenderCard();
            _discussions.SetMergeRequest(detail, false);
            LoadLinkedIssues(detail, request);

            if (GitLabWorkflow.IsChecking(detail)) PollMergeStatus(detail.Iid, request);

            await LoadFilesAsync(detail, request);
        }

        /// <summary>
        /// Задачи MR. Сначала — те, что GitLab закроет при слиянии; затем
        /// упомянутые в описании «Closes #N» (старый сервер их может не
        /// сопоставить) и задача из имени ветки.
        /// </summary>
        private async void LoadLinkedIssues(GlMr mr, int request)
        {
            var client = Client(out _);
            if (client == null) return;
            var encoded = GitLabInstance.EffectiveEncodedId;

            var list = await client.ClosesIssuesAsync(encoded, mr.Iid);

            var wanted = IssueRefs.ClosingRefs(mr.Description);
            var fromBranch = GitLabWorkflow.IssueFromBranch(mr.SourceBranch);
            if (fromBranch > 0 && !wanted.Contains(fromBranch)) wanted.Add(fromBranch);

            foreach (var iid in wanted)
            {
                if (list.Count >= 6 || request != _request) break;
                if (list.Exists(i => i.Iid == iid)) continue;
                var (issue, _) = await client.GetIssueAsync(encoded, iid);
                if (issue != null) list.Add(issue);
            }

            if (request != _request) return;
            _linkedIssues = list;
            RenderCard();
        }

        /// <summary>
        /// Пока GitLab считает, можно ли слить, карточка переспрашивает сама:
        /// иначе в ней навсегда застыл бы ответ «проверяется», хотя на сервере
        /// всё давно готово. Не дольше полуминуты — дальше по кнопке «Обновить».
        /// </summary>
        private async void PollMergeStatus(int iid, int request)
        {
            var client = Client(out _);
            if (client == null) return;

            var encoded = GitLabInstance.EffectiveEncodedId;
            await client.RequestMergeStatusRecheckAsync(encoded, iid);

            for (int attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(3000);
                if (request != _request || _selected == null || _selected.Iid != iid) return;

                var (detail, _) = await client.GetMergeRequestAsync(encoded, iid);
                if (request != _request || detail == null) return;

                if (GitLabWorkflow.IsChecking(detail)) continue;

                _selected = detail;
                _approvals = await client.GetApprovalsAsync(encoded, iid) ?? _approvals;
                if (request != _request) return;

                RenderCard();
                _list.RefreshItems();
                return;
            }
        }

        /// <summary>
        /// Изменения MR: от точки ответвления до вершины. Коммиты берутся из
        /// локального репозитория, а если их там нет — забираются с сервера
        /// ссылкой refs/merge-requests/N/head, которую GitLab держит у каждого MR.
        /// </summary>
        private async Task LoadFilesAsync(GlMr mr, int request)
        {
            var head = mr.HeadSha ?? mr.Sha;
            var baseSha = mr.BaseSha ?? mr.StartSha;
            if (string.IsNullOrEmpty(head) || string.IsNullOrEmpty(baseSha))
            {
                _files.ShowMessage(L.T("Changes unavailable"), L.T("GitLab did not report the commits of this MR."));
                return;
            }

            _files.ShowMessage(L.T("Reading MR changes…"), null);
            var problem = await GitLabMrGit.EnsureCommitsAsync(mr);
            if (request != _request) return;

            if (problem != null)
            {
                _filesProblem = problem;
                RenderCard();
                _files.ShowMessage(L.T("Changes unavailable"), problem);
                return;
            }

            var files = await GitHistory.RangeFilesAsync(baseSha, head);
            if (request != _request) return;
            _rangePaths = files.ConvertAll(f => f.GitPath);
            _files.SetRange(baseSha, head, files);
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
            var mr = _items[index];
            var top = element.ElementAt(0);
            var dot = (Label)top.ElementAt(0);
            var title = (Label)top.ElementAt(1);
            var chips = top.ElementAt(2);
            var meta = (Label)element.ElementAt(1);

            bool mine = mr.SourceBranch == GitStatusCache.Branch;
            dot.style.color = mine ? GitPalette.Text(GitFileStatus.Added) : new Color(0.5f, 0.5f, 0.5f, 0.6f);
            dot.tooltip = mine ? L.T("MR of the current branch") : null;

            title.text = mr.Title;
            element.tooltip = mr.Title;

            chips.Clear();
            if (mr.IsMerged) chips.Add(Chip(L.Tc("mr state", "merged"), false));
            else if (mr.State == "closed") chips.Add(Chip(L.Tc("mr state", "closed"), false));
            if (mr.IsOpen && mr.Draft) chips.Add(Chip("draft", false));
            if (mr.IsOpen && mr.HasConflicts) chips.Add(Chip(L.T("conflict"), true));
            if (mr.UserNotesCount > 0) chips.Add(Chip("💬 " + mr.UserNotesCount, false));

            // У слитого важнее, кто и когда слил, чем кто открыл.
            string who, when;
            if (mr.IsMerged)
            {
                who = mr.MergedBy != null ? L.F("merged by {0}", mr.MergedBy) : (mr.Author != null ? mr.Author.ToString() : null);
                when = LockAdvice.Ago(mr.MergedAt ?? mr.UpdatedAt, DateTime.UtcNow);
            }
            else
            {
                who = mr.Author != null ? mr.Author.ToString() : null;
                when = mr.UpdatedAt.HasValue ? LockAdvice.Ago(mr.State == "closed" ? mr.ClosedAt ?? mr.UpdatedAt : mr.UpdatedAt, DateTime.UtcNow) : null;
            }

            meta.text = "!" + mr.Iid + " · " + mr.SourceBranch + " → " + mr.TargetBranch +
                        (who != null ? " · " + who : string.Empty) + (when != null ? " · " + when : string.Empty);
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
            var mr = _selected;

            if (mr == null)
            {
                _card.Add(Ui.Empty(L.T("No merge request selected"), L.T("Select an MR in the list on the left.")));
                return;
            }

            var s = GitLabSettings.instance;

            _card.Add(Ui.Text(mr.Title, "detail__name"));
            _card.Add(Ui.Text("!" + mr.Iid + (mr.Author != null ? " · " + mr.Author : string.Empty) +
                              (mr.CreatedAt.HasValue ? " · " + L.F("created {0}", LockAdvice.Ago(mr.CreatedAt, DateTime.UtcNow)) : string.Empty),
                              "detail__path"));

            var facts = Ui.Facts();
            facts.Add(Ui.Fact(L.T("Branches:"), mr.SourceBranch + " → " + mr.TargetBranch));
            if (mr.IsMerged)
                facts.Add(Ui.Fact(L.T("Merged:"), (mr.MergedBy != null ? mr.MergedBy + ", " : string.Empty) +
                                           (mr.MergedAt.HasValue ? LockAdvice.Stamp(mr.MergedAt, DateTime.UtcNow) : L.T("time unknown")), "t-ok"));
            else if (mr.State == "closed")
                facts.Add(Ui.Fact(L.T("Closed:"), mr.ClosedAt.HasValue ? LockAdvice.Stamp(mr.ClosedAt, DateTime.UtcNow) : L.T("without merging"), "t-dim"));
            if (mr.Draft) facts.Add(Ui.Fact(L.T("State:"), "draft", "t-warn"));
            if (mr.Assignees.Count > 0) facts.Add(Ui.Fact(L.T("Assignee:"), Join(mr.Assignees)));
            if (mr.Reviewers.Count > 0) facts.Add(Ui.Fact(L.T("Reviewers:"), Join(mr.Reviewers)));
            if (mr.Labels.Count > 0) facts.Add(Ui.Fact(L.T("Labels:"), string.Join(", ", mr.Labels)));

            if (_approvals != null)
            {
                var approved = _approvals.ApprovedBy.Count > 0 ? Join(_approvals.ApprovedBy) : L.T("none");
                if (_approvals.ApprovalsRequired > 0) approved += " " + L.F("({0} more needed)", _approvals.ApprovalsLeft);
                facts.Add(Ui.Fact(L.T("Approved by:"), approved, _approvals.ApprovedBy.Count > 0 ? "t-ok" : "t-dim"));
            }

            if (mr.HasDetails)
            {
                facts.Add(Ui.Fact(L.T("Pipeline:"), string.IsNullOrEmpty(mr.PipelineStatus) ? L.T("none") : PipelineName(mr.PipelineStatus),
                                  mr.PipelineStatus == "success" ? "t-ok" : mr.PipelineStatus == "failed" ? "t-error" : "t-dim"));
                facts.Add(Ui.Fact(L.T("Threads:"), mr.UserNotesCount == 0 ? L.T("none")
                    : mr.BlockingDiscussionsResolved ? L.F("{0}, resolved", mr.UserNotesCount) : L.F("{0}, some unresolved", mr.UserNotesCount),
                    mr.BlockingDiscussionsResolved ? "t-dim" : "t-warn"));
            }
            _card.Add(facts);

            if (_linkedIssues.Count > 0)
            {
                var issues = Ui.Box("commit__bar");
                issues.style.alignItems = Align.Center;
                issues.style.justifyContent = Justify.FlexStart;
                issues.style.flexWrap = Wrap.Wrap;
                issues.style.marginBottom = 6f;
                issues.Add(Ui.Text(L.T("Issues:"), "detail__path"));

                foreach (var issue in _linkedIssues)
                {
                    var iid = issue.Iid;
                    var title = issue.Title.Length > 48 ? issue.Title.Substring(0, 48) + "…" : issue.Title;
                    issues.Add(Ui.Action("#" + iid + " " + title + (issue.IsOpen ? string.Empty : " · " + L.Tc("issue state", "closed")),
                        () => GitLabEvents.OpenIssue(iid, mr.Iid),
                        L.T("Open the issue in the “Issues” tab. Return to this MR with the button above the issue list")));
                }
                _card.Add(issues);
            }

            var gate = GitLabWorkflow.Evaluate(mr, _approvals, (MergePolicy)s.mergePolicy,
                                               s.requireResolvedDiscussions, s.allowMergeWhenPipelineSucceeds);
            if (mr.HasDetails || mr.State != "opened")
            {
                var reason = gate.Reason + (GitLabWorkflow.IsChecking(mr) ? " " + L.T("Asking the server again…") : string.Empty);
                var banner = Ui.Banner(reason, gate.CanMerge ? "info" : gate.CanMergeWhenPipelineSucceeds ? "info" : "warn");
                // Сырой ответ сервера — чтобы по скриншоту было видно, что именно сказал GitLab.
                banner.tooltip = "GitLab: detailed_merge_status = " + (mr.DetailedMergeStatus ?? "—") +
                                 ", merge_status = " + (mr.MergeStatus ?? "—") +
                                 (string.IsNullOrEmpty(mr.PipelineStatus) ? string.Empty : ", " + L.T("pipeline") + " = " + mr.PipelineStatus);
                _card.Add(banner);
            }

            if (!string.IsNullOrEmpty(_filesProblem)) _card.Add(Ui.Banner(_filesProblem, "error"));

            if (!string.IsNullOrWhiteSpace(mr.Description))
            {
                var fold = new Foldout { text = L.T("Description"), value = false };
                fold.Add(new GitLabMarkdownView(mr.Description, () => Client(out _), "banner__text", 4000));
                _card.Add(fold);
            }

            var actions = Ui.Box("commit__bar");
            actions.style.marginTop = 4f;
            actions.style.flexWrap = Wrap.Wrap;

            if (mr.IsOpen && mr.HasDetails)
            {
                if (GitStatusCache.Branch != mr.SourceBranch)
                    actions.Add(Ui.Action(L.T("Switch to Branch"), Checkout, L.F("Fetch branch “{0}” and switch to it", mr.SourceBranch)));

                if (!gate.ShowButton)
                {
                    actions.Add(Ui.Action(L.T("Merge in GitLab"), () => Application.OpenURL(mr.WebUrl), gate.Reason));
                }
                else if (mr.MergeWhenPipelineSucceeds)
                {
                    actions.Add(Ui.Action(L.T("Cancel Auto-Merge"), CancelAutoMerge, L.T("The MR will merge by itself when the pipeline succeeds")));
                }
                else
                {
                    bool queue = !gate.CanMerge && gate.CanMergeWhenPipelineSucceeds;
                    var merge = Ui.Action(queue ? L.T("Merge When Pipeline Succeeds") : L.Tc("merge request action", "Merge"), () => Merge(queue), gate.Reason, true);
                    merge.SetEnabled(!_host.Busy && (gate.CanMerge || gate.CanMergeWhenPipelineSucceeds));
                    actions.Add(merge);
                }

                if (mr.DetailedMergeStatus == "need_rebase")
                    actions.Add(Ui.Action(L.T("Rebase on Server"), Rebase, L.T("GitLab will move the MR commits on top of the target branch")));

                if (_approvals != null && _approvals.UserHasApproved)
                    actions.Add(Ui.Action(L.T("Revoke Approval"), () => SetApproval(false)));
                else if (_approvals != null && _approvals.UserCanApprove)
                    actions.Add(Ui.Action(L.T("Approve"), () => SetApproval(true), L.T("Approve the version of the MR you are viewing now")));

                actions.Add(Ui.Action(mr.Draft ? L.T("Mark as Ready") : L.T("Mark as Draft"), ToggleDraft));
                actions.Add(Ui.Action(L.T("Close…"), CloseMr, L.T("Close without merging")));
            }

            actions.Add(Ui.Action(L.T("Open in GitLab"), () => Application.OpenURL(mr.WebUrl)));
            _card.Add(actions);
        }

        // --------------------------------------------------------------- ревью ---

        private void SetRightMode(bool discussions)
        {
            _files.style.display = discussions ? DisplayStyle.None : DisplayStyle.Flex;
            _discussions.style.display = discussions ? DisplayStyle.Flex : DisplayStyle.None;
            _modeFiles.EnableInClassList("act--primary", !discussions);
            _modeDiscussions.EnableInClassList("act--primary", discussions);
            UpdateModeLabels();
        }

        private void UpdateModeLabels()
        {
            int total = _discussions.ThreadCount, open = _discussions.OpenCount;
            _modeDiscussions.text = total == 0 ? L.T("Threads")
                : open > 0 ? L.F("Threads · {0} of {1} unresolved", open, total)
                : L.F("Threads · {0}", total);
        }

        private void OnDiscussionsChanged()
        {
            _lineThreads.Clear();
            _objectThreads.Clear();

            foreach (var d in _discussions.Items)
            {
                if (d.IsSystem) continue;
                var mark = d.Object;
                if (mark != null)
                {
                    var key = mark.GitPath + "|" + mark.FileId;
                    if (!_objectThreads.TryGetValue(key, out var list)) _objectThreads[key] = list = new List<GlDiscussion>();
                    list.Add(d);
                }
                else if (d.Position != null)
                {
                    _lineThreads.Add(d);
                }
            }

            UpdateModeLabels();
            _files.Diff.RefreshNotes();
        }

        private void HookDiff(DiffView diff)
        {
            diff.LineMenu = (menu, line) =>
            {
                if (_selected == null || !_selected.HasDetails) return;
                menu.AppendSeparator();
                menu.AppendAction(L.T("Comment on Line…"), _ => CommentLine(line));
                var thread = LineThread(line);
                if (thread != null) menu.AppendAction(L.T("Show Thread"), _ => FocusThread(thread));
            };

            diff.ObjectMenu = (menu, obj) =>
            {
                if (_selected == null || !_selected.HasDetails) return;
                menu.AppendSeparator();
                menu.AppendAction(obj.OwnerTitle != null ? L.T("Comment on Component…") : L.T("Comment on Object…"), _ => CommentObject(obj));
                var threads = ObjectThreads(obj);
                if (threads != null) menu.AppendAction(L.T("Show Thread"), _ => FocusThread(threads[0]));
            };

            diff.LineNote = line => Note(CountLine(line));
            diff.ObjectNote = obj =>
            {
                var threads = ObjectThreads(obj);
                return Note(threads != null ? threads.Count : 0);
            };
            diff.LineNoteClicked = line =>
            {
                var thread = LineThread(line);
                if (thread != null) FocusThread(thread);
            };
            diff.ObjectNoteClicked = obj =>
            {
                var threads = ObjectThreads(obj);
                if (threads != null) FocusThread(threads[0]);
            };
        }

        private static string Note(int count)
        {
            return count > 0 ? "💬 " + count : null;
        }

        private string HeadSha => _selected != null ? _selected.HeadSha ?? _selected.Sha : null;

        private int CountLine(DiffLineRef line)
        {
            int n = 0;
            var head = HeadSha;
            foreach (var d in _lineThreads)
                if (ReviewAnchors.Matches(d.Position, head, line.GitPath, line.OldLine, line.NewLine)) n++;
            return n;
        }

        private GlDiscussion LineThread(DiffLineRef line)
        {
            var head = HeadSha;
            foreach (var d in _lineThreads)
                if (ReviewAnchors.Matches(d.Position, head, line.GitPath, line.OldLine, line.NewLine)) return d;
            return null;
        }

        private List<GlDiscussion> ObjectThreads(DiffObjectRef obj)
        {
            return _objectThreads.TryGetValue(obj.GitPath + "|" + obj.FileId, out var list) && list.Count > 0 ? list : null;
        }

        private void FocusThread(GlDiscussion d)
        {
            SetRightMode(true);
            _discussions.FocusThread(d.Id);
        }

        /// <summary>Из обсуждения — к месту в изменениях: файл, затем строка или объект.</summary>
        private void ShowThreadInChanges(GlDiscussion d)
        {
            var mark = d.Object;
            var pos = d.Position;
            var path = mark != null ? mark.GitPath : pos != null ? pos.Path : null;
            if (path == null) return;

            if (!_files.ShowPath(path) && (pos == null || pos.OldPath == null || !_files.ShowPath(pos.OldPath)))
            {
                _host.SetStatus(L.F("File “{0}” is not among the changes of this MR version.", path), true);
                return;
            }

            SetRightMode(false);
            var shown = _files.Diff.CurrentGitPath;
            if (mark != null) _files.Diff.RevealObject(shown, mark.FileId);
            else _files.Diff.RevealLine(shown, pos.OldLine, pos.NewLine);
        }

        private void CommentLine(DiffLineRef line)
        {
            var mr = _selected;
            if (mr == null || !mr.HasDetails) return;

            int number = line.NewLine > 0 ? line.NewLine : line.OldLine;
            var snippet = (line.Text ?? string.Empty).Trim();
            if (snippet.Length > 140) snippet = snippet.Substring(0, 140) + "…";

            var text = CommentWindow.Ask(L.T("Comment on Line"), line.GitPath + ":" + number + "\n" + snippet);
            if (text == null) return;

            ReviewAnchors.LinesFor(line.Kind == DiffLineKind.Added, line.Kind == DiffLineKind.Removed,
                                   line.OldLine, line.NewLine, out var oldLine, out var newLine);
            var position = new GlNotePosition
            {
                BaseSha = mr.BaseSha, StartSha = mr.StartSha, HeadSha = mr.HeadSha,
                OldPath = line.OldGitPath ?? line.GitPath, NewPath = line.GitPath,
                OldLine = oldLine, NewLine = newLine
            };

            _host.Run(L.T("Comment on Line"), async () =>
            {
                var client = Client(out var problem);
                if (client == null) { _host.SetStatus(problem ?? L.T("API token is not set."), true); return; }

                var (d, error) = await client.StartDiscussionAsync(GitLabInstance.EffectiveEncodedId, mr.Iid, text, position);
                if (d == null) { _host.SetStatus(error, true); return; }

                _host.SetStatus(L.F("Comment on {0}:{1} sent — it is visible in GitLab too.", line.GitPath, number), false);
                _discussions.Reload();
            });
        }

        /// <summary>
        /// Комментарий к объекту сцены. В GitLab он ложится на строку заголовка
        /// объекта в YAML — так его видно у файла и в браузере. Если сервер такую
        /// позицию не принял (строка далеко от изменений), уходит общим
        /// комментарием: метка в тексте всё равно приведёт к объекту в редакторе.
        /// </summary>
        private void CommentObject(DiffObjectRef obj)
        {
            var mr = _selected;
            if (mr == null || !mr.HasDetails) return;

            var what = obj.OwnerTitle != null ? L.F("component “{0}” on “{1}”", obj.Title, obj.OwnerTitle) : L.F("object “{0}”", obj.Title);
            var text = CommentWindow.Ask(L.T("Comment on Scene Object"),
                what + " · " + obj.GitPath + "\n" + L.T("In GitLab the comment will appear at this object's line in the file."));
            if (text == null) return;

            var body = ReviewAnchors.ObjectCommentBody(obj.Title, obj.OwnerTitle, obj.GitPath, obj.FileId, text);

            _host.Run(L.T("Comment on Object"), async () =>
            {
                var client = Client(out var problem);
                if (client == null) { _host.SetStatus(problem ?? L.T("API token is not set."), true); return; }
                var encoded = GitLabInstance.EffectiveEncodedId;

                var head = mr.HeadSha ?? mr.Sha;
                var baseSha = mr.BaseSha ?? mr.StartSha;
                int newLine = obj.Removed ? 0 : ReviewAnchors.FindObjectHeaderLine(await GitHistory.ShowTextAsync(head, obj.GitPath), obj.FileId);
                int oldLine = obj.Added ? 0 : ReviewAnchors.FindObjectHeaderLine(await GitHistory.ShowTextAsync(baseSha, obj.OldGitPath ?? obj.GitPath), obj.FileId);

                GlDiscussion d = null;
                if (newLine > 0 || oldLine > 0)
                {
                    var position = new GlNotePosition
                    {
                        BaseSha = mr.BaseSha, StartSha = mr.StartSha, HeadSha = mr.HeadSha,
                        OldPath = obj.OldGitPath ?? obj.GitPath, NewPath = obj.GitPath,
                        OldLine = oldLine, NewLine = newLine
                    };
                    (d, _) = await client.StartDiscussionAsync(encoded, mr.Iid, body, position);
                }

                bool general = false;
                if (d == null)
                {
                    string error;
                    (d, error) = await client.StartDiscussionAsync(encoded, mr.Iid, body, null);
                    if (d == null) { _host.SetStatus(error, true); return; }
                    general = true;
                }

                _host.SetStatus(general
                    ? L.T("Object comment sent as a general comment: the object's line is far from the changes, and GitLab did not attach it to the file.")
                    : L.F("Object comment sent — in GitLab it is at the object's line in {0}.", obj.GitPath), false);
                _discussions.Reload();
            });
        }

        private void SetApproval(bool approve)
        {
            var mr = _selected;
            if (mr == null) return;

            RunOnMr(approve ? L.T("Approving") : L.T("Revoking approval"), async client =>
            {
                var encoded = GitLabInstance.EffectiveEncodedId;
                var error = approve
                    ? await client.ApproveAsync(encoded, mr.Iid, mr.HeadSha ?? mr.Sha)
                    : await client.UnapproveAsync(encoded, mr.Iid);
                return (error ?? (approve ? L.F("!{0} approved.", mr.Iid) : L.F("Approval of !{0} revoked.", mr.Iid)), error != null);
            }, mr.Iid);
        }

        // ------------------------------------------------------------ действия ---

        private void Checkout()
        {
            var mr = _selected;
            if (mr == null) return;

            _host.Run(L.T("Switching to MR branch"), async () =>
            {
                var r = await GitLabMrGit.CheckoutSourceAsync(mr);
                _host.SetStatus(r.Ok ? (string.IsNullOrEmpty(r.StdOut) ? L.F("Current branch: {0}", mr.SourceBranch) : r.StdOut.Trim()) : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                RenderCard();
                _list.RefreshItems();
            });
        }

        private void Merge(bool whenPipelineSucceeds)
        {
            var mr = _selected;
            if (mr == null) return;
            var s = GitLabSettings.instance;

            bool removeSource;
            switch ((RemoteBranchDeletion)s.afterMergeDeleteRemoteBranch)
            {
                case RemoteBranchDeletion.Always: removeSource = true; break;
                case RemoteBranchDeletion.Never: removeSource = false; break;
                default: removeSource = mr.ForceRemoveSourceBranch; break;
            }

            var question = (whenPipelineSucceeds
                               ? L.F("Merge automatically when the pipeline succeeds: !{0} “{1}” into “{2}”?", mr.Iid, mr.Title, mr.TargetBranch)
                               : L.F("Merge !{0} “{1}” into “{2}”?", mr.Iid, mr.Title, mr.TargetBranch)) + "\n\n" +
                           L.F("Squash: {0}", mr.Squash ? L.T("yes") : L.T("no")) + "\n" +
                           L.F("Branch “{0}” on the server: {1}", mr.SourceBranch, removeSource ? L.T("will be deleted") : L.T("will remain"));

            if (!EditorUtility.DisplayDialog(L.T("Merge the Merge Request"), question,
                    whenPipelineSucceeds ? L.T("Queue") : L.Tc("merge request action", "Merge"), L.T("Cancel")))
                return;

            var paths = new List<string>(_rangePaths);
            _host.Run(L.F("Merging !{0}", mr.Iid), async () =>
            {
                var client = Client(out var problem);
                if (client == null) { _host.SetStatus(problem ?? L.T("API token is not set."), true); return; }

                var (merged, error) = await client.MergeAsync(GitLabInstance.EffectiveEncodedId, mr, removeSource, whenPipelineSucceeds);
                if (merged == null) { _host.SetStatus(error, true); Select(mr); return; }

                if (merged.State != "merged")
                {
                    _host.SetStatus(L.F("!{0} will merge by itself when the pipeline succeeds.", mr.Iid), false);
                    _pendingIid = mr.Iid;
                    Load();
                    return;
                }

                _host.SetStatus(L.F("!{0} merged into “{1}”.", mr.Iid, mr.TargetBranch), false);
                var after = await GitLabMrGit.AfterMergeAsync(merged.HasDetails ? merged : mr, paths);
                if (after != null) _host.SetStatus(L.F("!{0} merged: {1}.", mr.Iid, after), false);

                _selected = null;
                Load();
            });
        }

        private void CancelAutoMerge()
        {
            var mr = _selected;
            if (mr == null) return;
            RunOnMr(L.T("Canceling auto-merge"), async client =>
            {
                var error = await client.CancelAutoMergeAsync(GitLabInstance.EffectiveEncodedId, mr.Iid);
                return (error ?? L.F("Auto-merge of !{0} canceled.", mr.Iid), error != null);
            }, mr.Iid);
        }

        private void Rebase()
        {
            var mr = _selected;
            if (mr == null) return;
            RunOnMr("Rebase", async client =>
            {
                var error = await client.RebaseAsync(GitLabInstance.EffectiveEncodedId, mr.Iid);
                return (error ?? L.F("Rebase of !{0} started on the server — refresh the list when it finishes.", mr.Iid), error != null);
            }, mr.Iid);
        }

        private void ToggleDraft()
        {
            var mr = _selected;
            if (mr == null) return;
            RunOnMr(mr.Draft ? L.T("Marking as ready") : L.T("Converting to draft"), async client =>
            {
                var (_, error) = await client.SetDraftAsync(GitLabInstance.EffectiveEncodedId, mr, !mr.Draft);
                return (error ?? (mr.Draft ? L.F("!{0} marked as ready.", mr.Iid) : L.F("!{0} converted to draft.", mr.Iid)), error != null);
            }, mr.Iid);
        }

        private void CloseMr()
        {
            var mr = _selected;
            if (mr == null) return;
            if (!EditorUtility.DisplayDialog(L.T("Close Merge Request"),
                    L.F("Close !{0} “{1}” without merging? The branch will remain, and the MR can be reopened in GitLab.", mr.Iid, mr.Title),
                    L.Tc("merge request action", "Close"), L.T("Cancel")))
                return;

            RunOnMr(L.T("Closing"), async client =>
            {
                var (_, error) = await client.CloseAsync(GitLabInstance.EffectiveEncodedId, mr.Iid);
                return (error ?? L.F("!{0} closed.", mr.Iid), error != null);
            }, 0);
        }

        /// <summary>Действие над MR: итог — в строку состояния, затем список перечитывается и выбирается нужный MR.</summary>
        private void RunOnMr(string title, Func<GitLabClient, Task<(string message, bool error)>> action, int selectAfter)
        {
            _host.Run(title, async () =>
            {
                var client = Client(out var problem);
                if (client == null) { _host.SetStatus(problem ?? L.T("API token is not set."), true); return; }

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

        private static string PipelineName(string status)
        {
            switch (status)
            {
                case "success": return L.Tc("pipeline status", "passed");
                case "failed": return L.Tc("pipeline status", "failed");
                case "running": return L.Tc("pipeline status", "running");
                case "pending":
                case "created":
                case "waiting_for_resource":
                case "preparing": return L.Tc("pipeline status", "queued");
                case "canceled": return L.Tc("pipeline status", "canceled");
                case "skipped": return L.Tc("pipeline status", "skipped");
                case "manual": return L.Tc("pipeline status", "waiting for manual run");
                default: return status;
            }
        }
    }
}
