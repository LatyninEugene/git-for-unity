<p align="center">
  <img src=".github/media/icon.png" width="96" height="96" alt="Git for Unity icon">
</p>

<h1 align="center">Git for Unity</h1>

<p align="center">
  Git inside the Unity Editor — made for scenes, prefabs and assets, not only for text.
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/github/license/LatyninEugene/git-for-unity"></a>
  <img alt="Unity 6000.0+" src="https://img.shields.io/badge/Unity-6000.0%2B-222?logo=unity">
  <a href="https://github.com/LatyninEugene/git-for-unity/releases"><img alt="Latest release" src="https://img.shields.io/github/v/release/LatyninEugene/git-for-unity?include_prereleases&sort=semver"></a>
  <a href="https://github.com/LatyninEugene/git-for-unity/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/LatyninEugene/git-for-unity/actions/workflows/ci.yml/badge.svg"></a>
</p>

<p align="center">
  <b>English</b> · <a href="README.ru.md">Русский</a>
</p>

<p align="center"><img src=".github/media/hero.png" width="900" alt="The Git window: a scene change shown as objects and components"></p>

A general-purpose git client sees a Unity project as text: a scene is thousands of lines of YAML, a
texture is "binary file changed", and a missing `.meta` file shows up only when someone else opens the
project. Git for Unity works inside the editor and sees the project the way you do — objects,
components, properties and how assets actually look.

