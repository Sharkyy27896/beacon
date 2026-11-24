using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Beacon.Models;

namespace Beacon.Services;

/// <summary>
/// Tracks reply / approval prompts. A popup reports the user's decision here;
/// the HTTP endpoint either waits for it or exposes it for polling, and an
/// optional webhook receives the answer so Muse can be notified immediately.
/// </summary>
public sealed class InteractionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly HttpClient WebhookClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly NotificationManager _manager;
    private readonly ConcurrentDictionary<string, PendingInteraction> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InteractionResult> _results = new(StringComparer.OrdinalIgnoreCase);

    public InteractionService(NotificationManager manager) => _manager = manager;

    public async Task<InteractionResult> CreateAsync(
        NotificationPayload payload,
        Func<InteractionResult, Task<InteractionResult>>? onDecision,
        bool wait,
        TimeSpan waitTimeout)
    {
        var id = payload.Id;
        _results[id] = new InteractionResult { Id = id, Kind = "pending" };

        var completion = new TaskCompletionSource<InteractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new PendingInteraction
        {
            Id = id,
            Payload = payload,
            Completion = completion,
            OnDecision = onDecision
        };

        _manager.Enqueue(payload);

        if (!wait)
            return new InteractionResult { Id = id, Kind = "pending" };

        var finished = await Task.WhenAny(completion.Task, Task.Delay(waitTimeout)).ConfigureAwait(false);
        if (finished != completion.Task)
        {
            var timeout = new InteractionResult { Id = id, Kind = "timeout", Error = "No response before the timeout." };
            await CompleteCoreAsync(id, timeout, runDecisionHook: false).ConfigureAwait(false);
            return timeout;
        }
        return await completion.Task.ConfigureAwait(false);
    }

    public Task CompleteAsync(string id, InteractionResult decision) => CompleteCoreAsync(id, decision, runDecisionHook: true);

    /// <summary>
    /// Delivers an additional reply after the original interaction has already
    /// been resolved. The webhook still fires so Muse receives every message in
    /// an ongoing chat. Each follow-up gets a fresh id because an agent that
    /// already handled the original request would otherwise discard the second
    /// callback as a duplicate.
    /// </summary>
    public Task ReportFollowUpAsync(NotificationPayload payload, InteractionResult result, int sequence)
    {
        result.Id = $"{payload.Id}-r{sequence}";
        result.At = DateTime.Now;
        _results[result.Id] = result;
        return SendWebhookAsync(payload, result, payload.Id, sequence);
    }

    private async Task CompleteCoreAsync(string id, InteractionResult decision, bool runDecisionHook)
    {
        if (!_pending.TryGetValue(id, out var pending)) return;
        if (Interlocked.Exchange(ref pending.CompletedFlag, 1) != 0) return;

        InteractionResult final;
        try
        {
            final = runDecisionHook && pending.OnDecision is not null
                ? await pending.OnDecision(decision).ConfigureAwait(false)
                : decision;
        }
        catch (Exception ex)
        {
            final = new InteractionResult { Id = id, Kind = "failed", Error = ex.Message };
        }

        final.Id = id;
        final.At = DateTime.Now;
        _results[id] = final;
        _pending.TryRemove(id, out _);
        pending.Completion.TrySetResult(final);
        _ = SendWebhookAsync(pending.Payload, final);
    }

    public InteractionResult GetResult(string id) =>
        _results.TryGetValue(id, out var result)
            ? result
            : new InteractionResult { Id = id, Kind = "not_found", Error = "Unknown or expired request id." };

    private static async Task SendWebhookAsync(
        NotificationPayload payload,
        InteractionResult result,
        string? conversationId = null,
        int? sequence = null)
    {
        if (string.IsNullOrWhiteSpace(payload.ReplyUrl) ||
            !Uri.TryCreate(payload.ReplyUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            var json = JsonSerializer.Serialize(new
            {
                source = "Beacon",
                id = result.Id,
                requestId = result.Id,
                conversationId = conversationId,
                sequence = sequence,
                kind = result.Kind,
                status = result.Kind,
                value = result.Value,
                label = result.Label,
                stdout = result.StdOut,
                stderr = result.StdErr,
                exitCode = result.ExitCode,
                durationMs = result.DurationMs,
                timedOut = result.TimedOut,
                error = result.Error,
                at = result.At
            }, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            foreach (var header in payload.ReplyHeaders)
            {
                if (string.IsNullOrWhiteSpace(header.Key)) continue;
                request.Headers.TryAddWithoutValidation(header.Key.Trim(), header.Value);
            }
            if (App.Instance is not null && !string.IsNullOrWhiteSpace(App.Instance.Settings.Token))
                request.Headers.TryAddWithoutValidation("X-Beacon-Token", App.Instance.Settings.Token);

            using var response = await WebhookClient.SendAsync(request).ConfigureAwait(false);
        }
        catch { /* webhook delivery is best-effort */ }
    }
}
