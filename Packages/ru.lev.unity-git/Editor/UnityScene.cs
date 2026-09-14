using System;
using System.Collections.Generic;
using System.Text;

namespace Lev.Git
{
    /// <summary>Игровой объект, собранный из документов сцены или префаба.</summary>
    public sealed class UnityGameObject
    {
        public long FileId;
        public string Name;
        public bool IsActive = true;

        public long TransformId;
        public long ParentTransformId;

        /// <summary>Компоненты в том порядке, в каком они перечислены у объекта.</summary>
        public readonly List<UnityDocument> Components = new List<UnityDocument>();

        /// <summary>Путь в иерархии: «Родитель/Ребёнок/Cube». Считается лениво.</summary>
        public string Path;

        public override string ToString() { return Path ?? Name; }
    }

    /// <summary>
    /// Сцена или префаб как набор объектов с иерархией.
    ///
    /// Нужна затем, что человек мыслит объектами и компонентами, а не
    /// документами с числовыми идентификаторами. Разница «изменился
    /// Rigidbody.mass у Player/Body» получается только здесь: в самом YAML
    /// компонент не знает своего имени, а объект не знает своего пути.
    /// </summary>
    public sealed class UnityScene
    {
        public readonly List<UnityDocument> Documents;
        public readonly Dictionary<long, UnityDocument> ById = new Dictionary<long, UnityDocument>();
        public readonly Dictionary<long, UnityGameObject> GameObjects = new Dictionary<long, UnityGameObject>();

        /// <summary>Компонент → объект, которому он принадлежит.</summary>
        private readonly Dictionary<long, long> _ownerOf = new Dictionary<long, long>();

        private UnityScene(List<UnityDocument> documents)
        {
            Documents = documents;
        }

        public static UnityScene Build(string text)
        {
            var docs = UnityYamlParser.Parse(text);
            var scene = new UnityScene(docs);

            foreach (var d in docs) scene.ById[d.FileId] = d;

            scene.BuildGameObjects();
            scene.ResolvePaths();
            return scene;
        }

        /// <summary>Идентификатор из ссылки {fileID: N}. 0 — ссылки нет или она внешняя.</summary>
        public static long RefId(string value)
        {
            var map = UnityYamlParser.ParseFlowMap(value);
            if (map == null) return 0;

            // Ссылка с guid указывает в другой файл — внутри этого её не разрешить.
            if (map.ContainsKey("guid")) return 0;

            string id;
            long result;
            return map.TryGetValue("fileID", out id) && long.TryParse(id, out result) ? result : 0;
        }

        public UnityGameObject OwnerOf(UnityDocument component)
        {
            long owner;
            if (!_ownerOf.TryGetValue(component.FileId, out owner)) return null;

            UnityGameObject go;
            return GameObjects.TryGetValue(owner, out go) ? go : null;
        }

        private void BuildGameObjects()
        {
            foreach (var d in Documents)
            {
                if (d.TypeName != "GameObject") continue;

                var go = new UnityGameObject
                {
                    FileId = d.FileId,
                    Name = d.Get("m_Name") ?? L.T("(unnamed)"),
                    IsActive = d.Get("m_IsActive") != "0"
                };

                // Список компонентов разложен парсером в m_Component[i].component.
                for (int i = 0; ; i++)
                {
                    var value = d.Get("m_Component[" + i + "].component");
                    if (value == null) break;

                    var id = RefId(value);
                    if (id == 0) continue;

                    UnityDocument comp;
                    if (!ById.TryGetValue(id, out comp)) continue;

                    go.Components.Add(comp);
                    _ownerOf[id] = go.FileId;

                    if (comp.TypeName == "Transform" || comp.TypeName == "RectTransform")
                    {
                        go.TransformId = comp.FileId;
                        go.ParentTransformId = RefId(comp.Get("m_Father"));
                    }
                }

                GameObjects[go.FileId] = go;
            }
        }

        /// <summary>
        /// Достраивает путь каждого объекта, поднимаясь по m_Father.
        ///
        /// Ограничение на глубину не паранойя: сцена может прийти из merge с
        /// зациклённой иерархией, и обход без предела в этом случае не вернётся.
        /// </summary>
        private void ResolvePaths()
        {
            var goByTransform = new Dictionary<long, UnityGameObject>();
            foreach (var go in GameObjects.Values)
                if (go.TransformId != 0) goByTransform[go.TransformId] = go;

            foreach (var go in GameObjects.Values)
            {
                var parts = new List<string> { go.Name };

                var parentTransform = go.ParentTransformId;
                for (int depth = 0; parentTransform != 0 && depth < 128; depth++)
                {
                    UnityGameObject parent;
                    if (!goByTransform.TryGetValue(parentTransform, out parent)) break;

                    parts.Add(parent.Name);

                    UnityDocument parentDoc;
                    if (!ById.TryGetValue(parent.TransformId, out parentDoc)) break;
                    parentTransform = RefId(parentDoc.Get("m_Father"));
                }

                parts.Reverse();
                go.Path = string.Join("/", parts.ToArray());
            }
        }

        /// <summary>
        /// Понятное имя документа: объект — по пути, компонент — «Тип на Пути»,
        /// всё остальное — по типу. Это то, что попадёт в текст изменения.
        /// </summary>
        public string Describe(UnityDocument doc)
        {
            if (doc.TypeName == "GameObject")
            {
                UnityGameObject go;
                return GameObjects.TryGetValue(doc.FileId, out go) ? go.Path : "GameObject";
            }

            var owner = OwnerOf(doc);
            if (owner != null) return L.F("{0} on “{1}”", doc.TypeName, owner.Path);

            return doc.TypeName ?? L.F("object {0}", doc.FileId);
        }
    }
}
