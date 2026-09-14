#!/usr/bin/env python3
"""
Сборка .unitypackage без Unity.

  python "Tools~/build-unitypackage.py" [--layout assets|packages] [--out ПАПКА]

.unitypackage — это tar.gz в формате POSIX ustar: на каждый файл и папку
каталог с именем GUID, в нём asset (содержимое, у папок нет), asset.meta и
pathname (путь при импорте, с хвостом «\\n00»). Файл .icon.png в корне Unity
показывает иконкой пакета в окне импорта. Формат сверен с пакетами из
Asset Store, собранными самим Unity.

Раскладка (--layout):
  packages — Packages/ru.lev.unity-git/… (по умолчанию): встаёт встроенным
             пакетом, виден в Package Manager.
  assets   — Assets/Git for Unity/… — для тех, кто держит плагины в Assets.

Samples~ входит в пакет: Unity эту папку не импортирует и мет для неё не
создаёт, поэтому .meta генерируются здесь, с постоянными GUID. После импорта
пример ставится из Package Manager, вкладка Samples. Tests~ и Tools~ в пакет
не входят.

Нужен Pillow (pip install pillow) — только чтобы нарисовать иконку, если
Tools~/icon.png ещё нет.
"""
import argparse
import gzip
import hashlib
import io
import json
import os
import re
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ICON = os.path.join(HERE, "icon.png")

NUL = b"\x00"
PATHNAME_TAIL = "\n00"


def draw_icon(path, size=256):
    """
    Иконка пакета — родственница иконки окна Git (GitIcons): ствол с точками
    и отросток, скруглённые концы, плоские цвета без градиентов. Не копия:
    отросток уходит от ствола вверх, а не приходит в него, и цвета — колонок
    графа истории. Рисуется в 4 раза крупнее и сглаживается уменьшением.
    """
    from PIL import Image, ImageDraw

    s = size * 4
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Плитка — один спокойный тёмный тон, как фон панелей редактора.
    tile = (42, 46, 51, 255)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.2), fill=tile)

    # Цвета колонок графа истории в тёмном скине — GitPalette.Lane(0) и Lane(1).
    trunk = (107, 168, 242, 255)
    branch = (133, 209, 122, 255)

    stroke = s * 0.07
    dot_r = s * 0.078

    def brush(points, color):
        # Кривая «кистью» из кругов: у толстой line на изгибах зазубрины.
        for (ax, ay), (bx, by) in zip(points, points[1:]):
            n = max(1, int(((bx - ax) ** 2 + (by - ay) ** 2) ** 0.5 / 2))
            for k in range(n + 1):
                x, y = ax + (bx - ax) * k / n, ay + (by - ay) * k / n
                d.ellipse([x - stroke / 2, y - stroke / 2, x + stroke / 2, y + stroke / 2], fill=color)

    def quad(p0, p1, p2, steps=80):
        return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
                 (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1])
                for t in (i / steps for i in range(steps + 1))]

    def dot(x, y, color):
        d.ellipse([x - dot_r, y - dot_r, x + dot_r, y + dot_r], fill=color)

    # Ствол слева, отросток от его нижней части плавно поднимается вправо.
    tx, top_y, bottom_y = s * 0.38, s * 0.27, s * 0.73
    bx, by = s * 0.64, s * 0.40
    fork_y = s * 0.64

    brush(quad((tx, fork_y), (bx, fork_y), (bx, by)), branch)
    brush([(tx, top_y), (tx, bottom_y)], trunk)

    dot(tx, top_y, trunk)
    dot(tx, bottom_y, trunk)
    dot(bx, by, branch)

    img = img.resize((size, size), Image.LANCZOS)
    img.save(path, format="PNG")


def guid_of(meta, where):
    m = re.search(r"^guid:\s*([0-9a-f]{32})\s*$", meta.decode("utf-8"), re.MULTILINE)
    if not m:
        raise SystemExit("нет guid в .meta у " + where)
    return m.group(1)


# Папки с «~», которые входят в пакет. Tests~ и Tools~ — нет: это разработка.
PACKED_TILDE = ("Samples~",)


def generated_meta(rel, is_dir):
    """
    .meta для файла из Samples~. Unity такие папки не импортирует и мет для них
    не создаёт, а без .meta запись в .unitypackage не добавить. GUID постоянный —
    из пути, поэтому при каждой сборке одинаковый. Импортёр выбран по типу файла,
    как его выбрал бы сам Unity: иначе при установке примера меты перепишутся.
    """
    guid = hashlib.md5(("sample:" + rel).encode("utf-8")).hexdigest()
    if is_dir:
        head = "folderAsset: yes\nDefaultImporter:\n"
    elif rel.endswith(".cs"):
        head = ("MonoImporter:\n  serializedVersion: 2\n  defaultReferences: []\n"
                "  executionOrder: 0\n  icon: {instanceID: 0}\n")
    elif rel.endswith(".asmdef"):
        head = "AssemblyDefinitionImporter:\n"
    elif rel.endswith((".md", ".txt", ".json")):
        head = "TextScriptImporter:\n"
    else:
        head = "DefaultImporter:\n"
    text = ("fileFormatVersion: 2\nguid: %s\n%s  externalObjects: {}\n"
            "  userData: \n  assetBundleName: \n  assetBundleVariant: \n") % (guid, head)
    return text.encode("utf-8")


