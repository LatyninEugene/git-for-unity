using System;
using System.Collections.Generic;

namespace Lev.Git
{
    /// <summary>Одна версия файла сцены: коммит и содержимое до и после него.</summary>
    public sealed class SceneRevision
    {
        public string Sha = string.Empty;
        public string ShortSha = string.Empty;
        public string Author = string.Empty;
        public string Email = string.Empty;
        public string Subject = string.Empty;
        public DateTime Date;

        /// <summary>Путь файла в этом коммите: при переименовании отличается от текущего.</summary>
        public string GitPath = string.Empty;

        /// <summary>Идентификаторы содержимого файла в git до и после коммита.</summary>
        public string OldBlob;
        public string NewBlob;

        /// <summary>Содержимое до коммита. Пусто — файл в этом коммите появился.</summary>
        public string OldText = string.Empty;

        /// <summary>Содержимое после коммита. Пусто — файл в этом коммите удалён.</summary>
        public string NewText = string.Empty;
    }

    /// <summary>Что случилось с объектом или компонентом в одном коммите.</summary>
    public enum SceneEventKind
    {
        Added = 0,
        Removed,
        Modified,
        Renamed,
        Reparented
    }

    /// <summary>
    /// Событие истории: один объект или компонент в одном коммите.
    ///
    /// Ключ — fileID, а не путь в иерархии: путь меняется при переименовании
    /// и переносе, а fileID Unity держит неизменным, для того он и существует.
    /// </summary>
    public sealed class SceneEvent
    {
        public SceneRevision Revision;

        /// <summary>fileID игрового объекта. 0 — настройки сцены, у них объекта нет.</summary>
        public long ObjectId;

        /// <summary>Путь объекта в иерархии на момент этого коммита.</summary>
        public string ObjectPath = string.Empty;

        /// <summary>fileID компонента. 0 — событие про сам объект.</summary>
        public long ComponentId;

        public string ComponentType;

        /// <summary>Номер среди компонентов того же типа на объекте.</summary>
        public int TypeIndex;

        public SceneEventKind Kind;
        public List<ScenePropertyChange> Props = new List<ScenePropertyChange>();

        /// <summary>Сколько служебных свойств скрыто — счётчик из семантического diff.</summary>
        public int HiddenProps;

        /// <summary>Узел разницы: по нему открывается окно «было и стало».</summary>
        public SceneNode Node;

        /// <summary>Событие про экземпляр префаба: свойства — это его переопределения.</summary>
        public bool IsPrefabInstance;

        /// <summary>
        /// Служебный документ сцены, а не игровой объект: RenderSettings,
        /// LightmapSettings, NavMeshSettings, OcclusionCullingSettings,
        /// SceneRoots. Они есть в каждой сцене, в иерархии не видны, удалить
        /// их нельзя, и перейти к ним на сцене некуда — ObjectPath у них равен
        /// имени типа.
        /// </summary>
        public bool IsSceneSettings;

        /// <summary>
        /// GUID файла префаба у экземпляра — ссылка на историю самого .prefab:
        /// объекты внутри экземпляра живут там, а в сцене только переопределения.
        /// </summary>
        public string SourcePrefabGuid;

        /// <summary>Прежнее имя объекта — только у переименования.</summary>
        public string OldName;
        public string NewName;

        public bool IsObjectLevel { get { return ComponentId == 0; } }

        /// <summary>Заголовок строки в ленте: тип компонента или имя объекта.</summary>
        public string Title
        {
            get
            {
                if (!IsObjectLevel) return ComponentType ?? L.T("Component");
                return string.IsNullOrEmpty(ObjectPath) ? L.T("Scene") : ObjectPath;
            }
        }
    }

    /// <summary>
    /// Превращает последовательность версий сцены в ленту событий по объектам.
    ///
    /// Чистый код без git и без Unity: на вход — тексты версий, на выход —
    /// события. Так его можно прогнать тестами на синтетической истории, а не
    /// проверять глазами в редакторе.
    ///
    /// Почему не построчный `git blame`: Unity время от времени пересохраняет
    /// сцену целиком, переставляя документы местами. Blame приписал бы каждую
    /// строку такому коммиту, хотя ни один объект не менялся. Семантический
    /// diff в этом случае пуст, и коммит честно не попадает в ленту.
    /// </summary>
    public static class SceneTimeline
    {
        /// <summary>
        /// Строит ленту. Версии передаются от новых к старым — в том порядке,
        /// в каком их отдаёт `git log`.
        /// </summary>
        public static List<SceneEvent> Build(IEnumerable<SceneRevision> revisions)
        {
            var list = new List<SceneRevision>();
            var diffs = new List<List<SceneNode>>();

            if (revisions != null)
            {
                foreach (var rev in revisions)
                {
                    if (rev == null) continue;
                    list.Add(rev);
                    diffs.Add(Diff(rev));
                }
            }

            return BuildFromDiffs(list, diffs);
        }

