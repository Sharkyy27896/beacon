using System.IO;
using System.Text.Json;

namespace Beacon.Services;

public sealed class AppSettings
{
    public int Port { get; set; } = 8765;
    public string BindAddress { get; set; } = "Any";
    public bool RequireAuth { get; set; } = true;
    public string Token { get; set; } = "";
    public int DefaultDurationSeconds { get; set; } = 20;
    public bool PlaySound { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    /// <summary>True for 21:59, false for 9:59 PM. Defaults to current behaviour.</summary>
    public bool Use24HourClock { get; set; } = true;
    /// <summary>When false, /shell and its aliases are rejected with HTTP 403.</summary>
    public bool AllowShell { get; set; } = true;
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Beacon");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static bool SettingsFileExists => File.Exists(SettingsPath);

    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch
        {
            settings = new AppSettings();
        }

        if (settings.Port is < 1 or > 65535) settings.Port = 8765;
        settings.BindAddress = settings.BindAddress is "Loopback" or "Any" ? settings.BindAddress : "Any";
        if (settings.DefaultDurationSeconds < 0) settings.DefaultDurationSeconds = 0;
        if (string.IsNullOrWhiteSpace(settings.Token)) settings.Token = NewToken();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, SettingsPath, true);
    }

    public static string NewToken() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..8];
}
