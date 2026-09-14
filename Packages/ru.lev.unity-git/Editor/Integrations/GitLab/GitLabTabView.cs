using Lev.Git.UI;
using UnityEditor;
using UnityEngine.UIElements;

namespace Lev.Git.GitLab
{
    /// <summary>Вкладка GitLab: merge request'ы и задачи — переключателем сверху.</summary>
    public sealed class GitLabTabView : VisualElement
    {
        private const string ModePref = "LevGit.GitLab.ShowIssues";

        private readonly GitLabView _mergeRequests;
        private readonly IssuesView _issues;
        private readonly Button _mrButton, _issuesButton;
        private bool _showIssues;

        public GitLabTabView(IGitHost host)
        {
            style.flexGrow = 1f;
            style.minHeight = 0f;

            var bar = Ui.Box("modebar");
            _mrButton = Ui.Action(L.T("Merge Requests"), () => SetMode(false));
            _issuesButton = Ui.Action(L.T("Issues"), () => SetMode(true));
            bar.Add(_mrButton);
            bar.Add(_issuesButton);
            bar.Add(Ui.Spacer());
            Add(bar);

            _mergeRequests = new GitLabView(host);
            _issues = new IssuesView(host);
            Add(_mergeRequests);
            Add(_issues);

            _showIssues = EditorPrefs.GetBool(ModePref, false);
            Apply();

            // Попросили показать MR (значком у ветки, из задачи) — переключаемся на MR.
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                GitLabEvents.OpenMergeRequestRequested += OnOpenMergeRequest;
                GitLabEvents.OpenIssueRequested += Refresh;
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                GitLabEvents.OpenMergeRequestRequested -= OnOpenMergeRequest;
                GitLabEvents.OpenIssueRequested -= Refresh;
            });
        }

        /// <summary>
        /// Обе половины живут всё время и только прячутся: ушёл из MR в задачу и
        /// вернулся — MR выбран тот же, и искать его заново не нужно.
        /// </summary>
        public void Refresh()
        {
            if (GitLabEvents.PendingOpenIid > 0 && _showIssues)
            {
                SetMode(false);
                return;
            }

            if (GitLabEvents.PendingOpenIssueIid > 0 && !_showIssues)
            {
                SetMode(true);
                return;
            }

            if (_showIssues) _issues.Refresh();
            else _mergeRequests.Refresh();
        }

        private void OnOpenMergeRequest(int iid)
        {
            if (_showIssues) SetMode(false);
        }

        private void SetMode(bool issues)
        {
            _showIssues = issues;
            EditorPrefs.SetBool(ModePref, issues);
            Apply();
            Refresh();
        }

        private void Apply()
        {
            _mergeRequests.style.display = _showIssues ? DisplayStyle.None : DisplayStyle.Flex;
            _issues.style.display = _showIssues ? DisplayStyle.Flex : DisplayStyle.None;
            _mrButton.EnableInClassList("act--primary", !_showIssues);
            _issuesButton.EnableInClassList("act--primary", _showIssues);
        }
    }
}
