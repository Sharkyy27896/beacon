using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beacon.Models;
using Beacon.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Beacon.Views;

public partial class NotificationWindow : Window
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private readonly NotificationPayload _payload;
    private readonly DateTime _createdAt = DateTime.Now;
    private readonly DispatcherTimer? _countdownTimer;
    private readonly DateTime _expiresAt;
    private readonly System.Windows.Media.Brush _accent;
    private readonly string _accentHex;
    private readonly List<Control> _gatedControls = new();
    private readonly List<Control> _quickReplyButtons = new();
    private readonly List<NotificationAction> _quickReplyActions = new();
    private QuickReplyLimits _quickReplyLimits;

    private DispatcherTimer? _safetyTimer;
    private DateTime _lockedUntil;
    private bool _unlocked;
    private bool _closing;
    private bool _interactionCompleted;
    private bool _replySent;
    private int _replyCount;
    private Button? _cancelButton;
    private string _footerText = "";

    public NotificationWindow(NotificationPayload payload)
    {
        InitializeComponent();
        _payload = payload;
        var theme = Theme(payload.Kind, payload.AccentHex);
        _accent = BrushFromHex(theme.Accent);
        _accentHex = theme.Accent;
        // Layout hasn't run yet, so estimate from the virtual screen. FitOverlay
        // replaces this with the measured chat-panel width once the window loads.
        _quickReplyLimits = QuickReplyLimits.Estimate(SystemParameters.VirtualScreenWidth);

        // SystemParameters.VirtualScreen* are ALREADY DPI-adjusted device-independent
        // units, which are exactly the units Window.Left/Top/Width/Height expect.
        // Never convert these values again: doing so shrinks the overlay by the
        // display scale factor (a 200% monitor becomes a top-left quarter).
        SizeOverlayToVirtualScreen();
        Topmost = payload.Topmost;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        ReplyBox.TextChanged += (_, _) => UpdateReplyPlaceholder();
        ApplyTheme(theme);
        ApplyContent(theme);
        if (App.Instance is not null) App.Instance.SettingsChanged += App_SettingsChanged;
        UpdateReplyPlaceholder();
        _footerText = FooterText.Text;
        StartSafetyLock();

        if (payload.Duration.HasValue)
        {
            _expiresAt = DateTime.Now.Add(payload.Duration.Value);
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _countdownTimer.Tick += CountdownTick;
            _countdownTimer.Start();
        }
        else
        {
            CountdownTrack.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) =>
        {
            KeepTaskbarOnTop();
            FitOverlay();
            ApplyModernScrollbars();
            UpdateChatClip();
            StartCountdownAnimation();
            ProgressFill.Width = Math.Max(0, ContentScroll.ActualWidth - 68) * Math.Clamp(payload.Progress ?? 0, 0, 100) / 100.0;
            ((Storyboard)FindResource("IntroStoryboard")).Begin(this);
            SoundService.Play(payload);
        };
        ChatPanel.SizeChanged += (_, _) => UpdateChatClip();
        SizeChanged += (_, _) =>
        {
            FitOverlay();
            ApplyModernScrollbars();
            UpdateChatClip();
            StartCountdownAnimation();
        };
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            SizeOverlayToVirtualScreen();
            KeepTaskbarOnTop();
            FitOverlay();
            ApplyModernScrollbars();
            UpdateChatClip();
            StartCountdownAnimation();
        }));
    }

    private void ApplyTheme((string Accent, string Emoji, string Label) theme)
    {
        AccentBar.Fill = _accent;
        KindText.Text = theme.Label;
        KindText.Foreground = _accent;
        KindChip.Background = BrushFromHex(theme.Accent, 42);
        IconBox.Background = BrushFromHex(theme.Accent, 48);
        IconText.Text = string.IsNullOrWhiteSpace(_payload.Emoji) ? theme.Emoji : _payload.Emoji;
        IconText.Foreground = _accent;
        CountdownFill.Background = _accent;
        ProgressFill.Background = _accent;
    }

    private void ApplyContent((string Accent, string Emoji, string Label) theme)
    {
        TitleText.Text = _payload.Title;
        TimeText.Text = ClockFormat.Format(_createdAt);

        if (!string.IsNullOrWhiteSpace(_payload.Category))
        {
            SubtitleText.Text = _payload.Category;
            SubtitleText.Visibility = Visibility.Visible;
        }
        SourceText.Text = string.IsNullOrWhiteSpace(_payload.Source) ? "" : $"from {_payload.Source}";

        if (_payload.Mode == InteractionMode.Shell)
        {
            WarningBanner.Visibility = Visibility.Visible;
            WarningText.Text = _payload.Elevated
                ? "This will run with administrator privileges. Windows may show a UAC prompt — only approve if you trust this request."
                : "This will run with your current user rights. Only approve if you trust this request.";
            if (string.IsNullOrWhiteSpace(_payload.Code))
            {
                CodePanel.Visibility = Visibility.Visible;
                CodeLabel.Text = "Command to run";
                CodeText.Text = _payload.Command ?? "";
                CopyCodeButton.Background = BrushFromHex(theme.Accent, 230);
                CopyCodeButton.Foreground = new SolidColorBrush(Color.FromRgb(8, 14, 26));
                CopyCodeButton.BorderBrush = _accent;
            }
        }

        var richText = _payload.Markdown ?? _payload.Html ?? _payload.Message;
        if (!string.IsNullOrWhiteSpace(richText))
            MessageViewer.Document = RichTextParser.ToDocument(richText);
        else
            MessageViewer.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(_payload.Code))
        {
            CodePanel.Visibility = Visibility.Visible;
            CodeLabel.Text = string.IsNullOrWhiteSpace(_payload.CodeLabel) ? "Verification code" : _payload.CodeLabel;
            CodeText.Text = _payload.Code;
            CopyCodeButton.Background = BrushFromHex(theme.Accent, 230);
            CopyCodeButton.Foreground = new SolidColorBrush(Color.FromRgb(8, 14, 26));
            CopyCodeButton.BorderBrush = _accent;
        }

        // The chat card is part of every popup now, whatever the alert type.
        ChatPanel.Visibility = Visibility.Visible;
        ChatList.ItemsSource = App.Instance.Chat.Messages;
        App.Instance.Chat.Changed += ChatService_Changed;
        ChatStatusDot.Fill = _accent;
        UpdateChatStatus();
        ScrollChatToEnd();

        if (_payload.AllowCustomReply)
        {
            ReplyPlaceholderText.Text = string.IsNullOrWhiteSpace(_payload.ReplyPlaceholder)
                ? "Message Muse…"
                : _payload.ReplyPlaceholder;
            ReplyBox.ToolTip = _payload.ReplyPlaceholder;
            SendReplyButton.Content = string.IsNullOrWhiteSpace(_payload.ReplyButtonText) ? "Send" : _payload.ReplyButtonText;
            _gatedControls.Add(SendReplyButton);
        }
        else
        {
            ReplyComposer.Visibility = Visibility.Collapsed;
        }

        if (_payload.Details.Count > 0)
        {
            DetailsList.ItemsSource = _payload.Details;
            DetailsList.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(_payload.ImageSource))
            _ = LoadImageAsync(_payload.ImageSource);

        if (_payload.Progress.HasValue)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            var value = Math.Clamp(_payload.Progress.Value, 0, 100);
            ProgressLabel.Text = string.IsNullOrWhiteSpace(_payload.ProgressLabel) ? "Progress" : _payload.ProgressLabel;
            ProgressPercent.Text = $"{value:0}%";
        }

        var footer = _payload.Footer ?? "";
        if (string.IsNullOrWhiteSpace(footer) && _payload.Mode == InteractionMode.None)
            footer = "Press Esc or choose an action to dismiss";
        FooterText.Text = footer;
        FooterText.Visibility = string.IsNullOrWhiteSpace(footer) ? Visibility.Collapsed : Visibility.Visible;

        for (var i = 0; i < _payload.Actions.Count; i++)
        {
            var action = _payload.Actions[i];
            if (action.ReplyValue is not null)
            {
                _quickReplyActions.Add(action);
                continue;
            }
            var automationId = action.IsApprove ? "ApproveButton"
                : action.IsDeny ? "DenyButton"
                : $"ActionButton{i}";
            ActionPanel.Children.Add(CreateActionButton(action, automationId, false, action.Label));
        }
        RebuildQuickReplies();

        _cancelButton = new Button
        {
            Content = _payload.Mode == InteractionMode.None ? "Dismiss" : "Cancel",
            Style = (Style)FindResource("PillButton"),
            Name = "CancelButton"
        };
        AutomationProperties.SetAutomationId(_cancelButton, "CancelButton");
        _cancelButton.Click += DismissButton_Click;
        ActionPanel.Children.Add(_cancelButton);
        _gatedControls.Add(_cancelButton);
    }

    private Button CreateActionButton(NotificationAction action, string automationId, bool isQuickReply, string? displayLabel = null)
    {
        var button = new Button
        {
            // The full label/value stay in Tag; only the visible text is fitted.
            Content = displayLabel ?? action.Label,
            ToolTip = displayLabel is not null && displayLabel != action.Label ? action.Label : null,
            Style = (Style)FindResource(isQuickReply ? "QuickReplyButton" : "PillButton"),
            // A bottom gap on quick replies keeps wrapped rows from touching.
            Margin = isQuickReply ? new Thickness(0, 0, 10, 6) : new Thickness(0, 0, 10, 0),
            Tag = action,
            Name = automationId
        };
        if (action.Primary || isQuickReply)
        {
            button.Background = BrushFromHex(_accentHex, 235);
            button.Foreground = new SolidColorBrush(Color.FromRgb(8, 14, 26));
            button.BorderBrush = _accent;
        }
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += ActionButton_Click;
        _gatedControls.Add(button);
        return button;
    }

    /// <summary>
    /// Muse can send a fresh "options" array with each /chat message. Swap the
    /// buttons in place so the next choice always fits what it just said.
    /// </summary>
    public void SetQuickReplies(string? requestId, IReadOnlyList<NotificationAction> actions)
    {
        if (_closing) return;
        if (!ChatService.SameConversation(_payload.Id, requestId)) return;

        _quickReplyActions.Clear();
        _quickReplyActions.AddRange(actions);
        // Layout may have changed since the popup opened (another monitor, DPI).
        _quickReplyLimits = QuickReplyLimits.ForPanelWidth(
            ChatPanel.ActualWidth > 0 ? ChatPanel.ActualWidth : Root.ActualWidth * 0.36);
        RebuildQuickReplies();
        FitOverlay();
    }

    /// <summary>
    /// Renders only as many buttons as fit the current card width, truncating
    /// overlong labels while keeping each action's full reply value in Tag.
    /// </summary>
    private void RebuildQuickReplies()
    {
        foreach (var old in _quickReplyButtons) _gatedControls.Remove(old);
        _quickReplyButtons.Clear();
        QuickReplyPanel.Children.Clear();

        var count = Math.Min(_quickReplyActions.Count, _quickReplyLimits.MaxButtons);
        for (var i = 0; i < count; i++)
        {
            var action = _quickReplyActions[i];
            var button = CreateActionButton(action, $"ReplyButton{i}", true, _quickReplyLimits.Fit(action.Label));
            button.IsEnabled = _unlocked;
            QuickReplyPanel.Children.Add(button);
            _quickReplyButtons.Add(button);
        }

        QuickReplyBar.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateReplyPlaceholder() =>
        ReplyPlaceholderText.Visibility = string.IsNullOrEmpty(ReplyBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void StartSafetyLock()
    {
        if (_gatedControls.Count == 0) { _unlocked = true; return; }
        _unlocked = false;
        foreach (var control in _gatedControls) control.IsEnabled = false;
        _lockedUntil = DateTime.Now.AddSeconds(3);
        _safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _safetyTimer.Tick += SafetyTick;
        _safetyTimer.Start();
        SafetyTick(null, EventArgs.Empty);
    }

    private void SafetyTick(object? sender, EventArgs e)
    {
        var remaining = Math.Ceiling((_lockedUntil - DateTime.Now).TotalSeconds);
        if (remaining > 0)
        {
            FooterText.Text = $"Actions unlock in {remaining:0}s…";
            FooterText.Visibility = Visibility.Visible;
            return;
        }
        _unlocked = true;
        foreach (var control in _gatedControls) control.IsEnabled = true;
        FooterText.Text = _footerText;
        FooterText.Visibility = string.IsNullOrWhiteSpace(_footerText) ? Visibility.Collapsed : Visibility.Visible;
        _safetyTimer?.Stop();
    }

    private void CountdownTick(object? sender, EventArgs e)
    {
        if (DateTime.Now >= _expiresAt) CloseWithAnimation();
    }

    /// <summary>
    /// Covers the entire virtual screen. These metrics are already DPI-adjusted
    /// device-independent units, the same units used by Window.Left/Top/Width/
    /// Height. Applying any additional scale conversion here would shrink the
    /// overlay on high-DPI displays and pin it into the top-left corner. The
    /// taskbar is kept above this window separately, so no bottom strip has to
    /// be left uncovered.
    /// </summary>
    private void SizeOverlayToVirtualScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var width = SystemParameters.VirtualScreenWidth;
        var height = SystemParameters.VirtualScreenHeight;
        if (width <= 0 || height <= 0) return;

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// The taskbar is itself a topmost window. When the prompt is shown on top
    /// of it, the taskbar can look like it vanished. Re-asserting HWND_TOPMOST
    /// on the taskbar after the prompt appears keeps it visible while the
    /// overlay still covers the whole screen.
    /// </summary>
    private static void KeepTaskbarOnTop()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return;
        SetWindowPos(taskbar, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// Keeps the popup and the chat card fully inside the screen. The popup
    /// shrinks and scrolls internally before the chat card is allowed to push
    /// it off screen.
    /// </summary>
    private void FitOverlay()
    {
        // The backdrop covers every display, but the card itself is centered on
        // ONE display. With a laptop + TV the virtual-screen center lands on the
        // seam and the popup appears cut off or off-center.
        var rootWidth = Root.ActualWidth > 0 ? Root.ActualWidth : ActualWidth;
        var rootHeight = Root.ActualHeight > 0 ? Root.ActualHeight : ActualHeight;
        var (centerX, centerY, displayWidth, displayHeight) =
            GetTargetDisplayMetrics(rootWidth, rootHeight);

        var maxOverlay = Math.Max(320, displayHeight - 56);
        var availableWidth = Math.Max(300, displayWidth - 48);
        var chatVisible = ChatPanel.Visibility == Visibility.Visible;

        // Give the chat card a generous share of the right side while keeping
        // the popup at its comfortable width; both shrink on narrow displays.
        var chatWidth = chatVisible ? Math.Clamp(availableWidth * 0.36, 220, 520) : 0;
        var cardWidth = chatVisible
            ? Math.Clamp(availableWidth - chatWidth - 12, 260, 680)
            : Math.Min(680, availableWidth);
        if (chatVisible && cardWidth + chatWidth + 12 > availableWidth)
        {
            var ratio = availableWidth / (cardWidth + chatWidth + 12);
            cardWidth *= ratio;
            chatWidth *= ratio;
        }

        CardColumn.Width = new GridLength(cardWidth);
        // The panel keeps a 12px left margin, so the column must reserve it.
        ChatColumn.Width = chatVisible ? new GridLength(chatWidth + 12) : new GridLength(0);
        ChatPanel.Width = chatWidth;
        OverlayStack.MaxWidth = Math.Max(0, availableWidth);
        OverlayStack.MaxHeight = maxOverlay;

        // Grid centering puts the stack at the virtual-screen center; shift it
        // so its middle lines up with the middle of the target display.
        OverlayOffset.X = centerX - rootWidth / 2;
        OverlayOffset.Y = centerY - rootHeight / 2;

        // The chat transcript can use most of the available height. The popup
        // itself is capped well below full screen and only scrolls when its
        // content genuinely needs more room.
        // Reserve enough room for the header, wrapped quick-reply rows and the
        // composer hint so a full transcript never clips the bottom text.
        ChatScroll.MaxHeight = Math.Clamp(maxOverlay - 270, 120, 560);
        ChatPanel.MaxHeight = maxOverlay;
        Card.MaxHeight = Math.Min(maxOverlay, Math.Clamp(displayHeight * 0.62, 360, 640));

        // The chat width (and therefore quick-reply capacity) follows screen
        // size and DPI. Rebuild only when the measured limits actually change.
        var limits = QuickReplyLimits.ForPanelWidth(chatWidth);
        if (_quickReplyActions.Count > 0 && limits != _quickReplyLimits)
        {
            _quickReplyLimits = limits;
            RebuildQuickReplies();
        }
    }

    /// <summary>
    /// Center and size of the display hosting the popup, in this window's DIPs.
    /// PointFromScreen handles mixed monitor DPI scales correctly; if anything
    /// is unavailable we fall back to the whole virtual screen (old behaviour).
    /// </summary>
    private (double CenterX, double CenterY, double Width, double Height)
        GetTargetDisplayMetrics(double rootWidth, double rootHeight)
    {
        var fallback = (rootWidth / 2, rootHeight / 2, rootWidth, rootHeight);
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens
                .Select(s => new ScreenRect(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height, s.Primary))
                .ToArray();
            var cursor = System.Windows.Forms.Cursor.Position;
            var target = MonitorSelector.Pick(screens, cursor.X, cursor.Y);
            if (target is null) return fallback;

            var topLeft = Root.PointFromScreen(new System.Windows.Point(target.Left, target.Top));
            var bottomRight = Root.PointFromScreen(new System.Windows.Point(target.Right, target.Bottom));
            var width = bottomRight.X - topLeft.X;
            var height = bottomRight.Y - topLeft.Y;
            if (width < 200 || height < 200) return fallback;

            return ((topLeft.X + bottomRight.X) / 2, (topLeft.Y + bottomRight.Y) / 2, width, height);
        }
        catch
        {
            return fallback;
        }
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            SizeOverlayToVirtualScreen();
            KeepTaskbarOnTop();
            FitOverlay();
        }));

    /// <summary>
    /// Re-applies the custom scrollbar template after layout. Some internal
    /// scrollbars (including those inside ScrollViewer templates) ignore the
    /// application-level implicit style until the style is set directly.
    /// </summary>
    private void ApplyModernScrollbars() => ApplyModernScrollbars(this, null);

    private static void ApplyModernScrollbars(DependencyObject root, Style? style)
    {
        style ??= Application.Current?.Resources.Contains("ModernScrollBar") == true
            ? Application.Current.Resources["ModernScrollBar"] as Style
            : null;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollBar bar)
            {
                bar.OverridesDefaultStyle = true;
                if (style is not null) bar.Style = style;
                bar.ApplyTemplate();
            }
            ApplyModernScrollbars(child, style);
        }
    }

    /// <summary>
    /// Clips the chat's content to the panel's rounded corners so the right
    /// edge no longer looks square or cut off when the transcript scrolls.
    /// </summary>
    private void UpdateChatClip()
    {
        var width = ChatContent.ActualWidth > 0 ? ChatContent.ActualWidth : ChatPanel.ActualWidth;
        var height = ChatContent.ActualHeight > 0 ? ChatContent.ActualHeight : ChatPanel.ActualHeight;
        if (width <= 0 || height <= 0) return;
        ChatContent.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 18, 18);
    }

    /// <summary>
    /// Animates the inset countdown rail continuously from full width to zero
    /// instead of stepping it on a timer.
    /// </summary>
    private void StartCountdownAnimation()
    {
        if (!_payload.Duration.HasValue || CountdownTrack.Visibility != Visibility.Visible) return;
        var remaining = (_expiresAt - DateTime.Now).TotalMilliseconds;
        var width = CountdownTrack.ActualWidth;
        if (remaining <= 0 || width <= 0) return;

        CountdownFill.Width = width;
        CountdownFill.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(width, 0, TimeSpan.FromMilliseconds(remaining))
            {
                FillBehavior = FillBehavior.HoldEnd
            });
    }

    private void CloseWithAnimation()
    {
        if (_closing) return;
        _closing = true;
        _countdownTimer?.Stop();
        _safetyTimer?.Stop();
        CountdownFill.BeginAnimation(FrameworkElement.WidthProperty, null);
        var storyboard = (Storyboard)FindResource("ExitStoryboard");
        storyboard.Completed += (_, _) => Close();
        storyboard.Begin(this);
    }

    private Task CompleteCurrentAsync(string kind, string? value = null, string? label = null)
    {
        if (App.Instance is null) return Task.CompletedTask;

        var result = new InteractionResult { Kind = kind, Value = value, Label = label };

        if (kind.Equals("reply", StringComparison.OrdinalIgnoreCase) && value is not null)
        {
            // Keep the prompt alive. Every message is shown in the transcript
            // and delivered to Muse, not just the first one.
            _replySent = true;
            _replyCount++;
            App.Instance.Chat.AddUser(value, _payload.Id);
            App.Instance.Chat.BeginMuseReply(_payload.Id);
            ReplyBox.Text = "";
            ReplyBox.Focus();
            SetReplySentState();

            // The first reply resolves the original HTTP interaction. Later
            // replies are follow-ups that still reach Muse through the webhook.
            return _interactionCompleted
                ? App.Instance.Interactions.ReportFollowUpAsync(_payload, result, _replyCount)
                : CompleteOnce(result);
        }

        if (_interactionCompleted) return Task.CompletedTask;
        return CompleteOnce(result);
    }

    private Task CompleteOnce(InteractionResult result)
    {
        _interactionCompleted = true;
        return App.Instance.Interactions.CompleteAsync(_payload.Id, result);
    }

    private void SetReplySentState()
    {
        // The chat stays fully usable; only the dismiss button changes so the
        // user can keep replying and finish whenever they are done.
        if (_cancelButton is not null) _cancelButton.Content = "Done";
        ScrollChatToEnd();
    }

    private void ChatService_Changed(object? sender, EventArgs e)
    {
        UpdateChatStatus();
        FitOverlay();
        ScrollChatToEnd();
        ApplyModernScrollbars();
        UpdateChatClip();
    }

    private void App_SettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TimeText.Text = ClockFormat.Format(_createdAt);
            App.Instance?.Chat.RefreshTimeLabels();
        }));
    }

    private void UpdateChatStatus()
    {
        if (App.Instance is null) return;
        var streaming = App.Instance.Chat.IsStreaming(_payload.Id);
        ChatStatusText.Text = !_replySent ? "Live chat with Muse"
            : streaming ? "Muse is replying…" : "Muse replied";
        ChatStatusDot.Fill = streaming ? _accent : BrushFromHex("#35D49A");
    }

    private void ScrollChatToEnd() =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ChatList.UpdateLayout();
            ChatScroll.ScrollToEnd();
        }), DispatcherPriority.Background);

    public void CancelInteraction() => _ = CompleteCurrentAsync("cancelled");

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_unlocked || (sender as Button)?.Tag is not NotificationAction action) return;

        if (_payload.Mode == InteractionMode.Shell)
        {
            _ = CompleteCurrentAsync(action.IsApprove ? "approved" : "denied", _payload.Command, action.Label);
            CloseWithAnimation();
            return;
        }
        if (action.ReplyValue is not null)
        {
            // Keep the chat card open so Muse's streamed response is visible.
            _ = CompleteCurrentAsync("reply", action.ReplyValue, action.Label);
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(action.Url))
                Process.Start(new ProcessStartInfo(action.Url) { UseShellExecute = true });
            else if (!string.IsNullOrWhiteSpace(action.CopyText))
            {
                Clipboard.SetText(action.CopyText);
                if (sender is Button button) button.Content = "Copied!";
                return;
            }
        }
        catch { /* ignore invalid links */ }
        CloseWithAnimation();
    }

    private void SendReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_unlocked) return;
        var text = ReplyBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            ReplyBox.Focus();
            return;
        }
        _ = CompleteCurrentAsync("reply", text, "Custom reply");
    }

    private void ReplyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Handle Enter before the TextBox inserts a newline (AcceptsReturn).
        // Shift+Enter is left alone so multi-line replies still work.
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (_unlocked) SendReplyButton_Click(sender, e);
    }

    private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = !string.IsNullOrWhiteSpace(_payload.Code) ? _payload.Code : _payload.Command;
            Clipboard.SetText(text ?? "");
            CopyCodeButton.Content = "Copied";
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            reset.Tick += (_, _) => { CopyCodeButton.Content = "Copy"; reset.Stop(); };
            reset.Start();
        }
        catch { /* clipboard unavailable */ }
    }

    private void Backdrop_Click(object sender, MouseButtonEventArgs e)
    {
        // Intentionally inert: click-outside never dismisses a prompt.
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_unlocked) return;
        CancelInteraction();
        CloseWithAnimation();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_unlocked || (!_payload.Dismissible && !_replySent)) return;
        CancelInteraction();
        CloseWithAnimation();
    }

    protected override void OnClosed(EventArgs e)
    {
        _countdownTimer?.Stop();
        _safetyTimer?.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        if (App.Instance is not null)
        {
            App.Instance.SettingsChanged -= App_SettingsChanged;
            App.Instance.Chat.Changed -= ChatService_Changed;
        }
        CancelInteraction();
        base.OnClosed(e);
    }

    private Task LoadImageAsync(string source)
    {
        try
        {
            var text = source.Trim();
            BitmapImage image;

            if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = text.IndexOf(',');
                if (comma < 0) return Task.CompletedTask;
                image = FromBase64(text[(comma + 1)..]);
            }
            else if (Regex.IsMatch(text, "^[A-Za-z0-9+/=\\r\\n]{200,}$"))
            {
                image = FromBase64(text);
            }
            else if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                     (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile))
            {
                image = new BitmapImage(uri);
            }
            else if (File.Exists(text))
            {
                image = new BitmapImage(new Uri(Path.GetFullPath(text)));
            }
            else return Task.CompletedTask;

            PreviewImage.Source = image;
            ImagePanel.Visibility = Visibility.Visible;
        }
        catch { ImagePanel.Visibility = Visibility.Collapsed; }
        return Task.CompletedTask;
    }

    private static BitmapImage FromBase64(string base64)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(base64));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static (string Accent, string Emoji, string Label) Theme(NotificationKind kind, string? custom)
    {
        var theme = kind switch
        {
            NotificationKind.Success => ("#35D49A", "✓", "SUCCESS"),
            NotificationKind.Warning => ("#FFB84D", "!", "WARNING"),
            NotificationKind.Error => ("#FF6473", "✕", "ACTION NEEDED"),
            NotificationKind.Question => ("#B79CFF", "?", "DECISION NEEDED"),
            NotificationKind.Captcha => ("#FFC24B", "🛡", "CAPTCHA NEEDED"),
            NotificationKind.Verification => ("#4FD7F5", "🔐", "VERIFICATION"),
            NotificationKind.Reminder => ("#9DB4D8", "⏰", "REMINDER"),
            NotificationKind.Shell => ("#FF8A5C", "⚡", "ADMIN APPROVAL"),
            _ => ("#5C9BFF", "i", "NOTIFICATION")
        };
        return (string.IsNullOrWhiteSpace(custom) ? theme.Item1 : custom!, theme.Item2, theme.Item3);
    }

    private static SolidColorBrush BrushFromHex(string hex, byte alpha = 255)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 7) hex = "#5C9BFF";
        var r = Convert.ToByte(hex[1..3], 16);
        var g = Convert.ToByte(hex[3..5], 16);
        var b = Convert.ToByte(hex[5..7], 16);
        return new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
    }
}
