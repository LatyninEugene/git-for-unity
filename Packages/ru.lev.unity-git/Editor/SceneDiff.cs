using System;
using System.Collections.Generic;

namespace Lev.Git
{
    public enum SceneChangeKind
    {
        Added = 0,
        Removed,
        Modified
    }

    /// <summary>Одно изменившееся свойство.</summary>
    public sealed class ScenePropertyChange
    {
        public string Path;   // m_LocalPosition.y, m_Mass
        public string Old;
        public string New;
    }

    /// <summary>
    /// Узел разницы: игровой объект, компонент или настройка сцены.
    /// Компоненты лежат детьми своего объекта.
    /// </summary>
    public sealed class SceneNode
    {
        public SceneChangeKind Kind;
        public string Title;
        public long FileId;

        /// <summary>
        /// Документы обеих сторон. Хранятся ссылками, а не копиями: они уже
        /// разобраны, и по ним окно «было и стало» показывает состояние
        /// компонента целиком, а не только изменившиеся поля.
        /// </summary>
        public UnityDocument OldDoc;
        public UnityDocument NewDoc;

        /// <summary>Номер среди компонентов того же типа на объекте: их бывает несколько.</summary>
        public int TypeIndex;
        public readonly List<ScenePropertyChange> Props = new List<ScenePropertyChange>();
        public readonly List<SceneNode> Children = new List<SceneNode>();

        /// <summary>Сколько служебных свойств скрыто — чтобы не делать вид, что их не было.</summary>
        public int HiddenProps;

        public bool IsEmpty => Props.Count == 0 && Children.Count == 0 && HiddenProps == 0;
    }

    /// <summary>
    /// Разница двух версий сцены или префаба, выраженная объектами и
    /// свойствами вместо строк текста.
    ///
    /// Ради этого пакет и существует: перестановка двух объектов в иерархии даёт
    /// текстовый diff на девятьсот строк, ни одна из которых не говорит человеку
    /// ничего. Здесь то же изменение — одна строка про один объект.
    ///
    /// Сопоставление идёт по fileID. Это надёжно: Unity держит их неизменными
    /// при правках, для того они и существуют.
    /// </summary>
    public static class SceneDiffBuilder
    {
        /// <summary>
        /// Свойства, которые меняются сами и ничего не значат для человека.
        /// Список намеренно короткий: прятать лишнее хуже, чем показать шум,
        /// поэтому скрытое ещё и пересчитывается и выносится в подпись.
        /// </summary>
        private static readonly string[] NoiseSuffixes =
        {
            "serializedVersion",
            "m_LocalEulerAnglesHint"
        };

        public static List<SceneNode> Build(string oldText, string newText)
        {
            var oldScene = UnityScene.Build(oldText ?? string.Empty);
            var newScene = UnityScene.Build(newText ?? string.Empty);

            var ids = new List<long>();
            var seen = new HashSet<long>();
            foreach (var d in newScene.Documents) if (seen.Add(d.FileId)) ids.Add(d.FileId);
            foreach (var d in oldScene.Documents) if (seen.Add(d.FileId)) ids.Add(d.FileId);

            // Узлы игровых объектов заводятся заранее: компоненты лягут в них
            // детьми, даже если сам объект не менялся.
            var nodes = new Dictionary<long, SceneNode>();
            var roots = new List<SceneNode>();

            foreach (var id in ids)
            {
                UnityDocument oldDoc, newDoc;
                oldScene.ById.TryGetValue(id, out oldDoc);
                newScene.ById.TryGetValue(id, out newDoc);

                var doc = newDoc ?? oldDoc;
                if (doc == null) continue;

                var kind = newDoc == null ? SceneChangeKind.Removed
                         : oldDoc == null ? SceneChangeKind.Added
                         : SceneChangeKind.Modified;

                var scene = newDoc != null ? newScene : oldScene;

                var node = new SceneNode
                {
                    Kind = kind,
                    FileId = id,
                    Title = scene.Describe(doc),
                    OldDoc = oldDoc,
                    NewDoc = newDoc
                };

                if (kind == SceneChangeKind.Modified)
                {
                    CompareProps(oldDoc, newDoc, node);
                    if (node.IsEmpty) continue;
                }

                nodes[id] = node;

                // Компонент уходит под свой объект, если тот тоже попал в разницу.
                var owner = scene.OwnerOf(doc);
                if (owner == null) { roots.Add(node); continue; }

                SceneNode ownerNode;
                if (!nodes.TryGetValue(owner.FileId, out ownerNode))
                {
                    // Объект сам не менялся — заводим для него заголовок без свойств,
                    // иначе изменение компонента повисло бы без адреса.
                    ownerNode = new SceneNode
                    {
                        Kind = SceneChangeKind.Modified,
                        FileId = owner.FileId,
                        Title = owner.Path
                    };
                    nodes[owner.FileId] = ownerNode;
                    roots.Add(ownerNode);
                }

                // Заголовок компонента внутри объекта — только тип: путь уже назван выше.
                node.Title = ShortTitle(doc, node.Title);

                // Номер среди ВСЕХ компонентов этого типа на объекте, а не среди
                // изменившихся: живая сторона перебирает GetComponents целиком, и
                // неизменившийся первый коллайдер там тоже считается. Считать по
                // детям узла значило бы сдвинуть нумерацию и открыть не тот
                // компонент при переходе.
                foreach (var sibling in owner.Components)
                {
                    if (ReferenceEquals(sibling, doc)) break;
                    if (sibling.TypeName == doc.TypeName) node.TypeIndex++;
                }

                ownerNode.Children.Add(node);
            }

            // Заголовки объектов, заведённые ради компонентов, могли остаться
            // пустыми, если все их компоненты в итоге отсеялись. Убираем только
            // их: у появившегося или удалённого документа своих свойств нет и
            // не должно быть, и он сам по себе — событие. Так в разницу
            // возвращается, например, добавленный экземпляр префаба.
            roots.RemoveAll(n => n.Kind == SceneChangeKind.Modified && n.IsEmpty);

            roots.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            return roots;
        }