        /// <summary>
        /// Семантическая разница одной версии. null — версия не разобралась:
        /// сцена из старой версии Unity может не читаться, и это не повод терять
        /// остальную историю.
        /// </summary>
        public static List<SceneNode> Diff(SceneRevision rev)
        {
            if (rev == null) return null;

            try
            {
                return SceneDiffBuilder.Build(rev.OldText ?? string.Empty, rev.NewText ?? string.Empty);
            }
            catch (Exception e)
            {
                Diagnostics.Journal.Warn(
                    L.F("Scene version from {0} couldn't be parsed: {1}", rev.ShortSha, e.Message));
                return null;
            }
        }

        /// <summary>
        /// Лента из уже посчитанных разниц — например, взятых из кэша на диске.
        /// Версия и её разница идут парами по индексу; пропуски (null) не мешают.
        /// </summary>
        public static List<SceneEvent> BuildFromDiffs(IList<SceneRevision> revisions, IList<List<SceneNode>> diffs)
        {
            var events = new List<SceneEvent>();
            if (revisions == null || diffs == null) return events;

            for (int i = 0; i < revisions.Count && i < diffs.Count; i++)
            {
                if (revisions[i] == null || diffs[i] == null) continue;
                foreach (var node in diffs[i]) AddObject(revisions[i], node, events);
            }

            return events;
        }

        private static void AddObject(SceneRevision rev, SceneNode node, List<SceneEvent> events)
        {
            var path = node.Title ?? string.Empty;

            // Событие про сам объект появляется, только если у него есть что
            // сказать. Добавление компонента меняет у объекта служебный список
            // m_Component, и это единственное изменение: строка «объект изменён»
            // рядом со строкой «компонент добавлен» ничего не добавила бы.
            bool ownChange = node.Kind != SceneChangeKind.Modified || node.Props.Count > 0;

            // Экземпляр префаба — не обычный документ: его правки лежат списком
            // переопределений, и читать их надо по-своему.
            bool prefab = PrefabOverrides.Is(node.NewDoc) || PrefabOverrides.Is(node.OldDoc);
            var props = node.Props;

            if (prefab)
            {
                props = PrefabOverrides.Diff(node.OldDoc, node.NewDoc);
                var name = PrefabOverrides.NameOf(node.NewDoc) ?? PrefabOverrides.NameOf(node.OldDoc);
                path = string.IsNullOrEmpty(name) ? L.T("Prefab Instance") : L.F("{0} (prefab)", name);
                ownChange = node.Kind != SceneChangeKind.Modified || props.Count > 0;
            }

            // Корневой документ, который не игровой объект и не префаб, —
            // служебные данные самой сцены. У заголовка объекта, заведённого
            // ради изменившегося компонента, документов нет вовсе: это
            // GameObject, который сам не менялся, и под правило он не попадает.
            bool settings = !prefab && SceneDiffBuilder.IsSceneSettings(node);

            if (ownChange)
            {
                var e = new SceneEvent
                {
                    Revision = rev,
                    ObjectId = node.FileId,
                    ObjectPath = path,
                    Kind = ToEventKind(node.Kind),
                    Props = props,
                    HiddenProps = prefab ? 0 : node.HiddenProps,
                    Node = node,
                    IsPrefabInstance = prefab,
                    IsSceneSettings = settings,
                    SourcePrefabGuid = prefab ? PrefabOverrides.SourceGuid(node.NewDoc ?? node.OldDoc) : null
                };

                // Переименование видно по свойству m_Name. Оно важнее, чем
                // «изменён»: по прежнему имени объект и ищут в истории.
                if (node.Kind == SceneChangeKind.Modified)
                {
                    foreach (var p in props)
                    {
                        if (p.Path != "m_Name") continue;
                        e.Kind = SceneEventKind.Renamed;
                        e.OldName = p.Old;
                        e.NewName = p.New;
                        break;
                    }
                }

                events.Add(e);
            }

            foreach (var child in node.Children)
            {
                var e = new SceneEvent
                {
                    Revision = rev,
                    ObjectId = node.FileId,
                    ObjectPath = path,
                    ComponentId = child.FileId,
                    ComponentType = child.Title,
                    TypeIndex = child.TypeIndex,
                    Kind = ToEventKind(child.Kind),
                    Props = child.Props,
                    HiddenProps = child.HiddenProps,
                    Node = child
                };

                // Смена родителя лежит в Transform.m_Father, но по смыслу это
                // событие объекта, а не компонента: «перенесён в иерархии».
                if (child.Kind == SceneChangeKind.Modified && IsTransform(child.Title))
                {
                    foreach (var p in child.Props)
                    {
                        // Ссылку diff раскладывает покомпонентно, поэтому здесь
                        // не «m_Father», а «m_Father.fileID».
                        if (p.Path != "m_Father" &&
                            !p.Path.StartsWith("m_Father.", StringComparison.Ordinal)) continue;
                        e.Kind = SceneEventKind.Reparented;
                        break;
                    }
                }

                events.Add(e);
            }
        }

