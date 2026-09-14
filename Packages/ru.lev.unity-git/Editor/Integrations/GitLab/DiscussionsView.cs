using System;
using System.Collections.Generic;
using Lev.Git.UI;
using UnityEditor;
using UnityEngine.UIElements;

namespace Lev.Git.GitLab
{
    /// <summary>
    /// Обсуждения merge request'а или задачи: новый комментарий, треды, ответы,
    /// решение тредов. У MR треды бывают привязаны к строкам diff и к объектам
    /// сцены. Всё уходит в GitLab и видно в браузере там же, где его оставили
    /// бы оттуда.
    /// </summary>
    public sealed class DiscussionsView : VisualElement
    {
        private const string HideResolvedPref = "LevGit.GitLab.HideResolved";

        private readonly IGitHost _host;
        private readonly Func<GitLabClient> _client;
        private readonly ScrollView _scroll = new ScrollView(ScrollViewMode.Vertical);
        private readonly Label _summary;
        private readonly Button _hideButton;
        private readonly TextField _composer;
        private readonly List<GlDiscussion> _items = new List<GlDiscussion>();

        /// <summary>Недописанные ответы: переживают перечитывание списка.</summary>
        private readonly Dictionary<string, string> _drafts = new Dictionary<string, string>();

        /// <summary>Треды, у которых открыто поле ответа.</summary>
        private readonly HashSet<string> _replying = new HashSet<string>();

        private int _iid;
        private bool _isIssue;
        private string _head;

        private string _error;
        private bool _loading;
        private int _request;
        private string _focusId;
        private bool _hideResolved = EditorPrefs.GetBool(HideResolvedPref, false);

        /// <summary>Список обсуждений перечитан — пометкам и счётчикам пора обновиться.</summary>
        public event Action Changed;

        /// <summary>Перейти к месту обсуждения во вкладке «Изменения».</summary>
        public Action<GlDiscussion> ShowInChanges;

        public IReadOnlyList<GlDiscussion> Items => _items;

        public int ThreadCount
        {
            get
            {
                int n = 0;
                foreach (var d in _items) if (!d.IsSystem) n++;
                return n;
            }
        }

        public int OpenCount
        {
            get
            {
                int n = 0;
                foreach (var d in _items) if (!d.IsSystem && d.Resolvable && !d.Resolved) n++;
                return n;
            }
        }

        public DiscussionsView(IGitHost host, Func<GitLabClient> client)
        {
            _host = host;
            _client = client;
            style.flexGrow = 1f;
            style.minHeight = 0f;

            var bar = Ui.Box("subbar");
            _summary = Ui.Text(string.Empty, "subbar__label");
            bar.Add(_summary);
            bar.Add(Ui.Spacer());
            _hideButton = Ui.Action(string.Empty, () =>
            {
                _hideResolved = !_hideResolved;
                EditorPrefs.SetBool(HideResolvedPref, _hideResolved);
                Render();
            });
            bar.Add(_hideButton);
            bar.Add(Ui.Action(L.T("Refresh"), Reload, L.T("Reload threads from the server")));
            Add(bar);

            var compose = Ui.Box("thread__reply");
            compose.style.marginLeft = 8f;
            compose.style.marginRight = 8f;
            compose.style.flexShrink = 0f;
            _composer = new TextField { multiline = true };
            _composer.AddToClassList("thread__input");
            _composer.textEdition.hidePlaceholderOnFocus = true;
            compose.Add(_composer);
            compose.Add(Ui.Action(L.T("Send"), SendGeneral, null, true));
            Add(compose);

            _scroll.style.flexGrow = 1f;
            _scroll.style.minHeight = 0f;
            Add(_scroll);

            Render();
        }

        private string TargetPath => GitLabReviewApi.TargetPath(GitLabInstance.EffectiveEncodedId, _isIssue, _iid);

        /// <param name="reload">Перечитать с сервера, даже если это тот же MR.</param>
        public void SetMergeRequest(GlMr mr, bool reload)
        {
            SetTarget(mr != null ? mr.Iid : 0, false, mr != null ? mr.HeadSha ?? mr.Sha : null, reload);
        }

