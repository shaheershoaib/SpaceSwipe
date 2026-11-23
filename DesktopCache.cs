namespace SpaceSwipe;

/// <summary>
/// The last known look of each monitor's desktop, for the overview's Desktop card. (Live wallpapers such as
/// Wallpaper Engine have no image file to load, and the desktop itself is hidden while an app is in front.)
/// </summary>
internal static class DesktopCache
{
    static readonly Dictionary<string, Shot> shots = new();

    /// <summary>A private copy of the cached desktop for this monitor, or null.</summary>
    public static Shot? CopyOf(string device)
    {
        if (!shots.TryGetValue(device, out var s)) return null;
        var copy = Shot.Blank(s.W, s.H);
        Native.BitBlt(copy.Dc, 0, 0, s.W, s.H, s.Dc, 0, 0, Native.SRCCOPY);
        return copy;
    }

    /// <summary>Store a desktop image (the cache takes ownership).</summary>
    public static void Set(string device, Shot shot)
    {
        if (shots.Remove(device, out var old)) old.Dispose();
        shots[device] = shot;
    }
}