        private static bool IsTransform(string type)
        {
            return type == "Transform" || type == "RectTransform";
        }

        private static SceneEventKind ToEventKind(SceneChangeKind kind)
        {
            switch (kind)
            {
                case SceneChangeKind.Added: return SceneEventKind.Added;
                case SceneChangeKind.Removed: return SceneEventKind.Removed;
                default: return SceneEventKind.Modified;
            }
        }

        // ------------------------------------------------------------ отборы ---

        /// <summary>События одного объекта: и его собственные, и его компонентов.</summary>
        public static List<SceneEvent> ForObject(List<SceneEvent> events, long objectId)
        {
            var result = new List<SceneEvent>();
            foreach (var e in events) if (e.ObjectId == objectId) result.Add(e);
            return result;
        }

        /// <summary>События одного компонента.</summary>
        public static List<SceneEvent> ForComponent(List<SceneEvent> events, long componentId)
        {
            var result = new List<SceneEvent>();
            foreach (var e in events) if (e.ComponentId == componentId) result.Add(e);
            return result;
        }

        /// <summary>Сводка по коммиту для ленты сцены: сколько объектов добавили, удалили, изменили.</summary>
        public static void CountObjects(List<SceneEvent> events, string sha,
                                        out int added, out int removed, out int modified)
        {
            added = removed = modified = 0;

            var seen = new HashSet<long>();
            foreach (var e in events)
            {
                if (e.Revision == null || e.Revision.Sha != sha) continue;
                if (!e.IsObjectLevel || !seen.Add(e.ObjectId)) continue;

                if (e.Kind == SceneEventKind.Added) added++;
                else if (e.Kind == SceneEventKind.Removed) removed++;
                else modified++;
            }
        }
    }

    /// <summary>
    /// Разбор вывода `git log --raw` в список версий файла.
    ///
    /// Отдельно от <see cref="SceneHistory"/>: здесь нет ни git, ни Unity —
    /// только текст на входе и записи на выходе, поэтому разбор проверяется
    /// тестами на заранее известном выводе.
    /// </summary>
    public static class SceneRevisionParser
    {
        /// <summary>
        /// Разбирает вывод `git log --raw`: описание коммита, затем строки вида
        /// «:100644 100644 &lt;до&gt; &lt;после&gt; M\tпуть».
        /// </summary>
        public static List<SceneRevision> Parse(string raw)
        {
            var result = new List<SceneRevision>();
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (var record in raw.Split('\u001E'))
            {
                if (record.Trim().Length == 0) continue;

                var lines = record.Split('\n');
                var fields = lines[0].Split('\0');
                if (fields.Length < 6) continue;

                var rev = new SceneRevision
                {
                    Sha = fields[0],
                    // %h вместе с --no-abbrev, который нужен для идентификаторов
                    // версий, печатает полный хеш. Сокращаем сами.
                    ShortSha = fields[1].Length > 10 && fields[0].Length >= 7
                        ? fields[0].Substring(0, 7)
                        : fields[1],
                    Author = fields[2],
                    Email = fields[3],
                    Date = ParseDate(fields[4]),
                    Subject = fields[5].TrimStart('\uFEFF')
                };

                for (int i = 1; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (line.Length == 0 || line[0] != ':') continue;

                    // Поля до пути разделены пробелами, путь — табуляцией.
                    int tab = line.IndexOf('\t');
                    if (tab < 0) continue;

                    var head = line.Substring(1, tab - 1)
                                   .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (head.Length < 5) continue;

                    rev.OldBlob = head[2];
                    rev.NewBlob = head[3];

                    // У переименования путей два: старый и новый. Нам нужен тот,
                    // под которым файл лежит после коммита.
                    var paths = line.Substring(tab + 1).Split('\t');
                    rev.GitPath = paths[paths.Length - 1];
                    break;
                }

                if (rev.OldBlob == null && rev.NewBlob == null) continue;
                result.Add(rev);
            }

            return result;
        }

        private static DateTime ParseDate(string s)
        {
            DateTime d;
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal, out d))
                return d.ToLocalTime();

            return DateTime.MinValue;
        }
    }
}
