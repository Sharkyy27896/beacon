# HTTP API reference

Base URL: `http://<TAILSCALE_HOST_OR_IP>:8765` (port is configurable in tray settings).
All examples assume the token from **Settings → Secret token**.

## Authentication

When `Require a secret token` is enabled, supply the token in one of these ways:

| Method | Example |
| --- | --- |
| Bearer header (recommended) | `Authorization: Bearer <TOKEN>` |
| Custom header | `X-Auth-Token: <TOKEN>` or `X-API-Key: <TOKEN>` |
| Query string | `?token=<TOKEN>`, `?key=…`, `?auth=…`, `?apikey=…` |

A missing or incorrect token returns `401 Unauthorized`.
`GET /health`, `/ping` and `/status` are always public.

## Endpoints

| Path | Methods | Description |
| --- | --- | --- |
| `/notify` | POST, PUT, GET | Show a popup. Aliases: `/alert`, `/message`, `/show`, `/captcha`, `/verification`, `/verify`, `/code`, `/otp`, `/confirm`, `/reminder`, `/notification`, `/push`, `/` |
| `/reply` | POST, PUT | Popup with quick replies and a persistent chat card. Aliases: `/ask`, `/question`, `/prompt`, `/interact`, `/quickreply` |
| `/shell` | POST, PUT | Approval popup for a command on the current platform. Aliases: `/exec`, `/run`, `/admin`, `/command` |
| `/chat` | POST, PUT | Append complete or streamed chat text to the card. Aliases: `/chat/stream`, `/reply/stream`, `/muse/reply` |
| `/result/{id}` | GET | Poll a reply or approval result. Aliases: `/interaction/{id}`, `/reply/result/{id}`, `/shell/result/{id}` |
| `/health` | GET | Status JSON — no token required. Aliases: `/ping`, `/status` |
| `/clear` | POST | Dismiss the current popup and empty the queue. Alias: `/dismiss` |

Unknown paths return `404` with a usage hint.

## Request bodies

Beacon accepts `application/json` objects, JSON strings, `text/plain`,
`application/x-www-form-urlencoded`, and GET query parameters. Field names are
case-insensitive and have several aliases.

### Common fields

| Field | Aliases | Meaning |
| --- | --- | --- |
| `title` | `heading`, `subject`, `name` | Large title |
| `message` | `body`, `text`, `content`, `description`, `question` | Main text; Markdown and simple HTML supported |
| `markdown` / `html` | `md`, `markup` | Explicit rich text |
| `type` | `kind`, `severity`, `level`, `priority` | `info`, `success`, `warning`, `error`, `question`, `captcha`, `verification`, `reminder` |
| `code` | `otp`, `pin`, `verificationCode`, `2fa` | Large monospace code with a Copy action |
| `source` | `app`, `service`, `from` | Small "from …" label |
| `category` | `tag`, `topic`, `label` | Subtitle under the title |
| `duration` | `timeout`, `showFor`, `seconds` | Seconds, `"15s"`, `"2m"`, `"never"` / `0` for sticky |
| `sticky` | `persistent`, `requireAction` | Keep the popup open until dismissed |
| `sound` | `playSound`, `silent` | `true`/`false`, or `none`, `beep`, `success`, `warning`, `error` |
| `emoji` / `icon` | — | Override the themed icon |
| `color` | `accent`, `themeColor` | Hex accent, e.g. `#FFB84D` |
| `details` | `fields`, `meta` | Object or `[{label, value}]` rows |
| `image` | `imageUrl`, `imageBase64`, `screenshot` | URL, file path, data URI, or base64 |
| `progress` | `percent` | `0–100` or `{value, label}` |
| `actions` | `buttons` | Up to four `{label, url}` / `{label, copy}` buttons |
| `url` | `link`, `actionUrl` | Shortcut for one "Open link" button |
| `footer` | `hint`, `note` | Fine print at the bottom |
| `raw` | `json`, `payload` | Arbitrary object rendered as formatted JSON |
| `dismissible` | — | `false` keeps the popup until cleared |

### Reply and chat fields

