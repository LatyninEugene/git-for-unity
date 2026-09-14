using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lev.Git
{
    /// <summary>Результат построения истории сцены.</summary>
    public sealed class SceneHistoryResult
    {
        public List<SceneRevision> Revisions = new List<SceneRevision>();
        public List<SceneEvent> Events = new List<SceneEvent>();

        /// <summary>История длиннее запрошенной глубины — есть что дочитать.</summary>
        public bool More;

        /// <summary>Сколько версий взято из кэша на диске, а сколько разобрано заново.</summary>
        public int FromCache;
        public int Computed;

        public string Error;
        public bool Canceled;
    }

    /// <summary>
    /// История сцены или префаба: достаёт версии файла из git и строит по ним
    /// ленту событий по объектам.
    ///
    /// Версии берутся одним вызовом `git log --raw`: он отдаёт и описание
    /// коммита, и идентификаторы содержимого файла до и после него.
    ///
    /// Дорогое здесь — чтение версий из git и их разбор. Поэтому разница каждой
    /// пары версий лежит на диске (см. <see cref="SceneDiffStore"/>): повторное
    /// открытие окна и дочитывание глубже разбирают только то, чего ещё не
    /// разбирали. Тексты версий в памяти не копятся — сцена бывает в десятки
    /// мегабайт, и сотня её версий заняла бы гигабайты. Каждый текст живёт
    /// ровно до последнего коммита, которому он нужен.
    /// </summary>
    public static class SceneHistory
    {
        /// <summary>Нулевой идентификатор: файла с этой стороны коммита нет.</summary>
        private const string NoBlob = "0000000000000000000000000000000000000000";

        /// <summary>
        /// Готовые ленты по файлам. Ключ включает HEAD: после коммита или
        /// checkout история другая, и старый ответ показывать нельзя.
        /// </summary>
        private static readonly Dictionary<string, SceneHistoryResult> _results =
            new Dictionary<string, SceneHistoryResult>(StringComparer.Ordinal);

        /// <summary>Уборка кэша на диске — раз за сессию редактора.</summary>
        private static bool _trimmed;

        public static void ClearCache()
        {
            lock (_results) _results.Clear();
        }

        private static string ResultKey(string projectPath)
        {
            return projectPath + "|" + (GitStatusCache.HeadOid ?? string.Empty);
        }

        /// <summary>
        /// Уже посчитанная лента, если она есть. Для подписей в инспекторе:
        /// сами они историю не заказывают — чтение сцены из десятков коммитов
        /// не должно начинаться от того, что человек выделил объект.
        /// </summary>
        public static bool TryGetCached(string projectPath, out SceneHistoryResult result)
        {
            lock (_results) return _results.TryGetValue(ResultKey(projectPath), out result);
        }

        public static async Task<SceneHistoryResult> BuildAsync(
            string projectPath, int limit,
            Action<string> onProgress = null, CancellationToken cancellation = default(CancellationToken),
            string fromRev = null)
        {
            var result = new SceneHistoryResult();

            // Путь к Library — API движка, его можно спрашивать только с главного
            // потока. Берём сразу, до первого ожидания.
            var library = LibraryDir();
            var folder = SceneDiffStore.FolderIn(library);

            var gitPath = GitRepository.ToGitPath(projectPath);
            if (string.IsNullOrEmpty(gitPath))
            {
                result.Error = L.F("File outside the repository: {0}", projectPath);
                return result;
            }

            if (GitStatusCache.IsInitialCommit)
            {
                result.Error = L.T("The repository has no commits yet.");
                return result;
            }

            try
            {
                Report(onProgress, L.T("Reading the version list…"));

                var revisions = await LoadRevisionsAsync(gitPath, limit, cancellation, fromRev);
                if (cancellation.IsCancellationRequested) { result.Canceled = true; return result; }

                if (revisions == null)
                {
                    result.Error = L.T("Couldn't get the file history.");
                    return result;
                }

                result.Revisions = revisions;
                result.More = revisions.Count >= limit;

                if (revisions.Count == 0) return result;

                if (!_trimmed)
                {
                    _trimmed = true;
                    await Task.Run(() =>
                    {
                        SceneDiffStore.DropOtherVersions(library);
                        SceneDiffStore.Trim(folder, SceneDiffStore.MaxEntries);
                    });
                }

                Report(onProgress, L.T("Checking the cache…"));

                var diffs = new List<SceneNode>[revisions.Count];
                var missing = new List<int>();

                await Task.Run(() =>
                {
                    for (int i = 0; i < revisions.Count; i++)
                    {
                        diffs[i] = SceneDiffStore.Load(folder, revisions[i].OldBlob, revisions[i].NewBlob);
                        if (diffs[i] == null) missing.Add(i);
                    }
                }, cancellation);

                result.FromCache = revisions.Count - missing.Count;

                if (missing.Count > 0)
                {
                    if (!await ComputeMissingAsync(revisions, diffs, missing, folder, result,
                                                   onProgress, cancellation))
                        return result;
                }

                if (cancellation.IsCancellationRequested) { result.Canceled = true; return result; }

                Report(onProgress, L.T("Building the timeline…"));

                result.Events = await Task.Run(() => SceneTimeline.BuildFromDiffs(revisions, diffs), cancellation);

                lock (_results) _results[ResultKey(projectPath)] = result;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Canceled = true;
                return result;
            }
        }

        /// <summary>
        /// Разбирает версии, которых нет в кэше, и складывает разницу на диск.
        /// false — чтение отменили.
        /// </summary>
        private static async Task<bool> ComputeMissingAsync(
            List<SceneRevision> revisions, List<SceneNode>[] diffs, List<int> missing,
            string folder, SceneHistoryResult result,
            Action<string> onProgress, CancellationToken cancellation)
        {
            // Сколько раз каждая версия ещё понадобится: соседние коммиты делят
            // версии — «после» одного это «до» следующего, — и текст держится в
            // памяти ровно до последнего коммита, которому он нужен.
            var uses = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var i in missing)
            {
                foreach (var blob in BlobsOf(revisions[i]))
                {
                    int n;
                    uses[blob] = uses.TryGetValue(blob, out n) ? n + 1 : 1;
                }
            }

            var texts = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int k = 0; k < missing.Count; k++)
            {
                if (cancellation.IsCancellationRequested) { result.Canceled = true; return false; }

                var rev = revisions[missing[k]];

                Report(onProgress, L.F("Parsing versions: {0} of {1}{2}",
                    k + 1, missing.Count,
                    result.FromCache > 0 ? " · " + L.F("from cache {0}", result.FromCache) : string.Empty));

                bool readable = true;
                foreach (var blob in BlobsOf(rev))
                {
                    if (texts.ContainsKey(blob)) continue;

                    var text = await ReadBlobAsync(blob, cancellation);
                    if (text == null) { readable = false; break; }
                    texts[blob] = text;
                }

                // Отмену проверяем до разбора: из недочитанного текста получилась
                // бы разница «всё добавлено», и в кэш она легла бы навсегда.
                if (cancellation.IsCancellationRequested) { result.Canceled = true; return false; }

                if (readable)
                {
                    rev.OldText = TextOf(texts, rev.OldBlob);
                    rev.NewText = TextOf(texts, rev.NewBlob);

                    var diff = await Task.Run(() => SceneTimeline.Diff(rev), cancellation);
                    diffs[missing[k]] = diff;

                    // Не разобравшуюся версию в кэш не пишем: после исправления
                    // разборщика она должна посчитаться заново.
                    if (diff != null)
                        await Task.Run(() => SceneDiffStore.Save(folder, rev.OldBlob, rev.NewBlob, diff));

                    rev.OldText = string.Empty;
                    rev.NewText = string.Empty;
                    result.Computed++;
                }

                foreach (var blob in BlobsOf(rev))
                {
                    int left;
                    if (!uses.TryGetValue(blob, out left)) continue;

                    if (left <= 1) { uses.Remove(blob); texts.Remove(blob); }
                    else uses[blob] = left - 1;
                }
            }

            return true;
        }

        private static IEnumerable<string> BlobsOf(SceneRevision rev)
        {
            if (!string.IsNullOrEmpty(rev.OldBlob) && rev.OldBlob != NoBlob) yield return rev.OldBlob;
            if (!string.IsNullOrEmpty(rev.NewBlob) && rev.NewBlob != NoBlob && rev.NewBlob != rev.OldBlob)
                yield return rev.NewBlob;
        }

        private static string TextOf(Dictionary<string, string> texts, string blob)
        {
            if (string.IsNullOrEmpty(blob) || blob == NoBlob) return string.Empty;

            string text;
            return texts.TryGetValue(blob, out text) ? text : string.Empty;
        }

        private static string LibraryDir()
        {
            return System.IO.Path.GetFullPath(
                System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "Library"));
        }

        private static void Report(Action<string> onProgress, string message)
        {
            if (onProgress != null) onProgress(message);
        }

        // ------------------------------------------------------------- git ---

        /// <summary>
        /// Формат записи журнала. Разделитель записей — RS (0x1E), полей — NUL:
        /// ни то, ни другое не встречается ни в именах, ни в заголовках.
        /// </summary>
        private const string Format = "%x1e%H%x00%h%x00%an%x00%ae%x00%aI%x00%s";

        private static async Task<List<SceneRevision>> LoadRevisionsAsync(
            string gitPath, int limit, CancellationToken cancellation, string fromRev = null)
        {
            // --follow продолжает историю через переименование файла сцены.
            // Он работает только с одним путём — здесь ровно этот случай.
            // --first-parent — намеренно: без него правка из ветки показывалась бы
            // дважды, в коммите ветки и в слиянии, которое её принесло.
            var args = new StringBuilder("-c core.quotepath=false --no-optional-locks log");
            args.Append(" --first-parent --follow --raw --no-abbrev -M");
            args.Append(" -n ").Append(Math.Max(1, limit));
            args.Append(" --format=").Append(Format);

            // Ревизия — до «--»: после него git счёл бы её путём.
            if (!string.IsNullOrEmpty(fromRev)) args.Append(' ').Append(GitOperations.Q(fromRev));
            args.Append(" -- ").Append(GitOperations.Q(gitPath));

            var r = await GitProcess.RunAsync(
                GitRepository.GitExe, args.ToString(), GitRepository.RepoRoot,
                null, 120000, cancellation);

            if (!r.Ok) return null;
            return SceneRevisionParser.Parse(r.StdOut);
        }

        private static async Task<string> ReadBlobAsync(string blob, CancellationToken cancellation)
        {
            var r = await GitProcess.RunAsync(
                GitRepository.GitExe, "cat-file blob " + blob, GitRepository.RepoRoot,
                null, 120000, cancellation);

            return r.Ok ? r.StdOut : null;
        }
    }
}
