using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Beacon.Services;

public sealed record UpdateInfo(string Version, string Tag, string AssetName, string DownloadUrl,
    string Notes, string? Digest);

/// <summary>
/// Checks the GitHub Releases feed for a newer Beacon and applies it on demand.
/// The repository is public, so no credentials are required. Downgrades are not
/// offered here; they require running an older setup manually and confirming it.
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
        string? digest = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), assetName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                downloadUrl = asset.GetProperty("browser_download_url").GetString();
                if (asset.TryGetProperty("digest", out var digestValue)) digest = digestValue.GetString();
                break;
            }
        }

        // A release without this platform's asset is not an update for us.
        if (string.IsNullOrWhiteSpace(downloadUrl)) return null;

        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(latest, tag, assetName, downloadUrl, notes, digest);
    }

    public static async Task<string> DownloadAndApplyAsync(UpdateInfo update,
        IProgress<(int Percent, string Text)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Windows installs normally live under Program Files, which a
        // non-elevated app cannot write to. Stream the installer to a unique
        // writable temp path with a real .exe name so ShellExecute can run it
        // elevated. Linux keeps the running AppImage semantics: a sibling .new
        // file that is chmod'ed and atomically moved over the current image.
        var windows = OperatingSystem.IsWindows();
        string? target = null;
        string pending;

        if (windows)
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "Beacon");
            Directory.CreateDirectory(tempDirectory);
            pending = Path.Combine(tempDirectory, $"Beacon-Setup-{Guid.NewGuid():N}.exe");
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

            VerifyDigest(pending, update.Digest);

            if (windows)
            {
                // Tell the silent installer which install to repair. Without
                // this it would default to Program Files and leave custom
                // installs stale. The installer refuses to downgrade silently.
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

    /// <summary>GitHub publishes release asset hashes as "sha256:&lt;hex&gt;".</summary>
    private static void VerifyDigest(string path, string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest) ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return;

        var expected = digest[7..].Trim();
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The downloaded update failed its SHA-256 checksum check and was not applied.");
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
            if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0)
                throw new InvalidOperationException("Could not mark the downloaded AppImage executable.");
        }
        catch (InvalidOperationException) { throw; }
        catch
        {
            throw new InvalidOperationException("Could not mark the downloaded AppImage executable.");
        }
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var version = typeof(UpdateService).Assembly
                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? "1.0.1";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Beacon/{version}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static bool IsNewer(string latest, string current)
    {
        // GitHub tags may carry prerelease/build suffixes that Version rejects.
        static string NumericPart(string value)
        {
            var cut = value.IndexOfAny(new[] { '-', '+' });
            return cut < 0 ? value : value[..cut];
        }

        if (!Version.TryParse(NumericPart(latest), out var latestVersion) ||
            !Version.TryParse(NumericPart(current), out var currentVersion)) return false;
        return latestVersion > currentVersion;
    }
}
