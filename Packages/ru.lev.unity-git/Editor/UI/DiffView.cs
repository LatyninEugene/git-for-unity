using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine.UIElements;

namespace Lev.Git.UI
{
    /// <summary>Строка diff для интеграций: куда привязать комментарий.</summary>
    public sealed class DiffLineRef
    {
        public string GitPath;
        public string OldGitPath;

        /// <summary>0 — у строки нет номера на этой стороне (добавленная или удалённая).</summary>
        public int OldLine, NewLine;
        public DiffLineKind Kind;
        public string Text;
    }

    /// <summary>Объект или компонент сцены в diff — для комментария к объекту.</summary>
    public sealed class DiffObjectRef
    {
        public string GitPath;
        public string OldGitPath;
        public string ProjectPath;
        public long FileId;
        public string Title;

        /// <summary>Объект, на котором висит компонент; null — это сам объект.</summary>
        public string OwnerTitle;
        public bool Added, Removed;
    }

    /// <summary>
    /// Изменения файла с отметкой фрагментов, которые войдут в коммит.
    ///
    /// Показывается ОДИН diff — рабочая копия против последнего коммита.
    /// Индекса здесь нет: он не состояние, которое пользователь должен держать
    /// в голове, а способ, которым коммит собирается в последний момент.
    ///
    /// Строки живут в виртуализированном списке: diff сцены — это десятки тысяч
    /// строк, и рисовать их все разом нельзя.
    /// </summary>
    public sealed class DiffView : VisualElement
    {
        private sealed class Row
        {
            public DiffHunk Hunk;      // не null у заголовка фрагмента
            public DiffLine Line;      // не null у строки
            public DiffHunk OwnerHunk; // фрагмент, которому принадлежит строка
            public int LineIndex;      // номер строки внутри фрагмента

            public SceneNode Node;             // не null у объекта или компонента сцены
            public SceneNode OwnerNode;        // объект, которому принадлежит компонент
            public ScenePropertyChange Prop;   // не null у изменившегося свойства
            public int Depth;                  // вложенность в дереве объектов

            public SceneNode PropNode;         // узел, которому принадлежит свойство

            // ---- внутри экземпляра префаба ----
            public SceneNode Instance;                 // документ PrefabInstance, внутри которого строка
            public string TargetKey;                   // «guid|fileId» объекта или компонента в файле префаба
            public UnityEngine.Object TargetAsset;     // он же, загруженный, — для имени и иконки
            public List<string> TargetKeys;            // все цели под строкой объекта — для отката целиком
            public PrefabModificationChange Mod;       // переопределение у строки свойства
            public string Detail;                      // суть правок для свёрнутой строки
            public bool Placeholder;                   // объект сам не менялся — строка ради его детей
            public string GroupName;           // имя объекта в дереве; у промежуточного узла без своих правок Node == null
            public string FoldKey;             // ключ свёрнутости; null — строка не сворачивается
            public bool SettingsGroup;         // заголовок группы «Настройки сцены»
        }

        /// <summary>Узел дерева объектов по пути в иерархии.</summary>
        private sealed class SceneGroup
        {
            public string Name, Path;
            public SceneNode Node;
            public SceneGroup Parent;
            public readonly List<SceneGroup> Children = new List<SceneGroup>();
        }

        private sealed class RowWidgets
        {
            public VisualElement HunkBox, LineBox;
            public TriCheck Check, LineCheck;
            public Label HunkTitle, HunkStats, Old, New;
            public VisualElement TextBox;
            public Label TextPre, TextMark, TextPost;
            public Button Discard;

            public VisualElement SceneBox, PropBox;
            public Label SceneSign, SceneTitle, SceneNote;
            public Label PropPath, PropOld, PropArrow, PropNew;
            public VisualElement PropOldSwatch, PropNewSwatch;
            public Image PropOldIcon, PropNewIcon;
            public Label LineNote, SceneComments;
            public Label SceneArrow, SceneDetail;
            public Image SceneIcon;

            public Row Bound;
        }

        private readonly IGitHost _host;
        private readonly ListView _list = new ListView();
        private readonly List<Row> _rows = new List<Row>();
        private readonly Label _stats;
        private readonly VisualElement _empty;

        private GitChange _change;

        /// <summary>Показан файл из коммита, а не из рабочей копии: только чтение.</summary>
        private bool _revision;

        /// <summary>Путь в проекте — по нему ищутся объекты сцены в обоих режимах.</summary>
        private string _projectPath;

        /// <summary>Отпечаток показанного: пока он тот же, перечитывать нечего.</summary>
        private string _key;
        private FileDiff _diff;
        private bool _selectable;
        private bool _lineLevel;

        private List<SceneNode> _sceneDiff;
        private bool _semanticAvailable;
        private bool _preferText;          // выбор пользователя переживает смену файла
        private Button _modeObjects, _modeText, _objectsHistory;

        /// <summary>Коммит показанного файла — с него открывается история объектов; null — рабочая копия.</summary>
        private string _historySha;
        private Label _sceneSummary;

        /// <summary>Вкладки «Объекты / Текст» рисует панель превью над diff: свои кнопки прячутся.</summary>
        private bool _externalModes;

        public bool ExternalModeBar
        {
            get { return _externalModes; }
            set
            {
                if (_externalModes == value) return;
                _externalModes = value;
                UpdateModeButtons();
            }
        }

        /// <summary>Есть ли вид «Объекты» у показанного файла. Известно, когда файл дочитан.</summary>
        public bool SemanticAvailable => _semanticAvailable;

        public bool TextMode => _preferText || !_semanticAvailable;

        /// <summary>Файл дочитан или сброшен — набор доступных видов мог смениться.</summary>
        public event Action ModesChanged;

        /// <summary>Переход к строке (true) или объекту (false) — внешним вкладкам пора переключиться.</summary>
        public event Action<bool> ModeRequested;

        public void SetTextMode(bool text)
        {
            SetMode(text);
        }

        private void UpdateModeButtons()
        {
            bool semantic = _semanticAvailable && !_preferText;
            var modeDisplay = _semanticAvailable && !_externalModes ? DisplayStyle.Flex : DisplayStyle.None;
            _modeObjects.style.display = modeDisplay;
            _modeText.style.display = modeDisplay;
            _modeObjects.EnableInClassList("act--primary", semantic);
            _modeText.EnableInClassList("act--primary", !semantic);
        }

        /// <summary>Отмечена ли строка фрагмента. Состояние живёт в списке изменений.</summary>
        public Func<GitChange, DiffHunk, int, bool> IsLineSelected;

        /// <summary>Отметить или снять одну строку.</summary>
        public Action<GitChange, DiffHunk, int, bool> SetLineSelected;

        /// <summary>Состояние фрагмента целиком: все, часть или ничего.</summary>
        public Func<GitChange, DiffHunk, CheckState> GetHunkState;

        /// <summary>Отметить или снять фрагмент целиком.</summary>
        public Action<GitChange, DiffHunk, bool> SetHunkSelected;

        /// <summary>Файл на диске изменился — списку изменений пора перечитать статус.</summary>
        public event Action FileChanged;

        // ---- точки расширения для ревью: интеграция добавляет пункты меню и пометки ----

        /// <summary>Дополнить контекстное меню строки diff.</summary>
        public Action<DropdownMenu, DiffLineRef> LineMenu;

        /// <summary>Дополнить контекстное меню объекта или компонента сцены.</summary>
        public Action<DropdownMenu, DiffObjectRef> ObjectMenu;

        /// <summary>Пометка у строки — например, «💬 2». null или пусто — без пометки.</summary>
        public Func<DiffLineRef, string> LineNote;
        public Func<DiffObjectRef, string> ObjectNote;

        public Action<DiffLineRef> LineNoteClicked;
        public Action<DiffObjectRef> ObjectNoteClicked;

        /// <summary>Путь показанного файла в репозитории; null — ничего не показано.</summary>
        public string CurrentGitPath => _gitPath;

        private string _gitPath, _oldGitPath;

        /// <summary>К какой строке прокрутить, когда файл дочитается.</summary>
        private Func<Row, bool> _reveal;
        private string _revealPath;

        /// <summary>Отпечаток, для которого построены строки: пока файл дочитывается, он старый.</summary>
        private string _rowsKey;

        /// <summary>Свёрнутые объекты и компоненты. Ключ — путь или fileID: переживает смену файла.</summary>
        private readonly HashSet<string> _folded = new HashSet<string>(StringComparer.Ordinal);

        public DiffView(IGitHost host)
        {
            _host = host;
            AddToClassList("diff");
            style.flexGrow = 1f;
            style.minHeight = 0f;

            var bar = Ui.Box("subbar");

            _modeObjects = Ui.Action(L.T("Objects"), () => SetMode(false),
                L.T("What changed in the scene: objects, components, properties"));
            _modeText = Ui.Action(L.T("Text"), () => SetMode(true),
                L.T("Plain line-by-line comparison"));
            bar.Add(_modeObjects);
            bar.Add(_modeText);

            _sceneSummary = Ui.Text(string.Empty, "subbar__label");
            bar.Add(_sceneSummary);

            bar.Add(Ui.Spacer());
            _stats = Ui.Text(string.Empty, "diff__stats");
            bar.Add(_stats);

            _objectsHistory = Ui.Action(L.T("Object History"), OpenObjectsHistory,
                L.T("How the objects of this scene or prefab changed commit by commit — the timeline opens at this commit"));
            _objectsHistory.style.display = DisplayStyle.None;
            bar.Add(_objectsHistory);
            Add(bar);

            _list.fixedItemHeight = 18f;
            _list.selectionType = SelectionType.None;
            _list.makeItem = MakeRow;
            _list.bindItem = BindRow;
            _list.itemsSource = _rows;
            _list.AddToClassList("list");
            Add(_list);

            _empty = Ui.Empty(string.Empty, null);
            _empty.style.display = DisplayStyle.None;
            Add(_empty);
        }

        // ------------------------------------------------------------ показ ---

