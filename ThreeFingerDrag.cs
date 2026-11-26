using System.Diagnostics;
using static SpaceSwipe.Native;

namespace SpaceSwipe;

/// <summary>
/// macOS-style three-finger drag: rest three fingers on the touchpad and move them to drag (the left button is held
/// for you), so you can move windows and select text without clicking. Lift your fingers and the drag ends a moment
/// later; put three fingers back within that moment to carry on (to reposition your hand mid-drag).
/// Space swipes move to four fingers while this is on, like on a Mac.
///
/// Finger movement is sent as ordinary relative mouse movement, so Windows applies its own pointer speed and
/// acceleration ("Enhance pointer precision") to it, the same curve the touchpad pointer uses: the drag moves
/// like your normal pointer instead of following a curve of our own.
/// </summary>
internal sealed class ThreeFingerDrag
{
    enum State { Idle, Armed, Dragging, Lifted }

    State state;
    double refX, refY;          // finger centroid last frame (touchpad units)
    double accX, accY;          // movement not yet sent (fractions of a mouse count)

    // Window mode: the drag started on a window's title bar, so SpaceSwipe moves the window itself (with the pointer)
    // instead of holding the button for Windows' own move loop, which trails behind injected input.
    IntPtr moveWin;
    double px, py, wx, wy;      // pointer and window position (physical pixels, sub-pixel)
    int skipped;                // positions superseded before the window got to them
    double pixelsPerCount = 1;
    long armedAt, liftedAt, lastFrameAt;
    bool sawFullLift;
    readonly Stopwatch clock = Stopwatch.StartNew();
    int frames, sent;           // stats for the log
    long delaySum;
    int maxDelay;
    double lastMoveMs, maxGap, beganMs;

    public bool Enabled { get; set; }

    /// <summary>Pointer speed multiplier (1 = default).</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Finger movement that starts a drag, in touchpad widths (small: it should feel immediate).</summary>
    const double StartThreshold = 0.006;
    /// <summary>Three fingers must be down this long first, so landing four fingers for a swipe never starts a drag.</summary>
    const long SettleMs = 40;
    /// <summary>How long after lifting the drag waits for three fingers to come back before releasing.</summary>
    const long ReleaseDelayMs = 450;
    /// <summary>Mouse counts per millimetre of finger travel (about a 400 dpi mouse) before Windows' pointer settings.</summary>
    const double CountsPerMm = 16;

    /// <summary>True while the button is held for a drag (including the short wait after lifting).</summary>
    public bool Active => state is State.Dragging or State.Lifted;

    /// <summary>One touchpad frame: the number of fingers down and their centroid (touchpad units).</summary>
    public void OnFrame(int fingers, double cx, double cy, double padWidth, double padWidthMm)
    {
        long now = Environment.TickCount64;
        lastFrameAt = now;
        if (!Enabled)
        {
            if (Active) Release("turned off");
            state = State.Idle;
            return;
        }

        switch (state)
        {
            case State.Idle:
                if (fingers == 3) Arm(cx, cy, now);
                break;

            case State.Armed:
                if (fingers != 3) { state = State.Idle; break; }
                double moved = Math.Sqrt(Math.Pow((cx - refX) / padWidth, 2) + Math.Pow((cy - refY) / padWidth, 2));
                if (moved >= StartThreshold && now - armedAt >= SettleMs) Begin(cx, cy);
                break;

            case State.Dragging:
                if (fingers == 3) Move(cx, cy, padWidthMm / padWidth);
                else if (fingers >= 4) Release("four fingers");
                else
                {
                    state = State.Lifted;
                    liftedAt = now;
                    sawFullLift = fingers == 0;
                }
                break;

            case State.Lifted:
                if (fingers == 0) sawFullLift = true;
                else if (fingers == 3) Resume(cx, cy);  // fingers back: carry on dragging
                else if (fingers >= 4 || sawFullLift) Release("another touch"); // e.g. a one-finger tap afterwards
                // (one or two fingers still down while the others lift: keep waiting)
                break;
        }
    }

