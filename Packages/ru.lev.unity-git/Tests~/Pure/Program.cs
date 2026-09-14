using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Lev.Git;

internal sealed class Comp
{
    public long Id;
    public string Type;
    public int ClassId;
    public SortedDictionary<string, string> Props = new SortedDictionary<string, string>(StringComparer.Ordinal);
}

internal sealed class Go
{
    public long Id;
    public string Name;
    public long ParentGo;
    public string Pos = "{x: 0, y: 0, z: 0}";
    public List<long> ChildOrder;
    public List<Comp> Comps = new List<Comp>();
    public long T => Id + 1;
}

internal sealed class Inst
{
    public long Id;
    public SortedDictionary<string, string> Mods = new SortedDictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Сцена из настоящего Unity-YAML, собираемая из модели — как в генераторе истории.</summary>
internal sealed class SceneModel
{
    public List<Go> Gos = new List<Go>();
    public List<Inst> Insts = new List<Inst>();
    public string Fog = "0";

    public Go Find(string name) { return Gos.First(g => g.Name == name); }

    public static SceneModel Base()
    {
        var s = new SceneModel();
        var player = new Go { Id = 100, Name = "Player" };
        player.Comps.Add(new Comp { Id = 150, Type = "Rigidbody", ClassId = 54 });
        player.Comps[0].Props["m_Mass"] = "1";
        player.Comps[0].Props["m_UseGravity"] = "1";
        s.Gos.Add(player);

        var enemy = new Go { Id = 200, Name = "Enemy", Pos = "{x: 10, y: 0, z: 0}" };
        enemy.Comps.Add(new Comp { Id = 250, Type = "BoxCollider", ClassId = 65 });
        enemy.Comps[0].Props["m_IsTrigger"] = "0";
        enemy.Comps[0].Props["m_Size"] = "{x: 1, y: 1, z: 1}";
        s.Gos.Add(enemy);

        s.Gos.Add(new Go { Id = 300, Name = "Arm", ParentGo = 100 });
        s.Gos.Add(new Go { Id = 400, Name = "Leg", ParentGo = 100 });

        var inst = new Inst { Id = 700 };
        inst.Mods["m_LocalPosition.y"] = "0";
        s.Insts.Add(inst);
        return s;
    }

    public static string Make(Action<SceneModel> edit)
    {
        var s = Base();
        if (edit != null) edit(s);
        return s.Text();
    }

    public string Text()
    {
        var sb = new StringBuilder();
        sb.Append("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");
        sb.Append("--- !u!104 &2\nRenderSettings:\n  m_ObjectHideFlags: 0\n  serializedVersion: 10\n  m_Fog: " + Fog + "\n");

        foreach (var g in Gos)
        {
            sb.Append("--- !u!1 &" + g.Id + "\nGameObject:\n  m_ObjectHideFlags: 0\n  serializedVersion: 6\n  m_Component:\n");
            sb.Append("  - component: {fileID: " + g.T + "}\n");
            foreach (var c in g.Comps) sb.Append("  - component: {fileID: " + c.Id + "}\n");
            sb.Append("  m_Layer: 0\n  m_Name: " + g.Name + "\n  m_IsActive: 1\n");

            var children = g.ChildOrder ?? Gos.Where(x => x.ParentGo == g.Id).Select(x => x.Id).ToList();
            var father = g.ParentGo != 0 ? g.ParentGo + 1 : 0;

            sb.Append("--- !u!4 &" + g.T + "\nTransform:\n  m_ObjectHideFlags: 0\n  m_GameObject: {fileID: " + g.Id + "}\n");
            sb.Append("  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n  m_LocalPosition: " + g.Pos + "\n  m_LocalScale: {x: 1, y: 1, z: 1}\n");
            if (children.Count == 0) sb.Append("  m_Children: []\n");
            else
            {
                sb.Append("  m_Children:\n");
                foreach (var ch in children) sb.Append("  - {fileID: " + (ch + 1) + "}\n");
            }
            sb.Append("  m_Father: {fileID: " + father + "}\n");

            foreach (var c in g.Comps)
            {
                sb.Append("--- !u!" + c.ClassId + " &" + c.Id + "\n" + c.Type + ":\n  m_ObjectHideFlags: 0\n  m_GameObject: {fileID: " + g.Id + "}\n");
                foreach (var p in c.Props) sb.Append("  " + p.Key + ": " + p.Value + "\n");
            }
        }

        foreach (var inst in Insts)
        {
            sb.Append("--- !u!1001 &" + inst.Id + "\nPrefabInstance:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n  m_Modification:\n");
            sb.Append("    serializedVersion: 3\n    m_TransformParent: {fileID: 0}\n    m_Modifications:\n");
            foreach (var m in inst.Mods)
                sb.Append("    - target: {fileID: 900, guid: 5f5f5f, type: 3}\n      propertyPath: " + m.Key +
                          "\n      value: " + m.Value + "\n      objectReference: {fileID: 0}\n");
            sb.Append("    m_RemovedComponents: []\n  m_SourcePrefab: {fileID: 100100000, guid: 5f5f5f, type: 3}\n");
        }

        return sb.ToString();
    }
}

internal static class Program
{
    private static int _passed, _failed;

    private static void Check(bool ok, string what)
    {
        if (ok) { _passed++; return; }
        _failed++;
        Console.WriteLine("  ПРОВАЛ: " + what);
    }

    private static void Localization()
    {
        Console.WriteLine("== перевод: .po и множественные формы ==");

        const string po =
            "# комментарий\n" +
            "msgid \"\"\n" +
            "msgstr \"\"\n" +
            "\"Language: ru\\n\"\n" +
            "\"Plural-Forms: nplurals=3;\\n\"\n\n" +
            "msgid \"Commit\"\n" +
            "msgstr \"Закоммитить\"\n\n" +
            "msgctxt \"file status\"\n" +
            "msgid \"Deleted\"\n" +
            "msgstr \"Удалён\"\n\n" +
            "msgid \"Deleted\"\n" +
            "msgstr \"Удалено\"\n\n" +
            "msgid \"Branch {0} not found\"\n" +
            "msgstr \"Ветка {0} \"\n" +
            "\"не найдена\"\n\n" +
            "msgid \"Line one\\nline \\\"two\\\"\"\n" +
            "msgstr \"Строка один\\nстрока «два»\"\n\n" +
            "#, fuzzy\n" +
            "msgid \"Push\"\n" +
            "msgstr \"Толкнуть\"\n\n" +
            "msgid \"Empty\"\n" +
            "msgstr \"\"\n\n" +
            "msgid \"Broken {0}\"\n" +
            "msgstr \"Сломано {1}\"\n\n" +
            "msgid \"{0} file\"\n" +
            "msgid_plural \"{0} files\"\n" +
            "msgstr[0] \"{0} файл\"\n" +
            "msgstr[1] \"{0} файла\"\n" +
            "msgstr[2] \"{0} файлов\"\n";

        var catalog = Lev.Git.Catalog.Parse(po, "ru");
        Lev.Git.L.Apply("ru", catalog);

        Eq(Lev.Git.L.T("Commit"), "Закоммитить", "простая строка");
        Eq(Lev.Git.L.T("Unknown text"), "Unknown text", "нет перевода — английский");
        Eq(Lev.Git.L.Tc("file status", "Deleted"), "Удалён", "строка с контекстом");
        Eq(Lev.Git.L.T("Deleted"), "Удалено", "та же строка без контекста — своя");
        Eq(Lev.Git.L.F("Branch {0} not found", "main"), "Ветка main не найдена", "формат и строки-продолжения");
        Eq(Lev.Git.L.T("Line one\nline \"two\""), "Строка один\nстрока «два»", "экранирование \\n и кавычек");
        Eq(Lev.Git.L.T("Push"), "Push", "fuzzy пропускается");
        Eq(Lev.Git.L.T("Empty"), "Empty", "пустой перевод пропускается");
        Eq(Lev.Git.L.F("Broken {0}", 5), "Broken 5", "сломанный формат перевода — оригинал, без исключения");
        Eq(Lev.Git.L.N("{0} file", "{0} files", 1), "1 файл", "1 файл");
        Eq(Lev.Git.L.N("{0} file", "{0} files", 3), "3 файла", "3 файла");
        Eq(Lev.Git.L.N("{0} file", "{0} files", 11), "11 файлов", "11 файлов");
        Eq(Lev.Git.L.N("{0} file", "{0} files", 21), "21 файл", "21 файл");
        Eq(Lev.Git.L.N("{0} file", "{0} files", 25), "25 файлов", "25 файлов");
        Eq(Lev.Git.L.N("{0} dir", "{0} dirs", 2), "2 dirs", "нет перевода множественного — английское правило");

        Lev.Git.L.Apply("en", null);
        Eq(Lev.Git.L.T("Commit"), "Commit", "английский — без каталога");
        Eq(Lev.Git.L.N("{0} file", "{0} files", 1), "1 file", "английское единственное");
        Eq(Lev.Git.L.N("{0} file in {1}", "{0} files in {1}", 4, "Assets"), "4 files in Assets", "дополнительные аргументы — с {1}");
    }

    private static void Redaction()
    {
        Console.WriteLine("== отчёт: вычистка секретов ==");
        const string token = "glpat-AbCdEf1234567890xyzW";

        Eq(Lev.Git.Diagnostics.Redactor.Clean("fatal: unable to access 'http://oauth2:" + token + "@gitlab.local/g/r.git/'"),
           "fatal: unable to access 'http://***@gitlab.local/g/r.git/'", "логин и токен в адресе");
        Eq(Lev.Git.Diagnostics.Redactor.Clean("PRIVATE-TOKEN: abc123secret"), "PRIVATE-TOKEN: ***", "заголовок PRIVATE-TOKEN");
        Eq(Lev.Git.Diagnostics.Redactor.Clean("Authorization: Bearer xyz.abc.def"), "Authorization: ***", "заголовок Authorization");
        Eq(Lev.Git.Diagnostics.Redactor.Clean("token is " + token), "token is ***", "токен GitLab по виду");
        Eq(Lev.Git.Diagnostics.Redactor.Clean("GET /api/v4/user?private_token=qwerty&x=1"), "GET /api/v4/user?private_token=***&x=1", "токен в запросе");
        Eq(Lev.Git.Diagnostics.Redactor.Clean("protocol=http\nhost=gitlab\nusername=me\npassword=hunter2hunter"),
           "protocol=http\nhost=gitlab\nusername=me\npassword=***", "вывод git credential fill");

        Lev.Git.Diagnostics.Redactor.RegisterSecret("my-own-secret-value");
        Eq(Lev.Git.Diagnostics.Redactor.Clean("value my-own-secret-value in text"), "value *** in text", "известный секрет по точному совпадению");
        Eq(Lev.Git.Diagnostics.Redactor.Clean("status: 3 files changed"), "status: 3 files changed", "обычный текст не трогается");

        var hidden = Lev.Git.Diagnostics.Redactor.HidePaths(
            "C:/Work/Game/Assets/Levels/Boss.unity and C:\\Work\\Game\\Library and Assets/Levels/Boss.unity",
            "C:/Work/Game", "C:/Users/me");
        var h1 = Lev.Git.Diagnostics.Redactor.Hash("Levels");
        var h2 = Lev.Git.Diagnostics.Redactor.Hash("Boss");
        Eq(hidden, "<project>/Assets/" + h1 + "/" + h2 + ".unity and <project>\\Library and Assets/" + h1 + "/" + h2 + ".unity",
           "пути: корень меткой, части хэшем, расширение остаётся");

        Eq(Lev.Git.Diagnostics.Redactor.HideServers("remote http://gitlab.corp.local:8080/g/r.git and git@gitlab.corp.local:g/r.git"),
           "remote http://<server>:8080/g/r.git and git@<server>:g/r.git", "адрес сервера скрыт");

        string H(string s) => Lev.Git.Diagnostics.Redactor.Hash(s);

        Eq(Lev.Git.Diagnostics.Redactor.HidePaths(
               "git show \"HEAD:Assets/Samples/Git for Unity/Example.level\" then Assets/Samples/Other.cs", null, null),
           "git show \"HEAD:Assets/" + H("Samples") + "/" + H("Git for Unity") + "/" + H("Example") + ".level\" then Assets/" +
           H("Samples") + "/" + H("Other") + ".cs",
           "путь в кавычках с пробелами скрыт целиком, вместе с «HEAD:»");

        Eq(Lev.Git.Diagnostics.Redactor.HideNames(
               "git push -u \"origin\" \"main\" to origin/main; mainline stays", new[] { "main", "origin/main" }),
           "git push -u \"origin\" \"#" + H("main") + "\" to #" + H("origin/main") + "; mainline stays",
           "имена веток скрыты целиком, части других слов не тронуты");
    }