def items():
    """(полный путь, путь относительно пакета, папка ли, содержимое .meta)."""
    for dirpath, dirs, files in os.walk(ROOT):
        rel_dir = os.path.relpath(dirpath, ROOT).replace(os.sep, "/")
        in_tilde = rel_dir.split("/")[0] in PACKED_TILDE
        dirs[:] = sorted(d for d in dirs
                         if not d.startswith(".") and (not d.endswith("~") or (rel_dir == "." and d in PACKED_TILDE)))
        for name in dirs + sorted(files):
            if name.endswith(".meta") or name.startswith("."):
                continue
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, ROOT).replace(os.sep, "/")
            is_dir = os.path.isdir(full)
            if os.path.exists(full + ".meta"):
                with open(full + ".meta", "rb") as f:
                    meta = f.read()
            elif in_tilde or rel in PACKED_TILDE:
                meta = generated_meta(rel, is_dir)
            else:
                raise SystemExit("нет .meta у " + rel + " — открой проект в Unity, чтобы она появилась")
            yield full, rel, is_dir, meta


class UstarWriter:
    """
    Архив в формате POSIX ustar — как его пишет сам Unity.

    Не модуль tarfile: он пишет GNU-заголовки (сигнатура «ustar» + два пробела)
    и добавляет «/» к имени каталога. Разборщик .unitypackage в Unity такой
    заголовок не узнаёт и видит пустой пакет, а окно импорта падает с
    NullReferenceException в PackageImportTreeView на пустом корне дерева.
    """

    def __init__(self, path, mtime):
        self._raw = open(path, "wb")
        self._gz = gzip.GzipFile(filename="", mode="wb", fileobj=self._raw, mtime=mtime)
        self._mtime = mtime

    def _header(self, name, size, typeflag, mode):
        encoded = name.encode("utf-8")
        if len(encoded) > 100:
            raise SystemExit("имя в архиве длиннее 100 байт: " + name)

        def octal(value, width):
            # Число в восьмеричной записи с ведущими нулями и завершающим NUL.
            return ("%0*o" % (width - 1, value)).encode("ascii") + NUL

        h = bytearray(512)
        h[0:len(encoded)] = encoded
        h[100:108] = octal(mode, 8)
        h[108:116] = octal(0, 8)            # uid
        h[116:124] = octal(0, 8)            # gid
        h[124:136] = octal(size, 12)
        h[136:148] = octal(self._mtime, 12)
        h[148:156] = b" " * 8               # контрольная сумма считается с пробелами на её месте
        h[156:157] = typeflag
        h[257:263] = b"ustar" + NUL
        h[263:265] = b"00"
        checksum = sum(h)
        h[148:156] = ("%06o" % checksum).encode("ascii") + NUL + b" "
        return bytes(h)

    def directory(self, name):
        self._gz.write(self._header(name, 0, b"5", 0o755))

    def file(self, name, data):
        self._gz.write(self._header(name, len(data), b"0", 0o644))
        self._gz.write(data)
        if len(data) % 512:
            self._gz.write(NUL * (512 - len(data) % 512))

    def close(self):
        self._gz.write(NUL * 1024)          # конец архива — два пустых блока
        self._gz.close()
        self._raw.close()


def add_entry(tar, guid, pathname, meta, asset):
    """Запись как у Unity: каталог GUID, затем asset, asset.meta и pathname."""
    tar.directory(guid)
    if asset is not None:
        tar.file(guid + "/asset", asset)
    tar.file(guid + "/asset.meta", meta)
    tar.file(guid + "/pathname", (pathname + PATHNAME_TAIL).encode("utf-8"))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--layout", choices=("packages", "assets"), default="packages")
    parser.add_argument("--out", default=os.path.normpath(os.path.join(ROOT, "..", "..", "..", "GitForUnity-Builds")))
    args = parser.parse_args()

    with io.open(os.path.join(ROOT, "package.json"), encoding="utf-8") as f:
        package = json.load(f)
    name, version, title = package["name"], package["version"], package.get("displayName", package["name"])

    if not os.path.exists(ICON):
        draw_icon(ICON)

    os.makedirs(args.out, exist_ok=True)
    suffix = "" if args.layout == "packages" else " (Assets)"
    out = os.path.join(args.out, "%s %s%s.unitypackage" % (title, version, suffix))
    base = "Assets/" + title if args.layout == "assets" else "Packages/" + name
    mtime = int(time.time())
    count = 0

    tar = UstarWriter(out, mtime)
    try:
        # Корень пакета — тоже папка в дереве окна импорта, а своей .meta у него
        # нет. GUID постоянный — из имени пакета.
        root_guid = hashlib.md5(("package-root:" + name).encode("utf-8")).hexdigest()
        root_meta = ("fileFormatVersion: 2\n"
                     "guid: %s\n"
                     "folderAsset: yes\n"
                     "DefaultImporter:\n"
                     "  externalObjects: {}\n"
                     "  userData: \n"
                     "  assetBundleName: \n"
                     "  assetBundleVariant: \n") % root_guid
        add_entry(tar, root_guid, base, root_meta.encode("utf-8"), None)

        for full, rel, is_dir, meta in items():
            guid = guid_of(meta, rel)
            asset = None
            if not is_dir:
                with open(full, "rb") as f:
                    asset = f.read()
            add_entry(tar, guid, base + "/" + rel, meta, asset)
            count += 1

        with open(ICON, "rb") as f:
            tar.file(".icon.png", f.read())
    finally:
        tar.close()

    print("%d элементов → %s (%.0f КБ)" % (count, out, os.path.getsize(out) / 1024))


if __name__ == "__main__":
    main()
