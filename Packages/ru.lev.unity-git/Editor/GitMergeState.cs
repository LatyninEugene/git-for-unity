using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Lev.Git
{
    public enum GitOperationKind
    {
        /// <summary>Операции нет. Конфликты при этом бывают — после stash pop.</summary>
        None = 0,
        Merge,
        Rebase,
        CherryPick,
        Revert
    }

    /// <summary>
    /// Какая операция git сейчас прервана конфликтом.
    ///
    /// Читается из файлов в .git, а не вызовом git: так дёшево спросить на
    /// каждом обновлении статуса, и git для этого не нужен вовсе. Файлы те же,
    /// по которым своё состояние узнаёт сам git: MERGE_HEAD, каталоги
    /// rebase-merge и rebase-apply, CHERRY_PICK_HEAD, REVERT_HEAD.
    ///
    /// Главная тонкость — стороны. В слиянии «ours» — моя ветка. В rebase
    /// наоборот: HEAD стоит на чужой ветке, а переносится МОЙ коммит, и git
    /// кладёт его третьей стадией. Показывать человеку git-овские «ours» и
    /// «theirs» значит перепутать ему стороны ровно в rebase, поэтому пакет
    /// везде говорит «моё» и «их», а номер стадии выбирает здесь.
    /// </summary>
    public sealed class GitOperationState
    {
        public GitOperationKind Kind;

        /// <summary>Коммит, который вливается или переносится.</summary>
        public string IncomingSha;

        /// <summary>Имя сливаемой ветки, если его удалось узнать из сообщения слияния.</summary>
        public string IncomingName;

        /// <summary>В rebase — переносимая ветка.</summary>
        public string HeadName;

        /// <summary>В rebase — на что переносим.</summary>
        public string OntoSha;

        /// <summary>Первая строка готового сообщения коммита.</summary>
        public string Message;

        /// <summary>В rebase — номер текущего коммита и сколько всего.</summary>
        public int Step, Total;

        public bool InProgress => Kind != GitOperationKind.None;

        /// <summary>Номер стадии индекса с моей версией.</summary>
        public int MineStage => Kind == GitOperationKind.Rebase ? 3 : 2;

        /// <summary>Номер стадии индекса с их версией.</summary>
        public int TheirsStage => Kind == GitOperationKind.Rebase ? 2 : 3;

        public string Title
        {
            get
            {
                switch (Kind)
                {
                    case GitOperationKind.Merge:
                        return IncomingName != null ? L.F("Merge of branch “{0}”", IncomingName) : L.F("Merge of {0}", Short(IncomingSha));
                    case GitOperationKind.Rebase:
                        var what = HeadName != null ? L.F("Rebase of branch “{0}”", HeadName) : L.T("Rebase of commits");
                        return Total > 0 ? L.F("{0} — commit {1} of {2}", what, Step, Total) : what;
                    case GitOperationKind.CherryPick:
                        return L.F("Cherry-pick of commit {0}", Short(IncomingSha));
                    case GitOperationKind.Revert:
                        return L.F("Revert of commit {0}", Short(IncomingSha));
                    default:
                        return L.T("Applying changes with conflicts");
                }
            }
        }

        /// <summary>Откуда моя версия — подпись колонки.</summary>
        public string MineLabel
        {
            get
            {
                switch (Kind)
                {
                    case GitOperationKind.Rebase: return L.T("Mine · my commit");
                    case GitOperationKind.None: return L.T("Mine · working tree");
                    default: return L.T("Mine · current branch");
                }
            }
        }

        public string TheirsLabel
        {
            get
            {
                switch (Kind)
                {
                    case GitOperationKind.Merge: return IncomingName != null ? L.F("Theirs · “{0}”", IncomingName) : L.F("Theirs · {0}", Short(IncomingSha));
                    case GitOperationKind.Rebase: return L.F("Theirs · {0}", Short(OntoSha));
                    case GitOperationKind.CherryPick:
                    case GitOperationKind.Revert: return L.F("Theirs · commit {0}", Short(IncomingSha));
                    default: return L.T("Theirs · stash");
                }
            }
        }

        /// <summary>Можно ли продолжить операцию командой git — у stash pop такой нет.</summary>
        public bool CanContinue => Kind != GitOperationKind.None;

        public bool CanSkip => Kind == GitOperationKind.Rebase || Kind == GitOperationKind.CherryPick;

        public static string Short(string sha)
        {
            return string.IsNullOrEmpty(sha) ? "?" : (sha.Length > 7 ? sha.Substring(0, 7) : sha);
        }

        // --------------------------------------------------------- чтение ---

        /// <summary>
        /// Каталог .git. В рабочем дереве, созданном `git worktree`, и в
        /// подмодуле .git — файл со строкой «gitdir: путь».
        /// </summary>
        public static string ResolveGitDir(string repoRoot)
        {
            if (string.IsNullOrEmpty(repoRoot)) return null;

            var dotGit = Path.Combine(repoRoot, ".git");
            if (Directory.Exists(dotGit)) return dotGit;
            if (!File.Exists(dotGit)) return null;

            try
            {
                foreach (var line in File.ReadAllLines(dotGit))
                {
                    if (!line.StartsWith("gitdir:", StringComparison.Ordinal)) continue;
                    var path = line.Substring("gitdir:".Length).Trim();
                    if (!Path.IsPathRooted(path)) path = Path.GetFullPath(Path.Combine(repoRoot, path));
                    return Directory.Exists(path) ? path : null;
                }
            }
            catch { }

            return null;
        }

        public static GitOperationState Read(string gitDir)
        {
            var state = new GitOperationState();
            if (string.IsNullOrEmpty(gitDir) || !Directory.Exists(gitDir)) return state;

            // rebase проверяется первым: во время него git сам делает
            // cherry-pick и тоже оставляет CHERRY_PICK_HEAD.
            var rebaseMerge = Path.Combine(gitDir, "rebase-merge");
            var rebaseApply = Path.Combine(gitDir, "rebase-apply");

            if (Directory.Exists(rebaseMerge) || Directory.Exists(rebaseApply))
            {
                bool merge = Directory.Exists(rebaseMerge);
                var dir = merge ? rebaseMerge : rebaseApply;

                state.Kind = GitOperationKind.Rebase;
                state.HeadName = BranchName(ReadLine(Path.Combine(dir, "head-name")));
                state.OntoSha = ReadLine(Path.Combine(dir, "onto"));
                state.Step = ReadInt(Path.Combine(dir, merge ? "msgnum" : "next"));
                state.Total = ReadInt(Path.Combine(dir, merge ? "end" : "last"));
                state.IncomingSha = ReadLine(Path.Combine(gitDir, "REBASE_HEAD"));
                state.Message = FirstLine(ReadAll(Path.Combine(dir, "message")));
                return state;
            }

            var mergeHead = ReadLine(Path.Combine(gitDir, "MERGE_HEAD"));
            if (mergeHead != null)
            {
                state.Kind = GitOperationKind.Merge;
                state.IncomingSha = mergeHead;
                state.Message = FirstLine(ReadAll(Path.Combine(gitDir, "MERGE_MSG")));
                state.IncomingName = MergeSourceName(state.Message);
                return state;
            }

            var pick = ReadLine(Path.Combine(gitDir, "CHERRY_PICK_HEAD"));
            if (pick != null)
            {
                state.Kind = GitOperationKind.CherryPick;
                state.IncomingSha = pick;
                state.Message = FirstLine(ReadAll(Path.Combine(gitDir, "MERGE_MSG")));
                return state;
            }

            var revert = ReadLine(Path.Combine(gitDir, "REVERT_HEAD"));
            if (revert != null)
            {
                state.Kind = GitOperationKind.Revert;
                state.IncomingSha = revert;
                state.Message = FirstLine(ReadAll(Path.Combine(gitDir, "MERGE_MSG")));
            }

            return state;
        }

        private static readonly Regex[] MergePatterns =
        {
            new Regex(@"^Merge (?:remote-tracking )?branch '([^']+)'"),
            new Regex(@"^Merge tag '([^']+)'"),
            new Regex(@"^Merge commit '([^']+)'")
        };

        /// <summary>«Merge branch 'feature' into main» → feature. Язык сообщения git всегда английский.</summary>
        public static string MergeSourceName(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            foreach (var r in MergePatterns)
            {
                var m = r.Match(message);
                if (m.Success) return m.Groups[1].Value;
            }
            return null;
        }

        private static string BranchName(string headName)
        {
            if (string.IsNullOrEmpty(headName) || headName == "detached HEAD") return null;
            return headName.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? headName.Substring("refs/heads/".Length) : headName;
        }

        private static string ReadAll(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        private static string ReadLine(string path)
        {
            var s = FirstLine(ReadAll(path));
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static int ReadInt(string path)
        {
            int n;
            return int.TryParse(ReadLine(path), out n) ? n : 0;
        }

        private static string FirstLine(string text)
        {
            if (text == null) return null;
            int nl = text.IndexOf('\n');
            return (nl >= 0 ? text.Substring(0, nl) : text).Trim();
        }
    }

    public enum ConflictShape
    {
        /// <summary>Файл изменили обе стороны.</summary>
        BothModified,

        /// <summary>Обе стороны добавили файл с одним путём.</summary>
        BothAdded,

        /// <summary>Я удалил, они изменили.</summary>
        RemovedByMine,

        /// <summary>Они удалили, я изменил.</summary>
        RemovedByTheirs,

        /// <summary>Удалили обе стороны, но git всё равно не свёл — бывает при переименованиях.</summary>
        RemovedByBoth,

        AddedByMine,
        AddedByTheirs
    }

    /// <summary>Что лежит в файле: от этого зависит, чем его разрешать.</summary>
    public enum ConflictContent
    {
        /// <summary>Сцена, префаб, ассет, мета — сливаются своим движком.</summary>
        UnityYaml,

        /// <summary>Код и прочий текст — во внешний инструмент.</summary>
        Text,

        /// <summary>Бинарник или указатель LFS — только сторона целиком.</summary>
        Binary
    }

    /// <summary>Конфликтный файл: стадии индекса 1–3.</summary>
    public sealed class ConflictStages
    {
        public string GitPath;

        /// <summary>Идентификатор содержимого по номеру стадии; [0] не используется.</summary>
        public readonly string[] Blob = new string[4];
        public readonly string[] Mode = new string[4];

        public bool Has(int stage) { return Blob[stage] != null; }
    }

    public static class GitConflictParser
    {
        /// <summary>
        /// Разбор `git ls-files -u -z`: «режим объект стадия\tпуть\0».
        /// Путь — после табуляции и целиком: -z отдаёт его без кавычек.
        /// </summary>
        public static List<ConflictStages> ParseUnmerged(string raw)
        {
            var list = new List<ConflictStages>();
            if (string.IsNullOrEmpty(raw)) return list;

            var byPath = new Dictionary<string, ConflictStages>(StringComparer.Ordinal);

            foreach (var record in raw.Split('\0'))
            {
                if (record.Length == 0) continue;

                int tab = record.IndexOf('\t');
                if (tab < 0) continue;

                var head = record.Substring(0, tab).Split(' ');
                var path = record.Substring(tab + 1);
                if (head.Length < 3) continue;

                int stage;
                if (!int.TryParse(head[2], out stage) || stage < 1 || stage > 3) continue;

                ConflictStages entry;
                if (!byPath.TryGetValue(path, out entry))
                {
                    entry = new ConflictStages { GitPath = path };
                    byPath[path] = entry;
                    list.Add(entry);
                }

                entry.Mode[stage] = head[0];
                entry.Blob[stage] = head[1];
            }

            return list;
        }

        public static ConflictShape Shape(ConflictStages s, GitOperationState state)
        {
            bool b = s.Has(1), m = s.Has(state.MineStage), t = s.Has(state.TheirsStage);

            if (m && t) return b ? ConflictShape.BothModified : ConflictShape.BothAdded;
            if (b && t) return ConflictShape.RemovedByMine;
            if (b && m) return ConflictShape.RemovedByTheirs;
            if (m) return ConflictShape.AddedByMine;
            if (t) return ConflictShape.AddedByTheirs;
            return ConflictShape.RemovedByBoth;
        }

        public static string ShapeName(ConflictShape shape)
        {
            switch (shape)
            {
                case ConflictShape.BothModified: return L.T("changed by both sides");
                case ConflictShape.BothAdded: return L.T("added by both sides");
                case ConflictShape.RemovedByMine: return L.T("deleted by me, changed by them");
                case ConflictShape.RemovedByTheirs: return L.T("deleted by them, changed by me");
                case ConflictShape.RemovedByBoth: return L.T("deleted by both sides");
                case ConflictShape.AddedByMine: return L.T("only on my side");
                default: return L.T("only on their side");
            }
        }

        private static readonly HashSet<string> YamlExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".unity", ".prefab", ".asset", ".mat", ".meta", ".anim", ".controller", ".overrideController",
            ".mask", ".mixer", ".playable", ".preset", ".physicMaterial", ".physicsMaterial2D", ".lighting",
            ".guiskin", ".fontsettings", ".flare", ".giparams", ".renderTexture", ".spriteatlas",
            ".spriteatlasv2", ".terrainlayer", ".signal", ".brush", ".shadervariants", ".scenetemplate", ".cubemap"
        };

        /// <summary>Unity-YAML по расширению — без чтения содержимого.</summary>
        public static bool IsYamlPath(string path)
        {
            return YamlExtensions.Contains(Path.GetExtension(path ?? string.Empty));
        }

        /// <summary>
        /// Что внутри. Расширение подсказывает, но решает содержимое: ассет с
        /// Force Binary сериализацией лежит под тем же .asset, что и текстовый.
        /// </summary>
        public static ConflictContent Classify(string path, string sample)
        {
            if (sample != null)
            {
                if (sample.StartsWith("version https://git-lfs.github.com/spec/", StringComparison.Ordinal))
                    return ConflictContent.Binary;
                if (sample.IndexOf('\0') >= 0) return ConflictContent.Binary;
                if (UnityYamlParser.LooksLikeUnityYaml(sample)) return ConflictContent.UnityYaml;
            }

            if (path != null && path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                return ConflictContent.UnityYaml;

            if (IsYamlPath(path) && sample != null && sample.StartsWith("%YAML", StringComparison.Ordinal))
                return ConflictContent.UnityYaml;

            return sample == null && IsYamlPath(path) ? ConflictContent.UnityYaml : ConflictContent.Text;
        }

        /// <summary>Остались ли в тексте маркеры конфликта git.</summary>
        public static bool HasConflictMarkers(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            bool open = false, middle = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("<<<<<<< ", StringComparison.Ordinal) || line == "<<<<<<<") open = true;
                else if (open && line == "=======") middle = true;
                else if (middle && (line.StartsWith(">>>>>>> ", StringComparison.Ordinal) || line == ">>>>>>>")) return true;
            }
            return false;
        }
    }
}
