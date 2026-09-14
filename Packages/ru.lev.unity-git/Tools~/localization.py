#!/usr/bin/env python3
"""
Переводы Git for Unity: проверка кода и сборка .po.

В коде пишется английский текст внутри вызовов L.T / L.Tc / L.F / L.Fc /
L.N / L.C / L.Cs / L.Ts / L.M, перевод лежит в Editor/Localization/<язык>.po.

  python "Tools~/localization.py" scan
      Весь пакет против ru.po: строки без перевода, лишние переводы,
      несовпадающие {0}, кириллица в строковых литералах кода.

  python "Tools~/localization.py" scan --refs --prune
      То же и переписать ru.po: обновить ссылки на файлы, убрать неиспользуемое.

  python "Tools~/localization.py" pot Editor/Localization/template.pot
      Шаблон для нового перевода: все строки с пустым переводом.

  python "Tools~/localization.py" check --po part.po File1.cs File2.cs
      Проверка отдельных файлов против отдельного куска .po.

  python "Tools~/localization.py" merge --out Editor/Localization/ru.po parts/*.po
      Слить куски в один файл. Одна строка с разными переводами — конфликт.

Строка с комментарием «loc-ignore» не проверяется на кириллицу, с
«loc-dynamic» — на нелитеральный аргумент перевода.
"""
import argparse
import glob
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_PO = os.path.join(ROOT, "Editor", "Localization", "ru.po")

CYRILLIC = re.compile("[\u0400-\u04FF]")
CALL = re.compile(r"(?<![\w.])L\.(Tc|Fc|Cs|Ts|T|F|N|C|M)\s*\(")
PLACEHOLDER = re.compile(r"(?<!\{)\{(\d+)(?:[,:][^{}]*)?\}(?!\})")

HEADER_RU = (
    "Project-Id-Version: Git for Unity\n"
    "Language: ru\n"
    "MIME-Version: 1.0\n"
    "Content-Type: text/plain; charset=UTF-8\n"
    "Content-Transfer-Encoding: 8bit\n"
    "Plural-Forms: nplurals=3; plural=(n%10==1 && n%100!=11 ? 0 : n%10>=2 && n%10<=4 && (n%100<10 || n%100>=20) ? 1 : 2);\n"
)


# ----------------------------------------------------------------- C# ---

