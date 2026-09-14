using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lev.Git.Preview
{
    /// <summary>
    /// «Зависимости из того же коммита». Прошлая версия материала по GUID
    /// ссылается на текстуру проекта — то есть на текущую. Если текстура с тех
    /// пор тоже менялась, показ был бы неправдой. Здесь такие ссылки находятся,
    /// их версии на том же коммите загружаются, и ссылки переводятся на них.
    ///
    /// Глубина — один шаг и не больше десятка ассетов: этого хватает материалу и
    /// префабу, а долгого ожидания при показе не бывает.
    /// </summary>
    internal static class DependencySnapshot
    {
        private const int MaxAssets = 10;

        private sealed class Reference
        {
            public Object Owner;
            public string PropertyPath;
            public long LocalId;
        }

        public static async Task<int> ApplyAsync(LoadedAsset loaded, RevisionSide side)
        {
            if (loaded == null || loaded.Main == null || side == null || side.Kind == SideKind.Worktree) return 0;

            var byPath = new Dictionary<string, List<Reference>>(StringComparer.Ordinal);
            int scanned = 0;

            foreach (var owner in loaded.All.Concat(new[] { loaded.Main }).Distinct())
            {
                if (owner == null || EditorUtility.IsPersistent(owner)) continue;

                var so = new SerializedObject(owner);
                var it = so.GetIterator();
                while (it.Next(true) && ++scanned < 200000)
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;

                    var value = it.objectReferenceValue;
                    if (value == null || !EditorUtility.IsPersistent(value)) continue;

                    var path = AssetDatabase.GetAssetPath(value);
                    if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) continue;

                    var loader = AssetLoaders.Find(path);
                    if (!(loader is YamlAssetLoader) && !(loader is ImageAssetLoader)) continue;

                    string guid;
                    long localId;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out guid, out localId)) continue;

                    List<Reference> list;
                    if (!byPath.TryGetValue(path, out list)) byPath[path] = list = new List<Reference>();
                    list.Add(new Reference { Owner = owner, PropertyPath = it.propertyPath, LocalId = localId });
                }
            }

            if (byPath.Count == 0) return 0;

            var rev = side.Kind == SideKind.Head ? "HEAD" : side.Sha;
            var changed = await ChangedSinceAsync(rev, byPath.Keys.ToList());

            int remapped = 0, assets = 0;
            foreach (var path in changed)
            {
                if (assets >= MaxAssets) break;

                var gitPath = GitRepository.ToGitPath(path);
                var depSide = side.Kind == SideKind.Head ? RevisionSide.Head(gitPath, string.Empty) : RevisionSide.Commit(side.Sha, gitPath, string.Empty);

                var bytes = await depSide.ReadAsync(null);
                if (bytes == null) continue;

                var loader = AssetLoaders.Find(path);
                var dep = await AssetLoaders.SafeLoadAsync(loader, path, bytes, await depSide.ReadMetaAsync());
                if (dep.Main == null)
                {
                    dep.Dispose();
                    continue;
                }

                var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
                string mainGuid;
                long mainId = 0;
                if (mainAsset != null) AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mainAsset, out mainGuid, out mainId);

                var byLocal = new Dictionary<long, Object>();
                foreach (var pair in dep.FileIds) byLocal[pair.Value] = pair.Key;

                foreach (var reference in byPath[path])
                {
                    Object target;
                    if (reference.LocalId == mainId) target = dep.Main;
                    else if (!byLocal.TryGetValue(reference.LocalId, out target)) continue;

                    var so = new SerializedObject(reference.Owner);
                    var property = so.FindProperty(reference.PropertyPath);
                    if (property == null) continue;

                    property.objectReferenceValue = target;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    remapped++;
                }

                loaded.Dependencies.Add(dep);
                assets++;
            }

            if (assets > 0) loaded.Notes.Add(L.F("dependencies from the same commit: {0}", assets));
            return remapped;
        }

        /// <summary>Какие из файлов менялись между ревизией и рабочей копией.</summary>
        private static async Task<List<string>> ChangedSinceAsync(string rev, List<string> projectPaths)
        {
            var result = new List<string>();
            var byGit = projectPaths.ToDictionary(GitRepository.ToGitPath, p => p, StringComparer.Ordinal);

            foreach (var batch in byGit.Keys.Select((p, i) => new { p, i }).GroupBy(x => x.i / 40, x => x.p))
            {
                var args = "diff --name-only " + GitOperations.Q(rev) + " -- " +
                           string.Join(" ", batch.Select(GitOperations.Q).ToArray());
                var r = await GitOperations.Git(args);
                if (!r.Ok) continue;

                foreach (var line in r.StdOut.Split('\n'))
                {
                    string project;
                    if (byGit.TryGetValue(line.Trim(), out project)) result.Add(project);
                }
            }

            return result;
        }
    }
}
