using static SpaceSwipe.Native;

namespace SpaceSwipe;

/// <summary>
/// Moves between spaces on the monitor under the cursor. Space 0 is that monitor's desktop
/// (its normal windows); spaces 1..n are its maximized / full-screen apps in a stable order.
/// </summary>
internal sealed class SpaceSwitcher(WindowTracker tracker, SlideOverlay overlay, Settings settings, Parking parking)
{
    /// <summary>The monitor's spaces at the moment a gesture starts.</summary>
    sealed class Context
    {
        public required MonitorDesc Mon;
        public required Dictionary<IntPtr, WinInfo> Present;
        public required List<IntPtr> Spaces;
        public WinInfo? Cur;
        public int Ci;

        public static Context Capture(WindowTracker tracker, MonitorDesc? on = null)
        {
            var mon = on ?? WindowTracker.MonitorAtCursor();
            var snap = tracker.Snapshot();
            var present = snap.Where(w => w.Monitor == mon.Device).ToDictionary(w => w.Hwnd);
            var spaces = new List<IntPtr> { IntPtr.Zero };
            spaces.AddRange(tracker.Ring(mon.Device).Where(present.ContainsKey));
            var cur = present.Values.Where(w => w.IsShowing).OrderBy(w => w.Z).FirstOrDefault();
            int ci = cur is null ? 0 : Math.Max(0, spaces.IndexOf(cur.Hwnd));
            return new Context { Mon = mon, Present = present, Spaces = spaces, Cur = cur, Ci = ci };
        }

        public WinInfo? Space(int i) => i >= 1 && i < Spaces.Count && Present.TryGetValue(Spaces[i], out var w) ? w : null;

        public RECT Stage(int otherIndex) =>
            Cur?.Kind == WinKind.FullScreen || Space(otherIndex)?.Kind == WinKind.FullScreen ? Mon.Bounds : Mon.Work;

        public string Describe() =>
            "[" + string.Join(" | ", Spaces.Select(h => h == IntPtr.Zero ? "Desktop" : Short(Present[h].Title))) + "]";
    }

    /// <summary>While true (e.g. the overview is open), swipes and shortcuts are ignored.</summary>
    public Func<bool>? Suspended { get; set; }

    /// <summary>One space as shown in the overview.</summary>
    public sealed record SpaceItem(IntPtr Hwnd, string Title, WinKind Kind, bool Current);

    /// <summary>The spaces of the monitor under the cursor (Desktop first), or null if there is nothing to show.</summary>
    public (MonitorDesc Mon, List<SpaceItem> Spaces)? GetSpaces()
    {
        if (!CanAct(out _)) return null;
        var ctx = Context.Capture(tracker);
        var items = ctx.Spaces.Select((h, i) => h == IntPtr.Zero
            ? new SpaceItem(IntPtr.Zero, "Desktop", WinKind.None, i == ctx.Ci)
            : new SpaceItem(h, ctx.Present[h].Title, ctx.Present[h].Kind, i == ctx.Ci)).ToList();
        return (ctx.Mon, items);
    }

    /// <summary>
    /// Photograph a monitor's desktop even while apps cover it: park them for a moment (instant, no redraw),
    /// capture, and put them back. Call only while something covers the monitor (the overview does).
    /// </summary>
    public Shot? CaptureDesktop(MonitorDesc mon)
    {
        try
        {
            var ctx = Context.Capture(tracker, mon);
            var showing = ctx.Present.Values.Where(w => w.IsShowing).Select(w => w.Hwnd).ToList();
            foreach (var h in showing) parking.Park(h, mon.Device);
            DwmFlush();
            Thread.Sleep(30);
            DwmFlush();
            var shot = Shot.Capture(mon.Work);
            foreach (var h in showing) parking.Unpark(h);
            DesktopCache.Set(mon.Device, shot);
            return DesktopCache.CopyOf(mon.Device);
        }
        catch (Exception ex)
        {
            Log.Write("desktop capture failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Shortly after switching to a desktop space, remember what it looks like (for the overview).</summary>
    void RefreshDesktopCacheSoon(MonitorDesc mon)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 400 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            try
            {
                var ctx = Context.Capture(tracker, mon);
                if (ctx.Present.Values.Any(w => w.IsShowing)) return; // no longer on the desktop
                DesktopCache.Set(mon.Device, Shot.Capture(mon.Work));
            }
            catch (Exception ex) { Log.Write("desktop cache refresh failed: " + ex.Message); }
        };
        timer.Start();
    }

    /// <summary>Switch a monitor straight to one of its spaces (used by the overview; no slide).</summary>
    public void ActivateSpace(MonitorDesc mon, IntPtr target)
    {
        try
        {
            var ctx = Context.Capture(tracker, mon);
            if (target != IntPtr.Zero && !ctx.Present.ContainsKey(target)) return; // closed meanwhile
            SwitchTo(ctx, target, out _);
        }
        catch (Exception ex) { Log.Write("activate space failed: " + ex); }
    }

    /// <summary>False when there is nothing sensible to act on (e.g. a game is running, or a KVM switched the screens away).</summary>
    bool CanAct(out MonitorDesc mon)
    {
        mon = WindowTracker.MonitorAtCursor();
        if (!settings.Enabled || (Suspended?.Invoke() ?? false)) return false;
        if (SHQueryUserNotificationState(out int state) == 0 && state == QUNS_RUNNING_D3D_FULL_SCREEN)
        {
            Log.Write("skip: exclusive full-screen game running");
            return false;
        }
        if (!mon.Device.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) || mon.Work.Width <= 0)
        {
            Log.Write($"skip: no real monitor under the cursor ({mon.Device})");
            return false;
        }
        return true;
    }

