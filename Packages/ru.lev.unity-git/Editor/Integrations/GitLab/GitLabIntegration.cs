using System;
using System.Collections.Generic;
using Lev.Git.UI;
using UnityEngine.UIElements;

namespace Lev.Git.GitLab
{
    /// <summary>GitLab поверх ядра git: веб-адреса, страницы ключей и токенов, своя вкладка, значки MR у веток.</summary>
    public sealed class GitLabIntegration : IGitIntegration, IGitBranchBadges
    {
        public const string SettingsPagePath = "Project/Git/GitLab";

        public string Id => "gitlab";
        public string DisplayName => "GitLab";
        public string Description => L.T("Merge requests, issues and reviews for self-hosted GitLab and gitlab.com.");

        public GitLabIntegration()
        {
            // Создали или слили MR — значки у веток устарели.
            GitLabEvents.MergeRequestsChanged += _ => LoadBadges(true);
        }

        public bool Enabled
        {
            get { return GitLabSettings.instance.enabled; }
            set
            {
                if (GitLabSettings.instance.enabled == value) return;
                GitLabSettings.instance.enabled = value;
                GitLabSettings.instance.Persist();
                GitIntegrations.NotifyChanged();
            }
        }

        public string SettingsPath => SettingsPagePath;

        public bool Recognizes(GitRemote remote)
        {
            return remote != null && !string.IsNullOrEmpty(remote.Host) &&
                   remote.Host.IndexOf("gitlab", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public IGitTab CreateTab(IGitHost host)
        {
            return new Tab(new GitLabTabView(host));
        }

        private sealed class Tab : IGitTab
        {
            private readonly GitLabTabView _view;
            public Tab(GitLabTabView view) { _view = view; }
            public string Title => "GitLab";
            public VisualElement Element => _view;
            public int Badge => 0;
            public void Refresh() { _view.Refresh(); }
        }

        private static string Base => GitLabInstance.EffectiveBaseUrl;
        private static string Web => GitLabInstance.EffectiveProjectWebUrl;

        public string SshKeysUrl => string.IsNullOrEmpty(Base) ? null : Base + "/-/user_settings/ssh_keys";
        public string TokenPageUrl => string.IsNullOrEmpty(Base) ? null : Base + "/-/user_settings/personal_access_tokens";
        public string HttpCloneUrl => string.IsNullOrEmpty(Web) ? null : Web + ".git";

        public string CommitUrl(string sha)
        {
            return string.IsNullOrEmpty(Web) || string.IsNullOrEmpty(sha) ? null : Web + "/-/commit/" + sha;
        }

        public string BranchUrl(string branch)
        {
            return string.IsNullOrEmpty(Web) || string.IsNullOrEmpty(branch) ? null : Web + "/-/tree/" + Uri.EscapeDataString(branch);
        }

        public string FileUrl(string gitPath, string gitRef)
        {
            if (string.IsNullOrEmpty(Web) || string.IsNullOrEmpty(gitPath)) return null;
            return Web + "/-/blob/" + Uri.EscapeDataString(string.IsNullOrEmpty(gitRef) ? "HEAD" : gitRef) + "/" + gitPath;
        }

        // ------------------------------------------------------ значки у веток ---

        /// <summary>Не чаще раза в минуту: журнал перерисовывается часто, а сервер у всех один.</summary>
        private const double BadgesRefreshSeconds = 60;

        private Dictionary<string, GlMr> _badges = new Dictionary<string, GlMr>();
        private DateTime _badgesAt = DateTime.MinValue;
        private bool _badgesLoading;

        public event Action BadgesChanged;

        public GitRefBadge BadgeFor(string branch)
        {
            if (branch == null || !_badges.TryGetValue(branch, out var mr)) return null;
            var iid = mr.Iid;
            return new GitRefBadge
            {
                Text = BranchBadges.Text(mr),
                Tooltip = BranchBadges.Tooltip(mr),
                Open = () => GitLabEvents.OpenMergeRequest(iid)
            };
        }

        public void RequestBadges()
        {
            LoadBadges(false);
        }

        private async void LoadBadges(bool force)
        {
            if (_badgesLoading || !Enabled) return;
            if (!force && (DateTime.UtcNow - _badgesAt).TotalSeconds < BadgesRefreshSeconds) return;

            var baseUrl = GitLabInstance.EffectiveBaseUrl;
            var encoded = GitLabInstance.EffectiveEncodedId;
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(encoded)) return;

            var token = GitLabToken.Read(baseUrl);
            if (string.IsNullOrEmpty(token)) return;

            _badgesLoading = true;
            _badgesAt = DateTime.UtcNow;
            try
            {
                var (items, error) = await new GitLabClient(baseUrl, token).OpenMergeRequestsAsync(encoded);
                if (error != null) return;

                _badges = BranchBadges.Map(items);
                BadgesChanged?.Invoke();
            }
            catch
            {
                // Значки — подсказка, а не функция: без сети журнал просто без них.
            }
            finally
            {
                _badgesLoading = false;
            }
        }
    }
}
