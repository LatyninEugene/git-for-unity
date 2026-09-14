using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Lev.Git.Preview
{
    /// <summary>
    /// Миниатюры версий ассетов: для ленты «Истории ассета», значков в списках
    /// файлов и обсуждений merge request.
    ///
    /// Каждая версия рисуется тем же показом, что и в панели, только в маленькую
    /// текстуру. Готовое кладётся в память и на диск — Library/LevGit/Previews,
    /// под именем от ключа версии: версия из коммита не меняется никогда и
    /// рисуется один раз за всю жизнь проекта.
    ///
    /// Запросы идут в очередь и выполняются по одному за кадр редактора: список
    /// из сотни файлов не должен останавливать работу, пока рисуются значки.
    /// Кто ждёт миниатюру, подписывается на Ready.
    /// </summary>
    internal static class PreviewThumbnails
    {
        public const int Size = 64;

        private const int MemoryLimit = 400;
        private const int DiskLimit = 3000;

        /// <summary>Готова очередная миниатюра — списку пора перерисовать строки.</summary>
        public static event Action Ready;

        private sealed class Request
        {
            public string Key;
            public string ProjectPath;
            public RevisionSide Side;
        }

        private static readonly Dictionary<string, Texture2D> Memory = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly HashSet<string> Failed = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Queued = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Queue<Request> Pending = new Queue<Request>();
        private static bool _working;
        private static int _written;

        static PreviewThumbnails()
        {
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                foreach (var texture in Memory.Values)
                    if (texture != null) Object.DestroyImmediate(texture);
                Memory.Clear();
            };

            TrimDisk();
        }

        /// <summary>
        /// Показывать ли миниатюры в списках файлов — по настройке проекта. Ассеты,
        /// которые собираются только временным импортом, в списках не рисуются никогда.
        /// </summary>
        public static bool Enabled(string projectPath)
        {
            int mode = GitSettings.instance.listThumbnails;
            if (mode == 0 || string.IsNullOrEmpty(projectPath)) return false;

            var loader = AssetLoaders.Find(projectPath);
            if (loader == null || loader is SandboxAssetLoader) return false;
            return mode == 2 || loader is ImageAssetLoader;
        }

        public static Texture2D ForWorktree(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return null;
            return Get("wt|" + projectPath + "|" + GitRepository.WorktreeStamp(projectPath) + "|" + GitRepository.WorktreeStamp(projectPath + ".meta"),
                       projectPath, RevisionSide.Worktree(projectPath, string.Empty));
        }

        public static Texture2D ForHead(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(GitStatusCache.HeadOid)) return null;
            var gitPath = GitRepository.ToGitPath(projectPath);
            return Get("c|" + GitStatusCache.HeadOid + "|" + gitPath, projectPath, RevisionSide.Head(gitPath, string.Empty));
        }

        public static Texture2D ForCommit(string sha, string projectPath, string gitPath = null)
        {
            if (string.IsNullOrEmpty(sha) || string.IsNullOrEmpty(projectPath)) return null;
            gitPath = gitPath ?? GitRepository.ToGitPath(projectPath);
            return Get("c|" + sha + "|" + gitPath, projectPath, RevisionSide.Commit(sha, gitPath, string.Empty));
        }

        /// <summary>Перерисовывать строки списка, когда дорисовываются миниатюры, — не чаще раза в 150 мс.</summary>
        public static void Watch(ListView list)
        {
            bool scheduled = false;
            Action onReady = () =>
            {
                if (scheduled) return;
                scheduled = true;
                list.schedule.Execute(() =>
                {
                    scheduled = false;
                    list.RefreshItems();
                }).StartingIn(150);
            };

            list.RegisterCallback<AttachToPanelEvent>(_ => Ready += onReady);
            list.RegisterCallback<DetachFromPanelEvent>(_ => Ready -= onReady);
        }

        private static Texture2D Get(string key, string projectPath, RevisionSide side)
        {
            Texture2D texture;
            if (Memory.TryGetValue(key, out texture) && texture != null) return texture;
            if (Failed.Contains(key) || Queued.Contains(key)) return null;

            Queued.Add(key);
            Pending.Enqueue(new Request { Key = key, ProjectPath = projectPath, Side = side });
            return null;
        }

        private static async void Pump()
        {
            if (_working || Pending.Count == 0) return;
            _working = true;

            var request = Pending.Dequeue();
            try
            {
                var texture = FromDisk(request.Key) ?? await RenderAsync(request);
                Queued.Remove(request.Key);

                if (texture == null)
                {
                    Failed.Add(request.Key);
                    return;
                }

                if (Memory.Count >= MemoryLimit)
                {
                    foreach (var old in Memory.Values)
                        if (old != null) Object.DestroyImmediate(old);
                    Memory.Clear();
                }

                Memory[request.Key] = texture;
                var handler = Ready;
                if (handler != null) handler();
            }
            catch (Exception e)
            {
                Queued.Remove(request.Key);
                Failed.Add(request.Key);
                Diagnostics.Journal.Warn(L.F("Thumbnail for {0} was not rendered: {1}", request.ProjectPath, e.Message));
            }
            finally
            {
                _working = false;
            }
        }

        private static async Task<Texture2D> RenderAsync(Request request)
        {
            var loader = AssetLoaders.Find(request.ProjectPath);
            if (loader == null || loader.NeedsConsent || loader is SandboxAssetLoader) return null;

            LoadedAsset loaded;
            var side = request.Side;

            if (side.Kind == SideKind.Worktree && !loader.PreferFileForWorktree)
            {
                loaded = LoadedAsset.FromProject(request.ProjectPath);
            }
            else
            {
                var oid = await side.ResolveOidAsync();
                var cacheKey = oid != null ? oid + "|" + request.ProjectPath : null;
                loaded = RevisionCache.Acquire(cacheKey);

                if (loaded == null)
                {
                    var bytes = await side.ReadAsync(null);
                    if (bytes == null) return null;

                    var meta = await side.ReadMetaAsync();
                    loaded = await AssetLoaders.SafeLoadAsync(loader, request.ProjectPath, bytes, meta);
                    loaded.MetaText = meta;
                    RevisionCache.Add(cacheKey, loaded);
                }
            }

            try
            {
                if (loaded.Main == null) return null;

                var presenter = AssetPresenters.Create(loaded);
                if (presenter == null) return null;

                try
                {
                    presenter.Prepare(null, loaded);

                    var sync = new PreviewSync { Static = true };
                    var texture = presenter.Render(new Rect(0f, 0f, Size, Size), loaded, 0, sync);
                    if (texture == null || texture == PreviewCanvas.Blank) return null;

                    var result = Snapshot(texture, presenter.Aspect(loaded));

                    // Статичное превью отдаёт новую текстуру на каждый вызов — она наша.
                    if (texture is Texture2D && texture != loaded.Main && !EditorUtility.IsPersistent(texture))
                        Object.DestroyImmediate(texture);

                    ToDisk(request.Key, result);
                    return result;
                }
                finally
                {
                    presenter.Dispose();
                }
            }
            finally
            {
                RevisionCache.Release(loaded);
            }
        }

        /// <summary>Картинка показа — в квадрат Size×Size с сохранением пропорций.</summary>
        private static Texture2D Snapshot(Texture texture, float aspect)
        {
            var rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;

            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.clear);

                GL.PushMatrix();
                GL.LoadPixelMatrix(0f, Size, Size, 0f);
                if (aspect <= 0f) aspect = texture.height > 0 ? texture.width / (float)texture.height : 1f;
                Graphics.DrawTexture(PreviewCanvas.Fit(new Rect(0f, 0f, Size, Size), aspect), texture);
                GL.PopMatrix();

                var result = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
                result.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0);
                result.Apply(false);
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        // ------------------------------------------------------------ диск ---

        private static string Directory
        {
            get { return Path.Combine(GitRepository.ProjectRoot, "Library/LevGit/Previews"); }
        }

        public static string DiskFolder
        {
            get { return Directory; }
        }

        /// <summary>Удаляет все миниатюры с диска и из памяти. Возвращает число удалённых файлов.</summary>
        public static int ClearDisk()
        {
            foreach (var texture in Memory.Values)
                if (texture != null) Object.DestroyImmediate(texture);
            Memory.Clear();
            Failed.Clear();

            int n = 0;
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return 0;
                foreach (var file in System.IO.Directory.GetFiles(Directory, "*.png"))
                {
                    try { File.Delete(file); n++; } catch { }
                }
            }
            catch { }

            var handler = Ready;
            if (handler != null) handler();
            return n;
        }

        /// <summary>Удаляет долю самых давно открытых миниатюр — при превышении лимита места.</summary>
        public static void TrimOldest(float fraction)
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return;

                var files = new DirectoryInfo(Directory).GetFiles("*.png");
                int count = Mathf.CeilToInt(files.Length * Mathf.Clamp01(fraction));
                foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc).Take(count))
                    file.Delete();
            }
            catch { }
        }

        private static string FileOf(string key)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                return Path.Combine(Directory, string.Concat(hash.Select(b => b.ToString("x2")).ToArray()) + ".png");
            }
        }

        private static Texture2D FromDisk(string key)
        {
            try
            {
                var file = FileOf(key);
                if (!File.Exists(file)) return null;

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                if (texture.LoadImage(File.ReadAllBytes(file))) return texture;

                Object.DestroyImmediate(texture);
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static void ToDisk(string key, Texture2D texture)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllBytes(FileOf(key), texture.EncodeToPNG());

                if (++_written % 100 == 0) PreviewStorage.EnforceLimit();
            }
            catch { }
        }

        /// <summary>Миниатюры рабочей копии копятся с каждым сохранением — старые убираются.</summary>
        private static void TrimDisk()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return;

                var files = new DirectoryInfo(Directory).GetFiles("*.png");
                if (files.Length <= DiskLimit) return;

                foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc).Take(files.Length - DiskLimit / 2))
                    file.Delete();
            }
            catch { }
        }
    }
}