It works with any git server. Hosting features come from integrations; the first one is
[GitLab](#gitlab-integration) — merge requests, code review with comments on scene objects, and issues.

- [What it fixes](#what-it-fixes)
- [GitLab integration](#gitlab-integration)
- [Installation](#installation)
- [Getting started](#getting-started)
- [Documentation](#documentation)

## What it fixes

### "The scene diff is 3,000 lines of YAML"

Scenes and prefabs are compared by objects, not lines: which GameObject was added, removed or moved,
which component changed and which property, under the names you see in the Inspector. The same
marks appear right in the Hierarchy and the Inspector, so you can find the change in the open scene.
Any change can be opened as a before/after Inspector or compared with the current state of the scene.

<p align="center"><img src=".github/media/scene-diff.png" width="860" alt="Scene changes as objects, components and properties"></p>

<p align="center"><img src=".github/media/hierarchy-marks.png" width="860" alt="Change marks in the Hierarchy and the Inspector"></p>

### "Binary file changed — but what exactly?"

Every version of an asset is shown the way it looks: textures, materials, meshes and models, audio,
animation clips and controllers, Timeline, prefabs, scene settings. Compare two versions side by side,
with a swipe, by toggling, as an overlay or as a difference mask. File lists show thumbnails, and
**Asset History** lays out all versions of an asset on a timeline.

<p align="center"><img src=".github/media/asset-compare.png" width="860" alt="Two versions of an asset compared"></p>

<p align="center"><img src=".github/media/asset-history.png" width="860" alt="Asset History: every version of an asset on a timeline"></p>

### "Who changed this object, and when?"

Right-click an object in the Hierarchy → **Git → Object History**. The window lists every commit that
touched the object — which components were added and which properties changed, with author and time.

<p align="center"><img src=".github/media/object-history.png" width="860" alt="Object History opened from the Hierarchy: commits with changed components and properties"></p>

Switch the view to **Inspector** to see each change as real Inspectors — before and after the commit,
or compared with the current state. Any component can be restored from that version without reverting
the whole scene. Component History opens the same way from the Inspector.

<p align="center"><img src=".github/media/object-history-inspector.png" width="860" alt="Object History as Inspectors: each component before and after the commit, with Restore"></p>

### "Merge conflict"

Merge a branch right from the History tab.

<p align="center"><img src=".github/media/conflicts-0.png" width="860" alt="Merging a branch into the current one from the branch menu"></p>

If both sides changed the same scene, the Changes tab shows the conflict as objects and properties —
what exactly collides — with a **Resolve…** button.

<p align="center"><img src=".github/media/conflicts-1.png" width="860" alt="A conflicted scene in the Changes tab, shown as objects and properties"></p>

The Conflicts window puts the hierarchy and the Inspector in three columns — mine, result, theirs.
Take a whole object or a single value from either side; what changed on one side only is taken
automatically. The result is validated before it is written, and a backup is kept in
`Library/LevGit/MergeBackups`. Text files are resolved in the same window, binary files by choosing a
whole version.

<p align="center"><img src=".github/media/conflicts-2.png" width="860" alt="The Conflicts window: mine, result and theirs in three columns, value by value"></p>

### Your own asset types

For your ScriptableObjects and file formats you can describe changes in your own words and draw a
preview with three small classes — no registration needed. See
[Custom asset previews](Packages/ru.lev.unity-git/README.md#custom-asset-previews) and the sample in
Package Manager.

## GitLab integration

Everything a team does around a merge request — review, discussion, merge, issues — happens in the
same window where the changes are made, and scenes stay objects there too. The integration works with
gitlab.com and self-managed GitLab.

### Connect in a minute

Turn it on in **Project Settings → Git → Integrations**; the page offers it by itself when the remote
looks like GitLab. The server and the project path come from the git remote and can be set manually,
for example when the remote uses SSH.

The API token is separate from the git credentials used for push, so revoking one does not break the
other. It is stored only in the credential store of this computer (Windows Credential Manager, Secret
Service on Linux) — never in the project or in logs. From the same page you can create a token in GitLab
with the name and scope filled in, check its user, scopes and expiry, rotate it through the API or revoke it.

The team workflow is a project setting, not a package decision: who can merge from the editor, what
happens after a merge, the branch name template for issues, target branch, draft, squash and the
description of new merge requests.

<p align="center"><img src=".github/media/gitlab-settings.png" width="860" alt="GitLab settings: server, API token and team workflow"></p>

### Issues become branches

Issues are listed by assigned to me, created by me, all open and closed. **Start Work** creates a
branch named by the project template — for example `42-fix-door-trigger` — or switches to the existing
one, and can assign the issue to you. On an issue branch, a merge request is created with `Closes #42`.

A new issue can be filed without leaving the editor, with a screenshot of the Game or Scene view attached.

<p align="center"><img src=".github/media/gitlab-new-issue.png" width="700" alt="A new issue with a screenshot of the Scene view"></p>

<p align="center"><img src=".github/media/gitlab-issues.png" width="860" alt="An issue in the GitLab tab: description with a screenshot and the Start Work button"></p>

<p align="center"><img src=".github/media/gitlab-issues-start-work.png" width="860" alt="Start Work: a branch for the issue, named by the project template"></p>

### Merge requests

The **GitLab** tab lists merge requests: mine, waiting for my review, all open, merged, closed. A card
shows whether it can be merged and why not, approvals and the pipeline. Check out the branch, approve,
merge or merge when the pipeline succeeds, rebase on the server, mark as draft, close. In the log,
branches with an open merge request get a `!12` badge that opens it.

A new merge request is created from the current branch with assignee, reviewers, labels, draft and
squash — the defaults come from the project settings.

<p align="center"><img src=".github/media/gitlab-create-mr.png" width="700" alt="Creating a merge request from the current branch"></p>

<p align="center"><img src=".github/media/gitlab-merge-requests.png" width="860" alt="The GitLab tab: a merge request with approvals, pipeline and merge status"></p>

### Code review on scenes, not on YAML

The changes of a merge request look exactly like local ones: a file tree, asset previews, scenes and
prefabs as objects. Right-click a diff line to comment on it — or a scene object or component to comment
on that object. In GitLab the comment sits on the object's line in the scene file and is visible in the
browser; in Unity it finds the object again by its `fileID`, even after the file has changed.

<p align="center"><img src=".github/media/gitlab-review.png" width="860" alt="Commenting on a component of a scene object in a merge request"></p>

<p align="center"><img src=".github/media/gitlab-review-1.png" width="860" alt="The comment marker on the component in the object tree"></p>

Discussions are in the same tab: reply, resolve and reopen threads, and jump from a thread to its line
or object in the changes.

<p align="center"><img src=".github/media/gitlab-threads.png" width="860" alt="Merge request discussions with replies and resolved threads"></p>

## Installation

Requires Unity 6000.0 or newer, `git` in PATH and, for locks, `git-lfs`.

In Unity: **Window → Package Manager → + → Add package from git URL…** and paste:

```text
https://github.com/LatyninEugene/git-for-unity.git?path=/Packages/ru.lev.unity-git#v1.0.0-alpha.1
```

Or add it to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "ru.lev.unity-git": "https://github.com/LatyninEugene/git-for-unity.git?path=/Packages/ru.lev.unity-git#v1.0.0-alpha.1"
  }
}
```

The part after `#` pins a release. Without it you get the latest commit of the default branch.
A `.unitypackage` is attached to every [release](https://github.com/LatyninEugene/git-for-unity/releases).

## Getting started

1. Open a project that lives in a git repository and choose **Window → Git**.
2. On **Changes**, include files, hunks or lines, write a message and commit. Pull, Push and Fetch are in the header.
3. Select a scene, a prefab or an asset to see what changed — as objects, in the Inspector or as a picture.
4. Right-click an object in the Hierarchy → **Git → Object History**; right-click an asset in the Project window for its history and locks.
5. If your project is on GitLab, enable the integration in **Project Settings → Git**.

The interface is in English and Russian and follows the system language; switch it in **Preferences → Git**.

## Documentation

- Full reference: [English](Packages/ru.lev.unity-git/README.md) · [Русский](Packages/ru.lev.unity-git/README.ru.md)
- [Changelog](Packages/ru.lev.unity-git/CHANGELOG.md)

This is an alpha: only the documented preview extension API and integration interfaces are meant for
your code, and they may still change before 1.0.0.

## Reporting problems

Use **Help → Git for Unity → Report a Problem…** in Unity. It saves a zip with versions, a repository
summary, recent errors and git commands — tokens and passwords are removed, and file paths and server
addresses can be hidden. You can study it yourself or attach it to a
[new issue](https://github.com/LatyninEugene/git-for-unity/issues/new/choose).

Security issues: see [SECURITY.md](SECURITY.md).

## Contributing

Contributions are welcome — bug fixes, translations, previews for more asset types.
See [CONTRIBUTING.md](CONTRIBUTING.md) for how the repository is organized and how to run the tests.

## License

[MIT](LICENSE)
