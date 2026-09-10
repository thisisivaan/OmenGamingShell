# v1.0.2 — Functional Update Notifications

## Features added

- **Update available now lives in the Notification Center** — instead of a transient toast, the update prompt is a persistent notification ("Update available — vX.Y.Z is ready to install") that stays until the app is updated. It is refreshed on each startup and hourly check and cleared automatically once the new version runs.
- **Notification Center is now interactive** — clicking the update notification opens the Update overlay; clicking the dimmed area closes the Notification Center.
- **UPDATE NOW flow** — the shell shows a "Downloading update" overlay (indeterminate progress) for the entire download, then restarts into the updated executable automatically. On failure it dismisses the dialog and shows an error toast.
- **Session dismissal** — pressing NOT NOW or CLEAR ALL hides the update notification for the current session; it reappears on the next launch if the update is still available.
- **Per-version release notes** — `versions/v1.0.2/RELEASE-NOTES.md` is auto-created by the new `release-notes` workflow on every `v*` tag push (this repository's release convention).

## Bugs fixed

- **Accessories container always showed "No devices"** — the home-screen Accessories container is a Controller + Earphone status panel, not a device list. The empty-state text was removed; the container now always shows its status widgets and never the "No devices" state.
- **Notification Center was permanently empty** — `NotificationCenter.Push` was never called anywhere in the app. The container is now fed by the update-available notification.
- **Update notification duplicated on every check** — a single persistent, tagged notification is kept and refreshed instead of pushing a new one hourly.
- **Stale update notification after updating** — the update notification is removed automatically once the running version is current.

## Commits since v1.0.1

30776df Make update notifications functional and remove Accessories empty state
df10307 Add version release-note folders and auto-generating release-notes workflow