# Changelog

All notable changes to Beacon are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## [1.0.0] — first public release

### Added
- Tray app with a raw `TcpListener` HTTP server and token authentication over Tailscale.
- Full-screen, multi-monitor aware popups with a dark, modern card UI.
- Persistent chat cards with streaming replies, Muse/user avatars, and a typing indicator.
- Adaptive quick replies, custom composer, and dynamic buttons that can change mid-conversation.
- Human-in-the-loop admin shell approval with UAC elevation, result polling, and a global off switch.
- 3-second safety lock on every actionable control; clicking outside never dismisses a prompt.
- Themed single-file `Beacon-Setup.exe` installer with Start menu shortcut and registry uninstall entry.
- A **Check for updates** button in tray settings. It reads the GitHub Releases feed and can
  download/apply the newest asset in one click (`Beacon-Setup.exe` on Windows,
  `Beacon-x86_64.AppImage` on Linux).
- A native Avalonia Linux port packaged as `Beacon-x86_64.AppImage`, with the same HTTP API,
  chat, quick replies, shell approval, and 3-second safety lock.
- Installer shows a **Repair** button when Beacon is already installed, and has
  **Advanced options** to choose custom installation and app-data folders.

### Changed
- Smoothed transitions across the app and installer: compositor-friendly fade/scale
  animation only (no layout-affecting width animations).

### Fixed
- Windows one-click updates now download to a writable temp path and pass the
  running install directory to the silent installer, preserving custom installs.
- `Auto-dismiss = 0` and `duration: 0` now keep popups sticky as documented.

### Safety
- Beacon never closes or restarts Tailscale, Stremio, other apps, or the PC.
- Settings in `%APPDATA%\Beacon\settings.json` are preserved across installation.
