# Contributing to Git for Unity

Thanks for your interest. Bug reports, fixes, translations and previews for more asset types are all welcome.

## Reporting a bug

1. In Unity, reproduce the problem. If it is hard to catch, turn on **Detailed Log** in the report window first.
2. **Help → Git for Unity → Report a Problem…** → save the zip.
3. Open a [bug report](https://github.com/LatyninEugene/git-for-unity/issues/new/choose) and attach the zip.

The report never contains tokens or passwords. You can also hide file paths, branch names and server addresses.

## Repository layout

This repository is a Unity project used for development. The package itself lives in `Packages/ru.lev.unity-git` —
that is the folder users install by git URL.

| Path | What it is |
|---|---|
| `Packages/ru.lev.unity-git/Editor` | the package: core (`Lev.Git.Editor`), GitLab integration, Timeline previews |
| `Packages/ru.lev.unity-git/Editor/Localization` | `L` — interface translation and `ru.po` |
| `Packages/ru.lev.unity-git/Samples~` | the sample installed from Package Manager |
| `Packages/ru.lev.unity-git/Tests~/Pure` | tests of the Unity-independent engines, run with .NET |
| `Packages/ru.lev.unity-git/Tools~` | compile check, localization check, `.unitypackage` build |

Folders ending with `~` are ignored by Unity and are not imported into user projects.

## Development setup

1. Clone the repository and open the root folder in Unity 6000.0 or newer (the package is developed on 6000.3).
2. The package is embedded, so edits in `Packages/ru.lev.unity-git` recompile immediately.
3. For the tests and tools install the [.NET 8 SDK](https://dotnet.microsoft.com/download) and Python 3.

## Checks before a pull request

```bash
cd "Packages/ru.lev.unity-git/Tests~/Pure"
dotnet run -c Release
```

```bash
python "Packages/ru.lev.unity-git/Tools~/localization.py" scan
```

Optionally, compile every assembly without opening Unity:

```bash
UNITY_EDITOR_DATA="C:/Program Files/Unity/Hub/Editor/6000.3.10f1/Editor/Data" UNITY_PROJECT="$PWD" bash "Packages/ru.lev.unity-git/Tools~/compile-check.sh"
```

The same tests and the localization check run on every pull request.

## Interface text and translations

- Write interface text in English inside `L.T("…")`, `L.F("… {0}", x)`, `L.N("{0} file", "{0} files", n)` or `L.C("Label", "Tooltip")`. Never put UI text into a plain string literal.
- Add the Russian translation to `Editor/Localization/ru.po`. `localization.py scan` lists strings without a translation and placeholders that don't match.
- Title Case for window titles, menu items, buttons and headings; sentence case for tooltips and messages.
- To add a language, generate a template with `python "Packages/ru.lev.unity-git/Tools~/localization.py" pot template.pot` and save the translation as `Editor/Localization/<code>.po`.

## Code

- Match the surrounding code: naming, structure, comment density. Most existing comments are in Russian; new comments in English are welcome.
- Log through `Lev.Git.Diagnostics.Journal`, not `Debug.Log`.
- Never log tokens or passwords. Credentials go through `git credential` or the system credential store.
- Unity-independent logic (parsers, merge, diff) belongs in files covered by `Tests~/Pure` — add tests there.

## Pull requests

- One change per pull request, with a short description of what and why.
- Add an entry to `Packages/ru.lev.unity-git/CHANGELOG.md` under an "Unreleased" heading for user-visible changes.
- By contributing you agree that your work is released under the [MIT License](LICENSE).

Please follow the [Code of Conduct](CODE_OF_CONDUCT.md).
