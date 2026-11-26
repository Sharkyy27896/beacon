using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Beacon.Models;

namespace Beacon.Services;

/// <summary>
/// Avalonia version of the shared transcript. The protocol semantics are
/// identical to WPF; only avatar loading and UI marshalling differ.
/// </summary>
public sealed class ChatService
{
    private const string ThinkingText = "Thinking...";

    private readonly Bitmap? _museAvatar;
    private readonly Bitmap? _userAvatar;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public event EventHandler? Changed;

    public ChatService()
    {
        _museAvatar = LoadMuseAvatar();
        _userAvatar = LoadUserAvatar();
    }

    public void AddMuse(string text, string requestId = "", bool streaming = false) =>
        Add("muse", text, requestId, streaming, _museAvatar);

    public void AddUser(string text, string requestId = "") =>
        Add("user", text, requestId, false, _userAvatar);

    public void BeginMuseReply(string requestId = "", string text = "")
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var root = BaseId(requestId);
            var existing = Messages.LastOrDefault(m =>
                m.Role == "muse" && m.Streaming &&
                (string.IsNullOrEmpty(root) || BaseId(m.RequestId) == root));

            if (existing is not null)
            {
                // Keep any partial answer; a second reply must not reset it.
                if (string.IsNullOrEmpty(existing.Text) || existing.Text == ThinkingText)
                    existing.Text = string.IsNullOrEmpty(text) ? ThinkingText : text;
                existing.Streaming = true;
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            Add("muse", string.IsNullOrEmpty(text) ? ThinkingText : text, requestId, true, _museAvatar);
        });
    }

    public void Stream(string? requestId, string? delta, string? text, bool done)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var root = BaseId(requestId);
            var target = Messages.LastOrDefault(m =>
                m.Role == "muse" && m.Streaming &&
                (string.IsNullOrEmpty(root) || BaseId(m.RequestId) == root || m.RequestId == requestId))
                ?? (string.IsNullOrEmpty(root)
                    ? Messages.LastOrDefault(m => m.Role == "muse" && m.Streaming)
                    : null);

            var placeholderOnly = delta is null && text == ThinkingText;

            if (target is null)
            {
                // {"delta":"","done":true} is a terminator, not a message.
                if (done && string.IsNullOrEmpty(delta) && text is null) return;
                var initial = delta ?? text ?? "";
                Add("muse", string.IsNullOrEmpty(initial) && !done ? ThinkingText : initial,
                    requestId ?? "", !done || placeholderOnly, _museAvatar);
                return;
            }

            if (delta is not null)
                target.Text = target.Text == ThinkingText ? delta : target.Text + delta;
            else if (text is not null)
                target.Text = text;

            target.Streaming = !done || placeholderOnly;
            Trim();
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public void RefreshTimeLabels()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            foreach (var message in Messages) message.RefreshTimeLabel();
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public bool IsStreaming(string? requestId = null)
    {
        var root = BaseId(requestId);
        return Messages.Any(m =>
            m.Role == "muse" && m.Streaming &&
            (string.IsNullOrEmpty(root) || BaseId(m.RequestId) == root));
    }

    public static bool SameConversation(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second) || BaseId(first) == BaseId(second);

    private static string BaseId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var match = Regex.Match(id, @"^(.+)-r\d+$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : id;
    }

    private void Add(string role, string text, string requestId, bool streaming, Bitmap? avatar)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            Messages.Add(new ChatMessage
            {
                Role = role,
                Text = text,
                RequestId = requestId,
                Streaming = streaming,
                Avatar = avatar
            });
            Trim();
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Trim()
    {
        while (Messages.Count > 120) Messages.RemoveAt(0);
    }

    private static Bitmap? LoadMuseAvatar()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Beacon/Assets/muse-logo.png"));
            return new Bitmap(stream);
        }
        catch { return null; }
    }

    private static Bitmap? LoadUserAvatar()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".face"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".face.icon")
            };
            var best = candidates.FirstOrDefault(File.Exists);
            return best is null ? null : new Bitmap(best);
        }
        catch { return null; }
    }
}
