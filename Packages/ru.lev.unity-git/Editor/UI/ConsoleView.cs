using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lev.Git.UI
{
    /// <summary>
    /// Журнал выполненных команд и строка своих команд git.
    ///
    /// Та же раскладка, что в остальных разделах: слева список, справа полный
    /// вывод выбранной команды. Разворачивать строки прямо в списке было бы
    /// заманчиво, но тогда у строк разная высота и виртуализация ломается —
    /// а журнал на несколько сотен записей должен листаться без рывков.
    ///
    /// Строка команд — внизу, как у терминала. Введённое попадает в тот же
    /// журнал и сразу выбирается в нём, так что вывод виден справа.
    /// </summary>
    public sealed class ConsoleView : VisualElement
    {
        private const string HistoryKey = "LevGit.Terminal.History";
        private const int HistoryLimit = 50;

        private readonly ListView _list = new ListView();
        private readonly List<GitCommandRecord> _rows = new List<GitCommandRecord>();
        private readonly VisualElement _detail = new VisualElement();
        private readonly Label _timing;
        private readonly ToolbarToggle _background;
        private readonly TextField _input;
        private readonly Button _run;
        private readonly List<string> _history = new List<string>();

        private string _filter = string.Empty;
        private int _historyIndex = -1;
        private bool _running;

        public ConsoleView()
        {
            style.flexGrow = 1f;

            var split = new TwoPaneSplitView(0, 400f, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1f;
            split.style.minHeight = 0f;
            Add(split);

            var left = Ui.Box("pane");
            split.Add(left);

            var bar = Ui.Box("subbar");

            var search = new ToolbarSearchField();
            search.style.flexGrow = 1f;
            search.style.marginRight = 4f;
            search.RegisterValueChangedCallback(e =>
            {
                _filter = e.newValue ?? string.Empty;
                Refresh();
            });
            bar.Add(search);

            _background = new ToolbarToggle
            {
                text = L.T("Background"),
                tooltip = L.T("Status polling that the plugin does by itself, without a user request")
            };
            _background.RegisterValueChangedCallback(_ => Refresh());
            bar.Add(_background);

            bar.Add(Ui.Action(L.T("Clear"), () =>
            {
                GitCommandLog.Clear();
                Refresh();
            }));
            left.Add(bar);

            _list.fixedItemHeight = 20f;
            _list.selectionType = SelectionType.Single;
            _list.makeItem = MakeRow;
            _list.bindItem = BindRow;
            _list.itemsSource = _rows;
            _list.AddToClassList("list");
            _list.selectionChanged += _ => RenderDetail();
            left.Add(_list);

            var foot = Ui.Box("status-line");
            _timing = Ui.Text(string.Empty, "status-line__text");
            foot.Add(_timing);
            left.Add(foot);

            var right = Ui.Box("pane", "pane--detail");
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.minHeight = 0f;
            _detail.AddToClassList("detail");
            scroll.Add(_detail);
            right.Add(scroll);
            split.Add(right);

            // ---- строка команд ----
            var terminal = Ui.Box("subbar");
            terminal.style.flexShrink = 0f;

            var prompt = Ui.Text("git", "kv__k");
            prompt.style.unityFontStyleAndWeight = FontStyle.Bold;
            prompt.style.unityTextAlign = TextAnchor.MiddleLeft;
            prompt.style.marginLeft = 4f;
            prompt.style.marginRight = 2f;
            terminal.Add(prompt);

            _input = new TextField
            {
                tooltip = L.T("Any git command, run in the repository folder. Enter runs it, ↑ and ↓ recall previous commands.")
            };
            _input.style.flexGrow = 1f;
            _input.style.flexShrink = 1f;
            _input.RegisterCallback<KeyDownEvent>(OnInputKey, TrickleDown.TrickleDown);
            terminal.Add(_input);

            _run = Ui.Action(L.T("Run"), Submit, L.T("Run the command (Enter)"), true);
            terminal.Add(_run);
            Add(terminal);

            LoadHistory();
        }

        public void Refresh()
        {
            var records = GitCommandLog.Records;
            bool showBackground = _background.value;

            _rows.Clear();
            for (int i = records.Count - 1; i >= 0; i--)   // свежие сверху
            {
                var r = records[i];
                if (r.Background && !showBackground && !r.Failed) continue;
                if (_filter.Length > 0 &&
                    r.Args.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _rows.Add(r);
            }

            _list.itemsSource = _rows;
            _list.Rebuild();

            _timing.text = L.F(
                "last status: {0} ms · total commands: {1}",
                GitStatusCache.LastRefreshMs, records.Count);

            RenderDetail();
        }

        // ------------------------------------------------------ строка команд ---

        private void OnInputKey(KeyDownEvent e)
        {
            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Submit();
                    e.StopImmediatePropagation();
                    break;
                case KeyCode.UpArrow:
                    Recall(+1);
                    e.StopImmediatePropagation();
                    break;
                case KeyCode.DownArrow:
                    Recall(-1);
                    e.StopImmediatePropagation();
                    break;
            }
        }

        private async void Submit()
        {
            if (_running) return;

            var line = (_input.value ?? string.Empty).Trim();
            var args = GitTerminal.Normalize(line);
            if (args.Length == 0) return;

            if (!GitRepository.IsRepo)
            {
                EditorUtility.DisplayDialog(L.T("Run Git Command"), L.T("The project is not in a git repository."), L.T("OK"));
                return;
            }

            var warning = GitTerminal.Warning(args);
            if (warning != null &&
                !EditorUtility.DisplayDialog(L.T("Run Git Command"), "git " + args + "\n\n" + warning, L.T("Run"), L.T("Cancel")))
                return;

            Remember(line);
            _input.value = string.Empty;
            _historyIndex = -1;

            _running = true;
            _run.SetEnabled(false);
            try
            {
                await GitTerminal.RunAsync(args);
            }
            catch (Exception ex)
            {
                Diagnostics.Journal.Exception(ex, "Git command line");
            }
            finally
            {
                _running = false;
                _run.SetEnabled(true);
            }

            // Только что выполненная команда — первая в списке: выбираем её, вывод справа.
            Refresh();
            if (_rows.Count > 0) _list.SetSelection(0);
            _input.Focus();
        }

        private void Recall(int step)
        {
            if (_history.Count == 0) return;

            _historyIndex = Mathf.Clamp(_historyIndex + step, -1, _history.Count - 1);
            _input.value = _historyIndex < 0 ? string.Empty : _history[_history.Count - 1 - _historyIndex];
            _input.SelectRange(_input.value.Length, _input.value.Length);
        }

        private void Remember(string line)
        {
            _history.Remove(line);
            _history.Add(line);
            if (_history.Count > HistoryLimit) _history.RemoveRange(0, _history.Count - HistoryLimit);
            SessionState.SetString(HistoryKey, string.Join("\n", _history.ToArray()));
        }

        private void LoadHistory()
        {
            _history.Clear();
            foreach (var line in SessionState.GetString(HistoryKey, string.Empty).Split('\n'))
                if (line.Trim().Length > 0) _history.Add(line);
        }

        // ------------------------------------------------------------ список ---

        private VisualElement MakeRow()
        {
            var row = Ui.Box("row");
            row.style.height = 20f;

            var stamp = Ui.Text(string.Empty, "row__dir");
            stamp.style.flexGrow = 0f;
            stamp.style.width = 52f;
            stamp.style.marginLeft = 6f;
            stamp.style.marginRight = 0f;
            row.Add(stamp);

            row.Add(Ui.Text(string.Empty, "row__name"));

            var dur = Ui.Text(string.Empty, "row__dir");
            dur.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(dur);

            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            var r = _rows[index];
            var stamp = (Label)element.ElementAt(0);
            var name = (Label)element.ElementAt(1);
            var dur = (Label)element.ElementAt(2);

            stamp.text = r.Stamp;
            name.text = "git " + r.Args;
            dur.text = L.F("{0} ms", r.DurationMs);

            bool failed = r.Failed;
            name.EnableInClassList("t-error", failed);
            name.EnableInClassList("t-dim", r.Background && !failed);
        }

        private void RenderDetail()
        {
            _detail.Clear();

            var r = _list.selectedItem as GitCommandRecord;
            if (r == null)
            {
                _detail.Add(Ui.Empty(L.T("No Command Selected"),
                    L.T("The full output is shown here. It is the only way to check " +
                        "what the plugin does with the repository. Your own commands go in the line below.")));
                return;
            }

            bool failed = r.Failed;

            _detail.Add(Ui.Text("git " + r.Args, "detail__name"));
            _detail.Add(Ui.Text(r.Cwd, "detail__path"));

            if (failed)
                _detail.Add(Ui.Banner(L.F("The command exited with code {0}", r.ExitCode), "error"));
            else if (!r.Ok)
                _detail.Add(Ui.Banner(L.F("Exit code {0} is an answer for this command, not an error: for example, “setting not found” or “files differ”.", r.ExitCode)));

            var card = Ui.Card(L.T("Run"));
            card.Add(Ui.KeyValue(L.T("Time"), r.Stamp));
            card.Add(Ui.KeyValue(L.T("Duration"), L.F("{0} ms", r.DurationMs)));
            card.Add(Ui.KeyValue(L.T("Exit Code"), r.ExitCode.ToString(), failed ? "t-error" : "t-ok"));
            card.Add(Ui.KeyValue(L.T("Source"), r.Background ? L.T("background polling") : L.T("user action")));
            _detail.Add(card);

            if (!string.IsNullOrWhiteSpace(r.Timeline))
            {
                // Где ушло время: хуки, ssh, упаковка — по трассировке самого git.
                var stages = Ui.Card(L.T("Stages"));
                stages.Add(Ui.Text(L.T("Where the time went, from git's own trace: offset from start, duration, step and exit code."), "kv__k"));
                var stagesText = new TextField { multiline = true, isReadOnly = true, value = r.Timeline.TrimEnd() };
                stagesText.style.maxHeight = 320f;
                stages.Add(stagesText);
                _detail.Add(stages);
            }

            var output = Ui.Card(L.T("Output"));
            if (string.IsNullOrWhiteSpace(r.Output))
            {
                output.Add(Ui.Text(L.T("empty"), "kv__k"));
            }
            else
            {
                // Только для чтения, но с выделением: вывод git часто нужно скопировать.
                var text = new TextField { multiline = true, isReadOnly = true, value = r.Output.Trim() };
                text.style.maxHeight = 320f;
                output.Add(text);
            }
            _detail.Add(output);
        }
    }
}