        public void SetIssue(GlIssue issue, bool reload)
        {
            SetTarget(issue != null ? issue.Iid : 0, true, null, reload);
        }

        private void SetTarget(int iid, bool isIssue, string head, bool reload)
        {
            bool other = iid != _iid || isIssue != _isIssue || iid == 0;
            _iid = iid;
            _isIssue = isIssue;
            _head = head;

            _composer.textEdition.placeholder = isIssue ? L.T("Comment on the issue…") : L.T("General comment on the merge request…");

            if (other)
            {
                ++_request;
                _items.Clear();
                _drafts.Clear();
                _replying.Clear();
                _error = null;
                _focusId = null;
                _loading = false;
                Changed?.Invoke();
            }

            if (iid > 0 && (reload || other)) Reload();
            else Render();
        }

        public async void Reload()
        {
            if (_iid <= 0) { Render(); return; }

            var client = _client();
            if (client == null)
            {
                _error = L.T("The API token is not set — set it in Project Settings → Git → GitLab.");
                Render();
                return;
            }

            int request = ++_request;
            var path = TargetPath;
            _loading = true;
            Render();

            try
            {
                var (items, error) = await client.DiscussionsAsync(path);
                if (request != _request) return;
                _error = error;
                _items.Clear();
                _items.AddRange(items);
            }
            catch (Exception e)
            {
                if (request == _request) _error = e.Message;
            }
            finally
            {
                if (request == _request)
                {
                    _loading = false;
                    Render();
                    Changed?.Invoke();
                }
            }
        }

        /// <summary>Показать тред: раскрыть скрытые решённые, если он среди них, и прокрутить к нему.</summary>
        public void FocusThread(string discussionId)
        {
            _focusId = discussionId;
            Render();
        }

        // ------------------------------------------------------------ показ ---

        private void Render()
        {
            _scroll.Clear();
            _hideButton.text = _hideResolved ? L.T("Show Resolved") : L.T("Hide Resolved");
            // У задач тредов с решением нет — и кнопке скрывать нечего.
            _hideButton.style.display = _isIssue ? DisplayStyle.None : DisplayStyle.Flex;
            _composer.SetEnabled(_iid > 0);

            int total = ThreadCount, open = OpenCount;
            _summary.text = _iid <= 0 ? string.Empty
                : _loading && total == 0 ? L.T("Loading threads…")
                : (_isIssue ? L.F("Comments: {0}", total) : L.F("Threads: {0}", total)) + (open > 0 ? L.F(", unresolved: {0}", open) : string.Empty);

            if (_iid <= 0)
            {
                _scroll.Add(Ui.Empty(_isIssue ? L.T("No Issue Selected") : L.T("No MR Selected"), null));
                return;
            }

            if (!string.IsNullOrEmpty(_error)) _scroll.Add(Ui.Banner(_error, "error"));

            if (!_loading && total == 0 && string.IsNullOrEmpty(_error))
                _scroll.Add(_isIssue
                    ? Ui.Empty(L.T("No Comments Yet"), L.T("Leave a comment in the field above."))
                    : Ui.Empty(L.T("No Threads Yet"),
                        L.T("Leave a general comment above, or right-click a diff line or a scene object in the “Changes” tab.")));

            VisualElement focus = null;
            int hidden = 0;
            foreach (var d in _items)
            {
                if (d.IsSystem) continue;
                if (_hideResolved && d.Resolved && d.Id != _focusId) { hidden++; continue; }

                var element = Thread(d);
                if (d.Id == _focusId) focus = element;
                _scroll.Add(element);
            }

            if (hidden > 0)
            {
                var more = Ui.Text(L.F("Resolved threads hidden: {0}", hidden), "note__meta");
                more.style.marginLeft = 10f;
                more.style.marginTop = 6f;
                _scroll.Add(more);
            }

            if (focus != null)
            {
                var target = focus;
                // Раскладка ещё не посчитана — прокручиваем на следующем кадре.
                _scroll.schedule.Execute(() => _scroll.ScrollTo(target)).StartingIn(30);
            }
        }

