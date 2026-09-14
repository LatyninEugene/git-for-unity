using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lev.Git.Preview
{
    /// <summary>Куда описатель складывает изменения; сам проставляет группу и объекты сторон.</summary>
    public sealed class DescribeContext
    {
        public const int Limit = 1000;

        internal readonly List<ChangeItem> Items = new List<ChangeItem>();

        /// <summary>Объект «было» → его пара в «стало»: главный объект и подобъекты файла.</summary>
        internal readonly Dictionary<Object, Object> Pairs = new Dictionary<Object, Object>();

        public string Group;
        internal Object BeforeTarget, AfterTarget;

        public bool Full { get { return Items.Count >= Limit; } }

        public ChangeItem Add(string label, ChangeValue before, ChangeValue after, ChangeKind kind = ChangeKind.Modified, string key = null)
        {
            var item = new ChangeItem
            {
                Key = key,
                Label = label,
                Group = Group,
                Kind = kind,
                Before = before,
                After = after,
                BeforeTarget = BeforeTarget,
                AfterTarget = AfterTarget,
                Pairs = Pairs
            };

            if (!Full) Items.Add(item);
            return item;
        }

        /// <summary>
        /// Одна ли это ссылка. Ассеты проекта сравниваются как есть: у обеих
        /// версий ссылка по GUID ведёт на один и тот же объект. Подобъекты самого
        /// файла у версии из git — свои копии, их сравниваем по паре или по имени.
        /// </summary>
        public bool SameRef(Object before, Object after)
        {
            if (before == null || after == null) return before == after;

            Object paired;
            if (Pairs.TryGetValue(before, out paired)) return paired == after;
            if (ReferenceEquals(before, after)) return true;
            if (EditorUtility.IsPersistent(before) && EditorUtility.IsPersistent(after)) return false;

            return before.GetType() == after.GetType() && before.name == after.name;
        }
    }

    /// <summary>
    /// Описывает, что поменялось в объекте, словами инспектора. Выбирается по
    /// типу объекта, самый производный побеждает — как CustomEditor.
    /// </summary>
    public abstract class ChangeDescriber
    {
        public virtual int Priority { get { return 0; } }

        /// <summary>
        /// Описатель сам разбирает внутренние объекты файла — состояния аниматора,
        /// дорожки Timeline. Тогда они не описываются ещё раз по отдельности.
        /// </summary>
        public virtual bool CoversSubAssets { get { return false; } }
        public abstract Type Target { get; }
        public abstract void Describe(Object before, Object after, DescribeContext ctx);
    }

    public abstract class ChangeDescriber<T> : ChangeDescriber where T : Object
    {
        public sealed override Type Target { get { return typeof(T); } }

        public sealed override void Describe(Object before, Object after, DescribeContext ctx)
        {
            Describe((T)before, (T)after, ctx);
        }

        protected abstract void Describe(T before, T after, DescribeContext ctx);
    }

    internal static class ChangeDescribers
    {
        private static List<ChangeDescriber> _all;
        private static readonly Dictionary<Type, ChangeDescriber> Cache = new Dictionary<Type, ChangeDescriber>();

        public static ChangeDescriber For(Type type)
        {
            ChangeDescriber found;
            if (Cache.TryGetValue(type, out found)) return found;

            if (_all == null)
            {
                _all = new List<ChangeDescriber>();
                foreach (var t in TypeCache.GetTypesDerivedFrom<ChangeDescriber>())
                {
                    if (t.IsAbstract || t.GetConstructor(Type.EmptyTypes) == null) continue;
                    try { _all.Add((ChangeDescriber)Activator.CreateInstance(t)); }
                    catch (Exception e) { Diagnostics.Journal.Warn(L.F("Change describer {0} was not created: {1}", t.Name, e.Message)); }
                }
            }

            found = _all.Where(d => d.Target.IsAssignableFrom(type))
                        .OrderByDescending(d => Depth(d.Target))
                        .ThenByDescending(d => d.Priority)
                        .FirstOrDefault() ?? new GenericDescriber();

            Cache[type] = found;
            return found;
        }

        public static int Depth(Type t)
        {
            int depth = 0;
            for (; t != null; t = t.BaseType) depth++;
            return depth;
        }

        /// <summary>
        /// Все изменения между двумя версиями файла: главный объект и его
        /// подобъекты (компоненты профиля, состояния аниматора). Подобъекты
        /// сопоставляются по типу и имени, служебные пропускаются.
        /// </summary>
        public static List<ChangeItem> Describe(LoadedAsset before, LoadedAsset after)
        {
            var ctx = new DescribeContext();
            ctx.Pairs[before.Main] = after.Main;

            bool covered = before.Main.GetType() == after.Main.GetType() && For(after.Main.GetType()).CoversSubAssets;
            var subsBefore = covered ? new List<Object>() : Subs(before);
            var subsAfter = covered ? new List<Object>() : Subs(after);

            var afterByKey = new Dictionary<string, Object>(StringComparer.Ordinal);
            var counters = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var a in subsAfter) afterByKey[KeyOf(a, counters)] = a;

            var matched = new List<KeyValuePair<Object, Object>>();
            var removed = new List<Object>();
            var used = new HashSet<Object>();

            counters.Clear();
            foreach (var b in subsBefore)
            {
                Object a;
                if (afterByKey.TryGetValue(KeyOf(b, counters), out a))
                {
                    ctx.Pairs[b] = a;
                    used.Add(a);
                    matched.Add(new KeyValuePair<Object, Object>(b, a));
                }
                else removed.Add(b);
            }

            DescribePair(before.Main, after.Main, null, ctx);
            foreach (var pair in matched) DescribePair(pair.Key, pair.Value, GroupName(pair.Value), ctx);

            ctx.Group = null;
            foreach (var b in removed)
            {
                ctx.BeforeTarget = b;
                ctx.AfterTarget = null;
                ctx.Add(GroupName(b), ChangeValue.Of(L.T("present")), ChangeValue.None, ChangeKind.Removed).Note = L.T("sub-object removed");
            }

            foreach (var a in subsAfter.Where(o => !used.Contains(o)))
            {
                ctx.BeforeTarget = null;
                ctx.AfterTarget = a;
                ctx.Add(GroupName(a), ChangeValue.None, ChangeValue.Of(L.T("present")), ChangeKind.Added).Note = L.T("sub-object added");
            }

            return ctx.Items;
        }

        /// <summary>
        /// Всё, что отличает две версии: поля объектов, сведения о файле
        /// (размер, длительность), настройки импорта и нарезку спрайтов из .meta.
        /// Сведения о файле и мета есть и у сторон, чей объект собрать не вышло, —
        /// у модели изменения импорта видны и без самой модели. null — сравнивать нечего.
        /// </summary>
        public static List<ChangeItem> DescribeAll(LoadedAsset before, LoadedAsset after)
        {
            if (before == null || after == null) return null;

            var items = new List<ChangeItem>();
            bool objects = before.Main != null && after.Main != null && before.Describable && after.Describable;

            if (objects) items.AddRange(Describe(before, after));

            AddFacts(items, before, after);
            bool meta = AddImporter(items, before, after);

            bool comparable = objects || meta || before.Facts.Count > 0 || after.Facts.Count > 0;
            return comparable ? items : null;
        }

        private static void AddFacts(List<ChangeItem> items, LoadedAsset before, LoadedAsset after)
        {
            var old = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var fact in before.Facts) old[fact.Key] = fact.Value;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fact in after.Facts)
            {
                seen.Add(fact.Key);

                string value;
                if (old.TryGetValue(fact.Key, out value) && value == fact.Value) continue;

                items.Add(new ChangeItem
                {
                    Group = L.T("File"),
                    Label = L.T(fact.Key), // loc-dynamic
                    Key = "fact:" + fact.Key,
                    Before = value != null ? ChangeValue.Of(value) : ChangeValue.None,
                    After = ChangeValue.Of(fact.Value)
                });
            }

            foreach (var fact in before.Facts)
                if (!seen.Contains(fact.Key) && after.Main != null)
                    items.Add(new ChangeItem { Group = L.T("File"), Label = L.T(fact.Key), Key = "fact:" + fact.Key, Before = ChangeValue.Of(fact.Value) }); // loc-dynamic
        }

        /// <summary>false — меты нет хотя бы у одной стороны.</summary>
        private static bool AddImporter(List<ChangeItem> items, LoadedAsset before, LoadedAsset after)
        {
            if (before.MetaText == null || after.MetaText == null) return false;

            var importerBefore = ImporterMeta.Parse(before.MetaText);
            var importerAfter = ImporterMeta.Parse(after.MetaText);
            if (importerBefore == null || importerAfter == null) return false;

            foreach (var change in ImporterMeta.SliceChanges(ImporterMeta.Slices(importerBefore), ImporterMeta.Slices(importerAfter)))
                items.Add(new ChangeItem
                {
                    Group = L.T("Sprite Slices"),
                    Label = change.Label,
                    Key = change.Path,
                    Before = ChangeValue.Of(change.Before),
                    After = ChangeValue.Of(change.After),
                    Note = change.Before == "—" ? L.T("added") : change.After == "—" ? L.T("removed") : null
                });

            foreach (var change in ImporterMeta.Changes(importerBefore, importerAfter))
                items.Add(new ChangeItem
                {
                    Group = L.F("Import · {0}", change.Group),
                    Label = change.Label,
                    Key = "meta:" + change.Path,
                    Tooltip = change.Path,
                    Before = ChangeValue.Of(change.Before),
                    After = ChangeValue.Of(change.After)
                });

            return true;
        }

        private static void DescribePair(Object before, Object after, string group, DescribeContext ctx)
        {
            ctx.Group = group;
            ctx.BeforeTarget = before;
            ctx.AfterTarget = after;

            if (before.GetType() != after.GetType())
            {
                ctx.Add(L.T("Type"), ChangeValue.Of(before.GetType().Name), ChangeValue.Of(after.GetType().Name));
                return;
            }

            try
            {
                For(after.GetType()).Describe(before, after, ctx);
            }
            catch (Exception e)
            {
                ctx.Add(L.T("Changes not described"), ChangeValue.None, ChangeValue.None).Note = e.Message;
            }
        }

        private static List<Object> Subs(LoadedAsset loaded)
        {
            return loaded.All
                .Where(o => o != null && o != loaded.Main && !LoadedAsset.IsMissingClass(o) &&
                            (loaded.FlagsOf(o) & HideFlags.NotEditable) == 0)
                .Take(300)
                .ToList();
        }

        private static string KeyOf(Object o, Dictionary<string, int> counters)
        {
            var key = o.GetType().FullName + "|" + o.name;
            int n;
            counters.TryGetValue(key, out n);
            counters[key] = n + 1;
            return key + "#" + n;
        }

        private static string GroupName(Object o)
        {
            var type = o.GetType().Name;
            if (string.IsNullOrEmpty(o.name)) return type;
            return o.name == type ? o.name : o.name + " (" + type + ")";
        }
    }

    /// <summary>
    /// Описатель по умолчанию: обход сериализованных полей так, как их видит
    /// инспектор. Подпись — цепочка имён полей, значение — целиком для цвета,
    /// вектора, ссылки.
    /// </summary>
    internal sealed class GenericDescriber : ChangeDescriber
    {
        public override int Priority { get { return -1000; } }

        public override Type Target { get { return typeof(Object); } }

        private static readonly HashSet<string> Skipped = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
            "m_EditorHideFlags", "m_EditorClassIdentifier"
        };

        public override void Describe(Object before, Object after, DescribeContext ctx)
        {
            var sb = new SerializedObject(before);
            var sa = new SerializedObject(after);

            Walk(sa, sb, ctx, true);
            Walk(sb, sa, ctx, false);
        }

        /// <summary>
        /// Проход по одной стороне. На стороне «стало» ищутся изменённые и новые
        /// поля, на стороне «было» — только исчезнувшие: изменённые уже найдены.
        /// </summary>
        private static void Walk(SerializedObject primary, SerializedObject other, DescribeContext ctx, bool afterSide)
        {
            var labels = new List<string>();
            var it = primary.GetIterator();

            bool enter = true;
            int guard = 0;

            while (it.NextVisible(enter) && ++guard < 200000 && !ctx.Full)
            {
                int depth = it.depth;
                while (labels.Count > depth) labels.RemoveAt(labels.Count - 1);
                labels.Add(it.displayName);

                if (depth == 0 && Skipped.Contains(it.name)) { enter = false; continue; }

                var counterpart = other.FindProperty(it.propertyPath);
                bool container = IsContainer(it);

                if (counterpart == null)
                {
                    enter = false;
                    var value = container ? ChangeValue.Of(L.T("present")) : ChangeValue.From(it);

                    var item = afterSide
                        ? ctx.Add(Label(labels), ChangeValue.None, value, ChangeKind.Added)
                        : ctx.Add(Label(labels), value, ChangeValue.None, ChangeKind.Removed);

                    item.Tooltip = item.Key = it.propertyPath;

                    // Исчезнувшее поле возвращается в «стало» копированием. Новое
                    // так не убрать — у массива его убирает откат размера.
                    if (!afterSide) item.AddPath(it.propertyPath);
                    continue;
                }

                enter = container;
                if (container || !afterSide) continue;
                if (Same(counterpart, it, ctx)) continue;

                var change = ctx.Add(Label(labels), ChangeValue.From(counterpart), ChangeValue.From(it));
                change.Tooltip = it.propertyPath;
                change.AddPath(it.propertyPath);
            }
        }

        private static bool IsContainer(SerializedProperty p)
        {
            return p.hasVisibleChildren &&
                   (p.propertyType == SerializedPropertyType.Generic ||
                    p.propertyType == SerializedPropertyType.ManagedReference ||
                    (p.isArray && p.propertyType != SerializedPropertyType.String));
        }

        private static bool Same(SerializedProperty before, SerializedProperty after, DescribeContext ctx)
        {
            if (before.propertyType != after.propertyType) return false;

            if (after.propertyType == SerializedPropertyType.ObjectReference)
                return ctx.SameRef(before.objectReferenceValue, after.objectReferenceValue);

            return SerializedProperty.DataEquals(before, after);
        }

        private static string Label(List<string> labels)
        {
            return string.Join(" › ", labels.ToArray());
        }
    }

    /// <summary>Откат одного изменения в живой ассет проекта — с Undo и сохранением файла.</summary>
    internal static class ChangeReverter
    {
        public static bool CanRevert(ChangeItem item, LoadedAsset after)
        {
            if (item == null || after == null || !after.Live) return false;
            if (item.BeforeTarget == null || item.AfterTarget == null) return false;
            if (item.Revert != null) return true;
            if (item.SerializedPaths.Count == 0) return false;

            var source = new SerializedObject(item.BeforeTarget);
            foreach (var path in item.SerializedPaths)
                if (source.FindProperty(path) == null) return false;

            return true;
        }

        /// <summary>null — откат выполнен; иначе причина, почему нет.</summary>
        public static string Revert(ChangeItem item)
        {
            var live = item.AfterTarget;
            var source = item.BeforeTarget;
            if (live == null || source == null) return L.T("Nothing to revert to.");

            Undo.RecordObject(live, L.F("Revert: {0}", item.Label));

            if (item.Revert != null)
            {
                item.Revert(live, source);
            }
            else
            {
                var src = new SerializedObject(source);
                var dst = new SerializedObject(live);

                foreach (var path in item.SerializedPaths)
                {
                    var from = src.FindProperty(path);
                    if (from == null) continue;

                    dst.CopyFromSerializedProperty(from);

                    // Ссылка на подобъект версии из git указывала бы на временный
                    // объект, который скоро исчезнет. Переводим её на пару в проекте.
                    if (!RemapReferences(dst.FindProperty(path), item.Pairs))
                        return L.T("The field refers to an object that is not in the current version — revert canceled.");
                }

                dst.ApplyModifiedProperties();
            }

            EditorUtility.SetDirty(live);
            AssetDatabase.SaveAssetIfDirty(live);
            return null;
        }

        private static bool RemapReferences(SerializedProperty property, Dictionary<Object, Object> pairs)
        {
            if (property == null) return true;

            var it = property.Copy();
            var end = property.GetEndProperty();

            do
            {
                if (SerializedProperty.EqualContents(it, end)) break;
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;

                var value = it.objectReferenceValue;
                if (value == null || EditorUtility.IsPersistent(value)) continue;

                Object mapped;
                if (pairs == null || !pairs.TryGetValue(value, out mapped) || mapped == null) return false;
                it.objectReferenceValue = mapped;
            }
            while (it.Next(true));

            return true;
        }
    }
}
