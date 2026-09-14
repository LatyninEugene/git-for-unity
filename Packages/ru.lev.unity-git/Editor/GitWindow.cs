using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lev.Git.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git
{
    /// <summary>
    /// Оболочка окна: шапка с веткой и сетевыми действиями, полоса текущей
    /// операции, вкладки и строка состояния. Содержимое разделов живёт в
    /// отдельных классах и об окне знает только через <see cref="IGitHost"/>.
    ///
    /// Вкладки — список, а не перечисление: свои вкладки добавляют включённые
    /// интеграции, и окно пересобирается, когда интеграцию включили или выключили.
    ///
    /// Интерфейс на UI Toolkit, а не на IMGUI: списку изменений нужна
    /// виртуализация, раскладке — сплиттер, оформлению — таблица стилей.
    /// </summary>
    public sealed class GitWindow : EditorWindow, IGitHost, IHasCustomMenu, ILocalizedWindow
    {
        private const string ChangesTab = "changes";
        private const string HistoryTab = "history";
        private const string LocksTab = "locks";
        private const string ConsoleTab = "console";

        private sealed class TabEntry
        {
            public string Id;
            public string Title;
            public VisualElement Element;
            public Action Refresh;
            public Func<int> Badge;
            public Func<bool> Alert;

            public VisualElement Button;
            public Label BadgeLabel;
        }

        private readonly List<TabEntry> _tabs = new List<TabEntry>();

        private VisualElement _jobBar, _mergeBar, _body, _shell;
        private Label _mergeTitle, _mergeCount;
        private Button _mergeResolve, _mergeContinue, _mergeAbort;
        private Label _jobProgress, _statusText, _branchName, _branchTrack, _repoLabel;
        private Button _jobCancel, _pull, _push, _fetch, _refresh, _pushMenu, _fetchMenu, _reportLink;

        private ChangesView _changes;
        private HistoryView _history;
        private LocksView _locks;
        private ConsoleView _console;

        [SerializeField] private string _tabId = ChangesTab;
        private bool _busy;
        private bool _lastProSkin;
        private List<string> _branches = new List<string>();

        [MenuItem("Window/Git")]
        public static void Open()
        {
            var w = GetWindow<GitWindow>();
            // GetWindow с текстом заголовка ставит вкладке один текст и затирает иконку.
            w.SetTitle();
            w.minSize = new Vector2(460f, 300f);
            w.Show();
        }

        /// <summary>Журнал, отобранный по одному файлу. Точка входа для меню окна Project и списка изменений.</summary>
        public static void ShowFileHistory(string projectPath)
        {
            var w = GetWindow<GitWindow>();
            // GetWindow с текстом заголовка ставит вкладке один текст и затирает иконку.
            w.SetTitle();
            w.minSize = new Vector2(460f, 300f);
            w.Show();
            w.Focus();

            // Окно могло быть создано прямо сейчас, и CreateGUI ещё не отработал.
            w._pendingHistoryPath = projectPath;
            w._pendingHistorySha = null;
            w.ApplyPendingHistory();
        }

        /// <summary>Журнал на конкретном коммите, отобранный по файлу, в котором коммит найден.</summary>
        public static void ShowCommit(string sha, string projectPath)
        {
            var w = GetWindow<GitWindow>();
            // GetWindow с текстом заголовка ставит вкладке один текст и затирает иконку.
            w.SetTitle();
            w.minSize = new Vector2(460f, 300f);
            w.Show();
            w.Focus();

            w._pendingHistoryPath = projectPath;
            w._pendingHistorySha = sha;
            w.ApplyPendingHistory();
        }

        /// <summary>Вкладка интеграции: «gitlab». Если окна ещё нет — откроется сразу на ней.</summary>
        public static void ShowIntegration(string integrationId)
        {
            var w = GetWindow<GitWindow>();
            // GetWindow с текстом заголовка ставит вкладке один текст и затирает иконку.
            w.SetTitle();
            w.minSize = new Vector2(460f, 300f);
            w.Show();
            w.Focus();

            var id = "integration:" + integrationId;
            w._tabId = id;
            if (w._body != null && w.Find(id) != null) w.SelectTab(id);
        }

        // Разовая просьба, а не состояние окна. Без NonSerialized Unity переносит
        // приватные поля окна через перезагрузку домена и превращает null в "":
        // после каждой правки скрипта окно «искало» пустой коммит и писало ошибку.
        [NonSerialized] private string _pendingHistoryPath;
        [NonSerialized] private string _pendingHistorySha;

        private void ApplyPendingHistory()
        {
            if (_history == null || _pendingHistoryPath == null) return;

            var path = _pendingHistoryPath;
            var sha = _pendingHistorySha;
            _pendingHistoryPath = null;
            _pendingHistorySha = null;

            SelectTab(HistoryTab);
            _history.FilterByPath(path, sha);
        }

        // ------------------------------------------------------ жизненный цикл ---

        private void OnEnable()
        {
            // Заголовок хранится в раскладке окон: без этого у тех, кто открывал
            // окно ещё при пакете «GitLab for Unity», вкладка так и звалась бы GitLab.
            SetTitle();

            GitStatusCache.Updated += OnStatusUpdated;
            LfsLockCache.Updated += OnStatusUpdated;
            GitCommandLog.Changed += OnCommandLogged;
            GitIntegrations.Changed += OnIntegrationsChanged;
            Selection.selectionChanged += OnUnitySelectionChanged;
        }

        private void OnDisable()
        {
            GitStatusCache.Updated -= OnStatusUpdated;
            LfsLockCache.Updated -= OnStatusUpdated;
            GitCommandLog.Changed -= OnCommandLogged;
            GitIntegrations.Changed -= OnIntegrationsChanged;
            Selection.selectionChanged -= OnUnitySelectionChanged;
            ReleasePreviews();
        }

        /// <summary>
        /// Освобождает превью своих вкладок: и видимой, и скрытых (скрытые вне
        /// дерева, у них нет панели). Превью в других окнах не трогает.
        /// </summary>
        private void ReleasePreviews()
        {
            var own = rootVisualElement != null ? rootVisualElement.panel : null;
            AssetPreviewPane.ReleaseAll(p => p.panel == null || p.panel == own);
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.Clear();
            Ui.Attach(root);
            _lastProSkin = EditorGUIUtility.isProSkin;

            if (!GitRepository.IsRepo) GitRepository.Locate();

            _shell = Ui.Box();
            _shell.style.flexGrow = 1f;
            root.Add(_shell);

            BuildShell();

            // Прогресс операции приходит из фонового потока и событий не шлёт,
            // поэтому полосу опрашиваем по таймеру. Заодно ловим смену скина.
            root.schedule.Execute(Tick).Every(100);

            RefreshLocal();
        }

        /// <summary>Заголовок вкладки с иконкой. Ставится и при восстановлении окна, и при открытии кодом.</summary>
        private void SetTitle()
        {
            titleContent = new GUIContent("Git", GitIcons.Window, L.T("Git: changes, history, branches"));
        }

        private void OnIntegrationsChanged()
        {
            if (_shell == null) return;
            BuildShell();
            RefreshLocal();
        }

        /// <summary>Пункты меню «⋮» у вкладки окна.</summary>
        public void AddItemsToMenu(GenericMenu menu)
        {
            L.AddLanguageMenu(menu, L.T("Language") + "/");
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.T("Report a Problem…")), false, Diagnostics.ProblemReportWindow.Open);
            menu.AddItem(new GUIContent(L.T("Settings")), false, () => SettingsService.OpenProjectSettings(GitSettingsProvider.PagePath));
        }

        /// <summary>Язык сменился: подписи на UI Toolkit расставлены при сборке, поэтому окно собирается заново.</summary>
        void ILocalizedWindow.OnLanguageChanged()
        {
            SetTitle();
            if (_shell == null) return;
            BuildShell();
            RefreshLocal();
        }

        private void BuildShell()
        {
            // Вкладки пересоздаются — у старых превью ресурсы надо отдать сейчас.
            ReleasePreviews();
            _shell.Clear();

            if (!GitRepository.IsRepo)
            {
                var empty = Ui.Empty(L.T("Project is not in a git repository"),
                    L.T("Open a project located in a git repository, or create a repository — for example, " +
                        "by connecting the project to a server via Unity Hub."));
                var retry = Ui.Action(L.T("Check Again"), () =>
                {
                    GitRepository.Locate();
                    BuildShell();
                    if (GitRepository.IsRepo) RefreshLocal();
                }, null, true);
                retry.style.marginTop = 10f;
                empty.Add(retry);
                _shell.Add(empty);
                return;
            }

            _changes = new ChangesView(this);
            _history = new HistoryView(this);
            _locks = new LocksView(this);
            _console = new ConsoleView();

            BuildTabList();

            _shell.Add(BuildHeader());
            _shell.Add(BuildJobBar());
            _shell.Add(BuildMergeBar());
            _shell.Add(BuildTabs());

            _body = Ui.Box("body");
            _shell.Add(_body);

            _shell.Add(BuildStatusLine());

            if (Find(_tabId) == null) _tabId = ChangesTab;
            SelectTab(_tabId);
            ApplyPendingHistory();
        }

        private void BuildTabList()
        {
            _tabs.Clear();

            _tabs.Add(new TabEntry
            {
                Id = ChangesTab, Title = L.T("Changes"), Element = _changes, Refresh = _changes.Refresh,
                Badge = () => GitStatusCache.Changes.Count, Alert = () => GitStatusCache.ConflictCount > 0
            });
            _tabs.Add(new TabEntry { Id = HistoryTab, Title = L.T("History"), Element = _history, Refresh = _history.Refresh });

            // Вкладки интеграций — после истории: они про тот же проект, но на сервере.
            foreach (var integration in GitIntegrations.Enabled)
            {
                IGitTab tab = null;
                try { tab = integration.CreateTab(this); }
                catch (Exception e) { Diagnostics.Journal.Warn(L.F("Tab {0} was not created: {1}", integration.DisplayName, e.Message)); }
                if (tab == null) continue;

                var t = tab;
                _tabs.Add(new TabEntry
                {
                    Id = "integration:" + integration.Id, Title = t.Title, Element = t.Element, Refresh = t.Refresh,
                    Badge = () => t.Badge
                });
            }

            // На вкладке локов — чужие локи; тревога — если что-то из LFS не скачано.
            _tabs.Add(new TabEntry
            {
                Id = LocksTab, Title = L.T("LFS Locks"), Element = _locks, Refresh = _locks.Refresh,
                Badge = () => LfsLockCache.Locks.Verified ? LfsLockCache.Locks.TheirsCount : 0,
                Alert = () => LfsLockCache.NotDownloadedCount > 0
            });
            _tabs.Add(new TabEntry { Id = ConsoleTab, Title = L.T("Console"), Element = _console, Refresh = _console.Refresh });
        }

        private TabEntry Find(string id)
        {
            foreach (var t in _tabs) if (t.Id == id) return t;
            return null;
        }

        private VisualElement BuildHeader()
        {
            var header = Ui.Box("header");

            var pill = Ui.Box("branch-pill");
            _branchName = Ui.Text("…", "branch-pill__name");
            _branchTrack = Ui.Text(string.Empty, "branch-pill__track");
            pill.Add(_branchName);
            pill.Add(_branchTrack);
            pill.Add(Ui.Text("▾", "branch-pill__caret"));
            pill.RegisterCallback<MouseDownEvent>(_ => ShowBranchMenu(pill.worldBound));
            header.Add(pill);

            _repoLabel = Ui.Text(string.Empty, "header__repo");
            header.Add(_repoLabel);

            header.Add(Ui.Spacer());

            _refresh = Ui.Action(L.T("Refresh"), RefreshLocal, L.T("Re-read the repository status"));
            _fetch = Ui.Action("Fetch", () => Run("Fetch", () => NetworkOp("Fetch", GitOperations.FetchAsync)));
            _pull = Ui.Action("Pull", () => Run("Pull", () => NetworkOp("Pull", GitOperations.PullAsync)));
            _push = Ui.Action("Push", () => Run("Push", () => NetworkOp("Push", GitOperations.PushAsync)), null, true);

            _fetchMenu = MenuArrow(() => ShowFetchMenu(_fetchMenu.worldBound), L.T("Fetch from another remote or from all at once"));
            _pushMenu = MenuArrow(() => ShowPushMenu(_pushMenu.worldBound), L.T("Choose where to push"));

            header.Add(_refresh);
            header.Add(_fetch);
            header.Add(_fetchMenu);
            header.Add(_pull);
            header.Add(_push);
            header.Add(_pushMenu);
            header.Add(Ui.Action("⚙", () => SettingsService.OpenProjectSettings(GitSettingsProvider.PagePath),
                                 L.T("Project Settings → Git: settings and integrations")));
            return header;
        }

        /// <summary>Узкая кнопка «▾» вплотную к основной — как у разделённой кнопки.</summary>
        private static Button MenuArrow(Action onClick, string tooltip)
        {
            var b = Ui.Action("▾", onClick, tooltip);
            b.style.marginLeft = -3f;
            b.style.paddingLeft = 4f;
            b.style.paddingRight = 4f;
            b.style.minWidth = 0f;
            return b;
        }

        /// <summary>'/' в GenericMenu делает подменю, а в именах веток он обычен.</summary>
        private static string MenuText(string s)
        {
            return (s ?? string.Empty).Replace('/', '∕');
        }

        private void ShowPushMenu(Rect anchor)
        {
            var menu = new GenericMenu();
            var branch = GitStatusCache.Branch;
            var upstream = GitStatusCache.Upstream;

            if (string.IsNullOrEmpty(branch))
            {
                menu.AddDisabledItem(new GUIContent(L.T("No current branch (detached HEAD)")));
            }
            else
            {
                if (!string.IsNullOrEmpty(upstream))
                    menu.AddItem(new GUIContent(L.F("To {0} — upstream", MenuText(upstream))), false,
                                 () => Run("Push", () => NetworkOp("Push", GitOperations.PushAsync)));

                var remotes = GitRepository.Remotes;
                if (remotes.Count == 0) menu.AddDisabledItem(new GUIContent(L.T("No remotes configured")));

                foreach (var r in remotes)
                {
                    var remote = r.Name;
                    var target = remote + "/" + branch;
                    if (target == upstream) continue;

                    menu.AddItem(new GUIContent(L.F("To {0}", MenuText(target))), false,
                        () => Run("Push → " + remote, () => NetworkOp("Push", () => GitOperations.PushToAsync(remote, branch, false))));
                    menu.AddItem(new GUIContent(L.F("To {0} and set as upstream", MenuText(target))), false,
                        () => Run("Push → " + remote, () => NetworkOp("Push", () => GitOperations.PushToAsync(remote, branch, true))));
                }

                if (remotes.Count > 0)
                {
                    int slash = string.IsNullOrEmpty(upstream) ? -1 : upstream.IndexOf('/');
                    var forceRemote = slash > 0 ? upstream.Substring(0, slash) : GitRepository.SelectedRemoteName;
                    bool forceUpstream = slash <= 0;
                    var forceTarget = forceRemote + "/" + branch;

                    menu.AddSeparator(string.Empty);
                    menu.AddItem(new GUIContent(L.F("Push --force to {0}…", MenuText(forceTarget))), false, () =>
                    {
                        if (!EditorUtility.DisplayDialog(L.T("Push --force"),
                                L.F("The branch {0} on the server will be replaced with your local branch: commits that exist only on the server will disappear from it.\n\n" +
                                    "The package runs it as --force-with-lease: if someone pushed to the branch since the last fetch, the server refuses the replacement and nothing is lost.", forceTarget),
                                L.T("Push --force"), L.T("Cancel")))
                            return;
                        Run(L.T("Push --force"), () => NetworkOp("Push", () => GitOperations.ForcePushAsync(forceRemote, branch, forceUpstream)));
                    });
                }
            }

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.T("Configure Remotes…")), false, () => SettingsService.OpenProjectSettings(GitSettingsProvider.PagePath));
            menu.DropDown(anchor);
        }

        private void ShowFetchMenu(Rect anchor)
        {
            var menu = new GenericMenu();

            foreach (var r in GitRepository.Remotes)
            {
                var remote = r.Name;
                menu.AddItem(new GUIContent(remote == GitRepository.SelectedRemoteName
                        ? L.F("From {0} — primary", MenuText(remote))
                        : L.F("From {0}", MenuText(remote))), false,
                    () => Run("Fetch " + remote, () => NetworkOp("Fetch", () => GitOperations.FetchRemoteAsync(remote))));
            }

            if (GitRepository.Remotes.Count > 1)
            {
                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent(L.T("From All Remotes")), false,
                    () => Run(L.T("Fetch All"), () => NetworkOp("Fetch", GitOperations.FetchAllAsync)));
            }

            if (GitRepository.Remotes.Count == 0) menu.AddDisabledItem(new GUIContent(L.T("No remotes configured")));

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.T("Configure Remotes…")), false, () => SettingsService.OpenProjectSettings(GitSettingsProvider.PagePath));
            menu.DropDown(anchor);
        }

        private VisualElement BuildJobBar()
        {
            _jobBar = Ui.Box("job");
            _jobBar.style.display = DisplayStyle.None;

            _jobProgress = Ui.Text(string.Empty, "job__progress");
            _jobCancel = Ui.Action(L.Tc("job", "Cancel"), () =>
            {
                var job = GitJobs.Current;
                if (job != null) job.Cancel();
            });

            _jobBar.Add(_jobProgress);
            _jobBar.Add(_jobCancel);
            return _jobBar;
        }

        /// <summary>
        /// Полоса прерванной операции: что сливается, сколько осталось и что с
        /// этим делать. Видна, пока идёт слияние, rebase, cherry-pick или в
        /// рабочей копии есть конфликты.
        /// </summary>
        private VisualElement BuildMergeBar()
        {
            _mergeBar = Ui.Box("job", "merge");
            _mergeBar.style.display = DisplayStyle.None;

            _mergeTitle = Ui.Text(string.Empty, "job__title");
            _mergeCount = Ui.Text(string.Empty, "job__progress");

            _mergeResolve = Ui.Action(L.T("Resolve…"), () => ConflictResolverWindow.Open(null),
                                      L.T("Open the conflict resolution window"), true);
            _mergeContinue = Ui.Action(L.Tc("git operation", "Continue"), () => Run(L.T("Continuing"), async () =>
            {
                var r = await GitConflicts.ContinueAsync();
                SetStatus(r.Ok ? L.T("Operation completed") : r.Message, !r.Ok);
                if (r.Ok) GitConflicts.EndBackupSession();
                await GitStatusCache.RefreshAsync();
            }));
            _mergeAbort = Ui.Action(L.T("Abort"), () =>
            {
                var state = GitConflicts.RefreshState();
                if (!EditorUtility.DisplayDialog(L.T("Abort Operation"),
                        L.F("{0} will be aborted, and the working tree will return to its state before it started.", state.Title),
                        L.T("Abort Operation"), L.T("Back"))) return;

                Run(L.T("Aborting"), async () =>
                {
                    var r = await GitConflicts.AbortAsync();
                    SetStatus(r.Ok ? L.T("Operation aborted") : r.Message, !r.Ok);
                    if (r.Ok) GitConflicts.EndBackupSession();
                    await GitStatusCache.RefreshAsync();
                });
            });

            _mergeBar.Add(_mergeTitle);
            _mergeBar.Add(_mergeCount);
            _mergeBar.Add(_mergeResolve);
            _mergeBar.Add(_mergeContinue);
            _mergeBar.Add(_mergeAbort);
            return _mergeBar;
        }

        private void UpdateMergeBar()
        {
            if (_mergeBar == null) return;

            var state = GitConflicts.RefreshState();
            int conflicts = GitStatusCache.ConflictCount;
            bool show = state.InProgress || conflicts > 0;

            _mergeBar.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show) return;

            _mergeTitle.text = state.Title;
            _mergeCount.text = conflicts > 0
                ? L.F("Conflicts: {0}", conflicts)
                : (state.CanContinue ? L.T("No conflicts — ready to continue") : string.Empty);

            _mergeResolve.style.display = conflicts > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _mergeContinue.style.display = state.CanContinue ? DisplayStyle.Flex : DisplayStyle.None;
            _mergeAbort.style.display = state.CanContinue ? DisplayStyle.Flex : DisplayStyle.None;
            _mergeContinue.SetEnabled(conflicts == 0 && !_busy);
            _mergeContinue.tooltip = conflicts > 0 ? L.T("Resolve all conflicts first") : L.T("Continue the git operation");
        }

        private VisualElement BuildTabs()
        {
            var bar = Ui.Box("tabs");

            foreach (var entry in _tabs)
            {
                var e = entry;

                // Не Button: у TextElement собственный текст рисуется в той же
                // области, что и дочерние элементы, и бейдж налез бы на подпись.
                var tab = Ui.Box("tab");
                tab.Add(Ui.Text(e.Title));

                var badge = Ui.Text(string.Empty, "tab__badge");
                badge.style.display = DisplayStyle.None;
                tab.Add(badge);

                tab.AddManipulator(new Clickable(() => SelectTab(e.Id)));

                e.Button = tab;
                e.BadgeLabel = badge;
                bar.Add(tab);
            }

            bar.Add(Ui.Spacer());
            return bar;
        }

        private VisualElement BuildStatusLine()
        {
            var line = Ui.Box("status-line");
            _statusText = Ui.Text(L.T("Ready"), "status-line__text");
            line.Add(_statusText);

            // При ошибке — прямой путь к отчёту: человек уже видит, что что-то не так.
            _reportLink = Ui.Action(L.T("Report a Problem…"), Diagnostics.ProblemReportWindow.Open,
                                    L.T("Collect a report for the author of Git for Unity"));
            _reportLink.style.display = DisplayStyle.None;
            line.Add(_reportLink);
            return line;
        }

        private void SelectTab(string id)
        {
            var entry = Find(id) ?? Find(ChangesTab);
            if (entry == null || _body == null) return;
            _tabId = entry.Id;

            foreach (var t in _tabs)
                if (t.Button != null) t.Button.EnableInClassList("tab--active", t == entry);

            _body.Clear();
            _body.Add(entry.Element);
            SafeRefresh(entry);
        }

        private static void SafeRefresh(TabEntry entry)
        {
            if (entry.Refresh == null) return;
            try { entry.Refresh(); }
            catch (Exception e) { Diagnostics.Journal.Exception(e, "Git window tab refresh"); }
        }

        // ------------------------------------------------------------ такт ---

        private void Tick()
        {
            // Скин переключается без перезагрузки домена — класс темы переставляем.
            if (EditorGUIUtility.isProSkin != _lastProSkin)
            {
                _lastProSkin = EditorGUIUtility.isProSkin;
                Ui.ApplyTheme(rootVisualElement);
            }

            if (_jobBar == null) return;

            var job = GitJobs.Current;
            bool running = job != null;
            _jobBar.style.display = running ? DisplayStyle.Flex : DisplayStyle.None;

            if (!running) return;

            _jobProgress.text = string.IsNullOrEmpty(job.Progress)
                ? job.Title + "…"
                : job.Title + " — " + job.Progress;

            _jobCancel.text = job.CancelRequested ? L.T("Canceling…") : L.Tc("job", "Cancel");
            _jobCancel.SetEnabled(!job.CancelRequested);
        }

        // ------------------------------------------------------- обновление ---

        private async void RefreshLocal()
        {
            if (!GitRepository.IsRepo) return;

            GitRepository.LoadRemotes();

            // Статус идёт первым: `status --porcelain=v2 --branch` заодно приносит
            // ветку, upstream и ahead/behind — тремя процессами это больше не стоит.
            await GitStatusCache.RefreshAsync();
            GitRepository.Upstream = GitStatusCache.Upstream;

            _branches = await GitOperations.LocalBranchesAsync();

            UpdateHeader();
            UpdateBadges();
            RefreshActiveView();
        }

        private void UpdateHeader()
        {
            if (_branchName == null) return;

            _branchName.text = GitStatusCache.IsDetached
                ? "detached HEAD"
                : (string.IsNullOrEmpty(GitStatusCache.Branch) ? L.T("no branch") : GitStatusCache.Branch);

            var track = string.Empty;
            if (GitStatusCache.Ahead > 0) track += "↑" + GitStatusCache.Ahead + " ";
            if (GitStatusCache.Behind > 0) track += "↓" + GitStatusCache.Behind;
            _branchTrack.text = track.Trim();

            var remote = GitRepository.Remote;
            _repoLabel.text = remote != null && !string.IsNullOrEmpty(remote.FullPath) ? remote.FullPath : string.Empty;
        }

        private void UpdateBadges()
        {
            UpdateMergeBar();

            foreach (var t in _tabs)
            {
                if (t.BadgeLabel == null) continue;

                int count = 0;
                bool alert = false;
                try
                {
                    if (t.Badge != null) count = t.Badge();
                    if (t.Alert != null) alert = t.Alert();
                }
                catch { }

                t.BadgeLabel.style.display = count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                t.BadgeLabel.text = count > 99 ? "99+" : count.ToString();
                t.BadgeLabel.EnableInClassList("tab__badge--alert", alert);
            }
        }

        private void RefreshActiveView()
        {
            // Журнал перечитывается по кнопке, а не по такту: он дорогой.
            if (_tabId == HistoryTab) return;

            var entry = Find(_tabId);
            if (entry != null) SafeRefresh(entry);
        }

        private void OnStatusUpdated()
        {
            if (_shell == null) return;
            UpdateHeader();
            UpdateBadges();
            if (_tabId == ChangesTab && _changes != null) _changes.Refresh();
        }

        private void OnCommandLogged()
        {
            if (_tabId == ConsoleTab && _console != null) _console.Refresh();
        }

        private void OnUnitySelectionChanged()
        {
            if (_tabId == LocksTab && _locks != null) _locks.Refresh();
        }

        // ------------------------------------------------ аутентификация ---

        /// <summary>
        /// Сетевая операция с одной повторной попыткой после починки доступа.
        /// Повтор ровно один: если и он не прошёл, дальше это уже не про пароль.
        /// </summary>
        private async Task NetworkOp(string title, Func<Task<ProcessResult>> op)
        {
            var r = await op();

            if (!r.Ok && await TryFixAuthAsync(r))
                r = await op();

            // Сервер отклонил push из-за расхождения истории — предлагаем выход, а не только текст ошибки.
            if (!r.Ok && title == "Push")
                r = await PushRecovery.OfferAsync(r);

            SetStatus(r.Ok ? L.F("{0} completed", title) : r.Message, !r.Ok);
            await GitStatusCache.RefreshAsync();
            UpdateHeader();

            // После обмена с сервером локи и набор скачанного могли измениться.
            LfsLockCache.RequestRefresh();
        }

        /// <summary>Возвращает true, если что-то починили и операцию стоит повторить.</summary>
        private async Task<bool> TryFixAuthAsync(ProcessResult r)
        {
            // Операция могла идти не в основной remote (Push → другой remote),
            // поэтому чиним тот, что отказал, — его видно по тексту ошибки git.
            string remoteName;
            var remote = GitAuth.RemoteFromError(r, out remoteName) ?? GitRepository.Remote;
            if (remoteName == null) remoteName = GitRepository.SelectedRemoteName;

            var problem = GitAuth.Classify(r, remote);
            if (problem == GitAuthProblem.None) return false;

            return problem == GitAuthProblem.SshKey
                ? await FixSshAsync(r, remote, remoteName)
                : await AskCredentialsAsync(remote);
        }

        private async Task<bool> AskCredentialsAsync(GitRemote remote)
        {
            if (remote == null || remote.IsSsh) return false;

            // Спрашиваем по адресу REMOTE: git пойдёт именно туда, и запись
            // должна лечь ровно под тот протокол и хост.
            var target = remote.Scheme + "://" + remote.HostPort;

            var cred = await CredentialPromptWindow.AskAndStoreAsync(
                target,
                L.T("The server rejected the credentials, or they are not in the git credential store yet."),
                true,
                m => SetStatus(m, true));

            if (cred == null)
            {
                SetStatus(L.T("The operation cannot succeed without credentials."), true);
                return false;
            }

            SetStatus(L.T("Credentials saved, retrying…"), false);
            return true;
        }

        private async Task<bool> FixSshAsync(ProcessResult r, GitRemote remote, string remoteName)
        {
            // Адрес HTTP(S) и страница ключей — того сервера, куда шла операция,
            // а не первой включённой интеграции.
            var host = remote != null ? remote.Host : null;
            var httpUrl = GitAuth.HttpUrlFor(remote);
            var keysUrl = string.IsNullOrEmpty(host) ? null : CredentialHosts.ForHost(host).SshKeysUrl;
            string details = null;

            if (remote != null && remote.IsSsh)
            {
                // Сначала выясняем причину. Чаще всего ключ на сервере есть, но ssh
                // его не предлагает (нестандартное имя файла) или не может открыть (пароль).
                SetStatus(L.F("Checking SSH keys for {0}…", host), false);
                var d = await GitSsh.DiagnoseAsync(remote);

                switch (d.Kind)
                {
                    case SshDiagnosisKind.KeyNotOffered:
                    {
                        var added = await GitSsh.AddKeyAsync(d.KeyPath, null);
                        if (added.Ok)
                        {
                            SetStatus(L.F("Key {0} added to ssh-agent, retrying…", d.KeyName), false);
                            return true;
                        }
                        details = L.F("Couldn't add the key {0} to ssh-agent: {1}", d.KeyName, added.Message);
                        break;
                    }

                    case SshDiagnosisKind.NeedsPassphrase:
                    {
                        var choice = await UnlockSshKeyAsync(d, host, httpUrl);
                        if (choice == SshPassphraseChoice.Unlock) return true;
                        if (choice == SshPassphraseChoice.Cancel) return false;
                        return await SwitchRemoteToHttpAsync(remoteName, httpUrl);
                    }

                    case SshDiagnosisKind.AlreadyWorks:
                        details = L.F("ssh itself signs in to {0} with these keys, so the server most likely denies access to this repository.", host);
                        break;

                    case SshDiagnosisKind.Network:
                        details = d.Details;
                        // Отказ по ключу приходит от сервера: значит, git до него только что дошёл.
                        if ((r.StdErr ?? string.Empty).Contains("Permission denied"))
                            details += "\n\n" + L.T("git reached the server a moment ago, so this is most likely a temporary network failure. Try the operation again.");
                        break;

                    default:
                        details = d.Details;
                        break;
                }
            }

            if (!SshProblemWindow.AskSwitchToHttp(remoteName, host, r.Message.Trim(), httpUrl, keysUrl, details))
            {
                SetStatus(r.Message, true);
                return false;
            }

            return await SwitchRemoteToHttpAsync(remoteName, httpUrl);
        }

        /// <summary>Спрашивает пароль ключа, пока ssh-add его не примет или человек не передумает.</summary>
        private async Task<SshPassphraseChoice> UnlockSshKeyAsync(SshDiagnosis d, string host, string httpUrl)
        {
            string error = null;
            while (true)
            {
                string passphrase;
                var choice = SshPassphraseWindow.Ask(host, d.KeyName, httpUrl, error, out passphrase);
                if (choice != SshPassphraseChoice.Unlock) return choice;

                Diagnostics.Redactor.RegisterSecret(passphrase);
                var added = await GitSsh.AddKeyAsync(d.KeyPath, passphrase);
                passphrase = null;

                if (added.Ok)
                {
                    SetStatus(L.F("Key {0} unlocked until Unity is closed, retrying…", d.KeyName), false);
                    return choice;
                }

                // Неверный пароль ssh-add не объясняет: askpass отказывает на повторный запрос, stderr пуст.
                error = string.IsNullOrWhiteSpace(added.StdErr) ? L.T("Wrong passphrase.") : added.Message;
            }
        }

        private async Task<bool> SwitchRemoteToHttpAsync(string remoteName, string httpUrl)
        {
            if (httpUrl == null)
            {
                SetStatus(L.T("The repository HTTP(S) URL is unknown — switch the remote manually."), true);
                return false;
            }

            var set = await GitAuth.SetRemoteUrlAsync(remoteName, httpUrl);
            if (!set.Ok)
            {
                SetStatus(L.F("Failed to change the remote URL: {0}", set.Message), true);
                return false;
            }

            GitRepository.LoadRemotes();
            SetStatus(L.F("Remote “{0}” switched to {1}, retrying…", remoteName, httpUrl), false);
            return true;
        }

        // -------------------------------------------------------- ветки ---

        private void ShowBranchMenu(Rect anchor)
        {
            var menu = new GenericMenu();

            if (_branches.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent(L.T("No branches found")));
            }
            else
            {
                var current = GitStatusCache.Branch;
                foreach (var b in _branches)
                {
                    var branch = b;
                    menu.AddItem(new GUIContent(branch), branch == current, () =>
                    {
                        if (branch == current) return;
                        Run(L.Fc("git window", "Switching to {0}", branch), async () =>
                        {
                            var r = await GitOperations.CheckoutAsync(branch);
                            SetStatus(r.Ok ? L.F("Current branch: {0}", branch) : r.Message, !r.Ok);
                            await GitStatusCache.RefreshAsync();
                            UpdateHeader();
                        });
                    });
                }
            }

            menu.DropDown(anchor);
        }

        // ------------------------------------------------------- IGitHost ---

        public bool Busy => _busy;

        public async void Run(string title, Func<Task> body)
        {
            if (_busy) return;
            _busy = true;
            SetStatus(title + "…", false);
            SetActionsEnabled(false);

            try
            {
                await body();
            }
            catch (Exception e)
            {
                SetStatus(L.F("Error: {0}", e.Message), true);
                Diagnostics.Journal.Exception(e, "Git window operation: " + title);
            }
            finally
            {
                _busy = false;
                SetActionsEnabled(true);
                RefreshActiveView();
            }
        }

        public void SetStatus(string message, bool error)
        {
            if (_statusText == null) return;
            _statusText.text = message;
            _statusText.EnableInClassList("status-line__text--error", error);
            if (_reportLink != null) _reportLink.style.display = error ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void SetActionsEnabled(bool enabled)
        {
            if (_refresh == null) return;
            _refresh.SetEnabled(enabled);
            _fetch.SetEnabled(enabled);
            _pull.SetEnabled(enabled);
            _push.SetEnabled(enabled);
            _pushMenu.SetEnabled(enabled);
            _fetchMenu.SetEnabled(enabled);
        }
    }
}