def lex(src):
    """Исходник без комментариев, строки заменены метками \\x01N\\x02. Возвращает (текст, литералы)."""
    out, lits = [], []
    i, n, line = 0, len(src), 1
    escapes = {"n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\", "0": "\0", "'": "'"}

    while i < n:
        c = src[i]
        if c == "\n":
            line += 1
            out.append(c)
            i += 1
            continue
        if src.startswith("//", i):
            j = src.find("\n", i)
            i = n if j < 0 else j
            out.append(" ")
            continue
        if src.startswith("/*", i):
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            nl = src.count("\n", i, j)
            line += nl
            out.append("\n" * nl if nl else " ")
            i = j
            continue
        if c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                if src[j] == "\\":
                    j += 1
                j += 1
            out.append("' '")
            i = j + 1
            continue

        m = re.match(r'(\$@|@\$|@|\$)?"', src[i:i + 3])
        if m and (m.group(1) or c == '"'):
            prefix = m.group(1) or ""
            verbatim, interpolated = "@" in prefix, "$" in prefix
            j = i + len(m.group(0))
            start, nl = line, 0
            buf = []
            while j < n:
                ch = src[j]
                if verbatim:
                    if ch == '"':
                        if j + 1 < n and src[j + 1] == '"':
                            buf.append('"')
                            j += 2
                            continue
                        break
                    if ch == "\n":
                        nl += 1
                    buf.append(ch)
                    j += 1
                    continue
                if ch == "\\" and j + 1 < n:
                    e = src[j + 1]
                    if e in escapes:
                        buf.append(escapes[e])
                        j += 2
                        continue
                    if e == "u":
                        buf.append(chr(int(src[j + 2:j + 6], 16)))
                        j += 6
                        continue
                    buf.append(e)
                    j += 2
                    continue
                if ch == '"' or ch == "\n":
                    break
                buf.append(ch)
                j += 1
            lits.append(("".join(buf), start, interpolated))
            out.append("\x01%d\x02" % (len(lits) - 1) + "\n" * nl)
            line += nl
            i = j + 1
            continue

        out.append(c)
        i += 1

    return "".join(out), lits


def literal(arg, lits):
    a = arg.strip()
    if not a:
        return None
    value = []
    for part in re.split(r"\s*\+\s*", a):
        m = re.fullmatch(r"\x01(\d+)\x02\s*", part)
        if not m:
            return None
        value.append(lits[int(m.group(1))][0])
    return "".join(value)


def scan_file(path):
    """Ключи перевода и замечания по одному файлу."""
    src = io.open(path, encoding="utf-8-sig").read()
    lines = src.split("\n")
    masked, lits = lex(src)
    rel = os.path.relpath(path, ROOT).replace(os.sep, "/")

    keys = []      # (ctx, id, plural, line)
    problems = []  # (line, text)

    def flag(line_no, word):
        return 0 < line_no <= len(lines) and word in lines[line_no - 1]

    for m in CALL.finditer(masked):
        name = m.group(1)
        depth, args, cur, j = 0, [], [], m.end()
        while j < len(masked):
            ch = masked[j]
            if ch in "([{":
                depth += 1
            elif ch in ")]}":
                if depth == 0:
                    args.append("".join(cur))
                    break
                depth -= 1
            elif ch == "," and depth == 0:
                args.append("".join(cur))
                cur = []
                j += 1
                continue
            cur.append(ch)
            j += 1

        line = masked.count("\n", 0, m.start()) + 1
        values = [literal(a, lits) for a in args]
        dynamic = flag(line, "loc-dynamic")

        def need(index):
            if index < len(values) and values[index] is not None:
                return values[index]
            if not dynamic:
                problems.append((line, "L.%s: аргумент %d не литерал — пометь строку «loc-dynamic», если это намеренно" % (name, index + 1)))
            return None

        if name in ("T", "F", "M"):
            v = need(0)
            if v is not None:
                keys.append((None, v, None, line))
        elif name in ("Tc", "Fc"):
            ctx, v = need(0), need(1)
            if ctx is not None and v is not None:
                keys.append((ctx, v, None, line))
        elif name == "N":
            s, p = need(0), need(1)
            if s is not None and p is not None:
                keys.append((None, s, p, line))
        elif name == "C":
            v = need(0)
            if v is not None:
                keys.append((None, v, None, line))
            if len(args) > 1 and args[1].strip() not in ("null", ""):
                tip = values[1]
                if tip is not None:
                    keys.append((None, tip, None, line))
                elif not dynamic:
                    problems.append((line, "L.C: подсказка не литерал"))
        elif name in ("Cs", "Ts"):
            for index, v in enumerate(values):
                if v is not None:
                    keys.append((None, v, None, line))
                elif not dynamic:
                    problems.append((line, "L.%s: аргумент %d не литерал" % (name, index + 1)))

    for text, line, _ in lits:
        if CYRILLIC.search(text) and not flag(line, "loc-ignore"):
            problems.append((line, "кириллица в строке: " + text.replace("\n", "\\n")[:90]))

    return rel, keys, problems


def source_files(paths=None):
    if paths:
        return [os.path.abspath(p) for p in paths]
    found = []
    for base in ("Editor", "Samples~"):
        for dirpath, _, files in os.walk(os.path.join(ROOT, base)):
            for f in files:
                if f.endswith(".cs"):
                    found.append(os.path.join(dirpath, f))
    return sorted(found)


# ----------------------------------------------------------------- PO ---

def unquote(s):
    s = s.strip()
    if len(s) >= 2 and s[0] == '"' and s[-1] == '"':
        s = s[1:-1]
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == "\\" and i + 1 < len(s):
            e = s[i + 1]
            out.append({"n": "\n", "t": "\t", "r": "\r", '"': '"', "\\": "\\"}.get(e, "\\" + e))
            i += 2
            continue
        out.append(c)
        i += 1
    return "".join(out)


def quote(s):
    s = s.replace("\\", "\\\\").replace('"', '\\"').replace("\t", "\\t").replace("\r", "\\r")
    if "\n" not in s or s == "\n":
        return '"' + s.replace("\n", "\\n") + '"'
    parts = s.split("\n")
    chunks = [p + "\\n" for p in parts[:-1]] + ([parts[-1]] if parts[-1] else [])
    return '""\n' + "\n".join('"' + c + '"' for c in chunks)


def parse_po(path):
    """{(ctx, id): {"plural", "str", "forms", "refs", "fuzzy"}} и заголовок."""
    entries, header = {}, None
    if not os.path.exists(path):
        return entries, header

    cur, field = None, None

    def flush():
        nonlocal cur, header
        if cur and cur.get("id") is not None:
            if cur["id"] == "" and cur.get("ctx") is None:
                header = cur.get("str", "")
            else:
                entries[(cur.get("ctx"), cur["id"])] = cur
        cur = None

    pending = {"refs": [], "fuzzy": False}
    for raw in io.open(path, encoding="utf-8-sig").read().replace("\r\n", "\n").split("\n"):
        line = raw.strip()
        if not line:
            continue
        if line.startswith("#:"):
            pending["refs"].extend(line[2:].split())
            continue
        if line.startswith("#,"):
            pending["fuzzy"] = pending["fuzzy"] or "fuzzy" in line
            continue
        if line.startswith("#"):
            continue
        if line.startswith('"'):
            value = unquote(line)
            if field and field.startswith("str["):
                cur["forms"][int(field[4:-1])] += value
            elif field:
                cur[field] = (cur.get(field) or "") + value
            continue

        keyword, _, rest = line.partition(" ")
        value = unquote(rest)
        if keyword == "msgctxt" or (keyword == "msgid" and (cur is None or "id" in cur)):
            flush()
            cur = {"refs": pending["refs"], "fuzzy": pending["fuzzy"], "forms": {}}
            pending = {"refs": [], "fuzzy": False}
        if keyword == "msgctxt":
            cur["ctx"], field = value, "ctx"
        elif keyword == "msgid":
            cur["id"], field = value, "id"
        elif keyword == "msgid_plural":
            cur["plural"], field = value, "plural"
        elif keyword == "msgstr":
            cur["str"], field = value, "str"
        elif keyword.startswith("msgstr["):
            index = int(keyword[7:-1])
            cur["forms"][index] = value
            field = "str[%d]" % index
    flush()
    return entries, header


def write_po(path, entries, header=HEADER_RU, template=False):
    def order(item):
        (ctx, msgid), e = item
        refs = e.get("refs") or ["~"]
        return (refs[0].split(":")[0], msgid, ctx or "")

    out = ['msgid ""', 'msgstr ' + quote(header or ""), ""]
    for (ctx, msgid), e in sorted(entries.items(), key=order):
        refs = sorted(set(r.split(":")[0] for r in e.get("refs", [])))
        if refs:
            out.append("#: " + " ".join(refs))
        if e.get("fuzzy") and not template:
            out.append("#, fuzzy")
        if ctx is not None:
            out.append("msgctxt " + quote(ctx))
        out.append("msgid " + quote(msgid))
        if e.get("plural") is not None:
            out.append("msgid_plural " + quote(e["plural"]))
            forms = e.get("forms") or {}
            for i in range(3 if not template else 2):
                out.append("msgstr[%d] %s" % (i, quote("" if template else forms.get(i, ""))))
        else:
            out.append("msgstr " + quote("" if template else e.get("str", "")))
        out.append("")

    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with io.open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(out))


