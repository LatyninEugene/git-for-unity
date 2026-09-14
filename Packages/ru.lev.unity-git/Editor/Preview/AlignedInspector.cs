using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Lev.Git.Preview
{
    /// <summary>
    /// Две версии ассета одним обходом полей: слева прежняя, справа новая,
    /// между ними отметка у изменённой строки. Так же устроена история
    /// компонентов — строки не разъезжаются, и изменённое видно сразу.
    ///
    /// Настоящий инспектор Unity так не выровнять и не подсветить: это чужой
    /// IMGUI-код, в строки которого не залезть. Поэтому поля рисуются по одному:
    /// у материала — свойствами шейдера через MaterialEditor, у остальных —
    /// сериализованными полями, как у компонентов.
    /// </summary>
    internal sealed class AlignedInspector : IDisposable
    {
        private const float Marker = 16f;
        private const float Scrollbar = 16f;

        private enum Access { Edit, Browse, Locked }

        /// <summary>Выбор человека общий для всех панелей.</summary>
        public static bool OnlyChanged;

        public bool ShowHidden;

        /// <summary>Правка в живой стороне — пора пересчитать список изменений.</summary>
        public event Action LiveEdited;

        private LoadedAsset _before, _after;
        private string _beforeCaption, _afterCaption;

        private MaterialEditor _beforeMaterial, _afterMaterial;
        private SerializedObject _beforeSo, _afterSo;

        /// <summary>Изменённые поля и все их родители: строка массива подсвечена, если внутри что-то поменялось.</summary>
        private readonly HashSet<string> _changed = new HashSet<string>(StringComparer.Ordinal);

        private Vector2 _scroll;

        public void Set(LoadedAsset before, string beforeCaption, LoadedAsset after, string afterCaption)
        {
            _beforeCaption = beforeCaption;
            _afterCaption = afterCaption;

            if (before == _before && after == _after) return;

            Release();
            _before = before;
            _after = after;

            var b = before != null ? before.Main : null;
            var a = after != null ? after.Main : null;

            if ((a ?? b) is Material)
            {
                if (b is Material) _beforeMaterial = Editor.CreateEditor(b, typeof(MaterialEditor)) as MaterialEditor;
                if (a is Material) _afterMaterial = Editor.CreateEditor(a, typeof(MaterialEditor)) as MaterialEditor;
            }
            else
            {
                if (b != null) _beforeSo = new SerializedObject(b);
                if (a != null) _afterSo = new SerializedObject(a);
            }
        }

        public void SetChanges(List<ChangeItem> items)
        {
            _changed.Clear();
            if (items == null) return;

            foreach (var item in items)
            {
                AddKey(item.Key);
                foreach (var path in item.SerializedPaths) AddKey(path);
            }
        }

        private void AddKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            _changed.Add(key);
            for (int i = key.IndexOf('.'); i >= 0; i = key.IndexOf('.', i + 1))
                _changed.Add(key.Substring(0, i));
        }

        public void Dispose()
        {
            Release();
            _before = _after = null;
        }

        private void Release()
        {
            if (_beforeMaterial != null) Object.DestroyImmediate(_beforeMaterial);
            if (_afterMaterial != null) Object.DestroyImmediate(_afterMaterial);
            _beforeMaterial = _afterMaterial = null;
            _beforeSo = _afterSo = null;
        }

        // ------------------------------------------------------------ показ ---

        public void Draw(float width, float height)
        {
            bool hasBefore = _before != null && _before.Main != null;
            bool hasAfter = _after != null && _after.Main != null;
            if (!hasBefore && !hasAfter) return;

            float column = Mathf.Max(90f, (width - Marker - Scrollbar - 4f) * 0.5f);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(Caption(_before, _beforeCaption), EditorStyles.miniBoldLabel, GUILayout.Width(column));
                GUILayout.Space(Marker);
                GUILayout.Label(Caption(_after, _afterCaption), EditorStyles.miniBoldLabel, GUILayout.Width(Mathf.Max(40f, column - 120f)));
                GUILayout.FlexibleSpace();
                OnlyChanged = EditorGUILayout.ToggleLeft(L.T("Only Changed"), OnlyChanged, EditorStyles.miniLabel, GUILayout.Width(120f));
            }

            float labelWidth = EditorGUIUtility.labelWidth;
            bool wideMode = EditorGUIUtility.wideMode;

            try
            {
                EditorGUIUtility.labelWidth = Mathf.Max(70f, column * 0.45f);
                EditorGUIUtility.wideMode = column > 330f;

                // Высота задаётся явно: без неё прокрутка в IMGUIContainer
                // сжимается до пары строк.
                _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar,
                    GUIStyle.none, GUILayout.Height(Mathf.Max(40f, height - EditorGUIUtility.singleLineHeight - 6f)));

                int shown = _beforeMaterial != null || _afterMaterial != null ? DrawMaterial(column) : DrawSerialized(column);

                if (shown == 0)
                    GUILayout.Label(OnlyChanged ? L.T("No changed fields.") : L.T("No fields."), EditorStyles.miniLabel);

                GUILayout.EndScrollView();
            }
            finally
            {
                EditorGUIUtility.labelWidth = labelWidth;
                EditorGUIUtility.wideMode = wideMode;
            }
        }

        private static string Caption(LoadedAsset side, string caption)
        {
            if (side == null || side.Main == null) return L.F("{0} · missing", caption);
            return side.Live ? L.F("{0} · editable", caption) : L.F("{0} · read-only", caption);
        }

        // ---------------------------------------------------------- материал ---

        private int DrawMaterial(float column)
        {
            var b = _before != null ? _before.Main as Material : null;
            var a = _after != null ? _after.Main as Material : null;
            var shader = a != null && a.shader != null ? a.shader : b != null ? b.shader : null;

            float line = EditorGUIUtility.singleLineHeight;
            var afterAccess = _after != null && _after.Live ? Access.Edit : Access.Locked;
            int shown = 0;

            if (Visible("m_Shader"))
            {
                shown++;
                Row(column, line, _changed.Contains("m_Shader"),
                    b != null ? r => EditorGUI.ObjectField(r, "Shader", b.shader, typeof(Shader), false) : (Action<Rect>)null,
                    a != null ? r => EditorGUI.ObjectField(r, "Shader", a.shader, typeof(Shader), false) : (Action<Rect>)null,
                    Access.Locked, Access.Locked);
            }

            if (shader != null)
            {
                for (int i = 0; i < shader.GetPropertyCount(); i++)
                {
                    var name = shader.GetPropertyName(i);
                    bool changed = _changed.Contains(name);
                    bool hidden = (shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0;

                    // Скрытое свойство показываем, только если оно изменилось и
                    // скрытые включены — как в списке изменений.
                    if (hidden && !(changed && ShowHidden)) continue;
                    if (OnlyChanged && !changed) continue;

                    var description = shader.GetPropertyDescription(i);
                    var label = string.IsNullOrEmpty(description) || description.StartsWith("_", StringComparison.Ordinal) ? name : description;

                    var pb = b != null && _beforeMaterial != null && b.HasProperty(name) ? MaterialEditor.GetMaterialProperty(new Object[] { b }, name) : null;
                    var pa = a != null && _afterMaterial != null && a.HasProperty(name) ? MaterialEditor.GetMaterialProperty(new Object[] { a }, name) : null;

                    float height = Mathf.Max(
                        pb != null ? _beforeMaterial.GetPropertyHeight(pb, label) : 0f,
                        pa != null ? _afterMaterial.GetPropertyHeight(pa, label) : 0f);
                    if (height <= 0f) continue;

                    shown++;
                    Row(column, height, changed,
                        pb != null ? r => _beforeMaterial.ShaderProperty(r, pb, label) : (Action<Rect>)null,
                        pa != null ? r => Edited(() => _afterMaterial.ShaderProperty(r, pa, label)) : (Action<Rect>)null,
                        Access.Locked, afterAccess);
                }
            }

            if (Visible("m_CustomRenderQueue"))
            {
                shown++;
                Row(column, line, _changed.Contains("m_CustomRenderQueue"),
                    _beforeMaterial != null ? r => _beforeMaterial.RenderQueueField(r) : (Action<Rect>)null,
                    _afterMaterial != null ? r => Edited(() => _afterMaterial.RenderQueueField(r)) : (Action<Rect>)null,
                    Access.Locked, afterAccess);
            }

            if (Visible("enableInstancing"))
            {
                shown++;
                Row(column, line, _changed.Contains("enableInstancing"),
                    _beforeMaterial != null ? r => _beforeMaterial.EnableInstancingField(r) : (Action<Rect>)null,
                    _afterMaterial != null ? r => Edited(() => _afterMaterial.EnableInstancingField(r)) : (Action<Rect>)null,
                    Access.Locked, afterAccess);
            }

            return shown;
        }

        private bool Visible(string key)
        {
            return !OnlyChanged || _changed.Contains(key);
        }

        private void Edited(Action draw)
        {
            EditorGUI.BeginChangeCheck();
            draw();
            if (EditorGUI.EndChangeCheck() && LiveEdited != null) LiveEdited();
        }

        // ---------------------------------------------------- прочие ассеты ---

        private int DrawSerialized(float column)
        {
            if (_beforeSo != null) _beforeSo.Update();
            if (_afterSo != null) _afterSo.Update();

            var walker = (_afterSo ?? _beforeSo).GetIterator();
            var afterAccess = _after != null && _after.Live ? Access.Edit : Access.Browse;

            bool enter = true;
            int shown = 0;

            while (walker.NextVisible(enter))
            {
                enter = false;

                var path = walker.propertyPath;
                if (path == "m_ObjectHideFlags") continue;

                var left = _beforeSo != null ? _beforeSo.FindProperty(path) : null;
                var right = _afterSo != null ? _afterSo.FindProperty(path) : null;
                if (left == null && right == null) continue;

                bool changed = _changed.Contains(path);
                if (OnlyChanged && !changed) continue;

                bool wasExpanded = right != null ? right.isExpanded : left.isExpanded;
                if (left != null && right != null) left.isExpanded = right.isExpanded;

                float height = Mathf.Max(
                    left != null ? EditorGUI.GetPropertyHeight(left, true) : 0f,
                    right != null ? EditorGUI.GetPropertyHeight(right, true) : 0f);

                bool script = path == "m_Script";
                shown++;

                Row(column, height, changed,
                    left != null ? r => EditorGUI.PropertyField(r, left, true) : (Action<Rect>)null,
                    right != null ? r => EditorGUI.PropertyField(r, right, true) : (Action<Rect>)null,
                    script ? Access.Locked : Access.Browse,
                    script ? Access.Locked : afterAccess);

                // Раскрыли слева — раскрываем и справа, иначе строки разъедутся.
                if (left != null && right != null && left.isExpanded != wasExpanded)
                    right.isExpanded = left.isExpanded;
            }

            // Прежняя версия не применяется никогда: её случайная правка
            // пропадёт при следующем Update. Живая — обычная правка с Undo.
            if (_after != null && _after.Live && _afterSo != null && _afterSo.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(_after.Main);
                if (LiveEdited != null) LiveEdited();
            }

            return shown;
        }

        // ------------------------------------------------------------ строка ---

        private static void Row(float column, float height, bool changed, Action<Rect> left, Action<Rect> right,
                                Access leftAccess, Access rightAccess)
        {
            float width = column * 2f + Marker;
            var row = GUILayoutUtility.GetRect(width, height + 2f, GUILayout.Width(width));

            var fill = GitPalette.Fill(GitFileStatus.Modified);
            if (changed && Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(row, new Color(fill.r, fill.g, fill.b, 0.12f));

            var l = new Rect(row.x, row.y + 1f, column, height);
            var m = new Rect(l.xMax, row.y + 1f, Marker, height);
            var r = new Rect(m.xMax, row.y + 1f, column, height);

            if (left != null) Side(l, left, leftAccess);

            if (changed && Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(m.x + 4.5f, m.y + 5f, 7f, 7f), fill);

            if (right != null) Side(r, right, rightAccess);
        }

        private static void Side(Rect rect, Action<Rect> draw, Access access)
        {
            if (access == Access.Edit)
            {
                draw(rect);
                return;
            }

            if (access == Access.Locked)
            {
                using (new EditorGUI.DisabledScope(true)) draw(rect);
                return;
            }

            // Просмотр: раскрывать массивы можно, значения затемнены и не применяются.
            var color = GUI.color;
            GUI.color = new Color(color.r, color.g, color.b, color.a * 0.6f);
            try { draw(rect); }
            finally { GUI.color = color; }
        }
    }
}
