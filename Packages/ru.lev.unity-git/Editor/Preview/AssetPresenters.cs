using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lev.Git.Preview
{
    /// <summary>
    /// Рисует одну сторону сравнения. Раскладку сторон — рядом, шторкой,
    /// наложением, разницей — увеличение и общее вращение делает панель: показ
    /// отдаёт картинку и говорит, что умеет, поэтому любой режим работает с
    /// любым типом.
    /// </summary>
    public abstract class AssetPresenter : IDisposable
    {
        public virtual int Priority { get { return 0; } }

        public abstract Type Target { get; }

        public virtual CompareModes Modes { get { return CompareModes.All; } }

        public virtual PresenterFeatures Features { get { return PresenterFeatures.None; } }

        /// <summary>Сторону можно вращать мышью — панель передаёт перетаскивание в PreviewSync.Orbit.</summary>
        public virtual bool Interactive { get { return false; } }

        /// <summary>Варианты формы превью (шар, куб…); null — выбора нет.</summary>
        public virtual string[] Shapes { get { return null; } }

        /// <summary>Картинка ещё строится или идёт воспроизведение — панели стоит перерисоваться.</summary>
        public virtual bool NeedsRepaint { get { return false; } }

        /// <summary>Стороны «рядом» — одна под другой: так удобнее волне звука.</summary>
        public virtual bool StackSides { get { return false; } }

        /// <summary>Стороны одна под другой при этом состоянии: у клипа — для кривых, но не для модели.</summary>
        public virtual bool Stack(PreviewSync sync)
        {
            return StackSides;
        }

        /// <summary>Обе стороны загружены — время посчитать то, что общее для них: шкалу, диапазон, список вариантов.</summary>
        public virtual void Prepare(LoadedAsset before, LoadedAsset after)
        {
        }

        /// <summary>Варианты показа на выбор (кривая клипа, слой аниматора); null — выбирать нечего.</summary>
        public virtual string[] Choices { get { return null; } }

        public virtual int DefaultChoice { get { return 0; } }

        public virtual bool ChoicesVisible(PreviewSync sync)
        {
            var choices = Choices;
            return choices != null && choices.Length > 0;
        }

        /// <summary>Воспроизведение сейчас возможно — у звука это зависит от внутреннего API редактора.</summary>
        public virtual bool PlaybackAvailable { get { return true; } }

        /// <summary>Играет одна сторона (звук) или обе вместе на общем времени (анимация, Timeline).</summary>
        public virtual bool PlaybackPerSide { get { return true; } }

        public virtual bool CanPresent(LoadedAsset side)
        {
            return side != null && side.Main != null && Target.IsInstanceOfType(side.Main);
        }

        /// <summary>Ширина к высоте картинки стороны; 0 — картинка заполняет всю ячейку.</summary>
        public virtual float Aspect(LoadedAsset side)
        {
            return 0f;
        }

        /// <summary>Вызывается только на Repaint. slot — номер стороны: у каждой свой буфер отрисовки.</summary>
        public abstract Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync);

        /// <summary>Есть ли что нарисовать поверх картинки — например, нарезка спрайтов.</summary>
        public virtual bool HasOverlay(LoadedAsset side)
        {
            return false;
        }

        /// <param name="fit">Где на экране нарисована картинка.</param>
        /// <param name="source">Какая её часть видна, в UV с началом внизу слева.</param>
        public virtual void DrawOverlay(Rect fit, Rect source, LoadedAsset side, LoadedAsset other, bool isAfter, PreviewSync sync)
        {
        }

        // ---- воспроизведение ----

        public virtual float Length(LoadedAsset side) { return 0f; }
        public virtual void Play(LoadedAsset side, int slot, float time) { }
        public virtual void Stop() { }

        /// <summary>Перемотка без запуска: играло — продолжит с нового места, стояло — останется стоять.</summary>
        public virtual void Seek(LoadedAsset side, int slot, float time) { }

        /// <summary>Часть кадра, по ширине которой идёт шкала времени; у Timeline слева колонка с именами дорожек.</summary>
        public virtual Rect TimeArea(Rect fit)
        {
            return fit;
        }
        public virtual bool Playing { get { return false; } }
        public virtual int PlayingSlot { get { return -1; } }
        public virtual float PlayTime { get { return 0f; } }

        public virtual void Dispose()
        {
        }
    }

    internal static class AssetPresenters
    {
        private static List<Type> _types;

        /// <summary>Новый показ для типа главного объекта; null — показать нечем.</summary>
        public static AssetPresenter Create(LoadedAsset sample)
        {
            if (sample == null || sample.Main == null) return null;

            if (_types == null)
                _types = TypeCache.GetTypesDerivedFrom<AssetPresenter>()
                    .Where(t => !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                    .ToList();

            var type = sample.Main.GetType();
            var candidates = new List<AssetPresenter>();

            foreach (var t in _types)
            {
                try
                {
                    var presenter = (AssetPresenter)Activator.CreateInstance(t);
                    if (presenter.Target.IsAssignableFrom(type)) candidates.Add(presenter);
                    else presenter.Dispose();
                }
                catch (Exception e)
                {
                    Diagnostics.Journal.Warn(L.F("Preview presenter {0} was not created: {1}", t.Name, e.Message));
                }
            }

            AssetPresenter chosen = null;
            foreach (var p in candidates.OrderByDescending(c => ChangeDescribers.Depth(c.Target)).ThenByDescending(c => c.Priority))
            {
                if (chosen == null && p.CanPresent(sample)) chosen = p;
                else p.Dispose();
            }

            return chosen;
        }
    }

    /// <summary>
    /// Отрисовка картинки стороны: вписывание, увеличение, каналы, маска разницы.
    ///
    /// Каналы и разница считаются маленьким шейдером, собранным из текста при
    /// первом обращении. Не собрался — картинки рисуются как есть, а панель
    /// прячет «Разницу» и каналы.
    /// </summary>
    public static class PreviewCanvas
    {
        private const string ShaderSource =
            "Shader \"Hidden/LevGit/PreviewBlit\"\n" +
            "{\n" +
            "    Properties { _MainTex (\"\", 2D) = \"white\" {} _OtherTex (\"\", 2D) = \"black\" {} }\n" +
            "    SubShader\n" +
            "    {\n" +
            "        Tags { \"Queue\" = \"Transparent\" }\n" +
            "        Cull Off ZWrite Off ZTest Always\n" +
            "        Blend SrcAlpha OneMinusSrcAlpha\n" +
            "        Pass\n" +
            "        {\n" +
            "            CGPROGRAM\n" +
            "            #pragma vertex vert\n" +
            "            #pragma fragment frag\n" +
            "            #include \"UnityCG.cginc\"\n" +
            "            sampler2D _MainTex;\n" +
            "            sampler2D _OtherTex;\n" +
            "            sampler2D _GUIClipTexture;\n" +
            "            uniform float4x4 unity_GUIClipTextureMatrix;\n" +
            "            float4 _Channels;\n" +
            "            float _Mode;\n" +
            "            float _Threshold;\n" +
            "            float _Opacity;\n" +
            "            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 clipUV : TEXCOORD1; };\n" +
            "            v2f vert (appdata_img v)\n" +
            "            {\n" +
            "                v2f o;\n" +
            "                o.pos = UnityObjectToClipPos(v.vertex);\n" +
            "                o.uv = v.texcoord;\n" +
            "                float3 eyePos = UnityObjectToViewPos(v.vertex);\n" +
            "                o.clipUV = mul(unity_GUIClipTextureMatrix, float4(eyePos.xy, 0, 1.0)).xy;\n" +
            "                return o;\n" +
            "            }\n" +
            "            fixed4 frag (v2f i) : SV_Target\n" +
            "            {\n" +
            "                float clip = tex2D(_GUIClipTexture, i.clipUV).a;\n" +
            "                fixed4 a = tex2D(_MainTex, i.uv);\n" +
            "                if (_Mode > 1.5)\n" +
            "                {\n" +
            "                    fixed4 b = tex2D(_OtherTex, i.uv);\n" +
            "                    fixed4 d = abs(a - b);\n" +
            "                    float m = max(max(d.r, d.g), max(d.b, d.a));\n" +
            "                    float hit = step(_Threshold, m);\n" +
            "                    float gray = dot(a.rgb, float3(0.299, 0.587, 0.114)) * 0.3;\n" +
            "                    fixed3 c = lerp(gray.xxx, fixed3(1.0, 0.25, 0.45), hit * saturate(0.4 + m * 3.0));\n" +
            "                    return fixed4(c, clip);\n" +
            "                }\n" +
            "                float count = _Channels.r + _Channels.g + _Channels.b;\n" +
            "                if (_Channels.a > 0.5 && count < 0.5) return fixed4(a.aaa, _Opacity * clip);\n" +
            "                if (count < 1.5) return fixed4(dot(a.rgb, _Channels.rgb).xxx, _Opacity * clip);\n" +
            "                return fixed4(a.rgb, a.a * _Opacity * clip);\n" +
            "            }\n" +
            "            ENDCG\n" +
            "        }\n" +
            "    }\n" +
            "}\n";

        private static Material _material;
        private static bool _tried;
        private static Texture2D _checker;

        private static readonly int ModeId = Shader.PropertyToID("_Mode");
        private static readonly int OtherId = Shader.PropertyToID("_OtherTex");
        private static readonly int ChannelsId = Shader.PropertyToID("_Channels");
        private static readonly int ThresholdId = Shader.PropertyToID("_Threshold");
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");

        private static Material Material
        {
            get
            {
                if (_tried) return _material;
                _tried = true;

                try
                {
                    var shader = ShaderUtil.CreateShaderAsset(ShaderSource);
                    if (shader != null && shader.isSupported && !ShaderUtil.ShaderHasError(shader))
                    {
                        shader.hideFlags = HideFlags.HideAndDontSave;
                        _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    }
                    else if (shader != null)
                    {
                        Object.DestroyImmediate(shader);
                    }
                }
                catch (Exception e)
                {
                    Diagnostics.Journal.Warn(L.F("Preview shader was not compiled — channels and the difference mask are unavailable: {0}", e.Message));
                }

                return _material;
            }
        }

        /// <summary>Доступны ли каналы и маска разницы.</summary>
        internal static bool Advanced
        {
            get { return Material != null; }
        }

        private static Texture2D _blank;

        /// <summary>Прозрачная точка — для показов, которые рисуют всё сами поверх ячейки.</summary>
        public static Texture2D Blank
        {
            get
            {
                if (_blank == null)
                {
                    _blank = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                    _blank.SetPixel(0, 0, Color.clear);
                    _blank.Apply(false);
                }
                return _blank;
            }
        }

        /// <summary>Сплошной цвет фона превью из настроек проекта — для показов, рисующих свою сцену.</summary>
        public static Color BackgroundColor
        {
            get { return PreviewBackground.SolidColor; }
        }

        public static Rect Fit(Rect cell, float aspect)
        {
            if (aspect <= 0f || cell.width <= 0f || cell.height <= 0f) return cell;

            float width = cell.width, height = cell.width / aspect;
            if (height > cell.height)
            {
                height = cell.height;
                width = cell.height * aspect;
            }

            return new Rect(cell.x + (cell.width - width) * 0.5f, cell.y + (cell.height - height) * 0.5f, width, height);
        }

        /// <summary>Видимая часть картинки в UV при общем увеличении.</summary>
        public static Rect Source(PreviewSync sync, bool zoom)
        {
            if (!zoom || sync.Zoom <= 1.001f) return new Rect(0f, 0f, 1f, 1f);

            float size = 1f / sync.Zoom;
            float cx = Mathf.Clamp(sync.Pan.x, size * 0.5f, 1f - size * 0.5f);
            float cy = Mathf.Clamp(sync.Pan.y, size * 0.5f, 1f - size * 0.5f);
            return new Rect(cx - size * 0.5f, cy - size * 0.5f, size, size);
        }

        internal static void Draw(Rect fit, Texture texture, Rect source, PreviewSync sync, bool channels,
                                Texture other, bool difference, float opacity)
        {
            if (texture == null || fit.width < 1f || fit.height < 1f) return;

            var material = Material;
            if (material == null)
            {
                var color = GUI.color;
                GUI.color = new Color(color.r, color.g, color.b, color.a * opacity);
                GUI.DrawTextureWithTexCoords(fit, texture, source);
                GUI.color = color;
                return;
            }

            material.SetFloat(ModeId, difference ? 2f : 0f);
            material.SetTexture(OtherId, other != null ? other : Texture2D.blackTexture);
            material.SetVector(ChannelsId, Mask(channels ? sync.Channel : 0));
            material.SetFloat(ThresholdId, sync.Threshold);
            material.SetFloat(OpacityId, opacity);

            Graphics.DrawTexture(fit, texture, source, 0, 0, 0, 0, material);
        }

        private static Vector4 Mask(int channel)
        {
            switch (channel)
            {
                case 1: return new Vector4(1f, 0f, 0f, 0f);
                case 2: return new Vector4(0f, 1f, 0f, 0f);
                case 3: return new Vector4(0f, 0f, 1f, 0f);
                case 4: return new Vector4(0f, 0f, 0f, 1f);
                default: return new Vector4(1f, 1f, 1f, 1f);
            }
        }

        /// <summary>Шахматная подложка — чтобы прозрачность была видна.</summary>
        public static void Checker(Rect fit)
        {
            if (_checker == null)
            {
                _checker = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Repeat
                };

                var light = EditorGUIUtility.isProSkin ? new Color32(78, 78, 78, 255) : new Color32(220, 220, 220, 255);
                var dark = EditorGUIUtility.isProSkin ? new Color32(58, 58, 58, 255) : new Color32(190, 190, 190, 255);
                _checker.SetPixels32(new[] { light, dark, dark, light });
                _checker.Apply(false);
            }

            GUI.DrawTextureWithTexCoords(fit, _checker, new Rect(0f, 0f, fit.width / 12f, fit.height / 12f));
        }

        public static void Outline(Rect r, Color color, float thickness)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - thickness, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.x, r.y, thickness, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - thickness, r.y, thickness, r.height), color);
        }
    }

    /// <summary>
    /// Текстура: исходные пиксели обеих версий, увеличение с общей лупой,
    /// каналы, маска разницы и нарезка спрайтов из .meta поверх картинки.
    /// </summary>
    internal sealed class ImagePresenter : AssetPresenter
    {
        private readonly Dictionary<LoadedAsset, List<SpriteSlice>> _slices = new Dictionary<LoadedAsset, List<SpriteSlice>>();

        public override Type Target { get { return typeof(Texture2D); } }

        public override PresenterFeatures Features
        {
            get { return PresenterFeatures.Zoom | PresenterFeatures.Channels | PresenterFeatures.Slices; }
        }

        public override float Aspect(LoadedAsset side)
        {
            var texture = side != null ? side.Main as Texture : null;
            return texture != null && texture.height > 0 ? texture.width / (float)texture.height : 1f;
        }

        public override Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync)
        {
            var texture = side.Main as Texture;

            // При сильном увеличении пиксели должны быть квадратами, а не мылом.
            // Трогаем только свои текстуры: у ассета проекта это правка настроек.
            if (texture != null && !EditorUtility.IsPersistent(texture))
            {
                var mode = sync.Zoom >= 4f ? FilterMode.Point : FilterMode.Bilinear;
                if (texture.filterMode != mode) texture.filterMode = mode;
            }

            return texture;
        }

        public override bool HasOverlay(LoadedAsset side)
        {
            return SlicesOf(side).Count > 0;
        }

        private List<SpriteSlice> SlicesOf(LoadedAsset side)
        {
            if (side == null || side.MetaText == null) return new List<SpriteSlice>();

            List<SpriteSlice> slices;
            if (!_slices.TryGetValue(side, out slices))
                _slices[side] = slices = ImporterMeta.Slices(ImporterMeta.Parse(side.MetaText));

            return slices;
        }

        public override void DrawOverlay(Rect fit, Rect source, LoadedAsset side, LoadedAsset other, bool isAfter, PreviewSync sync)
        {
            if (!sync.ShowSlices) return;

            var texture = side.Main as Texture;
            var mine = SlicesOf(side);
            if (texture == null || mine.Count == 0 || texture.width == 0 || texture.height == 0) return;

            var theirs = other != null && other.MetaText != null ? SlicesOf(other) : null;

            GUI.BeginClip(fit);
            try
            {
                foreach (var slice in mine)
                {
                    var status = ImporterMeta.StatusOf(slice, theirs, isAfter);

                    float u0 = slice.X / texture.width, u1 = (slice.X + slice.Width) / texture.width;
                    float v0 = slice.Y / texture.height, v1 = (slice.Y + slice.Height) / texture.height;

                    float x0 = (u0 - source.x) / source.width * fit.width;
                    float x1 = (u1 - source.x) / source.width * fit.width;
                    float yTop = fit.height - (v1 - source.y) / source.height * fit.height;
                    float yBottom = fit.height - (v0 - source.y) / source.height * fit.height;

                    var r = Rect.MinMaxRect(x0, yTop, x1, yBottom);
                    if (r.xMax < 0f || r.x > fit.width || r.yMax < 0f || r.y > fit.height) continue;

                    PreviewCanvas.Outline(r, ColorOf(status), status == SliceStatus.Same ? 1f : 2f);

                    if (status != SliceStatus.Same && r.width > 40f && r.height > 14f)
                        GUI.Label(new Rect(r.x + 3f, r.y + 1f, r.width - 6f, 14f), slice.Name, EditorStyles.miniLabel);
                }
            }
            finally
            {
                GUI.EndClip();
            }
        }

        private static Color ColorOf(SliceStatus status)
        {
            switch (status)
            {
                case SliceStatus.Added: return new Color(0.42f, 0.86f, 0.5f, 0.95f);
                case SliceStatus.Removed: return new Color(0.95f, 0.38f, 0.33f, 0.95f);
                case SliceStatus.Changed: return new Color(0.96f, 0.76f, 0.26f, 0.95f);
                default: return new Color(1f, 1f, 1f, 0.35f);
            }
        }
    }

    /// <summary>
    /// Миниатюра Unity для ассетов, прошлую версию которых пока не собрать:
    /// модели, префабы, PSD. Только для живой стороны — у временных объектов
    /// миниатюр нет.
    /// </summary>
    internal sealed class ThumbnailPresenter : AssetPresenter
    {
        public override int Priority { get { return -100; } }
        public override Type Target { get { return typeof(Object); } }
        public override CompareModes Modes { get { return CompareModes.SideBySide; } }
        public override bool NeedsRepaint { get { return AssetPreview.IsLoadingAssetPreviews(); } }

        public static bool Supports(string projectPath)
        {
            var type = AssetDatabase.GetMainAssetTypeAtPath(projectPath);
            return type != null && IsThumbnailType(type);
        }

        private static bool IsThumbnailType(Type type)
        {
            return typeof(GameObject).IsAssignableFrom(type) || typeof(Mesh).IsAssignableFrom(type) ||
                   typeof(Texture).IsAssignableFrom(type) || typeof(Sprite).IsAssignableFrom(type) ||
                   typeof(AudioClip).IsAssignableFrom(type);
        }

        public override bool CanPresent(LoadedAsset side)
        {
            return side != null && side.Live && side.Main != null && EditorUtility.IsPersistent(side.Main) &&
                   IsThumbnailType(side.Main.GetType());
        }

        public override float Aspect(LoadedAsset side)
        {
            return 1f;
        }

        public override Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync)
        {
            if (side.Main == null) return null;
            return AssetPreview.GetAssetPreview(side.Main) ?? AssetPreview.GetMiniThumbnail(side.Main);
        }
    }

    /// <summary>
    /// Материал на шаре, кубе или плоскости. Своя отрисовка, а не превью
    /// MaterialEditor: камера у сторон должна быть общей, иначе разницу в
    /// бликах не сравнить.
    /// </summary>
    internal sealed class MaterialPresenter : AssetPresenter
    {
        private static readonly string[] MeshNames = { "New-Sphere.fbx", "Cube.fbx", "Quad.fbx" };
        private static readonly Mesh[] Meshes = new Mesh[3];

        private readonly PreviewRenderUtility[] _utilities = new PreviewRenderUtility[4];

        public override Type Target { get { return typeof(Material); } }
        public override bool Interactive { get { return true; } }
        public override string[] Shapes { get { return L.Ts("Sphere", "Cube", "Plane"); } }

        public override Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync)
        {
            var material = side.Main as Material;
            if (material == null || rect.width < 4f || rect.height < 4f) return null;

            slot = Mathf.Clamp(slot, 0, _utilities.Length - 1);
            var utility = _utilities[slot] ?? (_utilities[slot] = new PreviewRenderUtility());

            var mesh = MeshFor(sync.Shape);
            if (mesh == null) return null;

            if (sync.Static) utility.BeginStaticPreview(rect);
            else utility.BeginPreview(rect, GUIStyle.none);

            var camera = utility.camera;
            camera.fieldOfView = 30f;
            float distance = (sync.Shape == 2 ? 3.2f : 4.2f) * Mathf.Max(0.05f, sync.Dolly);
            camera.nearClipPlane = 0.02f;
            camera.farClipPlane = Mathf.Max(20f, distance * 4f);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = PreviewBackground.SolidColor;

            var rotation = Quaternion.Euler(-sync.Orbit.y, -sync.Orbit.x, 0f);
            camera.transform.rotation = rotation;
            camera.transform.position = rotation * new Vector3(0f, 0f, -distance) -
                                        rotation * new Vector3(sync.Offset.x, sync.Offset.y, 0f) * distance * 0.55f;

            utility.lights[0].intensity = 1.1f;
            utility.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
            utility.lights[1].intensity = 0.6f;
            utility.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);
            utility.ambientColor = new Color(0.2f, 0.2f, 0.2f);

            for (int i = 0; i < mesh.subMeshCount; i++)
                utility.DrawMesh(mesh, Matrix4x4.identity, material, i);

            utility.Render(true);
            return sync.Static ? (Texture)utility.EndStaticPreview() : utility.EndPreview();
        }

        private static Mesh MeshFor(int shape)
        {
            shape = Mathf.Clamp(shape, 0, Meshes.Length - 1);

            if (Meshes[shape] == null)
            {
                try { Meshes[shape] = Resources.GetBuiltinResource<Mesh>(MeshNames[shape]); }
                catch { }
            }

            return Meshes[shape] != null ? Meshes[shape] : (shape != 0 ? MeshFor(0) : null);
        }

        public override void Dispose()
        {
            for (int i = 0; i < _utilities.Length; i++)
            {
                if (_utilities[i] == null) continue;
                _utilities[i].Cleanup();
                _utilities[i] = null;
            }
        }
    }

    /// <summary>
    /// Звук: волны версий и прослушивание. Переключение между версиями во время
    /// воспроизведения продолжает с того же места — так разницу слышно сразу.
    /// </summary>
    internal sealed class AudioPresenter : AssetPresenter
    {
        private const int Width = 1024, Height = 256;

        private static readonly Color[] Colors =
        {
            new Color(0.93f, 0.5f, 0.38f, 1f),
            new Color(0.42f, 0.8f, 0.54f, 1f)
        };

        private readonly Dictionary<LoadedAsset, Texture2D>[] _waves =
        {
            new Dictionary<LoadedAsset, Texture2D>(),
            new Dictionary<LoadedAsset, Texture2D>(),
            new Dictionary<LoadedAsset, Texture2D>(),
            new Dictionary<LoadedAsset, Texture2D>()
        };

        private AudioClip _clip;
        private int _slot = -1;
        private float _pausedAt;

        public override Type Target { get { return typeof(AudioClip); } }
        public override CompareModes Modes { get { return CompareModes.SideBySide | CompareModes.Overlay | CompareModes.Toggle; } }
        public override PresenterFeatures Features { get { return PresenterFeatures.Playback; } }
        public override bool StackSides { get { return true; } }
        public override bool NeedsRepaint { get { return Playing; } }
        public override bool PlaybackAvailable { get { return AudioPreview.Available; } }

        public override bool CanPresent(LoadedAsset side)
        {
            var summary = side != null ? side.Data as AudioSummary : null;
            return base.CanPresent(side) && summary != null && summary.Min != null;
        }

        public override Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync)
        {
            slot = Mathf.Clamp(slot, 0, _waves.Length - 1);

            Texture2D texture;
            if (_waves[slot].TryGetValue(side, out texture) && texture != null) return texture;

            var summary = side.Data as AudioSummary;
            if (summary == null || summary.Min == null) return null;

            texture = Waveform(summary, Colors[slot % Colors.Length]);
            _waves[slot][side] = texture;
            return texture;
        }

        private static Texture2D Waveform(AudioSummary summary, Color color)
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color32[Width * Height];
            var fill = (Color32)color;
            var axis = new Color32(fill.r, fill.g, fill.b, 60);

            for (int x = 0; x < Width; x++) pixels[(Height / 2) * Width + x] = axis;

            int buckets = summary.Min.Length;
            for (int x = 0; x < Width; x++)
            {
                int from = x * buckets / Width;
                int to = Math.Max(from + 1, (x + 1) * buckets / Width);

                float min = 0f, max = 0f;
                for (int b = from; b < to && b < buckets; b++)
                {
                    min = Mathf.Min(min, summary.Min[b]);
                    max = Mathf.Max(max, summary.Max[b]);
                }

                int y0 = Mathf.Clamp(Mathf.RoundToInt((min * 0.5f + 0.5f) * (Height - 1)), 0, Height - 1);
                int y1 = Mathf.Clamp(Mathf.RoundToInt((max * 0.5f + 0.5f) * (Height - 1)), 0, Height - 1);
                for (int y = y0; y <= y1; y++) pixels[y * Width + x] = fill;
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        public override float Length(LoadedAsset side)
        {
            var summary = side != null ? side.Data as AudioSummary : null;
            return summary != null ? summary.Length : 0f;
        }

        public override void Play(LoadedAsset side, int slot, float time)
        {
            var clip = side != null ? side.Main as AudioClip : null;
            if (clip == null || !AudioPreview.Available) return;

            time = Mathf.Clamp(time, 0f, Mathf.Max(0f, clip.length - 0.01f));
            AudioPreview.Play(clip, Mathf.RoundToInt(time * clip.frequency));
            _clip = clip;
            _slot = slot;
        }

        public override void Stop()
        {
            if (_clip == null) return;
            _pausedAt = PlayTime;
            AudioPreview.Stop();
            _clip = null;
            _slot = -1;
        }

        public override void Seek(LoadedAsset side, int slot, float time)
        {
            if (Playing && _clip != null)
            {
                AudioPreview.Play(_clip, Mathf.RoundToInt(Mathf.Clamp(time, 0f, Mathf.Max(0f, _clip.length - 0.01f)) * _clip.frequency));
                return;
            }

            _clip = null;
            _pausedAt = Mathf.Max(0f, time);
        }

        public override bool Playing
        {
            get { return _clip != null && AudioPreview.IsPlaying; }
        }

        public override int PlayingSlot
        {
            get { return Playing ? _slot : -1; }
        }

        public override float PlayTime
        {
            get
            {
                if (_clip == null) return _pausedAt;
                return AudioPreview.IsPlaying ? AudioPreview.Position / (float)_clip.frequency : _pausedAt;
            }
        }

        public override void DrawOverlay(Rect fit, Rect source, LoadedAsset side, LoadedAsset other, bool isAfter, PreviewSync sync)
        {
            float length = Length(side);
            float time = PlayTime;
            if (length <= 0f || (time <= 0f && !Playing)) return;

            float x = fit.x + fit.width * Mathf.Clamp01(time / length);
            bool active = PlayingSlot == (isAfter ? 1 : 0);
            EditorGUI.DrawRect(new Rect(x - 1f, fit.y, 2f, fit.height), active ? Color.white : new Color(1f, 1f, 1f, 0.35f));
        }

        public override void Dispose()
        {
            if (_clip != null) AudioPreview.Stop();
            _clip = null;

            foreach (var waves in _waves)
            {
                foreach (var texture in waves.Values)
                    if (texture != null) Object.DestroyImmediate(texture);
                waves.Clear();
            }
        }
    }

    /// <summary>
    /// Прослушивание в редакторе без режима игры. Публичного API у Unity для
    /// этого нет — используется внутренний AudioUtil, которым пользуется сам
    /// инспектор звука. Не нашёлся — кнопок воспроизведения не будет.
    /// </summary>
    internal static class AudioPreview
    {
        private static readonly Type Util = FindUtil();
        private static readonly MethodInfo PlayMethod = Find("PlayPreviewClip", 3);
        private static readonly MethodInfo StopMethod = Find("StopAllPreviewClips", 0);
        private static readonly MethodInfo PositionMethod = Find("GetPreviewClipSamplePosition", 0);
        private static readonly MethodInfo PlayingMethod = Find("IsPreviewClipPlaying", 0);

        public static bool Available
        {
            get { return PlayMethod != null && StopMethod != null && PlayingMethod != null; }
        }

        private static Type FindUtil()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType("UnityEditor.AudioUtil", false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static MethodInfo Find(string name, int parameters)
        {
            if (Util == null) return null;
            return Util.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                       .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameters);
        }

        public static void Play(AudioClip clip, int startSample)
        {
            try
            {
                StopMethod.Invoke(null, null);
                PlayMethod.Invoke(null, new object[] { clip, startSample, false });
            }
            catch (Exception e)
            {
                Diagnostics.Journal.Warn(L.F("Audio does not play: {0}", e.Message));
            }
        }

        public static void Stop()
        {
            try { if (StopMethod != null) StopMethod.Invoke(null, null); }
            catch { }
        }

        public static bool IsPlaying
        {
            get
            {
                try { return PlayingMethod != null && (bool)PlayingMethod.Invoke(null, null); }
                catch { return false; }
            }
        }

        public static int Position
        {
            get
            {
                try { return PositionMethod != null ? Convert.ToInt32(PositionMethod.Invoke(null, null)) : 0; }
                catch { return 0; }
            }
        }
    }
}
