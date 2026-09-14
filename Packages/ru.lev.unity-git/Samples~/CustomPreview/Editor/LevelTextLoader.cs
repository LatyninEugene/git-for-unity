#if LEV_GIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Lev.Git.Preview;
using UnityEngine;

namespace Lev.Git.Samples.Levels.Editor
{
    /// <summary>
    /// Загрузчик своего формата: уровень в простом текстовом файле .level.
    ///
    /// Нужен, только когда формат пакету незнаком. Ассет LevelConfig (.asset) —
    /// обычный YAML, его пакет читает сам, и описатель с показом работают без
    /// загрузчика. Здесь версия из git превращается в тот же LevelConfig — и
    /// дальше срабатывают те же описатель и показ.
    ///
    /// Формат:
    ///   title: Dungeon
    ///   time: 45
    ///   wave: boss 8 3.5
    ///   ##########
    ///   #S.....E.#
    /// </summary>
    public sealed class LevelTextLoader : AssetLoader
    {
        /// <summary>
        /// И рабочую копию читать из файла: в проекте .level — просто файл, и
        /// загруженный Unity объект не был бы LevelConfig.
        /// </summary>
        public override bool PreferFileForWorktree
        {
            get { return true; }
        }

        public override bool CanLoad(string projectPath)
        {
            return projectPath.EndsWith(".level", StringComparison.OrdinalIgnoreCase);
        }

        public override LoadedAsset Load(string projectPath, byte[] bytes)
        {
            var level = ScriptableObject.CreateInstance<LevelConfig>();

            // Объект версии — временный: скрыт и не сохраняется. Панель уничтожит
            // его сама, когда версия станет не нужна.
            level.hideFlags = HideFlags.HideAndDontSave;
            level.name = Path.GetFileNameWithoutExtension(projectPath);
            level.rows = new List<string>();
            level.waves = new List<LevelConfig.Wave>();

            foreach (var raw in Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) continue;

                if (line.StartsWith("title:", StringComparison.Ordinal))
                {
                    level.title = line.Substring(6).Trim();
                }
                else if (line.StartsWith("time:", StringComparison.Ordinal))
                {
                    float time;
                    if (float.TryParse(line.Substring(5).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out time))
                        level.timeLimit = time;
                }
                else if (line.StartsWith("wave:", StringComparison.Ordinal))
                {
                    var parts = line.Substring(5).Trim().Split(' ');
                    var wave = new LevelConfig.Wave { id = parts[0] };
                    if (parts.Length > 1) int.TryParse(parts[1], out wave.enemies);
                    if (parts.Length > 2) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out wave.delay);
                    level.waves.Add(wave);
                }
                else
                {
                    level.rows.Add(line);
                }
            }

            if (level.rows.Count == 0)
            {
                UnityEngine.Object.DestroyImmediate(level);
                return LoadedAsset.Failed("The file has no level map.");
            }

            var loaded = new LoadedAsset
            {
                Main = level,
                All = new UnityEngine.Object[] { level },

                // Описывать по полям можно: это настоящий LevelConfig.
                Describable = true,
                Info = level.Width + "×" + level.Height + " · " + PreviewText.Bytes(bytes.Length)
            };

            // Сведения о файле попадают в «Изменения» сами — строками группы «Файл».
            loaded.AddFact("Map Size", level.Width + "×" + level.Height);
            loaded.AddFact("Waves", level.waves.Count.ToString());
            return loaded;
        }
    }
}
#endif
