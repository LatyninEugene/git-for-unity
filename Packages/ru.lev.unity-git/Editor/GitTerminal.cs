using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lev.Git
{
    /// <summary>
    /// Строка своих команд git на вкладке «Консоль».
    ///
    /// Команда запускается в корне репозитория тем же способом, что и команды
    /// самого пакета, и попадает в тот же журнал: что человек ввёл руками, видно
    /// рядом с тем, что делал пакет.
    ///
    /// Окна для ввода у процесса нет. Поэтому git не должен ничего спрашивать:
    /// редактор сообщений и пейджер подменены, а команды, которые без клавиатуры
    /// не работают или стирают данные, сначала показывают предупреждение.
    /// </summary>
    internal static class GitTerminal
    {
        /// <summary>Подкоманды, которые меняют файлы рабочей копии: на время их работы Unity не должна начинать импорт.</summary>
        private static readonly HashSet<string> TouchesFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "checkout", "switch", "restore", "reset", "pull", "merge", "rebase", "cherry-pick", "revert",
            "stash", "clean", "am", "apply", "rm", "mv", "lfs", "submodule", "worktree", "sparse-checkout"
        };

        private static readonly Dictionary<string, string> Environment = new Dictionary<string, string>
        {
            // Редактор, который сразу соглашается с текстом по умолчанию: иначе git
            // ждал бы сохранения файла в окне, которого нет.
            { "GIT_EDITOR", "true" },
            { "GIT_SEQUENCE_EDITOR", "true" },
            { "GIT_MERGE_AUTOEDIT", "no" },
            // Вывод целиком, без постраничного просмотра.
            { "GIT_PAGER", "cat" },
            { "PAGER", "cat" }
        };

        /// <summary>Введённая строка без «git» в начале и лишних пробелов.</summary>
        public static string Normalize(string input)
        {
            var s = (input ?? string.Empty).Trim();
            if (string.Equals(s, "git", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (s.StartsWith("git ", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4).TrimStart();
            return s;
        }

        /// <summary>Что спросить перед запуском. null — запускать без вопросов.</summary>
        public static string Warning(string args)
        {
            string rest;
            var verb = GitExitCodes.Verb(args, out rest);

            bool interactive =
                (verb == "add" && (Has(rest, "-p") || Has(rest, "--patch") || Has(rest, "-i") || Has(rest, "--interactive"))) ||
                (verb == "rebase" && (Has(rest, "-i") || Has(rest, "--interactive"))) ||
                verb == "mergetool" || verb == "difftool" || verb == "gui" || verb == "citool";
            if (interactive)
                return L.T("This command expects keyboard input, which this line cannot provide. " +
                           "Git will continue without it or stop. Run it anyway?");

            bool destructive =
                (verb == "push" && (Has(rest, "--force") || Has(rest, "--force-with-lease") || ShortFlag(rest, 'f') || rest.IndexOf(" +", StringComparison.Ordinal) >= 0)) ||
                (verb == "reset" && Has(rest, "--hard")) ||
                (verb == "clean" && (Has(rest, "--force") || ShortFlag(rest, 'f'))) ||
                (verb == "branch" && ShortFlag(rest, 'D')) ||
                (verb == "checkout" && (Has(rest, "--") || Has(rest, ".") || Has(rest, "--force") || ShortFlag(rest, 'f'))) ||
                (verb == "restore" && !Has(rest, "--staged")) ||
                (verb == "stash" && (Has(rest, "drop") || Has(rest, "clear"))) ||
                verb == "filter-branch" || verb == "filter-repo" ||
                (verb == "reflog" && Has(rest, "expire")) ||
                (verb == "gc" && Has(rest, "--prune=now"));
            if (destructive)
                return L.T("This command can irreversibly remove commits or uncommitted changes. Run it?");

            return null;
        }

        /// <summary>Запускает команду и обновляет всё, что она могла изменить.</summary>
        public static async Task<ProcessResult> RunAsync(string args)
        {
            string rest;
            var verb = GitExitCodes.Verb(args, out rest);

            // `git credential fill` печатает пароль — его вывод в журнал не попадает.
            bool secret = verb == "credential";

            Func<Task<ProcessResult>> run = async () =>
            {
                var job = GitJobs.Begin("git " + args);
                try
                {
                    return await GitProcess.RunAsync(
                        GitRepository.GitExe, args, GitRepository.RepoRoot,
                        null, 600000, job.Token, line => job.Progress = line,
                        false, secret, Environment, true);
                }
                finally
                {
                    GitJobs.End(job);
                }
            };

            var result = TouchesFiles.Contains(verb)
                ? await GitOperations.WithAssetGuard(run)
                : await run();

            // Команда могла сменить ветку, remote, локи — перечитываем.
            GitRepository.LoadRemotes();
            await GitStatusCache.RefreshAsync();
            LfsLockCache.RequestRefresh();
            return result;
        }

        private static bool Has(string args, string token)
        {
            foreach (var part in args.Split(' '))
                if (part == token || part.StartsWith(token + "=", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Короткий флаг, в том числе в связке: «-fd» содержит «f».</summary>
        private static bool ShortFlag(string args, char flag)
        {
            foreach (var part in args.Split(' '))
                if (part.Length > 1 && part[0] == '-' && part[1] != '-' && part.IndexOf(flag, 1) > 0) return true;
            return false;
        }
    }
}
