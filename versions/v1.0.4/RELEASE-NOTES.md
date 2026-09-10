# v1.0.4

## Features added

- **Update rollback on crash** — `apply-update.ps1` now backs up the current `OmenGamingShell.exe` before applying an update. After install it relaunches the shell and watches it for 30 seconds: if the new build exits on its own, the previous working build is restored and relaunched automatically.
- **Blocked-version skip** — versions that crash and roll back are recorded in `%LOCALAPPDATA%\OmenGamingShell\blocked-versions.json`; the updater no longer offers notifications for those builds.
- **CI smoke test** — the `build` workflow now launches the published exe for 20 seconds and fails the build if the process dies or new `.NET Runtime` crash events appear (guards against install-path crashes like the v1.0.2 incident).
- **Per-item notification dismiss** — every notification-center entry now has a quiet ✕ dismiss button; update notifications remember the dismiss for the session.
- **Real notification-center events** — drive eject, new-game detection, Wi-Fi connect/disconnect, Bluetooth pair/remove, earphone connect/disconnect, and update failures now surface in the notification center with distinct icons, in addition to the toast.
- **Zero compiler warnings** — all previously reported warnings (CS4014, CS8602, CS8604, CS8620, CS0169, CS0162) fixed.

## Bugs fixed

- None.

## Commits since v1.0.3

945e31b Harden update apply with rollback and blocked-version skip, wire notification center events, clean all warnings (v1.0.4)
806f723 Author release notes for v1.0.3
1045254 Add release notes for v1.0.3