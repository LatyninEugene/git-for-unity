using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Lev.Git.Diagnostics
{
    internal sealed class ReportFact
    {
        public string Key;
        public string Value;

        /// <summary>Имя ветки и подобное: при «Скрыть пути» заменяется кодом.</summary>
        public bool Private;
    }

    internal sealed class ReportSection
    {
        public string Id;

        /// <summary>Заголовок в отчёте — всегда по-английски: отчёт читает автор.</summary>
        public string Title;

        /// <summary>Что в разделе — подсказка в окне, переводится при показе.</summary>
        public string Hint;

        public bool Included = true;
        public readonly List<ReportFact> Facts = new List<ReportFact>();
        public string Text;

        public ReportSection(string id, string title, string hint)
        {
            Id = id;
            Title = title;
            Hint = hint;
        }

        public void Add(string key, object value, bool isPrivate = false)
        {
            string text;
            if (value == null) text = "—";
            else if (value is bool) text = (bool)value ? "yes" : "no";
            else text = Convert.ToString(value, CultureInfo.InvariantCulture);
            Facts.Add(new ReportFact { Key = key, Value = text, Private = isPrivate });
        }
    }

    internal sealed class ReportOptions
    {
        public string Description = string.Empty;
        public string Steps = string.Empty;
        public bool HidePaths;
        public bool HideServers = true;
        public int LogDays = 3;
        public bool Screenshot;
        public readonly List<string> Attachments = new List<string>();
    }

    /// <summary>
    /// Отчёт о проблеме: что за среда, что за репозиторий, что пакет делал и
    /// где падал. Собирается в zip, который человек сам отправляет автору.
    ///
    /// Отчёт — по-английски независимо от языка интерфейса: его читает автор,
    /// и ему удобнее один формат. Секреты вычищаются всегда; пути и адрес
    /// сервера — по выбору.
    /// </summary>
    internal static class ProblemReport
    {
        public const int Format = 1;
        private const long MaxAttachmentBytes = 50L * 1024 * 1024;

        /// <summary>Имена веток последнего сбора — их скрывает «Скрыть пути».</summary>
        private static List<string> _privateNames = new List<string>();

        private static readonly string[] SectionNames =
        {
            L.M("Environment"), L.M("Unity, operating system, package version and interface language."),
            L.M("Git"), L.M("Git and Git LFS versions and the git settings that affect the package."),
            L.M("Repository"), L.M("Branch, number of changes and conflicts, locks. Without file contents."),
            L.M("Settings"), L.M("Project settings of the package. Server addresses are not included."),
            L.M("Integrations and extensions"), L.M("Enabled integrations and third-party preview extensions."),
            L.M("Self-check"), L.M("Whether the package found the Unity internals it relies on."),
            L.M("Recent problems"), L.M("Warnings and errors of the package in this editor session."),
            L.M("Git commands"), L.M("Recent git commands with exit codes and error output.")
        };

        // ------------------------------------------------------------ сбор ---

        /// <summary>Собирает разделы. Данные редактора — в главном потоке, команды git — в фоне.</summary>
        public static async Task<List<ReportSection>> CollectAsync()
        {
            var projectRoot = GitRepository.ProjectRoot;
            var cwd = GitRepository.IsRepo ? GitRepository.RepoRoot : projectRoot;

            var environment = Environment();
            var repository = RepositoryFromCache();
            var settings = Settings();
            var integrations = Integrations();
            var selfCheck = SelfCheck();
            var problems = Problems();
            var commands = Commands();

            // Ветка и remote для сверки с сервером — из главного потока, из кэша статуса.
            var branch = GitStatusCache.IsDetached ? null : GitStatusCache.Branch;
            var upstream = GitStatusCache.Upstream;
            int slash = string.IsNullOrEmpty(upstream) ? -1 : upstream.IndexOf('/');
            var remote = slash > 0 ? upstream.Substring(0, slash) : GitRepository.SelectedRemoteName;

            var git = new ReportSection("git", "Git", SectionNames[3]);
            var names = new List<string>();
            await Task.Run(() =>
            {
                GitFacts(git, cwd);
                RepositoryFromGit(repository, cwd, branch, remote, names);
            });

            // Имена веток скрываются при «Скрыть пути» по всему тексту отчёта.
            if (!string.IsNullOrEmpty(branch) && !names.Contains(branch)) names.Add(branch);
            _privateNames = names;

            return new List<ReportSection> { environment, git, repository, settings, integrations, selfCheck, problems, commands };
        }

        private static ReportSection Environment()
        {
            var s = new ReportSection("environment", "Environment", SectionNames[1]);
            s.Add("Git for Unity", PackageMeta.Version);
            s.Add("Unity", Application.unityVersion);
            s.Add("Operating system", SystemInfo.operatingSystem);
            s.Add("Editor platform", Application.platform);
            s.Add("System language", L.SystemLanguage);
            s.Add("Interface language", L.Language + (L.Preference == L.Auto ? " (automatic)" : " (chosen)"));
            s.Add(".NET", System.Environment.Version);

            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            s.Add("Render pipeline", pipeline != null ? pipeline.GetType().Name : "Built-in");
            s.Add("Timeline package", AssemblyLoaded("Unity.Timeline"));
            s.Add("Timeline support", AssemblyLoaded("Lev.Git.Timeline.Editor"));
            s.Add("Project inside a git repository", GitRepository.IsRepo);
            s.Add("Project in a subfolder of the repository", GitRepository.IsRepo && !string.IsNullOrEmpty(GitRepository.PathPrefix));
            s.Add("Detailed log", Journal.Verbose);
            s.Add("Report created", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            return s;
        }

        private static void GitFacts(ReportSection s, string cwd)
        {
            var version = Git("--version", cwd);
            s.Add("git", version ?? "not found — git is not in PATH or does not start");
            if (version == null) return;

            s.Add("git-lfs", Git("lfs version", cwd) ?? "not installed");

            var helpers = Git("config --get-all credential.helper", cwd);
            if (helpers != null)
            {
                var names = new List<string>();
                foreach (var h in helpers.Split('\n'))
                {
                    var t = h.Trim();
                    if (t.Length == 0) continue;
                    // Путь до helper'а может содержать имя пользователя — оставляем только имя.
                    var first = t.Split(' ')[0];
                    names.Add(Path.GetFileName(first.Trim('"')));
                }
                s.Add("credential.helper", names.Count > 0 ? string.Join(", ", names.ToArray()) : "—");
            }
            else
            {
                s.Add("credential.helper", "not set");
            }

            foreach (var key in new[] { "core.autocrlf", "core.longpaths", "core.ignorecase", "core.fsmonitor", "core.sparseCheckout", "lfs.locksverify" })
                s.Add(key, Git("config --get " + key, cwd) ?? "not set");

            s.Add("LFS filter configured", Git("config --get filter.lfs.process", cwd) != null);

            var drivers = Git("config --get-regexp ^merge\\..*\\.driver$", cwd);
            s.Add("Merge drivers", drivers == null ? "none" : Regex.Replace(drivers, @"^merge\.([^.]+)\.driver\s.*$", "$1", RegexOptions.Multiline).Replace("\n", ", "));
        }

        private static ReportSection RepositoryFromCache()
        {
            var s = new ReportSection("repository", "Repository", SectionNames[5]);
            if (!GitRepository.IsRepo)
            {
                s.Add("Repository", "not found");
                return s;
            }

            s.Add("Branch", GitStatusCache.IsDetached ? "detached HEAD" : GitStatusCache.Branch, true);
            s.Add("Upstream", GitStatusCache.Upstream, true);
            s.Add("Ahead / behind", GitStatusCache.Ahead + " / " + GitStatusCache.Behind);
            s.Add("Initial commit", GitStatusCache.IsInitialCommit);
            s.Add("Changed files", GitStatusCache.Changes.Count);
            s.Add("Conflicts", GitStatusCache.ConflictCount);
            s.Add("Status refresh", GitStatusCache.LastRefreshMs + " ms");
            if (!string.IsNullOrEmpty(GitStatusCache.LastError)) s.Add("Status error", GitStatusCache.LastError);

            var state = GitConflicts.State;
            s.Add("Operation in progress", state != null && state.InProgress ? state.Title : "none");

            s.Add("Remotes", GitRepository.Remotes.Count);
            var remote = GitRepository.Remote;
            if (remote != null)
            {
                s.Add("Primary remote protocol", remote.Scheme);
                s.Add("Primary remote", remote.Raw);
            }

            s.Add("LFS locks loaded", LfsLockCache.HasData);
            if (LfsLockCache.HasData)
            {
                s.Add("Locks verified by server", LfsLockCache.Locks.Verified);
                s.Add("Locks held by others", LfsLockCache.Locks.TheirsCount);
            }
            s.Add("LFS files not downloaded", LfsLockCache.NotDownloadedCount);
            if (!string.IsNullOrEmpty(LfsLockCache.LastError)) s.Add("Locks error", LfsLockCache.LastError);
            return s;
        }

        private static void RepositoryFromGit(ReportSection s, string cwd, string branch, string remote, List<string> names)
        {
            if (!GitRepository.IsRepo) return;

            s.Add("Commits in HEAD", Git("rev-list --count HEAD", cwd) ?? "—");
            s.Add("Shallow clone", Git("rev-parse --is-shallow-repository", cwd) ?? "—");

            var gitDir = Git("rev-parse --git-dir", cwd);
            var common = Git("rev-parse --git-common-dir", cwd);
            s.Add("Linked worktree", gitDir != null && common != null && gitDir != common);
            s.Add("Submodules", File.Exists(Path.Combine(cwd, ".gitmodules")));

            // Сверка с сервером — по тому, что знает локальный репозиторий. Сам сервер
            // отчёт не спрашивает: сети может не быть, а отчёт должен собраться.
            if (!string.IsNullOrEmpty(branch) && !string.IsNullOrEmpty(remote))
            {
                var tracking = remote + "/" + branch;
                var known = Git("rev-parse --verify --quiet " + GitOperations.Q("refs/remotes/" + tracking), cwd);
                s.Add("Server branch", tracking, true);
                s.Add("Server branch known locally", known != null);
                if (known != null)
                {
                    var counts = Git("rev-list --left-right --count " + GitOperations.Q("HEAD..." + tracking), cwd);
                    var parts = counts == null ? null : Regex.Split(counts.Trim(), @"\s+");
                    s.Add("Ahead / behind server branch", parts != null && parts.Length == 2 ? parts[0] + " / " + parts[1] : "—");
                }
            }

            var fetchHead = gitDir == null ? null
                : Path.Combine(Path.IsPathRooted(gitDir) ? gitDir : Path.Combine(cwd, gitDir), "FETCH_HEAD");
            s.Add("Last fetch", fetchHead != null && File.Exists(fetchHead)
                ? File.GetLastWriteTime(fetchHead).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "never");

            var contains = Git("branch -r --contains HEAD", cwd);
            s.Add("Last commit is on a remote", !string.IsNullOrEmpty(contains));

            var refs = Git("for-each-ref --format=%(refname:short) refs/heads refs/remotes", cwd);
            if (refs != null)
            {
                foreach (var line in refs.Split('\n'))
                {
                    var name = line.Trim();
                    if (name.Length > 1 && !name.EndsWith("/HEAD", StringComparison.Ordinal)) names.Add(name);
                }
            }

            var objects = Git("count-objects -vH", cwd);
            if (objects != null)
                foreach (var line in objects.Split('\n'))
                    if (line.StartsWith("size-pack:", StringComparison.Ordinal) || line.StartsWith("count:", StringComparison.Ordinal))
                        s.Add("objects " + line.Split(':')[0].Trim(), line.Substring(line.IndexOf(':') + 1).Trim());
        }

        private static ReportSection Settings()
        {
            var s = new ReportSection("settings", "Settings", SectionNames[7]);
            try
            {
                if (Directory.Exists("ProjectSettings"))
                {
                    foreach (var file in Directory.GetFiles("ProjectSettings", "LevGit*.asset"))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        foreach (var line in File.ReadAllLines(file))
                        {
                            var m = Regex.Match(line, @"^  (?<key>[A-Za-z_]\w*):\s?(?<value>.*)$");
                            if (!m.Success) continue;
                            var key = m.Groups["key"].Value;
                            if (key.StartsWith("m_", StringComparison.Ordinal)) continue;

                            var value = m.Groups["value"].Value.Trim();
                            // Адреса и пути проекта на сервере — приватные: только «задано / пусто».
                            if (Regex.IsMatch(key, "url|host|projectPath|Override$", RegexOptions.IgnoreCase))
                                value = value.Length == 0 ? "(empty)" : "(set)";
                            s.Add(name + "." + key, value);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                s.Add("Settings could not be read", e.Message);
            }
            return s;
        }

        private static ReportSection Integrations()
        {
            var s = new ReportSection("integrations", "Integrations and extensions", SectionNames[9]);

            foreach (var i in GitIntegrations.All)
            {
                bool enabled;
                try { enabled = i.Enabled; }
                catch { enabled = false; }
                s.Add("Integration " + i.Id, enabled ? "enabled" : "disabled");
            }

            AddExtensions(s, "Preview loaders", TypeCache.GetTypesDerivedFrom<Preview.AssetLoader>());
            AddExtensions(s, "Change describers", TypeCache.GetTypesDerivedFrom<Preview.ChangeDescriber>());
            AddExtensions(s, "Preview presenters", TypeCache.GetTypesDerivedFrom<Preview.AssetPresenter>());
            return s;
        }

        private static void AddExtensions(ReportSection s, string title, TypeCache.TypeCollection types)
        {
            int builtIn = 0;
            var thirdParty = new List<string>();
            foreach (var t in types)
            {
                if (t.IsAbstract) continue;
                var assembly = t.Assembly.GetName().Name;
                if (assembly.StartsWith("Lev.Git", StringComparison.Ordinal)) builtIn++;
                else thirdParty.Add(t.FullName + " (" + assembly + ")");
            }
            s.Add(title, builtIn + " built-in" + (thirdParty.Count > 0 ? ", third-party: " + string.Join("; ", thirdParty.ToArray()) : string.Empty));
        }

        private static ReportSection SelfCheck()
        {
            var s = new ReportSection("self-check", "Self-check", SectionNames[11]);
            s.Add("Component header badge installed", ComponentHeaderBadge.Installed);
            s.Add("Inspector columns: view width hook", UI.AssetPreviewPane.ViewWidthHookFound);
            s.Add("Inspector columns: firstInspectedEditor", UI.AssetPreviewPane.FirstInspectedEditorFound);

            bool shader;
            try { shader = Preview.PreviewCanvas.Advanced; }
            catch { shader = false; }
            s.Add("Preview shader (channels, difference mask)", shader);

            s.Add("Translations found", string.Join(", ", L.Available.ToArray()));

            try
            {
                foreach (var area in Preview.PreviewStorage.Measure(true))
                    s.Add("Storage " + Path.GetFileName((area.Path ?? string.Empty).TrimEnd('/', '\\')),
                          Preview.PreviewStorage.Human(area.Bytes) + ", " + area.Files + " files");
            }
            catch (Exception e)
            {
                s.Add("Storage", "not measured: " + e.Message);
            }
            return s;
        }

        private static ReportSection Problems()
        {
            var s = new ReportSection("problems", "Recent problems", SectionNames[13]);
            var sb = new StringBuilder();
            var list = Journal.RecentProblems();
            int from = Math.Max(0, list.Count - 80);
            for (int i = from; i < list.Count; i++)
            {
                var e = list[i];
                sb.Append(e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(e.Level == JournalLevel.Error ? "E " : "W ")
                  .Append(e.Message.Replace("\n", "\n    ")).Append('\n');
            }
            s.Add("Problems in this session", list.Count);
            s.Text = sb.Length > 0 ? sb.ToString() : null;
            return s;
        }

        private static ReportSection Commands()
        {
            var s = new ReportSection("commands", "Git commands", SectionNames[15]);
            var records = GitCommandLog.Records;
            var sb = new StringBuilder();
            int failed = 0;
            int from = Math.Max(0, records.Count - 60);
            for (int i = from; i < records.Count; i++)
            {
                var r = records[i];
                if (r.Failed) failed++;
                // «=1» — ненулевой код, который у этой команды означает ответ, а не сбой.
                sb.Append(r.Stamp).Append("  ").Append(r.Failed ? "E" + r.ExitCode + " " : r.Ok ? "ok " : "=" + r.ExitCode + " ")
                  .Append(r.DurationMs).Append(" ms  git ").Append(r.Args);
                if (r.Background) sb.Append("  (background)");
                sb.Append('\n');

                if (r.Failed && !string.IsNullOrEmpty(r.Output))
                {
                    int lines = 0;
                    foreach (var line in r.Output.Split('\n'))
                    {
                        if (line.Trim().Length == 0) continue;
                        sb.Append("      ").Append(line.TrimEnd()).Append('\n');
                        if (++lines == 6) break;
                    }
                }

                if (!string.IsNullOrEmpty(r.Timeline) && (r.Failed || r.DurationMs >= GitTimeline.SlowMs))
                {
                    sb.Append("      stages:\n");
                    foreach (var line in r.Timeline.Split('\n'))
                        if (line.Trim().Length > 0) sb.Append("        ").Append(line.TrimEnd()).Append('\n');
                }
            }
            s.Add("Commands in log", records.Count);
            s.Add("Failed among the last " + (records.Count - from), failed);
            s.Text = sb.Length > 0 ? sb.ToString() : null;
            return s;
        }

        // ----------------------------------------------------------- вывод ---

        public static string RenderMarkdown(List<ReportSection> sections, ReportOptions o)
        {
            var sb = new StringBuilder();
            sb.Append("# Git for Unity — problem report\n\n");
            sb.Append("Format ").Append(Format).Append(", created ")
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\n\n");

            sb.Append("## What happened\n\n").Append(Or(o.Description, "(not described)")).Append("\n\n");
            sb.Append("## Steps to reproduce\n\n").Append(Or(o.Steps, "(not described)")).Append("\n\n");

            foreach (var s in sections)
            {
                if (!s.Included) continue;
                sb.Append("## ").Append(s.Title).Append("\n\n");
                foreach (var f in s.Facts)
                    sb.Append("- **").Append(f.Key).Append(":** ").Append(Value(f, o)).Append('\n');
                if (!string.IsNullOrEmpty(s.Text))
                    sb.Append("\n```text\n").Append(s.Text.TrimEnd()).Append("\n```\n");
                sb.Append('\n');
            }

            if (o.Attachments.Count > 0)
            {
                sb.Append("## Attached files\n\n");
                foreach (var a in o.Attachments) sb.Append("- ").Append(Path.GetFileName(a)).Append('\n');
                sb.Append('\n');
            }

            return Privacy(sb.ToString(), o);
        }

        public static string RenderJson(List<ReportSection> sections, ReportOptions o)
        {
            var sb = new StringBuilder("{\n");
            sb.Append("  \"format\": ").Append(Format).Append(",\n");
            sb.Append("  \"created\": ").Append(Json(DateTime.Now.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
            sb.Append("  \"description\": ").Append(Json(o.Description)).Append(",\n");
            sb.Append("  \"steps\": ").Append(Json(o.Steps)).Append(",\n");
            sb.Append("  \"sections\": {");

            bool firstSection = true;
            foreach (var s in sections)
            {
                if (!s.Included) continue;
                sb.Append(firstSection ? "\n" : ",\n");
                firstSection = false;

                sb.Append("    ").Append(Json(s.Id)).Append(": {");
                bool first = true;
                foreach (var f in s.Facts)
                {
                    sb.Append(first ? "\n" : ",\n");
                    first = false;
                    sb.Append("      ").Append(Json(f.Key)).Append(": ").Append(Json(Value(f, o)));
                }
                if (!string.IsNullOrEmpty(s.Text))
                {
                    sb.Append(first ? "\n" : ",\n");
                    sb.Append("      \"text\": ").Append(Json(s.Text));
                }
                sb.Append("\n    }");
            }
            sb.Append("\n  }\n}\n");
            return Privacy(sb.ToString(), o);
        }

        /// <summary>Одна строка для письма или issue: версии, по которым сразу видно, о чём речь.</summary>
        public static string Summary(List<ReportSection> sections)
        {
            string Find(string id, string key)
            {
                foreach (var s in sections)
                    if (s.Id == id)
                        foreach (var f in s.Facts)
                            if (f.Key == key) return f.Value;
                return "?";
            }

            return "Git for Unity " + Find("environment", "Git for Unity") +
                   ", Unity " + Find("environment", "Unity") +
                   ", " + Find("environment", "Operating system") +
                   ", " + Find("git", "git");
        }

        public static void SaveZip(string path, List<ReportSection> sections, ReportOptions o, byte[] screenshot)
        {
            Journal.Flush();

            using (var stream = File.Create(path))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Put(zip, "report.md", RenderMarkdown(sections, o));
                Put(zip, "report.json", RenderJson(sections, o));

                if (o.LogDays > 0)
                {
                    foreach (var log in Journal.Files(o.LogDays))
                    {
                        string text;
                        using (var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8))
                            text = reader.ReadToEnd();
                        Put(zip, "logs/" + Path.GetFileName(log), Privacy(text, o));
                    }
                }

                if (screenshot != null)
                {
                    var entry = zip.CreateEntry("screenshot.png", System.IO.Compression.CompressionLevel.NoCompression);
                    using (var s = entry.Open()) s.Write(screenshot, 0, screenshot.Length);
                }

                long total = 0;
                foreach (var file in o.Attachments)
                {
                    if (!File.Exists(file)) continue;
                    total += new FileInfo(file).Length;
                    if (total > MaxAttachmentBytes)
                        throw new IOException(L.F("Attached files are larger than {0} MB.", MaxAttachmentBytes / (1024 * 1024)));
                    zip.CreateEntryFromFile(file, "attachments/" + Path.GetFileName(file));
                }
            }
        }

        /// <summary>Снимок окна Git как оно есть на экране. null — окна нет или снимок не получился.</summary>
        public static byte[] CaptureGitWindow()
        {
            try
            {
                var windows = Resources.FindObjectsOfTypeAll<GitWindow>();
                if (windows.Length == 0) return null;

                var r = windows[0].position;
                float scale = EditorGUIUtility.pixelsPerPoint;
                int width = Mathf.RoundToInt(r.width * scale);
                int height = Mathf.RoundToInt(r.height * scale);
                if (width <= 0 || height <= 0) return null;

                var pixels = InternalEditorUtility.ReadScreenPixel(new Vector2(r.x * scale, r.y * scale), width, height);
                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                try
                {
                    texture.SetPixels(pixels);
                    texture.Apply();
                    return texture.EncodeToPNG();
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
            catch (Exception e)
            {
                Journal.Warn("Screenshot of the Git window failed: " + e.Message);
                return null;
            }
        }

        // --------------------------------------------------------- служебное ---

        private static string Git(string args, string cwd)
        {
            var r = GitProcess.Run(GitRepository.GitExe, args, cwd, null, 15000, default(CancellationToken), null, true);
            return r.Ok ? r.StdOut.Trim() : null;
        }

        private static string Privacy(string text, ReportOptions o)
        {
            text = Redactor.Clean(text);
            if (o.HidePaths)
            {
                text = Redactor.HidePaths(text, GitRepository.ProjectRoot,
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
                text = Redactor.HideNames(text, _privateNames);
            }
            if (o.HideServers) text = Redactor.HideServers(text);
            return text;
        }

        private static string Value(ReportFact f, ReportOptions o)
        {
            return f.Private && o.HidePaths && f.Value != "—" ? "#" + Redactor.Hash(f.Value) : f.Value;
        }

        private static bool AssemblyLoaded(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == name) return true;
            return false;
        }

        private static string Or(string text, string fallback)
        {
            return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
        }

        private static void Put(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                writer.Write(text);
        }

        private static string Json(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
