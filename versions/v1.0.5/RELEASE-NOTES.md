# v1.0.5

_Generated automatically by the release-notes workflow, enriched manually._

## Commits since v1.0.4

cb0cc00 v1.0.5 features: functional volume/brightness with OSD, app search in WinKey overlay, game store widget, media icon buttons, tighter sliders
ea9dc23 v1.0.5: battery warnings, update-banner rework, dead apps-code removal, floating WinKey overlay
d4622e0 Author release notes for v1.0.4
f14e111 Add release notes for v1.0.4

## Features added

- **Functional volume and brightness controls** (CoreAudio + WMI): draggable sliders in the game library, in-proc volume reading, Windows 11 brightness support via WMI, live OSD overlay on change.
- **Volume/brightness OSD**: red/black blended background matching the shell dialogs, fixed track filled with red as the percentage increases, faster keyboard-change detection (150 ms poll).
- **Sliders improved**: thumb no longer overflows the track at 100%, and the volume/brightness bars are vertically shorter.
- **App search in the WinKey overlay**: type to search installed apps (Start Menu shortcuts + UWP apps via StartApps) in addition to games, with high-quality icons and no 20-result cap; launching supports both classic links and UWP apps.
- **Game Store widget** replaces the old "Continue playing" container in the overlay, with a purple gradient design and featured-game handling.
- **Media buttons in the overlay now use icons** (Segoe Fluent Icons: prev/play/pause/next) instead of text, matching the game library style.
- **AltTab improvements**: source-client-area-only thumbnails for cleaner window previews.
- **Floating WinKey overlay** reworked (window repositioning/behavior), battery critical warnings, update-banner rework; dead apps-code removed.

## Bugs fixed

- Volume/brightness slider thumb overflowed past the track when at 100%.
- OSD popup had noticeable delay when changing volume/brightness via keyboard.
- Media overlay container used text buttons instead of aligned icon buttons.
- Mixed rendering/focus issues around the overlay and its content containers.