using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace Lev.Git.Preview.Timeline
{
    internal static class TimelineTracks
    {
        /// <summary>Дорожки по порядку, с путём через группы: «Персонажи / Анимация».</summary>
        public static List<KeyValuePair<string, TrackAsset>> Of(TimelineAsset timeline)
        {
            var result = new List<KeyValuePair<string, TrackAsset>>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (timeline != null) Walk(timeline.GetRootTracks(), string.Empty, result, counts);
            return result;
        }

        private static void Walk(IEnumerable<TrackAsset> tracks, string prefix, List<KeyValuePair<string, TrackAsset>> result, Dictionary<string, int> counts)
        {
            foreach (var track in tracks)
            {
                if (track == null) continue;

                var path = prefix + track.name;
                int n;
                counts.TryGetValue(path, out n);
                counts[path] = n + 1;

                result.Add(new KeyValuePair<string, TrackAsset>(n == 0 ? path : path + " #" + (n + 1), track));
                Walk(track.GetChildTracks(), path + " / ", result, counts);
            }
        }

        public static Dictionary<string, TimelineClip> Clips(TrackAsset track)
        {
            var result = new Dictionary<string, TimelineClip>(StringComparer.Ordinal);
            if (track == null) return result;

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var clip in track.GetClips())
            {
                int n;
                counts.TryGetValue(clip.displayName, out n);
                counts[clip.displayName] = n + 1;
                result[n == 0 ? clip.displayName : clip.displayName + " #" + (n + 1)] = clip;
            }

            return result;
        }

        public static string Summary(TimelineClip clip)
        {
            var text = Seconds(clip.start) + "–" + Seconds(clip.end);
            if (clip.clipIn > 0.0001) text += ", clip in " + Seconds(clip.clipIn);
            if (Math.Abs(clip.timeScale - 1.0) > 0.0001) text += ", ×" + PreviewText.Number(clip.timeScale);
            if (clip.easeInDuration > 0.0001 || clip.easeOutDuration > 0.0001)
                text += ", ease " + Seconds(clip.easeInDuration) + "/" + Seconds(clip.easeOutDuration);

            var asset = clip.asset;
            if (asset != null) text += ", " + asset.GetType().Name;
            return text;
        }

        public static string Seconds(double value)
        {
            return L.F("{0} s", PreviewText.Number(value));
        }
    }

    /// <summary>Изменения Timeline: дорожки, клипы на них (время, ускорение, переходы), маркеры.</summary>
    internal sealed class TimelineDescriber : ChangeDescriber<TimelineAsset>
    {
        public override bool CoversSubAssets { get { return true; } }

        protected override void Describe(TimelineAsset before, TimelineAsset after, DescribeContext ctx)
        {
            if (Math.Abs(before.duration - after.duration) > 0.0001)
                ctx.Add(L.T("Duration"), ChangeValue.Of(TimelineTracks.Seconds(before.duration)), ChangeValue.Of(TimelineTracks.Seconds(after.duration)));

            if (before.durationMode != after.durationMode)
                ctx.Add("Duration Mode", ChangeValue.Of(before.durationMode.ToString()), ChangeValue.Of(after.durationMode.ToString()));

            var tracksBefore = TimelineTracks.Of(before).ToDictionary(p => p.Key, p => p.Value);
            var tracksAfter = TimelineTracks.Of(after);

            foreach (var pair in tracksAfter)
            {
                TrackAsset previous;
                if (!tracksBefore.TryGetValue(pair.Key, out previous))
                {
                    ctx.Group = L.T("Tracks");
                    ctx.Add(L.F("“{0}”", pair.Key), ChangeValue.None, ChangeValue.Of(pair.Value.GetType().Name), ChangeKind.Added).Note = L.Tc("track", "added");
                    continue;
                }

                DescribeTrack(pair.Key, previous, pair.Value, ctx);
            }

            ctx.Group = L.T("Tracks");
            var remaining = new HashSet<string>(tracksAfter.Select(p => p.Key), StringComparer.Ordinal);
            foreach (var pair in tracksBefore)
                if (!remaining.Contains(pair.Key))
                    ctx.Add(L.F("“{0}”", pair.Key), ChangeValue.Of(pair.Value.GetType().Name), ChangeValue.None, ChangeKind.Removed).Note = L.Tc("track", "removed");

            ctx.Group = null;
        }

        private static void DescribeTrack(string path, TrackAsset before, TrackAsset after, DescribeContext ctx)
        {
            ctx.Group = L.F("Track “{0}”", path);

            if (before.muted != after.muted) ctx.Add("Mute", ChangeValue.Of(before.muted), ChangeValue.Of(after.muted));
            if (before.locked != after.locked) ctx.Add("Lock", ChangeValue.Of(before.locked), ChangeValue.Of(after.locked));

            var clipsBefore = TimelineTracks.Clips(before);
            var clipsAfter = TimelineTracks.Clips(after);

            foreach (var pair in clipsAfter)
            {
                TimelineClip previous;
                var now = TimelineTracks.Summary(pair.Value);

                if (!clipsBefore.TryGetValue(pair.Key, out previous))
                    ctx.Add(L.F("Clip “{0}”", pair.Key), ChangeValue.None, ChangeValue.Of(now), ChangeKind.Added).Note = L.T("added");
                else if (TimelineTracks.Summary(previous) != now)
                    ctx.Add(L.F("Clip “{0}”", pair.Key), ChangeValue.Of(TimelineTracks.Summary(previous)), ChangeValue.Of(now));
            }

            foreach (var pair in clipsBefore)
                if (!clipsAfter.ContainsKey(pair.Key))
                    ctx.Add(L.F("Clip “{0}”", pair.Key), ChangeValue.Of(TimelineTracks.Summary(pair.Value)), ChangeValue.None, ChangeKind.Removed).Note = L.T("removed");

            var markersBefore = string.Join(", ", before.GetMarkers().Select(m => PreviewText.Number(m.time)).ToArray());
            var markersAfter = string.Join(", ", after.GetMarkers().Select(m => PreviewText.Number(m.time)).ToArray());
            if (markersBefore != markersAfter)
                ctx.Add(L.T("Markers"), ChangeValue.Of(markersBefore.Length == 0 ? L.T("none") : markersBefore),
                        ChangeValue.Of(markersAfter.Length == 0 ? L.T("none") : markersAfter));
        }
    }

    /// <summary>
    /// Timeline дорожками на общей шкале: клипы на своих местах, добавленные —
    /// зелёные, удалённые — красные, изменённые — жёлтые. Стороны одна под
    /// другой, дорожки обеих версий в одном порядке — сдвиги видны сразу.
    /// </summary>
    internal sealed class TimelinePresenter : AssetPresenter
    {
        private readonly List<string> _lanes = new List<string>();
        private readonly PlaybackClock _clock = new PlaybackClock();
        private double _length = 1.0;

        public override Type Target { get { return typeof(TimelineAsset); } }
        public override CompareModes Modes { get { return CompareModes.SideBySide | CompareModes.Toggle; } }
        public override PresenterFeatures Features { get { return PresenterFeatures.Playback; } }
        public override bool PlaybackPerSide { get { return false; } }
        public override bool NeedsRepaint { get { return _clock.Playing; } }
        public override bool StackSides { get { return true; } }

        public override void Prepare(LoadedAsset before, LoadedAsset after)
        {
            var b = before != null ? before.Main as TimelineAsset : null;
            var a = after != null ? after.Main as TimelineAsset : null;

            _length = Math.Max(0.01, Math.Max(b != null ? b.duration : 0, a != null ? a.duration : 0));
            _clock.Length = (float)_length;

            _lanes.Clear();
            foreach (var pair in TimelineTracks.Of(a).Concat(TimelineTracks.Of(b)))
                if (!_lanes.Contains(pair.Key)) _lanes.Add(pair.Key);
        }

        public override float Length(LoadedAsset side) { return (float)_length; }
        public override void Play(LoadedAsset side, int slot, float time) { _clock.Play(slot, time); }
        public override void Stop() { _clock.Stop(); }
        public override void Seek(LoadedAsset side, int slot, float time) { _clock.Seek(time); }

        public override Rect TimeArea(Rect fit)
        {
            float header = Mathf.Min(150f, fit.width * 0.3f);
            return new Rect(fit.x + header, fit.y, Mathf.Max(1f, fit.width - header - 4f), fit.height);
        }
        public override bool Playing { get { return _clock.Playing; } }
        public override int PlayingSlot { get { return _clock.Slot; } }
        public override float PlayTime { get { return _clock.Time; } }

        public override Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync)
        {
            return PreviewCanvas.Blank;
        }

        public override void DrawOverlay(Rect fit, Rect source, LoadedAsset side, LoadedAsset other, bool isAfter, PreviewSync sync)
        {
            var timeline = side.Main as TimelineAsset;
            if (timeline == null) return;

            if (_lanes.Count == 0)
            {
                GUI.Label(fit, L.T("No tracks"), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var mine = TimelineTracks.Of(timeline).ToDictionary(p => p.Key, p => p.Value);
            var theirs = other != null && other.Main is TimelineAsset
                ? TimelineTracks.Of((TimelineAsset)other.Main).ToDictionary(p => p.Key, p => p.Value)
                : null;

            float top = fit.y + 18f;
            float row = Mathf.Clamp((fit.height - 20f) / _lanes.Count, 12f, 24f);
            float header = Mathf.Min(150f, fit.width * 0.3f);
            float width = fit.width - header - 4f;

            for (int i = 0; i < _lanes.Count; i++)
            {
                var lane = new Rect(fit.x, top + i * row, fit.width, row - 2f);
                if (lane.yMax > fit.yMax) break;

                EditorGUI.DrawRect(lane, new Color(1f, 1f, 1f, i % 2 == 0 ? 0.04f : 0.07f));
                GUI.Label(new Rect(lane.x + 4f, lane.y, header - 6f, lane.height), _lanes[i], EditorStyles.miniLabel);

                TrackAsset track;
                if (!mine.TryGetValue(_lanes[i], out track)) continue;

                TrackAsset otherTrack = null;
                var otherClips = theirs != null && theirs.TryGetValue(_lanes[i], out otherTrack) ? TimelineTracks.Clips(otherTrack) : null;

                foreach (var pair in TimelineTracks.Clips(track))
                {
                    var clip = pair.Value;
                    float x0 = fit.x + header + (float)(clip.start / _length) * width;
                    float x1 = fit.x + header + (float)(clip.end / _length) * width;
                    var r = new Rect(x0, lane.y + 1f, Mathf.Max(2f, x1 - x0), lane.height - 2f);

                    TimelineClip counterpart = null;
                    var status = theirs == null ? SliceStatus.Same
                        : otherClips == null || !otherClips.TryGetValue(pair.Key, out counterpart) ? (isAfter ? SliceStatus.Added : SliceStatus.Removed)
                        : TimelineTracks.Summary(counterpart) == TimelineTracks.Summary(clip) ? SliceStatus.Same
                        : SliceStatus.Changed;

                    EditorGUI.DrawRect(r, ColorOf(status));
                    if (r.width > 30f) GUI.Label(r, clip.displayName, new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip });
                }
            }

            float time = _clock.Time;
            if (time > 0f || _clock.Playing)
            {
                float x = fit.x + header + time / (float)_length * width;
                EditorGUI.DrawRect(new Rect(x - 1f, fit.y + 16f, 2f, fit.height - 16f), new Color(1f, 1f, 1f, 0.8f));
            }
        }

        private static Color ColorOf(SliceStatus status)
        {
            switch (status)
            {
                case SliceStatus.Added: return new Color(0.3f, 0.66f, 0.38f, 0.9f);
                case SliceStatus.Removed: return new Color(0.76f, 0.3f, 0.26f, 0.9f);
                case SliceStatus.Changed: return new Color(0.8f, 0.62f, 0.2f, 0.9f);
                default: return new Color(0.32f, 0.42f, 0.56f, 0.9f);
            }
        }
    }
}
