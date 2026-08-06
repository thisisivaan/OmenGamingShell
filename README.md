# Omen Gaming Shell

A fullscreen Windows shell for launching games without the Explorer desktop or taskbar.

![Platform](https://img.shields.io/badge/Platform-Windows%20win--x64-0078D6)
![.NET](https://img.shields.io/badge/.NET-8-512BD4)

Omen Gaming Shell turns your PC into a game console. It boots into a borderless, controller-first library where you can browse, launch, and play games, then drop back to a minimal home screen — no desktop, no taskbar, no Explorer.

> **Warning:** this project deliberately has no recovery route back to Explorer. A broken executable path or startup crash can leave the account without a usable desktop. Test thoroughly before registering it as your shell.

## Features

- **Fullscreen borderless library** with Home, Game Library, and Applications views
- **Controller, keyboard, and mouse input** — XInput and legacy joystick support with button remapping, dead zones, vibration, and long-press context menus
- **Automatic game discovery** from Steam (`appmanifest_*.acf`), Epic Games manifests, standalone `\Games` folders, Start-menu shortcuts, registry entries, and Windows Store apps
- **Applications rail** with Quick Launch pinning and smart categorization (Games, Communication, Streaming, Creative, and more)
- **Metadata enrichment** from IGDB and SteamGridDB — cover art, hero backgrounds, descriptions, genres, developers, and ratings
- **Artwork normalization** — covers resized to 600 × 800, backgrounds to 2560 × 1440, with automatic cover-based background fallbacks
- **Play-time tracking** with Continue Playing, Favorites, and Hide controls
- **Task switcher** that replaces Alt+Tab, with live DWM window thumbnails and a top-corner hot zone
- **Wi-Fi and Bluetooth management** — scan, connect, pair, and disconnect without leaving the shell
- **Power controls** — restart, shutdown, sleep, lock, desktop mode, and one-time boot into a Linux UEFI entry
- **Performance modes** that switch Windows power plans (Ultimate / Balanced / Eco)
- **In-game home overlay** opened with the controller Guide button
- **Shell keyboard guard** — suppresses the Windows key, Alt+Tab, Escape, and Alt+F4
- **Error reporting** — a built-in error log with crash logging to disk

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK (to build; the self-contained publish needs no runtime on target machines)
- A controller is optional but recommended

## Build

```powershell
dotnet publish .\OmenGamingShell\OmenGamingShell.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The published executable appears in `OmenGamingShell\bin\Release\net8.0-windows\win-x64\publish\`.

## Configuration

Copy `games.json` beside the published executable and edit it with real executable paths or launcher URIs.

Supported fields per entry:

| Field | Description |
| --- | --- |
| `Name` | Display name shown in the library |
| `Target` | Executable path, `.lnk` shortcut, or store URI (e.g. `steam://rungameid/440`) |
| `Arguments` | Optional command-line arguments |
| `WorkingDirectory` | Optional working directory for the launched process |
| `Cover` | Optional path to cover artwork |
| `Background` | Optional path to hero background artwork |

Games are also discovered automatically from installed launchers, so a manual entry is only needed for standalone titles.

## Running

Run the published executable normally. It deliberately prevents ordinary window closing; use its **Restart** or **Shut Down** controls when testing the full shell behavior.

## Register as the Windows shell

Open PowerShell and run this only after testing the published executable:

```powershell
.\scripts\set-as-shell.ps1 -PublishedExecutable 'D:\Path\To\OmenGamingShell.exe'
```

The script changes the shell only for the current Windows account. Sign out and back in to activate it. It does not configure or launch Explorer as a fallback.

To return to Explorer, the shell provides **Desktop Mode** in the power menu (or the `Ctrl+Alt+Shift+E` shortcut), which starts Explorer and exits the shell.

## Uninstall

Restore the standard Windows shell before uninstalling. A companion installer app is provided in `OmenGamingShell.Installer`; you can also reset the shell manually:

```powershell
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name Shell -Value 'explorer.exe'
```

## Project layout

```
OmenGamingShell/          Main WPF application
OmenGamingShell.Installer Installer / uninstaller app
scripts/                  Shell registration helpers
tools/                    Standalone helper tools
```

## Persisted data

Settings and state are stored under `%LOCALAPPDATA%\OmenGamingShell\`:

- `controller-settings.json` — input method, controller profiles, performance mode
- `metadata-sources.json` — metadata source configuration (API keys stay in Windows Credential Manager)
- `Metadata/` — cached artwork and game metadata
- `play-history.json` — play time and last-played dates
- `Diagnostics/` — logged and fixed error reports
- `Logs/` — crash logs
