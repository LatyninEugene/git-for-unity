using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;

namespace Lev.Git
{
    public enum CheckSeverity
    {
        Warning = 0,
        Error
    }

    public sealed class CommitCheck
    {
        public CheckSeverity Severity;
        public string Title;
        public string Detail;
        public string ProjectPath;
    }

    /// <summary>
    /// Проверки перед коммитом.
    ///
    /// Всё, что здесь ловится, ломается не у того, кто коммитит, а у того, кто
    /// потом делает pull: ассет без меты получит новый GUID и оторвёт все ссылки
    /// на себя, забытый ассет превратит ссылку в None, бинарник мимо LFS раздует
    /// репозиторий навсегда. Сам автор ничего этого не заметит — у него на диске
    /// всё на месте.
    ///
    /// Проверки ничего не запрещают. Они называют последствие и дают решить.
    /// </summary>
    public static class CommitChecks
    {
        private static readonly Regex GuidRef = new Regex(
            @"guid:\s*([0-9a-f]{32})", RegexOptions.Compiled);

        /// <summary>Каталоги, которых в репозитории быть не должно вовсе.</summary>
        private static readonly string[] Generated =
        {
            "Library/", "Temp/", "Logs/", "obj/", "Build/", "Builds/", "UserSettings/"
        };

        /// <summary>Порог, с которого файл вне LFS уже заметно вредит репозиторию.</summary>
        private const long LargeFileBytes = 1024 * 1024;

        public static async Task<List<CommitCheck>> RunAsync(List<GitChange> included)
        {
            var found = new List<CommitCheck>();
            if (included == null || included.Count == 0) return found;

            var includedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in included)
            {
                includedPaths.Add(c.ProjectPath);

                // Мета не имеет отдельной строки в списке: она свёрнута в свой
                // ассет и уезжает вместе с ним — см. GitOperations.PathsOf.
                // Без этой строки проверка считала бы её забытой.
                if (c.HasMeta) includedPaths.Add(c.ProjectPath + ".meta");
            }

            var metasToCheck = new List<string>();

            CheckGenerated(included, found);
            CheckMetaPairs(included, metasToCheck, found);
            CheckDuplicateGuids(included, found);
            CheckReferences(included, includedPaths, found);
            await CheckIgnoredMetasAsync(metasToCheck, found);
            await CheckLargeFilesOutsideLfsAsync(included, found);
            await CheckLfsPointersAsync(included, found);

            found.Sort((a, b) => b.Severity.CompareTo(a.Severity));
            return found;
        }

        // ------------------------------------------------- сгенерированное ---

