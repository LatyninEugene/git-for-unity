using System;
using System.Collections.Generic;

namespace Lev.Git
{
    /// <summary>
    /// Переопределения экземпляра префаба на человеческом языке.
    ///
    /// В файле сцены объектов префаба нет — есть один документ PrefabInstance
    /// со списком переопределений. В сыром виде правка одного поля выглядит как
    /// «m_Modification.m_Modifications[3].value: 1 → 5», и по такой строке
    /// нельзя понять даже, какое поле меняли. Здесь список раскладывается
    /// обратно в пары «путь свойства → значение» и сравнивается уже по ним.
    /// </summary>
    /// <summary>Одно переопределение экземпляра: какой объект префаба, какое свойство, что записано.</summary>
    public sealed class PrefabModification
    {
        public string TargetGuid;
        public string TargetFileId;
        public string PropertyPath;
        public string Value = string.Empty;
        public string Reference = "{fileID: 0}";

        /// <summary>«guid|fileId» — объект или компонент в файле префаба.</summary>
        public string Target => TargetGuid + "|" + TargetFileId;
        public string Key => Target + "|" + PropertyPath;

        /// <summary>Что показать человеку: у ссылки на объект value пустое, смысл в objectReference.</summary>
        public string Display => string.IsNullOrEmpty(Value) && !string.IsNullOrEmpty(Reference) && Reference != "{fileID: 0}"
            ? Reference : Value;
    }

    /// <summary>Переопределение, изменившееся между двумя состояниями экземпляра.</summary>
    public sealed class PrefabModificationChange
    {
        /// <summary>null — в старом состоянии переопределения не было (значение шло из префаба).</summary>
        public PrefabModification Before;

        /// <summary>null — переопределение сняли, значение снова из префаба.</summary>
        public PrefabModification After;

        public string Target => (After ?? Before).Target;
        public string TargetGuid => (After ?? Before).TargetGuid;
        public string TargetFileId => (After ?? Before).TargetFileId;
        public string PropertyPath => (After ?? Before).PropertyPath;
        public string Key => (After ?? Before).Key;

        public string Old => Before != null ? Before.Display : null;
        public string New => After != null ? After.Display : null;

        /// <summary>
        /// Значение этого свойства в самом префабе — на стороне, где переопределения
        /// нет, действует именно оно. Заполняет тот, у кого есть доступ к файлу префаба;
        /// null — прочитать не удалось.
        /// </summary>
        public string PrefabValue;

        /// <summary>Подпись «было»: значение переопределения или «1 (из префаба)».</summary>
        public string OldText => Old ?? FromPrefab;

        /// <summary>Подпись «стало»: значение переопределения или «1 (из префаба)».</summary>
        public string NewText => New ?? FromPrefab;

        private string FromPrefab => string.IsNullOrEmpty(PrefabValue) ? L.T("from prefab") : L.F("{0} (from prefab)", PrefabValue);
    }

    public static class PrefabOverrides
    {
        public const string TypeName = "PrefabInstance";

        /// <summary>Переопределения экземпляра по ключу «цель|свойство».</summary>
        public static Dictionary<string, PrefabModification> Entries(UnityDocument doc)
        {
            var result = new Dictionary<string, PrefabModification>(StringComparer.Ordinal);
            if (doc == null) return result;

            for (int i = 0; ; i++)
            {
                var prefix = ListPrefix + i + "].";
                var path = doc.Get(prefix + "propertyPath");
                if (path == null) break;
                if (path.Length == 0) continue;

                var target = UnityYamlParser.ParseFlowMap(doc.Get(prefix + "target"));
                string fileId = null, guid = null;
                if (target != null)
                {
                    target.TryGetValue("fileID", out fileId);
                    target.TryGetValue("guid", out guid);
                }
                if (string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(guid)) continue;

                var m = new PrefabModification
                {
                    TargetGuid = guid,
                    TargetFileId = fileId,
                    PropertyPath = path,
                    Value = doc.Get(prefix + "value") ?? string.Empty,
                    Reference = doc.Get(prefix + "objectReference") ?? "{fileID: 0}"
                };
                result[m.Key] = m;
            }

            return result;
        }

