using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Метки git-статуса поверх ассетов в окне Project и фоновое обновление кэша.
    ///
    /// Обновление живёт здесь, а не в окне, специально: статус должен быть виден
    /// и когда окно плагина закрыто.
    /// </summary>
    [InitializeOnLoad]
    public static class ProjectStatusOverlay
    {
        private const double RefreshIntervalSec = 4.0;

        private static double _nextRefresh;
        private static bool _located;
        private static GUIStyle _badgeStyle;

        static ProjectStatusOverlay()
        {
            EditorApplication.projectWindowItemOnGUI += OnItemGUI;
            EditorApplication.update += OnUpdate;
            EditorApplication.projectChanged += () => _nextRefresh = 0;
        }

        private static void OnUpdate()
        {
            // Пока идёт импорт или компиляция, домен может перезагрузиться в любой
            // момент — не начинаем ничего асинхронного.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.timeSinceStartup < _nextRefresh) return;

            _nextRefresh = EditorApplication.timeSinceStartup + RefreshIntervalSec;

            if (!_located)
            {
                _located = true;
                GitRepository.Locate();
            }

            if (!GitRepository.IsRepo) return;

            _ = GitStatusCache.RefreshAsync();
        }

        private static void OnItemGUI(string guid, Rect rect)
        {
            if (!GitStatusCache.HasData || Event.current.type != EventType.Repaint) return;

            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return;

            var status = GitStatusCache.GetStatus(path);
            if (status == GitFileStatus.None) return;

            if (_badgeStyle == null)
            {
                _badgeStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = 9,
                    normal = { textColor = Color.black }
                };
            }

            // Сетка иконок — метка в левом верхнем углу; список — у правого края строки.
            bool grid = rect.height > 20f;
            Rect badge = grid
                ? new Rect(rect.x + 2f, rect.y + 2f, 13f, 13f)
                : new Rect(rect.xMax - 15f, rect.y + 1f, 13f, 13f);

            // Цвета и буквы — из общей палитры, чтобы метка здесь и строка
            // в окне плагина выглядели одинаково.
            EditorGUI.DrawRect(badge, GitPalette.Fill(status));
            GUI.Label(badge, GitPalette.Letter(status), _badgeStyle);
        }
    }
}
