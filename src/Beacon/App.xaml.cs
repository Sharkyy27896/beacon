using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Beacon.Models;
using Beacon.Services;
using Beacon.Views;
using Application = System.Windows.Application;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace Beacon;

public partial class App : Application
{
    public static App Instance { get; private set; } = null!;

    public static string AppVersion { get; } =
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    public AppSettings Settings { get; private set; } = new();
    public HttpServer Server { get; } = new();
    public InteractionService Interactions { get; private set; } = null!;
    public ChatService Chat { get; private set; } = null!;
    public bool IsListening => Server.IsRunning;

    /// <summary>Raised after settings are saved so open windows can re-format.</summary>
    public event EventHandler? SettingsChanged;

    public void NotifySettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    private readonly SettingsService _settingsService = new();
    private NotificationManager _manager = null!;
    private NotifyIcon _tray = null!;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Hidden helper process used for administrator command execution.
        if (e.Args.Length >= 2 && e.Args[0] == "--run-elevated")
        {
            ElevatedRunner.RunHelper(e.Args[1]);
            Shutdown();
            return;
        }

        Instance = this;
        var firstRun = !SettingsService.SettingsFileExists;
        Settings = _settingsService.Load();
        if (firstRun) _settingsService.Save(Settings);

        _manager = new NotificationManager();
        Interactions = new InteractionService(_manager);
        Chat = new ChatService();
        DispatcherUnhandledException += OnUnhandledException;
        Server.RequestReceived += OnRequest;

        CreateTray();
        StartListener();

