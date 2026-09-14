using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lev.Git.Samples.Levels
{
    /// <summary>
    /// Уровень игры — пример собственного ассета, для которого пакет Git for Unity
    /// показывает изменения по-своему: картой, а не списком полей.
    /// </summary>
    [CreateAssetMenu(menuName = "Lev Git Samples/Level Config", fileName = "Level")]
    public sealed class LevelConfig : ScriptableObject
    {
        [Serializable]
        public sealed class Wave
        {
            public string id = "wave";
            [Min(0)] public int enemies = 5;
            [Min(0)] public float delay = 2f;
        }

        public string title = "New Level";

        [Min(1)] public float timeLimit = 60f;

        [Tooltip("Map by rows: . — floor, # — wall, S — start, E — enemy, $ — reward")]
        public List<string> rows = new List<string>
        {
            "##########",
            "#S.....E.#",
            "#..###...#",
            "#E...$...#",
            "##########"
        };

        public List<Wave> waves = new List<Wave>();

        public int Width
        {
            get
            {
                int width = 0;
                foreach (var row in rows) width = Math.Max(width, row != null ? row.Length : 0);
                return width;
            }
        }

        public int Height
        {
            get { return rows.Count; }
        }

        /// <summary>Клетка карты; за пределами строк — пробел.</summary>
        public char CellAt(int x, int y)
        {
            if (y < 0 || y >= rows.Count) return ' ';
            var row = rows[y];
            return row != null && x >= 0 && x < row.Length ? row[x] : ' ';
        }
    }
}
