# Git for Unity

`ru.lev.unity-git` — git inside the Unity Editor. Open it with **Window → Git**,
configure it in **Edit → Project Settings → Git**.

[Русская версия](README.ru.md)

The package does not try to replace a git client for code. It does what only the
editor can do: it knows that `Foo.cs` and `Foo.cs.meta` are one thing, sees a scene
as objects and components rather than lines of YAML, and understands that switching
a branch means a reimport.

It works with any git server. Hosting features — merge requests, issues, reviews —
come from **integrations**; the first one is GitLab.

## Requirements

* Unity 6000.0 or newer (tested with 6000.3)
* `git` in PATH; `git-lfs` for locks and LFS
* For the GitLab integration — an API token with the `api` scope

## Installation

* **Asset Store:** add the package to your assets and import it from Package Manager → My Assets.
* **Git URL:** Package Manager → **+** → **Add package from git URL…** and enter
  `https://github.com/LatyninEugene/git-for-unity.git?path=/Packages/ru.lev.unity-git`.
  Append `#v1.0.0-alpha.1` to pin the version.
* **.unitypackage:** Assets → Import Package → Custom Package…. The package is placed in
  `Packages/ru.lev.unity-git` as an embedded package and appears in Package Manager,
  with the sample available on its Samples tab.

## Getting started

1. Open the project that lives in a git repository and choose **Window → Git**.
2. On **Changes**, include files, hunks or lines, write a message and commit. Pull, Push and
   Fetch are in the window header.
3. Select a scene, a prefab or any asset in the list to see what changed — as objects,
   in the Inspector, or as a picture.
4. Right-click an asset in the Project window → **Git** for its history and locks.
5. If your project is on GitLab, enable the integration in **Project Settings → Git**.

## Core features

* **Status in the Project window.** A badge over the asset icon: `M` modified, `A` added,
  `?` untracked, `D` deleted, `R` renamed, `!` conflict. An asset and its `.meta` share one badge.
* **Changes and commits.** A tree of changes, checkboxes on files, hunks and lines,
  diff with the changed part of a line highlighted, amend, and pre-commit checks:
  asset without `.meta`, duplicate GUIDs, references to files left out of the commit,
  large files outside LFS, an LFS pointer instead of content.
* **Scenes and prefabs as objects.** Semantic diff by `fileID`: which object was added,
  which component changed, which property moved. Change marks in the Hierarchy and the
  Inspector, a before/after window with reverting of values.
* **Asset previews.** Every version of an asset is shown the way it looks: textures,
  materials, meshes and models, audio, animations, Timeline, prefabs, scene settings.
  Tabs View, Inspector, Changes, Objects and Text; compare modes Side by Side, Swipe,
  Toggle, Overlay and Difference.
* **History and branches.** Log with a graph, filters, commit actions, safe branch
  switching, file and asset history.
* **Object history.** Who changed a scene, an object or a component, when and in which
  commit — from the Hierarchy and Inspector context menus.
* **Conflicts.** Scenes and prefabs are merged by objects and properties: hierarchy and
  inspector in three columns — mine, result, theirs; the result is validated before it is
  written, backups go to `Library/LevGit/MergeBackups`. Text files are resolved in the
  window, binaries by whole version.
* **LFS and locks.** Lock marks in the Project window, Hierarchy and Inspector; lock and
  unlock from context menus with freshness checks; saving a file locked by someone else is
  intercepted; auto-lock; `lockable` setup; downloading LFS content that is missing.
* **Push recovery.** When the server rejects a push because histories diverged (for example
  after an amend), the window explains why and offers `push --force-with-lease` after a fresh
  fetch; a protected branch is reported as such.
* **Console.** Every git command the package runs, with its output, exit code and duration,
  and a command line for your own git commands. For push, pull, fetch, clone and ls-remote the
  console shows **stages** from git's own trace (trace2): hooks, ssh, packing — when each started
  and how long it took. Stages of failed and slow commands also go to the log and the problem report.

## Language

The interface is available in English and Russian and follows the system language by
default. Change it in **Preferences → Git → Language** or in the **⋮** menu of the Git
window — windows switch immediately, without a restart. Menu items stay in English.

