using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text;
using static SpaceSwipe.Native;

namespace SpaceSwipe;

/// <summary>
/// Per-monitor overview (like macOS Mission Control with separate Spaces per display).
/// Everything is drawn from one "progress" value: 0 = the normal screen, 1 = fully open. Dragging three fingers
/// up moves the progress with the fingers: the current app shrinks into its card, the other cards fade in and the
/// background blurs. Choosing a card runs the progress back to 0 with that card growing to full screen.
/// </summary>
internal sealed class OverviewForm : Form
{
    sealed class Card
    {
        public required SpaceSwitcher.SpaceItem Item;
        public string Label = "";
        public Icon? Icon;
        public IntPtr Thumb;
        public Rectangle Crop;      // visible part of the source window (empty = whole window)
        public Rectangle Rect;      // where it's drawn when open (glides towards Home while cards are rearranged)
        public Rectangle Home;      // its slot in the layout
        public bool IsCurrent;
        public Shot? Image;         // desktop card
        public Shot? Snap;          // exact screenshot used while this card is the one zooming (no live-preview blip)
    }

    /// <summary>An ordinary window on the desktop, shown as a small live preview inside the Desktop card.</summary>
    sealed class MiniWin
    {
        public IntPtr Hwnd;
        public IntPtr Thumb;
        public Rectangle Crop;      // visible frame inside the thumbnail source (drops the invisible resize borders)
        public RectangleF Real;     // where it really is, as a fraction of the work area
        public RectangleF Home;     // its spot in the Desktop card when spread out, same units
        public double Lift;         // 0..1: grows a little while the pointer is over it
    }

    enum State { Opening, Open, Closing }

    static readonly Color Accent = Color.FromArgb(0x8B, 0x5C, 0xF6);

    readonly MonitorDesc mon;
    readonly List<Card> cards = new();
    Shot clean;                     // the monitor as it looked when the overview started (refreshed after changes)
    readonly System.Windows.Forms.Timer glideTimer = new() { Interval = 10 };
    int pressIndex = -1, dragIndex = -1;
    Point pressPos, dragOffset, cursor;
    MonitorDesc? dropMonitor;
    string? lastDropLogged = "";
    enum Side { Top, Right, Bottom, Left }
    double bannerAnim;              // 0 = hidden, 1 = shown (eases towards the target each frame)
    string bannerText = "";
    Side bannerSide;
    bool screenChanged;             // an app was moved away, so the snapshot of the screen is stale

    /// <summary>The user reordered the app cards (new order of app windows, Desktop excluded).</summary>
    public event Action<List<IntPtr>>? Reordered;
    /// <summary>The user dragged an app card onto another monitor.</summary>
    public event Action<IntPtr, MonitorDesc>? MoveRequested;

    int CurrentIndex => cards.FindIndex(c => c.IsCurrent);
    readonly Shot black = Shot.Blank(1, 1);
    readonly Task<Shot> blurTask;   // blurred + dimmed backdrop, made in the background
    readonly IntPtr priorForeground;
    readonly Func<Shot?>? captureDesktop;
    bool needDesktopCapture;
    readonly List<MiniWin> minis = new();   // the desktop's windows, bottom of the z-order first
    IntPtr deskThumb;                       // live preview of the desktop itself (wallpaper + icons, no windows)
    Rectangle deskCrop;
    int DesktopIndex => cards.FindIndex(c => c.Item.Hwnd == IntPtr.Zero);
    int hoverMini = -1, pressMini = -1;     // desktop window under the pointer / pressed (index into minis)
    double miniDim;                         // 0..1: the other desktop windows dim while one is hovered

    /// <summary>A desktop window was picked in the Desktop card: go to the desktop with it in front.</summary>
    public event Action<IntPtr>? WindowChosen;
    Shot? back;
    static readonly Dictionary<uint, string> appNames = new();
    float scale = 1f;
    int cols = 1, hover = -1, selected, focusIndex;
    double progress;
    State state = State.Opening;

    /// <summary>Raised when a space is picked, before the zoom-in (zero = Desktop).</summary>
    public event Action<IntPtr>? Chosen;

    public bool IsOpen => state == State.Open;

    /// <summary>Normally true. The overview test turns it off so the open overview can be photographed.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ExcludeFromCapture { get; init; } = true;

    public OverviewForm(MonitorDesc mon, List<SpaceSwitcher.SpaceItem> spaces, Func<Shot?>? captureDesktop = null,
                        List<IntPtr>? desktopWindows = null)
    {
        this.mon = mon;
        this.captureDesktop = captureDesktop;
        priorForeground = GetForegroundWindow();

        float ww = Math.Max(1, mon.Work.Width), wh = Math.Max(1, mon.Work.Height);
        foreach (var h in desktopWindows ?? new List<IntPtr>())
        {
            var f = VisibleFrame(h);
            minis.Add(new MiniWin
            {
                Hwnd = h,
                Real = new RectangleF((f.Left - mon.Work.Left) / ww, (f.Top - mon.Work.Top) / wh, f.Width / ww, f.Height / wh),
            });
        }
        LayoutMinis();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        BackColor = Color.Black;
        Bounds = new Rectangle(mon.Bounds.Left, mon.Bounds.Top, mon.Bounds.Width, mon.Bounds.Height);

        clean = Shot.Capture(mon.Bounds);
        PatBlt(black.Dc, 0, 0, 1, 1, BLACKNESS);
        var cleanCopy = clean.ToBitmap();
        blurTask = Task.Run(() =>
        {
            using (cleanCopy)
            using (var blurred = MakeBackdrop(cleanCopy))
                return Shot.FromBitmap(blurred);
        });

        foreach (var s in spaces)
        {
            var card = new Card { Item = s, IsCurrent = s.Current };
            if (s.Hwnd == IntPtr.Zero)
            {
                card.Label = "Desktop";
                if (s.Current)
                {
                    // We're looking at the desktop right now: use it, and remember it for next time.
                    var w = WorkInClient();
                    card.Image = Shot.Blank(w.Width, w.Height);
                    BitBlt(card.Image.Dc, 0, 0, w.Width, w.Height, clean.Dc, w.Left, w.Top, SRCCOPY);
                    var keep = Shot.Blank(w.Width, w.Height);
                    BitBlt(keep.Dc, 0, 0, w.Width, w.Height, clean.Dc, w.Left, w.Top, SRCCOPY);
                    DesktopCache.Set(mon.Device, keep);
                }
                else
                {
                    // Hidden behind apps: use the last look we saw, or photograph it once the overview covers the screen.
                    card.Image = DesktopCache.CopyOf(mon.Device);
                    needDesktopCapture = card.Image == null;
                }
            }
            else
            {
                card.Label = AppName(s.Hwnd, s.Title);
                var hIcon = WindowIcon(s.Hwnd);
                if (hIcon != IntPtr.Zero) try { card.Icon = Icon.FromHandle(hIcon); } catch { }
            }
            cards.Add(card);
        }
        int ci = Math.Max(0, CurrentIndex);
        selected = focusIndex = ci;
        glideTimer.Tick += (_, _) => Glide();

        // App cards (including the one that zooms) are live previews: true colours, even for HDR-rendering apps.
        // Only the desktop, which has no window, is a picture.
    }

