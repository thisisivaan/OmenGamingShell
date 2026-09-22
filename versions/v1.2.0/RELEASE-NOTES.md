# v1.2.0

_Generated automatically by the release-notes workflow, enriched manually._

## Commits since v1.1.0

228f671 v1.2.0: silent FitGirl installs with storage guard, detected-repack installs, uninstall option, installer media muting, FitGirl search/availability caching, store card progress, misc fixes
8ec2ceb Remove brand names from documentation
1d2ec7c Add comprehensive app documentation
e0979d4 Add release notes for v1.1.0

## Features added

- **Silent FitGirl installs**: finished repacks install with no interaction — setup.exe runs `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`, progress is watched by tracking the target folder as it grows, and the install finishes without the shell lifting a finger.
- **Detected-repack installs**: any finished FitGirl repack found on disk (never downloaded through the shell) surfaces in the Download Center as "Downloaded - not installed" with a one-click **Install** button, and is deleted from disk once its game is installed.
- **Storage guard**: the shell now refuses to start an install when the drive can't fit the real post-extraction size. When a repack's published size is unknown it falls back to the compressed archive size +25%, so installs no longer die mid-way on a full disk.
- **Install-size scraping**: detected-repack rows automatically look up their published install size on the FitGirl post, so the storage guard uses the real requirement (e.g. God of War Ragnarok = ~176 GB) instead of the compressible archive size.
- **Uninstall option**: right-click a game cover in the library → **UNINSTALL GAME** with a confirmation dialog. Local installs delete their game folder and purge all persisted state (play history, favorites/hidden/profile, metadata overrides, cached metadata + art); Steam/Epic games delegate to the store's own uninstaller.
- **Installer media muting**: while a repack installer runs, its BASS/ISDone audio is muted via Core Audio session enumeration, so installs stay silent without killing system audio.
- **FitGirl query search + availability caching**: keyword search with memoization and HEAD-request availability checks with a 30-minute TTL, so failed/moved posts don't hammer the site.
- **Store card progress**: game-store cards show live download/install progress state.
- **Game name resolution**: better display-name picking between the executable's product name and its folder name.

## Bugs fixed

- Download Center card no longer gets stuck after a successful install — the row is removed once install completes.
- Install could die on a near-full disk at ~104 GB because the storage guard gated on the compressed repack size instead of the real install requirement; the guard now uses the true published size (or archive +25% fallback).
- Repack folders were left behind after install in some paths; completed-repack folders are now reclaimed immediately when their game is installed.
- OSD popup for volume/brightness is positioned at the top-center of screen instead of center-screen.