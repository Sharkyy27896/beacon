using System.ComponentModel;
using System.Windows.Media;
using Beacon.Services;

namespace Beacon.Models;

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _text = "";
    private bool _streaming;

    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Role { get; init; } = "muse";
    public string RequestId { get; init; } = "";
    public DateTime At { get; init; } = DateTime.Now;
    public ImageSource? Avatar { get; init; }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public bool Streaming
    {
        get => _streaming;
        set
        {
            if (_streaming == value) return;
            _streaming = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Streaming)));
        }
    }

    public string RoleName => Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "You" : "Muse";

    public string TimeLabel => ClockFormat.Format(At);

    /// <summary>Called when the 12/24-hour setting changes.</summary>
    public void RefreshTimeLabel() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeLabel)));

    public event PropertyChangedEventHandler? PropertyChanged;
}
