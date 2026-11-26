using System;
using System.Windows;
using System.Windows.Input;

namespace Beacon.Installer;

public partial class DowngradeConfirmWindow : Window
{
    public DowngradeConfirmWindow(Version current, Version target)
    {
        InitializeComponent();

        MessageText.Text =
            $"Current version is newer than the one you're trying to install ({current} > {target}). Continue?";

        HeaderGrid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