        public void Show(GitChange change)
        {
            if (change == null)
            {
                _key = null;
                _change = null;
                _revision = false;
                _projectPath = null;
                _diff = null;
                _rows.Clear();
                _list.Rebuild();
                return;
            }

            // Список изменений перечитывается каждые пару секунд и каждый раз
            // создаёт новые GitChange. Сравнение по ссылке считало бы это сменой
            // файла и заново гоняло бы git diff, а для сцены ещё и git show с
            // разбором всего YAML — вместе со сбросом прокрутки и раскрытых узлов.
            var key = KeyOf(change);

            // Ссылку обновляем всегда: на неё смотрят обработчики флажков, и она
            // обязана указывать на объект из свежего списка.
            _change = change;
            _revision = false;
            _projectPath = change.ProjectPath;
            _gitPath = GitRepository.ToGitPath(change.ProjectPath);
            _oldGitPath = null;
            _historySha = null;

            if (key == _key) { _list.RefreshItems(); return; }

            _key = key;
            Reload(key);
        }

        /// <summary>
        /// Файл внутри коммита: тот же вид, что и для рабочей копии, — объекты
        /// сцены, подсветка правок внутри строки, — но только для чтения:
        /// отмечать и откатывать в прошлом нечего.
        /// </summary>
        public void ShowRevision(GitCommit commit, GitCommitFile file)
        {
            if (commit == null || file == null) { ShowMessage(L.T("No file selected"), null); return; }

            var key = "rev|" + commit.Sha + "|" + file.GitPath;

            _change = null;
            _revision = true;
            _projectPath = file.ProjectPath;
            _gitPath = file.GitPath;
            _oldGitPath = file.OriginalGitPath;
            _historySha = commit.Sha;

            if (key == _key) return;

            _key = key;
            ReloadRevision(key, commit, file);
        }

        /// <summary>
        /// Файл между двумя коммитами — как изменения merge request'а от точки
        /// ответвления до вершины. Вид тот же, что у коммита: объекты сцены,
        /// подсветка внутри строки, только для чтения.
        /// </summary>
        public void ShowRange(string baseSha, string headSha, GitCommitFile file)
        {
            if (string.IsNullOrEmpty(baseSha) || string.IsNullOrEmpty(headSha) || file == null)
            {
                ShowMessage(L.T("No file selected"), null);
                return;
            }

            var key = "range|" + baseSha + "|" + headSha + "|" + file.GitPath;

            _change = null;
            _revision = true;
            _projectPath = file.ProjectPath;
            _gitPath = file.GitPath;
            _oldGitPath = file.OriginalGitPath;
            _historySha = headSha;

            if (key == _key) { ApplyReveal(); return; }

            _key = key;
            ReloadRange(key, baseSha, headSha, file);
        }

        /// <summary>
        /// Любые две версии файла — для «Истории ассета»: коммит с коммитом или
        /// коммит с рабочей копией. null у стороны — файла на ней нет. Только чтение.
        /// </summary>
        internal void ShowCompare(string projectPath, Lev.Git.Preview.RevisionSide before, Lev.Git.Preview.RevisionSide after)
        {
            if (string.IsNullOrEmpty(projectPath) || (before == null && after == null))
            {
                ShowMessage(L.T("No version selected"), null);
                return;
            }

            bool worktree = after != null && after.Kind == Lev.Git.Preview.SideKind.Worktree;
            var key = "cmp|" + projectPath + "|" + (before != null ? before.Key : "-") + "|" +
                      (after != null ? after.Key : "-") + (worktree ? "|" + GitRepository.WorktreeStamp(projectPath) : string.Empty);

            _change = null;
            _revision = true;
            _projectPath = projectPath;
            _gitPath = GitRepository.ToGitPath(projectPath);
            _oldGitPath = null;
            _historySha = after != null && after.Kind == Lev.Git.Preview.SideKind.Commit ? after.Sha
                        : before != null && before.Kind == Lev.Git.Preview.SideKind.Commit ? before.Sha : null;

            if (key == _key) { ApplyReveal(); return; }

            _key = key;
            ReloadCompare(key, before, after);
        }

        private static string RevOf(Lev.Git.Preview.RevisionSide side)
        {
            return side.Kind == Lev.Git.Preview.SideKind.Head ? "HEAD" : side.Sha;
        }

        private async void ReloadCompare(string key, Lev.Git.Preview.RevisionSide before, Lev.Git.Preview.RevisionSide after)
        {
            var gitPath = _gitPath;
            bool worktree = after != null && after.Kind == Lev.Git.Preview.SideKind.Worktree;

            FileDiff diff;
            if (worktree)
                diff = await GitHistory.WorktreeDiffAsync(before != null ? RevOf(before) : GitHistory.EmptyTree, gitPath);
            else
                diff = await GitHistory.RangeDiffAsync(before != null ? RevOf(before) : GitHistory.EmptyTree,
                                                       after != null ? RevOf(after) : GitHistory.EmptyTree, gitPath);
            if (key != _key) return;

            if (diff != null && !diff.IsBinary) GitPatchBuilder.MarkIntraLineChanges(diff);

            _diff = diff;
            _selectable = false;
            _lineLevel = false;
            _sceneDiff = null;
            _semanticAvailable = false;

            if (diff != null && !diff.IsBinary && IsUnityYamlAsset(gitPath))
            {
                var oldText = before != null ? await GitHistory.ShowTextAsync(RevOf(before), gitPath) : string.Empty;
                var newText = after == null ? string.Empty
                            : worktree ? GitOperations.ReadWorktreeText(gitPath)
                            : await GitHistory.ShowTextAsync(RevOf(after), gitPath);

                if (key != _key) return;
                TryBuildSceneDiff(oldText ?? string.Empty, newText ?? string.Empty);
            }

            BuildRows();
        }

        private async void ReloadRange(string key, string baseSha, string headSha, GitCommitFile file)
        {
            var diff = await GitHistory.RangeDiffAsync(baseSha, headSha, file.GitPath, file.OriginalGitPath);
            if (key != _key) return;

            if (diff != null && !diff.IsBinary) GitPatchBuilder.MarkIntraLineChanges(diff);

            _diff = diff;
            _selectable = false;
            _lineLevel = false;
            _sceneDiff = null;
            _semanticAvailable = false;

            if (diff != null && !diff.IsBinary && IsUnityYamlAsset(file.GitPath))
            {
                var oldText = file.Status != GitCommitFileStatus.Added
                    ? await GitHistory.ShowTextAsync(baseSha, file.OriginalGitPath ?? file.GitPath)
                    : string.Empty;

                var newText = file.Status != GitCommitFileStatus.Deleted
                    ? await GitHistory.ShowTextAsync(headSha, file.GitPath)
                    : string.Empty;

                if (key != _key) return;
                TryBuildSceneDiff(oldText ?? string.Empty, newText ?? string.Empty);
            }

            BuildRows();
        }

        /// <summary>Пустое состояние с пояснением — когда показывать нечего.</summary>
        public void ShowMessage(string title, string hint)
        {
            _key = null;
            _change = null;
            _projectPath = null;
            _gitPath = _oldGitPath = null;
            _diff = null;
            _sceneDiff = null;
            _semanticAvailable = false;

            _rows.Clear();
            _list.Rebuild();
            _list.style.display = DisplayStyle.None;

            _modeObjects.style.display = DisplayStyle.None;
            _modeText.style.display = DisplayStyle.None;
            _objectsHistory.style.display = DisplayStyle.None;
            _sceneSummary.text = string.Empty;
            _stats.text = string.Empty;

            SetEmptyText(title, hint);
            _empty.style.display = DisplayStyle.Flex;

            if (ModesChanged != null) ModesChanged();
        }

        private async void ReloadRevision(string key, GitCommit commit, GitCommitFile file)
        {
            var diff = await GitHistory.DiffAsync(commit.Sha, file.GitPath, file.OriginalGitPath);
            if (key != _key) return;

            if (diff != null && !diff.IsBinary) GitPatchBuilder.MarkIntraLineChanges(diff);

            _diff = diff;
            _selectable = false;
            _lineLevel = false;
            _sceneDiff = null;
            _semanticAvailable = false;

            if (diff != null && !diff.IsBinary && IsUnityYamlAsset(file.GitPath))
            {
                // Сравниваем с первым родителем — ровно с тем, от чего считается
                // патч коммита (diff-tree --first-parent).
                var parent = commit.Parents.Length > 0 ? commit.Parents[0] : null;

                var oldText = parent != null && file.Status != GitCommitFileStatus.Added
                    ? await GitHistory.ShowTextAsync(parent, file.OriginalGitPath ?? file.GitPath)
                    : string.Empty;

                var newText = file.Status != GitCommitFileStatus.Deleted
                    ? await GitHistory.ShowTextAsync(commit.Sha, file.GitPath)
                    : string.Empty;

                if (key != _key) return;
                TryBuildSceneDiff(oldText ?? string.Empty, newText ?? string.Empty);
            }

            BuildRows();
        }

        /// <summary>
        /// Что делает показанный diff устаревшим: сам файл, его состояние в
        /// индексе, коммит под HEAD и содержимое рабочей копии. Настройка
        /// построчного выбора тоже здесь — иначе переключение в Project Settings
        /// не было бы видно до выбора другого файла.
        /// </summary>
        private static string KeyOf(GitChange c)
        {
            return c.ProjectPath + "|" + (int)c.IndexStatus + ":" + (int)c.WorkStatus +
                   "|" + (GitStatusCache.HeadOid ?? string.Empty) +
                   "|" + GitRepository.WorktreeStamp(c.ProjectPath) +
                   "|" + (GitSettings.instance.lineLevelSelection ? 1 : 0);
        }

        /// <summary>Перерисовать флажки, не перечитывая diff.</summary>
        public void RefreshChecks()
        {
            _list.RefreshItems();
        }

        private async void Reload(string key)
        {
            var change = _change;
            if (change == null) return;

            var diff = await GitOperations.DiffAsync(change);

            // Пока ходили за diff, пользователь мог выбрать другой файл.
            if (key != _key) return;

            _diff = diff;

            // По фрагментам можно отмечать только там, где есть применимый патч:
            // у бинарников его нет, у неотслеживаемых он снят через --no-index
            // и к индексу не применяется. Такие файлы идут целиком.
            // Мета без ассета частями не собирается: собранное содержимое пишется
            // по пути записи, а у неё это путь ассета или каталога, а не меты.
            _selectable = diff != null && !diff.IsBinary &&
                          change.WorkStatus != GitFileStatus.Untracked &&
                          !change.MetaOnly;

            // Настройка читается на каждый показ: переключили в Project Settings —
            // видно со следующего же выбранного файла, без перезапуска.
            _lineLevel = GitSettings.instance.lineLevelSelection;

            await BuildSceneDiffAsync(change);

            if (key != _key) return;
            BuildRows();
        }

