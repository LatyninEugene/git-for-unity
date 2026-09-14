using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Project Settings → Git.
    ///
    /// Remote репозитория, настройки самого git и список интеграций. Remote
    /// хранятся не в настройках проекта, а в .git/config рабочей копии, — здесь
    /// им просто удобное место: правка идёт командами git, и то же самое
    /// увидит любой другой клиент.
    ///
    /// На IMGUI: страница целиком из полей и переключателей, и так она выглядит
    /// ровно как остальные страницы настроек проекта.
    /// </summary>
    internal static class GitSettingsProvider
    {
        public const string PagePath = "Project/Git";

        private static string _status;
        private static bool _statusError;
        private static bool _busy;

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(PagePath, SettingsScope.Project)
            {
                label = "Git",
                // Remote могли поменять из консоли, пока страница была закрыта.
                activateHandler = (_, __) => { if (GitRepository.IsRepo) GitRepository.LoadRemotes(); },
                guiHandler = _ => Draw(),
                keywords = new HashSet<string>(new[]
                {
                    "git", "remote", "push", "fetch", "origin", "коммит", "строки", "сцена", "лок", "lfs", "интеграция", "gitlab", // loc-ignore
                    "превью", "фон", "preview", "background", // loc-ignore
                    "commit", "lines", "scene", "lock", "integration", "thumbnail", "language"
                })
            };
        }

        private static void Draw()
        {
            var s = GitSettings.instance;
            EditorGUIUtility.labelWidth = 240f;

            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(10, 10, 10, 10) }))
            {
                L.LanguageField();
                EditorGUILayout.Space(8f);

                DrawRemotes(s);

                EditorGUI.BeginChangeCheck();

                // ------------------------------------------------------ изменения ---
                EditorGUILayout.Space(12f);
                EditorGUILayout.LabelField(L.T("Working with Changes"), EditorStyles.boldLabel);

                var lineLevel = EditorGUILayout.Toggle(L.C("Select Individual Lines",
                    "A checkbox on every changed diff line, not only on the hunk."), s.lineLevelSelection);

                // ---------------------------------------------------------- сцены ---
                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField(L.T("Scenes"), EditorStyles.boldLabel);

                var sceneIndicators = EditorGUILayout.Toggle(L.C("Scene Changes in Hierarchy",
                    "Markers on changed objects in the Hierarchy window and a summary in the Inspector. " +
                    "Recalculation parses the whole scene file — you can turn it off for very large scenes."), s.sceneChangeIndicators);

                var blame = EditorGUILayout.Toggle(L.C("Last Changed By in Inspector",
                    "The object header shows the last change from the scene history."), s.objectBlame);

                // ----------------------------------------------------------- локи ---
                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField(L.T("Locks"), EditorStyles.boldLabel);

                var autoLock = EditorGUILayout.Popup(L.C("Auto-Lock on First Edit",
                        "The first edit of a file takes the lock automatically. A lock is visible to the whole team, so it is off by default."),
                    s.autoLock,
                    L.Cs("Off", "Scenes Only", "All Lockable Files"));

                var readOnlySave = EditorGUILayout.Popup(L.C("Saving Without a Lock",
                        "git-lfs keeps a file with the lockable attribute read-only until it is locked, and Unity will not write it. " +
                        "What to do on save: ask, take the lock, or save without a lock by clearing “read-only”. " +
                        "If auto-lock is enabled for this file, the lock is taken without asking."),
                    s.readOnlySave,
                    L.Cs("Ask", "Take Lock", "Save Without Lock"));

                // ------------------------------------------------- превью ассетов ---
                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField(L.T("Asset Preview"), EditorStyles.boldLabel);

                var background = EditorGUILayout.Popup(L.C("Preview Background",
                        "Background under images, material spheres and audio waveforms on the “View” tab. " +
                        "Dark sprites are easier to see on a light background, light ones on a dark background."),
                    Mathf.Clamp(s.previewBackground, 0, Preview.PreviewBackground.Names.Length - 1),
                    Preview.PreviewBackground.Names);

                var backgroundColor = s.previewBackgroundColor;
                if (background == Preview.PreviewBackground.Custom)
                    backgroundColor = EditorGUILayout.ColorField(L.C("Background Color"), s.previewBackgroundColor, true, false, false);

                var checker = EditorGUILayout.Toggle(L.C("Checkerboard Under Transparent Images",
                    "A checkerboard is drawn under the texture so that transparent areas are visible. With the “Checkerboard” background it is everywhere anyway."),
                    s.previewCheckerUnderImages);

                var importSandbox = EditorGUILayout.Popup(L.C("Import for Preview",
                        "Unity reads models, PSD, EXR and fonts only through import. The previous version is placed in the temporary folder " +
                        "Assets/GitPreview (git does not see it) and deleted when no longer needed. " +
                        "“On Button” imports only after you click “Import Previous Version”."),
                    Mathf.Clamp(s.importSandbox, 0, 2),
                    L.Cs("Off", "On Button", "Always"));

                var dependencies = EditorGUILayout.Popup(L.C("Previous Version Dependencies",
                        "The previous version of a material or prefab references the project's textures and materials — the current ones. " +
                        "“From the Same Commit” substitutes their versions from that commit if they have changed since."),
                    Mathf.Clamp(s.previewDependencies, 0, 1),
                    L.Cs("From the Current Project", "From the Same Commit"));

                var listThumbnails = EditorGUILayout.Popup(L.C("Thumbnails in File Lists",
                        "Instead of an icon — a small “before → after” pair in “Changes”, commit files and merge requests. " +
                        "Materials and models take longer to draw than images, so by default only images."),
                    Mathf.Clamp(s.listThumbnails, 0, 2),
                    new[] { new GUIContent(L.Tc("thumbnails", "Off")), new GUIContent(L.T("Images Only")), new GUIContent(L.T("All Types")) });

                var storageLimit = EditorGUILayout.IntField(L.C("Preview Storage Limit, MB",
                        "Import sandbox, thumbnails and temporary versions together. When exceeded, " +
                        "import not open in a preview is deleted first, then old thumbnails."),
                    Mathf.Max(64, s.previewStorageLimitMb));

                if (EditorGUI.EndChangeCheck())
                {
                    s.previewStorageLimitMb = Mathf.Max(64, storageLimit);
                    s.listThumbnails = listThumbnails;
                    s.lineLevelSelection = lineLevel;
                    s.sceneChangeIndicators = sceneIndicators;
                    s.objectBlame = blame;
                    s.autoLock = autoLock;
                    s.readOnlySave = readOnlySave;
                    s.previewBackground = background;
                    s.previewBackgroundColor = backgroundColor;
                    s.previewCheckerUnderImages = checker;
                    s.importSandbox = importSandbox;
                    s.previewDependencies = dependencies;
                    s.Persist();
                    SceneChangeIndex.MarkDirty();
                    Preview.PreviewBackground.NotifyChanged();
                }

                DrawStorage();

                // ----------------------------------------------------- интеграции ---
                EditorGUILayout.Space(12f);
                DrawIntegrations();

                EditorGUILayout.Space(10f);
                if (GUILayout.Button(L.T("Open Git Window"), GUILayout.Width(180f)))
                    GitWindow.Open();
            }
        }

        // ------------------------------------------------------ место на диске ---

        private static string _storageStatus;

        private static void DrawStorage()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(L.T("Disk Space"), EditorStyles.boldLabel);

            var areas = Preview.PreviewStorage.Measure();
            long managed = areas[0].Bytes + areas[1].Bytes + areas[2].Bytes;
            long limit = Preview.PreviewStorage.LimitBytes;

            EditorGUILayout.LabelField(L.F("Preview uses {0} of {1}", Preview.PreviewStorage.Human(managed),
                Preview.PreviewStorage.Human(limit)), EditorStyles.miniLabel);

            var bar = GUILayoutUtility.GetRect(0f, 4f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(bar, new Color(0.5f, 0.5f, 0.5f, 0.2f));
                float part = Mathf.Clamp01(managed / (float)limit);
                EditorGUI.DrawRect(new Rect(bar.x, bar.y, bar.width * part, bar.height),
                    part > 0.9f ? new Color(0.9f, 0.45f, 0.35f) : new Color(0.35f, 0.55f, 0.85f));
            }

            var warning = Preview.PreviewStorage.GitWarning;
            if (warning != null) EditorGUILayout.HelpBox(warning, MessageType.Warning);

            for (int i = 0; i < areas.Length; i++)
            {
                var area = areas[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(area.Title, area.Hint + "\n" + area.Path),
                        new GUIContent(area.Files > 0
                            ? L.F("{0} · files {1}", Preview.PreviewStorage.Human(area.Bytes), area.Files)
                            : Preview.PreviewStorage.Human(area.Bytes)));

                    using (new EditorGUI.DisabledScope(area.Bytes == 0 && area.Files == 0))
                    {
                        if (GUILayout.Button(L.T("Open"), EditorStyles.miniButtonLeft, GUILayout.Width(64f)))
                            EditorUtility.RevealInFinder(area.Path);

                        if (GUILayout.Button(L.T("Clear"), EditorStyles.miniButtonRight, GUILayout.Width(72f)))
                        {
                            bool backups = i == 3;
                            if (!backups || EditorUtility.DisplayDialog(L.T("Delete Merge Backups"),
                                    L.F("File copies made before conflict resolution will be permanently deleted ({0}).",
                                        Preview.PreviewStorage.Human(area.Bytes)), L.T("Delete"), L.T("Cancel")))
                                _storageStatus = Preview.PreviewStorage.Clear(i);
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(_storageStatus))
                EditorGUILayout.LabelField(_storageStatus, EditorStyles.miniLabel);
        }

        // ------------------------------------------------------------ remotes ---

        private static void DrawRemotes(GitSettings s)
        {
            EditorGUILayout.LabelField("Remote", EditorStyles.boldLabel);

            if (!GitRepository.IsRepo)
            {
                EditorGUILayout.HelpBox(L.T("The project is not inside a git repository."), MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(L.T("Stored in .git/config of this working tree and not committed to the project."),
                                       EditorStyles.wordWrappedMiniLabel);

            var remotes = GitRepository.Remotes;
            var upstream = GitStatusCache.Upstream;
            var upstreamRemote = !string.IsNullOrEmpty(upstream) && upstream.IndexOf('/') > 0
                ? upstream.Substring(0, upstream.IndexOf('/')) : null;

            if (remotes.Count == 0)
                EditorGUILayout.HelpBox(L.T("The repository has no remotes: push and fetch have nowhere to go."), MessageType.Warning);

            using (new EditorGUI.DisabledScope(_busy))
            {
                foreach (var remote in remotes)
                {
                    var r = remote;
                    bool main = r.Name == GitRepository.SelectedRemoteName;

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var title = r.Name + (main ? "   · " + L.Tc("remote", "primary") : string.Empty) +
                                        (r.Name == upstreamRemote ? "   · " + L.F("upstream of branch {0}", GitStatusCache.Branch) : string.Empty);
                            GUILayout.Label(title, EditorStyles.boldLabel);
                            GUILayout.FlexibleSpace();

                            if (!main && GUILayout.Button(L.C("Make Primary",
                                    "Primary remote: a branch without upstream is pushed here, integrations work with it"), EditorStyles.miniButtonLeft))
                            {
                                s.remoteName = r.Name;
                                s.Persist();
                                GitRepository.LoadRemotes();
                                GitIntegrations.NotifyChanged();
                            }

                            if (GUILayout.Button(L.T("Edit…"), main ? EditorStyles.miniButtonLeft : EditorStyles.miniButtonMid)) Edit(r);
                            if (GUILayout.Button(L.T("Test"), EditorStyles.miniButtonMid)) Test(r.Name);
                            if (GUILayout.Button(L.T("Delete…"), EditorStyles.miniButtonRight)) Remove(r, upstreamRemote);
                        }

                        EditorGUILayout.SelectableLabel((r.SameBothWays ? string.Empty : "fetch: ") + (r.FetchUrl ?? "—"),
                                                        EditorStyles.miniLabel, GUILayout.Height(14f));
                        if (!r.SameBothWays)
                            EditorGUILayout.SelectableLabel("push:  " + (r.PushUrl ?? "—"), EditorStyles.miniLabel, GUILayout.Height(14f));
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L.T("Add Remote…"), GUILayout.Width(150f))) Add();
                    GUILayout.FlexibleSpace();
                }
            }

            // `git push` без аргументов уходит в upstream, а он может быть и на другом remote.
            var dest = !string.IsNullOrEmpty(upstream) ? upstream
                : !string.IsNullOrEmpty(GitStatusCache.Branch) && remotes.Count > 0
                    ? L.F("{0}/{1} (upstream will be created)", GitRepository.SelectedRemoteName, GitStatusCache.Branch)
                    : "—";
            EditorGUILayout.LabelField(L.T("Push Button Sends To"), dest);
            EditorGUILayout.LabelField(" ", L.T("Another remote — via ▾ next to the Push button in the Git window."), EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, _statusError ? MessageType.Error : MessageType.Info);
        }

        private static List<string> Names()
        {
            var list = new List<string>();
            foreach (var r in GitRepository.Remotes) list.Add(r.Name);
            return list;
        }

        private static void Add()
        {
            var result = RemoteEditWindow.Ask(L.T("Add Remote"), Names(), null,
                GitRepository.Remotes.Count == 0 ? "origin" : string.Empty, string.Empty, null);
            if (result == null) return;

            Run(L.F("Adding {0}", result.Name), async () =>
            {
                var r = await GitOperations.RemoteAddAsync(result.Name, result.FetchUrl);
                if (!r.Ok) { SetStatus(r.Message, true); return; }

                if (result.PushUrl != null)
                {
                    r = await GitOperations.RemoteSetUrlAsync(result.Name, result.PushUrl, true);
                    if (!r.Ok) { SetStatus(L.F("Remote added, but the push URL was not set: {0}", r.Message), true); return; }
                }

                // Первый remote в репозитории — сразу основной.
                if (GitRepository.Remotes.Count == 0)
                {
                    GitSettings.instance.remoteName = result.Name;
                    GitSettings.instance.Persist();
                }

                SetStatus(L.F("Remote “{0}” added. Its branches will appear after Fetch.", result.Name), false);
            });
        }

        private static void Edit(GitRemoteEntry remote)
        {
            var result = RemoteEditWindow.Ask(L.T("Edit Remote"), Names(), remote.Name, remote.Name,
                remote.FetchUrl ?? string.Empty, remote.SameBothWays ? null : remote.PushUrl);
            if (result == null) return;

            Run(L.F("Editing {0}", remote.Name), async () =>
            {
                var name = remote.Name;

                if (result.Name != name)
                {
                    var rename = await GitOperations.RemoteRenameAsync(name, result.Name);
                    if (!rename.Ok) { SetStatus(rename.Message, true); return; }

                    if (GitSettings.instance.remoteName == name)
                    {
                        GitSettings.instance.remoteName = result.Name;
                        GitSettings.instance.Persist();
                    }
                    name = result.Name;
                }

                if (result.FetchUrl != remote.FetchUrl)
                {
                    var set = await GitOperations.RemoteSetUrlAsync(name, result.FetchUrl, false);
                    if (!set.Ok) { SetStatus(set.Message, true); return; }
                }

                var push = result.PushUrl != null
                    ? await GitOperations.RemoteSetUrlAsync(name, result.PushUrl, true)
                    : await GitOperations.RemoteClearPushUrlAsync(name);
                if (!push.Ok) { SetStatus(L.F("Push URL not changed: {0}", push.Message), true); return; }

                SetStatus(L.F("Remote “{0}” saved.", name), false);
            });
        }

        private static void Remove(GitRemoteEntry remote, string upstreamRemote)
        {
            var text = L.F("Remote “{0}” will be removed from .git/config together with its remote-tracking branches " +
                           "(refs/remotes/{0}). Branches on the server itself are not touched.", remote.Name);
            if (remote.Name == upstreamRemote)
                text += "\n\n" + L.T("The current branch tracks this remote: after removal it will have no upstream.");
            if (remote.Name == GitRepository.SelectedRemoteName && GitRepository.Remotes.Count > 1)
                text += "\n\n" + L.T("This is the primary remote — the next one in the list will become primary.");

            if (!EditorUtility.DisplayDialog(L.T("Delete Remote"), text, L.T("Delete"), L.T("Cancel"))) return;

            Run(L.F("Deleting {0}", remote.Name), async () =>
            {
                var r = await GitOperations.RemoteRemoveAsync(remote.Name);
                if (!r.Ok) { SetStatus(r.Message, true); return; }

                if (GitSettings.instance.remoteName == remote.Name)
                {
                    foreach (var other in GitRepository.Remotes)
                        if (other.Name != remote.Name) { GitSettings.instance.remoteName = other.Name; break; }
                    GitSettings.instance.Persist();
                }

                SetStatus(L.F("Remote “{0}” deleted.", remote.Name), false);
            });
        }

        private static void Test(string name)
        {
            Run(L.F("Testing {0}", name), async () =>
            {
                var r = await GitOperations.RemoteTestAsync(name);
                if (r.Ok)
                {
                    int heads = 0;
                    foreach (var line in r.StdOut.Split('\n')) if (line.Trim().Length > 0) heads++;
                    SetStatus(heads > 0
                        ? L.F("“{0}” is reachable, branches on the server: {1}.", name, heads)
                        : L.F("“{0}” is reachable — the repository is empty.", name), false);
                    return;
                }

                var problem = GitAuth.Classify(r, GitRepository.Remote);
                SetStatus(problem == GitAuthProblem.HttpCredentials
                    ? L.F("“{0}” requires signing in. Fetch from it in the Git window — a credentials prompt will appear there.", name)
                    : problem == GitAuthProblem.SshKey
                        ? L.F("“{0}”: the server did not accept the SSH key.", name)
                        : L.F("“{0}” is unreachable: {1}", name, r.Message), true);
            });
        }

        private static async void Run(string title, Func<Task> body)
        {
            if (_busy) return;
            _busy = true;
            SetStatus(title + "…", false);

            try
            {
                await body();
            }
            catch (Exception e)
            {
                SetStatus(title + ": " + e.Message, true);
            }
            finally
            {
                _busy = false;
                GitRepository.LoadRemotes();
                await GitStatusCache.RefreshAsync();
                GitIntegrations.NotifyChanged();
                SettingsService.RepaintAllSettingsWindow();
            }
        }

        private static void SetStatus(string text, bool error)
        {
            _status = text;
            _statusError = error;
            SettingsService.RepaintAllSettingsWindow();
        }

        // --------------------------------------------------------- интеграции ---

        private static void DrawIntegrations()
        {
            EditorGUILayout.LabelField(L.T("Integrations"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(L.T("Everything related to git itself works without them. An integration adds hosting features."),
                                       EditorStyles.wordWrappedMiniLabel);

            var all = GitIntegrations.All;
            if (all.Count == 0)
            {
                EditorGUILayout.HelpBox(L.T("No integrations found."), MessageType.Info);
                return;
            }

            var suggested = GitIntegrations.Suggested(GitRepository.Remote);
            if (suggested != null)
                EditorGUILayout.HelpBox(L.F("The remote of this project looks like {0} — you can enable the integration.", suggested.DisplayName), MessageType.Info);

            foreach (var integration in all)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        bool on = EditorGUILayout.ToggleLeft(integration.DisplayName, integration.Enabled, EditorStyles.boldLabel);
                        if (on != integration.Enabled) integration.Enabled = on;
                        EditorGUILayout.LabelField(integration.Description, EditorStyles.wordWrappedMiniLabel);
                    }

                    using (new EditorGUI.DisabledScope(!integration.Enabled))
                        if (GUILayout.Button(L.T("Configure…"), GUILayout.Width(100f)))
                            SettingsService.OpenProjectSettings(integration.SettingsPath);
                }
            }
        }
    }

    /// <summary>Модальное окно добавления и правки remote с проверкой полей на лету.</summary>
    internal sealed class RemoteEditWindow : EditorWindow
    {
        public sealed class Result
        {
            public string Name;
            public string FetchUrl;

            /// <summary>Отдельный адрес для push. null — push идёт по адресу fetch.</summary>
            public string PushUrl;
        }

        private List<string> _existing;
        private string _current;
        private string _name, _fetch, _push;
        private bool _separatePush;
        private bool _ok;

        public static Result Ask(string title, List<string> existing, string current, string name, string fetchUrl, string pushUrl)
        {
            var w = CreateInstance<RemoteEditWindow>();
            w.titleContent = new GUIContent(title);
            w._existing = existing;
            w._current = current;
            w._name = name ?? string.Empty;
            w._fetch = fetchUrl ?? string.Empty;
            w._separatePush = pushUrl != null;
            w._push = pushUrl ?? string.Empty;
            w.minSize = new Vector2(520f, 250f);
            w.maxSize = new Vector2(760f, 300f);
            w.ShowModalUtility();

            if (!w._ok) return null;
            return new Result
            {
                Name = w._name.Trim(),
                FetchUrl = w._fetch.Trim(),
                PushUrl = w._separatePush ? w._push.Trim() : null
            };
        }

        private void OnGUI()
        {
            EditorGUIUtility.labelWidth = 130f;
            EditorGUILayout.Space(8f);

            _name = EditorGUILayout.TextField(L.C("Name", "Usually origin, upstream, backup"), _name);
            var nameError = GitRemoteRules.ValidateName(_name, _existing, _current);
            Hint(nameError);

            _fetch = EditorGUILayout.TextField(L.C("URL", "http(s)://host/group/repo.git, git@host:group/repo.git or ssh://…"), _fetch);
            var fetchError = GitRemoteRules.ValidateUrl(_fetch);
            Hint(_fetch.Length == 0 ? null : fetchError);

            _separatePush = EditorGUILayout.Toggle(L.C("Separate Push URL",
                "Fetch from one URL and push to another — for example, read over http and write over ssh"), _separatePush);

            string pushError = null;
            if (_separatePush)
            {
                _push = EditorGUILayout.TextField(L.T("Push URL"), _push);
                pushError = GitRemoteRules.ValidateUrl(_push);
                Hint(_push.Length == 0 ? null : pushError);
            }

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(L.T("Cancel"), GUILayout.Width(90f))) { _ok = false; Close(); }
                using (new EditorGUI.DisabledScope(nameError != null || fetchError != null || (_separatePush && pushError != null)))
                    if (GUILayout.Button(L.T("Save"), GUILayout.Width(90f))) { _ok = true; Close(); }
            }
            EditorGUILayout.Space(6f);
        }

        private static void Hint(string error)
        {
            if (error == null) return;
            var style = new GUIStyle(EditorStyles.wordWrappedMiniLabel);
            style.normal.textColor = GitPalette.Text(GitFileStatus.Conflicted);
            EditorGUILayout.LabelField(" ", error, style);
        }
    }
}
