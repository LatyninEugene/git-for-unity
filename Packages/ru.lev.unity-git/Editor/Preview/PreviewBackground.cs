using System;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.Preview
{
    /// <summary>
    /// Фон превью из настроек проекта (Project Settings → Git → Превью ассетов).
    /// Один на все панели и показы: ячейки вида, камера материала, подложка звука.
    /// </summary>
    internal static class PreviewBackground
    {
        public const int Dark = 0, Gray = 1, Light = 2, Checker = 3, Custom = 4;

        public static GUIContent[] Names
        {
            get { return L.Cs("Dark", "Gray", "Light", "Checkerboard", "Custom Color"); }
        }

        /// <summary>Настройку поменяли — открытым превью пора перерисоваться.</summary>
        public static event Action Changed;

        public static void NotifyChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        private static int Mode
        {
            get { return Mathf.Clamp(GitSettings.instance.previewBackground, Dark, Custom); }
        }

        public static bool IsChecker
        {
            get { return Mode == Checker; }
        }

        /// <summary>Шахматка под текстурой с прозрачностью. При фоне-шахматке не нужна: она уже везде.</summary>
        public static bool CheckerUnderImages
        {
            get { return GitSettings.instance.previewCheckerUnderImages && !IsChecker; }
        }

        /// <summary>
        /// Сплошной цвет фона. Для шахматки — средний серый: камера материала
        /// рисует непрозрачную картинку, и под шар шахматку не подложить.
        /// </summary>
        public static Color SolidColor
        {
            get
            {
                switch (Mode)
                {
                    case Gray: return new Color(0.36f, 0.36f, 0.36f);
                    case Light: return new Color(0.86f, 0.86f, 0.86f);
                    case Checker: return new Color(0.3f, 0.3f, 0.3f);
                    case Custom:
                    {
                        var c = GitSettings.instance.previewBackgroundColor;
                        return new Color(c.r, c.g, c.b, 1f);
                    }
                    default: return new Color(0.13f, 0.13f, 0.13f);
                }
            }
        }

        public static void Fill(Rect rect)
        {
            if (IsChecker) PreviewCanvas.Checker(rect);
            else EditorGUI.DrawRect(rect, SolidColor);
        }
    }
}
