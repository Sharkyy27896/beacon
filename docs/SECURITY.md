# Security policy

## Supported versions

Only the latest released version receives fixes. Check the version in tray
settings or via `GET /health`.

## Reporting a vulnerability

This repository is private. If you find a security issue, contact the repository
owner directly rather than opening a public issue. Include the affected version,
steps to reproduce, and impact.

## Security model

### Authentication

- Every request except `/health`, `/ping` and `/status` requires the shared token.
- The token is generated on first run and stored in `%APPDATA%\Beacon\settings.json`.
- Accepted transports: `Authorization: Bearer`, `X-Auth-Token`, `X-API-Key`, or
  query parameters `token`, `key`, `auth`, `apikey`.
- Query-string auth is convenient for simple scripts but leaks tokens into logs
  and shell history; prefer the Bearer header for automation.
- Use **New** in tray settings to rotate a token. Beacon must be listening on the
  same network interface for the new token to take effect (press **Save settings**).

### Network exposure

- Default bind is `Any interface (Tailscale)` on port `8765`.
- Beacon is intended to be reachable only over a private Tailscale network.
- Do not port-forward the listener to the public internet.
- Windows Firewall may need one inbound rule for `Beacon.exe`; the exact `netsh`
  command is in the main README.

### Shell approval

- `/shell` never executes anything without an explicit **Approve & run** click.
- **Deny**, **Cancel**, Escape and timeout never execute the command.
- `elevated: true` triggers a real Windows UAC consent prompt that cannot be
  suppressed programmatically.
- The **Allow admin shell requests** checkbox rejects all shell endpoints with
  `403` before a popup is even created.
- Beacon never uses shell approval to restart the PC, Tailscale, Stremio, or any
  unrelated application.

### Webhooks

- `replyUrl` must be an absolute `http://` or `https://` URL.
- Each webhook POST carries `X-Beacon-Token` so the receiver can authenticate the
  callback. Use `replyHeaders` to supply any additional secret the receiver needs.
- Prefer HTTPS whenever the callback leaves the local machine.

### Data handling

- Settings and tokens stay in the local user profile; Beacon sends no telemetry.
- Images, markdown and JSON are rendered locally in the popup.
- Setup prompts contain placeholders only — never put a real token in a prompt,
  popup, commit, or screenshot.

## Secrets in this repository

No tokens, Tailscale addresses, machine names, or absolute user-profile paths
should ever be committed. Build outputs and local toolchains are ignored via
`.gitignore`. The installer file list is generated with repository-relative paths
by `tools/build_installer.ps1`.
