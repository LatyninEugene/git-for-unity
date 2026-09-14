using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lev.Git.GitLab
{
    /// <summary>
    /// Ревью и задачи через REST API: обсуждения MR, ответы, решение тредов,
    /// одобрение; список, создание и состояние задач; загрузка скриншотов.
    /// </summary>
    public static class GitLabReviewApi
    {
        private static string Mr(string encodedId, int iid) { return "/projects/" + encodedId + "/merge_requests/" + iid; }
        private static string Issue(string encodedId, int iid) { return "/projects/" + encodedId + "/issues/" + iid; }

        // ------------------------------------------------------------ обсуждения ---

        /// <summary>Чьи обсуждения: «/projects/:id/merge_requests/:iid» или «/projects/:id/issues/:iid» — API у них одинаковый.</summary>
        public static string TargetPath(string encodedId, bool issue, int iid)
        {
            return issue ? Issue(encodedId, iid) : Mr(encodedId, iid);
        }

        public static async Task<(List<GlDiscussion> items, string error)> DiscussionsAsync(this GitLabClient c, string targetPath)
        {
            var all = new List<GlDiscussion>();
            for (int page = 1; page <= 20; page++)
            {
                var r = await c.GetAsync(targetPath + "/discussions?per_page=100&page=" + page);
                if (!r.Ok) return (all, GitLabMrApi.Explain(r, L.T("Could not read threads")));

                var chunk = GitLabReviewModels.ParseDiscussions(r.Body);
                all.AddRange(chunk);
                if (chunk.Count < 100) break;
            }
            return (all, null);
        }

        /// <summary>Новый тред: общий (position = null) или к строке diff — только у MR.</summary>
        public static async Task<(GlDiscussion discussion, string error)> StartDiscussionAsync(
            this GitLabClient c, string targetPath, string body, GlNotePosition position)
        {
            var json = JsonText.Obj(
                ("body", body),
                ("position", position != null ? new JsonRaw(ReviewAnchors.PositionJson(position)) : null));

            var r = await c.SendAsync("POST", targetPath + "/discussions", json);
            if (r.Ok) return (GitLabReviewModels.ParseDiscussion(r.Body), null);
            return (null, GitLabMrApi.Explain(r, position != null ? L.T("Could not send the line comment") : L.T("Could not send the comment")));
        }

        public static Task<(GlDiscussion discussion, string error)> StartDiscussionAsync(
            this GitLabClient c, string encodedId, int iid, string body, GlNotePosition position)
        {
            return c.StartDiscussionAsync(Mr(encodedId, iid), body, position);
        }

        public static async Task<string> ReplyAsync(this GitLabClient c, string targetPath, string discussionId, string body)
        {
            var r = await c.SendAsync("POST", targetPath + "/discussions/" + Uri.EscapeDataString(discussionId) + "/notes",
                                      JsonText.Obj(("body", body)));
            return r.Ok ? null : GitLabMrApi.Explain(r, L.T("Could not send the reply"));
        }

        public static async Task<string> ResolveAsync(this GitLabClient c, string targetPath, string discussionId, bool resolved)
        {
            var r = await c.SendAsync("PUT", targetPath + "/discussions/" + Uri.EscapeDataString(discussionId) +
                                             "?resolved=" + (resolved ? "true" : "false"), JsonText.Obj(("resolved", resolved)));
            return r.Ok ? null : GitLabMrApi.Explain(r, resolved ? L.T("Could not mark the thread as resolved") : L.T("Could not reopen the thread"));
        }

        // ------------------------------------------------------------ одобрение ---

        /// <param name="sha">Вершина, которую человек видел: если в MR пришли новые коммиты, GitLab откажет.</param>
        public static async Task<string> ApproveAsync(this GitLabClient c, string encodedId, int iid, string sha)
        {
            var r = await c.SendAsync("POST", Mr(encodedId, iid) + "/approve", JsonText.Obj(("sha", sha)));
            if (r.Ok) return null;
            switch (r.Code)
            {
                case 401: return L.T("Cannot approve: it is your own MR, or you don't have permission to approve in this project.");
                case 409: return L.T("The MR has new commits — refresh the card and look at them before approving.");
                default: return GitLabMrApi.Explain(r, L.T("Could not send the approval"));
            }
        }

        public static async Task<string> UnapproveAsync(this GitLabClient c, string encodedId, int iid)
        {
            var r = await c.SendAsync("POST", Mr(encodedId, iid) + "/unapprove", "{}");
            return r.Ok ? null : GitLabMrApi.Explain(r, L.T("Could not revoke the approval"));
        }

        // ---------------------------------------------------------------- значки ---

        /// <summary>Все открытые MR проекта — для значков у веток.</summary>
        public static async Task<(List<GlMr> items, string error)> OpenMergeRequestsAsync(this GitLabClient c, string encodedId)
        {
            var r = await c.GetAsync("/projects/" + encodedId + "/merge_requests?state=opened&scope=all&per_page=100&order_by=updated_at");
            return r.Ok ? (GitLabModels.ParseMergeRequests(r.Body), null) : (new List<GlMr>(), GitLabMrApi.Explain(r, L.T("Could not read merge requests")));
        }

        // ---------------------------------------------------------------- задачи ---

        public static async Task<(List<GlIssue> items, string error)> ListIssuesAsync(this GitLabClient c, string encodedId,
                                                                                       IssueListFilter filter, string search)
        {
            var r = await c.GetAsync(IssueQuery.ListPath(encodedId, filter, search));
            if (!r.Ok) return (new List<GlIssue>(), r.Code == 401 ? L.T("GitLab did not accept the token.") : GitLabMrApi.Explain(r, L.T("Could not read issues")));
            return (GitLabReviewModels.ParseIssues(r.Body), null);
        }

        public static async Task<(GlIssue issue, string error)> GetIssueAsync(this GitLabClient c, string encodedId, int iid)
        {
            var r = await c.GetAsync(Issue(encodedId, iid));
            if (r.Ok) return (GitLabReviewModels.ParseIssue(r.Body), null);
            return (null, r.Code == 404 ? L.F("Issue #{0} not found.", iid) : GitLabMrApi.Explain(r, L.T("Could not read the issue")));
        }

        public static async Task<(GlIssue issue, string error)> CreateIssueAsync(this GitLabClient c, string encodedId, string title,
            string description, IList<long> assigneeIds, IList<string> labels, bool confidential)
        {
            var body = JsonText.Obj(
                ("title", title),
                ("description", description ?? string.Empty),
                ("assignee_ids", assigneeIds != null && assigneeIds.Count > 0 ? assigneeIds : null),
                ("labels", labels != null && labels.Count > 0 ? string.Join(",", labels) : null),
                ("confidential", confidential ? (object)true : null));

            var r = await c.SendAsync("POST", "/projects/" + encodedId + "/issues", body);
            return r.Ok ? (GitLabReviewModels.ParseIssue(r.Body), null) : (null, GitLabMrApi.Explain(r, L.T("Could not create the issue")));
        }

        public static async Task<(GlIssue issue, string error)> SetIssueStateAsync(this GitLabClient c, string encodedId, int iid, bool close)
        {
            var r = await c.SendAsync("PUT", Issue(encodedId, iid), JsonText.Obj(("state_event", close ? "close" : "reopen")));
            return r.Ok ? (GitLabReviewModels.ParseIssue(r.Body), null)
                        : (null, GitLabMrApi.Explain(r, close ? L.T("Could not close the issue") : L.T("Could not reopen the issue")));
        }

        public static async Task<(GlIssue issue, string error)> AssignIssueAsync(this GitLabClient c, string encodedId, int iid, IList<long> assigneeIds)
        {
            var r = await c.SendAsync("PUT", Issue(encodedId, iid), JsonText.Obj(("assignee_ids", assigneeIds ?? new List<long>())));
            return r.Ok ? (GitLabReviewModels.ParseIssue(r.Body), null) : (null, GitLabMrApi.Explain(r, L.T("Could not set the assignee")));
        }

        /// <summary>Задачи, которые GitLab закроет при слиянии MR.</summary>
        public static async Task<List<GlIssue>> ClosesIssuesAsync(this GitLabClient c, string encodedId, int iid)
        {
            var r = await c.GetAsync(Mr(encodedId, iid) + "/closes_issues");
            return r.Ok ? GitLabReviewModels.ParseIssues(r.Body) : new List<GlIssue>();
        }

        /// <summary>MR, которые упоминают задачу или закроют её.</summary>
        public static async Task<List<GlMr>> RelatedMergeRequestsAsync(this GitLabClient c, string encodedId, int iid)
        {
            var r = await c.GetAsync(Issue(encodedId, iid) + "/related_merge_requests");
            return r.Ok ? GitLabModels.ParseMergeRequests(r.Body) : new List<GlMr>();
        }

        /// <summary>Файл в проект GitLab. Возвращает markdown-ссылку для описания.</summary>
        public static async Task<(string markdown, string error)> UploadFileAsync(this GitLabClient c, string encodedId,
                                                                                  string fileName, string contentType, byte[] content)
        {
            var r = await c.UploadAsync("/projects/" + encodedId + "/uploads", fileName, contentType, content);
            if (!r.Ok) return (null, r.Code == 413 ? L.T("The file is too large for this GitLab.") : GitLabMrApi.Explain(r, L.T("Could not upload the file")));

            var markdown = GitLabReviewModels.ParseUploadMarkdown(r.Body);
            return markdown != null ? (markdown, null) : (null, L.T("GitLab did not return a link to the uploaded file."));
        }
    }
}
