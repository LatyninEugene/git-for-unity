using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>
    /// Модальная правка сообщения коммита: первая строка — заголовок, дальше
    /// после пустой строки — описание, как принято в git.
    ///
    /// На IMGUI по той же причине, что и <see cref="TextPromptWindow"/>: окно
    /// должно блокировать вызывающий код до ответа.
    /// </summary>
    internal sealed class CommitMessageWindow : EditorWindow
    {
        private string _hint = string.Empty;
        private string _message = string.Empty;
        private bool _accepted;
        private bool _focusRequested;
        private Vector2 _scroll;

        /// <summary>Возвращает новое сообщение или null, если отменили.</summary>
        public static string Ask(string title, string hint, string message)
        {
            var w = CreateInstance<CommitMessageWindow>();
            w.titleContent = new GUIContent(title);
            w._hint = hint ?? string.Empty;
            w._message = message ?? string.Empty;
            w.minSize = new Vector2(520f, 260f);
            w.maxSize = new Vector2(1000f, 800f);

            w.ShowModalUtility();
            return w._accepted ? Normalize(w._message) : null;
        }

        private static string Normalize(string message)
        {
            return (message ?? string.Empty).Replace("\r\n", "\n").Trim();
        }

        private void OnGUI()
        {
            bool empty = string.IsNullOrWhiteSpace(_message);

            EditorGUILayout.Space(6f);
            if (!string.IsNullOrEmpty(_hint))
            {
                EditorGUILayout.LabelField(_hint, EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space(4f);
            }

            HandleKeys(!empty);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            GUI.SetNextControlName("message");
            _message = EditorGUILayout.TextArea(_message, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();

            if (!_focusRequested)
            {
                // Фокус ставится после первой отрисовки: до неё поля ещё нет.
                EditorGUI.FocusTextInControl("message");
                _focusRequested = true;
            }

            EditorGUILayout.LabelField(
                empty ? L.T("The message can't be empty.") : L.T("The first line is the title. Ctrl+Enter saves."),
                EditorStyles.miniLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(L.T("Cancel"), GUILayout.Width(90f))) Close();
                using (new EditorGUI.DisabledScope(empty))
                {
                    if (GUILayout.Button(L.T("Save"), GUILayout.Width(90f))) Accept();
                }
            }

            EditorGUILayout.Space(6f);
        }

        private void HandleKeys(bool valid)
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;

            if (e.keyCode == KeyCode.Escape)
            {
                Close();
                e.Use();
                return;
            }

            // Просто Enter — перевод строки в описании, сохранение — с Ctrl.
            if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && (e.control || e.command) && valid)
            {
                Accept();
                e.Use();
            }
        }

        private void Accept()
        {
            _accepted = true;
            Close();
        }
    }
}
