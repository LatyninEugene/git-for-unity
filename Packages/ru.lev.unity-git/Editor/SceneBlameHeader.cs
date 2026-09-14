using System;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Lev.Git.UI;

namespace Lev.Git
{
    /// <summary>
    /// Строка «кто менял этот объект последним» в шапке инспектора.
    ///
    /// Ответ берётся из уже прочитанной истории сцены и никогда не заказывает
    /// её сам: разбор десятков версий сцены не должен начинаться от того, что
    /// человек кликнул по объекту. Пока история не прочитана, вместо ответа
    /// стоит кнопка — решение остаётся за человеком.
    ///
    /// Рисуется через публичный Editor.finishedDefaultHeaderGUI: он вызывается
    /// для шапки объекта, в отличие от заголовков компонентов, куда приходится
    /// встраиваться отражением (см. <see cref="ComponentHeaderBadge"/>).
    /// </summary>
    [InitializeOnLoad]
    internal static class SceneBlameHeader
    {
        /// <summary>Путь сцены, для которой сейчас читается история. Пусто — не читается.</summary>
        private static string _loading;

        static SceneBlameHeader()
        {
            Editor.finishedDefaultHeaderGUI += OnHeader;
        }

        private static void OnHeader(Editor editor)
        {
            if (!GitSettings.instance.objectBlame) return;
            if (!GitRepository.IsRepo) return;
            if (editor == null || editor.targets == null || editor.targets.Length != 1) return;

            var go = editor.target as GameObject;
            if (go == null) return;

            var address = SceneObjectRef.Of(go);
            if (!address.IsValid) return;

            // Объект из экземпляра префаба в файле сцены не лежит — там только
            // переопределения. Исключение — корень экземпляра: его запись в сцене
            // и есть документ PrefabInstance, и «последнее изменение» у него честное.
            if (address.InsidePrefab)
            {
                if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go)) return;
                address.FileId = unchecked((long)address.PrefabId);
                address.PrefabId = 0;
            }

            SceneHistoryResult history;
            if (!SceneHistory.TryGetCached(address.ScenePath, out history))
            {
                DrawOffer(address);
                return;
            }

            DrawAnswer(address, history);
        }

        private static void DrawOffer(SceneObjectAddress address)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool busy = _loading == address.ScenePath;

                using (new EditorGUI.DisabledScope(busy))
                {
                    if (GUILayout.Button(busy ? L.T("Reading Scene History…") : L.T("Who Changed This Object"),
                                         EditorStyles.miniButton, GUILayout.Width(190f)))
                        Load(address.ScenePath);
                }

                GUILayout.FlexibleSpace();
            }
        }

        private static void DrawAnswer(SceneObjectAddress address, SceneHistoryResult history)
        {
            SceneEvent last = null;

            // Лента идёт от новых коммитов к старым: первое совпадение и есть
            // последнее изменение.
            foreach (var e in history.Events)
            {
                if (e.ObjectId != address.FileId) continue;
                last = e;
                break;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (last == null)
                {
                    EditorGUILayout.LabelField(L.T("Not changed in the loaded history"), EditorStyles.miniLabel);
                }
                else
                {
                    var text = string.Format("{0} · {1} · {2}",
                        last.Revision.Author, Ago(last.Revision.Date), last.Revision.ShortSha);

                    EditorGUILayout.LabelField(
                        new GUIContent(text, last.Revision.Subject + "\n\n" + Detail(last)),
                        EditorStyles.miniLabel);
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button(L.T("History"), EditorStyles.miniButton, GUILayout.Width(70f)))
                    SceneHistoryWindow.ShowObject(address);
            }
        }

        private static string Detail(SceneEvent e)
        {
            if (e.Kind == SceneEventKind.Added) return L.T("Object created in this commit");
            if (e.Kind == SceneEventKind.Renamed) return L.F("Renamed: {0} → {1}", e.OldName, e.NewName);
            if (e.Kind == SceneEventKind.Reparented) return L.T("Moved in the hierarchy");
            if (e.Props.Count == 0) return L.Tc("scene object", "Modified");

            var first = e.Props[0];
            return first.Path + ": " + first.Old + " → " + first.New;
        }

        private static async void Load(string scenePath)
        {
            if (_loading != null) return;
            _loading = scenePath;

            try
            {
                await SceneHistory.BuildAsync(scenePath, 100);
            }
            finally
            {
                _loading = null;
                // Перерисовать нужно и инспектор, и иерархию: ответ появился
                // не в ответ на событие ввода, а сам по себе.
                InternalEditorUtility.RepaintAllViews();
            }
        }

        private static string Ago(DateTime when)
        {
            if (when == DateTime.MinValue) return string.Empty;

            var span = DateTime.Now - when;
            if (span.TotalHours < 1) return L.F("{0} min ago", (int)span.TotalMinutes);
            if (span.TotalDays < 1) return L.F("{0} h ago", (int)span.TotalHours);
            if (span.TotalDays < 7) return L.Fc("scene history", "{0} d ago", (int)span.TotalDays);
            if (span.TotalDays < 365) return when.ToString("d MMM");
            return when.ToString("MM.yyyy");
        }
    }
}
