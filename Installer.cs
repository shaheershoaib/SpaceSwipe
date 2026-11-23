using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static SpaceSwipe.Native;

namespace SpaceSwipe;

/// <summary>Per-user install: no admin rights, no installer framework. The exe copies itself into place.</summary>
internal static class Installer
{
    public const string RepoUrl = "https://github.com/shaheershoaib/SpaceSwipe";
    public const string MagicTrackpadDriverUrl = "https://github.com/vitoplantamura/MagicTrackpad2ForWindows/releases";

    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SpaceSwipe";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SpaceSwipe");

    public static string InstalledExe => Path.Combine(InstallDir, "SpaceSwipe.exe");

    static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SpaceSwipe.lnk");

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static string Version => Application.ProductVersion.Split('+')[0];

    /// <summary>Copy this exe into the install folder and register it (Start menu, Apps list, optional sign-in start).</summary>
    public static void Install(bool startWithWindows)
    {
        Directory.CreateDirectory(InstallDir);
        if (!IsRunningInstalled)
        {
            // Clear out files from older builds, then copy ourselves in.
            foreach (var f in Directory.GetFiles(InstallDir))
                try { File.Delete(f); } catch { /* in use: overwritten below or harmless */ }
            File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
        }

        CreateShortcut(StartMenuShortcut, InstalledExe);
        Startup.Set(startWithWindows, InstalledExe);

        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "SpaceSwipe");
        k.SetValue("DisplayVersion", Version);
        k.SetValue("Publisher", "SpaceSwipe");
        k.SetValue("DisplayIcon", InstalledExe);
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("URLInfoAbout", RepoUrl);
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        Log.Write($"installed {Version} to {InstallDir}");
    }

    /// <summary>Remove everything SpaceSwipe added. The install folder is deleted after this process exits.</summary>
    public static void Uninstall()
    {
        Startup.Set(false);
        try { File.Delete(StartMenuShortcut); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        try { Directory.Delete(Path.GetDirectoryName(Settings.FilePath)!, true); } catch { }
        try { Directory.Delete(Log.Dir, true); } catch { }

        // Can't delete our own running exe: let a short-lived cmd do it once we've exited.
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    static void CreateShortcut(string lnk, string target)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.Description = "macOS-style three-finger swipes between full-screen apps";
            sc.IconLocation = target + ",0";
            sc.Save();
            Marshal.FinalReleaseComObject(sc);
            Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception ex)
        {
            Log.Write("shortcut failed: " + ex.Message);
        }
    }
}

/// <summary>What the setup screen checks.</summary>
internal static class SystemCheck
{
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [DllImport("user32.dll")]
    static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);

    const uint RIDI_DEVICEINFO = 0x2000000b;

    /// <summary>Is any Windows Precision Touchpad (HID digitizer, usage page 0x0D / usage 0x05) connected?</summary>
    public static bool HasPrecisionTouchpad()
    {
        uint n = 0, sz = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref n, sz);
        if (n == 0) return false;
        var list = new RAWINPUTDEVICELIST[n];
        if (GetRawInputDeviceList(list, ref n, sz) == uint.MaxValue) return false;

        IntPtr info = Marshal.AllocHGlobal(32);
        try
        {
            foreach (var d in list)
            {
                if (d.dwType != RIM_TYPEHID) continue;
                Marshal.WriteInt32(info, 32);
                uint size = 32;
                uint r = GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICEINFO, info, ref size);
                if (r == uint.MaxValue || r == 0) continue;
                ushort page = (ushort)Marshal.ReadInt16(info, 20);
                ushort usage = (ushort)Marshal.ReadInt16(info, 22);
                if (page == 0x0D && usage == 0x05) return true;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
        return false;
    }

    /// <summary>How many Windows virtual desktops exist (SpaceSwipe replaces them, and apps on other desktops are hidden from it).</summary>
    public static int VirtualDesktopCount()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
        return k?.GetValue("VirtualDesktopIDs") is byte[] ids && ids.Length >= 16 ? ids.Length / 16 : 1;
    }

    /// <summary>
    /// Close extra virtual desktops (Win+Ctrl+F4) until one is left. Windows on a closed desktop move to the
    /// neighbouring one, so no app is closed.
    /// </summary>
    public static void MergeVirtualDesktops()
    {
        const uint VK_CONTROL = 0x11, VK_F4 = 0x73;
        for (int i = 0; i < 30 && VirtualDesktopCount() > 1; i++)
        {
            Native.Send(Native.Key(Native.VK_LWIN, false), Native.Key(VK_CONTROL, false), Native.Key(VK_F4, false),
                        Native.Key(VK_F4, true), Native.Key(VK_CONTROL, true), Native.Key(Native.VK_LWIN, true));
            Thread.Sleep(600);
        }
        Log.Write($"merged virtual desktops; now {VirtualDesktopCount()}");
    }

    /// <summary>Windows' four-finger swipes switch virtual desktops by default (missing value = default).</summary>
    public static bool WindowsFourFingerSwipesOff()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
        return k?.GetValue("FourFingerSlideEnabled") is int v && v == 0;
    }

    /// <summary>
    /// Windows' own three-finger swipe action (0 = Nothing). Missing means the default, "Switch apps".
    /// Only the Settings app applies a change immediately, so we just read it.
    /// </summary>
    public static bool WindowsThreeFingerSwipesOff()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
        return k?.GetValue("ThreeFingerSlideEnabled") is int v && v == 0;
    }
}
