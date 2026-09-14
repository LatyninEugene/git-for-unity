using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>
    /// Модальный запрос учётных данных — то самое окно, которое в IDEA
    /// появляется при отказе аутентификации.
    ///
    /// Намеренно на IMGUI: окно живёт секунды, модально и не должно зависеть
    /// от загрузки таблицы стилей по путям пакета.
    ///
    /// Введённое здесь нигде не сохраняется полем и не пишется в журнал:
    /// значение уходит в `git credential approve` через stdin и живёт дальше
    /// в системном хранилище, а не в плагине.
    ///
    /// Подсказки зависят от сервера: у GitHub, GitLab и Bitbucket разные
    /// правила — какое имя пользователя, какой токен и где его создать.
    /// </summary>
    public sealed class CredentialPromptWindow : EditorWindow
    {
        private string _host;
        private string _reason;
        private string _tokenPageUrl;
        private string _hint;
        private string _username = string.Empty;
        private string _token = string.Empty;
        private bool _ok;
        private bool _decided;

        /// <summary>
        /// Показывает окно и ждёт ответа. Возвращает false, если отменили.
        /// Вызывать только из главного потока.
        /// </summary>
        /// <param name="tokenPageUrl">
        /// Страница создания токена на этом сервере. Если задана, в окне появляется
        /// кнопка — искать её самому пользователю не приходится.
        /// </param>
        /// <param name="defaultUsername">Имя, подставленное заранее: у GitLab при токене это oauth2.</param>
        /// <param name="hint">Что вводить на этом сервере. null — общая подсказка.</param>
        public static bool Ask(string host, string reason, string tokenPageUrl,
                               out string username, out string token,
                               string defaultUsername = null, string hint = null)
        {
            var w = CreateInstance<CredentialPromptWindow>();
            w.titleContent = new GUIContent(L.T("Sign In to Git Server"));
            w._host = host;
            w._reason = reason;
            w._tokenPageUrl = tokenPageUrl;
            w._username = defaultUsername ?? string.Empty;
            w._hint = hint ?? L.T("Usually a personal access token with write access to the repository.");
            w.minSize = new Vector2(560f, 300f);
            w.maxSize = new Vector2(860f, 440f);
            w.ShowModalUtility();   // блокирует до закрытия окна

            username = w._username;
            token = w._token;
            bool ok = w._ok;

            // Токен не должен пережить окно даже в памяти дольше необходимого.
            w._token = string.Empty;
            return ok;
        }

        /// <summary>
        /// Спрашивает учётные данные для адреса и кладёт их в системный
        /// credential helper. Возвращает null, если пользователь отказался или
        /// запись сохранить не удалось.
        /// </summary>
        /// <param name="forgetExisting">
        /// Забыть прежнюю запись перед сохранением. Нужно, когда сохранённый
        /// токен отвергнут сервером: иначе helper отдаст его снова.
        /// </param>
        /// <param name="onError">
        /// Куда сообщить, если сохранить не удалось. Без этого отказ git выглядел
        /// бы как «окно закрылось, и ничего не произошло».
        /// </param>
        public static async Task<GitCredential> AskAndStoreAsync(
            string baseUrl, string reason, bool forgetExisting, Action<string> onError = null)
        {
            Uri uri;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                if (onError != null)
                    onError(L.F("The instance URL must start with http:// or https://: {0}", baseUrl));
                return null;
            }

            var protocol = uri.Scheme;
            var host = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
            var server = CredentialHosts.For(uri);

            string user, token;
            if (!Ask(baseUrl, reason, server.TokenPageUrl, out user, out token, server.DefaultUsername, server.Hint))
                return null;  // отказ — молча
            Diagnostics.Redactor.RegisterSecret(token);

            if (forgetExisting) await GitAuth.RejectAsync(protocol, host);

            var saved = await GitAuth.ApproveAsync(protocol, host, user, token);
            if (!saved.Ok)
            {
                if (onError != null) onError(L.F("Couldn't save the token: {0}", saved.Message));
                return null;
            }

            // Возвращаем введённое напрямую: перечитывать через `credential fill`
            // значило бы лишний запуск процесса ради того, что уже известно.
            return new GitCredential
            {
                Username = user, Token = token, Protocol = protocol, Host = host
            };
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);

            EditorGUILayout.LabelField(L.T("Sign-In Required"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(_host, EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(_reason))
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.HelpBox(_reason, MessageType.Warning);
            }

            EditorGUILayout.Space(4f);
            EditorGUIUtility.labelWidth = 140f;
            _username = EditorGUILayout.TextField(L.T("Username"), _username);
            _token = EditorGUILayout.PasswordField(L.T("Token or Password"), _token);

            // Подсказка на всю ширину: в колонке значения длинный текст обрезался.
            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(_hint, EditorStyles.wordWrappedMiniLabel);

            if (!string.IsNullOrEmpty(_tokenPageUrl))
            {
                EditorGUILayout.Space(2f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L.T("Create Token on Server"), GUILayout.ExpandWidth(false)))
                        Application.OpenURL(_tokenPageUrl);
                    GUILayout.FlexibleSpace();
                }
            }

            GUILayout.FlexibleSpace();

            EditorGUILayout.LabelField(L.T("Will be saved in the system git credential helper"), EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (GUILayout.Button(L.T("Cancel"), GUILayout.Width(90f)))
                {
                    _ok = false;
                    _decided = true;
                    Close();
                }

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_token)))
                {
                    if (GUILayout.Button(L.T("Sign In"), GUILayout.Width(90f)))
                    {
                        _ok = true;
                        _decided = true;
                        Close();
                    }
                }
            }

            EditorGUILayout.Space(6f);
        }

        private void OnDestroy()
        {
            // Крестик в углу — это тоже отказ, а не пустой успех.
            if (!_decided) _ok = false;
        }
    }

    /// <summary>
    /// Что известно о входе на конкретный сервер: имя пользователя при токене,
    /// подсказка и страница создания токена. Раньше это брали у первой
    /// включённой интеграции — и для github.com показывали страницу GitLab.
    /// </summary>
    internal static class CredentialHosts
    {
        internal sealed class Info
        {
            public string DefaultUsername;
            public string Hint;
            public string TokenPageUrl;
            public string SshKeysUrl;
        }

        /// <summary>По имени хоста — например, из ssh-адреса git@github.com:group/repo.git.</summary>
        public static Info ForHost(string host)
        {
            Uri uri;
            return Uri.TryCreate("https://" + host, UriKind.Absolute, out uri) ? For(uri) : new Info();
        }

        public static Info For(Uri server)
        {
            var host = server.Host.ToLowerInvariant();
            var origin = server.Scheme + "://" + (server.IsDefaultPort ? server.Host : server.Host + ":" + server.Port);

            if (host == "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal))
                return new Info
                {
                    Hint = L.T("GitHub: your GitHub username and a personal access token with the “repo” scope " +
                               "(for a fine-grained token — “Contents: Read and write”). The account password doesn't work here."),
                    TokenPageUrl = "https://github.com/settings/tokens/new?scopes=repo&description=" + Uri.EscapeDataString("Git for Unity"),
                    SshKeysUrl = "https://github.com/settings/keys"
                };

            if (host == "bitbucket.org")
                return new Info
                {
                    Hint = L.T("Bitbucket: your Bitbucket username and an app password with write access to repositories."),
                    TokenPageUrl = "https://bitbucket.org/account/settings/app-passwords/new",
                    SshKeysUrl = "https://bitbucket.org/account/settings/ssh-keys/"
                };

            // Страницы включённой интеграции — только если это тот же сервер.
            var integrationPage = IntegrationPage(origin, i => i.TokenPageUrl, out var integrationId);
            var integrationKeys = IntegrationPage(origin, i => i.SshKeysUrl, out _);

            if (host == "gitlab.com" || integrationId == "gitlab" || host.IndexOf("gitlab", StringComparison.Ordinal) >= 0)
                return new Info
                {
                    DefaultUsername = "oauth2",
                    Hint = L.T("GitLab: the username for a token is oauth2, and the token needs the “write_repository” scope."),
                    TokenPageUrl = integrationPage ??
                                   origin + "/-/user_settings/personal_access_tokens?name=" + Uri.EscapeDataString("Git for Unity") +
                                   "&scopes=write_repository",
                    SshKeysUrl = integrationKeys ?? origin + "/-/user_settings/ssh_keys"
                };

            return new Info { TokenPageUrl = integrationPage, SshKeysUrl = integrationKeys };
        }

        private static string IntegrationPage(string origin, Func<IGitIntegration, string> pick, out string integrationId)
        {
            integrationId = null;
            foreach (var integration in GitIntegrations.Enabled)
            {
                string url;
                try { url = pick(integration); }
                catch { continue; }

                Uri page;
                if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out page)) continue;

                var pageOrigin = page.Scheme + "://" + (page.IsDefaultPort ? page.Host : page.Host + ":" + page.Port);
                if (!string.Equals(pageOrigin, origin, StringComparison.OrdinalIgnoreCase)) continue;

                integrationId = integration.Id;
                return url;
            }
            return null;
        }
    }

    internal enum SshPassphraseChoice
    {
        Cancel,
        Unlock,
        SwitchToHttp
    }

    /// <summary>
    /// Пароль ключа SSH. Спрашивает плагин, потому что ssh под редактором
    /// спросить не может: консоли у процесса нет. Введённое уходит в ssh-add
    /// и нигде не остаётся.
    /// </summary>
    internal sealed class SshPassphraseWindow : EditorWindow
    {
        private const string FieldName = "LevGitSshPassphrase";

        private string _host;
        private string _keyName;
        private string _httpUrl;
        private string _error;
        private string _passphrase = string.Empty;
        private SshPassphraseChoice _choice = SshPassphraseChoice.Cancel;
        private bool _focused;

        /// <param name="error">Почему спрашиваем снова — например, пароль не подошёл.</param>
        public static SshPassphraseChoice Ask(string host, string keyName, string httpUrl, string error, out string passphrase)
        {
            var w = CreateInstance<SshPassphraseWindow>();
            w.titleContent = new GUIContent(L.T("SSH Key Passphrase"));
            w._host = host ?? string.Empty;
            w._keyName = keyName ?? string.Empty;
            w._httpUrl = httpUrl;
            w._error = error;
            w.minSize = new Vector2(540f, string.IsNullOrEmpty(error) ? 220f : 270f);
            w.maxSize = new Vector2(860f, 380f);
            w.ShowModalUtility();

            passphrase = w._choice == SshPassphraseChoice.Unlock ? w._passphrase : null;
            w._passphrase = string.Empty;
            return w._choice;
        }

        private void OnGUI()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    Finish(SshPassphraseChoice.Cancel);
                    e.Use();
                    return;
                }
                if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && _passphrase.Length > 0)
                {
                    Finish(SshPassphraseChoice.Unlock);
                    e.Use();
                    return;
                }
            }

            EditorGUILayout.Space(8f);
            var title = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };
            EditorGUILayout.LabelField(
                L.F("{0} accepts the key {1}, but the key is protected with a passphrase.", _host, _keyName), title);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(
                L.T("Enter the passphrase: the key will be added to ssh-agent until Unity is closed. The passphrase itself is not saved anywhere."),
                EditorStyles.wordWrappedLabel);

            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }

            EditorGUILayout.Space(6f);
            EditorGUIUtility.labelWidth = 110f;
            GUI.SetNextControlName(FieldName);
            _passphrase = EditorGUILayout.PasswordField(L.T("Passphrase"), _passphrase);
            if (!_focused)
            {
                EditorGUI.FocusTextInControl(FieldName);
                _focused = true;
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (!string.IsNullOrEmpty(_httpUrl) && GUILayout.Button(L.T("Switch to HTTP(S)"), GUILayout.ExpandWidth(false)))
                    Finish(SshPassphraseChoice.SwitchToHttp);

                GUILayout.FlexibleSpace();

                if (GUILayout.Button(L.T("Cancel"), GUILayout.Width(90f)))
                    Finish(SshPassphraseChoice.Cancel);

                using (new EditorGUI.DisabledScope(_passphrase.Length == 0))
                {
                    if (GUILayout.Button(L.T("Unlock Key"), GUILayout.ExpandWidth(false)))
                        Finish(SshPassphraseChoice.Unlock);
                }
            }

            EditorGUILayout.Space(6f);
        }

        private void Finish(SshPassphraseChoice choice)
        {
            _choice = choice;
            Close();
        }
    }

    /// <summary>
    /// Сервер не принял SSH-ключ. Своё окно, а не системный диалог: в системном
    /// длинный вывод git переносился посреди слов и обрезался справа.
    /// </summary>
    internal sealed class SshProblemWindow : EditorWindow
    {
        private string _remote;
        private string _host;
        private string _output;
        private string _httpUrl;
        private string _keysUrl;
        private string _details;
        private bool _switch;
        private Vector2 _scroll;

        /// <summary>Показывает, что случилось, и ждёт ответа. true — переключить remote на HTTP(S).</summary>
        /// <param name="details">Итог проверки ключей: какие есть и что сервер о них думает.</param>
        public static bool AskSwitchToHttp(string remoteName, string host, string gitOutput, string httpUrl, string keysUrl,
                                           string details = null)
        {
            var w = CreateInstance<SshProblemWindow>();
            w.titleContent = new GUIContent(L.T("SSH Access Failed"));
            w._remote = remoteName ?? string.Empty;
            w._host = host ?? string.Empty;
            w._output = gitOutput ?? string.Empty;
            w._httpUrl = httpUrl;
            w._keysUrl = keysUrl;
            w._details = details;
            w.minSize = new Vector2(580f, string.IsNullOrEmpty(details) ? 330f : 440f);
            w.maxSize = new Vector2(900f, 720f);
            w.ShowModalUtility();
            return w._switch;
        }

        private void OnGUI()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                Close();
                e.Use();
                return;
            }

            EditorGUILayout.Space(8f);
            var title = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };
            EditorGUILayout.LabelField(
                L.F("Remote “{0}” ({1}) uses SSH, and the server did not accept the key.", _remote, _host), title);

            EditorGUILayout.Space(4f);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(84f));
            var outputStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            float height = outputStyle.CalcHeight(new GUIContent(_output), Mathf.Max(100f, position.width - 40f));
            EditorGUILayout.SelectableLabel(_output, outputStyle, GUILayout.Height(Mathf.Max(40f, height)));
            EditorGUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_details))
            {
                // Выделяемым текстом: отпечатки ключей сверяют со страницей сервера и копируют.
                EditorGUILayout.Space(6f);
                var detailsStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };
                float detailsHeight = detailsStyle.CalcHeight(new GUIContent(_details), Mathf.Max(100f, position.width - 12f));
                EditorGUILayout.SelectableLabel(_details, detailsStyle, GUILayout.Height(detailsHeight));
            }

            EditorGUILayout.Space(6f);
            if (!string.IsNullOrEmpty(_httpUrl))
                EditorGUILayout.LabelField(
                    L.F("The easiest fix is to switch this remote to HTTP(S): {0}. Then a token from the git credential store will work.", _httpUrl),
                    EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField(
                L.T("Or add your public key to the server and make sure ssh-agent is running."),
                EditorStyles.wordWrappedLabel);

            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.HorizontalScope())
            {
                // Страница ключей открывается в браузере, а окно остаётся: после
                // добавления ключа можно просто закрыть его и повторить операцию.
                if (!string.IsNullOrEmpty(_keysUrl) && GUILayout.Button(L.T("SSH Keys Page"), GUILayout.ExpandWidth(false)))
                    Application.OpenURL(_keysUrl);

                GUILayout.FlexibleSpace();

                if (GUILayout.Button(L.T("Cancel"), GUILayout.Width(90f))) Close();

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_httpUrl)))
                {
                    if (GUILayout.Button(L.T("Switch to HTTP(S)"), GUILayout.ExpandWidth(false)))
                    {
                        _switch = true;
                        Close();
                    }
                }
            }

            EditorGUILayout.Space(6f);
        }
    }
}
