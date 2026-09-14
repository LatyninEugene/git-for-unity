using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lev.Git
{
    /// <summary>
    /// Разбивка сетевой команды git по этапам: хуки, ssh, упаковка объектов —
    /// когда начался каждый и сколько длился. Источник — встроенная трассировка
    /// git (trace2, формат событий), ничего не угадывается.
    ///
    /// Зачем: итог «push упал через 51 с» не говорит, куда ушло время. Если хук
    /// pre-push от git-lfs держит соединение, а сервер его закрывает, без разбивки
    /// причину можно только предполагать.
    ///
    /// От Unity не зависит: проверяется чистыми тестами.
    /// </summary>
    internal static class GitTimeline
    {
        /// <summary>С этой длительности этапы показываются и у успешной команды.</summary>
        public const int SlowMs = 5000;

        /// <summary>Команды, которые ходят на сервер, — только у них есть что разбивать.</summary>
        private static readonly HashSet<string> NetworkVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "push", "fetch", "pull", "clone", "ls-remote"
        };

        /// <summary>Вложенные этапы короче этого не показываются: их десятки, а время уходит не на них.</summary>
        private const double MinNestedSeconds = 0.2;

        /// <summary>Глубже внуков не спускаемся: дальше идут внутренности сервера или хука.</summary>
        private const int MaxDepth = 2;

        private const int MaxLines = 40;
        private const int MaxLabel = 160;

        private static readonly string[] TimeFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"
        };

        private sealed class Span
        {
            public DateTime Start;
            public double? Seconds;
            public int? Code;
            public int Depth;
            public string Label;
            /// <summary>Событие самого процесса (start/exit), а не запуска потомка (child_start/child_exit).</summary>
            public bool Process;
            public int Order;
        }

        public static bool ShouldTrace(string arguments)
        {
            var verb = Verb(arguments);
            return verb != null && NetworkVerbs.Contains(verb);
        }

        /// <summary>Подкоманда git после глобальных опций: `-c k=v`, `-C dir`, `--no-optional-locks`.</summary>
        public static string Verb(string arguments)
        {
            var tokens = Tokenize(arguments);
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t == "-c" || t == "-C" || t == "--git-dir" || t == "--work-tree" || t == "--namespace")
                {
                    i++;
                    continue;
                }
                if (t.StartsWith("-", StringComparison.Ordinal)) continue;
                return t;
            }
            return null;
        }

        /// <summary>
        /// Строки «+смещение  длительность  этап → код» из событий trace2.
        /// null — событий нет или они не разобрались.
        /// </summary>
        public static string Summarize(string events)
        {
            if (string.IsNullOrEmpty(events)) return null;

            Span root = null;
            var spans = new List<Span>();
            var children = new Dictionary<string, Span>(StringComparer.Ordinal);
            var processes = new Dictionary<string, Span>(StringComparer.Ordinal);

            foreach (var raw in events.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] != '{') continue;

                var ev = Field(line, "event");
                var sid = Field(line, "sid");
                if (ev == null || sid == null) continue;

                DateTime time;
                bool timed = TryTime(Field(line, "time"), out time);
                // Вложенный процесс получает sid родителя через «/».
                int depth = sid.Count(ch => ch == '/');

                if (ev == "start" && timed)
                {
                    var p = new Span { Start = time, Depth = depth, Label = CommandLine(Argv(line)), Process = true, Order = spans.Count };
                    processes[sid] = p;
                    if (depth == 0)
                    {
                        if (root == null) root = p;
                    }
                    else if (depth <= MaxDepth)
                    {
                        spans.Add(p);
                    }
                }
                else if (ev == "exit")
                {
                    Span p;
                    if (processes.TryGetValue(sid, out p))
                    {
                        p.Seconds = Number(line, "t_abs");
                        p.Code = Integer(line, "code");
                    }
                }
                else if (ev == "child_start" && timed)
                {
                    var c = new Span { Start = time, Depth = depth + 1, Label = ChildLabel(line), Order = spans.Count };
                    children[sid + "#" + Field(line, "child_id", true)] = c;
                    if (depth + 1 <= MaxDepth) spans.Add(c);
                }
                else if (ev == "child_exit")
                {
                    Span c;
                    if (children.TryGetValue(sid + "#" + Field(line, "child_id", true), out c))
                    {
                        c.Seconds = Number(line, "t_rel");
                        c.Code = Integer(line, "code");
                    }
                }
            }

            if (root == null) return null;

            var shown = new List<Span>();
            foreach (var s in spans)
            {
                // Подкоманду git видно дважды: запуск у родителя и события её самой. Оставляем запуск.
                if (s.Process && spans.Exists(c => !c.Process && c.Depth == s.Depth && c.Start <= s.Start &&
                                                   (s.Start - c.Start).TotalSeconds <= 0.5 &&
                                                   c.Label.Contains(CommandKey(s.Label))))
                    continue;

                bool topLevel = !s.Process && s.Depth == 1;
                if (!topLevel && s.Seconds.HasValue && s.Seconds.Value < MinNestedSeconds) continue;
                shown.Add(s);
            }

            var ordered = shown.OrderBy(s => s.Start).ThenBy(s => s.Order).ToList();

            var sb = new StringBuilder();
            sb.Append(Format(root, root.Start, true));
            foreach (var s in ordered.Take(MaxLines))
                sb.Append('\n').Append(Format(s, root.Start, false));
            if (ordered.Count > MaxLines)
                sb.Append('\n').Append("… ").Append(ordered.Count - MaxLines).Append(" more steps");

            return sb.ToString();
        }

        private static string Format(Span s, DateTime origin, bool isRoot)
        {
            var offset = "+" + Math.Max(0, (s.Start - origin).TotalSeconds).ToString("0.00", CultureInfo.InvariantCulture) + " s";
            var duration = s.Seconds.HasValue
                ? s.Seconds.Value.ToString("0.00", CultureInfo.InvariantCulture) + " s"
                : "—";

            var sb = new StringBuilder();
            sb.Append(offset.PadLeft(9)).Append("  ").Append(duration.PadLeft(9)).Append("  ");
            sb.Append(' ', (s.Depth) * 2).Append(s.Label);

            if (s.Code.HasValue) sb.Append(" → ").Append(s.Code.Value);
            else if (!s.Seconds.HasValue)
                // Конец не записан: либо git остановили, либо он вышел (например, с ошибкой), не дождавшись этапа.
                sb.Append(isRoot ? " (end not recorded: git was stopped)" : " (end not recorded: git exited first)");

            return sb.ToString();
        }

        private static string ChildLabel(string line)
        {
            var cls = Field(line, "child_class");
            var command = CommandLine(Argv(line));

            if (cls == "hook") return "hook " + (Field(line, "hook_name") ?? command);
            if (cls != null && cls.StartsWith("transport/", StringComparison.Ordinal))
                return cls.Substring("transport/".Length) + ": " + command;
            return command;
        }

        /// <summary>Команда без пути к программе: «C:\…\git.exe push» → «git push».</summary>
        private static string CommandLine(List<string> argv)
        {
            if (argv.Count == 0) return "?";

            var first = argv[0];
            int exe = first.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe >= 0)
            {
                int dir = first.LastIndexOfAny(new[] { '/', '\\' }, exe);
                first = first.Substring(dir + 1, exe - dir - 1) + first.Substring(exe + 4);
            }
            else if (first.IndexOf(' ') < 0)
            {
                first = first.Substring(first.LastIndexOfAny(new[] { '/', '\\' }) + 1);
            }

            var text = string.Join(" ", new[] { first }.Concat(argv.Skip(1)));
            return text.Length <= MaxLabel ? text : text.Substring(0, MaxLabel) + "…";
        }

        /// <summary>По чему узнать ту же подкоманду в запуске родителя: «git pack-objects», «git-receive-pack».</summary>
        private static string CommandKey(string label)
        {
            var tokens = label.Split(' ');
            return tokens[0] == "git" && tokens.Length > 1 ? "git " + tokens[1] : tokens[0];
        }

        // ------------------------------------------------ разбор строки JSON ---
        // Формат событий trace2 — плоские объекты, по одному на строку; полный
        // JSON-разборщик ради них не нужен, а в чистых тестах его и нет.

        private static string Field(string line, string name, bool number = false)
        {
            if (number)
            {
                var n = Regex.Match(line, "\"" + name + "\":(-?[0-9]+)");
                return n.Success ? n.Groups[1].Value : null;
            }

            var m = Regex.Match(line, "\"" + name + "\":\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Unescape(m.Groups[1].Value) : null;
        }

        private static double? Number(string line, string name)
        {
            var m = Regex.Match(line, "\"" + name + "\":(-?[0-9][0-9.eE+\\-]*)");
            double v;
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                ? v : (double?)null;
        }

        private static int? Integer(string line, string name)
        {
            var s = Field(line, name, true);
            int v;
            return s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : (int?)null;
        }

        private static bool TryTime(string s, out DateTime time)
        {
            return DateTime.TryParseExact(s ?? string.Empty, TimeFormats, CultureInfo.InvariantCulture,
                                          DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);
        }

        private static List<string> Argv(string line)
        {
            var list = new List<string>();
            int i = line.IndexOf("\"argv\":[", StringComparison.Ordinal);
            if (i < 0) return list;
            i += "\"argv\":[".Length;

            while (i < line.Length)
            {
                char ch = line[i];
                if (ch == ']') break;
                if (ch != '"')
                {
                    i++;
                    continue;
                }

                var sb = new StringBuilder();
                i++;
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\' && i + 1 < line.Length)
                    {
                        sb.Append(line[i]).Append(line[i + 1]);
                        i += 2;
                        continue;
                    }
                    sb.Append(line[i]);
                    i++;
                }
                list.Add(Unescape(sb.ToString()));
                i++;
            }

            return list;
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(ch);
                    continue;
                }

                char next = s[++i];
                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'u':
                        int code;
                        if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(next); break;
                }
            }
            return sb.ToString();
        }

        private static List<string> Tokenize(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false, any = false;

            foreach (var ch in s ?? string.Empty)
            {
                if (ch == '"')
                {
                    quoted = !quoted;
                    any = true;
                    continue;
                }
                if (!quoted && char.IsWhiteSpace(ch))
                {
                    if (any)
                    {
                        list.Add(sb.ToString());
                        sb.Length = 0;
                        any = false;
                    }
                    continue;
                }
                sb.Append(ch);
                any = true;
            }

            if (any) list.Add(sb.ToString());
            return list;
        }
    }
}
