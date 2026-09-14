using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Проверки перед операцией, которая переписывает рабочую копию:
    /// checkout, cherry-pick, revert, reset.
    ///
    /// Git о Unity ничего не знает и молча подменит файлы под работающим
    /// редактором. Последствия разные по тяжести: несохранённая сцена просто
    /// пропадёт, а смена версии редактора или списка пакетов приведёт к
    /// переимпорту всего проекта — и это стоит сказать ДО, а не после.
    /// </summary>
    public static class SafeSwitch
    {
        public sealed class Report
        {
            /// <summary>Продолжать нельзя ни при каких условиях.</summary>
            public string Blocker;

            /// <summary>О чём предупредить, но выбор оставить за пользователем.</summary>
            public List<string> Warnings = new List<string>();

            public bool Blocked { get { return Blocker != null; } }
        }

        /// <summary>Файлы, смена которых перетряхивает весь проект.</summary>
        private static readonly string[] HeavyFiles =
        {
            "ProjectSettings/ProjectVersion.txt",
            "Packages/manifest.json",
            "Packages/packages-lock.json"
        };

        /// <summary>
        /// Собирает отчёт о том, что мешает переключению на <paramref name="target"/>.
        /// Ничего не меняет и ничего не спрашивает.
        /// </summary>
        public static async Task<Report> InspectAsync(string target)
        {
            var report = new Report();

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                report.Blocker = L.T(
                    "Play mode is running. Replacing files under a running scene " +
                    "will lose unsaved changes and crash the domain — " +
                    "stop Play and try again.");
                return report;
            }

            if (EditorApplication.isCompiling)
            {
                report.Blocker = L.T(
                    "Scripts are compiling. Wait until it finishes: the domain will reload " +
                    "in the middle of the operation and break it.");
                return report;
            }

            if (GitStatusCache.ConflictCount > 0)
            {
                report.Blocker = L.T(
                    "The working tree has unresolved conflicts. Resolve them first: " +
                    "the bar above the tabs of the Git window, the “Resolve…” button.");
                return report;
            }

            int dirty = GitStatusCache.Changes.Count;
            if (dirty > 0)
            {
                report.Warnings.Add(L.F(
                    "Uncommitted changes: {0}. Git will carry them over " +
                    "if they don't get in the way, and will refuse if they do.", dirty));
            }

            if (!string.IsNullOrEmpty(target))
                await AddHeavyFileWarningsAsync(target, report);

            return report;
        }

        /// <summary>
        /// Сверяет файлы, от которых зависит вся сборка проекта.
        ///
        /// diff --name-only между HEAD и целью: спрашивать git дешевле, чем
        /// читать и разбирать оба состояния самим, и точнее — сравнение идёт
        /// по содержимому дерева, а не по датам на диске.
        /// </summary>
        private static async Task AddHeavyFileWarningsAsync(string target, Report report)
        {
            var args = new System.Text.StringBuilder("diff --name-only ");
            args.Append(GitOperations.Q(target)).Append(" -- ");
            foreach (var f in HeavyFiles) args.Append(GitOperations.Q(f)).Append(' ');

            var r = await GitOperations.Git(args.ToString(), 30000);
            if (!r.Ok) return;

            foreach (var line in r.StdOut.Split('\n'))
            {
                var path = line.Trim();
                if (path.Length == 0) continue;

                if (path.EndsWith("ProjectVersion.txt", StringComparison.OrdinalIgnoreCase))
                    report.Warnings.Add(L.T(
                        "The target has a different editor version (ProjectVersion.txt). Unity will offer " +
                        "to upgrade the project, and that is irreversible for this working tree."));

                else if (path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                    report.Warnings.Add(L.T(
                        "The target has a different set of packages (manifest.json). Package Manager " +
                        "will reinstall dependencies and reimport whatever depends on them."));

                else if (path.EndsWith("packages-lock.json", StringComparison.OrdinalIgnoreCase))
                    report.Warnings.Add(L.T(
                        "The target has different package versions (packages-lock.json)."));
            }
        }

        /// <summary>
        /// Показывает отчёт и спрашивает разрешение. Возвращает false, если
        /// продолжать нельзя или пользователь отказался.
        /// </summary>
        public static bool Confirm(Report report, string title, string action)
        {
            if (report.Blocked)
            {
                EditorUtility.DisplayDialog(title, report.Blocker, L.T("Got It"));
                return false;
            }

            if (report.Warnings.Count == 0) return true;

            var text = action + "\n\n" + string.Join("\n\n", report.Warnings.ToArray());
            return EditorUtility.DisplayDialog(title, text, L.T("Continue"), L.T("Cancel"));
        }

        /// <summary>Проверить и спросить одним вызовом — обычный путь для UI.</summary>
        public static async Task<bool> AskAsync(string target, string title, string action)
        {
            var report = await InspectAsync(target);
            return Confirm(report, title, action);
        }
    }
}
