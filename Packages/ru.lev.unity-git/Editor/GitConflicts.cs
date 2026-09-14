using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Lev.Git
{
    /// <summary>Конфликтный файл вместе со всем, что о нём известно.</summary>
    public sealed class ConflictFile
    {
        public ConflictStages Stages;
        public string GitPath;
        public string ProjectPath;
        public ConflictShape Shape;
        public ConflictContent Content;

        public string BaseBlob, MineBlob, TheirsBlob;

        public bool IsMeta => GitPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

        /// <summary>Обе стороны файл сохранили — есть что сливать по содержимому.</summary>
        public bool BothPresent => MineBlob != null && TheirsBlob != null;

        public string BlobOf(MergeSide side)
        {
            return side == MergeSide.Theirs ? TheirsBlob : side == MergeSide.Base ? BaseBlob : MineBlob;
        }
    }

    /// <summary>
    /// Операции над конфликтами: что конфликтует, чем разрешить, как
    /// продолжить или отменить прерванную операцию.
    ///
    /// Всё, что пишет в рабочую копию, сначала кладёт исходные версии в
    /// Library/LevGit/MergeBackups: разрешение конфликта — место, где
    /// чужую работу теряют одним щелчком, и откатиться должно быть к чему.
    /// </summary>
    public static class GitConflicts
    {
        /// <summary>Файлы крупнее этого не читаются текстом, если расширение не говорит, что это YAML.</summary>
        private const long MaxTextBytes = 8 * 1024 * 1024;

        private static GitOperationState _state = new GitOperationState();

        public static GitOperationState State => _state;

        /// <summary>Перечитывает состояние операции. Дёшево: только файлы в .git.</summary>
        public static GitOperationState RefreshState()
        {
            if (!GitRepository.IsRepo) return _state = new GitOperationState();
            _state = GitOperationState.Read(GitOperationState.ResolveGitDir(GitRepository.RepoRoot));
            return _state;
        }

        // ---------------------------------------------------------- список ---

        public static async Task<List<ConflictFile>> ListAsync()
        {
            var state = RefreshState();
            var list = new List<ConflictFile>();

            var r = await GitOperations.Git("-c core.quotepath=false ls-files -u -z", 30000);
            if (!r.Ok) return list;

            foreach (var stages in GitConflictParser.ParseUnmerged(r.StdOut))
            {
                var file = new ConflictFile
                {
                    Stages = stages,
                    GitPath = stages.GitPath,
                    ProjectPath = GitRepository.ToProjectPath(stages.GitPath) ?? stages.GitPath,
                    Shape = GitConflictParser.Shape(stages, state),
                    BaseBlob = stages.Blob[1],
                    MineBlob = stages.Blob[state.MineStage],
                    TheirsBlob = stages.Blob[state.TheirsStage]
                };
                file.Content = await ClassifyAsync(file);
                list.Add(file);
            }

            list.Sort((a, b) => string.Compare(a.GitPath, b.GitPath, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static async Task<ConflictContent> ClassifyAsync(ConflictFile f)
        {
            var blob = f.MineBlob ?? f.TheirsBlob ?? f.BaseBlob;
            if (blob == null) return GitConflictParser.Classify(f.GitPath, null);

            var size = await GitOperations.Git("cat-file -s " + blob, 15000);
            long bytes;
            if (size.Ok && long.TryParse(size.StdOut.Trim(), out bytes) && bytes > MaxTextBytes &&
                !GitConflictParser.IsYamlPath(f.GitPath))
                return ConflictContent.Binary;

            // Для решения хватает начала файла, но git отдаёт объект только целиком.
            var text = await BlobTextAsync(blob);
            if (text != null && text.Length > 16000) text = text.Substring(0, 16000);
            return GitConflictParser.Classify(f.GitPath, text);
        }

        /// <summary>Содержимое версии текстом. null — версии нет или прочитать не вышло.</summary>
        public static async Task<string> BlobTextAsync(string blob)
        {
            if (string.IsNullOrEmpty(blob)) return null;
            var r = await GitOperations.Git("cat-file blob " + blob, 120000);
            return r.Ok ? r.StdOut : null;
        }

        /// <summary>
        /// Версия файла в том виде, в каком её положил бы checkout: с фильтрами
        /// .gitattributes и LFS. Для превью бинарников и для «взять сторону».
        /// </summary>
        public static async Task<bool> ExportBlobAsync(ConflictFile f, MergeSide side, string destination)
        {
            var blob = f.BlobOf(side);
            if (blob == null) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            return await GitProcess.RunToFileAsync(
                GitRepository.GitExe,
                "cat-file --filters --path=" + GitOperations.Q(f.GitPath) + " " + blob,
                GitRepository.RepoRoot, destination, 300000);
        }

        // ----------------------------------------------------- разрешение ---

        /// <summary>
        /// Берёт файл целиком с одной стороны. У стороны, где файла нет,
        /// «взять» значит удалить.
        /// </summary>
        public static async Task<ProcessResult> TakeSideAsync(ConflictFile f, MergeSide side)
        {
            await BackupAsync(f);

            if (f.BlobOf(side) == null)
                return await GitOperations.Git("rm -q --force -- " + GitOperations.Q(f.GitPath), 60000);

            var full = FullPath(f.GitPath);
            var temp = full + ".levgitlab-" + Guid.NewGuid().ToString("N") + ".tmp";

            if (!await ExportBlobAsync(f, side, temp))
            {
                TryDelete(temp);
                return Fail(L.T("Couldn't get the file version from the repository."));
            }

            try
            {
                if (File.Exists(full)) File.Delete(full);
                File.Move(temp, full);
            }
            catch (Exception e)
            {
                TryDelete(temp);
                return Fail(L.F("Couldn't write {0}: {1}", f.ProjectPath, e.Message));
            }

            return await GitOperations.Git("add -- " + GitOperations.Q(f.GitPath), 60000);
        }

        /// <summary>
        /// Слияние текстового файла: git сводит всё, что правила одна сторона, а
        /// места, где правили обе, отдаёт тремя версиями. null — версии не прочитались.
        ///
        /// Версии достаются с фильтрами .gitattributes — как их положил бы checkout,
        /// поэтому итог сохраняет переводы строк рабочей копии.
        /// </summary>
        public static async Task<TextMergeResult> TextMergeAsync(ConflictFile f)
        {
            var dir = Path.Combine(Path.GetTempPath(), "levgit_merge_" + Guid.NewGuid().ToString("N"));
            var ext = Path.GetExtension(f.GitPath);
            var mine = Path.Combine(dir, "mine" + ext);
            var baseFile = Path.Combine(dir, "base" + ext);
            var theirs = Path.Combine(dir, "theirs" + ext);

            try
            {
                Directory.CreateDirectory(dir);

                if (!await ExportBlobAsync(f, MergeSide.Mine, mine) || !await ExportBlobAsync(f, MergeSide.Theirs, theirs))
                    return null;

                // Обе стороны добавили файл — базы нет, сливаем с пустой.
                if (f.BaseBlob == null || !await ExportBlobAsync(f, MergeSide.Base, baseFile))
                    File.WriteAllBytes(baseFile, new byte[0]);

                var head = File.ReadAllBytes(mine);
                bool bom = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;

                // Без -p результат пишется в файл «моё»: вывод в stdout читался бы
                // текстом и терял бы переводы строк. Код возврата — число конфликтов.
                var r = await GitOperations.Git("merge-file --diff3" +
                    " -L " + GitOperations.Q(TextMerge.MineLabel) +
                    " -L " + GitOperations.Q(TextMerge.BaseLabel) +
                    " -L " + GitOperations.Q(TextMerge.TheirsLabel) +
                    " " + GitOperations.Q(mine) + " " + GitOperations.Q(baseFile) + " " + GitOperations.Q(theirs), 120000);

                if (r.ExitCode < 0 || r.ExitCode > 127) return null;

                var result = TextMerge.Parse(File.ReadAllText(mine, new UTF8Encoding(false)));
                result.Bom = bom;
                return result;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>Записывает слитый текст и отмечает файл решённым.</summary>
        public static async Task<ProcessResult> WriteResolvedAsync(ConflictFile f, string text, bool bom = false)
        {
            if (GitConflictParser.HasConflictMarkers(text))
                return Fail(L.T("Conflict markers remain in the result — such a file can't be written."));

            await BackupAsync(f);

            try
            {
                var full = FullPath(f.GitPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, text, new UTF8Encoding(bom));
            }
            catch (Exception e)
            {
                return Fail(L.F("Couldn't write {0}: {1}", f.ProjectPath, e.Message));
            }

            return await GitOperations.Git("add -- " + GitOperations.Q(f.GitPath), 60000);
        }

        /// <summary>
        /// Отметить текстовый файл решённым после внешнего инструмента.
        /// Маркеры в файле — значит, решено не всё, и git add их молча закоммитил бы.
        /// </summary>
        public static async Task<ProcessResult> MarkResolvedAsync(ConflictFile f)
        {
            var full = FullPath(f.GitPath);
            if (File.Exists(full))
            {
                string text;
                try { text = File.ReadAllText(full); }
                catch (Exception e) { return Fail(e.Message); }

                if (GitConflictParser.HasConflictMarkers(text))
                    return Fail(L.T("Conflict markers remain in the file (<<<<<<<, =======, >>>>>>>)."));

                return await GitOperations.Git("add -- " + GitOperations.Q(f.GitPath), 60000);
            }

            return await GitOperations.Git("rm -q --cached -- " + GitOperations.Q(f.GitPath), 60000);
        }

        /// <summary>
        /// Текстовый файл — во внешний инструмент. Настроенный mergetool
        /// лучше всего, что можно сделать здесь; без него — IDE из настроек Unity.
        /// </summary>
        public static async Task<ProcessResult> OpenExternalAsync(ConflictFile f)
        {
            var tool = await GitOperations.Git("config --get merge.tool", 15000);
            if (tool.Ok && tool.StdOut.Trim().Length > 0)
                return await GitOperations.LongGit("mergetool " + f.ProjectPath,
                    "mergetool --no-prompt -- " + GitOperations.Q(f.GitPath), 3 * 60 * 60 * 1000);

            UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(FullPath(f.GitPath), 1);
            return new ProcessResult
            {
                ExitCode = 0,
                StdOut = L.T("mergetool isn't configured — the file was opened in the external editor. Remove the markers and click “Mark Resolved”.")
            };
        }

        // ------------------------------------------------- UnityYAMLMerge ---

        public static string UnityYamlMergePath
        {
            get
            {
                var tools = Path.Combine(EditorApplication.applicationContentsPath, "Tools");
                var exe = Path.Combine(tools, "UnityYAMLMerge.exe");
                if (File.Exists(exe)) return exe;
                exe = Path.Combine(tools, "UnityYAMLMerge");
                return File.Exists(exe) ? exe : null;
            }
        }

        /// <summary>
        /// Штатное слияние Unity. null — инструмента нет или он не свёл файл сам.
        /// Резервный инструмент выключен: здесь он открыл бы своё окно поверх
        /// редактора, а разбирать остаток будет наше окно.
        /// </summary>
        public static async Task<string> UnityYamlMergeAsync(string gitPath, string baseText, string mine, string theirs)
        {
            var exe = UnityYamlMergePath;
            if (exe == null || mine == null || theirs == null) return null;

            var dir = Path.Combine(Path.GetTempPath(), "levgitlab_uym_" + Guid.NewGuid().ToString("N"));
            var ext = Path.GetExtension(gitPath);

            try
            {
                Directory.CreateDirectory(dir);
                var b = Path.Combine(dir, "base" + ext);
                var m = Path.Combine(dir, "mine" + ext);
                var t = Path.Combine(dir, "theirs" + ext);
                var dest = Path.Combine(dir, "result" + ext);

                var utf8 = new UTF8Encoding(false);
                File.WriteAllText(b, baseText ?? string.Empty, utf8);
                File.WriteAllText(m, mine, utf8);
                File.WriteAllText(t, theirs, utf8);

                // Порядок как у драйвера git: база, их (left), моё (right), результат.
                var r = await GitProcess.RunAsync(exe,
                    "merge -h --force --fallback none " + Quote(b) + " " + Quote(t) + " " + Quote(m) + " " + Quote(dest),
                    dir, null, 300000);

                return r.Ok && File.Exists(dest) ? File.ReadAllText(dest, utf8) : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static string Quote(string s) { return "\"" + s + "\""; }

        // --------------------------------------------------------- операция ---

        public static Task<ProcessResult> ContinueAsync()
        {
            var state = RefreshState();
            string args;
            switch (state.Kind)
            {
                case GitOperationKind.Merge: args = "-c core.editor=true commit --no-edit"; break;
                case GitOperationKind.Rebase: args = "-c core.editor=true rebase --continue"; break;
                case GitOperationKind.CherryPick: args = "-c core.editor=true cherry-pick --continue"; break;
                case GitOperationKind.Revert: args = "-c core.editor=true revert --continue"; break;
                default: return Task.FromResult(Fail(L.T("Nothing to continue: no operation in progress.")));
            }

            return GitOperations.WithAssetGuard(() => GitOperations.LongGit(L.F("{0}: continue", state.Title), args, 600000));
        }

        public static Task<ProcessResult> SkipAsync()
        {
            var state = RefreshState();
            string args;
            switch (state.Kind)
            {
                case GitOperationKind.Rebase: args = "rebase --skip"; break;
                case GitOperationKind.CherryPick: args = "cherry-pick --skip"; break;
                default: return Task.FromResult(Fail(L.T("Only a commit in a rebase or cherry-pick can be skipped.")));
            }

            return GitOperations.WithAssetGuard(() => GitOperations.LongGit(L.F("{0}: skip", state.Title), args, 600000));
        }

        public static Task<ProcessResult> AbortAsync()
        {
            var state = RefreshState();
            string args;
            switch (state.Kind)
            {
                case GitOperationKind.Merge: args = "merge --abort"; break;
                case GitOperationKind.Rebase: args = "rebase --abort"; break;
                case GitOperationKind.CherryPick: args = "cherry-pick --abort"; break;
                case GitOperationKind.Revert: args = "revert --abort"; break;
                default: return Task.FromResult(Fail(L.T("Nothing to abort: no operation in progress.")));
            }

            return GitOperations.WithAssetGuard(() => GitOperations.LongGit(L.F("{0}: abort", state.Title), args, 600000));
        }

        // ------------------------------------------------------ открытая сцена ---

        /// <summary>
        /// Перед записью сцены, открытой в редакторе. Её несохранённые правки
        /// после записи потеряются — об этом спрашиваем. false — человек отказался.
        /// </summary>
        public static bool ConfirmSceneWrite(string projectPath)
        {
            var scene = SceneManager.GetSceneByPath(projectPath);
            if (!scene.IsValid() || !scene.isLoaded || !scene.isDirty) return true;

            return EditorUtility.DisplayDialog(L.T("Scene Is Open"),
                L.F("“{0}” is open and has unsaved changes. After the merged version is written, the scene will reload from disk and those changes will be lost.", scene.name),
                L.T("Write and Reload"), L.T("Cancel"));
        }

        /// <summary>После записи: переимпорт и перечитывание сцены, если она открыта.</summary>
        public static void ReloadAfterWrite(string projectPath)
        {
            AssetDatabase.ImportAsset(projectPath, ImportAssetOptions.ForceUpdate);

            var scene = SceneManager.GetSceneByPath(projectPath);
            if (!scene.IsValid() || !scene.isLoaded) return;

            if (SceneManager.sceneCount == 1)
            {
                EditorSceneManager.OpenScene(projectPath, OpenSceneMode.Single);
                return;
            }

            EditorSceneManager.CloseScene(scene, true);
            EditorSceneManager.OpenScene(projectPath, OpenSceneMode.Additive);
        }

        // ------------------------------------------------------ резервные копии ---

        public static string BackupRoot => Path.Combine(Path.Combine(GitRepository.ProjectRoot, "Library"), "LevGit/MergeBackups");

        private static string _session;

        /// <summary>
        /// Кладёт все версии файла и текущее содержимое рабочей копии рядом.
        /// Одна папка на сеанс разрешения, чтобы связанные файлы лежали вместе.
        /// </summary>
        private static async Task BackupAsync(ConflictFile f)
        {
            try
            {
                if (_session == null) _session = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var dir = Path.Combine(BackupRoot, _session);
                var target = Path.Combine(dir, f.GitPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));

                foreach (var side in new[] { MergeSide.Base, MergeSide.Mine, MergeSide.Theirs })
                {
                    if (f.BlobOf(side) == null) continue;
                    await ExportBlobAsync(f, side, target + "." + side.ToString().ToLowerInvariant());
                }

                var full = FullPath(f.GitPath);
                if (File.Exists(full)) File.Copy(full, target + ".worktree", true);
            }
            catch
            {
                // Копия — страховка, а не условие: не вышло — не повод не решать конфликт.
            }
        }

        /// <summary>Новый сеанс резервных копий — после завершения или отмены операции.</summary>
        public static void EndBackupSession()
        {
            _session = null;
        }

        private static string FullPath(string gitPath)
        {
            return Path.Combine(GitRepository.RepoRoot, gitPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static ProcessResult Fail(string message)
        {
            return new ProcessResult { ExitCode = 1, StdErr = message };
        }
    }
}
