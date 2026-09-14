using System;
using System.Collections.Generic;
using System.Globalization;

namespace Lev.Git.GitLab
{
    public sealed class GlUserRef
    {
        public long Id;
        public string Username;
        public string Name;

        public override string ToString() { return string.IsNullOrEmpty(Name) ? Username : Name; }
    }

    /// <summary>Merge request в том объёме, что нужен окну: список и карточка.</summary>
    public sealed class GlMr
    {
        public long Id;
        public int Iid;
        public string Title = string.Empty;
        public string Description = string.Empty;
        public string State;
        public string SourceBranch;
        public string TargetBranch;
        public string WebUrl;
        public string Sha;

        /// <summary>Старое поле: can_be_merged, cannot_be_merged, unchecked.</summary>
        public string MergeStatus;

        /// <summary>Подробная причина (GitLab 15.6+): mergeable, not_approved, ci_must_pass, conflict…</summary>
        public string DetailedMergeStatus;

        public bool Draft;
        public bool HasConflicts;
        public bool BlockingDiscussionsResolved = true;
        public bool Squash;
        public bool ForceRemoveSourceBranch;
        public bool MergeWhenPipelineSucceeds;
        public int UserNotesCount;

        public GlUserRef Author;
        public readonly List<GlUserRef> Assignees = new List<GlUserRef>();
        public readonly List<GlUserRef> Reviewers = new List<GlUserRef>();
        public readonly List<string> Labels = new List<string>();

        /// <summary>Статус пайплайна вершины; есть только в ответе на один MR.</summary>
        public string PipelineStatus;

        public string BaseSha, StartSha, HeadSha;
        public DateTime? CreatedAt, UpdatedAt;

        /// <summary>Когда и кем слит; для закрытого — когда закрыт.</summary>
        public DateTime? MergedAt, ClosedAt;
        public GlUserRef MergedBy;

        public bool IsMerged => State == "merged";

        public bool IsOpen => State == "opened";
        public bool HasDetails;
    }

    public sealed class GlApprovals
    {
        public bool Approved;
        public int ApprovalsRequired;
        public int ApprovalsLeft;
        public bool UserHasApproved;
        public bool UserCanApprove;
        public readonly List<GlUserRef> ApprovedBy = new List<GlUserRef>();
    }

    /// <summary>Проект GitLab — то, что нужно при создании MR.</summary>
    public sealed class GlProjectInfo
    {
        public long Id;
        public string DefaultBranch;
        public string WebUrl;
        public string PathWithNamespace;

        /// <summary>never, always, default_on, default_off.</summary>
        public string SquashOption;

        /// <summary>«Удалять исходную ветку» по умолчанию в новых MR.</summary>
        public bool RemoveSourceBranchAfterMerge;
    }

    /// <summary>Готовый кусок JSON — вложенный объект в теле запроса.</summary>
    public sealed class JsonRaw
    {
        public readonly string Json;
        public JsonRaw(string json) { Json = json; }
    }

    /// <summary>Тело JSON-запроса. Без библиотеки: значения — строки, числа, флаги и списки.</summary>
    public static class JsonText
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new System.Text.StringBuilder(s.Length + 2).Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Объект из пар. null-значения пропускаются — в API GitLab это «не менять».</summary>
        public static string Obj(params (string key, object value)[] fields)
        {
            var sb = new System.Text.StringBuilder("{");
            foreach (var (key, value) in fields)
            {
                if (value == null) continue;
                if (sb.Length > 1) sb.Append(',');
                sb.Append(Str(key)).Append(':').Append(Value(value));
            }
            return sb.Append('}').ToString();
        }

