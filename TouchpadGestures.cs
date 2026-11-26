using System.Runtime.InteropServices;
using static SpaceSwipe.Native;

namespace SpaceSwipe;


/// <summary>
/// Reads raw Precision Touchpad reports (HID digitizer, usage page 0x0D / usage 0x05)
/// and detects three-finger swipes. Works in the background via RIDEV_INPUTSINK.
/// </summary>
internal sealed class TouchpadGestures
{
    sealed class Device
    {
        public IntPtr Preparsed;
        public readonly List<ushort> FingerLinks = new();
        public int XMin, XMax, YMin, YMax;
        public int CountLink = -1;
        public double WidthMm = 130; // physical width (from the HID descriptor; a typical size if it doesn't say)
    }

    readonly record struct Contact(bool Tip, int X, int Y);

    enum Mode { Idle, Pending, Drag, VDrag, Done }

    readonly Dictionary<IntPtr, Device?> devices = new();
    readonly List<Contact> frame = new();
    readonly ushort[] usageBuf = new ushort[32];
    readonly List<(long T, double Fx)> samples = new();
    int expected;
    Mode mode;
    double sx, sy, lastValue;
    long lastFrameMs;

    /// <summary>Movement before a drag starts, as a fraction of the touchpad width.</summary>
    public double DeadZone { get; set; } = 0.02;
    public bool LogFrames { get; set; }

    /// <summary>How long the touch report being handled waited in the message queue (diagnostics).</summary>
    public static int InputDelayMs;

    /// <summary>Fingers used for space swipes: 3, or 4 while three-finger drag is on (like on a Mac).</summary>
    public int SwipeFingers { get; set; } = 3;

    /// <summary>Three-finger drag, fed every frame (it decides for itself whether it's on).</summary>
    public ThreeFingerDrag? FingerDrag { get; set; }

    /// <summary>A vertical three-finger drag started (true = upwards).</summary>
    public event Action<bool>? VDragBegan;
    /// <summary>Vertical travel since the drag started, in touchpad widths (negative = up).</summary>
    public event Action<double>? VDragMoved;
    /// <summary>Vertical drag finished: final travel and velocity (touchpad widths per second, negative = up).</summary>
    public event Action<double, double>? VDragEnded;
    /// <summary>A horizontal three-finger drag started.</summary>
    public event Action? DragBegan;
    /// <summary>Horizontal finger travel since the drag started, as a fraction of the touchpad width (negative = left).</summary>
    public event Action<double>? DragMoved;
    /// <summary>Drag finished: final travel and velocity (touchpad widths per second).</summary>
    public event Action<double, double>? DragEnded;

    public bool Register(IntPtr hwnd)
    {
        var rid = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
        };
        return RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    public void OnWmInput(IntPtr lParam)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (header.dwType != RIM_TYPEHID) return;
            var dev = GetDevice(header.hDevice);
            if (dev == null) return;

