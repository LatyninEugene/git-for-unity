using System;
using System.Collections.Generic;
using Lev.Git.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git
{
    /// <summary>Вкладка, которую интеграция добавляет в окно Git.</summary>
    public interface IGitTab
    {
        string Title { get; }
        VisualElement Element { get; }

        /// <summary>Число на ярлыке вкладки; 0 — без числа.</summary>
        int Badge { get; }

        void Refresh();
    }

    /// <summary>
    /// Интеграция с хостингом: GitLab, а в будущем и другие.
    ///
    /// Ядро пакета — чистый git и о хостингах не знает ничего. Всё, что
    /// зависит от сервера — веб-адреса, страница ключей, API, — приходит сюда
    /// через интерфейс. Интеграция живёт в своей сборке и находится
    /// автоматически: добавить новую — значит написать класс, ядро не меняется.
    ///
    /// Необязательные возможности возвращают null: «не умею» — нормальный
    /// ответ, и ядро тогда просто не показывает соответствующую кнопку.
    /// </summary>
    public interface IGitIntegration
    {
        /// <summary>Постоянный идентификатор: «gitlab».</summary>
        string Id { get; }

        string DisplayName { get; }
        string Description { get; }

        /// <summary>Включена ли в этом проекте. Хранится в настройках самой интеграции.</summary>
        bool Enabled { get; set; }

        /// <summary>Путь страницы в Project Settings: «Project/Git/GitLab».</summary>
        string SettingsPath { get; }

        /// <summary>Похож ли remote на сервер этой интеграции — чтобы предложить её включить.</summary>
        bool Recognizes(GitRemote remote);

        /// <summary>Вкладка в окне Git. null — вкладки нет.</summary>
        IGitTab CreateTab(IGitHost host);

        /// <summary>Страница, где добавляют SSH-ключ.</summary>
        string SshKeysUrl { get; }

        /// <summary>Страница, где создают токен для push по http(s).</summary>
        string TokenPageUrl { get; }

        /// <summary>Адрес репозитория по http(s) — чтобы переключить на него ssh-remote.</summary>
        string HttpCloneUrl { get; }

        string CommitUrl(string sha);
        string BranchUrl(string branch);
        string FileUrl(string gitPath, string gitRef);
    }

    /// <summary>Значок у ветки: «!12» — открытый merge request.</summary>
    public sealed class GitRefBadge
    {
        public string Text;
        public string Tooltip;

        /// <summary>Что делать по щелчку. null — значок только для чтения.</summary>
        public Action Open;
    }

    /// <summary>
    /// Необязательная возможность интеграции: значки у веток в журнале.
    /// Данные приходят с сервера асинхронно — ядро спрашивает значок при
    /// отрисовке и перерисовывается по событию Changed.
    /// </summary>
    public interface IGitBranchBadges
    {
        /// <param name="branch">Имя ветки без remote: «feature/42-jump».</param>
        GitRefBadge BadgeFor(string branch);

        /// <summary>Попросить обновить данные. Интеграция сама решает, не рано ли.</summary>
        void RequestBadges();

        event Action BadgesChanged;
    }

    /// <summary>Реестр интеграций, найденных в загруженных сборках.</summary>
    public static class GitIntegrations
    {
        private static List<IGitIntegration> _all;

        /// <summary>Включили или выключили интеграцию — окну пора пересобрать вкладки.</summary>
        public static event Action Changed;

        /// <summary>Какая-то интеграция обновила значки веток.</summary>
        public static event Action BadgesChanged;

        /// <summary>Значок ветки от первой включённой интеграции, которая его знает.</summary>
        public static GitRefBadge BranchBadge(string branch)
        {
            if (string.IsNullOrEmpty(branch)) return null;
            foreach (var i in Enabled)
            {
                if (!(i is IGitBranchBadges badges)) continue;
                try
                {
                    var b = badges.BadgeFor(branch);
                    if (b != null) return b;
                }
                catch { }
            }
            return null;
        }

        public static void RequestBadges()
        {
            foreach (var i in Enabled)
                if (i is IGitBranchBadges badges)
                    try { badges.RequestBadges(); }
                    catch (Exception e) { Diagnostics.Journal.Warn(L.F("Integration “{0}”: {1}", i.DisplayName, e.Message)); }
        }

        /// <summary>
        /// Пункты «Открыть в …» для меню: по одному на каждую включённую
        /// интеграцию, у которой есть адрес. Возвращает число добавленных.
        /// </summary>
        public static int AddOpenLinks(Action<string, Action> add, Func<IGitIntegration, string> url)
        {
            int count = 0;
            foreach (var i in Enabled)
            {
                string u = null;
                try { u = url(i); }
                catch { }
                if (string.IsNullOrEmpty(u)) continue;

                var link = u;
                add(L.F("Open in {0}", i.DisplayName), () => Application.OpenURL(link));
                count++;
            }
            return count;
        }

        public static IReadOnlyList<IGitIntegration> All
        {
            get
            {
                if (_all == null) Discover();
                return _all;
            }
        }

        public static IEnumerable<IGitIntegration> Enabled
        {
            get
            {
                foreach (var i in All)
                    if (IsEnabled(i)) yield return i;
            }
        }

        public static void NotifyChanged()
        {
            Changed?.Invoke();
        }

        /// <summary>Первый непустой ответ среди включённых интеграций.</summary>
        public static string FirstUrl(Func<IGitIntegration, string> pick)
        {
            foreach (var i in Enabled)
            {
                try
                {
                    var url = pick(i);
                    if (!string.IsNullOrEmpty(url)) return url;
                }
                catch (Exception e)
                {
                    Diagnostics.Journal.Warn(L.F("Integration “{0}”: {1}", i.DisplayName, e.Message));
                }
            }
            return null;
        }

        /// <summary>Выключенная интеграция, которая узнаёт текущий remote, — её стоит предложить.</summary>
        public static IGitIntegration Suggested(GitRemote remote)
        {
            if (remote == null) return null;
            foreach (var i in All)
            {
                if (IsEnabled(i)) continue;
                try { if (i.Recognizes(remote)) return i; }
                catch { }
            }
            return null;
        }

        private static bool IsEnabled(IGitIntegration i)
        {
            try { return i.Enabled; }
            catch { return false; }
        }

        private static void Discover()
        {
            _all = new List<IGitIntegration>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<IGitIntegration>())
            {
                if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) == null) continue;

                try
                {
                    var integration = (IGitIntegration)Activator.CreateInstance(type);
                    if (integration is IGitBranchBadges badges) badges.BadgesChanged += () => BadgesChanged?.Invoke();
                    _all.Add(integration);
                }
                catch (Exception e)
                {
                    Diagnostics.Journal.Warn(L.F("Integration {0} failed to load: {1}", type.Name, e.Message));
                }
            }

            _all.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
