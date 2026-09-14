using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lev.Git.Preview
{
    /// <summary>Один спрайт из нарезки текстуры: имя и прямоугольник в пикселях, начало — внизу слева.</summary>
    public sealed class SpriteSlice
    {
        public string Name;
        public float X, Y, Width, Height;
        public string Border;

        public string RectText
        {
            get
            {
                return "(" + N(X) + ", " + N(Y) + ") " + N(Width) + "×" + N(Height);
            }
        }

        private static string N(float v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }
    }

    public enum SliceStatus { Same, Changed, Added, Removed }

    /// <summary>Одно изменение настроек импорта — уже словами, как в инспекторе импортёра.</summary>
    public sealed class ImporterChange
    {
        public string Group;
        public string Label;
        public string Path;
        public string Before;
        public string After;
    }

    /// <summary>
    /// Разбор .meta без Unity: настройки импорта и нарезка спрайтов.
    ///
    /// Мета — тот же YAML, только без заголовков документов: сначала
    /// fileFormatVersion и guid, затем раздел импортёра. Перед разделом
    /// дописывается заголовок документа, и дальше работает обычный разбор.
    ///
    /// Подписи — имена полей в человеческом виде и несколько известных
    /// переводов («maxTextureSize» → «Max Size»), значения перечислений —
    /// именами. Незнакомое поле всё равно показывается, просто без перевода.
    /// </summary>
    public static class ImporterMeta
    {
        private static readonly Regex ImporterLine = new Regex(@"^([A-Za-z0-9]+Importer):[ \t]*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        public static UnityDocument Parse(string meta)
        {
            if (string.IsNullOrEmpty(meta)) return null;

            var text = meta.Replace("\r\n", "\n");
            var match = ImporterLine.Match(text);
            if (!match.Success) return null;

            var docs = UnityYamlParser.Parse("--- !u!1 &1\n" + text.Substring(match.Index));
            return docs.Count > 0 ? docs[0] : null;
        }

        // ---------------------------------------------------- нарезка спрайтов ---

        public static List<SpriteSlice> Slices(UnityDocument importer)
        {
            var list = new List<SpriteSlice>();
            if (importer == null) return list;

            for (int i = 0; i < 100000; i++)
            {
                var prefix = "spriteSheet.sprites[" + i + "].";
                var name = importer.Get(prefix + "name");
                var x = importer.Get(prefix + "rect.x");
                if (name == null && x == null) break;

                list.Add(new SpriteSlice
                {
                    Name = Unquote(name) ?? "#" + i,
                    X = F(x),
                    Y = F(importer.Get(prefix + "rect.y")),
                    Width = F(importer.Get(prefix + "rect.width")),
                    Height = F(importer.Get(prefix + "rect.height")),
                    Border = importer.Get(prefix + "border")
                });
            }

            return list;
        }

        /// <summary>Чем спрайт стороны отличается от другой стороны; у «было» пропавший — удалён, у «стало» новый — добавлен.</summary>
        public static SliceStatus StatusOf(SpriteSlice slice, List<SpriteSlice> other, bool isAfter)
        {
            if (other == null) return SliceStatus.Same;

            foreach (var o in other)
            {
                if (o.Name != slice.Name) continue;
                return SameSlice(o, slice) ? SliceStatus.Same : SliceStatus.Changed;
            }

            return isAfter ? SliceStatus.Added : SliceStatus.Removed;
        }

        public static List<ImporterChange> SliceChanges(List<SpriteSlice> before, List<SpriteSlice> after)
        {
            var result = new List<ImporterChange>();
            if (before == null || after == null) return result;

            var old = new Dictionary<string, SpriteSlice>(StringComparer.Ordinal);
            foreach (var s in before) old[s.Name] = s;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in after)
            {
                seen.Add(s.Name);

                SpriteSlice b;
                if (!old.TryGetValue(s.Name, out b))
                    result.Add(new ImporterChange { Label = s.Name, Before = "—", After = s.RectText, Path = "slice:" + s.Name });
                else if (!SameSlice(b, s))
                    result.Add(new ImporterChange
                    {
                        Label = s.Name,
                        Before = b.RectText + BorderSuffix(b, s),
                        After = s.RectText + BorderSuffix(s, b),
                        Path = "slice:" + s.Name
                    });
            }

            foreach (var s in before)
                if (!seen.Contains(s.Name))
                    result.Add(new ImporterChange { Label = s.Name, Before = s.RectText, After = "—", Path = "slice:" + s.Name });

            return result;
        }

        private static string BorderSuffix(SpriteSlice mine, SpriteSlice other)
        {
            return mine.Border != other.Border && mine.Border != null ? L.F(", border {0}", mine.Border) : string.Empty;
        }

        private static bool SameSlice(SpriteSlice a, SpriteSlice b)
        {
            return a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height && a.Border == b.Border;
        }

        // ------------------------------------------------ настройки импорта ---

        private static readonly HashSet<string> SkippedFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "serializedVersion", "fileFormatVersion", "guid", "userData", "assetBundleName", "assetBundleVariant",
            "spriteID", "internalID"
        };

        private static readonly string[] SkippedPrefixes =
        {
            "spriteSheet", "internalIDToNameTable", "nameFileIdTable", "fileIDToRecycleName", "externalObjects"
        };

        public static List<ImporterChange> Changes(UnityDocument before, UnityDocument after)
        {
            var result = new List<ImporterChange>();
            if (before == null || after == null) return result;

            var keys = new List<string>(after.Props.Keys);
            foreach (var key in before.Props.Keys)
                if (!after.Props.ContainsKey(key)) keys.Add(key);

            foreach (var key in keys)
            {
                if (Skipped(key)) continue;

                var b = before.Get(key);
                var a = after.Get(key);
                if (b == a) continue;

                result.Add(new ImporterChange
                {
                    Path = key,
                    Group = GroupOf(key, after.Props.ContainsKey(key) ? after : before),
                    Label = LabelOf(key),
                    Before = ValueOf(key, b),
                    After = ValueOf(key, a)
                });
            }

            return result;
        }

        private static bool Skipped(string key)
        {
            foreach (var prefix in SkippedPrefixes)
                if (key.StartsWith(prefix, StringComparison.Ordinal)) return true;

            return SkippedFields.Contains(LastField(key));
        }

        /// <summary>Поле без пути и индексов: «platformSettings[2].maxTextureSize» → «maxTextureSize».</summary>
        private static string LastField(string key)
        {
            var last = key.Substring(key.LastIndexOf('.') + 1);
            int bracket = last.IndexOf('[');
            return bracket >= 0 ? last.Substring(0, bracket) : last;
        }

        private static string GroupOf(string key, UnityDocument doc)
        {
            int dot = key.IndexOf('.');
            int bracket = key.IndexOf('[');
            if (dot < 0 && bracket < 0) return L.T("General");

            // Элемент списка настроек платформы — группа по самой платформе.
            if (bracket >= 0 && (dot < 0 || bracket < dot))
            {
                int close = key.IndexOf(']', bracket);
                if (close > 0)
                {
                    var target = doc.Get(key.Substring(0, close + 1) + ".buildTarget");
                    if (target != null) return L.F("Platform {0}", PlatformName(Unquote(target)));
                }
            }

            int cut = dot < 0 ? bracket : bracket < 0 ? dot : Math.Min(dot, bracket);
            return Nicify(key.Substring(0, cut));
        }

        private static string PlatformName(string target)
        {
            switch (target)
            {
                case "DefaultTexturePlatform": return L.Tc("texture platform", "Default");
                case "iPhone": return "iOS";
                default: return target;
            }
        }

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "sRGBTexture", "sRGB (Color Texture)" },
            { "isReadable", "Read/Write" },
            { "maxTextureSize", "Max Size" },
            { "textureCompression", "Compression" },
            { "compressionQuality", "Compressor Quality" },
            { "crunchedCompression", "Use Crunch Compression" },
            { "spritePixelsToUnits", "Pixels Per Unit" },
            { "aniso", "Aniso Level" },
            { "wrapU", "Wrap Mode U" },
            { "wrapV", "Wrap Mode V" },
            { "wrapW", "Wrap Mode W" },
            { "npotScale", "Non-Power of 2" },
            { "enableMipMap", "Generate Mipmaps" },
            { "sampleRateOverride", "Sample Rate" }
        };

        private static string LabelOf(string key)
        {
            var field = LastField(key);
            string label;
            return Labels.TryGetValue(field, out label) ? label : Nicify(field);
        }

        private static Dictionary<string, string> Enum(params string[] pairs)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < pairs.Length; i += 2) map[pairs[i]] = pairs[i + 1];
            return map;
        }

        /// <summary>Значение «не задано» в таблице ниже; переводится при показе.</summary>
        private static readonly string NotSet = "not set";

        private static readonly Dictionary<string, Dictionary<string, string>> Enums =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
            {
                { "textureType", Enum("0", "Default", "1", "Normal map", "2", "Editor GUI and Legacy GUI", "4", "Cookie",
                                      "6", "Lightmap", "7", "Cursor", "8", "Sprite (2D and UI)", "10", "Single Channel",
                                      "11", "Directional Lightmap", "12", "Shadowmask") },
                { "textureShape", Enum("1", "2D", "2", "Cube", "4", "2D Array", "8", "3D") },
                { "spriteMode", Enum("0", "None", "1", "Single", "2", "Multiple", "3", "Polygon") },
                { "spriteMeshType", Enum("0", "Full Rect", "1", "Tight") },
                { "filterMode", Enum("-1", NotSet, "0", "Point (no filter)", "1", "Bilinear", "2", "Trilinear") },
                { "wrapU", Enum("-1", NotSet, "0", "Repeat", "1", "Clamp", "2", "Mirror", "3", "Mirror Once") },
                { "wrapV", Enum("-1", NotSet, "0", "Repeat", "1", "Clamp", "2", "Mirror", "3", "Mirror Once") },
                { "wrapW", Enum("-1", NotSet, "0", "Repeat", "1", "Clamp", "2", "Mirror", "3", "Mirror Once") },
                { "textureCompression", Enum("0", "None", "1", "Normal Quality", "2", "High Quality", "3", "Low Quality") },
                { "alphaSource", Enum("0", "None", "1", "Input Texture Alpha", "2", "From Gray Scale") },
                { "npotScale", Enum("0", "None", "1", "To nearest", "2", "To larger", "3", "To smaller") },
                { "mipMapMode", Enum("0", "Box", "1", "Kaiser") },
                { "resizeAlgorithm", Enum("0", "Mitchell", "1", "Bilinear") },
                { "loadType", Enum("0", "Decompress On Load", "1", "Compressed In Memory", "2", "Streaming") },
                { "compressionFormat", Enum("0", "PCM", "1", "Vorbis", "2", "ADPCM", "3", "MP3", "7", "AAC") },
                { "sampleRateSetting", Enum("0", "Preserve Sample Rate", "1", "Optimize Sample Rate", "2", "Override Sample Rate") }
            };

        private static readonly HashSet<string> Booleans = new HashSet<string>(StringComparer.Ordinal)
        {
            "sRGBTexture", "isReadable", "enableMipMap", "alphaIsTransparency", "crunchedCompression", "streamingMipmaps",
            "overridden", "borderMipMap", "fadeOut", "ignorePngGamma", "mipMapsPreserveCoverage", "forceToMono",
            "normalize", "preloadAudioData", "loadInBackground", "ambisonic", "3D", "allowsAlphaSplitting",
            "vTOnly", "applyGammaDecoding"
        };

        private static readonly string[] BooleanPrefixes =
        {
            "is", "enable", "use", "import", "generate", "preserve", "keep", "optimize", "force", "allow", "ignore"
        };

        private static string ValueOf(string key, string raw)
        {
            if (raw == null) return "—";

            var field = LastField(key);
            var value = Unquote(raw.Trim());

            Dictionary<string, string> names;
            string name;
            if (Enums.TryGetValue(field, out names) && names.TryGetValue(value, out name))
                return ReferenceEquals(name, NotSet) ? L.Tc("importer", "not set") : name;

            if ((value == "0" || value == "1") && IsBoolean(field))
                return value == "1" ? L.T("on") : L.T("off");

            return value.Length == 0 ? L.T("“”") : value;
        }

        private static bool IsBoolean(string field)
        {
            if (Booleans.Contains(field)) return true;

            foreach (var prefix in BooleanPrefixes)
                if (field.Length > prefix.Length && field.StartsWith(prefix, StringComparison.Ordinal) &&
                    char.IsUpper(field[prefix.Length])) return true;

            return false;
        }

        /// <summary>«enableMipMap» → «Enable Mip Map», «platformSettings» → «Platform Settings».</summary>
        public static string Nicify(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (key.StartsWith("m_", StringComparison.Ordinal)) key = key.Substring(2);
            if (key.Length == 0) return key;

            var sb = new StringBuilder(key.Length + 8);
            sb.Append(char.ToUpperInvariant(key[0]));

            for (int i = 1; i < key.Length; i++)
            {
                char c = key[i];
                char prev = key[i - 1];
                bool next = i + 1 < key.Length && char.IsLower(key[i + 1]);

                if (char.IsUpper(c) && (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && next)))
                    sb.Append(' ');

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static string Unquote(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (s.Length >= 2 && (s[0] == '\'' || s[0] == '"') && s[s.Length - 1] == s[0])
                return s.Substring(1, s.Length - 2);
            return s;
        }

        private static float F(string s)
        {
            float v;
            return s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0f;
        }
    }
}
