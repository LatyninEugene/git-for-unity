using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditorInternal;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Окно на UI Toolkit, которое при смене языка пересобирает интерфейс.
    /// Окнам на IMGUI это не нужно — им хватает перерисовки; реализовать стоит,
    /// только если у окна переводится заголовок.
    /// </summary>
    internal interface ILocalizedWindow
    {
        void OnLanguageChanged();
    }

    /// <summary>
    /// Редакторская половина перевода: какой язык выбран, где лежат .po и что
    /// перерисовать, когда язык сменили.
    ///
    /// Язык — личная настройка человека, а не проекта: он в EditorPrefs.
    /// По умолчанию — язык системы; если перевода на него нет, английский.
    /// </summary>
    [InitializeOnLoad]
    internal static partial class L
    {
        public const string Auto = "auto";
        private const string PrefKey = "LevGit.Language";

        /// <summary>Самоназвания языков — список выбора показывает их без перевода.</summary>
        private static readonly Dictionary<string, string> NativeNames = new Dictionary<string, string>
        {
            { "en", "English" }, { "ru", "Русский" }, { "uk", "Українська" }, { "de", "Deutsch" }, // loc-ignore
            { "fr", "Français" }, { "es", "Español" }, { "pl", "Polski" }, { "ja", "日本語" },
            { "zh", "中文" }, { "ko", "한국어" }, { "pt", "Português" }, { "it", "Italiano" }, { "tr", "Türkçe" }
        };

        private static readonly Dictionary<string, GUIContent> _contents = new Dictionary<string, GUIContent>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GUIContent[]> _contentLists = new Dictionary<string, GUIContent[]>(StringComparer.Ordinal);
        private static string _directory;

        static L()
        {
            Reload(false);
        }

        /// <summary>Выбор человека: <see cref="Auto"/> или код языка.</summary>
        public static string Preference
        {
            get { return EditorPrefs.GetString(PrefKey, Auto); }
            set
            {
                if (string.IsNullOrEmpty(value)) value = Auto;
                if (value == Preference) return;
                EditorPrefs.SetString(PrefKey, value);
                Reload(true);
            }
        }

        /// <summary>Языки, для которых есть перевод, английский — первым.</summary>
        public static List<string> Available
        {
            get
            {
                var list = new List<string> { English };
                var dir = Directory;
                if (dir == null || !System.IO.Directory.Exists(dir)) return list;

                var found = new List<string>();
                foreach (var file in System.IO.Directory.GetFiles(dir, "*.po"))
                {
                    var code = Path.GetFileNameWithoutExtension(file);
                    if (code != English) found.Add(code);
                }
                found.Sort(StringComparer.Ordinal);
                list.AddRange(found);
                return list;
            }
        }

        public static string NativeName(string code)
        {
            string name;
            return code != null && NativeNames.TryGetValue(code, out name) ? name : code;
        }

        /// <summary>Язык системы, двухбуквенный код.</summary>
        public static string SystemLanguage
        {
            get
            {
                var fromUnity = FromUnity(Application.systemLanguage);
                if (fromUnity != null) return fromUnity;

                try
                {
                    if (Application.platform == RuntimePlatform.WindowsEditor)
                        return new CultureInfo(GetUserDefaultUILanguage()).TwoLetterISOLanguageName;
                }
                catch { /* ниже — запасные способы */ }

                foreach (var name in new[] { "LC_ALL", "LC_MESSAGES", "LANGUAGE", "LANG" })
                {
                    var v = Environment.GetEnvironmentVariable(name);
                    if (string.IsNullOrEmpty(v) || v == "C" || v == "POSIX" || v.StartsWith("C.", StringComparison.Ordinal)) continue;
                    return v.Split(':')[0].Split('.')[0].Split('_', '-')[0].ToLowerInvariant();
                }

                try { return CultureInfo.InstalledUICulture.TwoLetterISOLanguageName; }
                catch { return English; }
            }
        }

        /// <summary>Перечитать выбор и каталог. Нужен и после правки .po — чтобы увидеть перевод без перезагрузки.</summary>
        public static void Reload(bool notify)
        {
            string language = Resolve(Preference);
            Catalog catalog = null;

            if (language != English)
            {
                try
                {
                    var path = Path.Combine(Directory ?? string.Empty, language + ".po");
                    if (File.Exists(path)) catalog = Catalog.Parse(File.ReadAllText(path), language);
                    else language = English;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Git] Translation " + language + ".po could not be read: " + e.Message);
                    language = English;
                }
            }

            Apply(language, catalog);
            _contents.Clear();
            _contentLists.Clear();

            if (!notify) return;
            RaiseChanged();
            RefreshWindows();
        }

        private static string Resolve(string preference)
        {
            var available = Available;
            var wanted = preference == Auto || string.IsNullOrEmpty(preference) ? SystemLanguage : preference;
            return available.Contains(wanted) ? wanted : English;
        }

        // ------------------------------------------------------- для IMGUI ---

        /// <summary>
        /// GUIContent с переведённым текстом и подсказкой. Кэшируется до смены
        /// языка, поэтому возвращённый объект менять нельзя.
        /// </summary>
        public static GUIContent C(string text, string tooltip = null)
        {
            var key = text + "\u0001" + tooltip;
            GUIContent c;
            if (!_contents.TryGetValue(key, out c))
            {
                c = new GUIContent(T(text), tooltip == null ? null : T(tooltip));
                _contents[key] = c;
            }
            return c;
        }

        /// <summary>Переведённый список для Popup и Toolbar. Кэшируется до смены языка.</summary>
        public static GUIContent[] Cs(params string[] texts)
        {
            var key = string.Join("\u0001", texts);
            GUIContent[] list;
            if (!_contentLists.TryGetValue(key, out list))
            {
                list = new GUIContent[texts.Length];
                for (int i = 0; i < texts.Length; i++) list[i] = new GUIContent(T(texts[i]));
                _contentLists[key] = list;
            }
            return list;
        }

        /// <summary>Переведённые строки массивом — для Popup, принимающих string[].</summary>
        public static string[] Ts(params string[] texts)
        {
            var list = new string[texts.Length];
            for (int i = 0; i < texts.Length; i++) list[i] = T(texts[i]);
            return list;
        }

        /// <summary>Пункты «Language» для контекстного меню окна.</summary>
        public static void AddLanguageMenu(GenericMenu menu, string prefix)
        {
            var pref = Preference;
            var system = SystemLanguage;
            var systemName = Available.Contains(system) ? NativeName(system) : NativeName(English);

            menu.AddItem(new GUIContent(prefix + L.F("Automatic ({0})", systemName)), pref == Auto, () => Preference = Auto);
            foreach (var code in Available)
            {
                var c = code;
                menu.AddItem(new GUIContent(prefix + NativeName(c)), pref == c, () => Preference = c);
            }
        }

        /// <summary>Выбор языка строкой настроек — для Preferences и Project Settings.</summary>
        public static void LanguageField()
        {
            var codes = new List<string> { Auto };
            codes.AddRange(Available);

            var names = new GUIContent[codes.Count];
            var system = SystemLanguage;
            names[0] = new GUIContent(L.F("Automatic ({0})", NativeName(Available.Contains(system) ? system : English)));
            for (int i = 1; i < codes.Count; i++) names[i] = new GUIContent(NativeName(codes[i]));

            int index = Mathf.Max(0, codes.IndexOf(Preference));
            int chosen = EditorGUILayout.Popup(L.C("Language", "Interface language of Git for Unity. Personal setting: it is not stored in the project."), index, names);
            if (chosen != index) Preference = codes[chosen];
        }

        // --------------------------------------------------------- служебное ---

        private static string Directory
        {
            get
            {
                if (_directory != null) return _directory;

                try
                {
                    var asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("Lev.Git.Editor");
                    if (string.IsNullOrEmpty(asmdef)) return null;

                    string full = null;
                    if (asmdef.StartsWith("Packages/", StringComparison.Ordinal))
                    {
                        // Пакет из реестра или git лежит в Library/PackageCache,
                        // и путь Packages/… на диске не существует.
                        var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(asmdef);
                        if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                            full = Path.Combine(info.resolvedPath, asmdef.Substring(info.assetPath.Length).TrimStart('/'));
                    }
                    if (full == null) full = Path.GetFullPath(asmdef);

                    _directory = Path.Combine(Path.GetDirectoryName(full), "Localization");
                }
                catch
                {
                    return null;
                }
                return _directory;
            }
        }

        private static void RefreshWindows()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                try
                {
                    var localized = w as ILocalizedWindow;
                    if (localized != null) localized.OnLanguageChanged();
                    else if ((w.GetType().Namespace ?? string.Empty).StartsWith("Lev.Git", StringComparison.Ordinal)) w.Repaint();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            SettingsService.RepaintAllSettingsWindow();
            InternalEditorUtility.RepaintAllViews();
        }

        private static string FromUnity(UnityEngine.SystemLanguage language)
        {
            switch (language)
            {
                case UnityEngine.SystemLanguage.English: return "en";
                case UnityEngine.SystemLanguage.Russian: return "ru";
                case UnityEngine.SystemLanguage.Ukrainian: return "uk";
                case UnityEngine.SystemLanguage.Belarusian: return "be";
                case UnityEngine.SystemLanguage.German: return "de";
                case UnityEngine.SystemLanguage.French: return "fr";
                case UnityEngine.SystemLanguage.Spanish: return "es";
                case UnityEngine.SystemLanguage.Polish: return "pl";
                case UnityEngine.SystemLanguage.Japanese: return "ja";
                case UnityEngine.SystemLanguage.Korean: return "ko";
                case UnityEngine.SystemLanguage.Chinese:
                case UnityEngine.SystemLanguage.ChineseSimplified:
                case UnityEngine.SystemLanguage.ChineseTraditional: return "zh";
                case UnityEngine.SystemLanguage.Portuguese: return "pt";
                case UnityEngine.SystemLanguage.Italian: return "it";
                case UnityEngine.SystemLanguage.Turkish: return "tr";
                default: return null;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern ushort GetUserDefaultUILanguage();
    }
}
