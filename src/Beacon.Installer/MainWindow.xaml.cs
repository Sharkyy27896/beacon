using System;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Forms = System.Windows.Forms;
using TextBox = System.Windows.Controls.TextBox;

namespace Beacon.Installer;

public partial class MainWindow : Window
{
    private InstallOptions _options = InstallOptions.Default;
    private InstallState _state = DetectDefault();
    private bool _busy;
    private bool _done;

    public MainWindow()
    {
        InitializeComponent();

        HeaderGrid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        Loaded += MainWindow_Loaded;
    }

    private static InstallState DetectDefault() =>
        InstallerEngine.Detect(InstallOptions.Default);

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _options = InstallOptions.FromInstallDirectory(InstallOptions.Default.InstallDirectory);
        _state = InstallerEngine.Detect(_options);

        InstallPathBox.Text = _options.InstallDirectory;
        DataPathBox.Text = _options.DataDirectory;
        PathText.Text = _options.InstallDirectory;
        DataText.Text = _options.DataDirectory;
        InstalledText.Text = _state.Found
            ? _state.Version?.ToString() ?? "installed"
            : "Not installed";

        // When Beacon is already installed, the only UI change is the Repair button.
        RepairButton.Visibility = _state.Found ? Visibility.Visible : Visibility.Collapsed;

        ((Storyboard)FindResource("IntroStoryboard")).Begin(this);
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            Close();
            return;
        }
        await RunAsync(repair: false);
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e) => await RunAsync(repair: true);

    private async System.Threading.Tasks.Task RunAsync(bool repair)
    {
        if (_busy) return;

        var installDirectory = InstallPathBox.Text.Trim();
        var dataDirectory = DataPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installDirectory) || string.IsNullOrWhiteSpace(dataDirectory))
        {
            StatusText.Text = "Choose both folders.";
            return;
        }

        _options = new InstallOptions(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(installDirectory)),
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataDirectory)));

        _busy = true;
        RepairButton.IsEnabled = false;
        PrimaryButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        AdvancedToggle.IsEnabled = false;
        SetProgress(0);
        PercentText.Text = "";

        var progress = new Progress<(int Value, string Text)>(p =>
        {
            SetProgress(p.Value);
            StatusText.Text = p.Text;
            PercentText.Text = p.Value > 0 ? $"{p.Value}%" : "";
        });

        try
        {
            await System.Threading.Tasks.Task.Run(() =>
                InstallerEngine.Install(_options, progress, repair));

            _done = true;
            Headline.Text = repair ? "Repair complete" : "Beacon is ready";
            StatusText.Text = repair ? "Beacon was reinstalled at 1.0.0." : "Beacon 1.0.0 is ready.";
            PercentText.Text = "100%";
            CancelButton.Visibility = Visibility.Collapsed;
            RepairButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = "Close";
            PrimaryButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            PercentText.Text = "";
            PrimaryButton.Content = "Retry";
            PrimaryButton.IsEnabled = true;
            RepairButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            AdvancedToggle.IsEnabled = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetProgress(int value)
    {
        Progress.BeginAnimation(RangeBase.ValueProperty,
            new DoubleAnimation(value, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void AdvancedToggle_Click(object sender, RoutedEventArgs e)
    {
        if (AdvancedPanel.Visibility == Visibility.Visible)
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) => AdvancedPanel.Visibility = Visibility.Collapsed;
            AdvancedPanel.BeginAnimation(OpacityProperty, fade);
            AdvancedOffset.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, -6, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                });
            AdvancedToggle.Content = "Advanced options  +";
        }
        else
        {
            AdvancedPanel.Visibility = Visibility.Visible;
            AdvancedPanel.Opacity = 0;
            AdvancedOffset.Y = -6;
            AdvancedPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
            AdvancedOffset.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
            AdvancedToggle.Content = "Advanced options  −";
        }
    }

    private void BrowseInstall_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(InstallPathBox);

    private void BrowseData_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(DataPathBox);

    private static void BrowseFolder(TextBox target)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder",
            ShowNewFolderButton = true
        };
        if (!string.IsNullOrWhiteSpace(target.Text) && Directory.Exists(target.Text))
            dialog.SelectedPath = target.Text;

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            target.Text = dialog.SelectedPath;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
