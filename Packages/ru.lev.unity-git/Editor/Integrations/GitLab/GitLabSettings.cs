using System;
using UnityEditor;

namespace Lev.Git.GitLab
{
    /// <summary>
    /// Настройки интеграции с GitLab. Лежат в ProjectSettings/ и коммитятся:
    /// включена ли интеграция, где инстанс, какой проект. Токена здесь нет и
    /// быть не может — он в системном хранилище машины, см. <see cref="GitLabToken"/>.
    ///
    /// Пустая строка у адреса и пути — «вывести из git remote».
    /// </summary>
    [FilePath(GitLabSettings.AssetPath, FilePathAttribute.Location.ProjectFolder)]
    public sealed class GitLabSettings : ScriptableSingleton<GitLabSettings>
    {
        public const string AssetPath = "ProjectSettings/LevGitGitLabSettings.asset";

        public bool enabled;

        /// <summary>Веб-адрес инстанса, например http://gitlab.lev.ru</summary>
        public string instanceUrlOverride = string.Empty;

        /// <summary>namespace/repo, например Latynin/TestGitLab</summary>
        public string projectPathOverride = string.Empty;

        /// <summary>Не проверять TLS-сертификат — для self-hosted с самоподписанным.</summary>
        public bool allowInsecureCertificate;

        // ---------------------------------------------------- рабочий процесс ---
        // Команды работают по-разному, поэтому всё это — настройка проекта,
        // а не решение пакета. Значения — номера перечислений из GitLabWorkflow.

        /// <summary><see cref="MergePolicy"/>.</summary>
        public int mergePolicy;
        public bool allowMergeWhenPipelineSucceeds = true;
        public bool requireResolvedDiscussions;

        public string branchTemplate = "{iid}-{title}";
        public bool branchTransliterate = true;
        public int branchTitleMaxLength = 40;
        public string branchSeparator = "-";
        public bool closesIssueInDescription = true;

        /// <summary><see cref="AfterMergeAction"/>.</summary>
        public int afterMergeCheckoutTarget;
        public int afterMergeDeleteLocalBranch;

        /// <summary><see cref="RemoteBranchDeletion"/>.</summary>
        public int afterMergeDeleteRemoteBranch;
        public int afterMergeUnlock;

        /// <summary><see cref="TargetBranchMode"/>.</summary>
        public int targetBranchMode;
        public string fixedTargetBranch = "develop";
        public bool createAsDraft;

        /// <summary><see cref="SquashMode"/>.</summary>
        public int squashMode;

        /// <summary><see cref="DescriptionMode"/>.</summary>
        public int descriptionMode;

        /// <summary><see cref="MrListFilter"/>.</summary>
        public int defaultListFilter;

        // ---------------------------------------------------------- задачи ---

        /// <summary><see cref="IssueBranchBase"/>.</summary>
        public int issueBranchBase;

        /// <summary>Сразу отправлять новую ветку задачи на сервер — чтобы коллеги видели, что задача начата.</summary>
        public bool issueBranchPush;

        /// <summary>Назначать задачу на себя, когда из неё создаётся ветка.</summary>
        public bool issueAssignSelf = true;

        /// <summary><see cref="IssueListFilter"/>.</summary>
        public int defaultIssueFilter;

        /// <summary>Снимок в новой задаче по умолчанию: 0 — окно Game, 1 — окно Scene, 2 — без снимка.</summary>
        public int issueScreenshot;

        public void Persist()
        {
            Save(true);
        }
    }

    /// <summary>Куда обращаться: адрес инстанса и путь проекта — заданные вручную или выведенные из remote.</summary>
    public static class GitLabInstance
    {
        public static bool BaseUrlOverridden => !string.IsNullOrWhiteSpace(GitLabSettings.instance.instanceUrlOverride);
        public static bool ProjectPathOverridden => !string.IsNullOrWhiteSpace(GitLabSettings.instance.projectPathOverride);

        /// <summary>
        /// Remote, из которого выводятся адрес и путь проекта. Не обязательно основной:
        /// основным может быть GitHub, а GitLab — вторым remote.
        ///
        /// Адрес инстанса задан вручную — берётся remote на том же сервере, иначе путь
        /// проекта пришёл бы с чужого хостинга. Не задан — remote, похожий на GitLab,
        /// а если такого нет, основной. null — подходящего remote нет.
        /// </summary>
        public static GitRemote SourceRemote
        {
            get
            {
                string name;
                return Source(out name);
            }
        }

        /// <summary>Имя remote из <see cref="SourceRemote"/> — для подписи «из gitlab».</summary>
        public static string SourceRemoteName
        {
            get
            {
                string name;
                Source(out name);
                return name;
            }
        }

        public static string EffectiveBaseUrl
        {
            get
            {
                if (BaseUrlOverridden) return GitLabSettings.instance.instanceUrlOverride.Trim().TrimEnd('/');
                var remote = SourceRemote;
                return remote != null ? remote.GuessedBaseUrl : null;
            }
        }

        public static string EffectiveProjectPath
        {
            get
            {
                if (ProjectPathOverridden) return GitLabSettings.instance.projectPathOverride.Trim().Trim('/');
                var remote = SourceRemote;
                return remote != null ? remote.FullPath : null;
            }
        }

        private static GitRemote Source(out string name)
        {
            name = null;
            var primary = GitRepository.SelectedRemoteName;

            // Разобранные адреса всех remote, основной — первым.
            var names = new System.Collections.Generic.List<string>();
            var remotes = new System.Collections.Generic.List<GitRemote>();
            foreach (var entry in GitRepository.Remotes)
            {
                var parsed = GitRepository.ParseRemote(entry.PushUrl ?? entry.FetchUrl);
                if (parsed == null) continue;
                int at = entry.Name == primary ? 0 : remotes.Count;
                names.Insert(at, entry.Name);
                remotes.Insert(at, parsed);
            }
            if (remotes.Count == 0) return null;

            if (BaseUrlOverridden)
            {
                Uri instance;
                if (!Uri.TryCreate(GitLabSettings.instance.instanceUrlOverride.Trim(), UriKind.Absolute, out instance)) return null;

                for (int i = 0; i < remotes.Count; i++)
                    if (string.Equals(remotes[i].Host, instance.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        name = names[i];
                        return remotes[i];
                    }
                return null;
            }

            for (int i = 0; i < remotes.Count; i++)
                if (remotes[i].Host != null && remotes[i].Host.IndexOf("gitlab", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    name = names[i];
                    return remotes[i];
                }

            name = names[0];
            return remotes[0];
        }

        public static string EffectiveProjectWebUrl
        {
            get
            {
                var b = EffectiveBaseUrl;
                var p = EffectiveProjectPath;
                return string.IsNullOrEmpty(b) || string.IsNullOrEmpty(p) ? null : b + "/" + p;
            }
        }

        /// <summary>Идентификатор проекта для REST API: namespace/repo в URL-кодировке.</summary>
        public static string EffectiveEncodedId
        {
            get
            {
                var p = EffectiveProjectPath;
                return string.IsNullOrEmpty(p) ? null : Uri.EscapeDataString(p);
            }
        }

        public static bool IsHttps => (EffectiveBaseUrl ?? string.Empty).StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
