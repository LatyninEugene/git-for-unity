// Поля DTO заполняет рефлексия JsonUtility, компилятор об этом не знает.
#pragma warning disable 0649

using System;
using System.IO;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Lev.Git.GitLab
{
    /// <summary>JsonUtility не умеет массив на верхнем уровне — заворачиваем его в объект.</summary>
    public static class JsonHelper
    {
        [Serializable]
        private class Wrapper<T>
        {
            public T[] items;
        }

        public static T[] FromJsonArray<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new T[0];
            try
            {
                var wrapped = "{\"items\":" + json.Trim() + "}";
                var w = JsonUtility.FromJson<Wrapper<T>>(wrapped);
                return w != null && w.items != null ? w.items : new T[0];
            }
            catch (Exception e)
            {
                Diagnostics.Journal.Warn(L.F("Could not parse JSON: {0}", e.Message));
                return new T[0];
            }
        }
    }

    [Serializable]
    public class GlUser
    {
        public int id;
        public string username;
        public string name;
    }

    [Serializable]
    public class GlProject
    {
        public int id;
        public string name;
        public string path_with_namespace;
        public string web_url;
        public string default_branch;
    }

    [Serializable]
    public class GlMergeRequest
    {
        public int iid;
        public string title;
        public string state;
        public string source_branch;
        public string target_branch;
        public string web_url;
    }

    [Serializable]
    public class GlPipeline
    {
        public int id;
        public string status;
        public string web_url;
        public string updated_at;
        // 'ref' — ключевое слово C#; имя поля в JSON при этом остаётся "ref".
        public string @ref;
    }

    public sealed class GitLabResponse
    {
        public bool Ok;
        public long Code;
        public string Body;
        public string Error;
    }

    /// <summary>Скачанный файл: картинка из описания задачи.</summary>
    public sealed class GitLabBytes
    {
        public bool Ok;
        public long Code;
        public byte[] Data;
        public string ContentType;
        public string Error;
    }

    /// <summary>
    /// Тонкий клиент GitLab REST API v4. Токен — отдельный токен API из
    /// системного хранилища, см. <see cref="GitLabToken"/>; в журнал он не
    /// попадает и в URL не кладётся.
    /// </summary>
    public sealed class GitLabClient
    {
        private readonly string _baseUrl;
        private readonly string _token;

        public GitLabClient(string baseUrl, string token)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _token = token;
        }

        public Task<GitLabResponse> GetAsync(string apiPath)
        {
            return SendAsync("GET", apiPath);
        }

        /// <summary>
        /// Запрос идёт через System.Net, а не через UnityWebRequest.
        ///
        /// UnityWebRequest подчиняется PlayerSettings.insecureHttpOption, и в
        /// новых проектах обычный http блокируется с «Insecure connection not
        /// allowed». Для self-hosted GitLab в локальной сети это неприемлемо.
        /// System.Net этой политике не подчиняется, работает вне главного потока
        /// и позволяет отдельно разрешить самоподписанный сертификат.
        /// </summary>
        public Task<GitLabResponse> SendAsync(string method, string apiPath, string jsonBody = null)
        {
            var url = _baseUrl + "/api/v4" + apiPath;
            bool insecure = GitLabSettings.instance.allowInsecureCertificate;
            var body = jsonBody != null ? new UTF8Encoding(false).GetBytes(jsonBody) : null;
            return Task.Run(() => Send(method, url, body, "application/json", insecure));
        }

        /// <summary>Загрузка файла как multipart/form-data — скриншот в задачу.</summary>
        public Task<GitLabResponse> UploadAsync(string apiPath, string fileName, string contentType, byte[] content)
        {
            var url = _baseUrl + "/api/v4" + apiPath;
            bool insecure = GitLabSettings.instance.allowInsecureCertificate;
            var boundary = "levgit" + Guid.NewGuid().ToString("N");
            var body = Multipart.Build(boundary, "file", fileName, contentType, content);
            return Task.Run(() => Send("POST", url, body, "multipart/form-data; boundary=" + boundary, insecure));
        }

        private GitLabResponse Send(string method, string url, byte[] body, string contentType, bool insecure)
        {
            var res = new GitLabResponse();

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                // Скриншот в несколько мегабайт по медленной сети за 20 секунд может не уйти.
                req.Timeout = body != null && body.Length > 512 * 1024 ? 120000 : 20000;
                req.ReadWriteTimeout = req.Timeout;
                req.UserAgent = "ru.lev.unity-git";
                if (!string.IsNullOrEmpty(_token)) req.Headers["PRIVATE-TOKEN"] = _token;

                // Самоподписанный сертификат — осознанный выбор пользователя,
                // и проверка снимается только для этого запроса, а не глобально.
                if (insecure) req.ServerCertificateValidationCallback = (a, b, c, d) => true;

                if (body != null)
                {
                    req.ContentType = contentType;
                    req.ContentLength = body.Length;
                    using (var stream = req.GetRequestStream()) stream.Write(body, 0, body.Length);
                }
                else if (method != "GET")
                {
                    req.ContentLength = 0;
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    res.Code = (long)resp.StatusCode;
                    res.Body = ReadBody(resp);
                    res.Ok = res.Code < 400;
                    if (!res.Ok) res.Error = string.Format("HTTP {0}: {1}", res.Code, Shorten(res.Body));
                }
            }
            catch (WebException e)
            {
                var http = e.Response as HttpWebResponse;
                if (http != null)
                {
                    using (http)
                    {
                        res.Code = (long)http.StatusCode;
                        res.Body = ReadBody(http);
                        res.Error = string.Format("HTTP {0}: {1}", res.Code, Shorten(res.Body));
                    }
                }
                else
                {
                    res.Error = Explain(e, url);
                }
            }
            catch (Exception e)
            {
                res.Error = e.Message;
            }

            return res;
        }

        private const int MaxDownloadBytes = 25 * 1024 * 1024;

        /// <summary>
        /// Файл по пути API («/projects/…») или по полному адресу. Токен уходит
        /// только на сам инстанс: ни картинка со стороннего сайта, ни
        /// перенаправление в хранилище файлов его не получат.
        /// </summary>
        public Task<GitLabBytes> GetBytesAsync(string apiPathOrUrl)
        {
            bool absolute = apiPathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            apiPathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            var url = absolute ? apiPathOrUrl : _baseUrl + "/api/v4" + apiPathOrUrl;
            bool insecure = GitLabSettings.instance.allowInsecureCertificate;
            return Task.Run(() => Download(url, insecure));
        }

        private GitLabBytes Download(string url, bool insecure)
        {
            var res = new GitLabBytes();
            try
            {
                // Перенаправления — вручную: HttpWebRequest пронёс бы заголовок с токеном на чужой хост.
                for (int hop = 0; hop < 4; hop++)
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.Timeout = 30000;
                    req.ReadWriteTimeout = 30000;
                    req.UserAgent = "ru.lev.unity-git";
                    req.AllowAutoRedirect = false;
                    if (!string.IsNullOrEmpty(_token) && MarkdownImages.SameOrigin(url, _baseUrl)) req.Headers["PRIVATE-TOKEN"] = _token;
                    if (insecure) req.ServerCertificateValidationCallback = (a, b, c, d) => true;

                    HttpWebResponse resp;
                    try { resp = (HttpWebResponse)req.GetResponse(); }
                    catch (WebException e) when (e.Response is HttpWebResponse error) { resp = error; }

                    using (resp)
                    {
                        res.Code = (long)resp.StatusCode;
                        if (res.Code >= 300 && res.Code < 400 && !string.IsNullOrEmpty(resp.Headers["Location"]))
                        {
                            url = new Uri(new Uri(url), resp.Headers["Location"]).ToString();
                            continue;
                        }

                        if (res.Code >= 400)
                        {
                            res.Error = res.Code == 404 ? L.T("file not found") : res.Code == 401 || res.Code == 403 ? L.T("access denied") : "HTTP " + res.Code;
                            return res;
                        }

                        res.ContentType = resp.ContentType;
                        using (var stream = resp.GetResponseStream())
                        using (var ms = new MemoryStream())
                        {
                            var buffer = new byte[81920];
                            int read;
                            while (stream != null && (read = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                ms.Write(buffer, 0, read);
                                if (ms.Length > MaxDownloadBytes) { res.Error = L.T("the file is larger than 25 MB"); return res; }
                            }
                            res.Data = ms.ToArray();
                        }
                        res.Ok = true;
                        return res;
                    }
                }
                res.Error = L.T("too many redirects");
            }
            catch (WebException e)
            {
                res.Error = Explain(e, url);
            }
            catch (Exception e)
            {
                res.Error = e.Message;
            }
            return res;
        }

        private static string ReadBody(HttpWebResponse resp)
        {
            var stream = resp.GetResponseStream();
            if (stream == null) return null;
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        /// <summary>Переводит сетевую ошибку в то, с чем пользователь может что-то сделать.</summary>
        private static string Explain(WebException e, string url)
        {
            if (e.Status == WebExceptionStatus.TrustFailure ||
                (e.InnerException != null && e.InnerException is AuthenticationException))
                return L.T("The certificate was not accepted. If this is a self-hosted GitLab with a self-signed certificate, " +
                           "turn on “Trust Any Certificate” in Project Settings → Git → GitLab.");

            if (e.Status == WebExceptionStatus.NameResolutionFailure)
                return L.F("Could not resolve the host name in {0}", StripQuery(url));

            if (e.Status == WebExceptionStatus.ConnectFailure)
                return L.F("Could not connect to {0}. Check the instance URL and port.", StripQuery(url));

            return e.Message;
        }

        private static string StripQuery(string url)
        {
            int q = url.IndexOf('?');
            return q < 0 ? url : url.Substring(0, q);
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return L.T("(empty)");
            s = s.Replace('\n', ' ').Trim();
            return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
        }

        public async Task<GlProject> GetProjectAsync(string encodedId)
        {
            var r = await GetAsync("/projects/" + encodedId);
            if (!r.Ok) return null;
            return JsonUtility.FromJson<GlProject>(r.Body);
        }

        public async Task<GlUser> GetCurrentUserAsync()
        {
            var r = await GetAsync("/user");
            if (!r.Ok) return null;
            return JsonUtility.FromJson<GlUser>(r.Body);
        }

        public async Task<GlMergeRequest[]> GetOpenMergeRequestsAsync(string encodedId, string sourceBranch)
        {
            var r = await GetAsync(string.Format(
                "/projects/{0}/merge_requests?state=opened&source_branch={1}",
                encodedId, Uri.EscapeDataString(sourceBranch)));
            if (!r.Ok) return new GlMergeRequest[0];
            return JsonHelper.FromJsonArray<GlMergeRequest>(r.Body);
        }

        public async Task<GlPipeline> GetLatestPipelineAsync(string encodedId, string branch)
        {
            var r = await GetAsync(string.Format(
                "/projects/{0}/pipelines?ref={1}&per_page=1",
                encodedId, Uri.EscapeDataString(branch)));
            if (!r.Ok) return null;
            var arr = JsonHelper.FromJsonArray<GlPipeline>(r.Body);
            return arr.Length > 0 ? arr[0] : null;
        }

        // ---------------------------------------------------- merge request'ы ---

        /// <summary>
        /// Открытые MR по отбору. «Мои» — созданные мной и назначенные на меня:
        /// в GitLab это два разных запроса, результаты сливаются без повторов.
        /// </summary>
        public async Task<(System.Collections.Generic.List<GlMr> items, string error)> ListMergeRequestsAsync(
            string encodedId, MrListFilter filter, string search, string username)
        {
            var state = filter == MrListFilter.Merged ? "merged" : filter == MrListFilter.Closed ? "closed" : "opened";
            var basePath = "/projects/" + encodedId + "/merge_requests?state=" + state + "&per_page=50&order_by=updated_at";
            if (!string.IsNullOrWhiteSpace(search)) basePath += "&search=" + Uri.EscapeDataString(search.Trim());

            var paths = new System.Collections.Generic.List<string>();
            switch (filter)
            {
                case MrListFilter.Mine:
                    paths.Add(basePath + "&scope=created_by_me");
                    paths.Add(basePath + "&scope=assigned_to_me");
                    break;
                case MrListFilter.ReviewRequested:
                    if (string.IsNullOrEmpty(username)) return (new System.Collections.Generic.List<GlMr>(), L.T("The username is unknown — check the token."));
                    paths.Add(basePath + "&scope=all&reviewer_username=" + Uri.EscapeDataString(username));
                    break;
                default:
                    paths.Add(basePath + "&scope=all");
                    break;
            }

            var result = new System.Collections.Generic.List<GlMr>();
            var seen = new System.Collections.Generic.HashSet<int>();
            foreach (var p in paths)
            {
                var r = await GetAsync(p);
                if (!r.Ok) return (result, r.Code == 401 ? L.T("GitLab did not accept the token.") : r.Error);
                foreach (var mr in GitLabModels.ParseMergeRequests(r.Body))
                    if (seen.Add(mr.Iid)) result.Add(mr);
            }

            // Слитые — по времени слияния: «что влили последним» ищут именно так.
            if (filter == MrListFilter.Merged) result.Sort((a, b) => Nullable.Compare(b.MergedAt ?? b.UpdatedAt, a.MergedAt ?? a.UpdatedAt));
            else result.Sort((a, b) => Nullable.Compare(b.UpdatedAt, a.UpdatedAt));
            return (result, null);
        }

        public async Task<(GlMr mr, string error)> GetMergeRequestAsync(string encodedId, int iid)
        {
            var r = await GetAsync("/projects/" + encodedId + "/merge_requests/" + iid);
            if (!r.Ok) return (null, r.Code == 404 ? L.F("MR !{0} not found.", iid) : r.Error);
            return (GitLabModels.ParseMergeRequest(r.Body), null);
        }

        /// <summary>Одобрения MR. null — сервер их не отдаёт или запрос не удался.</summary>
        public async Task<GlApprovals> GetApprovalsAsync(string encodedId, int iid)
        {
            var r = await GetAsync("/projects/" + encodedId + "/merge_requests/" + iid + "/approvals");
            return r.Ok ? GitLabModels.ParseApprovals(r.Body) : null;
        }

        /// <summary>Страница создания MR в браузере — до того, как создание появится в редакторе.</summary>
        public static string NewMergeRequestUrl(string projectWebUrl, string sourceBranch)
        {
            return string.Format(
                "{0}/-/merge_requests/new?merge_request%5Bsource_branch%5D={1}",
                projectWebUrl.TrimEnd('/'), Uri.EscapeDataString(sourceBranch));
        }
    }
}
