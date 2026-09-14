using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.Preview
{
    /// <summary>
    /// Место, которое занимает система превью: временный импорт в Assets/GitPreview,
    /// миниатюры в Library, временные версии в Temp и резервные копии слияний.
    ///
    /// Всё это восстановимо, кроме резервных копий, — поэтому остальное чистится
    /// само, когда вместе превышает лимит из настроек, а копии только по кнопке.
    /// </summary>
    internal static class PreviewStorage
    {
        public sealed class Area
        {
            public string Title;
            public string Path;
            public string Hint;
            public long Bytes;
            public int Files;
        }

        private static double _lastMeasure = -100;
        private static Area[] _areas;
        private static string _gitWarning;
        private static bool _checkingGit;

        public static string TempVersions
        {
            get { return System.IO.Path.Combine(GitRepository.ProjectRoot, "Temp/LevGit/rev"); }
        }

        /// <summary>Замеры не чаще раза в две секунды: страница настроек перерисовывается постоянно.</summary>
        public static Area[] Measure(bool force = false)
        {
            if (!force && _areas != null && EditorApplication.timeSinceStartup - _lastMeasure < 2.0) return _areas;
            _lastMeasure = EditorApplication.timeSinceStartup;

            _areas = new[]
            {
                Make(L.T("Import Sandbox"), System.IO.Path.Combine(GitRepository.ProjectRoot, ImportSandbox.Root),
                     L.T("Previous versions of models, PSD, EXR. Previews that are open right now are not deleted.")),
                Make(L.T("Version Thumbnails"), PreviewThumbnails.DiskFolder,
                     L.T("Images for the “Asset History” timeline and file lists. Redrawn as needed.")),
                Make(L.T("Temporary Versions"), TempVersions,
                     L.T("Version files while loading. Usually empty — deleted right away.")),
                Make(L.T("Merge Backups"), GitConflicts.BackupRoot,
                     L.T("Original file versions from before conflicts were resolved. Deleted only manually."))
            };

            return _areas;
        }

        private static Area Make(string title, string path, string hint)
        {
            var area = new Area { Title = title, Path = path, Hint = hint };
            try
            {
                if (Directory.Exists(path))
                    foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        area.Bytes += file.Length;
                        area.Files++;
                    }
            }
            catch { }
            return area;
        }

        /// <summary>Сколько автоматически очищаемого места занято: импорт, миниатюры, временные версии.</summary>
        public static long ManagedBytes
        {
            get
            {
                var areas = Measure(true);
                return areas[0].Bytes + areas[1].Bytes + areas[2].Bytes;
            }
        }

        public static long LimitBytes
        {
            get { return Math.Max(64, GitSettings.instance.previewStorageLimitMb) * 1024L * 1024L; }
        }

        // ---------------------------------------------------------- очистка ---

        public static string Clear(int index)
        {
            string result;
            switch (index)
            {
                case 0:
                {
                    int kept;
                    int deleted = ImportSandbox.ClearUnused(out kept);
                    result = L.F("Import sandbox: versions deleted: {0}", deleted) + (kept > 0 ? L.F(", open in preview and kept: {0}", kept) : string.Empty);
                    break;
                }
                case 1:
                    result = L.F("Thumbnails: deleted {0}", PreviewThumbnails.ClearDisk());
                    break;
                case 2:
                    result = L.F("Temporary versions: deleted {0}", DeleteContents(TempVersions));
                    break;
                default:
                    result = L.F("Backups: deleted {0}", DeleteContents(GitConflicts.BackupRoot));
                    break;
            }

            Measure(true);
            return result;
        }

        private static int DeleteContents(string folder)
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(folder)) return 0;
                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); n++; } catch { }
                }
                foreach (var dir in Directory.GetDirectories(folder))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            catch { }
            return n;
        }

        /// <summary>
        /// Лимит превышен — сначала неиспользуемый импорт (он самый тяжёлый),
        /// затем старейшая половина миниатюр. Открытые превью не трогаются.
        /// </summary>
        public static void EnforceLimit()
        {
            try
            {
                if (ManagedBytes <= LimitBytes) return;

                int kept;
                ImportSandbox.ClearUnused(out kept);
                DeleteContents(TempVersions);
                if (ManagedBytes <= LimitBytes) return;

                PreviewThumbnails.TrimOldest(0.5f);
                Measure(true);
            }
            catch (Exception e)
            {
                Diagnostics.Journal.Warn(L.F("Preview storage was not freed: {0}", e.Message));
            }
        }

        [InitializeOnLoadMethod]
        private static void CheckOnLoad()
        {
            EditorApplication.delayCall += EnforceLimit;
        }

        // ------------------------------------------------------------- git ---

        /// <summary>
        /// Не видит ли git папку временного импорта. null — всё в порядке;
        /// текст — что именно не так. Проверка в фоне, результат при следующей отрисовке.
        /// </summary>
        public static string GitWarning
        {
            get
            {
                if (!_checkingGit) CheckGitAsync();
                return _gitWarning;
            }
        }

        private static async void CheckGitAsync()
        {
            if (!GitRepository.IsRepo) return;
            _checkingGit = true;

            try
            {
                var gitPath = GitRepository.ToGitPath(ImportSandbox.Root);
                var tracked = await GitOperations.Git("ls-files -- " + GitOperations.Q(gitPath) + " " + GitOperations.Q(gitPath + ".meta"));
                var visible = await GitOperations.Git("status --porcelain --untracked-files=all -- " + GitOperations.Q(gitPath) + " " + GitOperations.Q(gitPath + ".meta"));

                if (tracked.Ok && tracked.StdOut.Trim().Length > 0)
                    _gitWarning = L.F("The folder {0} is already added to git — remove it from the index: git rm -r --cached {1}", ImportSandbox.Root, gitPath);
                else if (visible.Ok && visible.StdOut.Trim().Length > 0)
                    _gitWarning = L.F("git sees the folder {0} — the exclusion in .git/info/exclude did not work, add it to .gitignore.", ImportSandbox.Root);
                else
                    _gitWarning = null;
            }
            catch { }
            finally
            {
                await Task.Delay(5000);
                _checkingGit = false;
            }
        }

        public static string Human(long bytes)
        {
            return bytes <= 0 ? L.Tc("storage", "empty") : PreviewText.Bytes(bytes);
        }
    }
}
