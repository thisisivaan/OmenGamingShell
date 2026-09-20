# Omen Gaming Shell — Release Conventions

## Build conventions

**One app exe, always in the main dir.** `OmenGamingShell.exe` lives in the repo root and is the only distributable copy of the app — never publish it anywhere else.

- Rebuild it with `scripts/build-app.ps1` (self-contained, single file, written straight to the repo root).
- `scripts/build-installer.ps1` calls `build-app.ps1` first, then stages its payload from the repo-root exe and deletes the staged copy once the zip is built. Staging is never left behind, so the root exe stays the only copy.
- The installer project embeds `Payload/OmenGamingShell.zip` at build time, so that zip stays on disk (it is an archive, not a loose exe).
- Build intermediates never land in the tree. `Directory.Build.props` enables the .NET artifacts layout, so every project's `bin/` and `obj/` output goes to the sibling folder `..\OmenGamingShell-build` instead. Delete that folder to force a clean build.
- The installer's published output stays at `build/installer-release/` (git-ignored), which is a deliverable, not an intermediate.

## Version folders

Every published version must have a folder `versions/vX.Y.Z/RELEASE-NOTES.md` describing that version (features added + bugs fixed).

- **Automatic:** the `release-notes` GitHub Actions workflow creates the folder on every `v*` tag push and commits it back to `main`. It derives the changelog from `git log <prev-tag>..<new-tag>`.
- **Manual authoring:** for meaningful notes, enrich the `.md` with a Features-added / Bugs-fixed breakdown before or just after tagging. Keep the auto-generated commit list, then add/adjust sections.
- **Editing a released note:** edit `versions/<tag>/RELEASE-NOTES.md` and push to `main` — do NOT re-tag.

## When cutting a new release (vX.Y.Z)

1. Commit the feature/fix work to `main`.
2. Create annotated tag `vX.Y.Z` pointing at that commit and push it.
3. `build` workflow publishes the exe and creates the GitHub release with auto-generaged notes.
4. `release-notes` workflow writes `versions/vX.Y.Z/RELEASE-NOTES.md` back to `main`.
5. Immediately author/enrich that file with Features added / Bugs fixed, pushed as a normal commit.
6. Local rebaseline: `git fetch origin --prune`, then rebuild via `scripts/build-installer.ps1` — it refreshes the main-dir `OmenGamingShell.exe` first, then the installer.
7. Bump the `Version` in both `.csproj` files to the next version only when work on that version starts, not at tag time (CI derives the exe version from the tag).

## Version history reference

- **v1.0.0** (`e36f1c7`) — initial shell, NO self-update feature.
- **v1.0.1** (`066e183`) — background updater added, installer build script, controller reconnect + guide bar + installer header + CI path + overlay nesting fixes.