using System.Diagnostics;
using System.IO;
using System.Text;
using Beacon.Models;

namespace Beacon.Services;

/// <summary>
/// Linux-safe audio feedback. A short generated WAV is played through
/// PulseAudio's <c>paplay</c> when present, with <c>canberra-gtk-play</c> as a
/// fallback. Missing audio tools are silently ignored: a notification must
/// never fail because the desktop has no sound server.
/// </summary>
public static class SoundService
{
    private const int SampleRate = 22050;

    public static void Play(NotificationPayload payload)
    {
        if (payload is null || !payload.PlaySound) return;
        if (App.Instance is not null && !App.Instance.Settings.PlaySound) return;

        var name = payload.SoundName?.Trim().ToLowerInvariant();
        if (name is "none" or "off" or "silent") return;

        try
        {
            var tones = TonesFor(name, payload.Kind);
            if (TryPlayWithPaplay(name ?? payload.Kind.ToString().ToLowerInvariant(), tones)) return;
            TryPlayWithCanberra(name, payload.Kind);
        }
        catch
        {
            // Audio is optional on headless Linux sessions.
        }
    }

    private static int[] TonesFor(string? name, NotificationKind kind) => name switch
    {
        "error" or "alert" or "hand" or "critical" => new[] { 196, 165 },
        "warning" or "exclamation" => new[] { 440, 330 },
        "success" or "ok" or "asterisk" or "complete" => new[] { 660, 880 },
        "question" or "beep" or "prompt" => new[] { 523 },
        "message" or "info" => new[] { 587, 784 },
        _ => kind switch
        {
            NotificationKind.Error => new[] { 196, 165 },
            NotificationKind.Warning or NotificationKind.Captcha => new[] { 440, 330 },
            NotificationKind.Success => new[] { 660, 880 },
            NotificationKind.Question or NotificationKind.Reminder => new[] { 523 },
            NotificationKind.Verification or NotificationKind.Shell => new[] { 587, 784 },
            _ => new[] { 660 }
        }
    };

    private static bool TryPlayWithPaplay(string key, int[] tones)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "Beacon", "sounds");
            Directory.CreateDirectory(directory);
            var safeKey = string.Concat(key.Where(char.IsLetterOrDigit));
            if (string.IsNullOrWhiteSpace(safeKey)) safeKey = "beacon";
            var path = Path.Combine(directory, $"{safeKey}-{string.Join('-', tones)}.wav");

            if (!File.Exists(path))
                File.WriteAllBytes(path, BuildWav(tones));

            var startInfo = new ProcessStartInfo
            {
                FileName = "paplay",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("--quiet");
            startInfo.ArgumentList.Add(path);

            using var process = Process.Start(startInfo);
            if (process is null) return false;

            // Drain the pipes so a short-lived paplay never blocks on a full
            // buffer, but do not wait for the audio device.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryPlayWithCanberra(string? name, NotificationKind kind)
    {
        var eventName = name switch
        {
            "error" or "alert" or "hand" => "dialog-error",
            "warning" or "exclamation" => "dialog-warning",
            "success" or "ok" or "asterisk" => "message-new-instant",
            "question" or "beep" => "dialog-question",
            _ => kind switch
            {
                NotificationKind.Error => "dialog-error",
                NotificationKind.Warning or NotificationKind.Captcha => "dialog-warning",
                NotificationKind.Question or NotificationKind.Shell => "dialog-question",
                _ => "dialog-information"
            }
        };

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "canberra-gtk-play",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(eventName);

            using var process = Process.Start(startInfo);
            if (process is null) return;

            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
        }
        catch
        {
            // No desktop sound daemon available.
        }
    }

    private static byte[] BuildWav(int[] tones)
    {
        const int bytesPerSample = 2;
        const double secondsPerTone = 0.14;
        var samplesPerTone = (int)(SampleRate * secondsPerTone);
        var sampleCount = samplesPerTone * tones.Length;
        var dataSize = sampleCount * bytesPerSample;

        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * bytesPerSample);
        writer.Write((short)bytesPerSample);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        for (var toneIndex = 0; toneIndex < tones.Length; toneIndex++)
        {
            var frequency = tones[toneIndex];
            for (var i = 0; i < samplesPerTone; i++)
            {
                var t = i / (double)SampleRate;
                var envelope = Math.Min(1.0, i / 300.0) *
                               Math.Min(1.0, (samplesPerTone - i) / 900.0);
                var value = Math.Sin(2 * Math.PI * frequency * t) * 0.28 * envelope;
                writer.Write((short)(value * short.MaxValue));
            }
        }

        writer.Flush();
        return stream.ToArray();
    }
}