def entry_problems(key, e):
    ctx, msgid = key
    problems = []
    want = set(PLACEHOLDER.findall(msgid))
    if e.get("plural") is not None:
        forms = e.get("forms") or {}
        if len(forms) != 3:
            problems.append("нужно 3 формы, а их %d" % len(forms))
        for i, form in forms.items():
            if not form:
                problems.append("пустая форма %d" % i)
            got = set(PLACEHOLDER.findall(form))
            if not got <= (want | set(PLACEHOLDER.findall(e["plural"]))):
                problems.append("форма %d: подстановки %s, в оригинале %s" % (i, sorted(got), sorted(want)))
    else:
        text = e.get("str", "")
        if not text:
            problems.append("пустой перевод")
        elif set(PLACEHOLDER.findall(text)) != want:
            problems.append("подстановки %s, в оригинале %s" % (sorted(set(PLACEHOLDER.findall(text))), sorted(want)))
    return problems


# ------------------------------------------------------------ команды ---

def collect(files):
    used, problems = {}, []
    for path in files:
        rel, keys, file_problems = scan_file(path)
        for line, text in file_problems:
            problems.append("%s:%d: %s" % (rel, line, text))
        for ctx, msgid, plural, line in keys:
            u = used.setdefault((ctx, msgid), {"plural": plural, "refs": []})
            u["refs"].append("%s:%d" % (rel, line))
            if plural is not None and u["plural"] is None:
                u["plural"] = plural
    return used, problems


