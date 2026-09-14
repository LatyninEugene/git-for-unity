#if LEV_GIT
using System;
using System.Collections.Generic;
using Lev.Git.Preview;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lev.Git.Samples.Levels.Editor
{
    /// <summary>
    /// Уровень на вкладке «Вид» — картой. Изменённые клетки обведены жёлтым.
    ///
    /// Показ рисует только одну сторону — текстурой. Раскладку сторон (рядом,
    /// шторка, наложение, разница), лупу и миниатюры в «Истории ассета» делает
    /// панель пакета: достаточно сказать, что показ умеет (Modes, Features).
    /// </summary>
    public sealed class LevelConfigPresenter : AssetPresenter
    {
        private const int Cell = 12;

        /// <summary>Карта по содержимому: у живой стороны оно меняется, и текстура должна обновиться.</summary>
        private readonly Dictionary<string, Texture2D> _maps = new Dictionary<string, Texture2D>();

        public override Type Target
        {
            get { return typeof(LevelConfig); }
        }

        public override PresenterFeatures Features
        {
            get { return PresenterFeatures.Zoom; }
        }

        public override float Aspect(LoadedAsset side)
        {
            var level = side != null ? side.Main as LevelConfig : null;
            return level != null && level.Height > 0 ? level.Width / (float)level.Height : 1f;
        }

        public override Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync)
        {
            var level = side.Main as LevelConfig;
            if (level == null || level.Width == 0 || level.Height == 0) return null;

            var key = string.Join("\n", level.rows.ToArray());
            Texture2D map;
            if (_maps.TryGetValue(key, out map) && map != null) return map;

            map = Build(level);
            _maps[key] = map;
            return map;
        }

        private static Texture2D Build(LevelConfig level)
        {
            int w = level.Width, h = level.Height;
            var texture = new Texture2D(w * Cell, h * Cell, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[texture.width * texture.height];
            var grid = new Color32(0, 0, 0, 60);

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var color = ColorOf(level.CellAt(x, y));

                // Строка 0 карты — сверху, а у текстуры Unity нулевая строка пикселей — снизу.
                int top = (h - 1 - y) * Cell;
                for (int py = 0; py < Cell; py++)
                for (int px = 0; px < Cell; px++)
                {
                    bool edge = px == 0 || py == 0;
                    pixels[(top + py) * texture.width + x * Cell + px] = edge ? Blend(color, grid) : color;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        private static Color32 ColorOf(char cell)
        {
            switch (cell)
            {
                case '#': return new Color32(70, 74, 82, 255);
                case 'S': return new Color32(80, 170, 240, 255);
                case 'E': return new Color32(220, 80, 70, 255);
                case '$': return new Color32(240, 200, 60, 255);
                case '.': return new Color32(190, 196, 180, 255);
                default: return new Color32(0, 0, 0, 0);
            }
        }

        private static Color32 Blend(Color32 a, Color32 b)
        {
            float t = b.a / 255f;
            return new Color32((byte)(a.r * (1 - t) + b.r * t), (byte)(a.g * (1 - t) + b.g * t), (byte)(a.b * (1 - t) + b.b * t), a.a);
        }

        /// <summary>Рамки клеток, которые отличаются от другой стороны. Учитывает лупу панели через source.</summary>
        public override void DrawOverlay(Rect fit, Rect source, LoadedAsset side, LoadedAsset other, bool isAfter, PreviewSync sync)
        {
            var level = side.Main as LevelConfig;
            var otherLevel = other != null ? other.Main as LevelConfig : null;
            if (level == null || otherLevel == null) return;

            int w = level.Width, h = level.Height;
            if (w == 0 || h == 0) return;

            var highlight = new Color(1f, 0.85f, 0.2f, 0.95f);

            GUI.BeginClip(fit);
            try
            {
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (level.CellAt(x, y) == otherLevel.CellAt(x, y)) continue;

                    float u0 = x / (float)w, u1 = (x + 1) / (float)w;
                    float v0 = (h - 1 - y) / (float)h, v1 = (h - y) / (float)h;

                    var r = Rect.MinMaxRect(
                        (u0 - source.x) / source.width * fit.width,
                        fit.height - (v1 - source.y) / source.height * fit.height,
                        (u1 - source.x) / source.width * fit.width,
                        fit.height - (v0 - source.y) / source.height * fit.height);

                    if (r.xMax < 0f || r.x > fit.width || r.yMax < 0f || r.y > fit.height) continue;
                    PreviewCanvas.Outline(r, highlight, 2f);
                }
            }
            finally
            {
                GUI.EndClip();
            }

            GUI.Label(new Rect(fit.x + 4f, fit.yMax - 18f, fit.width - 8f, 16f), level.title, EditorStyles.miniBoldLabel);
        }

        public override void Dispose()
        {
            foreach (var map in _maps.Values)
                if (map != null) Object.DestroyImmediate(map);
            _maps.Clear();
        }
    }
}
#endif
