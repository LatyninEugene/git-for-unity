using System;
using System.Threading;

namespace Lev.Git
{
    /// <summary>
    /// Одна долгая операция git: заголовок для UI, живой прогресс и возможность
    /// отмены.
    ///
    /// <see cref="Progress"/> пишется из потока, читающего stderr процесса, и
    /// читается из OnGUI. Присваивание ссылки атомарно, поэтому блокировка здесь
    /// не нужна: UI может увидеть предыдущую строку прогресса и не увидеть
    /// последнюю — для индикатора это несущественно.
    /// </summary>
    public sealed class GitJob
    {
        public readonly string Title;
        public volatile string Progress;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        internal GitJob(string title)
        {
            Title = title;
            Progress = string.Empty;
        }

        public CancellationToken Token => _cts.Token;
        public bool CancelRequested => _cts.IsCancellationRequested;

        public void Cancel()
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* уже завершилась */ }
        }

        internal void Dispose()
        {
            try { _cts.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Реестр выполняющихся операций. Намеренно без событий: UI опрашивает его
    /// в OnGUI, и это снимает весь вопрос о том, из какого потока прилетело
    /// уведомление.
    /// </summary>
    public static class GitJobs
    {
        private static GitJob _current;

        /// <summary>Текущая долгая операция или null.</summary>
        public static GitJob Current => _current;

        public static bool Busy => _current != null;

        public static GitJob Begin(string title)
        {
            var job = new GitJob(title);
            // Параллельные долгие операции над одним репозиторием всё равно
            // подрались бы за index.lock, поэтому вторая просто вытесняет
            // первую из индикатора — сама она при этом продолжает работать.
            _current = job;
            return job;
        }

        public static void End(GitJob job)
        {
            if (job == null) return;
            if (ReferenceEquals(_current, job)) _current = null;
            job.Dispose();
        }
    }
}