        private static bool IsSceneFile(string gitPath)
        {
            return gitPath != null && gitPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Миниатюра версии ассета на вершине merge request — чтобы из обсуждения
        /// было видно, о чём речь, не открывая файл. Щелчок открывает «Историю ассета».
        /// </summary>
        private void AddThumbnail(VisualElement head, string gitPath)
        {
            var projectPath = GitRepository.ToProjectPath(gitPath);
            if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(_head)) return;

            var sha = _head;
            var thumb = new Image { scaleMode = UnityEngine.ScaleMode.ScaleToFit, tooltip = L.T("Version at the merge request head · click for asset history") };
            thumb.AddToClassList("thread__thumb");
            thumb.image = Lev.Git.Preview.PreviewThumbnails.ForCommit(sha, projectPath, gitPath) ?? Ui.AssetIcon(projectPath);
            thumb.RegisterCallback<ClickEvent>(_ => Lev.Git.UI.AssetHistoryWindow.Open(projectPath, false));

            Action update = null;
            update = () =>
            {
                var ready = Lev.Git.Preview.PreviewThumbnails.ForCommit(sha, projectPath, gitPath);
                if (ready == null) return;
                thumb.image = ready;
                Lev.Git.Preview.PreviewThumbnails.Ready -= update;
            };

            Lev.Git.Preview.PreviewThumbnails.Ready += update;
            thumb.RegisterCallback<DetachFromPanelEvent>(_ => Lev.Git.Preview.PreviewThumbnails.Ready -= update);

            head.Add(thumb);
        }

        private VisualElement Thread(GlDiscussion d)
        {
            var box = Ui.Box("thread");
            var mark = d.Object;
            var pos = d.Position;
            box.EnableInClassList("thread--general", mark == null && pos == null);
            box.EnableInClassList("thread--resolved", d.Resolved);
            box.EnableInClassList("thread--focus", d.Id == _focusId);

            // ---- заголовок: к чему относится; у комментария задачи он не нужен ----
            string anchor = null;
            if (mark != null)
            {
                var what = ReviewAnchors.ObjectTitle(d.First.Body) ?? "fileID " + mark.FileId;
                anchor = IsSceneFile(mark.GitPath)
                    ? L.F("Scene object: {0} · {1}", what, mark.GitPath)
                    : L.F("Asset: {0} · {1}", what, mark.GitPath);
            }
            else if (pos != null)
                anchor = ReviewAnchors.Describe(pos) + (ReviewAnchors.IsOutdated(pos, _head) ? " · " + L.T("on an earlier MR version") : string.Empty);
            else if (!_isIssue)
                anchor = L.T("General thread");

            if (anchor != null || d.Resolvable)
            {
                var head = Ui.Box("thread__head");
                if (mark != null && !IsSceneFile(mark.GitPath)) AddThumbnail(head, mark.GitPath);
                if (anchor != null) head.Add(Ui.Text(anchor, "thread__anchor"));
                head.Add(Ui.Spacer());

                if (d.Resolvable)
                {
                    GlUserRef by = null;
                    foreach (var n in d.Notes) if (n.ResolvedBy != null) by = n.ResolvedBy;
                    head.Add(Ui.Text(d.Resolved ? L.T("resolved") + (by != null ? " · " + by : string.Empty) : L.T("unresolved"), "thread__state"));
                }

                if ((mark != null || pos != null) && ShowInChanges != null)
                    head.Add(Ui.Action(L.T("Show"), () => ShowInChanges(d), L.T("Open this place in the “Changes” tab")));
                box.Add(head);
            }

            // ---- заметки ----
            bool first = true;
            foreach (var n in d.Notes)
            {
                if (n.System) continue;

                var note = Ui.Box("note");
                note.Add(Ui.Text((n.Author != null ? n.Author.ToString() : "?") +
                                 (n.CreatedAt.HasValue ? " · " + LockAdvice.Ago(n.CreatedAt, DateTime.UtcNow) : string.Empty), "note__meta"));
                note.Add(new GitLabMarkdownView(first ? ReviewAnchors.DisplayBody(n.Body) : n.Body, _client, "note__body", 20000));
                box.Add(note);
                first = false;
            }

            box.Add(ReplyRow(d));
            return box;
        }