    private static void ExitCodes()
    {
        Console.WriteLine("== журнал: ожидаемые коды выхода git ==");
        Check(!Lev.Git.GitExitCodes.IsFailure("config --get commit.template", 1), "config --get: 1 — настройка не задана");
        Check(!Lev.Git.GitExitCodes.IsFailure("config --get-regexp ^merge\\..*\\.driver$", 1), "config --get-regexp: 1 — ничего не нашлось");
        Check(!Lev.Git.GitExitCodes.IsFailure("-c core.quotepath=false diff -U3 --no-index -- /dev/null \"a b\"", 1), "diff --no-index: 1 — файлы различаются");
        Check(!Lev.Git.GitExitCodes.IsFailure("check-ignore -- \"a.meta\"", 1), "check-ignore: 1 — ничего не игнорируется");
        Check(!Lev.Git.GitExitCodes.IsFailure("merge-file -p a b c", 3), "merge-file: код — число конфликтов");
        Check(!Lev.Git.GitExitCodes.IsFailure("rev-parse --verify --quiet refs/remotes/origin/main", 1), "rev-parse --verify --quiet: 1 — ссылки нет");
        Check(Lev.Git.GitExitCodes.IsFailure("push --progress -u \"origin\" \"main\"", 1), "push: 1 — сбой");
        Check(Lev.Git.GitExitCodes.IsFailure("config --get core.editor", 128), "config: 128 — сбой");
        Check(Lev.Git.GitExitCodes.IsFailure("diff -U3 HEAD -- a", 1), "diff без --no-index: 1 — сбой");
        Check(Lev.Git.GitExitCodes.IsFailure("show HEAD:a", -1), "-1 — сбой всегда");
        Check(!Lev.Git.GitExitCodes.IsFailure("status", 0), "0 — не сбой");

        Console.WriteLine("== журнал: вывод git без прогресса ==");
        const string push =
            "Enumerating objects: 79, done.\n" +
            "Counting objects:   1% (1/79)\rCounting objects:  50% (40/79)\rCounting objects: 100% (79/79), done.\n" +
            "Delta compression using up to 32 threads\n" +
            "Writing objects: 100% (60/60), 1.20 MiB | 4.00 MiB/s, done.\n" +
            "Total 60 (delta 10), reused 0 (delta 0)\n" +
            "remote: GitLab: You are not allowed to force push code to a protected branch on this project.\n" +
            "To http://server/group/repo.git\n" +
            " ! [remote rejected] main -> main (pre-receive hook declined)\n" +
            "error: failed to push some refs to 'http://server/group/repo.git'\n";

        var clean = Lev.Git.GitOutput.WithoutProgress(push);
        Eq(clean,
           "remote: GitLab: You are not allowed to force push code to a protected branch on this project.\n" +
           "To http://server/group/repo.git\n" +
           " ! [remote rejected] main -> main (pre-receive hook declined)\n" +
           "error: failed to push some refs to 'http://server/group/repo.git'",
           "прогресс убран, причина отказа осталась");
        Check(Lev.Git.GitOutput.IsProtectedBranchRefusal(clean), "отказ защищённой ветки узнаётся");
        Check(!Lev.Git.GitOutput.IsProtectedBranchRefusal(" ! [rejected] main -> main (non-fast-forward)"), "обычный отказ — не защита ветки");

        var longText = new string('a', 3000) + "\nERROR AT THE END";
        var shortened = Lev.Git.GitOutput.Shorten(longText, 1000, "…");
        Check(shortened.Length < 1100 && shortened.EndsWith("ERROR AT THE END", StringComparison.Ordinal), "длинный вывод укорачивается с середины, конец на месте");
        Eq(string.Join("|", Lev.Git.GitOutput.LastLines("a\n\nb\nc\nd", 2)), "c|d", "последние непустые строки");
    }

    private static void SceneSettingsGrouping()
    {
        Console.WriteLine("== diff сцены: служебные документы отдельно от объектов ==");

        const string scene =
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n" +
            "--- !u!29 &1\nOcclusionCullingSettings:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n" +
            "--- !u!104 &2\nRenderSettings:\n  m_ObjectHideFlags: 0\n  m_Fog: 0\n" +
            "--- !u!1 &10\nGameObject:\n  m_ObjectHideFlags: 0\n  m_Component:\n  - component: {fileID: 11}\n  m_Name: Main Camera\n" +
            "--- !u!4 &11\nTransform:\n  m_ObjectHideFlags: 0\n  m_GameObject: {fileID: 10}\n  m_Father: {fileID: 0}\n" +
            "--- !u!1001 &50\nPrefabInstance:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n  m_Modification:\n    serializedVersion: 3\n" +
            "    m_TransformParent: {fileID: 0}\n    m_Modifications: []\n  m_SourcePrefab: {fileID: 100100000, guid: 5f5f5f, type: 3}\n" +
            "--- !u!4 &12 stripped\nTransform:\n  m_CorrespondingSourceObject: {fileID: 400000, guid: 5f5f5f, type: 3}\n  m_PrefabInstance: {fileID: 50}\n" +
            "--- !u!1660057539 &9223372036854775807\nSceneRoots:\n  m_ObjectHideFlags: 0\n  m_Roots:\n  - {fileID: 11}\n  - {fileID: 12}\n";

        // Новая сцена целиком: так она выглядит в первом коммите.
        var roots = SceneDiffBuilder.Build(string.Empty, scene);
        var settingsTypes = roots.Where(SceneDiffBuilder.IsSceneSettings)
                                 .Select(n => (n.NewDoc ?? n.OldDoc).TypeName)
                                 .OrderBy(t => t, StringComparer.Ordinal);
        Eq(string.Join(",", settingsTypes), "OcclusionCullingSettings,RenderSettings,SceneRoots", "служебные документы узнаются по типу и полям");
        Check(!roots.Exists(n => SceneDiffBuilder.IsSceneSettings(n) && (n.NewDoc ?? n.OldDoc).TypeName == "Transform"), "stripped Transform экземпляра — не настройки");
        Check(!roots.Exists(n => SceneDiffBuilder.IsSceneSettings(n) && (n.NewDoc ?? n.OldDoc).TypeName == "PrefabInstance"), "экземпляр префаба — не настройки");
        Check(!roots.Exists(n => SceneDiffBuilder.IsSceneSettings(n) && (n.NewDoc ?? n.OldDoc).TypeName == "GameObject"), "GameObject — не настройки");

        int objects, props, settings;
        SceneDiffBuilder.Count(roots, out objects, out props, out settings);
        Eq(settings, 3, "настройки считаются отдельно");
        Check(objects >= 2, "объекты и компоненты без настроек: камера, экземпляр");

        // Изменился только туман: объектов нет, одна настройка, одно свойство.
        var fog = SceneDiffBuilder.Build(scene, scene.Replace("m_Fog: 0", "m_Fog: 1"));
        SceneDiffBuilder.Count(fog, out objects, out props, out settings);
        Check(objects == 0 && settings == 1 && props == 1, "правка RenderSettings — не «изменён объект»");
    }

    private static void TimelineParsing()
    {
        Console.WriteLine("== журнал: этапы сетевой команды (trace2) ==");

        Check(Lev.Git.GitTimeline.ShouldTrace("push --progress"), "push — этапы записываются");
        Check(Lev.Git.GitTimeline.ShouldTrace("-c core.quotepath=false fetch --prune origin"), "fetch после -c");
        Check(Lev.Git.GitTimeline.ShouldTrace("-C \"C:/a b\" ls-remote origin"), "ls-remote после -C с пробелом в пути");
        Check(!Lev.Git.GitTimeline.ShouldTrace("--no-optional-locks status --porcelain=v2"), "status — без этапов");
        Check(!Lev.Git.GitTimeline.ShouldTrace("lfs locks --verify --json"), "lfs locks — без этапов");
        Check(!Lev.Git.GitTimeline.ShouldTrace("log --grep push"), "слово push в аргументах — не подкоманда");
        Check(Lev.Git.GitTimeline.Summarize("") == null && Lev.Git.GitTimeline.Summarize("not json\n{}") == null, "пусто или чужой текст — без разбивки");

        // Как у обрыва push на GitHub: ssh открыт, хук pre-push (git lfs) идёт 48 с, упаковка падает, код 128.
        var events = string.Join("\n", new[]
        {
            @"{""event"":""version"",""sid"":""R"",""time"":""2026-09-14T12:07:37.000000Z"",""evt"":""3"",""exe"":""2.45.1.windows.1""}",
            @"{""event"":""start"",""sid"":""R"",""time"":""2026-09-14T12:07:37.000000Z"",""t_abs"":0.01,""argv"":[""C:\\Program Files\\Git\\mingw64\\bin\\git.exe"",""push"",""--progress""]}",
            @"{""event"":""child_start"",""sid"":""R"",""time"":""2026-09-14T12:07:37.050000Z"",""child_id"":0,""child_class"":""transport/ssh"",""use_shell"":true,""argv"":[""ssh"",""-o"",""SendEnv=GIT_PROTOCOL"",""git@github.com"",""git-receive-pack 'u/r.git'""]}",
            @"{""event"":""child_start"",""sid"":""R"",""time"":""2026-09-14T12:07:38.200000Z"",""child_id"":1,""child_class"":""hook"",""hook_name"":""pre-push"",""use_shell"":false,""argv"":["".git/hooks/pre-push"",""origin"",""git@github.com:u/r.git""]}",
            @"{""event"":""start"",""sid"":""R/L"",""time"":""2026-09-14T12:07:38.300000Z"",""t_abs"":0.004,""argv"":[""git"",""lfs"",""pre-push"",""origin"",""git@github.com:u/r.git""]}",
            @"{""event"":""start"",""sid"":""R/V"",""time"":""2026-09-14T12:07:38.310000Z"",""t_abs"":0.004,""argv"":[""git"",""--version""]}",
            @"{""event"":""exit"",""sid"":""R/V"",""time"":""2026-09-14T12:07:38.360000Z"",""t_abs"":0.05,""code"":0}",
            @"{""event"":""child_start"",""sid"":""R/L"",""time"":""2026-09-14T12:07:38.310000Z"",""child_id"":0,""child_class"":""dashed"",""use_shell"":false,""argv"":[""git-lfs"",""pre-push"",""origin"",""git@github.com:u/r.git""]}",
            @"{""event"":""child_exit"",""sid"":""R/L"",""time"":""2026-09-14T12:08:26.310000Z"",""child_id"":0,""pid"":1,""code"":0,""t_rel"":48.0}",
            @"{""event"":""exit"",""sid"":""R/L"",""time"":""2026-09-14T12:08:26.320000Z"",""t_abs"":48.02,""code"":0}",
            @"{""event"":""child_exit"",""sid"":""R"",""time"":""2026-09-14T12:08:26.400000Z"",""child_id"":1,""pid"":2,""code"":0,""t_rel"":48.2}",
            @"{""event"":""child_start"",""sid"":""R"",""time"":""2026-09-14T12:08:26.400000Z"",""child_id"":2,""child_class"":""?"",""use_shell"":false,""argv"":[""git"",""pack-objects"",""--stdout""]}",
            @"{""event"":""start"",""sid"":""R/P"",""time"":""2026-09-14T12:08:26.450000Z"",""t_abs"":0.004,""argv"":[""git"",""pack-objects"",""--stdout""]}",
            @"{""event"":""exit"",""sid"":""R/P"",""time"":""2026-09-14T12:08:27.450000Z"",""t_abs"":1.0,""code"":128}",
            @"{""event"":""child_exit"",""sid"":""R"",""time"":""2026-09-14T12:08:27.500000Z"",""child_id"":2,""pid"":3,""code"":128,""t_rel"":1.1}",
            @"{""event"":""exit"",""sid"":""R"",""time"":""2026-09-14T12:08:28.000000Z"",""t_abs"":51.0,""code"":128}"
        });

        var t = Lev.Git.GitTimeline.Summarize(events) ?? string.Empty;
        var first = t.Split('\n')[0];
        Check(first.Contains("git push --progress → 128") && first.Contains("51.00 s"), "первая строка — сама команда, её длительность и код");
        Check(t.Contains("hook pre-push → 0") && t.Contains("48.20 s"), "хук pre-push и сколько он шёл");
        Check(t.Contains("git lfs pre-push") && t.Contains("git-lfs pre-push"), "что делал хук: git lfs и git-lfs");
        Check(t.Contains("ssh: ssh -o SendEnv=GIT_PROTOCOL git@github.com") && t.Contains("end not recorded: git exited first"), "ssh-соединение, конец которого не записан");
        Eq((t.Length - t.Replace("git pack-objects", "").Length) / "git pack-objects".Length, 1, "подкоманда не дублируется запуском и своими событиями");
        Check(!t.Contains("--version"), "короткие вложенные этапы не засоряют разбивку");
        Check(t.IndexOf("hook pre-push", StringComparison.Ordinal) < t.IndexOf("git pack-objects", StringComparison.Ordinal), "этапы — по времени начала");
        Check(t.Contains("+1.20 s") && t.Contains("+49.40 s"), "смещение от начала команды");
        Check(!t.Contains("Program Files"), "путь к git.exe не попадает в строку");
    }

