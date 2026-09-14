# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [Semantic Versioning](https://semver.org/).

## [1.0.0-alpha.1] — 2026-09

First public release. There are no API stability guarantees in alpha: only the documented preview extension API (`Lev.Git.Preview`) and integration interfaces are intended for external code, and they may still change before 1.0.0.

### Added

- **Git window** (Window → Git): changes, history with graph, branches, LFS locks, command console.
- **Commits by hunks and lines**, amend, pre-commit checks: asset without `.meta`, duplicate GUIDs, references to files left out of the commit, large files outside LFS, LFS pointers instead of content.
- **Status badges** in the Project window; an asset and its `.meta` are one entry.
- **Semantic diff of scenes and prefabs** by `fileID`: added objects, changed components and properties; change marks in the Hierarchy and the Inspector.
- **Object, component, scene and asset history** with "last changed by" and reverting single values.
- **Conflict resolution** for scenes and prefabs by objects and properties, three-column Inspector, validation before writing, merge backups; text conflicts in the window; binaries by whole version.
- **Asset previews of every version**: textures, materials, meshes and models, audio, animation clips and controllers, Timeline, prefabs, scene settings; compare modes Side by Side, Swipe, Toggle, Overlay and Difference; thumbnails in file lists.
- **Syntax highlighting** in diffs for C#, JSON, YAML, shaders, XML, USS/CSS, Markdown and ignore files.
- **Preview extension API**: `ChangeDescriber<T>`, `AssetPresenter`, `AssetLoader`, with a sample.
- **LFS locks**: lock marks, lock and unlock from context menus, auto-lock, `lockable` setup, saving locked files, downloading missing LFS content.
- **Integrations**: GitLab — merge requests, review comments on lines and scene objects, issues with branches from templates, API token management.
- **English and Russian interface**, switchable on the fly (Preferences → Git or the Git window menu); follows the system language by default.
- **Problem report** (Help → Git for Unity → Report a Problem…): a zip with environment, repository summary, recent errors, git commands and logs; secrets are always removed, file paths and server addresses can be hidden.
- **Package log** in `Library/LevGit/Logs`, kept for 7 days.
