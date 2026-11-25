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
            var options = InstallOptions.FromInstallDirectory(ResolveInstallDirectory(e.Args));
            _ = Task.Run(() => InstallerEngine.Uninstall(options, progress))
                .ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            return;
        }

        var silent = e.Args.Any(a =>
            a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        if (silent)
        {
            // Used by Beacon's one-click updater. No UI is shown.
            var progress = new Progress<(int Value, string Text)>(_ => { });
            var options = InstallOptions.FromInstallDirectory(ResolveInstallDirectory(e.Args));
            _ = Task.Run(() => InstallerEngine.Install(options, progress, repair: true))
                .ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// Silent repairs must target the install that is actually running, which
    /// may be a custom folder. The updater passes /installdir explicitly; the
    /// copied uninstaller can be detected from its own process path or registry.
    /// </summary>
    private static string ResolveInstallDirectory(string[] args)
    {
        foreach (var argument in args)
        {
            if (argument.StartsWith("/installdir:", StringComparison.OrdinalIgnoreCase))
                return argument[12..].Trim('"');
            if (argument.StartsWith("/install-dir=", StringComparison.OrdinalIgnoreCase))
                return argument[13..].Trim('"');
            if (argument.StartsWith("--install-dir=", StringComparison.OrdinalIgnoreCase))
                return argument[14..].Trim('"');
        }

        return InstallerEngine.DetectCurrentInstallDirectory() ?? InstallOptions.Default.InstallDirectory;
    }
}