    /// <summary>
    /// Spread the desktop's windows out inside the Desktop card so none overlap, keeping their sizes in proportion
    /// and roughly where they are: split into rows by height on screen, left to right within a row, all scaled by
    /// the largest factor that fits. Computed in work-area pixels, stored as fractions of the card.
    /// </summary>
    void LayoutMinis()
    {
        int n = minis.Count;
        if (n == 0) return;
        double W = Math.Max(1, mon.Work.Width), H = Math.Max(1, mon.Work.Height);
        double gap = W * 0.03;
        double areaL = W * 0.06, areaT = H * 0.08, areaW = W * 0.88, areaH = H * 0.84;
        var px = minis.Select(m => (w: m.Real.Width * W, h: m.Real.Height * H, cx: (m.Real.X + m.Real.Width / 2) * W, cy: (m.Real.Y + m.Real.Height / 2) * H)).ToList();
        var byY = Enumerable.Range(0, n).OrderBy(i => px[i].cy).ThenBy(i => px[i].cx).ToList();

        double best = -1;
        List<List<int>>? bestRows = null;
        for (int r = 1; r <= n; r++)
        {
            var rows = new List<List<int>>();
            for (int k = 0, idx = 0; k < r; k++)
            {
                int cnt = n / r + (k < n % r ? 1 : 0);
                rows.Add(byY.Skip(idx).Take(cnt).OrderBy(i => px[i].cx).ToList());
                idx += cnt;
            }
            double s = double.MaxValue, sumH = 0;
            foreach (var row in rows)
            {
                s = Math.Min(s, (areaW - gap * (row.Count - 1)) / row.Sum(i => px[i].w));
                sumH += row.Max(i => px[i].h);
            }
            s = Math.Min(s, (areaH - gap * (r - 1)) / sumH);
            if (s > best + 1e-9) { best = s; bestRows = rows; }
        }
        if (bestRows == null) return;
        double sc = Math.Clamp(best, 0.02, 0.85);
        double total = bestRows.Sum(row => row.Max(i => px[i].h) * sc) + gap * (bestRows.Count - 1);
        double y = areaT + Math.Max(0, (areaH - total) / 2);
        foreach (var row in bestRows)
        {
            double rowH = row.Max(i => px[i].h) * sc;
            double rowW = row.Sum(i => px[i].w * sc) + gap * (row.Count - 1);
            double x = areaL + (areaW - rowW) / 2;
            foreach (var i in row)
            {
                double w = px[i].w * sc, h = px[i].h * sc;
                minis[i].Home = new RectangleF((float)(x / W), (float)((y + (rowH - h) / 2) / H), (float)(w / W), (float)(h / H));
                x += w + gap;
            }
            y += rowH + gap;
        }
    }

