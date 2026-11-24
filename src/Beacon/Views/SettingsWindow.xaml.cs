using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Beacon.Models;
using Beacon.Services;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace Beacon.Views;

public partial class SettingsWindow : Window
{
    private readonly App _app = App.Instance;
    private readonly SettingsService _settingsService = new();

    public SettingsWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Notification bridge · v{App.AppVersion}";
        var settings = _app.Settings;
        PortBox.Text = settings.Port.ToString();
        BindCombo.SelectedIndex = settings.BindAddress == "Loopback" ? 1 : 0;
        AuthCheck.IsChecked = settings.RequireAuth;
        TokenBox.Text = settings.Token;
        DurationBox.Text = settings.DefaultDurationSeconds.ToString();
        ClockCombo.SelectedIndex = settings.Use24HourClock ? 0 : 1;
        SoundCheck.IsChecked = settings.PlaySound;
        AutoStartCheck.IsChecked = settings.StartWithWindows;
        ShellCheck.IsChecked = settings.AllowShell;
        PortBox.TextChanged += (_, _) => UpdateUrl();
        UpdateUrl();
        RefreshStatus();
        Loaded += (_, _) =>
        {
            ApplyModernScrollbars(this);
            ((Storyboard)FindResource("IntroStoryboard")).Begin(this);
        };
    }

    private static void ApplyModernScrollbars(DependencyObject root)
    {
        var style = Application.Current?.Resources.Contains("ModernScrollBar") == true
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
            ApplyModernScrollbars(child);
        }
    }

    private void UpdateUrl()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port)) port = 8765;
        UrlBox.Text = $"http://{NetworkHelper.GetReachableAddress()}:{port}/notify";
    }

    private void RefreshStatus()
    {
        if (_app.IsListening)
        {
            StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(53, 212, 154));
            StatusText.Text = $"Listening on {(_app.Settings.BindAddress == "Loopback" ? "127.0.0.1" : "0.0.0.0")}:{_app.Settings.Port}";
        }
        else
        {
            StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 115));
            StatusText.Text = "Listener stopped — change settings and press Restart";
        }
    }

    private bool SaveSettings()
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            HintText.Text = "Port must be a number between 1 and 65535.";
            return false;
        }
        if (!int.TryParse(DurationBox.Text.Trim(), out var duration) || duration < 0)
        {
            HintText.Text = "Duration must be 0 or more seconds.";
            return false;
        }

        _app.Settings.Port = port;
        _app.Settings.BindAddress = BindCombo.SelectedIndex == 1 ? "Loopback" : "Any";
        _app.Settings.RequireAuth = AuthCheck.IsChecked == true;
        _app.Settings.Token = string.IsNullOrWhiteSpace(TokenBox.Text) ? SettingsService.NewToken() : TokenBox.Text.Trim();
        _app.Settings.DefaultDurationSeconds = duration;
        _app.Settings.PlaySound = SoundCheck.IsChecked == true;
        _app.Settings.StartWithWindows = AutoStartCheck.IsChecked == true;
        _app.Settings.AllowShell = ShellCheck.IsChecked == true;
        _app.Settings.Use24HourClock = ClockCombo.SelectedIndex != 1;
        _settingsService.Save(_app.Settings);
        _app.NotifySettingsChanged();
        SetAutoStart(_app.Settings.StartWithWindows);
        TokenBox.Text = _app.Settings.Token;
        UpdateUrl();
        HintText.Text = "Saved";
        return true;
    }

    private static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            if (enabled) key.SetValue("Beacon", $"\"{executable}\" --minimized");
            else key.DeleteValue("Beacon", false);
        }
        catch { /* registry may be restricted */ }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings()) return;
        if (!_app.IsListening) _app.RestartListener();
        RefreshStatus();
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings()) return;
        _app.RestartListener();
        RefreshStatus();
    }

    private void RegenToken_Click(object sender, RoutedEventArgs e)
    {
        TokenBox.Text = SettingsService.NewToken();
        HintText.Text = "New token generated — press Save to apply.";
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TokenBox.Text); HintText.Text = "Token copied"; }
        catch { HintText.Text = "Clipboard unavailable"; }
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(UrlBox.Text); HintText.Text = "URL copied"; }
        catch { HintText.Text = "Clipboard unavailable"; }
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        _app.SendTestNotification();
        HintText.Text = "Test notification sent";
    }

    private void Field_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox) textBox.SelectAll();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.OriginalSource is System.Windows.Controls.TextBox)
        {
            if (SaveSettings() && !_app.IsListening)
            {
                _app.RestartListener();
                RefreshStatus();
            }
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
