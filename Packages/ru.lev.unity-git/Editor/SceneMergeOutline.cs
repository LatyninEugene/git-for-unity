using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Lev.Git
{
    /// <summary>Объект в иерархии одной версии сцены: GameObject или экземпляр префаба.</summary>
    public sealed class OutlineNode
    {
        public long Id;
        public bool IsPrefabInstance;
        public string Name;
        public string PrefabGuid;
        public bool Active = true;
        public long Parent;

        public readonly List<long> Children = new List<long>();

        /// <summary>Компоненты в порядке списка объекта.</summary>
        public readonly List<long> Components = new List<long>();
    }

    /// <summary>
    /// Иерархия одной версии сцены или префаба — то, что показало бы окно
    /// Hierarchy, если открыть эту версию.
    ///
    /// Порядок детей берётся из m_Children, порядок корней — из SceneRoots:
    /// ровно так их расставляет сам Unity.
    /// </summary>
    public sealed class SceneOutline
    {
        public readonly UnityScene Scene;
        public readonly Dictionary<long, OutlineNode> Nodes = new Dictionary<long, OutlineNode>();
        public readonly List<long> Roots = new List<long>();

        /// <summary>Любой документ → объект иерархии, к которому он относится.</summary>
        public readonly Dictionary<long, long> NodeOfDoc = new Dictionary<long, long>();

        private readonly List<long> _order = new List<long>();

        private SceneOutline(UnityScene scene)
        {
            Scene = scene;
        }

        public static SceneOutline Build(UnityScene scene)
        {
            var o = new SceneOutline(scene);
            if (scene == null) return o;

            // Экземпляры префабов.
            foreach (var d in scene.Documents)
            {
                if (d.TypeName != "PrefabInstance") continue;

                o.Add(new OutlineNode
                {
                    Id = d.FileId,
                    IsPrefabInstance = true,
                    Name = PrefabName(d),
                    PrefabGuid = PrefabOverrides.SourceGuid(d)
                });
            }

            // Урезанные документы — представители объектов из префаба.
            foreach (var d in scene.Documents)
            {
                var instance = UnityScene.RefId(d.Get("m_PrefabInstance"));
                if (instance != 0 && o.Nodes.ContainsKey(instance) && IsStripped(d)) o.NodeOfDoc[d.FileId] = instance;
            }

            foreach (var d in scene.Documents)
            {
                if (d.TypeName != "GameObject" || o.NodeOfDoc.ContainsKey(d.FileId)) continue;

                UnityGameObject go;
                if (!scene.GameObjects.TryGetValue(d.FileId, out go)) continue;

                var node = new OutlineNode { Id = go.FileId, Name = go.Name, Active = go.IsActive };
                foreach (var c in go.Components)
                {
                    node.Components.Add(c.FileId);
                    o.NodeOfDoc[c.FileId] = go.FileId;
                }
                o.Add(node);
            }

            // Родители.
            foreach (var node in o.Nodes.Values)
            {
                long parentRef;
                if (node.IsPrefabInstance)
                {
                    parentRef = UnityScene.RefId(scene.ById[node.Id].Get("m_Modification.m_TransformParent"));
                }
                else
                {
                    UnityGameObject go = scene.GameObjects[node.Id];
                    UnityDocument transform;
                    parentRef = go.TransformId != 0 && scene.ById.TryGetValue(go.TransformId, out transform)
                        ? UnityScene.RefId(transform.Get("m_Father"))
                        : 0;
                }

                long parent;
                node.Parent = parentRef != 0 && o.NodeOfDoc.TryGetValue(parentRef, out parent) && parent != node.Id ? parent : 0;
            }

            // Дети — в порядке m_Children, остальные следом в порядке файла.
            foreach (var node in o.Nodes.Values)
            {
                if (node.IsPrefabInstance) continue;

                var go = scene.GameObjects[node.Id];
                UnityDocument transform;
                if (go.TransformId == 0 || !scene.ById.TryGetValue(go.TransformId, out transform)) continue;

                for (int i = 0; ; i++)
                {
                    var value = transform.Get("m_Children[" + i + "]");
                    if (value == null) break;

                    long child;
                    if (o.NodeOfDoc.TryGetValue(UnityScene.RefId(value), out child) &&
                        o.Nodes[child].Parent == node.Id && !node.Children.Contains(child))
                        node.Children.Add(child);
                }
            }

            foreach (var id in o._order)
            {
                var node = o.Nodes[id];
                if (node.Parent == 0) continue;
                var parent = o.Nodes[node.Parent];
                if (!parent.Children.Contains(id)) parent.Children.Add(id);
            }

            // Корни — в порядке SceneRoots.
            foreach (var d in scene.Documents)
            {
                if (d.TypeName != "SceneRoots") continue;
                for (int i = 0; ; i++)
                {
                    var value = d.Get("m_Roots[" + i + "]");
                    if (value == null) break;

                    long root;
                    if (o.NodeOfDoc.TryGetValue(UnityScene.RefId(value), out root) &&
                        o.Nodes[root].Parent == 0 && !o.Roots.Contains(root))
                        o.Roots.Add(root);
                }
            }

            foreach (var id in o._order)
                if (o.Nodes[id].Parent == 0 && !o.Roots.Contains(id)) o.Roots.Add(id);

            return o;
        }

        private void Add(OutlineNode node)
        {
            Nodes[node.Id] = node;
            NodeOfDoc[node.Id] = node.Id;
            _order.Add(node.Id);
        }

        private static bool IsStripped(UnityDocument d)
        {
            return d.Get("m_Component[0].component") == null && d.Get("m_GameObject") == null && d.Get("m_Name") == null;
        }

        /// <summary>Имя экземпляра — из переопределения m_Name, если его задавали.</summary>
        private static string PrefabName(UnityDocument d)
        {
            for (int i = 0; ; i++)
            {
                var path = d.Get("m_Modification.m_Modifications[" + i + "].propertyPath");
                if (path == null) return null;
                if (path == "m_Name") return d.Get("m_Modification.m_Modifications[" + i + "].value");
            }
        }
    }

    /// <summary>
    /// Экземпляр префаба, который одна сторона распаковала в обычные объекты.
    ///
    /// После распаковки у объектов новые fileID, и связи с экземпляром в файле
    /// не остаётся. Пара находится по смыслу: экземпляр пропал, а на том же
    /// месте в иерархии появился объект с тем же именем.
    /// </summary>
    public sealed class UnpackPair
    {
        public long PrefabId;
        public long ObjectId;

        /// <summary>Сторона, которая распаковала.</summary>
        public MergeSide Side;
    }

    public struct MergeOutlineRow
    {
        public long Id;
        public int Depth;
        public bool HasChildren;
    }

    /// <summary>
    /// Общая иерархия трёх версий и итога: строки выровнены, объект стоит в
    /// одной строке во всех колонках, даже если на какой-то стороне его нет.
    /// К строкам привязаны правки слияния — по объекту, компоненту и месту в
    /// иерархии.
    /// </summary>
    public sealed class MergeOutline
    {
        public SceneOutline Base, Mine, Theirs, Result;

        public readonly List<UnpackPair> Unpacks = new List<UnpackPair>();

        /// <summary>Правки строки: её документов и её места в родителе.</summary>
        public readonly Dictionary<long, List<YamlMergeConflict>> ByNode = new Dictionary<long, List<YamlMergeConflict>>();

        /// <summary>
        /// Правки по документам. Ключ — id компонента, а для правок самого
        /// объекта (GameObject, экземпляр, место в иерархии) — id строки.
        /// </summary>
        public readonly Dictionary<long, List<YamlMergeConflict>> ByDoc = new Dictionary<long, List<YamlMergeConflict>>();

        /// <summary>Правки вне объектов: настройки сцены, служебные документы.</summary>
        public readonly List<YamlMergeConflict> Loose = new List<YamlMergeConflict>();

        private readonly Dictionary<long, long> _alias = new Dictionary<long, long>();
        private readonly Dictionary<long, long> _parent = new Dictionary<long, long>();
        private readonly Dictionary<long, List<long>> _children = new Dictionary<long, List<long>>();
        private readonly List<long> _roots = new List<long>();
        private readonly Dictionary<long, List<YamlMergeConflict>> _subtree = new Dictionary<long, List<YamlMergeConflict>>();

        private static readonly Regex HierarchyItem = new Regex(@"(?:^|\.)(?:m_Children|m_Roots)\[#\{fileID: (-?\d+)\}\]$");
        private static readonly Regex ComponentItem = new Regex(@"^m_Component\[#component: \{fileID: (-?\d+)\}\]$");

        public static MergeOutline Build(SceneOutline b, SceneOutline m, SceneOutline t, SceneOutline r, YamlMergeResult merge)
        {
            var o = new MergeOutline { Base = b, Mine = m, Theirs = t, Result = r };
            o.FindUnpacks();
            o.BuildTree();
            o.AssignChanges(merge);
            return o;
        }

        private IEnumerable<SceneOutline> All()
        {
            if (Result != null) yield return Result;
            if (Mine != null) yield return Mine;
            if (Theirs != null) yield return Theirs;
            if (Base != null) yield return Base;
        }

        public long RowOf(long nodeId)
        {
            long row;
            return _alias.TryGetValue(nodeId, out row) ? row : nodeId;
        }

        private void FindUnpacks()
        {
            if (Base == null) return;

            foreach (var p in Base.Nodes.Values)
            {
                if (!p.IsPrefabInstance) continue;

                foreach (var side in new[] { MergeSide.Mine, MergeSide.Theirs })
                {
                    var o = side == MergeSide.Mine ? Mine : Theirs;
                    if (o == null || o.Nodes.ContainsKey(p.Id)) continue;

                    foreach (var g in o.Nodes.Values)
                    {
                        if (g.IsPrefabInstance || Base.Nodes.ContainsKey(g.Id) || _alias.ContainsKey(g.Id)) continue;
                        if (g.Parent != p.Parent || !string.Equals(g.Name, p.Name, StringComparison.Ordinal)) continue;

                        Unpacks.Add(new UnpackPair { PrefabId = p.Id, ObjectId = g.Id, Side = side });
                        _alias[g.Id] = p.Id;
                        break;
                    }
                }
            }
        }

        public UnpackPair UnpackOf(long rowId)
        {
            foreach (var u in Unpacks) if (u.PrefabId == rowId) return u;
            return null;
        }

        private void BuildTree()
        {
            foreach (var o in All())
                foreach (var n in o.Nodes.Values)
                {
                    var row = RowOf(n.Id);
                    if (!_parent.ContainsKey(row)) _parent[row] = n.Parent == 0 ? 0 : RowOf(n.Parent);
                }

            foreach (var o in All())
            {
                foreach (var n in o.Nodes.Values)
                {
                    var row = RowOf(n.Id);
                    foreach (var child in n.Children) Attach(row, RowOf(child));
                }
                foreach (var root in o.Roots) Attach(0, RowOf(root));
            }

            foreach (var pair in _parent) Attach(pair.Value, pair.Key);
        }

        private void Attach(long parent, long child)
        {
            long real;
            if (!_parent.TryGetValue(child, out real) || real != parent || child == parent) return;
            if (parent != 0 && !_parent.ContainsKey(parent)) { if (!_roots.Contains(child)) _roots.Add(child); return; }

            var list = parent == 0 ? _roots : Children(parent);
            if (!list.Contains(child)) list.Add(child);
        }

        private List<long> Children(long parent)
        {
            List<long> list;
            if (!_children.TryGetValue(parent, out list)) { list = new List<long>(); _children[parent] = list; }
            return list;
        }

        private long NodeOfDoc(long doc)
        {
            foreach (var o in All())
            {
                long node;
                if (o.NodeOfDoc.TryGetValue(doc, out node)) return RowOf(node);
            }
            return 0;
        }

        private bool IsNode(long doc)
        {
            foreach (var o in All()) if (o.Nodes.ContainsKey(doc)) return true;
            return false;
        }

        private void AssignChanges(YamlMergeResult merge)
        {
            if (merge == null) return;

            foreach (var c in merge.Changes)
            {
                long doc = c.FileId;
                long row;
                long docKey;

                var item = c.Key != null ? HierarchyItem.Match(c.Key) : Match.Empty;
                if (item.Success)
                {
                    // Место объекта в родителе — правка самого объекта, а не родителя.
                    row = NodeOfDoc(long.Parse(item.Groups[1].Value));
                    docKey = row;
                }
                else
                {
                    var comp = c.Key != null ? ComponentItem.Match(c.Key) : Match.Empty;
                    if (comp.Success) doc = long.Parse(comp.Groups[1].Value);

                    row = NodeOfDoc(doc);
                    docKey = IsNode(doc) ? RowOf(doc) : doc;
                }

                if (row == 0) { Loose.Add(c); continue; }

                Add(ByNode, row, c);
                Add(ByDoc, docKey, c);
            }
        }

        private static void Add(Dictionary<long, List<YamlMergeConflict>> map, long key, YamlMergeConflict c)
        {
            List<YamlMergeConflict> list;
            if (!map.TryGetValue(key, out list)) { list = new List<YamlMergeConflict>(); map[key] = list; }
            list.Add(c);
        }

        private static readonly List<YamlMergeConflict> None = new List<YamlMergeConflict>();

        public List<YamlMergeConflict> ChangesOf(long docKey)
        {
            List<YamlMergeConflict> list;
            return ByDoc.TryGetValue(docKey, out list) ? list : None;
        }

        /// <summary>Правки объекта и всех его потомков.</summary>
        public List<YamlMergeConflict> Subtree(long row)
        {
            List<YamlMergeConflict> cached;
            if (_subtree.TryGetValue(row, out cached)) return cached;

            var list = new List<YamlMergeConflict>();
            _subtree[row] = list;   // до обхода — защита от циклов в испорченной иерархии

            List<YamlMergeConflict> own;
            if (ByNode.TryGetValue(row, out own)) list.AddRange(own);

            List<long> children;
            if (_children.TryGetValue(row, out children))
                foreach (var child in children) list.AddRange(Subtree(child));

            return list;
        }

        /// <summary>Узел строки на стороне; unpacked — на этой стороне вместо экземпляра распакованный объект.</summary>
        public OutlineNode NodeOn(SceneOutline side, long row, out bool unpacked)
        {
            unpacked = false;
            if (side == null) return null;

            OutlineNode node;
            if (side.Nodes.TryGetValue(row, out node)) return node;

            foreach (var u in Unpacks)
                if (u.PrefabId == row && side.Nodes.TryGetValue(u.ObjectId, out node)) { unpacked = true; return node; }

            return null;
        }

        /// <summary>Компоненты строки на всех сторонах, в порядке итога, затем моей и их версии.</summary>
        public List<long> ComponentsOf(long row)
        {
            var list = new List<long>();
            foreach (var o in All())
            {
                bool unpacked;
                var node = NodeOn(o, row, out unpacked);
                if (node == null) continue;
                foreach (var c in node.Components) if (!list.Contains(c)) list.Add(c);
            }
            return list;
        }

        public List<MergeOutlineRow> Flatten(Func<long, bool> expanded, bool onlyChanged)
        {
            var rows = new List<MergeOutlineRow>();
            var seen = new HashSet<long>();
            foreach (var root in _roots) Walk(root, 0, rows, seen, expanded, onlyChanged);
            return rows;
        }

        private void Walk(long id, int depth, List<MergeOutlineRow> rows, HashSet<long> seen,
                          Func<long, bool> expanded, bool onlyChanged)
        {
            if (!seen.Add(id)) return;
            if (onlyChanged && Subtree(id).Count == 0) return;

            List<long> children;
            _children.TryGetValue(id, out children);

            bool hasChildren = false;
            if (children != null)
                foreach (var c in children) if (!onlyChanged || Subtree(c).Count > 0) { hasChildren = true; break; }

            rows.Add(new MergeOutlineRow { Id = id, Depth = depth, HasChildren = hasChildren });

            if (children == null || !expanded(id)) return;
            foreach (var c in children) Walk(c, depth + 1, rows, seen, expanded, onlyChanged);
        }
    }
}
