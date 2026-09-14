using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lev.Git.Preview
{
    internal enum SideKind { Worktree, Head, Commit }

    /// <summary>
    /// Где лежит одна сторона сравнения: рабочая копия, последний коммит или
    /// любой коммит. Сама ничего не загружает — только знает, откуда брать байты
    /// и чем эта версия отличается от других для кэша.
    /// </summary>
    internal sealed class RevisionSide
    {
        public SideKind Kind;
        public string ProjectPath;
        public string GitPath;
        public string Sha;
        public string Caption;

        public static RevisionSide Worktree(string projectPath, string caption)
        {
            return new RevisionSide { Kind = SideKind.Worktree, ProjectPath = projectPath, GitPath = GitRepository.ToGitPath(projectPath), Caption = caption };
        }

        public static RevisionSide Head(string gitPath, string caption)
        {
            return new RevisionSide { Kind = SideKind.Head, GitPath = gitPath, Caption = caption };
        }

        public static RevisionSide Commit(string sha, string gitPath, string caption)
        {
            return new RevisionSide { Kind = SideKind.Commit, Sha = sha, GitPath = gitPath, Caption = caption };
        }

        /// <summary>
        /// Что делает загруженную версию устаревшей. У рабочей копии отпечаток
        /// файла сюда не входит: её сторона — живой ассет проекта, он и так
        /// всегда текущий, перечитывать нужно только список изменений.
        /// </summary>
        public string Key
        {
            get
            {
                switch (Kind)
                {
                    case SideKind.Worktree: return "wt|" + ProjectPath;
                    case SideKind.Head: return "head|" + (GitStatusCache.HeadOid ?? string.Empty) + "|" + GitPath;
                    default: return "c|" + Sha + "|" + GitPath;
                }
            }
        }

        /// <summary>Текст .meta этой версии. null — меты нет или прочитать не удалось.</summary>
        public async Task<string> ReadMetaAsync()
        {
            if (string.IsNullOrEmpty(GitPath) && Kind != SideKind.Worktree) return null;

            byte[] bytes;
            switch (Kind)
            {
                case SideKind.Head:
                    bytes = await GitOperations.ShowHeadBytesAsync(GitPath + ".meta");
                    break;
                case SideKind.Commit:
                    bytes = await GitHistory.ShowBytesAsync(Sha, GitPath + ".meta");
                    break;
                default:
                    bytes = ReadWorktreeMeta(ProjectPath);
                    break;
            }

            return bytes != null ? Encoding.UTF8.GetString(bytes) : null;
        }

        public static byte[] ReadWorktreeMeta(string projectPath)
        {
            try
            {
                var full = Path.Combine(GitRepository.ProjectRoot, projectPath + ".meta");
                return File.Exists(full) ? File.ReadAllBytes(full) : null;
            }
            catch
            {
                return null;
            }
        }

        private static readonly Dictionary<string, string> Oids = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Объект git этой версии — ключ кэша. У рабочей копии его нет: она живая.
        /// Ответ запоминается: содержимое коммита не меняется никогда, а HEAD в
        /// ключе записан своим хешем, а не именем.
        /// </summary>
        public async Task<string> ResolveOidAsync()
        {
            if (Kind == SideKind.Worktree || string.IsNullOrEmpty(GitPath)) return null;

            var rev = Kind == SideKind.Head ? GitStatusCache.HeadOid : Sha;
            if (string.IsNullOrEmpty(rev)) return null;

            var spec = rev + ":" + GitPath;
            string oid;
            if (Oids.TryGetValue(spec, out oid)) return oid;

            // Объекты ассета и его меты — одним вызовом: версия в кэше зависит от
            // обоих, у текстуры нарезка и настройки живут в мете.
            var result = await GitOperations.Git("ls-tree -z " + GitOperations.Q(rev) + " -- " +
                                                 GitOperations.Q(GitPath) + " " + GitOperations.Q(GitPath + ".meta"));
            if (!result.Ok) return null;

            string asset = null, meta = null;
            foreach (var entry in result.StdOut.Split('\0'))
            {
                int tab = entry.IndexOf('\t');
                if (tab < 0) continue;

                var parts = entry.Substring(0, tab).Split(' ');
                if (parts.Length < 3) continue;

                var path = entry.Substring(tab + 1);
                if (path == GitPath) asset = parts[2];
                else if (path == GitPath + ".meta") meta = parts[2];
            }

            if (string.IsNullOrEmpty(asset)) return null;
            oid = asset + "+" + (meta ?? "-");

            if (Oids.Count > 5000) Oids.Clear();
            Oids[spec] = oid;
            return oid;
        }

        /// <summary>Байты версии с развёрнутым указателем LFS. null — прочитать не удалось.</summary>
        public async Task<byte[]> ReadAsync(Action onLfs)
        {
            byte[] bytes;

            switch (Kind)
            {
                case SideKind.Head:
                    bytes = await GitOperations.ShowHeadBytesAsync(GitPath);
                    break;
                case SideKind.Commit:
                    bytes = await GitHistory.ShowBytesAsync(Sha, GitPath);
                    break;
                default:
                    try { bytes = File.ReadAllBytes(Path.Combine(GitRepository.ProjectRoot, ProjectPath)); }
                    catch { bytes = null; }
                    break;
            }

            if (GitOperations.IsLfsPointer(bytes))
            {
                if (onLfs != null) onLfs();
                bytes = await GitOperations.SmudgeLfsAsync(bytes, GitPath);
            }

            return bytes;
        }
    }

    /// <summary>
    /// Одна сторона сравнения, превращённая в объекты Unity.
    ///
    /// Два происхождения. Версия из git — временные скрытые объекты, которые мы
    /// обязаны уничтожить. Рабочая копия — сам ассет проекта: его не уничтожают,
    /// зато его можно править, и откат пишет прямо в него.
    /// </summary>
    public sealed class LoadedAsset : IDisposable
    {
        public Object Main;
        public Object[] All = new Object[0];

        /// <summary>Сторона — живой ассет проекта: редактируется и не уничтожается.</summary>
        public bool Live { get; internal set; }

        /// <summary>Есть ли смысл описывать изменения по сериализованным полям.</summary>
        public bool Describable;

        public string Error;
        public string Info;
        public readonly List<string> Notes = new List<string>();

        /// <summary>Флаги объектов из файла: у загруженных мы их затираем, а по ним видно служебные объекты.</summary>
        internal readonly Dictionary<Object, HideFlags> SourceFlags = new Dictionary<Object, HideFlags>();

        /// <summary>Локальные идентификаторы объектов в файле версии — к ним цепляются комментарии.</summary>
        internal readonly Dictionary<Object, long> FileIds = new Dictionary<Object, long>();

        internal long FileIdOf(Object o)
        {
            if (o == null) return 0;

            long id;
            if (FileIds.TryGetValue(o, out id)) return id;

            string guid;
            if (Live && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out guid, out id)) return id;
            return 0;
        }

        /// <summary>Текст .meta этой версии — настройки импорта и нарезка. null — меты нет.</summary>
        public string MetaText;

        /// <summary>Сведения о файле для списка изменений, по порядку: «Размер» → «512×512».</summary>
        public readonly List<KeyValuePair<string, string>> Facts = new List<KeyValuePair<string, string>>();

        /// <summary>Данные для показа, собранные загрузчиком: волна звука и подобное.</summary>
        public object Data;

        public void AddFact(string label, string value)
        {
            Facts.Add(new KeyValuePair<string, string>(label, value));
        }

        /// <summary>Версию можно собрать только временным импортом, и на него нужно согласие.</summary>
        internal bool NeedsImport;

        /// <summary>Что сделать при уничтожении версии сверх её объектов: закрыть сцену, удалить папку импорта.</summary>
        internal Action DisposeAction;

        /// <summary>Версии ассетов, на которые переведены ссылки этой версии, — «зависимости из того же коммита».</summary>
        internal readonly List<LoadedAsset> Dependencies = new List<LoadedAsset>();

        private bool _disposed;

        private static readonly HashSet<LoadedAsset> Alive = new HashSet<LoadedAsset>();

        static LoadedAsset()
        {
            // Скрытые объекты пережили бы перезагрузку домена, но ссылок на них
            // уже ни у кого не будет — убираем их, пока ссылки ещё есть.
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                foreach (var loaded in new List<LoadedAsset>(Alive)) loaded.Dispose();
            };
        }

        public LoadedAsset()
        {
            Alive.Add(this);
        }

        public static LoadedAsset Failed(string error)
        {
            return new LoadedAsset { Error = error };
        }

        internal static LoadedAsset FromProject(string projectPath)
        {
            var all = AssetDatabase.LoadAllAssetsAtPath(projectPath) ?? new Object[0];
            var main = AssetDatabase.LoadMainAssetAtPath(projectPath);
            if (main == null && all.Length > 0) main = all[0];

            if (main == null) return Failed(L.T("The asset is not loaded in the project."));

            var loaded = new LoadedAsset
            {
                Main = main,
                All = all,
                Live = true,
                Describable = AssetYaml.IsYamlAssetPath(projectPath)
            };

            try
            {
                var full = Path.Combine(GitRepository.ProjectRoot, projectPath);
                if (File.Exists(full)) loaded.Info = PreviewText.Bytes(new FileInfo(full).Length);
            }
            catch { }

            var texture = main as Texture;
            if (texture != null) loaded.Info = texture.width + "×" + texture.height + (loaded.Info != null ? " · " + loaded.Info : string.Empty);

            ModelFacts.AddFor(loaded);
            return loaded;
        }

        internal HideFlags FlagsOf(Object o)
        {
            HideFlags flags;
            return SourceFlags.TryGetValue(o, out flags) ? flags : o.hideFlags;
        }

        /// <summary>Объект, который Unity не смогла собрать: его класса в этой версии нет.</summary>
        public static bool IsMissingClass(Object o)
        {
            return o != null && o.GetType().Name == "FallbackEditorWindow";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Alive.Remove(this);

            var disposable = Data as IDisposable;
            if (disposable != null) disposable.Dispose();
            Data = null;

            foreach (var dependency in Dependencies) dependency.Dispose();
            Dependencies.Clear();

            if (Live) return;

            var action = DisposeAction;
            DisposeAction = null;
            if (action != null)
            {
                try { action(); }
                catch (Exception e) { Diagnostics.Journal.Warn(L.F("Preview version was not fully disposed: {0}", e.Message)); }
            }

            // Ассеты временного импорта уничтожать нельзя — их убирает удаление папки.
            foreach (var o in All)
                if (o != null && !EditorUtility.IsPersistent(o)) Object.DestroyImmediate(o);

            if (Main != null && !EditorUtility.IsPersistent(Main)) Object.DestroyImmediate(Main);
            Main = null;
            All = new Object[0];
        }
    }

    public enum ChangeKind { Modified, Added, Removed }

    public enum ValueKind { None, Text, Color, Object }

    /// <summary>Значение одной стороны изменения — уже для показа, но с типом: цвет рисуется образцом.</summary>
    public struct ChangeValue
    {
        public ValueKind Kind;
        public string Text;
        public Color Color;
        public Object Ref;

        public static ChangeValue None
        {
            get { return new ChangeValue { Kind = ValueKind.None, Text = "—" }; }
        }

        public static ChangeValue Of(string text)
        {
            return new ChangeValue { Kind = ValueKind.Text, Text = text ?? string.Empty };
        }

        public static ChangeValue Of(Color c)
        {
            var hex = "#" + (c.a < 0.999f ? ColorUtility.ToHtmlStringRGBA(c) : ColorUtility.ToHtmlStringRGB(c));
            return new ChangeValue { Kind = ValueKind.Color, Color = c, Text = hex };
        }

        public static ChangeValue Of(Object o)
        {
            return new ChangeValue { Kind = ValueKind.Object, Ref = o, Text = o != null ? o.name : "None" };
        }

        public static ChangeValue Of(bool b)
        {
            return Of(b ? L.T("on") : L.T("off"));
        }

        public static ChangeValue Of(float f)
        {
            return Of(PreviewText.Number(f));
        }

        /// <summary>Значение сериализованного поля так, как его показал бы инспектор.</summary>
        public static ChangeValue From(SerializedProperty p)
        {
            try
            {
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Integer: return Of(p.longValue.ToString(CultureInfo.InvariantCulture));
                    case SerializedPropertyType.Boolean: return Of(p.boolValue);
                    case SerializedPropertyType.Float: return Of(PreviewText.Number(p.doubleValue));
                    case SerializedPropertyType.String: return Of(p.stringValue.Length == 0 ? L.T("“”") : p.stringValue);
                    case SerializedPropertyType.Color: return Of(p.colorValue);
                    case SerializedPropertyType.ObjectReference: return Of(p.objectReferenceValue);
                    case SerializedPropertyType.ArraySize: return Of(p.intValue.ToString(CultureInfo.InvariantCulture));
                    case SerializedPropertyType.Character: return Of(((char)p.intValue).ToString());
                    case SerializedPropertyType.LayerMask: return Of(p.intValue.ToString(CultureInfo.InvariantCulture));

                    case SerializedPropertyType.Enum:
                    {
                        var names = p.enumDisplayNames;
                        int index = p.enumValueIndex;
                        return Of(index >= 0 && index < names.Length ? names[index] : p.intValue.ToString(CultureInfo.InvariantCulture));
                    }

                    case SerializedPropertyType.Vector2: { var v = p.vector2Value; return Of(PreviewText.Vector(v.x, v.y)); }
                    case SerializedPropertyType.Vector3: { var v = p.vector3Value; return Of(PreviewText.Vector(v.x, v.y, v.z)); }
                    case SerializedPropertyType.Vector4: { var v = p.vector4Value; return Of(PreviewText.Vector(v.x, v.y, v.z, v.w)); }
                    case SerializedPropertyType.Vector2Int: { var v = p.vector2IntValue; return Of(PreviewText.Vector(v.x, v.y)); }
                    case SerializedPropertyType.Vector3Int: { var v = p.vector3IntValue; return Of(PreviewText.Vector(v.x, v.y, v.z)); }

                    case SerializedPropertyType.Quaternion:
                    {
                        var e = p.quaternionValue.eulerAngles;
                        return Of(PreviewText.Vector(e.x, e.y, e.z) + "°");
                    }

                    case SerializedPropertyType.Rect: { var r = p.rectValue; return Of(PreviewText.Vector(r.x, r.y, r.width, r.height)); }
                    case SerializedPropertyType.RectInt: { var r = p.rectIntValue; return Of(PreviewText.Vector(r.x, r.y, r.width, r.height)); }

                    case SerializedPropertyType.Bounds:
                    {
                        var b = p.boundsValue;
                        return Of(L.F("center {0}, size {1}", PreviewText.Vector(b.center.x, b.center.y, b.center.z),
                                  PreviewText.Vector(b.size.x, b.size.y, b.size.z)));
                    }

                    case SerializedPropertyType.AnimationCurve:
                    {
                        var curve = p.animationCurveValue;
                        return Of(L.F("curve, keys: {0}", curve != null ? curve.length : 0));
                    }

                    case SerializedPropertyType.Gradient: return Of(L.T("gradient"));
                    case SerializedPropertyType.Hash128: return Of(p.hash128Value.ToString());
                    case SerializedPropertyType.ManagedReference: return Of(p.managedReferenceFullTypename);
                    case SerializedPropertyType.ExposedReference: return Of(p.exposedReferenceValue);
                    default: return Of("…");
                }
            }
            catch
            {
                return Of("…");
            }
        }
    }

    /// <summary>
    /// Одно изменение словами инспектора. Сколько бы строк YAML за ним ни стояло,
    /// показывается одной строкой; пути сохраняются, чтобы откат и комментарии
    /// работали с группой так же, как раньше с одним полем.
    /// </summary>
    public sealed class ChangeItem
    {
        /// <summary>
        /// Что изменилось, одной строкой: имя свойства шейдера или путь поля.
        /// По нему инспектор «Поля» находит строку, которую надо отметить.
        /// </summary>
        public string Key;

        public string Label;
        public string Group;
        public string Tooltip;
        public ChangeKind Kind;
        public ChangeValue Before = ChangeValue.None;
        public ChangeValue After = ChangeValue.None;

        /// <summary>Сериализованные пути: по ним работает откат, если у изменения нет своего.</summary>
        public readonly List<string> SerializedPaths = new List<string>();

        /// <summary>Те же пути, как они записаны в файле.</summary>
        public readonly List<string> YamlPaths = new List<string>();

        /// <summary>Показывать только по «показать скрытые».</summary>
        public bool Folded;
        public string Note;

        /// <summary>Объекты сторон, к которым относится изменение: главный ассет или его подобъект.</summary>
        public Object BeforeTarget, AfterTarget;

        /// <summary>Свой откат — когда путь поля ничего не говорит, а у типа есть API: SetColor, EnableKeyword.</summary>
        public Action<Object, Object> Revert;

        /// <summary>Объект «было» → его пара в «стало»: ссылки на подобъекты при откате ведут сюда.</summary>
        internal Dictionary<Object, Object> Pairs;

        public void AddPath(string serializedPath)
        {
            if (Key == null) Key = serializedPath;
            SerializedPaths.Add(serializedPath);
            YamlPaths.Add(AssetYaml.ToYamlPath(serializedPath));
        }
    }

    [Flags]
    public enum CompareModes
    {
        None = 0,
        SideBySide = 1,
        Swipe = 2,
        Toggle = 4,
        Overlay = 8,
        Difference = 16,
        All = SideBySide | Swipe | Toggle | Overlay | Difference
    }

    /// <summary>Что умеет показ сверх картинки — по этому панель решает, какие инструменты показать.</summary>
    [Flags]
    public enum PresenterFeatures
    {
        None = 0,
        Zoom = 1,
        Channels = 2,
        Slices = 4,
        Playback = 8
    }

    /// <summary>Общее состояние сторон: повернул, увеличил, сдвинул одну — то же со всеми.</summary>
    public sealed class PreviewSync
    {
        public Vector2 Orbit = new Vector2(-25f, -15f);
        public float Swipe = 0.5f;
        public int Shape;
        public bool ShowBefore;

        /// <summary>Увеличение: 1 — вписано целиком.</summary>
        public float Zoom = 1f;

        /// <summary>Центр видимой области в UV, начало внизу слева.</summary>
        public Vector2 Pan = new Vector2(0.5f, 0.5f);

        /// <summary>Прозрачность новой версии в режиме наложения.</summary>
        public float Opacity = 0.5f;

        /// <summary>Порог маски разницы: отличие меньше — не отмечается.</summary>
        public float Threshold = 0.02f;

        /// <summary>0 — RGBA, 1 — R, 2 — G, 3 — B, 4 — альфа.</summary>
        public int Channel;

        public bool ShowSlices = true;

        /// <summary>
        /// Отрисовка вне окна — для миниатюр. Показы тогда рисуют через
        /// статичное превью, которому не нужен контекст IMGUI, и отдают свою текстуру.
        /// </summary>
        public bool Static;

        /// <summary>Приближение камеры 3D-превью: меньше 1 — ближе, больше — дальше.</summary>
        public float Dolly = 1f;

        /// <summary>Сдвиг камеры 3D-превью в долях высоты кадра.</summary>
        public Vector2 Offset;

        /// <summary>Выбранный вариант показа: кривая клипа, слой аниматора.</summary>
        public int Choice;
    }

    public static class PreviewText
    {
        public static string Bytes(long bytes)
        {
            if (bytes <= 0) return "—";
            if (bytes < 1024) return L.F("{0} B", bytes);
            if (bytes < 1024 * 1024) return L.F("{0} KB", (bytes / 1024f).ToString("0.#", CultureInfo.CurrentCulture));
            return L.F("{0} MB", (bytes / 1024f / 1024f).ToString("0.#", CultureInfo.CurrentCulture));
        }

        public static string Number(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static string Vector(params float[] parts)
        {
            var text = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++) text[i] = Number(parts[i]);
            return "(" + string.Join(", ", text) + ")";
        }
    }
}
