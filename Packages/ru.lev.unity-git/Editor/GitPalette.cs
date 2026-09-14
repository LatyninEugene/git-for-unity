using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Единственный источник цветов и букв git-статуса.
    ///
    /// Метки в окне Project рисуются на IMGUI, а окно плагина — на UI Toolkit.
    /// Держать палитру в таблице стилей нельзя: USS оттуда не прочитать. Поэтому
    /// цвета живут здесь, а UI Toolkit проставляет их инлайном. Иначе получаются
    /// две палитры, которые расходятся при первой же правке — ровно это и
    /// произошло: в окне Project «не отслеживается» был синим, а в списке серым.
    ///
    /// Тон подобран так, чтобы поверх читалась ЧЁРНАЯ буква — и в тёмном скине,
    /// и в светлом.
    /// </summary>
    public static class GitPalette
    {
        private static bool Pro => EditorGUIUtility.isProSkin;

        /// <summary>Заливка бейджа, точки группы и полосы стадирования.</summary>
        public static Color Fill(GitFileStatus s)
        {
            switch (s)
            {
                case GitFileStatus.Added:
                    return Pro ? new Color(0.42f, 0.80f, 0.50f) : new Color(0.30f, 0.70f, 0.40f);

                case GitFileStatus.Modified:
                    return Pro ? new Color(0.42f, 0.66f, 0.95f) : new Color(0.32f, 0.56f, 0.88f);

                case GitFileStatus.Deleted:
                    return Pro ? new Color(0.92f, 0.45f, 0.42f) : new Color(0.85f, 0.36f, 0.33f);

                case GitFileStatus.Renamed:
                    return Pro ? new Color(0.72f, 0.58f, 0.95f) : new Color(0.62f, 0.48f, 0.88f);

                case GitFileStatus.Untracked:
                    return Pro ? new Color(0.62f, 0.64f, 0.68f) : new Color(0.55f, 0.57f, 0.60f);

                case GitFileStatus.Conflicted:
                    return Pro ? new Color(1.00f, 0.45f, 0.28f) : new Color(0.90f, 0.36f, 0.20f);

                default:
                    return Color.clear;
            }
        }

        /// <summary>
        /// Цвет колонки в графе истории.
        ///
        /// Оттенки подобраны так, чтобы соседние колонки различались и в тёмном,
        /// и в светлом скине; после последнего круг замыкается. Смысла у цвета
        /// нет — он только помогает проследить линию взглядом, поэтому привязка
        /// идёт к номеру колонки, а не к ветке: имя ветки известно лишь у её
        /// вершины, а линия тянется на всю историю.
        /// </summary>
        public static Color Lane(int index)
        {
            if (index < 0) index = 0;
            switch (index % 7)
            {
                case 0: return Pro ? new Color(0.42f, 0.66f, 0.95f) : new Color(0.24f, 0.48f, 0.84f);
                case 1: return Pro ? new Color(0.52f, 0.82f, 0.48f) : new Color(0.28f, 0.62f, 0.32f);
                case 2: return Pro ? new Color(0.95f, 0.68f, 0.36f) : new Color(0.80f, 0.50f, 0.16f);
                case 3: return Pro ? new Color(0.78f, 0.58f, 0.96f) : new Color(0.55f, 0.36f, 0.82f);
                case 4: return Pro ? new Color(0.42f, 0.82f, 0.82f) : new Color(0.16f, 0.58f, 0.60f);
                case 5: return Pro ? new Color(0.94f, 0.52f, 0.60f) : new Color(0.80f, 0.28f, 0.40f);
                default: return Pro ? new Color(0.72f, 0.74f, 0.40f) : new Color(0.48f, 0.50f, 0.18f);
            }
        }

        /// <summary>
        /// Цвет для подписи. Отдельно от заливки: то, что читается чёрной буквой
        /// на плашке, как текст на панели теряется — особенно в светлом скине.
        /// </summary>
        public static Color Text(GitFileStatus s)
        {
            if (s == GitFileStatus.None) return Pro ? new Color(0.59f, 0.59f, 0.59f)
                                                    : new Color(0.35f, 0.35f, 0.35f);
            var c = Fill(s);
            // В светлом скине плашечный тон как текст слишком светлый — притемняем.
            return Pro ? c : c * 0.78f;
        }

        /// <summary>Буква на плашке. Одна и та же в окне Project и в списке изменений.</summary>
        public static string Letter(GitFileStatus s)
        {
            switch (s)
            {
                case GitFileStatus.Added: return "A";
                case GitFileStatus.Modified: return "M";
                case GitFileStatus.Deleted: return "D";
                case GitFileStatus.Renamed: return "R";
                case GitFileStatus.Untracked: return "?";
                case GitFileStatus.Conflicted: return "!";
                default: return "";
            }
        }

        public static string Name(GitFileStatus s)
        {
            switch (s)
            {
                case GitFileStatus.Added: return L.Tc("file status", "added");
                case GitFileStatus.Modified: return L.Tc("file status", "modified");
                case GitFileStatus.Deleted: return L.Tc("file status", "deleted");
                case GitFileStatus.Renamed: return L.Tc("file status", "renamed");
                case GitFileStatus.Untracked: return L.Tc("file status", "untracked");
                case GitFileStatus.Conflicted: return L.Tc("file status", "conflict");
                default: return L.Tc("file status", "unchanged");
            }
        }
    }
}
