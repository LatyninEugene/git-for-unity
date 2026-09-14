using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.Diagnostics
{
    /// <summary>
    /// Окно отчёта о проблеме. Собирает сведения, показывает, что именно
    /// попадёт в файл, и сохраняет zip. Ничего никуда не отправляет: куда
    /// переслать файл, решает сам человек.
    /// </summary>
    internal sealed class ProblemReportWindow : EditorWindow, ILocalizedWindow
    {
        private static readonly int[] LogDayOptions = { 0, 1, 3, 7 };

        [SerializeField] private string _description = string.Empty;
        [SerializeField] private string _steps = string.Empty;
        [SerializeField] private bool _hidePaths;
        [SerializeField] private bool _hideServers = true;
        [SerializeField] private int _logDays = 3;
        [SerializeField] private bool _screenshot;
        [SerializeField] private List<string> _attachments = new List<string>();
        [SerializeField] private List<string> _excluded = new List<string>();

        private List<ReportSection> _sections;
        private bool _collecting;
        private bool _showPreview;
        private string _preview;
        private string _previewKey;
        private string _status;
        private bool _statusError;
        private string _savedPath;
        private Vector2 _scroll, _previewScroll;

        [MenuItem("Help/Git for Unity/Report a Problem...", false, 2000)]
        public static void Open()
        {
            var w = GetWindow<ProblemReportWindow>(true);
            w.SetTitle();
            w.minSize = new Vector2(560f, 540f);
            w.Show();
            w.Focus();
            w.Collect();
        }

        [MenuItem("Help/Git for Unity/Open Log Folder", false, 2001)]
        public static void RevealLogs()
        {
            Journal.Flush();
            Directory.CreateDirectory(Journal.Directory);
            var files = Journal.Files(Journal.KeepDays);
            EditorUtility.RevealInFinder(files.Count > 0 ? files[files.Count - 1] : Journal.Directory);
        }

        private void SetTitle()
        {
            titleContent = new GUIContent(L.T("Report a Problem"));
        }

        void ILocalizedWindow.OnLanguageChanged()
        {
            SetTitle();
            Repaint();
        }

        private async void Collect()
        {
            if (_collecting) return;
            _collecting = true;
            _status = L.T("Collecting information…");
            _statusError = false;
            Repaint();

            try
            {
                var sections = await ProblemReport.CollectAsync();
                foreach (var s in sections) s.Included = !_excluded.Contains(s.Id);
                _sections = sections;
                _previewKey = null;
                _status = null;
            }
            catch (Exception e)
            {
                _status = L.F("Information was not collected: {0}", e.Message);
                _statusError = true;
                Journal.Exception(e, "Problem report");
            }
            finally
            {
                _collecting = false;
                Repaint();
            }
        }

        private ReportOptions Options()
        {
            var o = new ReportOptions
            {
                Description = _description,
                Steps = _steps,
                HidePaths = _hidePaths,
                HideServers = _hideServers,
                LogDays = _logDays,
                Screenshot = _screenshot
            };
            o.Attachments.AddRange(_attachments);
            return o;
        }

        private void OnGUI()
        {
            if (_sections == null && !_collecting) Collect();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUILayout.VerticalScope(new GUIStyle { padding = new RectOffset(12, 12, 10, 10) }))
            {
                EditorGUILayout.LabelField(
                    L.T("The report is a zip file with diagnostic data. You can study it yourself or attach it to an issue on GitHub. It is not sent anywhere automatically, and tokens and passwords are always removed from it."),
                    EditorStyles.wordWrappedLabel);

                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField(L.T("What Happened?"), EditorStyles.boldLabel);
                _description = EditorGUILayout.TextArea(_description, GUILayout.MinHeight(54f));

                EditorGUILayout.LabelField(L.T("Steps to Reproduce"), EditorStyles.boldLabel);
                _steps = EditorGUILayout.TextArea(_steps, GUILayout.MinHeight(54f));

                EditorGUILayout.Space(8f);
                DrawSections();

                EditorGUILayout.Space(8f);
                DrawFiles();

                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField(L.T("Privacy"), EditorStyles.boldLabel);
                _hidePaths = EditorGUILayout.ToggleLeft(
                    L.C("Hide File Paths and Branch Names", "Folder, file and branch names are replaced with short codes. File extensions stay."),
                    _hidePaths);
                _hideServers = EditorGUILayout.ToggleLeft(
                    L.C("Hide Server Address", "Host names in addresses are replaced with <server>."),
                    _hideServers);

                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField(L.T("Detailed Log"), EditorStyles.boldLabel);
                Journal.Verbose = EditorGUILayout.ToggleLeft(L.T("Record every git command until the editor restarts"), Journal.Verbose);
                EditorGUILayout.LabelField(
                    L.T("If the problem is hard to catch: turn this on, repeat the problem, then save the report."),
                    EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(8f);
                _showPreview = EditorGUILayout.Foldout(_showPreview, L.T("Preview of report.md"), true);
                if (_showPreview) DrawPreview();
            }
            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        private void DrawSections()
        {
            EditorGUILayout.LabelField(L.T("What to Include"), EditorStyles.boldLabel);

            if (_sections == null)
            {
                EditorGUILayout.LabelField(L.T("Collecting information…"), EditorStyles.miniLabel);
                return;
            }

            foreach (var s in _sections)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    // Заголовки и подсказки помечены L.M в ProblemReport.
                    bool on = EditorGUILayout.ToggleLeft(L.T(s.Title), s.Included, GUILayout.Width(220f)); // loc-dynamic
                    EditorGUILayout.LabelField(L.T(s.Hint), EditorStyles.wordWrappedMiniLabel); // loc-dynamic

                    if (on == s.Included) continue;
                    s.Included = on;
                    if (on) _excluded.Remove(s.Id);
                    else if (!_excluded.Contains(s.Id)) _excluded.Add(s.Id);
                }
            }
        }

        private void DrawFiles()
        {
            EditorGUILayout.LabelField(L.T("Files"), EditorStyles.boldLabel);

            int index = Mathf.Max(0, Array.IndexOf(LogDayOptions, _logDays));
            index = EditorGUILayout.Popup(L.C("Logs"), index, L.Cs("Don't Attach", "Last Day", "Last 3 Days", "Last 7 Days"));
            _logDays = LogDayOptions[index];

            _screenshot = EditorGUILayout.ToggleLeft(
                L.C("Screenshot of the Git Window", "The screenshot shows the window as it is: names on it are not hidden."),
                _screenshot);

            for (int i = 0; i < _attachments.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(Path.GetFileName(_attachments[i]), EditorStyles.miniLabel);
                    if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22f)))
                    {
                        _attachments.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L.T("Attach File…"), GUILayout.ExpandWidth(false)))
                {
                    var path = EditorUtility.OpenFilePanel(L.T("Attach File"), GitRepository.ProjectRoot, string.Empty);
                    if (!string.IsNullOrEmpty(path) && !_attachments.Contains(path)) _attachments.Add(path);
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.LabelField(L.T("Attached files go into the report as they are, without hiding anything."),
                                           EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void DrawPreview()
        {
            if (_sections == null) return;

            var o = Options();
            var key = string.Join("|", new[]
            {
                o.Description, o.Steps, o.HidePaths.ToString(), o.HideServers.ToString(), string.Join(",", _excluded.ToArray()),
                string.Join(",", _attachments.ToArray())
            });
            if (key != _previewKey)
            {
                _preview = ProblemReport.RenderMarkdown(_sections, o);
                _previewKey = key;
            }

            _previewScroll = EditorGUILayout.BeginScrollView(_previewScroll, GUILayout.Height(260f));
            var style = EditorStyles.textArea;
            float height = style.CalcHeight(new GUIContent(_preview), position.width - 60f);
            EditorGUILayout.SelectableLabel(_preview, style, GUILayout.Height(height));
            EditorGUILayout.EndScrollView();
        }

        private void DrawFooter()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (!string.IsNullOrEmpty(_status))
                {
                    var style = new GUIStyle(EditorStyles.miniLabel);
                    if (_statusError) style.normal.textColor = GitPalette.Text(GitFileStatus.Conflicted);
                    GUILayout.Label(_status, style);
                }
                GUILayout.FlexibleSpace();

                if (!string.IsNullOrEmpty(_savedPath) && File.Exists(_savedPath) &&
                    GUILayout.Button(L.T("Show in Explorer"), EditorStyles.toolbarButton))
                    EditorUtility.RevealInFinder(_savedPath);

                using (new EditorGUI.DisabledScope(_collecting))
                {
                    if (GUILayout.Button(L.T("Collect Again"), EditorStyles.toolbarButton)) Collect();

                    using (new EditorGUI.DisabledScope(_sections == null))
                    {
                        if (GUILayout.Button(L.T("Copy Summary"), EditorStyles.toolbarButton))
                        {
                            EditorGUIUtility.systemCopyBuffer = ProblemReport.Summary(_sections);
                            _status = L.T("Summary copied");
                            _statusError = false;
                        }

                        if (GUILayout.Button(L.T("Save Report…"), EditorStyles.toolbarButton)) Save();
                    }
                }
            }
        }

        private void Save()
        {
            var path = EditorUtility.SaveFilePanel(L.T("Save Problem Report"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "LevGit-report-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm"), "zip");
            if (string.IsNullOrEmpty(path)) GUIUtility.ExitGUI();

            try
            {
                var o = Options();
                var png = o.Screenshot ? ProblemReport.CaptureGitWindow() : null;
                ProblemReport.SaveZip(path, _sections, o, png);

                _savedPath = path;
                _status = L.F("Saved: {0}", path);
                _statusError = false;
                Journal.Info("Problem report saved");
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception e)
            {
                _status = L.F("The report was not saved: {0}", e.Message);
                _statusError = true;
                Journal.Exception(e, "Problem report");
            }

            GUIUtility.ExitGUI();
        }
    }
}