        private static void CheckGenerated(List<GitChange> included, List<CommitCheck> found)
        {
            foreach (var c in included)
            {
                foreach (var prefix in Generated)
                {
                    if (!c.ProjectPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                    found.Add(new CommitCheck
                    {
                        Severity = CheckSeverity.Error,
                        Title = L.T("Generated file in the commit"),
                        Detail = L.F("Unity recreates the {0} folder on every machine. " +
                                     "In the repository it bloats the history and causes conflicts out of nowhere. " +
                                     "Such paths belong in .gitignore.", prefix.TrimEnd('/')),
                        ProjectPath = c.ProjectPath
                    });
                    break;
                }
            }
        }

        // ------------------------------------------------------- меты ---

        /// <summary>
        /// Путь, который Unity по своим правилам не импортирует: какая-либо его
        /// часть кончается на «~» или начинается с точки, папка cvs, файл .tmp.
        /// Так устроены Samples~ и Documentation~ в пакетах.
        /// </summary>
        internal static bool IgnoredByUnity(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            var parts = path.Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part.Length == 0) continue;

                // Сама мета игнорируемого ассета тоже игнорируется — судим по имени без «.meta».
                if (i == parts.Length - 1 && part.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                    part = part.Substring(0, part.Length - ".meta".Length);

                if (part.EndsWith("~", StringComparison.Ordinal) || part.StartsWith(".", StringComparison.Ordinal) ||
                    part.Equals("cvs", StringComparison.OrdinalIgnoreCase) ||
                    (i == parts.Length - 1 && part.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            return false;
        }

        private static void CheckMetaPairs(List<GitChange> included,
                                           List<string> metasToCheck,
                                           List<CommitCheck> found)
        {
            foreach (var c in included)
            {
                var path = c.ProjectPath;
                bool isMeta = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

                if (!MetaExpected(path)) continue;

                // Samples~, .git и подобное Unity не импортирует и мет для них не
                // создаёт — их отсутствие здесь норма, а не забытый файл.
                if (IgnoredByUnity(path)) continue;

                if (isMeta)
                {
                    var asset = path.Substring(0, path.Length - ".meta".Length);
                    if (Exists(asset)) continue;

                    found.Add(new CommitCheck
                    {
                        Severity = CheckSeverity.Warning,
                        Title = L.T("Meta without an asset"),
                        Detail = L.F("File {0} not found. An orphaned meta usually means " +
                                     "the asset was deleted outside the editor.", asset),
                        ProjectPath = path
                    });
                    continue;
                }

                // Каталоги в git не хранятся сами по себе, их меты проверять незачем.
                if (Directory.Exists(FullPath(path))) continue;

                var meta = path + ".meta";

                // Удалённому ассету мета не нужна: она уходит из репозитория
                // вместе с ним, и её отсутствие на диске — ровно то, что должно
                // быть. Беда здесь обратная — ассет удалили мимо редактора, а
                // мета осталась и уедет в репозиторий сиротой.
                if (c.Status == GitFileStatus.Deleted)
                {
                    if (Exists(meta))
                        found.Add(new CommitCheck
                        {
                            Severity = CheckSeverity.Warning,
                            Title = L.T("Meta of a deleted asset remains"),
                            Detail = L.F("The asset is deleted, but {0} is still on disk. This happens " +
                                         "when the file was deleted outside the editor. Delete the meta too, otherwise " +
                                         "Unity will stumble over it on import for everyone else.", meta),
                            ProjectPath = meta
                        });
                    continue;
                }

                if (!Exists(meta))
                {
                    found.Add(new CommitCheck
                    {
                        Severity = CheckSeverity.Error,
                        Title = L.T("Asset without .meta"),
                        Detail = L.F("File {0} is not on disk. For whoever pulls, Unity " +
                                     "will create the meta again with a DIFFERENT GUID, and all references to this asset " +
                                     "in scenes and prefabs will break.", meta),
                        ProjectPath = path
                    });
                    continue;
                }

                // Проверять «отмечена ли мета» незачем: она свёрнута в строку
                // ассета и уезжает вместе с ним. А вот попасть под .gitignore
                // она может — это и проверяется отдельно, разом для всех.
                metasToCheck.Add(meta);
            }
        }

        /// <summary>
        /// Мета под .gitignore — редкий, но злой случай: ассет уезжает в коммит,
        /// мета остаётся на машине автора, и у всех остальных Unity выдаёт ассету
        /// новый GUID. Правило вида «*.meta» в чужом .gitignore это устраивает.
        /// </summary>
        private static async Task CheckIgnoredMetasAsync(List<string> metas, List<CommitCheck> found)
        {
            if (metas.Count == 0) return;

            var args = new StringBuilder("check-ignore --");
            foreach (var meta in metas)
            {
                var gitPath = GitRepository.ToGitPath(meta);
                if (string.IsNullOrEmpty(gitPath)) continue;
                args.Append(" \"").Append(gitPath.Replace("\"", "\\\"")).Append('"');
                if (args.Length > 24000) break;
            }

            var r = await GitProcess.RunAsync(GitRepository.GitExe, args.ToString(),
                                              GitRepository.RepoRoot, null, 30000);

            // Код 1 означает «ни один путь не игнорируется» — это не ошибка.
            if (r.ExitCode != 0) return;

            foreach (var line in r.StdOut.Split('\n'))
            {
                var gitPath = line.Trim();
                if (gitPath.Length == 0) continue;

                var projectPath = GitRepository.ToProjectPath(gitPath);
                if (string.IsNullOrEmpty(projectPath)) continue;

                found.Add(new CommitCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = L.T(".meta is ignored by .gitignore"),
                    Detail = L.T("The asset goes into the commit, but its meta does not: it is excluded by a rule " +
                                 "in .gitignore. For everyone else Unity will give the asset a new GUID, " +
                                 "and references to it will break."),
                    ProjectPath = projectPath
                });
            }
        }

        // -------------------------------------------------- дубли GUID ---

        private static void CheckDuplicateGuids(List<GitChange> included, List<CommitCheck> found)
        {
            foreach (var c in included)
            {
                if (!c.ProjectPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;

                var guid = ReadGuid(c.ProjectPath);
                if (string.IsNullOrEmpty(guid)) continue;

                var asset = c.ProjectPath.Substring(0, c.ProjectPath.Length - ".meta".Length);
                var known = AssetDatabase.GUIDToAssetPath(guid);

                if (string.IsNullOrEmpty(known)) continue;
                if (string.Equals(known, asset, StringComparison.OrdinalIgnoreCase)) continue;

                found.Add(new CommitCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = L.T("GUID already used by another asset"),
                    Detail = L.F("The same GUID belongs to {0}. This usually comes from copying " +
                                 "files together with their metas: references will point to the wrong asset.", known),
                    ProjectPath = c.ProjectPath
                });
            }
        }

        // ------------------------------------------- оборванные ссылки ---

        /// <summary>
        /// Самая полезная проверка: сцена ссылается на ассет, который в коммит
        /// не попадёт. У автора всё работает, у остальных на его месте None.
        /// </summary>
        private static void CheckReferences(List<GitChange> included,
                                            HashSet<string> includedPaths,
                                            List<CommitCheck> found)
        {
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in included)
            {
                if (!IsUnityYaml(c.ProjectPath)) continue;

                var text = ReadText(c.ProjectPath);
                if (string.IsNullOrEmpty(text)) continue;

                foreach (Match m in GuidRef.Matches(text))
                {
                    var guid = m.Groups[1].Value;
                    if (IsBuiltInGuid(guid)) continue;

                    var target = AssetDatabase.GUIDToAssetPath(guid);

                    if (string.IsNullOrEmpty(target))
                    {
                        if (!reported.Add("missing:" + guid)) continue;

                        found.Add(new CommitCheck
                        {
                            Severity = CheckSeverity.Warning,
                            Title = L.T("Reference to nowhere"),
                            Detail = L.F("{0} references GUID {1}, " +
                                         "which matches no asset in the project. " +
                                         "Most likely the reference broke before the commit.", c.ProjectPath, guid),
                            ProjectPath = c.ProjectPath
                        });
                        continue;
                    }

                    // Ассет есть на диске, но в репозитории его не будет.
                    var status = GitStatusCache.GetStatus(target);
                    bool untracked = status == GitFileStatus.Untracked;
                    if (!untracked || includedPaths.Contains(target)) continue;

                    if (!reported.Add("left:" + target)) continue;

                    found.Add(new CommitCheck
                    {
                        Severity = CheckSeverity.Error,
                        Title = L.T("Reference to an asset that is not in the commit"),
                        Detail = L.F("{0} references {1}, " +
                                     "but that file is untracked and not included. " +
                                     "For everyone but you it will be None.", c.ProjectPath, target),
                        ProjectPath = target
                    });
                }
            }
        }

        // ------------------------------------------------ бинарники и LFS ---

        private static async Task CheckLargeFilesOutsideLfsAsync(List<GitChange> included,
                                                                 List<CommitCheck> found)
        {
            var candidates = new List<GitChange>();

            foreach (var c in included)
            {
                if (c.Status == GitFileStatus.Deleted) continue;

                var full = FullPath(c.ProjectPath);
                if (!File.Exists(full)) continue;

                try
                {
                    if (new FileInfo(full).Length >= LargeFileBytes) candidates.Add(c);
                }
                catch { /* файл могли удалить прямо сейчас */ }
            }

            if (candidates.Count == 0) return;

            var lfs = await LfsTrackedAsync(candidates);

            foreach (var c in candidates)
            {
                if (lfs.Contains(c.ProjectPath)) continue;

                long size = 0;
                try { size = new FileInfo(FullPath(c.ProjectPath)).Length; } catch { }

                found.Add(new CommitCheck
                {
                    Severity = CheckSeverity.Warning,
                    Title = L.T("Large file outside LFS"),
                    Detail = L.F(
                        "{0:0.#} MB, but .gitattributes does not send this file to LFS. " +
                        "Every change goes into the history in full and stays there forever: " +
                        "it can only be removed later by rewriting history.",
                        size / 1024f / 1024f),
                    ProjectPath = c.ProjectPath
                });
            }
        }

        /// <summary>
        /// Указатель LFS в файле, который LFS не отслеживает. Такой файл уйдёт в
        /// git как есть — три строки текста вместо картинки — и у всех после
        /// pull ассет окажется испорчен. Бывает, когда файл скопировали из
        /// клона, где LFS не скачал содержимое.
        /// </summary>
        private static async Task CheckLfsPointersAsync(List<GitChange> included, List<CommitCheck> found)
        {
            var pointers = new List<GitChange>();

            foreach (var c in included)
            {
                if (c.Status == GitFileStatus.Deleted || !c.HasAsset) continue;

                var full = FullPath(c.ProjectPath);
                try
                {
                    if (!File.Exists(full) || new FileInfo(full).Length > 1024) continue;
                }
                catch { continue; }

                if (LfsFiles.LooksLikePointer(ReadText(c.ProjectPath))) pointers.Add(c);
            }

            if (pointers.Count == 0) return;

            var lfs = await LfsTrackedAsync(pointers);
            foreach (var c in pointers)
            {
                if (lfs.Contains(c.ProjectPath)) continue;

                found.Add(new CommitCheck
                {
                    Severity = CheckSeverity.Error,
                    Title = L.T("LFS pointer instead of the file"),
                    Detail = L.T("The file contains the three lines of an LFS pointer, but .gitattributes does not send this path to LFS. " +
                                 "The pointer will go into the repository, and after pull the asset will be broken for everyone. " +
                                 "Use the real file or add its type to LFS."),
                    ProjectPath = c.ProjectPath
                });
            }
        }

        /// <summary>Спрашивает у самого git, какие пути уходят в LFS: он знает правила .gitattributes.</summary>
        private static async Task<HashSet<string>> LfsTrackedAsync(List<GitChange> changes)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var args = new StringBuilder("check-attr filter --");
            foreach (var c in changes)
            {
                var gitPath = GitRepository.ToGitPath(c.ProjectPath);
                if (string.IsNullOrEmpty(gitPath)) continue;
                args.Append(" \"").Append(gitPath.Replace("\"", "\\\"")).Append('"');

                // Командная строка Windows ограничена: длинный список режем.
                if (args.Length > 24000) break;
            }

            var r = await GitProcess.RunAsync(GitRepository.GitExe, args.ToString(),
                                              GitRepository.RepoRoot, null, 30000);
            if (!r.Ok) return result;

            foreach (var line in r.StdOut.Split('\n'))
            {
                // Формат ответа: «путь: filter: lfs»
                int marker = line.LastIndexOf(": filter: ", StringComparison.Ordinal);
                if (marker < 0) continue;

                var value = line.Substring(marker + ": filter: ".Length).Trim();
                if (value != "lfs") continue;

                var gitPath = line.Substring(0, marker).Trim();
                var projectPath = GitRepository.ToProjectPath(gitPath);
                if (!string.IsNullOrEmpty(projectPath)) result.Add(projectPath);
            }

            return result;
        }

