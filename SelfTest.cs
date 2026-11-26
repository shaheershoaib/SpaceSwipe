using System.Drawing.Imaging;
using static SpaceSwipe.Native;

namespace SpaceSwipe;

/// <summary>
/// `SpaceSwipe.exe --selftest [folder]`: on each monitor, capture the screen, show that capture in the
/// overlay (visible to capture this time), capture again, and compare. If the two differ, Windows is
/// changing the overlay's pixels on the way to the screen; if they match, the capture itself is faithful.
/// </summary>
internal static class SelfTest
{
    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        using var overlay = new SlideOverlay(excludeFromCapture: false);
        int i = 0;
        foreach (var screen in Screen.AllScreens)
        {
            i++;
            var hmon = MonitorFromPoint(new POINT { X = screen.Bounds.Left + 10, Y = screen.Bounds.Top + 10 }, MONITOR_DEFAULTTONEAREST);
            var mon = WindowTracker.GetMonitor(hmon);
            var r = mon.Work;

            overlay.Begin(r);                 // captures "shown" and puts it on screen
            DwmFlush(); DwmFlush(); Thread.Sleep(50); DwmFlush();
            using var seen = Shot.Capture(r); // what the screen shows while the overlay is up
            using var shownBmp = overlay.Before!.ToBitmap();
            overlay.Hide();

            using var seenBmp = seen.ToBitmap();
            var (meanDiff, maxDiff, changed) = Compare(shownBmp, seenBmp);
            var (lumA, sdA) = Stats(shownBmp);
            var (lumB, sdB) = Stats(seenBmp);
            string line = $"{mon.Device} {r}: mean|diff|={meanDiff:F2} max={maxDiff} changedPixels={changed:P2} " +
                          $"luma {lumA:F1}->{lumB:F1} contrast(sd) {sdA:F1}->{sdB:F1}";
            report.Add(line);
            Log.Write("selftest " + line);

            shownBmp.Save(Path.Combine(outDir, $"m{i}-captured.png"), ImageFormat.Png);
            seenBmp.Save(Path.Combine(outDir, $"m{i}-while-overlay-shown.png"), ImageFormat.Png);
        }
        File.WriteAllLines(Path.Combine(outDir, "report.txt"), report);
    }

    /// <summary>
    /// `--overview-test [folder] [\\.\DISPLAYn]`: open the desktop overview (spread-out windows) on a monitor,
    /// visible to capture this time, and photograph it at progress 0, 0.5 and fully open. Moves no windows.
    /// </summary>
    public static void Overview(string outDir, string? device)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        var mon = WindowTracker.MonitorAtCursor();
        if (device != null)
            foreach (var s in Screen.AllScreens)
                if (s.DeviceName.Equals(device, StringComparison.OrdinalIgnoreCase))
                    mon = WindowTracker.GetMonitor(MonitorFromPoint(new POINT { X = s.Bounds.Left + 10, Y = s.Bounds.Top + 10 }, MONITOR_DEFAULTTONEAREST));

        var tracker = new WindowTracker();
        var snap = tracker.Snapshot();
        var wins = tracker.DesktopWindows(mon.Device)
            .Where(h => snap.All(w => w.Hwnd != h)) // leave out maximized / full-screen apps (they're spaces)
            .ToList();
        // The current space: the frontmost full-screen app actually on this monitor, else the desktop.
        var mb = new Rectangle(mon.Bounds.Left, mon.Bounds.Top, mon.Bounds.Width, mon.Bounds.Height);
        var onMon = snap.Where(w => w.Monitor == mon.Device).ToList();
        var cur = onMon.Where(w => { var f = VisibleFrame(w.Hwnd); return mb.IntersectsWith(Rectangle.FromLTRB(f.Left, f.Top, f.Right, f.Bottom)); })
                       .OrderBy(w => w.Z).FirstOrDefault();
        if (Environment.GetEnvironmentVariable("SPACESWIPE_TEST_AS_DESKTOP") == "1") cur = null; // time the desktop's own zoom
        var spaces = new List<SpaceSwitcher.SpaceItem> { new(IntPtr.Zero, "Desktop", WinKind.None, cur == null) };
        spaces.AddRange(onMon.Select(w => new SpaceSwitcher.SpaceItem(w.Hwnd, w.Title, w.Kind, w == cur)));
        report.Add($"{mon.Device} {mon.Bounds}: {wins.Count} desktop window(s), {spaces.Count - 1} app space(s)");
        foreach (var h in wins) report.Add($"  window: {TitleOf(h)}  frame {VisibleFrame(h)}");

        using var before = Shot.Capture(mon.Bounds);
        using (var b = before.ToBitmap()) b.Save(Path.Combine(outDir, "0-real-screen.png"), ImageFormat.Png);

        var form = new OverviewForm(mon, spaces, null, wins) { ExcludeFromCapture = false };
        void Save(string name)
        {
            DwmFlush(); DwmFlush(); Thread.Sleep(40); DwmFlush();
            using var s = Shot.Capture(mon.Bounds);
            using var bmp = s.ToBitmap();
            bmp.Save(Path.Combine(outDir, name), ImageFormat.Png);
            if (name.StartsWith("1-"))
            {
                using var bb = before.ToBitmap();
                report.Add($"progress 0 vs real screen: changed pixels {ChangedFraction(bb, bmp):P2}");
            }
        }
        form.BeginOpen();
        Save("1-progress-0.png");
        form.DragOpen(0.5);
        Save("2-progress-50.png");
        form.EndOpen(true);
        Save("3-open.png");
        if (wins.Count > 1 && Environment.GetEnvironmentVariable("SPACESWIPE_TEST_PICK_WINDOW") == "1")
        {
            // Hover and click a desktop window (the switch itself is only logged: this test moves no windows).
            form.WindowChosen += h => report.Add($"window chosen: {TitleOf(h)}");
            form.TestHoverWindow(1);
            Save("4-hover.png");
            form.TestChooseWindow(1);
        }
        else form.Dismiss();
        if (!form.IsDisposed) form.Dispose();
        File.WriteAllLines(Path.Combine(outDir, "overview.txt"), report);
    }

    /// <summary>
    /// `--drag-test [folder]`: drag a throwaway window by its title bar with injected mouse input (like three-finger
    /// drag) at the touchpad's ~90 updates/s, and record how far the window trails the pointer after each step.
    /// </summary>
    public static void Drag(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        var mon = WindowTracker.GetMonitor(MonitorFromPoint(new POINT { X = 10, Y = 10 }, MONITOR_DEFAULTTONEAREST));
        var form = new Form
        {
            Text = "SpaceSwipe drag test", StartPosition = FormStartPosition.Manual, TopMost = true,
            Bounds = new Rectangle(mon.Work.Left + 200, mon.Work.Top + 200, 600, 400), BackColor = Color.DimGray,
        };
        GetCursorPos(out var original);
        form.Shown += (_, _) => new Thread(() =>
        {
            try
            {
                Thread.Sleep(300);
                IntPtr h = IntPtr.Zero;
                form.Invoke(() => { h = form.Handle; SpaceSwitcher.Focus(h); });
                Thread.Sleep(150);
                GetWindowRect(h, out var r0);
                int gx = r0.Left + 150, gy = r0.Top + 15; // on the title bar
                Send(MouseTo(gx, gy));
                Thread.Sleep(50);
                Send(Mouse(MOUSEEVENTF_LEFTDOWN));
                Thread.Sleep(80);
                foreach (var (name, absolute) in new[] { ("relative", false), ("absolute", true) })
                {
                    GetCursorPos(out var c0);
                    GetWindowRect(h, out var w0);
                    var lags = new List<int>();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 1; i <= 60; i++)
                    {
                        int dir = i <= 30 ? 1 : -1;
                        if (absolute) { GetCursorPos(out var c); Send(MouseTo(c.X + 12 * dir, c.Y)); }
                        else Send(Mouse(MOUSEEVENTF_MOVE, 12 * dir, 0));
                        Thread.Sleep(11);
                        GetCursorPos(out var cp);
                        GetWindowRect(h, out var wr);
                        lags.Add((cp.X - c0.X) - (wr.Left - w0.Left)); // pointer travel minus window travel
                    }
                    Thread.Sleep(150);
                    GetCursorPos(out var c1);
                    GetWindowRect(h, out var w1);
                    report.Add($"{name}: 60 steps of 12 px in {sw.ElapsedMilliseconds} ms; window behind pointer after each step: " +
                               $"avg {lags.Average(x => Math.Abs(x)):F1} px, max {lags.Max(x => Math.Abs(x))} px; settled offset {(c1.X - c0.X) - (w1.Left - w0.Left)} px");
                    report.Add("  per step: " + string.Join(" ", lags));
                }
                Send(Mouse(MOUSEEVENTF_LEFTUP));
            }
            catch (Exception ex) { report.Add("failed: " + ex); try { Send(Mouse(MOUSEEVENTF_LEFTUP)); } catch { } }
            finally
            {
                Send(MouseTo(original.X, original.Y));
                form.BeginInvoke(form.Close);
            }
        }) { IsBackground = true }.Start();
        Application.Run(form);
        File.WriteAllLines(Path.Combine(outDir, "drag.txt"), report);
    }

    /// <summary>
    /// `--move-test "title" [folder]`: move a real app's window ~6 px per step at ~90 steps/s, two ways: through
    /// Windows' own move loop (title bar held with injected input) and by direct SetWindowPos from a helper thread.
    /// Counts how many distinct positions the app actually reaches. Puts the window and pointer back afterwards.
    /// </summary>
    public static void Move(string titlePart, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        IntPtr h = IntPtr.Zero;
        EnumWindows((w, _) =>
        {
            if (IsWindowVisible(w) && !IsZoomed(w) && !IsIconic(w) && TitleOf(w).Contains(titlePart, StringComparison.OrdinalIgnoreCase)) { h = w; return false; }
            return true;
        }, IntPtr.Zero);
        if (h == IntPtr.Zero) { File.WriteAllText(Path.Combine(outDir, "move.txt"), "window not found (or maximized)"); return; }
        GetCursorPos(out var original);
        GetWindowRect(h, out var start);
        report.Add($"window: {TitleOf(h)} at {start}");

        // A spot on its title bar.
        POINT? grip = null;
        for (int x = start.Right - 200; x > start.Left + 100 && grip == null; x -= 20)
            for (int y = start.Top + 8; y < start.Top + 40 && grip == null; y += 6)
            {
                var lp = (IntPtr)((y & 0xFFFF) << 16 | (x & 0xFFFF));
                if (SendMessageTimeout(h, WM_NCHITTEST, IntPtr.Zero, lp, SMTO_ABORTIFHUNG, 100, out var hit) != IntPtr.Zero && (int)hit == HTCAPTION)
                    grip = new POINT { X = x, Y = y };
            }
        if (grip is not { } g) { File.WriteAllText(Path.Combine(outDir, "move.txt"), "no title bar spot found"); return; }

        SpaceSwitcher.Focus(h);
        Thread.Sleep(200);

        (int distinct, double avgLag, int maxLag) Run(Action<int> step)
        {
            GetWindowRect(h, out var w0);
            var seen = new HashSet<int>();
            var lags = new List<int>();
            for (int i = 1; i <= 60; i++)
            {
                int offset = i <= 30 ? i * 6 : (60 - i) * 6; // out 180 px and back
                step(offset);
                Thread.Sleep(11);
                GetWindowRect(h, out var wr);
                seen.Add(wr.Left);
                lags.Add(Math.Abs(w0.Left + offset - wr.Left));
            }
            Thread.Sleep(300);
            return (seen.Count, lags.Average(), lags.Max());
        }

        // 1) Windows' own move loop, driven by injected input (button held on the title bar).
        Send(MouseTo(g.X, g.Y));
        Thread.Sleep(60);
        Send(Mouse(MOUSEEVENTF_LEFTDOWN));
        Thread.Sleep(60);
        Send(Mouse(MOUSEEVENTF_MOVE, 6, 0)); Send(Mouse(MOUSEEVENTF_MOVE, -6, 0)); // get past the drag threshold
        Thread.Sleep(60);
        var a = Run(off => Send(MouseTo(g.X + off, g.Y)));
        Send(Mouse(MOUSEEVENTF_LEFTUP));
        report.Add($"move loop (button held): {a.distinct} distinct positions of 31, lag avg {a.avgLag:F0} px, max {a.maxLag} px");
        Thread.Sleep(300);
        SetWindowPos(h, IntPtr.Zero, start.Left, start.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Thread.Sleep(300);

        // 2) Direct SetWindowPos from a helper thread, newest position wins.
        int target = start.Left; bool pending = false; var wake = new AutoResetEvent(false); bool stop = false;
        var mover = new Thread(() =>
        {
            while (!stop)
            {
                wake.WaitOne(50);
                while (true)
                {
                    int x;
                    lock (wake) { if (!pending) break; pending = false; x = target; }
                    SetWindowPos(h, IntPtr.Zero, x, start.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                }
            }
        }) { IsBackground = true };
        mover.Start();
        var b = Run(off => { lock (wake) { target = start.Left + off; pending = true; } wake.Set(); });
        stop = true;
        report.Add($"direct moves (helper thread): {b.distinct} distinct positions of 31, lag avg {b.avgLag:F0} px, max {b.maxLag} px");

        Thread.Sleep(200);
        SetWindowPos(h, IntPtr.Zero, start.Left, start.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        Send(MouseTo(original.X, original.Y));
        File.WriteAllLines(Path.Combine(outDir, "move.txt"), report);
    }

    /// <summary>
    /// `--activation-test`: focus each maximized app in turn and capture the screen over time,
    /// to see how long it keeps changing after it gets focus (title bar, Mica backdrop, repaint).
    /// </summary>
    public static void Activation(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        var tracker = new WindowTracker();
        var snap = tracker.Snapshot();
        int[] times = [16, 33, 50, 80, 120, 160, 200, 300, 400, 600, 900];
        int n = 0;
        foreach (var group in snap.Where(w => w.Kind == WinKind.Maximized).GroupBy(w => w.Monitor))
        {
            var mon = WindowTracker.GetMonitor(MonitorFromWindow(group.First().Hwnd, MONITOR_DEFAULTTONEAREST));
            var r = mon.Work;
            foreach (var w in group.Take(3))
            {
                n++;
                var prior = GetForegroundWindow();
                bool wasMin = IsIconic(w.Hwnd);
                if (wasMin) ShowWindow(w.Hwnd, SW_RESTORE);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                SpaceSwitcher.Focus(w.Hwnd);
                var shots = new List<(long t, Bitmap b)>();
                foreach (var t in times)
                {
                    while (sw.ElapsedMilliseconds < t) Thread.Sleep(1);
                    using var s = Shot.Capture(r);
                    shots.Add((sw.ElapsedMilliseconds, s.ToBitmap()));
                }
                var final = shots[^1].b;
                var parts = shots.Select(s => $"{s.t}ms:{Compare(s.b, final).changed:P1}");
                string line = $"{mon.Device} '{w.Title}' (was {(wasMin ? "minimized" : "behind")}): changed vs final -> {string.Join("  ", parts)}";
                report.Add(line);
                shots[0].b.Save(Path.Combine(outDir, $"a{n}-first.png"), ImageFormat.Png);
                shots[2].b.Save(Path.Combine(outDir, $"a{n}-50ms.png"), ImageFormat.Png);
                final.Save(Path.Combine(outDir, $"a{n}-final.png"), ImageFormat.Png);
                foreach (var s in shots) s.b.Dispose();

                if (wasMin) ShowWindow(w.Hwnd, SW_SHOWMINNOACTIVE);
                SpaceSwitcher.Focus(prior);
                Thread.Sleep(300);
            }
        }
        File.WriteAllLines(Path.Combine(outDir, "activation.txt"), report);
    }

    /// <summary>
    /// `--redraw-test "title"`: cover the named app with another maximized app on its monitor, bring it back,
    /// and capture at fine intervals to see how long it keeps changing after it comes forward.
    /// </summary>
    public static void Redraw(string titlePart, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        var snap = new WindowTracker().Snapshot();
        var target = snap.FirstOrDefault(w => w.Title.Contains(titlePart, StringComparison.OrdinalIgnoreCase));
        var cover = target == null ? null : snap.FirstOrDefault(w => w.Monitor == target.Monitor && w.Hwnd != target.Hwnd && w.Kind == WinKind.Maximized);
        if (target == null || cover == null)
        {
            File.WriteAllText(Path.Combine(outDir, "redraw.txt"), $"need '{titlePart}' and another maximized app on the same monitor");
            return;
        }
        var prior = GetForegroundWindow();
        var mon = WindowTracker.GetMonitor(MonitorFromWindow(target.Hwnd, MONITOR_DEFAULTTONEAREST));

        SpaceSwitcher.Focus(cover.Hwnd);
        Thread.Sleep(1500); // let it settle while covered (Chromium stops drawing occluded windows)

        int[] times = [0, 8, 16, 25, 33, 50, 66, 90, 120, 160, 250, 400, 700];
        var shots = new List<(long t, Bitmap b)>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SpaceSwitcher.Focus(target.Hwnd);
        foreach (var t in times)
        {
            while (sw.ElapsedMilliseconds < t) Thread.Sleep(1);
            using var s = Shot.Capture(mon.Work);
            shots.Add((sw.ElapsedMilliseconds, s.ToBitmap()));
        }
        var final = shots[^1].b;
        report.Add($"'{target.Title}' on {mon.Device}, covered by '{cover.Title}':");
        report.Add("changed vs final -> " + string.Join("  ", shots.Select(s => $"{s.t}ms:{Compare(s.b, final).changed:P1}")));
        for (int i = 0; i < shots.Count; i++)
            if (i is 0 or 2 or 4 or 6) shots[i].b.Save(Path.Combine(outDir, $"r{shots[i].t}ms.png"), ImageFormat.Png);
        final.Save(Path.Combine(outDir, "r-final.png"), ImageFormat.Png);
        foreach (var s in shots) s.b.Dispose();
        SpaceSwitcher.Focus(prior);
        File.WriteAllLines(Path.Combine(outDir, "redraw.txt"), report);
    }

    /// <summary>
    /// `--park-test`: move a maximized app just below the screen (instead of minimizing it), bring it back,
    /// and measure how quickly it is fully drawn again.
    /// </summary>
    public static void Park(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        var tracker = new WindowTracker();
        var w = tracker.Snapshot().FirstOrDefault(x => x.Kind == WinKind.Maximized);
        if (w == null) { File.WriteAllText(Path.Combine(outDir, "park.txt"), "no on-screen maximized window"); return; }

        var prior = GetForegroundWindow();
        SpaceSwitcher.Focus(w.Hwnd);
        Thread.Sleep(500);
        GetWindowRect(w.Hwnd, out var orig);
        if (IsIconic(w.Hwnd) || orig.Left <= -10000 || orig.Width < 300)
        {
            File.WriteAllText(Path.Combine(outDir, "park.txt"), $"'{w.Title}' not in a usable state ({orig}); nothing moved");
            return;
        }
        var mon = WindowTracker.GetMonitor(MonitorFromWindow(w.Hwnd, MONITOR_DEFAULTTONEAREST));
        int parkY = SystemInformation.VirtualScreen.Bottom + 200;

        SpaceSwitcher.Focus(prior);
        SetWindowPos(w.Hwnd, IntPtr.Zero, orig.Left, parkY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        GetWindowRect(w.Hwnd, out var parked);
        report.Add($"'{w.Title}': orig={orig} parked={parked} zoomedWhileParked={IsZoomed(w.Hwnd)} " +
                   $"monitorWhileParked={WindowTracker.GetMonitor(MonitorFromWindow(w.Hwnd, MONITOR_DEFAULTTONEAREST)).Device}");
        Thread.Sleep(1500);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        SetWindowPos(w.Hwnd, IntPtr.Zero, orig.Left, orig.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        SpaceSwitcher.Focus(w.Hwnd);
        var shots = new List<(long t, Bitmap b)>();
        foreach (var t in new[] { 16, 33, 50, 80, 120, 200, 300, 500, 800 })
        {
            while (sw.ElapsedMilliseconds < t) Thread.Sleep(1);
            using var s = Shot.Capture(mon.Work);
            shots.Add((sw.ElapsedMilliseconds, s.ToBitmap()));
        }
        var final = shots[^1].b;
        report.Add("unparked: changed vs final -> " + string.Join("  ", shots.Select(s => $"{s.t}ms:{Compare(s.b, final).changed:P1}")));
        report.Add($"after unpark: zoomed={IsZoomed(w.Hwnd)} rect={(GetWindowRect(w.Hwnd, out var back) ? back.ToString() : "?")}");
        shots[0].b.Save(Path.Combine(outDir, "p-first.png"), ImageFormat.Png);
        final.Save(Path.Combine(outDir, "p-final.png"), ImageFormat.Png);
        foreach (var s in shots) s.b.Dispose();
        SpaceSwitcher.Focus(prior);
        File.WriteAllLines(Path.Combine(outDir, "park.txt"), report);
    }

    /// <summary>Top-down BGRA bytes, width*4 per row (handles bottom-up bitmaps).</summary>
    static byte[] Pixels(Bitmap bmp)
    {
        var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int row = bmp.Width * 4;
            var p = new byte[row * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0 + y * d.Stride, p, y * row, row);
            return p;
        }
        finally
        {
            bmp.UnlockBits(d);
        }
    }

    public static double ChangedFraction(Bitmap a, Bitmap b) => Compare(a, b).changed;

    static (double mean, int max, double changed) Compare(Bitmap a, Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return (-1, -1, -1);
        var pa = Pixels(a);
        var pb = Pixels(b);
        long sum = 0, n = 0, changed = 0;
        int max = 0;
        for (int o = 0; o < pa.Length; o += 8) // every other pixel
        {
            int d = Math.Abs(pa[o] - pb[o]) + Math.Abs(pa[o + 1] - pb[o + 1]) + Math.Abs(pa[o + 2] - pb[o + 2]);
            sum += d; n++;
            if (d > max) max = d;
            if (d > 12) changed++;
        }
        return (n == 0 ? 0 : sum / (3.0 * n), max, n == 0 ? 0 : (double)changed / n);
    }

    static (double mean, double sd) Stats(Bitmap bmp)
    {
        var p = Pixels(bmp);
        double sum = 0, sq = 0; long n = 0;
        for (int o = 0; o < p.Length; o += 8)
        {
            double l = 0.0722 * p[o] + 0.7152 * p[o + 1] + 0.2126 * p[o + 2];
            sum += l; sq += l * l; n++;
        }
        double mean = sum / n;
        return (mean, Math.Sqrt(Math.Max(0, sq / n - mean * mean)));
    }
}
