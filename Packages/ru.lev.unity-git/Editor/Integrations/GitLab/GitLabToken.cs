// Поля DTO заполняет рефлексия JsonUtility, компилятор об этом не знает.
#pragma warning disable 0649

using System;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace Lev.Git.GitLab
{
    /// <summary>Что известно о токене со слов сервера.</summary>
    public sealed class GitLabTokenInfo
    {
        public string Name;
        public string[] Scopes = new string[0];
        public DateTime? ExpiresAt;
        public bool Active = true;
        public string UserName;
        public string UserDisplayName;

        /// <summary>Сервер сообщил подробности о самом токене (GitLab 15.5+). Иначе известен только пользователь.</summary>
        public bool Detailed;

        public DateTime CheckedAt = DateTime.Now;

        public int? DaysLeft => ExpiresAt.HasValue ? (int)Math.Floor((ExpiresAt.Value.Date - DateTime.Today).TotalDays) : (int?)null;

        public bool HasScope(string scope)
        {
            foreach (var s in Scopes) if (s == scope) return true;
            return false;
        }
    }

    /// <summary>
    /// Токен API GitLab — отдельный от учётных данных git для push.
    ///
    /// Отдельный намеренно: отзыв или замена токена API не должны ломать
    /// push, и наоборот. Хранится в системном хранилище этой машины под
    /// ключом инстанса — у каждого разработчика свой, в проект не попадает,
    /// в журнал команд тоже.
    /// </summary>
    public static class GitLabToken
    {
        [Serializable]
        private class SelfDto
        {
            public int id;
            public string name;
            public bool revoked;
            public bool active;
            public string[] scopes;
            public string expires_at;
            public string token;
        }

        [Serializable]
        private class UserDto
        {
            public string username;
            public string name;
        }

        private static string Key(string baseUrl)
        {
            Uri uri;
            if (!Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out uri)) return "gitlab-api:" + baseUrl;
            var host = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
            return "gitlab-api:" + uri.Scheme + "://" + host.ToLowerInvariant();
        }

        public static string Read(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl)) return null;
            var token = SecretStore.Read(Key(baseUrl));
            // Токен, который пакет знает, вырезается из журнала и отчёта по точному совпадению.
            Diagnostics.Redactor.RegisterSecret(token);
            return token;
        }

        public static bool Has(string baseUrl)
        {
            return !string.IsNullOrEmpty(Read(baseUrl));
        }

        public static bool Save(string baseUrl, string token, out string error)
        {
            _lastInfo = null;
            Diagnostics.Redactor.RegisterSecret(token);
            return SecretStore.Write(Key(baseUrl), token.Trim(), "GitLab API — Unity Git (" + baseUrl + ")", out error);
        }

        public static void Forget(string baseUrl)
        {
            _lastInfo = null;
            SecretStore.Delete(Key(baseUrl));
        }

        // ---------------------------------------------------------- сервер ---

        private static GitLabTokenInfo _lastInfo;
        private static string _lastInfoUrl;

        /// <summary>Последняя проверка токена этого инстанса за сессию — чтобы не спрашивать сервер на каждую перерисовку.</summary>
        public static GitLabTokenInfo LastInfo(string baseUrl)
        {
            return _lastInfoUrl == baseUrl ? _lastInfo : null;
        }

        /// <summary>Проверяет токен на сервере. error не null — токен не принят или сервер недоступен.</summary>
        public static async Task<GitLabTokenInfo> CheckAsync(string baseUrl, string token, Action<string> error)
        {
            var client = new GitLabClient(baseUrl, token);

            var user = await client.SendAsync("GET", "/user");
            if (!user.Ok)
            {
                error(user.Code == 401 ? L.T("GitLab did not accept the token: it is wrong, expired or revoked.") : user.Error);
                return null;
            }

            var info = new GitLabTokenInfo();
            var u = Parse<UserDto>(user.Body);
            if (u != null) { info.UserName = u.username; info.UserDisplayName = u.name; }

            // Подробности о самом токене есть с GitLab 15.5. На старом сервере
            // проверка всё равно полезна: токен принят, пользователь известен.
            var self = await client.SendAsync("GET", "/personal_access_tokens/self");
            if (self.Ok)
            {
                var s = Parse<SelfDto>(self.Body);
                if (s != null)
                {
                    info.Detailed = true;
                    info.Name = s.name;
                    info.Scopes = s.scopes ?? new string[0];
                    info.Active = s.active && !s.revoked;
                    info.ExpiresAt = ParseDate(s.expires_at);
                }
            }

            _lastInfo = info;
            _lastInfoUrl = baseUrl;
            return info;
        }

        /// <summary>
        /// Выпускает токен на замену: сервер отзывает старый и отдаёт новый с
        /// теми же правами. Новый сразу сохраняется. Нужен GitLab с поддержкой
        /// замены через API — на старом сервере вернётся понятная ошибка.
        /// </summary>
        public static async Task<bool> RotateAsync(string baseUrl, string token, int days, Action<string> error)
        {
            var client = new GitLabClient(baseUrl, token);
            var expires = DateTime.Today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var r = await client.SendAsync("POST", "/personal_access_tokens/self/rotate", "{\"expires_at\":\"" + expires + "\"}");
            if (!r.Ok)
            {
                error(r.Code == 404
                    ? L.T("This GitLab cannot issue a replacement token via the API. Create a new token in GitLab and set it here.")
                    : r.Code == 401 ? L.T("GitLab did not accept the current token.") : r.Error);
                return false;
            }

            var s = Parse<SelfDto>(r.Body);
            if (s == null || string.IsNullOrEmpty(s.token))
            {
                error(L.T("GitLab responded without a new token."));
                return false;
            }

            string saveError;
            if (!Save(baseUrl, s.token, out saveError))
            {
                // Старый токен уже отозван, а новый сохранить не вышло — показать
                // его негде, не нарушив правило «токен не в журнале». Честно говорим.
                error(L.F("A new token was issued, but it could not be saved: {0} The old one is already revoked — create a new one in GitLab.", saveError));
                return false;
            }

            return true;
        }

        /// <summary>Отзывает токен на сервере и забывает его на этой машине.</summary>
        public static async Task<bool> RevokeAsync(string baseUrl, string token, Action<string> error)
        {
            var r = await new GitLabClient(baseUrl, token).SendAsync("DELETE", "/personal_access_tokens/self");
            if (!r.Ok && r.Code != 401)
            {
                error(r.Code == 404
                    ? L.T("This GitLab cannot revoke a token via the API. Revoke it on the tokens page in GitLab.")
                    : r.Error);
                return false;
            }

            Forget(baseUrl);
            return true;
        }

        /// <summary>Страница создания токена с уже заполненными названием и правами.</summary>
        public static string CreatePageUrl(string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl)) return null;
            var name = "Unity Git — " + Environment.MachineName;
            return baseUrl.TrimEnd('/') + "/-/user_settings/personal_access_tokens?name=" + Uri.EscapeDataString(name) + "&scopes=api";
        }

        private static T Parse<T>(string json) where T : class
        {
            try { return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<T>(json); }
            catch { return null; }
        }

        private static DateTime? ParseDate(string s)
        {
            DateTime d;
            return !string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : (DateTime?)null;
        }
    }
}
