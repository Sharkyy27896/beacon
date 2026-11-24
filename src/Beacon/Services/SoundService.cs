using System.Media;
using Beacon.Models;

namespace Beacon.Services;

public static class SoundService
{
    public static void Play(NotificationPayload payload)
    {
        if (!payload.PlaySound) return;
        try
        {
            switch (payload.SoundName?.ToLowerInvariant())
            {
                case "none":
                case "off":
                    return;
                case "error":
                case "alert":
                case "hand":
                    SystemSounds.Hand.Play();
                    return;
                case "warning":
                case "exclamation":
                    SystemSounds.Exclamation.Play();
                    return;
                case "success":
                case "ok":
                case "asterisk":
                    SystemSounds.Asterisk.Play();
                    return;
                case "question":
                case "beep":
                    SystemSounds.Question.Play();
                    return;
                default:
                    switch (payload.Kind)
                    {
                        case NotificationKind.Error: SystemSounds.Hand.Play(); break;
                        case NotificationKind.Warning:
                        case NotificationKind.Captcha: SystemSounds.Exclamation.Play(); break;
                        case NotificationKind.Success: SystemSounds.Asterisk.Play(); break;
                        case NotificationKind.Question: SystemSounds.Question.Play(); break;
                        default: SystemSounds.Asterisk.Play(); break;
                    }
                    return;
            }
        }
        catch { /* audio is optional */ }
    }
}