| Field | Aliases | Meaning |
| --- | --- | --- |
| `options` | `quickReplies`, `choices`, `answers` | Strings or `{label, value}` objects; screen-adaptive rendering |
| `allowCustomReply` | `customReply` | Show the free-text composer (default `true` on `/reply`) |
| `replyPlaceholder`, `replyButtonText` | — | Composer hint and send-button label |
| `replyUrl` | `webhook`, `callback` | HTTP(S) endpoint that receives each decision as JSON |
| `replyHeaders` | — | Extra headers for the webhook POST |
| `wait`, `waitTimeout` | — | Block until the first answer; timeout in seconds |
| `id`, `delta`, `text`, `done` | — | Streaming chat protocol (below) |

### Shell fields

| Field | Aliases | Meaning |
| --- | --- | --- |
| `command` | `shell`, `executable`, `arguments` | Command to run after approval |
| `shell` | — | `cmd` or `powershell` |
| `workingDirectory`, `executionTimeout` | — | Working directory and timeout in seconds |
| `elevated` | `admin`, `runAsAdmin` | `true` (default) triggers UAC; `false` runs as current user |

Shell requests return `403` when **Allow admin shell requests** is disabled in settings.

## Quick replies

`POST /reply` with `options`:

```json
{
  "title": "Which should I do?",
  "message": "Pick the next action.",
  "type": "question",
  "source": "Muse.ai",
  "options": [
    { "label": "Accept all", "value": "accept" },
    { "label": "Review first", "value": "review" },
    { "label": "Done", "value": "done" }
  ],
  "replyUrl": "https://example.com/beacon-callback",
  "wait": false,
  "duration": "never"
}
```

- Up to **8** buttons render, with labels shortened to fit the chat-card width.
- With no `options`, the quick-reply section is hidden. `"options": []` clears it
  mid-conversation; a later message with `options` restores it. Omitting the key
  keeps the current buttons.
- `Enter` sends the custom reply; `Shift+Enter` inserts a newline.

Result shape:

```json
{
  "kind": "reply",
  "value": "accept",
  "label": "Accept all",
  "conversationId": "15e8c4af",
  "sequence": 1
}
```

`kind` is `reply`, `denied`, `cancelled`, or `timeout`.

## Follow-ups

The first answer uses the original request `id`. Each later answer gets a new id
`{baseId}-r2`, `-r3`, … Keep listening on `replyUrl` (recommended) or poll
`/result/{id}-r{N}` in the background. Each callback includes `conversationId`
and `sequence` so it can be treated as a new message in the same conversation.

Webhook requests include `X-Beacon-Token` plus anything in `replyHeaders`.

## Streaming chat

Send chunks to `/chat` with the id of the message being answered:

```json
{ "id": "15e8c4af", "delta": "Working " }
{ "id": "15e8c4af", "delta": "on it… " }
{ "id": "15e8c4af", "delta": "", "done": true }
```

Or one complete message: `{ "id": "15e8c4af", "text": "Done!" }`.
A `?stream=true` plain-text body is treated as a single chunk.

Fresh buttons can ride along with any chat message:

```json
{
  "id": "15e8c4af-r2",
  "delta": "Here are the matches. ",
  "options": [
    { "label": "Apply all", "value": "apply_all" },
    { "label": "Skip", "value": "skip" }
  ]
}
```

Aliases for `options` in chat messages: `quickReplies`, `quickReplyOptions`, `nextOptions`.

## Shell approval

```json
{
  "title": "Admin approval needed",
  "message": "Clear the stale build cache.",
  "command": "echo Beacon shell test",
  "shell": "cmd",
  "elevated": false,
  "wait": true,
  "waitTimeout": 60
}
```

Nothing executes until **Approve & run** is clicked. Result kinds: `executed`,
`denied`, `cancelled`, `failed`, `timeout`, with `stdOut`, `stdErr`, `exitCode`,
and `durationMs`.

## Health response

```json
{
  "ok": true,
  "service": "Beacon",
  "version": "1.0.1",
  "listening": true,
  "address": "0.0.0.0:8765",
  "pending": 0,
  "now": "2026-11-23T12:00:00-08:00"
}
```
