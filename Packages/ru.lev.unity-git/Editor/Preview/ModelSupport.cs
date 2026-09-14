using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Lev.Git.Preview
{
    /// <summary>Сведения о модели для списка изменений: вершины, треугольники, кости.</summary>
    internal static class ModelFacts
    {
        public static void AddFor(LoadedAsset loaded)
        {
            if (loaded == null || loaded.Main == null) return;

            var go = loaded.Main as GameObject;
            if (go != null)
            {
                Add(loaded, go);
                int clips = loaded.All.Count(o => o is AnimationClip && !o.name.StartsWith("__preview__", StringComparison.Ordinal));
                if (clips > 0) loaded.AddFact(L.M("Animations"), clips.ToString());
                return;
            }

            var mesh = loaded.Main as Mesh;
            if (mesh != null) AddMesh(loaded, mesh);
        }

        public static void Add(LoadedAsset loaded, GameObject root)
        {
            int meshes = 0, vertices = 0, triangles = 0, submeshes = 0, bones = 0, blendShapes = 0;
            var materials = new HashSet<Material>();

            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                Count(filter.sharedMesh, ref meshes, ref vertices, ref triangles, ref submeshes, ref blendShapes);

            foreach (var skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Count(skinned.sharedMesh, ref meshes, ref vertices, ref triangles, ref submeshes, ref blendShapes);
                bones += skinned.bones != null ? skinned.bones.Length : 0;
            }

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in renderer.sharedMaterials)
                    if (m != null) materials.Add(m);

            loaded.AddFact(L.M("Objects"), root.GetComponentsInChildren<Transform>(true).Length.ToString());
            loaded.AddFact(L.M("Meshes"), meshes.ToString());
            loaded.AddFact(L.M("Vertices"), vertices.ToString("N0"));
            loaded.AddFact(L.M("Triangles"), triangles.ToString("N0"));
            loaded.AddFact(L.M("Submeshes"), submeshes.ToString());
            if (bones > 0) loaded.AddFact(L.M("Bones"), bones.ToString());
            if (blendShapes > 0) loaded.AddFact("Blend shapes", blendShapes.ToString());
            loaded.AddFact(L.M("Materials"), materials.Count.ToString());
        }

        public static void AddMesh(LoadedAsset loaded, Mesh mesh)
        {
            int meshes = 0, vertices = 0, triangles = 0, submeshes = 0, blendShapes = 0;
            Count(mesh, ref meshes, ref vertices, ref triangles, ref submeshes, ref blendShapes);
            loaded.AddFact(L.M("Vertices"), vertices.ToString("N0"));
            loaded.AddFact(L.M("Triangles"), triangles.ToString("N0"));
            loaded.AddFact(L.M("Submeshes"), submeshes.ToString());
            if (blendShapes > 0) loaded.AddFact("Blend shapes", blendShapes.ToString());
        }

        private static void Count(Mesh mesh, ref int meshes, ref int vertices, ref int triangles, ref int submeshes, ref int blendShapes)
        {
            if (mesh == null) return;

            meshes++;
            vertices += mesh.vertexCount;
            submeshes += mesh.subMeshCount;
            blendShapes += mesh.blendShapeCount;

            for (int i = 0; i < mesh.subMeshCount; i++)
                if (mesh.GetTopology(i) == MeshTopology.Triangles) triangles += (int)(mesh.GetIndexCount(i) / 3);
        }
    }

    /// <summary>
    /// Префаб из истории. YAML читается сериализатором Unity, как у остальных
    /// ассетов, но этого мало: вложенный префаб в файле — только ссылка на
    /// исходник и список переопределений. Такие экземпляры собираются из
    /// текущих исходников проекта с переопределениями этой версии — поэтому
    /// вложенные части показываются приблизительно, о чём говорит примечание.
    /// </summary>
    internal sealed class PrefabAssetLoader : AssetLoader
    {
        public override bool CanLoad(string projectPath)
        {
            return AssetLoaders.HasExtension(projectPath, ".prefab");
        }

        public override LoadedAsset Load(string projectPath, byte[] bytes)
        {
            if (!AssetYaml.LooksLikeYaml(bytes))
                return LoadedAsset.Failed(L.T("The version is saved in binary form — it can only be read with Asset Serialization: Force Text."));

            var file = AssetLoaders.TempFile(projectPath);
            Object[] objects;
            try
            {
                File.WriteAllBytes(file, bytes);
                objects = InternalEditorUtility.LoadSerializedFileAndForget(file) ?? new Object[0];
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }

            if (objects.Length == 0) return LoadedAsset.Failed(L.T("Unity could not parse this prefab version."));

            var scene = EditorSceneManager.NewPreviewScene();
            var loaded = new LoadedAsset { Info = PreviewText.Bytes(bytes.Length) };
            loaded.DisposeAction = () => EditorSceneManager.ClosePreviewScene(scene);

            var all = new List<Object>();
            foreach (var o in objects)
            {
                if (o == null) continue;
                loaded.SourceFlags[o] = o.hideFlags;
                o.hideFlags = HideFlags.HideAndDontSave;
                all.Add(o);
            }

            var docs = UnityYamlParser.Parse(Encoding.UTF8.GetString(bytes));
            var byId = new Dictionary<long, UnityDocument>();
            foreach (var d in docs) byId[d.FileId] = d;

            var gameObjects = objects.OfType<GameObject>().ToList();
            var goByDoc = MatchGameObjects(docs, gameObjects);

            foreach (var go in gameObjects)
                if (go != null && go.transform.parent == null) SceneManager.MoveGameObjectToScene(go, scene);

            var clones = new Dictionary<long, GameObject>();
            var sources = new Dictionary<long, GameObject>();
            int missing = 0, instances = 0;

            // Родитель вложенного экземпляра может сам лежать внутри другого
            // экземпляра — такие собираются на следующих проходах.
            var pending = docs.Where(PrefabOverrides.Is).ToList();
            for (int pass = 0; pass < 8 && pending.Count > 0; pass++)
            {
                var next = new List<UnityDocument>();

                foreach (var doc in pending)
                {
                    var parentId = FileIdOf(doc.Get("m_Modification.m_TransformParent"));
                    Transform parent = null;

                    if (parentId != 0)
                    {
                        parent = ResolveTransform(parentId, byId, goByDoc, clones, sources);
                        if (parent == null && pass < 7) { next.Add(doc); continue; }
                    }

                    var guid = PrefabOverrides.SourceGuid(doc);
                    var source = string.IsNullOrEmpty(guid) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    if (source == null) { missing++; continue; }

                    var clone = Object.Instantiate(source);
                    SceneManager.MoveGameObjectToScene(clone, scene);
                    if (parent != null) clone.transform.SetParent(parent, false);

                    HideAll(clone);
                    ApplyModifications(doc, source, clone);

                    clones[doc.FileId] = clone;
                    sources[doc.FileId] = source;
                    all.Add(clone);
                    instances++;
                }

                pending = next;
            }

            var root = FindRoot(docs, byId, goByDoc, clones) ??
                       gameObjects.FirstOrDefault(g => g != null && g.transform.parent == null);

            if (root == null)
            {
                loaded.All = all.ToArray();
                loaded.Dispose();
                return LoadedAsset.Failed(L.T("The root object of this prefab version was not found."));
            }

            loaded.Main = root;
            loaded.All = all.ToArray();
            YamlAssetLoader.MapFileIds(loaded, docs, objects);

            ModelFacts.Add(loaded, root);
            if (instances > 0) loaded.Notes.Add(L.F("nested prefabs built from current sources: {0}", instances));
            if (missing > 0) loaded.Notes.Add(L.F("nested prefab sources not found: {0}", missing));
            return loaded;
        }

        private static long FileIdOf(string flowMap)
        {
            var map = UnityYamlParser.ParseFlowMap(flowMap);
            string raw;
            long id;
            return map != null && map.TryGetValue("fileID", out raw) && long.TryParse(raw, out id) ? id : 0;
        }

        private static Dictionary<long, GameObject> MatchGameObjects(List<UnityDocument> docs, List<GameObject> gameObjects)
        {
            var queues = new Dictionary<string, Queue<GameObject>>(StringComparer.Ordinal);
            foreach (var go in gameObjects)
            {
                if (go == null) continue;
                Queue<GameObject> q;
                if (!queues.TryGetValue(go.name, out q)) queues[go.name] = q = new Queue<GameObject>();
                q.Enqueue(go);
            }

            var result = new Dictionary<long, GameObject>();
            foreach (var d in docs)
            {
                if (d.ClassId != 1 || FileIdOf(d.Get("m_PrefabInstance")) != 0) continue;

                Queue<GameObject> q;
                if (queues.TryGetValue(AssetYaml.NameOf(d) ?? string.Empty, out q) && q.Count > 0) result[d.FileId] = q.Dequeue();
            }

            return result;
        }

        private static Transform ResolveTransform(long id, Dictionary<long, UnityDocument> byId, Dictionary<long, GameObject> goByDoc,
                                                  Dictionary<long, GameObject> clones, Dictionary<long, GameObject> sources)
        {
            UnityDocument doc;
            if (!byId.TryGetValue(id, out doc)) return null;

            GameObject go;
            var goId = FileIdOf(doc.Get("m_GameObject"));
            if (goId != 0 && goByDoc.TryGetValue(goId, out go) && go != null) return go.transform;

            // Stripped-документ: объект внутри вложенного экземпляра — ищем его копию в собранном экземпляре.
            var instanceId = FileIdOf(doc.Get("m_PrefabInstance"));
            var sourceRef = UnityYamlParser.ParseFlowMap(doc.Get("m_CorrespondingSourceObject"));

            GameObject clone, source;
            string guid, fileId;
            if (instanceId == 0 || sourceRef == null || !clones.TryGetValue(instanceId, out clone) || !sources.TryGetValue(instanceId, out source) ||
                !sourceRef.TryGetValue("guid", out guid) || !sourceRef.TryGetValue("fileID", out fileId)) return null;

            var target = Counterpart(source, clone, PrefabTargets.Resolve(guid, fileId));
            var component = target as Component;
            return component != null ? component.transform : target is GameObject ? ((GameObject)target).transform : clone.transform;
        }

        private static GameObject FindRoot(List<UnityDocument> docs, Dictionary<long, UnityDocument> byId,
                                           Dictionary<long, GameObject> goByDoc, Dictionary<long, GameObject> clones)
        {
            foreach (var d in docs)
            {
                if ((d.ClassId != 4 && d.ClassId != 224) || FileIdOf(d.Get("m_PrefabInstance")) != 0) continue;
                if (FileIdOf(d.Get("m_Father")) != 0) continue;

                GameObject go;
                if (goByDoc.TryGetValue(FileIdOf(d.Get("m_GameObject")), out go) && go != null) return go;
            }

            // Вариант префаба: корень — сам вложенный экземпляр без родителя.
            foreach (var d in docs)
            {
                GameObject clone;
                if (PrefabOverrides.Is(d) && FileIdOf(d.Get("m_Modification.m_TransformParent")) == 0 &&
                    clones.TryGetValue(d.FileId, out clone)) return clone;
            }

            return null;
        }

        /// <summary>Объект копии, соответствующий объекту исходника: тот же путь по иерархии, тот же номер компонента.</summary>
        private static Object Counterpart(GameObject sourceRoot, GameObject cloneRoot, Object target)
        {
            if (target == null) return null;

            var go = target as GameObject ?? (target is Component ? ((Component)target).gameObject : null);
            if (go == null) return null;
            if (go.transform != sourceRoot.transform && !go.transform.IsChildOf(sourceRoot.transform)) return null;

            var path = new List<int>();
            for (var t = go.transform; t != sourceRoot.transform; t = t.parent) path.Add(t.GetSiblingIndex());

            var current = cloneRoot.transform;
            for (int i = path.Count - 1; i >= 0; i--)
            {
                if (path[i] >= current.childCount) return null;
                current = current.GetChild(path[i]);
            }

            if (target is GameObject) return current.gameObject;

            int index = Array.IndexOf(go.GetComponents<Component>(), (Component)target);
            var components = current.GetComponents<Component>();
            return index >= 0 && index < components.Length ? components[index] : null;
        }

        private static void ApplyModifications(UnityDocument doc, GameObject source, GameObject clone)
        {
            var serialized = new Dictionary<Object, SerializedObject>();

            foreach (var m in PrefabOverrides.Entries(doc).Values)
            {
                var counterpart = Counterpart(source, clone, PrefabTargets.Resolve(m.TargetGuid, m.TargetFileId));
                if (counterpart == null) continue;

                SerializedObject so;
                if (!serialized.TryGetValue(counterpart, out so)) serialized[counterpart] = so = new SerializedObject(counterpart);

                var property = so.FindProperty(m.PropertyPath);
                if (property == null) continue;

                UnityPropertyApplier.ApplyValue(property,
                    property.propertyType == SerializedPropertyType.ObjectReference ? m.Reference : m.Value);
            }

            foreach (var so in serialized.Values) so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void HideAll(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.hideFlags = HideFlags.HideAndDontSave;
                foreach (var c in t.GetComponents<Component>())
                    if (c != null) c.hideFlags = HideFlags.HideAndDontSave;
            }
        }
    }

    /// <summary>
    /// Модель или префаб в 3D: копия объекта в сцене превью, общая камера сторон,
    /// режим сетки. Масштаб кадра общий, чтобы изменение размера было видно.
    /// </summary>
    internal sealed class ModelPresenter : AssetPresenter
    {
        private readonly PreviewRenderUtility[] _utilities = new PreviewRenderUtility[4];
        private readonly GameObject[] _instances = new GameObject[4];
        private readonly Object[] _sources = new Object[4];
        private readonly Bounds[] _bounds = new Bounds[4];

        public override Type Target { get { return typeof(GameObject); } }
        public override bool Interactive { get { return true; } }
        public override string[] Shapes { get { return L.Ts("Model", "Wireframe"); } }

        public override Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync)
        {
            var go = side.Main as GameObject;
            if (go == null || rect.width < 4f || rect.height < 4f) return null;

            slot = Mathf.Clamp(slot, 0, 3);
            var utility = _utilities[slot] ?? (_utilities[slot] = new PreviewRenderUtility());

            if (_sources[slot] != go || _instances[slot] == null)
            {
                if (_instances[slot] != null) Object.DestroyImmediate(_instances[slot]);

                var instance = Object.Instantiate(go);
                instance.hideFlags = HideFlags.HideAndDontSave;
                utility.AddSingleGO(instance);

                _instances[slot] = instance;
                _sources[slot] = go;
                _bounds[slot] = BoundsOf(instance);
            }

            var bounds = _bounds[slot];
            for (int i = 0; i < _instances.Length; i++)
                if (i != slot && _instances[i] != null) bounds.Encapsulate(_bounds[i]);

            return RenderScene(utility, rect, bounds, sync, sync.Shape == 1);
        }

        public static Bounds BoundsOf(GameObject go)
        {
            bool has = false;
            var bounds = new Bounds(go.transform.position, Vector3.one);

            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer) continue;
                if (!has) { bounds = renderer.bounds; has = true; }
                else bounds.Encapsulate(renderer.bounds);
            }

            return bounds;
        }

        public static Texture RenderScene(PreviewRenderUtility utility, Rect rect, Bounds bounds, PreviewSync sync, bool wireframe)
        {
            if (sync.Static) utility.BeginStaticPreview(rect);
            else utility.BeginPreview(rect, GUIStyle.none);
            SetupCamera(utility, bounds, sync);

            var previous = GL.wireframe;
            GL.wireframe = wireframe;
            try { utility.Render(true); }
            finally { GL.wireframe = previous; }

            return sync.Static ? (Texture)utility.EndStaticPreview() : utility.EndPreview();
        }

        public static void SetupCamera(PreviewRenderUtility utility, Bounds bounds, PreviewSync sync)
        {
            var camera = utility.camera;
            float radius = Mathf.Max(bounds.extents.magnitude, 0.05f);
            float distance = radius / Mathf.Sin(15f * Mathf.Deg2Rad) * 1.05f * Mathf.Max(0.05f, sync.Dolly);

            camera.fieldOfView = 30f;
            camera.nearClipPlane = distance * 0.02f;
            camera.farClipPlane = distance * 4f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = PreviewBackground.SolidColor;

            var rotation = Quaternion.Euler(-sync.Orbit.y, -sync.Orbit.x, 0f);
            camera.transform.rotation = rotation;
            camera.transform.position = bounds.center - rotation * Vector3.forward * distance -
                                        rotation * new Vector3(sync.Offset.x, sync.Offset.y, 0f) * distance * 0.55f;

            utility.lights[0].intensity = 1.1f;
            utility.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
            utility.lights[1].intensity = 0.6f;
            utility.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);
            utility.ambientColor = new Color(0.25f, 0.25f, 0.25f);
        }

        public override void Dispose()
        {
            for (int i = 0; i < 4; i++)
            {
                if (_instances[i] != null) Object.DestroyImmediate(_instances[i]);
                _instances[i] = null;
                _sources[i] = null;
                if (_utilities[i] != null) _utilities[i].Cleanup();
                _utilities[i] = null;
            }
        }
    }

    /// <summary>Меш-ассет: заливка материалом по умолчанию или сетка.</summary>
    internal sealed class MeshPresenter : AssetPresenter
    {
        private readonly PreviewRenderUtility[] _utilities = new PreviewRenderUtility[4];

        public override Type Target { get { return typeof(Mesh); } }
        public override bool Interactive { get { return true; } }
        public override string[] Shapes { get { return L.Ts("Shaded", "Wireframe"); } }

        public override Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync)
        {
            var mesh = side.Main as Mesh;
            var material = DefaultMaterial;
            if (mesh == null || material == null || rect.width < 4f || rect.height < 4f) return null;

            slot = Mathf.Clamp(slot, 0, 3);
            var utility = _utilities[slot] ?? (_utilities[slot] = new PreviewRenderUtility());

            if (sync.Static) utility.BeginStaticPreview(rect);
            else utility.BeginPreview(rect, GUIStyle.none);
            ModelPresenter.SetupCamera(utility, mesh.bounds, sync);

            for (int i = 0; i < mesh.subMeshCount; i++)
                utility.DrawMesh(mesh, Matrix4x4.identity, material, i);

            var previous = GL.wireframe;
            GL.wireframe = sync.Shape == 1;
            try { utility.Render(true); }
            finally { GL.wireframe = previous; }

            return sync.Static ? (Texture)utility.EndStaticPreview() : utility.EndPreview();
        }

        private static Material DefaultMaterial
        {
            get
            {
                var pipeline = GraphicsSettings.currentRenderPipeline;
                return pipeline != null && pipeline.defaultMaterial != null
                    ? pipeline.defaultMaterial
                    : AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");
            }
        }

        public override void Dispose()
        {
            for (int i = 0; i < 4; i++)
            {
                if (_utilities[i] != null) _utilities[i].Cleanup();
                _utilities[i] = null;
            }
        }
    }

    /// <summary>Меш сравнивается по сути, а не по байтам вершинного буфера.</summary>
    internal sealed class MeshDescriber : ChangeDescriber<Mesh>
    {
        public override bool CoversSubAssets { get { return true; } }

        protected override void Describe(Mesh before, Mesh after, DescribeContext ctx)
        {
            Compare(ctx, L.T("Vertices"), before.vertexCount, after.vertexCount);
            Compare(ctx, L.T("Submeshes"), before.subMeshCount, after.subMeshCount);
            Compare(ctx, L.T("Triangles"), Triangles(before), Triangles(after));
            Compare(ctx, "Blend shapes", before.blendShapeCount, after.blendShapeCount);
            Compare(ctx, "Bind poses", before.bindposeCount, after.bindposeCount);

            var bb = before.bounds;
            var ba = after.bounds;
            if (bb != ba)
                ctx.Add("Bounds",
                    ChangeValue.Of(L.F("center {0}, size {1}", PreviewText.Vector(bb.center.x, bb.center.y, bb.center.z), PreviewText.Vector(bb.size.x, bb.size.y, bb.size.z))),
                    ChangeValue.Of(L.F("center {0}, size {1}", PreviewText.Vector(ba.center.x, ba.center.y, ba.center.z), PreviewText.Vector(ba.size.x, ba.size.y, ba.size.z))));

            for (int i = 0; i < 8; i++)
            {
                var attribute = VertexAttribute.TexCoord0 + i;
                bool b = before.HasVertexAttribute(attribute), a = after.HasVertexAttribute(attribute);
                if (b != a) ctx.Add("UV" + i, ChangeValue.Of(b ? L.T("present") : L.T("absent")), ChangeValue.Of(a ? L.T("present") : L.T("absent")));
            }
        }

        private static int Triangles(Mesh mesh)
        {
            int n = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                if (mesh.GetTopology(i) == MeshTopology.Triangles) n += (int)(mesh.GetIndexCount(i) / 3);
            return n;
        }

        private static void Compare(DescribeContext ctx, string label, int before, int after)
        {
            if (before != after) ctx.Add(label, ChangeValue.Of(before.ToString("N0")), ChangeValue.Of(after.ToString("N0")));
        }
    }

    /// <summary>
    /// Ассеты, которые Unity умеет только импортировать: модели, PSD, EXR, шрифты.
    /// Прошлая версия кладётся во временную папку проекта и импортируется —
    /// только если это разрешено настройкой «Импорт для превью».
    /// </summary>
    internal sealed class SandboxAssetLoader : AssetLoader
    {
        public override int Priority { get { return -10; } }

        internal override bool NeedsConsent
        {
            get { return GitSettings.instance.importSandbox == 1; }
        }

        public override bool CanLoad(string projectPath)
        {
            return AssetLoaders.HasExtension(projectPath, ".fbx", ".obj", ".blend", ".dae", ".3ds", ".max", ".ma", ".mb",
                ".psd", ".exr", ".hdr", ".tif", ".tiff", ".bmp", ".gif", ".iff", ".pict", ".dds", ".ttf", ".otf");
        }

        public override LoadedAsset Load(string projectPath, byte[] bytes)
        {
            return LoadedAsset.Failed(L.T("The import sandbox only runs asynchronously."));
        }

        public override Task<LoadedAsset> LoadAsync(string projectPath, byte[] bytes, string metaText)
        {
            if (GitSettings.instance.importSandbox == 0)
                return Task.FromResult(LoadedAsset.Failed(L.T("a previous version of this type can only be built by importing — " +
                                                              "turn on “Import for Preview” in Project Settings → Git")));

            return Task.FromResult(ImportSandbox.Import(projectPath, bytes, metaText));
        }
    }

    /// <summary>
    /// Временная папка для импорта прошлых версий. Git её не видит — путь
    /// дописывается в .git/info/exclude этой рабочей копии; папка удаляется,
    /// когда версия больше не нужна, и при следующем запуске редактора.
    /// </summary>
    internal static class ImportSandbox
    {
        public const string Root = "Assets/GitPreview";

        /// <summary>Прежнее имя папки — остаётся от ранних версий пакета и удаляется при загрузке.</summary>
        private const string LegacyRoot = "Assets/LevGitPreview";

        /// <summary>Папки версий, которые сейчас показаны в превью: их очистка не трогает.</summary>
        private static readonly HashSet<string> Active = new HashSet<string>(StringComparer.Ordinal);

        [InitializeOnLoadMethod]
        private static void CleanupOnLoad()
        {
            // Исключение — сразу, а не при первом импорте: папка не должна
            // оказаться в git ни на одной машине, даже на короткое время.
            EnsureExcluded();

            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
                    if (AssetDatabase.IsValidFolder(LegacyRoot)) AssetDatabase.DeleteAsset(LegacyRoot);
                }
                catch { }
            };
        }

        /// <summary>Удаляет версии, не открытые в превью. Возвращает число удалённых; kept — оставленных.</summary>
        public static int ClearUnused(out int kept)
        {
            kept = 0;
            int deleted = 0;
            if (!AssetDatabase.IsValidFolder(Root)) return 0;

            foreach (var folder in AssetDatabase.GetSubFolders(Root))
            {
                if (Active.Contains(folder)) { kept++; continue; }
                if (AssetDatabase.DeleteAsset(folder)) deleted++;
            }

            if (kept == 0 && AssetDatabase.IsValidFolder(Root)) AssetDatabase.DeleteAsset(Root);
            return deleted;
        }

        public static LoadedAsset Import(string projectPath, byte[] bytes, string metaText)
        {
            EnsureExcluded();
            PreviewStorage.EnforceLimit();

            if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets", "GitPreview");

            var folderName = Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(Root, folderName);
            var folder = Root + "/" + folderName;
            Active.Add(folder);

            var assetPath = folder + "/" + Path.GetFileName(projectPath);
            var full = Path.Combine(GitRepository.ProjectRoot, assetPath);

            try
            {
                File.WriteAllBytes(full, bytes);

                // GUID в мете заменяется новым: с GUID исходного ассета Unity сочла
                // бы копию дубликатом и перевыдала бы GUID самому ассету.
                if (metaText != null)
                    File.WriteAllText(full + ".meta", Regex.Replace(metaText, @"^guid:\s*[0-9a-fA-F]+",
                        "guid: " + Guid.NewGuid().ToString("N"), RegexOptions.Multiline));

                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
            catch (Exception e)
            {
                Delete(folder);
                return LoadedAsset.Failed(L.F("import sandbox failed: {0}", e.Message));
            }

            var main = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (main == null)
            {
                Delete(folder);
                return LoadedAsset.Failed(L.T("Unity did not import this version"));
            }

            var loaded = new LoadedAsset
            {
                Main = main,
                All = AssetDatabase.LoadAllAssetsAtPath(assetPath),
                Info = PreviewText.Bytes(bytes.Length),
                DisposeAction = () => Delete(folder)
            };

            var texture = main as Texture;
            if (texture != null)
            {
                loaded.Info = texture.width + "×" + texture.height + " · " + loaded.Info;
                loaded.AddFact(L.M("Size"), texture.width + "×" + texture.height);
            }

            ModelFacts.AddFor(loaded);
            loaded.AddFact(L.M("File"), PreviewText.Bytes(bytes.Length));
            loaded.Notes.Add(L.T("built by the import sandbox"));
            return loaded;
        }

        private static void Delete(string folder)
        {
            Active.Remove(folder);

            try
            {
                if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                if (AssetDatabase.IsValidFolder(Root) && AssetDatabase.GetSubFolders(Root).Length == 0 &&
                    AssetDatabase.FindAssets(string.Empty, new[] { Root }).Length == 0)
                    AssetDatabase.DeleteAsset(Root);
            }
            catch { }
        }

        private static void EnsureExcluded()
        {
            try
            {
                var gitDir = Path.Combine(GitRepository.RepoRoot, ".git");
                if (!Directory.Exists(gitDir)) return;

                var exclude = Path.Combine(gitDir, "info", "exclude");
                Directory.CreateDirectory(Path.GetDirectoryName(exclude));

                var text = File.Exists(exclude) ? File.ReadAllText(exclude) : string.Empty;
                var sb = new StringBuilder(text);
                bool changed = false;

                foreach (var root in new[] { Root, LegacyRoot })
                {
                    var pattern = "/" + GitRepository.ToGitPath(root) + "/";
                    if (text.Contains(pattern)) continue;

                    if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                    sb.Append("# Lev Git: import sandbox for previews\n")
                      .Append(pattern).Append('\n')
                      .Append("/" + GitRepository.ToGitPath(root) + ".meta").Append('\n');
                    changed = true;
                }

                if (changed) File.WriteAllText(exclude, sb.ToString());
            }
            catch { }
        }
    }
}
