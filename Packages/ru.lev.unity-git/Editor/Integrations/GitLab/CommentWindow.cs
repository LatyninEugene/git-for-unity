using UnityEditor;
using UnityEngine;

namespace Lev.Git.GitLab
{
    /// <summary>
    /// Модальный ввод комментария: к строке diff, к объекту сцены, к MR.
    ///
    /// На IMGUI по той же причине, что и TextPromptWindow: вызывающему коду
    /// нужен ответ сразу, а ShowModalUtility возвращает управление только
    /// после закрытия окна.
    /// </summary>
    public sealed class CommentWindow : EditorWindow
    {
        private string _context = string.Empty;
        private string _value = string.Empty;
        private string _ok;
        private bool _accepted;
        private bool _focusRequested;
        private Vector2 _scroll;

        /// <summary>Текст комментария или null, если отменили или ничего не написали.</summary>
        public static string Ask(string title, string context, string okLabel = null, string initial = null)
        {
            var w = CreateInstance<CommentWindow>();
            w.titleContent = new GUIContent(title);
            w._context = context ?? string.Empty;
            w._ok = okLabel ?? L.T("Send");
            w._value = initial ?? string.Empty;
            w.minSize = new Vector2(480f, 240f);
            w.maxSize = new Vector2(1200f, 900f);
            w.ShowModalUtility();

            return w._accepted && !string.IsNullOrWhiteSpace(w._value) ? w._value.Trim() : null;
        }

        private void OnGUI()
        {
            // До TextArea: иначе поле само съест Enter и Escape.
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape) { Close(); e.Use(); return; }
                if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && (e.control || e.command) &&
                    !string.IsNullOrWhiteSpace(_value))
                {
                    _accepted = true;
                    Close();
                    e.Use();
                    return;
                }
            }

            EditorGUILayout.Space(6f);
            if (_context.Length > 0)
            {
                EditorGUILayout.LabelField(_context, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(4f);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            GUI.SetNextControlName("comment");
            _value = EditorGUILayout.TextArea(_value, EditorStyles.textArea, GUILayout.ExpandHeight(true), GUILayout.MinHeight(110f));
            EditorGUILayout.EndScrollView();

            if (!_focusRequested)
            {
                EditorGUI.FocusTextInControl("comment");
                _focusRequested = true;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(L.T("GitLab Markdown · Ctrl+Enter to send"), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(L.T("Cancel"), GUILayout.Width(90f))) Close();
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_value)))
                    if (GUILayout.Button(_ok ?? L.T("Send"), GUILayout.Width(110f)))
                    {
                        _accepted = true;
                        Close();
                    }
            }
            EditorGUILayout.Space(6f);
        }
    }
}