Translations are gettext files in `Editor/Localization/<language>.po`. To add a language,
generate a template with `python "Tools~/localization.py" pot template.pot`, translate it
and save it as `Editor/Localization/<code>.po`.

## Reporting a problem

**Help → Git for Unity → Report a Problem…** (also in the Git window menu and next to an
error in the status line) collects a zip file with diagnostic data:

* Unity, OS, git and git-lfs versions, the git settings that matter;
* a repository summary — branch, number of changes and conflicts, locks — without file contents;
* package settings without server addresses;
* whether the package found the Unity internals it relies on;
* recent warnings, errors and git commands, and the package log for the chosen number of days.

You can study the report yourself or attach it to an
[issue on GitHub](https://github.com/LatyninEugene/git-for-unity/issues/new/choose). It is not sent anywhere automatically.
Tokens, passwords and credentials in URLs are always removed. File paths, branch names
and server addresses can be hidden. You see the report text before saving it.

If a problem is hard to catch, turn on **Detailed Log** in the report window, repeat the
problem, then save the report. The package log lives in `Library/LevGit/Logs` and is kept
for 7 days (**Help → Git for Unity → Open Log Folder**).

## Integrations

**Project Settings → Git → Integrations** — every integration has a checkbox and its own
settings page. A disabled integration shows no tab and never contacts the server. If the
project remote looks like an integration's server, the page offers to enable it.

An integration is a class implementing `Lev.Git.IGitIntegration` in its own assembly on
top of `Lev.Git.Editor`. The core finds it through `TypeCache` and uses only what it
provides: web addresses of commits and branches, the SSH keys page, the token page, a
tab in the window.

Optional capabilities:

* `IGitBranchBadges` — badges next to branches in the log (GitLab: an open merge request);
* `DiffView.LineMenu`, `ObjectMenu`, `LineNote`, `ObjectNote` — menu items and notes on diff
  lines and scene objects: this is how an integration tab attaches review comments;
* "Open in …" items for commits and branches appear automatically when the integration
  returns `CommitUrl` and `BranchUrl`.

### GitLab

**Project Settings → Git → GitLab.**

* **Server.** The instance address and project path are derived from the git remote but
  can be set manually — for example when the remote uses SSH. They are stored in
  `ProjectSettings/LevGitGitLabSettings.asset` and committed.
* **The API token is separate** from the git credentials used for push, so revoking one does
  not break the other. The token is stored only in the credential store of this computer
  (Windows Credential Manager, Secret Service on Linux) — never in the project or in logs.
  From the settings page you can set or replace it (it is checked on the server first),
  create one in GitLab with the name and scope filled in, check its user, scopes and
  expiry, rotate it through the API, remove it from this computer or revoke it.
* **Workflow** is a project setting, not a package decision: who can merge from the editor,
  what happens after a merge, the branch name template for issues (`{iid}`, `{title}`),
  the base and target branches, draft, squash and the description of new merge requests,
  default list filters.

**The GitLab tab** in the Git window switches between merge requests and issues.

* **Merge requests.** Filters: mine, waiting for my review, all open, merged, closed.
  The card shows whether it can be merged and why not, approvals, pipeline. Actions:
  check out the branch, merge or merge when the pipeline succeeds, rebase on the server,
  draft, approve, close. Create a merge request from the current branch.
* **Merge request changes** look like the Changes tab: tree, previews, diff, scenes as
  objects. Right-click a line to comment on it, or a scene object or component to comment
  on the object. In GitLab the object comment sits on that object's line in the scene YAML
  and is visible in the browser; in the editor it finds the object again by `fileID`.
* **Threads** — comments, replies, resolve and reopen, jump to the line or object.
* **Issues.** Filters: assigned to me, created by me, all open, closed. **Start Work** creates
  a branch from the project template or switches to the existing one and can assign the issue
  to you. On an issue branch — create a merge request with `Closes #N`. A new issue can
  include a screenshot of the Game or Scene view.
* **In the log**, branches with an open merge request get a `!12` badge that opens it in the tab.

GitLab over plain HTTP is supported for local networks. In that case the token travels
unencrypted — the package does not forbid it, but never writes the token to logs or reports.

## Core settings

`ProjectSettings/LevGitSettings.asset`, committed, contains no secrets.

| Setting | Default |
|---|---|
| Primary remote | `origin` |
| Select Individual Lines | off |
| Scene Changes in Hierarchy | on |
| Last Changed By in Inspector | off |
| Auto-Lock on First Edit | off |
| Preview Background | dark |
| Import for Preview | on request |
| Thumbnails in File Lists | images only |
| Preview Storage Limit, MB | 1024 |

## Credentials for push

When an http(s) remote rejects authentication, a sign-in window opens with hints for that
server (GitHub, GitLab, Bitbucket): which user name to use and a button that opens its token
page. What you enter goes to `git credential approve` through stdin and stays in git's
credential store — the package does not keep it.

When an SSH remote rejects the key, the package checks which key in `~/.ssh` the server
accepts. Only public keys are used for this, so no passphrase is needed:

* a key with a non-standard file name, which ssh does not offer by itself, is added to an
  ssh-agent for this editor session;
* for a key protected with a passphrase, the package asks for the passphrase and adds the key
  to the agent until Unity is closed. The passphrase is passed to `ssh-add` only through the
  environment of that one process — never in arguments, files or logs;
* if the server accepts none of the keys, the window lists local keys with fingerprints to
  compare with the server's SSH keys page, and offers to switch the remote to HTTP(S).

On Windows the package uses the OpenSSH tools shipped with Git for Windows, because that is
the ssh git runs. If git is configured to use another ssh (`core.sshCommand`, `GIT_SSH`), the
check is skipped. Output of commands that handle credentials never reaches the log.

## Custom asset previews

Asset changes are shown the way the asset looks — in the View, Inspector and Changes tabs of
the Git window, in Asset History, conflicts and thumbnails. For your own types this is
configured with three classes from the `Lev.Git.Preview` namespace. Nothing has to be
registered: the package finds subclasses through `TypeCache` in any editor assembly.

| Class | Responsible for | When you need it |
|---|---|---|
| `ChangeDescriber<T>` | what changed, in Inspector terms; reverting items | to show readable items instead of field paths |
| `AssetPresenter` | how to draw one version | to make View show the asset, not only fields |
| `AssetLoader` | how to turn the bytes of a version from git into Unity objects | only for custom file formats |

Without custom classes an asset is still shown: YAML assets (ScriptableObjects, materials,
animations), PNG, JPEG, TGA, audio, prefabs and models are read and described by the package.

A **describer** receives two versions and adds changes to `DescribeContext`:

```csharp
public sealed class LevelConfigDescriber : ChangeDescriber<LevelConfig>
{
    protected override void Describe(LevelConfig before, LevelConfig after, DescribeContext ctx)
    {
        if (!Mathf.Approximately(before.timeLimit, after.timeLimit))
            ctx.Add("Time limit", ChangeValue.Of(before.timeLimit), ChangeValue.Of(after.timeLimit))
               .AddPath("timeLimit");   // with a path the item can be reverted and is marked in the Inspector
    }
}
```

- `ctx.Group` — a section of the list ("Waves", "Map"); items added after it belong to it.
- `ChangeValue.Of` accepts a string, number, bool, color (drawn as a swatch) and an object reference (drawn with an icon).
- Revert: `AddPath` for a serialized field, or `item.Revert = (live, source) => …` for custom code.
- `CoversSubAssets = true` if the describer handles sub-assets of the file itself.

A **presenter** draws one side into a texture; the layout of sides (side by side, swipe,
overlay, difference), zoom, orbit and playback are provided by the panel:

```csharp
public sealed class LevelConfigPresenter : AssetPresenter
{
    public override Type Target => typeof(LevelConfig);
    public override PresenterFeatures Features => PresenterFeatures.Zoom;   // the panel adds zoom

    public override Texture Render(Rect rect, LoadedAsset side, int slot, PreviewSync sync)
        => BuildMap((LevelConfig)side.Main);                                  // cache it, release it in Dispose

    public override void DrawOverlay(Rect fit, Rect source, LoadedAsset side, LoadedAsset other, bool isAfter, PreviewSync sync)
    {
        // outlines over the picture; source is the visible part after zoom
    }
}
```

- `Modes` — available compare modes; `Interactive = true` — the panel passes orbiting to `sync.Orbit`.
- `Shapes` and `Choices` — toggles and a dropdown in the tool bar (shape, layer, curve).
- `Prepare(before, after)` — called once both sides are loaded: common scale, range, list of options.
- `sync.Static` — a thumbnail is rendered outside the window: use `BeginStaticPreview` for 3D.

A **loader** is needed for formats the package does not know:

```csharp
public sealed class LevelTextLoader : AssetLoader
{
    public override bool CanLoad(string projectPath) => projectPath.EndsWith(".level");

    public override LoadedAsset Load(string projectPath, byte[] bytes)
    {
        var level = ScriptableObject.CreateInstance<LevelConfig>();
        level.hideFlags = HideFlags.HideAndDontSave;          // a temporary object of the version
        // … parse bytes …
        return new LoadedAsset { Main = level, All = new Object[] { level }, Describable = true };
    }
}
```

- Return errors with `LoadedAsset.Failed("why")` rather than throwing.
- `PreferFileForWorktree = true` — read the current version from the file too instead of using the project asset.
- `LoadAsync` — when Unity has to be awaited (for example `UnityWebRequest`).
- `AddFact("Size", "512×512")` — file facts; they appear in the Changes tab.

**Rules.** Selection works like `CustomEditor`: the most derived `Target` wins, then the higher
`Priority`, so a project can replace a built-in presenter. Everything runs on the main thread.
The panel destroys `Main` and `All` of versions loaded from git; `Data` implementing
`IDisposable` is released with the version. An exception in your code does not break the
window: the error text is shown instead of the picture.

**Referencing from your package** — the editor assembly compiles only when Git for Unity is installed:

```json
"references": ["Lev.Git.Editor"],
"defineConstraints": ["LEV_GIT"],
"versionDefines": [{ "name": "ru.lev.unity-git", "expression": "1.0.0-alpha.1", "define": "LEV_GIT" }]
```

`PreviewApi.Version` is the contract version: it grows when a public member signature changes.
The full example is in Package Manager → Git for Unity → Samples → Custom Change Preview.

## Known limitations

* Menu items are always in English: Unity compiles menu paths, they cannot change at runtime.
* A few features rely on Unity internals: the git badge in component headers and the aligned
  multi-column Inspector. If a Unity version changes them, the feature turns off and the rest
  keeps working; the problem report shows what was found.
* The GitLab API token can be stored on Windows and on Linux with Secret Service (`secret-tool`).
* **No API stability guarantees in alpha.** Many types in the assemblies are public, but only
  what this README documents is meant for your code: the preview extension API
  (`Lev.Git.Preview`) and the integration interfaces (`IGitIntegration` and friends). Both may
  still change before 1.0.0. Everything else is an implementation detail and can change or
  disappear in any version.

## Structure

| Assembly | Contents |
|---|---|
| `Lev.Git.Editor` | core: git processes, status, commit, diff, scenes, history, conflicts, LFS, previews, integration registry, secret store, localization, diagnostics |
| `Lev.Git.GitLab.Editor` | GitLab integration: REST API client, token, settings, tab |
| `Lev.Git.Timeline.Editor` | Timeline previews; compiled only when the Timeline package is installed |

## Development

* `Tests~/Pure` — tests of the Unity-independent engines (YAML parsing, semantic diff, three-way
  merge, text merge, syntax highlighting, localization, redaction): `dotnet run -c Release`.
* `Tools~/compile-check.sh` — compiles every assembly and the sample with the .NET SDK against
  an installed editor, without opening Unity.
* `Tools~/localization.py` — checks that every interface string has a translation and builds `.po` files.

## License

[MIT](LICENSE.md). Changes between versions are listed in [CHANGELOG.md](CHANGELOG.md).
