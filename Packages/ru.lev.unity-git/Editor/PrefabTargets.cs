using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Объекты внутри файла префаба по ссылкам из переопределений экземпляра.
    ///
    /// У переопределения цель записана как {fileID, guid}: guid — файл префаба,
    /// fileID — объект или компонент внутри него. Файл загружается один раз, и
    /// его объекты раскладываются по fileID.
    /// </summary>
    internal static class PrefabTargets
    {
        private static readonly Dictionary<string, Dictionary<string, Object>> ByGuid =
            new Dictionary<string, Dictionary<string, Object>>();

        public static Object Resolve(string guid, string fileId)
        {
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(fileId)) return null;

            if (!ByGuid.TryGetValue(guid, out var map) || Stale(map))
            {
                map = Load(guid);
                ByGuid[guid] = map;
            }

            map.TryGetValue(fileId, out var obj);
            return obj;
        }

        /// <summary>«guid|fileId» объекта в файле ассета; null — это не объект ассета.</summary>
        public static string KeyOf(Object asset)
        {
            return asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long id)
                ? guid + "|" + id.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        /// <summary>
        /// Путь объекта внутри префаба: «Cube (3)/Body». Корень называется так, как
        /// назван экземпляр на сцене, — по этому имени его и ищут в иерархии.
        /// </summary>
        public static string PathInPrefab(GameObject go, string rootName)
        {
            var names = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) names.Add(t.name);
            if (!string.IsNullOrEmpty(rootName)) names[names.Count - 1] = rootName;

            var sb = new StringBuilder();
            for (int i = names.Count - 1; i >= 0; i--)
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(names[i]);
            }
            return sb.ToString();
        }

        private static Dictionary<string, Object> Load(string guid)
        {
            var map = new Dictionary<string, Object>();
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return map;

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string g, out long id))
                    map[id.ToString(CultureInfo.InvariantCulture)] = asset;

            return map;
        }

        /// <summary>Префаб переимпортировали — старые объекты уничтожены, файл читается заново.</summary>
        private static bool Stale(Dictionary<string, Object> map)
        {
            foreach (var o in map.Values) return o == null;
            return true;
        }
    }
}
