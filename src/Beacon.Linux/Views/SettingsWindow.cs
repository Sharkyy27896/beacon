using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Beacon.Services;

namespace Beacon.Views;

public sealed class SettingsWindow : Window
{
    private static readonly Brush WindowBrush = new SolidColorBrush(Color.FromRgb(8, 13, 24));
    private static readonly Brush PanelBackground = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.FromRgb(23, 34, 56), 0),
            new GradientStop(Color.FromRgb(15, 23, 39), 1)
        }
    };
    private static readonly Brush PanelBorderBrush = new SolidColorBrush(Color.FromRgb(58, 76, 107));
    private static readonly Brush HeaderBrush = new SolidColorBrush(Color.FromRgb(28, 43, 70));
    private static readonly Brush SectionBorderBrush = new SolidColorBrush(Color.FromRgb(38, 52, 81));
    private static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(16, 27, 46));
    private static readonly Brush FieldBackground = new SolidColorBrush(Color.FromRgb(13, 22, 38));
    private static readonly Brush FieldBorderBrush = new SolidColorBrush(Color.FromRgb(51, 70, 101));
    private static readonly Brush ButtonBrush = new SolidColorBrush(Color.FromRgb(38, 53, 80));
    private static readonly Brush ButtonBorderBrush = new SolidColorBrush(Color.FromRgb(59, 80, 114));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(74, 140, 247));
    private static readonly Brush AccentBorderBrush = new SolidColorBrush(Color.FromRgb(111, 166, 255));
    private static readonly Brush IconBrush = new SolidColorBrush(Color.FromRgb(44, 85, 144));
    private static readonly Brush IconGlyphBrush = new SolidColorBrush(Color.FromRgb(191, 224, 255));
    private static readonly Brush TitleBrush = new SolidColorBrush(Color.FromRgb(242, 247, 255));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(230, 238, 252));
    private static readonly Brush TextSoftBrush = new SolidColorBrush(Color.FromRgb(198, 215, 240));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(127, 156, 198));
    private static readonly Brush HintBrush = new SolidColorBrush(Color.FromRgb(140, 163, 197));
    private static readonly Brush OnlineBrush = new SolidColorBrush(Color.FromRgb(53, 212, 154));
    private static readonly Brush OfflineBrush = new SolidColorBrush(Color.FromRgb(255, 100, 115));

    private readonly App _app = App.Instance;
    private readonly SettingsService _settingsService = new();

    private readonly TextBlock _versionText = CreateText("VersionText", "", 12, HintBrush);
    private readonly Ellipse _statusDot = new()
    {
        Name = "StatusDot",
        Width = 9,
        Height = 9,
        Fill = OnlineBrush,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _statusText = CreateText("StatusText", "Listener stopped", 12, TextSoftBrush);
    private readonly TextBox _portBox = CreateTextBox("PortBox", "8765", 110);
    private readonly ComboBox _bindCombo = CreateComboBox("BindCombo", "Any interface", "Loopback only");
    private readonly CheckBox _authCheck = CreateCheckBox("AuthCheck", "Require a secret token (recommended)", true);
    private readonly TextBox _tokenBox = CreateTextBox("TokenBox", "", null);
    private readonly TextBox _durationBox = CreateTextBox("DurationBox", "20", 86);
    private readonly ComboBox _clockCombo = CreateComboBox("ClockCombo", "24-hour (21:59)", "12-hour (9:59 PM)");
    private readonly CheckBox _soundCheck = CreateCheckBox("SoundCheck", "Play a sound", true);
    private readonly CheckBox _autostartCheck = CreateCheckBox("AutoStartCheck", "Start with Linux session", false);
    private readonly CheckBox _shellCheck = CreateCheckBox("ShellCheck", "Allow shell requests (/shell)", true);
    private readonly TextBlock _updateStatusText = CreateText("UpdateStatusText", "Check for a newer Beacon release.", 12, MutedBrush);
    private readonly Button _updateButton = CreateButton("UpdateButton", "Check for updates");
    private readonly TextBox _urlBox = CreateTextBox("UrlBox", "", null);
    private readonly TextBlock _hintText = CreateText("HintText", "Press Enter in a field to save.", 11, HintBrush);

    private UpdateInfo? _availableUpdate;
    private bool _updateBusy;

    public SettingsWindow()
    {
        Title = "Beacon settings";
        Width = 720;
        Height = 680;
        MinWidth = 560;
        MinHeight = 520;
        MaxWidth = 1200;
        MaxHeight = 1000;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = WindowBrush;

        var settings = _app.Settings;
        _versionText.Text = $"Notification bridge · v{App.AppVersion}";
        _statusText.Margin = new Thickness(10, 0, 0, 0);
        _portBox.Text = settings.Port.ToString();
        _bindCombo.SelectedIndex = settings.BindAddress == "Loopback" ? 1 : 0;
        _authCheck.IsChecked = settings.RequireAuth;
        _tokenBox.Text = settings.Token;
        _tokenBox.FontFamily = new FontFamily("monospace");
        _tokenBox.FontSize = 12;
        _durationBox.Text = settings.DefaultDurationSeconds.ToString();
        _clockCombo.SelectedIndex = settings.Use24HourClock ? 0 : 1;
        _soundCheck.IsChecked = settings.PlaySound;
        _autostartCheck.IsChecked = settings.StartWithWindows;
        _shellCheck.IsChecked = settings.AllowShell;
        _urlBox.FontFamily = new FontFamily("monospace");
        _urlBox.FontSize = 12;
        _urlBox.IsReadOnly = true;
        _updateStatusText.TextWrapping = TextWrapping.Wrap;
        _updateStatusText.Margin = new Thickness(0, 0, 10, 0);
        _hintText.Margin = new Thickness(14, 0, 14, 0);

        WireSelectAll(_portBox);
        WireSelectAll(_tokenBox);
        WireSelectAll(_durationBox);
        _portBox.TextChanged += (_, _) => UpdateUrl();
        _bindCombo.SelectionChanged += (_, _) => UpdateUrl();

        Content = BuildContent();
        UpdateUrl();
        RefreshStatus();
        KeyDown += OnKeyDown;
    }

    private Control BuildContent()
    {
        var shell = new Border
        {
            Name = "Shell",
            Margin = new Thickness(8),
            Background = PanelBackground,
            BorderBrush = PanelBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Star });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader();
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            Name = "SettingsScroll",
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(28, 0, 28, 8)
        };
        scroll.Content = BuildBody();
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        shell.Child = root;
        return shell;
    }

    private Border BuildHeader()
    {
        var header = new Border
        {
            Name = "DragHeader",
            Padding = new Thickness(26, 18, 22, 14),
            Background = HeaderBrush,
            BorderBrush = SectionBorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        header.PointerPressed += Header_PointerPressed;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Name = "HeaderIcon",
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(11),
            Background = IconBrush
        };
        icon.Child = new TextBlock
        {
            Text = "◔",
            FontSize = 24,
            Foreground = IconGlyphBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0)
        };
        grid.Children.Add(icon);

        var titleStack = new StackPanel
        {
            Name = "HeaderTitleStack",
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        titleStack.Children.Add(CreateText("TitleText", "Beacon", 20, TitleBrush, FontWeight.SemiBold));
        titleStack.Children.Add(_versionText);
        Grid.SetColumn(titleStack, 1);
        grid.Children.Add(titleStack);

        var closeButton = CreateButton("CloseButton", "✕", onClick: (_, _) => Close(), width: 30);
        closeButton.Padding = new Thickness(0);
        closeButton.FontSize = 14;
        closeButton.PointerPressed += (_, e) => e.Handled = true;
        Grid.SetColumn(closeButton, 2);
        grid.Children.Add(closeButton);

        header.Child = grid;
        return header;
    }

    private StackPanel BuildBody()
    {
        var stack = new StackPanel
        {
            Name = "SettingsStack",
            Orientation = Orientation.Vertical
        };

        var statusCard = new Border
        {
            Name = "StatusCard",
            Background = CardBrush,
            BorderBrush = FieldBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 10)
        };
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        statusRow.Children.Add(_statusDot);
        statusRow.Children.Add(_statusText);
        statusCard.Child = statusRow;
        stack.Children.Add(statusCard);

        stack.Children.Add(CreateSectionLabel("LISTENER"));
        var listenerGrid = new Grid();
        listenerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        listenerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        listenerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var portLabel = CreateText("PortLabel", "Port", 13, TextSoftBrush);
        portLabel.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(portLabel, 0);
        listenerGrid.Children.Add(portLabel);

        _portBox.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(_portBox, 1);
        listenerGrid.Children.Add(_portBox);

        _bindCombo.Width = 210;
        _bindCombo.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_bindCombo, 2);
        listenerGrid.Children.Add(_bindCombo);
        stack.Children.Add(listenerGrid);

        var restartButton = CreateButton("RestartButton", "Restart listener", onClick: RestartButton_Click);
        restartButton.HorizontalAlignment = HorizontalAlignment.Left;
        restartButton.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(restartButton);

        stack.Children.Add(CreateSectionLabel("SECURITY"));
        _authCheck.Margin = new Thickness(0, 2, 0, 0);
        stack.Children.Add(_authCheck);

        var tokenGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        tokenGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        tokenGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tokenGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_tokenBox, 0);
        tokenGrid.Children.Add(_tokenBox);

        var regenButton = CreateButton("RegenTokenButton", "New token", onClick: RegenToken_Click);
        regenButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(regenButton, 1);
        tokenGrid.Children.Add(regenButton);

        var copyTokenButton = CreateButton("CopyTokenButton", "Copy", onClick: CopyToken_Click);
        copyTokenButton.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(copyTokenButton, 2);
        tokenGrid.Children.Add(copyTokenButton);
        stack.Children.Add(tokenGrid);

        stack.Children.Add(CreateSectionLabel("BEHAVIOR"));
        var durationGrid = new Grid();
        durationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        durationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        durationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });

        var durationLabel = CreateText("DurationLabel", "Auto-dismiss after", 13, TextSoftBrush);
        durationLabel.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(durationLabel, 0);
        durationGrid.Children.Add(durationLabel);

        _durationBox.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(_durationBox, 1);
        durationGrid.Children.Add(_durationBox);

        var durationNote = CreateText("DurationNote", "seconds · 0 keeps notifications on screen", 12, MutedBrush);
        Grid.SetColumn(durationNote, 2);
        durationGrid.Children.Add(durationNote);
        stack.Children.Add(durationGrid);

        var clockGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        clockGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        clockGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });

        var clockLabel = CreateText("ClockLabel", "Time format", 13, TextSoftBrush);
        clockLabel.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(clockLabel, 0);
        clockGrid.Children.Add(clockLabel);

        _clockCombo.Width = 210;
        _clockCombo.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(_clockCombo, 1);
        clockGrid.Children.Add(_clockCombo);
        stack.Children.Add(clockGrid);

        var behaviorChecks = new StackPanel
        {
            Name = "BehaviorChecks",
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0)
        };
        _soundCheck.Margin = new Thickness(0, 0, 24, 0);
        behaviorChecks.Children.Add(_soundCheck);
        behaviorChecks.Children.Add(_autostartCheck);
        stack.Children.Add(behaviorChecks);

        _shellCheck.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_shellCheck);

        stack.Children.Add(CreateSectionLabel("UPDATES"));
        var updateGrid = new Grid();
        updateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        updateGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_updateStatusText, 0);
        updateGrid.Children.Add(_updateStatusText);
        Grid.SetColumn(_updateButton, 1);
        updateGrid.Children.Add(_updateButton);
        stack.Children.Add(updateGrid);
        _updateButton.Click += UpdateButton_Click;

        stack.Children.Add(CreateSectionLabel("SEND FROM MUSE / ANY DEVICE"));
        var urlGrid = new Grid();
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_urlBox, 0);
        urlGrid.Children.Add(_urlBox);

        var copyUrlButton = CreateButton("CopyUrlButton", "Copy", onClick: CopyUrl_Click);
        copyUrlButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(copyUrlButton, 1);
        urlGrid.Children.Add(copyUrlButton);
        stack.Children.Add(urlGrid);

        var endpointNote = CreateText("EndpointNote",
            "POST JSON to this URL. Add the token as a Bearer header, ?token=..., or X-Auth-Token header.",
            12, MutedBrush);
        endpointNote.Margin = new Thickness(0, 8, 0, 0);
        endpointNote.TextWrapping = TextWrapping.Wrap;
        endpointNote.VerticalAlignment = VerticalAlignment.Top;
        stack.Children.Add(endpointNote);

        return stack;
    }

    private Border BuildFooter()
    {
        var footer = new Border
        {
            Name = "Footer",
            Padding = new Thickness(28, 12, 28, 20),
            BorderBrush = SectionBorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var testButton = CreateButton("TestButton", "Send test", onClick: TestButton_Click);
        Grid.SetColumn(testButton, 0);
        grid.Children.Add(testButton);

        Grid.SetColumn(_hintText, 1);
        grid.Children.Add(_hintText);

        var saveButton = CreateButton("SaveButton", "Save settings", onClick: SaveButton_Click, primary: true);
        Grid.SetColumn(saveButton, 2);
        grid.Children.Add(saveButton);

        footer.Child = grid;
        return footer;
    }

    private static TextBlock CreateText(string name, string text, double size = 13,
        Brush? foreground = null, FontWeight? weight = null)
    {
        return new TextBlock
        {
            Name = name,
            Text = text,
            FontSize = size,
            Foreground = foreground ?? TextBrush,
            FontWeight = weight ?? FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static TextBlock CreateSectionLabel(string text)
    {
        var label = CreateText("Section_" + text.Replace(" ", "_"), text, 11, MutedBrush, FontWeight.Bold);
        label.Margin = new Thickness(0, 16, 0, 6);
        return label;
    }

    private static TextBox CreateTextBox(string name, string text, double? width)
    {
        var box = new TextBox
        {
            Name = name,
            Text = text,
            Background = FieldBackground,
            Foreground = TextBrush,
            BorderBrush = FieldBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 7),
            FontSize = 13,
            CaretBrush = AccentBorderBrush,
            SelectionBrush = AccentBrush,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (width.HasValue) box.Width = width.Value;
        return box;
    }

    private static ComboBox CreateComboBox(string name, params string[] items)
    {
        return new ComboBox
        {
            Name = name,
            ItemsSource = items,
            Background = FieldBackground,
            Foreground = TextBrush,
            BorderBrush = FieldBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 6),
            FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private static CheckBox CreateCheckBox(string name, string content, bool isChecked)
    {
        return new CheckBox
        {
            Name = name,
            Content = content,
            IsChecked = isChecked,
            Foreground = TextBrush,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
    }

    private static Button CreateButton(string name, string content,
        EventHandler<RoutedEventArgs>? onClick = null, bool primary = false, double? width = null)
    {
        var button = new Button
        {
            Name = name,
            Content = content,
            Background = primary ? AccentBrush : ButtonBrush,
            Foreground = primary ? TitleBrush : TextBrush,
            BorderBrush = primary ? AccentBorderBrush : ButtonBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = primary ? new Thickness(16, 9) : new Thickness(12, 7),
            FontSize = primary ? 13 : 12,
            FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        if (width.HasValue) button.Width = width.Value;
        if (onClick is not null) button.Click += onClick;
        return button;
    }

    private static void WireSelectAll(TextBox box) =>
        box.GotFocus += (_, _) => box.SelectAll();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.Source is TextBox)
        {
            if (SaveSettings()) RefreshStatus();
            e.Handled = true;
        }
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void Header_PointerMoved(object? sender, PointerEventArgs e)
    {
        // BeginMoveDrag owns the drag interaction once PointerPressed starts it;
        // keeping the handler wired preserves header pointer tracking on Linux.
    }


    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (SaveSettings()) RefreshStatus();
    }

    private void RestartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!SaveSettings()) return;
        _app.RestartListener();
        RefreshStatus();
    }

    private bool SaveSettings()
    {
        if (!int.TryParse(_portBox.Text?.Trim(), out var port) || port is < 1 or > 65535)
        {
            _hintText.Text = "Port must be a number between 1 and 65535.";
            return false;
        }
        if (!int.TryParse(_durationBox.Text?.Trim(), out var duration) || duration < 0)
        {
            _hintText.Text = "Duration must be 0 or more seconds.";
            return false;
        }

        var oldPort = _app.Settings.Port;
        var oldBindAddress = _app.Settings.BindAddress;
        var bindAddress = _bindCombo.SelectedIndex == 1 ? "Loopback" : "Any";
        var listenerChanged = oldPort != port ||
                              !string.Equals(oldBindAddress, bindAddress, StringComparison.Ordinal);

        var tokenText = _tokenBox.Text?.Trim();
        _app.Settings.Port = port;
        _app.Settings.BindAddress = bindAddress;
        _app.Settings.RequireAuth = _authCheck.IsChecked == true;
        _app.Settings.Token = string.IsNullOrWhiteSpace(tokenText)
            ? SettingsService.NewToken()
            : tokenText!;
        _app.Settings.DefaultDurationSeconds = duration;
        _app.Settings.PlaySound = _soundCheck.IsChecked == true;
        _app.Settings.StartWithWindows = _autostartCheck.IsChecked == true;
        _app.Settings.Use24HourClock = _clockCombo.SelectedIndex != 1;
        _app.Settings.AllowShell = _shellCheck.IsChecked == true;

        try
        {
            _settingsService.Save(_app.Settings);
        }
        catch (Exception ex)
        {
            _hintText.Text = $"Could not save settings: {ex.Message}";
            return false;
        }

        _app.NotifySettingsChanged();

        var autostartFailed = false;
        try
        {
            autostartFailed = !PlatformServices.SetAutostart(_app.Settings.StartWithWindows);
        }
        catch
        {
            autostartFailed = true;
        }

        _tokenBox.Text = _app.Settings.Token;
        UpdateUrl();

        if (listenerChanged)
        {
            _app.RestartListener();
            RefreshStatus();
            _hintText.Text = "Saved · listener restarted" +
                (autostartFailed ? " · session autostart unavailable" : "");
            return true;
        }

        _hintText.Text = autostartFailed
            ? "Saved · session autostart unavailable"
            : "Saved";
        return true;
    }

    private void RegenToken_Click(object? sender, RoutedEventArgs e)
    {
        _tokenBox.Text = SettingsService.NewToken();
        _hintText.Text = "New token generated — press Save to apply.";
    }

    private async void CopyToken_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            _hintText.Text = "Clipboard unavailable";
            return;
        }

        try
        {
            await clipboard.SetTextAsync(_tokenBox.Text ?? string.Empty);
            _hintText.Text = "Token copied";
        }
        catch
        {
            _hintText.Text = "Clipboard unavailable";
        }
    }

    private async void CopyUrl_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            _hintText.Text = "Clipboard unavailable";
            return;
        }

        try
        {
            await clipboard.SetTextAsync(_urlBox.Text ?? string.Empty);
            _hintText.Text = "URL copied";
        }
        catch
        {
            _hintText.Text = "Clipboard unavailable";
        }
    }

    private void TestButton_Click(object? sender, RoutedEventArgs e)
    {
        _app.SendTestNotification();
        _hintText.Text = "Test notification sent";
    }

    private async void UpdateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        _updateButton.IsEnabled = false;

        try
        {
            if (_availableUpdate is not null)
            {
                var update = _availableUpdate;
                _hintText.Text = "Downloading update";
                _updateStatusText.Text = "Downloading update…";
                var progress = new Progress<(int Percent, string Text)>(p =>
                    _updateStatusText.Text = $"{p.Text} {p.Percent}%");

                var message = await UpdateService.DownloadAndApplyAsync(update, progress);
                _updateStatusText.Text = message;
                _availableUpdate = null;
                _updateButton.Content = "Check for updates";
                return;
            }

            _updateStatusText.Text = "Checking GitHub…";
            var found = await UpdateService.CheckAsync(App.AppVersion);
            if (found is null)
            {
                _updateStatusText.Text = $"You're up to date (v{App.AppVersion}).";
            }
            else
            {
                _availableUpdate = found;
                _updateStatusText.Text = $"v{found.Version} is available — click Update.";
                _updateButton.Content = $"Update to {found.Version}";
            }
        }
        catch (Exception ex)
        {
            _updateStatusText.Text = _availableUpdate is null
                ? $"Check failed: {ex.Message}"
                : $"Update failed: {ex.Message}";
        }
        finally
        {
            _updateBusy = false;
            _updateButton.IsEnabled = true;
        }
    }

    private void UpdateUrl()
    {
        if (!int.TryParse(_portBox.Text?.Trim(), out var port)) port = 8765;
        var loopback = _bindCombo.SelectedIndex == 1 || _app.Settings.BindAddress == "Loopback";
        _urlBox.Text = $"http://{NetworkHelper.GetAdvertisedAddress(loopback)}:{port}/notify";
    }

    private void RefreshStatus()
    {
        if (_app.IsListening)
        {
            _statusDot.Fill = OnlineBrush;
            var address = _app.Settings.BindAddress == "Loopback" ? "127.0.0.1" : "0.0.0.0";
            _statusText.Text = $"Listening on {address}:{_app.Settings.Port}";
        }
        else
        {
            _statusDot.Fill = OfflineBrush;
            _statusText.Text = "Listener stopped — change settings and press Restart";
        }
    }
}
