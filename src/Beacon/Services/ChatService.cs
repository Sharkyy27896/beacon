using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beacon.Models;
using Application = System.Windows.Application;

namespace Beacon.Services;

/// <summary>
/// One persistent chat transcript shared by every reply prompt. The popup's
/// chat card binds directly to Messages, while HTTP callers can push Muse's
/// reply text in chunks so it appears as it streams.
/// </summary>
public sealed class ChatService
{
    /// <summary>Shown while a reply bubble has no text yet.</summary>
    private const string ThinkingText = "Thinking...";

    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly ImageSource? _museAvatar;
    private readonly ImageSource? _userAvatar;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public event EventHandler? Changed;

    public ChatService()
    {
        _museAvatar = LoadMuseAvatar();
        _userAvatar = LoadProfilePicture();
    }

    public void AddMuse(string text, string requestId = "", bool streaming = false) =>
        Add("muse", text, requestId, streaming, _museAvatar);

    public void AddUser(string text, string requestId = "") =>
        Add("user", text, requestId, false, _userAvatar);

    public void BeginMuseReply(string requestId = "", string text = "") =>
        BeginMuseReplyCore(requestId, text);

    private void BeginMuseReplyCore(string requestId, string text)
    {
        _dispatcher.Invoke(() =>
        {
            var root = BaseId(requestId);

            // Reuse the still-open bubble. Otherwise a second reply (or a chunk
            // carrying the -rN follow-up id) creates a duplicate empty "typing"
            // message above the real answer.
            var existing = Messages.LastOrDefault(m =>
                m.Role == "muse" && m.Streaming &&
                (string.IsNullOrEmpty(root) || BaseId(m.RequestId) == root));

            if (existing is not null)
            {
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
        _dispatcher.Invoke(() =>
        {
            var root = BaseId(requestId);

            // Muse streams follow-ups with the callback id ({id}-r2, -r3, ...)
            // while the bubble was opened with the original id. Match on the
            // conversation root first, then fall back to the newest open bubble
            // so a missing/unknown id never spawns a stray message.
            var target = Messages.LastOrDefault(m =>
                m.Role == "muse" && m.Streaming &&
                (string.IsNullOrEmpty(root) || BaseId(m.RequestId) == root || m.RequestId == requestId))
                ?? Messages.LastOrDefault(m => m.Role == "muse" && m.Streaming);

            // A bare "Thinking..." payload is the indicator itself, not a
            // finished answer. Never close that bubble or the real text below
            // would appear as a second message.
            var placeholderOnly = delta is null && text == ThinkingText;

            if (target is null)
            {
                var initial = delta ?? text ?? "";
                Add("muse", string.IsNullOrEmpty(initial) && !done ? ThinkingText : initial,
                    requestId ?? "", !done || placeholderOnly, _museAvatar);
                return;
            }

            if (delta is not null)
            {
                // Replace the placeholder once real text starts arriving.
                target.Text = target.Text == ThinkingText ? delta : target.Text + delta;
            }
            else if (text is not null)
            {
                target.Text = text;
            }
            target.Streaming = !done || placeholderOnly;
            Trim();
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public void RefreshTimeLabels()
    {
        _dispatcher.Invoke(() =>
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

    /// <summary>True when two ids belong to the same chat conversation.</summary>
    public static bool SameConversation(string? first, string? second) =>
        string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second) || BaseId(first) == BaseId(second);

    /// <summary>Strips the -r2/-r3 follow-up suffix so ids in one conversation match.</summary>
    private static string BaseId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var match = Regex.Match(id, @"^(.+)-r\d+$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : id;
    }

    private void Add(string role, string text, string requestId, bool streaming, ImageSource? avatar)
    {
        _dispatcher.Invoke(() =>
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

    private static ImageSource? LoadMuseAvatar()
    {
        try
        {
            var image = new BitmapImage(new Uri("pack://application:,,,/Assets/muse-logo.png", UriKind.Absolute));
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static ImageSource? LoadProfilePicture()
    {
        try
        {
            var candidates = new List<string>();

            var accountPictures = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "AccountPictures");
            if (Directory.Exists(accountPictures))
                candidates.AddRange(Directory.GetFiles(accountPictures, "*.*", SearchOption.AllDirectories)
                    .Where(IsPicture));

            var userPictures = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "User Account Pictures");
            if (Directory.Exists(userPictures))
                candidates.AddRange(Directory.GetFiles(userPictures, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(IsPicture));

            var best = candidates
                .Where(File.Exists)
                .OrderByDescending(PictureScore)
                .FirstOrDefault();
            if (best is null) return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(best));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static bool IsPicture(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
    }

    private static int PictureScore(string path)
    {
        var name = Path.GetFileName(path);
        var score = 0;
        if (name.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase)) score += 1000;
        if (name.Contains("accountpicture", StringComparison.OrdinalIgnoreCase)) score += 500;
        if (name.Contains("192", StringComparison.OrdinalIgnoreCase)) score += 250;
        try { score += (int)Math.Min(new FileInfo(path).Length / 1024, 400); } catch { /* ignore */ }
        return score;
    }
}
