using System.Diagnostics;
using Beacon.Models;

namespace Beacon.Services;

/// <summary>Serializable description of an approved shell request.</summary>
public sealed record ElevatedRequest(
    string Command = "",
    string Shell = "bash",
    string? Executable = null,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    int TimeoutSeconds = 120,
    string ResultPath = "");

/// <summary>Outcome of an approved shell request.</summary>
public sealed record ElevatedResult(
    string Kind = "executed",
    string? StdOut = null,
    string? StdErr = null,
    int? ExitCode = null,
    long DurationMs = 0,
    bool TimedOut = false,
    string? Error = null);

/// <summary>
/// Linux implementation of the approved-command runner. Commands are executed
/// with <c>/bin/bash -lc</c> (or directly when an executable is supplied) and
/// are never used to close applications, log out, reboot or shut down the PC.
/// Privilege escalation is attempted through <c>/usr/bin/pkexec</c> only when
/// the payload explicitly asks for it and pkexec is installed.
/// </summary>
public static class ElevatedRunner
{
    private const string PkexecPath = "/usr/bin/pkexec";
    private const string BashPath = "/bin/bash";

    public static Task<ElevatedResult> ExecuteAsync(NotificationPayload payload)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));

        var request = new ElevatedRequest(
            payload.Command ?? "",
            payload.ShellName ?? "bash",
            payload.Executable,
            payload.Arguments,
            payload.WorkingDirectory,
            Math.Clamp(payload.ExecutionTimeoutSeconds, 1, 600),
            "");

        return ExecuteRequestAsync(request, payload.Elevated);
    }

    /// <summary>Runs a request and optionally persists its result to ResultPath.</summary>
    public static async Task<ElevatedResult> ExecuteRequestAsync(ElevatedRequest request, bool elevated)
    {
        var stopwatch = Stopwatch.StartNew();
        ElevatedResult result;
        try
        {
            result = await RunAsync(request, elevated, stopwatch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new ElevatedResult
            {
                Kind = "failed",
                Error = ex.Message,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }

        if (!string.IsNullOrWhiteSpace(request.ResultPath))
        {
            try { File.WriteAllText(request.ResultPath, $"{result.Kind}\n{result.StdOut}\n{result.Error}"); }
            catch { /* result files are best-effort on Linux */ }
        }

        return result;
    }

    private static async Task<ElevatedResult> RunAsync(
        ElevatedRequest request, bool elevated, Stopwatch stopwatch)
    {
        var command = request.Command?.Trim() ?? "";
        var direct = (request.Shell?.Trim().ToLowerInvariant() is "direct" or "exe") ||
                     !string.IsNullOrWhiteSpace(request.Executable);
        if (!direct && string.IsNullOrWhiteSpace(command))
            return Fail("No command was supplied.", stopwatch);

        var usePkexec = elevated && File.Exists(PkexecPath);
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (usePkexec)
        {
            startInfo.FileName = PkexecPath;
            startInfo.ArgumentList.Add("--");
        }

        if (direct)
        {
            var executable = string.IsNullOrWhiteSpace(request.Executable) ? command : request.Executable!;
            startInfo.FileName = usePkexec ? startInfo.FileName : executable;
            if (usePkexec) startInfo.ArgumentList.Add(executable);
            if (request.Arguments is not null)
                foreach (var argument in request.Arguments)
                    startInfo.ArgumentList.Add(argument ?? "");
        }
        else
        {
            startInfo.FileName = usePkexec ? startInfo.FileName : BashPath;
            if (usePkexec) startInfo.ArgumentList.Add(BashPath);
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory) &&
            Directory.Exists(request.WorkingDirectory))
            startInfo.WorkingDirectory = request.WorkingDirectory;

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start the command process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var timeout = TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* the process may have exited during the race */ }

            var partialOut = await ReadSafelyAsync(stdoutTask).ConfigureAwait(false);
            var partialErr = await ReadSafelyAsync(stderrTask).ConfigureAwait(false);
            partialErr = Append(partialErr,
                $"Command timed out after {request.TimeoutSeconds}s; its process tree was terminated.");

            return new ElevatedResult
            {
                Kind = "timeout",
                TimedOut = true,
                StdOut = partialOut,
                StdErr = partialErr,
                Error = $"Command timed out after {request.TimeoutSeconds}s.",
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }

        var stdOut = await ReadSafelyAsync(stdoutTask).ConfigureAwait(false);
        var stdErr = await ReadSafelyAsync(stderrTask).ConfigureAwait(false);
        if (elevated && !usePkexec)
            stdErr = Append(stdErr,
                "Note: /usr/bin/pkexec was not available; the command ran with the current user's privileges.");

        var exitCode = process.ExitCode;
        var kind = elevated && usePkexec && exitCode is 127 or 126 ? "cancelled" : "executed";
        return new ElevatedResult
        {
            Kind = kind,
            StdOut = stdOut,
            StdErr = stdErr,
            ExitCode = exitCode,
            Error = kind == "cancelled" ? "The privilege prompt was declined or could not be started." : null,
            DurationMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static ElevatedResult Fail(string message, Stopwatch stopwatch) => new()
    {
        Kind = "failed",
        Error = message,
        StdErr = message,
        DurationMs = stopwatch.ElapsedMilliseconds
    };

    private static async Task<string> ReadSafelyAsync(Task<string> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { return ""; }
    }

    private static string Append(string existing, string note) =>
        string.IsNullOrWhiteSpace(existing) ? note : existing.TrimEnd() + "\n" + note;
}
