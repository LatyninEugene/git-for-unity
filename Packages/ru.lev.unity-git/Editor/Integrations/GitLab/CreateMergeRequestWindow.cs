using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.GitLab
{
    /// <summary>
    /// Создание merge request'а из текущей ветки.
    ///
    /// Значения по умолчанию — из настроек проекта: целевая ветка, draft,
    /// squash, описание. Если ветка не отправлена, перед созданием она
    /// уходит на сервер обычным push с проверкой локов.
    ///
    /// На IMGUI: окно — форма из полей и выпадающих списков.
    /// </summary>
    public sealed class CreateMergeRequestWindow : EditorWindow, ILocalizedWindow
    {
        private string _source;
        private string _target = string.Empty;
        private string _title = string.Empty;
        private string _description = string.Empty;
        private bool _draft, _squash, _squashLocked, _removeSource;

        private GlUserRef _assignee;
        private readonly List<GlUserRef> _reviewers = new List<GlUserRef>();
        private readonly List<string> _labels = new List<string>();

        private List<GlUserRef> _members = new List<GlUserRef>();
        private List<string> _projectLabels = new List<string>();
        private List<string> _branches = new List<string>();
        private List<KeyValuePair<string, string>> _templates = new List<KeyValuePair<string, string>>();
        private List<string> _subjects = new List<string>();

        private bool _loading = true, _busy;
        private string _status;
        private bool _statusError;
        private Vector2 _scroll;

        public static void Open()
        {
            var branch = GitStatusCache.Branch;
            if (string.IsNullOrEmpty(branch))
            {
                EditorUtility.DisplayDialog(L.T("Create Merge Request"), L.T("No current branch (detached HEAD) — there is nothing to create an MR from."), L.T("Got It"));
                return;
            }

            var w = GetWindow<CreateMergeRequestWindow>(true, L.T("New Merge Request"), true);
            w.minSize = new Vector2(560f, 520f);
            w._source = branch;
            w.Show();
            w.LoadAsync();
        }

        void ILocalizedWindow.OnLanguageChanged()
        {
            titleContent = new GUIContent(L.T("New Merge Request"));
            Repaint();
        }

        private GitLabClient Client()
        {
            var baseUrl = GitLabInstance.EffectiveBaseUrl;
            var token = GitLabToken.Read(baseUrl);
            return string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(token) ? null : new GitLabClient(baseUrl, token);
        }

        private async void LoadAsync()
        {
            _loading = true;
            var s = GitLabSettings.instance;
            var client = Client();
            if (client == null) { SetStatus(L.T("API token is not set — set it in Project Settings → Git → GitLab."), true); _loading = false; return; }

            try
            {
                var encoded = GitLabInstance.EffectiveEncodedId;
                var projectTask = client.GetProjectInfoAsync(encoded);
                var membersTask = client.MembersAsync(encoded);
                var labelsTask = client.LabelsAsync(encoded);
                var meTask = client.CurrentUserAsync();
                var branchesTask = GitLabMrGit.RemoteBranchesAsync();

                var (project, projectError) = await projectTask;
                _members = (await membersTask).users;
                _projectLabels = (await labelsTask).labels;
                var me = await meTask;
                _branches = await branchesTask;
                _branches.Remove(_source);

                if (project == null) { SetStatus(projectError, true); return; }

                // Целевая ветка — по настройке проекта.
                switch ((TargetBranchMode)s.targetBranchMode)
                {
                    case TargetBranchMode.Fixed: _target = s.fixedTargetBranch; break;
                    case TargetBranchMode.ParentBranch: _target = await GitLabMrGit.ParentBranchAsync(_source) ?? project.DefaultBranch; break;
                    default: _target = project.DefaultBranch; break;
                }

                _subjects = await GitLabMrGit.CommitSubjectsAsync(_target);
                _title = GitLabWorkflow.DefaultTitle(_source, _subjects);
                _draft = s.createAsDraft;
                (_squash, _squashLocked) = GitLabWorkflow.SquashFor((SquashMode)s.squashMode, project.SquashOption);
                _removeSource = project.RemoveSourceBranchAfterMerge;
                _templates = GitLabMrGit.LocalTemplates();

                switch ((DescriptionMode)s.descriptionMode)
                {
                    case DescriptionMode.Template: _description = _templates.Count > 0 ? _templates[0].Value : string.Empty; break;
                    case DescriptionMode.Commits: _description = GitLabWorkflow.CommitsDescription(_subjects); break;
                    default: _description = string.Empty; break;
                }

                if (s.closesIssueInDescription)
                    _description = GitLabWorkflow.WithClosesLine(_description, GitLabWorkflow.IssueFromBranch(_source));

                if (me != null) _assignee = _members.Find(u => u.Id == me.Id) ?? me;
            }
            catch (Exception e)
            {
                SetStatus(e.Message, true);
            }
            finally
            {
                _loading = false;
                Repaint();
            }
        }

        private void OnGUI()
        {
            EditorGUIUtility.labelWidth = 140f;

            if (_loading)
            {
                EditorGUILayout.Space(20f);
                // После перезагрузки домена null в строке становится "": пустая надпись вместо «читаю».
                EditorGUILayout.LabelField(string.IsNullOrEmpty(_status) ? L.T("Reading project, members and labels…") : _status,
                                           EditorStyles.centeredGreyMiniLabel);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUI.DisabledScope(_busy))
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField(L.T("From Branch"), _source, EditorStyles.boldLabel);
                if (string.IsNullOrEmpty(GitStatusCache.Upstream) || GitStatusCache.Ahead > 0)
                    EditorGUILayout.LabelField(" ", L.T("The branch is not on the server yet or is ahead of it — it will be pushed before creating."), EditorStyles.miniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    _target = EditorGUILayout.TextField(L.T("To Branch"), _target);
                    var rect = GUILayoutUtility.GetRect(new GUIContent("▾"), EditorStyles.miniButton, GUILayout.Width(24f));
                    if (GUI.Button(rect, "▾", EditorStyles.miniButton)) BranchMenu(rect);
                }
                if (_subjects.Count > 0)
                    EditorGUILayout.LabelField(" ", L.F("Commits in MR: {0}", _subjects.Count), EditorStyles.miniLabel);

                EditorGUILayout.Space(4f);
                _title = EditorGUILayout.TextField(L.T("Title"), _title);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel(L.T("Description"));
                    GUILayout.FlexibleSpace();
                    var insert = L.C("Insert ▾");
                    var rect = GUILayoutUtility.GetRect(insert, EditorStyles.miniButton, GUILayout.Width(100f));
                    if (GUI.Button(rect, insert, EditorStyles.miniButton)) DescriptionMenu(rect);
                }
                _description = EditorGUILayout.TextArea(_description, GUILayout.MinHeight(140f));

                EditorGUILayout.Space(6f);
                PeopleRow(L.T("Assignee"), _assignee != null ? _assignee.ToString() : L.Tc("merge request", "unassigned"), AssigneeMenu);
                PeopleRow(L.T("Reviewers"), _reviewers.Count == 0 ? L.T("none") : string.Join(", ", _reviewers.ConvertAll(u => u.ToString())), ReviewersMenu);
                PeopleRow(L.T("Labels"), _labels.Count == 0 ? L.T("none") : string.Join(", ", _labels), LabelsMenu);

                EditorGUILayout.Space(6f);
                _draft = EditorGUILayout.Toggle(L.C("Draft", "Draft: cannot be merged until marked as ready"), _draft);
                using (new EditorGUI.DisabledScope(_squashLocked))
                    _squash = EditorGUILayout.Toggle(_squashLocked ? L.C("Squash", "Set by a project rule in GitLab") : L.C("Squash", "Merge as a single commit"), _squash);
                _removeSource = EditorGUILayout.Toggle(L.T("Delete Branch After Merge"), _removeSource);
            }
            EditorGUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, _statusError ? MessageType.Error : MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(L.T("Cancel"), GUILayout.Width(90f))) Close();
                using (new EditorGUI.DisabledScope(_busy || string.IsNullOrWhiteSpace(_title) || string.IsNullOrWhiteSpace(_target) || _target == _source))
                    if (GUILayout.Button(_busy ? L.T("Creating…") : L.T("Create"), GUILayout.Width(110f))) Create();
            }
            EditorGUILayout.Space(6f);
        }

        private static void PeopleRow(string label, string value, Action<Rect> menu)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, value);
                var change = L.C("Change ▾");
                var rect = GUILayoutUtility.GetRect(change, EditorStyles.miniButton, GUILayout.Width(90f));
                if (GUI.Button(rect, change, EditorStyles.miniButton)) menu(rect);
            }
        }

        private static string M(string s) { return (s ?? string.Empty).Replace('/', '∕'); }

        private void BranchMenu(Rect rect)
        {
            var menu = new GenericMenu();
            if (_branches.Count == 0) menu.AddDisabledItem(new GUIContent(L.T("No branches on the remote — do a Fetch")));
            foreach (var b in _branches)
            {
                var branch = b;
                menu.AddItem(new GUIContent(M(branch)), branch == _target, () => { _target = branch; RefreshSubjects(); });
            }
            menu.DropDown(rect);
        }

        private async void RefreshSubjects()
        {
            _subjects = await GitLabMrGit.CommitSubjectsAsync(_target);
            Repaint();
        }

        private void DescriptionMenu(Rect rect)
        {
            var menu = new GenericMenu();
            foreach (var t in _templates)
            {
                var template = t;
                menu.AddItem(new GUIContent(L.T("Template") + "/" + M(template.Key)), false, () => SetDescription(template.Value));
            }
            if (_templates.Count == 0) menu.AddDisabledItem(new GUIContent(L.T("No templates (.gitlab/merge_request_templates)")));
            menu.AddItem(new GUIContent(L.T("Commit List")), false, () => SetDescription(GitLabWorkflow.CommitsDescription(_subjects)));
            menu.AddItem(new GUIContent(L.T("Clear")), false, () => SetDescription(string.Empty));
            menu.DropDown(rect);
        }

        private void SetDescription(string text)
        {
            if (GitLabSettings.instance.closesIssueInDescription)
                text = GitLabWorkflow.WithClosesLine(text, GitLabWorkflow.IssueFromBranch(_source));
            _description = text;
            GUI.FocusControl(null);
            Repaint();
        }

        private void AssigneeMenu(Rect rect)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(L.T("Don't Assign")), _assignee == null, () => _assignee = null);
            menu.AddSeparator(string.Empty);
            foreach (var u in _members)
            {
                var user = u;
                menu.AddItem(new GUIContent(M(user.ToString()) + "  @" + user.Username), _assignee != null && _assignee.Id == user.Id, () => _assignee = user);
            }
            menu.DropDown(rect);
        }

        private void ReviewersMenu(Rect rect)
        {
            var menu = new GenericMenu();
            foreach (var u in _members)
            {
                var user = u;
                bool on = _reviewers.Exists(r => r.Id == user.Id);
                menu.AddItem(new GUIContent(M(user.ToString()) + "  @" + user.Username), on, () =>
                {
                    if (on) _reviewers.RemoveAll(r => r.Id == user.Id);
                    else _reviewers.Add(user);
                });
            }
            if (_members.Count == 0) menu.AddDisabledItem(new GUIContent(L.T("Members not loaded")));
            menu.DropDown(rect);
        }

        private void LabelsMenu(Rect rect)
        {
            var menu = new GenericMenu();
            foreach (var l in _projectLabels)
            {
                var label = l;
                bool on = _labels.Contains(label);
                menu.AddItem(new GUIContent(M(label)), on, () => { if (on) _labels.Remove(label); else _labels.Add(label); });
            }
            if (_projectLabels.Count == 0) menu.AddDisabledItem(new GUIContent(L.T("The project has no labels")));
            menu.DropDown(rect);
        }

        private async void Create()
        {
            var client = Client();
            if (client == null) return;

            _busy = true;
            SetStatus(null, false);

            try
            {
                // Ветка должна быть на сервере — и со всеми коммитами, что есть локально.
                if (string.IsNullOrEmpty(GitStatusCache.Upstream) || GitStatusCache.Ahead > 0)
                {
                    SetStatus(L.T("Pushing the branch…"), false);
                    var push = await GitOperations.PushAsync();
                    if (!push.Ok) push = await Lev.Git.UI.PushRecovery.OfferAsync(push);
                    await GitStatusCache.RefreshAsync();
                    if (!push.Ok)
                    {
                        SetStatus(L.F("Push failed: {0}\nIf you need to sign in, push from the Git window: the credentials dialog will appear there.", push.Message), true);
                        return;
                    }
                }

                var q = new CreateMrRequest
                {
                    SourceBranch = _source,
                    TargetBranch = _target.Trim(),
                    Title = GitLabWorkflow.DraftTitle(_title.Trim(), _draft),
                    Description = _description,
                    AssigneeId = _assignee != null ? _assignee.Id : 0,
                    RemoveSourceBranch = _removeSource,
                    Squash = _squash
                };
                foreach (var r in _reviewers) q.ReviewerIds.Add(r.Id);
                q.Labels.AddRange(_labels);

                SetStatus(L.T("Creating merge request…"), false);
                var (mr, error) = await client.CreateMergeRequestAsync(GitLabInstance.EffectiveEncodedId, q);
                if (mr == null) { SetStatus(error, true); return; }

                GitLabEvents.RaiseChanged(mr.Iid);
                Diagnostics.Journal.Notice(L.F("Created merge request !{0}: {1}", mr.Iid, mr.Title));
                Close();
            }
            catch (Exception e)
            {
                SetStatus(e.Message, true);
            }
            finally
            {
                _busy = false;
                Repaint();
            }
        }

        private void SetStatus(string text, bool error)
        {
            _status = text;
            _statusError = error;
            Repaint();
        }
    }
}
