using System.Text.Json;
using static SpaceSwipe.Native;

namespace SpaceSwipe;

/// <summary>
/// Hides full-screen apps for the Desktop space by moving them just below the screen instead of minimizing.
/// Minimized windows are shrunk by Windows and need ~0.3–0.4 s to redraw when restored; parked windows keep
/// their size and content, so they come back instantly and look exactly right.
/// </summary>
internal sealed class Parking : IDisposable
{
    sealed record Spot(int Left, int Top, string Monitor);

    readonly Dictionary<IntPtr, Spot> parked = new();
    readonly WinEventProc hookProc; // keep the delegate alive
    readonly IntPtr fgHook, minHook;

    static string StatePath => Path.Combine(Log.Dir, "parked.json");

    public Parking()
    {
        RecoverFromCrash();
        hookProc = OnWinEvent;
        fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        minHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero, hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public bool IsParked(IntPtr h) => parked.ContainsKey(h);

    public string? MonitorOf(IntPtr h) => parked.TryGetValue(h, out var s) ? s.Monitor : null;

    static int ParkY => GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN) + 200;

    /// <summary>Move a window just below all monitors, keeping its size, z-order and maximized state.</summary>
    public void Park(IntPtr h, string monitor)
    {
        if (parked.ContainsKey(h) || !GetWindowRect(h, out var r)) return;
        parked[h] = new Spot(r.Left, r.Top, monitor);
        SetWindowPos(h, IntPtr.Zero, r.Left, ParkY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Save();
    }

    /// <summary>Put a parked window back where it was. Returns false if it wasn't parked.</summary>
    public bool Unpark(IntPtr h)
    {
        if (!parked.Remove(h, out var s)) return false;
        if (IsWindow(h)) SetWindowPos(h, IntPtr.Zero, s.Left, s.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Save();
        return true;
    }

    public void UnparkAll()
    {
        foreach (var h in parked.Keys.ToList()) Unpark(h);
    }

    /// <summary>Forget windows that no longer exist.</summary>
    public void Prune()
    {
        bool changed = false;
        foreach (var h in parked.Keys.Where(h => !IsWindow(h)).ToList()) { parked.Remove(h); changed = true; }
        if (changed) Save();
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return;
        var root = GetAncestor(hwnd, GA_ROOT);
        var h = parked.ContainsKey(hwnd) ? hwnd : parked.ContainsKey(root) ? root : IntPtr.Zero;
        if (h == IntPtr.Zero) return;
        // Activated via Alt+Tab / taskbar / Task View, or being minimized: bring it back on screen.
        Log.Write($"parked window {(evt == EVENT_SYSTEM_FOREGROUND ? "activated" : "minimized")}: unparking");
        Unpark(h);
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            var list = parked.Select(kv => new long[] { kv.Key.ToInt64(), kv.Value.Left, kv.Value.Top }).ToList();
            File.WriteAllText(StatePath, JsonSerializer.Serialize(list));
        }
        catch (Exception ex) { Log.Write("parking state save failed: " + ex.Message); }
    }

    /// <summary>If SpaceSwipe was killed while windows were parked, put them back.</summary>
    static void RecoverFromCrash()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var list = JsonSerializer.Deserialize<List<long[]>>(File.ReadAllText(StatePath)) ?? new();
            int y = ParkY - 100;
            foreach (var e in list)
            {
                var h = new IntPtr(e[0]);
                if (IsWindow(h) && GetWindowRect(h, out var r) && r.Top >= y)
                {
                    SetWindowPos(h, IntPtr.Zero, (int)e[1], (int)e[2], 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    Log.Write($"recovered parked window {h}");
                }
            }
            File.Delete(StatePath);
        }
        catch (Exception ex) { Log.Write("parking recovery failed: " + ex.Message); }
    }

    public void Dispose()
    {
        if (fgHook != IntPtr.Zero) UnhookWinEvent(fgHook);
        if (minHook != IntPtr.Zero) UnhookWinEvent(minHook);
        UnparkAll();
        try { File.Delete(StatePath); } catch { }
    }
}
