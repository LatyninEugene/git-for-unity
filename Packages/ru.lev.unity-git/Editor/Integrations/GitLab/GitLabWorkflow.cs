using System;
using System.Collections.Generic;
using System.Text;

namespace Lev.Git.GitLab
{
    /// <summary>Кто может слить MR из редактора.</summary>
    public enum MergePolicy
    {
        /// <summary>Кнопка активна, если GitLab говорит, что слить можно.</summary>
        AsServerAllows = 0,

        /// <summary>Даже если сервер разрешает — только когда у MR есть одобрение.</summary>
        RequireApproval = 1,

        /// <summary>Слияние остаётся в браузере.</summary>
        NeverInEditor = 2
    }

    public enum AfterMergeAction { Ask = 0, Always = 1, Never = 2 }
    public enum RemoteBranchDeletion { AsInMergeRequest = 0, Always = 1, Never = 2 }
    public enum TargetBranchMode { ProjectDefault = 0, ParentBranch = 1, Fixed = 2 }
    public enum SquashMode { AsProject = 0, Always = 1, Never = 2 }
    public enum DescriptionMode { Template = 0, Commits = 1, Empty = 2 }
    public enum MrListFilter { Mine = 0, ReviewRequested = 1, AllOpen = 2, Merged = 3, Closed = 4 }

    /// <summary>Можно ли слить — с причиной, если нельзя.</summary>
    public sealed class MergeGate
    {
        /// <summary>Показывать кнопку слияния вообще.</summary>
        public bool ShowButton = true;
        public bool CanMerge;

        /// <summary>Сейчас нельзя, но можно поставить «слить, когда пайплайн пройдёт».</summary>
        public bool CanMergeWhenPipelineSucceeds;

        public string Reason;
    }

    /// <summary>
    /// Правила рабочего процесса: кто и когда может слить, как назвать ветку,
    /// что написать в описании. Без Unity — проверяется тестами.
    ///
    /// Правила проекта в редакторе накладываются поверх правил сервера и
    /// никогда их не ослабляют: если GitLab не даёт слить, кнопка неактивна
    /// при любой настройке.
    /// </summary>
    public static class GitLabWorkflow
    {
        public static MergeGate Evaluate(GlMr mr, GlApprovals approvals, MergePolicy policy,
                                         bool requireResolvedDiscussions, bool allowWhenPipelineSucceeds)
        {
            var gate = new MergeGate();

            if (mr == null) { gate.Reason = L.T("No MR selected."); return gate; }

            if (policy == MergePolicy.NeverInEditor)
            {
                gate.ShowButton = false;
                gate.Reason = L.T("Merging from the editor is turned off in the project settings — merge in GitLab.");
                return gate;
            }

            if (mr.State == "merged") { gate.Reason = L.T("The MR is already merged."); return gate; }
            if (!mr.IsOpen) { gate.Reason = L.T("The MR is closed."); return gate; }
            if (!mr.HasDetails) { gate.Reason = L.T("Checking whether it can be merged…"); return gate; }

            if (mr.Draft) { gate.Reason = L.T("This is a draft — mark it as ready first."); return gate; }
            if (mr.HasConflicts) { gate.Reason = L.T("Conflicts with the target branch: rebase or merge the target branch into this one."); return gate; }

            if (requireResolvedDiscussions && !mr.BlockingDiscussionsResolved)
            {
                gate.Reason = L.T("There are unresolved threads — the project rule requires resolving them before merging.");
                return gate;
            }

            if (policy == MergePolicy.RequireApproval && (approvals == null || approvals.ApprovedBy.Count == 0))
            {
                gate.Reason = L.T("There are no approvals — the project rule allows merging only an approved MR.");
                return gate;
            }

            var serverReason = ServerReason(mr);
            bool pipelineRunning = mr.PipelineStatus == "running" || mr.PipelineStatus == "pending" ||
                                   mr.PipelineStatus == "created" || mr.PipelineStatus == "waiting_for_resource" ||
                                   mr.PipelineStatus == "preparing";

            if (serverReason == null && !pipelineRunning)
            {
                gate.CanMerge = true;
                gate.Reason = L.T("Can be merged.");
                return gate;
            }

            bool waitingForPipeline = pipelineRunning || mr.DetailedMergeStatus == "ci_must_pass" || mr.DetailedMergeStatus == "ci_still_running";
            if (waitingForPipeline && allowWhenPipelineSucceeds && mr.PipelineStatus != "failed")
            {
                gate.CanMergeWhenPipelineSucceeds = true;
                gate.Reason = L.T("The pipeline has not passed yet — you can set “merge when pipeline succeeds”.");
                return gate;
            }

            gate.Reason = serverReason ?? L.T("The pipeline has not passed yet.");
            return gate;
        }

