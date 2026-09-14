using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Lev.Git.Preview
{
    /// <summary>
    /// То, что системе превью нужно знать о YAML-файле ассета, без Unity:
    /// какой документ в нём главный и как путь сериализованного поля пишется в
    /// самом файле. Отдельно от загрузчика, чтобы проверялось тестами вне редактора.
    /// </summary>
    public static class AssetYaml
    {
        /// <summary>
        /// Ассеты, которые Unity хранит YAML-текстом и умеет прочитать без импорта.
        /// Сцены и префабы сюда не входят: их показывает дерево объектов, а
        /// собранный из YAML префаб без вложенных префабов показал бы неправду.
        /// </summary>
        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mat", ".asset", ".anim", ".controller", ".overrideController", ".mask",
            ".physicMaterial", ".physicsMaterial", ".physicsMaterial2D", ".playable", ".signal",
            ".lighting", ".terrainlayer", ".flare", ".renderTexture", ".cubemap", ".guiskin",
            ".fontsettings", ".mixer", ".brush", ".spriteatlas", ".spriteatlasv2", ".giparams",
            ".preset", ".shadervariants"
        };

        private static readonly Regex ArrayElement = new Regex(@"\.Array\.data\[(\d+)\]", RegexOptions.CultureInvariant);

        public static bool IsYamlAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            int dot = path.LastIndexOf('.');
            int slash = path.LastIndexOf('/');
            return dot > slash && Extensions.Contains(path.Substring(dot));
        }

        /// <summary>Файл сохранён текстом, а не бинарной сериализацией.</summary>
        public static bool LooksLikeYaml(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 5 &&
                   bytes[0] == '%' && bytes[1] == 'Y' && bytes[2] == 'A' && bytes[3] == 'M' && bytes[4] == 'L';
        }

        /// <summary>
        /// Главный объект файла. У Unity его локальный идентификатор — номер класса,
        /// умноженный на 100000: материал 2100000, клип 7400000, ScriptableObject
        /// 11400000. Файлы настроек проекта этому правилу не следуют — там берём
        /// первый объект, не помеченный как служебный.
        /// </summary>
        public static UnityDocument MainDocument(IList<UnityDocument> docs)
        {
            if (docs == null || docs.Count == 0) return null;

            foreach (var d in docs)
                if (d.ClassId > 0 && d.FileId == d.ClassId * 100000L) return d;

            foreach (var d in docs)
                if ((HideFlagsOf(d) & NotEditable) == 0) return d;

            return docs[0];
        }

        /// <summary>HideFlags.NotEditable: так Unity помечает служебные объекты вроде версии ассета URP.</summary>
        public const int NotEditable = 8;

        public static int HideFlagsOf(UnityDocument d)
        {
            int flags;
            var raw = d != null ? d.Get("m_ObjectHideFlags") : null;
            return raw != null && int.TryParse(raw, out flags) ? flags : 0;
        }

        /// <summary>Имя объекта без кавычек, которыми YAML обрамляет необычные строки.</summary>
        public static string NameOf(UnityDocument d)
        {
            var raw = d != null ? d.Get("m_Name") : null;
            if (raw == null) return null;

            raw = raw.Trim();
            if (raw.Length >= 2 && (raw[0] == '\'' || raw[0] == '"') && raw[raw.Length - 1] == raw[0])
                raw = raw.Substring(1, raw.Length - 2);
            return raw;
        }

        /// <summary>
        /// m_Colors.Array.data[0].second → m_Colors[0].second. Размер массива в
        /// файле отдельно не пишется, поэтому «x.Array.size» — это сам список x.
        /// </summary>
        public static string ToYamlPath(string serializedPath)
        {
            if (string.IsNullOrEmpty(serializedPath)) return serializedPath;

            const string size = ".Array.size";
            if (serializedPath.EndsWith(size, StringComparison.Ordinal))
                serializedPath = serializedPath.Substring(0, serializedPath.Length - size.Length);

            return ArrayElement.Replace(serializedPath, "[$1]");
        }
    }
}
