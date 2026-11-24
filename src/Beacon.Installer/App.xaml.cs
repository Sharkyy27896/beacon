using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Application = System.Windows.Application;

namespace Beacon.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var uninstall = e.Args.Any(a =>
            a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));

        if (uninstall)
        {
            // Silent path used by the copied Beacon-Uninstall.exe. No UI is shown.
            var progress = new Progress<(int Value, string Text)>(_ => { });
            var options = InstallOptions.LoadCurrent();
            _ = Task.Run(() => InstallerEngine.Uninstall(options, progress))
                .ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            return;
        }

        base.OnStartup(e);
    }
}
