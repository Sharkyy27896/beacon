using System;
using System.IO;
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
            RunSilent(() => InstallerEngine.Uninstall(options, progress));
            return;
        }

        var silent = e.Args.Any(a =>
            a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        if (silent)
        {
            // Used by Beacon's one-click updater. No UI is shown, and downgrades
            // are refused unless an explicit flag is supplied by a confirmed UI.
            var progress = new Progress<(int Value, string Text)>(_ => { });
            var options = InstallOptions.FromInstallDirectory(ResolveInstallDirectory(e.Args));
            RunSilent(() => InstallerEngine.Install(options, progress, repair: true));
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// Silent callers get no dialog, so failures must be logged and reported
    /// through the exit code instead of vanishing into a discarded task.
    /// </summary>
    private void RunSilent(Action action)
    {
        _ = Task.Run(action).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                try
                {
                    var directory = Path.Combine(Path.GetTempPath(), "Beacon");
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(Path.Combine(directory, "setup.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {task.Exception}\n\n");
                }
                catch { /* logging is best effort */ }
                Environment.ExitCode = 1;
            }
            Dispatcher.Invoke(Shutdown);
        });
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