    private static void SshParsing()
    {
        Console.WriteLine("== ssh: разбор ssh -v и ssh-agent ==");

        const string defaults =
            "OpenSSH_9.7p1, OpenSSL 3.2.1 30 Jan 2024\n" +
            "debug1: identity file /c/Users/u/.ssh/id_rsa type 0\n" +
            "debug1: Authentications that can continue: publickey\n" +
            "debug1: Offering public key: /c/Users/u/.ssh/id_rsa RSA SHA256:ibP39OQt38azCvMZzqq46M5+d9pEEiBNIns+9AiULqI\n" +
            "debug1: Authentications that can continue: publickey\n" +
            "git@github.com: Permission denied (publickey).\n";
        var p = Lev.Git.SshOutput.ParseProbe(defaults);
        Check(!p.Authenticated && p.AcceptedKey == null, "ключ по умолчанию сервер не принял");
        Eq(p.Offered.Count, 1, "предложен один ключ");
        Eq(p.Offered.Count > 0 ? p.Offered[0] : null, "/c/Users/u/.ssh/id_rsa", "путь предложенного ключа");

        const string explicitKey =
            "debug1: Offering public key: C:\\\\Users\\\\Ivan Petrov\\\\.ssh\\\\github ED25519 SHA256:zCdoKrzeeyn1ZF1peHIGraBefUOd2SFQ05LXS7fbuqU explicit\r\n" +
            "debug1: Server accepts key: C:\\\\Users\\\\Ivan Petrov\\\\.ssh\\\\github ED25519 SHA256:zCdoKrzeeyn1ZF1peHIGraBefUOd2SFQ05LXS7fbuqU explicit\r\n" +
            "git@github.com: Permission denied (publickey).\r\n";
        p = Lev.Git.SshOutput.ParseProbe(explicitKey);
        Eq(p.AcceptedKey, "C:\\Users\\Ivan Petrov\\.ssh\\github", "принятый ключ: удвоенные слэши и пробел в пути");
        Check(!p.AcceptedFromAgent && !p.Authenticated, "ключ принят, но вход не состоялся — он под паролем");

        const string timedOut =
            "OpenSSH_9.7p1, OpenSSL 3.2.1 30 Jan 2024\n" +
            "debug1: Reading configuration data /etc/ssh/ssh_config\n" +
            "debug1: connect to address 140.82.121.4 port 22: Connection timed out\n" +
            "ssh: connect to host github.com port 22: Connection timed out\n";
        Check(Lev.Git.SshOutput.ParseProbe(timedOut).ConnectionFailed, "таймаут соединения — это сбой сети, а не ключа");
        Eq(Lev.Git.SshOutput.DiagnosticLines(timedOut).Count, 2, "в журнал — только строки о соединении и ключах");
        Eq(Lev.Git.SshOutput.DiagnosticLines(explicitKey).Count, 3, "предложенный, принятый ключ и отказ попадают в журнал");

        const string agent =
            "debug1: Server accepts key: test ED25519 SHA256:flNz4GGmhJm5hnts7ZwIXyI0EQPuDB5Ll+L50bXekJk agent\n" +
            "Authenticated to github.com ([140.82.121.4]:22) using \"publickey\".\n";
        p = Lev.Git.SshOutput.ParseProbe(agent);
        Check(p.Authenticated && p.AcceptedFromAgent, "вход ключом из агента");

        Check(Lev.Git.SshOutput.ParseProbe("Host key verification failed.\r\nfatal: Could not read from remote repository.").HostKeyFailed, "ключ сервера не подтверждён");
        Check(Lev.Git.SshOutput.ParseProbe("ssh: Could not resolve hostname gitlab.example: Name or service not known").ConnectionFailed, "сервер не найден");

        Eq(Lev.Git.SshOutput.UserFromUrl("git@github.com:u/r.git"), "git", "пользователь из scp-адреса");
        Eq(Lev.Git.SshOutput.UserFromUrl("ssh://deploy@host:2424/g/r.git"), "deploy", "пользователь из ssh://");
        Eq(Lev.Git.SshOutput.UserFromUrl("ssh://host/g/r.git"), "git", "без пользователя — git");

        string socket, pid;
        Check(Lev.Git.SshOutput.ParseAgent(
                  "SSH_AUTH_SOCK=/tmp/ssh-x35HlfP3HJkA/agent.727; export SSH_AUTH_SOCK;\nSSH_AGENT_PID=728; export SSH_AGENT_PID;\necho Agent pid 728;\n",
                  out socket, out pid)
              && socket == "/tmp/ssh-x35HlfP3HJkA/agent.727" && pid == "728", "адрес и pid из ssh-agent -s");
        Check(!Lev.Git.SshOutput.ParseAgent("Could not open a connection", out socket, out pid), "мусор вместо вывода агента");

        Eq(Lev.Git.SshOutput.Fingerprint("256 SHA256:AbC+/12 user@example.com (ED25519)\n"), "SHA256:AbC+/12 (ED25519)", "отпечаток из ssh-keygen -l");
        Eq(Lev.Git.SshOutput.Fingerprint("3072 SHA256:XyZ (RSA)"), "SHA256:XyZ (RSA)", "отпечаток без комментария");

        Check(Lev.Git.SshOutput.SamePath("/c/Users/u/.ssh/id_rsa", "C:\\Users\\u\\.ssh\\id_rsa"), "msys- и Windows-путь — один файл");
        Eq(Lev.Git.SshOutput.DisplayPath("C:\\Users\\u\\.ssh\\github", "C:\\Users\\u"), "~/.ssh/github", "путь ключа через ~");
        Eq(Lev.Git.SshOutput.DisplayPath("/c/Users/u/.ssh/id_rsa", "C:\\Users\\u\\"), "~/.ssh/id_rsa", "msys-путь ключа через ~");

        Check(Lev.Git.SshOutput.AskpassScript.StartsWith("#!/bin/sh\n", StringComparison.Ordinal) &&
              Lev.Git.SshOutput.AskpassScript.IndexOf('\r') < 0, "askpass-скрипт без CR");
    }

    private static void Eq(object actual, object expected, string what)
    {
        if (Equals(actual, expected)) { _passed++; return; }
        _failed++;
        Console.WriteLine("  ПРОВАЛ: " + what + " — ожидалось [" + expected + "], получено [" + actual + "]");
    }

    private static UnityDocument Doc(string text, long id)
    {
        return UnityScene.Build(text).ById.TryGetValue(id, out var d) ? d : null;
    }

    private static string Scene(Action<SceneModel> edit) { return SceneModel.Make(edit); }

    // ------------------------------------------------------------ разбиение ---

    private static void RoundTrip(string scratch)
    {
        Console.WriteLine("— разбиение и сборка без правок");

        var real = File.ReadAllText(Path.Combine(scratch, "head_scene.unity"));
        Eq(YamlMerge.Merge(real, real, real).Build(), real, "настоящая сцена Unity собирается байт в байт");

        var crlf = real.Replace("\n", "\r\n");
        Eq(YamlMerge.Merge(crlf, crlf, crlf).Build(), crlf, "с CRLF — тоже байт в байт");

        var b = Scene(null);
        Eq(YamlMerge.Merge(b, b, b).Build(), b, "сгенерированная сцена байт в байт");

        // Все единицы документа, собранные обратно, — тот же текст. Проверяется
        // через слияние, где обе стороны меняют разные документы.
        var m = Scene(s => s.Find("Player").Pos = "{x: 3, y: 0, z: 0}");
        var t = Scene(s => s.Fog = "1");
        Eq(YamlMerge.Merge(b, b, t).Build(), t, "их правка при моём нетронутом — ровно их файл");
        Eq(YamlMerge.Merge(b, m, b).Build(), m, "моя правка при их нетронутом — ровно мой файл");

        var file = YamlFile.Split(real);
        Check(file.Blocks.Count > 10, "в настоящей сцене найдены документы");
        Check(file.Preamble.StartsWith("%YAML"), "заголовок %YAML отделён от документов");

        const string meta = "fileFormatVersion: 2\nguid: aaa111\nTextureImporter:\n  mipmaps:\n    enableMipMap: 1\n  isReadable: 0\n";
        Eq(YamlMerge.Merge(meta, meta, meta).Build(), meta, "мета без разделителей — байт в байт");

        // Документ, где правили обе стороны, тоже собирается из своих же строк:
        // правка в одном свойстве не трогает форматирование остальных.
        var both = YamlMerge.Merge(real, real.Replace("m_Fog: 0", "m_Fog: 1"), real.Replace("m_FogMode: 3", "m_FogMode: 1"));
        Eq(both.Build(), real.Replace("m_Fog: 0", "m_Fog: 1").Replace("m_FogMode: 3", "m_FogMode: 1"),
           "две правки одного документа настоящей сцены — остальной текст не тронут");
        Eq(both.Count(YamlDocumentOutcome.Merged), 1, "ровно один документ слит по свойствам");
    }

    // ------------------------------------------------------------ сценарии ---

