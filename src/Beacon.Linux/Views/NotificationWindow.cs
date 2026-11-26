using System.Net.Http;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Beacon.Models;
using Beacon.Services;

namespace Beacon.Views;

/// <summary>
/// Avalonia/Linux replacement for the WPF notification overlay. The window is
/// borderless, transparent and taskbar-less; it covers the working area of one
/// display and centres the card and the persistent Muse chat panel on it.
/// </summary>
public sealed class NotificationWindow : Window
{
    private static readonly HttpClient ImageClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Regex Base64Pattern = new(@"^[A-Za-z0-9+/=\r\n]{200,}$", RegexOptions.Compiled);

    private readonly NotificationPayload _payload;
    private readonly DateTime _createdAt = DateTime.Now;
    private readonly IBrush _accent;
    private readonly string _accentHex;
    private readonly List<Control> _gatedControls = new();
    private readonly List<Button> _quickReplyButtons = new();
    private readonly List<NotificationAction> _quickReplyActions = new();

    private QuickReplyLimits _quickReplyLimits;
    private DispatcherTimer? _safetyTimer;
    private DispatcherTimer? _countdownTimer;
    private DispatcherTimer? _copyResetTimer;
    private DateTime _lockedUntil;
    private DateTime _expiresAt;
    private bool _unlocked;
    private bool _closing;
    private bool _cancelInteraction;
    private bool _interactionCompleted;
    private bool _replySent;
    private int _replyCount;
    private string _footerText = "";
    private PixelPoint? _lastPointerPixel;
    private Screens? _screens;

    private Grid _layoutGrid = null!;
    private Grid _chatRows = null!;
    private Border _card = null!;
    private Border _chatPanel = null!;
    private Border _messageHost = null!;
    private Border _countdownTrack = null!;
    private Border _countdownFill = null!;
    private TextBlock _timeText = null!;
    private TextBlock _footerLabel = null!;
    private TextBlock _chatStatusText = null!;
    private Border _chatStatusDot = null!;
    private TextBox? _replyBox;
    private Button _sendButton = null!;
    private Button? _cancelButton;
    private Button _copyCodeButton = null!;
    private WrapPanel _actionPanel = null!;
    private Border _quickReplyBar = null!;
    private WrapPanel _quickReplyPanel = null!;
    private ItemsControl _chatList = null!;
    private ScrollViewer _chatScroll = null!;
    private Image _previewImage = null!;
    private Border _imagePanel = null!;
    private Border _progressPanel = null!;
    private Border _progressFill = null!;
    private TextBlock _progressPercent = null!;
    private double _cardWidth = 520;
    private double _chatWidth = 330;

