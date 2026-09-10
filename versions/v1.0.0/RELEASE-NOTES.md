# v1.0.0 — Initial Shell

Released with commit `e36f1c7`. The first published version. This version has **no self-update feature**; updates to a new release require manually downloading the latest installer or executable from GitHub.

## Features

- Fullscreen borderless game library (Home, Game Library, Applications)
- Controller, keyboard, and mouse input — XInput and legacy joystick support with remapping, dead zones, vibration, long-press context menus
- Automatic game discovery: Steam, Epic Games, standalone `\Games` folders, Start-menu shortcuts, registry entries, Windows Store apps
- Applications rail with Quick Launch pinning and smart categorization
- Metadata enrichment from IGDB and SteamGridDB (covers, backgrounds, descriptions, genres, developers, ratings)
- Artwork normalization (covers 600 × 800, backgrounds 2560 × 1440, cover-based fallbacks)
- Play-time tracking with Continue Playing, Favorites, and Hide
- Task switcher replacing Alt+Tab with live DWM thumbnails and a hot zone
- Wi-Fi and Bluetooth management (scan, connect, pair, disconnect)
- Power controls (restart, shutdown, sleep, lock, desktop mode, Linux UEFI boot)
- Performance modes switching Windows power plans (Ultimate / Balanced / Eco)
- In-game home overlay via controller Guide button
- Shell keyboard guard (suppresses Windows key, Alt+Tab, Escape, Alt+F4)
- Error reporting with crash logging to disk
- Installer (`OMEN Gaming Shell Setup.exe`) and shell registration helpers

## Known limitations at this version

- No self-update; releases must be applied manually.