using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using static SpaceSwipe.Native;

namespace SpaceSwipe;

/// <summary>The Settings window (tray icon → Settings…, or double-click the tray icon). Changes apply immediately.</summary>
internal sealed class SettingsForm : Form
{
    readonly Settings settings;
    readonly FlowLayoutPanel body;
    readonly Label updateStatus = new() { AutoSize = true, ForeColor = Color.FromArgb(88, 88, 96), Margin = new Padding(0, 6, 0, 0) };

    /// <summary>Raised after any change (already saved).</summary>
    public event Action? Changed;

    public SettingsForm(Settings settings)
    {
        this.settings = settings;
        Text = "SpaceSwipe Settings";
        Icon = TrayIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(600, 760);

        body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(24, 16, 24, 16),
        };
        Controls.Add(body);

        BuildGeneral();
        BuildMonitors();
        BuildGestures();
        BuildSpaces();
        BuildKeyboard();
        BuildAbout();
    }

    // ---------------- sections ----------------

    void BuildGeneral()
    {
        Header("General", first: true);
        Check("SpaceSwipe is on", settings.Enabled, v => settings.Enabled = v);
        Check("Start SpaceSwipe when I sign in", Startup.IsEnabled, v => Startup.Set(v), save: false);
    }

    void BuildMonitors()
    {
        Header("Monitors");
        Note("Use SpaceSwipe on these displays. Swipes and the overview do nothing on a display that's unticked.");
        DisplayInfo.Invalidate();
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
        var primary = screens.FirstOrDefault(s => s.Primary) ?? screens.FirstOrDefault();
        int n = 0;
        foreach (var s in screens)
        {
            n++;
            var info = DisplayInfo.For(s.DeviceName);
            string name = info.FriendlyName.Length > 0 ? info.FriendlyName : $"Display {n}";
            string where = s.Primary ? "main" :
                primary == null ? "" :
                s.Bounds.Left >= primary.Bounds.Right ? "right" :
                s.Bounds.Right <= primary.Bounds.Left ? "left" :
                s.Bounds.Top >= primary.Bounds.Bottom ? "below" : "above";
            var mon = WindowTracker.GetMonitor(MonitorFromPoint(new POINT { X = s.Bounds.Left + 5, Y = s.Bounds.Top + 5 }, MONITOR_DEFAULTTONEAREST));
            string label = $"{n}.  {name}  ·  {mon.Bounds.Width}×{mon.Bounds.Height}, {where}";
            string id = info.StableId;
            Check(label, !settings.DisabledMonitors.Contains(id), v =>
            {
                settings.DisabledMonitors.Remove(id);
                if (!v) settings.DisabledMonitors.Add(id);
            });
        }
        var identify = new Button { Text = "Identify displays", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        identify.Click += (_, _) => Identify(screens);
        body.Controls.Add(identify);
    }

    void BuildGestures()
    {
        Header("Gestures");
        Check("Three-finger drag left / right switches apps on that display", settings.SwipeSideways, v => settings.SwipeSideways = v);
        Check("Slide animation (follows your fingers)", settings.Animate, v => settings.Animate = v);
        Check("Reverse direction (off = natural, like macOS)", settings.ReverseDirection, v => settings.ReverseDirection = v);

        Choice("Three-finger swipe up", new[] { "App overview (this display)", "Windows Task View", "Nothing" },
            settings.SwipeUpOverview ? 0 : settings.SwipeUpTaskView ? 1 : 2,
            i => { settings.SwipeUpOverview = i == 0; settings.SwipeUpTaskView = i == 1; });
        Choice("Three-finger swipe down", new[] { "This display's desktop", "Nothing" },
            settings.SwipeDownShowsDesktop ? 0 : 1,
            i => settings.SwipeDownShowsDesktop = i == 0);

        // Swipe distance: how far the fingers travel for one full screen.
        var row = Row("Swipe distance");
        var slider = new TrackBar
        {
            Minimum = 30, Maximum = 80, TickFrequency = 10, SmallChange = 5, LargeChange = 10,
            Value = Math.Clamp((int)Math.Round(settings.FullSwipeFraction * 100), 30, 80),
            Width = 260, AutoSize = false, Height = 36,
        };
        var hint = new Label { AutoSize = true, ForeColor = Color.FromArgb(88, 88, 96), Margin = new Padding(6, 8, 0, 0) };
        void UpdateHint() => hint.Text = slider.Value <= 40 ? "short" : slider.Value >= 65 ? "long" : "medium";
        UpdateHint();
        slider.ValueChanged += (_, _) => { settings.FullSwipeFraction = slider.Value / 100.0; UpdateHint(); Save(); };
        row.Controls.Add(slider);
        row.Controls.Add(hint);
    }

    void BuildSpaces()
    {
        Header("Spaces");
        Note("Every maximized app on a display is a space, after that display's Desktop. Minimized apps aren't spaces.");
        Check("Count borderless full-screen windows (games, F11 browsers) as spaces",
            settings.IncludeBorderlessFullScreen, v => settings.IncludeBorderlessFullScreen = v);

        Note("Never treat these apps as spaces (one program per line, e.g. steam.exe):", top: 10);
        var box = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 520, Height = 90,
            Text = string.Join(Environment.NewLine, settings.ExcludedApps),
        };
        box.Leave += (_, _) =>
        {
            settings.ExcludedApps = box.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Save();
        };
        body.Controls.Add(box);

        var add = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Margin = new Padding(0, 6, 0, 0) };
        add.Items.Add("Add an open app…");
        foreach (var name in OpenAppNames()) add.Items.Add(name);
        add.SelectedIndex = 0;
        add.SelectedIndexChanged += (_, _) =>
        {
            if (add.SelectedIndex <= 0) return;
            var name = (string)add.SelectedItem!;
            if (!settings.ExcludedApps.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                settings.ExcludedApps.Add(name);
                box.Text = string.Join(Environment.NewLine, settings.ExcludedApps);
                Save();
            }
            add.SelectedIndex = 0;
        };
        body.Controls.Add(add);
    }

    void BuildKeyboard()
    {
        Header("Keyboard");
        Check("Ctrl + Alt + Win + ← / → / ↑ / ↓ shortcuts", settings.KeyboardShortcuts, v => settings.KeyboardShortcuts = v);
    }

    void BuildAbout()
    {
        Header("About");
        Note($"SpaceSwipe {Installer.Version}: macOS-style three-finger swipes, one row of apps per display.");
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0) };
        row.Controls.Add(Button("Check for updates", (_, _) => _ = CheckForUpdates()));
        row.Controls.Add(Button("Project page", (_, _) => Open(Installer.RepoUrl)));
        row.Controls.Add(Button("Setup checks…", (_, _) => { using var s = new SetupForm(installed: true); s.ShowDialog(this); }));
        row.Controls.Add(Button("Open log", (_, _) => { if (File.Exists(Log.FilePath)) Open(Log.FilePath); }));
        body.Controls.Add(row);
        body.Controls.Add(updateStatus);
    }

    // ---------------- helpers ----------------

    void Header(string text, bool first = false) => body.Controls.Add(new Label
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 12.5f),
        Margin = new Padding(0, first ? 0 : 22, 0, 6),
    });

    void Note(string text, int top = 0) => body.Controls.Add(new Label
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(530, 0),
        ForeColor = Color.FromArgb(88, 88, 96),
        Margin = new Padding(0, top, 0, 6),
    });

    void Check(string text, bool value, Action<bool> set, bool save = true)
    {
        var c = new CheckBox { Text = text, AutoSize = true, Checked = value, Margin = new Padding(0, 2, 0, 2) };
        c.CheckedChanged += (_, _) => { set(c.Checked); if (save) Save(); else Changed?.Invoke(); };
        body.Controls.Add(c);
    }

    FlowLayoutPanel Row(string label)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        row.Controls.Add(new Label { Text = label, AutoSize = false, Width = 190, Height = 28, TextAlign = ContentAlignment.MiddleLeft });
        body.Controls.Add(row);
        return row;
    }

    void Choice(string label, string[] options, int selected, Action<int> set)
    {
        var row = Row(label);
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        combo.Items.AddRange(options);
        combo.SelectedIndex = Math.Clamp(selected, 0, options.Length - 1);
        combo.SelectedIndexChanged += (_, _) => { set(combo.SelectedIndex); Save(); };
        row.Controls.Add(combo);
    }

    static Button Button(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        b.Click += onClick;
        return b;
    }

    void Save()
    {
        settings.Save();
        Changed?.Invoke();
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open failed: " + ex.Message); }
    }

    /// <summary>Program names of apps with windows open right now (for the exclusion picker).</summary>
    static List<string> OpenAppNames()
    {
        var tracker = new WindowTracker();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((h, _) =>
        {
            if (tracker.IsAppWindow(h))
            {
                var n = tracker.ProcessName(h);
                if (n.Length > 0 && n != "spaceswipe.exe") names.Add(n);
            }
            return true;
        }, IntPtr.Zero);
        return names.ToList();
    }

    /// <summary>Show a big number on each display for a moment, matching the list above.</summary>
    void Identify(List<Screen> screens)
    {
        var popups = new List<Form>();
        for (int i = 0; i < screens.Count; i++)
        {
            var b = screens[i].Bounds;
            var f = new Form
            {
                FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true, StartPosition = FormStartPosition.Manual,
                BackColor = Color.FromArgb(32, 32, 36), Size = new Size(220, 220),
                Location = new Point(b.Left + (b.Width - 220) / 2, b.Top + (b.Height - 220) / 2),
            };
            f.Controls.Add(new Label
            {
                Text = (i + 1).ToString(), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 96f),
            });
            f.Show();
            popups.Add(f);
        }
        var t = new System.Windows.Forms.Timer { Interval = 1800 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); foreach (var f in popups) { f.Close(); f.Dispose(); } };
        t.Start();
    }

    async Task CheckForUpdates()
    {
        updateStatus.ForeColor = Color.FromArgb(88, 88, 96);
        updateStatus.Text = "Checking…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SpaceSwipe");
            var repo = Installer.RepoUrl.Replace("https://github.com/", "");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var url = doc.RootElement.GetProperty("html_url").GetString() ?? Installer.RepoUrl;
            if (Version.TryParse(tag.TrimStart('v'), out var latest) && Version.TryParse(Installer.Version, out var mine) && latest > mine)
            {
                updateStatus.ForeColor = Color.SeaGreen;
                updateStatus.Text = $"Version {latest} is available. Opening the download page… (run the new SpaceSwipe.exe to update)";
                Open(url);
            }
            else updateStatus.Text = $"You're up to date ({Installer.Version}).";
        }
        catch (Exception ex)
        {
            updateStatus.ForeColor = Color.Firebrick;
            updateStatus.Text = "Couldn't check for updates: " + ex.Message;
        }
    }
}