        private static string Value(object v)
        {
            switch (v)
            {
                case JsonRaw raw: return raw.Json;
                case string s: return Str(s);
                case bool b: return b ? "true" : "false";
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case System.Collections.IEnumerable list:
                    var sb = new System.Text.StringBuilder("[");
                    foreach (var item in list)
                    {
                        if (sb.Length > 1) sb.Append(',');
                        sb.Append(Value(item));
                    }
                    return sb.Append(']').ToString();
                default: return Str(Convert.ToString(v, CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>
    /// Разбор ответов GitLab REST API.
    ///
    /// Не JsonUtility: ему нужны классы с полями под каждый ответ, он молча
    /// пропускает вложенные массивы объектов и недоступен вне редактора — а
    /// разбор проверяется тестами без Unity. Незнакомые и отсутствующие поля
    /// не ломают разбор: API меняется от версии к версии.
    /// </summary>
    public static class GitLabModels
    {
        public static List<GlMr> ParseMergeRequests(string json)
        {
            var list = new List<GlMr>();
            if (!(MiniJson.Parse(json) is List<object> items)) return list;

            foreach (var item in items)
                if (item is Dictionary<string, object> o) list.Add(ReadMr(o, false));
            return list;
        }

        public static GlMr ParseMergeRequest(string json)
        {
            return MiniJson.Parse(json) is Dictionary<string, object> o ? ReadMr(o, true) : null;
        }

        public static GlApprovals ParseApprovals(string json)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object> o)) return null;

            var a = new GlApprovals
            {
                Approved = Bool(o, "approved"),
                ApprovalsRequired = Int(o, "approvals_required"),
                ApprovalsLeft = Int(o, "approvals_left"),
                UserHasApproved = Bool(o, "user_has_approved"),
                UserCanApprove = Bool(o, "user_can_approve")
            };

            if (o.TryGetValue("approved_by", out var by) && by is List<object> entries)
                foreach (var e in entries)
                    if (e is Dictionary<string, object> entry && entry.TryGetValue("user", out var u) && u is Dictionary<string, object> user)
                        a.ApprovedBy.Add(ReadUser(user));

            return a;
        }

        public static GlUserRef ParseUser(string json)
        {
            return MiniJson.Parse(json) is Dictionary<string, object> o ? ReadUser(o) : null;
        }

        public static GlProjectInfo ParseProject(string json)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object> o)) return null;
            return new GlProjectInfo
            {
                Id = Long(o, "id"),
                DefaultBranch = Str(o, "default_branch"),
                WebUrl = Str(o, "web_url"),
                PathWithNamespace = Str(o, "path_with_namespace"),
                SquashOption = Str(o, "squash_option"),
                RemoveSourceBranchAfterMerge = Bool(o, "remove_source_branch_after_merge")
            };
        }

        public static List<GlUserRef> ParseUsers(string json)
        {
            var list = new List<GlUserRef>();
            if (!(MiniJson.Parse(json) is List<object> items)) return list;
            foreach (var item in items)
                if (item is Dictionary<string, object> u && Str(u, "state") != "blocked") list.Add(ReadUser(u));
            list.Sort((a, b) => string.Compare(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase));
            return list;
        }

        public static List<string> ParseLabelNames(string json)
        {
            var list = new List<string>();
            if (!(MiniJson.Parse(json) is List<object> items)) return list;
            foreach (var item in items)
                if (item is Dictionary<string, object> l && Str(l, "name") is string name) list.Add(name);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>Сообщение об ошибке из тела ответа GitLab: {"message": …} или {"error": …}.</summary>
        public static string ErrorMessage(string json)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object> o)) return null;
            foreach (var key in new[] { "message", "error" })
            {
                if (!o.TryGetValue(key, out var v) || v == null) continue;
                if (v is string s) return s;
                if (v is List<object> list) return string.Join("; ", list.ConvertAll(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
                if (v is Dictionary<string, object> d)
                {
                    var parts = new List<string>();
                    foreach (var pair in d)
                        parts.Add(pair.Key + ": " + (pair.Value is List<object> l ? string.Join(", ", l) : Convert.ToString(pair.Value, CultureInfo.InvariantCulture)));
                    return string.Join("; ", parts);
                }
            }
            return null;
        }

        private static GlMr ReadMr(Dictionary<string, object> o, bool details)
        {
            var mr = new GlMr
            {
                Id = Long(o, "id"),
                Iid = Int(o, "iid"),
                Title = Str(o, "title") ?? string.Empty,
                Description = Str(o, "description") ?? string.Empty,
                State = Str(o, "state"),
                SourceBranch = Str(o, "source_branch"),
                TargetBranch = Str(o, "target_branch"),
                WebUrl = Str(o, "web_url"),
                Sha = Str(o, "sha"),
                MergeStatus = Str(o, "merge_status"),
                DetailedMergeStatus = Str(o, "detailed_merge_status"),
                // До 14.0 draft назывался work_in_progress.
                Draft = Bool(o, "draft") || Bool(o, "work_in_progress"),
                HasConflicts = Bool(o, "has_conflicts"),
                BlockingDiscussionsResolved = !o.ContainsKey("blocking_discussions_resolved") || Bool(o, "blocking_discussions_resolved"),
                Squash = Bool(o, "squash"),
                ForceRemoveSourceBranch = Bool(o, "force_remove_source_branch"),
                MergeWhenPipelineSucceeds = Bool(o, "merge_when_pipeline_succeeds"),
                UserNotesCount = Int(o, "user_notes_count"),
                CreatedAt = Date(o, "created_at"),
                UpdatedAt = Date(o, "updated_at"),
                HasDetails = details
            };

            if (o.TryGetValue("author", out var author) && author is Dictionary<string, object> a) mr.Author = ReadUser(a);

            mr.MergedAt = Date(o, "merged_at");
            mr.ClosedAt = Date(o, "closed_at");
            // merged_by устарел в пользу merge_user (GitLab 14.7) — берём новое, а старое на запас.
            if (o.TryGetValue("merge_user", out var mu) && mu is Dictionary<string, object> mergeUser) mr.MergedBy = ReadUser(mergeUser);
            else if (o.TryGetValue("merged_by", out var mb) && mb is Dictionary<string, object> mergedBy) mr.MergedBy = ReadUser(mergedBy);
            ReadUsers(o, "assignees", mr.Assignees);
            ReadUsers(o, "reviewers", mr.Reviewers);

            if (o.TryGetValue("labels", out var labels) && labels is List<object> ls)
                foreach (var l in ls) if (l is string s) mr.Labels.Add(s);

            if (o.TryGetValue("head_pipeline", out var hp) && hp is Dictionary<string, object> pipeline)
                mr.PipelineStatus = Str(pipeline, "status");

            if (o.TryGetValue("diff_refs", out var dr) && dr is Dictionary<string, object> refs)
            {
                mr.BaseSha = Str(refs, "base_sha");
                mr.StartSha = Str(refs, "start_sha");
                mr.HeadSha = Str(refs, "head_sha");
            }

            return mr;
        }

        internal static void ReadUsers(Dictionary<string, object> o, string key, List<GlUserRef> into)
        {
            if (!o.TryGetValue(key, out var v) || !(v is List<object> list)) return;
            foreach (var item in list)
                if (item is Dictionary<string, object> u) into.Add(ReadUser(u));
        }

        internal static GlUserRef ReadUser(Dictionary<string, object> o)
        {
            return new GlUserRef { Id = Long(o, "id"), Username = Str(o, "username"), Name = Str(o, "name") };
        }

        internal static string Str(Dictionary<string, object> o, string key)
        {
            return o.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        internal static bool Bool(Dictionary<string, object> o, string key)
        {
            return o.TryGetValue(key, out var v) && v is bool b && b;
        }

        internal static long Long(Dictionary<string, object> o, string key)
        {
            return o.TryGetValue(key, out var v) && v is double d ? (long)d : 0;
        }

        internal static int Int(Dictionary<string, object> o, string key)
        {
            return (int)Long(o, key);
        }

        internal static DateTime? Date(Dictionary<string, object> o, string key)
        {
            var s = Str(o, key);
            return !string.IsNullOrEmpty(s) &&
                   DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d)
                ? d : (DateTime?)null;
        }
    }
}
