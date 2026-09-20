# Omen Gaming Shell

**A full-screen Windows gaming shell that replaces Explorer as the default desktop environment.**

Built in C# / WPF (.NET 8), Omen Gaming Shell gives you a controller-navigable game library, built-in torrent downloads, media controls, Wi-Fi/Bluetooth management, screenshots, clipboard history, and a custom Alt-Tab overlay -- all in one dark, minimal interface.

---

## Table of Contents

1. [App Overview](#1-app-overview)
2. [Design System](#2-design-system)
3. [Navigation and Input](#3-navigation-and-input)
4. [Dashboard / Home Page](#4-dashboard--home-page)
5. [Game Library](#5-game-library)
6. [Game Details and Metadata](#6-game-details-and-metadata)
7. [WinKey Overlay](#7-winkey-overlay)
8. [Media Controls](#8-media-controls)
9. [Alt-Tab Overlay](#9-alt-tab-overlay)
10. [Connections Overlay](#10-connections-overlay)
11. [Notifications](#11-notifications)
12. [Game Store (IGDB)](#12-game-store-igdb)
13. [Download Center](#13-download-center)
14. [FitGirl Repack System](#14-fitgirl-repack-system)
15. [Screenshots and Captures](#15-screenshots-and-captures)
16. [Clipboard History](#16-clipboard-history)
17. [Backup Settings](#17-backup-settings)
18. [Volume and Brightness OSD](#18-volume-and-brightness-osd)
19. [Battery Warning](#19-battery-warning)
20. [Setup Wizard](#20-setup-wizard)
21. [Onboarding](#21-onboarding)
22. [Update System](#22-update-system)
23. [Error Log](#23-error-log)
24. [Metadata Correction](#24-metadata-correction)
25. [Controller Support](#25-controller-support)
26. [Performance Modes](#26-performance-modes)
27. [Crash Handling and Firewall](#27-crash-handling-and-firewall)
28. [Data Stores Reference](#28-data-stores-reference)
29. [Native Interop Reference](#29-native-interop-reference)
30. [File Inventory](#30-file-inventory)

---

## 1. App Overview

| Property | Value |
|---|---|
| Framework | .NET 8, WPF, `net8.0-windows10.0.19041.0` |
| Self-contained | Yes -- ships as a single `.exe` |
| Shell mode | Replaces Explorer as default Windows shell |
| Primary input | Xbox controller + Mouse and Keyboard |
| Window | `WindowStyle=None`, `WindowState=Maximized` |
| Build | `scripts/build-app.ps1` produces `OmenGamingShell.exe` at repo root |

### Startup Flow

1. `App.OnStartup` registers crash handlers (`DispatcherUnhandledException` + `AppDomain.UnhandledException`) and adds firewall rules via `netsh`.
2. If setup is incomplete (`SetupStateStore.IsSetupComplete()` returns false), `SetupWindow` launches.
3. Otherwise, `MainWindow` opens:
   - Loads game library from disk + runs background scan.
   - Starts background slideshow from game cover art.
   - Binds Xbox controller via XInput.
   - Shows boot animation (brand wordmark scales, slides to corner, library fades in).
   - Checks for updates in background.

---

## 2. Design System

### Color Palette

| Name | Hex | Usage |
|---|---|---|
| **Background** | `#050505` | App background, overlay scrims |
| **Panel** | `#0D0D0D` | Card backgrounds, overlay containers |
| **Card** | `#121212` | Download items, list rows |
| **Card Hover** | `#1AFFFFFF` | Hover states on filter buttons |
| **Accent** | `#FF003C` | Progress bars, selection highlights, primary action |
| **Accent Purple** | `#9B6BFF` | Game store, download center, FitGirl actions |
| **Success** | `#24C486` | Installed/completed states |
| **Warning** | `#E8B83C` | Downloading state indicator |
| **Error** | `#FF4444` | Failed states |
| **Scrim** | `#C8000000` | Overlay background dimming (78% opacity black) |
| **Text Primary** | `#FFFFFF` | Headings, titles |
| **Text Secondary** | `#909090` | Descriptions, status text |
| **Text Muted** | `#505050` | Empty states, hints |
| **Text Dim** | `#606060` | Size text, metadata |
| **Text Detail** | `#707070` | Progress percentages, sub-labels |
| **Text Info** | `#808080` | Section headers |
| **Text Breadcrumb** | `#C0C0C0` | Download path display |

### Typography

| Usage | Font | Size | Weight |
|---|---|---|---|
| App brand wordmark | Segoe UI Variable Display | 22-28pt | Bold |
| Section titles | Segoe UI Variable Display | 18-24pt | SemiBold |
| Overlay titles | Segoe UI Variable Display | 16-22pt | SemiBold |
| Card titles | Segoe UI Variable Display | 13-15pt | SemiBold |
| Status text | Segoe UI Variable | 10-11pt | Normal |
| Hints / empty states | Segoe UI Variable | 11-13pt | Normal |
| Icons (all) | Segoe Fluent Icons | 11-32pt | Normal |

### Corner Radii

| Element | Radius |
|---|---|
| Dashboard cards | 13-14px |
| Overlay panels | 12-14px |
| Overlay cards / rows | 8-10px |
| Notification toast | 18px |
| Filter buttons | 16-22px |
| Download progress bar | 2px |
| OSD window | 16px |
| Search bar | 22px |

### Animation Patterns

All overlays use the same `TranslateTransform` + `Opacity` pattern:

- **Open:** FadeIn (0 to 1 opacity, 200ms) + SlideInFromBottom (Y: +70 to 0, 250ms, EaseOut)
- **Close:** AnimateOut (1 to 0 opacity, 200ms) + SlideOutToBottom (Y: 0 to +70, 220ms)
- **Boot animation:** Brand wordmark scales down, slides to top-left, library fades in
- **Slideshow cross-fade:** 800ms opacity transition between background images
- **Game card hover:** ScaleTransform to 1.02 over 200ms
- **OSD:** Auto-hide after 1400ms

---

## 3. Navigation and Input

### Keyboard Shortcuts

| Key | Action |
|---|---|
| `Escape` | Close topmost overlay (cascading close order) |
| `Windows` | Open WinKey overlay (when shell is active) |
| `Ctrl+Alt+Shift+E` | Desktop Mode (launch Explorer, close shell) |
| `Alt+F4` | Blocked (shell cannot be closed normally) |

### Controller (Xbox / XInput)

| Input | Action |
|---|---|
| Guide button (short press) | Open WinKey overlay (desktop) / game library (in-game) |
| Guide button (long hold) | Alt-Tab overlay |
| D-Pad / Left Stick | Navigate UI |
| A button | Select / Activate |
| B button | Back / Close overlay |
| X button | Context action (varies by view) |
| Y button | Context action (varies by view) |
| Left/Right Bumper | Switch tabs / panels |
| Triggers | Scroll lists |
| Start | Open settings / context menu |

### Mouse

- Auto-hidden after 650ms in controller mode.
- Hot corner: move to top-right 10px corner triggers WinKey overlay after 250ms dwell.
- All overlays: click scrim (outside panel) to close.
- All cards: hover scale effect (1.0x to 1.02x).
- Double-click game card to launch.

---

## 4. Dashboard / Home Page

The first screen after boot animation. Full-screen background slideshow with overlaid UI elements.

### Layout

```
+---------------------------------------------------+
|  Background slideshow (cross-fade, 12s timer)     |
|                                                   |
|  Omen Brand wordmark (top-left)                   |
|  Clock (top-right, HH:mm, updates every 1s)      |
|  Network status icon (top-right)                  |
|                                                   |
|  +----------------+  +-------------------+        |
|  | Continue        |  | Performance Mode  |        |
|  | (last played)   |  | Ultimate/Bal/Eco  |        |
|  +----------------+  +-------------------+        |
|                                                   |
|  [Media controls bar, bottom-center]              |
|  [Notifications area, bottom-right]               |
|  [Download badge, top-right of nav]               |
+---------------------------------------------------+
```

### Background Slideshow

- Rotates through game cover art every 12 seconds.
- Cross-fades between images (800ms transition).
- Pauses when a game cover is hovered.
- Can be replaced with a custom image via Background Settings.

### Top Navigation Bar

Horizontal row of icon buttons (48x48 each):

| Button | Icon | Action |
|---|---|---|
| Download Center | `E895` | Opens download overlay |
| Connections | `E774` | Opens Wi-Fi/Bluetooth overlay |
| Notifications | `EA8F` | Opens notification center |
| Settings | `E713` | Opens settings page |
| Shutdown | `E7E8` | Power menu (Shutdown, Restart, Sleep) |

---

## 5. Game Library

### Scanning

Games are detected automatically from multiple sources:

| Source | Method |
|---|---|
| Steam | Reads `libraryfolders.vdf` then `appmanifest_*.acf` files |
| Epic Games | Scans `ProgramData/Epic/UnrealEngineLauncher/Data/Manifests/*.item` |
| GOG | Registry keys under `HKLM\GOG.com\Games` |
| Manual folders | User-configurable scan paths |
| Windows shortcuts | `.lnk` files in Start Menu and Desktop |
| UWP / Microsoft Store | `Get-AppxPackage` via PowerShell |

### Display Modes

| Mode | Layout |
|---|---|
| Grid view | Cards with cover art, title, metadata (default) |
| List view | Compact rows with title, last played, playtime |

### Filtering and Sorting

- **Search bar** -- real-time fuzzy filtering on game title.
- **Filter buttons** -- Installed, Favorites, Recently Played, All.
- **Sort** -- by name, last played, playtime, install status.

### Game Card Design

Each card shows:

- Cover art image (from IGDB or local executable icon)
- Game title (13pt SemiBold)
- Developer and genres (10pt, `#909090`)
- Star rating and playtime
- Action buttons on hover: Play, Details, Context menu

Hover behavior: scale to 1.02x, reveal action buttons.
Click: launches game or opens details overlay.
Right-click / Y button: context menu (Edit Metadata, Add to Favorites, Hide, Open Folder).

### Favorites and Hidden Games

Managed via `LibraryPreferencesStore`. Users can:
- Toggle favorites (star icon on card).
- Hide games (removed from visible list, shown in Hidden section).

---

## 6. Game Details and Metadata

Full-screen overlay when a game card is selected.

### Layout

- **Background:** game artwork (blurred, darkened)
- **Cover:** large thumbnail, left side
- **Info panel:** title, genres, developer, publisher, release date, platforms, rating
- **Description:** game summary from IGDB
- **Action buttons:** PLAY, EDIT METADATA, OPEN FOLDER

### Metadata Pipeline

1. `MetadataSettingsStore` stores IGDB API credentials (Client ID, Client Secret).
2. `MetadataEnrichmentService` fetches data on library load from IGDB.
3. `GameMetadataCache` caches responses to reduce API calls.
4. `MetadataOverrideStore` allows per-game manual edits that override IGDB data.

### Edit Metadata Overlay

Editable fields: Name, Genres, Developer, Publisher, Release Date, Platforms, Description, Cover Image, Background Image.

Cover/background can be changed via file picker (supports png, jpg, jpeg, webp, bmp).

Saves to `metadata-overrides.json`.

---

## 7. WinKey Overlay

Triggered by: Windows key press, guide button (short press), or top-right hot corner (250ms dwell).

Full-screen overlay with app shortcuts and system controls.

### Layout

```
+------------------------------------------------+
|                                                |
|  [Search Bar]  (type to filter apps)           |
|                                                |
|  +------------------+  +------------------+    |
|  | App Grid          |  | Recent Games     |    |
|  | (detected apps)   |  | (from library)   |    |
|  +------------------+  +------------------+    |
|                                                |
|  +------------------------------------------+  |
|  | System Tray Row                           |  |
|  | Wi-Fi | Bluetooth | Volume | Battery %   |  |
|  | Date  | Time      | Power  | Updates     |  |
|  +------------------------------------------+  |
|                                                |
|  +------------------------------------------+  |
|  | Quick Actions                             |  |
|  | Screenshot | Clipboard | Settings        |  |
|  | Task View  | Desktop   | Shutdown        |  |
|  +------------------------------------------+  |
+------------------------------------------------+
```

### Features

- **App search:** type to search installed apps (UWP + desktop). Launch on Enter/A button.
- **App grid:** scrollable grid of detected apps with icons and names.
- **System tray:** live Wi-Fi name, Bluetooth status, volume slider, battery %, date, time.
- **Quick actions:** Screenshot, Clipboard History, Settings, Task View (DWM Alt-Tab), Desktop (minimize all), Shutdown/Restart/Sleep.
- **DWM thumbnails:** live previews of open windows on task view cards.

---

## 8. Media Controls

A bar at the bottom of the home page showing currently playing media.

### Layout

```
+--------------------------------------------------+
|  [Album Art]  Song Title        Artist - Album   |
|  Previous  Play/Pause  Next  Shuffle  Repeat     |
|  Volume: [====slider====]  Progress: 2:34/4:12   |
+--------------------------------------------------+
```

### Features

- Reads media metadata via Windows SMTC (System Media Transport Controls).
- Album art thumbnail when available.
- Controls: Play/Pause, Previous, Next, Shuffle, Repeat, Volume.
- Progress bar with elapsed time and duration.
- Clicking album art opens full media overlay.

---

## 9. Alt-Tab Overlay

Custom Alt-Tab replacement triggered by long-pressing the guide button on controller.

### Features

- Shows all open windows as DWM thumbnail previews.
- Horizontal strip of window cards (150x100px).
- Selected window highlighted with `#FF003C` border and `#26FF003C` background.
- Live DWM thumbnails via `DwmRegisterThumbnail` API.
- D-Pad Left/Right to cycle, A to activate, B to cancel.
- Auto-activates on guide button release.

---

## 10. Connections Overlay

Side-by-side Wi-Fi and Bluetooth panels in a single overlay.

### Wi-Fi Panel

| Element | Detail |
|---|---|
| Title | "WI-FI NETWORKS" (24pt) |
| Scan trigger | `netsh wlan show networks` + native `WlanScan` API |
| Auto-refresh | Every 8 seconds when visible |
| Connect | `netsh wlan connect name=X` |
| Disconnect | `netsh wlan disconnect` |
| Secured networks | Password input dialog, profile creation via `netsh` |
| Network list | ListBox with name, signal strength, secured status |

### Bluetooth Panel

| Element | Detail |
|---|---|
| Title | "BLUETOOTH DEVICES" (24pt) |
| Scan | `BluetoothApis.dll` radio/device enumeration |
| Connect | Native `BluetoothConnect` |
| Disconnect | `BluetoothDisconnect` + PowerShell PnP disable |
| Status | Shows CONNECTED / PAIRED / AVAILABLE per device |
| Refresh button | Triggers re-scan |

---

## 11. Notifications

### Toast System

- Centered at top of screen.
- Dark gradient background (`#F2420C0C` to `#F20B0B0B`), CornerRadius 18.
- Auto-hide after 4 seconds.
- Click invokes optional action callback (e.g., open download center).

### Notification Center

- Accessible from WinKey overlay or notification area button.
- `ObservableCollection` with max 50 entries.
- Each entry: Time, Title, Message, Icon, Tag.
- Tags: `update`, `audio`, `battery`, `controller`.
- Persists to `notifications.json` in app data folder.

---

## 12. Game Store (IGDB)

Full-screen overlay for searching and downloading games via the IGDB API.

### Search Flow

1. User types in search bar (200ms debounce before firing query).
2. Query sent to IGDB API via configured metadata source credentials.
3. Results displayed as row items with cover art, name, developer, genres, rating.
4. Click to view details or initiate download.

### IGDB Integration

| Setting | Value |
|---|---|
| API | `api.igdb.com/v4` |
| Auth | Twitch OAuth (client_credentials grant) |
| Token cache | `ConcurrentDictionary` with TTL expiry |
| Rate limit | 260ms between requests |
| Query | `search "query"; fields name,summary,cover.image_id,...; limit 20` |

### Search Result Row Design

```
+-----------------------------------------------+
| [Cover 50x68]  Game Name              [Action] |
|               Developer / Publisher            |
|               Genres  |  Rating  |  Platforms  |
+-----------------------------------------------+
```

Action circle states:
- Download: purple `#9B6BFF` circle with download icon
- Downloading: yellow spinner animation
- Installed: green `#24C486` circle with checkmark

---

## 13. Download Center

Overlay panel managing all active and completed downloads.

### UI

```
+--------------------------------------------+
| [B]  DOWNLOADS                   [Gear]    |
|                                            |
| +----------------------------------------+ |
| | [Cover] | Game Name          [Status]   | |
| |         | Searching...                  | |
| |         | 2.1 GB                        | |
| |         | [===========------]  67%     | |
| |         | 1.2 MB/s - ETA 14m - 3 peers | |
| |         |  [Pause] [Resume] [Cancel]    | |
| +----------------------------------------+ |
|                                            |
| +----------------------------------------+ |
| | [Cover] | Another Game       [Done]     | |
| |         | Installed                     | |
| |         | [====================] 100%   | |
| |         |  [Open Folder]                | |
| +----------------------------------------+ |
|                                            |
|  [1] active download badge (top-right nav) |
+--------------------------------------------+
```

### Features

- **Progress bar:** `#FF003C` fill, 4px height, 420px max width, animated via binding.
- **Speed/ETA/Peers:** live updates every 500ms from libtorrent status.
- **Pause/Resume/Cancel:** per-download action buttons.
- **Open Folder:** opens download directory in Explorer.
- **Auto-retry:** failed downloads retry automatically (up to 5 attempts, 30s watchdog interval).
- **State persistence:** `active_downloads.json` saves magnet URIs, progress, retry counts.
- **Auto-resume:** on app restart, saved downloads resume from where they left off.
- **Duplicate prevention:** detects same info-hash, releases old manager to prevent bandwidth splitting.
- **Stop seeding:** automatically stops seeding when download completes to free upload bandwidth.

### Status Icons

| Status | Icon Code | Color |
|---|---|---|
| Searching | `E721` (Search) | `#9B6BFF` |
| Matching | `E73E` (Checkmark) | `#9B6BFF` |
| Queued | `E945` (Clock) | `#909090` |
| Downloading | `E895` (Download) | `#E8B83C` |
| Paused | `E769` (Pause) | `#909090` |
| Completed | `E73E` (Checkmark) | `#24C486` |
| Failed | `EA39` (Error) | `#FF4444` |

### Download Settings Overlay

Accessed via gear icon (top-right of download center header).

| Setting | Description |
|---|---|
| Download Location | Shows current path with browse button. Opens `OpenFolderDialog`. Path persisted to `download_settings.json`. |

---

## 14. FitGirl Repack System

Integrated search and download of FitGirl repacks.

### Search Flow

1. User types game name in the store search bar.
2. `FitGirlScrapingService.SearchAsync` queries `fitgirl-repacks.site/?s={query}`.
3. HTML parsed with `AngleSharp` library, extracts article titles and URLs.
4. `FuzzySharp.WeightedRatio` scores each result against the query (70%+ threshold).
5. Best match returned. Up to 3 retry attempts with Cloudflare detection.

### Download Flow

1. `FitGirlScrapingService.ExtractMagnetLinkAsync` scrapes the post page for magnet links.
2. Extracts additional tracker URLs from page HTML and appends them to the magnet URI.
3. `TrackerList.AppendTo` adds well-known public trackers as fallback.
4. `DownloadCenterService.StartDownloadAsync` initiates the torrent.
5. Client tries in order: .torrent file download -> torrent cache services -> magnet link fallback.
6. libtorrent fetches metadata (up to 90s timeout), then begins download.

### Torrent Speed Optimization

| Setting | Value | Purpose |
|---|---|---|
| User Agent | `OmenGamingShell/1.0 libtorrent/2.0` | Avoid tracker throttling |
| DHT | Enabled | Decentralized peer discovery |
| LSD | Enabled | Local network peer discovery |
| NAT-PMP + UPnP | Enabled | Automatic port forwarding |
| uTP | Enabled | UDP-based transport, less blocking |
| `announce_to_all_trackers` | true | Query every tracker |
| `announce_to_all_tiers` | true | Query every tracker tier |
| `connections_limit` | 500 | Max simultaneous peer connections |
| `max_peerlist_size` | 5000 | Max known peers in pool |
| `torrent_connect_boost` | 100 | Burst connections on torrent start |
| `aio_threads` | 8 | Disk I/O parallelism |
| `send_buffer_watermark` | 512 KB | Network send buffer size |
| `send_buffer_low_watermark` | 32 KB | Send buffer drain threshold |
| `max_queued_disk_bytes` | 32 MB | Write coalescing buffer |
| `allow_multiple_connections_per_ip` | true | More peers from seedboxes |
| `handshake_timeout` | 5s | Fast peer culling |
| `inactivity_timeout` | 20s | Drop dead peers quickly |
| `tracker_receive_timeout` | 5s | Fast tracker failover |
| `peer_timeout` | 20s | Peer connection timeout |
| `max_out_request_queue` | 500 | Request pipelining depth |
| `whole_pieces_threshold` | 4 | Request whole pieces for large files |
| `seed_choking_algorithm` | 1 (round-robin) | Fair upload distribution |

### Torrent Cache Services

Before falling back to magnet, the system races `.torrent` file downloads from:

- `itorrents.org`
- `torrage.info`
- `torcache.net`

All three are fetched concurrently with a 5-second deadline. First valid response wins.

### Post-Download Behavior

- Seeding stops automatically when download completes (`StopSeeding`), releasing upload bandwidth.
- Files remain on disk for installation.

---

## 15. Screenshots and Captures

Overlay showing recent screenshots and game captures.

### Scan Paths

- `Pictures\Screenshots`
- `Videos\Captures`
- `Camera Roll`
- `Public\Pictures\Screenshots`
- `Public\Videos\Captures`

### Features

- Displays up to 20 recent captures.
- Card grid layout with file icon (Segoe Fluent Icons), filename, date.
- Click any card to open in default viewer.
- "OPEN FOLDER" link to navigate to capture directory.

---

## 16. Clipboard History

Overlay showing recently copied text items.

### Features

- Shows text snippets (truncated to 120 chars) with timestamps.
- Click any item to copy it to clipboard.
- Persists to clipboard manager store.
- Empty state: "Nothing copied yet".

---

## 17. Backup Settings

Overlay for configuring automatic backup of game saves.

### Settings

| Setting | Type |
|---|---|
| Enable auto-backup | Toggle |
| Backup folders | Configurable folder paths |
| Backup drive | Drive picker |
| Backup status | Shows last backup time and size |

Automatic backup runs in background every 2 minutes. Detects new/changed files and copies to the configured backup drive.

---

## 18. Volume and Brightness OSD

Floating on-screen display when volume or brightness changes.

### Design

```
+----------------------------+
|  [Icon]  Volume      72%  |
|  [==============------]   |
+----------------------------+

+----------------------------+
|  [Icon]  Brightness   45% |
|  [=========----------]    |
+----------------------------+
```

| Property | Value |
|---|---|
| Size | 320x104px transparent window |
| CornerRadius | 16px |
| Progress bar height | 7px, CornerRadius 3.5px |
| Volume color | `#FF003C` |
| Brightness color | `#FFC928` |
| Auto-hide delay | 1400ms |
| Position | Centered on the active monitor |

### Implementation

- Volume: reads/writes via `SystemAudio` (Core Audio API).
- Brightness: reads/writes via `SetMonitorBrightness` / `GetMonitorBrightness`.

---

## 19. Battery Warning

Full-screen overlay when battery drops below critical threshold.

### Design

- Dark background, large battery icon.
- "LOW BATTERY" title (26pt).
- Message: "Connect your charger or the shell will shut down in X seconds."
- Countdown timer with auto-shutdown at 0.
- Triggered at 5% battery (configurable).

---

## 20. Setup Wizard

7-step first-run wizard with dot progress indicators.

### Steps

| Step | Title | Controls |
|---|---|---|
| 0 | Welcome | Brand wordmark, "GET STARTED" button |
| 1 | Input Method | Toggle: Controller / Mouse and Keyboard |
| 2 | Performance Mode | Three mode buttons: Ultimate / Balanced / Eco |
| 3 | Desktop Selection | ComboBox of detected desktops + "SCAN OS" button |
| 4 | Backup | Automatic backup toggle, drive picker |
| 5 | Metadata | API source name, URL, Client ID, API Key fields. SKIP option |
| 6 | Finish | Summary of all choices + "LAUNCH SHELL" button |

Applies selected performance mode (switches Windows power plan via `powercfg.exe`), saves all settings, calls `SetupStateStore.MarkComplete()`.

---

## 21. Onboarding

9-step guided walkthrough shown on first launch (after setup).

### Steps

1. Welcome
2. Game Library
3. Applications
4. Media Controls
5. Volume and Brightness
6. Controller and Audio
7. Win Key Overlay
8. Notifications
9. All Set

Each step has an icon, title, and description. Progress bar (`#FF003C` fill) and dot indicators at the bottom. SKIP / CONTINUE / FINISH buttons.

First-run detection via `.onboarded` file in `%LocalAppData%\OmenGamingShell\`.

---

## 22. Update System

### Service: `UpdateChecker`

| Property | Value |
|---|---|
| API | `api.github.com/repos/thisisivaan/OmenGamingShell/releases/latest` |
| Comparison | `Version.TryParse()` semantic version comparison |
| Download | Downloads new `.exe` to temp directory |
| Apply script | PowerShell script that: waits for shell exit, backs up current, copies new, launches, monitors 30s, rolls back on premature exit |

### UI

- **Update Banner:** macOS-style top-right banner. `#F2161616` background, CornerRadius 16, DropShadow. Shows version text, "UPDATE NOW" button, dismiss (x) button.
- **Update Progress Overlay:** "DOWNLOADING UPDATE" title, indeterminate `ProgressBar` (`#4C9AFF`), auto-restart message.

### Blocking

Failed versions stored in `blocked-versions.json` to prevent repeated failed updates.

---

## 23. Error Log

Overlay displaying logged errors.

### Features

- Title "ERROR LOG" (26pt).
- Back button.
- ListBox of `ShellErrorEntry` items: message, source, details, timestamp.
- Each item has a "MARK FIXED" button.
- Empty state: "No errors logged" (13pt, `#505050`).
- Errors logged from `DispatcherUnhandledException` and `AppDomain.UnhandledException`.

---

## 24. Metadata Correction

Overlay for manually editing game metadata.

### Editable Fields

- Name
- Genres
- Developer
- Publisher
- Release Date
- Platforms
- Description
- Cover Image (file picker: png/jpg/jpeg/webp/bmp)
- Background Image (file picker)

### Persistence

Saves to `MetadataOverrideStore` (`metadata-overrides.json`). Overrides take priority over IGDB data.

---

## 25. Controller Support

### Xbox (XInput)

- Uses `xinput1_4.dll` (ordinal #100) for modern Xbox controllers.
- Falls back to `winmm.dll` (DirectInput: `joyGetPosEx`, `joyGetDevCaps`) for older/gamepad controllers.
- Polling rate: 60Hz (`DispatcherTimer` at 16ms interval).
- Per-controller profiles saved in `ControllerSettingsStore`.
- Guide button detection via `Gamepad.Buttons.HasFlag(Gamepad.ButtonFlags.Home)`.

### Controller Calibration

- Auto-detected on connection.
- Dead zone adjustment.
- Vibration feedback on navigation.
- Navigation sounds via `NavigationSound` service (subtle click on D-Pad navigation).

---

## 26. Performance Modes

Three modes that map to Windows power plans:

| Mode | Power Plan GUID | Process Priority |
|---|---|---|
| Ultimate | `e9a42b02-d5df-448d-aa00-03f14749eb61` | High |
| Balanced | `381b4222-f694-41f0-9685-ff5bb260df2e` | Normal |
| Eco | `a1841308-3541-4fab-bc81-f71556f20b4a` | BelowNormal |

Switched via `powercfg.exe /setactive {GUID}`. Per-game profiles set process priority when launching.

---

## 27. Crash Handling and Firewall

### Crash Handling

- `DispatcherUnhandledException` handler on the UI thread logs to `ErrorLogStore` and `crashes.log`.
- `AppDomain.UnhandledException` handler catches non-UI crashes.
- Crash log path: `%LocalAppData%\OmenGamingShell\Logs\crashes.log`.

### Firewall Rule

`EnsureFirewallRule()` runs on startup:
- Adds inbound + outbound allow rules for the shell exe via `netsh advfirewall firewall add rule`.
- Rule name: "OmenGamingShell" (inbound) and "OmenGamingShell Out" (outbound).

---

## 28. Data Stores Reference

All stores persist JSON to `%LocalAppData%\OmenGamingShell\`.

| Store | File | Purpose |
|---|---|---|
| `ControllerSettingsStore` | `controller-settings.json` | Input method, guide home, audio, performance mode, per-controller profiles |
| `MetadataSettingsStore` | `metadata-settings.json` | Metadata sources, primary source ID |
| `MetadataOverrideStore` | `metadata-overrides.json` | Per-game manual metadata overrides |
| `LibraryPreferencesStore` | `library-preferences.json` | Favorites, hidden games |
| `PlayHistoryStore` | `play-history.json` | Per-game play time and last played |
| `GameMetadataCache` | `game-metadata-cache.json` | Cached IGDB metadata |
| `BackgroundSettingsStore` | `background-settings.json` | Custom background path, use custom flag |
| `SetupStateStore` | `setup-complete.json` | Setup wizard completion flag |
| `ErrorLogStore` | `errors-logged.json` / `fixed-errors.json` | Error logging and fixed tracking |
| `NotificationCenter` | `notifications.json` | Notification history (max 50) |
| `DownloadCenterService` | `active_downloads.json` | Active download state (magnet URIs, retry counts) |
| `TorrentDownloadService` | `download_settings.json` | Download path setting |
| `InstalledAppsCatalog` | `installed-apps-cache.json` | Cached app list (30min TTL) |
| Onboarding | `.onboarded` | First-run onboarding completion flag |

---

## 29. Native Interop Reference

| API | Usage |
|---|---|
| `xinput1_4.dll` (ordinal #100) | XInput controller polling and guide button |
| `winmm.dll` | DirectInput fallback (joyGetPosEx, joyGetDevCaps) |
| `BluetoothApis.dll` | Bluetooth radio/device enumeration, connect/disconnect |
| `wlanapi.dll` | Wi-Fi scan trigger |
| `kernel32.dll` | Power status, process management, thread attach |
| `user32.dll` | Window enumeration, foreground control, DWM thumbnails |
| `dwmapi.dll` | Window cloaking detection, thumbnail registration |
| `shell32.dll` | Executable icon extraction (SHGetFileInfo) |
| `netsh.exe` | Wi-Fi profile management, network scanning |
| `powercfg.exe` | Power plan switching |
| `bcdedit.exe` | Boot entry detection for multi-OS |
| `csdl` (csdl.dll) | libtorrent wrapper for BitTorrent downloads |

---

## 30. File Inventory

### Core Files

| File | Role |
|---|---|
| `MainWindow.xaml` | Main UI layout (all overlays, panels, cards) |
| `MainWindow.xaml.cs` | Main code-behind (navigation, overlays, game launch) |
| `MainWindow.Features.cs` | Partial class: media, notifications, clipboard, screenshots, onboarding |
| `App.xaml` | Resource dictionaries, styles, brushes, converters |
| `App.xaml.cs` | Startup, crash handling, firewall rules |

### Overlay Windows

| File | Role |
|---|---|
| `WinKeyOverlayWindow.xaml` / `.cs` | Full WinKey overlay with app grid, system tray, quick actions |
| `VolumeBrightnessOsd.xaml` / `.cs` | Floating volume/brightness display |
| `BatteryCriticalOverlay.xaml` / `.cs` | Low battery warning with countdown |
| `SetupWindow.xaml` / `.cs` | First-run setup wizard (7 steps) |

### Services

| File | Role |
|---|---|
| `GameScanner.cs` | Game detection from Steam, Epic, GOG, shortcuts, UWP |
| `GameLibrary.cs` | Game library loader |
| `GameSessionManager.cs` | Game launch, process monitoring, play time tracking |
| `MediaService.cs` | SMTC media control (play/pause, next/prev, metadata) |
| `ConnectionService.cs` | Wi-Fi/Bluetooth native interop |
| `SystemAudio.cs` | Volume control via Core Audio API |
| `ScreenshotService.cs` | Screenshot scanning from standard paths |
| `GameStoreSearchService.cs` | IGDB API search with OAuth |
| `FitGirlScrapingService.cs` | FitGirl repack scraping with fuzzy matching |
| `TorrentDownloadService.cs` | csdl/libtorrent wrapper with speed tuning |
| `DownloadCenterService.cs` | Download state management, progress, auto-resume, watchdog |
| `TrackerList.cs` | Fallback tracker list for torrents |
| `UpdateChecker.cs` | GitHub release version check and auto-update |
| `MetadataEnrichmentService.cs` | IGDB metadata download and caching |
| `InstalledAppsCatalog.cs` | App scanning (Start Menu, UWP) |
| `ArtworkCache.cs` | Icon extraction, cover/background normalization |
| `StoreIcon.cs` | Store SVG icon renderer |
| `NavigationSound.cs` | Controller navigation audio feedback |
| `ClipboardManager.cs` | Clipboard history |
| `ShellKeyboardGuard.cs` | Alt+Tab interception, Windows key guard |
| `PowerStatus.cs` | Battery status via kernel32 |
| `ControllerInput.cs` | XInput/DirectInput controller polling |
| `ConnectedDeviceScanner.cs` | Bluetooth device scanning |
| `ApplicationIconConverter.cs` | Icon path to ImageSource converter |
| `BoolToVisibilityConverter.cs` | Bool to Visibility converter |
| `TaskWindowEntry.cs` | DWM window capture for Alt-Tab |

### Data Stores

| File | Role |
|---|---|
| `ControllerSettingsStore.cs` | Controller configuration persistence |
| `MetadataSettingsStore.cs` | IGDB API settings |
| `MetadataOverrideStore.cs` | Per-game metadata overrides |
| `MetadataSourceConfig.cs` | Metadata source config models |
| `LibraryPreferencesStore.cs` | Favorites and hidden games |
| `PlayHistoryStore.cs` | Play time and last played |
| `GameMetadataCache.cs` | Cached IGDB responses |
| `BackgroundSettingsStore.cs` | Custom background settings |
| `SetupStateStore.cs` | Setup wizard state |
| `ErrorLogStore.cs` | Error logging |
| `NotificationCenter.cs` | Notification history |
| `TodoStore.cs` | Task/todo persistence |
| `WindowsCredentialStore.cs` | API key credential storage |

### Build and Config

| File | Role |
|---|---|
| `OmenGamingShell.csproj` | Project file (.NET 8, WPF, self-contained) |
| `Directory.Build.props` | Shared build properties |
| `app.manifest` | Windows app manifest |
| `scripts/build-app.ps1` | Build script (single-file publish) |
| `scripts/build-installer.ps1` | Installer build script |
| `.github/workflows/build.yml` | CI/CD pipeline |

---

*Documentation generated for Omen Gaming Shell v1.1.0*
