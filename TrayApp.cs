using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Win32;
using static SpaceSwipe.Native;

namespace SpaceSwipe;

internal static class Program
{
    const string MutexName = @"Local\SpaceSwipe";
    public const string ExitEventName = @"Local\SpaceSwipe.Exit";
    public const string ShowSettingsEventName = @"Local\SpaceSwipe.ShowSettings";

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.Run(args.Length > 1 ? args[1] : Path.Combine(Log.Dir, "selftest"));
            return;
        }
        if (args.Length > 1 && args[0].Equals("--redraw-test", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.Redraw(args[1], args.Length > 2 ? args[2] : Path.Combine(Log.Dir, "selftest"));
            return;
        }
        if (args.Length > 0 && args[0].Equals("--overview-test", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.Overview(args.Length > 1 ? args[1] : Path.Combine(Log.Dir, "selftest"), args.Length > 2 ? args[2] : null);
            return;
        }
        if (args.Length > 0 && args[0].Equals("--park-test", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.Park(args.Length > 1 ? args[1] : Path.Combine(Log.Dir, "selftest"));
            return;
        }
        if (args.Length > 0 && args[0].Equals("--activation-test", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.Activation(args.Length > 1 ? args[1] : Path.Combine(Log.Dir, "selftest"));
            return;
        }

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            if (MessageBox.Show("Remove SpaceSwipe from this PC?", "SpaceSwipe",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            StopRunningInstance();
            Installer.Uninstall();
            MessageBox.Show("SpaceSwipe was removed.\n\nTo get Windows' own three-finger app switching back, open " +
                            "Settings → Bluetooth & devices → Touchpad → Three-finger gestures and set Swipes to \"Switch apps\".",
                            "SpaceSwipe", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var settings = Settings.Load();

        if (!Installer.IsRunningInstalled && settings.SetupDone && File.Exists(Installer.InstalledExe))
        {
            // A newer download while SpaceSwipe is already set up: upgrade in place, no questions.
            StopRunningInstance();
            try { Installer.Install(Startup.IsEnabled); }
            catch (Exception ex)
            {
                MessageBox.Show("SpaceSwipe couldn't be updated:\n\n" + ex.Message, "SpaceSwipe",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true });
            return;
        }

        if (!Installer.IsRunningInstalled)
        {
            // Launched from a download: show setup, install a copy, and hand over to it.
            using var setup = new SetupForm(installed: false);
            if (setup.ShowDialog() != DialogResult.OK) return;
            StopRunningInstance(); // an older version may be running from the install folder
            try
            {
                Installer.Install(setup.StartWithWindows);
            }
            catch (Exception ex)
            {
                MessageBox.Show("SpaceSwipe couldn't be installed:\n\n" + ex.Message, "SpaceSwipe",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            settings.SetupDone = true;
            settings.Save();
            Process.Start(new ProcessStartInfo(Installer.InstalledExe) { UseShellExecute = true });
            return;
        }

        using var mutex = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            // Already running (e.g. opened again from the Start menu): show its Settings window.
            try
            {
                using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
                show.Set();
            }
            catch (Exception ex) { Log.Write("couldn't reach the running SpaceSwipe: " + ex.Message); }
            return;
        }

        if (!settings.SetupDone)
        {
            using var setup = new SetupForm(installed: true);
            setup.ShowDialog();
            Startup.Set(setup.StartWithWindows);
            settings.SetupDone = true;
            settings.Save();
        }

        Application.Run(new TrayContext(settings));
    }

    /// <summary>Ask a running SpaceSwipe to exit (used for upgrades and uninstall) and wait briefly.</summary>
    static void StopRunningInstance()
    {
        try
        {
            using var exit = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            exit.Set();
            for (int i = 0; i < 30; i++)
            {
                if (!Mutex.TryOpenExisting(MutexName, out var m)) return;
                m.Dispose();
                Thread.Sleep(100);
            }
        }
        catch (Exception ex) { Log.Write("stop running instance: " + ex.Message); }
    }
}

internal sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public bool Animate { get; set; } = true;
    public int AnimationMs { get; set; } = 300;
    public double SwipeThreshold { get; set; } = 0.10;
    /// <summary>Finger travel (fraction of touchpad width) that moves the screen by one full width.</summary>
    public double FullSwipeFraction { get; set; } = 0.50;
    /// <summary>Release past this fraction of the screen width and the switch completes.</summary>
    public double CommitFraction { get; set; } = 0.30;
    /// <summary>A flick faster than this (screen widths per second) completes the switch.</summary>
    public double FlickSpeed { get; set; } = 1.0;
    public double DragDeadZone { get; set; } = 0.02;
    public bool SwipeUpTaskView { get; set; } = true;
    /// <summary>Three-finger swipe up opens SpaceSwipe's per-monitor overview (otherwise Windows Task View).</summary>
    public bool SwipeUpOverview { get; set; } = true;
    public bool SwipeDownShowsDesktop { get; set; } = true;
    public bool LogTouchFrames { get; set; }
    public bool SetupDone { get; set; }

    /// <summary>Three-finger drag left/right switches spaces.</summary>
    public bool SwipeSideways { get; set; } = true;
    /// <summary>Content moves opposite to the fingers (off = natural, like macOS).</summary>
    public bool ReverseDirection { get; set; }
    /// <summary>Borderless full-screen windows (games, F11 browsers) count as spaces.</summary>
    public bool IncludeBorderlessFullScreen { get; set; } = true;
    /// <summary>App executables (e.g. "steam.exe") that are never spaces.</summary>
    public List<string> ExcludedApps { get; set; } = new();
    /// <summary>Stable IDs of monitors SpaceSwipe ignores (see DisplayInfo).</summary>
    public List<string> DisabledMonitors { get; set; } = new();
    /// <summary>Ctrl+Alt+Win + arrow shortcuts.</summary>
    public bool KeyboardShortcuts { get; set; } = true;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceSwipe", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
    }
}

internal static class Log
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceSwipe");
    public static string FilePath => Path.Combine(Dir, "log.txt");
    static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}

internal static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "SpaceSwipe";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) != null;
        }
    }

    public static void Set(bool on, string? exe = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{exe ?? Environment.ProcessPath}\"");
        else k.DeleteValue(Name, false);
    }
}

