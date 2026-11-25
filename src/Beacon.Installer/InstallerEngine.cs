using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace Beacon.Installer;

public sealed record InstallOptions(string InstallDirectory, string DataDirectory)
{
    public static InstallOptions Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Beacon"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Beacon"));

    /// <summary>Reads the paths file written by an existing install, if present.</summary>
    public static InstallOptions FromInstallDirectory(string installDirectory)
    {
        var data = Default.DataDirectory;
        var pathsFile = Path.Combine(installDirectory, "Beacon.paths.json");
        if (File.Exists(pathsFile))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(pathsFile));
                if (document.RootElement.TryGetProperty("DataDirectory", out var value))
                {
                    var custom = value.GetString();
                    if (!string.IsNullOrWhiteSpace(custom))
                        data = Path.GetFullPath(Environment.ExpandEnvironmentVariables(custom));
                }
            }
            catch
            {
                // Keep the default when the file is corrupt.
            }
        }

        return new InstallOptions(installDirectory, data);
    }

    public static InstallOptions LoadCurrent() => FromInstallDirectory(Default.InstallDirectory);
}

public sealed record InstallState(
    bool Found,
    Version? Version,
    string? Executable,
    string InstallDirectory);

/// <summary>
/// File copying, version checks and registry registration for the setup.
/// No telemetry, no network calls, no service manipulation.
/// </summary>
public static class InstallerEngine
{
    public const string TargetVersionText = "1.0.0";
    public static readonly Version TargetVersion = new(1, 0, 0);

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Beacon";

    /// <summary>
    /// Finds an existing Beacon install from the running executable or the
    /// uninstall registry entry. Used by silent repair/uninstall paths so a
    /// custom install directory is preserved instead of falling back to
    /// Program Files.
    /// </summary>
    public static string? DetectCurrentInstallDirectory()
    {
        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(processDirectory) &&
            File.Exists(Path.Combine(processDirectory, "Beacon.exe")))
            return processDirectory;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
            var location = key?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(location))
            {
                var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(location));
                if (File.Exists(Path.Combine(full, "Beacon.exe"))) return full;
            }
        }
        catch
        {
            // The registry may be unavailable from a non-elevated context.
        }

        return null;
    }

    public static InstallState Detect(InstallOptions options)
    {
        var exe = Path.Combine(options.InstallDirectory, "Beacon.exe");
        var exists = File.Exists(exe);
        Version? version = null;

        if (exists)
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            if (Version.TryParse(info.FileVersion, out var parsed)) version = parsed;
        }

        foreach (var (root, subKey) in UninstallRoots())
        {
            using var key = root.OpenSubKey(subKey);
            if (key is null) continue;

            foreach (var name in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(name);
                if (child is null) continue;
                if (!string.Equals(child.GetValue("DisplayName") as string, "Beacon",
                        StringComparison.OrdinalIgnoreCase)) continue;

                if (version is null && child.GetValue("DisplayVersion") is string v &&
                    Version.TryParse(v, out var fromRegistry)) version = fromRegistry;
            }
        }

        return new InstallState(exists || version is not null, version,
            exists ? exe : null, options.InstallDirectory);
    }

    public static void Install(InstallOptions options, IProgress<(int Value, string Text)> progress,
        bool repair = false)
    {
        var verb = repair ? "Repairing" : "Installing";

        progress.Report((4, "Checking existing installation..."));
        var state = Detect(options);

        if (state.Found && state.Version is not null && state.Version > TargetVersion)
            throw new InvalidOperationException(
                "A newer version of Beacon is already installed. This installer will not replace it.");

        progress.Report((10, "Closing any running Beacon window..."));
        StopBeacon();

        var temp = Path.Combine(Path.GetTempPath(), "beacon-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            progress.Report((32, "Unpacking Beacon..."));
            using (var stream = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("BeaconPayload.zip"))
            {
                if (stream is null)
                    throw new InvalidOperationException("Installer payload is missing from this build.");
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(temp, overwriteFiles: true);
            }

            progress.Report((62, "Copying files..."));
            Directory.CreateDirectory(options.InstallDirectory);
            foreach (var file in Directory.GetFiles(temp, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(temp, file);
                var destination = Path.Combine(options.InstallDirectory, relative);
                var folder = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.Copy(file, destination, overwrite: true);
            }

            WritePathsFile(options);

            progress.Report((76, "Creating Start menu shortcut..."));
            CreateShortcut(options.InstallDirectory);

            progress.Report((86, "Registering installation..."));
            Register(options);
            progress.Report((100, repair ? "Repair complete." : "Beacon 1.0.0 is ready."));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* temp cleanup is best effort */ }
        }
    }

    public static void Uninstall(InstallOptions options, IProgress<(int Value, string Text)> progress)
    {
        progress.Report((10, "Closing Beacon..."));
        StopBeacon();

        progress.Report((45, "Removing files..."));
        if (Directory.Exists(options.InstallDirectory))
        {
            foreach (var file in Directory.GetFiles(options.InstallDirectory, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { /* a running file may be locked until reboot */ }
            }
        }

        progress.Report((70, "Removing shortcut and registration..."));
        var shortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Beacon.lnk");
        try { if (File.Exists(shortcut)) File.Delete(shortcut); } catch { /* best effort */ }

        try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); }
        catch { /* best effort */ }

        progress.Report((100, "Beacon was removed."));
    }

    private static IEnumerable<(RegistryKey Root, string SubKey)> UninstallRoots()
    {
        yield return (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        yield return (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        yield return (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        yield return (Registry.CurrentUser, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
    }

    private static void StopBeacon()
    {
        foreach (var process in Process.GetProcessesByName("Beacon"))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exiting */ }
            finally { process.Dispose(); }
        }
    }

    private static void WritePathsFile(InstallOptions options)
    {
        var json = JsonSerializer.Serialize(new { DataDirectory = options.DataDirectory },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(options.InstallDirectory, "Beacon.paths.json"), json);
    }

    private static void CreateShortcut(string installDirectory)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Beacon.lnk");
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = Path.Combine(installDirectory, "Beacon.exe");
            shortcut.WorkingDirectory = installDirectory;
            shortcut.IconLocation = Path.Combine(installDirectory, "Beacon.exe") + ",0";
            shortcut.Description = "Beacon notification bridge";
            shortcut.Save();
        }
        catch
        {
            // Start menu shortcut is a convenience; installation still succeeds.
        }
    }

    private static void Register(InstallOptions options)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Beacon");
        key.SetValue("DisplayVersion", TargetVersionText);
        key.SetValue("Publisher", "Beacon");
        key.SetValue("DisplayIcon", Path.Combine(options.InstallDirectory, "Beacon.exe"));
        key.SetValue("InstallLocation", options.InstallDirectory);
        key.SetValue("UninstallString",
            $"\"{Path.Combine(options.InstallDirectory, "Beacon-Uninstall.exe")}\" /uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);

        // Copy this setup as a persistent uninstaller next to the app.
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self)) return;
            File.Copy(self, Path.Combine(options.InstallDirectory, "Beacon-Uninstall.exe"), overwrite: true);
        }
        catch { /* the app still uninstalls cleanly via the registry entry */ }
    }
}
