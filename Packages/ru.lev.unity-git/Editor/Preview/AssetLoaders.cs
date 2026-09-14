using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace Lev.Git.Preview
{
    /// <summary>
    /// Превращает байты версии в объекты Unity, ничего не кладя в проект.
    /// Загрузчики находятся сами через TypeCache: достаточно унаследоваться.
    /// </summary>
    public abstract class AssetLoader
    {
        /// <summary>При нескольких подходящих побеждает больший приоритет.</summary>
        public virtual int Priority { get { return 0; } }

        /// <summary>
        /// Рабочую копию тоже читать из файла, а не брать ассет проекта. Для
        /// картинок и звука так честнее: обе стороны — исходные данные, без
        /// сжатия импорта, и маска разницы сравнивает одинаковое с одинаковым.
        /// </summary>
        public virtual bool PreferFileForWorktree { get { return false; } }

        public abstract bool CanLoad(string projectPath);

        /// <summary>Вызывается на главном потоке. Ошибку возвращать через LoadedAsset.Failed, а не исключением.</summary>
        public abstract LoadedAsset Load(string projectPath, byte[] bytes);

        /// <summary>Для загрузчиков, которым нужно дождаться Unity — например, звука.</summary>
        public virtual Task<LoadedAsset> LoadAsync(string projectPath, byte[] bytes)
        {
            return Task.FromResult(Load(projectPath, bytes));
        }

        /// <summary>Загрузка с метой версии — нужна тем, кто импортирует: мета и есть настройки импорта.</summary>
        public virtual Task<LoadedAsset> LoadAsync(string projectPath, byte[] bytes, string metaText)
        {
            return LoadAsync(projectPath, bytes);
        }

        /// <summary>Загрузка что-то меняет в проекте (временный импорт) — сначала спросить человека.</summary>
        internal virtual bool NeedsConsent { get { return false; } }

        /// <summary>
        /// Новый временный файл в Temp проекта с расширением ассета — для API Unity,
        /// которые читают только с диска. Удалить за собой после загрузки.
        /// </summary>
        protected static string TempFileFor(string projectPath)
        {
            return AssetLoaders.TempFile(projectPath);
        }
    }

    internal static class AssetLoaders
    {
        private static List<AssetLoader> _all;

        public static AssetLoader Find(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return null;

            if (_all == null)
            {
                _all = new List<AssetLoader>();
                foreach (var type in TypeCache.GetTypesDerivedFrom<AssetLoader>())
                {
                    if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null) continue;
                    try { _all.Add((AssetLoader)Activator.CreateInstance(type)); }
                    catch (Exception e) { Diagnostics.Journal.Warn(L.F("Preview loader {0} was not created: {1}", type.Name, e.Message)); }
                }
                _all.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }

            foreach (var loader in _all)
                if (loader.CanLoad(projectPath)) return loader;

            return null;
        }

        public static async Task<LoadedAsset> SafeLoadAsync(AssetLoader loader, string projectPath, byte[] bytes, string metaText = null)
        {
            try
            {
                return await loader.LoadAsync(projectPath, bytes, metaText) ?? LoadedAsset.Failed(L.T("The version did not load."));
            }
            catch (Exception e)
            {
                return LoadedAsset.Failed(L.F("The version did not load: {0}", e.Message));
            }
        }

        public static bool HasExtension(string path, params string[] extensions)
        {
            var ext = Path.GetExtension(path ?? string.Empty);
            foreach (var e in extensions)
                if (ext.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Временный файл версии в Temp проекта — для API Unity, которые читают только с диска.</summary>
        public static string TempFile(string projectPath)
        {
            var dir = Path.Combine(GitRepository.ProjectRoot, "Temp/LevGit/rev");
            Directory.CreateDirectory(dir);

            // Имя каждый раз новое: так версии не могут перепутаться, даже если
            // Unity когда-нибудь начнёт кэшировать файлы по пути.
            return Path.Combine(dir, Guid.NewGuid().ToString("N") + Path.GetExtension(projectPath));
        }
    }

    /// <summary>
    /// YAML-ассеты Unity: материалы, ScriptableObject, анимации, настройки.
    ///
    /// Версия пишется во временный файл и читается сериализатором Unity в обход
    /// базы ассетов. Получаются настоящие объекты: ссылки по GUID ведут на ассеты
    /// проекта, скрипты находятся, у объекта работает его инспектор и превью.
    /// Проверено «Проверкой превью» на Unity 6000.3.
    /// </summary>
    internal sealed class YamlAssetLoader : AssetLoader
    {
        public override bool CanLoad(string projectPath)
        {
            return AssetYaml.IsYamlAssetPath(projectPath);
        }

        public override LoadedAsset Load(string projectPath, byte[] bytes)
        {
            if (!AssetYaml.LooksLikeYaml(bytes))
                return LoadedAsset.Failed(L.T("The version is saved in binary form — it can only be read with Asset Serialization: Force Text."));

            var file = AssetLoaders.TempFile(projectPath);

            Object[] objects;
            try
            {
                File.WriteAllBytes(file, bytes);
                objects = InternalEditorUtility.LoadSerializedFileAndForget(file);
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }

            if (objects == null || objects.Length == 0)
                return LoadedAsset.Failed(L.T("Unity could not parse this file version."));

            var loaded = new LoadedAsset { All = objects, Describable = true, Info = PreviewText.Bytes(bytes.Length) };

            int missing = 0;
            foreach (var o in objects)
            {
                if (o == null) continue;
                loaded.SourceFlags[o] = o.hideFlags;
                o.hideFlags = HideFlags.HideAndDontSave;
                if (LoadedAsset.IsMissingClass(o)) missing++;
            }

            // Документы файла нужны дважды: найти главный объект и узнать
            // локальные идентификаторы — по ним к объекту цепляются комментарии.
            var docs = bytes.Length <= 64 * 1024 * 1024 ? UnityYamlParser.Parse(Encoding.UTF8.GetString(bytes)) : null;
            loaded.Main = PickMain(projectPath, docs, objects);
            if (docs != null) MapFileIds(loaded, docs, objects);

            if (missing > 0)
                loaded.Notes.Add(L.F("objects without a class: {0} — their scripts are no longer in the project", missing));

            if (loaded.Main == null)
            {
                loaded.Dispose();
                return LoadedAsset.Failed(L.T("The main object of this version was not found."));
            }

            return loaded;
        }

        /// <summary>
        /// Объекты сопоставляются с документами по имени и классу в порядке файла.
        /// У скриптовых объектов класс в файле один на всех — MonoBehaviour, —
        /// поэтому для них сравнивается только имя.
        /// </summary>
        internal static void MapFileIds(LoadedAsset loaded, List<UnityDocument> docs, Object[] objects)
        {
            var queues = new Dictionary<string, Queue<long>>(StringComparer.Ordinal);

            foreach (var d in docs)
            {
                var key = (AssetYaml.NameOf(d) ?? string.Empty) + "|" + (d.ClassId == 114 ? "script" : d.TypeName);
                Queue<long> queue;
                if (!queues.TryGetValue(key, out queue)) queues[key] = queue = new Queue<long>();
                queue.Enqueue(d.FileId);
            }

            foreach (var o in objects)
            {
                if (o == null) continue;

                Queue<long> queue;
                bool scripted = o is ScriptableObject || o is MonoBehaviour;
                if ((scripted && queues.TryGetValue(o.name + "|script", out queue) && queue.Count > 0) ||
                    (queues.TryGetValue(o.name + "|" + o.GetType().Name, out queue) && queue.Count > 0))
                    loaded.FileIds[o] = queue.Dequeue();
            }
        }

        private static Object PickMain(string projectPath, List<UnityDocument> docs, Object[] objects)
        {
            var usable = objects.Where(o => o != null && !LoadedAsset.IsMissingClass(o)).ToArray();

            var expected = AssetDatabase.GetMainAssetTypeAtPath(projectPath);
            if (expected != null)
            {
                var byType = usable.FirstOrDefault(o => expected.IsInstanceOfType(o));
                if (byType != null) return byType;
            }

            // Ассета уже нет в проекте или это файл настроек — ищем главный
            // документ по самому файлу и объект с его именем и классом.
            var doc = AssetYaml.MainDocument(docs);
            if (doc != null)
            {
                var name = AssetYaml.NameOf(doc) ?? string.Empty;
                var exact = usable.FirstOrDefault(o => o.name == name &&
                    (doc.ClassId == 114 ? o is ScriptableObject : o.GetType().Name == doc.TypeName));
                if (exact != null) return exact;

                var byName = usable.FirstOrDefault(o => o.name == name);
                if (byName != null) return byName;
            }

            return usable.FirstOrDefault();
        }
    }

    /// <summary>
    /// Картинки, которые можно разобрать без импорта: PNG и JPEG — средствами
    /// Unity, TGA — своим декодером. Настройки импорта не применяются:
    /// показываются исходные пиксели, а сами настройки — списком изменений.
    /// </summary>
    internal sealed class ImageAssetLoader : AssetLoader
    {
        public override bool PreferFileForWorktree { get { return true; } }

        public override bool CanLoad(string projectPath)
        {
            return AssetLoaders.HasExtension(projectPath, ".png", ".jpg", ".jpeg", ".tga");
        }

        public override LoadedAsset Load(string projectPath, byte[] bytes)
        {
            Texture2D texture;

            if (AssetLoaders.HasExtension(projectPath, ".tga"))
            {
                int width, height;
                byte[] rgba;
                string error;
                if (!TgaDecoder.TryDecode(bytes, out width, out height, out rgba, out error))
                    return LoadedAsset.Failed(L.F("TGA not parsed: {0}.", error));

                texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                texture.LoadRawTextureData(rgba);
                texture.Apply(false);
            }
            else
            {
                // Проверка сигнатуры обязательна ДО LoadImage: на чужих байтах тот
                // пишет ошибку в консоль сам, и перехватить её нельзя.
                if (!LooksLikeImage(bytes))
                    return LoadedAsset.Failed(L.T("The version was not parsed: it is neither PNG nor JPEG."));

                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                if (!texture.LoadImage(bytes))
                {
                    Object.DestroyImmediate(texture);
                    return LoadedAsset.Failed(L.T("The version was not parsed."));
                }
            }

            texture.name = Path.GetFileNameWithoutExtension(projectPath);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;

            var size = texture.width + "×" + texture.height;
            var loaded = new LoadedAsset
            {
                Main = texture,
                All = new Object[] { texture },
                Info = size + " · " + PreviewText.Bytes(bytes.Length)
            };

            loaded.AddFact(L.M("Size"), size);
            loaded.AddFact(L.M("File"), PreviewText.Bytes(bytes.Length));
            return loaded;
        }

        private static bool LooksLikeImage(byte[] b)
        {
            if (b == null || b.Length < 8) return false;

            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
                b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return true;

            return b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;
        }
    }

    /// <summary>Волна звука, посчитанная один раз при загрузке: минимумы и максимумы по отрезкам.</summary>
    internal sealed class AudioSummary
    {
        public float[] Min, Max;
        public float Length;
        public int Frequency, Channels, Samples;
        public float Peak;
    }

    /// <summary>
    /// Звук. Unity декодирует его только из файла, поэтому версия пишется во
    /// временный файл и читается UnityWebRequest — проверено «Проверкой превью».
    /// Клип получается распакованным: из него можно и нарисовать волну, и
    /// проиграть его.
    /// </summary>
    internal sealed class AudioAssetLoader : AssetLoader
    {
        private const int Buckets = 2048;

        public override bool PreferFileForWorktree { get { return true; } }

        public override bool CanLoad(string projectPath)
        {
            return AssetLoaders.HasExtension(projectPath, ".wav", ".ogg", ".mp3", ".aif", ".aiff");
        }

        public override LoadedAsset Load(string projectPath, byte[] bytes)
        {
            return LoadedAsset.Failed(L.T("Audio only loads asynchronously."));
        }

        public override async Task<LoadedAsset> LoadAsync(string projectPath, byte[] bytes)
        {
            var file = AssetLoaders.TempFile(projectPath);

            try
            {
                File.WriteAllBytes(file, bytes);

                using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, TypeOf(projectPath)))
                {
                    ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = false;

                    var operation = request.SendWebRequest();
                    if (!await EditorWait.Until(() => operation.isDone, 30000))
                        return LoadedAsset.Failed(L.T("The audio did not load within 30 seconds."));

                    if (request.result != UnityWebRequest.Result.Success)
                        return LoadedAsset.Failed(L.F("The audio was not parsed: {0}", request.error));

                    var clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip == null) return LoadedAsset.Failed(L.T("The audio was not parsed."));

                    clip.hideFlags = HideFlags.HideAndDontSave;
                    clip.name = Path.GetFileNameWithoutExtension(projectPath);

                    var summary = Summarize(clip);
                    var loaded = new LoadedAsset
                    {
                        Main = clip,
                        All = new Object[] { clip },
                        Data = summary,
                        Info = Duration(clip.length) + " · " + PreviewText.Bytes(bytes.Length)
                    };

                    loaded.AddFact(L.M("Duration"), Duration(clip.length));
                    loaded.AddFact(L.M("Frequency"), L.F("{0} Hz", clip.frequency));
                    loaded.AddFact(L.M("Channels"), clip.channels.ToString());
                    if (summary.Min != null)
                        loaded.AddFact(L.M("Peak Level"), summary.Peak > 0f
                            ? (20f * Mathf.Log10(summary.Peak)).ToString("0.0") + " dB"
                            : L.T("silence"));
                    loaded.AddFact(L.M("File"), PreviewText.Bytes(bytes.Length));

                    if (summary.Min == null) loaded.Notes.Add(L.T("samples cannot be read — no waveform"));
                    return loaded;
                }
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }

        private static AudioType TypeOf(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".wav": return AudioType.WAV;
                case ".ogg": return AudioType.OGGVORBIS;
                case ".mp3": return AudioType.MPEG;
                default: return AudioType.AIFF;
            }
        }

        private static AudioSummary Summarize(AudioClip clip)
        {
            var summary = new AudioSummary
            {
                Length = clip.length,
                Frequency = clip.frequency,
                Channels = clip.channels,
                Samples = clip.samples
            };

            var data = new float[Math.Max(1, clip.samples * clip.channels)];
            if (!clip.GetData(data, 0)) return summary;

            summary.Min = new float[Buckets];
            summary.Max = new float[Buckets];

            int perBucket = Math.Max(1, data.Length / Buckets);
            for (int b = 0; b < Buckets; b++)
            {
                int start = b * data.Length / Buckets;
                int end = Math.Min(data.Length, start + perBucket);
                float min = 0f, max = 0f;

                for (int i = start; i < end; i++)
                {
                    float v = data[i];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }

                summary.Min[b] = min;
                summary.Max[b] = max;
                summary.Peak = Mathf.Max(summary.Peak, Mathf.Max(-min, max));
            }

            return summary;
        }

        public static string Duration(float seconds)
        {
            int minutes = (int)(seconds / 60f);
            return minutes + ":" + (seconds - minutes * 60f).ToString("00.00", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