internal sealed class MessageWindow : NativeWindow
{
    public Action<Message>? Handler;

    public MessageWindow() => CreateHandle(new CreateParams { Caption = "SpaceSwipe Messages" });

    protected override void WndProc(ref Message m)
    {
        Handler?.Invoke(m);
        base.WndProc(ref m);
    }
}

internal sealed class TrayContext : ApplicationContext
{
    const int HK_PREV = 1, HK_NEXT = 2, HK_DESKTOP = 3, HK_OVERVIEW = 4;
    const int WM_EXIT_REQUEST = WM_APP + 2, WM_SHOW_SETTINGS = WM_APP + 3;

    readonly Settings settings;
    readonly WindowTracker tracker = new();
    readonly SlideOverlay overlay = new();
    readonly TouchpadGestures gestures = new();
    readonly MessageWindow msg = new();
    readonly SpaceSwitcher switcher;
    readonly Parking parking = new();
    readonly NotifyIcon tray;
    OverviewForm? overview;
    readonly System.Windows.Forms.Timer touchTimer = new() { Interval = 100 };

    public TrayContext(Settings settings)
    {
        this.settings = settings;
        // Never leave windows parked below the screen, even if something goes wrong.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write("unhandled: " + e.ExceptionObject);
            try { parking.UnparkAll(); } catch { }
        };
        Application.ThreadException += (_, e) =>
        {
            Log.Write("ui exception: " + e.Exception);
            try { parking.UnparkAll(); } catch { }
        };
        tracker.OwnWindows.Add(overlay.Handle);
        tracker.OwnWindows.Add(msg.Handle);
        tracker.Parking = parking;
        switcher = new SpaceSwitcher(tracker, overlay, settings, parking) { Suspended = () => overview != null };

        gestures.DeadZone = settings.DragDeadZone;
        gestures.LogFrames = settings.LogTouchFrames;
        gestures.VDragBegan += OnVDragBegan;
        gestures.VDragMoved += OnVDragMoved;
        gestures.VDragEnded += OnVDragEnded;
        // Drags are handled inline (on the UI thread, inside WM_INPUT) so the frame tracks the fingers.
        gestures.DragBegan += switcher.DragBegin;
        gestures.DragMoved += switcher.DragMove;
        gestures.DragEnded += switcher.DragEnd;
        msg.Handler = OnMessage;
        touchTimer.Tick += (_, _) => gestures.CheckTimeout();
        touchTimer.Start();

        bool raw = gestures.Register(msg.Handle);
        Log.Write($"SpaceSwipe {Application.ProductVersion} started; touchpad raw input registered={raw}");

        RegisterHotkeys();

        tracker.Settings = settings;
        tracker.Snapshot(); // seed the per-monitor order

