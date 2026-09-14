using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git.UI
{
    /// <summary>
    /// То, что раздел просит у окна: запустить операцию, написать в строку
    /// состояния, узнать, занято ли окно. Разделы не знают друг о друге и
    /// не обращаются к EditorWindow напрямую.
    /// </summary>
    public interface IGitHost
    {
        bool Busy { get; }
        void Run(string title, Func<System.Threading.Tasks.Task> body);
        void SetStatus(string message, bool error);
    }

    /// <summary>
    /// Общие кирпичики интерфейса.
    ///
    /// Здесь нет ни одного цвета: всё оформление живёт в GitWindow.uss,
    /// а C# только расставляет классы. Иначе второй скин редактора чинится
    /// по одному месту за раз.
    /// </summary>
    public static class Ui
    {
        private const string StyleSheetPath =
            "Packages/ru.lev.unity-git/Editor/UI/GitWindow.uss";

        private static bool _warned;

        /// <summary>Подключает таблицу стилей и класс скина. Вызывается один раз на окно.</summary>
        public static void Attach(VisualElement root)
        {
            root.AddToClassList("root");
            ApplyTheme(root);

            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);

            if (sheet == null)
            {
                // Пакет мог быть переименован или лежать по другому пути —
                // ищем по имени, прежде чем сдаваться.
                foreach (var guid in AssetDatabase.FindAssets("GitWindow t:StyleSheet"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
                    if (sheet != null) break;
                }
            }

            if (sheet != null)
            {
                root.styleSheets.Add(sheet);
            }
            else if (!_warned)
            {
                _warned = true;
                Diagnostics.Journal.Warn(L.F(
                    "GitWindow.uss not found — the window will have no styling. " +
                    "Expected path: {0}", StyleSheetPath));
            }
        }

        /// <summary>Скин редактора переключается без перезагрузки домена, поэтому класс переставляем.</summary>
        public static void ApplyTheme(VisualElement root)
        {
            bool pro = EditorGUIUtility.isProSkin;
            root.EnableInClassList("theme-dark", pro);
            root.EnableInClassList("theme-light", !pro);
        }

        // ------------------------------------------------------ примитивы ---

        public static VisualElement Box(params string[] classes)
        {
            var e = new VisualElement();
            foreach (var c in classes) e.AddToClassList(c);
            return e;
        }

        /// <summary>
        /// Группа кнопок, слитых в одну полосу. Скругляются только крайние видимые
        /// кнопки: псевдоклассов :first-child и :last-child в USS нет, поэтому края
        /// отмечаются классами act--first и act--last — и заново при каждой смене
        /// раскладки, ведь кнопки в группе показываются и прячутся на ходу.
        /// </summary>
        public static VisualElement ButtonGroup(params string[] classes)
        {
            var group = Box(classes);
            group.RegisterCallback<GeometryChangedEvent>(_ => MarkEnds(group));
            return group;
        }

        private static void MarkEnds(VisualElement group)
        {
            VisualElement first = null, last = null;
            foreach (var child in group.Children())
            {
                if (child.resolvedStyle.display == DisplayStyle.None) continue;
                if (first == null) first = child;
                last = child;
            }

            foreach (var child in group.Children())
            {
                child.EnableInClassList("act--first", child == first);
                child.EnableInClassList("act--last", child == last);
            }
        }

        public static VisualElement RowBox(params string[] classes)
        {
            var e = Box(classes);
            e.style.flexDirection = FlexDirection.Row;
            return e;
        }

        public static Label Text(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        public static Button Action(string text, Action onClick, string tooltip = null, bool primary = false)
        {
            var b = new Button(onClick) { text = text };
            b.AddToClassList("act");
            if (primary) b.AddToClassList("act--primary");
            if (tooltip != null) b.tooltip = tooltip;
            return b;
        }

        public static VisualElement Spacer()
        {
            var e = new VisualElement();
            e.style.flexGrow = 1f;
            return e;
        }

        public static VisualElement KeyValue(string key, string value, string valueClass = null)
        {
            var row = Box("kv");
            row.Add(Text(key, "kv__k"));
            var v = Text(string.IsNullOrEmpty(value) ? "—" : value, "kv__v");
            if (valueClass != null) v.AddToClassList(valueClass);
            row.Add(v);
            return row;
        }

        /// <summary>
        /// Строка фактов «ключ: значение», переносящаяся по ширине.
        ///
        /// Столбцом эти же три-четыре факта съедали высоту у панели diff, ради
        /// которой человек сюда и смотрит. В строку они занимают одну-две.
        /// </summary>
        public static VisualElement Facts()
        {
            return Box("facts");
        }

        public static VisualElement Fact(string key, string value, string valueClass = null)
        {
            var f = Box("fact");
            f.Add(Text(key, "fact__k"));
            var v = Text(string.IsNullOrEmpty(value) ? "—" : value, "fact__v");
            if (valueClass != null) v.AddToClassList(valueClass);
            f.Add(v);
            return f;
        }

        public static VisualElement FactStatus(string key, GitFileStatus s)
        {
            var f = Box("fact");
            f.Add(Text(key, "fact__k"));
            var v = Text(GitPalette.Name(s), "fact__v");
            SetStatusText(v, s);
            f.Add(v);
            return f;
        }

        public static VisualElement Card(string head)
        {
            var c = Box("card");
            if (!string.IsNullOrEmpty(head)) c.Add(Text(head.ToUpperInvariant(), "card__head"));
            return c;
        }

        public static VisualElement Banner(string text, string kind = "info")
        {
            var b = Box("banner", "banner--" + kind);
            b.Add(Text(text, "banner__text"));
            return b;
        }

        /// <summary>Пустое состояние: всегда говорит, что сделать, а не «данных нет».</summary>
        public static VisualElement Empty(string title, string hint)
        {
            var e = Box("empty");
            e.Add(Text(title, "empty__title"));
            if (!string.IsNullOrEmpty(hint)) e.Add(Text(hint, "empty__hint"));
            return e;
        }

        /// <summary>Заглушка под будущую панель — честно называет, что тут появится.</summary>
        public static VisualElement Slot(string text)
        {
            var s = Box("slot");
            s.Add(Text(text, "slot__text"));
            return s;
        }

        // --------------------------------------------------- статусы git ---

        /// <summary>
        /// Цвета статуса ставятся инлайном, а не классом USS, — намеренно.
        /// Те же цвета нужны меткам в окне Project, а те рисуются на IMGUI и
        /// таблицу стилей прочитать не могут. Общий источник — <see cref="GitPalette"/>.
        /// </summary>
        public static void SetStatusFill(VisualElement e, GitFileStatus s)
        {
            e.style.backgroundColor = GitPalette.Fill(s);
        }

        public static void SetStatusText(VisualElement e, GitFileStatus s)
        {
            e.style.color = GitPalette.Text(s);
        }

        public static string StatusLetter(GitFileStatus s)
        {
            return GitPalette.Letter(s);
        }

        public static string StatusName(GitFileStatus s)
        {
            return GitPalette.Name(s);
        }

        /// <summary>Пара «ключ — значение», где значение окрашено по статусу git.</summary>
        public static VisualElement KeyValueStatus(string key, GitFileStatus s)
        {
            var row = Box("kv");
            row.Add(Text(key, "kv__k"));
            var v = Text(GitPalette.Name(s), "kv__v");
            SetStatusText(v, s);
            row.Add(v);
            return row;
        }

        private static Font _mono;

        /// <summary>
        /// Моноширинный шрифт для diff. В редакторе своего такого нет, поэтому
        /// берём системный: без него выравнивание отступов в коде разъезжается,
        /// а именно по отступам код и читают.
        /// </summary>
        public static Font Mono
        {
            get
            {
                if (_mono == null)
                    _mono = Font.CreateDynamicFontFromOSFont(
                        new[] { "Consolas", "Cascadia Mono", "Courier New", "Menlo", "monospace" }, 12);
                return _mono;
            }
        }

        public static Texture AssetIcon(string projectPath)
        {
            var icon = AssetDatabase.GetCachedIcon(projectPath);
            // Удалённого ассета в базе уже нет — иконку берём обобщённую.
            if (icon == null) icon = EditorGUIUtility.IconContent("DefaultAsset Icon").image;
            return icon;
        }

        public static string DirOf(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return string.Empty;
            int i = projectPath.LastIndexOf('/');
            return i <= 0 ? string.Empty : projectPath.Substring(0, i);
        }

        public static string NameOf(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath)) return string.Empty;
            int i = projectPath.LastIndexOf('/');
            return i < 0 ? projectPath : projectPath.Substring(i + 1);
        }
    }

    public enum CheckState
    {
        Off = 0,
        On,
        /// <summary>Часть содержимого отмечена — рисуется прочерком.</summary>
        Partial
    }

    /// <summary>
    /// Флажок с тремя состояниями.
    ///
    /// Не Toggle из редактора: у того нет неопределённого состояния, а
    /// подрисовать его через таблицу стилей нельзя — галочка там фоновая
    /// картинка. Своя реализация в тридцать строк даёт ровно тот вид, к
    /// которому люди привыкли по IDEA: галочка, прочерк, пусто.
    /// </summary>
    public sealed class TriCheck : VisualElement
    {
        private readonly Label _mark;

        /// <summary>Щёлкнули. true — пользователь хочет включить, false — выключить.</summary>
        public event Action<bool> Clicked;

        public CheckState State { get; private set; }

        public TriCheck()
        {
            AddToClassList("tri");
            _mark = Ui.Text(string.Empty, "tri__mark");
            Add(_mark);

            // AddManipulator — метод расширения, без this его не вызвать.
            this.AddManipulator(new Clickable(() =>
            {
                var h = Clicked;
                // Из «частично» щелчок включает всё — так же ведёт себя IDEA.
                if (h != null) h(State != CheckState.On);
            }));
        }

        public void Set(CheckState state)
        {
            State = state;
            _mark.text = state == CheckState.On ? "✓" : state == CheckState.Partial ? "–" : string.Empty;
            EnableInClassList("tri--on", state != CheckState.Off);
        }
    }

    /// <summary>Буквенный бейдж статуса: A / M / D / R / ? / !</summary>
    public sealed class StatusBadge : Label
    {
        public StatusBadge()
        {
            AddToClassList("badge");
        }

        public void Set(GitFileStatus s)
        {
            Ui.SetStatusFill(this, s);
            text = GitPalette.Letter(s);
        }
    }
}
