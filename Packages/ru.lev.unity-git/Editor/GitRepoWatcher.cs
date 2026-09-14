using System;
using System.IO;
using UnityEditor;

namespace Lev.Git
{
    /// <summary>
    /// Держит кэш статуса свежим без участия пользователя.
    ///
    /// Два источника событий:
    ///  * импорт ассетов — правки, сделанные внутри редактора;
    ///  * файловые события в .git — правки, сделанные снаружи: коммит из Rider,
    ///    checkout из консоли, pull из другого клиента.
    ///
    /// Наблюдение намеренно не заходит в .git/objects: во время fetch там тысячи
    /// событий, которых хватает, чтобы переполнить буфер FileSystemWatcher.
    /// </summary>
    [InitializeOnLoad]
    public static class GitRepoWatcher
    {
        /// <summary>Сколько ждать после последнего события, прежде чем обновлять.</summary>
        private const double DebounceSeconds = 0.4;

        /// <summary>Нижняя граница между двумя обновлениями по событию.</summary>
        private const double MinIntervalSeconds = 1.5;

        private static FileSystemWatcher _gitDirWatcher;
        private static FileSystemWatcher _refsWatcher;

        // Пишутся из потоков FileSystemWatcher, читаются из главного — только через volatile.
        private static volatile bool _pending;

        // Поменялся .git/config: remote могли добавить или переименовать снаружи.
        private static volatile bool _remotesPending;
        private static double _pendingSince;
        private static double _lastRefresh;
        private static string _watchedRoot;

        public static bool IsWatching => _gitDirWatcher != null;

        /// <summary>Причина последнего отказа от наблюдения — для показа в UI.</summary>
        public static string WatchProblem { get; private set; }

        static GitRepoWatcher()
        {
            EditorApplication.update += OnUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            EditorApplication.quitting += Dispose;

            // На старте домена репозиторий ещё не найден — отложим до первого апдейта.
            EditorApplication.delayCall += TryAttach;
        }

        /// <summary>Пометить, что статус устарел. Обновление произойдёт с задержкой.</summary>
        public static void MarkDirty()
        {
            _pendingSince = EditorApplication.timeSinceStartup;
            _pending = true;
        }

        private static void TryAttach()
        {
            if (!GitRepository.IsRepo && !GitRepository.Locate())
            {
                WatchProblem = L.Tc("watcher", "The project is not inside a git repository.");
                return;
            }
            Attach(GitRepository.RepoRoot);
        }

        private static void Attach(string repoRoot)
        {
            if (_watchedRoot == repoRoot && _gitDirWatcher != null) return;

            Dispose();
            WatchProblem = null;

            var gitDir = Path.Combine(repoRoot, ".git");

            // В worktree и подмодулях .git — файл со ссылкой на настоящий каталог.
            // Разбирать его ради наблюдения не будем: деградируем до обновления
            // по импорту ассетов и по кнопке.
            if (!Directory.Exists(gitDir))
            {
                WatchProblem = File.Exists(gitDir)
                    ? L.T(".git is a file (worktree or submodule): watching for external changes is off.")
                    : L.T(".git directory not found.");
                return;
            }

            try
            {
                // index, HEAD, ORIG_HEAD, MERGE_HEAD, packed-refs — всё в корне .git.
                _gitDirWatcher = MakeWatcher(gitDir, false);
                // Обновления веток лежат отдельными файлами в refs/.
                var refs = Path.Combine(gitDir, "refs");
                if (Directory.Exists(refs)) _refsWatcher = MakeWatcher(refs, true);

                _watchedRoot = repoRoot;
            }
            catch (Exception e)
            {
                Dispose();
                WatchProblem = L.F("Failed to start watching .git: {0}", e.Message);
            }
        }

        private static FileSystemWatcher MakeWatcher(string path, bool recursive)
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };

            w.Changed += OnFileEvent;
            w.Created += OnFileEvent;
            w.Deleted += OnFileEvent;
            w.Renamed += OnFileEvent;
            // Переполнение буфера — не повод молчать: просто считаем, что всё устарело.
            w.Error += (s, e) => MarkDirty();
            w.EnableRaisingEvents = true;
            return w;
        }

        private static void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            // Никакого API редактора: обработчик приходит из потока пула.
            var name = e.Name;
            if (!string.IsNullOrEmpty(name))
            {
                // .lock-файлы git создаёт и удаляет вокруг каждой своей операции —
                // само по себе это ничего не означает.
                if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return;
                if (string.Equals(name, "config", StringComparison.OrdinalIgnoreCase)) _remotesPending = true;
            }
            MarkDirty();
        }

        private static void OnUpdate()
        {
            if (_gitDirWatcher == null && GitRepository.IsRepo) Attach(GitRepository.RepoRoot);
            if (!_pending) return;

            var now = EditorApplication.timeSinceStartup;
            if (now - _pendingSince < DebounceSeconds) return;
            if (now - _lastRefresh < MinIntervalSeconds) return;

            // Пока git работает, индекс шевелится по нашей же вине — ждём тишины.
            if (GitCommandLog.Running > 0) return;

            _pending = false;
            _lastRefresh = now;

            // `git remote add` или `rename` из консоли: без этого окно Git и настройки
            // показывали бы прежние remote до ручного обновления.
            if (_remotesPending)
            {
                _remotesPending = false;
                var before = RemotesSignature();
                GitRepository.LoadRemotes();
                if (RemotesSignature() != before)
                {
                    GitIntegrations.NotifyChanged();
                    SettingsService.RepaintAllSettingsWindow();
                }
            }

            var _ = GitStatusCache.RefreshAsync();
        }

        private static string RemotesSignature()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var r in GitRepository.Remotes)
                sb.Append(r.Name).Append('\n').Append(r.FetchUrl).Append('\n').Append(r.PushUrl).Append('\n');
            return sb.ToString();
        }

        private static void Dispose()
        {
            DisposeOne(ref _gitDirWatcher);
            DisposeOne(ref _refsWatcher);
            _watchedRoot = null;
        }

        private static void DisposeOne(ref FileSystemWatcher w)
        {
            if (w == null) return;
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch { /* редактор закрывается — уже неважно */ }
            w = null;
        }
    }

    /// <summary>Правки, сделанные внутри редактора, доходят до статуса отсюда.</summary>
    internal sealed class GitAssetHook : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Length + deleted.Length + moved.Length > 0)
                GitRepoWatcher.MarkDirty();
        }
    }
}
