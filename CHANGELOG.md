# Changelog

All notable changes to Beacon are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## [1.6.0]

### Added
- Multi-monitor aware popups — the translucent backdrop still covers the whole virtual screen, but the card is centered on a single display (the one under the cursor, falling back to the primary display). Fixes cut-off popups on laptop + TV setups.
- Automatic re-fit when displays are plugged in, unplugged, or change resolution.

### Changed
- Card sizing limits now come from the target display instead of the entire virtual screen, including correct mixed-DPI conversion.
- Large transcript reserves extra space for wrapped quick-reply rows and the composer hint.

## [1.5.0]

### Added
- **Allow admin shell requests (/shell)** setting. When disabled, shell endpoints return HTTP 403 before any popup is created.
- Adaptive quick-reply rows gain a 6 px vertical gap.

### Changed
- Removed the redundant "clicking outside will not dismiss" banner.
- Both Muse setup prompts document the shell 403 behavior.

## [1.4.0]

### Added
- Universal persistent chat card for every alert type.
- Dynamic quick replies — Muse can replace buttons in any `/chat` message; `options: []` clears them.
- Screen-adaptive quick-reply limits (button count and label length scale with chat-card width).
- 12/24-hour clock setting.

### Fixed
- Custom reply send button now reads exactly **Send**.

## [1.3.0]

### Added
- Live streaming chat with typing indicator and Muse avatar.
- Webhook callbacks and follow-up result ids (`-r2`, `-r3`, …).
- Windows profile photo avatar for user messages.

## [1.2.0]

### Added
- Admin shell approval popup with UAC elevation, result polling, and a 3-second safety lock.

## [1.1.0]

### Added
- Quick replies, custom reply box, and persistent conversations.
- Tray settings window with token regeneration, port/bind configuration, and firewall hint.

## [1.0.0]

### Added
- Initial release: tray app, raw `TcpListener` HTTP server, token auth, and full-screen popups.
