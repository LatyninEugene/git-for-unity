using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>
    /// Разница компонента между двумя версиями, нарисованная настоящим
    /// инспектором: слева состояние до коммита, справа после.
    ///
    /// Обе стороны собираются на скрытых временных объектах — другого способа
    /// показать инспектор несуществующего состояния нет. Живой компонент здесь
    /// не участвует: это история, и править в ней нечего.
    ///
    /// Обе колонки рисуются ОДНИМ обходом свойств, а не двумя вызовами
    /// OnInspectorGUI: независимые инспекторы съезжают друг относительно друга
    /// на любом поле разной высоты, и сравнивать строки становится нечем.
    /// </summary>
    internal sealed class ComponentDiffView : IDisposable
    {
        public const string PreviewName = "Git History Preview";

        private const float MarkerWidth = 16f;

        private GameObject _oldObject, _newObject;
        private SerializedObject _oldSo, _newSo;

        /// <summary>Живой компонент на открытой сцене — для сравнения с текущим состоянием.</summary>
        private Component _live;
        private SerializedObject _liveSo;

        /// <summary>Почему текущее состояние сейчас не править; null — править можно.</summary>
        private string _liveReadOnly;

        /// <summary>Инспектор собрать не вышло — вызывающая сторона покажет таблицу.</summary>
        public bool Ready { get { return _oldSo != null || _newSo != null; } }

        public string Problem { get; private set; }

        /// <summary>
        /// Готовит стороны. <paramref name="type"/> — настоящий тип компонента:
        /// из числового кода класса Unity его не восстановить, поэтому тип
        /// берётся у живого экземпляра на сцене.
        /// </summary>
        public ComponentDiffView(Type type, UnityDocument oldDoc, UnityDocument newDoc)
        {
            if (type == null)
            {
                Problem = L.T("Component type can't be determined — showing property values.");
                return;
            }

            // Проверка обязательна ДО AddComponent: на недопустимом типе Unity
            // не бросает исключение, а сама пишет ошибку в консоль, и перехватить
            // её нечем.
            if (!SceneHistoryIcons.CanHost(type))
            {
                Problem = L.F("Can't build the {0} inspector — showing property values.", type.Name);
                return;
            }

            try
            {
                if (oldDoc != null) _oldSo = Build(type, oldDoc, ref _oldObject);
                if (newDoc != null) _newSo = Build(type, newDoc, ref _newObject);
            }
            catch (Exception e)
            {
                Problem = L.F("Couldn't build the inspector: {0}", e.Message);
                Dispose();
            }
        }

        /// <summary>
        /// Компонент внутри экземпляра префаба. В сцене его документа нет — есть
        /// компонент в файле префаба и переопределения поверх него. Каждая сторона
        /// собирается так же, как Unity собирает экземпляр: значения префаба, затем
        /// переопределения этой стороны. null у стороны — экземпляра тогда не было.
        /// </summary>
        public ComponentDiffView(Component prefabComponent, List<PrefabModification> before, List<PrefabModification> after)
        {
            if (prefabComponent == null)
            {
                Problem = L.T("Component not found in the prefab file — showing override values.");
                return;
            }

            var type = prefabComponent.GetType();
            if (!SceneHistoryIcons.CanHost(type))
            {
                Problem = L.F("Can't build the {0} inspector — showing override values.", type.Name);
                return;
            }

            try
            {
                if (before != null) _oldSo = BuildFromPrefab(prefabComponent, before, ref _oldObject);
                if (after != null) _newSo = BuildFromPrefab(prefabComponent, after, ref _newObject);
            }
            catch (Exception e)
            {
                Problem = L.F("Couldn't build the inspector: {0}", e.Message);
                Dispose();
            }
        }

        private static SerializedObject BuildFromPrefab(Component source, List<PrefabModification> mods, ref GameObject host)
        {
            host = EditorUtility.CreateGameObjectWithHideFlags(PreviewName, HideFlags.HideAndDontSave);
            var type = source.GetType();

            Component component;
            if (source is Transform sourceTransform)
            {
                // Transform не копируется целиком: вместе с ним скопировался бы родитель
                // из файла префаба, и временный объект повис бы внутри ассета.
                component = host.transform;
                host.transform.localPosition = sourceTransform.localPosition;
                host.transform.localRotation = sourceTransform.localRotation;
                host.transform.localScale = sourceTransform.localScale;
            }
            else
            {
                component = host.AddComponent(type);
                if (component == null) return null;
                EditorUtility.CopySerialized(source, component);
            }

            var so = new SerializedObject(component);
            foreach (var m in mods)
            {
                var property = so.FindProperty(m.PropertyPath);
                if (property == null) continue;
                UnityPropertyApplier.ApplyValue(property,
                    property.propertyType == SerializedPropertyType.ObjectReference ? m.Reference : m.Value);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            return so;
        }

        private static SerializedObject Build(Type type, UnityDocument doc, ref GameObject host)
        {
            host = EditorUtility.CreateGameObjectWithHideFlags(PreviewName, HideFlags.HideAndDontSave);

            // Transform есть у объекта с рождения — второй такой не добавить.
            var component = type == typeof(Transform) ? host.transform : host.AddComponent(type);
            if (component == null) return null;

            var so = new SerializedObject(component);
            UnityPropertyApplier.Apply(so, doc);
            so.ApplyModifiedPropertiesWithoutUndo();
            return so;
        }

        public void Dispose()
        {
            if (_oldObject != null) { UnityEngine.Object.DestroyImmediate(_oldObject); _oldObject = null; }
            if (_newObject != null) { UnityEngine.Object.DestroyImmediate(_newObject); _newObject = null; }
            _oldSo = null;
            _newSo = null;
            _live = null;
            _liveSo = null;
        }

        /// <summary>
        /// Живой компонент для режима «с текущим состоянием». Правки в нём —
        /// обычные правки сцены: с записью в Undo и пометкой сцены изменённой.
        /// </summary>
        /// <param name="readOnlyReason">Если задано — правая колонка только для чтения, причина показана над ней.</param>
        public void SetLive(Component live, string readOnlyReason = null)
        {
            _liveReadOnly = readOnlyReason;
            if (live == _live && (_liveSo != null || live == null)) return;
            _live = live;
            _liveSo = live != null ? new SerializedObject(live) : null;
        }

        /// <summary>
        /// Подбирает объекты, оставшиеся от прошлых сессий: HideAndDontSave
        /// переживает перезагрузку домена, а ссылки на них — нет.
        /// </summary>
        public static void SweepStrays()
        {
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.name != PreviewName) continue;
                if ((go.hideFlags & HideFlags.HideAndDontSave) == 0) continue;
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ------------------------------------------------------------ показ ---

        /// <param name="againstLive">Справа — не состояние после коммита, а текущее на сцене, и его можно править.</param>
        public void Draw(float width, bool onlyChanged, bool againstLive = false)
        {
            if (!Ready)
            {
                EditorGUILayout.LabelField(Problem ?? L.T("Inspector unavailable."), EditorStyles.miniLabel);
                return;
            }

            if (againstLive)
            {
                DrawAgainstLive(width, onlyChanged);
                return;
            }

            float column = Mathf.Max(110f, (width - MarkerWidth - 30f) * 0.5f);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(_oldSo == null ? L.T("Component Didn't Exist") : L.T("Before Commit"),
                    EditorStyles.miniBoldLabel, GUILayout.Width(column));
                GUILayout.Space(MarkerWidth);
                EditorGUILayout.LabelField(_newSo == null ? L.T("Component Removed") : L.T("After Commit"),
                    EditorStyles.miniBoldLabel, GUILayout.Width(column));
            }

            if (_oldSo != null) _oldSo.Update();
            if (_newSo != null) _newSo.Update();

            // Обход ведётся по той стороне, которая есть: у добавленного
            // компонента это правая, у удалённого — левая.
            var walker = (_newSo ?? _oldSo).GetIterator();
            bool enterChildren = true;

            var tint = GitPalette.Fill(GitFileStatus.Modified);
            tint.a = 0.12f;
            var dot = GitPalette.Fill(GitFileStatus.Modified);

            while (walker.NextVisible(enterChildren))
            {
                enterChildren = false;

                var left = _oldSo != null ? _oldSo.FindProperty(walker.propertyPath) : null;
                var right = _newSo != null ? _newSo.FindProperty(walker.propertyPath) : null;
                if (left == null && right == null) continue;

                bool same = left != null && right != null && SerializedProperty.DataEquals(left, right);
                if (onlyChanged && same) continue;

                // Раскрытие держим общим, иначе стороны разъедутся по высоте.
                if (left != null && right != null) left.isExpanded = right.isExpanded;

                var row = EditorGUILayout.BeginHorizontal();

                if (!same && Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(row, tint);

                using (new EditorGUI.DisabledScope(true))
                {
                    if (left != null) EditorGUILayout.PropertyField(left, true, GUILayout.Width(column));
                    else GUILayout.Space(column);

                    float height = EditorGUI.GetPropertyHeight(right ?? left, true);
                    var marker = GUILayoutUtility.GetRect(MarkerWidth, height, GUILayout.Width(MarkerWidth));

                    if (!same && Event.current.type == EventType.Repaint)
                        EditorGUI.DrawRect(new Rect(marker.x + 4f, marker.y + 6f, 7f, 7f), dot);

                    if (right != null) EditorGUILayout.PropertyField(right, true, GUILayout.Width(column));
                    else GUILayout.Space(column);
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// Слева — состояние после коммита, справа — живой компонент. Обход идёт
        /// по живой стороне: её поля и правятся, а снимок коммита только для чтения.
        /// </summary>
        private void DrawAgainstLive(float width, bool onlyChanged)
        {
            float column = Mathf.Max(110f, (width - MarkerWidth - 30f) * 0.5f);
            var snapshot = _newSo;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(snapshot == null ? L.T("Component Removed in This Commit") : L.T("After Commit"),
                    EditorStyles.miniBoldLabel, GUILayout.Width(column));
                GUILayout.Space(MarkerWidth);
                EditorGUILayout.LabelField(_liveReadOnly != null ? L.T("Now") : L.T("Now — Editable"),
                    EditorStyles.miniBoldLabel, GUILayout.Width(column));
            }

            if (_liveReadOnly != null) EditorGUILayout.LabelField(_liveReadOnly, EditorStyles.miniLabel);

            if (_live == null || _liveSo == null)
            {
                EditorGUILayout.LabelField(L.T("The component isn't in the open scene."), EditorStyles.miniLabel);
                return;
            }

            if (snapshot != null) snapshot.Update();
            _liveSo.Update();

            var tint = GitPalette.Fill(GitFileStatus.Modified);
            tint.a = 0.12f;
            var dot = GitPalette.Fill(GitFileStatus.Modified);

            var live = _liveSo.GetIterator();
            bool enterChildren = true;
            int shown = 0;

            while (live.NextVisible(enterChildren))
            {
                enterChildren = false;

                var left = snapshot != null ? snapshot.FindProperty(live.propertyPath) : null;
                bool same = left != null && SerializedProperty.DataEquals(left, live);
                if (onlyChanged && same) continue;
                shown++;

                if (left != null) left.isExpanded = live.isExpanded;

                var row = EditorGUILayout.BeginHorizontal();
                if (!same && Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(row, tint);

                using (new EditorGUI.DisabledScope(true))
                {
                    if (left != null) EditorGUILayout.PropertyField(left, true, GUILayout.Width(column));
                    else GUILayout.Space(column);
                }

                float height = EditorGUI.GetPropertyHeight(live, true);
                var marker = GUILayoutUtility.GetRect(MarkerWidth, height, GUILayout.Width(MarkerWidth));
                if (!same && Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(new Rect(marker.x + 4f, marker.y + 6f, 7f, 7f), dot);

                using (new EditorGUI.DisabledScope(_liveReadOnly != null || live.propertyPath == "m_Script"))
                    EditorGUILayout.PropertyField(live, true, GUILayout.Width(column));

                EditorGUILayout.EndHorizontal();
            }

            if (shown == 0)
                EditorGUILayout.LabelField(L.T("The component is now the same as after this commit."), EditorStyles.miniLabel);

            // Правка живого компонента — обычная правка сцены: SerializedObject сам
            // заводит запись в истории отмены.
            if (_liveReadOnly == null && _liveSo.ApplyModifiedProperties())
            {
                // У префаба «сейчас» — это сам файл префаба: пометка грязным сохранит правку с проектом.
                EditorUtility.SetDirty(_live);
                SceneChangeIndex.MarkDirty();
            }
        }
    }
}
