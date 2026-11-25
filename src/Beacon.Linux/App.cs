using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Beacon.Models;
using Beacon.Services;
using Beacon.Views;

namespace Beacon;

/// <summary>
/// Linux/Avalonia composition root. The HTTP contract, auth, parsing, queue and
/// interaction semantics mirror src/Beacon/App.xaml.cs exactly.
/// </summary>
public sealed class App : Application
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

    public event EventHandler? SettingsChanged;
    public void NotifySettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    private readonly SettingsService _settingsService = new();
    private NotificationManager _manager = null!;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;

    public App()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        Instance = this;
        var firstRun = !SettingsService.SettingsFileExists;
        Settings = _settingsService.Load();
        if (firstRun) _settingsService.Save(Settings);

        _manager = new NotificationManager();
        Interactions = new InteractionService(_manager);
        Chat = new ChatService();
        Server.RequestReceived += OnRequest;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogException(args.ExceptionObject as Exception ?? new Exception("Unhandled exception"));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException(args.Exception.Flatten());
            args.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var args = lifetime.Args?.Select(a => a.ToLowerInvariant()).ToList() ?? new List<string>();
            CreateTray();
            StartListener();

            if (args.Contains("--settings") || firstRun)
                OpenSettings();
            else if (!args.Contains("--minimized"))
                ShowStartupStatus();
        }
        else
        {
            CreateTray();
            StartListener();
        }
    }

    private void CreateTray()
    {
        using var iconStream = AssetLoader.Open(new Uri("avares://Beacon/Assets/beacon.png"));
        var icon = new WindowIcon(iconStream);

        _tray = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "Beacon · notification bridge",
            IsVisible = true,
            Command = new RelayCommand(OpenSettings),
            Menu = new NativeMenu
            {
                new NativeMenuItem("Open settings") { Command = new RelayCommand(OpenSettings) },
                new NativeMenuItem("Send test notification") { Command = new RelayCommand(SendTestNotification) },
                new NativeMenuItem("Dismiss current popup") { Command = new RelayCommand(() => _manager.ClearAll()) },
                new NativeMenuItemSeparator(),
                new NativeMenuItem("Start / stop listener") { Command = new RelayCommand(ToggleListener) },
                new NativeMenuItemSeparator(),
                new NativeMenuItem("Exit") { Command = new RelayCommand(Shutdown) }
            }
        };
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
        RefreshTrayText();
    }

    private void RefreshTrayText()
    {
        if (_tray is null) return;
        _tray.ToolTipText = Server.IsRunning
            ? $"Beacon · listening on port {Settings.Port}"
            : "Beacon · listener stopped";
    }

    private void ToggleListener()
    {
        if (Server.IsRunning) Server.Stop();
        else StartListener();
        RefreshTrayText();
    }

    private void ShowStartupStatus()
    {
        _manager.Enqueue(new NotificationPayload
        {
            Title = "Beacon is running",
            Message = $"Listening on http://{NetworkHelper.GetReachableAddress()}:{Settings.Port}/notify",
            Kind = NotificationKind.Success,
            Source = "Beacon",
            Category = "Connection check",
            Duration = TimeSpan.FromSeconds(4),
            PlaySound = false
        });
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
            _manager.Enqueue(new NotificationPayload
            {
                Title = "Beacon could not start",
                Message = $"Port {Settings.Port} may be in use. Open settings to change it. ({ex.SocketErrorCode})",
                Kind = NotificationKind.Error,
                Source = "Beacon",
                Duration = TimeSpan.FromSeconds(10),
                PlaySound = false
            });
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

    public void Shutdown()
    {
        Server.Stop();
        if (_tray is not null) _tray.IsVisible = false;
        TrayIcon.SetIcons(this, null);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.Shutdown();
        else
            Environment.Exit(0);
    }

    private Task<HttpResponse> OnRequest(HttpRequestData request)
    {
        if (request.Method == "OPTIONS")
            return Task.FromResult(new HttpResponse(204, "", "text/plain"));

        var path = request.Path.TrimEnd('/').ToLowerInvariant();
        if (path is "" or "/")
        {
            var body = string.IsNullOrWhiteSpace(request.Body)
                ? JsonSerializer.Serialize(new { message = "hello" })
                : request.Body;
            return ShowNotificationAsync(request, body);
        }

        if (path is "/notify" or "/alert" or "/message" or "/show" or "/captcha" or "/verification" or "/verify"
            or "/code" or "/otp" or "/confirm" or "/approval" or "/reminder" or "/notification" or "/push")
            return ShowNotificationAsync(request, request.Body);

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

        if (path is "/clear" or "/dismiss")
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

    private async Task<HttpResponse> HandlePayloadRequestAsync(
        HttpRequestData request, string body, InteractionMode forcedMode)
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

            var prompt = string.IsNullOrWhiteSpace(payload.Message) ? payload.Markdown ?? "" : payload.Message;
            Chat.AddMuse(prompt, payload.Id);

            if (payload.Mode == InteractionMode.None)
            {
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

    private static void LogException(Exception exception)
    {
        try
        {
            var dir = SettingsService.SettingsDirectory;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\n\n");
        }
        catch { /* ignore logging errors */ }
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) => _execute = execute;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute();
    }
}
