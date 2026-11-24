namespace Beacon.Services;

/// <summary>A monitor's physical-pixel bounds (the unit Win32/Screen uses).</summary>
public sealed record ScreenRect(int Left, int Top, int Width, int Height, bool Primary = false)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>
/// Chooses which display should host the popup. Centering on the whole virtual
/// screen splits the card across the seam between a laptop and an external TV,
/// so we pick one display instead. The mouse cursor is the best signal for
/// which display the user is currently looking at.
/// </summary>
public static class MonitorSelector
{
    public static ScreenRect? Pick(IReadOnlyList<ScreenRect> screens, int cursorX, int cursorY)
    {
        if (screens.Count == 0) return null;
        return screens.FirstOrDefault(s => s.Contains(cursorX, cursorY))
               ?? screens.FirstOrDefault(s => s.Primary)
               ?? screens[0];
    }
}