    // ---------------- Discrete switches (keyboard shortcuts) ----------------

    public void Navigate(int dir) => Go(dir, toDesktop: false);

    public void GoDesktop() => Go(-1, toDesktop: true);

    void Go(int dir, bool toDesktop)
    {
        if (drag != null || !CanAct(out _)) return;

        var ctx = Context.Capture(tracker);
        int ci = ctx.Ci;
        int ti = toDesktop ? 0 : ci + dir;
        Log.Write($"{ctx.Mon.Device}: {ctx.Describe()} current={ci} target={ti}");

        if (ti == ci && toDesktop) return;
        if (ti < 0 || ti >= ctx.Spaces.Count)
        {
            if (settings.Animate) overlay.Bounce(ctx.Stage(ci), dir);
            return;
        }

        bool animate = settings.Animate;
        try
        {
            if (animate) overlay.Begin(ctx.Stage(ti));
            SwitchTo(ctx, ctx.Spaces[ti], out _);
            if (animate) WaitForRepaint();
            if (animate) overlay.Finish(ti > ci ? 1 : -1, settings.AnimationMs);
        }
        catch (Exception ex)
        {
            Log.Write("switch failed: " + ex);
            overlay.Hide();
        }
    }

    /// <summary>
    /// Make <paramref name="target"/> the visible, focused space (zero = desktop).
    /// For the desktop, the monitor's full-screen apps are parked below the screen (listed in <paramref name="parkedNow"/>).
    /// Returns true if the target app itself was parked and has been brought back.
    /// </summary>
    bool SwitchTo(Context ctx, IntPtr target, out List<IntPtr> parkedNow)
    {
        parkedNow = new List<IntPtr>();
        if (target == IntPtr.Zero)
        {
            foreach (var w in ctx.Present.Values.Where(w => w.IsShowing).OrderBy(w => w.Z))
            {
                parking.Park(w.Hwnd, ctx.Mon.Device);
                parkedNow.Add(w.Hwnd);
            }
            var next = tracker.TopWindowOn(ctx.Mon.Device);
            Focus(next != IntPtr.Zero ? next : GetShellWindow());
            RefreshDesktopCacheSoon(ctx.Mon);
            return false;
        }

        bool wasParked = parking.Unpark(target);
        Focus(target);
        return wasParked;
    }

    // ---------------- Finger-tracked (interactive) swipes ----------------

    sealed class Prepared
    {
        public IntPtr Target;                    // zero = desktop
        public bool WasParked;                   // app: it was parked and we brought it back
        public List<IntPtr> Parked = new();     // desktop: windows we parked underneath
        public IntPtr PriorForeground;
    }

    sealed class DragState
    {
        public required Context Ctx;
        public double W;
        public double Offset;
        public int PreparedDir;
        public Prepared? Prep;
    }

    DragState? drag;

    public void DragBegin()
    {
        drag = null;
        if (!settings.Animate || !CanAct(out _)) return;
        try
        {
            var ctx = Context.Capture(tracker);
            // Full-screen (taskbar-covering) neighbours need the whole monitor as the stage.
            var stage = ctx.Cur?.Kind == WinKind.FullScreen ||
                        ctx.Space(ctx.Ci - 1)?.Kind == WinKind.FullScreen ||
                        ctx.Space(ctx.Ci + 1)?.Kind == WinKind.FullScreen
                ? ctx.Mon.Bounds : ctx.Mon.Work;

            Log.Write($"{ctx.Mon.Device}: drag over {ctx.Describe()} current={ctx.Ci}");
            overlay.Begin(stage);
            drag = new DragState { Ctx = ctx, W = stage.Width };
        }
        catch (Exception ex)
        {
            Log.Write("drag begin failed: " + ex);
            overlay.Hide();
            drag = null;
        }
    }

    public void DragMove(double fx)
    {
        var d = drag;
        if (d == null) return;
        try
        {
            // Content follows the fingers: fingers left => old space moves left, next space comes from the right.
            double raw = fx / settings.FullSwipeFraction * d.W;
            int dir = raw < 0 ? 1 : raw > 0 ? -1 : 0;
            bool has = dir != 0 && d.Ctx.Ci + dir >= 0 && d.Ctx.Ci + dir < d.Ctx.Spaces.Count;

            if (has && d.PreparedDir != dir)
            {
                Unprepare(d);
                Prepare(d, dir);
            }
            else if (!has && dir != 0 && d.PreparedDir != 0)
            {
                Unprepare(d); // reversed past the start towards an edge
            }

            double travel = overlay.Travel;
            d.Offset = has ? Math.Clamp(raw, -travel, travel) : Rubber(raw, d.W);
            overlay.Render(d.Offset, d.PreparedDir);
        }
        catch (Exception ex)
        {
            Log.Write("drag move failed: " + ex);
            AbortDrag(d);
        }
    }

