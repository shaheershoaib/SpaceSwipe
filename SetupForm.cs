using System.Diagnostics;

namespace SpaceSwipe;

/// <summary>First-run (and "Setup…") screen: checks the touchpad and Windows' gesture setting, then installs.</summary>
internal sealed class SetupForm : Form
{
    readonly Label touchpadStatus = Status();
    readonly LinkLabel driverLink = new() { AutoSize = true, Margin = new Padding(24, 0, 0, 8) };
    readonly Label swipeStatus = Status();
    readonly Label swipeHelp = new()
    {
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(24, 0, 0, 4),
        Text = "In Settings: Bluetooth & devices → Touchpad → Three-finger gestures → set Swipes to \"Nothing\".",
    };
    readonly Button openSettings = new() { Text = "Open touchpad settings", AutoSize = true, Margin = new Padding(24, 0, 0, 12) };
    readonly Label desktopStatus = Status();
    readonly Label desktopHelp = new()
    {
        AutoSize = true,
        MaximumSize = new Size(520, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(24, 0, 0, 4),
    };
    readonly Button mergeDesktops = new() { Text = "Merge into one desktop", AutoSize = true, Margin = new Padding(24, 0, 0, 12) };
    readonly CheckBox startup = new() { Text = "Start SpaceSwipe when I sign in", AutoSize = true, Checked = true, Margin = new Padding(0, 4, 0, 12) };
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };

    public bool StartWithWindows => startup.Checked;

    public SetupForm(bool installed)
    {
        Text = "SpaceSwipe setup";
        Icon = TrayIcon.Create();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        if (installed) startup.Checked = Startup.IsEnabled;

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };

        layout.Controls.Add(new Label
        {
            Text = "SpaceSwipe",
            Font = new Font("Segoe UI Semibold", 16f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        });
        layout.Controls.Add(new Label
        {
            Text = "macOS-style three-finger swipes for Windows. Each monitor gets its own row of full-screen apps; " +
                   "drag with three fingers to slide between them. Alt+Tab keeps working as usual.",
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            Margin = new Padding(0, 0, 0, 16),
        });

        layout.Controls.Add(touchpadStatus);
        driverLink.Text = "Using an Apple Magic Trackpad? Install the free Magic Trackpad driver first.";
        driverLink.LinkClicked += (_, _) => OpenUrl(Installer.MagicTrackpadDriverUrl);
        layout.Controls.Add(driverLink);

        layout.Controls.Add(swipeStatus);
        layout.Controls.Add(swipeHelp);
        openSettings.Click += (_, _) => OpenUrl("ms-settings:devices-touchpad");
        layout.Controls.Add(openSettings);

        layout.Controls.Add(desktopStatus);
        layout.Controls.Add(desktopHelp);
        mergeDesktops.Click += (_, _) =>
        {
            mergeDesktops.Enabled = false;
            UseWaitCursor = true;
            SystemCheck.MergeVirtualDesktops();
            UseWaitCursor = false;
            mergeDesktops.Enabled = true;
            Activate();
            UpdateStatus();
        };
        layout.Controls.Add(mergeDesktops);

        layout.Controls.Add(startup);

        layout.Controls.Add(new Label
        {
            Text = "Gestures: three-finger drag left/right switches apps on the monitor under the cursor · " +
                   "up opens that monitor's app overview · down shows its desktop. Keyboard: Ctrl+Alt+Win + ←/→/↑/↓.",
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 16),
        });

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        var ok = new Button { Text = installed ? "Done" : "Install and start", AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(10, 2, 10, 2) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(ok);
        if (!installed) buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = installed ? ok : cancel;

        refresh.Tick += (_, _) => UpdateStatus();
        refresh.Start();
        UpdateStatus();
    }

    static Label Status() => new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(0, 0, 0, 2) };

    void UpdateStatus()
    {
        bool touchpad = SystemCheck.HasPrecisionTouchpad();
        touchpadStatus.Text = touchpad ? "✔  Precision Touchpad found" : "✖  No Precision Touchpad found";
        touchpadStatus.ForeColor = touchpad ? Color.SeaGreen : Color.Firebrick;
        driverLink.Visible = !touchpad;

        bool off = SystemCheck.WindowsThreeFingerSwipesOff();
        swipeStatus.Text = off ? "✔  Windows' own three-finger swipes are off" : "➜  Turn off Windows' own three-finger swipes";
        swipeStatus.ForeColor = off ? Color.SeaGreen : Color.DarkOrange;
        swipeHelp.Visible = !off;
        openSettings.Visible = !off;

        // SpaceSwipe gives each monitor its own spaces, so Windows' all-monitor virtual desktops just get in the way.
        int desktops = SystemCheck.VirtualDesktopCount();
        bool fourOff = SystemCheck.WindowsFourFingerSwipesOff();
        bool desktopsOk = desktops <= 1 && fourOff;
        desktopStatus.Text = desktopsOk ? "✔  One Windows desktop, no desktop-switching swipes"
                                        : "➜  Turn off Windows' virtual desktops (optional, recommended)";
        desktopStatus.ForeColor = desktopsOk ? Color.SeaGreen : Color.DarkOrange;
        desktopHelp.Text = (desktops > 1 ? $"You have {desktops} Windows desktops; apps on the others are hidden from SpaceSwipe. " : "") +
                           (fourOff ? "" : "Also set Touchpad → Four-finger gestures → Swipes to \"Nothing\" so you don't switch desktops by accident.");
        desktopHelp.Visible = !desktopsOk;
        mergeDesktops.Visible = desktops > 1;
        if (!fourOff) openSettings.Visible = true;
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open failed: " + ex.Message); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        refresh.Stop();
        base.OnFormClosed(e);
    }
}
