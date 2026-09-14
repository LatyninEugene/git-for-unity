#!/bin/bash
# Сборка всех сборок пакета без Unity — быстрая проверка, что код компилируется.
#
# Unity собирает то же самое сама, но при открытом редакторе ошибка в середине
# правки превращается в каскад перекомпиляций. Этот скрипт компилирует файлы с
# диска компилятором из .NET SDK против библиотек установленного редактора.
#
# Пример из Samples~ собирается против готовой Lev.Git.Editor.dll без доступа
# к internal — так проверяется, что публичного API хватает стороннему пакету.
#
#   UNITY_EDITOR_DATA="C:/Program Files/Unity/Hub/Editor/6000.3.10f1/Editor/Data" \
#   UNITY_PROJECT="C:/Users/me/MyProject" \
#   bash "Tools~/compile-check.sh"
#
# UNITY_PROJECT нужен только для сборки поддержки Timeline (Unity.Timeline.dll
# берётся из Library/ScriptAssemblies проекта). Без него эта сборка пропускается.
set -e

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
PKG="$ROOT/Editor"
OUT="${OUT_DIR:-$(mktemp -d)}"

if [ -z "$UNITY_EDITOR_DATA" ]; then
  echo "Задай UNITY_EDITOR_DATA — папку Editor/Data установленного Unity." >&2
  exit 2
fi

CSC="${CSC:-$(ls -d "$(dirname "$(command -v dotnet)")"/sdk/*/Roslyn/bincore/csc.dll 2>/dev/null | tail -1)}"
if [ ! -f "$CSC" ]; then
  echo "Не найден csc.dll из .NET SDK. Укажи путь в переменной CSC." >&2
  exit 2
fi

win() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else echo "$1"; fi; }
quote() { printf '"%s"\n' "$(win "$1")"; }

refs() {
  echo "-target:library"
  echo "-nologo"
  echo "-nostdlib+"
  echo "-langversion:9.0"
  echo "-nowarn:CS0618,CS0649,CS0414"
  echo "-r:$(quote "$UNITY_EDITOR_DATA/NetStandard/ref/2.1.0/netstandard.dll")"
  for f in "$UNITY_EDITOR_DATA"/NetStandard/compat/2.1.0/shims/netstandard/*.dll "$UNITY_EDITOR_DATA"/Managed/UnityEngine/*.dll; do
    echo "-r:$(quote "$f")"
  done
}

sources() { find "$@" -name '*.cs' | sort | while read -r f; do quote "$f"; done; }

build() {
  local name="$1"; shift
  echo "== $name"
  dotnet "$CSC" @"$OUT/$name.rsp" 2>&1 | grep -E 'error|warning CS8' || true
  [ -f "$OUT/$name.dll" ] || { echo "   не собрано"; FAILED=1; }
}

FAILED=0

{ refs; echo "-define:UNITY_EDITOR;UNITY_6000_0_OR_NEWER"; echo "-out:$(quote "$OUT/Lev.Git.Editor.dll")"
  find "$PKG" -name '*.cs' ! -path '*/Integrations/GitLab/*' ! -path '*/Preview/Timeline/*' | sort | while read -r f; do quote "$f"; done
} > "$OUT/Lev.Git.Editor.rsp"
build Lev.Git.Editor

{ refs; echo "-define:UNITY_EDITOR;UNITY_6000_0_OR_NEWER"; echo "-r:$(quote "$OUT/Lev.Git.Editor.dll")"; echo "-out:$(quote "$OUT/Lev.Git.GitLab.Editor.dll")"
  sources "$PKG/Integrations/GitLab"
} > "$OUT/Lev.Git.GitLab.Editor.rsp"
build Lev.Git.GitLab.Editor

TIMELINE="$UNITY_PROJECT/Library/ScriptAssemblies/Unity.Timeline.dll"
if [ -n "$UNITY_PROJECT" ] && [ -f "$TIMELINE" ]; then
  { refs; echo "-define:UNITY_EDITOR;UNITY_6000_0_OR_NEWER;LEV_GIT_TIMELINE"; echo "-r:$(quote "$OUT/Lev.Git.Editor.dll")"; echo "-r:$(quote "$TIMELINE")"
    echo "-out:$(quote "$OUT/Lev.Git.Timeline.Editor.dll")"
    sources "$PKG/Preview/Timeline"
  } > "$OUT/Lev.Git.Timeline.Editor.rsp"
  build Lev.Git.Timeline.Editor
else
  echo "== Lev.Git.Timeline.Editor — пропущено: нет UNITY_PROJECT с установленным Timeline"
fi

{ refs; echo "-define:UNITY_EDITOR;UNITY_6000_0_OR_NEWER;LEV_GIT"; echo "-r:$(quote "$OUT/Lev.Git.Editor.dll")"; echo "-out:$(quote "$OUT/Lev.Git.Samples.dll")"
  sources "$ROOT/Samples~"
} > "$OUT/Lev.Git.Samples.rsp"
build Lev.Git.Samples

echo "Сборки: $OUT"
exit $FAILED
