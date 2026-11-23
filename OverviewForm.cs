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
        public Rectangle Rect;      // position when fully open (client coordinates)
        public Shot? Image;         // desktop card
        public Shot? Snap;          // exact screenshot used while this card is the one zooming (no live-preview blip)
    }

    enum State { Opening, Open, Closing }

    static readonly Color Accent = Color.FromArgb(0x8B, 0x5C, 0xF6);

    readonly MonitorDesc mon;
    readonly List<Card> cards = new();
    readonly Shot clean;            // the monitor as it looked when the overview started
    readonly Shot black = Shot.Blank(1, 1);
    readonly Task<Shot> blurTask;   // blurred + dimmed backdrop, made in the background
    readonly IntPtr priorForeground;
    readonly int currentIndex;
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
            var card = new Card { Item = s };
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
        currentIndex = Math.Max(0, cards.FindIndex(c => c.Item.Current));
        selected = focusIndex = currentIndex;

        // The current app shrinks from (and grows back to) exactly what is on screen: use the screenshot.
        if (cards.Count > 0 && cards[currentIndex].Item.Hwnd != IntPtr.Zero)
            cards[currentIndex].Snap = Crop(clean, FullRectFor(cards[currentIndex].Item.Kind));
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
        focusIndex = currentIndex;
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
        focusIndex = currentIndex;
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

        AnimateTo(0);
        Close();
    }

    /// <summary>Close without switching: the current space grows back to full screen.</summary>
    public void Dismiss()
    {
        if (state == State.Closing) return;
        bool wasOpen = state == State.Open;
        state = State.Closing;
        focusIndex = currentIndex;
        if (wasOpen && priorForeground != IntPtr.Zero) SpaceSwitcher.Focus(priorForeground);
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

    void LayoutCards()
    {
        int n = cards.Count;
        if (n == 0) return;
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
                cards[i].Rect = new Rectangle((int)(left + j * (bestW + gap)), (int)(top + r * (ch + labelH + gap)), (int)bestW, (int)ch);
        }
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
            if (i == focusIndex && c.Snap != null)
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

        using var hint = new Font("Segoe UI", 9.5f * scale, GraphicsUnit.Point);
        var hr = new Rectangle(0, ClientSize.Height - (int)(56 * scale), ClientSize.Width, (int)(30 * scale));
        TextRenderer.DrawText(g, "Click an app  ·  ← → and Enter  ·  Esc or swipe down to close", hint, hr,
            Color.FromArgb(190, 255, 255, 255), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
            if (a == 0 || r.Width <= 0 || r.Height <= 0 || (i == focusIndex && c.Snap != null))
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
            var r = cards[i].Rect;
            if (new Rectangle(r.Left, r.Top, r.Width, r.Height + (int)(40 * scale)).Contains(p)) return i;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (state != State.Open) return;
        int h = HitTest(e.Location);
        if (h != hover) { hover = h; if (h >= 0) selected = h; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (state != State.Open || e.Button != MouseButtons.Left) return;
        int i = HitTest(e.Location);
        if (i >= 0) Choose(i); else Dismiss();
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
        if (state == State.Open) BeginInvoke(Dismiss); // clicked another monitor, Alt+Tab, etc.
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
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
