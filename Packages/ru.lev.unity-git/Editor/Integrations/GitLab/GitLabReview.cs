using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Lev.Git.GitLab
{
    public enum IssueListFilter { AssignedToMe = 0, CreatedByMe = 1, AllOpen = 2, Closed = 3 }

    /// <summary>От чего отводить ветку задачи.</summary>
    public enum IssueBranchBase { ProjectDefault = 0, CurrentBranch = 1 }

    /// <summary>Где в diff стоит комментарий. Номер 0 — у строки нет номера на этой стороне.</summary>
    public sealed class GlNotePosition
    {
        public string BaseSha, StartSha, HeadSha;
        public string OldPath, NewPath;
        public int OldLine, NewLine;

        public string Path => NewPath ?? OldPath;
    }

    public sealed class GlNote
    {
        public long Id;
        public string Body = string.Empty;
        public GlUserRef Author;
        public DateTime? CreatedAt;
        public bool System;
        public bool Resolvable, Resolved;
        public GlUserRef ResolvedBy;

        /// <summary>DiffNote — к строке diff; DiscussionNote и null — общий.</summary>
        public string Type;
        public GlNotePosition Position;
    }

    /// <summary>Тред обсуждения MR: первая заметка и ответы на неё.</summary>
    public sealed class GlDiscussion
    {
        public string Id;
        public bool IndividualNote;
        public readonly List<GlNote> Notes = new List<GlNote>();

        public GlNote First => Notes.Count > 0 ? Notes[0] : null;

        /// <summary>Служебные заметки GitLab: «добавил коммит», «изменил заголовок».</summary>
        public bool IsSystem
        {
            get
            {
                if (Notes.Count == 0) return true;
                foreach (var n in Notes) if (!n.System) return false;
                return true;
            }
        }

        public bool Resolvable
        {
            get
            {
                foreach (var n in Notes) if (n.Resolvable) return true;
                return false;
            }
        }

        /// <summary>Решён — все решаемые заметки треда решены.</summary>
        public bool Resolved
        {
            get
            {
                bool any = false;
                foreach (var n in Notes)
                {
                    if (!n.Resolvable) continue;
                    any = true;
                    if (!n.Resolved) return false;
                }
                return any;
            }
        }

        public GlNotePosition Position
        {
            get
            {
                foreach (var n in Notes) if (n.Position != null) return n.Position;
                return null;
            }
        }

        /// <summary>Объект сцены, к которому оставлен комментарий; null — не к объекту.</summary>
        public SceneObjectMark Object => First != null ? ReviewAnchors.ParseObjectMark(First.Body) : null;
    }

    /// <summary>Привязка комментария к объекту сцены: файл и fileID объекта или компонента.</summary>
    public sealed class SceneObjectMark
    {
        public string GitPath;
        public long FileId;
    }

    public sealed class GlIssue
    {
        public long Id;
        public int Iid;
        public string Title = string.Empty;
        public string Description = string.Empty;
        public string State;
        public string WebUrl;
        public GlUserRef Author;
        public readonly List<GlUserRef> Assignees = new List<GlUserRef>();
        public readonly List<string> Labels = new List<string>();
        public string Milestone;
        public string DueDate;
        public DateTime? CreatedAt, UpdatedAt, ClosedAt;
        public int UserNotesCount;
        public int MergeRequestsCount;
        public bool Confidential;

        public bool IsOpen => State == "opened";
    }

    /// <summary>Разбор обсуждений, задач и загрузок. Без Unity — проверяется тестами.</summary>
    public static class GitLabReviewModels
    {
        public static List<GlDiscussion> ParseDiscussions(string json)
        {
            var list = new List<GlDiscussion>();
            if (!(MiniJson.Parse(json) is List<object> items)) return list;
            foreach (var item in items)
                if (item is Dictionary<string, object> o) list.Add(ReadDiscussion(o));
            return list;
        }

        public static GlDiscussion ParseDiscussion(string json)
        {
            return MiniJson.Parse(json) is Dictionary<string, object> o ? ReadDiscussion(o) : null;
        }

        public static GlNote ParseNote(string json)
        {
            return MiniJson.Parse(json) is Dictionary<string, object> o ? ReadNote(o) : null;
        }

        public static List<GlIssue> ParseIssues(string json)
        {
            var list = new List<GlIssue>();
            if (!(MiniJson.Parse(json) is List<object> items)) return list;
            foreach (var item in items)
                if (item is Dictionary<string, object> o) list.Add(ReadIssue(o));
            return list;
        }

        public static GlIssue ParseIssue(string json)
        {
            return MiniJson.Parse(json) is Dictionary<string, object> o ? ReadIssue(o) : null;
        }

        /// <summary>Ответ на POST /projects/:id/uploads — готовая markdown-ссылка на файл.</summary>
        public static string ParseUploadMarkdown(string json)
        {
            return MiniJson.Parse(json) is Dictionary<string, object> o ? GitLabModels.Str(o, "markdown") : null;
        }

        private static GlDiscussion ReadDiscussion(Dictionary<string, object> o)
        {
            var d = new GlDiscussion
            {
                Id = GitLabModels.Str(o, "id"),
                IndividualNote = GitLabModels.Bool(o, "individual_note")
            };

            if (o.TryGetValue("notes", out var notes) && notes is List<object> list)
                foreach (var n in list)
                    if (n is Dictionary<string, object> note) d.Notes.Add(ReadNote(note));

            return d;
        }

        private static GlNote ReadNote(Dictionary<string, object> o)
        {
            var n = new GlNote
            {
                Id = GitLabModels.Long(o, "id"),
                Body = GitLabModels.Str(o, "body") ?? string.Empty,
                CreatedAt = GitLabModels.Date(o, "created_at"),
                System = GitLabModels.Bool(o, "system"),
                Resolvable = GitLabModels.Bool(o, "resolvable"),
                Resolved = GitLabModels.Bool(o, "resolved"),
                Type = GitLabModels.Str(o, "type")
            };

            if (o.TryGetValue("author", out var a) && a is Dictionary<string, object> author) n.Author = GitLabModels.ReadUser(author);
            if (o.TryGetValue("resolved_by", out var r) && r is Dictionary<string, object> by) n.ResolvedBy = GitLabModels.ReadUser(by);

            if (o.TryGetValue("position", out var p) && p is Dictionary<string, object> pos)
            {
                n.Position = new GlNotePosition
                {
                    BaseSha = GitLabModels.Str(pos, "base_sha"),
                    StartSha = GitLabModels.Str(pos, "start_sha"),
                    HeadSha = GitLabModels.Str(pos, "head_sha"),
                    OldPath = GitLabModels.Str(pos, "old_path"),
                    NewPath = GitLabModels.Str(pos, "new_path"),
                    OldLine = GitLabModels.Int(pos, "old_line"),
                    NewLine = GitLabModels.Int(pos, "new_line")
                };
            }

            return n;
        }

        private static GlIssue ReadIssue(Dictionary<string, object> o)
        {
            var i = new GlIssue
            {
                Id = GitLabModels.Long(o, "id"),
                Iid = GitLabModels.Int(o, "iid"),
                Title = GitLabModels.Str(o, "title") ?? string.Empty,
                Description = GitLabModels.Str(o, "description") ?? string.Empty,
                State = GitLabModels.Str(o, "state"),
                WebUrl = GitLabModels.Str(o, "web_url"),
                DueDate = GitLabModels.Str(o, "due_date"),
                CreatedAt = GitLabModels.Date(o, "created_at"),
                UpdatedAt = GitLabModels.Date(o, "updated_at"),
                ClosedAt = GitLabModels.Date(o, "closed_at"),
                UserNotesCount = GitLabModels.Int(o, "user_notes_count"),
                MergeRequestsCount = GitLabModels.Int(o, "merge_requests_count"),
                Confidential = GitLabModels.Bool(o, "confidential")
            };

            if (o.TryGetValue("author", out var a) && a is Dictionary<string, object> author) i.Author = GitLabModels.ReadUser(author);
            GitLabModels.ReadUsers(o, "assignees", i.Assignees);

            // Очень старый GitLab отдаёт одного исполнителя полем assignee.
            if (i.Assignees.Count == 0 && o.TryGetValue("assignee", out var one) && one is Dictionary<string, object> assignee)
                i.Assignees.Add(GitLabModels.ReadUser(assignee));

            if (o.TryGetValue("labels", out var labels) && labels is List<object> ls)
                foreach (var l in ls) if (l is string s) i.Labels.Add(s);

            if (o.TryGetValue("milestone", out var m) && m is Dictionary<string, object> milestone)
                i.Milestone = GitLabModels.Str(milestone, "title");

            return i;
        }
    }

    /// <summary>
    /// Привязка комментариев: к строке diff и к объекту сцены.
    ///
    /// У GitLab нет понятия «объект сцены». Такой комментарий — обычный
    /// комментарий к строке заголовка объекта в YAML (<c>--- !u!1 &amp;123</c>),
    /// а в тексте — понятная человеку подпись и невидимая в браузере метка
    /// с fileID. По метке редактор снова находит объект, а в браузере
    /// комментарий виден у нужной строки файла.
    /// </summary>
    public static class ReviewAnchors
    {
        // Метка узнаётся по тексту — постоянная, не переводится.
        private const string HeaderPrefix = "> **Scene object:**";

        /// <summary>Та же метка в комментариях, оставленных до перевода интерфейса.</summary>
        private const string LegacyHeaderPrefix = "> **Объект сцены:**"; // loc-ignore

        private static int HeaderLength(string line)
        {
            if (line.StartsWith(HeaderPrefix, StringComparison.Ordinal)) return HeaderPrefix.Length;
            if (line.StartsWith(LegacyHeaderPrefix, StringComparison.Ordinal)) return LegacyHeaderPrefix.Length;
            return -1;
        }

        private static readonly Regex MarkRegex = new Regex(
            "<!--\\s*lev-git:object\\s+file=\"([^\"]*)\"\\s+id=\"(-?\\d+)\"\\s*-->", RegexOptions.CultureInvariant);

        public static string ObjectMark(string gitPath, long fileId)
        {
            return "<!-- lev-git:object file=\"" + (gitPath ?? string.Empty).Replace("\"", "'").Replace("--", "- -") +
                   "\" id=\"" + fileId.ToString(CultureInfo.InvariantCulture) + "\" -->";
        }

        public static SceneObjectMark ParseObjectMark(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            var m = MarkRegex.Match(body);
            if (!m.Success || !long.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
            return new SceneObjectMark { GitPath = m.Groups[1].Value, FileId = id };
        }

        /// <summary>Текст комментария к объекту: подпись для людей, сам текст, метка для редактора.</summary>
        public static string ObjectCommentBody(string title, string ownerTitle, string gitPath, long fileId, string text)
        {
            var what = string.IsNullOrEmpty(ownerTitle)
                ? Code(title)
                : L.F("{0} → component {1}", Code(ownerTitle), Code(title));

            var sb = new StringBuilder();
            sb.Append(HeaderPrefix).Append(' ').Append(what).Append(" · ").Append(Code(gitPath)).Append("\n\n");
            sb.Append((text ?? string.Empty).Trim()).Append("\n\n");
            sb.Append(ObjectMark(gitPath, fileId));
            return sb.ToString();
        }

        /// <summary>Текст для показа в редакторе: без метки и без подписи — объект показан отдельно.</summary>
        public static string DisplayBody(string body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;
            var text = MarkRegex.Replace(body, string.Empty);

            var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            if (lines.Count > 0 && HeaderLength(lines[0]) >= 0) lines.RemoveAt(0);
            return string.Join("\n", lines).Trim();
        }

        /// <summary>Подпись объекта из текста комментария: «Player → компонент Rigidbody». null — подписи нет.</summary>
        public static string ObjectTitle(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
            {
                int prefix = HeaderLength(raw);
                if (prefix < 0) continue;
                var text = raw.Substring(prefix);
                int dot = text.LastIndexOf(" · ", StringComparison.Ordinal);
                if (dot >= 0) text = text.Substring(0, dot);
                return text.Replace("`", string.Empty).Trim();
            }
            return null;
        }

        private static string Code(string s)
        {
            return "`" + (s ?? string.Empty).Replace('`', '\'') + "`";
        }

        /// <summary>Номер строки (с 1) заголовка документа с этим fileID: «--- !u!4 &amp;123» или «… stripped». 0 — не найден.</summary>
        public static int FindObjectHeaderLine(string yaml, long fileId)
        {
            if (string.IsNullOrEmpty(yaml)) return 0;

            var id = fileId.ToString(CultureInfo.InvariantCulture);
            int line = 1;
            int start = 0;
            while (start <= yaml.Length)
            {
                int end = yaml.IndexOf('\n', start);
                if (end < 0) end = yaml.Length;

                if (end - start > 7 && string.CompareOrdinal(yaml, start, "--- !u!", 0, 7) == 0)
                {
                    int amp = yaml.IndexOf('&', start, end - start);
                    if (amp > 0)
                    {
                        int idEnd = amp + 1;
                        while (idEnd < end && (char.IsDigit(yaml[idEnd]) || yaml[idEnd] == '-')) idEnd++;
                        if (idEnd - amp - 1 == id.Length && string.CompareOrdinal(yaml, amp + 1, id, 0, id.Length) == 0) return line;
                    }
                }

                if (end >= yaml.Length) break;
                start = end + 1;
                line++;
            }
            return 0;
        }

        /// <summary>
        /// Номера строки для позиции комментария по правилам GitLab: у добавленной —
        /// только новый, у удалённой — только старый, у неизменённой — оба.
        /// </summary>
        public static void LinesFor(bool added, bool removed, int oldNumber, int newNumber, out int oldLine, out int newLine)
        {
            oldLine = added ? 0 : oldNumber;
            newLine = removed ? 0 : newNumber;
        }

        public static string PositionJson(GlNotePosition p)
        {
            return JsonText.Obj(
                ("position_type", "text"),
                ("base_sha", p.BaseSha),
                ("start_sha", p.StartSha),
                ("head_sha", p.HeadSha),
                ("old_path", p.OldPath ?? p.NewPath),
                ("new_path", p.NewPath ?? p.OldPath),
                ("old_line", p.OldLine > 0 ? (object)p.OldLine : null),
                ("new_line", p.NewLine > 0 ? (object)p.NewLine : null));
        }

        /// <summary>
        /// Относится ли комментарий к этой строке показанного diff. Комментарии к
        /// прежним версиям MR (другая вершина) к строкам не приклеиваются: номера
        /// строк там другие.
        /// </summary>
        public static bool Matches(GlNotePosition p, string headSha, string gitPath, int oldLine, int newLine)
        {
            if (p == null || string.IsNullOrEmpty(gitPath)) return false;
            if (!string.IsNullOrEmpty(p.HeadSha) && !string.IsNullOrEmpty(headSha) && p.HeadSha != headSha) return false;
            if (!string.Equals(p.NewPath, gitPath, StringComparison.Ordinal) && !string.Equals(p.OldPath, gitPath, StringComparison.Ordinal)) return false;

            if (p.NewLine > 0) return newLine == p.NewLine;
            return p.OldLine > 0 && newLine <= 0 && oldLine == p.OldLine;
        }

        /// <summary>Комментарий оставлен к прежней версии MR — строки могли уехать.</summary>
        public static bool IsOutdated(GlNotePosition p, string headSha)
        {
            return p != null && !string.IsNullOrEmpty(p.HeadSha) && !string.IsNullOrEmpty(headSha) && p.HeadSha != headSha;
        }

        /// <summary>Подпись привязки для списка обсуждений: «Assets/Player.cs:42».</summary>
        public static string Describe(GlNotePosition p)
        {
            if (p == null) return null;
            var path = p.Path ?? string.Empty;
            if (p.NewLine > 0) return path + ":" + p.NewLine;
            if (p.OldLine > 0) return L.F("{0}:{1} (deleted line)", path, p.OldLine);
            return path;
        }
    }

    /// <summary>Кусок текста из GitLab: обычный текст или картинка.</summary>
    public sealed class MdSegment
    {
        public string Text;
        public string ImageAlt;
        public string ImageUrl;

        public bool IsImage => ImageUrl != null;
    }

    /// <summary>Картинки в markdown GitLab и адреса, по которым их брать.</summary>
    public static class MarkdownImages
    {
        private static readonly Regex ImageRegex = new Regex(
            "!\\[([^\\]]*)\\]\\(\\s*<?([^)\\s>]+)>?(?:\\s+\"[^\"]*\")?\\s*\\)", RegexOptions.CultureInvariant);

        private static readonly Regex UploadRegex = new Regex("^/uploads/([0-9A-Za-z]{8,64})/([^/?#]+)$", RegexOptions.CultureInvariant);

        /// <summary>Текст и картинки по порядку. Пустые куски между ними пропускаются.</summary>
        public static List<MdSegment> Split(string text)
        {
            var list = new List<MdSegment>();
            if (string.IsNullOrEmpty(text)) return list;

            int at = 0;
            foreach (Match m in ImageRegex.Matches(text))
            {
                AddText(list, text.Substring(at, m.Index - at));
                list.Add(new MdSegment { ImageAlt = m.Groups[1].Value, ImageUrl = m.Groups[2].Value });
                at = m.Index + m.Length;
            }
            AddText(list, text.Substring(at));
            return list;
        }

        private static void AddText(List<MdSegment> list, string text)
        {
            var trimmed = text.Replace("\r\n", "\n").Trim('\n');
            if (trimmed.Trim().Length > 0) list.Add(new MdSegment { Text = trimmed });
        }

        /// <summary>
        /// Полный адрес картинки. «/uploads/…» в GitLab отсчитывается от проекта,
        /// остальные пути от корня — от инстанса.
        /// </summary>
        public static string Resolve(string url, string projectWebUrl, string baseUrl)
        {
            url = (url ?? string.Empty).Trim();
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;

            var project = (projectWebUrl ?? string.Empty).TrimEnd('/');
            if (url.StartsWith("/uploads/", StringComparison.Ordinal)) return project + url;
            if (url.StartsWith("/", StringComparison.Ordinal)) return (baseUrl ?? string.Empty).TrimEnd('/') + url;
            return project + "/" + url;
        }

        /// <summary>
        /// Путь API для файла, загруженного в этот проект: GET
        /// /projects/:id/uploads/:secret/:filename. null — файл не из этого проекта.
        /// </summary>
        public static string UploadApiPath(string webUrl, string projectWebUrl, string encodedId)
        {
            if (string.IsNullOrEmpty(webUrl) || string.IsNullOrEmpty(projectWebUrl) || string.IsNullOrEmpty(encodedId)) return null;

            var project = projectWebUrl.TrimEnd('/');
            if (!webUrl.StartsWith(project + "/", StringComparison.OrdinalIgnoreCase)) return null;

            var m = UploadRegex.Match(webUrl.Substring(project.Length));
            return m.Success ? "/projects/" + encodedId + "/uploads/" + m.Groups[1].Value + "/" + m.Groups[2].Value : null;
        }

        /// <summary>Тот же сервер — схема, хост и порт. Только туда можно отправлять токен.</summary>
        public static bool SameOrigin(string url, string baseUrl)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var a) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)) return false;
            return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
                   a.Port == b.Port;
        }
    }

    /// <summary>Тело multipart/form-data с одним файлом — для загрузки скриншота.</summary>
    public static class Multipart
    {
        public static byte[] Build(string boundary, string field, string fileName, string contentType, byte[] content)
        {
            var utf8 = new UTF8Encoding(false);
            using (var ms = new MemoryStream())
            {
                var head = "--" + boundary + "\r\n" +
                           "Content-Disposition: form-data; name=\"" + field + "\"; filename=\"" + fileName.Replace("\"", "") + "\"\r\n" +
                           "Content-Type: " + contentType + "\r\n\r\n";
                var headBytes = utf8.GetBytes(head);
                ms.Write(headBytes, 0, headBytes.Length);
                if (content != null) ms.Write(content, 0, content.Length);
                var tail = utf8.GetBytes("\r\n--" + boundary + "--\r\n");
                ms.Write(tail, 0, tail.Length);
                return ms.ToArray();
            }
        }
    }

    /// <summary>Значки MR у веток: ветка → открытый merge request.</summary>
    public static class BranchBadges
    {
        /// <summary>У ветки может быть несколько открытых MR (в разные целевые) — берём последний обновлённый.</summary>
        public static Dictionary<string, GlMr> Map(IEnumerable<GlMr> openMrs)
        {
            var map = new Dictionary<string, GlMr>(StringComparer.Ordinal);
            if (openMrs == null) return map;

            foreach (var mr in openMrs)
            {
                if (mr == null || string.IsNullOrEmpty(mr.SourceBranch)) continue;
                if (map.TryGetValue(mr.SourceBranch, out var existing) &&
                    Nullable.Compare(existing.UpdatedAt, mr.UpdatedAt) >= 0) continue;
                map[mr.SourceBranch] = mr;
            }
            return map;
        }

        public static string Text(GlMr mr)
        {
            return "!" + mr.Iid + (mr.Draft ? " draft" : string.Empty);
        }

        public static string Tooltip(GlMr mr)
        {
            return "Merge request !" + mr.Iid + ": " + mr.Title + "\n" + mr.SourceBranch + " → " + mr.TargetBranch +
                   (mr.HasConflicts ? "\n" + L.T("conflicts with the target branch") : string.Empty) +
                   (mr.UserNotesCount > 0 ? "\n" + L.F("threads: {0}", mr.UserNotesCount) : string.Empty);
        }
    }

    /// <summary>Ссылки на задачи в тексте MR.</summary>
    public static class IssueRefs
    {
        // Ключевые слова закрытия GitLab по умолчанию, в любом регистре.
        private static readonly Regex Closing = new Regex(
            @"\b(?:close[sd]?|closing|fix(?:e[sd]|ing)?|resolve[sd]?|resolving|implement(?:s|ed|ing)?)\s*:?\s+((?:#\d+(?:\s*(?:,|and)\s*)?)+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Number = new Regex(@"#(\d+)", RegexOptions.CultureInvariant);

        /// <summary>Номера из «Closes #1», «fixes #2, #3» — по порядку, без повторов.</summary>
        public static List<int> ClosingRefs(string text)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(text)) return list;

            foreach (Match m in Closing.Matches(text))
                foreach (Match n in Number.Matches(m.Groups[1].Value))
                    if (int.TryParse(n.Groups[1].Value, out var iid) && iid > 0 && !list.Contains(iid)) list.Add(iid);
            return list;
        }
    }

    /// <summary>Адреса запросов задач.</summary>
    public static class IssueQuery
    {
        public static string ListPath(string encodedId, IssueListFilter filter, string search)
        {
            var path = "/projects/" + encodedId + "/issues?state=" + (filter == IssueListFilter.Closed ? "closed" : "opened") +
                       "&per_page=50&order_by=updated_at&sort=desc";

            switch (filter)
            {
                case IssueListFilter.AssignedToMe: path += "&scope=assigned_to_me"; break;
                case IssueListFilter.CreatedByMe: path += "&scope=created_by_me"; break;
                default: path += "&scope=all"; break;
            }

            if (!string.IsNullOrWhiteSpace(search)) path += "&search=" + Uri.EscapeDataString(search.Trim());
            return path;
        }
    }
}