        // A newer copy (upgrade) or the uninstaller can ask us to exit.
        var exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        new Thread(() =>
        {
            exitRequest.WaitOne();
            PostMessage(msg.Handle, WM_EXIT_REQUEST, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true, Name = "exit-request" }.Start();

        // Launching SpaceSwipe again (Start menu) asks us to open Settings.
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSettingsEventName);
        new Thread(() =>
        {
            while (true)
            {
                showRequest.WaitOne();
                PostMessage(msg.Handle, WM_SHOW_SETTINGS, IntPtr.Zero, IntPtr.Zero);
            }
        }) { IsBackground = true, Name = "show-settings" }.Start();

        tray = new NotifyIcon
        {
            Icon = TrayIcon.Create(),
            Text = "SpaceSwipe: per-monitor spaces",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        tray.DoubleClick += (_, _) => ShowSettings();
    }

    void RegisterKey(int id, uint mods, uint vk)
    {
        if (!RegisterHotKey(msg.Handle, id, mods, vk)) Log.Write($"hotkey {id} could not be registered (in use?)");
    }

    void OnMessage(Message m)
    {
        switch (m.Msg)
        {
            case WM_INPUT:
                gestures.OnWmInput(m.LParam);
                break;
            case WM_HOTKEY:
                switch ((int)m.WParam)
                {
                    case HK_PREV: switcher.Navigate(-1); break;
                    case HK_NEXT: switcher.Navigate(+1); break;
                    case HK_DESKTOP: switcher.GoDesktop(); break;
                    case HK_OVERVIEW: ToggleOverview(); break;
                }
                break;
            case WM_EXIT_REQUEST:
                ExitThread();
                break;
            case WM_SHOW_SETTINGS:
                ShowSettings();
                break;
            case WM_DISPLAYCHANGE:
                // Monitors changed (e.g. a KVM switch): put parked windows back and let Windows rearrange them.
                Log.Write("display change: unparking all windows");
                parking.UnparkAll();
                DisplayInfo.Invalidate();
                break;
        }
    }

    // ---------------- vertical three-finger drags ----------------

    enum VMode { None, OpeningOverview, ClosingOverview, Down, UpTaskView }
    VMode vmode;

    /// <summary>Finger travel (touchpad widths) that fully opens or closes the overview.</summary>
    const double OverviewTravel = 0.20;
    /// <summary>Travel or speed that counts as a deliberate up/down swipe.</summary>
    const double SwipeTravel = 0.08, SwipeSpeed = 0.5;

    void OnVDragBegan(bool up)
    {
        vmode = VMode.None;
        if (!settings.Enabled) return;
        if (overview != null)
        {
            if (overview.IsOpen && !up) vmode = VMode.ClosingOverview;
            return;
        }
        if (up && settings.SwipeUpOverview)
        {
            var form = CreateOverview();
            if (form == null) return;
            form.BeginOpen();
            vmode = VMode.OpeningOverview;
        }
        else if (up) vmode = settings.SwipeUpTaskView ? VMode.UpTaskView : VMode.None;
        else vmode = settings.SwipeDownShowsDesktop ? VMode.Down : VMode.None;
    }

    void OnVDragMoved(double travel) // negative = up
    {
        switch (vmode)
        {
            case VMode.OpeningOverview: overview?.DragOpen(-travel / OverviewTravel); break;
            case VMode.ClosingOverview: overview?.DragClose(Math.Max(0, travel) / OverviewTravel); break;
        }
    }

    void OnVDragEnded(double travel, double velocity)
    {
        var mode = vmode;
        vmode = VMode.None;
        switch (mode)
        {
            case VMode.OpeningOverview:
            {
                double p = -travel / OverviewTravel;
                bool open = (p > 0.3 || velocity < -SwipeSpeed) && velocity < SwipeSpeed;
                overview?.EndOpen(open);
                break;
            }
            case VMode.ClosingOverview:
            {
                double t = travel / OverviewTravel;
                bool close = (t > 0.3 || velocity > SwipeSpeed) && velocity > -SwipeSpeed;
                overview?.EndClose(close);
                break;
            }
            case VMode.Down when travel >= SwipeTravel || velocity > SwipeSpeed:
                switcher.GoDesktop();
                break;
            case VMode.UpTaskView when -travel >= SwipeTravel || velocity < -SwipeSpeed:
                Send(Key(VK_LWIN, false), Key(VK_TAB, false), Key(VK_TAB, true), Key(VK_LWIN, true));
                break;
        }
    }

    /// <summary>Keyboard shortcut: open the overview with its animation, or close it.</summary>
    void ToggleOverview()
    {
        if (overview != null) { if (overview.IsOpen) overview.Dismiss(); return; }
        CreateOverview()?.OpenAnimated();
    }

    OverviewForm? CreateOverview()
    {
        if (switcher.GetSpaces() is not { } s) return null;
        try
        {
            var mon = s.Mon;
            var form = new OverviewForm(mon, s.Spaces, () => switcher.CaptureDesktop(mon), s.DesktopWindows);
            form.Chosen += h => switcher.ActivateSpace(mon, h);
            form.WindowChosen += h => switcher.ActivateDesktopWindow(mon, h);
            form.Reordered += order => switcher.Reorder(mon, order);
            form.MoveRequested += (h, target) => switcher.MoveToMonitor(h, target);
            form.FormClosed += (_, _) => { if (overview == form) overview = null; form.Dispose(); };
            overview = form;
            Log.Write($"{mon.Device}: overview with {s.Spaces.Count} space(s), {s.DesktopWindows.Count} desktop window(s)");
            return form;
        }
        catch (Exception ex)
        {
            Log.Write("overview failed: " + ex);
            overview = null;
            return null;
        }
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"SpaceSwipe {Installer.Version}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()) { Font = new Font(menu.Font, FontStyle.Bold) });
        enabledItem = Toggle("SpaceSwipe is on", settings.Enabled, v => { settings.Enabled = v; settings.Save(); });
        menu.Items.Add(enabledItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Project page", null, (_, _) =>
            Process.Start(new ProcessStartInfo(Installer.RepoUrl) { UseShellExecute = true })));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Uninstall SpaceSwipe…", null, (_, _) =>
        {
            if (MessageBox.Show("Remove SpaceSwipe from this PC?", "SpaceSwipe",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Installer.Uninstall();
            ExitThread();
            MessageBox.Show("SpaceSwipe was removed.\n\nTo get Windows' own three-finger app switching back, open " +
                            "Settings → Bluetooth & devices → Touchpad → Three-finger gestures and set Swipes to \"Switch apps\".",
                            "SpaceSwipe", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        return menu;
    }

    ToolStripMenuItem? enabledItem;
    SettingsForm? settingsForm;

    void ShowSettings()
    {
        if (settingsForm != null) { settingsForm.Activate(); return; }
        settingsForm = new SettingsForm(settings);
        settingsForm.Changed += ApplySettings;
        settingsForm.FormClosed += (_, _) => { settingsForm.Dispose(); settingsForm = null; };
        settingsForm.Show();
        SpaceSwitcher.Focus(settingsForm.Handle);
    }

    /// <summary>Apply settings that need more than being read at the next swipe.</summary>
    void ApplySettings()
    {
        RegisterHotkeys();
        if (enabledItem != null && enabledItem.Checked != settings.Enabled) enabledItem.Checked = settings.Enabled;
        tracker.Snapshot(); // re-evaluate spaces (excluded apps, borderless full screen)
    }

    void RegisterHotkeys()
    {
        foreach (var id in new[] { HK_PREV, HK_NEXT, HK_DESKTOP, HK_OVERVIEW }) UnregisterHotKey(msg.Handle, id);
        if (!settings.KeyboardShortcuts) return;
        const uint mods = MOD_CONTROL | MOD_ALT | MOD_WIN | MOD_NOREPEAT;
        RegisterKey(HK_PREV, mods, VK_LEFT);
        RegisterKey(HK_NEXT, mods, VK_RIGHT);
        RegisterKey(HK_DESKTOP, mods, VK_DOWN);
        RegisterKey(HK_OVERVIEW, mods, VK_UP);
    }

    static ToolStripMenuItem Toggle(string text, bool initial, Action<bool> onChange)
    {
        var item = new ToolStripMenuItem(text) { Checked = initial, CheckOnClick = true };
        item.CheckedChanged += (_, _) => onChange(item.Checked);
        return item;
    }

    protected override void ExitThreadCore()
    {
        Log.Write("SpaceSwipe exiting");
        touchTimer.Stop();
        tray.Visible = false;
        tray.Dispose();
        UnregisterHotKey(msg.Handle, HK_PREV);
        UnregisterHotKey(msg.Handle, HK_NEXT);
        UnregisterHotKey(msg.Handle, HK_DESKTOP);
        UnregisterHotKey(msg.Handle, HK_OVERVIEW);
        overview?.Close();
        overlay.Dispose();
        parking.Dispose(); // puts every parked window back on screen
        msg.DestroyHandle();
        base.ExitThreadCore();
    }
}

internal static class TrayIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var purple = new SolidBrush(Color.FromArgb(0x8B, 0x5C, 0xF6));
            using var white = new SolidBrush(Color.FromArgb(240, 255, 255, 255));
            using var dim = new SolidBrush(Color.FromArgb(120, 255, 255, 255));
            FillRounded(g, purple, new RectangleF(1, 3, 30, 26), 7);
            FillRounded(g, dim, new RectangleF(5, 9, 6, 14), 2);    // previous space
            FillRounded(g, white, new RectangleF(13, 8, 14, 16), 3); // current space
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    static void FillRounded(Graphics g, Brush b, RectangleF r, float radius)
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
}