    public void DragEnd(double fx, double vfx)
    {
        var d = drag;
        drag = null;
        if (d == null)
        {
            // Animation off (or drag refused): fall back to a plain switch.
            if (settings.Enabled && !settings.Animate && Math.Abs(fx) >= settings.SwipeThreshold)
                Navigate(fx < 0 ? 1 : -1);
            return;
        }

        try
        {
            double v = vfx / settings.FullSwipeFraction * d.W; // px/s, same sign as Offset
            double o = d.Offset;
            double travel = overlay.Travel;
            bool towardsNeighbour = d.PreparedDir != 0 && Math.Sign(o) == -d.PreparedDir;
            bool far = Math.Abs(o) > settings.CommitFraction * d.W;
            bool fast = Math.Abs(v) > settings.FlickSpeed * d.W;
            bool flick = fast && Math.Sign(v) == Math.Sign(o) && Math.Abs(o) > 0.02 * d.W;
            bool flickBack = fast && Math.Sign(v) != Math.Sign(o);
            bool commit = towardsNeighbour && (far || flick) && !flickBack;

            double target = commit ? -d.PreparedDir * travel : 0;
            double remain = Math.Abs(target - o);
            double ms = Math.Clamp(remain / d.W * 420, 90, 320);
            if (Math.Abs(v) > 1) ms = Math.Clamp(Math.Min(ms, remain / Math.Abs(v) * 1000 * 1.7), 90, 320);

            Log.Write($"drag release offset={o / d.W:P0} v={v / d.W:F2}W/s -> {(commit ? "switch" : "cancel")}");
            overlay.AnimateOffset(o, target, (int)ms, d.PreparedDir);
            if (!commit) Unprepare(d); // the overlay still shows the old space while we put it back
        }
        catch (Exception ex)
        {
            Log.Write("drag end failed: " + ex);
        }
        finally
        {
            overlay.Hide();
        }
    }

    void AbortDrag(DragState d)
    {
        try { Unprepare(d); } catch { /* best effort */ }
        overlay.Hide();
        drag = null;
    }

    /// <summary>Resistance when dragging past the first or last space.</summary>
    static double Rubber(double raw, double w)
    {
        double r = w * 0.10;
        double x = Math.Abs(raw);
        return Math.Sign(raw) * r * (1 - 1 / (x / r * 0.6 + 1));
    }

    /// <summary>Switch to the neighbouring space underneath the overlay and snapshot it.</summary>
    void Prepare(DragState d, int dir)
    {
        var t = d.Ctx.Spaces[d.Ctx.Ci + dir];
        var p = new Prepared { Target = t, PriorForeground = GetForegroundWindow() };
        p.WasParked = SwitchTo(d.Ctx, t, out p.Parked);
        WaitForRepaint();
        overlay.SetAfter();
        d.Prep = p;
        d.PreparedDir = dir;
    }

    /// <summary>Undo <see cref="Prepare"/>: put the original space back underneath the overlay.</summary>
    void Unprepare(DragState d)
    {
        var p = d.Prep;
        if (p == null) return;
        if (p.Target == IntPtr.Zero)
        {
            foreach (var h in p.Parked) parking.Unpark(h);
        }
        else
        {
            if (d.Ctx.Cur != null)
                SetWindowPos(p.Target, d.Ctx.Cur.Hwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (p.WasParked) parking.Park(p.Target, d.Ctx.Mon.Device);
        }
        Focus(p.PriorForeground);
        WaitForRepaint();
        overlay.ClearAfter();
        d.Prep = null;
        d.PreparedDir = 0;
    }

    // ---------------- helpers ----------------

    /// <summary>Nothing is ever minimized, so windows are already drawn: just let the compositor catch up.</summary>
    static void WaitForRepaint()
    {
        DwmFlush();
        Thread.Sleep(20);
        DwmFlush();
    }

    /// <summary>Bring a window to the foreground even though we're a background tray app.</summary>
    internal static void Focus(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        var fg = GetForegroundWindow();
        uint me = GetCurrentThreadId();
        uint other = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        bool attached = other != 0 && other != me && AttachThreadInput(me, other, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally
        {
            if (attached) AttachThreadInput(me, other, false);
        }

        if (GetForegroundWindow() != h)
        {
            // Holding Alt lifts the foreground lock; tap a no-op key so Alt doesn't open a menu bar.
            Send(Key(VK_MENU, false));
            SetForegroundWindow(h);
            Send(Key(VK_NONAME, false), Key(VK_NONAME, true), Key(VK_MENU, true));
        }
    }

    static string Short(string s) => s.Length <= 28 ? s : s[..27] + "…";
}
