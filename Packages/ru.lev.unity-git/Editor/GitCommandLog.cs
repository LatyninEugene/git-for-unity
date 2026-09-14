using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>Одна выполненная команда git.</summary>
    [Serializable]
    public sealed class GitCommandRecord
    {
        /// <summary>Устойчивый идентификатор: индекс в журнале сдвигается по мере вытеснения.</summary>
        public long Id;
        public string Stamp;        // HH:mm:ss
        public string Args;         // аргументы без имени исполняемого файла
        public string Cwd;
        public int ExitCode;
        public long DurationMs;
        public string Output;       // stderr, если он есть, иначе stdout; обрезано
        public bool Background;     // фоновый опрос — по умолчанию скрыт в консоли
        public string Timeline;     // этапы сетевой команды по trace2; null — не записывались

        public bool Ok => ExitCode == 0;

        /// <summary>Настоящий сбой. У части команд ненулевой код — это ответ: «настройки нет», «файлы различаются».</summary>
        public bool Failed => GitExitCodes.IsFailure(Args, ExitCode);
    }

    /// <summary>
    /// Журнал фактически выполненных команд. Нужен ровно для того, чтобы
    /// пользователь мог проверить, что пакет делает с его репозиторием, —
    /// без этого любой git-клиент внутри чужого инструмента приходится
    /// принимать на веру.
    ///
    /// Живёт в <see cref="SessionState"/>: переживает перезагрузку домена
    /// (то есть любую правку скрипта) и не переживает перезапуск редактора,
    /// что для журнала команд ровно то, что нужно.
    /// </summary>
    [InitializeOnLoad]
    public static class GitCommandLog
    {
        private const int Capacity = 250;
        private const int MaxOutputChars = 4000;
        private const string StateKey = "LevGit.CommandLog";

        [Serializable]
        private sealed class Payload
        {
            public List<GitCommandRecord> items = new List<GitCommandRecord>();
        }

        private static readonly List<GitCommandRecord> _records;
        private static readonly object _lock = new object();
        private static bool _dirty;
        private static long _nextId;

        // Команды завершаются в потоке пула, а событие для UI обязано подняться
        // из главного. Флаг ставит любой поток, снимает — насос на EditorApplication.update.
        private static volatile bool _changedPending;

        /// <summary>Счётчик выполняющихся прямо сейчас команд. Нужен наблюдателю за .git.</summary>
        public static int Running;

        public static event Action Changed;

        static GitCommandLog()
        {
            _records = Load();
            foreach (var r in _records) if (r.Id >= _nextId) _nextId = r.Id + 1;

            // Флашим не на каждую команду, а один раз перед перезагрузкой домена:
            // сериализовать четверть тысячи записей на каждый `git status` — дорого.
            AssemblyReloadEvents.beforeAssemblyReload += Flush;
            EditorApplication.quitting += Flush;
            EditorApplication.update += Pump;
        }

        /// <summary>Поднимает событие в главный поток. Статический конструктор
        /// выполняется в нём, поэтому подписка здесь безопасна.</summary>
        private static void Pump()
        {
            if (!_changedPending) return;
            _changedPending = false;

            var h = Changed;
            if (h != null) h();
        }

        public static IReadOnlyList<GitCommandRecord> Records
        {
            get { lock (_lock) return _records.ToArray(); }
        }

        public static void Add(GitCommandRecord record)
        {
            if (record == null) return;

            // Прогресс git — сотни строк, за которыми не видно причины ошибки. Она
            // в конце, поэтому длинный вывод укорачивается с середины, а не с конца.
            record.Output = GitOutput.Shorten(GitOutput.WithoutProgress(record.Output), MaxOutputChars, L.T("… truncated …"));

            lock (_lock)
            {
                record.Id = _nextId++;
                _records.Add(record);
                if (_records.Count > Capacity) _records.RemoveRange(0, _records.Count - Capacity);
                _dirty = true;
            }

            // Никакого API редактора отсюда: Add вызывается из потока пула.
            _changedPending = true;

            Diagnostics.Journal.Git(record);
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _records.Clear();
                _dirty = true;
            }
            SessionState.EraseString(StateKey);

            var h = Changed;
            if (h != null) h();
        }

        private static void Flush()
        {
            lock (_lock)
            {
                if (!_dirty) return;
                var payload = new Payload { items = new List<GitCommandRecord>(_records) };
                SessionState.SetString(StateKey, JsonUtility.ToJson(payload));
                _dirty = false;
            }
        }

        private static List<GitCommandRecord> Load()
        {
            var json = SessionState.GetString(StateKey, null);
            if (string.IsNullOrEmpty(json)) return new List<GitCommandRecord>();

            try
            {
                var payload = new Payload();
                JsonUtility.FromJsonOverwrite(json, payload);
                return payload.items ?? new List<GitCommandRecord>();
            }
            catch
            {
                // Формат журнала — не то, ради чего стоит ронять инициализацию.
                return new List<GitCommandRecord>();
            }
        }
    }
}
