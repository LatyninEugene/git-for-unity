using System.Collections.Generic;
using UnityEditor;

namespace Lev.Git
{
    /// <summary>
    /// Команды плагина в контекстном меню окна Project.
    ///
    /// Там их и ищут: вопрос «кто и когда трогал этот префаб» возникает, когда
    /// смотришь на сам префаб, а не когда открыто окно GitLab. С локами так же:
    /// лок берут на ассет, который собираются править, прямо на нём.
    /// </summary>
    internal static class ProjectContextMenu
    {
        private const string HistoryItem = "Assets/Git/File History";
        private const string LockItem = "Assets/Git/Lock";
        private const string UnlockItem = "Assets/Git/Unlock";
        private const string ForceUnlockItem = "Assets/Git/Unlock Someone Else's Lock…";
        private const string PullItem = "Assets/Git/Download from LFS";

        [MenuItem(HistoryItem, false, 1100)]
        private static void ShowHistory()
        {
            var path = SelectedPath();
            if (path != null) GitWindow.ShowFileHistory(path);
        }

        [MenuItem(HistoryItem, true)]
        private static bool ShowHistoryValidate()
        {
            return GitRepository.IsRepo && SelectedPath() != null;
        }

        private const string AssetHistoryItem = "Assets/Git/Asset History";

        [MenuItem(AssetHistoryItem, false, 1101)]
        private static void ShowAssetHistory()
        {
            var path = SelectedPath();
            if (path != null) UI.AssetHistoryWindow.Open(path, false);
        }

        [MenuItem(AssetHistoryItem, true)]
        private static bool ShowAssetHistoryValidate()
        {
            var path = SelectedPath();
            return GitRepository.IsRepo && path != null && !AssetDatabase.IsValidFolder(path);
        }

        // ------------------------------------------------------------ локи ---

        [MenuItem(LockItem, false, 1120)]
        private static void Lock()
        {
            var paths = SelectedFiles();
            Run(L.Tc("noun", "Lock"), () => LfsLockOps.LockAsync(paths));
        }

        [MenuItem(LockItem, true)]
        private static bool LockValidate()
        {
            if (!GitRepository.IsRepo) return false;
            foreach (var p in SelectedFiles()) if (LfsLockCache.LockOf(p) == null) return true;
            return false;
        }

        [MenuItem(UnlockItem, false, 1121)]
        private static void Unlock()
        {
            var paths = new List<string>();
            foreach (var p in SelectedFiles())
            {
                var l = LfsLockCache.LockOf(p);
                if (l != null && !LfsLockCache.IsTheirs(l)) paths.Add(p);
            }
            Run(L.T("Unlocking"), () => LfsLockOps.UnlockAsync(paths));
        }

        [MenuItem(UnlockItem, true)]
        private static bool UnlockValidate()
        {
            foreach (var p in SelectedFiles())
            {
                var l = LfsLockCache.LockOf(p);
                if (l != null && !LfsLockCache.IsTheirs(l)) return true;
            }
            return false;
        }

        [MenuItem(ForceUnlockItem, false, 1122)]
        private static void ForceUnlock()
        {
            var paths = new List<string>();
            foreach (var p in SelectedFiles()) if (LfsLockCache.IsTheirs(LfsLockCache.LockOf(p))) paths.Add(p);
            Run(L.T("Unlocking someone else's lock"), () => LfsLockOps.UnlockAsync(paths));
        }

        [MenuItem(ForceUnlockItem, true)]
        private static bool ForceUnlockValidate()
        {
            foreach (var p in SelectedFiles()) if (LfsLockCache.IsTheirs(LfsLockCache.LockOf(p))) return true;
            return false;
        }

        [MenuItem(PullItem, false, 1130)]
        private static void Pull()
        {
            var gitPaths = new List<string>();
            foreach (var p in SelectedFiles()) if (LfsLockCache.IsNotDownloaded(p)) gitPaths.Add(GitRepository.ToGitPath(p));
            Run(L.T("Downloading from LFS"), () => LfsLockOps.PullContentAsync(gitPaths));
        }

        [MenuItem(PullItem, true)]
        private static bool PullValidate()
        {
            foreach (var p in SelectedFiles()) if (LfsLockCache.IsNotDownloaded(p)) return true;
            return false;
        }

        /// <summary>Операция из меню: итог — в консоль, окна плагина может и не быть.</summary>
        internal static async void Run(string title, System.Func<System.Threading.Tasks.Task<ProcessResult>> op)
        {
            try
            {
                var r = await op();
                if (!r.Ok && !string.IsNullOrEmpty(r.Message)) Diagnostics.Journal.Warn(title + ": " + r.Message);
                else if (r.Ok && !string.IsNullOrEmpty(r.StdOut)) Diagnostics.Journal.Notice(r.StdOut.Trim());
            }
            catch (System.Exception e)
            {
                Diagnostics.Journal.Exception(e, "Project context menu operation");
            }
        }

        // ---------------------------------------------------------- выделение ---

        /// <summary>
        /// Путь выделенного ассета относительно проекта.
        ///
        /// Через Selection.assetGUIDs, а не activeObject: у папки нет объекта в
        /// привычном смысле, а историю папки смотреть так же осмысленно, как и
        /// историю файла.
        /// </summary>
        private static string SelectedPath()
        {
            var guids = Selection.assetGUIDs;
            if (guids == null || guids.Length != 1) return null;

            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>Все выделенные файлы. Папки пропускаются: лока на папку не бывает.</summary>
        private static List<string> SelectedFiles()
        {
            var list = new List<string>();
            var guids = Selection.assetGUIDs;
            if (guids == null) return list;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;
                list.Add(path);
            }
            return list;
        }
    }
}