        /// <summary>
        /// Считает семантическую разницу для сцен и префабов.
        ///
        /// Здесь нужен не патч, а обе версии файла целиком: сравнение идёт по
        /// объектам и их свойствам, а не по строкам. Если YAML не разобрался —
        /// молча остаёмся на текстовом сравнении: показать текст всегда лучше,
        /// чем показать ошибку.
        /// </summary>
        private async Task BuildSceneDiffAsync(GitChange change)
        {
            _sceneDiff = null;
            _semanticAvailable = false;

            if (_diff == null || _diff.IsBinary) return;
            if (!IsUnityYamlAsset(change.ProjectPath)) return;

            var gitPath = GitRepository.ToGitPath(change.ProjectPath);
            var newText = GitOperations.ReadWorktreeText(gitPath) ?? string.Empty;
            var oldText = await GitOperations.ShowHeadTextAsync(gitPath) ?? string.Empty;

            TryBuildSceneDiff(oldText, newText);
        }

        /// <summary>Разбор двух версий сцены; не разобралось — остаёмся на тексте.</summary>
        private void TryBuildSceneDiff(string oldText, string newText)
        {
            if (!UnityYamlParser.LooksLikeUnityYaml(newText) &&
                !UnityYamlParser.LooksLikeUnityYaml(oldText)) return;

            try
            {
                _sceneDiff = SceneDiffBuilder.Build(oldText, newText);
                _semanticAvailable = true;
            }
            catch (Exception e)
            {
                // Формат сцен меняется от версии Unity к версии: неизвестное поле
                // не повод лишать человека diff вообще.
                Diagnostics.Journal.Warn(L.F("Could not parse the scene: {0}", e.Message));
            }
        }

        private static bool IsUnityYamlAsset(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            foreach (var ext in new[] { ".unity", ".prefab", ".asset", ".controller", ".mat", ".physicMaterial" })
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private void SetMode(bool text)
        {
            if (_preferText == text) return;
            _preferText = text;
            BuildRows();
        }

        private void BuildRows()
        {
            _rows.Clear();
            _rowsKey = _key;

            bool semantic = _semanticAvailable && !_preferText;

            // Кнопки режима показываются только там, где есть из чего выбирать.
            UpdateModeButtons();
            _objectsHistory.style.display = _semanticAvailable && IsObjectFile(_projectPath) ? DisplayStyle.Flex : DisplayStyle.None;

            if (semantic)
                AddSceneTree(_sceneDiff);
            else if (_diff != null && !_diff.IsBinary && !IsLfsPointerDiff(_diff))
            {
                ComputeSyntax();

                foreach (var h in _diff.Hunks)
                {
                    _rows.Add(new Row { Hunk = h });
                    for (int i = 0; i < h.Lines.Count; i++)
                    {
                        var l = h.Lines[i];
                        if (l.Kind == DiffLineKind.NoNewline) continue;
                        _rows.Add(new Row { Line = l, OwnerHunk = h, LineIndex = i });
                    }
                }
            }

            bool hasRows = _rows.Count > 0;
            _list.style.display = hasRows ? DisplayStyle.Flex : DisplayStyle.None;
            _empty.style.display = hasRows ? DisplayStyle.None : DisplayStyle.Flex;

            if (!hasRows)
            {
                if (_diff != null && _diff.IsBinary)
                    SetEmptyText(L.T("Binary file"), _revision
                        ? L.T("No line-by-line comparison.")
                        : L.T("No line-by-line comparison — the file goes into the commit as a whole. " +
                              "This is exactly what an LFS lock is for."));
                else if (!semantic && IsLfsPointerDiff(_diff))
                    SetEmptyText(L.T("The file is stored in LFS"),
                        L.T("Git holds only a pointer to the content — the “oid” and “size” lines. " +
                            "Comparing them line by line is pointless; the image is visible in the preview."));
                else if (semantic)
                    SetEmptyText(L.T("No objects changed"),
                        L.T("The file text differs, but no object, component or property " +
                            "changed. Usually the serializer just reordered lines — " +
                            "see the “Text” tab."));
                else
                    SetEmptyText(L.T("No changes"), null);
            }

            if (semantic)
            {
                int objects, props, settings;
                SceneDiffBuilder.Count(_sceneDiff, out objects, out props, out settings);
                _sceneSummary.text = settings > 0
                    ? L.F("objects: {0} · properties: {1} · scene settings: {2}", objects, props, settings)
                    : L.F("objects: {0} · properties: {1}", objects, props);
                _stats.text = _revision ? string.Empty : L.T("hunk selection is on the “Text” tab");
            }
            else
            {
                _sceneSummary.text = string.Empty;
                _stats.text = _diff == null || IsLfsPointerDiff(_diff) ? string.Empty
                    : string.Format("+{0}  −{1}", _diff.Added, _diff.Removed);
            }

            _list.itemsSource = _rows;
            _list.Rebuild();
            ApplyReveal();

            if (ModesChanged != null) ModesChanged();
        }

        // ---------------------------------------------------------- переходы ---

        /// <summary>Прокрутить к строке файла — сейчас или когда он дочитается.</summary>
        public void RevealLine(string gitPath, int oldLine, int newLine)
        {
            _revealPath = gitPath;
            _reveal = r => r.Line != null &&
                           (newLine > 0 ? r.Line.NewNumber == newLine : oldLine > 0 && r.Line.OldNumber == oldLine);

            if (ModeRequested != null) ModeRequested(true);

            // Строки видны только в текстовом виде.
            if (!_preferText)
            {
                _preferText = true;
                if (RowsReadyFor(gitPath)) { BuildRows(); return; }
            }
            ApplyReveal();
        }

        /// <summary>Прокрутить к объекту или компоненту сцены.</summary>
        public void RevealObject(string gitPath, long fileId)
        {
            _revealPath = gitPath;
            _reveal = r => r.Node != null && r.Node.FileId == fileId;

            if (ModeRequested != null) ModeRequested(false);
            // Объект мог оказаться в свёрнутом узле — раскрываем всё, чтобы строка точно была.
            _folded.Clear();
            _folded.Add(SettingsOpenKey);

            if (_preferText)
            {
                _preferText = false;
                if (RowsReadyFor(gitPath)) { BuildRows(); return; }
            }
            ApplyReveal();
        }

        /// <summary>Файл уже дочитан — строки можно перестроить сейчас, а не ждать загрузки.</summary>
        private bool RowsReadyFor(string gitPath)
        {
            return _key != null && _rowsKey == _key && _gitPath == gitPath;
        }

        private void ApplyReveal()
        {
            if (_reveal == null || !RowsReadyFor(_revealPath) || _rows.Count == 0) return;

            var match = _reveal;
            _reveal = null;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!match(_rows[i])) continue;
                int index = i;
                // Раскладка списка ещё не посчитана — прокрутка в том же кадре ничего не сделает.
                _list.schedule.Execute(() => _list.ScrollToItem(Math.Max(0, index - 3)));
                return;
            }
        }

        /// <summary>Пометки у строк изменились — перерисовать, не перечитывая diff.</summary>
        public void RefreshNotes()
        {
            _list.RefreshItems();
            if (NotesRefreshed != null) NotesRefreshed();
        }

        /// <summary>Пометки обсуждений пересчитаны — соседям над diff тоже пора их обновить.</summary>
        public event Action NotesRefreshed;

        private DiffLineRef LineRefOf(Row row)
        {
            return new DiffLineRef
            {
                GitPath = _gitPath,
                OldGitPath = _oldGitPath ?? _gitPath,
                OldLine = row.Line.OldNumber,
                NewLine = row.Line.NewNumber,
                Kind = row.Line.Kind,
                Text = row.Line.Text
            };
        }

        // ------------------------------------------------ подсветка синтаксиса ---

        /// <summary>Цветные участки каждой строки diff. Считаются один раз при построении строк.</summary>
        private readonly Dictionary<DiffLine, List<SyntaxSpan>> _syntax = new Dictionary<DiffLine, List<SyntaxSpan>>();

        /// <summary>Больше строк — без подсветки: diff огромной сцены должен открываться сразу.</summary>
        private const int SyntaxLineLimit = 30000;

        /// <summary>
        /// Подсветка по фрагментам. У каждой стороны своё состояние: удалённая
        /// строка продолжает старый текст, добавленная — новый, а общая — оба. Иначе
        /// удалённый «/*» окрасил бы комментарием добавленный код под ним.
        /// </summary>
        private void ComputeSyntax()
        {
            _syntax.Clear();
            if (_diff == null) return;

            var language = SyntaxHighlight.Detect(_gitPath ?? _projectPath);
            if (language == SyntaxLanguage.None) return;

            int total = 0;
            foreach (var h in _diff.Hunks) total += h.Lines.Count;
            if (total > SyntaxLineLimit) return;

            foreach (var h in _diff.Hunks)
            {
                var oldState = SyntaxState.None;
                var newState = SyntaxState.None;
                bool first = true;

                foreach (var line in h.Lines)
                {
                    if (line.Kind == DiffLineKind.NoNewline) continue;
                    var text = line.Text ?? string.Empty;

                    if (first)
                    {
                        oldState = newState = SyntaxHighlight.GuessState(language, text);
                        first = false;
                    }

                    switch (line.Kind)
                    {
                        case DiffLineKind.Added:
                            _syntax[line] = SyntaxHighlight.Tokenize(language, text, ref newState);
                            break;

                        case DiffLineKind.Removed:
                            _syntax[line] = SyntaxHighlight.Tokenize(language, text, ref oldState);
                            break;

                        default:
                        {
                            // Общая строка продолжает обе стороны. Совпадают состояния —
                            // второй разбор не нужен: результат был бы тем же.
                            bool same = oldState == newState;
                            _syntax[line] = SyntaxHighlight.Tokenize(language, text, ref newState);
                            if (same) oldState = newState;
                            else SyntaxHighlight.Tokenize(language, text, ref oldState);
                            break;
                        }
                    }
                }
            }
        }

        private static string Rich(string text, List<SyntaxSpan> spans, int from, int to)
        {
            return SyntaxRich.Rich(text, spans, from, to);
        }