        // ----------------------------------------------------- мелочи ---

        /// <summary>
        /// Создаёт ли Unity мету для этого пути. В Assets — для всего, на любой глубине.
        /// В Packages — только внутри папки пакета: manifest.json и packages-lock.json
        /// лежат в корне Packages без мет всегда, и требовать их — ложная тревога.
        /// </summary>
        private static bool MetaExpected(string path)
        {
            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return true;
            if (!path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) return false;

            // «Packages/<пакет>/…» — есть папка пакета и что-то внутри неё.
            return path.IndexOf('/', "Packages/".Length) > 0;
        }

        private static bool IsUnityYaml(string path)
        {
            foreach (var ext in new[] { ".unity", ".prefab", ".asset", ".controller", ".mat" })
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Ассеты самой Unity живут в файлах, которых в проекте нет.</summary>
        private static bool IsBuiltInGuid(string guid)
        {
            return guid == "0000000000000000f000000000000000" ||
                   guid == "0000000000000000e000000000000000" ||
                   guid == "00000000000000000000000000000000";
        }

        private static string FullPath(string projectPath)
        {
            return Path.Combine(GitRepository.ProjectRoot, projectPath);
        }

        private static bool Exists(string projectPath)
        {
            return File.Exists(FullPath(projectPath));
        }

        private static string ReadText(string projectPath)
        {
            try { return File.ReadAllText(FullPath(projectPath), new UTF8Encoding(false)); }
            catch { return null; }
        }

        private static string ReadGuid(string metaProjectPath)
        {
            var text = ReadText(metaProjectPath);
            if (text == null) return null;

            var m = GuidRef.Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