    private static void Scenarios()
    {
        Console.WriteLine("— сценарии слияния");
        var b = Scene(null);

        {
            var m = Scene(s => s.Find("Player").Pos = "{x: 5, y: 0, z: 0}");
            var t = Scene(s => s.Find("Player").Comps[0].Props["m_Mass"] = "9");
            var r = YamlMerge.Merge(b, m, t);
            var text = r.Build();
            Eq(r.Conflicts.Count, 0, "разные документы — без конфликтов");
            Eq(Doc(text, 101).Get("m_LocalPosition"), "{x: 5, y: 0, z: 0}", "моя позиция на месте");
            Eq(Doc(text, 150).Get("m_Mass"), "9", "их масса на месте");
            Eq(r.Count(YamlDocumentOutcome.FromMine), 1, "один документ взят у меня");
            Eq(r.Count(YamlDocumentOutcome.FromTheirs), 1, "один документ взят у них");
            Eq(MergeValidator.Check(text, r).Count, 0, "проверка чистая");
        }

        {
            var m = Scene(s => s.Find("Player").Pos = "{x: 5, y: 0, z: 0}");
            var t = Scene(s => s.Find("Player").Pos = "{x: 0, y: 7, z: 0}");
            var r = YamlMerge.Merge(b, m, t);
            Eq(r.Conflicts.Count, 0, "x у меня и y у них — не конфликт");
            Eq(Doc(r.Build(), 101).Get("m_LocalPosition"), "{x: 5, y: 7, z: 0}", "позиция собрана из обеих сторон");
            Eq(r.Count(YamlDocumentOutcome.Merged), 1, "документ помечен как слитый");
        }

        {
            var m = Scene(s => s.Find("Player").Comps[0].Props["m_Mass"] = "5");
            var t = Scene(s => s.Find("Player").Comps[0].Props["m_Mass"] = "9");
            var r = YamlMerge.Merge(b, m, t);
            Eq(r.Conflicts.Count, 1, "масса изменена по-разному — один конфликт");
            var c = r.Conflicts.FirstOrDefault();
            Eq(c?.Kind, MergeConflictKind.Property, "конфликт свойства");
            Eq(c?.Key, "m_Mass", "ключ — m_Mass");
            Eq(c?.FileId, 150L, "в документе Rigidbody");
            Eq(r.Unresolved, 1, "не решён");
            Eq(Doc(r.Build(), 150).Get("m_Mass"), "5", "в предпросмотре — моя сторона");
            c.Choice = MergeSide.Theirs;
            Eq(Doc(r.Build(), 150).Get("m_Mass"), "9", "выбрали их — 9");
            c.Choice = MergeSide.Base;
            Eq(Doc(r.Build(), 150).Get("m_Mass"), "1", "выбрали базовое — 1");
            Eq(r.Unresolved, 0, "решён");
        }

        {
            var m = Scene(s => s.Gos.Add(new Go { Id = 500, Name = "Hat", ParentGo = 100 }));
            var t = Scene(s => s.Gos.Add(new Go { Id = 600, Name = "Gun", ParentGo = 100 }));
            var r = YamlMerge.Merge(b, m, t);
            var text = r.Build();
            Eq(r.Conflicts.Count, 0, "два новых ребёнка у одного родителя — без конфликта");
            var scene = UnityScene.Build(text);
            Check(scene.GameObjects.Values.Any(g => g.Path == "Player/Hat"), "Hat в иерархии");
            Check(scene.GameObjects.Values.Any(g => g.Path == "Player/Gun"), "Gun в иерархии");
            var children = Enumerable.Range(0, 10).Select(i => Doc(text, 101).Get("m_Children[" + i + "]")).Where(v => v != null).ToList();
            Eq(children.Count, 4, "у Player четыре ребёнка");
            Eq(MergeValidator.Check(text, r).Count, 0, "проверка чистая");
        }

        {
            var e = Scene(s => { s.Gos.RemoveAll(g => g.Name == "Enemy"); });
            var t = Scene(s => s.Find("Enemy").Comps[0].Props["m_IsTrigger"] = "1");
            var r = YamlMerge.Merge(b, e, t);
            Eq(r.Conflicts.Count, 1, "удалил объект, они правили его коллайдер — один конфликт");
            var c = r.Conflicts.FirstOrDefault();
            Eq(c?.Kind, MergeConflictKind.RemovedByMine, "вид — удалено мной");
            Eq(c?.FileId, 250L, "конфликт на коллайдере");

            c.Choice = MergeSide.Mine;
            var gone = r.Build();
            Check(!gone.Contains("&200") && !gone.Contains("&250"), "выбрали моё — объекта нет целиком");
            Eq(MergeValidator.Check(gone, r).Count, 0, "и проверка чистая");

            c.Choice = MergeSide.Theirs;
            var half = r.Build();
            var issues = MergeValidator.Check(half, r);
            Check(issues.Any(i => i.Title == "Reference to a deleted object" && i.FileId == 250), "коллайдер без объекта пойман проверкой");

            r.SetDocumentSide(200, MergeSide.Theirs);
            r.SetDocumentSide(201, MergeSide.Theirs);
            var restored = r.Build();
            Eq(MergeValidator.Check(restored, r).Count, 0, "объект возвращён целиком — проверка чистая");
            Eq(Doc(restored, 250).Get("m_IsTrigger"), "1", "с их правкой");
        }

        {
            var m = Scene(s => s.Insts[0].Mods["m_Name"] = "Boss");
            var t = Scene(s => s.Insts[0].Mods["m_LocalPosition.x"] = "4");
            var r = YamlMerge.Merge(b, m, t);
            var text = r.Build();
            Eq(r.Conflicts.Count, 0, "разные переопределения префаба — без конфликта");
            Check(text.Contains("propertyPath: m_Name") && text.Contains("propertyPath: m_LocalPosition.x"),
                  "оба переопределения в результате");

            var m2 = Scene(s => s.Insts[0].Mods["m_LocalPosition.y"] = "2");
            var t2 = Scene(s => s.Insts[0].Mods["m_LocalPosition.y"] = "3");
            var r2 = YamlMerge.Merge(b, m2, t2);
            Eq(r2.Conflicts.Count, 1, "одно переопределение по-разному — конфликт");
            Eq(YamlMerge.DescribeKey(r2.Conflicts.FirstOrDefault()?.Key), "override m_LocalPosition.y",
               "понятное имя ключа переопределения");
        }

        {
            var m = Scene(s => s.Find("Player").ChildOrder = new List<long> { 400, 300 });
            var t = Scene(s => s.Gos.Add(new Go { Id = 800, Name = "Tail", ParentGo = 100 }));
            var r = YamlMerge.Merge(b, m, t);
            var text = r.Build();
            Eq(r.Conflicts.Count, 0, "перестановка у меня и новый ребёнок у них — без конфликта");
            var kids = Enumerable.Range(0, 5).Select(i => Doc(text, 101).Get("m_Children[" + i + "]")).Where(v => v != null).ToList();
            Eq(string.Join(",", kids), "{fileID: 401},{fileID: 301},{fileID: 801}", "мой порядок, новый ребёнок в конце");

            var t2 = Scene(s => s.Find("Player").ChildOrder = new List<long> { 400, 300 });
            var b3 = Scene(s => s.Gos.Add(new Go { Id = 800, Name = "Tail", ParentGo = 100 }));
            var m3 = Scene(s => { s.Gos.Add(new Go { Id = 800, Name = "Tail", ParentGo = 100 }); s.Find("Player").ChildOrder = new List<long> { 800, 300, 400 }; });
            var t3 = Scene(s => { s.Gos.Add(new Go { Id = 800, Name = "Tail", ParentGo = 100 }); s.Find("Player").ChildOrder = new List<long> { 400, 300, 800 }; });
            var r3 = YamlMerge.Merge(b3, m3, t3);
            var order = r3.Conflicts.FirstOrDefault(c => c.Kind == MergeConflictKind.Order);
            Check(order != null, "обе стороны переставили по-разному — конфликт порядка");
            if (order != null)
            {
                order.Choice = MergeSide.Theirs;
                var kids3 = Enumerable.Range(0, 5).Select(i => Doc(r3.Build(), 101).Get("m_Children[" + i + "]")).Where(v => v != null);
                Eq(string.Join(",", kids3), "{fileID: 401},{fileID: 301},{fileID: 801}", "выбрали их порядок");
            }
        }

        {
            const string mb = "fileFormatVersion: 2\nguid: aaa111\nTextureImporter:\n  isReadable: 0\n  maxTextureSize: 2048\n";
            var mm = mb.Replace("isReadable: 0", "isReadable: 1");
            var mt = mb.Replace("maxTextureSize: 2048", "maxTextureSize: 1024");
            var r = YamlMerge.Merge(mb, mm, mt);
            Eq(r.Conflicts.Count, 0, "мета: разные параметры импорта — без конфликта");
            Eq(r.Build(), mb.Replace("isReadable: 0", "isReadable: 1").Replace("maxTextureSize: 2048", "maxTextureSize: 1024"),
               "мета: обе правки");

            var r2 = YamlMerge.Merge(mb, mb.Replace("aaa111", "bbb222"), mb.Replace("aaa111", "ccc333"));
            Eq(r2.Conflicts.FirstOrDefault()?.Key, "guid", "мета: разный GUID — конфликт по guid");
            Check(MergeValidator.Check(r2.Build(), r2).Any(i => i.Title.Contains("GUID")), "мета: предупреждение о GUID");
        }

        {
            var m = Scene(s => s.Find("Player").Comps[0].Props["m_Mass"] = "0.30000001");
            var t = Scene(s => s.Find("Player").Comps[0].Props["m_Mass"] = "0.3");
            Eq(YamlMerge.Merge(b, m, t).Conflicts.Count, 0, "шум округления — не конфликт");
        }

        {
            var m = Scene(s => s.Find("Player").Pos = "{x: 5, y: 0, z: 0}").Replace("\n", "\r\n");
            var t = Scene(s => s.Fog = "1");
            var text = YamlMerge.Merge(b, m, t).Build();
            Check(text.Contains("\r\n") && !text.Replace("\r\n", "").Contains("\n"), "переводы строк — как в моём файле");
        }

        {
            var m = Scene(s => s.Gos.Add(new Go { Id = 500, Name = "Box" }));
            var t = Scene(s => { var g = new Go { Id = 500, Name = "Box", Pos = "{x: 0, y: 2, z: 0}" }; s.Gos.Add(g); });
            var r = YamlMerge.Merge(b, m, t);
            Eq(r.Conflicts.Count, 1, "один fileID добавлен обеими — сливается по свойствам");
            Eq(r.Conflicts.FirstOrDefault()?.Key, "m_LocalPosition", "конфликт только в позиции");
        }

        {
            // Список слотов материалов неделим: поэлементное слияние перепутало бы слоты.
            const string header = "%YAML 1.1\n--- !u!23 &5\nMeshRenderer:\n  m_Enabled: 1\n";
            var mb = header + "  m_Materials:\n  - {fileID: 1, guid: a, type: 2}\n  m_CastShadows: 1\n";
            var mm = header + "  m_Materials:\n  - {fileID: 1, guid: b, type: 2}\n  m_CastShadows: 1\n";
            var mt = header + "  m_Materials:\n  - {fileID: 1, guid: a, type: 2}\n  - {fileID: 1, guid: c, type: 2}\n  m_CastShadows: 1\n";
            var r = YamlMerge.Merge(mb, mm, mt);
            Eq(r.Conflicts.Count, 1, "материалы по-разному — один конфликт на весь список");
            Eq(r.Conflicts.FirstOrDefault()?.Key, "m_Materials", "ключ — список целиком");
        }
    }

    private static void Changes()
    {
        Console.WriteLine("— правки для трёх панелей");
        var b = Scene(null);

        {
            var m = Scene(s => s.Find("Player").Pos = "{x: 5, y: 0, z: 0}");
            var t = Scene(s => s.Find("Player").Comps[0].Props["m_Mass"] = "9");
            var r = YamlMerge.Merge(b, m, t);
            Eq(r.Changes.Count, 2, "две правки документов видны, хотя конфликтов нет");
            var mine = r.Changes.FirstOrDefault(c => c.FileId == 101);
            var theirs = r.Changes.FirstOrDefault(c => c.FileId == 150);
            Check(mine != null && !mine.IsConflict && mine.Auto == MergeSide.Mine && mine.Key == "m_LocalPosition",
                  "правка позиции — автоматически моя, отдельным свойством");
            Check(theirs != null && theirs.Auto == MergeSide.Theirs && theirs.TheirsChanged && !theirs.MineChanged, "правка массы — автоматически их");
            Eq(r.Unresolved, 0, "неконфликтные правки не требуют решения");

            theirs.Choice = MergeSide.Mine;
            Eq(Doc(r.Build(), 150).Get("m_Mass"), "1", "взяли мою сторону у их правки — масса осталась прежней");
            mine.Choice = MergeSide.Theirs;
            Eq(Doc(r.Build(), 101).Get("m_LocalPosition"), "{x: 0, y: 0, z: 0}", "отказались от своей правки позиции");
            r.ResetChoices();
            Eq(r.Build(), YamlMerge.Merge(b, m, t).Build(), "сброс возвращает решение автомата");
        }

        {
            var m = Scene(s => { s.Find("Player").Pos = "{x: 5, y: 0, z: 0}"; s.Find("Player").Comps[0].Props["m_UseGravity"] = "0"; });
            var t = Scene(s => { s.Find("Player").Pos = "{x: 0, y: 7, z: 0}"; s.Find("Player").Comps[0].Props["m_Mass"] = "9"; });
            var r = YamlMerge.Merge(b, m, t);
            var pos = r.Changes.FirstOrDefault(c => c.Key == "m_LocalPosition");
            Check(pos != null && pos.Auto == MergeSide.None && pos.AutoText != null && pos.AutoText.Contains("{x: 5, y: 7, z: 0}"),
                  "позиция слита из обеих — в итоге текст из обеих сторон");
            Eq(pos?.ResultText, pos?.AutoText, "итог слитой правки — собранный текст");
            if (pos != null)
            {
                pos.Choice = MergeSide.Theirs;
                Eq(Doc(r.Build(), 101).Get("m_LocalPosition"), "{x: 0, y: 7, z: 0}", "выбрали их — только их позиция");
            }

            var gravity = r.Changes.FirstOrDefault(c => c.Key == "m_UseGravity");
            var mass = r.Changes.FirstOrDefault(c => c.Key == "m_Mass");
            Check(gravity != null && gravity.Auto == MergeSide.Mine, "свойство внутри слитого документа — отдельная правка");
            if (mass != null)
            {
                mass.Choice = MergeSide.Mine;
                var text = r.Build();
                Eq(Doc(text, 150).Get("m_Mass"), "1", "у одного свойства взяли другую сторону");
                Eq(Doc(text, 150).Get("m_UseGravity"), "0", "соседнее свойство не тронуто");
            }
        }

        {
            var same = Scene(s => s.Fog = "1");
            var r = YamlMerge.Merge(b, same, same);
            Check(r.Changes.Count == 1 && r.Changes[0].Same, "одинаковая правка обеих сторон помечена");
        }

        {
            var m = Scene(s => s.Gos.Add(new Go { Id = 500, Name = "Hat", ParentGo = 100 }));
            var t = Scene(s => s.Gos.Add(new Go { Id = 600, Name = "Gun", ParentGo = 100 }));
            var r = YamlMerge.Merge(b, m, t);
            Check(!r.Changes.Any(c => c.Key != null && c.Key.EndsWith("[]")), "служебные заголовки списков в правки не попадают");
            var gun = r.Changes.FirstOrDefault(c => c.Key == "m_Children[#{fileID: 601}]");
            Check(gun != null && gun.Auto == MergeSide.Theirs && gun.Mine == null, "новый ребёнок у них — отдельная правка");
        }
    }

