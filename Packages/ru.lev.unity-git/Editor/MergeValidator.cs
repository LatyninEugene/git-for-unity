using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Lev.Git
{
    public enum MergeIssueSeverity
    {
        Warning = 0,
        Error
    }

    /// <summary>Что не так со слитым файлом.</summary>
    public sealed class MergeIssue
    {
        public MergeIssueSeverity Severity;
        public long FileId;
        public string Title;
        public string Detail;

        public override string ToString() { return Title + ": " + Detail; }
    }

    /// <summary>
    /// Проверка результата слияния до записи на диск.
    ///
    /// Слияние по свойствам решает за каждый документ отдельно и поэтому может
    /// собрать файл, в котором каждый документ правильный, а вместе они — нет:
    /// компонент без объекта, ребёнок, которого родитель не знает, ссылка на
    /// объект, удалённый другой стороной. Unity такую сцену откроет молча и
    /// потеряет куски, поэтому сказать надо здесь.
    ///
    /// Битой считается только ссылка на fileID, который существовал хотя бы
    /// на одной стороне: ссылки внутрь экземпляров префабов и на встроенные
    /// ресурсы в файле не лежат, и ругаться на них нельзя.
    /// </summary>
    public static class MergeValidator
    {
        private const int MaxIssues = 200;

        private static readonly Regex GuidLine = new Regex(@"^guid:\s*([0-9a-fA-F]+)\s*$", RegexOptions.Multiline);

        public static List<MergeIssue> Check(string merged, YamlMergeResult merge)
        {
            var issues = new List<MergeIssue>();
            if (merged == null || merge == null) return issues;

            if (!UnityYamlParser.LooksLikeUnityYaml(merged))
            {
                CheckMetaGuid(merged, merge, issues);
                return issues;
            }

            var scene = UnityScene.Build(merged);

            var known = new HashSet<long>();
            foreach (var d in merge.Documents) known.Add(d.FileId);

            CheckDuplicates(scene, issues);
            CheckReferences(scene, known, issues);
            CheckComponents(scene, issues);
            CheckHierarchy(scene, issues);

            return issues;
        }

        public static int Errors(List<MergeIssue> issues)
        {
            int n = 0;
            foreach (var i in issues) if (i.Severity == MergeIssueSeverity.Error) n++;
            return n;
        }

        // --------------------------------------------------------- мета ---

        private static void CheckMetaGuid(string merged, YamlMergeResult merge, List<MergeIssue> issues)
        {
            var mine = GuidOf(merge.MineFile);
            var theirs = GuidOf(merge.TheirsFile);
            if (mine == null || theirs == null || mine == theirs) return;

            var result = GuidLine.Match(merged);
            var kept = result.Success ? result.Groups[1].Value : null;

            issues.Add(new MergeIssue
            {
                Severity = MergeIssueSeverity.Warning,
                Title = L.T("The asset has a different GUID on each side"),
                Detail = L.F("Everything that references the asset by GUID {0} will lose the reference after the merge. This usually means the asset was created independently in two branches.", kept == mine ? theirs : mine)
            });
        }

        private static string GuidOf(YamlFile file)
        {
            if (file == null) return null;
            foreach (var b in file.Blocks)
                foreach (var l in b.Lines)
                {
                    var m = GuidLine.Match(l);
                    if (m.Success) return m.Groups[1].Value;
                }
            return null;
        }

        // -------------------------------------------------------- сцена ---

        private static void CheckDuplicates(UnityScene scene, List<MergeIssue> issues)
        {
            var seen = new HashSet<long>();
            foreach (var d in scene.Documents)
            {
                if (seen.Add(d.FileId)) continue;
                Add(issues, MergeIssueSeverity.Error, d.FileId, L.T("Duplicate fileID"),
                    L.F("Document {0} ({1}) appears twice — Unity will load only one.", d.FileId, d.TypeName));
            }
        }

        private static void CheckReferences(UnityScene scene, HashSet<long> known, List<MergeIssue> issues)
        {
            foreach (var d in scene.Documents)
            {
                foreach (var p in d.Props)
                {
                    var id = UnityScene.RefId(p.Value);
                    if (id == 0 || scene.ById.ContainsKey(id) || !known.Contains(id)) continue;

                    Add(issues, MergeIssueSeverity.Error, d.FileId, L.T("Reference to a deleted object"),
                        L.F("{0} · {1} points to {2}, which isn't in the result.", scene.Describe(d), p.Key, id));

                    if (issues.Count >= MaxIssues) return;
                }
            }
        }

        private static void CheckComponents(UnityScene scene, List<MergeIssue> issues)
        {
            foreach (var d in scene.Documents)
            {
                var owner = UnityScene.RefId(d.Get("m_GameObject"));
                if (owner == 0) continue;

                UnityDocument go;
                if (!scene.ById.TryGetValue(owner, out go) || go.TypeName != "GameObject") continue;

                // Урезанный документ экземпляра префаба списка компонентов не хранит.
                if (go.Get("m_Component[0].component") == null && go.Get("m_Name") == null) continue;

                if (ListContains(go, "m_Component[", "].component", d.FileId)) continue;

                Add(issues, MergeIssueSeverity.Error, d.FileId, L.T("Component isn't listed on its object"),
                    L.F("{0} references “{1}”, but isn't in its component list — Unity won't show it.", d.TypeName, go.Get("m_Name") ?? owner.ToString()));
            }
        }

        private static void CheckHierarchy(UnityScene scene, List<MergeIssue> issues)
        {
            foreach (var d in scene.Documents)
            {
                if (d.TypeName != "Transform" && d.TypeName != "RectTransform") continue;
                if (d.Get("m_GameObject") == null) continue;   // урезанный

                var father = UnityScene.RefId(d.Get("m_Father"));
                UnityDocument f;
                if (father != 0 && scene.ById.TryGetValue(father, out f) && f.Get("m_GameObject") != null &&
                    !ListContains(f, "m_Children[", "]", d.FileId))
                {
                    Add(issues, MergeIssueSeverity.Error, d.FileId, L.T("Object is missing from its parent's children"),
                        L.F("{0} considers {1} its parent, but the parent doesn't list it. The hierarchy will be different after loading.", scene.Describe(d), scene.Describe(f)));
                }

                for (int i = 0; ; i++)
                {
                    var value = d.Get("m_Children[" + i + "]");
                    if (value == null) break;

                    var child = UnityScene.RefId(value);
                    UnityDocument c;
                    if (child == 0 || !scene.ById.TryGetValue(child, out c) || c.Get("m_GameObject") == null) continue;

                    if (UnityScene.RefId(c.Get("m_Father")) == d.FileId) continue;

                    Add(issues, MergeIssueSeverity.Error, child, L.T("Child points to a different parent"),
                        L.F("{0} is listed under {1}, but its m_Father is different.", scene.Describe(c), scene.Describe(d)));
                }

                if (issues.Count >= MaxIssues) return;
            }
        }

        private static bool ListContains(UnityDocument doc, string prefix, string suffix, long id)
        {
            for (int i = 0; ; i++)
            {
                var value = doc.Get(prefix + i + suffix);
                if (value == null) return false;
                if (UnityScene.RefId(value) == id) return true;
            }
        }

        private static void Add(List<MergeIssue> issues, MergeIssueSeverity severity, long id, string title, string detail)
        {
            if (issues.Count >= MaxIssues) return;
            issues.Add(new MergeIssue { Severity = severity, FileId = id, Title = title, Detail = detail });
        }
    }
}
