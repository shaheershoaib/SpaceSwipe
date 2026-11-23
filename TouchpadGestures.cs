using System.Runtime.InteropServices;
using static SpaceSwipe.Native;

namespace SpaceSwipe;

/// <summary>Direction the fingers moved.</summary>
internal enum Swipe { Left = 1, Right = 2, Up = 3, Down = 4 }

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
    }

    readonly record struct Contact(bool Tip, int X, int Y);

    enum Mode { Idle, Pending, Drag, Done }

    readonly Dictionary<IntPtr, Device?> devices = new();
    readonly List<Contact> frame = new();
    readonly ushort[] usageBuf = new ushort[32];
    readonly List<(long T, double Fx)> samples = new();
    int expected;
    Mode mode;
    double sx, sy, lastFx;
    long lastFrameMs;

    /// <summary>Vertical swipe distance, as a fraction of the touchpad width.</summary>
    public double Threshold { get; set; } = 0.10;
    /// <summary>Horizontal movement before a drag starts, as a fraction of the touchpad width.</summary>
    public double DeadZone { get; set; } = 0.02;
    public bool LogFrames { get; set; }

    /// <summary>Discrete vertical swipes.</summary>
    public event Action<Swipe>? Swiped;
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
              $"Y={result.YMin}..{result.YMax} countLink={result.CountLink}");
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
        if (mode == Mode.Drag && Environment.TickCount64 - lastFrameMs > 300)
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
            if (mode == Mode.Drag) EndDrag();
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

        if (n == 0)
        {
            if (mode == Mode.Drag) EndDrag();
            mode = Mode.Idle;
            return;
        }
        if (n != 3)
        {
            if (mode == Mode.Drag) EndDrag();
            // Fewer fingers while still landing: wait. More fingers, or a finished gesture: wait for a full lift.
            mode = n > 3 || mode is Mode.Drag or Mode.Done ? Mode.Done : Mode.Idle;
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

        double fx = (cx - sx) / width, fy = (cy - sy) / width;
        if (mode == Mode.Pending)
        {
            if (Math.Abs(fx) >= DeadZone && Math.Abs(fx) > 1.2 * Math.Abs(fy))
            {
                mode = Mode.Drag;
                sx = cx; // start the frame from where the drag was recognised, so it doesn't jump
                lastFx = 0;
                samples.Clear();
                samples.Add((now, 0));
                Log.Write("drag begin");
                DragBegan?.Invoke();
            }
            else if (Math.Abs(fy) >= Threshold * 0.8 && Math.Abs(fy) > 1.4 * Math.Abs(fx))
            {
                mode = Mode.Done;
                var s = fy < 0 ? Swipe.Up : Swipe.Down;
                Log.Write("swipe " + s);
                Swiped?.Invoke(s);
            }
            return;
        }

        // Mode.Drag
        lastFx = fx;
        samples.Add((now, fx));
        if (samples.Count > 64) samples.RemoveRange(0, samples.Count - 64);
        DragMoved?.Invoke(fx);
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
        Log.Write($"drag end fx={lastFx:F3} v={v:F2}/s");
        DragEnded?.Invoke(lastFx, v);
    }
}
