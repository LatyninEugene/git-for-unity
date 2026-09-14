using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>
    /// Иконки пакета: окно Git и ветка на главной панели редактора.
    ///
    /// Рисуются кодом, а не лежат картинками: в пакете не появляются бинарники
    /// с настройками импорта, а иконки перекрашиваются под скин редактора.
    /// Рисуются один раз, до перезагрузки домена или смены скина.
    /// </summary>
    public static class GitIcons
    {
        private const int Size = 32;
        private const int Samples = 4;

        private static Texture2D _window, _branch;
        private static bool _windowProSkin, _branchProSkin;

        /// <summary>Серый — как у встроенных иконок вкладок, чтобы окно не выбивалось из ряда.</summary>
        private static Color IconGray => EditorGUIUtility.isProSkin
            ? new Color(0.77f, 0.77f, 0.77f, 1f)
            : new Color(0.33f, 0.33f, 0.33f, 1f);

        /// <summary>Иконка окна: ромб логотипа git с вырезанной в нём веткой.</summary>
        public static Texture2D Window
        {
            get
            {
                bool pro = EditorGUIUtility.isProSkin;
                if (_window == null || _windowProSkin != pro)
                {
                    if (_window != null) Object.DestroyImmediate(_window);
                    _windowProSkin = pro;
                    _window = Draw(true, IconGray);
                }
                return _window;
            }
        }

        /// <summary>Значок ветки цвета текста панели.</summary>
        public static Texture2D Branch
        {
            get
            {
                bool pro = EditorGUIUtility.isProSkin;
                if (_branch == null || _branchProSkin != pro)
                {
                    if (_branch != null) Object.DestroyImmediate(_branch);
                    _branchProSkin = pro;
                    _branch = Draw(false, IconGray);
                }
                return _branch;
            }
        }

        /// <param name="logo">Ромб цвета <paramref name="color"/> с веткой-вырезом; иначе одна ветка этого цвета.</param>
        private static Texture2D Draw(bool logo, Color color)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = logo ? "Git" : "Git Branch"
            };

            var pixels = new Color[Size * Size];
            float step = 1f / Samples;

            for (int py = 0; py < Size; py++)
            for (int px = 0; px < Size; px++)
            {
                int diamond = 0, glyph = 0;
                for (int sy = 0; sy < Samples; sy++)
                for (int sx = 0; sx < Samples; sx++)
                {
                    float u = (px + (sx + 0.5f) * step) / Size;
                    // Строки текстуры идут снизу вверх, а рисуем сверху вниз.
                    float v = 1f - (py + (sy + 0.5f) * step) / Size;

                    if (logo)
                    {
                        bool inside = Mathf.Abs(u - 0.5f) + Mathf.Abs(v - 0.5f) <= 0.49f;
                        if (!inside) continue;
                        diamond++;
                        // Ветка внутри ромба — уменьшенная, чтобы не вылезать за края.
                        if (Glyph((u - 0.5f) / 0.56f + 0.5f, (v - 0.5f) / 0.56f + 0.5f)) glyph++;
                    }
                    else if (Glyph(u, v))
                    {
                        glyph++;
                    }
                }

                float total = Samples * Samples;
                var c = color;
                // У логотипа ветка — прозрачный вырез в ромбе: так он читается одним цветом.
                c.a = logo ? (diamond - glyph) / total : glyph / total;
                pixels[py * Size + px] = c;
            }

            tex.SetPixels(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>Ветка: ствол с двумя точками и отросток к третьей точке.</summary>
        private static bool Glyph(float x, float y)
        {
            const float Stroke = 0.07f;
            const float Dot = 0.115f;

            float ax = 0.34f, ay = 0.17f;   // верх ствола
            float bx = 0.34f, by = 0.83f;   // низ ствола
            float cx = 0.70f, cy = 0.30f;   // конец отростка

            if (Near(x, y, ax, ay, Dot) || Near(x, y, bx, by, Dot) || Near(x, y, cx, cy, Dot)) return true;
            if (Segment(x, y, ax, ay, bx, by) <= Stroke) return true;

            // Отросток — квадратичная кривая от точки вниз к стволу.
            float px = cx, py = cy;
            for (int i = 1; i <= 14; i++)
            {
                float t = i / 14f;
                float k = 1f - t;
                float qx = k * k * cx + 2f * k * t * 0.70f + t * t * bx;
                float qy = k * k * cy + 2f * k * t * 0.66f + t * t * 0.70f;
                if (Segment(x, y, px, py, qx, qy) <= Stroke) return true;
                px = qx;
                py = qy;
            }
            return false;
        }

        private static bool Near(float x, float y, float cx, float cy, float r)
        {
            float dx = x - cx, dy = y - cy;
            return dx * dx + dy * dy <= r * r;
        }

        private static float Segment(float x, float y, float ax, float ay, float bx, float by)
        {
            float vx = bx - ax, vy = by - ay;
            float len = vx * vx + vy * vy;
            float t = len > 0f ? Mathf.Clamp01(((x - ax) * vx + (y - ay) * vy) / len) : 0f;
            float dx = x - (ax + t * vx), dy = y - (ay + t * vy);
            return Mathf.Sqrt(dx * dx + dy * dy);
        }
    }
}
