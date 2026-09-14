using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lev.Git
{
    /// <summary>
    /// Компонент до и после правки.
    ///
    /// Два режима. «Инспектор» показывает слева НАСТОЯЩИЙ инспектор компонента
    /// со значениями из последнего коммита, справа — текущий: сравнивать поля
    /// глазами удобнее там, где они выглядят как обычно, а не в виде путей
    /// сериализации. «Таблица» отвечает на другой вопрос — что именно
    /// изменилось, включая поля, которых в инспекторе не видно.
    ///
    /// Старое состояние живёт на скрытом временном объекте: другого способа
    /// нарисовать инспектор несуществующего состояния нет.
    /// </summary>
    public sealed class ComponentHistoryWindow : EditorWindow, ILocalizedWindow
    {
        private struct Row
        {
            public string Key;
            public string Old;
            public string New;
            public bool Changed;
        }

        private SceneNode _node;
        private string _subtitle;
        private Component _live;

        private GameObject _previewObject;
        private Component _previewComponent;
        private SerializedObject _oldSo;
        private SerializedObject _liveSo;
        private UnityPropertyApplier.Result _applyResult;

        private readonly List<Row> _rows = new List<Row>();
        // Прокрутка одна на обе стороны: строки выровнены, и разъезжаться им нельзя.
        private Vector2 _scrollCompare, _scrollTable;
        private bool _table;
        private bool _onlyChanged = true;

        public static void Show(SceneNode node, string subtitle, Component live = null)
        {
            if (node == null) return;

            var w = GetWindow<ComponentHistoryWindow>(true, L.T("Before and After"), true);
            w.minSize = new Vector2(680f, 420f);
            w.Fill(node, subtitle, live);
            w.Show();
        }

        /// <summary>Заголовок задаётся при открытии — при смене языка его надо переставить.</summary>
        void ILocalizedWindow.OnLanguageChanged()
        {
            titleContent = new GUIContent(L.T("Before and After"));
            Repaint();
        }

        private const string PreviewName = "Git Preview";

        /// <summary>
        /// Подбирает временные объекты, оставшиеся от прошлых сессий.
        ///
        /// HideAndDontSave переживает перезагрузку домена, а ссылка на объект в
        /// окне — нет. Без уборки такие объекты копились бы в памяти до
        /// перезапуска редактора, невидимые в иерархии.
        /// </summary>
        private static void SweepStrays()
        {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.name != PreviewName) continue;
                if ((go.hideFlags & HideFlags.HideAndDontSave) == 0) continue;
                DestroyImmediate(go);
            }
        }

        private void Fill(SceneNode node, string subtitle, Component live)
        {
            Cleanup();
            SweepStrays();

            _node = node;
            _subtitle = subtitle;
            _live = live;
            _table = false;

            BuildTable();
            BuildPreview();
        }

        private void OnDisable() { Cleanup(); }

        private void Cleanup()
        {
            if (_previewObject != null) { DestroyImmediate(_previewObject); _previewObject = null; }
            _previewComponent = null;
            _oldSo = null;
            _liveSo = null;
        }

        // ------------------------------------------------------- построение ---

        private void BuildTable()
        {
            _rows.Clear();

            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (_node.NewDoc != null)
                foreach (var k in _node.NewDoc.Props.Keys) if (seen.Add(k)) keys.Add(k);
            if (_node.OldDoc != null)
                foreach (var k in _node.OldDoc.Props.Keys) if (seen.Add(k)) keys.Add(k);

            keys.Sort(StringComparer.Ordinal);

            foreach (var key in keys)
            {
                var oldValue = _node.OldDoc != null ? _node.OldDoc.Get(key) : null;
                var newValue = _node.NewDoc != null ? _node.NewDoc.Get(key) : null;
                _rows.Add(new Row { Key = key, Old = oldValue, New = newValue, Changed = oldValue != newValue });
            }
        }

        /// <summary>
        /// Собирает скрытый объект со старым состоянием компонента.
        ///
        /// Тип берётся у живого компонента: восстановить его из числового кода
        /// класса Unity не даёт, а для удалённого компонента живого экземпляра
        /// нет вовсе — там остаётся только таблица.
        /// </summary>
        private void BuildPreview()
        {
            if (_live == null || _node.OldDoc == null) return;

            try
            {
                _previewObject = EditorUtility.CreateGameObjectWithHideFlags(
                    PreviewName, HideFlags.HideAndDontSave);

                var type = _live.GetType();

                // Transform есть у каждого объекта с рождения — добавить второй нельзя.
                _previewComponent = type == typeof(Transform)
                    ? _previewObject.transform
                    : _previewObject.AddComponent(type);

                if (_previewComponent == null) { Cleanup(); return; }

                _oldSo = new SerializedObject(_previewComponent);
                _applyResult = UnityPropertyApplier.Apply(_oldSo, _node.OldDoc);
                _oldSo.ApplyModifiedPropertiesWithoutUndo();

                _liveSo = new SerializedObject(_live);
            }
            catch (Exception e)
            {
                Diagnostics.Journal.Warn(L.F("Couldn't build the component preview: {0}", e.Message));
                Cleanup();
            }
        }

        // -------------------------------------------------------------- UI ---

        private void OnGUI()
        {
            if (_node == null) { EditorGUILayout.LabelField(L.T("No data.")); return; }

            DrawHeader();

            if (_table || _oldSo == null) DrawTable();
            else DrawInspectors();

            DrawFooter();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(6f);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(_node.Title, EditorStyles.boldLabel, GUILayout.Width(220f));
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(_oldSo == null))
                {
                    _table = GUILayout.Toolbar(_table ? 1 : 0,
                        L.Ts("Inspector", "Table"), GUILayout.Width(180f)) == 1;
                }
            }

            if (!string.IsNullOrEmpty(_subtitle))
                EditorGUILayout.LabelField(_subtitle, EditorStyles.miniLabel);

            if (_node.Kind == SceneChangeKind.Added)
                EditorGUILayout.HelpBox(L.T("The component wasn't in the last commit — the left side is empty."),
                    MessageType.None);
            else if (_node.Kind == SceneChangeKind.Removed)
                EditorGUILayout.HelpBox(L.T("The component was removed. There's no live instance, so only the table is available."),
                    MessageType.None);
            else if (_oldSo == null && _live != null)
                EditorGUILayout.HelpBox(L.T("Couldn't build the preview — the value table is available."),
                    MessageType.None);

            EditorGUILayout.Space(2f);
        }

        private const float MarkerWidth = 18f;

        /// <summary>
        /// Обе стороны рисуются ОДНИМ обходом свойств, а не двумя вызовами
        /// OnInspectorGUI.
        ///
        /// Причина простая: два независимых инспектора съезжают друг относительно
        /// друга. У старого состояния нет кнопки «Edit Collider», и всё ниже
        /// смещается на её высоту — сравнивать строки становится нечем. Общий
        /// обход гарантирует, что n-я строка слева и n-я справа — это одно и то
        /// же свойство, и только тогда метка изменения имеет смысл.
        ///
        /// Цена — собственный редактор компонента не используется: кнопки вроде
        /// «Edit Collider» здесь не будет. Отрисовщики отдельных полей при этом
        /// работают, их вызывает PropertyField.
        /// </summary>
        private void DrawInspectors()
        {
            if (_oldSo == null || _liveSo == null) { DrawTable(); return; }

            _oldSo.Update();
            _liveSo.Update();

            float column = Mathf.Max(120f, (position.width - MarkerWidth - 26f) * 0.5f);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(L.T("Before — Last Commit"),
                    EditorStyles.miniBoldLabel, GUILayout.Width(column));
                GUILayout.Space(MarkerWidth);
                EditorGUILayout.LabelField(L.T("After — Now (Editable)"),
                    EditorStyles.miniBoldLabel, GUILayout.Width(column));
            }

            _scrollCompare = EditorGUILayout.BeginScrollView(_scrollCompare);

            var tint = GitPalette.Fill(GitFileStatus.Modified);
            tint.a = 0.12f;
            var dotColor = GitPalette.Fill(GitFileStatus.Modified);

            var it = _oldSo.GetIterator();
            bool enterChildren = true;

            while (it.NextVisible(enterChildren))
            {
                enterChildren = false;

                var live = _liveSo.FindProperty(it.propertyPath);
                if (live == null) continue;

                // Раскрытие синхронизируется по живой стороне: иначе одна
                // сторона развернётся, другая нет, и высоты строк разойдутся.
                it.isExpanded = live.isExpanded;

                bool same = SerializedProperty.DataEquals(it, live);
                bool isScript = it.propertyPath == "m_Script";

                var row = EditorGUILayout.BeginHorizontal();

                // Подложка рисуется до полей, поэтому оказывается под ними.
                if (!same && Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(row, tint);

                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.PropertyField(it, true, GUILayout.Width(column));

                float height = EditorGUI.GetPropertyHeight(live, true);
                var markerRect = GUILayoutUtility.GetRect(
                    MarkerWidth, height, GUILayout.Width(MarkerWidth));

                if (!same && Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(new Rect(markerRect.x + 5f, markerRect.y + 6f, 7f, 7f), dotColor);

                using (new EditorGUI.DisabledScope(isScript))
                    EditorGUILayout.PropertyField(live, true, GUILayout.Width(column));

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();

            // Правка живого компонента — обычная правка сцены: SerializedObject
            // сам заводит запись в истории отмены.
            if (_liveSo.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(_live);
                SceneChangeIndex.MarkDirty();
            }
        }

        private void DrawTable()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _onlyChanged = EditorGUILayout.ToggleLeft(L.Tc("properties", "Only Changed"), _onlyChanged, GUILayout.Width(170f));
                GUILayout.FlexibleSpace();

                int changed = 0;
                foreach (var r in _rows) if (r.Changed) changed++;
                EditorGUILayout.LabelField(L.F("{0} of {1} changed", changed, _rows.Count),
                    EditorStyles.miniLabel, GUILayout.Width(170f));
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField(L.T("Property"), EditorStyles.miniBoldLabel, GUILayout.Width(220f));
                EditorGUILayout.LabelField(L.T("Before"), EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField(L.T("After"), EditorStyles.miniBoldLabel);
            }

            _scrollTable = EditorGUILayout.BeginScrollView(_scrollTable);

            bool any = false;
            var prev = GUI.color;

            foreach (var row in _rows)
            {
                if (_onlyChanged && !row.Changed) continue;
                any = true;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(row.Key, EditorStyles.miniLabel, GUILayout.Width(220f));

                    // Цвет только у изменившихся: подкрашивать всё — значит
                    // не подкрашивать ничего.
                    GUI.color = row.Changed ? new Color(1f, 0.6f, 0.55f) : prev;
                    EditorGUILayout.SelectableLabel(row.Old ?? "—", EditorStyles.miniLabel, GUILayout.Height(15f));

                    GUI.color = row.Changed ? new Color(0.55f, 0.9f, 0.65f) : prev;
                    EditorGUILayout.SelectableLabel(row.New ?? "—", EditorStyles.miniLabel, GUILayout.Height(15f));

                    GUI.color = prev;
                }
            }

            if (!any)
                EditorGUILayout.LabelField(_onlyChanged ? L.T("No changed properties.") : L.T("No properties."),
                    EditorStyles.miniLabel);

            EditorGUILayout.EndScrollView();
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(4f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (_applyResult != null)
                    EditorGUILayout.LabelField(L.F("Preview: {0}", _applyResult), EditorStyles.miniLabel);

                GUILayout.FlexibleSpace();

                bool canRestore = _live != null && _node.OldDoc != null &&
                                  _node.Kind == SceneChangeKind.Modified;

                using (new EditorGUI.DisabledScope(!canRestore))
                {
                    if (GUILayout.Button(L.T("Restore “Before” Values"), GUILayout.Width(200f)))
                        Restore();
                }
            }

            EditorGUILayout.Space(4f);
        }

        private void Restore()
        {
            if (_live == null || _node.OldDoc == null) return;

            if (!EditorUtility.DisplayDialog(
                    L.T("Restore Values"),
                    L.F("The component values will be replaced with those from the last commit.\n\n" +
                        "{0}\n\nYou can undo with Ctrl+Z.", _node.Title),
                    L.T("Restore"), L.T("Cancel")))
                return;

            // Через Undo: возврат значений — обычная правка сцены, и отменяться
            // она должна так же, как любая другая.
            var result = UnityPropertyApplier.RestoreLive(_live, _node.OldDoc, L.T("Restore Values from Commit"));
            SceneChangeIndex.MarkDirty();

            Diagnostics.Journal.Notice(_node.Title + ": " + result);

            // Сериализованный слепок надо пересоздать: значения под ним сменились.
            _liveSo = new SerializedObject(_live);
            BuildTable();
        }
    }
}
