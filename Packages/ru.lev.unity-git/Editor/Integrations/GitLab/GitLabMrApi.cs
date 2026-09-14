using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lev.Git.GitLab
{
    /// <summary>Запрос на создание merge request'а.</summary>
    public sealed class CreateMrRequest
    {
        public string SourceBranch;
        public string TargetBranch;
        public string Title;
        public string Description;
        public long AssigneeId;
        public readonly List<long> ReviewerIds = new List<long>();
        public readonly List<string> Labels = new List<string>();
        public bool RemoveSourceBranch;
        public bool Squash;
    }

    /// <summary>Список MR изменился — вкладке пора перечитать его и выбрать нужный.</summary>
    public static class GitLabEvents
    {
        public static event Action<int> MergeRequestsChanged;

        public static void RaiseChanged(int iid)
        {
            MergeRequestsChanged?.Invoke(iid);
        }

        /// <summary>Задача создана или изменена — списку задач пора перечитаться и выбрать её.</summary>
        public static event Action<int> IssuesChanged;

        public static void RaiseIssuesChanged(int iid)
        {
            IssuesChanged?.Invoke(iid);
        }

        /// <summary>
        /// MR, который попросили показать — значком у ветки, например. Пока
        /// вкладка GitLab не построена, номер ждёт здесь, и вкладка заберёт его сама.
        /// </summary>
        public static int PendingOpenIid;

        public static event Action<int> OpenMergeRequestRequested;

        /// <summary>Задача, которую попросили показать, и MR, к которому потом вернуться (0 — неоткуда).</summary>
        public static int PendingOpenIssueIid, PendingOpenIssueFromMr;

        public static event Action OpenIssueRequested;

        public static void OpenIssue(int iid, int fromMergeRequest)
        {
            PendingOpenIssueIid = iid;
            PendingOpenIssueFromMr = fromMergeRequest;
            GitWindow.ShowIntegration("gitlab");
            OpenIssueRequested?.Invoke();
        }

        public static void OpenMergeRequest(int iid)
        {
            PendingOpenIid = iid;
            GitWindow.ShowIntegration("gitlab");
            OpenMergeRequestRequested?.Invoke(iid);
        }
    }

    /// <summary>
    /// Действия с merge request'ами через REST API. Каждый метод возвращает
    /// понятную причину отказа, а не код HTTP: 405 у слияния значит одно,
    /// у rebase — другое.
    /// </summary>
    public static class GitLabMrApi
    {
        private static string P(string encodedId) { return "/projects/" + encodedId; }

        public static async Task<(GlProjectInfo project, string error)> GetProjectInfoAsync(this GitLabClient c, string encodedId)
        {
            var r = await c.GetAsync(P(encodedId));
            return r.Ok ? (GitLabModels.ParseProject(r.Body), null) : (null, Explain(r, L.T("Could not read the project")));
        }

        public static async Task<(List<GlUserRef> users, string error)> MembersAsync(this GitLabClient c, string encodedId)
        {
            var r = await c.GetAsync(P(encodedId) + "/members/all?per_page=100");
            return r.Ok ? (GitLabModels.ParseUsers(r.Body), null) : (new List<GlUserRef>(), Explain(r, L.T("Could not read project members")));
        }

        public static async Task<(List<string> labels, string error)> LabelsAsync(this GitLabClient c, string encodedId)
        {
            var r = await c.GetAsync(P(encodedId) + "/labels?per_page=100");
            return r.Ok ? (GitLabModels.ParseLabelNames(r.Body), null) : (new List<string>(), Explain(r, L.T("Could not read labels")));
        }

        public static async Task<GlUserRef> CurrentUserAsync(this GitLabClient c)
        {
            var r = await c.GetAsync("/user");
            return r.Ok ? GitLabModels.ParseUser(r.Body) : null;
        }

        public static async Task<(GlMr mr, string error)> CreateMergeRequestAsync(this GitLabClient c, string encodedId, CreateMrRequest q)
        {
            var body = JsonText.Obj(
                ("source_branch", q.SourceBranch),
                ("target_branch", q.TargetBranch),
                ("title", q.Title),
                ("description", q.Description ?? string.Empty),
                ("assignee_id", q.AssigneeId > 0 ? (object)q.AssigneeId : null),
                ("reviewer_ids", q.ReviewerIds.Count > 0 ? q.ReviewerIds : null),
                ("labels", q.Labels.Count > 0 ? string.Join(",", q.Labels) : null),
                ("remove_source_branch", q.RemoveSourceBranch),
                ("squash", q.Squash));

            var r = await c.SendAsync("POST", P(encodedId) + "/merge_requests", body);
            if (r.Ok) return (GitLabModels.ParseMergeRequest(r.Body), null);

            if (r.Code == 409)
                return (null, L.F("Branch “{0}” already has an open merge request.", q.SourceBranch));
            return (null, Explain(r, L.T("Could not create the merge request")));
        }

        /// <param name="whenPipelineSucceeds">Не сливать сейчас, а поставить в очередь до успешного пайплайна.</param>
        public static async Task<(GlMr mr, string error)> MergeAsync(this GitLabClient c, string encodedId, GlMr mr,
                                                                     bool removeSourceBranch, bool whenPipelineSucceeds)
        {
            // sha — защита от гонки: если в MR пришли коммиты, которых человек не видел,
            // GitLab откажет, а не сольёт их молча. Параметр очереди назывался по-разному
            // в разных версиях — неизвестный сервер просто пропускает.
            var body = JsonText.Obj(
                ("sha", mr.HeadSha ?? mr.Sha),
                ("should_remove_source_branch", removeSourceBranch),
                ("merge_when_pipeline_succeeds", whenPipelineSucceeds ? (object)true : null),
                ("auto_merge", whenPipelineSucceeds ? (object)true : null));

            var r = await c.SendAsync("PUT", P(encodedId) + "/merge_requests/" + mr.Iid + "/merge", body);
            if (r.Ok) return (GitLabModels.ParseMergeRequest(r.Body), null);

            switch (r.Code)
            {
                case 401:
                case 403: return (null, L.T("You don't have permission to merge this merge request."));
                case 405: return (null, L.F("GitLab does not allow merging now: {0}.", GitLabModels.ErrorMessage(r.Body) ?? L.T("the MR is not ready to be merged")));
                case 406: return (null, L.T("Merge failed: conflicts with the target branch."));
                case 409: return (null, L.T("The MR has new commits — refresh the card and check them before merging."));
                case 422: return (null, L.F("GitLab rejected the merge: {0}", GitLabModels.ErrorMessage(r.Body) ?? r.Error));
                default: return (null, Explain(r, L.T("Merge failed")));
            }
        }

        /// <summary>
        /// Просит GitLab пересчитать, можно ли слить MR. Пересчёт идёт в фоне:
        /// результат виден в следующем запросе самого MR, а не в этом ответе.
        /// </summary>
        public static async Task RequestMergeStatusRecheckAsync(this GitLabClient c, string encodedId, int iid)
        {
            await c.GetAsync(P(encodedId) + "/merge_requests?iids[]=" + iid + "&with_merge_status_recheck=true");
        }

        public static async Task<string> CancelAutoMergeAsync(this GitLabClient c, string encodedId, int iid)
        {
            var r = await c.SendAsync("POST", P(encodedId) + "/merge_requests/" + iid + "/cancel_merge_when_pipeline_succeeds", "{}");
            return r.Ok ? null : Explain(r, L.T("Could not cancel auto-merge"));
        }

        /// <summary>Rebase на стороне сервера. Выполняется асинхронно: результат виден позже.</summary>
        public static async Task<string> RebaseAsync(this GitLabClient c, string encodedId, int iid)
        {
            var r = await c.SendAsync("PUT", P(encodedId) + "/merge_requests/" + iid + "/rebase", "{}");
            if (r.Ok) return null;
            return r.Code == 403 ? L.T("You don't have permission to rebase this MR.") : Explain(r, L.T("Could not start rebase"));
        }

        public static async Task<(GlMr mr, string error)> SetDraftAsync(this GitLabClient c, string encodedId, GlMr mr, bool draft)
        {
            var body = JsonText.Obj(("title", GitLabWorkflow.DraftTitle(mr.Title, draft)));
            var r = await c.SendAsync("PUT", P(encodedId) + "/merge_requests/" + mr.Iid, body);
            return r.Ok ? (GitLabModels.ParseMergeRequest(r.Body), null) : (null, Explain(r, L.T("Could not change the draft state")));
        }

        public static async Task<(GlMr mr, string error)> CloseAsync(this GitLabClient c, string encodedId, int iid)
        {
            var r = await c.SendAsync("PUT", P(encodedId) + "/merge_requests/" + iid, JsonText.Obj(("state_event", "close")));
            return r.Ok ? (GitLabModels.ParseMergeRequest(r.Body), null) : (null, Explain(r, L.T("Could not close the merge request")));
        }

        public static string Explain(GitLabResponse r, string action)
        {
            if (r.Code == 0) return action + ": " + r.Error;
            if (r.Code == 401) return action + ": " + L.T("GitLab did not accept the token.");
            if (r.Code == 403) return action + ": " + L.T("no permission.");
            var message = GitLabModels.ErrorMessage(r.Body);
            return action + ": " + (message ?? r.Error);
        }
    }
}
