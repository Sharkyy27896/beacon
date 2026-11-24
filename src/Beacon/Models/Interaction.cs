namespace Beacon.Models;

public enum InteractionMode
{
    None,
    Reply,
    Shell
}

public sealed class InteractionResult
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "pending";
    public string? Value { get; set; }
    public string? Label { get; set; }
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
    public int? ExitCode { get; set; }
    public long DurationMs { get; set; }
    public bool TimedOut { get; set; }
    public string? Error { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
}

public sealed class PendingInteraction
{
    public required string Id { get; init; }
    public required NotificationPayload Payload { get; init; }
    public required TaskCompletionSource<InteractionResult> Completion { get; init; }
    public Func<InteractionResult, Task<InteractionResult>>? OnDecision { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public int CompletedFlag;
}
