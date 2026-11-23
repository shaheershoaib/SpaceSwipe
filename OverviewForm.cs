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
    Shot? back;
    static readonly Dictionary<uint, string> appNames = new();
    float scale = 1f;
    int cols = 1, hover = -1, selected, focusIndex;
    double progress;
    State state = State.Opening;

    /// <summary>Raised when a space is picked, before the zoom-in (zero = Desktop).</summary>
    public event Action<IntPtr>? Chosen;

    public bool IsOpen => state == State.Open;

    public OverviewForm(MonitorDesc mon, List<SpaceSwitcher.SpaceItem> spaces, Func<Shot?>? captureDesktop = null)
    {
        this.mon = mon;
        this.captureDesktop = captureDesktop;
        priorForeground = GetForegroundWindow();

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

        // The current app shrinks from (and grows back to) exactly what is on screen: use the screenshot.
        if (cards.Count > 0 && cards[ci].Item.Hwnd != IntPtr.Zero)
            cards[ci].Snap = Crop(clean, FullRectFor(cards[ci].Item.Kind));
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
        SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE); // so we can photograph what's underneath
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
    public void Choose(int i)
    {
        if (state != State.Open || i < 0 || i >= cards.Count) return;
        state = State.Closing;
        focusIndex = i;
        Chosen?.Invoke(cards[i].Item.Hwnd); // the real switch happens underneath this window

        // Zoom an exact screenshot of the result, so the hand-off to the real screen is seamless.
        DwmFlush();
        Thread.Sleep(25);
        DwmFlush();
        var full = FullRect(i);
        var c = cards[i];
        c.Snap?.Dispose();
        c.Snap = Shot.Capture(new RECT
        {
            Left = full.Left + mon.Bounds.Left, Top = full.Top + mon.Bounds.Top,
            Right = full.Right + mon.Bounds.Left, Bottom = full.Bottom + mon.Bounds.Top,
        });
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
        if (wasOpen && priorForeground != IntPtr.Zero && IsWindow(priorForeground)) SpaceSwitcher.Focus(priorForeground);
        AnimateTo(0);
        Close();
    }

    void AnimateTo(double target)
    {
        double from = progress;
        double ms = Math.Clamp(Math.Abs(target - from) * 380, 110, 320);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            double t = Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
            progress = from + (target - from) * EaseOut(t);
            Render(progress, false);
            if (DwmFlush() != 0) Thread.Sleep(6);
            if (t >= 1) break;
        }
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
        PatBlt(back.Dc, 0, 0, w, h, BLACKNESS);

        // Background: the real screen, crossfading into the blurred + dimmed version.
        int cw = Math.Min(w, clean.W), ch = Math.Min(h, clean.H);
        BitBlt(back.Dc, 0, 0, cw, ch, clean.Dc, 0, 0, SRCCOPY);
        byte bg = (byte)(255 * Math.Clamp(p, 0, 1));
        if (blurTask.IsCompletedSuccessfully)
        {
            var blur = blurTask.Result;
            AlphaBlend(back.Dc, 0, 0, Math.Min(cw, blur.W), Math.Min(ch, blur.H), blur.Dc, 0, 0, Math.Min(cw, blur.W), Math.Min(ch, blur.H), ConstantAlpha(bg));
        }
        else
            AlphaBlend(back.Dc, 0, 0, w, h, black.Dc, 0, 0, 1, 1, ConstantAlpha((byte)(bg * 0.55)));

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
            if (c.Image != null)
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
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (state != State.Open || e.Button != MouseButtons.Left) return;
        int pressed = pressIndex;
        pressIndex = -1;
        if (dragIndex >= 0) { EndDrag(); return; }
        int i = HitTest(e.Location);
        if (i >= 0 && i == pressed) Choose(i);
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
