using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lev.Git
{
    /// <summary>
    /// Кэш разницы версий сцены на диске, в Library проекта.
    ///
    /// Хранится не лента событий, а разница пары версий файла — узлы вместе с
    /// документами обеих сторон. Такая разница не устаревает никогда:
    /// идентификатор версии в git — хеш содержимого, и одна и та же пара всегда
    /// даёт один и тот же результат. Сверять запись с HEAD, веткой или временем
    /// не нужно.
    ///
    /// Ленту из узлов история собирает заново при каждом открытии: это дёшево,
    /// и правка правил ленты не требует сбрасывать кэш. Сбрасывать его нужно
    /// только при изменении разбора YAML или семантического diff — для этого
    /// номер формата входит в имя папки.
    ///
    /// Чистый .NET без Unity: папку передаёт вызывающая сторона, поэтому запись
    /// и чтение проверяются тестами.
    /// </summary>
    public static class SceneDiffStore
    {
        /// <summary>
        /// Номер формата. Увеличивать при любом изменении UnityYamlParser,
        /// SceneDiffBuilder или PrefabOverrides, которое меняет результат
        /// разбора: записи прежнего формата перестанут находиться, а их папка
        /// удалится при следующем чтении истории.
        /// </summary>
        public const int FormatVersion = 1;

        /// <summary>Сколько записей держать. Сверх лимита удаляются самые старые.</summary>
        public const int MaxEntries = 4000;

        private const int Magic = 0x4447534C;

        public static string FolderIn(string libraryDir)
        {
            return Path.Combine(Path.Combine(Path.Combine(libraryDir, "LevGit"), "SceneDiffs"),
                                "v" + FormatVersion);
        }

        public static string FileFor(string folder, string oldBlob, string newBlob)
        {
            return Path.Combine(folder, Id(oldBlob) + "-" + Id(newBlob) + ".bin");
        }

        private static string Id(string blob)
        {
            return string.IsNullOrEmpty(blob) ? "0" : blob;
        }

        // ------------------------------------------------------------ диск ---

        /// <summary>Разница из кэша или null — записи нет или она повреждена.</summary>
        public static List<SceneNode> Load(string folder, string oldBlob, string newBlob)
        {
            var file = FileFor(folder, oldBlob, newBlob);
            if (!File.Exists(file)) return null;

            List<SceneNode> nodes = null;

            try
            {
                using (var stream = File.OpenRead(file)) nodes = Read(stream);
            }
            catch
            {
                // Файл оборван — например, редактор закрыли посреди записи.
                // Считаем, что записи нет: разница просто посчитается заново.
                nodes = null;
            }

            // Удалять — только после закрытия потока: на Windows открытый файл
            // не удаляется, и повреждённая запись осталась бы лежать навсегда.
            if (nodes == null) TryDelete(file);
            return nodes;
        }

        public static void Save(string folder, string oldBlob, string newBlob, List<SceneNode> nodes)
        {
            if (nodes == null) return;

            string temp = null;

            try
            {
                Directory.CreateDirectory(folder);

                var file = FileFor(folder, oldBlob, newBlob);
                temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";

                // Сначала во временный файл, потом переименование: оборванная
                // запись не должна оставить на месте кэша полфайла.
                using (var stream = File.Create(temp)) Write(stream, nodes);

                if (File.Exists(file)) File.Delete(file);
                File.Move(temp, file);
                temp = null;
            }
            catch
            {
                // Кэш — ускорение, а не данные: не записался — посчитаем снова.
            }
            finally
            {
                if (temp != null) TryDelete(temp);
            }
        }

        /// <summary>
        /// Держит размер кэша в пределах: сверх лимита удаляются самые давние
        /// записи, заодно — временные файлы от оборванных записей.
        /// </summary>
        public static void Trim(string folder, int maxEntries)
        {
            try
            {
                if (!Directory.Exists(folder)) return;

                var dir = new DirectoryInfo(folder);

                foreach (var tmp in dir.GetFiles("*.tmp"))
                    if (DateTime.UtcNow - tmp.LastWriteTimeUtc > TimeSpan.FromHours(1)) TryDelete(tmp.FullName);

                var files = dir.GetFiles("*.bin");
                if (files.Length <= maxEntries) return;

                // По времени записи: время последнего чтения на Windows часто не
                // ведётся, и полагаться на него нельзя.
                Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

                for (int i = 0; i < files.Length - maxEntries; i++) TryDelete(files[i].FullName);
            }
            catch
            {
                // Не удалось прибраться — не беда, попробуем в другой раз.
            }
        }

        /// <summary>Удаляет папки кэша прежних форматов: читать их больше некому.</summary>
        public static void DropOtherVersions(string libraryDir)
        {
            try
            {
                var parent = Path.Combine(Path.Combine(libraryDir, "LevGit"), "SceneDiffs");
                if (!Directory.Exists(parent)) return;

                var current = "v" + FormatVersion;

                foreach (var dir in Directory.GetDirectories(parent))
                {
                    if (string.Equals(Path.GetFileName(dir), current, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // Папку держит другой процесс — удалим в следующий раз.
            }
        }

        private static void TryDelete(string file)
        {
            try { File.Delete(file); } catch { }
        }

        // ------------------------------------------------------ сериализация ---

        public static void Write(Stream stream, List<SceneNode> nodes)
        {
            using (var w = new BinaryWriter(stream, new UTF8Encoding(false), true))
            {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(nodes.Count);
                foreach (var n in nodes) WriteNode(w, n);
            }
        }

        /// <summary>Узлы из потока или null, если это не наш формат.</summary>
        public static List<SceneNode> Read(Stream stream)
        {
            using (var r = new BinaryReader(stream, new UTF8Encoding(false), true))
            {
                if (stream.Length - stream.Position < 12) return null;
                if (r.ReadInt32() != Magic) return null;
                if (r.ReadInt32() != FormatVersion) return null;

                int count = r.ReadInt32();
                var nodes = new List<SceneNode>(Math.Max(0, count));
                for (int i = 0; i < count; i++) nodes.Add(ReadNode(r));
                return nodes;
            }
        }

        private static void WriteNode(BinaryWriter w, SceneNode n)
        {
            w.Write((byte)n.Kind);
            WriteString(w, n.Title);
            w.Write(n.FileId);
            w.Write(n.TypeIndex);
            w.Write(n.HiddenProps);

            w.Write(n.Props.Count);
            foreach (var p in n.Props)
            {
                WriteString(w, p.Path);
                WriteString(w, p.Old);
                WriteString(w, p.New);
            }

            WriteDoc(w, n.OldDoc);
            WriteDoc(w, n.NewDoc);

            w.Write(n.Children.Count);
            foreach (var c in n.Children) WriteNode(w, c);
        }

        private static SceneNode ReadNode(BinaryReader r)
        {
            var n = new SceneNode
            {
                Kind = (SceneChangeKind)r.ReadByte(),
                Title = ReadString(r),
                FileId = r.ReadInt64(),
                TypeIndex = r.ReadInt32(),
                HiddenProps = r.ReadInt32()
            };

            int props = r.ReadInt32();
            for (int i = 0; i < props; i++)
                n.Props.Add(new ScenePropertyChange { Path = ReadString(r), Old = ReadString(r), New = ReadString(r) });

            n.OldDoc = ReadDoc(r);
            n.NewDoc = ReadDoc(r);

            int children = r.ReadInt32();
            for (int i = 0; i < children; i++) n.Children.Add(ReadNode(r));

            return n;
        }

        /// <summary>
        /// Документ пишется целиком, а не только изменившиеся поля: по нему окно
        /// «было и стало» и история компонента рисуют полный инспектор.
        /// </summary>
        private static void WriteDoc(BinaryWriter w, UnityDocument doc)
        {
            w.Write(doc != null);
            if (doc == null) return;

            w.Write(doc.FileId);
            w.Write(doc.ClassId);
            WriteString(w, doc.TypeName);

            w.Write(doc.Props.Count);
            foreach (var pair in doc.Props)
            {
                WriteString(w, pair.Key);
                WriteString(w, pair.Value);
            }
        }

        private static UnityDocument ReadDoc(BinaryReader r)
        {
            if (!r.ReadBoolean()) return null;

            var doc = new UnityDocument
            {
                FileId = r.ReadInt64(),
                ClassId = r.ReadInt32(),
                TypeName = ReadString(r)
            };

            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var key = ReadString(r) ?? string.Empty;
                doc.Props[key] = ReadString(r);
            }

            return doc;
        }

        private static void WriteString(BinaryWriter w, string s)
        {
            w.Write(s != null);
            if (s != null) w.Write(s);
        }

        private static string ReadString(BinaryReader r)
        {
            return r.ReadBoolean() ? r.ReadString() : null;
        }
    }
}