        /// <summary>
        /// GitLab ещё считает, можно ли слить. Считает он лениво и асинхронно:
        /// у только что созданного или обновлённого MR это обычное состояние на
        /// несколько секунд — ответ надо переспросить, а не показывать как итог.
        /// </summary>
        public static bool IsChecking(GlMr mr)
        {
            if (mr == null || !mr.IsOpen) return false;
            switch (mr.DetailedMergeStatus)
            {
                case "checking":
                case "unchecked":
                case "preparing":
                case "approvals_syncing":
                    return true;
                case null:
                case "":
                    return mr.MergeStatus == "unchecked" || mr.MergeStatus == "checking" ||
                           mr.MergeStatus == "cannot_be_merged_recheck";
                default:
                    return false;
            }
        }

        /// <summary>Почему GitLab не даёт слить — по-человечески. null — сервер не против.</summary>
        public static string ServerReason(GlMr mr)
        {
            switch (mr.DetailedMergeStatus)
            {
                case null:
                case "":
                    // Старый GitLab без подробной причины.
                    if (mr.MergeStatus == "cannot_be_merged") return L.T("GitLab cannot merge this MR.");
                    if (mr.MergeStatus == "unchecked" || mr.MergeStatus == "checking" || mr.MergeStatus == "cannot_be_merged_recheck")
                        return L.T("GitLab is still checking whether it can be merged.");
                    return null;
                case "mergeable": return null;
                case "not_approved": return L.T("Not enough approvals under the GitLab rules.");
                case "discussions_not_resolved": return L.T("There are unresolved threads — GitLab requires resolving them.");
                case "draft_status": return L.T("This is a draft — mark it as ready first.");
                case "ci_must_pass": return L.T("GitLab requires a successful pipeline.");
                case "ci_still_running": return L.T("The pipeline is still running.");
                case "conflict": return L.T("Conflicts with the target branch.");
                case "need_rebase": return L.T("A rebase onto the target branch is needed.");
                case "checking":
                case "unchecked":
                case "preparing":
                case "approvals_syncing": return L.T("GitLab is still checking whether it can be merged.");
                case "blocked_status": return L.T("The MR is blocked by another MR.");
                case "requested_changes": return L.T("A reviewer requested changes.");
                case "not_open": return L.T("The MR is not open.");
                case "jira_association_missing": return L.T("There is no linked Jira issue — the project rules require it.");
                case "security_policy_violations": return L.T("The project security policies are violated.");
                case "commits_status": return L.T("Commits are still being checked.");
                default: return L.F("GitLab does not allow merging: {0}.", mr.DetailedMergeStatus);
            }
        }

        // ---------------------------------------------------------- ветки ---

        /// <summary>Готовые шаблоны имени ветки для выпадающего списка.</summary>
        public static readonly string[] BranchPresets =
        {
            "{iid}-{title}",
            "feature/{iid}-{title}",
            "{type}/{iid}-{title}",
            "{user}/{iid}-{title}"
        };

