using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lev.Git.Preview
{
    /// <summary>Часы воспроизведения без звука: анимация и Timeline идут по времени редактора, по кругу.</summary>
    internal sealed class PlaybackClock
    {
        private bool _playing;
        private double _startedAt;
        private float _startTime, _pausedAt;
        private int _slot = -1;

        public float Length = 1f;

        public void Play(int slot, float time)
        {
            _startTime = Mathf.Repeat(time, Mathf.Max(0.01f, Length));
            _startedAt = EditorApplication.timeSinceStartup;
            _playing = true;
            _slot = slot;
        }

        public void Stop()
        {
            _pausedAt = Time;
            _playing = false;
            _slot = -1;
        }

        /// <summary>Перемотка: играло — продолжает с нового места, стояло — стоит на нём.</summary>
        public void Seek(float time)
        {
            time = Mathf.Clamp(time, 0f, Mathf.Max(0.01f, Length));

            if (_playing)
            {
                _startTime = time;
                _startedAt = EditorApplication.timeSinceStartup;
            }
            else
            {
                _pausedAt = time;
            }
        }

        public bool Playing { get { return _playing; } }
        public int Slot { get { return _playing ? _slot : -1; } }

        public float Time
        {
            get
            {
                return _playing
                    ? Mathf.Repeat(_startTime + (float)(EditorApplication.timeSinceStartup - _startedAt), Mathf.Max(0.01f, Length))
                    : _pausedAt;
            }
        }
    }

    internal static class AnimationText
    {
        public static string Keys(int n)
        {
            return L.N("{0} key", "{0} keys", n);
        }

        public static string Of(object value)
        {
            if (value == null) return "—";
            if (value is bool) return (bool)value ? L.T("on") : L.T("off");
            if (value is float) return PreviewText.Number((float)value);
            if (value is double) return PreviewText.Number((double)value);
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    internal static class AnimationCompare
    {
        public static string Key(EditorCurveBinding b)
        {
            return b.path + "|" + (b.type != null ? b.type.FullName : string.Empty) + "|" + b.propertyName;
        }

        /// <summary>«Body/Arm › Transform › Local Position.x».</summary>
        public static string Label(EditorCurveBinding b)
        {
            var target = string.IsNullOrEmpty(b.path) ? L.T("(root)") : b.path;
            var property = b.propertyName ?? string.Empty;
            int dot = property.IndexOf('.');
            var head = dot >= 0 ? property.Substring(0, dot) : property;
            var tail = dot >= 0 ? property.Substring(dot) : string.Empty;
            return target + " › " + (b.type != null ? b.type.Name : "?") + " › " + ObjectNames.NicifyVariableName(head) + tail;
        }

        public static bool SameCurve(AnimationCurve a, AnimationCurve b)
        {
            if (a == null || b == null) return a == b;
            if (a.length != b.length || a.preWrapMode != b.preWrapMode || a.postWrapMode != b.postWrapMode) return false;

            for (int i = 0; i < a.length; i++)
            {
                var ka = a[i];
                var kb = b[i];
                if (!Near(ka.time, kb.time) || !Near(ka.value, kb.value) || !Near(ka.inTangent, kb.inTangent) ||
                    !Near(ka.outTangent, kb.outTangent) || ka.weightedMode != kb.weightedMode ||
                    !Near(ka.inWeight, kb.inWeight) || !Near(ka.outWeight, kb.outWeight)) return false;
            }

            return true;
        }

        private static bool Near(float x, float y)
        {
            return x == y || Mathf.Abs(x - y) <= 1e-5f;
        }

        public static string CurveText(AnimationCurve c)
        {
            if (c == null) return "—";
            if (c.length == 0) return L.Tc("curve", "empty");

            float min = float.MaxValue, max = float.MinValue;
            foreach (var k in c.keys)
            {
                min = Mathf.Min(min, k.value);
                max = Mathf.Max(max, k.value);
            }

            return AnimationText.Keys(c.length) + ", " + PreviewText.Number(min) +
                   (Mathf.Approximately(min, max) ? string.Empty : "…" + PreviewText.Number(max));
        }

        public static void Range(AnimationCurve c, float length, ref float min, ref float max)
        {
            if (c == null || c.length == 0) return;

            foreach (var k in c.keys)
            {
                min = Mathf.Min(min, k.value);
                max = Mathf.Max(max, k.value);
            }

            for (int i = 0; i <= 64; i++)
            {
                var v = c.Evaluate(length * i / 64f);
                if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                min = Mathf.Min(min, v);
                max = Mathf.Max(max, v);
            }
        }
    }

    // ================================================================ клип ===

    /// <summary>
    /// Изменения клипа: кривые по привязкам («Arm › Transform › Local Position.x»),
    /// настройки клипа, события. Откат — через AnimationUtility, как это делает
    /// окно Animation: путь в сериализации у кривой ничего не говорит.
    /// </summary>
    internal sealed class AnimationClipDescriber : ChangeDescriber<AnimationClip>
    {
        public override bool CoversSubAssets { get { return true; } }

        protected override void Describe(AnimationClip before, AnimationClip after, DescribeContext ctx)
        {
            if (!Mathf.Approximately(before.length, after.length))
                ctx.Add(L.T("Length"), ChangeValue.Of(L.F("{0} s", PreviewText.Number(before.length))), ChangeValue.Of(L.F("{0} s", PreviewText.Number(after.length))));

            if (!Mathf.Approximately(before.frameRate, after.frameRate))
                ctx.Add(L.T("Frame Rate"), ChangeValue.Of(before.frameRate), ChangeValue.Of(after.frameRate), ChangeKind.Modified, "frameRate")
                   .Revert = (live, src) => ((AnimationClip)live).frameRate = ((AnimationClip)src).frameRate;

            DescribeSettings(before, after, ctx);
            DescribeCurves(before, after, ctx);
            DescribeReferenceCurves(before, after, ctx);
            DescribeEvents(before, after, ctx);
        }

        private static void DescribeSettings(AnimationClip before, AnimationClip after, DescribeContext ctx)
        {
            var sb = AnimationUtility.GetAnimationClipSettings(before);
            var sa = AnimationUtility.GetAnimationClipSettings(after);

            ctx.Group = L.T("Clip Settings");
            Setting(ctx, sb, sa, "Loop Time", s => s.loopTime, (l, s) => l.loopTime = s.loopTime);
            Setting(ctx, sb, sa, "Loop Pose", s => s.loopBlend, (l, s) => l.loopBlend = s.loopBlend);
            Setting(ctx, sb, sa, "Cycle Offset", s => s.cycleOffset, (l, s) => l.cycleOffset = s.cycleOffset);
            Setting(ctx, sb, sa, "Mirror", s => s.mirror, (l, s) => l.mirror = s.mirror);
            Setting(ctx, sb, sa, "Start", s => s.startTime, (l, s) => l.startTime = s.startTime);
            Setting(ctx, sb, sa, "Stop", s => s.stopTime, (l, s) => l.stopTime = s.stopTime);
            Setting(ctx, sb, sa, "Root Rotation · Bake Into Pose", s => s.loopBlendOrientation, (l, s) => l.loopBlendOrientation = s.loopBlendOrientation);
            Setting(ctx, sb, sa, "Root Position (Y) · Bake Into Pose", s => s.loopBlendPositionY, (l, s) => l.loopBlendPositionY = s.loopBlendPositionY);
            Setting(ctx, sb, sa, "Root Position (XZ) · Bake Into Pose", s => s.loopBlendPositionXZ, (l, s) => l.loopBlendPositionXZ = s.loopBlendPositionXZ);
            ctx.Group = null;
        }

        private static void Setting(DescribeContext ctx, AnimationClipSettings sb, AnimationClipSettings sa, string label,
                                    Func<AnimationClipSettings, object> get, Action<AnimationClipSettings, AnimationClipSettings> copy)
        {
            var vb = get(sb);
            var va = get(sa);
            if (Equals(vb, va)) return;

            ctx.Add(label, ChangeValue.Of(AnimationText.Of(vb)), ChangeValue.Of(AnimationText.Of(va)), ChangeKind.Modified, "setting:" + label)
               .Revert = (live, src) =>
               {
                   var liveSettings = AnimationUtility.GetAnimationClipSettings((AnimationClip)live);
                   copy(liveSettings, AnimationUtility.GetAnimationClipSettings((AnimationClip)src));
                   AnimationUtility.SetAnimationClipSettings((AnimationClip)live, liveSettings);
               };
        }

        private static void DescribeCurves(AnimationClip before, AnimationClip after, DescribeContext ctx)
        {
            var all = new List<EditorCurveBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in AnimationUtility.GetCurveBindings(after).Concat(AnimationUtility.GetCurveBindings(before)))
                if (seen.Add(AnimationCompare.Key(b))) all.Add(b);

            ctx.Group = L.T("Curves");
            foreach (var binding in all)
            {
                var cb = AnimationUtility.GetEditorCurve(before, binding);
                var ca = AnimationUtility.GetEditorCurve(after, binding);
                if (AnimationCompare.SameCurve(cb, ca)) continue;

                var b = binding;
                var kind = cb == null ? ChangeKind.Added : ca == null ? ChangeKind.Removed : ChangeKind.Modified;
                var item = ctx.Add(AnimationCompare.Label(binding),
                    ChangeValue.Of(AnimationCompare.CurveText(cb)), ChangeValue.Of(AnimationCompare.CurveText(ca)),
                    kind, "curve:" + AnimationCompare.Key(binding));

                item.Note = kind == ChangeKind.Added ? L.T("curve added") : kind == ChangeKind.Removed ? L.T("curve removed") : null;
                item.Revert = (live, src) =>
                    AnimationUtility.SetEditorCurve((AnimationClip)live, b, AnimationUtility.GetEditorCurve((AnimationClip)src, b));
            }
            ctx.Group = null;
        }

        private static void DescribeReferenceCurves(AnimationClip before, AnimationClip after, DescribeContext ctx)
        {
            var all = new List<EditorCurveBinding>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(after).Concat(AnimationUtility.GetObjectReferenceCurveBindings(before)))
                if (seen.Add(AnimationCompare.Key(b))) all.Add(b);

            ctx.Group = L.T("Reference Curves");
            foreach (var binding in all)
            {
                var kb = AnimationUtility.GetObjectReferenceCurve(before, binding);
                var ka = AnimationUtility.GetObjectReferenceCurve(after, binding);
                if (SameKeys(kb, ka, ctx)) continue;

                var b = binding;
                var item = ctx.Add(AnimationCompare.Label(binding),
                    ChangeValue.Of(kb != null ? AnimationText.Keys(kb.Length) : "—"),
                    ChangeValue.Of(ka != null ? AnimationText.Keys(ka.Length) : "—"),
                    kb == null ? ChangeKind.Added : ka == null ? ChangeKind.Removed : ChangeKind.Modified,
                    "refcurve:" + AnimationCompare.Key(binding));

                item.Revert = (live, src) =>
                    AnimationUtility.SetObjectReferenceCurve((AnimationClip)live, b, AnimationUtility.GetObjectReferenceCurve((AnimationClip)src, b));
            }
            ctx.Group = null;
        }

        private static bool SameKeys(ObjectReferenceKeyframe[] a, ObjectReferenceKeyframe[] b, DescribeContext ctx)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!Mathf.Approximately(a[i].time, b[i].time) || !ctx.SameRef(a[i].value, b[i].value)) return false;
            return true;
        }

        private static void DescribeEvents(AnimationClip before, AnimationClip after, DescribeContext ctx)
        {
            var eb = AnimationUtility.GetAnimationEvents(before);
            var ea = AnimationUtility.GetAnimationEvents(after);

            var sb = string.Join("; ", eb.Select(Signature).ToArray());
            var sa = string.Join("; ", ea.Select(Signature).ToArray());
            if (sb == sa) return;

            ctx.Add(L.T("Events"), ChangeValue.Of(eb.Length == 0 ? L.T("none") : sb), ChangeValue.Of(ea.Length == 0 ? L.T("none") : sa), ChangeKind.Modified, "events")
               .Revert = (live, src) => AnimationUtility.SetAnimationEvents((AnimationClip)live, AnimationUtility.GetAnimationEvents((AnimationClip)src));
        }

        private static string Signature(AnimationEvent e)
        {
            return e.functionName + " @" + PreviewText.Number(e.time) +
                   (string.IsNullOrEmpty(e.stringParameter) ? string.Empty : " " + L.F("“{0}”", e.stringParameter)) +
                   (e.floatParameter != 0f ? " " + PreviewText.Number(e.floatParameter) : string.Empty) +
                   (e.intParameter != 0 ? " " + e.intParameter : string.Empty) +
                   (e.objectReferenceParameter != null ? " " + e.objectReferenceParameter.name : string.Empty);
        }
    }

    /// <summary>
    /// Клип двумя способами. «Кривые» — выбранная кривая обеих версий на одной
    /// шкале времени и значений: наложением разница видна сразу. «На модели» —
    /// обе версии проигрываются на копии объекта сцены, который этот клип
    /// использует (или выделенного), с общей камерой и общим временем.
    /// </summary>
    internal sealed class AnimationClipPresenter : AssetPresenter
    {
        private const int Width = 1024, Height = 320;

        private static readonly Color[] Colors =
        {
            new Color(0.93f, 0.5f, 0.38f, 1f),
            new Color(0.42f, 0.8f, 0.54f, 1f)
        };

        private sealed class Binding
        {
            public EditorCurveBinding Value;
            public string Label;
            public bool Changed;
            public float Min, Max;
        }

        private readonly List<Binding> _bindings = new List<Binding>();
        private readonly Dictionary<string, Texture2D> _graphs = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private readonly PreviewRenderUtility[] _utilities = new PreviewRenderUtility[4];
        private readonly GameObject[] _instances = new GameObject[4];
        private readonly Bounds[] _bounds = new Bounds[4];
        private readonly PlaybackClock _clock = new PlaybackClock();

        private string[] _choices;
        private float _length = 1f;
        private GameObject _carrier;

        public override Type Target { get { return typeof(AnimationClip); } }
        public override bool Interactive { get { return true; } }
        public override string[] Shapes { get { return L.Ts("Curves", "On Model"); } }
        public override CompareModes Modes { get { return CompareModes.SideBySide | CompareModes.Overlay | CompareModes.Toggle | CompareModes.Swipe; } }
        public override PresenterFeatures Features { get { return PresenterFeatures.Playback; } }
        public override bool PlaybackPerSide { get { return false; } }
        public override bool NeedsRepaint { get { return _clock.Playing; } }
        public override string[] Choices { get { return _choices; } }

        public override bool Stack(PreviewSync sync)
        {
            return sync.Shape == 0;
        }

        public override bool ChoicesVisible(PreviewSync sync)
        {
            return sync.Shape == 0 && _choices != null && _choices.Length > 0;
        }

        public override void Prepare(LoadedAsset before, LoadedAsset after)
        {
            var b = before != null ? before.Main as AnimationClip : null;
            var a = after != null ? after.Main as AnimationClip : null;

            _length = Mathf.Max(0.01f, Mathf.Max(b != null ? b.length : 0f, a != null ? a.length : 0f));
            _clock.Length = _length;

            var byKey = new Dictionary<string, Binding>(StringComparer.Ordinal);
            _bindings.Clear();
            foreach (var clip in new[] { a, b })
            {
                if (clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var key = AnimationCompare.Key(binding);
                    if (byKey.ContainsKey(key)) continue;
                    var entry = new Binding { Value = binding, Label = AnimationCompare.Label(binding) };
                    byKey[key] = entry;
                    _bindings.Add(entry);
                }
            }

            foreach (var entry in _bindings)
            {
                var cb = b != null ? AnimationUtility.GetEditorCurve(b, entry.Value) : null;
                var ca = a != null ? AnimationUtility.GetEditorCurve(a, entry.Value) : null;
                entry.Changed = !AnimationCompare.SameCurve(cb, ca);

                float min = float.MaxValue, max = float.MinValue;
                AnimationCompare.Range(cb, _length, ref min, ref max);
                AnimationCompare.Range(ca, _length, ref min, ref max);
                if (min > max) { min = 0f; max = 1f; }
                if (max - min < 1e-4f) { min -= 0.5f; max += 0.5f; }
                entry.Min = min;
                entry.Max = max;
            }

            // Изменённые кривые — первыми: ради них панель и открыли.
            var ordered = _bindings.Where(x => x.Changed).Concat(_bindings.Where(x => !x.Changed)).ToList();
            _bindings.Clear();
            _bindings.AddRange(ordered);

            var used = new HashSet<string>(StringComparer.Ordinal);
            _choices = _bindings.Select(x =>
            {
                var label = (x.Changed ? "● " : "   ") + x.Label;
                var unique = label;
                for (int n = 2; !used.Add(unique); n++) unique = label + " (" + n + ")";
                return unique;
            }).ToArray();

            _carrier = FindCarrier(a ?? b);
        }

        public override float Length(LoadedAsset side) { return _length; }
        public override void Play(LoadedAsset side, int slot, float time) { _clock.Play(slot, time); }
        public override void Stop() { _clock.Stop(); }
        public override void Seek(LoadedAsset side, int slot, float time) { _clock.Seek(time); }
        public override bool Playing { get { return _clock.Playing; } }
        public override int PlayingSlot { get { return _clock.Slot; } }
        public override float PlayTime { get { return _clock.Time; } }

        public override Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync)
        {
            var clip = side.Main as AnimationClip;
            if (clip == null) return null;

            slot = Mathf.Clamp(slot, 0, 3);
            if (sync.Shape == 1) return RenderModel(rect, clip, slot, sync);
            if (_bindings.Count == 0) return PreviewCanvas.Blank;

            int choice = Mathf.Clamp(sync.Choice, 0, _bindings.Count - 1);
            var key = RuntimeHelpers.GetHashCode(side) + "|" + slot + "|" + choice;

            Texture2D graph;
            if (!_graphs.TryGetValue(key, out graph) || graph == null)
            {
                graph = Graph(clip, _bindings[choice], Colors[slot % Colors.Length]);
                _graphs[key] = graph;
            }

            return graph;
        }

        private Texture2D Graph(AnimationClip clip, Binding binding, Color color)
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color32[Width * Height];
            var grid = new Color32(255, 255, 255, 22);
            for (int i = 1; i < 4; i++)
                for (int x = 0; x < Width; x++) pixels[(i * Height / 4) * Width + x] = grid;
            for (int i = 1; i < 10; i++)
                for (int y = 0; y < Height; y++) pixels[y * Width + i * Width / 10] = grid;

            var curve = AnimationUtility.GetEditorCurve(clip, binding.Value);
            if (curve != null && curve.length > 0)
            {
                var fill = (Color32)color;
                float range = binding.Max - binding.Min;
                int previous = -1;

                for (int x = 0; x < Width; x++)
                {
                    float t = x / (float)(Width - 1) * _length;
                    if (t > clip.length + 1e-4f) break;

                    int y = Mathf.Clamp(Mathf.RoundToInt((curve.Evaluate(t) - binding.Min) / range * (Height - 12)) + 6, 0, Height - 1);
                    int from = previous < 0 ? y : Mathf.Min(previous, y);
                    int to = previous < 0 ? y : Mathf.Max(previous, y);

                    for (int yy = Mathf.Max(0, from - 1); yy <= Mathf.Min(Height - 1, to + 1); yy++)
                    {
                        pixels[yy * Width + x] = fill;
                        if (x + 1 < Width) pixels[yy * Width + x + 1] = fill;
                    }

                    previous = y;
                }

                var key = new Color32(255, 255, 255, 230);
                foreach (var k in curve.keys)
                {
                    int kx = Mathf.RoundToInt(k.time / _length * (Width - 1));
                    int ky = Mathf.RoundToInt((k.value - binding.Min) / range * (Height - 12)) + 6;
                    for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int px = kx + dx, py = ky + dy;
                        if (px >= 0 && px < Width && py >= 0 && py < Height) pixels[py * Width + px] = key;
                    }
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        public override void DrawOverlay(Rect fit, Rect source, LoadedAsset side, LoadedAsset other, bool isAfter, PreviewSync sync)
        {
            var clip = side.Main as AnimationClip;
            if (clip == null) return;

            var hint = new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true };
            float time = _clock.Time;

            if (sync.Shape == 0)
            {
                if (_bindings.Count == 0)
                {
                    GUI.Label(fit, L.T("The clip has no value curves"), hint);
                    return;
                }

                var binding = _bindings[Mathf.Clamp(sync.Choice, 0, _bindings.Count - 1)];
                GUI.Label(new Rect(fit.xMax - 90f, fit.y + 18f, 86f, 14f), PreviewText.Number(binding.Max), EditorStyles.miniLabel);
                GUI.Label(new Rect(fit.xMax - 90f, fit.yMax - 16f, 86f, 14f), PreviewText.Number(binding.Min), EditorStyles.miniLabel);

                var curve = AnimationUtility.GetEditorCurve(clip, binding.Value);
                if (curve == null)
                    GUI.Label(fit, L.T("This curve is not in this version"), hint);
                else if (time > 0f || _clock.Playing)
                    GUI.Label(new Rect(fit.x + 70f, fit.y + 3f, 220f, 14f),
                        L.F("{0} s · {1}", PreviewText.Number(time), PreviewText.Number(curve.Evaluate(time))), EditorStyles.miniBoldLabel);
            }
            else if (_carrier == null)
            {
                GUI.Label(fit, L.T("Select the scene object that plays this clip — the versions will play on its copy"), hint);
                return;
            }

            if (time <= 0f && !_clock.Playing) return;
            float x = fit.x + fit.width * Mathf.Clamp01(time / _length);
            EditorGUI.DrawRect(new Rect(x - 1f, fit.y, 2f, fit.height), new Color(1f, 1f, 1f, 0.8f));
        }

        // ---------------------------------------------------------- модель ---

        private Texture RenderModel(Rect rect, AnimationClip clip, int slot, PreviewSync sync)
        {
            if (_carrier == null) _carrier = FindCarrier(clip);
            if (_carrier == null || rect.width < 4f || rect.height < 4f) return PreviewCanvas.Blank;

            var utility = _utilities[slot] ?? (_utilities[slot] = new PreviewRenderUtility());

            if (_instances[slot] == null)
            {
                var instance = Object.Instantiate(_carrier);
                instance.hideFlags = HideFlags.HideAndDontSave;
                utility.AddSingleGO(instance);
                _instances[slot] = instance;
                _bounds[slot] = ModelPresenter.BoundsOf(instance);
            }

            // Легаси и generic-клипы сэмплируются прямо на объект; humanoid без
            // Animator в режиме игры покажет только то, что сможет.
            try { clip.SampleAnimation(_instances[slot], Mathf.Min(_clock.Time, clip.length)); }
            catch { }

            var bounds = _bounds[slot];
            for (int i = 0; i < _instances.Length; i++)
                if (i != slot && _instances[i] != null) bounds.Encapsulate(_bounds[i]);

            return ModelPresenter.RenderScene(utility, rect, bounds, sync, false);
        }

        private static GameObject FindCarrier(AnimationClip clip)
        {
            if (clip == null) return null;

            var selected = Selection.activeGameObject;
            if (selected != null && !EditorUtility.IsPersistent(selected) && Uses(selected, clip)) return selected;

            foreach (var animator in Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (animator != null && Uses(animator.gameObject, clip)) return animator.gameObject;

            foreach (var animation in Object.FindObjectsByType<Animation>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (animation != null && Uses(animation.gameObject, clip)) return animation.gameObject;

            return selected != null && !EditorUtility.IsPersistent(selected) ? selected : null;
        }

        private static bool Uses(GameObject go, AnimationClip clip)
        {
            var animator = go.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
                foreach (var c in animator.runtimeAnimatorController.animationClips)
                    if (c != null && c.name == clip.name) return true;

            var animation = go.GetComponent<Animation>();
            return animation != null && animation.GetClip(clip.name) != null;
        }

        public override void Dispose()
        {
            foreach (var graph in _graphs.Values)
                if (graph != null) Object.DestroyImmediate(graph);
            _graphs.Clear();

            for (int i = 0; i < _instances.Length; i++)
            {
                if (_instances[i] != null) Object.DestroyImmediate(_instances[i]);
                _instances[i] = null;
                if (_utilities[i] != null) _utilities[i].Cleanup();
                _utilities[i] = null;
            }
        }
    }

    // ============================================================ аниматор ===

    internal static class AnimatorCompare
    {
        public static string StateSignature(AnimatorState s)
        {
            if (s == null) return string.Empty;
            return (s.motion != null ? s.motion.name : "-") + "|" + PreviewText.Number(s.speed) + "|" + s.tag + "|" +
                   s.mirror + "|" + PreviewText.Number(s.cycleOffset) + "|" + s.writeDefaultValues + "|" +
                   s.behaviours.Length + "|" + string.Join(";", s.transitions.Select(Summary).ToArray());
        }

        public static string Destination(AnimatorStateTransition t)
        {
            if (t.destinationState != null) return t.destinationState.name;
            if (t.destinationStateMachine != null) return t.destinationStateMachine.name;
            return t.isExit ? "Exit" : "?";
        }

        public static string Summary(AnimatorStateTransition t)
        {
            var sb = new StringBuilder();
            sb.Append(t.conditions.Length == 0 ? L.T("no conditions") : string.Join(L.T(" and "), t.conditions.Select(Condition).ToArray()));
            sb.Append(L.F("; exit time {0}", t.hasExitTime ? PreviewText.Number(t.exitTime) : L.T("none")));
            sb.Append(L.F("; duration {0}", t.hasFixedDuration ? L.F("{0} s", PreviewText.Number(t.duration)) : PreviewText.Number(t.duration)));
            if (t.offset != 0f) sb.Append("; offset ").Append(PreviewText.Number(t.offset));
            if (t.solo) sb.Append("; solo");
            if (t.mute) sb.Append("; mute");
            return sb.ToString();
        }

        private static string Condition(AnimatorCondition c)
        {
            switch (c.mode)
            {
                case AnimatorConditionMode.If: return c.parameter;
                case AnimatorConditionMode.IfNot: return L.F("not {0}", c.parameter);
                case AnimatorConditionMode.Greater: return c.parameter + " > " + PreviewText.Number(c.threshold);
                case AnimatorConditionMode.Less: return c.parameter + " < " + PreviewText.Number(c.threshold);
                case AnimatorConditionMode.Equals: return c.parameter + " = " + PreviewText.Number(c.threshold);
                case AnimatorConditionMode.NotEqual: return c.parameter + " ≠ " + PreviewText.Number(c.threshold);
                default: return c.parameter;
            }
        }

        public static Dictionary<string, AnimatorState> States(AnimatorStateMachine machine)
        {
            var result = new Dictionary<string, AnimatorState>(StringComparer.Ordinal);
            if (machine == null) return result;
            foreach (var child in machine.states)
                if (child.state != null && !result.ContainsKey(child.state.name)) result[child.state.name] = child.state;
            return result;
        }

        public static Dictionary<string, AnimatorStateTransition> Transitions(AnimatorStateTransition[] transitions)
        {
            var result = new Dictionary<string, AnimatorStateTransition>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (transitions == null) return result;

            foreach (var t in transitions)
            {
                if (t == null) continue;
                var destination = Destination(t);
                int n;
                counts.TryGetValue(destination, out n);
                counts[destination] = n + 1;
                result[destination + "#" + n] = t;
            }

            return result;
        }

        public static AnimatorStateMachine LayerMachine(AnimatorController controller, string layer)
        {
            if (controller == null) return null;
            foreach (var l in controller.layers)
                if (l.name == layer) return l.stateMachine;
            return null;
        }
    }

    /// <summary>
    /// Изменения AnimatorController словами окна Animator: параметры, слои,
    /// состояния и переходы. Внутренние объекты файла (состояния, машины,
    /// переходы) описываются здесь же — по отдельности они бессмысленны.
    /// </summary>
    internal sealed class AnimatorControllerDescriber : ChangeDescriber<AnimatorController>
    {
        public override bool CoversSubAssets { get { return true; } }

        protected override void Describe(AnimatorController before, AnimatorController after, DescribeContext ctx)
        {
            DescribeParameters(before, after, ctx);

            var old = new Dictionary<string, AnimatorControllerLayer>(StringComparer.Ordinal);
            foreach (var layer in before.layers)
                if (!old.ContainsKey(layer.name)) old[layer.name] = layer;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var layer in after.layers)
            {
                if (!seen.Add(layer.name)) continue;

                AnimatorControllerLayer previous;
                if (!old.TryGetValue(layer.name, out previous))
                {
                    ctx.Group = L.T("Layers");
                    ctx.Add(L.F("“{0}”", layer.name), ChangeValue.None, ChangeValue.Of(L.T("added")), ChangeKind.Added);
                    continue;
                }

                ctx.Group = L.F("Layer “{0}”", layer.name);
                Compare(ctx, "Weight", previous.defaultWeight, layer.defaultWeight);
                Compare(ctx, "Blending", previous.blendingMode, layer.blendingMode);
                Compare(ctx, "IK Pass", previous.iKPass, layer.iKPass);
                if (!ctx.SameRef(previous.avatarMask, layer.avatarMask))
                    ctx.Add("Mask", ChangeValue.Of(previous.avatarMask), ChangeValue.Of(layer.avatarMask));

                DescribeMachine(previous.stateMachine, layer.stateMachine, string.Empty, ctx);
            }

            ctx.Group = L.T("Layers");
            foreach (var layer in before.layers)
                if (!seen.Contains(layer.name))
                    ctx.Add(L.F("“{0}”", layer.name), ChangeValue.Of(L.Tc("layer", "existed")), ChangeValue.None, ChangeKind.Removed).Note = L.T("layer removed");

            ctx.Group = null;
        }

        private static void DescribeParameters(AnimatorController before, AnimatorController after, DescribeContext ctx)
        {
            ctx.Group = L.T("Parameters");

            var old = new Dictionary<string, AnimatorControllerParameter>(StringComparer.Ordinal);
            foreach (var p in before.parameters) old[p.name] = p;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in after.parameters)
            {
                seen.Add(p.name);
                AnimatorControllerParameter previous;
                var now = ParameterText(p);

                if (!old.TryGetValue(p.name, out previous))
                    ctx.Add(p.name, ChangeValue.None, ChangeValue.Of(now), ChangeKind.Added).Note = L.T("added");
                else if (ParameterText(previous) != now)
                    ctx.Add(p.name, ChangeValue.Of(ParameterText(previous)), ChangeValue.Of(now));
            }

            foreach (var p in before.parameters)
                if (!seen.Contains(p.name))
                    ctx.Add(p.name, ChangeValue.Of(ParameterText(p)), ChangeValue.None, ChangeKind.Removed).Note = L.T("removed");

            ctx.Group = null;
        }

        private static string ParameterText(AnimatorControllerParameter p)
        {
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float: return "Float = " + PreviewText.Number(p.defaultFloat);
                case AnimatorControllerParameterType.Int: return "Int = " + p.defaultInt;
                case AnimatorControllerParameterType.Bool: return "Bool = " + (p.defaultBool ? "true" : "false");
                default: return "Trigger";
            }
        }

        private static void DescribeMachine(AnimatorStateMachine before, AnimatorStateMachine after, string prefix, DescribeContext ctx)
        {
            if (before == null || after == null) return;

            var statesBefore = AnimatorCompare.States(before);
            var statesAfter = AnimatorCompare.States(after);

            var defaultBefore = before.defaultState != null ? before.defaultState.name : "—";
            var defaultAfter = after.defaultState != null ? after.defaultState.name : "—";
            if (defaultBefore != defaultAfter)
                ctx.Add(prefix + L.T("Default State"), ChangeValue.Of(defaultBefore), ChangeValue.Of(defaultAfter));

            foreach (var pair in statesAfter)
            {
                var label = prefix + L.F("“{0}”", pair.Key);
                AnimatorState b;
                var a = pair.Value;

                if (!statesBefore.TryGetValue(pair.Key, out b))
                {
                    ctx.Add(L.F("State {0}", label), ChangeValue.None, ChangeValue.Of(a.motion), ChangeKind.Added).Note = L.Tc("state", "added");
                    continue;
                }

                if (!ctx.SameRef(b.motion, a.motion))
                    ctx.Add(label + " · Motion", ChangeValue.Of(b.motion), ChangeValue.Of(a.motion));
                Compare(ctx, label + " · Speed", b.speed, a.speed);
                Compare(ctx, label + " · Tag", b.tag, a.tag);
                Compare(ctx, label + " · Mirror", b.mirror, a.mirror);
                Compare(ctx, label + " · Cycle Offset", b.cycleOffset, a.cycleOffset);
                Compare(ctx, label + " · Write Defaults", b.writeDefaultValues, a.writeDefaultValues);
                Compare(ctx, label + " · Behaviours", b.behaviours.Length, a.behaviours.Length);

                DescribeTransitions(label, b.transitions, a.transitions, ctx);
            }

            foreach (var pair in statesBefore)
                if (!statesAfter.ContainsKey(pair.Key))
                    ctx.Add(L.F("State {0}“{1}”", prefix, pair.Key), ChangeValue.Of(pair.Value.motion), ChangeValue.None, ChangeKind.Removed).Note = L.Tc("state", "removed");

            DescribeTransitions(prefix + "Any State", before.anyStateTransitions, after.anyStateTransitions, ctx);

            var subBefore = new Dictionary<string, AnimatorStateMachine>(StringComparer.Ordinal);
            foreach (var child in before.stateMachines)
                if (child.stateMachine != null && !subBefore.ContainsKey(child.stateMachine.name)) subBefore[child.stateMachine.name] = child.stateMachine;

            var subSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in after.stateMachines)
            {
                if (child.stateMachine == null || !subSeen.Add(child.stateMachine.name)) continue;

                AnimatorStateMachine previous;
                if (subBefore.TryGetValue(child.stateMachine.name, out previous))
                    DescribeMachine(previous, child.stateMachine, prefix + child.stateMachine.name + " / ", ctx);
                else
                    ctx.Add(L.F("State Machine {0}“{1}”", prefix, child.stateMachine.name), ChangeValue.None, ChangeValue.Of(L.Tc("state machine", "added")), ChangeKind.Added);
            }

            foreach (var pair in subBefore)
                if (!subSeen.Contains(pair.Key))
                    ctx.Add(L.F("State Machine {0}“{1}”", prefix, pair.Key), ChangeValue.Of(L.Tc("state machine", "existed")), ChangeValue.None, ChangeKind.Removed);
        }

        private static void DescribeTransitions(string from, AnimatorStateTransition[] before, AnimatorStateTransition[] after, DescribeContext ctx)
        {
            var tb = AnimatorCompare.Transitions(before);
            var ta = AnimatorCompare.Transitions(after);

            foreach (var pair in ta)
            {
                var label = L.F("Transition {0} → “{1}”", from, AnimatorCompare.Destination(pair.Value));
                AnimatorStateTransition previous;
                var now = AnimatorCompare.Summary(pair.Value);

                if (!tb.TryGetValue(pair.Key, out previous))
                    ctx.Add(label, ChangeValue.None, ChangeValue.Of(now), ChangeKind.Added).Note = L.T("added");
                else if (AnimatorCompare.Summary(previous) != now)
                    ctx.Add(label, ChangeValue.Of(AnimatorCompare.Summary(previous)), ChangeValue.Of(now));
            }

            foreach (var pair in tb)
                if (!ta.ContainsKey(pair.Key))
                    ctx.Add(L.F("Transition {0} → “{1}”", from, AnimatorCompare.Destination(pair.Value)),
                            ChangeValue.Of(AnimatorCompare.Summary(pair.Value)), ChangeValue.None, ChangeKind.Removed).Note = L.T("removed");
        }

        private static void Compare(DescribeContext ctx, string label, object before, object after)
        {
            if (Equals(before, after)) return;
            if (before is float && after is float && Mathf.Approximately((float)before, (float)after)) return;
            ctx.Add(label, ChangeValue.Of(AnimationText.Of(before)), ChangeValue.Of(AnimationText.Of(after)));
        }
    }

    /// <summary>
    /// Граф состояний слоя, как в окне Animator: узлы на своих местах, переходы
    /// стрелками. Добавленное зелёным, удалённое красным, изменённое жёлтым.
    /// Колесо увеличивает, перетаскивание сдвигает — общая лупа панели.
    /// </summary>
    internal sealed class AnimatorGraphPresenter : AssetPresenter
    {
        private static readonly Vector2 NodeSize = new Vector2(200f, 40f);

        private readonly Dictionary<string, Rect> _bounds = new Dictionary<string, Rect>(StringComparer.Ordinal);
        private string[] _choices = new string[0];
        private int _choice;

        public override Type Target { get { return typeof(AnimatorController); } }
        public override CompareModes Modes { get { return CompareModes.SideBySide | CompareModes.Toggle | CompareModes.Swipe; } }
        public override PresenterFeatures Features { get { return PresenterFeatures.Zoom; } }
        public override string[] Choices { get { return _choices; } }

        public override bool ChoicesVisible(PreviewSync sync)
        {
            return _choices.Length > 1;
        }

        public override void Prepare(LoadedAsset before, LoadedAsset after)
        {
            var names = new List<string>();
            _bounds.Clear();

            foreach (var side in new[] { after, before })
            {
                var controller = side != null ? side.Main as AnimatorController : null;
                if (controller == null) continue;

                foreach (var layer in controller.layers)
                {
                    if (!names.Contains(layer.name)) names.Add(layer.name);
                    if (layer.stateMachine == null) continue;

                    Rect bounds;
                    bool has = _bounds.TryGetValue(layer.name, out bounds);
                    foreach (var p in Positions(layer.stateMachine))
                    {
                        var node = new Rect(p, NodeSize);
                        bounds = has ? Rect.MinMaxRect(Mathf.Min(bounds.xMin, node.xMin), Mathf.Min(bounds.yMin, node.yMin),
                                                       Mathf.Max(bounds.xMax, node.xMax), Mathf.Max(bounds.yMax, node.yMax)) : node;
                        has = true;
                    }

                    if (has) _bounds[layer.name] = bounds;
                }
            }

            foreach (var name in names.ToList())
            {
                Rect b;
                if (!_bounds.TryGetValue(name, out b)) b = new Rect(0f, 0f, 400f, 200f);
                _bounds[name] = new Rect(b.x - 60f, b.y - 60f, b.width + 120f, b.height + 120f);
            }

            _choices = names.ToArray();
        }

        private static IEnumerable<Vector2> Positions(AnimatorStateMachine machine)
        {
            foreach (var s in machine.states) yield return s.position;
            foreach (var m in machine.stateMachines) yield return m.position;
            yield return machine.entryPosition;
            yield return machine.anyStatePosition;
            yield return machine.exitPosition;
        }

        public override float Aspect(LoadedAsset side)
        {
            if (_choices.Length == 0) return 2f;
            Rect bounds;
            return _bounds.TryGetValue(_choices[Mathf.Clamp(_choice, 0, _choices.Length - 1)], out bounds) && bounds.height > 0f
                ? bounds.width / bounds.height
                : 2f;
        }

        public override Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync)
        {
            _choice = sync.Choice;
            return PreviewCanvas.Blank;
        }

        public override void DrawOverlay(Rect fit, Rect source, LoadedAsset side, LoadedAsset other, bool isAfter, PreviewSync sync)
        {
            var controller = side.Main as AnimatorController;
            if (controller == null || _choices.Length == 0) return;

            var layerName = _choices[Mathf.Clamp(sync.Choice, 0, _choices.Length - 1)];
            var machine = AnimatorCompare.LayerMachine(controller, layerName);
            var otherMachine = AnimatorCompare.LayerMachine(other != null ? other.Main as AnimatorController : null, layerName);

            if (machine == null)
            {
                GUI.Label(fit, L.F("Layer “{0}” is not in this version", layerName), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            Rect bounds;
            if (!_bounds.TryGetValue(layerName, out bounds)) return;

            float scale = fit.width / (bounds.width * source.width);
            Func<Vector2, Vector2> map = p =>
            {
                float u = (p.x - bounds.xMin) / bounds.width;
                float v = 1f - (p.y - bounds.yMin) / bounds.height;
                return new Vector2((u - source.x) / source.width * fit.width, (1f - (v - source.y) / source.height) * fit.height);
            };

            Func<Vector2, Rect> node = p =>
            {
                var topLeft = map(p);
                return new Rect(topLeft.x, topLeft.y, NodeSize.x * scale, NodeSize.y * scale);
            };

            var otherStates = otherMachine != null ? AnimatorCompare.States(otherMachine) : null;
            var centers = new Dictionary<string, Vector2>(StringComparer.Ordinal);
            foreach (var s in machine.states) if (s.state != null) centers[s.state.name] = node(s.position).center;
            foreach (var m in machine.stateMachines) if (m.stateMachine != null) centers[m.stateMachine.name] = node(m.position).center;
            centers["Exit"] = node(machine.exitPosition).center;

            GUI.BeginClip(fit);
            try
            {
                // Переходы — под узлами.
                if (machine.defaultState != null && centers.ContainsKey(machine.defaultState.name))
                    Arrow(node(machine.entryPosition).center, centers[machine.defaultState.name], new Color(0.9f, 0.6f, 0.2f, 0.8f));

                foreach (var s in machine.states)
                {
                    if (s.state == null) continue;
                    AnimatorState counterpart = null;
                    if (otherStates != null) otherStates.TryGetValue(s.state.name, out counterpart);
                    DrawTransitions(centers[s.state.name], s.state.transitions, counterpart != null ? counterpart.transitions : null,
                                    otherStates != null, isAfter, centers);
                }

                DrawTransitions(node(machine.anyStatePosition).center, machine.anyStateTransitions,
                                otherMachine != null ? otherMachine.anyStateTransitions : null, otherMachine != null, isAfter, centers);

                SpecialNode(node(machine.entryPosition), "Entry", new Color(0.2f, 0.5f, 0.25f));
                SpecialNode(node(machine.anyStatePosition), "Any State", new Color(0.25f, 0.55f, 0.6f));
                SpecialNode(node(machine.exitPosition), "Exit", new Color(0.6f, 0.2f, 0.2f));

                foreach (var m in machine.stateMachines)
                    if (m.stateMachine != null) SpecialNode(node(m.position), "⧉ " + m.stateMachine.name, new Color(0.3f, 0.3f, 0.38f));

                foreach (var s in machine.states)
                {
                    if (s.state == null) continue;

                    var status = otherStates == null ? SliceStatus.Same
                        : !otherStates.ContainsKey(s.state.name) ? (isAfter ? SliceStatus.Added : SliceStatus.Removed)
                        : AnimatorCompare.StateSignature(otherStates[s.state.name]) == AnimatorCompare.StateSignature(s.state) ? SliceStatus.Same
                        : SliceStatus.Changed;

                    var r = node(s.position);
                    bool isDefault = machine.defaultState == s.state;
                    EditorGUI.DrawRect(r, isDefault ? new Color(0.55f, 0.36f, 0.12f) : new Color(0.27f, 0.27f, 0.27f));
                    PreviewCanvas.Outline(r, StatusColor(status), status == SliceStatus.Same ? 1f : 2.5f);

                    if (r.height >= 10f)
                        GUI.Label(r, s.state.name, new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip });
                }
            }
            finally
            {
                GUI.EndClip();
            }
        }

        private static void DrawTransitions(Vector2 from, AnimatorStateTransition[] mine, AnimatorStateTransition[] theirs, bool compare,
                                             bool isAfter, Dictionary<string, Vector2> centers)
        {
            if (mine == null) return;

            var other = compare ? AnimatorCompare.Transitions(theirs) : null;
            foreach (var pair in AnimatorCompare.Transitions(mine))
            {
                Vector2 to;
                if (!centers.TryGetValue(AnimatorCompare.Destination(pair.Value), out to)) continue;

                AnimatorStateTransition counterpart = null;
                var status = other == null ? SliceStatus.Same
                    : !other.TryGetValue(pair.Key, out counterpart) ? (isAfter ? SliceStatus.Added : SliceStatus.Removed)
                    : AnimatorCompare.Summary(counterpart) == AnimatorCompare.Summary(pair.Value) ? SliceStatus.Same
                    : SliceStatus.Changed;

                Arrow(from, to, status == SliceStatus.Same ? new Color(0.8f, 0.8f, 0.8f, 0.55f) : StatusColor(status));
            }
        }

        private static void Arrow(Vector2 from, Vector2 to, Color color)
        {
            if ((to - from).sqrMagnitude < 1f) return;

            Handles.color = color;
            Handles.DrawAAPolyLine(2.5f, from, to);

            var direction = (to - from).normalized;
            var middle = Vector2.Lerp(from, to, 0.5f);
            var side = new Vector2(-direction.y, direction.x) * 5f;
            Handles.DrawAAConvexPolygon(middle + direction * 7f, middle - direction * 3f + side, middle - direction * 3f - side);
        }

        private static void SpecialNode(Rect r, string text, Color fill)
        {
            EditorGUI.DrawRect(r, fill);
            if (r.height >= 10f)
                GUI.Label(r, text, new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Clip });
        }

        private static Color StatusColor(SliceStatus status)
        {
            switch (status)
            {
                case SliceStatus.Added: return new Color(0.42f, 0.86f, 0.5f, 1f);
                case SliceStatus.Removed: return new Color(0.95f, 0.38f, 0.33f, 1f);
                case SliceStatus.Changed: return new Color(0.96f, 0.76f, 0.26f, 1f);
                default: return new Color(0.1f, 0.1f, 0.1f, 1f);
            }
        }
    }

    // ================================================ override и маска ===

    internal sealed class OverrideControllerDescriber : ChangeDescriber<AnimatorOverrideController>
    {
        public override bool CoversSubAssets { get { return true; } }

        protected override void Describe(AnimatorOverrideController before, AnimatorOverrideController after, DescribeContext ctx)
        {
            if (!ctx.SameRef(before.runtimeAnimatorController, after.runtimeAnimatorController))
                ctx.Add("Controller", ChangeValue.Of(before.runtimeAnimatorController), ChangeValue.Of(after.runtimeAnimatorController))
                   .AddPath("m_Controller");

            var ob = Overrides(before);
            var oa = Overrides(after);

            ctx.Group = L.T("Clip Overrides");
            foreach (var pair in oa)
            {
                AnimationClip previous;
                ob.TryGetValue(pair.Key, out previous);
                if (ctx.SameRef(previous, pair.Value)) continue;

                var original = pair.Key;
                ctx.Add(L.F("“{0}”", original), ChangeValue.Of(previous), ChangeValue.Of(pair.Value), ChangeKind.Modified, "override:" + original)
                   .Revert = (live, src) =>
                   {
                       AnimationClip clip;
                       Overrides((AnimatorOverrideController)src).TryGetValue(original, out clip);
                       ((AnimatorOverrideController)live)[original] = clip;
                   };
            }
            ctx.Group = null;
        }

        private static Dictionary<string, AnimationClip> Overrides(AnimatorOverrideController controller)
        {
            var list = new List<KeyValuePair<AnimationClip, AnimationClip>>(controller.overridesCount);
            controller.GetOverrides(list);

            var result = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
            foreach (var pair in list)
                if (pair.Key != null && !result.ContainsKey(pair.Key.name)) result[pair.Key.name] = pair.Value;
            return result;
        }
    }

    internal sealed class AvatarMaskDescriber : ChangeDescriber<AvatarMask>
    {
        public override bool CoversSubAssets { get { return true; } }

        protected override void Describe(AvatarMask before, AvatarMask after, DescribeContext ctx)
        {
            ctx.Group = L.T("Body");
            for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
            {
                var part = (AvatarMaskBodyPart)i;
                bool b = before.GetHumanoidBodyPartActive(part), a = after.GetHumanoidBodyPartActive(part);
                if (b == a) continue;

                ctx.Add(ObjectNames.NicifyVariableName(part.ToString()), ChangeValue.Of(b), ChangeValue.Of(a), ChangeKind.Modified, "body:" + part)
                   .Revert = (live, src) => ((AvatarMask)live).SetHumanoidBodyPartActive(part, ((AvatarMask)src).GetHumanoidBodyPartActive(part));
            }

            ctx.Group = L.T("Bones");
            var tb = Transforms(before);
            var ta = Transforms(after);

            foreach (var pair in ta)
            {
                bool previous;
                if (!tb.TryGetValue(pair.Key, out previous))
                    ctx.Add(pair.Key, ChangeValue.None, ChangeValue.Of(pair.Value), ChangeKind.Added).Note = L.Tc("bone", "added");
                else if (previous != pair.Value)
                {
                    var path = pair.Key;
                    ctx.Add(path, ChangeValue.Of(previous), ChangeValue.Of(pair.Value), ChangeKind.Modified, "bone:" + path)
                       .Revert = (live, src) =>
                       {
                           var mask = (AvatarMask)live;
                           bool value;
                           if (!Transforms((AvatarMask)src).TryGetValue(path, out value)) return;
                           for (int i = 0; i < mask.transformCount; i++)
                               if (mask.GetTransformPath(i) == path) mask.SetTransformActive(i, value);
                       };
                }
            }

            foreach (var pair in tb)
                if (!ta.ContainsKey(pair.Key))
                    ctx.Add(pair.Key, ChangeValue.Of(pair.Value), ChangeValue.None, ChangeKind.Removed).Note = L.Tc("bone", "removed");

            ctx.Group = null;
        }

        private static Dictionary<string, bool> Transforms(AvatarMask mask)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int i = 0; i < mask.transformCount; i++) result[mask.GetTransformPath(i)] = mask.GetTransformActive(i);
            return result;
        }
    }
}