    private static void Outline()
    {
        Console.WriteLine("— иерархия трёх версий");

        MergeOutline Build(string b, string m, string t, out YamlMergeResult r)
        {
            r = YamlMerge.Merge(b, m, t);
            var bs = SceneOutline.Build(UnityScene.Build(b));
            var ms = SceneOutline.Build(UnityScene.Build(m));
            var ts = SceneOutline.Build(UnityScene.Build(t));
            var rs = SceneOutline.Build(UnityScene.Build(r.Build()));
            return MergeOutline.Build(bs, ms, ts, rs, r);
        }

        var b0 = Scene(null);
        var single = SceneOutline.Build(UnityScene.Build(b0));
        Eq(string.Join(",", single.Nodes[100].Children), "300,400", "дети Player в порядке m_Children");
        Eq(single.Nodes[300].Parent, 100L, "родитель Arm — Player");
        Check(single.Nodes.ContainsKey(700) && single.Nodes[700].IsPrefabInstance, "экземпляр префаба — узел иерархии");
        Eq(single.NodeOfDoc[150], 100L, "Rigidbody принадлежит Player");
        Check(single.Roots.Contains(100) && single.Roots.Contains(200) && !single.Roots.Contains(300), "корни — только объекты без родителя");

        {
            var m = Scene(s => s.Gos.Add(new Go { Id = 500, Name = "Hat", ParentGo = 100 }));
            var t = Scene(s => s.Find("Player").Comps[0].Props["m_Mass"] = "9");
            YamlMergeResult r;
            var o = Build(b0, m, t, out r);

            var rows = o.Flatten(_ => true, true);
            Check(rows.Any(x => x.Id == 500 && x.Depth == 1), "новый Hat — строка под Player");
            Check(!rows.Any(x => x.Id == 200), "Enemy без правок скрыт в режиме «только изменённое»");
            Check(o.ByNode.ContainsKey(500) && o.ByNode[500].Any(c => c.Key == "m_Children[#{fileID: 501}]"),
                  "место Hat в списке детей родителя — правка самого Hat");
            Check(!o.ChangesOf(100).Any(c => c.Key != null && c.Key.StartsWith("m_Children")), "и не правка Player");
            Eq(o.ChangesOf(150).Count, 1, "масса — правка компонента Rigidbody");
            Eq(o.Subtree(100).Count, o.ChangesOf(150).Count + o.Subtree(500).Count, "поддерево Player включает Hat и Rigidbody");

            bool unpacked;
            Check(o.NodeOn(o.Mine, 500, out unpacked) != null && o.NodeOn(o.Theirs, 500, out unpacked) == null,
                  "Hat есть у меня и отсутствует у них");

            foreach (var c in o.Subtree(500)) c.Choice = MergeSide.Theirs;
            var text = r.Build();
            Check(!text.Contains("&500"), "«взять объект как у них» убрал Hat из итога");
            Eq(MergeValidator.Check(text, r).Count, 0, "и итог целостный");
        }

        {
            // У них экземпляр распакован, у меня переопределение изменено.
            var baseText = Scene(s => s.Insts[0].Mods["m_Name"] = "Crate");
            var m = Scene(s => { s.Insts[0].Mods["m_Name"] = "Crate"; s.Insts[0].Mods["m_LocalPosition.y"] = "6.96"; });
            var t = Scene(s =>
            {
                s.Insts.Clear();
                s.Gos.Add(new Go { Id = 900, Name = "Crate" });
                s.Gos.Add(new Go { Id = 950, Name = "Lid", ParentGo = 900 });
            });

            YamlMergeResult r;
            var o = Build(baseText, m, t, out r);
            Eq(o.Unpacks.Count, 1, "распаковка распознана");
            var pair = o.Unpacks.FirstOrDefault();
            Check(pair != null && pair.PrefabId == 700 && pair.ObjectId == 900 && pair.Side == MergeSide.Theirs,
                  "пара: экземпляр 700 ↔ объект 900, распаковали они");

            var rows = o.Flatten(_ => true, false);
            Check(rows.Any(x => x.Id == 700) && !rows.Any(x => x.Id == 900), "распакованный объект стоит в строке экземпляра");
            Check(rows.Any(x => x.Id == 950 && x.Depth == 1), "его ребёнок — под той же строкой");

            bool unpacked;
            var theirsNode = o.NodeOn(o.Theirs, 700, out unpacked);
            Check(theirsNode != null && theirsNode.Id == 900 && unpacked, "в их колонке — распакованный объект");
            Check(o.NodeOn(o.Mine, 700, out unpacked).IsPrefabInstance, "в моей — экземпляр");
            Check(o.Subtree(700).Any(c => c.Kind == MergeConflictKind.RemovedByTheirs), "конфликт экземпляра — в той же строке");

            foreach (var c in o.Subtree(700)) c.Choice = MergeSide.Mine;
            var keep = r.Build();
            Check(keep.Contains("&700") && !keep.Contains("&900") && keep.Contains("value: 6.96"),
                  "оставить экземпляр: мои переопределения на месте, распакованных объектов нет");

            foreach (var c in o.Subtree(700)) c.Choice = MergeSide.Theirs;
            var take = r.Build();
            Check(!take.Contains("&700") && take.Contains("&900") && take.Contains("&950"), "взять распакованные объекты");
            Eq(MergeValidator.Check(take, r).Count, 0, "итог целостный");
        }
    }

    private static void Validator()
    {
        Console.WriteLine("— проверки результата");
        var b = Scene(null);
        var r = YamlMerge.Merge(b, b, b);

        var orphan = b.Replace("  m_Children:\n  - {fileID: 301}\n  - {fileID: 401}\n", "  m_Children:\n  - {fileID: 401}\n");
        Check(MergeValidator.Check(orphan, r).Any(i => i.Title == "Object is missing from its parent's children" && i.FileId == 301),
              "ребёнок, которого родитель не перечисляет");

        var stray = b.Replace("  - component: {fileID: 150}\n", "");
        Check(MergeValidator.Check(stray, r).Any(i => i.Title == "Component isn't listed on its object" && i.FileId == 150),
              "компонент вне списка объекта");

        var dup = b + "--- !u!54 &150\nRigidbody:\n  m_GameObject: {fileID: 100}\n";
        Check(MergeValidator.Check(dup, r).Any(i => i.Title == "Duplicate fileID"), "повтор fileID");

        Eq(MergeValidator.Check(b, r).Count, 0, "исходная сцена чистая");
    }

    // ------------------------------------------------------------------ git ---

    private static string _repo;

