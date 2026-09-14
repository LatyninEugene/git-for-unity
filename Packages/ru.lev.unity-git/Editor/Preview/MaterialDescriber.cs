using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lev.Git.Preview
{
    /// <summary>
    /// Изменения материала словами его шейдера: «Color: белый → красный» вместо
    /// трёх строк m_SavedProperties.m_Colors[0]._BaseColor.r/g/b.
    ///
    /// Подписи, типы и скрытость берутся у шейдера, значения — через API
    /// материала. Им же делается откат: путь в сериализации у свойства материала
    /// зависит от набора остальных свойств, а SetColor — нет.
    /// </summary>
    internal sealed class MaterialDescriber : ChangeDescriber<Material>
    {
        protected override void Describe(Material before, Material after, DescribeContext ctx)
        {
            if (before.shader != after.shader)
            {
                var item = ctx.Add(L.T("Shader"), ChangeValue.Of(ShaderName(before)), ChangeValue.Of(ShaderName(after)));
                item.AddPath("m_Shader");
            }

            var shader = after.shader != null ? after.shader : before.shader;
            if (shader != null) DescribeProperties(shader, before, after, ctx);

            int queueBefore = RawQueue(before), queueAfter = RawQueue(after);
            if (queueBefore != queueAfter)
            {
                var item = ctx.Add("Render Queue", ChangeValue.Of(QueueText(queueBefore, before)), ChangeValue.Of(QueueText(queueAfter, after)));
                item.AddPath("m_CustomRenderQueue");
            }

            if (before.enableInstancing != after.enableInstancing)
                ctx.Add("Enable GPU Instancing", ChangeValue.Of(before.enableInstancing), ChangeValue.Of(after.enableInstancing), ChangeKind.Modified, "enableInstancing")
                   .Revert = (live, src) => ((Material)live).enableInstancing = ((Material)src).enableInstancing;

            if (before.doubleSidedGI != after.doubleSidedGI)
                ctx.Add("Double Sided Global Illumination", ChangeValue.Of(before.doubleSidedGI), ChangeValue.Of(after.doubleSidedGI))
                   .Revert = (live, src) => ((Material)live).doubleSidedGI = ((Material)src).doubleSidedGI;

            if (before.globalIlluminationFlags != after.globalIlluminationFlags)
                ctx.Add("Global Illumination", ChangeValue.Of(before.globalIlluminationFlags.ToString()), ChangeValue.Of(after.globalIlluminationFlags.ToString()))
                   .Revert = (live, src) => ((Material)live).globalIlluminationFlags = ((Material)src).globalIlluminationFlags;

            DescribeKeywords(before, after, ctx);
            DescribePasses(before, after, ctx);
        }

        private static void DescribeProperties(Shader shader, Material before, Material after, DescribeContext ctx)
        {
            for (int i = 0; i < shader.GetPropertyCount(); i++)
            {
                var name = shader.GetPropertyName(i);
                if (!before.HasProperty(name) || !after.HasProperty(name)) continue;

                var description = shader.GetPropertyDescription(i);
                var label = string.IsNullOrEmpty(description) || description.StartsWith("_", StringComparison.Ordinal) ? name : description;
                bool hidden = (shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0;
                var attributes = shader.GetPropertyAttributes(i);

                var added = new List<ChangeItem>();

                switch (shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                    {
                        Color b = before.GetColor(name), a = after.GetColor(name);
                        if (Same(b, a)) break;
                        var item = ctx.Add(label, ChangeValue.Of(b), ChangeValue.Of(a));
                        item.Revert = (live, src) => ((Material)live).SetColor(name, ((Material)src).GetColor(name));
                        added.Add(item);
                        break;
                    }

                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                    {
                        float b = before.GetFloat(name), a = after.GetFloat(name);
                        if (Mathf.Abs(b - a) < 1e-6f) break;
                        var item = ctx.Add(label, ChangeValue.Of(FloatText(b, attributes)), ChangeValue.Of(FloatText(a, attributes)));
                        item.Revert = (live, src) => ((Material)live).SetFloat(name, ((Material)src).GetFloat(name));
                        added.Add(item);
                        break;
                    }

                    case ShaderPropertyType.Int:
                    {
                        int b = before.GetInteger(name), a = after.GetInteger(name);
                        if (b == a) break;
                        var item = ctx.Add(label, ChangeValue.Of(b.ToString(CultureInfo.InvariantCulture)), ChangeValue.Of(a.ToString(CultureInfo.InvariantCulture)));
                        item.Revert = (live, src) => ((Material)live).SetInteger(name, ((Material)src).GetInteger(name));
                        added.Add(item);
                        break;
                    }

                    case ShaderPropertyType.Vector:
                    {
                        Vector4 b = before.GetVector(name), a = after.GetVector(name);
                        if ((b - a).sqrMagnitude < 1e-12f) break;
                        var item = ctx.Add(label, ChangeValue.Of(PreviewText.Vector(b.x, b.y, b.z, b.w)), ChangeValue.Of(PreviewText.Vector(a.x, a.y, a.z, a.w)));
                        item.Revert = (live, src) => ((Material)live).SetVector(name, ((Material)src).GetVector(name));
                        added.Add(item);
                        break;
                    }

                    case ShaderPropertyType.Texture:
                    {
                        Texture b = before.GetTexture(name), a = after.GetTexture(name);
                        if (!ctx.SameRef(b, a))
                        {
                            var item = ctx.Add(label, ChangeValue.Of(b), ChangeValue.Of(a));
                            item.Revert = (live, src) => ((Material)live).SetTexture(name, ((Material)src).GetTexture(name));
                            added.Add(item);
                        }

                        if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.NoScaleOffset) != 0) break;

                        Vector2 sb = before.GetTextureScale(name), sa = after.GetTextureScale(name);
                        if ((sb - sa).sqrMagnitude > 1e-12f)
                        {
                            var item = ctx.Add(label + " · Tiling", ChangeValue.Of(PreviewText.Vector(sb.x, sb.y)), ChangeValue.Of(PreviewText.Vector(sa.x, sa.y)));
                            item.Revert = (live, src) => ((Material)live).SetTextureScale(name, ((Material)src).GetTextureScale(name));
                            added.Add(item);
                        }

                        Vector2 ob = before.GetTextureOffset(name), oa = after.GetTextureOffset(name);
                        if ((ob - oa).sqrMagnitude > 1e-12f)
                        {
                            var item = ctx.Add(label + " · Offset", ChangeValue.Of(PreviewText.Vector(ob.x, ob.y)), ChangeValue.Of(PreviewText.Vector(oa.x, oa.y)));
                            item.Revert = (live, src) => ((Material)live).SetTextureOffset(name, ((Material)src).GetTextureOffset(name));
                            added.Add(item);
                        }
                        break;
                    }
                }

                foreach (var item in added)
                {
                    item.Tooltip = item.Key = name;
                    if (!hidden) continue;
                    item.Folded = true;
                    item.Note = L.T("hidden shader property");
                }
            }
        }

        private static void DescribeKeywords(Material before, Material after, DescribeContext ctx)
        {
            var b = new HashSet<string>(before.shaderKeywords ?? new string[0]);
            var a = new HashSet<string>(after.shaderKeywords ?? new string[0]);

            var all = new SortedSet<string>(b, StringComparer.Ordinal);
            all.UnionWith(a);

            foreach (var keyword in all)
            {
                bool wasOn = b.Contains(keyword), isOn = a.Contains(keyword);
                if (wasOn == isOn) continue;

                var k = keyword;
                var item = ctx.Add(L.F("Keyword {0}", k), ChangeValue.Of(wasOn), ChangeValue.Of(isOn));
                item.Folded = true;
                item.Note = L.T("usually follows from properties");
                item.Revert = (live, src) =>
                {
                    var m = (Material)live;
                    if (((Material)src).IsKeywordEnabled(k)) m.EnableKeyword(k); else m.DisableKeyword(k);
                };
            }
        }

        private static void DescribePasses(Material before, Material after, DescribeContext ctx)
        {
            if (before.shader != after.shader) return;

            for (int i = 0; i < after.passCount; i++)
            {
                var pass = after.GetPassName(i);
                if (string.IsNullOrEmpty(pass)) continue;

                bool b = before.GetShaderPassEnabled(pass), a = after.GetShaderPassEnabled(pass);
                if (b == a) continue;

                var item = ctx.Add(L.F("Pass {0}", pass), ChangeValue.Of(b), ChangeValue.Of(a));
                item.Folded = true;
                item.Revert = (live, src) => ((Material)live).SetShaderPassEnabled(pass, ((Material)src).GetShaderPassEnabled(pass));
            }
        }

        private static bool Same(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 1e-5f && Mathf.Abs(a.g - b.g) < 1e-5f &&
                   Mathf.Abs(a.b - b.b) < 1e-5f && Mathf.Abs(a.a - b.a) < 1e-5f;
        }

        private static string ShaderName(Material m)
        {
            return m.shader != null ? m.shader.name : "None";
        }

        private static int RawQueue(Material m)
        {
            var property = new SerializedObject(m).FindProperty("m_CustomRenderQueue");
            return property != null ? property.intValue : m.renderQueue;
        }

        private static readonly KeyValuePair<int, string>[] Queues =
        {
            new KeyValuePair<int, string>(1000, "Background"),
            new KeyValuePair<int, string>(2000, "Geometry"),
            new KeyValuePair<int, string>(2450, "AlphaTest"),
            new KeyValuePair<int, string>(3000, "Transparent"),
            new KeyValuePair<int, string>(4000, "Overlay")
        };

        private static string QueueText(int raw, Material m)
        {
            if (raw < 0)
                return L.T("from shader") + (m.shader != null ? " (" + m.shader.renderQueue + ")" : string.Empty);

            string name = null;
            int start = 0;
            foreach (var q in Queues)
                if (raw >= q.Key) { name = q.Value; start = q.Key; }

            if (name == null) return raw.ToString(CultureInfo.InvariantCulture);
            return raw + " (" + name + (raw > start ? "+" + (raw - start) : string.Empty) + ")";
        }

        /// <summary>Число с учётом атрибутов шейдера: [Toggle] — вкл/выкл, [Enum] и [KeywordEnum] — имя варианта.</summary>
        private static string FloatText(float value, string[] attributes)
        {
            int rounded = Mathf.RoundToInt(value);

            if (attributes != null)
            {
                foreach (var raw in attributes)
                {
                    var attribute = raw.Trim();

                    if (attribute.StartsWith("Toggle", StringComparison.Ordinal) ||
                        attribute.StartsWith("MaterialToggle", StringComparison.Ordinal))
                        return value > 0.5f ? L.T("on") : L.T("off");

                    if (attribute.StartsWith("KeywordEnum(", StringComparison.Ordinal))
                    {
                        var names = Arguments(attribute);
                        if (rounded >= 0 && rounded < names.Length) return names[rounded];
                    }

                    if (attribute.StartsWith("Enum(", StringComparison.Ordinal))
                    {
                        var args = Arguments(attribute);
                        if (args.Length == 1)
                        {
                            var type = FindEnum(args[0]);
                            var name = type != null ? Enum.GetName(type, rounded) : null;
                            if (name != null) return name;
                        }
                        else
                        {
                            for (int i = 0; i + 1 < args.Length; i += 2)
                            {
                                float v;
                                if (float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v) &&
                                    Mathf.Approximately(v, value)) return args[i];
                            }
                        }
                    }
                }
            }

            return PreviewText.Number(value);
        }

        private static string[] Arguments(string attribute)
        {
            int open = attribute.IndexOf('('), close = attribute.LastIndexOf(')');
            if (open < 0 || close <= open) return new string[0];

            var parts = attribute.Substring(open + 1, close - open - 1).Split(',');
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return parts;
        }

        private static readonly Dictionary<string, Type> Enums = new Dictionary<string, Type>(StringComparer.Ordinal);

        private static Type FindEnum(string name)
        {
            Type type;
            if (Enums.TryGetValue(name, out type)) return type;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(name, false);
                    if (type != null && type.IsEnum) break;
                    type = null;
                }
                catch { }
            }

            Enums[name] = type;
            return type;
        }
    }
}