        /// <summary>Ассеты по ссылкам из YAML — строки перерисовываются часто, а поиск по файлу не бесплатный.</summary>
        private static readonly Dictionary<string, UnityEngine.Object> ReferenceCache = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);

        /// <summary>
        /// Значение свойства в строке дерева: цвет — образцом и #RRGGBB, ссылка на
        /// ассет — иконкой и именем, вектор — (x, y, z). Исходная запись — в подсказке.
        /// </summary>
        private static void BindValue(Label label, VisualElement swatch, Image icon, string raw)
        {
            swatch.style.display = DisplayStyle.None;
            icon.style.display = DisplayStyle.None;
            label.tooltip = raw;

            if (raw == null)
            {
                label.text = "—";
                return;
            }

            float r, g, b, a;
            if (Lev.Git.Preview.ValueFormat.TryColor(raw, out r, out g, out b, out a))
            {
                swatch.style.backgroundColor = new UnityEngine.Color(r, g, b, 1f);
                swatch.style.display = DisplayStyle.Flex;
                label.text = "#" + UnityEngine.ColorUtility.ToHtmlStringRGB(new UnityEngine.Color(r, g, b)) +
                             (a < 0.999f ? " α" + a.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
                return;
            }

            string guid;
            long fileId;
            if (Lev.Git.Preview.ValueFormat.TryAssetRef(raw, out guid, out fileId))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path))
                {
                    label.text = L.F("not in the project ({0}…)", guid.Substring(0, 8));
                    return;
                }

                var asset = ReferencedAsset(path, guid, fileId);
                icon.image = asset != null ? AssetPreview.GetMiniThumbnail(asset) : Ui.AssetIcon(path);
                icon.style.display = DisplayStyle.Flex;
                label.text = asset != null ? asset.name : Ui.NameOf(path);
                return;
            }

            label.text = Lev.Git.Preview.ValueFormat.Compact(raw);
        }

        private static UnityEngine.Object ReferencedAsset(string path, string guid, long fileId)
        {
            var key = guid + "|" + fileId;
            UnityEngine.Object found;
            if (ReferenceCache.TryGetValue(key, out found) && found != null) return found;

            if (ReferenceCache.Count > 2000) ReferenceCache.Clear();

            found = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                string g;
                long id;
                if (o != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out g, out id) && id == fileId) { found = o; break; }
            }

            if (found == null) found = AssetDatabase.LoadMainAssetAtPath(path);
            ReferenceCache[key] = found;
            return found;
        }

        private DiffObjectRef ObjectRefOf(Row row)
        {
            return new DiffObjectRef
            {
                GitPath = _gitPath,
                OldGitPath = _oldGitPath ?? _gitPath,
                ProjectPath = _projectPath,
                FileId = row.Node.FileId,
                Title = row.Node.Title,
                OwnerTitle = row.OwnerNode != null ? row.OwnerNode.Title : null,
                Added = row.Node.Kind == SceneChangeKind.Added,
                Removed = row.Node.Kind == SceneChangeKind.Removed
            };
        }

        /// <summary>
        /// Объекты — деревом, как в иерархии и в окне «История сцены»: по пути
        /// объекта. Промежуточный объект сам мог не меняться, но без него
        /// изменившийся ребёнок висел бы без родителя.
        /// </summary>
        private void AddSceneTree(List<SceneNode> roots)
        {
            var top = new List<SceneGroup>();
            var byPath = new Dictionary<string, SceneGroup>(StringComparer.Ordinal);
            var settings = new List<SceneNode>();

            foreach (var n in roots)
            {
                // Служебные документы сцены — не объекты иерархии: отдельной группой в конце.
                if (SceneDiffBuilder.IsSceneSettings(n))
                {
                    settings.Add(n);
                    continue;
                }

                var path = string.IsNullOrEmpty(n.Title) ? "?" : n.Title;
                var g = EnsureGroup(top, byPath, path);

                // Два соседа с одинаковым именем — обычное дело в Unity: второй идёт
                // отдельной строкой рядом, а не затирает первого.
                if (g.Node != null)
                {
                    var twin = new SceneGroup { Name = g.Name, Path = path + "|#" + n.FileId, Parent = g.Parent, Node = n };
                    (g.Parent != null ? g.Parent.Children : top).Add(twin);
                    continue;
                }
                g.Node = n;
            }

            foreach (var g in top) AddGroupRows(g, 0);
            if (settings.Count > 0) AddSettingsRows(settings);
        }

        /// <summary>Группа «Настройки сцены» раскрыта. Ключ означает «раскрыта», а не «свёрнута»: по умолчанию она закрыта.</summary>
        private const string SettingsOpenKey = "s|open";

        /// <summary>
        /// Служебные документы сцены — отдельной группой в конце, как в окне «История сцены».
        /// Свёрнута по умолчанию: SceneRoots, например, меняется при каждом добавлении
        /// корневого объекта и засоряла бы дерево.
        /// </summary>
        private void AddSettingsRows(List<SceneNode> settings)
        {
            _rows.Add(new Row
            {
                SettingsGroup = true, GroupName = L.T("Scene Settings"), Depth = 0, FoldKey = SettingsOpenKey,
                Detail = L.F("{0} · not hierarchy objects", settings.Count)
            });
            if (!_folded.Contains(SettingsOpenKey)) return;

            foreach (var n in settings)
            {
                var key = "s|" + n.FileId;
                bool expandable = n.Props.Count > 0;
                _rows.Add(new Row { Node = n, GroupName = n.Title, Depth = 1, FoldKey = expandable ? key : null });

                if (expandable && !_folded.Contains(key))
                    foreach (var p in n.Props) _rows.Add(new Row { Prop = p, PropNode = n, Depth = 2 });
            }
        }

        /// <summary>Свёрнута ли строка. У группы настроек смысл ключа обратный.</summary>
        private bool IsFolded(Row row)
        {
            if (row.FoldKey == null) return false;
            return row.FoldKey == SettingsOpenKey ? !_folded.Contains(row.FoldKey) : _folded.Contains(row.FoldKey);
        }

        private static SceneGroup EnsureGroup(List<SceneGroup> top, Dictionary<string, SceneGroup> byPath, string path)
        {
            if (byPath.TryGetValue(path, out var existing)) return existing;

            int slash = path.LastIndexOf('/');
            var g = new SceneGroup { Name = slash <= 0 ? path : path.Substring(slash + 1), Path = path };
            byPath[path] = g;

            if (slash <= 0)
            {
                top.Add(g);
            }
            else
            {
                g.Parent = EnsureGroup(top, byPath, path.Substring(0, slash));
                g.Parent.Children.Add(g);
            }
            return g;
        }

        private void AddGroupRows(SceneGroup g, int depth)
        {
            var n = g.Node;
            var key = "g|" + g.Path;
            bool expandable = g.Children.Count > 0 || (n != null && (n.Props.Count > 0 || n.Children.Count > 0));

            _rows.Add(new Row { Node = n, GroupName = g.Name, Depth = depth, FoldKey = expandable ? key : null });
            if (!expandable || _folded.Contains(key)) return;

            if (n != null && PrefabOverrides.Is(n.NewDoc ?? n.OldDoc))
            {
                AddInstanceRows(n, depth + 1);
            }
            else if (n != null)
            {
                foreach (var p in n.Props) _rows.Add(new Row { Prop = p, PropNode = n, Depth = depth + 1 });

                // Владелец нужен, чтобы у компонента был адрес: по нему ищется объект
                // в живой сцене и строится заголовок окна «было и стало».
                foreach (var c in n.Children)
                {
                    var componentKey = "c|" + c.FileId;
                    bool hasProps = c.Props.Count > 0;
                    _rows.Add(new Row { Node = c, OwnerNode = n, Depth = depth + 1, FoldKey = hasProps ? componentKey : null });

                    if (hasProps && !_folded.Contains(componentKey))
                        foreach (var p in c.Props) _rows.Add(new Row { Prop = p, PropNode = c, OwnerNode = n, Depth = depth + 2 });
                }
            }

            foreach (var child in g.Children) AddGroupRows(child, depth + 1);
        }

        /// <summary>
        /// Экземпляр префаба — не документом PrefabInstance с номерами
        /// переопределений, а теми объектами и компонентами префаба, чьи
        /// переопределения поменялись. Разбор общий с окном истории сцены.
        /// </summary>
        private void AddInstanceRows(SceneNode instance, int depth)
        {
            var parts = PrefabInstanceParts.Build(instance.OldDoc, instance.NewDoc);

            // Как в иерархии: правки корня экземпляра — прямо под его строкой, дети —
            // вложенными строками по имени.
            int skipBelow = int.MaxValue;
            foreach (var entry in PrefabInstanceParts.Layout(parts, InstanceName(instance)))
            {
                if (entry.Level > skipBelow) continue;
                skipBelow = int.MaxValue;

                var part = entry.Part;
                int rowDepth = depth + Math.Max(0, entry.Level - 1);
                int inner = entry.Level == 0 ? depth : rowDepth + 1;

                if (entry.Level > 0)
                {
                    var key = "i|" + instance.FileId + "|" + entry.Key;
                    _rows.Add(new Row
                    {
                        Instance = instance, TargetKey = entry.Key, TargetAsset = part != null ? part.Asset : null,
                        TargetKeys = part != null ? part.AllKeys : new List<string> { entry.Key },
                        GroupName = entry.Name, Depth = rowDepth, FoldKey = key, Placeholder = part == null,
                        Detail = part != null ? PrefabInstanceParts.Summary(part.AllChanges) : string.Empty
                    });
                    if (_folded.Contains(key)) { skipBelow = entry.Level; continue; }
                }

                if (part == null) continue;

                foreach (var change in part.Own) _rows.Add(InstancePropRow(instance, change, inner));

                foreach (var component in part.Components)
                {
                    var componentKey = "i|" + instance.FileId + "|" + component.Key;
                    _rows.Add(new Row
                    {
                        Instance = instance, TargetKey = component.Key, TargetAsset = component.Asset,
                        TargetKeys = new List<string> { component.Key },
                        GroupName = component.Title, Depth = inner,
                        FoldKey = componentKey, Detail = PrefabInstanceParts.Summary(component.Changes)
                    });
                    if (_folded.Contains(componentKey)) continue;

                    foreach (var change in component.Changes) _rows.Add(InstancePropRow(instance, change, inner + 1));
                }
            }

            // Снятые компоненты в m_Modifications не попадают — строкой экземпляра.
            int removedBefore = PrefabOverrides.RemovedComponents(instance.OldDoc);
            int removedAfter = PrefabOverrides.RemovedComponents(instance.NewDoc);
            if (removedBefore != removedAfter)
                _rows.Add(new Row
                {
                    Prop = new ScenePropertyChange { Path = L.T("components removed"), Old = removedBefore.ToString(), New = removedAfter.ToString() },
                    Depth = depth
                });
        }

        private static Row InstancePropRow(SceneNode instance, PrefabModificationChange change, int depth)
        {
            return new Row
            {
                Prop = new ScenePropertyChange
                {
                    Path = change.PropertyPath,
                    // Где переопределения нет, действует значение префаба: «1 (из префаба)».
                    Old = change.OldText,
                    New = change.NewText
                },
                PropNode = instance,
                Instance = instance,
                Mod = change,
                Depth = depth
            };
        }

        /// <summary>Имя экземпляра: как его назвали на сцене, иначе — имя файла префаба.</summary>
        private static string InstanceName(SceneNode instance)
        {
            return PrefabInstanceParts.InstanceName(instance.NewDoc ?? instance.OldDoc);
        }

        /// <summary>
        /// Переход по двойному клику для любой строки дерева объектов: объект,
        /// компонент, объект внутри экземпляра префаба, свойство. false — строка
        /// не про объект сцены (фрагмент текста), переходить некуда.
        /// </summary>
        private bool NavigateFromRow(Row row)
        {
            if (row == null || _projectPath == null) return false;

            // Внутри экземпляра префаба — к объекту экземпляра.
            if (row.Instance != null && (row.Node == null || row.Mod != null))
            {
                NavigateInstance(row);
                return true;
            }

            // Объект или компонент. Настройки сцены на сцене не выделить — открываем их окно.
            if (row.Node != null)
            {
                if (row.OwnerNode == null && SceneDiffBuilder.IsSceneSettings(row.Node)) return OpenSettingsWindow(row.Node);
                NavigateTo(row);
                return true;
            }

            // Свойство — к объекту или компоненту, которому оно принадлежит.
            if (row.Prop != null && row.PropNode != null)
            {
                if (row.OwnerNode == null && SceneDiffBuilder.IsSceneSettings(row.PropNode)) return OpenSettingsWindow(row.PropNode);
                NavigateTo(new Row { Node = row.PropNode, OwnerNode = row.OwnerNode });
                return true;
            }

            return false;
        }

        /// <summary>Выделить на сцене объект или компонент внутри экземпляра префаба.</summary>
        private void NavigateInstance(Row row)
        {
            var targetKey = row.Mod != null ? row.Mod.Target : row.TargetKey;
            if (_projectPath == null || row.Instance == null || targetKey == null) return;

            if (SceneRevert.IsPrefabFile(_projectPath))
            {
                _host.SetStatus(L.T("An object of a nested prefab cannot be selected in a prefab file — open the prefab."), true);
                return;
            }

            // Не через Find: по fileID документа PrefabInstance находится служебная
            // запись экземпляра, а не корневой объект, — и переход считал, что объекта нет.
            var doc = row.Instance.NewDoc ?? row.Instance.OldDoc;
            var root = SceneObjectRef.FindInstance(_projectPath, row.Instance.FileId,
                                                   PrefabOverrides.SourceGuid(doc), PrefabOverrides.NameOf(doc));
            if (root == null)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(_projectPath);
                _host.SetStatus(scene.IsValid() && scene.isLoaded
                    ? L.F("Instance “{0}” is not in the open scene.", InstanceName(row.Instance))
                    : L.T("The scene is not open — open it to go there."), true);
                return;
            }

            // Строка объекта, у которого менялись только компоненты, — переходим к объекту.
            var target = SceneObjectRef.InstanceObject(root, targetKey);
            if (target == null && row.TargetKeys != null)
                foreach (var key in row.TargetKeys)
                    if ((target = SceneObjectRef.InstanceObject(root, key)) != null) break;

            if (target == null)
            {
                _host.SetStatus(L.T("The instance in the scene has no such object — the prefab may have changed since."), true);
                return;
            }

            // Итог — в строку состояния: без него неудачный переход выглядел как «ничего не произошло».
            if (SceneObjectRef.FocusObject(target))
                _host.SetStatus(L.F("Selected in the scene: {0}",
                                    (row.Mod != null ? L.F("{0} of {1}", row.Mod.PropertyPath, target.name) : target.name) +
                                    (target is UnityEngine.Component ? " · " + target.GetType().Name : string.Empty)), false);
            else
                _host.SetStatus(L.F("The object was found but could not be selected: {0}", target.name), true);
        }

        /// <summary>Объект в файле префаба по ключу «guid|fileId» — для «Открыть префаб на этом объекте».</summary>
        private static UnityEngine.Object ResolveTarget(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            int bar = key.IndexOf('|');
            return bar > 0 ? PrefabTargets.Resolve(key.Substring(0, bar), key.Substring(bar + 1)) : null;
        }

        /// <summary>Пункты про исходный префаб: показать в Project, открыть на этом объекте.</summary>
        private void AddPrefabSourceItems(DropdownMenu menu, SceneNode instance, UnityEngine.Object prefabTarget)
        {
            var guid = PrefabOverrides.SourceGuid(instance.NewDoc ?? instance.OldDoc);
            var path = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
            menu.AppendSeparator();

            if (string.IsNullOrEmpty(path))
            {
                menu.AppendAction(L.T("Prefab — Prefab File Not Found"), _ => { }, DropdownMenuAction.Status.Disabled);
                return;
            }

            var file = System.IO.Path.GetFileName(path);
            menu.AppendAction(L.F("Show Prefab “{0}” in Project", file), _ => PrefabInstanceParts.PingPrefab(guid));
            menu.AppendAction(prefabTarget != null ? L.T("Open Prefab at This Object") : L.T("Open Prefab"), _ =>
            {
                if (!PrefabInstanceParts.OpenInPrefab(guid, prefabTarget))
                    _host.SetStatus(L.F("Could not open prefab “{0}”.", file), true);
            });
        }

        private async void RevertInstanceRow(Row row)
        {
            if (row.Instance == null || _change == null || _projectPath == null) return;

            var notReady = SceneRevert.NotReady(_projectPath);
            if (notReady != null)
            {
                EditorUtility.DisplayDialog(L.T("Revert to Last Commit"), notReady, L.T("OK"));
                return;
            }
            bool live = SceneRevert.IsLive(_projectPath);

            var modKey = row.Mod != null ? row.Mod.Key : null;
            var targets = row.TargetKeys;
            var what = modKey != null
                ? L.F("Override “{0}”", row.Mod.PropertyPath)
                : row.TargetAsset is UnityEngine.Component
                    ? L.F("Component “{0}” in the prefab instance", row.GroupName)
                    : L.F("Object “{0}” in the prefab instance", row.GroupName);

            if (!EditorUtility.DisplayDialog(L.T("Revert to Last Commit"),
                    L.F("{0} will return to its state in the last commit.\n\n{1}", what, UndoHint(live)), L.T("Revert"), L.T("Cancel")))
                return;

            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(_projectPath);
            bool wasDirty = scene.IsValid() && scene.isDirty;

            Func<PrefabModificationChange, bool> filter = change =>
                modKey != null ? change.Key == modKey : targets != null && targets.Contains(change.Target);

            var outcome = live
                ? SceneRevert.InstanceOverrides(_projectPath, row.Instance, filter)
                : await SceneRevert.InstanceOverridesInFileAsync(_projectPath, row.Instance, filter);

            CompleteRevert(outcome, wasDirty);
        }

        /// <summary>Суть правки одной строкой — для свёрнутого узла: «m_Mass: 1 → 2  +3».</summary>
        private static string Summary(SceneNode n)
        {
            if (n == null || n.Props.Count == 0) return n != null && n.Children.Count > 0 ? L.F("components: {0}", n.Children.Count) : string.Empty;

            var first = n.Props[0];
            var text = first.Path + ": " + ShortValue(first.Old) + " → " + ShortValue(first.New);
            if (n.Props.Count > 1) text += "  +" + (n.Props.Count - 1);
            if (n.Children.Count > 0) text += " · " + L.F("components: {0}", n.Children.Count);
            return text;
        }

        private static string ShortValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "—";
            return value.Length <= 24 ? value : value.Substring(0, 24) + "…";
        }

        // ------------------------------------------------------------ откат ---

        /// <summary>
        /// Откат объекта, компонента или свойства к последнему коммиту — на живой
        /// сцене, одной операцией Ctrl+Z. Сцена без других несохранённых правок
        /// сохраняется сразу, чтобы откат ушёл из списка изменений.
        /// </summary>
        private async void RevertRow(Row row)
        {
            var node = row.Prop != null ? row.PropNode : row.Node;
            if (node == null || _change == null || _projectPath == null) return;

            var owner = row.OwnerNode;
            bool single = row.Prop != null;

            var notReady = SceneRevert.NotReady(_projectPath);
            if (notReady != null)
            {
                EditorUtility.DisplayDialog(L.T("Revert to Last Commit"), notReady, L.T("OK"));
                return;
            }

            // Открытая сцена — по живым объектам, с Ctrl+Z. Всё остальное — правкой файла.
            bool live = SceneRevert.IsLive(_projectPath);

            var what = single ? L.F("Property “{0}”", row.Prop.Path)
                     : owner != null ? L.F("Component “{0}” on “{1}”", node.Title, owner.Title)
                     : L.F("Object “{0}” together with its components", node.Title);

            if (!EditorUtility.DisplayDialog(L.T("Revert to Last Commit"),
                    L.F("{0} will return to its state in the last commit.\n\n{1}", what, UndoHint(live)),
                    L.T("Revert"), L.T("Cancel")))
                return;

            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(_projectPath);
            bool wasDirty = scene.IsValid() && scene.isDirty;

            SceneRevert.Outcome outcome;
            if (live)
                outcome = single ? SceneRevert.Property(_projectPath, node, owner, row.Prop) : SceneRevert.Node(_projectPath, node, owner);
            else
                outcome = single ? await SceneRevert.PropertyInFileAsync(_projectPath, node, row.Prop)
                                 : await SceneRevert.NodeInFileAsync(_projectPath, node, owner);

            CompleteRevert(outcome, wasDirty);
        }

        private static string UndoHint(bool live)
        {
            return live ? L.T("You can undo it with Ctrl+Z.") : L.T("The file is rewritten immediately — Ctrl+Z will not undo it.");
        }

        /// <summary>
        /// После отката: сцену без других несохранённых правок сохраняем, чтобы
        /// откат ушёл из списка изменений; с чужими правками — только подсказываем.
        /// </summary>
        private void CompleteRevert(SceneRevert.Outcome outcome, bool wasDirty)
        {
            if (!outcome.Ok)
            {
                _host.SetStatus(outcome.Message, true);
                return;
            }

            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(_projectPath);
            if (!SceneRevert.IsPrefabFile(_projectPath) && scene.IsValid() && scene.isLoaded)
            {
                if (!wasDirty)
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                else
                    outcome.Message += " " + L.T("The scene has other unsaved changes — save it so the revert leaves the list of changes.");
            }

            _host.SetStatus(outcome.Message, false);
            RefreshAfterRevert();
        }

        private async void RefreshAfterRevert()
        {
            await GitStatusCache.RefreshAsync();
            FileChanged?.Invoke();

            // Файл мог перезаписаться сохранением — пересчитываем отпечаток сами.
            if (_change == null) return;
            _key = KeyOf(_change);
            Reload(_key);
        }

        /// <summary>Сцена или префаб — файлы, у которых есть история по объектам.</summary>
        private static bool IsObjectFile(string projectPath)
        {
            return projectPath != null &&
                   (projectPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ||
                    projectPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
        }

        private void OpenObjectsHistory()
        {
            if (!IsObjectFile(_projectPath)) return;
            SceneHistoryWindow.ShowScene(_projectPath, _historySha);
        }

        /// <summary>
        /// Объект или компонент инспектором. Из рабочей копии — окно «было в коммите
        /// и сейчас»: несохранённой правки в истории ещё нет. Из коммита — лента
        /// истории на этом коммите: с прошлой версией или с текущим состоянием.
        /// </summary>
        private void OpenInspector(Row row, bool compareLive)
        {
            if (row == null || row.Node == null || _projectPath == null) return;

            var n = row.Node;
            var doc = n.NewDoc ?? n.OldDoc;

            if (row.OwnerNode == null)
            {
                SceneHistoryWindow.ShowObject(
                    new SceneObjectAddress { ScenePath = _projectPath, FileId = n.FileId, Name = row.GroupName ?? n.Title },
                    _historySha, true, compareLive);
                return;
            }

            var type = doc != null ? doc.TypeName : n.Title;
            var title = SceneHistoryIcons.DisplayName(type, doc);

            if (_change != null)
            {
                var live = SceneObjectRef.Find(new SceneObjectAddress { ScenePath = _projectPath, FileId = n.FileId }) as UnityEngine.Component;
                ComponentHistoryWindow.Show(n, L.F("{0} on “{1}”", title, row.OwnerNode.Title), live);
                return;
            }

            SceneHistoryWindow.ShowComponent(
                new SceneObjectAddress { ScenePath = _projectPath, FileId = row.OwnerNode.FileId, Name = row.OwnerNode.Title },
                new SceneObjectAddress { ScenePath = _projectPath, FileId = n.FileId, Name = title },
                title, _historySha, compareLive);
        }

        /// <summary>Переход к объекту или компоненту в открытой сцене.</summary>
        private void NavigateTo(Row row)
        {
            if (row == null || row.Node == null || _projectPath == null) return;

            // По fileID, а не по пути: два соседних объекта с одинаковым именем
            // путь не различает. Для компонента Focus сам выделит объект и
            // раскроет в инспекторе только этот компонент.
            bool ok = SceneObjectRef.Focus(new SceneObjectAddress
            {
                ScenePath = _projectPath,
                FileId = row.Node.FileId
            });

            if (!ok)
                _host.SetStatus(L.T("Not found in open scenes — open the scene to go there."), true);
        }

        /// <summary>Окно, где правятся эти настройки сцены. false — такого окна у них нет.</summary>
        private static bool OpenSettingsWindow(SceneNode node)
        {
            var doc = node.NewDoc ?? node.OldDoc;
            var window = SceneHistoryIcons.SettingsWindow(doc != null ? doc.TypeName : null);
            if (window == null) return false;

            EditorApplication.ExecuteMenuItem(window);
            return true;
        }

        /// <summary>
        /// Меню строки настроек сцены. Выделить их на сцене нечего, а откат писался
        /// для объектов и компонентов — этих пунктов здесь нет.
        /// </summary>
        private void AddSettingsItems(DropdownMenu menu, Row row)
        {
            var doc = row.Node.NewDoc ?? row.Node.OldDoc;
            var window = SceneHistoryIcons.SettingsWindow(doc != null ? doc.TypeName : null);

            if (window != null)
                menu.AppendAction(L.T("Open Settings Window"), _ => EditorApplication.ExecuteMenuItem(window));
            else
                menu.AppendAction(L.T("Open Settings Window") + " — " + L.T("this data has none"), _ => { },
                                  DropdownMenuAction.Status.Disabled);

            // У коммита «было и стало» показывает история сцены; у рабочей копии истории ещё нет.
            if (_projectPath != null && _change == null)
                menu.AppendAction(L.T("Show Change in Inspector"), _ => OpenInspector(row, false));
        }

        private static string SettingsRowTooltip(string type)
        {
            var tip = SceneHistoryIcons.SettingsTooltip(type);
            return SceneHistoryIcons.SettingsWindow(type) != null
                ? tip + "\n" + L.T("Double-click to open the window where these settings are edited")
                : tip;
        }

        /// <summary>
        /// Патч затрагивает только указатель LFS: «version», «oid», «size».
        /// Показывать его строками бессмысленно — это не содержимое файла,
        /// а его адрес в хранилище.
        /// </summary>
        private static bool IsLfsPointerDiff(FileDiff diff)
        {
            if (diff == null || diff.IsBinary || diff.Hunks.Count == 0) return false;

            foreach (var h in diff.Hunks)
            {
                foreach (var l in h.Lines)
                {
                    if (l.Kind == DiffLineKind.NoNewline) continue;

                    var t = l.Text ?? string.Empty;
                    bool pointer = t.Length == 0 ||
                                   t.StartsWith("version https://git-lfs.github.com/spec/", StringComparison.Ordinal) ||
                                   t.StartsWith("oid sha256:", StringComparison.Ordinal) ||
                                   t.StartsWith("size ", StringComparison.Ordinal) ||
                                   t.StartsWith("ext-", StringComparison.Ordinal);
                    if (!pointer) return false;
                }
            }

            return true;
        }

        private void SetEmptyText(string title, string hint)
        {
            _empty.Clear();
            _empty.Add(Ui.Text(title, "empty__title"));
            if (!string.IsNullOrEmpty(hint)) _empty.Add(Ui.Text(hint, "empty__hint"));
        }

        /// <summary>Все фрагменты файла — списку изменений нужно для состояния флажка.</summary>
        public IEnumerable<DiffHunk> Hunks
        {
            get { return _diff != null ? (IEnumerable<DiffHunk>)_diff.Hunks : new DiffHunk[0]; }
        }

        // ---------------------------------------------------------- строки ---

        private VisualElement MakeRow()
        {
            var wrap = new VisualElement();
            var w = new RowWidgets();

            w.HunkBox = Ui.Box("hunk");

            w.Check = new TriCheck();
            w.Check.Clicked += on =>
            {
                if (w.Bound == null || w.Bound.Hunk == null || _change == null) return;
                if (SetHunkSelected != null) SetHunkSelected(_change, w.Bound.Hunk, on);
                _list.RefreshItems();
            };
            w.HunkBox.Add(w.Check);

            w.HunkTitle = Ui.Text(string.Empty, "hunk__title");
            w.HunkStats = Ui.Text(string.Empty, "hunk__stats");
            w.Discard = Ui.Action(L.T("Discard"), () => Discard(w.Bound),
                L.T("Return the file to its state before this hunk"));

            w.HunkBox.Add(w.HunkTitle);
            w.HunkBox.Add(Ui.Spacer());
            w.HunkBox.Add(w.HunkStats);
            w.HunkBox.Add(w.Discard);
            wrap.Add(w.HunkBox);

            w.LineBox = Ui.Box("dline");

            w.LineCheck = new TriCheck();
            w.LineCheck.AddToClassList("tri--line");
            w.LineCheck.Clicked += on =>
            {
                if (w.Bound == null || w.Bound.Line == null || _change == null) return;
                if (SetLineSelected != null)
                    SetLineSelected(_change, w.Bound.OwnerHunk, w.Bound.LineIndex, on);
                _list.RefreshItems();
            };
            w.LineBox.Add(w.LineCheck);

            w.Old = Ui.Text(string.Empty, "dline__no");
            w.New = Ui.Text(string.Empty, "dline__no");

            // Три подписи вместо одной: изменившийся кусок строки подсвечивается
            // фоном. Цвет синтаксиса — rich text внутри каждой подписи; '<' в коде
            // экранируется, чтобы дженерики и XML не читались как теги.
            w.TextBox = Ui.Box("dline__text");
            w.TextPre = Ui.Text(string.Empty, "dline__seg");
            w.TextMark = Ui.Text(string.Empty, "dline__seg", "dline__seg--mark");
            w.TextPost = Ui.Text(string.Empty, "dline__seg");
            foreach (var seg in new[] { w.TextPre, w.TextMark, w.TextPost })
            {
                seg.style.unityFontDefinition = FontDefinition.FromFont(Ui.Mono);
                seg.enableRichText = true;
            }
            w.TextBox.Add(w.TextPre);
            w.TextBox.Add(w.TextMark);
            w.TextBox.Add(w.TextPost);

            w.LineBox.Add(w.Old);
            w.LineBox.Add(w.New);
            w.LineBox.Add(w.TextBox);

            w.LineNote = Ui.Text(string.Empty, "dline__note");
            w.LineNote.style.display = DisplayStyle.None;
            w.LineNote.RegisterCallback<ClickEvent>(e =>
            {
                if (w.Bound == null || w.Bound.Line == null || LineNoteClicked == null) return;
                LineNoteClicked(LineRefOf(w.Bound));
                e.StopPropagation();
            });
            w.LineBox.Add(w.LineNote);

            w.LineBox.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                if (w.Bound == null || w.Bound.Line == null || LineMenu == null || _gitPath == null) return;
                LineMenu(evt.menu, LineRefOf(w.Bound));
            }));
            wrap.Add(w.LineBox);

            // ---- строка дерева объектов ----
            // Строка как в иерархии: треугольник, иконка, имя, суть правки, знак у края.
            w.SceneBox = Ui.Box("sline");

            w.SceneArrow = Ui.Text(string.Empty, "sline__arrow");
            w.SceneArrow.RegisterCallback<MouseDownEvent>(e =>
            {
                var key = w.Bound != null ? w.Bound.FoldKey : null;
                if (key == null || e.button != 0) return;
                if (!_folded.Add(key)) _folded.Remove(key);
                e.StopPropagation();
                // Перестройка — на следующем кадре: сейчас мы внутри строки, которую она переиспользует.
                _list.schedule.Execute(BuildRows);
            });

            w.SceneIcon = new Image { scaleMode = UnityEngine.ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            w.SceneIcon.AddToClassList("sline__icon");

            w.SceneTitle = Ui.Text(string.Empty, "sline__title");
            w.SceneDetail = Ui.Text(string.Empty, "sline__detail");
            w.SceneNote = Ui.Text(string.Empty, "sline__note");
            w.SceneSign = Ui.Text(string.Empty, "sline__sign");

            w.SceneBox.Add(w.SceneArrow);
            w.SceneBox.Add(w.SceneIcon);
            w.SceneBox.Add(w.SceneTitle);
            w.SceneBox.Add(w.SceneDetail);
            w.SceneBox.Add(Ui.Spacer());
            w.SceneBox.Add(w.SceneNote);

            w.SceneComments = Ui.Text(string.Empty, "dline__note");
            w.SceneComments.style.display = DisplayStyle.None;
            w.SceneComments.RegisterCallback<ClickEvent>(e =>
            {
                if (w.Bound == null || w.Bound.Node == null || ObjectNoteClicked == null) return;
                ObjectNoteClicked(ObjectRefOf(w.Bound));
                e.StopPropagation();
            });
            w.SceneBox.Add(w.SceneComments);

            w.SceneBox.Add(w.SceneSign);

            // Двойной клик — перейти к объекту или компоненту на сцене. Через ClickEvent
            // на всей строке, а не MouseDownEvent: нажатие внутри списка забирает сам
            // список, и второе нажатие до строки не доходило. Работает только когда
            // сцена открыта: искать в закрытой негде.
            wrap.RegisterCallback<ClickEvent>(e =>
            {
                if (e.clickCount != 2 || e.button != 0) return;
                if (NavigateFromRow(w.Bound)) e.StopPropagation();
            }, TrickleDown.TrickleDown);

            w.SceneBox.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                var row = w.Bound;
                if (row == null) return;

                if (row.Instance != null && row.Node == null)
                {
                    evt.menu.AppendAction(L.T("Go To in Scene"), _ => NavigateInstance(row));
                    AddPrefabSourceItems(evt.menu, row.Instance, row.TargetAsset ?? ResolveTarget(row.TargetKey));
                    if (_change != null && _projectPath != null && !row.Placeholder)
                    {
                        // Вложенный префаб в файле .prefab тоже откатывается — правкой текста.
                        const bool nested = false;
                        evt.menu.AppendSeparator();
                        evt.menu.AppendAction(nested ? L.T("Revert — Nested Prefab by Reverting the File")
                                                     : row.TargetAsset is UnityEngine.Component ? L.T("Revert Component") : L.T("Revert Object"),
                            _ => RevertInstanceRow(row),
                            nested ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
                    }
                    return;
                }

                if (row.Node == null) return;

                if (row.OwnerNode == null && SceneDiffBuilder.IsSceneSettings(row.Node))
                {
                    AddSettingsItems(evt.menu, row);
                    if (ObjectMenu != null && _gitPath != null) ObjectMenu(evt.menu, ObjectRefOf(row));
                    return;
                }

                evt.menu.AppendAction(L.T("Go To in Scene"), _ => NavigateTo(row));
                if (PrefabOverrides.Is(row.Node.NewDoc ?? row.Node.OldDoc))
                    AddPrefabSourceItems(evt.menu, row.Node, null);

                if (_projectPath != null)
                {
                    bool component = row.OwnerNode != null;

                    // В рабочей копии изменение и так считается от текущего состояния —
                    // второй пункт здесь повторял бы первый. У объекта рабочей копии
                    // инспектора нет: несохранённых правок в истории ещё нет.
                    if (_change == null || component)
                        evt.menu.AppendAction(L.T("Show Change in Inspector"), _ => OpenInspector(row, false));
                    if (_change == null)
                        evt.menu.AppendAction(L.T("Compare with Current State"), _ => OpenInspector(row, true),
                            row.Node.Kind == SceneChangeKind.Removed ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
                }

                // Откат — только у рабочей копии: у коммита «вернуть к последнему коммиту» бессмысленно.
                if (_change != null && _projectPath != null)
                {
                    var cannot = SceneRevert.CannotReason(_projectPath, row.Node, row.OwnerNode);
                    evt.menu.AppendSeparator();
                    evt.menu.AppendAction(cannot == null
                            ? (row.OwnerNode != null ? L.T("Revert Component") : L.T("Revert Object"))
                            : L.F("Revert — {0}", cannot),
                        _ => RevertRow(row),
                        cannot == null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                }

                if (ObjectMenu != null && _gitPath != null) ObjectMenu(evt.menu, ObjectRefOf(row));
            }));

            wrap.Add(w.SceneBox);

            // ---- строка изменившегося свойства ----
            w.PropBox = Ui.Box("sprop");
            w.PropPath = Ui.Text(string.Empty, "sprop__path");
            w.PropOld = Ui.Text(string.Empty, "sprop__old");
            w.PropArrow = Ui.Text("→", "sprop__arrow");
            w.PropNew = Ui.Text(string.Empty, "sprop__new");
            foreach (var seg in new[] { w.PropPath, w.PropOld, w.PropNew })
                seg.style.unityFontDefinition = FontDefinition.FromFont(Ui.Mono);
            // Значение — не только текстом: у цвета образец, у ссылки на ассет его иконка.
            w.PropOldSwatch = Ui.Box("sprop__swatch");
            w.PropNewSwatch = Ui.Box("sprop__swatch");
            w.PropOldIcon = new Image();
            w.PropOldIcon.AddToClassList("sprop__icon");
            w.PropNewIcon = new Image();
            w.PropNewIcon.AddToClassList("sprop__icon");

            w.PropBox.Add(w.PropPath);
            w.PropBox.Add(w.PropOldSwatch);
            w.PropBox.Add(w.PropOldIcon);
            w.PropBox.Add(w.PropOld);
            w.PropBox.Add(w.PropArrow);
            w.PropBox.Add(w.PropNewSwatch);
            w.PropBox.Add(w.PropNewIcon);
            w.PropBox.Add(w.PropNew);

            w.PropBox.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                var row = w.Bound;

                // Переопределение внутри экземпляра префаба: переход к его объекту и откат одного свойства.
                if (row != null && row.Mod != null && _projectPath != null)
                {
                    evt.menu.AppendAction(L.T("Go To in Scene"), _ => NavigateInstance(row));
                    AddPrefabSourceItems(evt.menu, row.Instance, PrefabTargets.Resolve(row.Mod.TargetGuid, row.Mod.TargetFileId));
                    if (_change != null)
                    {
                        // Вложенный префаб в файле .prefab тоже откатывается — правкой текста.
                        const bool nested = false;
                        evt.menu.AppendSeparator();
                        evt.menu.AppendAction(nested ? L.T("Revert — Nested Prefab by Reverting the File") : L.T("Revert Property"),
                            _ => RevertInstanceRow(row),
                            nested ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
                    }
                    return;
                }

                if (row == null || row.Prop == null || row.PropNode == null || _change == null || _projectPath == null) return;

                bool modified = row.PropNode.Kind == SceneChangeKind.Modified;
                evt.menu.AppendAction(modified ? L.T("Revert Property") : L.T("Revert Property — Object Added or Removed as a Whole"),
                    _ => RevertRow(row), modified ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

                var cannot = SceneRevert.CannotReason(_projectPath, row.PropNode, row.OwnerNode);
                var whole = row.OwnerNode != null ? L.T("Revert Entire Component") : L.T("Revert Entire Object");
                evt.menu.AppendAction(cannot == null ? whole : whole + " — " + cannot,
                    _ => RevertRow(new Row { Node = row.PropNode, OwnerNode = row.OwnerNode }),
                    cannot == null ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            }));
            wrap.Add(w.PropBox);

            wrap.userData = w;
            return wrap;
        }

        private void BindRow(VisualElement element, int index)
        {
            var w = (RowWidgets)element.userData;
            var row = _rows[index];
            w.Bound = row;

            bool isHunk = row.Hunk != null;
            bool isNode = row.Node != null || row.GroupName != null;
            bool isProp = row.Prop != null;
            bool isLine = !isHunk && !isNode && !isProp;

            w.HunkBox.style.display = isHunk ? DisplayStyle.Flex : DisplayStyle.None;
            w.LineBox.style.display = isLine ? DisplayStyle.Flex : DisplayStyle.None;
            w.SceneBox.style.display = isNode ? DisplayStyle.Flex : DisplayStyle.None;
            w.PropBox.style.display = isProp ? DisplayStyle.Flex : DisplayStyle.None;

            if (isNode && row.Instance != null && row.Node == null)
            {
                // Объект или компонент внутри экземпляра префаба.
                bool foldable = row.FoldKey != null;
                bool folded = foldable && _folded.Contains(row.FoldKey);
                w.SceneArrow.text = foldable ? (folded ? "▸" : "▾") : string.Empty;

                w.SceneIcon.image = row.TargetAsset is UnityEngine.Component
                    ? EditorGUIUtility.ObjectContent(row.TargetAsset, row.TargetAsset.GetType()).image
                    : SceneHistoryIcons.GameObjectIcon;
                // Объект, который сам не менялся, — бледно: он здесь ради изменившихся детей.
                w.SceneIcon.style.opacity = row.Placeholder ? 0.45f : 1f;

                w.SceneTitle.text = row.GroupName;
                w.SceneTitle.EnableInClassList("sline__title--context", row.Placeholder);
                w.SceneTitle.EnableInClassList("sline__title--removed", false);
                w.SceneDetail.text = folded ? row.Detail ?? string.Empty : string.Empty;

                w.SceneSign.text = row.Placeholder ? string.Empty : "~";
                Ui.SetStatusText(w.SceneSign, GitFileStatus.Modified);
                w.SceneNote.text = string.Empty;
                w.SceneComments.style.display = DisplayStyle.None;

                w.SceneBox.style.paddingLeft = 4f + row.Depth * 14f;
                w.SceneBox.tooltip = row.Placeholder
                    ? L.T("The object itself did not change — its changed children are here. Double-click to select in the scene")
                    : L.T("Inside a prefab instance: its overrides changed. Double-click to select in the scene");
                return;
            }

            if (isNode && row.SettingsGroup)
            {
                // Заголовок группы: не объект, а место для служебных данных сцены.
                w.SceneArrow.text = IsFolded(row) ? "▸" : "▾";
                w.SceneIcon.image = SceneHistoryIcons.SettingsIcon;
                w.SceneIcon.style.opacity = 1f;
                w.SceneTitle.text = row.GroupName;
                w.SceneTitle.EnableInClassList("sline__title--context", false);
                w.SceneTitle.EnableInClassList("sline__title--removed", false);
                w.SceneDetail.text = row.Detail ?? string.Empty;
                w.SceneSign.text = string.Empty;
                w.SceneNote.text = string.Empty;
                w.SceneComments.style.display = DisplayStyle.None;
                w.SceneBox.style.paddingLeft = 4f;
                w.SceneBox.tooltip = L.T("Service data that every scene has and that isn't visible in the hierarchy: " +
                                         "environment and lighting, baking, occlusion, navigation, root object order.");
                return;
            }

            if (isNode)
            {
                var n = row.Node;
                bool component = row.OwnerNode != null;
                var doc = n != null ? n.NewDoc ?? n.OldDoc : null;
                var type = doc != null ? doc.TypeName : null;
                bool settings = !component && SceneDiffBuilder.IsSceneSettings(n);

                bool expandable = row.FoldKey != null;
                bool collapsed = expandable && IsFolded(row);
                w.SceneArrow.text = expandable ? (collapsed ? "▸" : "▾") : string.Empty;

                if (component)
                {
                    w.SceneIcon.image = SceneHistoryIcons.ComponentIcon(type ?? n.Title, doc);
                    w.SceneTitle.text = SceneHistoryIcons.DisplayName(type ?? n.Title, doc);
                }
                else if (settings)
                {
                    w.SceneIcon.image = SceneHistoryIcons.SettingsIcon;
                    w.SceneTitle.text = type ?? n.Title;
                }
                else
                {
                    w.SceneIcon.image = type == "PrefabInstance" ? SceneHistoryIcons.PrefabIcon : SceneHistoryIcons.GameObjectIcon;
                    w.SceneTitle.text = type == PrefabOverrides.TypeName ? InstanceName(n)
                                      : row.GroupName ?? (n != null ? n.Title : string.Empty);
                }

                // Объект, который сам не менялся, — приглушённым: он здесь только ради детей.
                w.SceneIcon.style.opacity = n == null ? 0.45f : 1f;
                w.SceneTitle.EnableInClassList("sline__title--context", n == null);
                w.SceneTitle.EnableInClassList("sline__title--removed", n != null && n.Kind == SceneChangeKind.Removed);

                // Суть правки видна, пока узел свёрнут; раскрытый показывает свойства строками ниже.
                w.SceneDetail.text = n != null && (collapsed || !expandable) ? Summary(n) : string.Empty;

                if (n == null)
                {
                    w.SceneSign.text = string.Empty;
                }
                else
                {
                    w.SceneSign.text = n.Kind == SceneChangeKind.Added ? "+"
                                     : n.Kind == SceneChangeKind.Removed ? "−" : "~";
                    Ui.SetStatusText(w.SceneSign,
                        n.Kind == SceneChangeKind.Added ? GitFileStatus.Added
                        : n.Kind == SceneChangeKind.Removed ? GitFileStatus.Deleted
                        : GitFileStatus.Modified);
                }

                // Скрытое не замалчивается: служебные свойства посчитаны и названы.
                w.SceneNote.text = n != null && n.HiddenProps > 0 ? L.F("+{0} internal", n.HiddenProps) : string.Empty;

                var objectNote = n != null && ObjectNote != null && _gitPath != null ? ObjectNote(ObjectRefOf(row)) : null;
                w.SceneComments.text = objectNote ?? string.Empty;
                w.SceneComments.style.display = string.IsNullOrEmpty(objectNote) ? DisplayStyle.None : DisplayStyle.Flex;

                w.SceneBox.style.paddingLeft = 4f + row.Depth * 14f;
                w.SceneBox.tooltip = n == null
                    ? L.T("The object itself did not change — its changed children are here")
                    : settings
                        ? SettingsRowTooltip(type)
                    : !component
                        ? L.T("Double-click to select the object in the scene")
                        : L.T("Double-click to select the object and expand only this component");
                return;
            }

            if (isProp)
            {
                var p = row.Prop;
                w.PropPath.text = p.Path;
                BindValue(w.PropOld, w.PropOldSwatch, w.PropOldIcon, p.Old);
                BindValue(w.PropNew, w.PropNewSwatch, w.PropNewIcon, p.New);
                // Под именем узла: отступ родителя, треугольник и иконка.
                w.PropBox.style.paddingLeft = 4f + Math.Max(0, row.Depth - 1) * 14f + 33f;
                return;
            }

            if (isHunk)
            {
                var h = row.Hunk;

                w.Check.style.display = _selectable ? DisplayStyle.Flex : DisplayStyle.None;
                if (_selectable)
                    w.Check.Set(GetHunkState != null ? GetHunkState(_change, h) : CheckState.On);

                w.HunkTitle.text = string.IsNullOrEmpty(h.Heading)
                    ? h.Header
                    : h.Header + "   " + h.Heading;
                w.HunkStats.text = string.Format("+{0} −{1}", h.Added, h.Removed);

                // Откат правит файл на диске, поэтому только там, где патч применим.
                w.Discard.style.display = _selectable ? DisplayStyle.Flex : DisplayStyle.None;
                return;
            }

            var line = row.Line;

            // Отмечать можно только изменённые строки: контекст есть с обеих
            // сторон и в решение не входит. Флажок при этом остаётся в потоке
            // раскладки — иначе номера строк прыгали бы влево-вправо.
            bool lineSelectable = _selectable && _lineLevel && GitPatchBuilder.IsSelectable(line);
            // Флажок строк выключен в настройках — прячем совсем, а не оставляем
            // пустое место: без него строка diff начинается ровно у номеров.
            w.LineCheck.style.display = _lineLevel ? DisplayStyle.Flex : DisplayStyle.None;
            w.LineCheck.style.visibility = lineSelectable ? Visibility.Visible : Visibility.Hidden;
            if (lineSelectable)
            {
                bool on = IsLineSelected == null || IsLineSelected(_change, row.OwnerHunk, row.LineIndex);
                w.LineCheck.Set(on ? CheckState.On : CheckState.Off);
            }

            w.Old.text = line.OldNumber > 0 ? line.OldNumber.ToString() : string.Empty;
            w.New.text = line.NewNumber > 0 ? line.NewNumber.ToString() : string.Empty;

            var text = line.Text ?? string.Empty;
            List<SyntaxSpan> spans;
            _syntax.TryGetValue(line, out spans);

            if (line.HasMark && line.MarkEnd <= text.Length)
            {
                w.TextPre.text = Rich(text, spans, 0, line.MarkStart);
                w.TextMark.text = Rich(text, spans, line.MarkStart, line.MarkEnd);
                w.TextPost.text = Rich(text, spans, line.MarkEnd, text.Length);
            }
            else
            {
                w.TextPre.text = Rich(text, spans, 0, text.Length);
                w.TextMark.text = string.Empty;
                w.TextPost.text = string.Empty;
            }

            var lineNote = LineNote != null && _gitPath != null ? LineNote(LineRefOf(row)) : null;
            w.LineNote.text = lineNote ?? string.Empty;
            w.LineNote.style.display = string.IsNullOrEmpty(lineNote) ? DisplayStyle.None : DisplayStyle.Flex;

            bool added = line.Kind == DiffLineKind.Added;
            w.LineBox.EnableInClassList("dline--add", added);
            w.LineBox.EnableInClassList("dline--del", line.Kind == DiffLineKind.Removed);
            w.TextMark.EnableInClassList("dline__seg--mark-add", added);
        }

        // -------------------------------------------------------- действия ---

        private void Discard(Row row)
        {
            if (row == null || row.Hunk == null || _diff == null || _change == null) return;

            if (!EditorUtility.DisplayDialog(
                    L.T("Discard Hunk"),
                    L.F("The changes in this hunk will be lost for good — they are not saved anywhere.\n\n{0}",
                        _change.ProjectPath),
                    L.T("Discard"), L.T("Cancel")))
                return;

            var change = _change;
            var hunk = row.Hunk;

            _host.Run(L.T("Discarding hunk"), async () =>
            {
                // Правим файл на диске сами, а не через `git apply`: тот на Windows
                // спотыкается о переводы строк, см. GitPatchBuilder.
                var gitPath = GitRepository.ToGitPath(change.ProjectPath);
                var worktree = GitOperations.ReadWorktreeText(gitPath);

                // Откат — тот же расчёт, что и для индекса: снимаем всё в этом
                // фрагменте, остальные оставляем как есть, и пишем результат на диск.
                var content = GitPatchBuilder.BuildStagedText(
                    worktree, _diff, (h, i) => !ReferenceEquals(h, hunk));

                if (content == null || !GitOperations.WriteWorktreeText(gitPath, content))
                {
                    _host.SetStatus(L.T("Could not discard the hunk: the file was not read or not written."), true);
                    return;
                }

                AssetDatabase.Refresh();
                _host.SetStatus(L.T("Hunk discarded"), false);

                await GitStatusCache.RefreshAsync();
                if (FileChanged != null) FileChanged();

                // Файл на диске переписан — отпечаток пересчитываем сами, не
                // дожидаясь, пока список изменений придёт со следующим тактом.
                _key = _change != null ? KeyOf(_change) : null;
                Reload(_key);
            });
        }
    }
}
