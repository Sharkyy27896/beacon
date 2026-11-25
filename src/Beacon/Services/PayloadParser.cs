using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Models;

namespace Beacon.Services;

/// <summary>
/// Turns any of the supported request shapes (JSON, form data, plain text, query
/// string) into one normalized notification payload. Field names are generous on
/// purpose: an AI caller should not have to memorise one exact schema.
/// </summary>
public static class PayloadParser
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };
    private static readonly Regex DurationPattern = new(
        @"^\s*(\d+(?:\.\d+)?)\s*(ms|msec|milliseconds?|s|sec|secs|seconds?|m|min|mins|minutes?|h|hr|hours?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static NotificationPayload Parse(HttpRequestData request, AppSettings settings)
    {
        var root = ParseRoot(request);
        var fields = Flatten(root);
        var payload = new NotificationPayload { PlaySound = settings.PlaySound };

        // ---------- kind / theme ----------
        var kindValue = Get(fields, "kind", "type", "severity", "level", "priority", "urgency", "importance", "variant");
        payload.Kind = ParseKind(kindValue.ValueKind == JsonValueKind.Undefined ? default : kindValue, request.Path);

        // ---------- text ----------
        payload.Title = Text(Get(fields, "title", "heading", "header", "subject", "headline", "name"))
                        ?? DefaultTitle(payload.Kind);
        payload.Message = Text(Get(fields, "message", "body", "text", "content", "description",
                                    "notification", "note", "prompt", "instructions", "question"))
                          ?? "";
        payload.Markdown = Text(Get(fields, "markdown", "md", "richText", "richtext", "formatted"));
        payload.Html = Text(Get(fields, "html", "htmlBody", "htmlbody", "markup"));

        var raw = Get(fields, "raw", "json", "payload", "object", "dataObject");
        if (raw.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            payload.RawJson = JsonSerializer.Serialize(raw, PrettyJson);

        payload.Code = Text(Get(fields, "code", "otp", "verificationCode", "verification_code", "pin",
                                "token", "passcode", "twoFactorCode", "2fa"));
        payload.CodeLabel = Text(Get(fields, "codeLabel", "code_label", "codeTitle", "codeName"));
        if (!string.IsNullOrWhiteSpace(payload.Code))
        {
            if (payload.Kind == NotificationKind.Info) payload.Kind = NotificationKind.Verification;
            payload.CodeLabel ??= "Verification code";
        }

        payload.Category = Text(Get(fields, "category", "tag", "topic", "label", "group"));
        payload.Source = Text(Get(fields, "source", "app", "application", "service", "from", "sender", "by"));
        payload.Emoji = Text(Get(fields, "emoji", "icon", "iconEmoji", "symbol"));
        payload.AccentHex = NormalizeHex(Text(Get(fields, "accent", "color", "accentColor", "accent_color", "theme", "themeColor")));
        payload.Footer = Text(Get(fields, "footer", "hint", "footnote", "subtext"));

        // ---------- behaviour ----------
        var duration = Get(fields, "duration", "timeout", "displayFor", "showFor", "visibleFor", "seconds", "secs", "ms", "milliseconds");
        payload.Duration = ParseDuration(duration) ??
                           (settings.DefaultDurationSeconds > 0
                               ? TimeSpan.FromSeconds(settings.DefaultDurationSeconds)
                               : null);
        if (!duration.ValueKind.IsNullish())
        {
            var sticky = Get(fields, "sticky", "persistent", "waitForAction", "requireAction", "stay");
            if (ParseBool(sticky) == true || ParseDuration(duration) is null)
                payload.Duration = null;
        }
        var stickyField = Get(fields, "sticky", "persistent", "waitForAction", "requireAction", "stay");
        if (ParseBool(stickyField) == true) payload.Duration = null;

        var sound = Get(fields, "sound", "playSound", "playsound", "audio", "chime");
        if (!sound.ValueKind.IsNullish())
        {
            var soundText = Text(sound)?.Trim().ToLowerInvariant();
            if (soundText is "none" or "off" or "silent" or "false" or "0") { payload.PlaySound = false; payload.SoundName = "none"; }
            else if (soundText is not null and not "true" and not "default" and not "on" and not "1") { payload.PlaySound = true; payload.SoundName = soundText; }
            else payload.PlaySound = ParseBool(sound) ?? settings.PlaySound;
        }
        var silent = ParseBool(Get(fields, "silent", "mute", "quiet"));
        if (silent == true) { payload.PlaySound = false; payload.SoundName = "none"; }
        if (ParseBool(Get(fields, "noSound", "nosound")) == true) { payload.PlaySound = false; payload.SoundName = "none"; }

        payload.Dismissible = ParseBool(Get(fields, "dismissible", "closeable", "closable", "allowDismiss")) ?? true;
        payload.Topmost = ParseBool(Get(fields, "topmost", "alwaysOnTop", "onTop")) ?? true;
        if (ParseBool(Get(fields, "modal", "forceAction")) == true) payload.Dismissible = false;

        // ---------- rich content ----------
        payload.ImageSource = Text(Get(fields, "image", "imageUrl", "image_url", "imageBase64", "image_base64",
                                       "photo", "picture", "screenshot", "attachment", "thumbnail"));
        var progressValue = Get(fields, "progress", "percent", "percentage", "progressPercent");
        if (!progressValue.ValueKind.IsNullish())
        {
            if (progressValue.ValueKind == JsonValueKind.Object)
            {
                payload.Progress = ParseDouble(GetObject(progressValue, "value", "percent", "progress"));
                payload.ProgressLabel = Text(GetObject(progressValue, "label", "text", "title", "message"));
            }
            else
            {
                payload.Progress = ParseDouble(progressValue);
                payload.ProgressLabel = Text(Get(fields, "progressLabel", "progress_label", "progressText"));
            }
        }

        payload.Details = ParseDetails(fields);
        payload.Actions = ParseActions(fields);

        payload.WaitForResponse = ParseBool(Get(fields, "wait", "waitForResponse", "block")) ?? true;
        payload.WaitTimeoutSeconds = ParseInt(Get(fields, "waitTimeout", "wait_timeout", "responseTimeout", "replyTimeout")) ?? 300;

        ApplyInteractionMode(fields, payload, request.Path);

        // Fallback: an unrecognized JSON object still shows something useful.
        if (string.IsNullOrWhiteSpace(payload.Message) && string.IsNullOrWhiteSpace(payload.Markdown) &&
            string.IsNullOrWhiteSpace(payload.Html) && string.IsNullOrWhiteSpace(payload.RawJson) &&
            string.IsNullOrWhiteSpace(payload.Code) && payload.Details.Count == 0)
        {
            payload.Message = "You have a new notification.";
        }

        return payload;
    }

    private static void ApplyInteractionMode(Dictionary<string, JsonElement> fields, NotificationPayload payload, string path)
    {
        var mode = Text(Get(fields, "mode", "interaction", "intent"))?.Trim().ToLowerInvariant() ?? "";
        var commandElement = Get(fields, "command", "cmd", "exec", "run");
        var quickReplyElement = Get(fields, "options", "quickReplies", "quick_replies", "replies", "choices");
        var replyUrlElement = Get(fields, "replyUrl", "reply_url", "callback", "callbackUrl", "webhook", "respondTo");
        var replyFlag = ParseBool(Get(fields, "reply", "replyEnabled", "expectReply", "ask", "needsReply"));
        var pathLower = path.ToLowerInvariant();
        var shellPath = pathLower is "/shell" or "/exec" or "/run" or "/admin" or "/command";
        var replyPath = pathLower is "/reply" or "/ask" or "/question" or "/prompt" or "/interact" or "/quickreply";

        if (mode is "shell" or "admin" or "exec" or "command" || shellPath || !commandElement.ValueKind.IsNullish())
        {
            payload.Mode = InteractionMode.Shell;
            payload.Kind = NotificationKind.Shell;
            payload.ShellName = Text(Get(fields, "shell", "shellType", "interpreter")) ?? "cmd";
            payload.Executable = Text(Get(fields, "executable", "exe", "program", "binary"));
            payload.Arguments = ParseStringList(Get(fields, "arguments", "args"));
            payload.Command = commandElement.ValueKind == JsonValueKind.Array
                ? string.Join(" ", commandElement.EnumerateArray().Select(Text).Where(t => !string.IsNullOrWhiteSpace(t)))
                : Text(commandElement) ?? string.Join(" ", payload.Arguments);
            payload.WorkingDirectory = Text(Get(fields, "workingDirectory", "cwd", "workdir"));
            payload.ExecutionTimeoutSeconds = ParseInt(Get(fields, "executionTimeout", "commandTimeout", "runTimeout",
                                                          "executionTimeoutSeconds")) ?? 120;
            payload.Elevated = ParseBool(Get(fields, "elevated", "runAsAdmin", "admin", "asAdmin")) ?? true;
            payload.Reason = Text(Get(fields, "reason", "why", "purpose")) ?? payload.Message;
            payload.Risk = Text(Get(fields, "risk", "danger"));

            if (payload.Title is "Notification" or "Notification from Muse") payload.Title = "Administrator approval needed";
            if (string.IsNullOrWhiteSpace(payload.Message) && string.IsNullOrWhiteSpace(payload.Markdown))
                payload.Message = $"Muse is requesting permission to run a command with {(payload.Elevated ? "administrator" : "current user")} rights.\n\nOnly approve if you trust this request.";
            payload.Category ??= "Admin command";
            payload.Source ??= "Muse.ai";
            payload.Details.Insert(0, new("Privilege", payload.Elevated ? "Administrator" : "Current user"));
            payload.Details.Insert(1, new("Shell", payload.ShellName));
            if (!string.IsNullOrWhiteSpace(payload.Risk)) payload.Details.Insert(2, new("Risk", payload.Risk));

            payload.Actions = new List<NotificationAction>
            {
                new() { Label = "Approve & run", IsApprove = true, Primary = true },
                new() { Label = "Deny", IsDeny = true }
            };
            payload.Dismissible = false;
            payload.Duration = null;
            payload.PlaySound = true;
        }
        else if (mode is "reply" or "ask" or "question" or "interact" || replyPath || replyFlag == true ||
                 !replyUrlElement.ValueKind.IsNullish() || !quickReplyElement.ValueKind.IsNullish())
        {
            payload.Mode = InteractionMode.Reply;
            if (payload.Kind == NotificationKind.Info) payload.Kind = NotificationKind.Question;
            payload.ReplyUrl = Text(replyUrlElement);
            payload.ReplyHeaders = ParseStringDictionary(Get(fields, "replyHeaders", "callbackHeaders", "headers"));
            payload.AllowCustomReply = ParseBool(Get(fields, "allowCustomReply", "customReply", "allowReply")) ?? true;
            payload.ReplyPlaceholder = Text(Get(fields, "replyPlaceholder", "placeholder", "replyHint")) ?? "Type your reply…";
            payload.ReplyButtonText = Text(Get(fields, "replyButtonText", "sendLabel", "submitLabel")) ?? "Send";

            var options = ParseReplyActions(quickReplyElement);
            if (options.Count > 0) payload.Actions = options;
            else foreach (var action in payload.Actions) action.ReplyValue ??= action.CopyText ?? action.Label;

            if (payload.Title is "Notification" or "Notification from Muse") payload.Title = "Quick reply needed";
            if (string.IsNullOrWhiteSpace(payload.Message) && string.IsNullOrWhiteSpace(payload.Markdown))
                payload.Message = "Choose a reply or type your own.";
            payload.Dismissible = false;
            payload.Duration = null;
        }
    }

    /// <summary>
    /// Reads quick reply options from any JSON object. Used both by /reply and
    /// by /chat so Muse can replace the buttons after each message.
    /// </summary>
    public static List<NotificationAction> ParseQuickReplies(JsonElement root) =>
        ParseReplyActions(GetObject(root, "options", "quickReplies", "quickReplyOptions", "nextOptions"));

    private static List<NotificationAction> ParseReplyActions(JsonElement element)
    {
        var result = new List<NotificationAction>();
        if (element.ValueKind.IsNullish()) return result;

        void Add(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(label)) return;
            result.Add(new NotificationAction { Label = label, ReplyValue = value ?? label, Primary = result.Count == 0 });
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var label = Text(GetObject(item, "label", "text", "title", "name")) ?? "Reply";
                    var value = Text(GetObject(item, "value", "reply", "id", "key", "data")) ?? label;
                    Add(label, value);
                }
                else Add(Text(item) ?? "", null);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject()) Add(PrettyKey(prop.Name), Text(prop.Value) ?? prop.Name);
        }
        // Keep a generous sanity bound here. The popup applies the real,
        // screen-size-adaptive limit (see QuickReplyLimits) when it renders.
        return result.Take(24).ToList();
    }

    private static List<string> ParseStringList(JsonElement element)
    {
        if (element.ValueKind.IsNullish()) return new();
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Select(Text).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
        var single = Text(element);
        return string.IsNullOrWhiteSpace(single) ? new() : new() { single };
    }

    private static Dictionary<string, string> ParseStringDictionary(JsonElement element)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in element.EnumerateObject())
            result[prop.Name] = Text(prop.Value) ?? "";
        return result;
    }

    private static int? ParseInt(JsonElement element)
    {
        if (element.ValueKind.IsNullish()) return null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number)) return number;
        return int.TryParse(Text(element), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static JsonElement ParseRoot(HttpRequestData request)
    {
        var body = request.Body?.Trim() ?? "";
        var contentType = request.ContentType.ToLowerInvariant();

        if (body.StartsWith('{') || body.StartsWith('[') || body.StartsWith('"'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.Clone();
            }
            catch { /* fall through to text */ }
        }

        if (contentType.Contains("x-www-form-urlencoded") || (request.Method == "GET" && request.Query.Count > 0))
        {
            var pairs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (contentType.Contains("x-www-form-urlencoded"))
                foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    var key = WebUtility.UrlDecode(eq < 0 ? pair : pair[..eq]);
                    var value = WebUtility.UrlDecode(eq < 0 ? "" : pair[(eq + 1)..]);
                    if (!string.IsNullOrEmpty(key)) pairs[key] = value;
                }
            foreach (var pair in request.Query)
            {
                if (pair.Key.Equals("token", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("key", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("auth", StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.Equals("apikey", StringComparison.OrdinalIgnoreCase)) continue;
                pairs[pair.Key] = pair.Value;
            }
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(pairs));
            return doc.RootElement.Clone();
        }

        using var textDoc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        return textDoc.RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Flatten(JsonElement root)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject()) map[prop.Name] = prop.Value;
            if (map.TryGetValue("data", out var nested) && nested.ValueKind == JsonValueKind.Object)
                foreach (var prop in nested.EnumerateObject())
                    if (!map.ContainsKey(prop.Name)) map[prop.Name] = prop.Value;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            var parts = root.EnumerateArray().Select(Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
            map["message"] = JsonSerializer.SerializeToElement(string.Join("\n", parts));
        }
        else
        {
            map["message"] = JsonSerializer.SerializeToElement(Text(root) ?? "");
        }
        return map;
    }

    private static JsonElement Get(Dictionary<string, JsonElement> map, params string[] names)
    {
        foreach (var name in names)
            if (map.TryGetValue(name, out var value) && !value.ValueKind.IsNullish())
                return value;
        return default;
    }

    private static JsonElement GetObject(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        foreach (var name in names)
            foreach (var prop in element.EnumerateObject())
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) && !prop.Value.ValueKind.IsNullish())
                    return prop.Value;
        return default;
    }

    private static bool IsNullish(this JsonValueKind kind) => kind is JsonValueKind.Undefined or JsonValueKind.Null;

    private static string? Text(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => el.GetRawText(),
        JsonValueKind.Object or JsonValueKind.Array => JsonSerializer.Serialize(el, PrettyJson),
        _ => null
    };

    private static bool? ParseBool(JsonElement el)
    {
        if (el.ValueKind.IsNullish()) return null;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.Number) return ParseDouble(el) != 0;
        var value = Text(el)?.Trim().ToLowerInvariant();
        return value switch
        {
            "1" or "true" or "yes" or "y" or "on" or "sticky" or "never" => true,
            "0" or "false" or "no" or "n" or "off" or "none" => false,
            _ => null
        };
    }

    private static double? ParseDouble(JsonElement el)
    {
        if (el.ValueKind.IsNullish()) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var number)) return number;
        var value = Text(el);
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static TimeSpan? ParseDuration(JsonElement el)
    {
        if (el.ValueKind.IsNullish()) return null;
        if (el.ValueKind == JsonValueKind.Number)
        {
            var seconds = el.GetDouble();
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
        }
        if (el.ValueKind == JsonValueKind.True) return null;
        var value = Text(el)?.Trim().ToLowerInvariant() ?? "";
        if (value is "" or "0" or "never" or "sticky" or "forever" or "until dismissed" or "persistent" or "none")
            return null;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedSeconds))
            return parsedSeconds > 0 ? TimeSpan.FromSeconds(parsedSeconds) : null;
        var match = DurationPattern.Match(value);
        if (!match.Success) return null;
        var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups[2].Value.ToLowerInvariant();
        var multiplier = unit.StartsWith("ms") || unit.StartsWith("msec") || unit.StartsWith("milli") ? 0.001
            : unit.StartsWith("m") ? 60
            : unit.StartsWith("h") ? 3600
            : 1;
        var result = TimeSpan.FromSeconds(amount * multiplier);
        return result > TimeSpan.Zero ? result : null;
    }

    private static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        return Regex.IsMatch(hex, "^[0-9a-fA-F]{6}$") ? "#" + hex.ToUpperInvariant() : null;
    }

    private static string DefaultTitle(NotificationKind kind) => kind switch
    {
        NotificationKind.Success => "All done",
        NotificationKind.Warning => "Heads up",
        NotificationKind.Error => "Attention needed",
        NotificationKind.Question => "Decision needed",
        NotificationKind.Captcha => "Captcha needs you",
        NotificationKind.Verification => "Verification code",
        NotificationKind.Reminder => "Reminder",
        _ => "Notification from Muse"
    };

    private static NotificationKind ParseKind(JsonElement el, string path)
    {
        var value = Text(el)?.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-') ?? "";
        if (value.Length == 0)
        {
            if (path.Contains("captcha", StringComparison.OrdinalIgnoreCase)) return NotificationKind.Captcha;
            if (path.Contains("verif", StringComparison.OrdinalIgnoreCase) || path.Contains("/otp", StringComparison.OrdinalIgnoreCase)) return NotificationKind.Verification;
            if (path.Contains("code", StringComparison.OrdinalIgnoreCase)) return NotificationKind.Verification;
            if (path.Contains("error", StringComparison.OrdinalIgnoreCase)) return NotificationKind.Error;
            if (path.Contains("warn", StringComparison.OrdinalIgnoreCase)) return NotificationKind.Warning;
            if (path.Contains("success", StringComparison.OrdinalIgnoreCase)) return NotificationKind.Success;
            if (path.Contains("confirm", StringComparison.OrdinalIgnoreCase) || path.Contains("approv", StringComparison.OrdinalIgnoreCase)) return NotificationKind.Question;
            return NotificationKind.Info;
        }
        if (value is "error" or "err" or "danger" or "fail" or "failed" or "failure" or "critical" or "fatal" or "alert") return NotificationKind.Error;
        if (value is "warn" or "warning" or "caution") return NotificationKind.Warning;
        if (value is "success" or "ok" or "done" or "passed" or "complete" or "completed") return NotificationKind.Success;
        if (value is "question" or "confirm" or "confirmation" or "approval" or "decision" or "choice" or "ask") return NotificationKind.Question;
        if (value is "captcha" or "challenge" or "human" or "robot" or "recaptcha" or "hcaptcha") return NotificationKind.Captcha;
        if (value is "verification" or "verify" or "otp" or "code" or "sms" or "2fa" or "mfa" or "auth" or "login" or "security") return NotificationKind.Verification;
        if (value is "reminder" or "todo" or "task" or "follow-up" or "followup") return NotificationKind.Reminder;
        if (value is "shell" or "admin" or "command" or "exec" or "elevated") return NotificationKind.Shell;
        return NotificationKind.Info;
    }

    private static List<KeyValuePair<string, string>> ParseDetails(Dictionary<string, JsonElement> fields)
    {
        var result = new List<KeyValuePair<string, string>>();
        var element = Get(fields, "details", "fields", "meta", "metadata", "values", "keyValues", "keyValues");
        if (element.ValueKind.IsNullish()) return result;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var value = prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? JsonSerializer.Serialize(prop.Value)
                    : Text(prop.Value) ?? "";
                result.Add(new(PrettyKey(prop.Name), value));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var key = Text(GetObject(item, "label", "key", "name", "title", "field")) ?? "";
                    var value = Text(GetObject(item, "value", "text", "content", "data")) ?? "";
                    result.Add(new(PrettyKey(key), value));
                }
                else
                {
                    var text = Text(item);
                    if (!string.IsNullOrWhiteSpace(text)) result.Add(new("", text));
                }
            }
        }
        return result.Take(12).ToList();
    }

    private static List<NotificationAction> ParseActions(Dictionary<string, JsonElement> fields)
    {
        var result = new List<NotificationAction>();
        var element = Get(fields, "actions", "buttons", "options", "choices");
        if (!element.ValueKind.IsNullish())
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    var value = Text(prop.Value) ?? "";
                    result.Add(MakeAction(PrettyKey(prop.Name), value, value));
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var label = Text(GetObject(item, "label", "title", "name", "text", "buttonText")) ?? "Open";
                        var url = Text(GetObject(item, "url", "href", "link", "target", "action"));
                        var copy = Text(GetObject(item, "copy", "copyText", "copyValue", "value", "clipboard"));
                        var urlOrCopy = url ?? copy;
                        if (url is not null && copy is not null && !string.Equals(url, copy, StringComparison.OrdinalIgnoreCase))
                            urlOrCopy = url;
                        result.Add(new NotificationAction
                        {
                            Label = label,
                            Url = url,
                            CopyText = url is not null ? null : copy,
                            Primary = ParseBool(GetObject(item, "primary", "main", "default")) == true
                        });
                    }
                    else
                    {
                        var text = Text(item) ?? "";
                        var pipe = text.IndexOf('|');
                        if (pipe > 0)
                            result.Add(MakeAction(text[..pipe].Trim(), text[(pipe + 1)..].Trim()));
                        else if (text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            result.Add(MakeAction("Open link", text));
                        else if (!string.IsNullOrWhiteSpace(text))
                            result.Add(new NotificationAction { Label = text });
                    }
                }
            }
        }

        var urlField = Text(Get(fields, "url", "link", "actionUrl", "action_url", "href", "openUrl"));
        if (!string.IsNullOrWhiteSpace(urlField) && result.All(a => a.Url != urlField))
            result.Add(new NotificationAction { Label = Text(Get(fields, "buttonText", "buttonLabel", "linkText")) ?? "Open link", Url = urlField, Primary = result.Count == 0 });

        if (result.Count == 0)
        {
            var copyField = Text(Get(fields, "copy", "copyText", "copyValue", "clipboard"));
            if (!string.IsNullOrWhiteSpace(copyField))
                result.Add(new NotificationAction { Label = "Copy", CopyText = copyField, Primary = true });
        }

        if (result.Count > 0)
        {
            var hasPrimary = false;
            foreach (var item in result)
                if (item.Primary) { hasPrimary = true; break; }
            if (!hasPrimary) result[0].Primary = true;
        }
        return result.Take(4).ToList();
    }

    private static NotificationAction MakeAction(string label, string value, string? copy = null)
    {
        var isUrl = Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        return new NotificationAction
        {
            Label = string.IsNullOrWhiteSpace(label) ? (isUrl ? "Open link" : "Copy") : label,
            Url = isUrl ? value : null,
            CopyText = isUrl ? null : (copy ?? value)
        };
    }

    private static string PrettyKey(string key)
    {
        var text = Regex.Replace(key.Trim(), "(?<=[a-z0-9])(?=[A-Z])", " ").Replace('_', ' ').Replace('-', ' ');
        return string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
