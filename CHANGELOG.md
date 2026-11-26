# Changelog

All notable changes to Beacon are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## [1.0.1]

### Added
- Tray action **Re-open last notification** on Windows and Linux. It re-shows the most
  recent ordinary popup without re-arming a pending reply or shell approval.
- Installer confirmation dialog when a newer Beacon version is already installed.
  Continuing performs the downgrade and cleans up stale uninstall registrations;
  silent one-click updates never downgrade.

### Changed
- Version metadata, installer UI, API sample and updater user agent now report `1.0.1`.

### Fixed
- The installer re-detects the destination after Advanced options change it, so the
  displayed current version and downgrade check match the actual install folder.
  Custom install locations, all Beacon uninstall registry rows, and silent-install
  failures are now handled correctly.
- The "allow shell requests" off switch is enforced for body-driven shell payloads
  on every route, not just `/shell` URLs. Unauthenticated shell requests now get 401.
- Webhooks no longer attach Beacon's secret token when authentication is disabled,
  follow redirects, or accept caller-supplied reserved headers.
- Sending another reply no longer erases Muse's in-progress streamed answer; chunks
  with an unknown id no longer attach to a different conversation, and an empty
  final chunk no longer creates a blank message.
- Dismissing a popup or letting a wait timeout expire now completes the pending
  request instead of leaving it hanging; late decisions are reported as such.
- `dismissible: false` is honored by the Dismiss button and auto-dismiss on both
  platforms, and approval popups always show the exact command even when a code
  field is present.
- Windows settings restarts the listener when the port or bind interface changes;
  advertised URLs use `127.0.0.1` when bound to loopback.
- `Expect: 100-continue` requests are answered, and chunked UTF-8 bodies are decoded
  once so multibyte characters survive chunk boundaries.
- Updates verify the GitHub SHA-256 digest, use unique temp files, tolerate releases
  without this platform's asset, and parse prerelease tags.
- Linux audio falls back correctly when `paplay` cannot reach a sound server; the
  settings window is resizable and session-autostart failures are no longer hidden.

---

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
