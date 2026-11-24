using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Beacon.Models;
using Beacon.Views;

namespace Beacon.Services;

public sealed class NotificationManager
{
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly Queue<NotificationPayload> _queue = new();
    private NotificationWindow? _active;

    public int PendingCount => _queue.Count + (_active is null ? 0 : 1);

    public void Enqueue(NotificationPayload payload)
    {
        _dispatcher.Invoke(() =>
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

    /// <summary>
    /// Replaces the quick reply buttons on the active chat card. Called when
    /// Muse includes a fresh "options" array in a /chat message.
    /// </summary>
    public void UpdateQuickReplies(string? requestId, IReadOnlyList<NotificationAction> actions)
    {
        _dispatcher.Invoke(() => _active?.SetQuickReplies(requestId, actions));
    }

    public void ClearAll()
    {
        _dispatcher.Invoke(() =>
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
