using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Beacon.Models;

namespace Beacon.Services;

/// <summary>
/// Tracks reply / approval prompts. A popup reports the user's decision here;
/// the HTTP endpoint either waits for it or exposes it for polling, and an
/// optional webhook receives the answer so Muse can be notified immediately.
/// </summary>
public sealed class InteractionService
{
    private const int MaxResults = 1000;
    private static readonly string[] ReservedHeaders =
    {
        "X-Beacon-Token", "Authorization", "Host", "Content-Type", "Content-Length",
        "Connection", "Transfer-Encoding", "Expect"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // Caller-supplied webhook URLs must not be able to relay Beacon's secret to
    // a different host via a redirect.
    private static readonly HttpClient WebhookClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    })
    { Timeout = TimeSpan.FromSeconds(10) };

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
        StoreResult(id, new InteractionResult { Id = id, Kind = "pending" });

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
            // Close a popup whose decision window has elapsed; a late click
            // must not appear actionable after the request has timed out.
            _manager.Expire(id);
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
        StoreResult(result.Id, result);
        return SendWebhookAsync(payload, result, payload.Id, sequence);
    }

    private async Task CompleteCoreAsync(string id, InteractionResult decision, bool runDecisionHook)
    {
        if (!_pending.TryGetValue(id, out var pending))
        {
            // A decision after timeout used to vanish silently. Surface it in
            // /result without overwriting a real earlier decision.
            if (_results.TryGetValue(id, out var prior) &&
                !string.Equals(prior.Kind, "pending", StringComparison.OrdinalIgnoreCase)) return;
            StoreResult(id, new InteractionResult
            {
                Id = id,
                Kind = "late_decision",
                Value = decision.Value,
                Label = decision.Label,
                At = DateTime.Now,
                Error = "The request had already timed out."
            });
            return;
        }

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
        StoreResult(id, final);
        _pending.TryRemove(id, out _);
        pending.Completion.TrySetResult(final);
        _ = SendWebhookAsync(pending.Payload, final);
    }

    private void StoreResult(string id, InteractionResult result)
    {
        _results[id] = result;
        if (_results.Count <= MaxResults) return;

        foreach (var pair in _results
                     .Where(pair => !string.Equals(pair.Key, id, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(pair => pair.Value.At)
                     .Take(_results.Count - MaxResults))
            _results.TryRemove(pair.Key, out _);
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
                var name = header.Key.Trim();
                if (ReservedHeaders.Any(reserved => string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase)))
                    continue;
                request.Headers.TryAddWithoutValidation(name, header.Value);
            }

            // Only send Beacon's secret when auth is actually enabled, and
            // never let a caller override the header via replyHeaders.
            var settings = App.Instance?.Settings;
            if (settings is not null && settings.RequireAuth && !string.IsNullOrWhiteSpace(settings.Token))
            {
                request.Headers.Remove("X-Beacon-Token");
                request.Headers.TryAddWithoutValidation("X-Beacon-Token", settings.Token);
            }

            using var response = await WebhookClient.SendAsync(request).ConfigureAwait(false);
        }
        catch { /* webhook delivery is best-effort */ }
    }
}
