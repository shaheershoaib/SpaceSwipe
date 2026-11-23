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

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            SelfTest.Run(args.Length > 1 ? args[1] : Path.Combine(Log.Dir, "selftest"));
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
        if (!created) return;

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
    public bool SwipeDownShowsDesktop { get; set; } = true;
    public bool LogTouchFrames { get; set; }
    public bool SetupDone { get; set; }

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
    const int HK_PREV = 1, HK_NEXT = 2, HK_DESKTOP = 3;
    const int WM_SWIPE = WM_APP + 1, WM_EXIT_REQUEST = WM_APP + 2;

    readonly Settings settings;
    readonly WindowTracker tracker = new();
    readonly SlideOverlay overlay = new();
    readonly TouchpadGestures gestures = new();
    readonly MessageWindow msg = new();
    readonly SpaceSwitcher switcher;
    readonly Parking parking = new();
    readonly NotifyIcon tray;
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
        switcher = new SpaceSwitcher(tracker, overlay, settings, parking);

        gestures.Threshold = settings.SwipeThreshold;
        gestures.DeadZone = settings.DragDeadZone;
        gestures.LogFrames = settings.LogTouchFrames;
        gestures.Swiped += s => PostMessage(msg.Handle, WM_SWIPE, (IntPtr)(int)s, IntPtr.Zero);
        // Drags are handled inline (on the UI thread, inside WM_INPUT) so the frame tracks the fingers.
        gestures.DragBegan += switcher.DragBegin;
        gestures.DragMoved += switcher.DragMove;
        gestures.DragEnded += switcher.DragEnd;
        msg.Handler = OnMessage;
        touchTimer.Tick += (_, _) => gestures.CheckTimeout();
        touchTimer.Start();

        bool raw = gestures.Register(msg.Handle);
        Log.Write($"SpaceSwipe {Application.ProductVersion} started; touchpad raw input registered={raw}");

        const uint mods = MOD_CONTROL | MOD_ALT | MOD_WIN | MOD_NOREPEAT;
        RegisterKey(HK_PREV, mods, VK_LEFT);
        RegisterKey(HK_NEXT, mods, VK_RIGHT);
        RegisterKey(HK_DESKTOP, mods, VK_DOWN);

        tracker.Snapshot(); // seed the per-monitor order

        // A newer copy (upgrade) or the uninstaller can ask us to exit.
        var exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        new Thread(() =>
        {
            exitRequest.WaitOne();
            PostMessage(msg.Handle, WM_EXIT_REQUEST, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true, Name = "exit-request" }.Start();

        tray = new NotifyIcon
        {
            Icon = TrayIcon.Create(),
            Text = "SpaceSwipe: per-monitor spaces",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
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
                }
                break;
            case WM_SWIPE:
                OnSwipe((Swipe)(int)m.WParam);
                break;
            case WM_EXIT_REQUEST:
                ExitThread();
                break;
            case WM_DISPLAYCHANGE:
                // Monitors changed (e.g. a KVM switch): put parked windows back and let Windows rearrange them.
                Log.Write("display change: unparking all windows");
                parking.UnparkAll();
                break;
        }
    }

    void OnSwipe(Swipe s)
    {
        if (!settings.Enabled) return;
        switch (s)
        {
            // Left/right are finger-tracked drags (DragBegin/Move/End); only vertical swipes arrive here.
            case Swipe.Up when settings.SwipeUpTaskView:
                Send(Key(VK_LWIN, false), Key(VK_TAB, false), Key(VK_TAB, true), Key(VK_LWIN, true));
                break;
            case Swipe.Down when settings.SwipeDownShowsDesktop: switcher.GoDesktop(); break;
        }
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"SpaceSwipe {Installer.Version}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Toggle("Enabled", settings.Enabled, v => { settings.Enabled = v; settings.Save(); }));
        menu.Items.Add(Toggle("Slide animation", settings.Animate, v => { settings.Animate = v; settings.Save(); }));
        var startup = Toggle("Start with Windows", Startup.IsEnabled, v => Startup.Set(v));
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripMenuItem("Setup…", null, (_, _) =>
        {
            using var setup = new SetupForm(installed: true);
            setup.ShowDialog();
            Startup.Set(setup.StartWithWindows);
            startup.Checked = setup.StartWithWindows;
        }));
        menu.Items.Add(new ToolStripSeparator());

        var help = new ToolStripMenuItem("Gestures");
        foreach (var line in new[]
                 {
                     "3-finger drag left / right: move between apps on this monitor",
                     "3 fingers up: Task View",
                     "3 fingers down: this monitor's desktop",
                     "Keyboard: Ctrl+Alt+Win + Left / Right / Down",
                 })
            help.DropDownItems.Add(new ToolStripMenuItem(line) { Enabled = false });
        menu.Items.Add(help);
        menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) =>
        {
            if (File.Exists(Log.FilePath)) Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }));
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
