using System.Diagnostics;
using System.Runtime.InteropServices;
using static SpaceSwipe.Native;

namespace SpaceSwipe;

/// <summary>An off-screen GDI bitmap.</summary>
internal sealed class Shot : IDisposable
{
    public IntPtr Dc { get; }
    readonly IntPtr bmp, old;
    public int W { get; }
    public int H { get; }

    Shot(int w, int h)
    {
        W = w; H = h;
        var screen = GetDC(IntPtr.Zero);
        Dc = CreateCompatibleDC(screen);
        bmp = CreateCompatibleBitmap(screen, w, h);
        ReleaseDC(IntPtr.Zero, screen);
        old = SelectObject(Dc, bmp);
    }

    public static Shot Blank(int w, int h) => new(w, h);

    /// <summary>Copy a managed bitmap into a GDI bitmap (for fast blits).</summary>
    public static Shot FromBitmap(Bitmap b)
    {
        var s = new Shot(b.Width, b.Height);
        using var g = Graphics.FromHdc(s.Dc);
        g.DrawImageUnscaled(b, 0, 0);
        return s;
    }

    /// <summary>
    /// Copies a region of the composited desktop, exactly as it looks on screen (HDR tone mapping included).
    /// Windows marked exclude-from-capture (our overlay) are not included.
    /// </summary>
    public static Shot Capture(RECT r)
    {
        var s = new Shot(r.Width, r.Height);
        var screen = GetDC(IntPtr.Zero);
        BitBlt(s.Dc, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, SRCCOPY);
        ReleaseDC(IntPtr.Zero, screen);
        return s;
    }

    /// <summary>Copy into a managed bitmap (diagnostics).</summary>
    public Bitmap ToBitmap()
    {
        SelectObject(Dc, old);               // a bitmap can't be selected into a DC while it is copied
        try { return Image.FromHbitmap(bmp); }
        finally { SelectObject(Dc, bmp); }
    }

    public void Dispose()
    {
        SelectObject(Dc, old);
        DeleteObject(bmp);
        DeleteDC(Dc);
    }
}

/// <summary>
/// A click-through topmost window that covers one monitor during a switch. It shows a snapshot
/// of the old space, the real switch happens underneath it, then the old and new snapshots slide.
/// It is excluded from screen capture, so it can snapshot what is really underneath it.
/// </summary>
internal sealed class SlideOverlay : NativeWindow, IDisposable
{
    Shot? before, after, back;
    RECT stage;

    /// <summary>The snapshot currently shown as the old space (for diagnostics).</summary>
    public Shot? Before => before;

    public SlideOverlay(bool excludeFromCapture = true)
    {
        CreateHandle(new CreateParams
        {
            Caption = "SpaceSwipe Overlay",
            Style = unchecked((int)WS_POPUP),
            ExStyle = (int)(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOPMOST),
            X = 0, Y = 0, Width = 1, Height = 1,
        });
        if (excludeFromCapture && !SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE))
            Log.Write($"WARN SetWindowDisplayAffinity failed: {Marshal.GetLastWin32Error()}");
        int on = 1;
        DwmSetWindowAttribute(Handle, DWMWA_TRANSITIONS_FORCEDISABLED, ref on, 4);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_ERASEBKGND: m.Result = 1; return;
            case WM_NCHITTEST: m.Result = HTTRANSPARENT; return;
            case WM_DPICHANGED: m.Result = IntPtr.Zero; return; // keep our physical-pixel size
            case WM_PAINT: Present(); ValidateRect(Handle, IntPtr.Zero); m.Result = IntPtr.Zero; return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Distance the content moves for one full space change (stage width plus a small gap).</summary>
    public double Travel => stage.Width + Math.Max(16, stage.Width / 80);

    /// <summary>Freeze the stage: snapshot it and put the snapshot on top.</summary>
    public void Begin(RECT r)
    {
        Cleanup();
        stage = r;
        before = Shot.Capture(r);
        back = Shot.Blank(r.Width, r.Height);
        Compose(0, before, 0, null);
        SetWindowPos(Handle, HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Present();
        DwmFlush();
        DwmFlush(); // make sure the frozen frame is on screen before anything changes underneath
    }

    /// <summary>Snapshot the new state underneath and slide. dir &gt; 0 = new space comes in from the right.</summary>
    public void Finish(int dir, int durationMs)
    {
        if (before == null || back == null) { Hide(); return; }
        SetAfter();
        AnimateOffset(0, -dir * Travel, durationMs, dir);
        Hide();
    }

    /// <summary>Snapshot what is underneath the overlay now, as the neighbouring space.</summary>
    public void SetAfter()
    {
        after?.Dispose();
        after = Shot.Capture(stage);
    }

    public void ClearAfter()
    {
        after?.Dispose();
        after = null;
    }

    /// <summary>
    /// Draw one frame. offset = how far the old space has moved (negative = left).
    /// side = where the neighbour sits: +1 to the right, -1 to the left, 0 none.
    /// </summary>
    public void Render(double offset, int side)
    {
        double travel = Travel;
        Compose((int)Math.Round(offset), before,
                (int)Math.Round(side > 0 ? offset + travel : offset - travel), side != 0 ? after : null);
        Present();
    }

    /// <summary>Ease the offset from one value to another, e.g. finishing or cancelling a drag.</summary>
    public void AnimateOffset(double from, double to, int durationMs, int side)
    {
        if (back == null) return;
        Animate(durationMs, t =>
        {
            double o = from + (to - from) * EaseOut(t);
            double travel = Travel;
            Compose((int)Math.Round(o), before,
                    (int)Math.Round(side > 0 ? o + travel : o - travel), side != 0 ? after : null);
        });
    }

    /// <summary>Rubber-band nudge when there is no space in that direction.</summary>
    public void Bounce(RECT r, int dir)
    {
        Begin(r);
        double amp = Math.Min(110, r.Width / 24.0);
        Animate(260, t =>
        {
            double off = amp * Math.Sin(Math.PI * t) * (1 - 0.3 * t);
            Compose((int)Math.Round(-dir * off), before, 0, null);
        });
        Hide();
    }

    public void Hide()
    {
        ShowWindow(Handle, SW_HIDE);
        Cleanup();
    }

    void Animate(int durationMs, Action<double> frame)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            double t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / Math.Max(1, durationMs));
            frame(t);
            Present();
            if (DwmFlush() != 0) Thread.Sleep(6); // DwmFlush paces us to the compositor
            if (t >= 1.0) break;
        }
    }

    static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    void Compose(int bx, Shot? b, int ax, Shot? a)
    {
        if (back == null) return;
        PatBlt(back.Dc, 0, 0, back.W, back.H, BLACKNESS);
        if (b != null) BitBlt(back.Dc, bx, 0, b.W, b.H, b.Dc, 0, 0, SRCCOPY);
        if (a != null) BitBlt(back.Dc, ax, 0, a.W, a.H, a.Dc, 0, 0, SRCCOPY);
    }

    void Present()
    {
        if (back == null || Handle == IntPtr.Zero) return;
        var dc = GetDC(Handle);
        BitBlt(dc, 0, 0, back.W, back.H, back.Dc, 0, 0, SRCCOPY);
        ReleaseDC(Handle, dc);
    }

    void Cleanup()
    {
        before?.Dispose(); before = null;
        after?.Dispose(); after = null;
        back?.Dispose(); back = null;
    }

    public void Dispose()
    {
        Cleanup();
        DestroyHandle();
    }
}
