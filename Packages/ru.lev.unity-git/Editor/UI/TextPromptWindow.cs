using System;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.UI
{
    /// <summary>
    /// Модальный ввод одной строки: имя ветки, имя тега.
    ///
    /// На IMGUI, а не на UI Toolkit: окно должно блокировать вызывающий код до
    /// ответа, а ShowModalUtility возвращает управление только после закрытия —
    /// разметку UI Toolkit к этому моменту ещё не успевают построить.
    /// </summary>
    public sealed class TextPromptWindow : EditorWindow
    {
        private string _hint = string.Empty;
        private string _label = string.Empty;
        private string _value = string.Empty;
        private Func<string, string> _validate;

        private bool _accepted;
        private bool _focusRequested;

        /// <summary>Возвращает введённое или null, если отменили.</summary>
        public static string Ask(string title, string hint, string label, string initial,
                                 Func<string, string> validate = null)
        {
            var w = CreateInstance<TextPromptWindow>();
            w.titleContent = new GUIContent(title);
            w._hint = hint ?? string.Empty;
            w._label = label ?? string.Empty;
            w._value = initial ?? string.Empty;
            w._validate = validate;

            var size = new Vector2(430f, 150f);
            w.minSize = size;
            w.maxSize = size;

            w.ShowModalUtility();
            return w._accepted ? w._value.Trim() : null;
        }

        private void OnGUI()
        {
            var problem = _validate != null ? _validate(_value) : DefaultValidate(_value);

            EditorGUILayout.Space(6f);

            if (!string.IsNullOrEmpty(_hint))
            {
                EditorGUILayout.LabelField(_hint, EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space(4f);
            }

            GUI.SetNextControlName("value");
            _value = EditorGUILayout.TextField(_label, _value);

            if (!_focusRequested)
            {
                // Фокус ставится после первой отрисовки: до неё контрола ещё нет.
                EditorGUI.FocusTextInControl("value");
                _focusRequested = true;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(
                problem ?? " ",
                problem == null ? EditorStyles.miniLabel : ErrorStyle);

            HandleKeys(problem == null);

            GUILayout.FlexibleSpace();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (GUILayout.Button(L.T("Cancel"), GUILayout.Width(90f))) Close();

                using (new EditorGUI.DisabledScope(problem != null))
                {
                    if (GUILayout.Button(L.T("Create"), GUILayout.Width(90f))) Accept();
                }
            }

            EditorGUILayout.Space(6f);
        }

        private void HandleKeys(bool valid)
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;

            if (e.keyCode == KeyCode.Escape) { Close(); e.Use(); return; }

            if ((e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && valid)
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

        /// <summary>
        /// Правила имён ссылок из git check-ref-format, сведённые к тому, что
        /// реально набирают руками. Проверяем здесь, а не по ответу git: своя
        /// подсказка понятнее, чем «fatal: invalid reference», и появляется до
        /// нажатия кнопки.
        /// </summary>
        private static string DefaultValidate(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return L.T("The name can't be empty.");

            var s = name.Trim();

            if (s.StartsWith("-") || s.StartsWith("/") || s.EndsWith("/"))
                return L.T("The name can't start with “-” or “/” or end with “/”.");

            if (s.EndsWith(".") || s.EndsWith(".lock"))
                return L.T("The name can't end with “.” or “.lock”.");

            if (s.Contains("..") || s.Contains("//") || s.Contains("@{"))
                return L.T("“..”, “//” and “@{” aren't allowed in a name.");

            foreach (var ch in s)
            {
                if (ch < 32 || ch == 127) return L.T("The name contains a control character.");
                if (" ~^:?*[\\".IndexOf(ch) >= 0)
                    return L.F("The character “{0}” can't be used in a branch or tag name.", ch);
            }

            return null;
        }

        private static GUIStyle _errorStyle;

        private static GUIStyle ErrorStyle
        {
            get
            {
                if (_errorStyle == null)
                {
                    _errorStyle = new GUIStyle(EditorStyles.miniLabel);
                    _errorStyle.normal.textColor = EditorGUIUtility.isProSkin
                        ? new Color(0.95f, 0.55f, 0.50f)
                        : new Color(0.75f, 0.20f, 0.15f);
                }
                return _errorStyle;
            }
        }
    }
}