        /// <summary>
        /// Поле ответа раскрывается по кнопке: у задачи каждый комментарий — свой
        /// тред, и поле под каждым превратило бы список в стену полей ввода.
        /// </summary>
        private VisualElement ReplyRow(GlDiscussion d)
        {
            var reply = Ui.Box("thread__reply");
            bool open = _replying.Contains(d.Id) || (_drafts.TryGetValue(d.Id, out var saved) && !string.IsNullOrEmpty(saved));

            if (!open)
            {
                reply.Add(Ui.Spacer());
                reply.Add(Ui.Action(L.T("Reply…"), () => { _replying.Add(d.Id); Render(); }));
            }
            else
            {
                var input = new TextField { multiline = true };
                input.AddToClassList("thread__input");
                input.textEdition.placeholder = L.Tc("placeholder", "Reply…");
                input.textEdition.hidePlaceholderOnFocus = true;
                if (_drafts.TryGetValue(d.Id, out var draft)) input.SetValueWithoutNotify(draft);
                input.RegisterValueChangedCallback(e => _drafts[d.Id] = e.newValue);
                reply.Add(input);
                reply.Add(Ui.Action(L.T("Send"), () => Reply(d, input.value), null, true));
                reply.Add(Ui.Action(L.T("Cancel"), () =>
                {
                    _replying.Remove(d.Id);
                    _drafts.Remove(d.Id);
                    Render();
                }));

                // Фокус в поле — после того как оно окажется в дереве.
                input.schedule.Execute(() => input.Focus()).StartingIn(20);
            }

            if (d.Resolvable)
                reply.Add(Ui.Action(d.Resolved ? L.T("Reopen") : L.T("Resolve"), () => Resolve(d, !d.Resolved)));

            return reply;
        }

        // --------------------------------------------------------- действия ---

        private void SendGeneral()
        {
            var text = _composer.value;
            if (_iid <= 0 || string.IsNullOrWhiteSpace(text)) return;
            var path = TargetPath;

            _host.Run(_isIssue ? L.F("Comment on #{0}", _iid) : L.F("Comment on !{0}", _iid), async () =>
            {
                var client = _client();
                if (client == null) { _host.SetStatus(L.T("The API token is not set."), true); return; }

                var (d, error) = await client.StartDiscussionAsync(path, text.Trim(), null);
                if (d == null) { _host.SetStatus(error, true); return; }

                _composer.SetValueWithoutNotify(string.Empty);
                _host.SetStatus(L.T("Comment sent."), false);
                _focusId = d.Id;
                Reload();
            });
        }

        private void Reply(GlDiscussion d, string text)
        {
            if (_iid <= 0 || string.IsNullOrWhiteSpace(text)) return;
            var path = TargetPath;

            _host.Run(L.T("Replying to Thread"), async () =>
            {
                var client = _client();
                if (client == null) { _host.SetStatus(L.T("The API token is not set."), true); return; }

                var error = await client.ReplyAsync(path, d.Id, text.Trim());
                if (error != null) { _host.SetStatus(error, true); return; }

                _drafts.Remove(d.Id);
                _replying.Remove(d.Id);
                _host.SetStatus(L.T("Reply sent."), false);
                _focusId = d.Id;
                Reload();
            });
        }

        private void Resolve(GlDiscussion d, bool resolved)
        {
            if (_iid <= 0) return;
            var path = TargetPath;

            _host.Run(resolved ? L.T("Resolving Thread") : L.T("Reopening Thread"), async () =>
            {
                var client = _client();
                if (client == null) { _host.SetStatus(L.T("The API token is not set."), true); return; }

                var error = await client.ResolveAsync(path, d.Id, resolved);
                if (error != null) { _host.SetStatus(error, true); return; }

                _host.SetStatus(resolved ? L.T("Thread resolved.") : L.T("Thread reopened."), false);
                _focusId = d.Id;
                Reload();
            });
        }
    }
}