        var args = e.Args.Select(a => a.ToLowerInvariant()).ToList();
        if (args.Contains("--settings") || firstRun)
            OpenSettings();
        else if (!args.Contains("--minimized"))
            _tray.ShowBalloonTip(3500, "Beacon is running",
                $"Listening on http://{NetworkHelper.GetReachableAddress()}:{Settings.Port}/notify", ToolTipIcon.Info);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Server.Stop();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnExit(e);
    }

    private void CreateTray()
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/beacon.ico"))?.Stream;
        _tray = new NotifyIcon
        {
            Icon = iconStream is not null ? new Icon(iconStream) : SystemIcons.Application,
            Text = "Beacon · notification bridge",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.BackColor = Color.FromArgb(18, 26, 44);
        menu.ForeColor = Color.FromArgb(230, 240, 255);
        menu.Items.Add(new ToolStripMenuItem("Open settings", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Send test notification", null, (_, _) => SendTestNotification()));
        menu.Items.Add(new ToolStripMenuItem("Dismiss current popup", null, (_, _) => _manager.ClearAll()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Start / stop listener", null, (_, _) =>
        {
            if (Server.IsRunning) Server.Stop(); else StartListener();
            RefreshTrayText();
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Shutdown()));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => OpenSettings();
        RefreshTrayText();
    }

    private void RefreshTrayText()
    {
        if (_tray is null) return;
        _tray.Text = Server.IsRunning
            ? $"Beacon · listening on port {Settings.Port}"
            : "Beacon · listener stopped";
    }

    public void StartListener()
    {
        try
        {
            var address = Settings.BindAddress == "Loopback" ? IPAddress.Loopback : IPAddress.Any;
            Server.Start(address, Settings.Port);
            RefreshTrayText();
        }
        catch (SocketException ex)
        {
            RefreshTrayText();
            try
            {
                _tray.ShowBalloonTip(5000, "Beacon could not start",
                    $"Port {Settings.Port} may be in use. Open settings to change it. ({ex.SocketErrorCode})", ToolTipIcon.Warning);
            }
            catch { /* tray may not be ready */ }
        }
    }

    public void RestartListener() => StartListener();

    public void OpenSettings()
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
        _settingsWindow.Focus();
    }

    public void SendTestNotification()
    {
        _manager.Enqueue(new NotificationPayload
        {
            Title = "Beacon is working",
            Message = "This is a **test notification**.\n\nYou can send much richer ones over HTTP — codes, captchas, links, images and more.",
            Kind = NotificationKind.Success,
            Source = "Beacon",
            Category = "Connection check",
            Duration = TimeSpan.FromSeconds(15),
            Details = new()
            {
                new("Status", "Listener active"),
                new("Tip", "Open settings from the tray to copy your endpoint URL")
            }
        });
    }

    private Task<HttpResponse> OnRequest(HttpRequestData request)
    {
        if (request.Method == "OPTIONS")
            return Task.FromResult(new HttpResponse(204, "", "text/plain"));

        var path = request.Path.TrimEnd('/').ToLowerInvariant();
        if (path is "" or "/")
        {
            var body = string.IsNullOrWhiteSpace(request.Body) ? JsonSerializer.Serialize(new { message = "hello" }) : request.Body;
            return ShowNotificationAsync(request, body);
        }

        if (path is "/notify" or "/alert" or "/message" or "/show" or "/captcha" or "/verification" or "/verify"
            or "/code" or "/otp" or "/confirm" or "/approval" or "/reminder" or "/notification" or "/push")
        {
            return ShowNotificationAsync(request, request.Body);
        }

        if (path is "/reply" or "/ask" or "/question" or "/prompt" or "/interact" or "/quickreply")
            return ShowInteractionAsync(request, InteractionMode.Reply);

        if (path is "/shell" or "/exec" or "/run" or "/admin" or "/command")
        {
            if (!Settings.AllowShell)
                return Task.FromResult(HttpResponse.Json(403, new
                {
                    ok = false,
                    error = "Shell requests are disabled in Beacon settings."
                }));
            return ShowInteractionAsync(request, InteractionMode.Shell);
        }

        if (path is "/chat" or "/chat/stream" or "/reply/stream" or "/muse/reply")
            return ChatStreamAsync(request);

        if (path.StartsWith("/result/", StringComparison.Ordinal) ||
            path.StartsWith("/interaction/", StringComparison.Ordinal) ||
            path.StartsWith("/reply/result/", StringComparison.Ordinal) ||
            path.StartsWith("/shell/result/", StringComparison.Ordinal))
            return GetInteractionResult(request, path);

        if (path is "/health" or "/ping" or "/status")
        {
            return Task.FromResult(HttpResponse.Json(200, new
            {
                ok = true,
                service = "Beacon",
                version = AppVersion,
                listening = Server.IsRunning,
                address = $"{(Settings.BindAddress == "Loopback" ? "127.0.0.1" : "0.0.0.0")}:{Settings.Port}",
                pending = _manager.PendingCount,
                now = DateTime.Now
            }));
        }

        if (path == "/clear" || path == "/dismiss")
        {
            if (!IsAuthorized(request)) return Task.FromResult(Unauthorized());
            _manager.ClearAll();
            return Task.FromResult(HttpResponse.Json(200, new { ok = true, cleared = true }));
        }

        return Task.FromResult(HttpResponse.Json(404, new
        {
            ok = false,
            error = "Unknown route.",
            use = "POST /notify (or /captcha, /verification, /alert) with a JSON body. GET /health for status."
        }));
    }

    private Task<HttpResponse> ShowNotificationAsync(HttpRequestData request, string body) =>
        HandlePayloadRequestAsync(request, body, InteractionMode.None);

    private Task<HttpResponse> ShowInteractionAsync(HttpRequestData request, InteractionMode mode) =>
        HandlePayloadRequestAsync(request, request.Body, mode);

    private async Task<HttpResponse> HandlePayloadRequestAsync(HttpRequestData request, string body, InteractionMode forcedMode)
    {
        if (!IsAuthorized(request)) return Unauthorized();
        if (request.Method is not ("GET" or "POST" or "PUT"))
            return HttpResponse.Json(405, new { ok = false, error = "Use GET, POST or PUT." });

        try
        {
            var enriched = body;
            if (request.Method == "GET" && string.IsNullOrWhiteSpace(body) && request.Query.Count > 0)
                enriched = JsonSerializer.Serialize(request.Query);

            var payload = PayloadParser.Parse(request with { Body = enriched }, Settings);
            if (forcedMode != InteractionMode.None) payload.Mode = forcedMode;
            if (payload.Mode == InteractionMode.Shell) payload.Kind = NotificationKind.Shell;
            if (payload.Mode == InteractionMode.Reply && payload.Kind == NotificationKind.Info)
                payload.Kind = NotificationKind.Question;

            // Every popup now has a chat card, so every alert type seeds the
            // transcript with the message that triggered it.
            var prompt = string.IsNullOrWhiteSpace(payload.Message) ? payload.Markdown ?? "" : payload.Message;
            Chat.AddMuse(prompt, payload.Id);

            if (payload.Mode == InteractionMode.None)
            {
                // Still register the request so /result/{id} and follow-up
                // replies work for plain notifications too. wait:false keeps
                // the normal non-blocking /notify response.
                _ = Interactions.CreateAsync(payload, null, false,
                    TimeSpan.FromSeconds(Math.Max(1, Math.Min(payload.WaitTimeoutSeconds, 3600))));
                return HttpResponse.Json(200, new
                {
                    ok = true,
                    id = payload.Id,
                    status = "shown",
                    title = payload.Title,
                    kind = payload.Kind.ToString().ToLowerInvariant()
                });
            }

            Func<InteractionResult, Task<InteractionResult>>? onDecision = null;
            if (payload.Mode == InteractionMode.Shell)
                onDecision = async decision =>
                {
                    if (!string.Equals(decision.Kind, "approved", StringComparison.OrdinalIgnoreCase))
                        return decision;
                    var execution = await ElevatedRunner.ExecuteAsync(payload).ConfigureAwait(false);
                    return new InteractionResult
                    {
                        Kind = execution.Kind,
                        Value = payload.Command,
                        StdOut = execution.StdOut,
                        StdErr = execution.StdErr,
                        ExitCode = execution.ExitCode,
                        DurationMs = execution.DurationMs,
                        TimedOut = execution.TimedOut,
                        Error = execution.Error
                    };
                };

            var result = await Interactions.CreateAsync(
                payload,
                onDecision,
                payload.WaitForResponse,
                TimeSpan.FromSeconds(Math.Max(1, Math.Min(payload.WaitTimeoutSeconds, 3600)))).ConfigureAwait(false);

            return HttpResponse.Json(200, new
            {
                ok = true,
                id = payload.Id,
                status = result.Kind,
                poll = $"/result/{payload.Id}",
                mode = payload.Mode == InteractionMode.Shell ? "shell" : "reply",
                result
            });
        }
        catch (Exception ex)
        {
            return HttpResponse.Json(400, new { ok = false, error = ex.Message });
        }
    }

    private Task<HttpResponse> GetInteractionResult(HttpRequestData request, string path)
    {
        if (!IsAuthorized(request)) return Task.FromResult(Unauthorized());
        var id = path[(path.LastIndexOf('/') + 1)..];
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult(HttpResponse.Json(400, new { ok = false, error = "Missing request id." }));
        return Task.FromResult(HttpResponse.Json(200, new { ok = true, result = Interactions.GetResult(id) }));
    }

    /// <summary>
    /// Muse pushes reply text here. Use "delta" for streamed chunks, or "text"
    /// for a complete reply. Plain text is treated as a chunk when ?stream=true.
    /// </summary>
    private Task<HttpResponse> ChatStreamAsync(HttpRequestData request)
    {
        if (!IsAuthorized(request)) return Task.FromResult(Unauthorized());
        if (request.Method is not ("POST" or "PUT"))
            return Task.FromResult(HttpResponse.Json(405, new { ok = false, error = "Use POST or PUT." }));

        try
        {
            string? id = null;
            string? delta = null;
            string? text = null;
            List<NotificationAction>? quickReplies = null;
            bool done;
            var body = request.Body.Trim();

            if (body.StartsWith('{'))
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                id = JsonString(root, "id", "requestId", "replyId");
                delta = JsonString(root, "delta", "chunk", "partial", "textDelta");
                text = JsonString(root, "text", "message", "content", "reply");
                done = JsonBool(root, "done", "complete", "final")
                       ?? (text is not null && delta is null);

                // Muse can send the next set of buttons with any chat message.
                // An explicit empty array clears the section; omitting the key
                // leaves the current buttons untouched.
                if (HasQuickReplyKey(root)) quickReplies = PayloadParser.ParseQuickReplies(root);
            }
            else
            {
                var streaming = request.Query.GetValueOrDefault("stream") is "1" or "true" or "yes";
                if (streaming) delta = body; else text = body;
                done = !streaming;
                id = request.Query.GetValueOrDefault("id");
            }

            if (delta is null && text is null)
                return Task.FromResult(HttpResponse.Json(400, new
                {
                    ok = false,
                    error = "Send JSON with 'delta' (streamed chunk) or 'text' (complete reply), or a plain-text body."
                }));

            Chat.Stream(id, delta, text, done);
            if (quickReplies is not null) _manager.UpdateQuickReplies(id, quickReplies);
            return Task.FromResult(HttpResponse.Json(200, new
            {
                ok = true,
                id = id,
                streaming = !done,
                characters = (delta ?? text ?? "").Length
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HttpResponse.Json(400, new { ok = false, error = ex.Message }));
        }
    }

    private static bool HasQuickReplyKey(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in root.EnumerateObject())
            if (new[] { "options", "quickReplies", "quickReplyOptions", "nextOptions" }
                .Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string? JsonString(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
            if (property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return property.Value.GetRawText();
        }
        return null;
    }

    private static bool? JsonBool(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (property.Value.ValueKind == JsonValueKind.True) return true;
            if (property.Value.ValueKind == JsonValueKind.False) return false;
            var value = property.Value.GetString()?.Trim().ToLowerInvariant();
            if (value is "1" or "true" or "yes") return true;
            if (value is "0" or "false" or "no") return false;
        }
        return null;
    }

    private static HttpResponse Unauthorized() => HttpResponse.Json(401, new
    {
        ok = false,
        error = "Missing or invalid token. Send Authorization: Bearer <token>, X-Auth-Token, or ?token=<token>."
    });

    private bool IsAuthorized(HttpRequestData request)
    {
        if (!Settings.RequireAuth) return true;
        var authorization = request.Headers.GetValueOrDefault("Authorization")?.Trim() ?? "";
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            authorization = authorization[7..].Trim();

        string?[] candidates =
        {
            authorization,
            request.Headers.GetValueOrDefault("X-Auth-Token")?.Trim(),
            request.Headers.GetValueOrDefault("X-API-Key")?.Trim(),
            request.Query.GetValueOrDefault("token"),
            request.Query.GetValueOrDefault("key"),
            request.Query.GetValueOrDefault("auth"),
            request.Query.GetValueOrDefault("apikey")
        };
        return candidates.Any(candidate => FixedTimeEquals(candidate, Settings.Token));
    }

    private static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);
        if (left.Length != right.Length) return false;
        var difference = 0;
        for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
        return difference == 0;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var dir = SettingsService.SettingsDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch { /* ignore logging errors */ }
        e.Handled = true;
    }
}