        /// <summary>
        /// Изменившиеся переопределения — по цели и свойству, а не по номеру в
        /// списке: добавленное в начало переопределение сдвигает все номера, и
        /// сравнение по ним показало бы правки там, где их не было.
        /// </summary>
        public static List<PrefabModificationChange> Changes(UnityDocument oldDoc, UnityDocument newDoc)
        {
            var before = Entries(oldDoc);
            var after = Entries(newDoc);
            var result = new List<PrefabModificationChange>();

            foreach (var pair in after)
            {
                before.TryGetValue(pair.Key, out var old);
                if (old != null && old.Value == pair.Value.Value && old.Reference == pair.Value.Reference) continue;
                result.Add(new PrefabModificationChange { Before = old, After = pair.Value });
            }

            foreach (var pair in before)
                if (!after.ContainsKey(pair.Key))
                    result.Add(new PrefabModificationChange { Before = pair.Value });

            result.Sort((a, b) =>
            {
                int t = string.CompareOrdinal(a.Target, b.Target);
                return t != 0 ? t : string.CompareOrdinal(a.PropertyPath, b.PropertyPath);
            });
            return result;
        }

        private const string ListPrefix = "m_Modification.m_Modifications[";

        public static bool Is(UnityDocument doc)
        {
            return doc != null && doc.TypeName == TypeName;
        }

        /// <summary>
        /// Имя экземпляра. Unity хранит его переопределением m_Name на корне,
        /// поэтому у большинства экземпляров оно есть.
        /// </summary>
        public static string NameOf(UnityDocument doc)
        {
            if (doc == null) return null;

            string name;
            return Overrides(doc).TryGetValue("m_Name", out name) ? name : null;
        }

        /// <summary>
        /// GUID файла префаба, из которого сделан экземпляр. Ссылка m_SourcePrefab
        /// хранится одной строкой: «{fileID: 100100000, guid: …, type: 3}».
        /// </summary>
        public static string SourceGuid(UnityDocument doc)
        {
            if (doc == null) return null;

            var raw = doc.Get("m_SourcePrefab");
            if (string.IsNullOrEmpty(raw)) return null;

            var m = System.Text.RegularExpressions.Regex.Match(raw, @"guid:\s*([0-9a-fA-F]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Список переопределений: путь свойства → значение. Ссылки на объекты
        /// берутся из objectReference: у них value пустое.
        /// </summary>
        public static Dictionary<string, string> Overrides(UnityDocument doc)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc == null) return result;

            for (int i = 0; ; i++)
            {
                var prefix = ListPrefix + i + "].";
                var path = doc.Get(prefix + "propertyPath");

                // Индексы идут подряд: первый пропуск — конец списка.
                if (path == null) break;
                if (path.Length == 0) continue;

                var value = doc.Get(prefix + "value");
                if (string.IsNullOrEmpty(value))
                {
                    var reference = doc.Get(prefix + "objectReference");
                    value = string.IsNullOrEmpty(reference) ? string.Empty : reference;
                }

                result[path] = value;
            }

            return result;
        }

        /// <summary>Сколько компонентов снято с экземпляра.</summary>
        public static int RemovedComponents(UnityDocument doc)
        {
            if (doc == null) return 0;

            int count = 0;
            while (doc.Get("m_Modification.m_RemovedComponents[" + count + "].fileID") != null ||
                   doc.Get("m_Modification.m_RemovedComponents[" + count + "]") != null) count++;

            return count;
        }

        /// <summary>
        /// Разница двух состояний экземпляра: что переопределили, что вернули
        /// к значению префаба, что поменяли.
        /// </summary>
        public static List<ScenePropertyChange> Diff(UnityDocument oldDoc, UnityDocument newDoc)
        {
            var result = new List<ScenePropertyChange>();

            var before = Overrides(oldDoc);
            var after = Overrides(newDoc);

            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in after.Keys) if (seen.Add(k)) keys.Add(k);
            foreach (var k in before.Keys) if (seen.Add(k)) keys.Add(k);
            keys.Sort(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                string oldValue, newValue;
                bool had = before.TryGetValue(key, out oldValue);
                bool has = after.TryGetValue(key, out newValue);

                if (had && has && oldValue == newValue) continue;

                result.Add(new ScenePropertyChange
                {
                    Path = key,
                    // Пропавшее переопределение — это возврат к значению префаба,
                    // и сказать это словами честнее, чем показать пустоту.
                    Old = had ? oldValue : L.T("from prefab"),
                    New = has ? newValue : L.T("from prefab")
                });
            }

            int removedBefore = RemovedComponents(oldDoc);
            int removedAfter = RemovedComponents(newDoc);

            if (removedBefore != removedAfter)
                result.Add(new ScenePropertyChange
                {
                    Path = L.T("components removed"),
                    Old = removedBefore.ToString(),
                    New = removedAfter.ToString()
                });

            return result;
        }
    }
}
