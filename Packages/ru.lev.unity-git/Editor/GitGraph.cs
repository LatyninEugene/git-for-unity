using System;
using System.Collections.Generic;

namespace Lev.Git
{
    /// <summary>
    /// Раскладка коммитов по колонкам графа.
    ///
    /// Работает одним проходом сверху вниз и хранит только «чего ждёт каждая
    /// колонка»: список sha, по одному на колонку. Встретив коммит, забираем
    /// колонку, которая его ждала, отдаём её первому родителю, а остальным
    /// родителям выделяем свободные — так ветвление уходит вправо, а слияние
    /// возвращает колонки обратно.
    ///
    /// Колонки намеренно не уплотняются: если сдвигать линии влево при каждом
    /// освобождении, вертикальная линия ветки будет прыгать по экрану на каждом
    /// чужом слиянии, и взглядом её не проследить.
    /// </summary>
    public static class GitGraphBuilder
    {
        /// <summary>
        /// Заполняет Lane, Edges и LaneCount. Возвращает наибольшую ширину —
        /// по ней задаётся ширина колонки графа, общая для всех строк.
        /// </summary>
        public static int Layout(List<GitCommit> commits)
        {
            var lanes = new List<string>();
            int max = 1;

            foreach (var c in commits)
            {
                c.Edges.Clear();

                int mine = lanes.IndexOf(c.Sha);
                if (mine < 0) mine = Occupy(lanes, c.Sha);
                c.Lane = mine;

                // Слияние: все колонки, ждавшие этот коммит, сходятся в его точку.
                for (int i = 0; i < lanes.Count; i++)
                {
                    if (i == mine || lanes[i] != c.Sha) continue;
                    c.Edges.Add(new GitGraphEdge { From = i, To = mine });
                    lanes[i] = null;
                }

                // Чужие ветки идут сквозь строку, никуда не сворачивая.
                for (int i = 0; i < lanes.Count; i++)
                {
                    if (i == mine || lanes[i] == null) continue;
                    c.Edges.Add(new GitGraphEdge { From = i, To = i });
                }

                if (c.Parents.Length == 0)
                {
                    // Корень истории: линия обрывается на этой точке.
                    lanes[mine] = null;
                }
                else
                {
                    lanes[mine] = c.Parents[0];
                    c.Edges.Add(new GitGraphEdge { From = mine, To = mine, FromCommit = true });

                    for (int p = 1; p < c.Parents.Length; p++)
                    {
                        int lane = lanes.IndexOf(c.Parents[p]);
                        if (lane < 0) lane = Occupy(lanes, c.Parents[p]);
                        c.Edges.Add(new GitGraphEdge { From = mine, To = lane, FromCommit = true });
                    }
                }

                c.LaneCount = Width(lanes, c);
                if (c.LaneCount > max) max = c.LaneCount;
            }

            return max;
        }

        /// <summary>Занимает свободную колонку, иначе добавляет новую справа.</summary>
        private static int Occupy(List<string> lanes, string sha)
        {
            for (int i = 0; i < lanes.Count; i++)
            {
                if (lanes[i] == null) { lanes[i] = sha; return i; }
            }

            lanes.Add(sha);
            return lanes.Count - 1;
        }

        private static int Width(List<string> lanes, GitCommit c)
        {
            int w = c.Lane + 1;

            for (int i = 0; i < lanes.Count; i++)
                if (lanes[i] != null && i + 1 > w) w = i + 1;

            foreach (var e in c.Edges)
            {
                if (e.From + 1 > w) w = e.From + 1;
                if (e.To + 1 > w) w = e.To + 1;
            }

            return w;
        }
    }
}
