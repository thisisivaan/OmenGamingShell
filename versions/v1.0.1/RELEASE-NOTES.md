# v1.0.1 — Self-Updating Shell

Released at commit `066e183`. First version with a built-in update feature.

## Features added

- **Background updater** (`cdd40c3`) — the shell now checks GitHub for new releases, downloads the update, applies it via an elevated PowerShell script, and relaunches itself. Updates no longer require manually reinstalling.
- **Installer build script** (`317a6ba`) — `scripts/build-installer.ps1` automates rebuilding the single-file payload and producing `OMEN Gaming Shell Setup.exe`.
- Installer refactor and shell UI cleanup (`cdd40c3`).

## Bugs fixed

- **Controller reconnect and guide bar** (`e7ae273`) — controller reconnect handling corrected and guide bar fixed.
- **Installer header** (`e7ae273`) — installer header display fixed.
- **CI artifact path** (`82b75f7`) — GitHub Actions publish path corrected for `net8.0-windows10.0.19041.0` so the release actually ships the exe.
- **Overlays trapped inside SettingsOverlay** (`066e183`) — `SettingsOverlay` was missing its closing `</Grid>`, so every later overlay (Notification Center, update dialog, Clipboard, Screenshots, Onboarding, Performance Details) was nested inside the collapsed settings panel and invisible until Settings was opened. Fixed so the update dialog, notifications, and all other overlays display on launch.

## Notes

- Repo hygiene: installer payload no longer tracked in git (`adc5c99`).
- Update flow: does not require Explorer; applies by replacing the running executable and issuing a fresh launch.