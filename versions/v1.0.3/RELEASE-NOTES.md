# v1.0.3 — Fix Update Crash

## Bugs fixed

- **Shell crashes on launch after updating to v1.0.2** — the v1.0.2 exe was published by CI as a single file **without** `IncludeNativeLibrariesForSelfExtract`, so WPF's native libraries were not bundled for self-extraction. After the updater replaced the installed exe and relaunched it from `C:\Program Files\OmenGamingShell`, the app died at startup with `System.DllNotFoundException` in WPF's `HwndSubclass` (`e0434352`), leaving the shell unable to open. Publishing now uses `-p:IncludeNativeLibrariesForSelfExtract=true` (matching `scripts/build-installer.ps1`), so native libraries are always bundled and extracted reliably from any install location.
- Installed `Version` bumped to `1.0.3` in both `.csproj` files.

## Commits since v1.0.2

4076baa Fix single-file publish to self-extract native libs (v1.0.2 crash), bump to 1.0.3
1274a68 Author release notes for v1.0.2
f0c8137 Add release notes for v1.0.2