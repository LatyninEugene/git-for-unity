# Custom Change Preview

An example of showing changes to your own asset type in Git for Unity your own way.

## What's Inside

- `Runtime/LevelConfig.cs` — a "level" asset: title, time limit, map as rows of text, enemy waves.
- `Editor/LevelConfigDescriber.cs` — a **describer**: the Changes tab reads
  "Time Limit: 60 s → 45 s", "Cells Changed: 3 (walls +2)", "“boss”: enemies 5 → 8".
  Waves get their own revert, matched by id.
- `Editor/LevelConfigPresenter.cs` — a **presenter**: the View tab draws the map with changed cells
  outlined. Side by Side, Swipe, Overlay, Difference, the magnifier and thumbnails in Asset History
  come from the package.
- `Editor/LevelTextLoader.cs` — a **loader** for a custom `.level` text format. You don't need one
  for `.asset` files: the package reads YAML itself.
- `Example.level` — a file in this format.

## Try It

1. Assets → Create → Lev Git Samples → Level Config, then commit.
2. Change the time limit, the map or the waves. In the Git window → Changes, select the asset and
   look at View, Changes and Inspector.
3. Same for `Example.level`: commit it, then edit the text.

## Using It in Your Own Package

The editor assembly references `Lev.Git.Editor` and compiles only when the package is installed:

```json
"references": ["Lev.Git.Editor"],
"defineConstraints": ["LEV_GIT"],
"versionDefines": [{ "name": "ru.lev.unity-git", "expression": "1.0.0-alpha.1", "define": "LEV_GIT" }]
```

Classes are discovered automatically — nothing needs to be registered.
