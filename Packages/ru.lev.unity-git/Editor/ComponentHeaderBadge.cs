using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Метка git-состояния в строке заголовка компонента — там же, где «?»,
    /// пресеты и «⋮».
    ///
    /// Штатного способа туда попасть нет. Unity рисует эти значки через
    /// внутренний атрибут EditorHeaderItem, помеченный internal: применить его
    /// из чужой сборки невозможно. Публичный Editor.finishedDefaultHeaderGUI для
    /// компонентов не срабатывает вовсе — он вызывается только для шапки самого
    /// объекта, поэтому первая попытка ничего и не показала.
    ///
    /// Остаётся зарегистрировать себя в том же внутреннем списке через рефлексию.
    /// Поле ищется ПО ФОРМЕ, а не по имени: статический список делегатов с
    /// сигнатурой bool(Rect, Object[]). Переименование поля это переживёт,
    /// перенос в другой класс — нет.
    ///
    /// Всё обёрнуто так, чтобы неудача ничего не ломала: не нашли — значка
    /// просто не будет, а сводка изменений на самом объекте останется на месте.
    /// </summary>
    [InitializeOnLoad]
    public static class ComponentHeaderBadge
    {
        private static bool _installed;
        private static bool _gaveUp;
        private static GUIStyle _style;

        static ComponentHeaderBadge()
        {
            EditorApplication.update += TryInstall;
        }

        /// <summary>Удалось ли встроиться в строку заголовка.</summary>
        public static bool Installed => _installed;

        private static void TryInstall()
        {
            if (_installed || _gaveUp) return;

            try
            {
                var list = FindHeaderItemList();

                // Список создаётся лениво, при первой отрисовке инспектора.
                // Своим созданием мы бы отменили заполнение штатными значками,
                // поэтому просто ждём.
                if (list == null) return;

                var elementType = list.GetType().GetGenericArguments()[0];
                var method = typeof(ComponentHeaderBadge).GetMethod(
                    "Draw", BindingFlags.NonPublic | BindingFlags.Static);

                var handler = Delegate.CreateDelegate(elementType, method);

                // Вставляем первым: слева от штатных значков, ближе к имени.
                list.Insert(0, handler);

                _installed = true;
                EditorApplication.update -= TryInstall;
            }
            catch (Exception e)
            {
                _gaveUp = true;
                EditorApplication.update -= TryInstall;
                Diagnostics.Journal.Warn(
                    L.F("Couldn't add a badge to the component header: {0}. " +
                        "Component state is still visible in the summary on the object.", e.Message));
            }
        }

        /// <summary>
        /// Ищет статический список делегатов bool(Rect, Object[]) в EditorGUIUtility.
        /// Поиск по сигнатуре, а не по имени поля — так правка переживает
        /// переименование внутреннего поля.
        /// </summary>
        private static IList FindHeaderItemList()
        {
            var fields = typeof(EditorGUIUtility).GetFields(
                BindingFlags.NonPublic | BindingFlags.Static);

            foreach (var field in fields)
            {
                var type = field.FieldType;
                if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>)) continue;

                var element = type.GetGenericArguments()[0];
                if (!typeof(Delegate).IsAssignableFrom(element)) continue;

                var invoke = element.GetMethod("Invoke");
                if (invoke == null || invoke.ReturnType != typeof(bool)) continue;

                var args = invoke.GetParameters();
                if (args.Length != 2) continue;
                if (args[0].ParameterType != typeof(Rect)) continue;
                if (args[1].ParameterType != typeof(UnityEngine.Object[])) continue;

                return field.GetValue(null) as IList;
            }

            return null;
        }

        /// <summary>
        /// Рисует метку. Возврат false означает «места не занял» — тогда Unity
        /// сдвинет остальные значки и пустоты в заголовке не останется.
        /// </summary>
        private static bool Draw(Rect rect, UnityEngine.Object[] targets)
        {
            if (!SceneChangeIndex.HasData) return false;
            if (targets == null || targets.Length != 1) return false;

            var component = targets[0] as Component;
            if (component == null) return false;

            var node = SceneChangeIndex.FindComponentNode(component);
            if (node == null) return false;

            if (_style == null)
            {
                _style = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    fontSize = 9,
                    normal = { textColor = Color.black }
                };
            }

            var status = node.Kind == SceneChangeKind.Added ? GitFileStatus.Added
                       : node.Kind == SceneChangeKind.Removed ? GitFileStatus.Deleted
                       : GitFileStatus.Modified;

            var letter = node.Kind == SceneChangeKind.Added ? "+"
                       : node.Kind == SceneChangeKind.Removed ? "−" : "~";

            var box = new Rect(rect.x, rect.y + 2f, 14f, 14f);

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(box, GitPalette.Fill(status));
                GUI.Label(box, letter, _style);
            }

            // Щелчок по метке открывает состояние компонента до правки.
            if (Event.current.type == EventType.MouseDown && box.Contains(Event.current.mousePosition))
            {
                ComponentHistoryWindow.Show(
                    node, L.F("{0} on “{1}”", node.Title, component.gameObject.name), component);
                Event.current.Use();
            }

            var tooltip = node.Kind == SceneChangeKind.Added ? L.T("Component added since the last commit")
                        : node.Kind == SceneChangeKind.Removed ? L.T("Component removed")
                        : L.T("Component modified") + (node.Props.Count > 0 ? ": " + node.Props[0].Path : string.Empty);

            GUI.Label(box, new GUIContent(string.Empty, tooltip + "\n" + L.T("Click — before and after")));
            return true;
        }
    }
}
