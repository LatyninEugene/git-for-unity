using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git.UI
{
    /// <summary>
    /// Ветки — боковая панель слева от журнала.
    ///
    /// Щелчок по ветке отбирает журнал по ней, как в IDEA: чтобы посмотреть,
    /// что лежит в чужой ветке, переключаться на неё не нужно. Всё, что меняет
    /// рабочую копию, — только из меню: случайный щелчок в списке не должен
    /// переписывать файлы проекта.
    ///
    /// На ScrollView, а не на ListView: веток десятки, а строки разнотипные —
    /// заголовки разделов и сами ветки.
    /// </summary>
    public sealed class BranchesPane : VisualElement
    {
        private readonly IGitHost _host;
        private readonly Action _afterChange;

        private readonly ScrollView _scroll;
        private readonly TextField _search;

        private List<GitBranchInfo> _branches = new List<GitBranchInfo>();
        private bool _loading;

        /// <summary>HEAD на момент загрузки: коммит, checkout и pull меняют ahead/behind.</summary>
        private string _loadedHead;

        /// <summary>Выбранная ветка; null — журнал по всем веткам.</summary>
        public string SelectedRef { get; private set; }

        /// <summary>Сменился выбор: имя ветки или null для «всех веток».</summary>
        public event Action<string> RefSelected;

        public BranchesPane(IGitHost host, Action afterChange)
        {
            _host = host;
            _afterChange = afterChange;

            // Без flex-grow: панель закреплена в TwoPaneSplitView, ширину ей
            // задаёт сплиттер, и растягивание сломало бы перетаскивание.
            AddToClassList("pane");
            AddToClassList("bside");

            var bar = Ui.Box("bside__bar");

            _search = new TextField { value = string.Empty };
            _search.AddToClassList("bside__search");
            _search.tooltip = L.T("Filter by branch name");
            _search.textEdition.placeholder = L.T("Branches…");
            _search.textEdition.hidePlaceholderOnFocus = true;
            _search.RegisterValueChangedCallback(_ => Render());
            bar.Add(_search);

            var create = Ui.Action("+", CreateFromHead, L.T("New branch from the current state"));
            create.AddToClassList("act--icon");
            bar.Add(create);

            // Иконка редактора, а не символ: круговой стрелки в шрифте редактора
            // может не оказаться, и кнопка превратилась бы в пустой квадрат.
            var reload = Ui.Action(string.Empty, Reload, L.T("Reload the branch list"));
            reload.AddToClassList("act--icon");
            var icon = new Image { image = EditorGUIUtility.IconContent("Refresh").image };
            icon.AddToClassList("act__icon");
            icon.pickingMode = PickingMode.Ignore;
            reload.Add(icon);
            bar.Add(reload);

            Add(bar);

            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.style.flexGrow = 1f;
            _scroll.style.minHeight = 0f;
            Add(_scroll);

            // Значки MR приходят с сервера позже списка веток — дорисовываем по событию.
            RegisterCallback<AttachToPanelEvent>(_ => GitIntegrations.BadgesChanged += Render);
            RegisterCallback<DetachFromPanelEvent>(_ => GitIntegrations.BadgesChanged -= Render);
        }

        // -------------------------------------------------------- загрузка ---

        public void Refresh()
        {
            if (_loading) return;
            if (_branches.Count == 0 || _loadedHead != GitStatusCache.HeadOid) Reload();
        }

        public async void Reload()
        {
            if (_loading) return;
            _loading = true;

            try
            {
                _branches = await GitHistory.BranchesAsync();
                _loadedHead = GitStatusCache.HeadOid;
                GitIntegrations.RequestBadges();

                // Выбранную ветку могли удалить — тогда возвращаемся к журналу
                // по всем веткам, а не оставляем человека перед пустым списком.
                if (SelectedRef != null && !_branches.Exists(b => b.Name == SelectedRef))
                    Select(null);
                else
                    Render();
            }
            finally
            {
                _loading = false;
            }
        }

        /// <param name="notify">
        /// false — только отметить в списке. Нужно, когда отбор журнала уже
        /// выставлен снаружи: событие запустило бы вторую загрузку журнала.
        /// </param>
        public void Select(string refName, bool notify = true)
        {
            SelectedRef = refName;
            Render();

            if (!notify) return;

            var h = RefSelected;
            if (h != null) h(refName);
        }

        // ------------------------------------------------------------ показ ---

        private void Render()
        {
            _scroll.Clear();

            var needle = _search.value == null ? string.Empty : _search.value.Trim();
            var local = new List<GitBranchInfo>();
            var remote = new List<GitBranchInfo>();

            foreach (var b in _branches)
            {
                if (needle.Length > 0 &&
                    b.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (b.IsRemote) remote.Add(b); else local.Add(b);
            }

            // Текущая ветка всегда первой: с неё начинается любой ответ на
            // вопрос «где я и куда отсюда».
            local.Sort((a, b) => a.IsCurrent != b.IsCurrent
                ? (a.IsCurrent ? -1 : 1)
                : b.Date.CompareTo(a.Date));

            _scroll.Add(AllRow());

            if (local.Count == 0 && remote.Count == 0)
            {
                if (needle.Length > 0)
                    _scroll.Add(Ui.Empty(L.T("Not Found"), L.F("No branch matches “{0}”.", needle)));
                return;
            }

            if (local.Count > 0)
            {
                _scroll.Add(Section(L.T("Local"), local.Count));
                foreach (var b in local) _scroll.Add(Row(b));
            }

            if (remote.Count > 0)
            {
                _scroll.Add(Section(L.T("Remote"), remote.Count));
                foreach (var b in remote) _scroll.Add(Row(b));
            }
        }

        private static VisualElement Section(string title, int count)
        {
            var s = Ui.Box("bsection");
            s.Add(Ui.Text(title.ToUpperInvariant(), "bsection__title"));
            s.Add(Ui.Text(count.ToString(), "bsection__count"));
            return s;
        }

        private VisualElement AllRow()
        {
            var row = Ui.Box("brow", "brow--all");
            row.EnableInClassList("brow--selected", SelectedRef == null);
            row.Add(Ui.Text(string.Empty, "brow__head"));
            row.Add(Ui.Text(L.T("All Branches"), "brow__name"));
            row.tooltip = L.T("Log of all branches at once");
            row.RegisterCallback<ClickEvent>(_ => Select(null));
            return row;
        }

        private VisualElement Row(GitBranchInfo b)
        {
            var row = Ui.Box("brow");
            row.EnableInClassList("brow--current", b.IsCurrent);
            row.EnableInClassList("brow--selected", b.Name == SelectedRef);

            // Текущая ветка — точкой перед именем, а не словом: в узкой панели
            // слово «текущая» съело бы половину имени.
            row.Add(Ui.Text(b.IsCurrent ? "●" : string.Empty, "brow__head"));
            row.Add(Ui.Text(b.Name, "brow__name"));

            if (b.Ahead > 0 || b.Behind > 0)
            {
                var track = string.Empty;
                if (b.Ahead > 0) track += "↑" + b.Ahead;
                if (b.Behind > 0) track += (track.Length > 0 ? " " : string.Empty) + "↓" + b.Behind;
                row.Add(Ui.Text(track, "brow__track"));
            }

            if (b.Gone) row.Add(Ui.Text("×", "brow__gone"));

            var badge = GitIntegrations.BranchBadge(ServerName(b));
            if (badge != null)
            {
                var chip = Ui.Text(badge.Text, "brow__mr");
                chip.tooltip = badge.Tooltip;
                if (badge.Open != null)
                {
                    var open = badge.Open;
                    chip.RegisterCallback<ClickEvent>(e => { e.StopPropagation(); open(); });
                }
                row.Add(chip);
            }

            row.Add(Ui.Spacer());

            // Кнопка меню не прячется до наведения: на узкой панели до действий
            // должно быть можно добраться, не выискивая строку мышью.
            var menu = Ui.Action("⋯", null, L.T("Branch actions"));
            menu.AddToClassList("brow__menu");
            menu.clicked += () =>
            {
                var m = new GenericMenu();
                FillMenu(m, b);
                m.DropDown(menu.worldBound);
            };
            row.Add(menu);

            row.tooltip = Tooltip(b) + (badge != null && !string.IsNullOrEmpty(badge.Tooltip) ? "\n" + badge.Tooltip : string.Empty);

            row.RegisterCallback<ClickEvent>(e =>
            {
                // Щелчок по кнопке меню — не выбор ветки.
                var t = e.target as VisualElement;
                if (t != null && (t == menu || menu.Contains(t))) return;
                Select(b.Name);
            });

            row.AddManipulator(new ContextualMenuManipulator(e => FillMenu(e.menu, b)));
            return row;
        }

        /// <summary>
        /// Всё, что не поместилось в узкую строку: заголовок последнего коммита,
        /// дата, upstream и расхождение с ним словами.
        /// </summary>
        private static string Tooltip(GitBranchInfo b)
        {
            var sb = new System.Text.StringBuilder(b.Name);
            if (b.IsCurrent) sb.Append("  ").Append(L.T("(current)"));
            if (!string.IsNullOrEmpty(b.Subject)) sb.Append('\n').Append(b.Subject);
            if (b.Date != DateTime.MinValue) sb.Append('\n').Append(b.Date.ToString("d MMMM yyyy, HH:mm", L.Culture));
            if (b.Upstream != null) sb.Append("\nupstream: ").Append(b.Upstream);
            if (b.Ahead > 0) sb.Append('\n').Append(L.F("ahead by {0}", b.Ahead));
            if (b.Behind > 0) sb.Append('\n').Append(L.F("behind by {0}", b.Behind));
            if (b.Gone) sb.Append('\n').Append(L.T("branch deleted on the server"));
            return sb.ToString();
        }

        // --------------------------------------------------------- действия ---

        /// <summary>
        /// То же меню для правой кнопки. Отдельный метод, а не общий строитель:
        /// у GenericMenu и DropdownMenu нет общего интерфейса, а сводить их
        /// через обёртку ради двух десятков строк — лишний слой.
        /// </summary>
        /// <summary>Имя ветки на сервере: у удалённой — без префикса remote, у локальной — её upstream или она сама.</summary>
        private static string ServerName(GitBranchInfo b)
        {
            if (b.IsRemote) return GitHistory.StripRemote(b.Name);
            return b.Upstream != null ? GitHistory.StripRemote(b.Upstream) : b.Name;
        }

        /// <summary>Ветка есть на сервере — только тогда у неё есть страница.</summary>
        private static bool OnServer(GitBranchInfo b)
        {
            return b.IsRemote || (b.Upstream != null && !b.Gone);
        }

        private void FillMenu(DropdownMenu m, GitBranchInfo b)
        {
            m.AppendAction(L.T("Show in Log"), _ => Select(b.Name));

            var badge = GitIntegrations.BranchBadge(ServerName(b));
            if (badge != null && badge.Open != null) m.AppendAction(L.F("Merge Request {0}", badge.Text), _ => badge.Open());
            if (OnServer(b))
                GitIntegrations.AddOpenLinks((title, act) => m.AppendAction(title, _ => act()), i => i.BranchUrl(ServerName(b)));

            m.AppendSeparator();

            if (!b.IsCurrent)
            {
                m.AppendAction(b.IsRemote ? L.T("Checkout as Local Branch") : L.T("Checkout"), _ => Checkout(b));
                m.AppendAction(L.T("Merge into Current"), _ => Merge(b));
                m.AppendSeparator();
            }

            if (!b.IsRemote)
                m.AppendAction(b.Upstream == null ? L.T("Push and Set Upstream") : L.T("Push"), _ => Push(b));

            m.AppendAction(L.T("Copy Name"), _ => EditorGUIUtility.systemCopyBuffer = b.Name);

            if (!b.IsCurrent)
            {
                m.AppendSeparator();
                m.AppendAction(b.IsRemote ? L.T("Delete on Server…") : L.T("Delete…"), _ => Delete(b));
            }
        }

        private void FillMenu(GenericMenu m, GitBranchInfo b)
        {
            m.AddItem(new GUIContent(L.T("Show in Log")), b.Name == SelectedRef, () => Select(b.Name));

            var badge = GitIntegrations.BranchBadge(ServerName(b));
            if (badge != null && badge.Open != null) m.AddItem(new GUIContent(L.F("Merge Request {0}", badge.Text)), false, () => badge.Open());
            if (OnServer(b))
                GitIntegrations.AddOpenLinks((title, act) => m.AddItem(new GUIContent(title), false, () => act()), i => i.BranchUrl(ServerName(b)));

            m.AddSeparator(string.Empty);

            if (!b.IsCurrent)
            {
                m.AddItem(new GUIContent(b.IsRemote ? L.T("Checkout as Local Branch") : L.T("Checkout")), false,
                          () => Checkout(b));
                m.AddItem(new GUIContent(L.T("Merge into Current")), false, () => Merge(b));
            }
            else
            {
                m.AddDisabledItem(new GUIContent(L.T("Checkout")));
                m.AddDisabledItem(new GUIContent(L.T("Merge into Current")));
            }

            m.AddSeparator(string.Empty);

            if (!b.IsRemote)
                m.AddItem(new GUIContent(b.Upstream == null ? L.T("Push and Set Upstream") : L.T("Push")),
                          false, () => Push(b));

            m.AddItem(new GUIContent(L.T("Copy Name")), false,
                      () => EditorGUIUtility.systemCopyBuffer = b.Name);

            m.AddSeparator(string.Empty);

            if (b.IsCurrent)
                m.AddDisabledItem(new GUIContent(L.T("Delete")));
            else
                m.AddItem(new GUIContent(b.IsRemote ? L.T("Delete on Server…") : L.T("Delete…")), false,
                          () => Delete(b));
        }

        private void Checkout(GitBranchInfo b)
        {
            var target = b.IsRemote ? GitHistory.StripRemote(b.Name) : b.Name;

            _host.Run(L.F("Switching to {0}", target), async () =>
            {
                if (!await SafeSwitch.AskAsync(b.Name, L.Tc("operation", "Switch to Branch"),
                        b.IsRemote
                            ? L.F("A local branch “{0}” tracking {1} will be created.", target, b.Name)
                            : L.F("The working tree will switch to branch “{0}”.", target)))
                    return;

                var r = await GitHistory.CheckoutBranchAsync(b);
                _host.SetStatus(r.Ok ? L.F("Current branch: {0}", target) : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                Done();
            });
        }

        private void Merge(GitBranchInfo b)
        {
            // Отдельный коммит слияния предлагается первым: в проекте на Unity
            // важно видеть, что ветка была, — по нему находится, откуда приехал
            // набор ассетов.
            int choice = EditorUtility.DisplayDialogComplex(
                L.T("Merge Branch"),
                L.F("Branch “{0}” will be merged into the current one ({1}).\n\n" +
                    "A separate merge commit keeps a trace of the branch in the history. " +
                    "Without it the commits simply line up in a row, if the history allows.",
                    b.Name, GitStatusCache.Branch ?? "HEAD"),
                L.T("With Merge Commit"), L.T("Cancel"), L.T("Without Merge Commit"));

            if (choice == 1) return;
            bool noFf = choice == 0;

            _host.Run(L.F("Merging {0}", b.Name), async () =>
            {
                var report = await SafeSwitch.InspectAsync(null);
                if (report.Blocked) { SafeSwitch.Confirm(report, L.T("Merging"), string.Empty); return; }

                var r = await GitHistory.MergeAsync(b.Name, noFf);

                if (!r.Ok && r.Message.IndexOf("conflict", StringComparison.OrdinalIgnoreCase) >= 0)
                    _host.SetStatus(L.T("The merge stopped on conflicts: resolve them " +
                                        "in the Changes tab."), true);
                else
                    _host.SetStatus(r.Ok ? L.F("Branch “{0}” merged", b.Name) : r.Message, !r.Ok);

                await GitStatusCache.RefreshAsync();
                Done();
            });
        }

        private void Push(GitBranchInfo b)
        {
            _host.Run("Push " + b.Name, async () =>
            {
                var r = await GitHistory.PushBranchAsync(b.Name, b.Upstream == null);
                _host.SetStatus(r.Ok ? L.F("Branch “{0}” pushed", b.Name) : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                Done();
            });
        }

        private void Delete(GitBranchInfo b)
        {
            if (b.IsRemote)
            {
                if (!EditorUtility.DisplayDialog(L.T("Delete Remote Branch"),
                        L.F("Branch “{0}” will be deleted on the server.\n\n" +
                            "Everyone with access to the repository will see this.", b.Name),
                        L.T("Delete"), L.T("Cancel")))
                    return;

                _host.Run(L.F("Deleting {0}", b.Name), async () =>
                {
                    var r = await GitHistory.DeleteRemoteBranchAsync(b.Name);
                    _host.SetStatus(r.Ok ? L.T("Branch deleted on the server") : r.Message, !r.Ok);
                    Done();
                });
                return;
            }

            if (!EditorUtility.DisplayDialog(L.T("Delete Branch"),
                    L.F("Delete local branch “{0}”?", b.Name), L.T("Delete"), L.T("Cancel")))
                return;

            _host.Run(L.F("Deleting {0}", b.Name), async () =>
            {
                var r = await GitHistory.DeleteBranchAsync(b.Name, false);

                // git отказывается удалять ветку, чьи коммиты никуда не влиты.
                // Это единственный случай, когда -D действительно теряет работу,
                // поэтому спрашиваем отдельно и прямо называем последствие.
                if (!r.Ok && r.Message.IndexOf("not fully merged", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!EditorUtility.DisplayDialog(L.T("Branch Not Merged"),
                            L.F("Branch “{0}” has commits that exist nowhere else.\n\n" +
                                "If you delete it now, this work will be lost.", b.Name),
                            L.T("Delete Anyway"), L.T("Cancel")))
                    {
                        _host.SetStatus(L.T("Deletion cancelled"), false);
                        return;
                    }

                    r = await GitHistory.DeleteBranchAsync(b.Name, true);
                }

                _host.SetStatus(r.Ok ? L.F("Branch “{0}” deleted", b.Name) : r.Message, !r.Ok);
                Done();
            });
        }

        private void CreateFromHead()
        {
            var name = TextPromptWindow.Ask(
                L.T("New Branch"),
                L.F("The branch will start from the current state ({0}).",
                    GitStatusCache.Branch ?? GitHistory.Short(GitStatusCache.HeadOid)),
                L.T("Branch Name"), string.Empty);

            if (string.IsNullOrEmpty(name)) return;

            bool switchTo = EditorUtility.DisplayDialog(L.T("New Branch"),
                L.F("Switch to “{0}” right after creating it?", name), L.T("Switch"), L.T("Stay Here"));

            _host.Run(L.F("Creating branch {0}", name), async () =>
            {
                var r = switchTo
                    ? await GitHistory.CreateBranchAndCheckoutAsync(name, "HEAD")
                    : await GitHistory.CreateBranchAsync(name, "HEAD");

                _host.SetStatus(r.Ok ? L.F("Branch “{0}” created", name) : r.Message, !r.Ok);
                await GitStatusCache.RefreshAsync();
                Done();
            });
        }

        private void Done()
        {
            Reload();
            if (_afterChange != null) _afterChange();
        }
    }
}
