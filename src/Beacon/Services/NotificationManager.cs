using System.Linq;
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
    private string? _activeId;
    private NotificationPayload? _last;

    public int PendingCount => _queue.Count + (_active is null ? 0 : 1);

    /// <summary>True when a tray re-open can show something.</summary>
    public bool HasLast => _last is not null;

    public void Enqueue(NotificationPayload payload) =>
        _dispatcher.Invoke(() => EnqueueCore(payload));

    private void EnqueueCore(NotificationPayload payload)
    {
        if (_active is null) Show(payload);
        else _queue.Enqueue(payload);
    }

    /// <summary>
    /// Re-shows the most recent ordinary notification. Interaction prompts are
    /// deliberately not remembered: re-opening one could duplicate a pending
    /// reply/shell decision.
    /// </summary>
    public bool ReopenLast() => _dispatcher.Invoke(() =>
    {
        if (_last is null) return false;
        if (_active is not null && string.Equals(_activeId, _last.Id, StringComparison.Ordinal))
        {
            _active.Activate();
            return true;
        }
        EnqueueCore(_last.Clone());
        return true;
    });

    /// <summary>Drops a prompt whose decision window has elapsed.</summary>
    public void Expire(string id) => _dispatcher.Invoke(() =>
    {
        if (_active is not null && string.Equals(_activeId, id, StringComparison.Ordinal))
        {
            _active.CancelInteraction();
            _active.Close();
        }

        var remaining = _queue.Where(item => !string.Equals(item.Id, id, StringComparison.Ordinal)).ToList();
        if (remaining.Count == _queue.Count) return;
        _queue.Clear();
        foreach (var item in remaining) _queue.Enqueue(item);
    });

    private void Show(NotificationPayload payload)
    {
        if (payload.Mode == InteractionMode.None) _last = payload;
        var window = new NotificationWindow(payload);
        _active = window;
        _activeId = payload.Id;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_active, window))
            {
                _active = null;
                _activeId = null;
            }
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
            _activeId = null;
        });
    }
}
