using System;
using System.Collections.Generic;
using Lev.Git.UI;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Текущая ветка на главной панели редактора — видна всегда, какие бы
    /// окна ни были открыты. Щелчок — переключить ветку или открыть окно Git.
    ///
    /// Элемент можно передвинуть или спрятать, как любой другой на панели:
    /// правой кнопкой по панели.
    /// </summary>
    [InitializeOnLoad]
    internal static class BranchToolbar
    {
        private const string ElementPath = "Git/Branch";

        private static string _shownText;
        private static string _loadedHead;
        private static List<string> _branches = new List<string>();
        private static bool _loadingBranches;

        static BranchToolbar()
        {
            GitStatusCache.Updated += OnStatusUpdated;
        }

        [MainToolbarElement(ElementPath, defaultDockPosition = MainToolbarDockPosition.Middle)]
        private static MainToolbarElement Create()
        {
            _shownText = Text();
            var content = new MainToolbarContent(_shownText, GitIcons.Branch, Tooltip());
            return new MainToolbarDropdown(content, ShowMenu);
        }

        /// <summary>Статус обновляется каждые пару секунд — панель перерисовываем, только когда текст изменился.</summary>
        private static void OnStatusUpdated()
        {
            if (_loadedHead != GitStatusCache.HeadOid)
            {
                _loadedHead = GitStatusCache.HeadOid;
                LoadBranches();
            }

            var text = Text();
            if (text == _shownText) return;
            _shownText = text;

            try { MainToolbar.Refresh(ElementPath); }
            catch (Exception e) { Diagnostics.Journal.Warn(L.F("Branch toolbar was not refreshed: {0}", e.Message)); }
        }

        private static string Text()
        {
            if (!GitRepository.IsRepo) return L.T("no git");
            if (GitStatusCache.IsDetached || string.IsNullOrEmpty(GitStatusCache.Branch))
                return "HEAD " + GitHistory.Short(GitStatusCache.HeadOid);

            var text = GitStatusCache.Branch;
            if (GitStatusCache.Ahead > 0) text += "  ↑" + GitStatusCache.Ahead;
            if (GitStatusCache.Behind > 0) text += "  ↓" + GitStatusCache.Behind;
            return text;
        }

        private static string Tooltip()
        {
            if (!GitRepository.IsRepo) return L.T("Project is not in a git repository");

            var tip = GitStatusCache.IsDetached
                ? L.T("HEAD is detached: new commits will not be on any branch")
                : L.F("Current branch: {0}", GitStatusCache.Branch);
            if (!string.IsNullOrEmpty(GitStatusCache.Upstream)) tip += "\nupstream: " + GitStatusCache.Upstream;
            if (GitStatusCache.Ahead > 0) tip += "\n" + L.F("unpushed commits: {0}", GitStatusCache.Ahead);
            if (GitStatusCache.Behind > 0) tip += "\n" + L.F("new commits on the server: {0}", GitStatusCache.Behind);
            return tip + "\n\n" + L.T("Click to switch branch or open the Git window");
        }

        private static async void LoadBranches()
        {
            if (_loadingBranches || !GitRepository.IsRepo) return;
            _loadingBranches = true;
            try { _branches = await GitOperations.LocalBranchesAsync(); }
            catch { }
            finally { _loadingBranches = false; }
        }

        private static void ShowMenu(Rect anchor)
        {
            var menu = new GenericMenu();

            if (GitRepository.IsRepo)
            {
                var current = GitStatusCache.Branch;
                foreach (var b in _branches)
                {
                    var branch = b;
                    // '/' в GenericMenu — подменю, а в именах веток он обычен.
                    menu.AddItem(new GUIContent(L.Tc("toolbar", "Switch to Branch") + "/" + branch.Replace('/', '∕')), branch == current,
                                 () => { if (branch != current) Switch(branch); });
                }
                if (_branches.Count == 0) menu.AddDisabledItem(new GUIContent(L.Tc("toolbar", "Switch to Branch") + "/" + L.T("Branch list is still loading")));
                menu.AddSeparator(string.Empty);
            }

            menu.AddItem(new GUIContent(L.T("Open Git Window")), false, GitWindow.Open);
            menu.DropDown(anchor);

            // Список могли поменять из консоли — к следующему открытию он будет свежим.
            LoadBranches();
        }

        private static async void Switch(string branch)
        {
            try
            {
                if (!await SafeSwitch.AskAsync(branch, L.T("Switch Branch"), L.F("The working tree will switch to branch “{0}”.", branch)))
                    return;

                var r = await GitOperations.CheckoutAsync(branch);
                await GitStatusCache.RefreshAsync();
                if (!r.Ok) EditorUtility.DisplayDialog(L.T("Switch Branch"), r.Message, L.T("Got It"));
            }
            catch (Exception e)
            {
                Diagnostics.Journal.Exception(e, "Branch switch from the main toolbar");
            }
        }
    }
}
