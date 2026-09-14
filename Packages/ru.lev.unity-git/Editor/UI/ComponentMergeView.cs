using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>
    /// Компонент в трёх колонках настоящим инспектором: моя версия, итог, их.
    ///
    /// Устроен как <see cref="ComponentDiffView"/>: стороны собираются на
    /// скрытых временных объектах, все колонки рисуются одним обходом свойств,
    /// чтобы строки не разъезжались. Между колонками — стрелки «взять», у тех
    /// свойств, к которым относятся правки слияния.
    /// </summary>
    internal sealed class ComponentMergeView : IDisposable
    {
        private readonly GameObject[] _hosts = new GameObject[3];
        private readonly SerializedObject[] _sides = new SerializedObject[3];

        public string Problem { get; private set; }

        public bool Ready => _sides[0] != null || _sides[1] != null || _sides[2] != null;

        /// <param name="docs">Три версии в порядке колонок; null — на этой стороне компонента нет.</param>
        public ComponentMergeView(Type type, UnityDocument[] docs)
        {
            if (type == null) { Problem = L.T("Component type can't be determined — showing values."); return; }
            if (!SceneHistoryIcons.CanHost(type)) { Problem = L.F("Can't build the {0} inspector — showing values.", type.Name); return; }

            try
            {
                for (int i = 0; i < 3; i++)
                    if (docs[i] != null) _sides[i] = Build(type, docs[i], i);
            }
            catch (Exception e)
            {
                Problem = L.F("Couldn't build the inspector: {0}", e.Message);
                Dispose();
            }
        }

        private SerializedObject Build(Type type, UnityDocument doc, int index)
        {
            var host = EditorUtility.CreateGameObjectWithHideFlags(ComponentDiffView.PreviewName, HideFlags.HideAndDontSave);
            _hosts[index] = host;

            var component = type == typeof(Transform) ? host.transform : host.AddComponent(type);
            if (component == null) return null;

            var so = new SerializedObject(component);
            UnityPropertyApplier.Apply(so, doc);
            so.ApplyModifiedPropertiesWithoutUndo();
            return so;
        }

        public void Dispose()
        {
            for (int i = 0; i < 3; i++)
            {
                if (_hosts[i] != null) UnityEngine.Object.DestroyImmediate(_hosts[i]);
                _hosts[i] = null;
                _sides[i] = null;
            }
        }

        /// <summary>
        /// Рисует свойства. Возвращает имена всех свойств инспектора, чтобы
        /// вызывающая сторона показала отдельно правки, которым строки не нашлось.
        /// </summary>
        /// <param name="changesFor">Правки, относящиеся к свойству верхнего уровня.</param>
        /// <param name="take">Взять версию стороны для набора правок.</param>
        public HashSet<string> Draw(float column, float gutter, bool onlyDifferent,
                                    Func<string, List<YamlMergeConflict>> changesFor,
                                    Action<List<YamlMergeConflict>, MergeSide> take)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            if (!Ready)
            {
                EditorGUILayout.LabelField(Problem ?? L.T("Inspector unavailable."), EditorStyles.miniLabel);
                return names;
            }

            foreach (var so in _sides) if (so != null) so.Update();

            var walker = (_sides[1] ?? _sides[0] ?? _sides[2]).GetIterator();
            bool enter = true;

            var modified = GitPalette.Fill(GitFileStatus.Modified);
            modified.a = 0.10f;
            var conflict = new Color(0.9f, 0.25f, 0.25f, 0.22f);

            while (walker.NextVisible(enter))
            {
                enter = false;
                names.Add(walker.propertyPath);

                var props = new SerializedProperty[3];
                for (int i = 0; i < 3; i++)
                    props[i] = _sides[i] != null ? _sides[i].FindProperty(walker.propertyPath) : null;

                var changes = changesFor(walker.propertyPath);
                bool differ = !Equal(props[0], props[1]) || !Equal(props[1], props[2]);
                if (onlyDifferent && !differ && changes.Count == 0) continue;

                // Раскрытие общее — иначе колонки разъедутся по высоте.
                var lead = props[1] ?? props[0] ?? props[2];
                foreach (var p in props) if (p != null && p != lead) p.isExpanded = lead.isExpanded;

                bool open = false;
                foreach (var c in changes) if (!c.IsResolved) open = true;

                float height = EditorGUI.GetPropertyHeight(lead, true);

                var row = EditorGUILayout.BeginHorizontal();
                if (differ && Event.current.type == EventType.Repaint) EditorGUI.DrawRect(row, modified);

                Field(props[0], column);
                Arrow(gutter, height, changes, "»", MergeSide.Mine, take);

                Field(props[1], column);
                if (open && Event.current.type == EventType.Repaint) EditorGUI.DrawRect(GUILayoutUtility.GetLastRect(), conflict);

                Arrow(gutter, height, changes, "«", MergeSide.Theirs, take);
                Field(props[2], column);

                EditorGUILayout.EndHorizontal();
            }

            return names;
        }

        private static void Field(SerializedProperty p, float column)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                if (p != null) EditorGUILayout.PropertyField(p, true, GUILayout.Width(column));
                else GUILayout.Label("—", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(column));
            }
        }

        private static void Arrow(float gutter, float height, List<YamlMergeConflict> changes, string text,
                                  MergeSide side, Action<List<YamlMergeConflict>, MergeSide> take)
        {
            var rect = GUILayoutUtility.GetRect(gutter, height, GUILayout.Width(gutter));
            if (changes.Count == 0) return;

            var button = new Rect(rect.x + 3f, rect.y + 1f, rect.width - 6f, 16f);
            if (GUI.Button(button, new GUIContent(text, side == MergeSide.Mine ? L.T("Take my value") : L.T("Take their value")),
                           EditorStyles.miniButton))
                take(changes, side);
        }

        private static bool Equal(SerializedProperty a, SerializedProperty b)
        {
            if (a == null || b == null) return a == null && b == null;
            return SerializedProperty.DataEquals(a, b);
        }
    }
}