        private static string ShortTitle(UnityDocument doc, string full)
        {
            return doc.TypeName ?? full;
        }

        private static void CompareProps(UnityDocument oldDoc, UnityDocument newDoc, SceneNode node)
        {
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in newDoc.Props.Keys) if (seen.Add(k)) keys.Add(k);
            foreach (var k in oldDoc.Props.Keys) if (seen.Add(k)) keys.Add(k);
            keys.Sort(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                var a = oldDoc.Get(key);
                var b = newDoc.Get(key);
                if (a == b) continue;

                if (IsNoise(key) || IsComponentList(newDoc, key)) { node.HiddenProps++; continue; }

                // Карту в одну строку раскладываем покомпонентно: «m_LocalPosition.y: 1 → 5»
                // читается, а вектор целиком с обеих сторон — нет.
                var oldMap = UnityYamlParser.ParseFlowMap(a);
                var newMap = UnityYamlParser.ParseFlowMap(b);

                if (oldMap != null && newMap != null && SameKeys(oldMap, newMap))
                {
                    foreach (var pair in newMap)
                    {
                        if (oldMap[pair.Key] == pair.Value) continue;
                        node.Props.Add(new ScenePropertyChange
                        {
                            Path = key + "." + pair.Key,
                            Old = oldMap[pair.Key],
                            New = pair.Value
                        });
                    }
                    continue;
                }

                node.Props.Add(new ScenePropertyChange { Path = key, Old = a, New = b });
            }
        }

        private static bool SameKeys(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var k in a.Keys) if (!b.ContainsKey(k)) return false;
            return true;
        }

        /// <summary>
        /// Список компонентов у объекта. Его правки не показываются: добавленный
        /// или удалённый компонент и так стоит отдельной строкой рядом, а
        /// «m_Component[4]: — → {fileID: 1746856667}» повторяет это числом,
        /// которое человеку ничего не говорит.
        /// </summary>
        private static bool IsComponentList(UnityDocument doc, string key)
        {
            return doc.TypeName == "GameObject" &&
                   key.StartsWith("m_Component[", StringComparison.Ordinal);
        }

        private static bool IsNoise(string key)
        {
            foreach (var suffix in NoiseSuffixes)
            {
                if (key == suffix) return true;
                if (key.EndsWith("." + suffix, StringComparison.Ordinal)) return true;
                if (key.StartsWith(suffix + ".", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Служебный документ сцены — RenderSettings, LightmapSettings, NavMeshSettings,
        /// OcclusionCullingSettings, SceneRoots, — а не объект иерархии. Они есть в каждой
        /// сцене, в иерархии не видны, удалить их нельзя и выделить на сцене нечего.
        /// Среди объектов они выглядят как объекты, к которым почему-то не перейти.
        ///
        /// Имеет смысл только для корней разницы: компонент тоже «не GameObject».
        /// Экземпляр префаба и его stripped-документы (у них m_PrefabInstance) — часть
        /// иерархии, как и всё, что принадлежит объекту (m_GameObject).
        /// </summary>
        public static bool IsSceneSettings(SceneNode node)
        {
            var doc = node != null ? node.NewDoc ?? node.OldDoc : null;
            if (doc == null || doc.TypeName == null) return false;
            if (doc.TypeName == "GameObject" || PrefabOverrides.Is(doc)) return false;

            foreach (var key in doc.Props.Keys)
                if (IsKey(key, "m_PrefabInstance") || IsKey(key, "m_CorrespondingSourceObject") || IsKey(key, "m_GameObject"))
                    return false;

            return true;
        }

        private static bool IsKey(string key, string name)
        {
            return key == name || key.StartsWith(name + ".", StringComparison.Ordinal);
        }

        /// <summary>
        /// Сколько всего изменений в дереве — для подписи и для пустого состояния.
        /// Настройки сцены считаются отдельно: объектами иерархии они не являются.
        /// </summary>
        public static void Count(List<SceneNode> roots, out int objects, out int properties, out int settings)
        {
            objects = 0;
            properties = 0;
            settings = 0;
            foreach (var n in roots)
            {
                if (IsSceneSettings(n))
                {
                    settings++;
                    properties += n.Props.Count;
                    continue;
                }
                CountNode(n, ref objects, ref properties);
            }
        }

        private static void CountNode(SceneNode n, ref int objects, ref int properties)
        {
            objects++;
            properties += n.Props.Count;
            foreach (var c in n.Children) CountNode(c, ref objects, ref properties);
        }
    }
}