    public NotificationWindow(NotificationPayload payload)
    {
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));

        var theme = ResolveTheme(_payload.Kind, _payload.AccentHex);
        _accent = BrushFromHex(theme.Accent);
        _accentHex = theme.Accent;
        _quickReplyLimits = QuickReplyLimits.Estimate(1920);

        Title = "Beacon";
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = true;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = _payload.Topmost;
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = new SolidColorBrush(Color.FromRgb(7, 11, 20));
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        Content = BuildUi(theme);
        SizeAndPlaceOnDisplay();

        ApplyContent(theme);
        _footerText = _footerLabel.Text ?? "";
        StartSafetyLock();

        if (_payload.Duration.HasValue)
        {
            _expiresAt = DateTime.Now.Add(_payload.Duration.Value);
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _countdownTimer.Tick += CountdownTick;
            _countdownTimer.Start();
            UpdateCountdown();
        }
        else
        {
            _countdownTrack.IsVisible = false;
        }

        Opened += (_, _) =>
        {
            SizeAndPlaceOnDisplay();
            SoundService.Play(_payload);
        };
        KeyDown += Window_KeyDown;
        PointerMoved += Window_PointerMoved;
        Resized += (_, _) => ApplyDisplayLayout(Width, Height);
        ScalingChanged += (_, _) => Dispatcher.UIThread.Post(SizeAndPlaceOnDisplay);

        _screens = TopLevel.GetTopLevel(this)?.Screens;
        if (_screens is not null) _screens.Changed += Screens_Changed;

        if (App.Instance is not null)
        {
            App.Instance.SettingsChanged += App_SettingsChanged;
            App.Instance.Chat.Changed += ChatService_Changed;
        }

        UpdateChatStatus();
    }

    /// <summary>
    /// Replaces the quick-reply row when Muse sends a fresh options array.
    /// Follow-up ids such as <c>abc-r2</c> still belong to this conversation.
    /// </summary>
    public void SetQuickReplies(string? requestId, IReadOnlyList<NotificationAction> actions)
    {
        if (_closing || !ChatService.SameConversation(_payload.Id, requestId)) return;

        void Apply()
        {
            if (_closing) return;
            _quickReplyActions.Clear();
            if (actions is not null) _quickReplyActions.AddRange(actions);

            var limits = QuickReplyLimits.ForPanelWidth(_chatWidth > 0 ? _chatWidth : 330);
            if (limits != _quickReplyLimits) _quickReplyLimits = limits;
            RebuildQuickReplies();
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>
    /// External dismissal (tray "clear"): suppress the Closed interaction
    /// completion without treating the close as a user decision.
    /// </summary>
    public void CancelInteraction()
    {
        _cancelInteraction = true;
        // Complete the pending HTTP request instead of leaving wait:true callers
        // blocked until the full timeout after a tray-side clear.
        _ = CompleteOnceAsync(new InteractionResult
        {
            Kind = "cancelled",
            Label = "Dismissed"
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _countdownTimer?.Stop();
        _safetyTimer?.Stop();
        _copyResetTimer?.Stop();

        if (_screens is not null) _screens.Changed -= Screens_Changed;
        if (App.Instance is not null)
        {
            App.Instance.SettingsChanged -= App_SettingsChanged;
            App.Instance.Chat.Changed -= ChatService_Changed;
        }

        if (!_cancelInteraction && !_interactionCompleted)
            _ = CompleteOnceAsync(new InteractionResult
            {
                Kind = "cancelled",
                Label = "Dismissed"
            });

        base.OnClosed(e);
    }

    // ---------------------------------------------------------------- UI build

    private Control BuildUi((string Accent, string Emoji, string Label) theme)
    {
        var accentSoft = BrushFromHex(_accentHex, 46);
        var text = new SolidColorBrush(Color.FromRgb(232, 238, 250));
        var muted = new SolidColorBrush(Color.FromRgb(158, 178, 210));

        _countdownTrack = new Border
        {
            Height = 3,
            Background = new SolidColorBrush(Color.FromRgb(30, 42, 64)),
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true
        };
        _countdownFill = new Border
        {
            Height = 3,
            Width = 0,
            Background = _accent,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _countdownTrack.Child = _countdownFill;

        var cardContent = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

        // Accent bar, icon, title, kind chip and time/source.
        cardContent.Children.Add(new Border
        {
            Width = 46,
            Height = 3,
            Background = _accent,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var iconBox = new Border
        {
            Width = 44,
            Height = 44,
            Background = BrushFromHex(_accentHex, 56),
            CornerRadius = new CornerRadius(13),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(_payload.Emoji) ? theme.Emoji : _payload.Emoji,
                FontSize = 20,
                Foreground = _accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var title = new TextBlock
        {
            Text = _payload.Title,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        };
        var subtitle = new TextBlock
        {
            Text = _payload.Category ?? "",
            FontSize = 11.5,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = !string.IsNullOrWhiteSpace(_payload.Category),
            Margin = new Thickness(0, 2, 0, 0)
        };
        var titleStack = new StackPanel { Margin = new Thickness(12, 1, 8, 0) };
        titleStack.Children.Add(title);
        titleStack.Children.Add(subtitle);

        var chip = new Border
        {
            Background = accentSoft,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 4),
            Child = new TextBlock
            {
                Text = theme.Label,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = _accent
            }
        };
        _timeText = new TextBlock
        {
            Text = ClockFormat.Format(_createdAt),
            FontSize = 10.5,
            Foreground = muted,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var sourceText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_payload.Source) ? "" : $"from {_payload.Source}",
            FontSize = 10.5,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 150
        };
        var metaStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        metaStack.Children.Add(chip);
        metaStack.Children.Add(_timeText);
        metaStack.Children.Add(sourceText);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,Auto") };
        header.Children.Add(iconBox);
        header.Children.Add(titleStack);
        Grid.SetColumn(titleStack, 1);
        header.Children.Add(metaStack);
        Grid.SetColumn(metaStack, 2);
        cardContent.Children.Add(header);

        var warning = new Border
        {
            IsVisible = _payload.Mode == InteractionMode.Shell,
            Background = new SolidColorBrush(Color.FromArgb(230, 76, 36, 18)),
            BorderBrush = BrushFromHex(_accentHex, 150),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(11, 9),
            Margin = new Thickness(0, 14, 0, 0),
            Child = new TextBlock
            {
                Text = _payload.Elevated
                    ? "This command requests administrator rights. Linux will ask for confirmation through pkexec — only approve requests you trust."
                    : "This command will run with your current user rights. Only approve requests you trust.",
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 194, 154)),
                TextWrapping = TextWrapping.Wrap
            }
        };
        cardContent.Children.Add(warning);

        _messageHost = new Border { Margin = new Thickness(0, 14, 0, 0) };
        cardContent.Children.Add(_messageHost);

        // Code panel. A shell prompt must always show the exact command, even
        // when the same payload also carries a code/OTP field.
        var hasCommand = _payload.Mode == InteractionMode.Shell &&
                         !string.IsNullOrWhiteSpace(_payload.Command);
        var codeLabel = new TextBlock
        {
            Text = hasCommand && !string.IsNullOrWhiteSpace(_payload.Code)
                ? "Command and verification code"
                : hasCommand
                    ? "Command to run"
                    : string.IsNullOrWhiteSpace(_payload.CodeLabel) ? "Verification code" : _payload.CodeLabel!,
            FontSize = 11,
            Foreground = muted,
            Margin = new Thickness(0, 0, 0, 6)
        };
        var codeText = new TextBlock
        {
            Text = hasCommand && !string.IsNullOrWhiteSpace(_payload.Code)
                ? $"Command: {_payload.Command}\nCode: {_payload.Code}"
                : hasCommand ? _payload.Command! : _payload.Code ?? "",
            FontFamily = new FontFamily("Consolas, Monaco, monospace"),
            FontSize = hasCommand ? 12 : 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = BrushFromHex(_accentHex),
            TextWrapping = TextWrapping.Wrap
        };
        _copyCodeButton = MakeButton("Copy", primary: true, compact: true);
        _copyCodeButton.Click += CopyCodeButton_Click;
        _gatedControls.Add(_copyCodeButton);

        var codePanel = new Border
        {
            IsVisible = !string.IsNullOrWhiteSpace(_payload.Code) ||
                        (_payload.Mode == InteractionMode.Shell && !string.IsNullOrWhiteSpace(_payload.Command)),
            Background = new SolidColorBrush(Color.FromRgb(13, 21, 38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 60, 92)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 14, 0, 0)
        };
        var codeContent = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto")
        };
        codeContent.Children.Add(codeLabel);
        var codeValue = new Border { Child = codeText, Padding = new Thickness(0, 0, 10, 0) };
        codeContent.Children.Add(codeValue);
        Grid.SetRow(codeValue, 1);
        codeContent.Children.Add(_copyCodeButton);
        Grid.SetRow(_copyCodeButton, 1);
        Grid.SetColumn(_copyCodeButton, 1);
        codePanel.Child = codeContent;
        cardContent.Children.Add(codePanel);

        // Details.
        var details = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<KeyValuePair<string, string>>((pair, _) =>
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
                row.Children.Add(new TextBlock
                {
                    Text = pair.Key,
                    FontSize = 11.5,
                    Foreground = muted,
                    TextWrapping = TextWrapping.Wrap
                });
                var value = new TextBlock
                {
                    Text = pair.Value,
                    FontSize = 11.5,
                    Foreground = text,
                    TextWrapping = TextWrapping.Wrap
                };
                row.Children.Add(value);
                Grid.SetColumn(value, 1);
                return row;
            })
        };
        details.ItemsSource = _payload.Details;
        var detailsPanel = new Border
        {
            IsVisible = _payload.Details.Count > 0,
            Background = new SolidColorBrush(Color.FromArgb(160, 18, 28, 48)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 10, 0, 0),
            Child = details
        };
        cardContent.Children.Add(detailsPanel);

        // Image preview.
        _previewImage = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxHeight = 170
        };
        _imagePanel = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.FromRgb(11, 18, 33)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 60, 92)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 10, 0, 0),
            Child = _previewImage
        };
        cardContent.Children.Add(_imagePanel);

        // Progress.
        _progressFill = new Border
        {
            Height = 6,
            Width = 0,
            Background = _accent,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var progressTrack = new Border
        {
            Height = 6,
            Background = new SolidColorBrush(Color.FromRgb(30, 42, 64)),
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Child = _progressFill
        };
        _progressPercent = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = _accent,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var progressHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        progressHeader.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_payload.ProgressLabel) ? "Progress" : _payload.ProgressLabel!,
            FontSize = 11,
            Foreground = muted
        });
        progressHeader.Children.Add(_progressPercent);
        Grid.SetColumn(_progressPercent, 1);

        _progressPanel = new Border
        {
            IsVisible = _payload.Progress.HasValue,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var progressStack = new StackPanel();
        progressStack.Children.Add(progressHeader);
        progressStack.Children.Add(progressTrack);
        _progressPanel.Child = progressStack;
        cardContent.Children.Add(_progressPanel);

        // Actions and dismiss/cancel.
        _actionPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        cardContent.Children.Add(_actionPanel);

        _footerLabel = new TextBlock
        {
            Text = "",
            FontSize = 10.5,
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        };
        cardContent.Children.Add(_footerLabel);

        var cardScroller = new ScrollViewer
        {
            Content = cardContent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 4, 0)
        };
        var cardRoot = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        cardRoot.Children.Add(cardScroller);
        cardRoot.Children.Add(_countdownTrack);
        Grid.SetRow(_countdownTrack, 1);

        _card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(13, 20, 35)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(44, 62, 94)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(22),
            Child = cardRoot,
            MaxHeight = 640
        };

        _chatPanel = BuildChatPanel(theme);

        _layoutGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ColumnDefinitions = new ColumnDefinitions($"{_cardWidth},14,{_chatWidth}")
        };
        _layoutGrid.Children.Add(_card);
        _layoutGrid.Children.Add(_chatPanel);
        Grid.SetColumn(_chatPanel, 2);

        var backdrop = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(224, 6, 10, 20)),
            Child = _layoutGrid
        };
        // Beacon never dismisses on an outside click: an accidental click can
        // otherwise discard a verification code or an approval prompt.
        backdrop.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, backdrop)) e.Handled = true;
        };
        return backdrop;
    }

    private Border BuildChatPanel((string Accent, string Emoji, string Label) theme)
    {
        _chatStatusDot = new Border
        {
            Width = 7,
            Height = 7,
            Background = BrushFromHex("#35D49A"),
            CornerRadius = new CornerRadius(4)
        };
        _chatStatusText = new TextBlock
        {
            Text = "Live chat with Muse",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(158, 178, 210))
        };
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        statusRow.Children.Add(_chatStatusDot);
        var statusLabel = new TextBlock { Text = "  ", FontSize = 10.5 };
        statusRow.Children.Add(statusLabel);
        statusRow.Children.Add(_chatStatusText);

        _chatList = new ItemsControl
        {
            ItemTemplate = new FuncDataTemplate<ChatMessage>((message, _) => BuildChatBubble(message)),
            ItemsSource = App.Instance?.Chat.Messages
        };
        _chatScroll = new ScrollViewer
        {
            Content = _chatList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 2, 0)
        };

        _quickReplyPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        _quickReplyBar = new Border
        {
            IsVisible = false,
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 60, 92)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 10, 10, 6),
            Child = _quickReplyPanel
        };

        _chatRows = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        _chatRows.Children.Add(statusRow);
        _chatRows.Children.Add(_chatScroll);
        Grid.SetRow(_chatScroll, 1);
        _chatRows.Children.Add(_quickReplyBar);
        Grid.SetRow(_quickReplyBar, 2);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(12, 19, 34)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 60, 92)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14),
            Child = _chatRows,
            MaxHeight = 620
        };
    }

    private static Control BuildChatBubble(ChatMessage message)
    {
        var isUser = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
        var bubble = new Border
        {
            Background = isUser
                ? new SolidColorBrush(Color.FromRgb(29, 55, 88))
                : new SolidColorBrush(Color.FromRgb(20, 29, 49)),
            CornerRadius = isUser
                ? new CornerRadius(12, 12, 3, 12)
                : new CornerRadius(12, 12, 12, 3),
            Padding = new Thickness(10, 7),
            Margin = isUser ? new Thickness(36, 4, 0, 4) : new Thickness(0, 4, 36, 4),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 320
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (message.Avatar is not null)
        {
            row.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 9, 0),
                Child = new Image
                {
                    Source = message.Avatar,
                    Stretch = Stretch.UniformToFill
                }
            });
        }

        var column = new StackPanel();
        column.Children.Add(new TextBlock
        {
            Text = $"{message.RoleName} · {message.TimeLabel}",
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 171, 205))
        });
        column.Children.Add(new TextBlock
        {
            Text = message.Text,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(232, 238, 250)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        if (message.Streaming)
        {
            column.Children.Add(new TextBlock
            {
                Text = "Muse is typing…",
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(92, 155, 255)),
                Margin = new Thickness(0, 3, 0, 0)
            });
        }
        row.Children.Add(column);
        bubble.Child = row;
        return bubble;
    }

    // ------------------------------------------------------------ content/data

    private void ApplyContent((string Accent, string Emoji, string Label) theme)
    {
        var richText = _payload.Markdown ?? _payload.Html ?? _payload.Message;
        if (!string.IsNullOrWhiteSpace(richText))
        {
            _messageHost.Child = RichTextParser.ToTextBlock(richText);
        }
        else
        {
            _messageHost.IsVisible = false;
        }

        // The chat card is present for every popup, whatever the notification kind.
        if (_payload.AllowCustomReply)
        {
            var composer = BuildComposer();
            _chatRows.RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto");
            _chatRows.Children.Add(composer);
            Grid.SetRow(composer, 3);
        }

        if (!string.IsNullOrWhiteSpace(_payload.ImageSource))
            _ = LoadImageAsync(_payload.ImageSource!);

        if (_payload.Progress.HasValue)
        {
            var value = Math.Clamp(_payload.Progress.Value, 0, 100);
            _progressPercent.Text = $"{value:0}%";
            _progressFill.Width = 0;
            Dispatcher.UIThread.Post(() =>
            {
                var width = Math.Max(0, _progressPanel.Bounds.Width - 28);
                _progressFill.Width = width * value / 100.0;
            });
        }

        var footer = string.IsNullOrWhiteSpace(_payload.Footer)
            ? _payload.Mode == InteractionMode.None ? "Press Esc or choose an action to dismiss" : ""
            : _payload.Footer!;
        _footerLabel.Text = footer;
        _footerLabel.IsVisible = !string.IsNullOrWhiteSpace(footer);

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
            _actionPanel.Children.Add(CreateActionButton(action, automationId, false, action.Label));
        }

        _cancelButton = MakeButton(
            _payload.Mode == InteractionMode.None ? "Dismiss" : "Cancel",
            primary: false);
        _cancelButton.Click += (_, _) => Dismiss();
        _cancelButton.IsVisible = _payload.Dismissible || _payload.Mode != InteractionMode.None;
        _actionPanel.Children.Add(_cancelButton);
        _gatedControls.Add(_cancelButton);

        RebuildQuickReplies();
        ScrollChatToEnd();
    }

    private Border BuildComposer()
    {
        _replyBox = new TextBox
        {
            PlaceholderText = string.IsNullOrWhiteSpace(_payload.ReplyPlaceholder)
                ? "Message Muse…"
                : _payload.ReplyPlaceholder!,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 90,
            FontSize = 12
        };
        _replyBox.KeyDown += ReplyBox_KeyDown;

        _sendButton = MakeButton(
            string.IsNullOrWhiteSpace(_payload.ReplyButtonText) ? "Send" : _payload.ReplyButtonText!,
            primary: true,
            compact: true);
        _sendButton.Click += (_, _) => SendReply();

        _gatedControls.Add(_replyBox);
        _gatedControls.Add(_sendButton);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(_replyBox);
        row.Children.Add(_sendButton);
        Grid.SetColumn(_sendButton, 1);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 60, 92)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 0),
            Margin = new Thickness(0, 8, 0, 0),
            Child = row
        };
    }

    private Button CreateActionButton(
        NotificationAction action, string automationId, bool isQuickReply, string? displayLabel = null)
    {
        var primary = action.Primary || isQuickReply;
        var button = MakeButton(displayLabel ?? action.Label, primary, compact: isQuickReply);
        button.Tag = action;
        button.Name = automationId;
        button.Margin = new Thickness(0, 0, 8, 6);
        button.Click += ActionButton_Click;
        _gatedControls.Add(button);
        return button;
    }

    private Button MakeButton(string label, bool primary, bool compact = false) => new()
    {
        Content = label,
        Background = primary
            ? BrushFromHex(_accentHex, 235)
            : new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
        Foreground = primary
            ? new SolidColorBrush(Color.FromRgb(8, 14, 26))
            : new SolidColorBrush(Color.FromRgb(224, 232, 248)),
        BorderBrush = primary
            ? _accent
            : new SolidColorBrush(Color.FromArgb(70, 120, 150, 200)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(compact ? 8 : 10),
        Padding = compact ? new Thickness(10, 6) : new Thickness(13, 8),
        Margin = new Thickness(0, 0, 8, 0),
        FontSize = compact ? 11.5 : 12.5,
        FontWeight = FontWeight.SemiBold,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private void RebuildQuickReplies()
    {
        foreach (var old in _quickReplyButtons) _gatedControls.Remove(old);
        _quickReplyButtons.Clear();
        _quickReplyPanel.Children.Clear();

        var count = Math.Min(_quickReplyActions.Count, _quickReplyLimits.MaxButtons);
        for (var i = 0; i < count; i++)
        {
            var action = _quickReplyActions[i];
            var button = CreateActionButton(action, $"ReplyButton{i}", true, _quickReplyLimits.Fit(action.Label));
            button.IsEnabled = _unlocked;
            _quickReplyPanel.Children.Add(button);
            _quickReplyButtons.Add(button);
        }
        _quickReplyBar.IsVisible = count > 0;
    }

    // -------------------------------------------------------------- geometry

    private void SizeAndPlaceOnDisplay()
    {
        if (_closing) return;
        try
        {
            _screens = TopLevel.GetTopLevel(this)?.Screens ?? _screens;
            var screens = _screens;
            var all = screens?.All ?? Array.Empty<Screen>();
            if (all.Count == 0)
            {
                Width = Math.Max(900, Width);
                Height = Math.Max(620, Height);
                ApplyDisplayLayout(Width, Height);
                return;
            }

            var rects = all.Select(s => new ScreenRect(
                s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, s.IsPrimary)).ToArray();

            // MonitorSelector falls back to the primary display when the
            // pointer is unknown (int.MinValue lies outside every display).
            var pointer = _lastPointerPixel ?? new PixelPoint(int.MinValue, int.MinValue);
            var picked = MonitorSelector.Pick(rects, pointer.X, pointer.Y);
            var pickedScreen = picked is null
                ? null
                : all.FirstOrDefault(s =>
                    s.Bounds.X == picked.Left && s.Bounds.Y == picked.Top &&
                    s.Bounds.Width == picked.Width && s.Bounds.Height == picked.Height);
            var target = _lastPointerPixel.HasValue
                ? screens?.ScreenFromPoint(_lastPointerPixel.Value)
                : null;
            target ??= pickedScreen ?? screens?.Primary ?? all[0];

            var scale = Math.Max(1.0, target.Scaling > 0 ? target.Scaling : 1.0);
            var area = target.WorkingArea.Width > 0 && target.WorkingArea.Height > 0
                ? target.WorkingArea
                : target.Bounds;

            Position = new PixelPoint(area.X, area.Y);
            Width = Math.Max(360, area.Width / scale);
            Height = Math.Max(300, area.Height / scale);
            ApplyDisplayLayout(Width, Height);
        }
        catch
        {
            // Layout still works with the default window size if screens are
            // unavailable (for example in a headless test host).
        }
    }

    private void ApplyDisplayLayout(double screenWidth, double screenHeight)
    {
        if (_closing || screenWidth <= 0 || screenHeight <= 0) return;

        var availableWidth = Math.Max(360, screenWidth - 48);
        var chatWidth = Math.Clamp(availableWidth * 0.34, 230, 380);
        var cardWidth = Math.Clamp(availableWidth - chatWidth - 14, 320, 560);
        if (cardWidth + chatWidth + 14 > availableWidth)
        {
            var ratio = availableWidth / (cardWidth + chatWidth + 14);
            cardWidth *= ratio;
            chatWidth *= ratio;
        }

        _cardWidth = cardWidth;
        _chatWidth = chatWidth;
        _layoutGrid.ColumnDefinitions = new ColumnDefinitions($"{_cardWidth:0},14,{_chatWidth:0}");

        var maxHeight = Math.Max(360, screenHeight - 36);
        _card.MaxHeight = maxHeight;
        _chatPanel.MaxHeight = maxHeight;
        _chatScroll.MaxHeight = Math.Clamp(maxHeight - 220, 120, 520);

        var limits = QuickReplyLimits.ForPanelWidth(chatWidth);
        if (limits != _quickReplyLimits)
        {
            _quickReplyLimits = limits;
            RebuildQuickReplies();
        }
    }

    private void Window_PointerMoved(object? sender, PointerEventArgs e)
    {
        try { _lastPointerPixel = this.PointToScreen(e.GetPosition(this)); }
        catch { /* pointer tracking is only a display-selection hint */ }
    }

    private void Screens_Changed(object? sender, EventArgs e)
    {
        if (_closing) return;
        if (Dispatcher.UIThread.CheckAccess()) SizeAndPlaceOnDisplay();
        else Dispatcher.UIThread.Post(SizeAndPlaceOnDisplay);
    }

    // ------------------------------------------------------------ interaction

    private void StartSafetyLock()
    {
        if (_gatedControls.Count == 0)
        {
            _unlocked = true;
            return;
        }

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
            _footerLabel.Text = $"Actions unlock in {remaining:0}s…";
            _footerLabel.IsVisible = true;
            return;
        }

        _unlocked = true;
        foreach (var control in _gatedControls) control.IsEnabled = true;
        _footerLabel.Text = _footerText;
        _footerLabel.IsVisible = !string.IsNullOrWhiteSpace(_footerText);
        _safetyTimer?.Stop();
    }

    private void CountdownTick(object? sender, EventArgs e)
    {
        UpdateCountdown();
        // dismissible:false means "keep until cleared", including auto-dismiss.
        if (DateTime.Now >= _expiresAt && (_payload.Dismissible || _payload.Mode != InteractionMode.None))
            ClosePopup();
    }

    private void UpdateCountdown()
    {
        if (!_payload.Duration.HasValue) return;
        var total = Math.Max(1, _payload.Duration.Value.TotalMilliseconds);
        var remaining = Math.Clamp((_expiresAt - DateTime.Now).TotalMilliseconds / total, 0, 1);
        var width = Math.Max(0, _countdownTrack.Bounds.Width);
        _countdownFill.Width = width * remaining;
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_closing || !_unlocked || (!_payload.Dismissible && !_replySent)) return;
        e.Handled = true;
        Dismiss();
    }

    private void ReplyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (_unlocked) SendReply();
    }

    private void ActionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_unlocked || (sender as Button)?.Tag is not NotificationAction action) return;

        if (_payload.Mode == InteractionMode.Shell)
        {
            var kind = action.IsApprove ? "approved" : "denied";
            _ = CompleteOnceAsync(new InteractionResult
            {
                Kind = kind,
                Value = _payload.Command,
                Label = action.Label
            });
            ClosePopup();
            return;
        }

        if (action.ReplyValue is not null)
        {
            _ = CompleteReplyAsync(action.ReplyValue, action.Label);
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(action.Url))
            {
                PlatformServices.OpenUrl(action.Url!);
            }
            else if (!string.IsNullOrWhiteSpace(action.CopyText))
            {
                _ = SetClipboardAsync(action.CopyText!);
                if (sender is Button button) button.Content = "Copied!";
                return;
            }
        }
        catch { /* invalid links never block dismissal */ }

        _ = CompleteOnceAsync(new InteractionResult
        {
            Kind = "action",
            Value = action.Label,
            Label = action.Label
        });
        ClosePopup();
    }

    private void SendReply()
    {
        if (!_unlocked || _replyBox is null) return;
        var text = _replyBox.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            _replyBox.Focus();
            return;
        }
        _ = CompleteReplyAsync(text, "Custom reply");
    }

    private void CopyCodeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_unlocked) return;
        var text = !string.IsNullOrWhiteSpace(_payload.Code) ? _payload.Code : _payload.Command;
        if (string.IsNullOrWhiteSpace(text)) return;

        _ = SetClipboardAsync(text!);
        _copyCodeButton.Content = "Copied";
        _copyResetTimer?.Stop();
        _copyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _copyResetTimer.Tick += (_, _) =>
        {
            _copyCodeButton.Content = "Copy";
            _copyResetTimer?.Stop();
        };
        _copyResetTimer.Start();
    }

    private void Dismiss()
    {
        if (!_unlocked || _closing) return;
        if (_payload.Mode == InteractionMode.None && !_payload.Dismissible && !_replySent) return;
        _ = CompleteOnceAsync(new InteractionResult
        {
            Kind = "cancelled",
            Label = "Dismissed"
        });
        ClosePopup();
    }

    private void ClosePopup()
    {
        if (_closing) return;
        _closing = true;
        _countdownTimer?.Stop();
        _safetyTimer?.Stop();
        _copyResetTimer?.Stop();
        Close();
    }

    private Task CompleteOnceAsync(InteractionResult result)
    {
        if (_interactionCompleted || App.Instance is null) return Task.CompletedTask;
        _interactionCompleted = true;
        return App.Instance.Interactions.CompleteAsync(_payload.Id, result);
    }

    private Task CompleteReplyAsync(string value, string label)
    {
        var app = App.Instance;
        if (app is null) return Task.CompletedTask;

        _replyCount++;
        _replySent = true;
        app.Chat.AddUser(value, _payload.Id);
        app.Chat.BeginMuseReply(_payload.Id);
        if (_replyBox is not null)
        {
            _replyBox.Text = "";
            _replyBox.Focus();
        }
        if (_cancelButton is not null) _cancelButton.Content = "Done";
        ScrollChatToEnd();

        var result = new InteractionResult { Kind = "reply", Value = value, Label = label };
        return _interactionCompleted
            ? app.Interactions.ReportFollowUpAsync(_payload, result, _replyCount)
            : CompleteOnceAsync(result);
    }

    private async Task SetClipboardAsync(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch { /* clipboard is unavailable in some headless sessions */ }
    }

    // ------------------------------------------------------------------- chat

    private void ChatService_Changed(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshChat();
        else Dispatcher.UIThread.Post(RefreshChat);
    }

    private void RefreshChat()
    {
        if (_closing) return;
        UpdateChatStatus();
        ScrollChatToEnd();
    }

    private void UpdateChatStatus()
    {
        if (App.Instance is null) return;
        var streaming = App.Instance.Chat.IsStreaming(_payload.Id);
        _chatStatusText.Text = !_replySent
            ? "Live chat with Muse"
            : streaming ? "Muse is replying…" : "Muse replied";
        _chatStatusDot.Background = streaming ? _accent : BrushFromHex("#35D49A");
    }

    private void ScrollChatToEnd()
    {
        void Scroll()
        {
            if (_closing) return;
            _chatScroll.ScrollToEnd();
        }
        if (Dispatcher.UIThread.CheckAccess()) Scroll();
        else Dispatcher.UIThread.Post(Scroll);
    }

    private void App_SettingsChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            if (_closing) return;
            _timeText.Text = ClockFormat.Format(_createdAt);
            App.Instance?.Chat.RefreshTimeLabels();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    // ------------------------------------------------------------------ image

    private async Task LoadImageAsync(string source)
    {
        try
        {
            var text = source.Trim();
            byte[]? bytes = null;

            if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = text.IndexOf(',');
                if (comma < 0) return;
                bytes = Convert.FromBase64String(text[(comma + 1)..]);
            }
            else if (Base64Pattern.IsMatch(text))
            {
                bytes = Convert.FromBase64String(text);
            }
            else if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                     (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                bytes = await ImageClient.GetByteArrayAsync(uri).ConfigureAwait(false);
            }
            else if (Uri.TryCreate(text, UriKind.Absolute, out var fileUri) &&
                     fileUri.Scheme == Uri.UriSchemeFile && File.Exists(fileUri.LocalPath))
            {
                bytes = File.ReadAllBytes(fileUri.LocalPath);
            }
            else if (File.Exists(text))
            {
                bytes = File.ReadAllBytes(Path.GetFullPath(text));
            }

            if (bytes is null || bytes.Length == 0) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);

            Dispatcher.UIThread.Post(() =>
            {
                if (_closing) return;
                _previewImage.Source = bitmap;
                _imagePanel.IsVisible = true;
            });
        }
        catch
        {
            // A broken image never prevents the notification from being shown.
        }
    }

    // ----------------------------------------------------------------- helpers

    private static (string Accent, string Emoji, string Label) ResolveTheme(
        NotificationKind kind, string? custom)
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
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 4 || hex[0] != '#') hex = "#5C9BFF";
        try
        {
            if (hex.Length == 4)
            {
                var r = Convert.ToByte($"{hex[1]}{hex[1]}", 16);
                var g = Convert.ToByte($"{hex[2]}{hex[2]}", 16);
                var b = Convert.ToByte($"{hex[3]}{hex[3]}", 16);
                return new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
            }
            var full = hex.Length > 7 ? hex[..7] : hex;
            return new SolidColorBrush(Color.FromArgb(
                alpha,
                Convert.ToByte(full[1..3], 16),
                Convert.ToByte(full[3..5], 16),
                Convert.ToByte(full[5..7], 16)));
        }
        catch
        {
            return new SolidColorBrush(Color.FromArgb(alpha, 92, 155, 255));
        }
    }
}