        /// <summary>Имя ветки для задачи по шаблону проекта. Результат годится для git.</summary>
        public static string BranchName(string template, int iid, string title, IEnumerable<string> labels,
                                         string username, bool transliterate, int maxTitleLength, string separator)
        {
            if (string.IsNullOrWhiteSpace(template)) template = BranchPresets[0];
            if (string.IsNullOrEmpty(separator)) separator = "-";

            var slug = Slug(title, transliterate, maxTitleLength, separator);
            var type = TypeOf(labels);
            var user = Slug(username, transliterate, 40, separator);

            // Канонические подстановки — английские; русские понимаются по-прежнему:
            // шаблоны с ними уже лежат в настройках проектов.
            var name = template
                .Replace("{iid}", iid.ToString()).Replace("{номер}", iid.ToString()) // loc-ignore
                .Replace("{title}", slug).Replace("{название}", slug) // loc-ignore
                .Replace("{type}", type).Replace("{тип}", type) // loc-ignore
                .Replace("{user}", user).Replace("{пользователь}", user); // loc-ignore

            return SanitizeRef(name, separator);
        }

        /// <summary>«Исправить прыжок!» → «ispravit-pryzhok».</summary>
        public static string Slug(string text, bool transliterate, int maxLength, string separator)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (string.IsNullOrEmpty(separator)) separator = "-";

            var source = transliterate ? Transliterate(text.ToLowerInvariant()) : text.ToLowerInvariant();
            var sb = new StringBuilder();
            bool pendingSeparator = false;

            foreach (char c in source)
            {
                bool keep = transliterate ? (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') : char.IsLetterOrDigit(c);
                if (!keep)
                {
                    pendingSeparator = sb.Length > 0;
                    continue;
                }

                if (pendingSeparator) { sb.Append(separator); pendingSeparator = false; }
                sb.Append(c);
            }

            var slug = sb.ToString();
            if (maxLength > 0 && slug.Length > maxLength)
            {
                // Обрезаем по границе слова, если она не слишком далеко.
                int cut = slug.LastIndexOf(separator, maxLength, StringComparison.Ordinal);
                slug = cut >= maxLength / 2 ? slug.Substring(0, cut) : slug.Substring(0, maxLength);
            }

            return slug.Trim(separator.ToCharArray());
        }

        private static readonly Dictionary<char, string> Cyrillic = new Dictionary<char, string>
        {
            {'а',"a"},{'б',"b"},{'в',"v"},{'г',"g"},{'д',"d"},{'е',"e"},{'ё',"e"},{'ж',"zh"},{'з',"z"},{'и',"i"},
            {'й',"y"},{'к',"k"},{'л',"l"},{'м',"m"},{'н',"n"},{'о',"o"},{'п',"p"},{'р',"r"},{'с',"s"},{'т',"t"},
            {'у',"u"},{'ф',"f"},{'х',"h"},{'ц',"ts"},{'ч',"ch"},{'ш',"sh"},{'щ',"sch"},{'ъ',""},{'ы',"y"},{'ь',""},
            {'э',"e"},{'ю',"yu"},{'я',"ya"}
        };

        public static string Transliterate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                var lower = char.ToLowerInvariant(c);
                if (Cyrillic.TryGetValue(lower, out var latin)) sb.Append(latin);
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Тип задачи по её меткам — для {тип} в имени ветки.</summary>
        public static string TypeOf(IEnumerable<string> labels)
        {
            if (labels != null)
            {
                foreach (var raw in labels)
                {
                    var l = (raw ?? string.Empty).ToLowerInvariant();
                    if (l.Contains("bug") || l.Contains("баг") || l.Contains("ошибк") || l.Contains("defect")) return "bug"; // loc-ignore
                    if (l.Contains("feature") || l.Contains("фич") || l.Contains("enhancement")) return "feature"; // loc-ignore
                    if (l.Contains("doc") || l.Contains("документ")) return "docs"; // loc-ignore
                    if (l.Contains("refactor") || l.Contains("рефактор")) return "refactor"; // loc-ignore
                }
            }
            return "task";
        }

