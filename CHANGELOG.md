# Changelog

All notable changes to Beacon are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## [1.0.0] — current release

The full feature set that was previously shipped as 1.6.0 is now released as 1.0.0.
The version number was intentionally reset for this private release.

### Added
- Tray app with a raw `TcpListener` HTTP server and token authentication over Tailscale.
- Full-screen, multi-monitor aware popups with a dark, modern card UI.
- Persistent chat cards with streaming replies, Muse/user avatars, and a typing indicator.
- Adaptive quick replies, custom composer, and dynamic buttons that can change mid-conversation.
- Human-in-the-loop admin shell approval with UAC elevation, result polling, and a global off switch.
- 3-second safety lock on every actionable control; clicking outside never dismisses a prompt.
- Themed single-file `Beacon-Setup.exe` installer with Start menu shortcut and registry uninstall entry.
- Added a **Check for updates** button to tray settings. It reads the public GitHub
  Releases feed and can download/apply the newest asset in one click (`Beacon-Setup.exe`
  on Windows, `Beacon-x86_64.AppImage` on Linux).
- Added a native Avalonia Linux port packaged as `Beacon-x86_64.AppImage`, with the
  same HTTP API, chat, quick replies, shell approval, and 3-second safety lock.
- Installer shows a **Repair** button when Beacon is already installed, and has
  **Advanced options** to choose custom installation and app-data folders.
- One-time downgrade path in this build only: running this installer over an existing
  1.6.0 install and choosing Repair or Install replaces it and resets its reported
  version to 1.0.0. Future builds cannot downgrade newer installations.

### Changed
- Version metadata, tray settings/API docs, README badge, and `/health` response now report `1.0.0`.
- Replaced the WiX MSI installer with a self-contained WPF EXE installer matching Beacon's theme.
- Smoothed transitions across the app and installer: compositor-friendly fade/scale
  animation only (no layout-affecting width animations).

### Fixed
- Windows one-click updates now download to a writable temp path and pass the
  running install directory to the silent installer, preserving custom installs.
- `Auto-dismiss = 0` and `duration: 0` now keep popups sticky as documented.

### Safety
- Beacon never closes or restarts Tailscale, Stremio, other apps, or the PC.
- Settings in `%APPDATA%\Beacon\settings.json` are preserved across installation.

---

## Pre-release history

These versions were used internally before the version reset. They are superseded by 1.0.0.

### 1.6.0
- Multi-monitor aware popups with per-display sizing and mixed-DPI handling.

### 1.5.0
- Admin shell permission switch and adaptive quick-reply spacing.

### 1.4.0
- Universal persistent chat card, dynamic quick replies, and 12/24-hour clock setting.

### 1.3.0
- Streaming chat, webhook callbacks, follow-up ids, and Windows profile photo avatar.

### 1.2.0
- Admin shell approval popup with UAC elevation and 3-second safety lock.

### 1.1.0
- Quick replies, custom reply box, persistent conversations, and tray settings.

### 1.0.0 (internal prototype)
- Initial tray bridge, token auth, and full-screen popups.
