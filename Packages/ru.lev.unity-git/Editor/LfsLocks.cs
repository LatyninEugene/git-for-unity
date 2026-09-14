using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lev.Git
{
    /// <summary>Лок Git LFS: запись на сервере «этот путь правит такой-то».</summary>
    public sealed class LfsLockInfo
    {
        public string Id;

        /// <summary>Путь относительно корня репозитория.</summary>
        public string GitPath;

        public string Owner;
        public string LockedAt;

        /// <summary>Мой лок. Известно, только если сервер ответил на --verify.</summary>
        public bool Mine;

        public DateTime? LockedAtUtc
        {
            get
            {
                DateTime t;
                return DateTime.TryParse(LockedAt, CultureInfo.InvariantCulture,
                                         DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)
                    ? t : (DateTime?)null;
            }
        }
    }

    /// <summary>
    /// Все локи проекта.
    ///
    /// Лок относится к пути, а не к ветке: взятый в одной ветке запирает файл
    /// во всех. Для Unity ассет — это файл вместе с его .meta, поэтому поиск
    /// по пути меты находит лок самого ассета.
    /// </summary>
    public sealed class LfsLockSet
    {
        public readonly List<LfsLockInfo> All = new List<LfsLockInfo>();
        private readonly Dictionary<string, LfsLockInfo> _byPath = new Dictionary<string, LfsLockInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Разделение на свои и чужие получено от сервера.</summary>
        public bool Verified;

        public int MineCount { get { int n = 0; foreach (var l in All) if (l.Mine) n++; return n; } }
        public int TheirsCount => All.Count - MineCount;

        public LfsLockInfo Find(string gitPath)
        {
            if (string.IsNullOrEmpty(gitPath)) return null;

            LfsLockInfo l;
            if (_byPath.TryGetValue(gitPath, out l)) return l;

            if (gitPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
                _byPath.TryGetValue(gitPath.Substring(0, gitPath.Length - 5), out l))
                return l;

            return null;
        }

        private void Add(LfsLockInfo l)
        {
            All.Add(l);
            _byPath[l.GitPath] = l;
        }

        /// <summary>
        /// Ответ `git lfs locks --verify --json`: {"ours": [...], "theirs": [...]}.
        /// null — ответ не разобрался.
        /// </summary>
        public static LfsLockSet ParseVerified(string json)
        {
            var root = MiniJson.Parse(json) as Dictionary<string, object>;
            if (root == null) return null;

            var set = new LfsLockSet { Verified = true };
            AddAll(set, root.ContainsKey("ours") ? root["ours"] as List<object> : null, true);
            AddAll(set, root.ContainsKey("theirs") ? root["theirs"] as List<object> : null, false);
            set.All.Sort((a, b) => string.Compare(a.GitPath, b.GitPath, StringComparison.OrdinalIgnoreCase));
            return set;
        }

        /// <summary>
        /// Ответ `git lfs locks --json` без проверки: просто массив. Свои
        /// определяются по имени владельца, если оно известно.
        /// </summary>
        public static LfsLockSet ParsePlain(string json, string me)
        {
            var list = MiniJson.Parse(json) as List<object>;
            if (list == null) return null;

            var set = new LfsLockSet { Verified = false };
            AddAll(set, list, false);
            if (!string.IsNullOrEmpty(me))
                foreach (var l in set.All) l.Mine = string.Equals(l.Owner, me, StringComparison.OrdinalIgnoreCase);
            return set;
        }

        private static void AddAll(LfsLockSet set, List<object> items, bool mine)
        {
            if (items == null) return;

            foreach (var item in items)
            {
                var o = item as Dictionary<string, object>;
                if (o == null) continue;

                var owner = o.ContainsKey("owner") ? o["owner"] as Dictionary<string, object> : null;
                var path = Str(o, "path");
                if (string.IsNullOrEmpty(path)) continue;

                set.Add(new LfsLockInfo
                {
                    Id = Str(o, "id"),
                    GitPath = path.Replace('\\', '/'),
                    Owner = owner != null ? Str(owner, "name") : "?",
                    LockedAt = Str(o, "locked_at"),
                    Mine = mine
                });
            }
        }

        private static string Str(Dictionary<string, object> o, string key)
        {
            object v;
            return o.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }
    }

    /// <summary>Файл под LFS в рабочей копии.</summary>
    public sealed class LfsFileEntry
    {
        public string Oid;
        public string GitPath;

        /// <summary>В рабочей копии настоящее содержимое. false — лежит указатель.</summary>
        public bool Downloaded;
    }

    public static class LfsFiles
    {
        /// <summary>
        /// Разбор `git lfs ls-files`: «oid * путь» — содержимое на месте,
        /// «oid - путь» — в рабочей копии только указатель.
        /// </summary>
        public static List<LfsFileEntry> Parse(string raw)
        {
            var list = new List<LfsFileEntry>();
            if (string.IsNullOrEmpty(raw)) return list;

            foreach (var line in raw.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                int first = l.IndexOf(' ');
                if (first <= 0 || l.Length < first + 4) continue;

                char mark = l[first + 1];
                if ((mark != '*' && mark != '-') || l[first + 2] != ' ') continue;

                list.Add(new LfsFileEntry
                {
                    Oid = l.Substring(0, first),
                    Downloaded = mark == '*',
                    GitPath = l.Substring(first + 3)
                });
            }

            return list;
        }

        public const string PointerMarker = "version https://git-lfs.github.com/spec/";

        public static bool LooksLikePointer(string text)
        {
            return text != null && text.Length < 1024 && text.StartsWith(PointerMarker, StringComparison.Ordinal);
        }
    }

    /// <summary>Коммит, изменивший файл где-то на сервере, но не у меня.</summary>
    public sealed class RemoteChange
    {
        public string Sha;
        public string Ref;
        public string Author;
        public DateTime? When;
        public string Subject;
    }

    /// <summary>Разбор истории и подписи для решений о локах.</summary>
    public static class LockAdvice
    {
        /// <summary>Формат лога, который разбирает <see cref="ParseLog"/>.</summary>
        public const string LogFormat = "%x1e%H%x00%S%x00%an%x00%aI%x00%s";

        public static List<RemoteChange> ParseLog(string raw)
        {
            var list = new List<RemoteChange>();
            if (string.IsNullOrEmpty(raw)) return list;

            foreach (var record in raw.Split('\x1e'))
            {
                var r = record.Trim('\n', '\r');
                if (r.Length == 0) continue;

                var f = r.Split('\0');
                if (f.Length < 5) continue;

                DateTime when;
                var refName = f[1].Trim();
                if (refName.StartsWith("refs/remotes/", StringComparison.Ordinal)) refName = refName.Substring("refs/remotes/".Length);
                else if (refName.StartsWith("refs/heads/", StringComparison.Ordinal)) refName = refName.Substring("refs/heads/".Length);

                list.Add(new RemoteChange
                {
                    Sha = f[0].Trim(),
                    Ref = refName,
                    Author = f[2],
                    When = DateTime.TryParse(f[3], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out when) ? when : (DateTime?)null,
                    Subject = f[4].Trim()
                });
            }

            return list;
        }

        /// <summary>«только что», «5 мин назад», «вчера», «12.09.2026».</summary>
        public static string Ago(DateTime? utc, DateTime nowUtc)
        {
            if (utc == null) return L.T("some time ago");

            var span = nowUtc - utc.Value;
            if (span.TotalSeconds < 60) return L.T("just now");
            if (span.TotalMinutes < 60) return L.F("{0} min ago", (int)span.TotalMinutes);
            if (span.TotalHours < 24) return L.F("{0} h ago", (int)span.TotalHours);
            if (span.TotalDays < 2) return L.T("yesterday");
            if (span.TotalDays < 7) return L.Fc("locks", "{0} d ago", (int)span.TotalDays);
            return utc.Value.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        }

        /// <summary>«12.09.2026 16:05 · 3 ч назад» — для подсказок и карточек.</summary>
        public static string Stamp(DateTime? utc, DateTime nowUtc)
        {
            if (utc == null) return "—";
            return utc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " · " + Ago(utc, nowUtc);
        }
    }

    /// <summary>
    /// Правка .gitattributes, после которой файлы запираются.
    ///
    /// Атрибут lockable делает незалоченный файл в рабочей копии доступным
    /// только для чтения: забыть взять лок уже нельзя. Сцены и префабы в LFS
    /// не переводятся — пометка lockable от LFS не зависит, а в LFS они
    /// потеряли бы семантический diff, историю объектов и слияние.
    ///
    /// Если в файле есть макрос [attr]lfs, пометка добавляется в него одной
    /// правкой — так она достаётся всем LFS-типам сразу, и новые типы тоже.
    /// </summary>
    public sealed class GitAttributesPlan
    {
        // Метки пишутся в .gitattributes репозитория и узнаются по точному
        // совпадению, поэтому они не переводятся и не меняются.
        public const string BlockStart = "# ru.lev.unity-git: lockable files (git lfs lock). Unlocked files are read-only.";
        public const string BlockEnd = "# ru.lev.unity-git: end";

        public string NewText;
        public readonly List<string> Added = new List<string>();
        public bool Changed;

        public static bool IsConfigured(string text)
        {
            return text != null &&
                   text.IndexOf(BlockStart, StringComparison.Ordinal) >= 0 &&
                   (HasLockableLfs(text) || text.IndexOf("filter=lfs", StringComparison.Ordinal) < 0);
        }

        private static bool HasLockableLfs(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[attr]lfs", StringComparison.Ordinal) && HasToken(line, "lockable")) return true;
            }
            return false;
        }

        public static GitAttributesPlan Build(string text, IEnumerable<string> extraPatterns)
        {
            text = text ?? string.Empty;
            var newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            bool trailing = text.EndsWith("\n", StringComparison.Ordinal);
            if (trailing && lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);

            var plan = new GitAttributesPlan();

            // Прежний блок пакета убираем и собираем заново: правка идемпотентна.
            {
                int start = lines.IndexOf(BlockStart);
                int end = start < 0 ? -1 : lines.IndexOf(BlockEnd, start);
                if (start >= 0)
                {
                    lines.RemoveRange(start, (end >= 0 ? end : start) - start + 1);
                    while (start > 0 && start <= lines.Count && lines[start - 1].Length == 0 &&
                           (start == lines.Count || lines[start].Length == 0)) { lines.RemoveAt(start - 1); start--; }
                }
            }

            var block = new List<string>();
            bool macro = false;

            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (!trimmed.StartsWith("[attr]lfs", StringComparison.Ordinal)) continue;
                var name = FirstToken(trimmed);
                if (name != "[attr]lfs") continue;

                macro = true;
                if (!HasToken(trimmed, "lockable"))
                {
                    lines[i] = lines[i].TrimEnd() + " lockable";
                    plan.Added.Add(L.T("lfs macro: + lockable — all LFS types become lockable"));
                }
            }

            if (!macro)
            {
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;
                    if (!HasToken(line, "filter=lfs") || HasToken(line, "lockable")) continue;

                    var pattern = FirstToken(line);
                    if (!block.Contains(pattern + " lockable")) block.Add(pattern + " lockable");
                }
            }

            if (extraPatterns != null)
            {
                foreach (var pattern in extraPatterns)
                {
                    bool already = false;
                    foreach (var raw in lines)
                    {
                        var line = raw.Trim();
                        if (FirstToken(line) == pattern && HasToken(line, "lockable")) { already = true; break; }
                    }
                    if (!already && !block.Contains(pattern + " lockable")) block.Add(pattern + " lockable");
                }
            }

            foreach (var b in block) plan.Added.Add(b);

            if (block.Count > 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Length > 0) lines.Add(string.Empty);
                lines.Add(BlockStart);
                lines.AddRange(block);
                lines.Add(BlockEnd);
            }

            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append(newline);
                sb.Append(lines[i]);
            }
            if (lines.Count > 0) sb.Append(newline);

            plan.NewText = sb.ToString();
            plan.Changed = plan.NewText != text;
            return plan;
        }

        private static string FirstToken(string line)
        {
            int i = 0;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            return line.Substring(0, i);
        }

        private static bool HasToken(string line, string token)
        {
            foreach (var t in line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (t == token) return true;
            return false;
        }
    }

    /// <summary>
    /// Маленький разборщик JSON — ровно для ответов git-lfs.
    /// JsonUtility не читает корневой объект с произвольными ключами и
    /// недоступен вне редактора, а тесты гоняются без Unity.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            int i = 0;
            try
            {
                var v = Value(json, ref i);
                return v;
            }
            catch
            {
                return null;
            }
        }

        private static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException();

            char c = s[i];
            if (c == '{') return Obj(s, ref i);
            if (c == '[') return Arr(s, ref i);
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }

            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException();
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        private static Dictionary<string, object> Obj(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            Skip(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }

            while (true)
            {
                Skip(s, ref i);
                var key = Str(s, ref i);
                Skip(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException();
                i++;
                d[key] = Value(s, ref i);
                Skip(s, ref i);
                if (i >= s.Length) throw new FormatException();
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException();
            }
        }

        private static List<object> Arr(string s, ref int i)
        {
            var list = new List<object>();
            i++;
            Skip(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }

            while (true)
            {
                list.Add(Value(s, ref i));
                Skip(s, ref i);
                if (i >= s.Length) throw new FormatException();
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException();
            }
        }

        private static string Str(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException();
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }

                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException();
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }
}
