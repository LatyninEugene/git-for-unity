using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.GitLab
{
    /// <summary>
    /// Project Settings → Git → GitLab: включение интеграции, сервер и токен API.
    ///
    /// Токен здесь — только отдельный токен для API. Учётные данные git для
    /// push живут в хранилище git и этой страницей не трогаются: отозвать
    /// токен API и заодно сломать push было бы худшим сюрпризом.
    /// </summary>
    internal static class GitLabSettingsProvider
    {
        private static string _status;
        private static bool _statusError;
        private static bool _busy;
        private static string _autoChecked;

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(GitLabIntegration.SettingsPagePath, SettingsScope.Project)
            {
                label = "GitLab",
                guiHandler = _ => Draw(),
                keywords = new HashSet<string>(new[] { "gitlab", "токен", "token", "api", "инстанс", "instance", "merge request" }) // loc-ignore
            };
        }

        private static GitLabIntegration Integration
        {
            get
            {
                foreach (var i in GitIntegrations.All)
                    if (i is GitLabIntegration g) return g;
                return new GitLabIntegration();
            }
        }

        private static void Draw()
        {
            var s = GitLabSettings.instance;
            var integration = Integration;
            EditorGUIUtility.labelWidth = 220f;

            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(10, 10, 10, 10) }))
            {
                bool on = EditorGUILayout.ToggleLeft(L.C("Enable GitLab Integration",
                    "GitLab tab in the Git window, merge requests, issues and reviews. Without it the package is a plain git client."), s.enabled,
                    EditorStyles.boldLabel);
                if (on != s.enabled) integration.Enabled = on;

                if (!s.enabled)
                {
                    EditorGUILayout.HelpBox(integration.Recognizes(GitLabInstance.SourceRemote)
                        ? L.T("The remote of this project looks like GitLab — you can enable the integration.")
                        : L.T("The integration is off. Everything about git itself — changes, history, conflicts, locks — works without it."),
                        MessageType.Info);
                    return;
                }

                EditorGUILayout.Space(8f);
                DrawServer(s);

                EditorGUILayout.Space(12f);
                DrawToken();

                EditorGUILayout.Space(12f);
                DrawWorkflow(s);

                if (!string.IsNullOrEmpty(_status))
                {
                    EditorGUILayout.Space(6f);
                    EditorGUILayout.HelpBox(_status, _statusError ? MessageType.Error : MessageType.Info);
                }
            }
        }

        // ------------------------------------------------------------ сервер ---

        private static void DrawServer(GitLabSettings s)
        {
            EditorGUILayout.LabelField(L.T("Server"), EditorStyles.boldLabel);

            // Remote, из которого выводятся значения, — не обязательно основной: при
            // origin на GitHub и втором remote на GitLab путь берётся со второго.
            var remote = GitLabInstance.SourceRemote;
            var remoteName = GitLabInstance.SourceRemoteName;
            var autoBase = remote != null ? remote.GuessedBaseUrl : null;
            var autoPath = remote != null ? remote.FullPath : null;

            // В полях — то, что реально используется, а не только ручное
            // переопределение. Иначе значение, совпавшее с выведенным из remote,
            // после ввода превращалось в пустое поле и выглядело потерянным.
            using (new EditorGUILayout.HorizontalScope())
            {
                var instance = EditorGUILayout.DelayedTextField(L.C("Instance URL",
                    "Root of the GitLab web interface, for example http://gitlab.lev.ru"), GitLabInstance.EffectiveBaseUrl ?? string.Empty);
                if (instance != (GitLabInstance.EffectiveBaseUrl ?? string.Empty)) ApplyBaseUrl(s, instance, autoBase);

                SourceLabel(GitLabInstance.BaseUrlOverridden, remote != null && remote.BaseUrlIsGuess, remoteName);
                using (new EditorGUI.DisabledScope(!GitLabInstance.BaseUrlOverridden))
                    if (GUILayout.Button(new GUIContent("↺", L.T("Take from git remote again")), GUILayout.Width(24f)))
                    {
                        s.instanceUrlOverride = string.Empty;
                        s.Persist();
                        GUI.FocusControl(null);
                        ServerChanged();
                    }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                var project = EditorGUILayout.DelayedTextField(L.C("Project Path",
                    "namespace/repo, for example Latynin/TestGitLab"), GitLabInstance.EffectiveProjectPath ?? string.Empty);
                if (project != (GitLabInstance.EffectiveProjectPath ?? string.Empty))
                {
                    var p = (project ?? string.Empty).Trim().Trim('/');
                    s.projectPathOverride = p == autoPath ? string.Empty : p;
                    s.Persist();
                    ServerChanged();
                }

                SourceLabel(GitLabInstance.ProjectPathOverridden, false, remoteName);
                using (new EditorGUI.DisabledScope(!GitLabInstance.ProjectPathOverridden))
                    if (GUILayout.Button(new GUIContent("↺", L.T("Take from git remote again")), GUILayout.Width(24f)))
                    {
                        s.projectPathOverride = string.Empty;
                        s.Persist();
                        GUI.FocusControl(null);
                        ServerChanged();
                    }
            }

            // Адрес задан вручную, а ни один remote не смотрит на этот сервер — путь выводить не из чего.
            if (GitLabInstance.BaseUrlOverridden && !GitLabInstance.ProjectPathOverridden && remote == null)
            {
                Uri instanceUri;
                var host = Uri.TryCreate(GitLabInstance.EffectiveBaseUrl ?? string.Empty, UriKind.Absolute, out instanceUri)
                    ? instanceUri.Host : GitLabInstance.EffectiveBaseUrl;
                EditorGUILayout.HelpBox(L.F("No git remote points to {0}: enter the project path manually.", host), MessageType.Info);
            }

            bool https = GitLabInstance.IsHttps;
            using (new EditorGUI.DisabledScope(!https))
            {
                var insecure = EditorGUILayout.Toggle(L.C("Trust Any Certificate",
                    "Disables TLS verification for API requests. Needed with a self-signed certificate."), s.allowInsecureCertificate);
                if (insecure != s.allowInsecureCertificate)
                {
                    s.allowInsecureCertificate = insecure;
                    s.Persist();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth + 2f);
                using (new EditorGUI.DisabledScope(_busy || string.IsNullOrEmpty(GitLabInstance.EffectiveBaseUrl)))
                    if (GUILayout.Button(L.C("Test Connection",
                            "Whether GitLab is at this address, which version, whether the project is visible and the token is accepted"), GUILayout.Width(170f)))
                        Run(L.T("Checking connection"), CheckServerAsync);
            }

            if (!GitLabInstance.BaseUrlOverridden && remote != null && remote.BaseUrlIsGuess)
                EditorGUILayout.HelpBox(L.T("The remote uses SSH: the web address is guessed from the host name. If it is wrong, enter your own."), MessageType.Warning);

            if (!string.IsNullOrEmpty(GitLabInstance.EffectiveBaseUrl) && !https)
                EditorGUILayout.HelpBox(L.T("The connection is not encrypted: the token is sent in plain text. Acceptable on a local network, not on someone else's."),
                                        MessageType.Warning);
        }

        private static void ApplyBaseUrl(GitLabSettings s, string value, string autoBase)
        {
            var b = (value ?? string.Empty).Trim().TrimEnd('/');

            if (b.Length > 0)
            {
                Uri uri;
                if (!Uri.TryCreate(b, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    SetStatus(L.T("The address was not saved: it must start with http:// or https://, for example http://gitlab.lev.ru"), true);
                    return;
                }

                // Путь API вписывают часто — корень инстанса из него достаётся сам.
                int api = b.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
                if (api > 0) b = b.Substring(0, api);
            }

            // Совпало с выведенным из remote — не запоминаем как ручное, иначе при смене remote значение «залипнет».
            s.instanceUrlOverride = b.Length == 0 || b == autoBase ? string.Empty : b;
            s.Persist();
            ServerChanged();
        }

        private static void SourceLabel(bool overridden, bool guessed, string remoteName)
        {
            var text = overridden ? L.T("manual")
                     : guessed ? L.T("guessed")
                     : string.IsNullOrEmpty(remoteName) ? L.T("from remote") : L.F("from {0}", remoteName);
            GUILayout.Label(new GUIContent(text, overridden ? L.T("Set manually") : L.T("Derived from git remote")),
                            EditorStyles.miniLabel, GUILayout.Width(90f));
        }

        private static void ServerChanged()
        {
            _autoChecked = null;
            SetStatus(null, false);
            GitIntegrations.NotifyChanged();
        }

        /// <summary>
        /// Проверка подключения по шагам — чтобы сказать, что именно не так:
        /// нет сервера, это не GitLab, проект не найден или токен не принят.
        /// </summary>
        private static async Task CheckServerAsync()
        {
            var baseUrl = GitLabInstance.EffectiveBaseUrl;
            var token = GitLabToken.Read(baseUrl);
            var client = new GitLabClient(baseUrl, token);

            var version = await client.SendAsync("GET", "/version");
            if (version.Code == 0)
            {
                SetStatus(L.F("Server is unavailable: {0}", version.Error), true);
                return;
            }

            if (version.Code == 404 || (version.Body != null && version.Body.TrimStart().StartsWith("<")))
            {
                SetStatus(L.F("A server responds at {0}, but it is not the GitLab API. The root of the web interface is needed, for example http://gitlab.lev.ru.", baseUrl), true);
                return;
            }

            if (version.Code == 401)
            {
                SetStatus(string.IsNullOrEmpty(token)
                              ? L.F("GitLab found at {0}, but the API token is not set — set it below.", baseUrl)
                              : L.F("GitLab found at {0}, but the token was not accepted — replace it below.", baseUrl),
                          true);
                return;
            }

            if (!version.Ok)
            {
                SetStatus(L.F("GitLab returned an error: {0}", version.Error), true);
                return;
            }

            var v = JsonUtility.FromJson<VersionDto>(version.Body);
            var line = L.F("GitLab {0} at {1}.", v != null && !string.IsNullOrEmpty(v.version) ? v.version : L.T("(version unknown)"), baseUrl);

            var encoded = GitLabInstance.EffectiveEncodedId;
            if (string.IsNullOrEmpty(encoded))
            {
                SetStatus(L.F("{0} Project path is not set.", line), true);
                return;
            }

            var project = await client.GetProjectAsync(encoded);
            if (project == null)
            {
                SetStatus(L.F("{0} Project “{1}” not found, or the token has no access to it.", line, GitLabInstance.EffectiveProjectPath), true);
                return;
            }

            SetStatus(L.F("{0} Project “{1}” found, the default branch is {2}.", line, project.path_with_namespace, project.default_branch), false);
        }

        [Serializable]
        private class VersionDto
        {
            public string version;
            public string revision;
        }

        // ------------------------------------------------------------- токен ---

        private static void DrawToken()
        {
            var baseUrl = GitLabInstance.EffectiveBaseUrl;
            EditorGUILayout.LabelField(L.T("API Token"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(L.T("Separate from the git credentials used for push. Stored in the system credential store of this machine, not in the project."),
                                       EditorStyles.wordWrappedMiniLabel);

            if (!SecretStore.Supported)
            {
                EditorGUILayout.HelpBox(SecretStore.UnsupportedReason ?? L.T("The system credential store is unavailable."), MessageType.Warning);
                return;
            }

            if (string.IsNullOrEmpty(baseUrl))
            {
                EditorGUILayout.HelpBox(L.T("Set the instance URL first."), MessageType.Info);
                return;
            }

            var token = GitLabToken.Read(baseUrl);
            bool has = !string.IsNullOrEmpty(token);
            var info = GitLabToken.LastInfo(baseUrl);

            // При открытии страницы токен проверяется сам — один раз за сессию на инстанс.
            if (has && info == null && !_busy && _autoChecked != baseUrl)
            {
                _autoChecked = baseUrl;
                Run(L.T("Checking token"), () => CheckAsync(baseUrl, token));
            }

            if (!has)
            {
                EditorGUILayout.LabelField(L.T("Status"), L.T("not set"));
            }
            else if (info == null)
            {
                EditorGUILayout.LabelField(L.T("Status"), _busy ? L.T("checking…") : L.T("saved, not checked"));
            }
            else
            {
                EditorGUILayout.LabelField(L.T("User"), string.IsNullOrEmpty(info.UserDisplayName) ? info.UserName : info.UserDisplayName + " (" + info.UserName + ")");
                if (info.Detailed)
                {
                    EditorGUILayout.LabelField(L.Tc("token", "Name"), string.IsNullOrEmpty(info.Name) ? "—" : info.Name);
                    EditorGUILayout.LabelField(L.T("Scopes"), info.Scopes.Length == 0 ? "—" : string.Join(", ", info.Scopes));
                    EditorGUILayout.LabelField(L.T("Expires"), info.ExpiresAt.HasValue
                        ? info.ExpiresAt.Value.ToString("dd.MM.yyyy") + (info.DaysLeft.HasValue ? L.F(" (days left: {0})", info.DaysLeft.Value) : string.Empty)
                        : L.T("never"));

                    if (!info.HasScope("api"))
                        EditorGUILayout.HelpBox(L.T("The token has no api scope: reading data works, but creating merge requests and commenting does not."), MessageType.Warning);
                    if (info.DaysLeft.HasValue && info.DaysLeft.Value <= 7)
                        EditorGUILayout.HelpBox(info.DaysLeft.Value < 0 ? L.T("The token has expired.") : L.F("The token expires in {0} d. — renew it.", info.DaysLeft.Value), MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.LabelField(L.T("Expiry and Scopes"), L.T("not reported by the server (GitLab older than 15.5)"));
                }
                EditorGUILayout.LabelField(L.T("Checked"), info.CheckedAt.ToString("HH:mm"), EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUI.DisabledScope(_busy))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(has ? L.T("Replace…") : L.T("Set…"), GUILayout.Width(110f))) SetToken(baseUrl);
                    if (GUILayout.Button(L.T("Create in GitLab"), GUILayout.Width(130f))) Application.OpenURL(GitLabToken.CreatePageUrl(baseUrl));

                    using (new EditorGUI.DisabledScope(!has))
                    {
                        if (GUILayout.Button(L.T("Check"), GUILayout.Width(90f)))
                            Run(L.T("Checking token"), () => CheckAsync(baseUrl, token));
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!has))
                    {
                        var rotate = GUILayoutUtility.GetRect(L.C("Rotate ▾"), GUI.skin.button, GUILayout.Width(110f));
                        if (GUI.Button(rotate, L.C("Rotate ▾", "Issue a new token to replace the current one: the server will revoke the old one")))
                            ShowRotateMenu(rotate, baseUrl, token);

                        if (GUILayout.Button(L.T("Delete from This Computer"), GUILayout.Width(190f)) &&
                            EditorUtility.DisplayDialog(L.T("Delete Token"), L.T("The API token will be deleted from this computer. It will remain valid on the server."), L.T("Delete"), L.T("Cancel")))
                        {
                            GitLabToken.Forget(baseUrl);
                            SetStatus(L.T("Token deleted from this computer."), false);
                        }

                        if (GUILayout.Button(L.T("Revoke on Server…"), GUILayout.Width(150f)) &&
                            EditorUtility.DisplayDialog(L.T("Revoke Token"),
                                L.T("The token will be revoked in GitLab and will stop working everywhere it is used. " +
                                    "Push is not affected: git uses its own credentials for it."), L.T("Revoke"), L.T("Cancel")))
                        {
                            Run(L.T("Revoking token"), async () =>
                            {
                                if (await GitLabToken.RevokeAsync(baseUrl, token, e => SetStatus(e, true)))
                                    SetStatus(L.T("Token revoked on the server and deleted from this computer."), false);
                            });
                        }
                    }
                }
            }
        }

        // ----------------------------------------------------- рабочий процесс ---

        private static GUIContent[] AfterMergeOptions => L.Cs("Ask", "Immediately", "Never");

        private static void DrawWorkflow(GitLabSettings s)
        {
            EditorGUILayout.LabelField(L.T("Workflow"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(L.T("Teams work differently — this is configured per project here and committed with it. " +
                                           "Editor rules are applied on top of GitLab rules and never loosen them."),
                                       EditorStyles.wordWrappedMiniLabel);

            EditorGUI.BeginChangeCheck();

            // ---- слияние ----
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(L.T("Merging"), EditorStyles.miniBoldLabel);
            s.mergePolicy = EditorGUILayout.Popup(L.C("Who Can Merge from the Editor"), s.mergePolicy,
                L.Cs("As GitLab Allows", "Approved MR Only", "Don't Merge from the Editor"));
            using (new EditorGUI.DisabledScope(s.mergePolicy == (int)MergePolicy.NeverInEditor))
            {
                s.allowMergeWhenPipelineSucceeds = EditorGUILayout.Toggle(L.C("“Merge When Pipeline Succeeds”",
                    "Allow queuing a merge before the pipeline finishes"), s.allowMergeWhenPipelineSucceeds);
                s.requireResolvedDiscussions = EditorGUILayout.Toggle(L.C("Require Resolved Threads",
                    "The button stays disabled while there are unresolved threads, even if GitLab does not require it"), s.requireResolvedDiscussions);
            }

            // ---- ветка из задачи ----
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(L.T("Branch from Issue"), EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                s.branchTemplate = EditorGUILayout.TextField(L.C("Name Template",
                    "Placeholders: {iid}, {title}, {type} — from the issue label, {user}"), s.branchTemplate);

                var presets = GUILayoutUtility.GetRect(L.C("Presets ▾"), EditorStyles.miniButton, GUILayout.Width(84f));
                if (GUI.Button(presets, L.T("Presets ▾"), EditorStyles.miniButton))
                {
                    var menu = new GenericMenu();
                    foreach (var p in GitLabWorkflow.BranchPresets)
                    {
                        var preset = p;
                        menu.AddItem(new GUIContent(preset.Replace('/', '∕')), s.branchTemplate == preset, () =>
                        {
                            s.branchTemplate = preset;
                            s.Persist();
                            GUI.FocusControl(null);
                        });
                    }
                    menu.DropDown(presets);
                }
            }
            s.branchTransliterate = EditorGUILayout.Toggle(L.C("Transliteration", "Cyrillic to Latin: “Исправить прыжок” → ispravit-pryzhok"), s.branchTransliterate); // loc-ignore
            s.branchTitleMaxLength = EditorGUILayout.IntSlider(L.C("Title Length"), s.branchTitleMaxLength, 10, 80);
            s.branchSeparator = EditorGUILayout.Popup(L.C("Separator"), s.branchSeparator == "_" ? 1 : 0,
                                                      L.Cs("Hyphen  -", "Underscore  _")) == 1 ? "_" : "-";

            var user = GitLabToken.LastInfo(GitLabInstance.EffectiveBaseUrl);
            var sampleTitle = L.T("Fix character jump");
            var example = GitLabWorkflow.BranchName(s.branchTemplate, 42, sampleTitle, new[] { "bug" },
                user != null && !string.IsNullOrEmpty(user.UserName) ? user.UserName : "user",
                s.branchTransliterate, s.branchTitleMaxLength, s.branchSeparator);
            EditorGUILayout.LabelField(" ", L.F("Example for issue #42 “{0}” with label bug:  {1}", sampleTitle, example), EditorStyles.miniLabel);

            s.closesIssueInDescription = EditorGUILayout.Toggle(L.C("“Closes #N” in MR Description",
                "GitLab will close the issue when the MR is merged"), s.closesIssueInDescription);
            s.issueBranchBase = EditorGUILayout.Popup(L.C("Branch Off From",
                "The project default branch is fetched fresh from the server"), s.issueBranchBase, new[]
            {
                new GUIContent(L.Tc("branch off from", "Project Default Branch")), new GUIContent(L.Tc("branch off from", "Current State"))
            });
            s.issueBranchPush = EditorGUILayout.Toggle(L.C("Push the Branch Right Away",
                "Colleagues will see that the issue has been started even before the first commit"), s.issueBranchPush);
            s.issueAssignSelf = EditorGUILayout.Toggle(L.C("Assign the Issue to Me",
                "When a branch is created from an issue that has no assignee"), s.issueAssignSelf);

            // ---- после слияния ----
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(L.T("After the MR Is Merged"), EditorStyles.miniBoldLabel);
            s.afterMergeCheckoutTarget = EditorGUILayout.Popup(L.C("Switch to Target Branch and Pull"), s.afterMergeCheckoutTarget, AfterMergeOptions);
            s.afterMergeDeleteLocalBranch = EditorGUILayout.Popup(L.C("Delete Local MR Branch"), s.afterMergeDeleteLocalBranch, AfterMergeOptions);
            s.afterMergeDeleteRemoteBranch = EditorGUILayout.Popup(L.C("Delete Branch on Server"), s.afterMergeDeleteRemoteBranch,
                L.Cs("As Set in the MR", "Always", "Never"));
            s.afterMergeUnlock = EditorGUILayout.Popup(L.C("Unlock My Locks on MR Files"), s.afterMergeUnlock, AfterMergeOptions);

            // ---- новый MR ----
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(L.T("New Merge Request"), EditorStyles.miniBoldLabel);
            s.targetBranchMode = EditorGUILayout.Popup(L.C("Target Branch"), s.targetBranchMode,
                L.Cs("Project Default Branch", "Branch the Current One Was Created From", "Fixed"));
            if (s.targetBranchMode == (int)TargetBranchMode.Fixed)
                s.fixedTargetBranch = EditorGUILayout.TextField(L.C("Fixed Branch"), s.fixedTargetBranch);
            s.createAsDraft = EditorGUILayout.Toggle(L.C("Create as Draft"), s.createAsDraft);
            s.squashMode = EditorGUILayout.Popup(new GUIContent("Squash"), s.squashMode,
                L.Cs("As Configured in the GitLab Project", "Always", "Never"));
            s.descriptionMode = EditorGUILayout.Popup(L.C("Description"), s.descriptionMode,
                L.Cs("Project Template (.gitlab/merge_request_templates)", "Commit List", "Leave Empty"));

            // ---- список ----
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(L.T("GitLab Tab"), EditorStyles.miniBoldLabel);
            s.defaultListFilter = EditorGUILayout.Popup(L.C("MR List on Open"), s.defaultListFilter, new[]
            {
                new GUIContent(L.Tc("list filter", "Mine")), new GUIContent(L.Tc("list filter", "Awaiting My Review")),
                new GUIContent(L.Tc("list filter", "All Open")), new GUIContent(L.Tc("list filter", "Merged")),
                new GUIContent(L.Tc("list filter", "Closed"))
            });
            s.defaultIssueFilter = EditorGUILayout.Popup(L.C("Issue List on Open"), s.defaultIssueFilter, new[]
            {
                new GUIContent(L.Tc("list filter", "Assigned to Me")), new GUIContent(L.Tc("list filter", "Created by Me")),
                new GUIContent(L.Tc("list filter", "All Open")), new GUIContent(L.Tc("list filter", "Closed"))
            });
            s.issueScreenshot = EditorGUILayout.Popup(L.C("Screenshot in New Issue",
                "What to attach to an issue by default; can be changed in the creation window"), s.issueScreenshot,
                L.Cs("Game Window", "Scene Window", "No Screenshot"));

            if (EditorGUI.EndChangeCheck())
            {
                s.branchTitleMaxLength = Mathf.Clamp(s.branchTitleMaxLength, 10, 80);
                s.Persist();
            }
        }

        private static void ShowRotateMenu(Rect anchor, string baseUrl, string token)
        {
            var menu = new GenericMenu();
            foreach (var days in new[] { 30, 90, 365 })
            {
                var d = days;
                var title = L.T("Replacing token");
                menu.AddItem(new GUIContent(d == 365 ? L.T("New Token for a Year") : L.F("New Token for {0} Days", d)), false, () =>
                    Run(title, async () =>
                    {
                        if (!await GitLabToken.RotateAsync(baseUrl, token, d, e => SetStatus(e, true))) return;
                        var fresh = GitLabToken.Read(baseUrl);
                        await CheckAsync(baseUrl, fresh);
                        SetStatus(L.T("A new token was issued, the old one was revoked."), false);
                    }));
            }
            menu.DropDown(anchor);
        }

        private static void SetToken(string baseUrl)
        {
            var token = TokenPromptWindow.Ask(baseUrl, GitLabToken.CreatePageUrl(baseUrl));
            if (string.IsNullOrEmpty(token)) return;

            Run(L.T("Checking new token"), async () =>
            {
                string error = null;
                var info = await GitLabToken.CheckAsync(baseUrl, token, e => error = e);

                // Непроверенный токен не сохраняем: сломанный токен в хранилище хуже, чем никакого.
                if (info == null)
                {
                    SetStatus(L.F("Token not saved: {0}", error), true);
                    return;
                }

                string saveError;
                if (!GitLabToken.Save(baseUrl, token, out saveError))
                {
                    SetStatus(L.F("The server accepted the token, but it was not saved: {0}", saveError), true);
                    return;
                }

                await GitLabToken.CheckAsync(baseUrl, token, e => { });
                SetStatus(L.F("Token saved: {0}.", info.UserName ?? L.T("user unknown")), false);
                GitIntegrations.NotifyChanged();
            });
        }

        private static async Task CheckAsync(string baseUrl, string token)
        {
            string error = null;
            var info = await GitLabToken.CheckAsync(baseUrl, token, e => error = e);
            if (info == null) SetStatus(error, true);
            else SetStatus(null, false);
        }

        private static async void Run(string title, Func<Task> body)
        {
            if (_busy) return;
            _busy = true;
            SetStatus(title + "…", false);
            try
            {
                await body();
                if (_status == title + "…") SetStatus(null, false);
            }
            catch (Exception e)
            {
                SetStatus(title + ": " + e.Message, true);
            }
            finally
            {
                _busy = false;
                SettingsService.RepaintAllSettingsWindow();
            }
        }

        private static void SetStatus(string text, bool error)
        {
            _status = text;
            _statusError = error;
            SettingsService.RepaintAllSettingsWindow();
        }
    }

    /// <summary>Модальный ввод токена. Значение не сохраняется окном и живёт в нём не дольше показа.</summary>
    internal sealed class TokenPromptWindow : EditorWindow, ILocalizedWindow
    {
        private string _baseUrl, _createUrl, _token = string.Empty;
        private bool _ok;

        public static string Ask(string baseUrl, string createUrl)
        {
            var w = CreateInstance<TokenPromptWindow>();
            w.SetTitle();
            w._baseUrl = baseUrl;
            w._createUrl = createUrl;
            w.minSize = new Vector2(460f, 170f);
            w.maxSize = new Vector2(640f, 200f);
            w.ShowModalUtility();

            var token = w._ok ? w._token : null;
            w._token = string.Empty;
            return token;
        }

        private void SetTitle()
        {
            titleContent = new GUIContent(L.T("GitLab API Token"));
        }

        void ILocalizedWindow.OnLanguageChanged()
        {
            SetTitle();
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(L.F("API Token for {0}", _baseUrl), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(L.T("Personal access token with the api scope. It is saved only after it is checked on the server."),
                                       EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4f);
            _token = EditorGUILayout.PasswordField(L.T("Token"), _token);

            if (!string.IsNullOrEmpty(_createUrl) && GUILayout.Button(L.T("Create Token in GitLab"), EditorStyles.miniButton, GUILayout.Width(170f)))
                Application.OpenURL(_createUrl);

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(L.T("Cancel"), GUILayout.Width(90f))) { _ok = false; Close(); }
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_token)))
                    if (GUILayout.Button(L.T("Save"), GUILayout.Width(90f))) { _ok = true; Close(); }
            }
            EditorGUILayout.Space(4f);
        }
    }
}
