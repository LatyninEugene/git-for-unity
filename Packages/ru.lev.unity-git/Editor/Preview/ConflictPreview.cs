using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Lev.Git.Preview
{
    /// <summary>
    /// Вид конфликта ассета: база, мои, их и — если файл сливается по частям —
    /// итог, который пересобирается после каждого выбора стороны. Тот же показ,
    /// что в панели превью: у материала четыре шара, у текстуры четыре картинки.
    ///
    /// Версии достаются из индекса с фильтрами .gitattributes и LFS; мета берётся
    /// из рабочей копии — конфликт в самой мете разбирается отдельной строкой списка.
    /// </summary>
    internal sealed class ConflictPreview : IDisposable
    {
        private readonly ConflictFile _file;
        private readonly Action _repaint;
        private readonly LoadedAsset[] _sides = new LoadedAsset[4];
        private readonly string[] _errors = new string[4];
        private readonly PreviewSync _sync = new PreviewSync();

        private AssetPresenter _presenter;
        private string _meta;
        private bool _loading = true, _resultLoading, _disposed, _dragging;
        private int _resultVersion = -1;

        public static bool Supports(string projectPath)
        {
            var loader = AssetLoaders.Find(projectPath);
            if (loader == null) return false;
            return !(loader is SandboxAssetLoader) || GitSettings.instance.importSandbox == 2;
        }

        public ConflictPreview(ConflictFile file, Action repaint)
        {
            _file = file;
            _repaint = repaint;
            LoadAsync();
        }

        public bool Matches(ConflictFile f)
        {
            return f.BaseBlob == _file.BaseBlob && f.MineBlob == _file.MineBlob && f.TheirsBlob == _file.TheirsBlob;
        }

        private async void LoadAsync()
        {
            var loader = AssetLoaders.Find(_file.ProjectPath);
            var metaBytes = RevisionSide.ReadWorktreeMeta(_file.ProjectPath);
            _meta = metaBytes != null ? Encoding.UTF8.GetString(metaBytes) : null;

            var sides = new[] { MergeSide.Base, MergeSide.Mine, MergeSide.Theirs };

            try
            {
                for (int i = 0; i < sides.Length; i++)
                {
                    if (_file.BlobOf(sides[i]) == null)
                    {
                        _errors[i] = i == 0 ? L.T("the base had no such file") : L.T("file deleted");
                        continue;
                    }

                    var temp = AssetLoaders.TempFile(_file.ProjectPath);
                    try
                    {
                        if (!await GitConflicts.ExportBlobAsync(_file, sides[i], temp))
                        {
                            _errors[i] = L.T("version not read");
                            continue;
                        }

                        var loaded = await AssetLoaders.SafeLoadAsync(loader, _file.ProjectPath, File.ReadAllBytes(temp), _meta);
                        loaded.MetaText = _meta;

                        if (_disposed)
                        {
                            loaded.Dispose();
                            return;
                        }

                        _sides[i] = loaded;
                        _errors[i] = loaded.Error;
                    }
                    finally
                    {
                        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    }
                }

                _presenter = AssetPresenters.Create(_sides[1] ?? _sides[2] ?? _sides[0]);
                if (_presenter != null) _presenter.Prepare(_sides[1], _sides[2]);
            }
            catch (Exception e)
            {
                _errors[1] = L.F("failed to load: {0}", e.Message);
            }
            finally
            {
                _loading = false;
                _repaint();
            }
        }

        /// <summary>Итог слияния текстом. Пересобирается, только когда выбор сторон поменялся.</summary>
        public async void SetResult(string text, int version)
        {
            if (text == null || version == _resultVersion || _resultLoading || _loading || _disposed) return;

            var loader = AssetLoaders.Find(_file.ProjectPath);
            if (!(loader is YamlAssetLoader) && !(loader is PrefabAssetLoader)) return;

            _resultVersion = version;
            _resultLoading = true;

            try
            {
                var loaded = await AssetLoaders.SafeLoadAsync(loader, _file.ProjectPath, Encoding.UTF8.GetBytes(text), _meta);
                loaded.MetaText = _meta;

                if (_disposed)
                {
                    loaded.Dispose();
                    return;
                }

                if (_sides[3] != null) _sides[3].Dispose();
                _sides[3] = loaded;
                _errors[3] = loaded.Error;
            }
            finally
            {
                _resultLoading = false;
                _repaint();
            }
        }

        public void Draw(Rect rect, string mineLabel, string theirsLabel)
        {
            if (_loading)
            {
                GUI.Label(rect, L.T("Loading three versions…"), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            if (_presenter == null)
            {
                GUI.Label(rect, _errors[1] != null ? L.F("Nothing can show these versions: {0}", _errors[1]) : L.T("Nothing can show these versions"), EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var captions = new[] { L.T("Base"), mineLabel, theirsLabel, L.Tc("merge preview", "Result") };
            int count = _sides[3] != null || _resultLoading ? 4 : 3;

            HandleInput(rect);
            if (Event.current.type != EventType.Repaint) return;

            const float gap = 4f;
            float width = (rect.width - gap * (count - 1)) / count;
            var features = _presenter.Features;

            for (int i = 0; i < count; i++)
            {
                var cell = new Rect(rect.x + i * (width + gap), rect.y, width, rect.height);
                PreviewBackground.Fill(cell);

                var side = _sides[i];
                if (side == null || side.Main == null || !_presenter.CanPresent(side))
                {
                    var text = i == 3 && _resultLoading ? L.T("Building the result…") : _errors[i] ?? L.T("nothing to show it with");
                    GUI.Label(new RectOffset(6, 6, 18, 6).Remove(cell), text, new GUIStyle(EditorStyles.centeredGreyMiniLabel) { wordWrap = true });
                }
                else
                {
                    var texture = _presenter.Render(cell, side, i, _sync);
                    if (texture != null)
                    {
                        var fit = PreviewCanvas.Fit(cell, _presenter.Aspect(side));
                        var source = PreviewCanvas.Source(_sync, (features & PresenterFeatures.Zoom) != 0);
                        PreviewCanvas.Draw(fit, texture, source, _sync, (features & PresenterFeatures.Channels) != 0, null, false, 1f);

                        var other = i == 2 ? _sides[1] : i == 0 ? null : _sides[2];
                        _presenter.DrawOverlay(fit, source, side, other, i != 0, _sync);
                    }
                }

                var style = EditorStyles.miniBoldLabel;
                var size = style.CalcSize(new GUIContent(captions[i]));
                var box = new Rect(cell.x + 4f, cell.y + 3f, size.x + 4f, size.y);
                EditorGUI.DrawRect(box, new Color(0f, 0f, 0f, 0.35f));
                GUI.Label(new Rect(box.x + 2f, box.y, size.x, size.y), captions[i], style);
            }

            if (_presenter.NeedsRepaint) _repaint();
        }

        private void HandleInput(Rect rect)
        {
            var e = Event.current;
            if (_presenter == null) return;

            switch (e.type)
            {
                case EventType.ScrollWheel:
                    if (!rect.Contains(e.mousePosition)) break;
                    if ((_presenter.Features & PresenterFeatures.Zoom) != 0)
                        _sync.Zoom = Mathf.Clamp(_sync.Zoom * (e.delta.y < 0f ? 1.25f : 0.8f), 1f, 64f);
                    else if (_presenter.Interactive)
                        _sync.Dolly = Mathf.Clamp(_sync.Dolly * (e.delta.y < 0f ? 0.9f : 1.1f), 0.15f, 6f);
                    else break;
                    e.Use();
                    _repaint();
                    break;

                case EventType.MouseDown:
                    if (e.button != 0 || !rect.Contains(e.mousePosition) || !_presenter.Interactive) break;
                    if (e.clickCount == 2)
                    {
                        _sync.Dolly = 1f;
                        _sync.Zoom = 1f;
                        _sync.Offset = Vector2.zero;
                    }
                    _dragging = true;
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (!_dragging) break;
                    _sync.Orbit += e.delta * 0.6f;
                    _sync.Orbit.y = Mathf.Clamp(_sync.Orbit.y, -89f, 89f);
                    e.Use();
                    _repaint();
                    break;

                case EventType.MouseUp:
                    _dragging = false;
                    break;
            }
        }

        public void Dispose()
        {
            _disposed = true;

            if (_presenter != null)
            {
                _presenter.Stop();
                _presenter.Dispose();
                _presenter = null;
            }

            for (int i = 0; i < _sides.Length; i++)
            {
                if (_sides[i] != null) _sides[i].Dispose();
                _sides[i] = null;
            }
        }
    }
}