        /// <summary>Приводит имя к тому, что git примет как ветку.</summary>
        public static string SanitizeRef(string name, string separator)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c) || c < 32 || "~^:?*[\\\"'".IndexOf(c) >= 0) sb.Append(separator);
                else sb.Append(c);
            }

            var s = sb.ToString();
            while (s.Contains("//")) s = s.Replace("//", "/");
            while (s.Contains("..")) s = s.Replace("..", ".");
            while (s.Contains(separator + separator)) s = s.Replace(separator + separator, separator);
            s = s.Replace("/" + separator, "/").Replace(separator + "/", "/");
            s = s.Trim('/', '.').Trim(separator.ToCharArray());
            if (s.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 5);
            return s;
        }

        // ---------------------------------------------------------- draft ---

        private static readonly System.Text.RegularExpressions.Regex DraftPrefix = new System.Text.RegularExpressions.Regex(
            @"^\s*(\[draft\]|\(draft\)|draft:|draft\s+-|\[wip\]|wip:)\s*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Заголовок с пометкой draft или без неё. GitLab хранит draft именно
        /// префиксом заголовка — так он переключается и через API.
        /// </summary>
        public static string DraftTitle(string title, bool draft)
        {
            var clean = DraftPrefix.Replace(title ?? string.Empty, string.Empty);
            while (DraftPrefix.IsMatch(clean)) clean = DraftPrefix.Replace(clean, string.Empty);
            return draft ? "Draft: " + clean : clean;
        }

        /// <summary>
        /// Squash для нового MR: настройка редактора поверх правила проекта в
        /// GitLab. Правило «никогда» или «всегда» на сервере сильнее — тогда
        /// флажок заблокирован.
        /// </summary>
        public static (bool value, bool locked) SquashFor(SquashMode mode, string projectOption)
        {
            if (projectOption == "never") return (false, true);
            if (projectOption == "always") return (true, true);

            switch (mode)
            {
                case SquashMode.Always: return (true, false);
                case SquashMode.Never: return (false, false);
                default: return (projectOption == "default_on", false);
            }
        }

        private static readonly System.Text.RegularExpressions.Regex IssueInBranch =
            new System.Text.RegularExpressions.Regex(@"(?:^|/)(\d+)(?:[-_]|$)");

        /// <summary>Номер задачи из имени ветки: «feature/42-fix-jump» → 42. 0 — не найден.</summary>
        public static int IssueFromBranch(string branch)
        {
            if (string.IsNullOrEmpty(branch)) return 0;
            var m = IssueInBranch.Match(branch);
            return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
        }

        /// <summary>
        /// Заголовок нового MR — как это делает сам GitLab: один коммит — его
        /// заголовок, иначе имя ветки человеческими словами.
        /// </summary>
        public static string DefaultTitle(string branch, IList<string> subjects)
        {
            if (subjects != null && subjects.Count == 1 && !string.IsNullOrWhiteSpace(subjects[0])) return subjects[0].Trim();
            if (string.IsNullOrEmpty(branch)) return string.Empty;

            var last = branch.Substring(branch.LastIndexOf('/') + 1);
            last = System.Text.RegularExpressions.Regex.Replace(last, @"^\d+[-_]", string.Empty);
            last = last.Replace('-', ' ').Replace('_', ' ').Trim();
            if (last.Length == 0) return branch;
            return char.ToUpperInvariant(last[0]) + last.Substring(1);
        }

        // ------------------------------------------------------- описание ---

        /// <summary>Описание нового MR списком коммитов ветки.</summary>
        public static string CommitsDescription(IEnumerable<string> subjects)
        {
            var sb = new StringBuilder();
            if (subjects != null)
                foreach (var s in subjects)
                    if (!string.IsNullOrWhiteSpace(s)) sb.Append("- ").Append(s.Trim()).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>Добавляет «Closes #N», если его ещё нет, — GitLab закроет задачу при слиянии.</summary>
        public static string WithClosesLine(string description, int issueIid)
        {
            if (issueIid <= 0) return description ?? string.Empty;
            var line = "Closes #" + issueIid;
            description = description ?? string.Empty;
            if (description.IndexOf(line, StringComparison.OrdinalIgnoreCase) >= 0) return description;
            return description.Length == 0 ? line : description.TrimEnd() + "\n\n" + line;
        }
    }
}
