namespace Beacon.Models;

public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error,
    Question,
    Captcha,
    Verification,
    Reminder,
    Shell
}

public sealed class NotificationAction
{
    public string Label { get; set; } = "OK";
    public string? Url { get; set; }
    public string? CopyText { get; set; }
    public string? ReplyValue { get; set; }
    public bool Primary { get; set; }
    public bool IsApprove { get; set; }
    public bool IsDeny { get; set; }
}

public sealed class NotificationPayload
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "Notification";
    public string Message { get; set; } = "";
    public string? Markdown { get; set; }
    public string? Html { get; set; }
    public string? RawJson { get; set; }
    public string? Code { get; set; }
    public string? CodeLabel { get; set; }
    public NotificationKind Kind { get; set; } = NotificationKind.Info;
    public string? Category { get; set; }
    public string? Source { get; set; }
    public string? Emoji { get; set; }
    public string? AccentHex { get; set; }
    public string? Footer { get; set; }
    public TimeSpan? Duration { get; set; }
    public bool PlaySound { get; set; } = true;
    public bool Dismissible { get; set; } = true;
    public bool Topmost { get; set; } = true;
    public string? ImageSource { get; set; }
    public double? Progress { get; set; }
    public string? ProgressLabel { get; set; }
    public string? SoundName { get; set; }
    public List<NotificationAction> Actions { get; set; } = new();
    public List<KeyValuePair<string, string>> Details { get; set; } = new();

    // Interaction / admin shell fields
    public InteractionMode Mode { get; set; } = InteractionMode.None;
    public string? Command { get; set; }
    public string? ShellName { get; set; } = "cmd";
    public string? Executable { get; set; }
    public List<string> Arguments { get; set; } = new();
    public string? WorkingDirectory { get; set; }
    public int ExecutionTimeoutSeconds { get; set; } = 120;
    public bool Elevated { get; set; } = true;
    public string? Reason { get; set; }
    public string? Risk { get; set; }
    public string? ReplyUrl { get; set; }
    public Dictionary<string, string> ReplyHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowCustomReply { get; set; } = true;
    public string? ReplyPlaceholder { get; set; }
    public string? ReplyButtonText { get; set; }
    public bool WaitForResponse { get; set; } = true;
    public int WaitTimeoutSeconds { get; set; } = 300;

    public bool IsSticky => Duration is null;

    /// <summary>Copy used when the tray re-opens a dismissed notification.</summary>
    public NotificationPayload Clone()
    {
        var copy = (NotificationPayload)MemberwiseClone();
        copy.Actions = Actions.Select(action => new NotificationAction
        {
            Label = action.Label,
            Url = action.Url,
            CopyText = action.CopyText,
            ReplyValue = action.ReplyValue,
            Primary = action.Primary,
            IsApprove = action.IsApprove,
            IsDeny = action.IsDeny
        }).ToList();
        copy.Details = new List<KeyValuePair<string, string>>(Details);
        copy.Arguments = new List<string>(Arguments);
        copy.ReplyHeaders = new Dictionary<string, string>(ReplyHeaders, StringComparer.OrdinalIgnoreCase);
        return copy;
    }
}
