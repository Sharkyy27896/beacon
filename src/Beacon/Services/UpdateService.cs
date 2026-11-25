using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Beacon.Services;

public sealed record UpdateInfo(string Version, string Tag, string AssetName, string DownloadUrl, string Notes);

/// <summary>
/// Checks the GitHub Releases feed for a newer Beacon and applies it on demand.
/// The repository is public by release time, so no credentials are required.
/// </summary>
public static class UpdateService
{
    private const string Repository = "Sharkyy27896/beacon";
    private const string ApiUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    public static string PreferredAssetName =>
        OperatingSystem.IsWindows() ? "Beacon-Setup.exe" : "Beacon-x86_64.AppImage";

    public static async Task<UpdateInfo?> CheckAsync(string currentVersion,
        CancellationToken cancellationToken = default)
    {
        using var client = NewClient();
        using var response = await client.GetAsync(ApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var latest = tag.TrimStart('v', 'V');

        if (!IsNewer(latest, currentVersion)) return null;

        var assetName = PreferredAssetName;
        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), assetName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException($"Release {tag} does not contain {assetName}.");

        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(latest, tag, assetName, downloadUrl, notes);
    }

    public static async Task<string> DownloadAndApplyAsync(UpdateInfo update,
        IProgress<(int Percent, string Text)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Windows installs normally live under Program Files, which a
        // non-elevated app cannot write to. Stream the installer to a writable
        // temp path with a real .exe name so ShellExecute can run it elevated.
        // Linux keeps the running AppImage semantics: a sibling .new file that
        // is chmod'ed and atomically moved over the current image.
        var windows = OperatingSystem.IsWindows();
        string? target = null;
        string pending;

        if (windows)
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "Beacon");
            Directory.CreateDirectory(tempDirectory);
            pending = Path.Combine(tempDirectory, "Beacon-Setup.exe");
        }
        else
        {
            target = GetTargetPath();
            var directory = Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("Cannot determine the Beacon install location.");
            Directory.CreateDirectory(directory);
            pending = Path.Combine(directory, Path.GetFileName(target) + ".new");
        }

        var keepPending = false;
        try
        {
            using var client = NewClient();
            using var response = await client.GetAsync(update.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(pending, FileMode.Create, FileAccess.Write,
                             FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    readTotal += read;
                    var percent = total > 0 ? (int)(readTotal * 100 / total) : 0;
                    progress?.Report((percent, $"Downloading {update.AssetName}…"));
                }

                await destination.FlushAsync(cancellationToken);
            }

            if (windows)
            {
                // Tell the silent installer which install to repair. Without
                // this it would default to Program Files and leave custom
                // installs stale.
                var installDirectory = Path.GetDirectoryName(Environment.ProcessPath);
                var arguments = string.IsNullOrWhiteSpace(installDirectory)
                    ? "/silent"
                    : $"/silent \"/installdir:{installDirectory}\"";
                var start = new ProcessStartInfo(pending, arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(start);
                keepPending = true;
                progress?.Report((100, "Installer launched. Beacon will close in a moment."));
                return "Update launched. Approve the UAC prompt to finish.";
            }

            MakeExecutable(pending);
            File.Move(pending, target!, overwrite: true);
            progress?.Report((100, "Update applied."));
            return "Update applied. Restart Beacon to use the new version.";
        }
        finally
        {
            // Do not delete a launched installer: the elevated process may
            // still be loading it from this temp path.
            if (!keepPending && File.Exists(pending))
            {
                try { File.Delete(pending); } catch { /* best effort */ }
            }
        }
    }

    private static string GetTargetPath()
    {
        if (OperatingSystem.IsLinux())
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage)) return appImage;
        }

        var process = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(process))
            throw new InvalidOperationException("Cannot determine the running Beacon executable.");
        return process;
    }

    private static void MakeExecutable(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/bin/chmod", $"+x \"{path}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
        }
        catch
        {
            // The AppImage runtime may already mark the replacement executable.
        }
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Beacon/1.0.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(latest, out var latestVersion) ||
            !Version.TryParse(current, out var currentVersion)) return false;
        return latestVersion > currentVersion;
    }
}
