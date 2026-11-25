using Avalonia.Threading;
using Beacon.Models;
using Beacon.Views;

namespace Beacon.Services;

/// <summary>
/// Serializes popups so only one overlay is active. Avalonia replacement for
/// the WPF dispatcher-based manager; queue semantics are unchanged.
/// </summary>
public sealed class NotificationManager
{
    private readonly Queue<NotificationPayload> _queue = new();
    private NotificationWindow? _active;

    public int PendingCount => _queue.Count + (_active is null ? 0 : 1);

    public void Enqueue(NotificationPayload payload)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (_active is null) Show(payload);
            else _queue.Enqueue(payload);
        });
    }

    private void Show(NotificationPayload payload)
    {
        var window = new NotificationWindow(payload);
        _active = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_active, window)) _active = null;
            if (_active is null && _queue.Count > 0) Show(_queue.Dequeue());
        };
        window.Show();
        window.Activate();
        window.Focus();
    }

    public void UpdateQuickReplies(string? requestId, IReadOnlyList<NotificationAction> actions)
    {
        Dispatcher.UIThread.Invoke(() => _active?.SetQuickReplies(requestId, actions));
    }

    public void ClearAll()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            _queue.Clear();
            if (_active is not null)
            {
                _active.CancelInteraction();
                _active.Close();
            }
            _active = null;
        });
    }
}