    private static (int code, string output) Git(string args, string cwd = null)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = cwd ?? _repo,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        psi.EnvironmentVariables["LC_ALL"] = "C";
        psi.EnvironmentVariables["GIT_EDITOR"] = "true";
        var p = Process.Start(psi);
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + err.Result);
    }

    private static void Write(string path, string text)
    {
        var full = Path.Combine(_repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllText(full, text, new UTF8Encoding(false));
    }

    private static void Commit(string message)
    {
        Git("add -A");
        Git("commit -q -m \"" + message + "\"");
    }

    private static void NewRepo(string root, string name)
    {
        _repo = Path.Combine(root, name);
        Directory.CreateDirectory(_repo);
        Git("init -q -b main");
        Git("config user.name Test");
        Git("config user.email test@example.com");
        Git("config core.autocrlf false");
        Write("Assets/Test.unity", Scene(null));
        Write("Assets/Readme.txt", "hello\n");
        Commit("base");
    }

    private static string Mass(string value) { return Scene(s => s.Find("Player").Comps[0].Props["m_Mass"] = value); }

    private static void GitIntegration(string root)
    {
        Console.WriteLine("— живой git");

        // --- слияние ---
        NewRepo(root, "merge");
        Git("checkout -q -b feature");
        Write("Assets/Test.unity", Mass("9"));
        Write("Assets/Readme.txt", "hello from feature\n");
        Commit("feature mass");
        Git("checkout -q main");
        Write("Assets/Test.unity", Scene(s => { s.Find("Player").Comps[0].Props["m_Mass"] = "5"; s.Fog = "1"; }));
        File.Delete(Path.Combine(_repo, "Assets/Readme.txt"));
        Commit("main mass");

        var merge = Git("merge feature");
        Check(merge.code != 0, "слияние остановилось на конфликте");

        var gitDir = GitOperationState.ResolveGitDir(_repo);
        var state = GitOperationState.Read(gitDir);
        Eq(state.Kind, GitOperationKind.Merge, "распознано слияние");
        Eq(state.IncomingName, "feature", "имя сливаемой ветки из MERGE_MSG");
        Eq(state.Title, "Merge of branch “feature”", "заголовок полосы");

        var list = GitConflictParser.ParseUnmerged(Git("ls-files -u -z").output);
        Eq(list.Count, 2, "два конфликтных файла");
        var scene = list.FirstOrDefault(c => c.GitPath == "Assets/Test.unity");
        var readme = list.FirstOrDefault(c => c.GitPath == "Assets/Readme.txt");
        Check(scene != null && scene.Has(1) && scene.Has(2) && scene.Has(3), "у сцены три стадии");
        Eq(scene != null ? GitConflictParser.Shape(scene, state) : (ConflictShape?)null, ConflictShape.BothModified, "сцену изменили обе");
        Eq(readme != null ? GitConflictParser.Shape(readme, state) : (ConflictShape?)null, ConflictShape.RemovedByMine, "readme: я удалил, они изменили");

        if (scene != null)
        {
            var baseText = Git("cat-file blob " + scene.Blob[1]).output;
            var mine = Git("cat-file blob " + scene.Blob[state.MineStage]).output;
            var theirs = Git("cat-file blob " + scene.Blob[state.TheirsStage]).output;
            Eq(GitConflictParser.Classify(scene.GitPath, mine), ConflictContent.UnityYaml, "содержимое — Unity-YAML");
            Check(GitConflictParser.HasConflictMarkers(File.ReadAllText(Path.Combine(_repo, scene.GitPath))),
                  "в рабочей копии маркеры git");

            var r = YamlMerge.Merge(baseText, mine, theirs);
            Eq(r.Conflicts.Count, 1, "движок: один конфликт (масса), туман слит сам");
            r.ResolveAll(MergeSide.Theirs);
            var result = r.Build();
            Eq(Doc(result, 150).Get("m_Mass"), "9", "их масса");
            Eq(Doc(result, 2).Get("m_Fog"), "1", "мой туман");
            Check(!GitConflictParser.HasConflictMarkers(result), "в результате маркеров нет");

            Write(scene.GitPath, result);
            Git("add -- Assets/Test.unity");
        }

        Git("rm -q -- Assets/Readme.txt");
        Eq(Git("ls-files -u").output.Trim(), "", "после разрешения конфликтов не осталось");
        Eq(Git("commit --no-edit -q").code, 0, "коммит слияния прошёл");
        Eq(GitOperationState.Read(gitDir).Kind, GitOperationKind.None, "после коммита операции нет");

        // --- rebase: стороны меняются местами ---
        NewRepo(root, "rebase");
        Git("checkout -q -b mywork");
        Write("Assets/Test.unity", Mass("5"));
        Commit("my mass");
        Git("checkout -q main");
        Write("Assets/Test.unity", Mass("9"));
        Commit("upstream mass");
        Git("checkout -q mywork");
        Check(Git("rebase main").code != 0, "rebase остановился на конфликте");

        state = GitOperationState.Read(GitOperationState.ResolveGitDir(_repo));
        Eq(state.Kind, GitOperationKind.Rebase, "распознан rebase");
        Eq(state.HeadName, "mywork", "переносимая ветка");
        Eq(state.Step, 1, "коммит 1");
        Eq(state.Total, 1, "из 1");
        Eq(state.MineStage, 3, "в rebase моя версия — третья стадия");
        var rb = GitConflictParser.ParseUnmerged(Git("ls-files -u -z").output).FirstOrDefault();
        if (rb != null)
        {
            Eq(Doc(Git("cat-file blob " + rb.Blob[state.MineStage]).output, 150)?.Get("m_Mass"), "5", "«моё» в rebase — мой коммит");
            Eq(Doc(Git("cat-file blob " + rb.Blob[state.TheirsStage]).output, 150)?.Get("m_Mass"), "9", "«их» в rebase — upstream");
        }
        Git("rebase --abort");
        Eq(GitOperationState.Read(GitOperationState.ResolveGitDir(_repo)).Kind, GitOperationKind.None, "после отмены rebase операции нет");

        // --- cherry-pick ---
        NewRepo(root, "pick");
        Git("checkout -q -b other");
        Write("Assets/Test.unity", Mass("5"));
        Commit("other mass");
        Git("checkout -q main");
        Write("Assets/Test.unity", Mass("9"));
        Commit("main mass");
        Check(Git("cherry-pick other").code != 0, "cherry-pick остановился на конфликте");
        state = GitOperationState.Read(GitOperationState.ResolveGitDir(_repo));
        Eq(state.Kind, GitOperationKind.CherryPick, "распознан cherry-pick");
        Eq(state.MineStage, 2, "в cherry-pick моя версия — вторая стадия");
        Git("cherry-pick --abort");
    }

    private static void UnityYamlMergeTool(string root)
    {
        const string exe = @"C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Data\Tools\UnityYAMLMerge.exe";
        if (!File.Exists(exe)) { Console.WriteLine("— UnityYAMLMerge не найден, пропуск"); return; }
        Console.WriteLine("— UnityYAMLMerge: порядок аргументов");

        var dir = Path.Combine(root, "uym");
        Directory.CreateDirectory(dir);
        string Put(string name, string text) { var p = Path.Combine(dir, name); File.WriteAllText(p, text); return p; }

        int Run(string b, string m, string t, string dest)
        {
            // Как у штатного драйвера git: база, их (left), моё (right), результат.
            var psi = new ProcessStartInfo(exe, "merge -h --fallback none \"" + b + "\" \"" + t + "\" \"" + m + "\" \"" + dest + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var p = Process.Start(psi);
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }

        var bp = Put("base.unity", Scene(null));
        var mp = Put("mine.unity", Scene(s => s.Find("Player").Pos = "{x: 5, y: 0, z: 0}"));
        var tp = Put("theirs.unity", Mass("9"));
        var dest = Path.Combine(dir, "out.unity");
        Eq(Run(bp, mp, tp, dest), 0, "без конфликтов — код 0");
        var merged = File.Exists(dest) ? File.ReadAllText(dest) : "";
        Eq(Doc(merged, 101)?.Get("m_LocalPosition"), "{x: 5, y: 0, z: 0}", "моя правка на месте");
        Eq(Doc(merged, 150)?.Get("m_Mass"), "9", "их правка на месте");

        var mc = Put("mine2.unity", Mass("5"));
        var dest2 = Path.Combine(dir, "out2.unity");
        Check(Run(bp, mc, tp, dest2) != 0, "с конфликтом — ненулевой код");
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // Настоящие файлы Unity для тестов лежат рядом, в Fixtures.
        var scratch = args.Length > 0 ? args[0] : "Fixtures";
        var root = Path.Combine(Path.GetTempPath(), "levgitlab_merge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            RoundTrip(scratch);
            Scenarios();
            Changes();
            Outline();
            Validator();
            GitIntegration(root);
            UnityYamlMergeTool(root);
        }
        finally
        {
            try
            {
                foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
            catch { }
        }

        Console.WriteLine();
        // ---- откат части YAML-файла к коммиту ----
        {
            Console.WriteLine("— откат части файла");
            const string pre = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
            string Go(string name, string comps) =>
                "--- !u!1 &10\nGameObject:\n  m_ObjectHideFlags: 0\n  m_Component:\n" + comps + "  m_Layer: 0\n  m_Name: " + name + "\n";
            string Tr(string pos) =>
                "--- !u!4 &11\nTransform:\n  m_GameObject: {fileID: 10}\n  m_LocalPosition: " + pos + "\n  m_Children: []\n  m_Father: {fileID: 0}\n";
            const string renderer = "--- !u!23 &12\nMeshRenderer:\n  m_GameObject: {fileID: 10}\n  m_Enabled: 1\n  m_Materials:\n  - {fileID: 2100000, guid: aaa, type: 2}\n  m_StaticBatchInfo:\n    firstSubMesh: 0\n";
            const string collider = "--- !u!65 &13\nBoxCollider:\n  m_GameObject: {fileID: 10}\n  m_Enabled: 1\n";

            var head = pre + Go("Cube", "  - component: {fileID: 11}\n  - component: {fileID: 12}\n") + Tr("{x: 1, y: 2, z: 3}") + renderer;
            // Сейчас: переименован, сдвинут, MeshRenderer снят, добавлен BoxCollider.
            var current = pre + Go("Cube2", "  - component: {fileID: 11}\n  - component: {fileID: 13}\n") + Tr("{x: 5, y: 2, z: 9}") + collider;

            Eq(YamlRevert.Document(head, head, 11), head, "документ без правок — файл байт в байт");

            var name = YamlRevert.Property(current, head, 10, "m_Name");
            Check(name != null && name.Contains("  m_Name: Cube\n") && name.Contains("{x: 5, y: 2, z: 9}") && name.Contains("&13"),
                  "откат имени не трогает остальное");

            var x = YamlRevert.Property(current, head, 11, "m_LocalPosition.x");
            Check(x != null && x.Contains("  m_LocalPosition: {x: 1, y: 2, z: 9}\n"), "откат одного поля flow-карты");
            Check(YamlRevert.Property(head, head, 11, "m_LocalPosition.x") == null, "нечего откатывать — null");

            var restored = YamlRevert.ComponentLink(YamlRevert.Document(current, head, 12), head, 10, 12);
            Check(restored != null && restored.Contains(renderer), "удалённый компонент вернулся документом");
            Check(restored != null && restored.Contains("  - component: {fileID: 11}\n  - component: {fileID: 12}\n  - component: {fileID: 13}\n"),
                  "ссылка на него — на прежнем месте в списке");
            Check(restored != null && restored.IndexOf("&12", StringComparison.Ordinal) > restored.IndexOf("&11", StringComparison.Ordinal) &&
                  restored.IndexOf("&12", StringComparison.Ordinal) < restored.IndexOf("&13", StringComparison.Ordinal),
                  "документ встал после предшественника по коммиту");

            var removed = YamlRevert.ComponentLink(YamlRevert.Document(current, head, 13), head, 10, 13);
            Check(removed != null && !removed.Contains("&13") && !removed.Contains("{fileID: 13}"), "добавленный компонент убран вместе со ссылкой");

            var matCurrent = pre + renderer.Replace("guid: aaa", "guid: bbb");
            Eq(YamlRevert.Property(matCurrent, pre + renderer, 12, "m_Materials[0]"), pre + renderer,
               "свойство-список — целиком, соседние свойства остаются");

            var one = pre + Go("Cube", "  - component: {fileID: 13}\n") + collider;
            var noneHead = pre + Go("Cube", string.Empty).Replace("  m_Component:\n", "  m_Component: []\n");
            var cleared = YamlRevert.ComponentLink(one, noneHead, 10, 13);
            Check(cleared != null && cleared.Contains("  m_Component: []\n  m_Layer"), "последняя ссылка — пустой список []");
            var refilled = YamlRevert.ComponentLink(noneHead, one, 10, 13);
            Check(refilled != null && refilled.Contains("  m_Component:\n  - component: {fileID: 13}\n  m_Layer"), "в пустой список ссылка встаёт элементом");

            var crlfName = YamlRevert.Property(current.Replace("\n", "\r\n"), head, 10, "m_Name");
            Check(crlfName != null && crlfName.Contains("  m_Name: Cube\r\n") && !crlfName.Replace("\r\n", string.Empty).Contains("\n"),
                  "переводы строк Windows сохраняются");

            const string ih = "--- !u!1001 &100\nPrefabInstance:\n  m_ObjectHideFlags: 0\n  serializedVersion: 2\n  m_Modification:\n    serializedVersion: 3\n    m_TransformParent: {fileID: 0}\n    m_Modifications:\n";
            const string it = "    m_RemovedComponents: []\n  m_SourcePrefab: {fileID: 100100000, guid: aaa, type: 3}\n";
            string M(long id, string p, string v) =>
                "    - target: {fileID: " + id + ", guid: aaa, type: 3}\n      propertyPath: " + p + "\n      value: " + v + "\n      objectReference: {fileID: 0}\n";

            var instHead = pre + ih + M(11, "m_Name", "Cube (3)") + M(22, "m_LocalPosition.x", "6.652") + M(22, "m_LocalScale.y", "2") + it;
            var instCur = pre + ih + M(33, "m_IsActive", "0") + M(11, "m_Name", "Cube (3)") + M(22, "m_LocalPosition.x", "6.5") + it;

            var onlyX = YamlRevert.InstanceOverrides(instCur, instHead, 100, c => c.PropertyPath == "m_LocalPosition.x");
            Check(onlyX != null && onlyX.Contains("value: 6.652") && onlyX.Contains("m_IsActive") && !onlyX.Contains("m_LocalScale"),
                  "одно переопределение — к коммиту, остальные как есть");
            Eq(YamlRevert.InstanceOverrides(instCur, instHead, 100, null), instHead, "все переопределения — как в коммите");
        }

        Console.WriteLine("== превью: YAML ассета ==");
        {
            const string header = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n";
            const string material = header +
                "--- !u!114 &-7886014654093693905\nMonoBehaviour:\n  m_ObjectHideFlags: 11\n  m_Name: \n  version: 10\n" +
                "--- !u!21 &2100000\nMaterial:\n  serializedVersion: 8\n  m_ObjectHideFlags: 0\n  m_Name: Test Material\n  m_CustomRenderQueue: 2000\n";

            var main = Lev.Git.Preview.AssetYaml.MainDocument(UnityYamlParser.Parse(material));
            Check(main != null && main.ClassId == 21, "главный объект материала — Material, а не служебный AssetVersion перед ним");
            Eq(Lev.Git.Preview.AssetYaml.NameOf(main), "Test Material", "имя главного объекта");

            const string tags = header + "--- !u!78 &1\nTagManager:\n  serializedVersion: 3\n  tags: []\n";
            var tagDoc = Lev.Git.Preview.AssetYaml.MainDocument(UnityYamlParser.Parse(tags));
            Check(tagDoc != null && tagDoc.TypeName == "TagManager", "у файла настроек нет правила ×100000 — берётся первый обычный объект");

            const string profile = header +
                "--- !u!114 &-100\nMonoBehaviour:\n  m_ObjectHideFlags: 3\n  m_Name: Bloom\n" +
                "--- !u!114 &11400000\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_Name: DefaultVolumeProfile\n";
            Eq(Lev.Git.Preview.AssetYaml.NameOf(Lev.Git.Preview.AssetYaml.MainDocument(UnityYamlParser.Parse(profile))),
               "DefaultVolumeProfile", "профиль: главный по идентификатору, хотя компоненты идут раньше");

            Eq(Lev.Git.Preview.AssetYaml.NameOf(Lev.Git.Preview.AssetYaml.MainDocument(UnityYamlParser.Parse(header + "--- !u!21 &2100000\nMaterial:\n  m_Name: 'Cube: red'\n"))),
               "Cube: red", "кавычки вокруг имени снимаются");

            Eq(Lev.Git.Preview.AssetYaml.ToYamlPath("m_SavedProperties.m_Colors.Array.data[0].second"), "m_SavedProperties.m_Colors[0].second", "элемент массива");
            Eq(Lev.Git.Preview.AssetYaml.ToYamlPath("a.Array.data[2].b.Array.data[10]"), "a[2].b[10]", "вложенные массивы");
            Eq(Lev.Git.Preview.AssetYaml.ToYamlPath("components.Array.size"), "components", "размер массива — сам список");
            Eq(Lev.Git.Preview.AssetYaml.ToYamlPath("m_Name"), "m_Name", "обычное поле без изменений");

            Check(Lev.Git.Preview.AssetYaml.IsYamlAssetPath("Assets/Test Material.mat"), ".mat — YAML-ассет");
            Check(Lev.Git.Preview.AssetYaml.IsYamlAssetPath("ProjectSettings/TagManager.asset"), "настройки проекта — YAML-ассет");
            Check(!Lev.Git.Preview.AssetYaml.IsYamlAssetPath("Assets/Scenes/SampleScene.unity"), "сцена не грузится как ассет");
            Check(!Lev.Git.Preview.AssetYaml.IsYamlAssetPath("Assets/Cube (2).prefab"), "префаб не грузится как ассет");
            Check(!Lev.Git.Preview.AssetYaml.IsYamlAssetPath("Assets/folder.mat/icon.png"), "расширение берётся у файла, а не у папки");

            Check(Lev.Git.Preview.AssetYaml.LooksLikeYaml(System.Text.Encoding.ASCII.GetBytes(header)), "текстовая сериализация распознаётся");
            Check(!Lev.Git.Preview.AssetYaml.LooksLikeYaml(new byte[] { 0, 0, 0, 0, 0x14, 0, 0 }), "бинарная сериализация — не YAML");
        }

        Console.WriteLine("== превью: мета импорта ==");
        {
            string Sprite(string name, string width) =>
                "    - serializedVersion: 2\n      name: " + name + "\n      rect:\n        serializedVersion: 2\n        x: 0\n        y: 0\n" +
                "        width: " + width + "\n        height: 32\n      border: {x: 0, y: 0, z: 0, w: 0}\n";

            string Meta(string mip, string max, string filter, string standaloneMax, string icon1, bool icon2) =>
                "fileFormatVersion: 2\nguid: 0123456789abcdef\nTextureImporter:\n  internalIDToNameTable: []\n  externalObjects: {}\n" +
                "  serializedVersion: 13\n  mipmaps:\n    mipMapMode: 0\n    enableMipMap: " + mip + "\n    sRGBTexture: 1\n" +
                "  isReadable: 0\n  maxTextureSize: " + max + "\n  textureSettings:\n    serializedVersion: 2\n    filterMode: " + filter + "\n" +
                "  textureType: 8\n  spriteMode: 2\n  platformSettings:\n" +
                "  - serializedVersion: 4\n    buildTarget: DefaultTexturePlatform\n    maxTextureSize: 2048\n    textureCompression: 1\n" +
                "  - serializedVersion: 4\n    buildTarget: Standalone\n    maxTextureSize: " + standaloneMax + "\n" +
                "  spriteSheet:\n    serializedVersion: 2\n    sprites:\n" + Sprite("icon_0", "32") + Sprite("icon_1", icon1) +
                (icon2 ? Sprite("icon_2", "8") : string.Empty) + "  userData: \n  assetBundleName: \n";

            var before = Lev.Git.Preview.ImporterMeta.Parse(Meta("1", "2048", "1", "2048", "32", false));
            var after = Lev.Git.Preview.ImporterMeta.Parse(Meta("0", "1024", "0", "512", "16", true).Replace("\n", "\r\n"));
            Check(before != null && before.TypeName == "TextureImporter", "мета разбирается как документ импортёра");
            Check(after != null && after.TypeName == "TextureImporter", "мета с переводами строк Windows тоже");

            var changes = Lev.Git.Preview.ImporterMeta.Changes(before, after);
            string Find(string group, string label)
            {
                foreach (var c in changes)
                    if (c.Group == group && c.Label == label) return c.Before + " → " + c.After;
                return null;
            }

            Eq(Find("Mipmaps", "Generate Mipmaps"), "on → off", "флажок словами, группа по разделу");
            Eq(Find("General", "Max Size"), "2048 → 1024", "поле верхнего уровня — «Общие», подпись как в инспекторе");
            Eq(Find("Texture Settings", "Filter Mode"), "Bilinear → Point (no filter)", "перечисление именами");
            Eq(Find("Platform Standalone", "Max Size"), "2048 → 512", "настройка платформы — группа по платформе");
            Check(Find("Platform Default", "Max Size") == null, "неизменённая платформа не попадает в список");
            Check(changes.TrueForAll(c => !c.Path.StartsWith("spriteSheet") && c.Label != "Serialized Version"),
                  "нарезка и служебные поля не смешиваются с настройками");
            Eq(changes.Count, 4, "ровно четыре изменения настроек");

            var slicesBefore = Lev.Git.Preview.ImporterMeta.Slices(before);
            var slicesAfter = Lev.Git.Preview.ImporterMeta.Slices(after);
            Eq(slicesBefore.Count, 2, "нарезка: два спрайта");
            Eq(slicesAfter.Count, 3, "нарезка: третий добавлен");
            Eq(slicesAfter[1].Width, 16f, "прямоугольник спрайта читается");

            var sliceChanges = Lev.Git.Preview.ImporterMeta.SliceChanges(slicesBefore, slicesAfter);
            Eq(sliceChanges.Count, 2, "изменён один спрайт и добавлен один");
            Eq(sliceChanges[0].Label + ": " + sliceChanges[0].Before + " → " + sliceChanges[0].After,
               "icon_1: (0, 0) 32×32 → (0, 0) 16×32", "изменённый спрайт — размером до и после");
            Eq(sliceChanges[1].Before, "—", "добавленного спрайта раньше не было");

            Eq(Lev.Git.Preview.ImporterMeta.StatusOf(slicesAfter[2], slicesBefore, true), Lev.Git.Preview.SliceStatus.Added, "рамка нового спрайта — «добавлен»");
            Eq(Lev.Git.Preview.ImporterMeta.StatusOf(slicesAfter[0], slicesBefore, true), Lev.Git.Preview.SliceStatus.Same, "неизменённый спрайт");
            Eq(Lev.Git.Preview.ImporterMeta.StatusOf(slicesBefore[1], slicesAfter, false), Lev.Git.Preview.SliceStatus.Changed, "у «было» — «изменён»");

            Eq(Lev.Git.Preview.ImporterMeta.Nicify("enableMipMap"), "Enable Mip Map", "имя поля по-человечески");
            Eq(Lev.Git.Preview.ImporterMeta.Nicify("m_UVSet"), "UV Set", "аббревиатура не разрывается по буквам");
            Check(Lev.Git.Preview.ImporterMeta.Parse("fileFormatVersion: 2\nguid: x\n") == null, "мета без импортёра — null");
        }

        Console.WriteLine("== превью: значения в дереве объектов ==");
        {
            float r, g, b, a;
            Check(Lev.Git.Preview.ValueFormat.TryColor("{r: 1, g: 0.5, b: 0.25, a: 1}", out r, out g, out b, out a) && g == 0.5f && b == 0.25f,
                  "цвет с альфой");
            Check(Lev.Git.Preview.ValueFormat.TryColor("{r: 0.1, g: 0.2, b: 0.3}", out r, out g, out b, out a) && a == 1f, "цвет без альфы — непрозрачный");
            Check(!Lev.Git.Preview.ValueFormat.TryColor("{x: 1, y: 2, z: 3}", out r, out g, out b, out a), "вектор — не цвет");
            Check(!Lev.Git.Preview.ValueFormat.TryColor("{r: 1, g: 1, b: 1, a: 1, extra: 2}", out r, out g, out b, out a), "лишние поля — не цвет");

            string guid;
            long fileId;
            Check(Lev.Git.Preview.ValueFormat.TryAssetRef("{fileID: 2100000, guid: 0123456789abcdef0123456789abcdef, type: 2}", out guid, out fileId) &&
                  fileId == 2100000 && guid == "0123456789abcdef0123456789abcdef", "ссылка на ассет");
            Check(!Lev.Git.Preview.ValueFormat.TryAssetRef("{fileID: 0}", out guid, out fileId), "пустая ссылка — не ассет");
            Check(!Lev.Git.Preview.ValueFormat.TryAssetRef("{fileID: 10303, guid: 0000000000000000f000000000000000, type: 0}", out guid, out fileId) == false,
                  "встроенный ресурс Unity тоже ссылка");
            Check(!Lev.Git.Preview.ValueFormat.TryAssetRef("{fileID: 5, guid: 00000000000000000000000000000000, type: 0}", out guid, out fileId),
                  "нулевой guid — не ассет");

            Eq(Lev.Git.Preview.ValueFormat.Compact("{x: 1, y: 2.5, z: -3}"), "(1, 2.5, -3)", "вектор компактно");
            Eq(Lev.Git.Preview.ValueFormat.Compact("{x: 0, y: 0, z: 0, w: 1}"), "(0, 0, 0, 1)", "кватернион компактно");
            Eq(Lev.Git.Preview.ValueFormat.Compact("{fileID: 0}"), "None", "пустая ссылка словом");
            Eq(Lev.Git.Preview.ValueFormat.Compact("42"), "42", "скаляр как есть");
            Eq(Lev.Git.Preview.ValueFormat.Compact("{a: 1, b: 2}"), "{a: 1, b: 2}", "незнакомая карта как есть");
            Eq(Lev.Git.Preview.ValueFormat.Compact(null), "—", "нет значения");
        }

        Console.WriteLine("== настройки сцены: документы внутри текста ==");
        {
            const string scene =
                "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n" +
                "--- !u!104 &2\nRenderSettings:\n  m_Fog: 0\n  m_FogColor: {r: 0.5, g: 0.5, b: 0.5, a: 1}\n  m_Sun: {fileID: 705507995}\n" +
                "--- !u!1 &10\nGameObject:\n  m_Name: 'Main Camera'\n" +
                "--- !u!4 &11\nTransform:\n  m_GameObject: {fileID: 10}\n  m_Father: {fileID: 0}\n" +
                "--- !u!4 &12 stripped\nTransform:\n  m_PrefabInstance: {fileID: 99}\n" +
                "--- !u!1660057539 &9223372036854775807\nSceneRoots:\n  m_ObjectHideFlags: 0\n  m_Roots:\n  - {fileID: 11}\n  - {fileID: 12}\n";

            var render = SceneDocText.Extract(scene.Replace("\n", "\r\n"), 2);
            Check(render != null && render.StartsWith("--- !u!104 &2\n") && render.Contains("m_Sun") && !render.Contains("GameObject"),
                  "документ вырезается до следующего, переводы строк Windows нормализуются");
            Check(SceneDocText.Extract(scene, 1) == null, "fileID 1 не путается с началом другого числа");
            Check(SceneDocText.Extract(scene, 12) != null, "stripped-документ тоже находится");
            Check(SceneDocText.Standalone(render).StartsWith(SceneDocText.Header + "--- !u!104 &2"), "самостоятельный файл с заголовком YAML");

            Eq(SceneDocText.NameOfTransform(scene, 11), "Main Camera", "имя корня по Transform, кавычки сняты");
            Check(SceneDocText.NameOfTransform(scene, 12) == null, "корень внутри префаба без имени — null");

            var roots = UnityYamlParser.Parse(scene).Find(d => d.TypeName == "SceneRoots");
            Eq(string.Join(",", SceneDocText.Roots(roots)), "11,12", "порядок корней из SceneRoots");

            var settings = UnityYamlParser.Parse(scene).Find(d => d.TypeName == "RenderSettings");
            Check(SceneDocText.HasLocalReferences(settings), "ссылка на солнце сцены — локальная");
            Check(!SceneDocText.HasLocalReferences(roots) == false, "корни — тоже ссылки на объекты сцены");
        }

        Console.WriteLine("== слияние текстовых файлов ==");
        {
            // Настоящий вывод `git merge-file --diff3` на файле с переводами строк Windows.
            var merged =
                "using System;\r\nusing System.IO;\r\n\r\nclass A\r\n{\r\n" +
                "<<<<<<< LEVGIT-MINE\r\n    int x = 10;\r\n||||||| LEVGIT-BASE\r\n    int x = 1;\r\n=======\r\n    int x = 20;\r\n>>>>>>> LEVGIT-THEIRS\r\n" +
                "    int y = 2;\r\n    void F() { Run(); }\r\n}\r\n";

            var m = TextMerge.Parse(merged);
            Eq(m.Chunks.Count, 3, "участки: спокойный, конфликт, спокойный");
            Eq(m.Newline, "\r\n", "перевод строки файла распознан");
            Check(m.FinalNewline, "перевод строки в конце файла сохраняется");
            Eq(m.Chunks[0].Lines.Count, 5, "до конфликта пять строк, git свёл «using System.IO» сам");
            Eq(m.Chunks[1].Mine[0] + "|" + m.Chunks[1].Base[0] + "|" + m.Chunks[1].Theirs[0], "    int x = 10;|    int x = 1;|    int x = 20;",
               "три версии места без \\r на концах");
            Eq(m.Unresolved, 1, "одно место не решено");
            Check(GitConflictParser.HasConflictMarkers(m.Build()), "нерешённое место остаётся маркерами — запись его не пропустит");

            m.Chunks[1].Choice = TextMergeChoice.Theirs;
            Eq(m.Unresolved, 0, "после выбора решено всё");
            Eq(m.Build(), merged.Replace("<<<<<<< LEVGIT-MINE\r\n    int x = 10;\r\n||||||| LEVGIT-BASE\r\n    int x = 1;\r\n=======\r\n", string.Empty)
                                .Replace(">>>>>>> LEVGIT-THEIRS\r\n", string.Empty),
               "итог с их версией — байт в байт, переводы строк CRLF");

            m.Chunks[1].Choice = TextMergeChoice.MineThenTheirs;
            Check(m.Build().Contains("    int x = 10;\r\n    int x = 20;\r\n"), "обе версии: сначала моя, потом их");

            m.Chunks[1].Choice = TextMergeChoice.Manual;
            m.Chunks[1].Manual = "    int x = 15;\n    int z = 0;";
            Check(m.Build().Contains("{\r\n    int x = 15;\r\n    int z = 0;\r\n    int y = 2;"), "ручная правка вписана с переводами строк файла");

            m.Reset();
            Eq(m.Unresolved, 1, "сброс снова делает место нерешённым");

            var added = TextMerge.Parse("<<<<<<< LEVGIT-MINE\na\n||||||| LEVGIT-BASE\n=======\nb\n>>>>>>> LEVGIT-THEIRS\n");
            Eq(added.Chunks.Count, 1, "обе стороны добавили файл — один конфликт на весь файл");
            Eq(added.Chunks[0].Base.Count, 0, "база пустая");
            Eq(added.Newline, "\n", "переводы строк LF");

            var same = TextMerge.Parse("x\n<<<<<<< LEVGIT-MINE\nq\n||||||| LEVGIT-BASE\np\n=======\nq\n>>>>>>> LEVGIT-THEIRS\n");
            Eq(same.Unresolved, 0, "одинаковая правка с обеих сторон решается сама");

            var tricky = TextMerge.Parse("a\n=======\n<<<<<<< HEAD\nb\n");
            Eq(tricky.Conflicts, 0, "чужие маркеры в тексте файла за конфликт не принимаются");
            Eq(tricky.Build(), "a\n=======\n<<<<<<< HEAD\nb\n", "текст без наших маркеров не меняется");

            Eq(TextMerge.Parse(string.Empty).Build(), string.Empty, "пустой файл");
        }

        Console.WriteLine("== подсветка синтаксиса ==");
        {
            string Kinds(SyntaxLanguage lang, string line, ref SyntaxState st)
            {
                var parts = new List<string>();
                foreach (var sp in SyntaxHighlight.Tokenize(lang, line, ref st))
                    parts.Add(sp.Kind + ":" + line.Substring(sp.Start, sp.Length));
                return string.Join(" | ", parts);
            }

            Eq(SyntaxHighlight.Detect("Assets/Scripts/Player.cs"), SyntaxLanguage.CSharp, "C# по расширению");
            Eq(SyntaxHighlight.Detect("Packages/x/Lev.Git.Editor.asmdef"), SyntaxLanguage.Json, "asmdef — JSON");
            Eq(SyntaxHighlight.Detect("Assets/Test Material.mat.meta"), SyntaxLanguage.Yaml, "мета — YAML");
            Eq(SyntaxHighlight.Detect("Assets/Lit.shader"), SyntaxLanguage.Shader, "шейдер");
            Eq(SyntaxHighlight.Detect("Assets/UI/Main.uxml"), SyntaxLanguage.Xml, "UXML — XML");
            Eq(SyntaxHighlight.Detect("Assets/UI/Main.uss"), SyntaxLanguage.Css, "USS — CSS");
            Eq(SyntaxHighlight.Detect(".gitattributes"), SyntaxLanguage.Ignore, ".gitattributes");
            Eq(SyntaxHighlight.Detect("image.png"), SyntaxLanguage.None, "картинка — без подсветки");

            var st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.CSharp, "    public static List<int> Load(string path) // читает", ref st),
               "Keyword:public | Keyword:static | Type:List | Keyword:int | Method:Load | Keyword:string | Comment:// читает",
               "C#: ключевые слова, тип, метод, комментарий");

            st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.CSharp, "var s = $\"a {b}\" + @\"c\\d\" + 'x' + 0.5f;", ref st),
               "Keyword:var | String:$\"a {b}\" | String:@\"c\\d\" | String:'x' | Number:0.5f", "C#: строки всех видов и число");

            st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.CSharp, "var sql = @\"select", ref st), "Keyword:var | String:@\"select", "verbatim-строка не закрыта");
            Eq(st, SyntaxState.VerbatimString, "состояние verbatim переходит на следующую строку");
            Eq(Kinds(SyntaxLanguage.CSharp, "from \"\"t\"\"\"; int x;", ref st), "String:from \"\"t\"\"\" | Keyword:int", "\"\" внутри verbatim не закрывает её");
            Eq(st, SyntaxState.None, "verbatim закрылась");

            st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.CSharp, "int a; /* начало", ref st), "Keyword:int | Comment:/* начало", "блочный комментарий открыт");
            Eq(Kinds(SyntaxLanguage.CSharp, "  конец */ return;", ref st), "Comment:  конец */ | Keyword:return", "и закрыт на следующей строке");
            Eq(SyntaxHighlight.GuessState(SyntaxLanguage.CSharp, "     * середина комментария"), SyntaxState.BlockComment, "фрагмент, начинающийся с «*», — внутри комментария");

            st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.CSharp, "#if UNITY_EDITOR", ref st), "Preprocessor:#if UNITY_EDITOR", "директива препроцессора");

            st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.Json, "  \"name\": \"ru.lev.unity-git\", \"autoReferenced\": false, \"n\": -12.5", ref st),
               "Key:\"name\" | String:\"ru.lev.unity-git\" | Key:\"autoReferenced\" | Constant:false | Key:\"n\" | Number:-12.5", "JSON: ключ, строка, константа, число");

            st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.Yaml, "--- !u!21 &2100000", ref st), "Preprocessor:--- !u!21 &2100000", "YAML: заголовок документа");
            Eq(Kinds(SyntaxLanguage.Yaml, "  m_Shader: {fileID: 4800000, guid: 9335, type: 3}", ref st),
               "Key:m_Shader | Key:fileID | Number:4800000 | Key:guid | Number:9335 | Key:type | Number:3", "YAML: ключ и flow-карта");
            Eq(Kinds(SyntaxLanguage.Yaml, "  - _BaseMap: 'x' # цвет", ref st), "Key:_BaseMap | String:'x' | Comment:# цвет", "YAML: элемент списка, строка, комментарий");

            st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.Shader, "#pragma vertex vert", ref st), "Preprocessor:#pragma vertex vert", "шейдер: pragma");
            Eq(Kinds(SyntaxLanguage.Shader, "Pass { float4 frag(v2f i) : SV_Target { return _Color; } }", ref st),
               "Keyword:Pass | Type:float4 | Method:frag | Keyword:return", "шейдер: ShaderLab, тип, функция");

            st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.Xml, "<ui:Button name=\"ok\" text='Да' /> <!-- кнопка", ref st),
               "Tag:<ui:Button | Attribute:name | String:\"ok\" | Attribute:text | String:'Да' | Tag:/> | Comment:<!-- кнопка", "XML: тег, атрибуты, комментарий");
            Eq(st, SyntaxState.XmlComment, "комментарий XML переходит на следующую строку");

            st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.Css, ".row__icon {", ref st), "Type:.row__icon", "USS: селектор класса");
            Eq(Kinds(SyntaxLanguage.Css, "    width: 16px; color: #FFAA00;", ref st), "Key:width | Number:16px | Key:color | Number:#FFAA00", "USS: свойства внутри блока");

            st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.Markdown, "## Заголовок", ref st), "Heading:## Заголовок", "Markdown: заголовок");
            Eq(Kinds(SyntaxLanguage.Markdown, "```csharp", ref st), "Preprocessor:```csharp", "Markdown: начало блока кода");
            Eq(Kinds(SyntaxLanguage.Markdown, "var x = 1;", ref st), "String:var x = 1;", "Markdown: внутри блока кода");

            st = SyntaxState.None;
            Eq(Kinds(SyntaxLanguage.Ignore, "*.png filter=lfs -text", ref st), "Type:*.png | Key:filter | String:lfs | Attribute:-text", ".gitattributes: шаблон и атрибуты");
        }

        Console.WriteLine("== превью: TGA ==");
        {
            byte[] Tga(int type, int depth, bool topDown, int w, int h, params byte[] body)
            {
                var header = new byte[18];
                header[2] = (byte)type;
                header[12] = (byte)w;
                header[14] = (byte)h;
                header[16] = (byte)depth;
                header[17] = (byte)(topDown ? 0x20 : 0);
                return header.Concat(body).ToArray();
            }

            int w, h;
            byte[] rgba;
            string error;

            Check(Lev.Git.Preview.TgaDecoder.TryDecode(Tga(2, 24, false, 2, 1, 255, 0, 0, 0, 0, 255), out w, out h, out rgba, out error) && w == 2 && h == 1,
                  "несжатый 24-битный TGA");
            Eq(string.Join(",", rgba), "0,0,255,255,255,0,0,255", "BGR в RGBA: синий, затем красный");

            Check(Lev.Git.Preview.TgaDecoder.TryDecode(Tga(2, 32, true, 1, 2, 0, 255, 0, 128, 0, 0, 0, 255), out w, out h, out rgba, out error),
                  "32-битный TGA сверху вниз");
            Eq(string.Join(",", rgba), "0,0,0,255,0,255,0,128", "строки переворачиваются: верхняя строка файла — последняя у Unity");

            Check(Lev.Git.Preview.TgaDecoder.TryDecode(Tga(10, 32, false, 4, 1, 0x82, 10, 20, 30, 40, 0x00, 1, 2, 3, 4), out w, out h, out rgba, out error),
                  "RLE-сжатый TGA");
            Eq(string.Join(",", rgba), "30,20,10,40,30,20,10,40,30,20,10,40,3,2,1,4", "повтор и одиночный пиксель RLE");

            Check(Lev.Git.Preview.TgaDecoder.TryDecode(Tga(3, 8, false, 1, 1, 77), out w, out h, out rgba, out error), "серый TGA");
            Eq(string.Join(",", rgba), "77,77,77,255", "серый в RGBA");

            Check(!Lev.Git.Preview.TgaDecoder.TryDecode(Tga(1, 8, false, 1, 1, 0), out w, out h, out rgba, out error) && error != null,
                  "TGA с палитрой — понятный отказ");
            Check(!Lev.Git.Preview.TgaDecoder.TryDecode(Tga(2, 24, false, 4, 4, 1, 2, 3), out w, out h, out rgba, out error) && error.Contains("truncated"),
                  "обрезанный TGA — отказ, а не исключение");
        }

        Localization();
        Redaction();
        ExitCodes();
        SshParsing();
        TimelineParsing();
        SceneSettingsGrouping();

        Console.WriteLine("Пройдено: " + _passed + ", провалено: " + _failed);
        return _failed == 0 ? 0 : 1;
    }
}
