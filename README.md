# Omen Gaming Shell

A fullscreen Windows shell for launching games without the Explorer desktop or taskbar.

## Current first milestone

- Borderless fullscreen library
- Keyboard and D-pad-style focus navigation
- Game and URI launching from `games.json`
- Automatic Steam and Epic Games discovery from local launcher manifests
- Cover artwork normalized to 600 × 800; hero artwork is restricted to 1920 × 1080 or 3840 × 2160
- Clock and launch status
- Restart and shutdown controls
- Escape and Alt+F4 suppression
- Per-user shell registration script
- No Explorer startup or Explorer fallback

## Build

Install the .NET 8 SDK, then run:

```powershell
dotnet publish .\OmenGamingShell\OmenGamingShell.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Copy `games.json` beside the published executable and edit it with real executable paths or launcher URIs.

## Try it before shell registration

Run the published executable normally. It deliberately prevents ordinary window closing; use its Restart or Shut Down control when testing the full shell behavior.

## Register as the Windows shell

Open PowerShell and run this only after testing the published executable:

```powershell
.\scripts\set-as-shell.ps1 -PublishedExecutable 'D:\Path\To\OmenGamingShell.exe'
```

The script changes the shell only for the current Windows account. Sign out and back in to activate it. It does not configure or launch Explorer as a fallback.

> Warning: this project intentionally has no recovery route to Explorer. A broken executable path or startup crash can leave the account without a usable desktop.
