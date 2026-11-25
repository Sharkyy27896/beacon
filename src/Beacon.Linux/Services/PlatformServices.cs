using System.Diagnostics;
using System.Text;

namespace Beacon.Services;

/// <summary>Small desktop integrations that differ between Windows and Linux.</summary>
public static class PlatformServices
{
    private const string DesktopFileName = "Beacon.desktop";

    /// <summary>Path of the running app, preferring the AppImage when available.</summary>
    public static string ExecutablePath
    {
        get
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage))
                return appImage;

            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
                return processPath;

            return Path.Combine(AppContext.BaseDirectory, "Beacon");
        }
    }

    /// <summary>Opens a link with the desktop's default browser, silently no-op on failure.</summary>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        if (TryStart("xdg-open", url)) return;
        if (TryStart("gio", "open", url)) return;
        if (TryStart("open", url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No browser or handler available in this session.
        }
    }

    /// <summary>
    /// Adds or removes the XDG autostart entry. Returns true when the requested
    /// state was applied (including removal of an entry that was already gone).
    /// </summary>
    public static bool SetAutostart(bool enabled)
    {
        try
        {
            var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configRoot))
                configRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

            var directory = Path.Combine(configRoot, "autostart");
            var path = Path.Combine(directory, DesktopFileName);

            if (!enabled)
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }

            Directory.CreateDirectory(directory);
            var exec = EscapeDesktopValue(ExecutablePath);
            var content = new StringBuilder()
                .AppendLine("[Desktop Entry]")
                .AppendLine("Type=Application")
                .AppendLine("Name=Beacon")
                .AppendLine("Comment=Beacon notification bridge")
                .AppendLine($"Exec={exec} --minimized")
                .AppendLine("Icon=beacon")
                .AppendLine("Terminal=false")
                .AppendLine("Categories=Utility;")
                .AppendLine("X-GNOME-Autostart-enabled=true")
                .ToString();

            File.WriteAllText(path, content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStart(string fileName, params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeDesktopValue(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            if (character is '\\' or '"' or '$' or '`')
                builder.Append('\\');
            builder.Append(character);
        }
        return $"\"{builder}\"";
    }
}
