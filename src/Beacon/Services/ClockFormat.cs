using System.Globalization;

namespace Beacon.Services;

/// <summary>
/// Single source of truth for clock strings shown in popups and the chat card.
/// Reads the live setting so a saved change reaches already-open windows too.
/// </summary>
public static class ClockFormat
{
    public static string Pattern =>
        App.Instance?.Settings.Use24HourClock == false ? "h:mm tt" : "HH:mm";

    public static string Format(DateTime value) => value.ToString(Pattern, CultureInfo.InvariantCulture);
}
