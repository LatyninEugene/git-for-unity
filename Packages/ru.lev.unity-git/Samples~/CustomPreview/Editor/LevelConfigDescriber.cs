#if LEV_GIT
using System;
using System.Collections.Generic;
using Lev.Git.Preview;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lev.Git.Samples.Levels.Editor
{
    /// <summary>
    /// Изменения уровня словами геймдизайнера для вкладки «Изменения»:
    /// «Время на уровень: 60 с → 45 с», «Карта: клеток изменено 3 (стен +2)»,
    /// «Волна boss: врагов 5 → 8».
    ///
    /// Находится сам: пакет ищет наследников ChangeDescriber через TypeCache и
    /// выбирает самый подходящий по типу. Без этого класса изменения уровня
    /// тоже были бы видны — общим описателем, по сериализованным полям.
    /// </summary>
    public sealed class LevelConfigDescriber : ChangeDescriber<LevelConfig>
    {
        private static readonly Dictionary<char, string> CellNames = new Dictionary<char, string>
        {
            { '#', "walls" }, { 'E', "enemies" }, { '$', "rewards" }, { 'S', "starts" }, { '.', "floor" }
        };

        protected override void Describe(LevelConfig before, LevelConfig after, DescribeContext ctx)
        {
            if (before.title != after.title)
            {
                // AddPath — сериализованное поле: по нему пункт откатывается и
                // отмечается в инспекторе «Поля». Своего кода отката не нужно.
                ctx.Add("Title",ChangeValue.Of(before.title), ChangeValue.Of(after.title), ChangeKind.Modified, "title")
                   .AddPath("title");
            }

            if (!Mathf.Approximately(before.timeLimit, after.timeLimit))
            {
                ctx.Add("Time Limit",Seconds(before.timeLimit), Seconds(after.timeLimit), ChangeKind.Modified, "timeLimit")
                   .AddPath("timeLimit");
            }

            DescribeMap(before, after, ctx);
            DescribeWaves(before, after, ctx);
        }

        private static ChangeValue Seconds(float value)
        {
            return ChangeValue.Of(PreviewText.Number(value) + " s");
        }

        private static void DescribeMap(LevelConfig before, LevelConfig after, DescribeContext ctx)
        {
            ctx.Group = "Map";

            if (before.Width != after.Width || before.Height != after.Height)
                ctx.Add("Size",ChangeValue.Of(before.Width + "×" + before.Height), ChangeValue.Of(after.Width + "×" + after.Height),
                        ChangeKind.Modified, "rows.size");

            int width = Math.Max(before.Width, after.Width);
            int height = Math.Max(before.Height, after.Height);
            int changed = 0;
            var balance = new Dictionary<char, int>();

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                char was = before.CellAt(x, y), now = after.CellAt(x, y);
                if (was == now) continue;

                changed++;
                Count(balance, was, -1);
                Count(balance, now, +1);
            }

            if (changed > 0)
            {
                var parts = new List<string>();
                foreach (var pair in balance)
                {
                    string name;
                    if (pair.Value == 0 || !CellNames.TryGetValue(pair.Key, out name)) continue;
                    parts.Add(name + " " + (pair.Value > 0 ? "+" : "−") + Math.Abs(pair.Value));
                }

                var item = ctx.Add("Cells Changed",ChangeValue.Of("—"), ChangeValue.Of(changed.ToString()), ChangeKind.Modified, "rows");
                item.Note = parts.Count > 0 ? string.Join(", ", parts.ToArray()) : null;
                item.AddPath("rows");
            }

            ctx.Group = null;
        }

        private static void Count(Dictionary<char, int> balance, char cell, int delta)
        {
            int value;
            balance.TryGetValue(cell, out value);
            balance[cell] = value + delta;
        }

        private static void DescribeWaves(LevelConfig before, LevelConfig after, DescribeContext ctx)
        {
            ctx.Group = "Waves";

            var old = new Dictionary<string, LevelConfig.Wave>();
            foreach (var wave in before.waves) if (wave != null) old[wave.id] = wave;

            var seen = new HashSet<string>();
            foreach (var wave in after.waves)
            {
                if (wave == null) continue;
                seen.Add(wave.id);

                LevelConfig.Wave previous;
                if (!old.TryGetValue(wave.id, out previous))
                {
                    ctx.Add("“" + wave.id + "”", ChangeValue.None, ChangeValue.Of(Text(wave)), ChangeKind.Added, "wave:" + wave.id)
                       .AddPath("waves");
                    continue;
                }

                if (previous.enemies == wave.enemies && Mathf.Approximately(previous.delay, wave.delay)) continue;

                var id = wave.id;
                var item = ctx.Add("“" + id + "”", ChangeValue.Of(Text(previous)), ChangeValue.Of(Text(wave)), ChangeKind.Modified, "wave:" + id);

                // Свой откат — когда сериализованный путь ничего не говорит: волна
                // ищется по id, а не по номеру в списке, который мог сдвинуться.
                item.Revert = (Object live, Object source) =>
                {
                    var target = Find((LevelConfig)live, id);
                    var from = Find((LevelConfig)source, id);
                    if (target == null || from == null) return;
                    target.enemies = from.enemies;
                    target.delay = from.delay;
                };
            }

            foreach (var wave in before.waves)
                if (wave != null && !seen.Contains(wave.id))
                    ctx.Add("“" + wave.id + "”", ChangeValue.Of(Text(wave)), ChangeValue.None, ChangeKind.Removed, "wave:" + wave.id)
                       .AddPath("waves");

            ctx.Group = null;
        }

        private static LevelConfig.Wave Find(LevelConfig level, string id)
        {
            foreach (var wave in level.waves) if (wave != null && wave.id == id) return wave;
            return null;
        }

        private static string Text(LevelConfig.Wave wave)
        {
            return "enemies " + wave.enemies + ", delay " + PreviewText.Number(wave.delay) + " s";
        }
    }
}
#endif
