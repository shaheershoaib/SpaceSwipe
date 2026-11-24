using static SpaceSwipe.Native;

namespace SpaceSwipe;

internal enum WinKind { None, Maximized, FullScreen }

internal sealed record WinInfo(IntPtr Hwnd, WinKind Kind, string Monitor, int Z, string Title, bool Parked)
{
    /// <summary>Actually on screen right now (not parked below the screen for the Desktop space).</summary>
    public bool IsShowing => !Parked;
}

internal sealed record MonitorDesc(IntPtr Handle, string Device, RECT Bounds, RECT Work);

/// <summary>
/// Keeps an ordered list of full-screen windows ("spaces") per monitor, like macOS
/// when "Displays have separate Spaces" is on. Order is stable: new spaces are added at the end.
/// </summary>
internal sealed class WindowTracker
{
    readonly Dictionary<string, List<IntPtr>> rings = new();
    readonly Dictionary<IntPtr, string> lastMonitor = new();

    public HashSet<IntPtr> OwnWindows { get; } = new();

    /// <summary>Windows hidden below the screen for a Desktop space still belong to their monitor's row.</summary>
    public Parking? Parking { get; set; }

    /// <summary>User preferences (borderless full screen, excluded apps).</summary>
    public Settings? Settings { get; set; }

    readonly Dictionary<uint, string> processNames = new();

    /// <summary>Lower-case executable name of the window's process ("firefox.exe"), cached per process.</summary>
    public string ProcessName(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        if (processNames.TryGetValue(pid, out var name)) return name;
        name = "";
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            name = (p.ProcessName + ".exe").ToLowerInvariant();
        }
        catch { /* gone or protected */ }
        if (processNames.Count > 500) processNames.Clear();
        processNames[pid] = name;
        return name;
    }

    bool Excluded(IntPtr h)
    {
        var list = Settings?.ExcludedApps;
        if (list == null || list.Count == 0) return false;
        var name = ProcessName(h);
        return name.Length > 0 && list.Any(x => string.Equals(x.Trim(), name, StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(x.Trim() + ".exe", name, StringComparison.OrdinalIgnoreCase));
    }

    static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
        "MultitaskingViewFrame", "ForegroundStaging", "Shell_InputSwitchTopLevelWindow", "TaskListThumbnailWnd",
    };

    public static MonitorDesc GetMonitor(IntPtr hmon)
    {
        var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hmon, ref mi);
        return new MonitorDesc(hmon, mi.szDevice ?? "", mi.rcMonitor, mi.rcWork);
    }

    public static MonitorDesc MonitorAtCursor()
    {
        GetCursorPos(out var p);
        return GetMonitor(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST));
    }

    /// <summary>A window that would appear in Alt+Tab.</summary>
    public bool IsAppWindow(IntPtr h)
    {
        if (OwnWindows.Contains(h) || !IsWindowVisible(h)) return false;
        long ex = ExStyle(h);
        bool appWin = (ex & WS_EX_APPWINDOW) != 0;
        if (!appWin)
        {
            if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
            if ((ex & WS_EX_NOACTIVATE) != 0) return false;
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        }
        if ((ex & WS_EX_TRANSPARENT) != 0) return false; // click-through overlays
        if (IsCloaked(h)) return false;                  // other virtual desktop / suspended UWP
        if (GetWindowTextLength(h) == 0) return false;
        return !ShellClasses.Contains(ClassOf(h));
    }

    WinKind Classify(IntPtr h, out string mon, out bool parked)
    {
        mon = "";
        parked = false;
        if (!IsAppWindow(h)) return WinKind.None;

        // Minimized windows are not spaces (same as macOS).
        if (IsIconic(h)) return WinKind.None;
        if (Excluded(h)) return WinKind.None;

        if (Parking?.MonitorOf(h) is { } parkedOn)
        {
            mon = parkedOn;
            parked = true;
            return IsZoomed(h) ? WinKind.Maximized : WinKind.FullScreen;
        }

        var md = GetMonitor(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST));
        mon = md.Device;
        if (IsZoomed(h)) return WinKind.Maximized;

        GetWindowRect(h, out var wr);
        var b = md.Bounds;
        if ((Settings?.IncludeBorderlessFullScreen ?? true) &&
            wr.Left <= b.Left && wr.Top <= b.Top && wr.Right >= b.Right && wr.Bottom >= b.Bottom)
            return WinKind.FullScreen; // borderless full screen (F11 browsers, games, video players)
        return WinKind.None;
    }

    /// <summary>Enumerates windows (top of z-order first) and updates the per-monitor rings.</summary>
    public List<WinInfo> Snapshot()
    {
        var list = new List<WinInfo>();
        int z = 0;
        EnumWindows((h, _) =>
        {
            var kind = Classify(h, out var mon, out bool parked);
            if (kind != WinKind.None) list.Add(new WinInfo(h, kind, mon, z, TitleOf(h), parked));
            z++;
            return true;
        }, IntPtr.Zero);

        var present = list.ToDictionary(w => w.Hwnd);
        foreach (var (mon, ring) in rings)
        {
            // Drop closed / un-maximized / moved windows. Keep cloaked ones (other virtual desktop)
            // so their position survives a round trip.
            ring.RemoveAll(h => present.TryGetValue(h, out var w) ? w.Monitor != mon : !(IsWindow(h) && IsCloaked(h)));
        }

        // New spaces go to the end; for a cold start, bottom of z-order first.
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var w = list[i];
            if (!rings.TryGetValue(w.Monitor, out var ring)) rings[w.Monitor] = ring = new List<IntPtr>();
            if (!ring.Contains(w.Hwnd)) ring.Add(w.Hwnd);
            lastMonitor[w.Hwnd] = w.Monitor;
        }

        foreach (var h in lastMonitor.Keys.Where(h => !IsWindow(h)).ToList()) lastMonitor.Remove(h);
        return list;
    }

    /// <summary>Put a monitor's spaces in this order (anything not listed, e.g. on another desktop, keeps its place at the end).</summary>
    public void SetOrder(string device, IEnumerable<IntPtr> order)
    {
        var list = order.ToList();
        if (rings.TryGetValue(device, out var ring)) list.AddRange(ring.Where(h => !list.Contains(h)));
        rings[device] = list;
    }

    public IReadOnlyList<IntPtr> Ring(string device) =>
        rings.TryGetValue(device, out var r) ? r : Array.Empty<IntPtr>();

    /// <summary>Topmost ordinary (non-minimized) app window on the given monitor, or zero.</summary>
    public IntPtr TopWindowOn(string device, Func<IntPtr, bool>? filter = null)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (IsAppWindow(h) && !IsIconic(h) && !(Parking?.IsParked(h) ?? false) && (filter?.Invoke(h) ?? true) &&
                GetMonitor(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST)).Device == device)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
