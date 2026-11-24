using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Beacon.Models;

namespace Beacon.Services;

public sealed class ElevatedRequest
{
    public string Command { get; set; } = "";
    public string Shell { get; set; } = "cmd";
    public string? Executable { get; set; }
    public List<string> Arguments { get; set; } = new();
    public string? WorkingDirectory { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public string ResultPath { get; set; } = "";
}

public sealed class ElevatedResult
{
    public string Kind { get; set; } = "executed";
    public string? StdOut { get; set; }
    public string? StdErr { get; set; }
    public int? ExitCode { get; set; }
    public long DurationMs { get; set; }
    public bool TimedOut { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Runs a command in a second copy of Beacon.exe launched with "runas". That
/// copy performs the actual execution and writes stdout/stderr/exit code to a
/// result file, which lets the unelevated tray app keep its normal UI.
/// </summary>
public static class ElevatedRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static void RunHelper(string requestPath)
    {
        ElevatedResult result;
        string? resultPath = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var request = JsonSerializer.Deserialize<ElevatedRequest>(File.ReadAllText(requestPath), JsonOptions)
                          ?? throw new InvalidDataException("Empty elevation request.");
            resultPath = request.ResultPath;
            result = ExecuteRequest(request, stopwatch);
        }
        catch (Exception ex)
        {
            result = new ElevatedResult { Kind = "failed", Error = ex.Message, DurationMs = stopwatch.ElapsedMilliseconds };
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(resultPath))
                File.WriteAllText(resultPath, JsonSerializer.Serialize(result, JsonOptions));
        }
        catch { /* nothing else we can do */ }
    }

    private static ElevatedResult ExecuteRequest(ElevatedRequest request, Stopwatch stopwatch)
    {
        var psi = BuildStartInfo(request);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory) && Directory.Exists(request.WorkingDirectory))
            psi.WorkingDirectory = request.WorkingDirectory;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the command process.");
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        var timeout = Math.Max(1, request.TimeoutSeconds) * 1000;
        var exited = process.WaitForExit(timeout);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* process may have exited */ }
            return new ElevatedResult
            {
                Kind = "timeout",
                TimedOut = true,
                StdOut = outTask.GetAwaiter().GetResult(),
                StdErr = errTask.GetAwaiter().GetResult(),
                Error = $"Command timed out after {request.TimeoutSeconds}s.",
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }

        return new ElevatedResult
        {
            Kind = "executed",
            StdOut = outTask.GetAwaiter().GetResult(),
            StdErr = errTask.GetAwaiter().GetResult(),
            ExitCode = process.ExitCode,
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static ProcessStartInfo BuildStartInfo(ElevatedRequest request)
    {
        var shell = (request.Shell ?? "cmd").Trim().ToLowerInvariant();
        var psi = new ProcessStartInfo();

        switch (shell)
        {
            case "powershell":
            case "ps":
            case "pwsh":
                psi.FileName = shell == "pwsh" ? "pwsh.exe" : "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(request.Command);
                break;

            case "direct":
            case "exe":
                psi.FileName = string.IsNullOrWhiteSpace(request.Executable) ? request.Command : request.Executable;
                foreach (var argument in request.Arguments) psi.ArgumentList.Add(argument);
                break;

            default:
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/s");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(request.Command);
                break;
        }
        return psi;
    }

    public static async Task<ElevatedResult> ExecuteAsync(NotificationPayload payload)
    {
        var stopwatch = Stopwatch.StartNew();
        var directory = Path.Combine(Path.GetTempPath(), "Beacon");
        Directory.CreateDirectory(directory);
        var requestPath = Path.Combine(directory, $"{payload.Id}.request.json");
        var resultPath = Path.Combine(directory, $"{payload.Id}.result.json");
        try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { /* ignore */ }

        var request = new ElevatedRequest
        {
            Command = payload.Command ?? "",
            Shell = payload.ShellName ?? "cmd",
            Executable = payload.Executable,
            Arguments = payload.Arguments,
            WorkingDirectory = payload.WorkingDirectory,
            TimeoutSeconds = Math.Max(1, payload.ExecutionTimeoutSeconds),
            ResultPath = resultPath
        };
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request, JsonOptions));

        if (!payload.Elevated)
        {
            RunHelper(requestPath);
            return ReadResult(resultPath) ?? new ElevatedResult { Kind = "failed", Error = "No result file was produced.", DurationMs = stopwatch.ElapsedMilliseconds };
        }

        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("Cannot locate Beacon.exe.");

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            psi.ArgumentList.Add("--run-elevated");
            psi.ArgumentList.Add(requestPath);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Administrator launch was cancelled.");
            var budget = TimeSpan.FromSeconds(request.TimeoutSeconds + 30);
            try
            {
                await process.WaitForExitAsync().WaitAsync(budget).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new ElevatedResult { Kind = "timeout", TimedOut = true, Error = "Administrator helper timed out.", DurationMs = stopwatch.ElapsedMilliseconds };
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ElevatedResult { Kind = "cancelled", Error = "The administrator prompt was declined.", DurationMs = stopwatch.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            return new ElevatedResult { Kind = "failed", Error = ex.Message, DurationMs = stopwatch.ElapsedMilliseconds };
        }

        return ReadResult(resultPath) ?? new ElevatedResult { Kind = "failed", Error = "No result file was produced.", DurationMs = stopwatch.ElapsedMilliseconds };
    }

    private static ElevatedResult? ReadResult(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var result = JsonSerializer.Deserialize<ElevatedResult>(File.ReadAllText(path), JsonOptions);
            if (result is not null) result.DurationMs = Math.Max(result.DurationMs, 0);
            return result;
        }
        catch { return null; }
    }
}