    static Shot Crop(Shot src, Rectangle r)
    {
        var s = Shot.Blank(r.Width, r.Height);
        BitBlt(s.Dc, 0, 0, r.Width, r.Height, src.Dc, r.Left, r.Top, SRCCOPY);
        return s;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)WS_EX_TOOLWINDOW; // not in Alt+Tab
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true; // take focus only once fully open

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED) { m.Result = IntPtr.Zero; return; } // stay in physical pixels
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        scale = Math.Max(1f, GetDpiForWindow(Handle) / 96f);
        if (ExcludeFromCapture) SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); // so we can photograph what's underneath
        int on = 1;
        DwmSetWindowAttribute(Handle, DWMWA_TRANSITIONS_FORCEDISABLED, ref on, 4); // no window-open animation
        EnsureBackBuffer();
        foreach (var c in cards.Where(c => c.Item.Hwnd != IntPtr.Zero))
        {
            if (DwmRegisterThumbnail(Handle, c.Item.Hwnd, out var t) != 0) continue;
            c.Thumb = t;
            DwmQueryThumbnailSourceSize(t, out var size);
            var vis = c.Item.Kind == WinKind.FullScreen ? mon.Bounds : mon.Work;
            c.Crop = size.cx >= vis.Width && size.cy >= vis.Height
                ? new Rectangle((size.cx - vis.Width) / 2, (size.cy - vis.Height) / 2, vis.Width, vis.Height)
                : Rectangle.Empty;
        }
        if (minis.Count > 0) RegisterDesktopThumbs();
    }

    /// <summary>
    /// The Desktop card: a live preview of the desktop itself (the shell's desktop window: wallpaper and icons,
    /// no app windows), with each desktop window's live preview on top. Registered after the app cards, the
    /// desktop first and then the windows bottom-up, so they stack the right way.
    /// </summary>
    void RegisterDesktopThumbs()
    {
        var shell = GetShellWindow();
        if (shell != IntPtr.Zero && DwmRegisterThumbnail(Handle, shell, out var dt) == 0)
        {
            deskThumb = dt;
            GetWindowRect(shell, out var sr);
            deskCrop = new Rectangle(mon.Work.Left - sr.Left, mon.Work.Top - sr.Top, mon.Work.Width, mon.Work.Height);
        }
        foreach (var m in minis) RegisterMini(m);
    }

    /// <summary>(Re-)register a desktop window's live preview; the latest registered is drawn on top.</summary>
    void RegisterMini(MiniWin m)
    {
        if (m.Thumb != IntPtr.Zero) { DwmUnregisterThumbnail(m.Thumb); m.Thumb = IntPtr.Zero; }
        if (DwmRegisterThumbnail(Handle, m.Hwnd, out var t) != 0) return;
        m.Thumb = t;
        // The source is the whole window rect; show only its visible frame (scaled, in case of DPI virtualization).
        DwmQueryThumbnailSourceSize(t, out var size);
        GetWindowRect(m.Hwnd, out var wr);
        var f = VisibleFrame(m.Hwnd);
        if (wr.Width <= 0 || wr.Height <= 0 || size.cx <= 0 || size.cy <= 0) return;
        double sx = (double)size.cx / wr.Width, sy = (double)size.cy / wr.Height;
        m.Crop = Rectangle.FromLTRB(
            (int)Math.Round((f.Left - wr.Left) * sx), (int)Math.Round((f.Top - wr.Top) * sy),
            (int)Math.Round((f.Right - wr.Left) * sx), (int)Math.Round((f.Bottom - wr.Top) * sy));
    }

    // ---------------- opening / closing (driven by fingers or animated) ----------------

    /// <summary>Show at progress 0 (looks exactly like the screen), ready to follow the fingers.</summary>
    public void BeginOpen()
    {
        state = State.Opening;
        focusIndex = CurrentIndex;
        progress = 0;

        // Keep the window off the screen (cloaked) until its first frame is painted, so it can't flash.
        _ = Handle;
        int cloak = 1;
        DwmSetWindowAttribute(Handle, DWMWA_CLOAK, ref cloak, 4);
        Show();
        // Pin it to exactly the monitor, in physical pixels, whatever WinForms did while showing it.
        SetWindowPos(Handle, HWND_TOPMOST, mon.Bounds.Left, mon.Bounds.Top, mon.Bounds.Width, mon.Bounds.Height, SWP_NOACTIVATE);
        Render(0, false);
        DwmFlush();
        cloak = 0;
        DwmSetWindowAttribute(Handle, DWMWA_CLOAK, ref cloak, 4);

        if (needDesktopCapture)
        {
            needDesktopCapture = false;
            DwmFlush();
            DwmFlush(); // the overview must be on screen before anything moves underneath it
            var desk = cards.FirstOrDefault(c => c.Item.Hwnd == IntPtr.Zero);
            var shot = captureDesktop?.Invoke();
            if (desk != null && shot != null) desk.Image = shot;
            else if (desk != null && LoadWallpaper() is { } wp) using (wp) desk.Image = Shot.FromBitmap(wp);
            else shot?.Dispose();
        }
    }

    /// <summary>Follow the fingers while opening (0..1, a little beyond 1 stretches).</summary>
    public void DragOpen(double p)
    {
        if (state != State.Opening) return;
        progress = Rubber(p);
        Render(progress, false);
    }

    /// <summary>Fingers lifted while opening: finish opening, or fall back and close.</summary>
    public void EndOpen(bool open)
    {
        if (state != State.Opening) return;
        if (open)
        {
            AnimateTo(1);
            state = State.Open;
            SpaceSwitcher.Focus(Handle);
            Activate();
            Render(1, true);
        }
        else
        {
            state = State.Closing;
            AnimateTo(0);
            Close();
        }
    }

    /// <summary>Open with the animation (keyboard shortcut).</summary>
    public void OpenAnimated()
    {
        BeginOpen();
        EndOpen(true);
    }

    /// <summary>Follow the fingers while swiping down to close (0 = still open, 1 = closed).</summary>
    public void DragClose(double t)
    {
        if (state != State.Open) return;
        focusIndex = CurrentIndex;
        progress = 1 - Math.Clamp(t, 0, 1);
        Render(progress, false);
    }

    /// <summary>Fingers lifted while swiping down: close, or spring back open.</summary>
    public void EndClose(bool close)
    {
        if (state != State.Open) return;
        if (close) Dismiss();
        else
        {
            AnimateTo(1);
            Render(1, true);
        }
    }

    /// <summary>Pick a space: switch to it underneath, then grow its card to full screen.</summary>
    public void Choose(int i) => Choose(i, IntPtr.Zero);

    /// <summary>
    /// Pick a space; for the desktop, optionally one of its windows to bring to the front (<paramref name="window"/>).
    /// </summary>
    void Choose(int i, IntPtr window)
    {
        if (state != State.Open || i < 0 || i >= cards.Count) return;
        state = State.Closing;
        focusIndex = i;

        if (cards[i].Item.Hwnd == IntPtr.Zero && deskThumb != IntPtr.Zero)
        {
            // The desktop with live previews: zoom straight away (the windows glide home, the picked one on top),
            // and do the real switch behind the last frame, once everything is at rest.
            int k = minis.FindIndex(m => m.Hwnd == window);
            if (k >= 0)
            {
                var m = minis[k];
                minis.RemoveAt(k);
                minis.Add(m);
                RegisterMini(m);
            }
            hoverMini = -1;
            miniDim = 0;
            foreach (var m in minis) m.Lift = 0;
            glideTimer.Stop();
            foreach (var x in cards) x.Rect = x.Home;
            AnimateTo(0, beforeLastFrame: () =>
            {
                var sw = Stopwatch.StartNew();
                if (window != IntPtr.Zero) WindowChosen?.Invoke(window);
                else Chosen?.Invoke(IntPtr.Zero);
                DwmFlush();
                Thread.Sleep(25);
                DwmFlush();
                RefreshScreen(); // the last frame is the real result
                Log.Write($"overview: switched to the desktop{(window != IntPtr.Zero ? " (window to front)" : "")} in {sw.ElapsedMilliseconds} ms");
            });
            Close();
            return;
        }

        Chosen?.Invoke(cards[i].Item.Hwnd); // the real switch happens underneath this window

        // An app zooms as its live preview (now focused, so it matches exactly). The desktop zooms as a fresh picture.
        DwmFlush();
        Thread.Sleep(25);
        DwmFlush();
        var c = cards[i];
        if (c.Item.Hwnd == IntPtr.Zero && deskThumb == IntPtr.Zero)
        {
            // (With the live desktop preview, the desktop's windows glide back into place instead.)
            var full = FullRect(i);
            c.Snap?.Dispose();
            c.Snap = Shot.Capture(new RECT
            {
                Left = full.Left + mon.Bounds.Left, Top = full.Top + mon.Bounds.Top,
                Right = full.Right + mon.Bounds.Left, Bottom = full.Bottom + mon.Bounds.Top,
            });
        }
        RefreshScreen(); // the background fades to the real result, not the old screen

        glideTimer.Stop();
        foreach (var x in cards) x.Rect = x.Home;
        AnimateTo(0);
        Close();
    }

    /// <summary>Re-photograph the real screen underneath (this window is excluded from capture).</summary>
    void RefreshScreen()
    {
        var fresh = Shot.Capture(mon.Bounds);
        clean.Dispose();
        clean = fresh;
    }

    /// <summary>Close without switching: the current space grows back to full screen.</summary>
    public void Dismiss()
    {
        if (state == State.Closing) return;
        bool wasOpen = state == State.Open;
        state = State.Closing;
        glideTimer.Stop();
        if (dragIndex >= 0) { dragIndex = -1; dropMonitor = null; }
        foreach (var x in cards) x.Rect = x.Home;
        if (screenChanged)
        {
            // An app was moved to another display: fade back to the real screen as it is now.
            DwmFlush();
            RefreshScreen();
            focusIndex = -1;
        }
        else focusIndex = CurrentIndex;
        AnimateTo(0);
        // Give focus back only once the animation is done: it can block for ~100 ms, which froze the windows
        // right as the fingers lifted. The overview still shows the final (exact) frame meanwhile.
        if (wasOpen && priorForeground != IntPtr.Zero && IsWindow(priorForeground))
        {
            var sw = Stopwatch.StartNew();
            SpaceSwitcher.Focus(priorForeground);
            Log.Write($"overview: focus returned in {sw.ElapsedMilliseconds} ms");
        }
        Close();
    }

    /// <param name="beforeLastFrame">Runs just before the final frame (everything is nearly at rest on screen meanwhile).</param>
    void AnimateTo(double target, Action? beforeLastFrame = null)
    {
        double from = progress;
        double ms = Math.Clamp(Math.Abs(target - from) * 380, 110, 320);
        var sw = Stopwatch.StartNew();
        double last = 0, worst = 0, worstRender = 0;
        int frames = 0;
        while (true)
        {
            double t = Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
            if (t >= 1 && beforeLastFrame != null)
            {
                beforeLastFrame();
                beforeLastFrame = null;
                last = sw.Elapsed.TotalMilliseconds; // (not an animation frame)
            }
            progress = from + (target - from) * EaseOut(t);
            double r0 = sw.Elapsed.TotalMilliseconds;
            Render(progress, false);
            worstRender = Math.Max(worstRender, sw.Elapsed.TotalMilliseconds - r0);
            if (DwmFlush() != 0) Thread.Sleep(6);
            double now = sw.Elapsed.TotalMilliseconds;
            worst = Math.Max(worst, now - last);
            last = now;
            frames++;
            if (t >= 1) break;
        }
        Log.Write($"overview animate {from:F2}->{target:F2}: {frames} frames in {last:F0} ms, slowest frame {worst:F1} ms (render {worstRender:F1} ms)");
    }

    static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    static double Rubber(double p) => p <= 1 ? Math.Max(0, p) : 1 + (1 - 1 / ((p - 1) * 3 + 1)) * 0.04;

    // ---------------- layout ----------------

    Rectangle WorkInClient() =>
        new(mon.Work.Left - mon.Bounds.Left, mon.Work.Top - mon.Bounds.Top, mon.Work.Width, mon.Work.Height);

    Rectangle FullRect(int i) => FullRectFor(cards[i].Item.Kind);

    Rectangle FullRectFor(WinKind kind) =>
        kind == WinKind.FullScreen ? new Rectangle(0, 0, mon.Bounds.Width, mon.Bounds.Height) : WorkInClient();

    /// <summary>Lay the cards out in their current order. snap = jump there; otherwise they glide.</summary>
    void LayoutCards(bool snap = true)
    {
        var slots = Slots(cards.Count);
        for (int i = 0; i < cards.Count; i++)
        {
            cards[i].Home = slots[i];
            if (snap) cards[i].Rect = slots[i];
        }
        if (!snap) glideTimer.Start();
    }

    /// <summary>Card positions for n cards, centred in rows.</summary>
    List<Rectangle> Slots(int n)
    {
        var result = new List<Rectangle>(n);
        if (n == 0) return result;
        double w = ClientSize.Width, h = ClientSize.Height;
        double aspect = (double)mon.Work.Width / mon.Work.Height;
        double labelH = 40 * scale, gap = 40 * scale;
        double availW = w * 0.88, availH = h * 0.70;

        double bestW = 0; int bestRows = 1;
        for (int rows = 1; rows <= n; rows++)
        {
            int c = (int)Math.Ceiling(n / (double)rows);
            double cw = Math.Min((availW - (c - 1) * gap) / c, ((availH - (rows - 1) * gap) / rows - labelH) * aspect);
            cw = Math.Min(cw, w * 0.40);
            if (cw > bestW) { bestW = cw; bestRows = rows; }
        }

        cols = (int)Math.Ceiling(n / (double)bestRows);
        double ch = bestW / aspect;
        double total = bestRows * (ch + labelH) + (bestRows - 1) * gap;
        double top = (h - total) / 2;
        for (int r = 0, i = 0; r < bestRows; r++)
        {
            int k = Math.Min(cols, n - r * cols);
            double rowW = k * bestW + (k - 1) * gap;
            double left = (w - rowW) / 2;
            for (int j = 0; j < k; j++, i++)
                result.Add(new Rectangle((int)(left + j * (bestW + gap)), (int)(top + r * (ch + labelH + gap)), (int)bestW, (int)ch));
        }
        return result;
    }

    // ---------------- dragging cards (reorder, or move to another monitor) ----------------

    /// <summary>Animate cards towards their slots; the dragged card follows the cursor.</summary>
    void Glide()
    {
        bool moving = dragIndex >= 0;

        double bannerTarget = dragIndex >= 0 && dropMonitor != null ? 1 : 0;
        if (Math.Abs(bannerTarget - bannerAnim) > 0.005)
        {
            bannerAnim += (bannerTarget - bannerAnim) * 0.2;
            moving = true;
        }
        else bannerAnim = bannerTarget;

        // Desktop windows under the pointer: ease the lift and the dimming of the others.
        moving |= Ease(ref miniDim, hoverMini >= 0 ? 1 : 0);
        for (int k = 0; k < minis.Count; k++)
            moving |= Ease(ref minis[k].Lift, k == hoverMini ? 1 : 0);

        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (i == dragIndex) { c.Rect = DraggedRect(c); continue; }
            var r = c.Rect;
            var target = c.Home;
            var next = new Rectangle(
                Step(r.Left, target.Left), Step(r.Top, target.Top), Step(r.Width, target.Width), Step(r.Height, target.Height));
            if (next != r) moving = true;
            c.Rect = next;
        }
        Render(1, true);
        if (!moving) glideTimer.Stop();
    }

    /// <summary>Move a 0..1 animation value a step towards its target; true while still moving.</summary>
    static bool Ease(ref double v, double target)
    {
        if (Math.Abs(target - v) < 0.01) { v = target; return false; }
        v += (target - v) * 0.3;
        return true;
    }

    static int Step(int from, int to)
    {
        int d = to - from;
        if (Math.Abs(d) <= 1) return to;
        return from + (int)Math.Round(d * 0.28);
    }

    /// <summary>The dragged card: slightly enlarged under the cursor, kept on this monitor.</summary>
    Rectangle DraggedRect(Card c)
    {
        // Lifted slightly while dragging; shrinks a little over a drop zone ("this will move away").
        double s = 1.06 - 0.24 * EaseOut(Math.Clamp(bannerAnim, 0, 1));
        int w = (int)(c.Home.Width * s), h = (int)(c.Home.Height * s);
        int x = Math.Clamp(cursor.X - (int)(dragOffset.X * s), 0, Math.Max(0, ClientSize.Width - w));
        int y = Math.Clamp(cursor.Y - (int)(dragOffset.Y * s), 0, Math.Max(0, ClientSize.Height - h));
        return new Rectangle(x, y, w, h);
    }

    void StartDrag(int i)
    {
        Log.Write($"overview: start dragging card {i} ({cards[i].Label})");
        lastDropLogged = "";
        dragIndex = i;
        var c = cards[i];
        dragOffset = new Point(pressPos.X - c.Home.Left, pressPos.Y - c.Home.Top);
        // Re-register its live preview so it draws above the other cards.
        if (c.Thumb != IntPtr.Zero)
        {
            DwmUnregisterThumbnail(c.Thumb);
            c.Thumb = IntPtr.Zero;
            if (DwmRegisterThumbnail(Handle, c.Item.Hwnd, out var t) == 0) c.Thumb = t;
        }
        glideTimer.Start();
    }

    void UpdateDrag(Point clientPos)
    {
        cursor = clientPos;
        var screen = new Point(clientPos.X + mon.Bounds.Left, clientPos.Y + mon.Bounds.Top);
        var b = new Rectangle(mon.Bounds.Left, mon.Bounds.Top, mon.Bounds.Width, mon.Bounds.Height);
        dropMonitor = null;
        if (!b.Contains(screen))
        {
            var other = WindowTracker.GetMonitor(MonitorFromPoint(new POINT { X = screen.X, Y = screen.Y }, MONITOR_DEFAULTTONEAREST));
            if (other.Device != mon.Device) dropMonitor = other;
        }
        else if (dragIndex >= 0)
        {
            // When the dragged card itself reaches an edge with another monitor beyond it, that edge is a drop zone.
            // (Measured from the card, not the cursor: you grab a card near its middle, and the card hits the
            // edge long before the cursor does.)
            var home = cards[dragIndex].Home;
            int cardW = (int)(home.Width * 1.06), cardH = (int)(home.Height * 1.06);
            var card = new Rectangle(clientPos.X - (int)(dragOffset.X * 1.06), clientPos.Y - (int)(dragOffset.Y * 1.06), cardW, cardH);
            int zone = (int)(24 * scale);
            var cs = ClientSize;
            int cx = Math.Clamp(screen.X, b.Left, b.Right - 1), cy = Math.Clamp(screen.Y, b.Top, b.Bottom - 1);
            POINT? beyond =
                card.Right >= cs.Width - zone ? new POINT { X = b.Right + 2, Y = cy } :
                card.Left <= zone ? new POINT { X = b.Left - 3, Y = cy } :
                card.Bottom >= cs.Height - zone ? new POINT { X = cx, Y = b.Bottom + 2 } :
                card.Top <= zone ? new POINT { X = cx, Y = b.Top - 3 } : null;
            if (beyond is { } pt)
            {
                var h = MonitorFromPoint(pt, 0 /* MONITOR_DEFAULTTONULL */);
                if (h != IntPtr.Zero)
                {
                    var other = WindowTracker.GetMonitor(h);
                    if (other.Device != mon.Device) dropMonitor = other;
                }
            }
        }

        if (dropMonitor is { } t)
        {
            bannerSide =
                t.Bounds.Left >= mon.Bounds.Right ? Side.Right :
                t.Bounds.Right <= mon.Bounds.Left ? Side.Left :
                t.Bounds.Top >= mon.Bounds.Bottom ? Side.Bottom : Side.Top;
            bannerText = bannerSide switch
            {
                Side.Right => "Move to the display on the right  →",
                Side.Left => "←  Move to the display on the left",
                Side.Bottom => "Move to the display below  ↓",
                _ => "Move to the display above  ↑",
            };
        }

        if (dropMonitor?.Device != lastDropLogged)
        {
            lastDropLogged = dropMonitor?.Device;
            Log.Write($"overview drag at screen ({screen.X},{screen.Y}) client ({clientPos.X},{clientPos.Y}) on {mon.Device} {b}: drop target = {dropMonitor?.Device ?? "none"}");
        }

        // Reorder: put the dragged card in the slot nearest the cursor (the Desktop always stays first).
        if (dropMonitor == null && cards.Count > 2)
        {
            var slots = Slots(cards.Count);
            int best = 1;
            double bestD = double.MaxValue;
            for (int s = 1; s < slots.Count; s++)
            {
                var sc = new Point(slots[s].Left + slots[s].Width / 2, slots[s].Top + slots[s].Height / 2);
                double d = Math.Pow(sc.X - cursor.X, 2) + Math.Pow(sc.Y - cursor.Y, 2);
                if (d < bestD) { bestD = d; best = s; }
            }
            if (best != dragIndex)
            {
                var c = cards[dragIndex];
                cards.RemoveAt(dragIndex);
                cards.Insert(best, c);
                dragIndex = best;
                selected = best;
                focusIndex = CurrentIndex;
                LayoutCards(snap: false);
            }
        }
        glideTimer.Start();
    }

    void EndDrag()
    {
        int i = dragIndex;
        dragIndex = -1;
        if (i < 0) return;
        var c = cards[i];
        Log.Write($"overview: dropped card {i} ({c.Label}) -> {(dropMonitor != null ? "move to " + dropMonitor.Device : "reorder")}");
        if (dropMonitor is { } target)
        {
            MoveRequested?.Invoke(c.Item.Hwnd, target);
            SpaceSwitcher.Focus(Handle); // maximizing it over there took focus; keep the overview open
            if (c.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(c.Thumb);
            c.Image?.Dispose();
            c.Snap?.Dispose();
            cards.RemoveAt(i);
            screenChanged = true;
            selected = Math.Clamp(selected, 0, cards.Count - 1);
            hover = -1;
        }
        else
        {
            Reordered?.Invoke(cards.Where(x => x.Item.Hwnd != IntPtr.Zero).Select(x => x.Item.Hwnd).ToList());
        }
        dropMonitor = null;
        focusIndex = CurrentIndex;
        LayoutCards(snap: false);
    }

    /// <summary>Ctrl+←/→: move the selected app card one place.</summary>
    void MoveSelected(int delta)
    {
        int to = selected + delta;
        if (selected < 1 || to < 1 || to >= cards.Count) return;
        (cards[selected], cards[to]) = (cards[to], cards[selected]);
        selected = to;
        focusIndex = CurrentIndex;
        LayoutCards(snap: false);
        Reordered?.Invoke(cards.Where(x => x.Item.Hwnd != IntPtr.Zero).Select(x => x.Item.Hwnd).ToList());
    }

    /// <summary>Where card i is at progress p: the focused card travels between full screen and its slot; others scale in.</summary>
    Rectangle RectAt(int i, double p)
    {
        var card = cards[i].Rect;
        if (i == focusIndex)
        {
            var full = FullRect(i);
            double q = Math.Min(1, p);
            var r = new Rectangle(
                (int)(full.Left + (card.Left - full.Left) * q), (int)(full.Top + (card.Top - full.Top) * q),
                (int)(full.Width + (card.Width - full.Width) * q), (int)(full.Height + (card.Height - full.Height) * q));
            return p <= 1 ? r : Scale(r, 1 + (p - 1));
        }
        return Scale(card, 0.85 + 0.15 * p);
    }

    static Rectangle Scale(Rectangle r, double s)
    {
        int w = (int)(r.Width * s), h = (int)(r.Height * s);
        return new Rectangle(r.Left + (r.Width - w) / 2, r.Top + (r.Height - h) / 2, w, h);
    }

    byte AlphaAt(int i, double p) =>
        i == focusIndex ? (byte)255 : (byte)(255 * Math.Clamp(p * 1.25 - 0.25, 0, 1));

    // ---------------- rendering ----------------

    /// <summary>Keep the back buffer exactly the size of the window (Windows may adjust it after creation).</summary>
    void EnsureBackBuffer()
    {
        var cs = ClientSize;
        if (back != null && back.W == cs.Width && back.H == cs.Height) return;
        Log.Write($"overview: client {cs.Width}x{cs.Height}, monitor {mon.Bounds.Width}x{mon.Bounds.Height}, buffer {(back == null ? "none" : $"{back.W}x{back.H}")}");
        back?.Dispose();
        back = Shot.Blank(Math.Max(1, cs.Width), Math.Max(1, cs.Height));
        LayoutCards();
    }

    void Render(double p, bool labels)
    {
        if (!IsHandleCreated) return;
        EnsureBackBuffer();
        if (back == null) return;
        int w = back.W, h = back.H;
        // Anything outside the captured monitor area (shouldn't happen) stays black rather than white.
        int cw = Math.Min(w, clean.W), ch = Math.Min(h, clean.H);
        if (cw < w || ch < h) PatBlt(back.Dc, 0, 0, w, h, BLACKNESS);

        // Background: the real screen, crossfading into the blurred + dimmed version.
        // (Each full-screen copy costs real frame time, so skip the ones that can't be seen.)
        byte bg = (byte)(255 * Math.Clamp(p, 0, 1));
        var blur = blurTask.IsCompletedSuccessfully ? blurTask.Result : null;
        if (blur != null && bg == 255 && blur.W >= cw && blur.H >= ch)
            BitBlt(back.Dc, 0, 0, cw, ch, blur.Dc, 0, 0, SRCCOPY);
        else
        {
            BitBlt(back.Dc, 0, 0, cw, ch, clean.Dc, 0, 0, SRCCOPY);
            if (bg > 0 && blur != null)
                AlphaBlend(back.Dc, 0, 0, Math.Min(cw, blur.W), Math.Min(ch, blur.H), blur.Dc, 0, 0, Math.Min(cw, blur.W), Math.Min(ch, blur.H), ConstantAlpha(bg));
            else if (bg > 0)
                AlphaBlend(back.Dc, 0, 0, w, h, black.Dc, 0, 0, 1, 1, ConstantAlpha((byte)(bg * 0.55)));
        }

        // Shadows behind cards once settled (live previews are drawn on top by the compositor).
        Graphics? g = labels ? Graphics.FromHdc(back.Dc) : null;
        if (g != null)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
            foreach (var c in cards) FillRounded(g, shadow, Rectangle.Inflate(c.Rect, (int)(6 * scale), (int)(6 * scale)), 12 * scale);
        }

        // Cards we draw ourselves: the zooming card's exact screenshot, and the desktop card.
        SetStretchBltMode(back.Dc, labels ? HALFTONE : COLORONCOLOR);
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            var r = RectAt(i, p);
            byte a = AlphaAt(i, p);
            if (a == 0 || r.Width <= 0 || r.Height <= 0) continue;
            if (i == focusIndex && c.Snap != null && i != dragIndex)
            {
                StretchBlt(back.Dc, r.Left, r.Top, r.Width, r.Height, c.Snap.Dc, 0, 0, c.Snap.W, c.Snap.H, SRCCOPY);
                continue;
            }
            if (c.Item.Hwnd != IntPtr.Zero) continue;
            if (minis.Count > 0 && DesktopLiveCovers(p) && !(i == focusIndex && c.Snap != null))
                continue; // the live desktop preview is on top of it; don't spend frame time drawing what can't be seen
            if (i == focusIndex && minis.Count > 0)
            {
                // The desktop growing to / shrinking from full screen: the real screen (exact, shadows and all)
                // shows through while the live desktop preview on top is still fading in.
                var wk = WorkInClient();
                StretchBlt(back.Dc, r.Left, r.Top, r.Width, r.Height, clean.Dc, wk.Left, wk.Top, wk.Width, wk.Height, SRCCOPY);
            }
            else if (c.Image != null)
            {
                var src = CoverSource(c.Image.W, c.Image.H, r.Width, r.Height);
                if (a == 255) StretchBlt(back.Dc, r.Left, r.Top, r.Width, r.Height, c.Image.Dc, src.Left, src.Top, src.Width, src.Height, SRCCOPY);
                else AlphaBlend(back.Dc, r.Left, r.Top, r.Width, r.Height, c.Image.Dc, src.Left, src.Top, src.Width, src.Height, ConstantAlpha(a));
            }
            else
            {
                g ??= Graphics.FromHdc(back.Dc);
                using var b = new LinearGradientBrush(r, Color.FromArgb(a, 60, 45, 110), Color.FromArgb(a, 25, 20, 45), 90f);
                g.FillRectangle(b, r);
            }
        }

        if (labels && g != null) DrawLabels(g);
        g?.Dispose();

        var dc = GetDC(Handle);
        BitBlt(dc, 0, 0, w, h, back.Dc, 0, 0, SRCCOPY);
        ReleaseDC(Handle, dc);
        UpdateThumbs(p);
    }

    void DrawLabels(Graphics g)
    {
        using var font = new Font("Segoe UI Semibold", 11f * scale, GraphicsUnit.Point);
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
        int iconSize = (int)(20 * scale);
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            var r = c.Rect;
            bool highlight = i == hover || i == selected;
            if (highlight || c.Item.Current)
            {
                using var pen = new Pen(highlight ? Accent : Color.FromArgb(160, 255, 255, 255), (highlight ? 4 : 2) * scale);
                g.DrawRectangle(pen, Rectangle.Inflate(r, (int)(3 * scale), (int)(3 * scale)));
            }

            var labelRect = new Rectangle(r.Left, r.Bottom + (int)(10 * scale), r.Width, (int)(26 * scale));
            var textSize = TextRenderer.MeasureText(g, c.Label, font, new Size(r.Width - iconSize - 8, labelRect.Height), flags);
            int totalW = Math.Min(r.Width, textSize.Width + (c.Icon != null ? iconSize + (int)(8 * scale) : 0));
            int x = r.Left + (r.Width - totalW) / 2;
            if (c.Icon != null)
            {
                try { g.DrawIcon(c.Icon, new Rectangle(x, labelRect.Top + (labelRect.Height - iconSize) / 2, iconSize, iconSize)); } catch { }
                x += iconSize + (int)(8 * scale);
            }
            TextRenderer.DrawText(g, c.Label, font, new Rectangle(x, labelRect.Top, r.Right - x, labelRect.Height), Color.White, flags);
        }

        DrawDropBanner(g);

        using var hint = new Font("Segoe UI", 9.5f * scale, GraphicsUnit.Point);
        var hr = new Rectangle(0, ClientSize.Height - (int)(56 * scale), ClientSize.Width, (int)(30 * scale));
        string text = dragIndex >= 0
            ? "Drop to reorder  ·  drag onto another display to move the app there"
            : minis.Count > 0
                ? "Click an app, or a window on the desktop  ·  drag to reorder or onto another display  ·  Esc or swipe down to close"
                : "Click an app  ·  drag to reorder or onto another display  ·  Esc or swipe down to close";
        TextRenderer.DrawText(g, text, hint, hr,
            Color.FromArgb(190, 255, 255, 255), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>
    /// "Move to another display" feedback: a frosted pill that slides down from the top, plus a soft glow on the
    /// edge you're heading for. Neutral (no accent colour), and it eases in and out with <see cref="bannerAnim"/>.
    /// </summary>
    void DrawDropBanner(Graphics g)
    {
        double e = EaseOut(Math.Clamp(bannerAnim, 0, 1));
        if (e < 0.01 || bannerText.Length == 0) return;
        var cs = ClientSize;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        // Edge glow.
        int gw = (int)(120 * scale);
        Rectangle glow = bannerSide switch
        {
            Side.Right => new Rectangle(cs.Width - gw, 0, gw, cs.Height),
            Side.Left => new Rectangle(0, 0, gw, cs.Height),
            Side.Bottom => new Rectangle(0, cs.Height - gw, cs.Width, gw),
            _ => new Rectangle(0, 0, cs.Width, gw),
        };
        var (from, to) = bannerSide switch
        {
            Side.Right => (new Point(glow.Left, 0), new Point(glow.Right, 0)),
            Side.Left => (new Point(glow.Right, 0), new Point(glow.Left, 0)),
            Side.Bottom => (new Point(0, glow.Top), new Point(0, glow.Bottom)),
            _ => (new Point(0, glow.Bottom), new Point(0, glow.Top)),
        };
        using (var glowBrush = new LinearGradientBrush(from, to, Color.FromArgb(0, 255, 255, 255), Color.FromArgb((int)(70 * e), 255, 255, 255)))
            g.FillRectangle(glowBrush, glow);

        // Frosted pill, sliding down from above the top edge.
        using var font = new Font("Segoe UI Semibold", 12f * scale, GraphicsUnit.Point);
        var size = g.MeasureString(bannerText, font);
        int padX = (int)(26 * scale), padY = (int)(12 * scale);
        int w = (int)size.Width + padX * 2, h = (int)size.Height + padY * 2;
        int restY = (int)(32 * scale);
        int y = (int)(-h + (restY + h) * e);
        var pill = new Rectangle((cs.Width - w) / 2, y, w, h);
        using (var shadow = new SolidBrush(Color.FromArgb((int)(60 * e), 0, 0, 0)))
            FillRounded(g, shadow, new Rectangle(pill.X, pill.Y + (int)(4 * scale), pill.Width, pill.Height), h / 2f);
        using (var fill = new SolidBrush(Color.FromArgb((int)(58 * e), 255, 255, 255)))
            FillRounded(g, fill, pill, h / 2f);
        using (var path = Rounded(pill, h / 2f))
        using (var border = new Pen(Color.FromArgb((int)(110 * e), 255, 255, 255), 1.2f * scale))
            g.DrawPath(border, path);
        using var text = new SolidBrush(Color.FromArgb((int)(245 * e), 255, 255, 255));
        g.DrawString(bannerText, font, text, pill.X + padX, pill.Y + padY);
    }

    static GraphicsPath Rounded(Rectangle r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    void UpdateThumbs(double p)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c.Thumb == IntPtr.Zero) continue;
            var r = RectAt(i, p);
            byte a = AlphaAt(i, p);
            var props = new DWM_THUMBNAIL_PROPERTIES();
            if (a == 0 || r.Width <= 0 || r.Height <= 0 || (i == focusIndex && c.Snap != null && i != dragIndex))
            {
                props.dwFlags = DWM_TNP_VISIBLE;
                props.fVisible = 0;
            }
            else
            {
                props.dwFlags = DWM_TNP_VISIBLE | DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY;
                props.fVisible = 1;
                props.opacity = a;
                props.rcDestination = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
                if (!c.Crop.IsEmpty)
                {
                    props.dwFlags |= DWM_TNP_RECTSOURCE;
                    props.rcSource = new RECT { Left = c.Crop.Left, Top = c.Crop.Top, Right = c.Crop.Right, Bottom = c.Crop.Bottom };
                }
            }
            DwmUpdateThumbnailProperties(c.Thumb, ref props);
        }
        UpdateDesktopThumbs(p);
    }

    /// <summary>
    /// Place the Desktop card's live previews. When the desktop is the card growing to / shrinking from full screen,
    /// its windows travel between where they really are (progress 0) and their spread-out spots (1).
    /// </summary>
    void UpdateDesktopThumbs(double p)
    {
        if (minis.Count == 0) return;
        int d = DesktopIndex;
        bool focus = d >= 0 && d == focusIndex;
        var card = d >= 0 ? RectAt(d, p) : Rectangle.Empty;
        byte a = d >= 0 ? AlphaAt(d, p) : (byte)0;
        bool hidden = d < 0 || a == 0 || card.Width <= 0 || card.Height <= 0 || (focus && cards[d].Snap != null);
        double q = focus ? Math.Clamp(p, 0, 1) : 1;

        if (deskThumb != IntPtr.Zero)
        {
            // Covers everything except the exact start/end frame (progress 0), where the photo of the real screen
            // shows instead: it matches the screen pixel for pixel (shadows too). No crossfade in between, or
            // copies of the windows would show up at their final spots while the previews are still on the way.
            byte da = focus && !DesktopLiveCovers(p) ? (byte)0 : a;
            Place(deskThumb, hidden || da == 0 ? Rectangle.Empty : card, deskCrop, da);
        }
        for (int k = 0; k < minis.Count; k++)
        {
            var m = minis[k];
            if (m.Thumb == IntPtr.Zero) continue;
            var dest = MiniRect(m, card, q);
            // Hover: the window under the pointer grows a little; the others dim a little.
            if (m.Lift > 0.001) dest = Scale(dest, 1 + 0.06 * m.Lift);
            byte ma = k == hoverMini ? a : (byte)(a * (1 - 0.16 * miniDim));
            // Keep it inside the card (a window may hang off the screen or over the taskbar): clip, and crop the source to match.
            var clip = Rectangle.Intersect(dest, card);
            var src = m.Crop.IsEmpty ? Rectangle.Empty : m.Crop;
            if (!src.IsEmpty && clip != dest && dest.Width > 0 && dest.Height > 0 && clip.Width > 0 && clip.Height > 0)
            {
                double sx = (double)src.Width / dest.Width, sy = (double)src.Height / dest.Height;
                src = Rectangle.FromLTRB(
                    src.Left + (int)Math.Round((clip.Left - dest.Left) * sx), src.Top + (int)Math.Round((clip.Top - dest.Top) * sy),
                    src.Right - (int)Math.Round((dest.Right - clip.Right) * sx), src.Bottom - (int)Math.Round((dest.Bottom - clip.Bottom) * sy));
            }
            Place(m.Thumb, hidden || clip.Width <= 0 || clip.Height <= 0 ? Rectangle.Empty : clip, src, ma);
        }
    }

    /// <summary>Where a desktop window is drawn in the Desktop card: between its real spot (q = 0) and its spread-out spot (1).</summary>
    static Rectangle MiniRect(MiniWin m, Rectangle card, double q)
    {
        float fx = (float)(m.Real.X + (m.Home.X - m.Real.X) * q), fy = (float)(m.Real.Y + (m.Home.Y - m.Real.Y) * q);
        float fw = (float)(m.Real.Width + (m.Home.Width - m.Real.Width) * q), fh = (float)(m.Real.Height + (m.Home.Height - m.Real.Height) * q);
        return Rectangle.FromLTRB(
            card.Left + (int)Math.Round(fx * card.Width), card.Top + (int)Math.Round(fy * card.Height),
            card.Left + (int)Math.Round((fx + fw) * card.Width), card.Top + (int)Math.Round((fy + fh) * card.Height));
    }

    /// <summary>Overview test: hover desktop window k (runs the hover animation to the end).</summary>
    internal void TestHoverWindow(int k)
    {
        if (k < 0 || k >= minis.Count) return;
        hoverMini = k;
        for (int i = 0; i < 40; i++) { Glide(); DwmFlush(); }
    }

    /// <summary>Overview test: click desktop window k.</summary>
    internal void TestChooseWindow(int k)
    {
        if (k >= 0 && k < minis.Count) Choose(DesktopIndex, minis[k].Hwnd);
    }

    /// <summary>The desktop window under a point in the open overview's Desktop card (index into minis), or -1.</summary>
    int MiniAt(Point pt)
    {
        int d = DesktopIndex;
        if (state != State.Open || d < 0 || dragIndex >= 0) return -1;
        var card = cards[d].Rect;
        if (!card.Contains(pt)) return -1;
        for (int k = minis.Count - 1; k >= 0; k--)
            if (minis[k].Thumb != IntPtr.Zero && MiniRect(minis[k], card, 1).Contains(pt)) return k;
        return -1;
    }

    /// <summary>The live desktop preview is showing (so nothing we draw underneath the Desktop card can be seen).</summary>
    bool DesktopLiveCovers(double p) => deskThumb != IntPtr.Zero && p > 0.0005;

    /// <summary>Show a live preview at <paramref name="dest"/> (empty = hide), showing <paramref name="src"/> of its window (empty = all).</summary>
    static void Place(IntPtr thumb, Rectangle dest, Rectangle src, byte alpha)
    {
        var props = new DWM_THUMBNAIL_PROPERTIES();
        if (dest.Width <= 0 || dest.Height <= 0)
        {
            props.dwFlags = DWM_TNP_VISIBLE;
            props.fVisible = 0;
        }
        else
        {
            props.dwFlags = DWM_TNP_VISIBLE | DWM_TNP_RECTDESTINATION | DWM_TNP_OPACITY;
            props.fVisible = 1;
            props.opacity = alpha;
            props.rcDestination = new RECT { Left = dest.Left, Top = dest.Top, Right = dest.Right, Bottom = dest.Bottom };
            if (!src.IsEmpty)
            {
                props.dwFlags |= DWM_TNP_RECTSOURCE;
                props.rcSource = new RECT { Left = src.Left, Top = src.Top, Right = src.Right, Bottom = src.Bottom };
            }
        }
        DwmUpdateThumbnailProperties(thumb, ref props);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* drawn in Render */ }

    protected override void OnPaint(PaintEventArgs e) => Render(state == State.Open ? 1 : progress, state == State.Open);

    static Rectangle CoverSource(int iw, int ih, int rw, int rh)
    {
        double ia = (double)iw / ih, ra = (double)rw / rh;
        return ia > ra
            ? new Rectangle((int)((iw - ih * ra) / 2), 0, (int)(ih * ra), ih)
            : new Rectangle(0, (int)((ih - iw / ra) / 2), iw, (int)(iw / ra));
    }

    static void FillRounded(Graphics g, Brush b, Rectangle r, float radius)
    {
        using var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(b, path);
    }

    // ---------------- input (only when fully open) ----------------

    int HitTest(Point p)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var r = cards[i].Home;
            if (new Rectangle(r.Left, r.Top, r.Width, r.Height + (int)(40 * scale)).Contains(p)) return i;
        }
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (state != State.Open || e.Button != MouseButtons.Left) return;
        pressIndex = HitTest(e.Location);
        pressMini = MiniAt(e.Location);
        pressPos = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (state != State.Open) return;
        if (dragIndex >= 0) { UpdateDrag(e.Location); return; }
        if (pressIndex >= 1 && e.Button == MouseButtons.Left && cards[pressIndex].Item.Hwnd != IntPtr.Zero &&
            Math.Abs(e.X - pressPos.X) + Math.Abs(e.Y - pressPos.Y) > 10 * scale)
        {
            StartDrag(pressIndex);
            UpdateDrag(e.Location);
            return;
        }
        int h = HitTest(e.Location);
        if (h != hover) { hover = h; if (h >= 0) selected = h; Invalidate(); }
        int hm = MiniAt(e.Location);
        if (hm != hoverMini) { hoverMini = hm; glideTimer.Start(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverMini >= 0 && state == State.Open) { hoverMini = -1; glideTimer.Start(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (state != State.Open || e.Button != MouseButtons.Left) return;
        int pressed = pressIndex, pressedMini = pressMini;
        pressIndex = pressMini = -1;
        if (dragIndex >= 0) { EndDrag(); return; }
        int i = HitTest(e.Location);
        // A window inside the Desktop card: go to the desktop with that window in front.
        if (pressedMini >= 0 && pressedMini == MiniAt(e.Location)) Choose(DesktopIndex, minis[pressedMini].Hwnd);
        else if (i >= 0 && i == pressed) Choose(i);
        else if (i < 0 && pressed < 0) Dismiss();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (state != State.Open) return;
        int n = cards.Count;
        switch (e.KeyCode)
        {
            case Keys.Escape: Dismiss(); break;
            case Keys.Enter or Keys.Space: Choose(selected); break;
            case Keys.Left when e.Control: MoveSelected(-1); break;
            case Keys.Right when e.Control: MoveSelected(+1); break;
            case Keys.Left: selected = (selected - 1 + n) % n; Invalidate(); break;
            case Keys.Right or Keys.Tab: selected = (selected + 1) % n; Invalidate(); break;
            case Keys.Up: if (selected - cols >= 0) { selected -= cols; Invalidate(); } break;
            case Keys.Down: if (selected + cols < n) { selected += cols; Invalidate(); } break;
        }
        e.Handled = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Clicked another monitor, Alt+Tab, etc. (but not our own refocus right after moving an app).
        if (state == State.Open)
            BeginInvoke(() => { if (state == State.Open && GetForegroundWindow() != Handle) Dismiss(); });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        glideTimer.Stop();
        glideTimer.Dispose();
        foreach (var c in cards)
        {
            if (c.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(c.Thumb);
            c.Image?.Dispose();
            c.Snap?.Dispose();
        }
        if (deskThumb != IntPtr.Zero) DwmUnregisterThumbnail(deskThumb);
        foreach (var m in minis)
            if (m.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(m.Thumb);
        clean.Dispose();
        black.Dispose();
        back?.Dispose();
        blurTask.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); });
        base.OnFormClosed(e);
    }

    // ---------------- helpers ----------------

    static Bitmap MakeBackdrop(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        using var small = new Bitmap(Math.Max(1, w / 14), Math.Max(1, h / 14));
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, small.Width, small.Height);
        }
        var result = new Bitmap(w, h);
        using (var g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(small, 0, 0, w, h);
            using var dim = new SolidBrush(Color.FromArgb(150, 12, 8, 24));
            g.FillRectangle(dim, 0, 0, w, h);
        }
        return result;
    }

    static Bitmap? LoadWallpaper()
    {
        try
        {
            var sb = new StringBuilder(520);
            if (!SystemParametersInfoString(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0)) return null;
            var path = sb.ToString();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            using var img = Image.FromStream(fs);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    static string AppName(IntPtr hwnd, string title)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (appNames.TryGetValue(pid, out var cached)) return cached.Length > 0 ? cached : title;
        string name = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (!p.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                name = p.MainModule?.FileVersionInfo.FileDescription?.Trim() ?? "";
        }
        catch { /* elevated or protected process */ }
        appNames[pid] = name; // "" = use the window title
        return name.Length > 0 ? name : title;
    }
}