def report(used, entries, problems, show_unused=True):
    failed = bool(problems)
    for p in problems:
        print(p)

    for key, u in sorted(used.items(), key=lambda kv: kv[1]["refs"][0]):
        e = entries.get(key)
        where = u["refs"][0]
        if e is None:
            failed = True
            print("%s: нет перевода: %s%s" % (where, ("[%s] " % key[0]) if key[0] else "", key[1].replace("\n", "\\n")))
            continue
        if (u["plural"] is None) != (e.get("plural") is None):
            failed = True
            print("%s: в коде %s, в .po %s: %s" % (where, "L.N" if u["plural"] is not None else "не L.N",
                                                   "множественное" if e.get("plural") is not None else "обычное", key[1]))
        for p in entry_problems(key, e):
            failed = True
            print("%s: %s: %s" % (where, key[1].replace("\n", "\\n"), p))

    if show_unused:
        for key in sorted(k for k in entries if k not in used):
            print("не используется: %s%s" % (("[%s] " % key[0]) if key[0] else "", key[1].replace("\n", "\\n")))

    print("строк в коде: %d, в .po: %d, замечаний: %s" % (len(used), len(entries), "есть" if failed else "нет"))
    return 1 if failed else 0


def cmd_scan(args):
    used, problems = collect(source_files())
    entries, header = parse_po(args.po)
    code = report(used, entries, problems, show_unused=not args.prune)

    if args.refs or args.prune:
        for key, e in list(entries.items()):
            if key in used:
                e["refs"] = used[key]["refs"]
            elif args.prune:
                del entries[key]
        write_po(args.po, entries, header or HEADER_RU)
        print("переписан " + os.path.relpath(args.po, ROOT))
    return code


def cmd_pot(args):
    used, _ = collect(source_files())
    entries = {k: {"plural": u["plural"], "refs": u["refs"]} for k, u in used.items()}
    write_po(args.out, entries, header="Project-Id-Version: Git for Unity\nContent-Type: text/plain; charset=UTF-8\n", template=True)
    print("шаблон: %d строк → %s" % (len(entries), args.out))
    return 0


def cmd_check(args):
    used, problems = collect(source_files(args.files))
    entries, _ = parse_po(args.po)
    return report(used, entries, problems)


def cmd_merge(args):
    merged, header = parse_po(args.out) if args.keep else ({}, None)
    conflicts = 0
    paths = []
    for pattern in args.parts:
        paths.extend(sorted(glob.glob(pattern)) or [pattern])

    for path in paths:
        entries, _ = parse_po(path)
        for key, e in entries.items():
            old = merged.get(key)
            if old is None:
                merged[key] = e
                continue
            same = (old.get("str"), old.get("forms")) == (e.get("str"), e.get("forms"))
            if not same:
                conflicts += 1
                print("конфликт: %s%s\n  %s ← %s\n  %s ← %s" % (
                    ("[%s] " % key[0]) if key[0] else "", key[1].replace("\n", "\\n"),
                    old.get("str") or old.get("forms"), ", ".join(old.get("refs", [])),
                    e.get("str") or e.get("forms"), ", ".join(e.get("refs", []))))
            old["refs"] = old.get("refs", []) + e.get("refs", [])

    write_po(args.out, merged, header or HEADER_RU)
    print("слито файлов: %d, строк: %d, конфликтов: %d" % (len(paths), len(merged), conflicts))
    return 1 if conflicts else 0


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("scan")
    p.add_argument("--po", default=DEFAULT_PO)
    p.add_argument("--refs", action="store_true", help="обновить ссылки на файлы в .po")
    p.add_argument("--prune", action="store_true", help="удалить из .po неиспользуемые строки")
    p.set_defaults(run=cmd_scan)

    p = sub.add_parser("pot")
    p.add_argument("out")
    p.set_defaults(run=cmd_pot)

    p = sub.add_parser("check")
    p.add_argument("--po", required=True)
    p.add_argument("files", nargs="+")
    p.set_defaults(run=cmd_check)

    p = sub.add_parser("merge")
    p.add_argument("--out", required=True)
    p.add_argument("--keep", action="store_true", help="сливать поверх существующего --out")
    p.add_argument("parts", nargs="+")
    p.set_defaults(run=cmd_merge)

    args = parser.parse_args()
    sys.exit(args.run(args))


if __name__ == "__main__":
    main()
