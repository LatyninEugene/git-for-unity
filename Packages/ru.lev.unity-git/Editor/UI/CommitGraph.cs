using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git.UI
{
    /// <summary>
    /// Колонка графа для ОДНОЙ строки журнала.
    ///
    /// Рисуется по отрезкам самого коммита и ничего не знает о соседях —
    /// иначе список нельзя было бы виртуализировать: ListView переиспользует
    /// строки и в произвольном порядке привязывает их к разным коммитам.
    /// Непрерывность линий обеспечивает раскладка: отрезок соседней строки
    /// приходит в ту же колонку, и стык получается сам собой.
    /// </summary>
    public sealed class CommitGraph : VisualElement
    {
        /// <summary>Шаг между колонками.</summary>
        public const float LaneWidth = 13f;

        /// <summary>
        /// Дальше колонки не расходятся: очень ветвистая история иначе съела бы
        /// всю строку, а читают в ней всё-таки заголовок коммита.
        /// </summary>
        public const int MaxLanes = 9;

        private const float DotRadius = 3.6f;
        private const float LineWidth = 1.8f;

        private GitCommit _commit;
        private bool _dim;

        public CommitGraph()
        {
            pickingMode = PickingMode.Ignore;
            style.flexShrink = 0f;

            // Растягиваемся на всю высоту строки явно. Строка выравнивает детей
            // по центру, а у элемента без содержимого своя высота нулевая —
            // contentRect пуст, и граф не рисовался вовсе.
            style.alignSelf = Align.Stretch;
            generateVisualContent += Paint;
        }

        /// <summary>
        /// Привязывает строку. <paramref name="lanes"/> — ширина графа на весь
        /// журнал, одна на все строки: иначе заголовки коммитов прыгали бы влево
        /// и вправо в зависимости от ветвистости конкретного места истории.
        /// </summary>
        public void Bind(GitCommit commit, int lanes, bool dim)
        {
            _commit = commit;
            _dim = dim;

            float w = Mathf.Clamp(lanes, 1, MaxLanes) * LaneWidth;
            style.width = w;
            style.minWidth = w;

            MarkDirtyRepaint();
        }

        private void Paint(MeshGenerationContext ctx)
        {
            var c = _commit;
            if (c == null) return;

            var rect = contentRect;
            if (rect.height <= 0f || rect.width <= 0f) return;

            var p = ctx.painter2D;
            float mid = rect.height * 0.5f;
            float alpha = _dim ? 0.35f : 1f;

            p.lineWidth = LineWidth;
            p.lineCap = LineCap.Round;

            foreach (var e in c.Edges)
            {
                // Линия, входящая в точку коммита, обрывается на ней, а не идёт
                // насквозь: иначе слияние выглядит как две независимые ветки,
                // случайно пересёкшиеся в одной строке.
                bool endsHere = e.To == c.Lane && !e.FromCommit;

                float x0 = X(e.From), x1 = X(e.To);
                float y0 = e.FromCommit ? mid : 0f;
                float y1 = endsHere ? mid : rect.height;

                // Цвет берём у колонки, которая продолжает жить: у сквозной это
                // она сама, у ответвления — новая, у слияния — та, в которую
                // линия пришла.
                var color = GitPalette.Lane(e.FromCommit ? e.To : e.From);
                color.a = alpha;
                p.strokeColor = color;

                p.BeginPath();
                p.MoveTo(new Vector2(x0, y0));

                if (Mathf.Approximately(x0, x1))
                {
                    p.LineTo(new Vector2(x1, y1));
                }
                else
                {
                    // Управляющие точки строго по вертикали: так изгиб выходит
                    // из колонки вверх-вниз и входит в соседнюю так же, без
                    // «клюва» в месте стыка с отрезком соседней строки.
                    float bend = (y1 - y0) * 0.5f;
                    p.BezierCurveTo(
                        new Vector2(x0, y0 + bend),
                        new Vector2(x1, y1 - bend),
                        new Vector2(x1, y1));
                }

                p.Stroke();
            }

            // Точка рисуется последней — поверх всех линий.
            var dot = GitPalette.Lane(c.Lane);
            dot.a = alpha;

            p.BeginPath();
            p.Arc(new Vector2(X(c.Lane), mid), DotRadius, 0f, 360f);

            if (c.IsMerge)
            {
                // Слияние — кольцом: по одному взгляду на столбец видно, где
                // ветки сходились, без чтения заголовков.
                p.strokeColor = dot;
                p.lineWidth = 2f;
                p.Stroke();
            }
            else
            {
                p.fillColor = dot;
                p.Fill();
            }
        }

        private static float X(int lane)
        {
            if (lane >= MaxLanes) lane = MaxLanes - 1;
            return lane * LaneWidth + LaneWidth * 0.5f;
        }
    }
}