    /// <summary>Called on a timer (touch frames stop when nothing touches the pad): ends a lifted drag.</summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        if (state == State.Dragging && now - lastFrameAt > 200)
        {
            // The pad stopped reporting without a "fingers lifted" frame: treat it as lifted.
            state = State.Lifted;
            liftedAt = lastFrameAt;
            sawFullLift = true;
        }
        if (state == State.Lifted && now - liftedAt >= ReleaseDelayMs) Release("lifted");
    }

    /// <summary>Let go of the button now if a drag is in progress (e.g. on exit).</summary>
    public void Cancel()
    {
        if (Active) Release("cancelled");
        state = State.Idle;
    }

    void Arm(double cx, double cy, long now)
    {
        state = State.Armed;
        refX = cx; refY = cy;
        armedAt = now;
    }

    void Begin(double cx, double cy)
    {
        refX = cx; refY = cy;   // start from here, so the pointer doesn't jump by the start threshold
        accX = accY = 0;
        frames = sent = 0;
        maxGap = 0;
        delaySum = 0;
        maxDelay = 0;
        lastMoveMs = beganMs = clock.Elapsed.TotalMilliseconds;
        state = State.Dragging;

        GetCursorPos(out var p);
        moveWin = TitleBarWindowAt(p);
        if (moveWin != IntPtr.Zero)
        {
            GetWindowRect(moveWin, out var r);
            px = p.X; py = p.Y; wx = r.Left; wy = r.Top;
            skipped = 0;
            EnsureMover();
            lock (moverLock) { moverWin = moveWin; targetX = r.Left; targetY = r.Top; moverPending = false; }
            pixelsPerCount = PointerSpeedFactor();
            // To the front now (cheap, doesn't wait for the app); keyboard focus follows on drop, since giving
            // focus can block for ~100 ms and would stall the start of the drag.
            SetWindowPos(moveWin, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            Log.Write($"three-finger drag: begin (moving window \"{TitleOf(moveWin)}\")");
            return;
        }
        Send(Mouse(MOUSEEVENTF_LEFTDOWN));
        Log.Write("three-finger drag: begin");
    }

    /// <summary>
    /// The window whose title bar is at a screen point, if it can be moved directly (not maximized: dragging a
    /// maximized window restores it, which Windows' own move loop handles). Zero otherwise.
    /// </summary>
    static IntPtr TitleBarWindowAt(POINT p)
    {
        var h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return IntPtr.Zero;
        var root = GetAncestor(h, GA_ROOT);
        if (root == IntPtr.Zero || IsZoomed(root) || root == GetShellWindow()) return IntPtr.Zero;
        var cls = ClassOf(root);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return IntPtr.Zero;
        // (WinUI 3 apps such as Files apply outside moves slowly, ~13/s measured, so they move in steps; holding
        // the button for Windows' own move loop was tried for them and was worse: it didn't follow the pointer.)
        var lp = (IntPtr)((p.Y & 0xFFFF) << 16 | (p.X & 0xFFFF));
        if (SendMessageTimeout(root, WM_NCHITTEST, IntPtr.Zero, lp, SMTO_ABORTIFHUNG, 40, out var hit) == IntPtr.Zero) return IntPtr.Zero;
        return (int)hit == HTCAPTION ? root : IntPtr.Zero;
    }

    /// <summary>Windows' pointer speed setting (1–20) as a multiplier, the way it scales mouse movement.</summary>
    static double PointerSpeedFactor()
    {
        if (!SystemParametersInfoInt(SPI_GETMOUSESPEED, 0, out int s, 0)) s = 10;
        s = Math.Clamp(s, 1, 20);
        return s switch
        {
            <= 2 => s / 32.0,
            <= 10 => (s - 2) / 8.0 * 0.9375 + 0.0625,
            _ => 1 + (s - 10) * 0.25,
        };
    }

    void Resume(double cx, double cy)
    {
        refX = cx; refY = cy;
        lastMoveMs = clock.Elapsed.TotalMilliseconds;
        if (moveWin != IntPtr.Zero) { GetCursorPos(out var p); px = p.X; py = p.Y; }
        state = State.Dragging;
    }

    void Move(double cx, double cy, double mmPerUnit)
    {
        double t = clock.Elapsed.TotalMilliseconds;
        frames++;
        maxGap = Math.Max(maxGap, t - lastMoveMs);
        lastMoveMs = t;
        int delay = TouchpadGestures.InputDelayMs;
        if (delay is >= 0 and < 5000) { delaySum += delay; maxDelay = Math.Max(maxDelay, delay); }

        accX += (cx - refX) * mmPerUnit * CountsPerMm * Speed;
        accY += (cy - refY) * mmPerUnit * CountsPerMm * Speed;
        refX = cx; refY = cy;
        if (moveWin != IntPtr.Zero)
        {
            // Move the pointer and the window by exactly the same amount, so the window stays glued to it.
            double ddx = accX * pixelsPerCount, ddy = accY * pixelsPerCount;
            accX = accY = 0;
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            double nx = Math.Clamp(px + ddx, vx, vx + GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1);
            double ny = Math.Clamp(py + ddy, vy, vy + GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1);
            wx += nx - px; wy += ny - py;
            px = nx; py = ny;
            if (!IsWindow(moveWin)) { moveWin = IntPtr.Zero; return; }
            Send(MouseTo((int)Math.Round(px), (int)Math.Round(py)));
            PlaceWindow(t);
            return;
        }

        int ix = (int)Math.Truncate(accX), iy = (int)Math.Truncate(accY);
        if (ix == 0 && iy == 0) return;
        accX -= ix; accY -= iy;
        Send(Mouse(MOUSEEVENTF_MOVE, ix, iy)); // relative: Windows applies the pointer speed and acceleration
        sent++;
    }

    /// <summary>Hand the newest window position to the mover thread (positions it hasn't got to yet are superseded).</summary>
    void PlaceWindow(double nowMs)
    {
        int x = (int)Math.Round(wx), y = (int)Math.Round(wy);
        lock (moverLock)
        {
            if (x == targetX && y == targetY && moverWin == moveWin) return;
            if (moverPending) skipped++;
            moverWin = moveWin; targetX = x; targetY = y; moverPending = true;
        }
        moverWake.Set();
        sent++;
    }

    // ---------------- mover thread ----------------
    // A window can only be moved by its own thread. Asynchronous requests are posted, so they wait behind whatever a
    // busy app is doing (WinUI apps such as Files redraw a lot) and trail. Synchronous ones are delivered at the app's
    // next chance, ahead of its queued work, but block the caller until then: so they're made from this thread, never
    // the one reading the touchpad, and always with the newest position (nothing piles up).

    readonly object moverLock = new();
    readonly AutoResetEvent moverWake = new(false);
    IntPtr moverWin;
    int targetX = int.MinValue, targetY = int.MinValue;
    bool moverPending;
    Thread? mover;

    void EnsureMover()
    {
        if (mover != null) return;
        mover = new Thread(MoverLoop) { IsBackground = true, Name = "window-mover", Priority = ThreadPriority.AboveNormal };
        mover.Start();
    }

    void MoverLoop()
    {
        while (true)
        {
            moverWake.WaitOne();
            while (true)
            {
                IntPtr h; int x, y;
                lock (moverLock)
                {
                    if (!moverPending) break;
                    h = moverWin; x = targetX; y = targetY; moverPending = false;
                }
                if (h == IntPtr.Zero || !IsWindow(h) || IsHungAppWindow(h)) continue;
                SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }
    }

    void Release(string why)
    {
        if (moveWin != IntPtr.Zero && IsWindow(moveWin)) PlaceWindow(0); // exactly where it was dropped
        if (moveWin == IntPtr.Zero) Send(Mouse(MOUSEEVENTF_LEFTUP));
        else if (IsWindow(moveWin)) SpaceSwitcher.Focus(moveWin); // dropped: it's the active window, as after a click
        moveWin = IntPtr.Zero;
        state = State.Idle;
        double ms = clock.Elapsed.TotalMilliseconds - beganMs;
        Log.Write($"three-finger drag: end ({why}); {frames} frames, {sent} moves ({skipped} skipped: window busy) in {ms:F0} ms, longest gap {maxGap:F0} ms, " +
                  $"touch reports queued avg {(frames > 0 ? (double)delaySum / frames : 0):F1} ms / max {maxDelay} ms");
    }
}
