Copy and paste everything below into Muse.

---

Muse, from now on use the Windows app **Beacon** whenever you need my attention while you are working (captcha, SMS/email verification code, approval, warning, or any message). Beacon is installed on my Windows machine and already reachable over Tailscale. It shows a large, modern popup in the middle of my screen.

## Connection details

- Base URL: `http://<TAILSCALE_HOST_OR_IP>:8765` (replace with the Tailscale hostname or IP shown in Beacon's tray settings; if it does not respond, ask me to open Beacon from the system tray and read the current URL from its settings window, because the port or IP may have changed).
- Main endpoints: `POST {base URL}/notify` (show a popup), `POST {base URL}/reply` (popup + quick replies + persistent chat card), `POST {base URL}/shell` (admin approval), `POST {base URL}/chat` (stream your reply text into the chat card).
- Header: `Content-Type: application/json`
- Authentication: `Authorization: Bearer <TOKEN>` where `<TOKEN>` is the secret shown in Beacon's tray settings window. You may instead use `?token=<TOKEN>` in the URL or an `X-Auth-Token: <TOKEN>` header. I will give you the token once; store it in your memory for this setup and never print it back to me. If a request returns HTTP 401, ask me to copy the current token from Beacon settings.

Check connectivity with `GET {base URL}/health` — it returns JSON with `"ok": true` and `"listening": true` when Beacon is running. This endpoint does not need the token.

## How to send a notification

Always send a JSON object. The only optional fields are the ones you want to customize; anything missing gets a sensible default.

Minimal example:

```json
{
  "title": "Captcha needs you",
  "message": "Please solve the captcha on the screen so I can continue applying to jobs.",
  "type": "captcha",
  "source": "Muse.ai"
}
```

Verification code example:

```json
{
  "title": "Verification code",
  "type": "verification",
  "source": "Muse.ai",
  "code": "482-913",
  "codeLabel": "SMS code",
  "message": "Enter this code in the browser tab that is open.",
  "duration": "never",
  "actions": [{ "label": "Copy code", "copy": "482-913" }]
}
```

Rich example (markdown, details, progress, buttons, image):

```json
{
  "title": "Decision needed",
  "message": "Muse found **3 strong matches**. Choose what to do next:\n\n- Accept all\n- Review first\n- Skip",
  "type": "question",
  "source": "Muse.ai",
  "category": "Job applications",
  "color": "#B79CFF",
  "emoji": "✨",
  "duration": "2m",
  "details": { "Company": "Example Co", "Role": "Senior Engineer", "Matches": 3 },
  "progress": { "value": 75, "label": "Applications reviewed" },
  "image": "https://example.com/screenshot.png",
  "actions": [
    { "label": "Open listing", "url": "https://example.com/job", "primary": true },
    { "label": "Copy company", "copy": "Example Co" }
  ],
  "footer": "Muse is paused until you respond."
}
```

### Quick replies (my decision goes back to you)

Use `/reply` when you need me to choose something or type an answer. The buttons and my typed reply appear in a small chat card under the popup, and the chat stays consistent across prompts.

```json
{
  "title": "Which should I do?",
  "message": "Pick the next action.",
  "type": "question",
  "source": "Muse.ai",
  "options": [
    { "label": "Accept all", "value": "accept" },
    { "label": "Review first", "value": "review" },
    { "label": "Skip", "value": "skip" }
  ],
  "replyUrl": "https://your-server.example.com/beacon-callback",
  "replyHeaders": { "X-Job": "job-123" },
  "wait": false,
  "duration": "never"
}
```

`options` (alias `quickReplies`) can be strings or `{label, value}` objects. `allowCustomReply` defaults to true; set it to false for buttons only. `replyPlaceholder` and `replyButtonText` customize the text box. `wait:true` blocks until I answer; otherwise poll `GET {base URL}/result/{id}` using the `id` from the response. If `replyUrl` is given, Beacon POSTs the decision JSON to it immediately (including `X-Beacon-Token`). The result has `kind` (`reply`, `denied`, `cancelled`, `timeout`), `value`, and `label`.

#### Fresh quick replies for every message

Do not leave the same buttons on screen forever. With **each answer you stream**, generate a new, context-specific set of 2–4 options for what I might want to do next and include them as `options` in any `/chat` message. Beacon replaces the buttons immediately; my free-text box stays available as always.

```json
{
  "id": "abc123-r2",
  "delta": "I found three strong matches. ",
  "options": [
    { "label": "Apply all", "value": "apply_all" },
    { "label": "Review first", "value": "review_first" },
    { "label": "Skip for now", "value": "skip" }
  ]
}
```

Finish the stream as usual:

```json
{ "id": "abc123-r2", "delta": "", "done": true }
```

Rules for the generated options:

- Write them for **this** message, not the original prompt. They should be the obvious next choices given what you just told me.
- Keep labels short (1–4 words) and values stable, unique, and meaningful (`apply_all`, not `1`). I may click one or type something else.
- Include an explicit exit when the conversation could go on forever, e.g. `{ "label": "Done", "value": "done" }` or `{ "label": "Cancel", "value": "cancel" }`.
- You can put `options` on the first chunk, the last chunk, or a complete `text` message. Omit it entirely if the current buttons are still the right ones.
- Aliases `quickReplies`, `quickReplyOptions`, and `nextOptions` also work.
- Follow-ups still arrive as new `-r2`, `-r3` ids. Stream each answer with the id of the message you are answering, and put that message's new options in the same request.

#### Measured limits — they adapt to my screen

Beacon sizes the quick-reply row from the actual chat-card width, which follows screen resolution and DPI scale. I measured the safe limits at 1920×1080 (chat card ≈ 490 DIPs wide):

- **Buttons:** up to **8** are rendered; extras are ignored. On a narrow card as few as **4** fit. One or two rows are used, and the transcript is never pushed off screen.
- **Label length:** about **58 characters** at that width (**≈22** on a very narrow card; hard ceiling **72**). Longer labels are shortened with `…` and the full text is shown on hover. The `value` is never truncated.
- Safest target that works on every screen: **2–4 buttons, labels under 30 characters, values under 40 characters**.

**No options at all:** when a popup starts without `options`, no buttons appear and the quick-reply section is hidden entirely. To clear the buttons mid-conversation, send `"options": []` — the section disappears and my free-text box remains. The next message that includes `options` restores both the section and the buttons. Omitting the `options` key entirely leaves the current buttons unchanged.

**Ongoing conversation:** the chat prompt stays open after my first reply, so you must keep listening on `replyUrl` for further messages. Every follow-up is a **separate callback with a new `id`** such as `abc123-r2`, plus `conversationId` (the original request id) and `sequence` (`2`, `3`, …). Treat each one as a new user message in the same conversation — do not discard it because the original request was already answered. Without `replyUrl` there is no channel back to you, so follow-up messages cannot reach Muse.

### Streaming your reply into the chat card

When you are ready to answer me in the chat, stream chunks to `{base URL}/chat` with the same `id` from the `/reply` response. Each call is a small JSON object:

```json
{ "id": "the-id-from-the-reply-response", "delta": "I found " }
```

Send as many `delta` chunks as you like, then finish with:

```json
{ "id": "the-id-from-the-reply-response", "delta": "", "done": true }
```

You can also send one complete message instead: `{ "id": "...", "text": "Done!" }`. The card shows the text live with a typing caret and switches from "Muse is replying…" to "Muse replied" when `done:true` arrives. My messages use my Windows profile photo; yours use the Muse.ai logo.

For a follow-up, stream your answer with the **new `id` from that follow-up callback** (for example `abc123-r2`), not the original request id. That keeps each answer attached to the message it replies to.

### Admin shell approval

Use `/shell` only when you truly need to run a Windows command. I must approve it in a dedicated popup before anything executes. If `/shell` returns HTTP 403, shell requests are disabled in Beacon's tray settings — ask me to re-enable "Allow admin shell requests" rather than retrying.

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

`elevated` defaults to `true`, which triggers a Windows UAC prompt that I must accept — you cannot bypass UAC. Use `false` for harmless commands that need no elevation. `shell` may be `cmd` or `powershell`; `workingDirectory` and `executionTimeout` (seconds) are optional. The final result includes `kind` (`executed`, `denied`, `cancelled`, `failed`, `timeout`), `stdOut`, `stdErr`, `exitCode`, and `durationMs`. For `wait:false`, poll `/result/{id}` or use `replyUrl`. Never restart my PC, Tailscale, or unrelated apps.

Plain text is also accepted: POST a raw string as `text/plain`, or use form data, or use GET with query parameters (`?title=...&message=...&token=...`). JSON is preferred.

Remember: all actionable controls are locked for 3 seconds after a popup appears, so do not expect an instant click. Clicking outside will not dismiss my prompt.

## Fields you can use (all case-insensitive)

- `title` (`heading`, `subject`, `name`) — large title.
- `message` (`body`, `text`, `content`, `description`, `question`) — main text. It understands Markdown (`**bold**`, `*italic*`, `` `code` ``, `[link](https://...)`, `- lists`, `> quotes`, `# headings`) and common HTML tags.
- `markdown` / `html` — use these instead of `message` if you want to be explicit.
- `type` (`severity`, `level`, `priority`, `kind`): `info`, `success`, `warning`, `error`, `question`, `captcha`, `verification`, `reminder`. The URL also sets a type: `/captcha`, `/verification`, `/code`, `/otp`, `/alert`, `/confirm`, `/reminder` all work in place of `/notify`.
- `code` (`otp`, `pin`, `verificationCode`, `2fa`) — renders a large monospace code with a Copy button.
- `codeLabel`, `source` (`app`, `service`, `from`), `category` (`tag`, `topic`) — small labels.
- `duration` (`timeout`, `showFor`, `seconds`): number of seconds or strings like `"15s"`, `"2m"`, `"1h"`. Use `"never"`, `0`, or `"sticky": true` when you need me to act before it disappears.
- `sound`: `true`/`false`, or `"none"`, `"beep"`, `"success"`, `"warning"`, `"error"`. `silent: true` also mutes it.
- `emoji` / `icon` — override the themed icon. `color` (`accent`) — custom hex accent such as `#FFB84D`.
- `details` (`fields`, `meta`) — object or `[{"label":"...","value":"..."}]`, shown as a neat table.
- `image` (`imageUrl`, `imageBase64`, `screenshot`) — URL, local path, data URI, or base64 image.
- `progress` — `0`–`100`, or `{"value":50,"label":"..."}`.
- `actions` (`buttons`) — up to four `{"label":"...","url":"..."}` or `{"label":"...","copy":"..."}` buttons. A single `url` field is a shortcut for one "Open link" button.
- `raw` (`json`, `payload`) — an arbitrary object displayed as formatted JSON when there is no better field.
- `footer` (`hint`, `note`) — fine print at the bottom.
- `dismissible: false` — keep the popup until a button is used or it is cleared from the tray.
- `options` (`quickReplies`) — quick reply strings or `{label, value}` objects for `/reply`.
- `replyUrl`, `replyHeaders` — webhook for the decision; `wait`, `waitTimeout` — block until I answer (timeout in seconds).
- `allowCustomReply`, `replyPlaceholder`, `replyButtonText` — customize the reply box.
- `command` (`executable`, `arguments`), `shell` (`cmd`/`powershell`), `workingDirectory`, `executionTimeout`, `elevated` — for `/shell`.

## Other endpoints

- `GET /health` — status check (no token required).
- `POST /clear` — dismiss the current popup and empty the queue (needs the token).
- `GET /result/{id}`, `/interaction/{id}`, `/reply/result/{id}`, `/shell/result/{id}` — poll a reply/approval result.
- `POST /chat`, `/chat/stream`, `/reply/stream`, `/muse/reply` — aliases for the streaming chat endpoint.

Notifications are queued, so if you send several at once they appear one after another. A successful call returns JSON like `{"ok":true,"id":"15e8c4af","status":"shown","title":"...","kind":"captcha"}`.

## Rules for you

1. Use Beacon every time you need my eyes or a decision, instead of assuming I am watching the screen.
2. For captchas and verification codes: set `type` correctly, put the exact code in `code`, use `duration: "never"` or `sticky: true`, and include a Copy action for the code.
3. Keep `message` short and actionable; put supporting facts in `details`.
4. Do not include the token in any popup or in any message you show me.
5. If `/health` fails, tell me: "Beacon is unreachable — check that it is running in the tray and that I am on Tailscale." Do not try to restart Tailscale or my other apps.
6. For decisions, use `/reply` with clear `options` so I can click once; use `replyUrl` so you actually receive my answer and can continue the conversation. Stream your follow-up answer to `/chat` with the `id` from the latest callback, and include a fresh `options` array with every answer so the buttons match what you just said.
7. Never run admin commands without going through `/shell` and waiting for my approval. Use `elevated:false` for harmless commands; expect a UAC prompt when `elevated` is true. Do not restart my PC, Tailscale, or unrelated apps.
8. Remember the 3-second safety lock on every popup; do not interpret a delayed click as a denial.
9. If a remote request fails but local works, tell me to run this once in an elevated PowerShell (it only adds a firewall rule, it does not restart anything):
   `netsh advfirewall firewall add rule name="Beacon Notification Listener" dir=in action=allow program="C:\Program Files\Beacon\Beacon.exe" enable=yes`

Acknowledging this prompt is enough; you do not need to send a test notification unless I ask.