            int off = (int)headerSize;
            int sizeHid = Marshal.ReadInt32(buf, off);
            int count = Marshal.ReadInt32(buf, off + 4);
            if (sizeHid <= 0 || count <= 0) return;
            for (int i = 0; i < count; i++)
            {
                var report = new byte[sizeHid];
                Marshal.Copy(buf + off + 8 + i * sizeHid, report, 0, sizeHid);
                Process(dev, report);
            }
        }
        catch (Exception ex)
        {
            Log.Write("raw input error: " + ex.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    Device? GetDevice(IntPtr h)
    {
        if (devices.TryGetValue(h, out var known)) return known;
        Device? result = null;
        IntPtr pp = IntPtr.Zero;
        try
        {
            uint size = 0;
            GetRawInputDeviceInfo(h, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
            if (size > 0)
            {
                pp = Marshal.AllocHGlobal((int)size);
                if (GetRawInputDeviceInfo(h, RIDI_PREPARSEDDATA, pp, ref size) != unchecked((uint)-1))
                {
                    var caps = new byte[64];
                    if (HidP_GetCaps(pp, caps) == HIDP_STATUS_SUCCESS)
                    {
                        ushort nv = BitConverter.ToUInt16(caps, 48);
                        var vc = new byte[72 * Math.Max(1, (int)nv)];
                        ushort len = nv;
                        if (nv > 0 && HidP_GetValueCaps(HidP_Input, vc, ref len, pp) == HIDP_STATUS_SUCCESS)
                            result = Describe(pp, vc, len);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("device probe failed: " + ex.Message);
        }

        if (result == null && pp != IntPtr.Zero) Marshal.FreeHGlobal(pp);
        devices[h] = result;
        Log.Write(result == null
            ? $"touchpad device {h}: not usable"
            : $"touchpad device {h}: fingers={result.FingerLinks.Count} X={result.XMin}..{result.XMax} " +
              $"Y={result.YMin}..{result.YMax} countLink={result.CountLink} width={result.WidthMm:F0}mm");
        return result;
    }

    static Device? Describe(IntPtr pp, byte[] vc, int len)
    {
        var dev = new Device { Preparsed = pp };
        for (int i = 0; i < len; i++)
        {
            int o = i * 72;
            ushort page = BitConverter.ToUInt16(vc, o);
            ushort link = BitConverter.ToUInt16(vc, o + 6);
            bool isRange = vc[o + 12] != 0;
            int lmin = BitConverter.ToInt32(vc, o + 40);
            int lmax = BitConverter.ToInt32(vc, o + 44);
            ushort umin = BitConverter.ToUInt16(vc, o + 56);
            ushort umax = isRange ? BitConverter.ToUInt16(vc, o + 58) : umin;
            bool Has(ushort u) => umin <= u && u <= umax;

            if (page == 0x01 && Has(0x30))
            {
                if (!dev.FingerLinks.Contains(link)) dev.FingerLinks.Add(link);
                dev.XMin = lmin; dev.XMax = lmax;
                // Physical extent: Units 0x11 = centimetres, 0x13 = inches; UnitsExp is a 4-bit signed exponent.
                uint unitsExp = BitConverter.ToUInt32(vc, o + 32), units = BitConverter.ToUInt32(vc, o + 36);
                int pmin = BitConverter.ToInt32(vc, o + 48), pmax = BitConverter.ToInt32(vc, o + 52);
                int exp = (int)(unitsExp & 0xF);
                if (exp > 7) exp -= 16;
                double mmPerUnit = (units & 0xFF) switch { 0x11 => 10.0, 0x13 => 25.4, _ => 0 };
                double mm = (pmax - pmin) * Math.Pow(10, exp) * mmPerUnit;
                if (mm is > 40 and < 400) dev.WidthMm = mm;
            }
            if (page == 0x01 && Has(0x31)) { dev.YMin = lmin; dev.YMax = lmax; }
            if (page == 0x0D && Has(0x54)) dev.CountLink = link;
        }
        return dev.FingerLinks.Count > 0 && dev.XMax > dev.XMin ? dev : null;
    }

    void Process(Device d, byte[] r)
    {
        uint len = (uint)r.Length;
        int cc = -1;
        if (d.CountLink >= 0 &&
            HidP_GetUsageValue(HidP_Input, 0x0D, (ushort)d.CountLink, 0x54, out uint v, d.Preparsed, r, len) == HIDP_STATUS_SUCCESS)
            cc = (int)v;

        if (cc > 0) { expected = cc; frame.Clear(); }            // first report of a frame (hybrid mode)
        else if (cc < 0) { expected = int.MaxValue; frame.Clear(); } // no contact count: each report is a frame
        else if (expected == 0) return;                           // stray continuation

        foreach (var link in d.FingerLinks)
        {
            if (frame.Count >= expected) break;
            if (HidP_GetUsageValue(HidP_Input, 0x01, link, 0x30, out uint x, d.Preparsed, r, len) != HIDP_STATUS_SUCCESS) continue;
            if (HidP_GetUsageValue(HidP_Input, 0x01, link, 0x31, out uint y, d.Preparsed, r, len) != HIDP_STATUS_SUCCESS) continue;
            uint n = (uint)usageBuf.Length;
            bool tip = HidP_GetUsages(HidP_Input, 0x0D, link, usageBuf, ref n, d.Preparsed, r, len) == HIDP_STATUS_SUCCESS &&
                       Array.IndexOf(usageBuf, (ushort)0x42, 0, (int)n) >= 0;
            frame.Add(new Contact(tip, (int)x, (int)y));
        }

        if (cc < 0 || frame.Count >= expected)
        {
            OnFrame(d);
            frame.Clear();
            expected = 0;
        }
    }

    /// <summary>Called on a timer: ends a drag if the touchpad stopped sending frames.</summary>
    public void CheckTimeout()
    {
        FingerDrag?.Tick();
        if (mode is Mode.Drag or Mode.VDrag && Environment.TickCount64 - lastFrameMs > 300)
        {
            Log.Write("drag timed out (no touch frames)");
            EndDrag();
            mode = Mode.Done;
        }
    }

    void OnFrame(Device d)
    {
        long now = Environment.TickCount64;
        if (now - lastFrameMs > 250)
        {
            if (mode is Mode.Drag or Mode.VDrag) EndDrag();
            mode = Mode.Idle;
        }
        lastFrameMs = now;

        int n = 0;
        double cx = 0, cy = 0;
        foreach (var c in frame)
        {
            if (!c.Tip) continue;
            n++; cx += c.X; cy += c.Y;
        }
        if (LogFrames) Log.Write($"frame tips={n} slots={frame.Count} mode={mode}");
        FingerDrag?.OnFrame(n, n > 0 ? cx / n : 0, n > 0 ? cy / n : 0, d.XMax - d.XMin, d.WidthMm);

        if (n == 0)
        {
            if (mode is Mode.Drag or Mode.VDrag) EndDrag();
            mode = Mode.Idle;
            return;
        }
        if (n != SwipeFingers)
        {
            if (mode is Mode.Drag or Mode.VDrag) EndDrag();
            // Fewer fingers while still landing: wait. More fingers, or a finished gesture: wait for a full lift.
            mode = n > SwipeFingers || mode is Mode.Drag or Mode.VDrag or Mode.Done ? Mode.Done : Mode.Idle;
            return;
        }
        if (mode == Mode.Done) return;

        cx /= n; cy /= n;
        double width = d.XMax - d.XMin;
        if (mode == Mode.Idle)
        {
            mode = Mode.Pending;
            sx = cx; sy = cy;
            return;
        }

        double fx = (cx - sx) / width, fy = (cy - sy) / width; // both in touchpad widths
        if (mode == Mode.Pending)
        {
            if (Math.Abs(fx) >= DeadZone && Math.Abs(fx) > 1.2 * Math.Abs(fy))
            {
                mode = Mode.Drag;
                sx = cx; // start from where the drag was recognised, so nothing jumps
                StartSamples(now);
                Log.Write("drag begin");
                DragBegan?.Invoke();
            }
            else if (Math.Abs(fy) >= DeadZone && Math.Abs(fy) > 1.2 * Math.Abs(fx))
            {
                mode = Mode.VDrag;
                bool up = fy < 0;
                sy = cy;
                StartSamples(now);
                Log.Write($"vertical drag begin ({(up ? "up" : "down")})");
                VDragBegan?.Invoke(up);
            }
            return;
        }

        double v = mode == Mode.Drag ? fx : fy;
        lastValue = v;
        samples.Add((now, v));
        if (samples.Count > 64) samples.RemoveRange(0, samples.Count - 64);
        if (mode == Mode.Drag) DragMoved?.Invoke(v);
        else VDragMoved?.Invoke(v);
    }

    void StartSamples(long now)
    {
        lastValue = 0;
        samples.Clear();
        samples.Add((now, 0));
    }

    void EndDrag()
    {
        double v = 0;
        if (samples.Count > 1)
        {
            var last = samples[^1];
            var first = samples.LastOrDefault(s => last.T - s.T >= 60, samples[0]);
            double dt = last.T - first.T;
            if (dt >= 8) v = (last.Fx - first.Fx) / dt * 1000.0;
        }
        bool vertical = mode == Mode.VDrag;
        Log.Write($"{(vertical ? "vertical " : "")}drag end travel={lastValue:F3} v={v:F2}/s");
        if (vertical) VDragEnded?.Invoke(lastValue, v);
        else DragEnded?.Invoke(lastValue, v);
    }
}
