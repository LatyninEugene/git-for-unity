using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.GitLab
{
    /// <summary>
    /// Новая задача со снимком окна Game или Scene.
    ///
    /// Снимок снимается рендером камер в текстуру, а не чтением пикселей
    /// экрана: так в кадр не попадают другие окна, а масштаб экрана и
    /// несколько мониторов ничего не ломают. Цена — в снимок Game не
    /// попадает интерфейс Canvas в режиме Screen Space Overlay.
    /// </summary>
    public sealed class CreateIssueWindow : EditorWindow, ILocalizedWindow
    {
        private static string[] SourceNames => L.Ts("Game View", "Scene View", "No Screenshot");

        private string _title = string.Empty;
        private string _description = string.Empty;
        private bool _confidential;
        private readonly List<GlUserRef> _assignees = new List<GlUserRef>();
        private readonly List<string> _labels = new List<string>();

        private List<GlUserRef> _members = new List<GlUserRef>();
        private List<string> _projectLabels = new List<string>();
        private GlUserRef _me;

        private int _source;
        private Texture2D _shot;
        private string _shotProblem;

        private bool _loading = true, _busy;
        private string _status;
        private bool _statusError;
        private Vector2 _scroll;

        public static void Open()
        {
            var w = GetWindow<CreateIssueWindow>(true, L.T("New Issue"), true);
            w.minSize = new Vector2(560f, 600f);
            w._source = Mathf.Clamp(GitLabSettings.instance.issueScreenshot, 0, SourceNames.Length - 1);
            w.Show();
            w.Capture();
            w.LoadAsync();
        }

        void ILocalizedWindow.OnLanguageChanged()
        {
            titleContent = new GUIContent(L.T("New Issue"));
            Repaint();
        }

        private void OnDestroy()
        {
            ReleaseShot();
        }

        private GitLabClient Client()
        {
            var baseUrl = GitLabInstance.EffectiveBaseUrl;
            var token = GitLabToken.Read(baseUrl);
            return string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(GitLabInstance.EffectiveEncodedId)
                ? null : new GitLabClient(baseUrl, token);
        }

        private async void LoadAsync()
        {
            _loading = true;
            var client = Client();
            if (client == null)
            {
                SetStatus(L.T("API token or project address is not set — Project Settings → Git → GitLab."), true);
                _loading = false;
                return;
            }

            try
            {
                var encoded = GitLabInstance.EffectiveEncodedId;
                var membersTask = client.MembersAsync(encoded);
                var labelsTask = client.LabelsAsync(encoded);
                var meTask = client.CurrentUserAsync();

                _members = (await membersTask).users;
                _projectLabels = (await labelsTask).labels;
                _me = await meTask;
            }
            catch (Exception e)
            {
                SetStatus(e.Message, true);
            }
            finally
            {
                _loading = false;
                Repaint();
            }
        }

        // ------------------------------------------------------------- снимок ---

        private void Capture()
        {
            ReleaseShot();
            _shotProblem = null;

            switch (_source)
            {
                case 0: _shot = EditorSnapshot.Game(out _shotProblem); break;
                case 1: _shot = EditorSnapshot.Scene(out _shotProblem); break;
            }
            Repaint();
        }

        private void ReleaseShot()
        {
            if (_shot != null) DestroyImmediate(_shot);
            _shot = null;
        }

        // -------------------------------------------------------------- форма ---

        private void OnGUI()
        {
            EditorGUIUtility.labelWidth = 120f;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUI.DisabledScope(_busy))
            {
                EditorGUILayout.Space(6f);
                _title = EditorGUILayout.TextField(L.T("Title"), _title);

                EditorGUILayout.LabelField(L.T("Description"));
                _description = EditorGUILayout.TextArea(_description, GUILayout.MinHeight(120f));

                EditorGUILayout.Space(6f);
                using (new EditorGUI.DisabledScope(_loading))
                {
                    Row(L.T("Assignee"), _assignees.Count == 0 ? L.Tc("issue", "unassigned") : string.Join(", ", _assignees.ConvertAll(u => u.ToString())), AssigneeMenu);
                    Row(L.T("Labels"), _labels.Count == 0 ? L.T("none") : string.Join(", ", _labels), LabelsMenu);
                }
                _confidential = EditorGUILayout.Toggle(L.C("Confidential", "Visible only to project members with Reporter access or higher"), _confidential);

                // ---- снимок ----
                EditorGUILayout.Space(8f);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    _source = EditorGUILayout.Popup(L.T("Screenshot"), _source, SourceNames);
                    if (EditorGUI.EndChangeCheck()) Capture();

                    using (new EditorGUI.DisabledScope(_source == 2))
                        if (GUILayout.Button(L.T("Capture Again"), EditorStyles.miniButton, GUILayout.Width(100f))) Capture();
                }

                if (_shot != null)
                {
                    var rect = GUILayoutUtility.GetRect(10f, 10000f, 140f, 220f);
                    EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.25f));
                    GUI.DrawTexture(rect, _shot, ScaleMode.ScaleToFit, false);
                    EditorGUILayout.LabelField(" ", L.F("{0} × {1} px · will be attached to the description", _shot.width, _shot.height), EditorStyles.miniLabel);
                }
                else if (!string.IsNullOrEmpty(_shotProblem))
                {
                    EditorGUILayout.HelpBox(_shotProblem, MessageType.Warning);
                }
                else if (_source == 0)
                {
                    EditorGUILayout.LabelField(" ", L.T("Canvas UI in Screen Space Overlay mode is not included in the screenshot."), EditorStyles.wordWrappedMiniLabel);
                }
            }
            EditorGUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, _statusError ? MessageType.Error : MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(L.T("Cancel"), GUILayout.Width(90f))) Close();
                using (new EditorGUI.DisabledScope(_busy || string.IsNullOrWhiteSpace(_title)))
                    if (GUILayout.Button(_busy ? L.T("Creating…") : L.T("Create"), GUILayout.Width(110f))) Create();
            }
            EditorGUILayout.Space(6f);
        }

        private static void Row(string label, string value, Action<Rect> menu)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, value);
                var change = L.C("Change ▾");
                var rect = GUILayoutUtility.GetRect(change, EditorStyles.miniButton, GUILayout.Width(90f));
                if (GUI.Button(rect, change, EditorStyles.miniButton)) menu(rect);
            }
        }

        private static string M(string s) { return (s ?? string.Empty).Replace('/', '∕'); }

        private void AssigneeMenu(Rect rect)
        {
            var menu = new GenericMenu();
            if (_me != null)
            {
                var me = _members.Find(u => u.Id == _me.Id) ?? _me;
                menu.AddItem(new GUIContent(L.T("Assign to Me")), _assignees.Exists(u => u.Id == me.Id), () => Toggle(me));
                menu.AddSeparator(string.Empty);
            }
            foreach (var u in _members)
            {
                var user = u;
                menu.AddItem(new GUIContent(M(user.ToString()) + "  @" + user.Username), _assignees.Exists(a => a.Id == user.Id), () => Toggle(user));
            }
            if (_members.Count == 0) menu.AddDisabledItem(new GUIContent(L.T("Members not loaded")));
            menu.DropDown(rect);
        }

        private void Toggle(GlUserRef user)
        {
            if (_assignees.RemoveAll(a => a.Id == user.Id) == 0) _assignees.Add(user);
        }

        private void LabelsMenu(Rect rect)
        {
            var menu = new GenericMenu();
            foreach (var l in _projectLabels)
            {
                var label = l;
                bool on = _labels.Contains(label);
                menu.AddItem(new GUIContent(M(label)), on, () => { if (on) _labels.Remove(label); else _labels.Add(label); });
            }
            if (_projectLabels.Count == 0) menu.AddDisabledItem(new GUIContent(L.T("The project has no labels")));
            menu.DropDown(rect);
        }

        private async void Create()
        {
            var client = Client();
            if (client == null) return;

            _busy = true;
            SetStatus(null, false);

            try
            {
                var encoded = GitLabInstance.EffectiveEncodedId;
                var description = _description ?? string.Empty;

                if (_shot != null)
                {
                    SetStatus(L.T("Uploading screenshot…"), false);
                    var png = _shot.EncodeToPNG();
                    var fileName = (_source == 1 ? "scene" : "game") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
                    var (markdown, uploadError) = await client.UploadFileAsync(encoded, fileName, "image/png", png);
                    if (markdown == null) { SetStatus(uploadError + "\n" + L.T("You can choose “No Screenshot” and create the issue without it."), true); return; }
                    description = description.TrimEnd() + (description.Trim().Length > 0 ? "\n\n" : string.Empty) + markdown;
                }

                SetStatus(L.T("Creating issue…"), false);
                var ids = _assignees.ConvertAll(u => u.Id);
                var (issue, error) = await client.CreateIssueAsync(encoded, _title.Trim(), description, ids, _labels, _confidential);
                if (issue == null) { SetStatus(error, true); return; }

                Diagnostics.Journal.Notice(L.F("Created issue #{0}: {1}", issue.Iid, issue.Title));
                GitLabEvents.RaiseIssuesChanged(issue.Iid);
                Close();
            }
            catch (Exception e)
            {
                SetStatus(e.Message, true);
            }
            finally
            {
                _busy = false;
                Repaint();
            }
        }

        private void SetStatus(string text, bool error)
        {
            _status = text;
            _statusError = error;
            Repaint();
        }
    }

    /// <summary>Снимки окон Game и Scene рендером камер в текстуру.</summary>
    internal static class EditorSnapshot
    {
        private const int MaxSide = 2560;

        public static Texture2D Game(out string problem)
        {
            problem = null;

            var cameras = new List<Camera>();
            foreach (var c in Camera.allCameras)
                if (c != null && c.cameraType == CameraType.Game && c.targetTexture == null) cameras.Add(c);

            if (cameras.Count == 0)
            {
                problem = L.T("There is no enabled camera in the open scenes — nothing to take a Game view screenshot from.");
                return null;
            }

            cameras.Sort((a, b) => a.depth.CompareTo(b.depth));
            var size = Handles.GetMainGameViewSize();
            return Render(cameras, (int)size.x, (int)size.y, out problem);
        }

        public static Texture2D Scene(out string problem)
        {
            problem = null;
            var view = SceneView.lastActiveSceneView;
            if (view == null || view.camera == null)
            {
                problem = L.T("The Scene view is not open.");
                return null;
            }

            var cam = view.camera;
            return Render(new List<Camera> { cam }, cam.pixelWidth, cam.pixelHeight, out problem);
        }

        private static Texture2D Render(List<Camera> cameras, int width, int height, out string problem)
        {
            problem = null;

            // Большой экран сжимаем пропорционально: снимок нужен понять, что на нём, а не для печати.
            if (width < 16 || height < 16) { width = 1280; height = 720; }
            float scale = Mathf.Min(1f, (float)MaxSide / Mathf.Max(width, height));
            width = Mathf.Max(16, Mathf.RoundToInt(width * scale));
            height = Mathf.Max(16, Mathf.RoundToInt(height * scale));

            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);

                foreach (var cam in cameras)
                {
                    var old = cam.targetTexture;
                    cam.targetTexture = rt;
                    try { cam.Render(); }
                    finally { cam.targetTexture = old; }
                }

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                return tex;
            }
            catch (Exception e)
            {
                problem = L.F("Screenshot failed: {0}", e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
