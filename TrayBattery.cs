// 2026-11-11 23:47
// UI and docs in English; menu device lines are just "<device> | <%>"; repository restarted from one commit

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TrayBattery
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // TrayBattery.exe --dump <file>: one reading per device into a text file, no tray.
            if (args.Length == 2 && args[0] == "--dump")
            {
                var lines = new List<string>();
                foreach (var g in new Gadget[] { new AtkMouse(), new HyperXCloud3S() })
                {
                    var r = g.Query();
                    lines.Add(string.Format("{0}: online={1} asleep={2} percent={3} charging={4}", g.Name, r.Online, r.Asleep, r.Percent, r.Charging));
                }
                File.WriteAllLines(args[1], lines);
                return;
            }

            bool first;
            using (var mutex = new Mutex(true, "TrayBattery.SingleInstance", out first))
            {
                if (!first) return;
                Native.EnterEfficiencyMode();
                Ui.InitTooltips();
                GC.KeepAlive(new App());
                // Plain message loop: the thread sleeps in GetMessage until something arrives.
                Ui.MSG msg;
                while (Ui.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    // Tab / Enter / Esc in the settings window, while it is open.
                    IntPtr dlg = SettingsWindow.Open;
                    if (dlg != IntPtr.Zero && Ui.IsDialogMessage(dlg, ref msg)) continue;
                    Ui.TranslateMessage(ref msg);
                    Ui.DispatchMessage(ref msg);
                }
            }
        }
    }

    // ---------------------------------------------------------------- readings

    struct Reading
    {
        public bool Online;     // device answered
        public bool Asleep;     // receiver answered, the device itself is asleep (mouse until moved)
        public int Percent;
        public bool Charging;
    }

    abstract class Gadget
    {
        public abstract string Name { get; }
        public abstract char Glyph { get; }
        public abstract Reading Query();

        // Collection on which the device pushes reports by itself (null if none),
        // and how such a report updates the current reading; true when it carried anything.
        public virtual HidInfo PushCollection() { return null; }
        public virtual bool ApplyPush(byte[] report, ref Reading r, out bool pollNow) { pollNow = false; return false; }

        // How long to wait after a push that asks for a poll (the last report of a burst restarts it).
        public virtual uint PushPollDelayMs { get { return 1500; } }
    }

    // ATK MAD R+ (dongle 373B:1040), collection FF02.
    // Request: report 08, 16 bytes [cmd, status, addr hi, addr lo, len, data x10, checksum],
    // checksum = 0x55 - (report id + all bytes). Battery cmd 0x04: reply data[0] = %, data[1] = charging.
    sealed class AtkMouse : Gadget
    {
        public override string Name { get { return "ATK MAD R+"; } }
        public override char Glyph { get { return '\uE962'; } }

        public override HidInfo PushCollection() { return Hid.Find(0x373B, 0x1040).Find(d => d.UsagePage == 0xFF02); }

        // Seen 2026-11-11: when the mouse wakes, is put on or taken off the charger it sends 08 0A data 40
        // (link up) 2-3 times, often with 08 04 (level, charging flag) — but not always: taking it off the
        // charger came with 08 0A only. The mouse is awake for ~10 s after that, so 08 0A asks for a poll
        // 0.5 s after the burst ends. On the charger it sleeps between reports and sends none. Copies of
        // the replies to our own polls land here too and are applied the same way.
        public override uint PushPollDelayMs { get { return 500; } }
        public override bool ApplyPush(byte[] b, ref Reading r, out bool pollNow)
        {
            pollNow = b.Length >= 2 && b[0] == 0x08 && b[1] == 0x0A;
            if (b.Length < 8 || b[0] != 0x08 || b[1] != 0x04 || b[5] < 1 || b[6] > 100) return false;
            r.Online = true;
            r.Percent = b[6];
            r.Charging = b[5] >= 2 && b[7] != 0;
            return true;
        }

        static byte[] Command(byte cmd)
        {
            var req = new byte[17];
            req[0] = 0x08; req[1] = cmd;
            int sum = 0;
            for (int i = 0; i < 16; i++) sum += req[i];
            req[16] = (byte)(0x55 - sum);
            return req;
        }

        public override Reading Query()
        {
            var r = new Reading();
            var dev = PushCollection();
            if (dev == null) return r;

            // 03 GetWirelessMouseOnline is answered by the dongle itself, at once: data[0] = 1 linked, 0 asleep.
            // Only a linked mouse is asked for the battery, which has to travel over the radio.
            var online = Hid.Transact(dev, Command(0x03), b => b[0] == 0x08 && b[1] == 0x03 && b[5] >= 1, 500);
            if (online == null) return r;
            if (online[6] == 0) { r.Asleep = true; return r; }

            var rep = Hid.Transact(dev, Command(0x04), b => b[0] == 0x08 && b[1] == 0x04 && b[5] >= 1, 1500);
            if (rep == null || rep[6] > 100) { r.Asleep = true; return r; }
            r.Online = true;
            r.Percent = rep[6];
            r.Charging = rep[5] >= 2 && rep[7] != 0;
            return r;
        }
    }

    // HyperX Cloud III S Wireless (dongle 03F0:06BE / 02CC), interface 3.
    // Request: 64-byte output report 0C 02 03 01 00 <cmd>; reply 0C .. .. .. .. <cmd> <value>.
    // cmd 0x06 battery %, cmd 0x48 charging (0 no, 1 charging, 2 full); 0xFF = no headset link.
    sealed class HyperXCloud3S : Gadget
    {
        public override string Name { get { return "HyperX Cloud III S Wireless"; } }
        public override char Glyph { get { return '\uE7F6'; } }

        static List<HidInfo> Collections()
        {
            var list = Hid.Find(0x03F0, 0x06BE);
            return list.Count > 0 ? list : Hid.Find(0x03F0, 0x02CC);
        }

        // Report 0D (declared by collection 01C0 together with the 0C replies):
        // byte 4 = 1 -> battery % in byte 5, byte 4 = 10 -> charging state in byte 5.
        public override HidInfo PushCollection() { return Collections().Find(d => d.UsagePage == 0x01C0); }

        // byte 4 = 0x0C: link state, byte 5 = 1 headset connected (power-on), 0 lost (power-off) — seen 2026-11-11.
        public override bool ApplyPush(byte[] b, ref Reading r, out bool pollNow)
        {
            pollNow = false;
            if (b.Length < 6 || b[0] != 0x0D) return false;
            if (b[4] == 1 && b[5] <= 100) { r.Online = true; r.Percent = b[5]; return true; }
            if (b[4] == 10) { r.Online = true; r.Charging = charging = b[5] == 1 || b[5] == 2; return true; }
            if (b[4] == 0x0C && b[5] == 0) { r.Online = false; r.Charging = charging = false; chargeKnown = false; return true; }
            if (b[4] == 0x0C && b[5] == 1) { pollNow = true; return false; }
            return false;
        }

        // The headset pushes every charging change (0D/0A), so the poll asks only for the level;
        // charging is asked after the link comes up and, as a cross-check, every 10th poll.
        volatile bool charging, chargeKnown;
        int sinceChargeQuery;

        public override Reading Query()
        {
            var r = new Reading();
            var list = Collections().FindAll(d => d.OutputLength >= 64);

            foreach (var dev in list)
            {
                int bat = Ask(dev, 0x06);
                if (bat == -2) continue;                                        // collection refused the report, try next
                if (bat < 0 || bat > 100) { chargeKnown = false; return r; }   // no reply or no link to the headset
                r.Online = true;
                r.Percent = bat;
                if (!chargeKnown || ++sinceChargeQuery >= 10)
                {
                    int chg = Ask(dev, 0x48);
                    if (chg >= 0) { charging = chg == 1 || chg == 2; chargeKnown = true; sinceChargeQuery = 0; }
                }
                r.Charging = charging;
                return r;
            }
            return r;
        }

        static int Ask(HidInfo dev, byte cmd)
        {
            var req = new byte[] { 0x0C, 0x02, 0x03, 0x01, 0x00, cmd };
            bool written;
            var rep = Hid.Transact(dev, req, b => b[0] == 0x0C && b[5] == cmd, 1500, out written);
            if (!written) return -2;
            return rep == null ? -1 : rep[6];
        }
    }

    // ---------------------------------------------------------------- HID access

    sealed class HidInfo
    {
        public string Path;
        public ushort UsagePage, Usage;
        public int InputLength, OutputLength;
    }

    static class Hid
    {
        // Device interface list is enumerated once and kept until WM_DEVICECHANGE says the set changed.
        static readonly object cacheLock = new object();
        static Dictionary<uint, List<HidInfo>> cache = new Dictionary<uint, List<HidInfo>>();

        public static void Invalidate()
        {
            lock (cacheLock) cache = new Dictionary<uint, List<HidInfo>>();
        }

        public static List<HidInfo> Find(ushort vid, ushort pid)
        {
            uint key = ((uint)vid << 16) | pid;
            List<HidInfo> list;
            lock (cacheLock)
                if (cache.TryGetValue(key, out list)) return new List<HidInfo>(list);
            list = Enumerate(vid, pid);
            lock (cacheLock) cache[key] = list;
            return new List<HidInfo>(list);
        }

        static List<HidInfo> Enumerate(ushort vid, ushort pid)
        {
            var result = new List<HidInfo>();
            string tag = string.Format("vid_{0:x4}&pid_{1:x4}", vid, pid);
            Guid guid;
            Native.HidD_GetHidGuid(out guid);
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return result;
            try
            {
                var ifd = new Native.SP_DEVICE_INTERFACE_DATA();
                ifd.cbSize = Marshal.SizeOf(ifd);
                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifd); i++)
                {
                    string path = InterfacePath(set, ref ifd);
                    if (path == null || path.ToLowerInvariant().IndexOf(tag, StringComparison.Ordinal) < 0) continue;
                    var info = Describe(path);
                    if (info != null) result.Add(info);
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        static string InterfacePath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA ifd)
        {
            int size;
            Native.SetupDiGetDeviceInterfaceDetail(set, ref ifd, IntPtr.Zero, 0, out size, IntPtr.Zero);
            if (size <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref ifd, buf, size, out size, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(buf + 4);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static HidInfo Describe(string path)
        {
            // Zero access rights: enough to read the capabilities without disturbing the device.
            using (var h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                IntPtr pp;
                if (!Native.HidD_GetPreparsedData(h, out pp)) return null;
                try
                {
                    Native.HIDP_CAPS caps;
                    if (Native.HidP_GetCaps(pp, out caps) != Native.HIDP_STATUS_SUCCESS) return null;
                    return new HidInfo
                    {
                        Path = path, UsagePage = caps.UsagePage, Usage = caps.Usage,
                        InputLength = caps.InputReportByteLength, OutputLength = caps.OutputReportByteLength
                    };
                }
                finally { Native.HidD_FreePreparsedData(pp); }
            }
        }

        public static byte[] Transact(HidInfo dev, byte[] request, Func<byte[], bool> isReply, int timeoutMs)
        {
            bool written;
            return Transact(dev, request, isReply, timeoutMs, out written);
        }

        // Writes one output report and sleeps in WaitForSingleObject until the matching input report
        // arrives or the timeout passes. Plain overlapped I/O: no tasks, no thread-pool callbacks.
        public static byte[] Transact(HidInfo dev, byte[] request, Func<byte[], bool> isReply, int timeoutMs, out bool written)
        {
            written = false;
            if (dev.OutputLength < request.Length || dev.InputLength == 0) return null;
            using (var h = Native.CreateFile(dev.Path, Native.GENERIC_READ | Native.GENERIC_WRITE, 3, IntPtr.Zero, 3,
                                             Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero))
            using (var io = new OverlappedIo(h, Math.Max(dev.InputLength, dev.OutputLength)))
            {
                if (h.IsInvalid) return null;
                var outBuf = new byte[dev.OutputLength];
                Buffer.BlockCopy(request, 0, outBuf, 0, request.Length);
                if (io.Write(outBuf, timeoutMs) != outBuf.Length) return null;
                written = true;

                int deadline = Environment.TickCount + timeoutMs;
                var inBuf = new byte[dev.InputLength];
                int left;
                while ((left = deadline - Environment.TickCount) > 0)
                {
                    int n = io.Read(inBuf, left);
                    if (n < 0) break;
                    if (n > 0 && isReply(inBuf)) return inBuf;
                }
                return null;
            }
        }
    }

    // One OVERLAPPED + event + unmanaged buffer per open handle. A timed-out operation is cancelled
    // and awaited before the memory is released, so the driver never writes into freed memory.
    sealed class OverlappedIo : IDisposable
    {
        const int ERROR_IO_PENDING = 997;
        const uint WAIT_OBJECT_0 = 0;
        readonly SafeFileHandle h;
        readonly IntPtr ov, buf, evt;

        public OverlappedIo(SafeFileHandle h, int size)
        {
            this.h = h;
            int ovSize = IntPtr.Size * 2 + 8 + IntPtr.Size;      // Internal, InternalHigh, Offset, OffsetHigh, hEvent
            ov = Marshal.AllocHGlobal(ovSize);
            buf = Marshal.AllocHGlobal(size);
            evt = Native.CreateEvent(IntPtr.Zero, true, false, null);
        }

        IntPtr Begin()
        {
            int ovSize = IntPtr.Size * 2 + 8 + IntPtr.Size;
            for (int i = 0; i < ovSize; i += 4) Marshal.WriteInt32(ov, i, 0);
            Marshal.WriteIntPtr(ov, IntPtr.Size * 2 + 8, evt);
            return ov;
        }

        // Bytes transferred, or -1 on error/timeout.
        int Finish(bool started, int timeoutMs)
        {
            if (!started && Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return -1;
            if (Native.WaitForSingleObject(evt, (uint)timeoutMs) != WAIT_OBJECT_0)
            {
                Native.CancelIoEx(h, ov);
                uint dummy;
                Native.GetOverlappedResult(h, ov, out dummy, true);
                return -1;
            }
            uint n;
            return Native.GetOverlappedResult(h, ov, out n, false) ? (int)n : -1;
        }

        public int Write(byte[] data, int timeoutMs)
        {
            Marshal.Copy(data, 0, buf, data.Length);
            return Finish(Native.WriteFile(h, buf, (uint)data.Length, IntPtr.Zero, Begin()), timeoutMs);
        }

        public int Read(byte[] data, int timeoutMs)
        {
            int n = Finish(Native.ReadFile(h, buf, (uint)data.Length, IntPtr.Zero, Begin()), timeoutMs);
            if (n > 0) Marshal.Copy(buf, data, 0, Math.Min(n, data.Length));
            return n;
        }

        public void Dispose()
        {
            if (evt != IntPtr.Zero) Native.CloseHandle(evt);
            Marshal.FreeHGlobal(buf);
            Marshal.FreeHGlobal(ov);
        }
    }

    // Keeps one overlapped read pending on the device's push collection. The thread sleeps in the
    // kernel until the device sends a report itself (e.g. the headset on charger plug/unplug) — zero
    // CPU while nothing happens. Rescan() re-opens after the USB device set changed.
    sealed class PushListener
    {
        readonly Gadget gadget;
        readonly Action<byte[]> onReport;
        readonly AutoResetEvent rescan = new AutoResetEvent(true);
        readonly object handleLock = new object();
        SafeFileHandle current;
        volatile bool stop;

        public PushListener(Gadget gadget, Action<byte[]> onReport)
        {
            this.gadget = gadget;
            this.onReport = onReport;
            new Thread(Run) { IsBackground = true, Name = "push " + gadget.Name }.Start();
        }

        public void Rescan()
        {
            lock (handleLock)
                if (current != null && !current.IsClosed) Native.CancelIoEx(current, IntPtr.Zero);
            rescan.Set();
        }

        public void Stop() { stop = true; Rescan(); }

        void Run()
        {
            while (true)
            {
                rescan.WaitOne();
                if (stop) return;
                HidInfo dev;
                try { dev = gadget.PushCollection(); } catch (Exception) { dev = null; }
                if (dev == null || dev.InputLength == 0) continue;     // absent: wait for the next device change

                using (var h = Native.CreateFile(dev.Path, Native.GENERIC_READ | Native.GENERIC_WRITE, 3, IntPtr.Zero, 3,
                                                 Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero))
                using (var io = new OverlappedIo(h, dev.InputLength))
                {
                    if (h.IsInvalid) continue;
                    lock (handleLock) current = h;
                    var buf = new byte[dev.InputLength];
                    while (!stop)
                    {
                        int n = io.Read(buf, -1);                    // INFINITE; CancelIoEx ends it
                        if (n < 0) break;                            // unplugged or rescan requested
                        if (n > 0) onReport((byte[])buf.Clone());
                    }
                    lock (handleLock) current = null;
                }
            }
        }
    }

    // ---------------------------------------------------------------- rendering

    static class Painter
    {
        static readonly FontFamily IconFont = PickFont();

        static FontFamily PickFont()
        {
            foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                try { var f = new FontFamily(name); if (f.Name == name) return f; } catch (ArgumentException) { }
            }
            return FontFamily.GenericSansSerif;
        }

        // Glyph outline scaled to fit the box, centred.
        static GraphicsPath GlyphPath(char glyph, RectangleF box)
        {
            var path = new GraphicsPath();
            path.AddString(glyph.ToString(), IconFont, 0, 100f, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            float scale = Math.Min(box.Width / b.Width, box.Height / b.Height);
            using (var m = new Matrix())
            {
                m.Translate(box.X + (box.Width - b.Width * scale) / 2f, box.Y + (box.Height - b.Height * scale) / 2f);
                m.Scale(scale, scale);
                m.Translate(-b.X, -b.Y);
                path.Transform(m);
            }
            return path;
        }

        // Solid six-point lightning bolt filling the box.
        static GraphicsPath BoltPath(RectangleF box)
        {
            float[] pts = { 0.66f, 0f, 0.05f, 0.60f, 0.45f, 0.60f, 0.32f, 1f, 0.95f, 0.38f, 0.55f, 0.38f };
            var p = new PointF[pts.Length / 2];
            for (int i = 0; i < p.Length; i++)
                p[i] = new PointF(box.X + pts[i * 2] * box.Width, box.Y + pts[i * 2 + 1] * box.Height);
            var path = new GraphicsPath();
            path.AddPolygon(p);
            return path;
        }

        // Layout at 100 % scale (taskbar 48 px high); everything is multiplied by height / 48.
        public const float CellW = 34, Spacing = 8, Margin = 4;

        public static int WidgetWidth(int count, float s)
        {
            return (int)Math.Ceiling((count * CellW + (count - 1) * Spacing + Margin) * s);
        }

        public static RectangleF CellRect(int i, int height, float s)
        {
            return new RectangleF(i * (CellW + Spacing) * s, 4 * s, CellW * s, height - 8 * s);
        }

        // Default 0-20 % one red dot, 21-40 % two orange, 41-70 % three green, 71-100 % four green
        // (bounds are settings); offline: four grey.
        public static int Dots(Reading r)
        {
            if (!r.Online && !r.Asleep) return 0;
            int[] t = Settings.Thresholds;
            return r.Percent <= t[0] ? 1 : r.Percent <= t[1] ? 2 : r.Percent <= t[2] ? 3 : 4;
        }

        public static void DrawCell(Graphics g, RectangleF cell, char glyph, Reading r, bool light, int style, bool hover, float s)
        {
            Color fg = light ? Color.FromArgb(28, 28, 28) : Color.White;
            Color dim = Color.FromArgb(light ? 70 : 85, fg);

            if (hover)
            {
                using (var hp = RoundRect(cell, 4 * s))
                using (var hb = new SolidBrush(light ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255)))
                    g.FillPath(hb, hp);
            }

            float glyphH = 20 * s, dot = 5 * s, gap = 3 * s, under = 5 * s;
            float top = cell.Y + (cell.Height - glyphH - under - dot) / 2f;

            RectangleF gb;
            using (var path = GlyphPath(glyph, new RectangleF(cell.X, top, cell.Width, glyphH)))
            using (var br = new SolidBrush(r.Online || r.Asleep ? fg : dim))
            {
                g.FillPath(br, path);
                gb = path.GetBounds();
            }

            // Charging: a bolt on the glyph's top-right corner, cut out of the glyph so it stays legible.
            if ((r.Online || r.Asleep) && r.Charging)
            {
                float bh = glyphH * 0.62f, bw = bh * 0.8f;
                var box = new RectangleF(gb.Right - bw * 0.45f, gb.Top - bh * 0.2f, bw, bh);
                using (var bolt = BoltPath(box))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    using (var cut = new Pen(Color.FromArgb(1, 0, 0, 0), 2.5f * s) { LineJoin = LineJoin.Round })
                        g.DrawPath(cut, bolt);
                    g.CompositingMode = CompositingMode.SourceOver;
                    using (var br = new SolidBrush(style != Settings.Coloured ? fg : light ? Color.FromArgb(157, 93, 0) : Color.FromArgb(255, 200, 61)))
                        g.FillPath(br, bolt);
                }
            }

            int lit = Dots(r);
            Color red = light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 72, 72);
            Color on = style == Settings.Mono ? fg
                     : style == Settings.MonoLowRed ? (lit <= 1 ? red : fg)
                     : lit <= 1 ? red
                     : lit == 2 ? (light ? Color.FromArgb(202, 80, 16) : Color.FromArgb(255, 140, 0))
                     : (light ? Color.FromArgb(16, 124, 16) : Color.FromArgb(46, 204, 64));
            float rowW = dot * 4 + gap * 3;
            float x0 = cell.X + (cell.Width - rowW) / 2f, y = top + glyphH + under;
            using (var onBr = new SolidBrush(on))
            using (var offBr = new SolidBrush(dim))
                for (int i = 0; i < 4; i++)
                    g.FillEllipse(i < lit ? onBr : offBr, x0 + i * (dot + gap), y, dot, dot);
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ---------------------------------------------------------------- taskbar widget

    sealed class Slot
    {
        public Gadget Gadget;
        public Reading Last;
        public bool HasValue;
        public DateTime LastSeen;
        public int Known;                 // percent from the last real answer (Last.Percent may be an estimate)
        public PushListener Listener;

        // A mouse asleep on the charger reports nothing until it is moved, so its level is counted here:
        // Settings.ChargeRate % a minute (default 2, 0 = off) from the last real answer, up to 100.
        // Only when it fell asleep while charging; the first real answer replaces the estimate.
        bool Estimating { get { return Last.Asleep && Last.Charging && Settings.ChargeRate > 0; } }

        int Estimate()
        {
            double minutes = Math.Max(0, (DateTime.Now - LastSeen).TotalMinutes);
            return (int)Math.Min(100, Known + minutes * Settings.ChargeRate);
        }

        // Merges a reading in; true if what is drawn changed.
        public bool Update(Reading r)
        {
            // Keep the last known level while the device is out of reach (asleep: shown normally; off: dimmed).
            if (!r.Online && HasValue) r.Percent = Known;
            // A charging mouse dozes off every few seconds between its own reports; asleep keeps the last
            // charging flag until an answer says otherwise (picking the mouse up wakes it and clears it).
            if (r.Asleep && HasValue) r.Charging = Last.Charging;
            if (r.Asleep && !HasValue) r.Asleep = false;     // nothing known yet: nothing to show but "no link"
            if (r.Online) { LastSeen = DateTime.Now; Known = r.Percent; }
            if (r.Asleep && r.Charging && Settings.ChargeRate > 0) r.Percent = Estimate();
            bool changed = !HasValue || r.Online != Last.Online || r.Asleep != Last.Asleep ||
                           r.Percent != Last.Percent || r.Charging != Last.Charging;
            // The registry is written only when the level or charging state moves, or when the device
            // goes quiet (asleep / off) — that is the moment whose time the tooltip shows — not on every poll.
            bool levelMoved = r.Online && (!HasValue || r.Percent != Last.Percent || r.Charging != Last.Charging);
            bool wentQuiet = HasValue && Last.Online && !r.Online;
            Last = r;
            HasValue = HasValue || r.Online;
            if (levelMoved || wentQuiet) Save();
            return changed;
        }

        // Last known level survives a restart: a mouse asleep at sign-in still shows its charge.
        public void Load()
        {
            int percent; long ticks;
            if (!Settings.LoadLast(Gadget.Name, out percent, out ticks)) return;
            Last = new Reading { Percent = percent };
            Known = percent;
            LastSeen = new DateTime(ticks);
            HasValue = true;
        }

        public void Save()
        {
            if (HasValue) Settings.SaveLast(Gadget.Name, Known, LastSeen.Ticks);
        }

        // Context menu: just "<device> | 99%" ("≈" while estimating, "—" when nothing is known yet).
        public string MenuLine()
        {
            return Gadget.Name + " | " + (HasValue ? (Estimating ? "≈" : "") + Last.Percent + "%" : "—");
        }

        public string Status(string sep)
        {
            if (Last.Online)
                return Gadget.Name + sep + Last.Percent + "%" + (Last.Charging ? ", charging" : "");
            if (!HasValue) return Gadget.Name + sep + "no link";
            string when = LastSeen.Date == DateTime.Today ? LastSeen.ToString("HH:mm")
                        : LastSeen.Date == DateTime.Today.AddDays(-1) ? "yesterday " + LastSeen.ToString("HH:mm")
                        : LastSeen.ToString("dd.MM HH:mm");
            if (Estimating)
                return Gadget.Name + sep + "≈" + Last.Percent + "%, charging (asleep; " + Known + "% at " + when +
                       ", counting +" + Settings.ChargeRate + "%/min)";
            if (Last.Asleep)
                return Gadget.Name + sep + Last.Percent + "%" + (Last.Charging ? ", charging" : "") + " (asleep, as of " + when + ")";
            return Gadget.Name + sep + "no link since " + when + sep + "last charge " + Known + "%";
        }
    }

    // Minimal Win32 window: one registered class (arrow cursor) shared by the host and the widget,
    // a static window procedure that routes messages to the owning object.
    abstract class Wnd
    {
        const string ClassName = "TrayBattery.Wnd";
        const int WM_NCDESTROY = 0x0082;
        static readonly Ui.WndProc Proc = StaticProc;            // kept alive for the class's lifetime
        static readonly Dictionary<IntPtr, Wnd> Map = new Dictionary<IntPtr, Wnd>();
        static Wnd creating;
        static bool registered;

        public IntPtr Handle { get; private set; }

        protected bool Create(int exStyle, int style, IntPtr parent, int width, int height)
        {
            if (!registered)
            {
                var wc = new Ui.WNDCLASSEX
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                    hInstance = Ui.GetModuleHandle(null),
                    hCursor = Ui.LoadCursor(IntPtr.Zero, new IntPtr(32512) /* IDC_ARROW */),
                    lpszClassName = ClassName
                };
                wc.cbSize = Marshal.SizeOf(wc);
                registered = Ui.RegisterClassEx(ref wc) != 0;
            }
            creating = this;
            Ui.CreateWindowEx(exStyle, ClassName, "TrayBattery", style, 0, 0, width, height, parent,
                              IntPtr.Zero, Ui.GetModuleHandle(null), IntPtr.Zero);
            creating = null;
            return Handle != IntPtr.Zero;
        }

        public void Destroy() { if (Handle != IntPtr.Zero) Ui.DestroyWindow(Handle); }

        static IntPtr StaticProc(IntPtr h, int msg, IntPtr w, IntPtr l)
        {
            Wnd wnd;
            if (!Map.TryGetValue(h, out wnd))
            {
                if (creating == null) return Ui.DefWindowProc(h, msg, w, l);
                wnd = creating;
                wnd.Handle = h;
                Map[h] = wnd;
            }
            if (msg == WM_NCDESTROY)
            {
                Map.Remove(h);
                wnd.Handle = IntPtr.Zero;
                wnd.OnDestroyed();
                return Ui.DefWindowProc(h, msg, w, l);
            }
            IntPtr result;
            return wnd.OnMessage(msg, w, l, out result) ? result : Ui.DefWindowProc(h, msg, w, l);
        }

        // true = handled, result returned as is; false = DefWindowProc.
        protected abstract bool OnMessage(int msg, IntPtr w, IntPtr l, out IntPtr result);
        protected virtual void OnDestroyed() { }
    }

    // A layered child window of the taskbar (Shell_TrayWnd), placed just left of the notification area.
    // Being a child, it shows and hides with the taskbar (full-screen games, auto-hide) and needs no
    // z-order fights. It follows the tray through a location-change WinEvent from explorer: no timers.
    sealed class TaskbarWidget : Wnd
    {
        const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MOUSELEAVE = 0x02A3;
        const int WM_SETCURSOR = 0x0020;

        readonly List<Slot> slots;
        readonly Action onLeftClick, onRightClick;
        readonly Action<int> onHover;
        IntPtr taskbar, tray, hook, tooltip;
        Ui.WinEventProc hookProc;
        int x, w, h, hover = -1;
        bool tracking;

        public TaskbarWidget(List<Slot> slots, Action onLeftClick, Action onRightClick, Action<int> onHover)
        {
            this.slots = slots;
            this.onLeftClick = onLeftClick;
            this.onRightClick = onRightClick;
            this.onHover = onHover;
        }

        public bool Attach()
        {
            Detach();
            taskbar = Ui.FindWindow("Shell_TrayWnd", null);
            tray = taskbar == IntPtr.Zero ? IntPtr.Zero : Ui.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero) return false;
            if (!Create(Ui.WS_EX_LAYERED, Ui.WS_CHILD | Ui.WS_CLIPSIBLINGS, taskbar, 1, 1)) return false;

            tooltip = Ui.CreateTooltip(Handle, slots.Count);
            Layout(true);

            // Only the taskbar's own thread: File Explorer windows and other explorer UI never wake us.
            uint pid, trayPid;
            uint tid = Ui.GetWindowThreadProcessId(taskbar, out pid);
            uint trayTid = Ui.GetWindowThreadProcessId(tray, out trayPid);
            hookProc = OnWinEvent;
            hook = Ui.SetWinEventHook(Ui.EVENT_OBJECT_LOCATIONCHANGE, Ui.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
                                      hookProc, pid, tid == trayTid ? tid : 0, Ui.WINEVENT_OUTOFCONTEXT);
            return true;
        }

        public void Detach()
        {
            if (tooltip != IntPtr.Zero) { Ui.DestroyWindow(tooltip); tooltip = IntPtr.Zero; }
            Destroy();
            Unhook();
        }

        void Unhook()
        {
            if (hook != IntPtr.Zero) { Ui.UnhookWinEvent(hook); hook = IntPtr.Zero; }
            w = h = 0;
        }

        // Explorer went away and took its children with it; TaskbarCreated will re-attach.
        protected override void OnDestroyed()
        {
            tooltip = IntPtr.Zero;
            Unhook();
        }

        void OnWinEvent(IntPtr hWinEventHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject == 0 && (hwnd == tray || hwnd == taskbar)) Layout(false);
        }

        float Scale { get { return h / 48f; } }

        // Re-reads the taskbar and tray rectangles; moves the widget, redraws only when the size changed.
        public void Layout(bool forcePaint)
        {
            if (Handle == IntPtr.Zero) return;
            Ui.RECT tb, tn;
            if (!Ui.GetWindowRect(taskbar, out tb) || !Ui.GetWindowRect(tray, out tn)) return;
            int nh = tb.Bottom - tb.Top;
            int nw = Painter.WidgetWidth(slots.Count, nh / 48f);
            int nx = tn.Left - tb.Left - nw;
            bool resized = nw != w || nh != h;
            if (!resized && nx == x && !forcePaint) return;
            x = nx; w = nw; h = nh;
            Ui.SetWindowPos(Handle, IntPtr.Zero, x, 0, w, h, Ui.SWP_NOACTIVATE | Ui.SWP_SHOWWINDOW);
            if (resized || forcePaint) Paint();
        }

        public void Paint()
        {
            if (Handle == IntPtr.Zero || w == 0 || h == 0) return;
            float s = Scale;
            bool light = Theme.LightTaskbar();
            int style = Settings.DotStyle;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // Alpha 1 everywhere: invisible, but keeps the whole widget clickable.
                    g.Clear(Color.FromArgb(1, 0, 0, 0));
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var cell = Painter.CellRect(i, h, s);
                        Painter.DrawCell(g, cell, slots[i].Gadget.Glyph, slots[i].Last, light, style, i == hover, s);
                        Ui.SetTooltip(tooltip, Handle, i, Rectangle.Round(cell), slots[i].Status("\r\n"));
                    }
                }
                Ui.PushLayered(Handle, bmp);
            }
        }

        int HitCell(int px)
        {
            float s = Scale;
            for (int i = 0; i < slots.Count; i++)
                if (Painter.CellRect(i, h, s).Contains(px, h / 2f)) return i;
            return -1;
        }

        protected override bool OnMessage(int msg, IntPtr wp, IntPtr lp, out IntPtr result)
        {
            result = IntPtr.Zero;
            switch (msg)
            {
                case WM_MOUSEMOVE:
                    if (!tracking) { Ui.TrackLeave(Handle); tracking = true; }
                    int c = HitCell((short)(lp.ToInt64() & 0xFFFF));
                    if (c != hover)
                    {
                        hover = c;
                        Paint();
                        if (c >= 0) onHover(c);
                    }
                    return true;
                case WM_MOUSELEAVE:
                    tracking = false;
                    if (hover != -1) { hover = -1; Paint(); }
                    return true;
                case WM_SETCURSOR:
                    // Answer here: DefWindowProc would first ask the parent — explorer's taskbar.
                    Ui.SetCursor(Ui.LoadCursor(IntPtr.Zero, new IntPtr(32512) /* IDC_ARROW */));
                    result = new IntPtr(1);
                    return true;
                case WM_LBUTTONUP:
                    onLeftClick();
                    return true;
                case WM_RBUTTONUP:
                    onRightClick();
                    return true;
            }
            return false;
        }
    }

    static class Settings
    {
        const string Key = @"Software\TrayBattery";

        // Dot colours, registry value "Monochrome" (kept from v0.3.0, where 1 meant monochrome):
        // 0 coloured (red/orange/green, yellow bolt), 1 monochrome (dots and bolt in the glyph's colour),
        // 2 monochrome but the single low-battery dot red. Read once; only this app changes it.
        public const int Coloured = 0, Mono = 1, MonoLowRed = 2;
        static int? dotStyle;

        public static int DotStyle
        {
            get
            {
                if (dotStyle == null)
                    using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    {
                        object v = k == null ? null : k.GetValue("Monochrome");
                        dotStyle = v is int && (int)v >= Coloured && (int)v <= MonoLowRed ? (int)v : Coloured;
                    }
                return dotStyle.Value;
            }
            set
            {
                dotStyle = value;
                using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Monochrome", value);
            }
        }

        // "Advanced" section (all cached; only this app changes them).
        public const int DefaultPollSeconds = 60, DefaultChargeRate = 2;
        public static readonly int[] DefaultThresholds = { 20, 40, 70 };
        static int? pollSeconds, chargeRate;
        static int[] thresholds;
        static bool? advanced;

        public static bool ValidPoll(int v) { return v >= 10 && v <= 3600; }
        public static bool ValidRate(int v) { return v >= 0 && v <= 50; }
        public static bool ValidThresholds(int[] t) { return t.Length == 3 && t[0] > 0 && t[0] < t[1] && t[1] < t[2] && t[2] < 100; }

        public static int PollSeconds
        {
            get { if (pollSeconds == null) pollSeconds = ReadInt("PollSeconds", DefaultPollSeconds, ValidPoll); return pollSeconds.Value; }
            set { pollSeconds = value; Write("PollSeconds", value); }
        }

        public static int ChargeRate
        {
            get { if (chargeRate == null) chargeRate = ReadInt("ChargeRate", DefaultChargeRate, ValidRate); return chargeRate.Value; }
            set { chargeRate = value; Write("ChargeRate", value); }
        }

        // "Thresholds" = "20|40|70": upper bounds of one, two and three dots.
        public static int[] Thresholds
        {
            get
            {
                if (thresholds == null)
                {
                    thresholds = DefaultThresholds;
                    var v = Read("Thresholds") as string;
                    if (v != null)
                    {
                        var parts = v.Split('|');
                        var t = new int[parts.Length];
                        bool ok = true;
                        for (int i = 0; i < parts.Length; i++) ok &= int.TryParse(parts[i], out t[i]);
                        if (ok && ValidThresholds(t)) thresholds = t;
                    }
                }
                return thresholds;
            }
            set { thresholds = value; Write("Thresholds", value[0] + "|" + value[1] + "|" + value[2]); }
        }

        public static bool Advanced
        {
            get { if (advanced == null) advanced = ReadInt("Advanced", 0, v => v == 0 || v == 1) == 1; return advanced.Value; }
            set { advanced = value; Write("Advanced", value ? 1 : 0); }
        }

        static object Read(string name)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
                return k == null ? null : k.GetValue(name);
        }

        static int ReadInt(string name, int def, Func<int, bool> valid)
        {
            object v = Read(name);
            return v is int && valid((int)v) ? (int)v : def;
        }

        static void Write(string name, object value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value); }
            catch (Exception) { }
        }

        // Icon order on the taskbar, left to right: "Order" = device names joined by '|'.
        // Devices missing from it (added in a later version) go after the listed ones.
        public static void SortByOrder(List<Slot> slots)
        {
            var v = Read("Order") as string;
            if (v == null) return;
            var names = new List<string>(v.Split('|'));
            var original = new List<Slot>(slots);
            slots.Sort((a, b) =>
            {
                int ia = names.IndexOf(a.Gadget.Name), ib = names.IndexOf(b.Gadget.Name);
                if (ia < 0) ia = names.Count + original.IndexOf(a);
                if (ib < 0) ib = names.Count + original.IndexOf(b);
                return ia.CompareTo(ib);
            });
        }

        public static void SaveOrder(List<Slot> slots)
        {
            Write("Order", string.Join("|", slots.ConvertAll(s => s.Gadget.Name)));
        }

        // HKCU\Software\TrayBattery\Last: "<device name>" = "<percent>|<last contact, DateTime ticks>".
        public static bool LoadLast(string device, out int percent, out long ticks)
        {
            percent = 0; ticks = 0;
            using (var k = Registry.CurrentUser.OpenSubKey(Key + @"\Last"))
            {
                var v = k == null ? null : k.GetValue(device) as string;
                if (v == null) return false;
                var parts = v.Split('|');
                return parts.Length == 2 && int.TryParse(parts[0], out percent) && percent >= 0 && percent <= 100 &&
                       long.TryParse(parts[1], out ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks;
            }
        }

        public static void SaveLast(string device, int percent, long ticks)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key + @"\Last"))
                    k.SetValue(device, percent + "|" + ticks);
            }
            catch (Exception) { }
        }
    }

    static class Theme
    {
        // Read once and again only after WM_SETTINGCHANGE "ImmersiveColorSet" (Invalidate).
        static bool? light;

        public static void Invalidate() { light = null; }

        public static bool LightTaskbar()
        {
            if (light == null)
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    light = v is int && (int)v == 1;
                }
            return light.Value;
        }
    }

    // ---------------------------------------------------------------- app

    // Invisible top-level window (WS_POPUP, never shown) that owns the app's logic. Being top-level it
    // gets the broadcasts the app reacts to — no SystemEvents thread, no WinForms:
    //   WM_DEVICECHANGE   a dongle came or went -> re-read at once
    //   TaskbarCreated    explorer restarted -> re-attach the widget
    //   WM_POWERBROADCAST sleep / resume, WM_WTSSESSION_CHANGE lock / unlock -> stop / restart polling
    //   WM_SETTINGCHANGE  "ImmersiveColorSet" -> light/dark taskbar changed
    //   WM_ENDSESSION     shutdown / sign-out -> save the time of the last contact
    sealed class App : Wnd
    {
        // Plug-in, wake-up, unlock, a click and the devices' own push reports refresh immediately anyway.
        const uint PollToleranceMs = 5000;   // the poll interval itself is a setting (default 60 s)
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "TrayBattery";

        const int WM_APP_INVOKE = 0x8001, WM_TIMER = 0x0113, WM_DEVICECHANGE = 0x0219, WM_POWERBROADCAST = 0x0218;
        const int WM_WTSSESSION_CHANGE = 0x02B1, WM_SETTINGCHANGE = 0x001A, WM_ENDSESSION = 0x0016;
        const int DBT_DEVNODES_CHANGED = 0x0007, PBT_APMSUSPEND = 0x4, PBT_APMRESUMEAUTOMATIC = 0x12;
        const int WTS_SESSION_LOCK = 0x7, WTS_SESSION_UNLOCK = 0x8;
        static readonly IntPtr PollTimer = new IntPtr(1), KickTimer = new IntPtr(2);
        static readonly int WM_TASKBARCREATED = Ui.RegisterWindowMessage("TaskbarCreated");

        readonly List<Slot> all = new List<Slot>();      // fixed: the poll thread's order
        readonly List<Slot> slots;                       // taskbar order, UI thread only (settings reorder it)
        readonly TaskbarWidget widget;
        SettingsWindow settings;
        readonly Queue<Action> invokes = new Queue<Action>();
        readonly AutoResetEvent pollRequest = new AutoResetEvent(false);
        volatile bool exiting;
        bool paused;

        public App()
        {
            Create(Ui.WS_EX_TOOLWINDOW, Ui.WS_POPUP, IntPtr.Zero, 0, 0);
            Ui.WTSRegisterSessionNotification(Handle, 0 /* NOTIFY_FOR_THIS_SESSION */);

            all.Add(new Slot { Gadget = new AtkMouse() });
            all.Add(new Slot { Gadget = new HyperXCloud3S() });
            slots = new List<Slot>(all);
            Settings.SortByOrder(slots);
            foreach (var slot in all)
            {
                var s = slot;
                s.Load();
                s.Listener = new PushListener(s.Gadget, rep => OnPush(s, rep));
            }

            widget = new TaskbarWidget(slots, Poll, ShowMenu, OnHover);
            // At sign-in the taskbar may not exist yet; TaskbarCreated attaches the widget then.
            widget.Attach();

            StartPollTimer();
            new Thread(PollLoop) { IsBackground = true, Name = "poll" }.Start();
            Poll();
        }

        // Coalescable: Windows may shift the tick by up to 5 s to batch it with other wake-ups.
        void StartPollTimer() { Ui.SetCoalescableTimer(Handle, PollTimer, (uint)Settings.PollSeconds * 1000, IntPtr.Zero, PollToleranceMs); }

        // Runs an action on the UI thread (from the poll / listener threads).
        void BeginInvoke(Action a)
        {
            lock (invokes) invokes.Enqueue(a);
            Ui.PostMessage(Handle, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
        }

        protected override bool OnMessage(int msg, IntPtr wp, IntPtr lp, out IntPtr result)
        {
            result = IntPtr.Zero;
            switch (msg)
            {
                case WM_APP_INVOKE:
                    while (true)
                    {
                        Action a;
                        lock (invokes) { if (invokes.Count == 0) break; a = invokes.Dequeue(); }
                        a();
                    }
                    return true;
                case WM_TIMER:
                    if (wp == KickTimer) Ui.KillTimer(Handle, KickTimer);
                    Poll();
                    return true;
                case WM_DEVICECHANGE:
                    if (wp.ToInt32() == DBT_DEVNODES_CHANGED) OnDevicesChanged();
                    result = new IntPtr(1);
                    return true;
                case WM_POWERBROADCAST:
                    if (wp.ToInt32() == PBT_APMSUSPEND) Pause(true);
                    else if (wp.ToInt32() == PBT_APMRESUMEAUTOMATIC) { Hid.Invalidate(); Pause(false); }
                    result = new IntPtr(1);
                    return true;
                case WM_WTSSESSION_CHANGE:
                    if (wp.ToInt32() == WTS_SESSION_LOCK) Pause(true);
                    else if (wp.ToInt32() == WTS_SESSION_UNLOCK) Pause(false);
                    return true;
                case WM_SETTINGCHANGE:
                    if (lp != IntPtr.Zero && Marshal.PtrToStringUni(lp) == "ImmersiveColorSet") { Theme.Invalidate(); widget.Paint(); }
                    return false;
                case WM_ENDSESSION:
                    if (wp != IntPtr.Zero) foreach (var s in slots) s.Save();
                    return true;
            }
            if (msg == WM_TASKBARCREATED) { widget.Attach(); return true; }
            return false;
        }

        void ShowMenu()
        {
            const uint MF_STRING = 0, MF_GRAYED = 0x1, MF_SEPARATOR = 0x800;
            const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;
            const int CmdRefresh = 1, CmdSettings = 2, CmdExit = 4;

            IntPtr m = Ui.CreatePopupMenu();
            foreach (var s in slots) Ui.AppendMenu(m, MF_STRING | MF_GRAYED, IntPtr.Zero, s.MenuLine());
            Ui.AppendMenu(m, MF_SEPARATOR, IntPtr.Zero, null);
            Ui.AppendMenu(m, MF_STRING, new IntPtr(CmdRefresh), "Refresh");
            Ui.AppendMenu(m, MF_STRING, new IntPtr(CmdSettings), "Settings…");
            Ui.AppendMenu(m, MF_SEPARATOR, IntPtr.Zero, null);
            Ui.AppendMenu(m, MF_STRING | MF_GRAYED, IntPtr.Zero, "TrayBattery " + Version);
            Ui.AppendMenu(m, MF_STRING, new IntPtr(CmdExit), "Exit");

            Ui.POINT pt;
            Ui.GetCursorPos(out pt);
            // A popup menu closes on an outside click only if its owner is the foreground window.
            Ui.SetForegroundWindow(Handle);
            int cmd = Ui.TrackPopupMenuEx(m, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, Handle, IntPtr.Zero);
            Ui.PostMessage(Handle, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);
            Ui.DestroyMenu(m);

            switch (cmd)
            {
                case CmdRefresh: Poll(); break;
                case CmdSettings: ShowSettings(pt); break;
                case CmdExit: Exit(); break;
            }
        }

        // Stamped by build.ps1 from the git tag (or git describe for local builds).
        static string Version
        {
            get
            {
                var a = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    typeof(App).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                return a == null ? "dev" : a.InformationalVersion;
            }
        }

        // Created on demand and destroyed on close: costs nothing while it is not open.
        void ShowSettings(Ui.POINT near)
        {
            if (settings == null || settings.Handle == IntPtr.Zero)
                settings = new SettingsWindow(slots, OnSettingsChanged);
            settings.Show(near);
        }

        void OnSettingsChanged(bool pollChanged)
        {
            if (pollChanged && !paused) StartPollTimer();
            // Thresholds / charge rate change what is drawn: recompute (estimate) and repaint.
            foreach (var s in all) if (s.HasValue) s.Update(s.Last);
            widget.Layout(true);
        }

        // Hovering over a device that looks asleep or off: someone is moving a mouse right now, so the
        // shown state is likely stale (a mouse wakes on movement but says nothing). Re-read at once,
        // at most every 5 s; the tooltip text follows when the result is painted.
        int lastHoverPoll = Environment.TickCount - 10000;

        void OnHover(int index)
        {
            if (slots[index].Last.Online || Environment.TickCount - lastHoverPoll < 5000) return;
            lastHoverPoll = Environment.TickCount;
            Poll();
        }

        void OnDevicesChanged()
        {
            Hid.Invalidate();
            foreach (var s in slots) s.Listener.Rescan();
            Kick(2000);
        }

        // No polling while the session is locked or the PC sleeps: nobody looks at the taskbar then.
        void Pause(bool on)
        {
            paused = on;
            Ui.KillTimer(Handle, KickTimer);
            if (on) Ui.KillTimer(Handle, PollTimer);
            else { StartPollTimer(); Kick(3000); }
        }

        void Kick(uint delayMs)
        {
            if (!paused) Ui.SetTimer(Handle, KickTimer, delayMs, IntPtr.Zero);
        }

        // Requests made while a poll runs collapse into one more poll.
        void Poll() { pollRequest.Set(); }

        // One long-lived thread, asleep on an event between polls; devices are read one after another,
        // since every wait sleeps in the kernel anyway.
        void PollLoop()
        {
            var gadgets = all.ConvertAll(s => s.Gadget);
            while (true)
            {
                pollRequest.WaitOne();
                if (exiting) return;
                var res = new Reading[gadgets.Count];
                for (int i = 0; i < gadgets.Count; i++)
                {
                    try { res[i] = gadgets[i].Query(); } catch (Exception) { res[i] = new Reading(); }
                }
                BeginInvoke(() => Apply(res));
            }
        }

        void Apply(Reading[] res)
        {
            bool changed = false;
            for (int i = 0; i < all.Count && i < res.Length; i++) changed |= all[i].Update(res[i]);
            if (changed) widget.Paint();
        }

        void OnPush(Slot s, byte[] report)
        {
            BeginInvoke(() =>
            {
                var r = s.Last;
                bool pollNow;
                if (s.Gadget.ApplyPush(report, ref r, out pollNow) && s.Update(r)) widget.Paint();
                // The link just came up (headset power-on, mouse woke / charger plugged or pulled): re-read.
                // Kick restarts one timer, so a burst of 2-3 reports gives a single poll once it is over.
                if (pollNow) Kick(s.Gadget.PushPollDelayMs);
            });
        }

        public static bool IsAutorun()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(AppName) != null;
        }

        public static void SetAutorun(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\"");
                else k.DeleteValue(AppName, false);
            }
        }

        void Exit()
        {
            foreach (var s in slots) s.Save();
            Ui.KillTimer(Handle, PollTimer);
            Ui.KillTimer(Handle, KickTimer);
            widget.Detach();
            if (settings != null) settings.Destroy();
            foreach (var s in all) s.Listener.Stop();
            exiting = true;
            pollRequest.Set();
            Ui.WTSUnRegisterSessionNotification(Handle);
            Destroy();
            Ui.PostQuitMessage(0);
        }
    }

    // ---------------------------------------------------------------- settings window

    // Plain Win32 controls on a top-level window, created when opened and destroyed when closed.
    // Every change applies at once (no OK / Cancel); a number field applies as soon as it holds a valid value.
    sealed class SettingsWindow : Wnd
    {
        const int WM_COMMAND = 0x0111, WM_ERASEBKGND = 0x0014, WM_CTLCOLORBTN = 0x0135, WM_CTLCOLORSTATIC = 0x0138;
        const int WM_SETFONT = 0x0030, BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1, EM_SETLIMITTEXT = 0x00C5;
        const int LB_ADDSTRING = 0x0180, LB_RESETCONTENT = 0x0184, LB_SETCURSEL = 0x0186, LB_GETCURSEL = 0x0188;
        const int WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000, WS_GROUP = 0x00020000, WS_VSCROLL = 0x00200000;
        const int WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_EX_CLIENTEDGE = 0x200;
        const int BS_DEFPUSHBUTTON = 1, BS_AUTOCHECKBOX = 3, BS_GROUPBOX = 7, BS_AUTORADIOBUTTON = 9;
        const int LBS_NOTIFY = 1, LBS_NOINTEGRALHEIGHT = 0x100, LBN_SELCHANGE = 1;
        const int ES_CENTER = 1, ES_NUMBER = 0x2000, EN_CHANGE = 0x0300;
        const int IdClose = 1, IdCancel = 2, IdStyle = 100, IdList = 200, IdUp = 201, IdDown = 202;
        const int IdAutorun = 300, IdAdvanced = 301, IdPoll = 400, IdT1 = 401, IdT2 = 402, IdT3 = 403, IdRate = 404, IdDefaults = 405;
        const int Style = WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        const int ClientW = 320, BasicH = 342, AdvancedH = 456;   // at 96 dpi

        public static IntPtr Open { get; private set; }

        readonly List<Slot> slots;
        readonly Action<bool> changed;                 // true: the poll interval changed
        readonly List<IntPtr> advanced = new List<IntPtr>();
        IntPtr font, list, up, down, close;
        int dpi = 96;
        bool loading;

        public SettingsWindow(List<Slot> slots, Action<bool> changed)
        {
            this.slots = slots;
            this.changed = changed;
        }

        int Px(int v) { return v * dpi / 96; }

        public void Show(Ui.POINT near)
        {
            if (Handle != IntPtr.Zero) { Ui.ShowWindow(Handle, 9 /* SW_RESTORE */); Ui.SetForegroundWindow(Handle); return; }

            // Sized for the monitor the menu was opened on, centred in its work area.
            IntPtr mon = Ui.MonitorFromPoint(near, 2 /* MONITOR_DEFAULTTONEAREST */);
            uint dx, dy;
            if (Ui.GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out dx, out dy) == 0) dpi = (int)dx;
            var mi = new Ui.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(mi);
            Ui.GetMonitorInfo(mon, ref mi);
            if (!Create(0, Style, IntPtr.Zero, 1, 1)) return;
            Open = Handle;
            loading = true;
            Ui.SetWindowText(Handle, "TrayBattery settings");
            font = Ui.CreateFont(-Px(12), 0, 0, 0, 400, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, "Segoe UI");

            Control("BUTTON", "Battery dots", BS_GROUPBOX, 0, 12, 8, 296, 104, 0);
            Control("BUTTON", "Coloured", BS_AUTORADIOBUTTON | WS_TABSTOP | WS_GROUP, 0, 24, 30, 276, 22, IdStyle + Settings.Coloured);
            Control("BUTTON", "Monochrome", BS_AUTORADIOBUTTON, 0, 24, 54, 276, 22, IdStyle + Settings.Mono);
            Control("BUTTON", "Monochrome, low battery dot red", BS_AUTORADIOBUTTON, 0, 24, 78, 276, 22, IdStyle + Settings.MonoLowRed);
            Check(IdStyle + Settings.DotStyle, true);

            Control("BUTTON", "Icon order (left to right)", BS_GROUPBOX, 0, 12, 120, 296, 120, 0);
            list = Control("LISTBOX", null, LBS_NOTIFY | LBS_NOINTEGRALHEIGHT | WS_VSCROLL | WS_TABSTOP | WS_GROUP,
                           WS_EX_CLIENTEDGE, 24, 142, 180, 86, IdList);
            up = Control("BUTTON", "Left", WS_TABSTOP | WS_GROUP, 0, 214, 142, 82, 28, IdUp);
            down = Control("BUTTON", "Right", WS_TABSTOP, 0, 214, 176, 82, 28, IdDown);
            FillList(0);

            Control("BUTTON", "Start with Windows", BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP, 0, 16, 250, 290, 22, IdAutorun);
            Check(IdAutorun, App.IsAutorun());
            Control("BUTTON", "Advanced", BS_AUTOCHECKBOX | WS_TABSTOP | WS_GROUP, 0, 16, 274, 290, 22, IdAdvanced);
            Check(IdAdvanced, Settings.Advanced);

            Adv(Control("STATIC", "Poll interval, s (10–3600)", 0, 0, 16, 309, 200, 20, 0));
            Adv(Number(IdPoll, 4, 250, 306, 58));
            Adv(Control("STATIC", "Dot thresholds, %", 0, 0, 16, 341, 130, 20, 0));
            Adv(Number(IdT1, 2, 150, 338, 46));
            Adv(Number(IdT2, 2, 206, 338, 46));
            Adv(Number(IdT3, 2, 262, 338, 46));
            Adv(Control("STATIC", "Mouse asleep on charger: +% per minute\n(0 = don't estimate)", 0, 0, 16, 370, 230, 36, 0));
            Adv(Number(IdRate, 2, 250, 376, 58));
            Adv(Control("BUTTON", "Defaults", WS_TABSTOP | WS_GROUP, 0, 16, 418, 120, 28, IdDefaults));
            FillNumbers();

            close = Control("BUTTON", "Close", BS_DEFPUSHBUTTON | WS_TABSTOP | WS_GROUP, 0, 214, 304, 94, 28, IdClose);
            loading = false;

            Arrange(mi.rcWork);
            Ui.SetForegroundWindow(Handle);
            Ui.SetFocus(close);
        }

        IntPtr Control(string cls, string text, int style, int exStyle, int x, int y, int w, int h, int id)
        {
            IntPtr c = Ui.CreateWindowEx(exStyle, cls, text, Ui.WS_CHILD | WS_VISIBLE | style, Px(x), Px(y), Px(w), Px(h),
                                         Handle, new IntPtr(id), Ui.GetModuleHandle(null), IntPtr.Zero);
            Ui.SendMessage(c, WM_SETFONT, font, IntPtr.Zero);
            return c;
        }

        IntPtr Number(int id, int digits, int x, int y, int w)
        {
            IntPtr c = Control("EDIT", null, ES_NUMBER | ES_CENTER | WS_TABSTOP | WS_GROUP, WS_EX_CLIENTEDGE, x, y, w, 24, id);
            Ui.SendMessage(c, EM_SETLIMITTEXT, new IntPtr(digits), IntPtr.Zero);
            return c;
        }

        void Adv(IntPtr c) { advanced.Add(c); }

        void Check(int id, bool on) { Ui.SendMessage(Ui.GetDlgItem(Handle, id), BM_SETCHECK, new IntPtr(on ? 1 : 0), IntPtr.Zero); }
        bool Checked(int id) { return Ui.SendMessage(Ui.GetDlgItem(Handle, id), BM_GETCHECK, IntPtr.Zero, IntPtr.Zero).ToInt32() == 1; }

        // Shows or hides the advanced block and fits the window to it; centred in workArea on first call.
        void Arrange(Ui.RECT? workArea)
        {
            bool adv = Settings.Advanced;
            foreach (var c in advanced) Ui.ShowWindow(c, adv ? 5 /* SW_SHOW */ : 0 /* SW_HIDE */);
            int closeY = adv ? 418 : 304;
            Ui.SetWindowPos(close, IntPtr.Zero, Px(214), Px(closeY), 0, 0, 0x0001 | 0x0004 /* SWP_NOSIZE | SWP_NOZORDER */);

            var rc = new Ui.RECT { Right = Px(ClientW), Bottom = Px(adv ? AdvancedH : BasicH) };
            Ui.AdjustWindowRectExForDpi(ref rc, Style, false, 0, (uint)dpi);
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (workArea.HasValue)
            {
                var wa = workArea.Value;
                Ui.SetWindowPos(Handle, IntPtr.Zero, wa.Left + (wa.Right - wa.Left - w) / 2, wa.Top + (wa.Bottom - wa.Top - h) / 2,
                                w, h, 0x0004 | Ui.SWP_SHOWWINDOW);
            }
            else Ui.SetWindowPos(Handle, IntPtr.Zero, 0, 0, w, h, 0x0002 | 0x0004 /* SWP_NOMOVE | SWP_NOZORDER */);
        }

        void FillList(int select)
        {
            Ui.SendMessage(list, LB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
            foreach (var s in slots) Ui.SendMessage(list, LB_ADDSTRING, IntPtr.Zero, s.Gadget.Name);
            Ui.SendMessage(list, LB_SETCURSEL, new IntPtr(select), IntPtr.Zero);
            UpdateButtons();
        }

        void FillNumbers()
        {
            bool was = loading;
            loading = true;
            int[] t = Settings.Thresholds;
            Ui.SetWindowText(Ui.GetDlgItem(Handle, IdPoll), Settings.PollSeconds.ToString());
            Ui.SetWindowText(Ui.GetDlgItem(Handle, IdT1), t[0].ToString());
            Ui.SetWindowText(Ui.GetDlgItem(Handle, IdT2), t[1].ToString());
            Ui.SetWindowText(Ui.GetDlgItem(Handle, IdT3), t[2].ToString());
            Ui.SetWindowText(Ui.GetDlgItem(Handle, IdRate), Settings.ChargeRate.ToString());
            loading = was;
        }

        int ReadNumber(int id)
        {
            var sb = new System.Text.StringBuilder(8);
            Ui.GetWindowText(Ui.GetDlgItem(Handle, id), sb, sb.Capacity);
            int v;
            return int.TryParse(sb.ToString(), out v) ? v : -1;
        }

        // A field that does not hold a valid value yet (being typed, or out of range) is simply not applied.
        void NumberChanged(int id)
        {
            if (id == IdPoll)
            {
                int v = ReadNumber(IdPoll);
                if (Settings.ValidPoll(v) && v != Settings.PollSeconds) { Settings.PollSeconds = v; changed(true); }
            }
            else if (id == IdRate)
            {
                int v = ReadNumber(IdRate);
                if (Settings.ValidRate(v) && v != Settings.ChargeRate) { Settings.ChargeRate = v; changed(false); }
            }
            else
            {
                var t = new[] { ReadNumber(IdT1), ReadNumber(IdT2), ReadNumber(IdT3) };
                var cur = Settings.Thresholds;
                if (Settings.ValidThresholds(t) && (t[0] != cur[0] || t[1] != cur[1] || t[2] != cur[2]))
                {
                    Settings.Thresholds = t;
                    changed(false);
                }
            }
        }

        int Selected { get { return Ui.SendMessage(list, LB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt32(); } }

        void UpdateButtons()
        {
            int i = Selected;
            Ui.EnableWindow(up, i > 0);
            Ui.EnableWindow(down, i >= 0 && i < slots.Count - 1);
        }

        void Move(int delta)
        {
            int i = Selected, j = i + delta;
            if (i < 0 || j < 0 || j >= slots.Count) return;
            var t = slots[i]; slots[i] = slots[j]; slots[j] = t;
            Settings.SaveOrder(slots);
            FillList(j);
            changed(false);
        }

        protected override bool OnMessage(int msg, IntPtr wp, IntPtr lp, out IntPtr result)
        {
            result = IntPtr.Zero;
            switch (msg)
            {
                case WM_ERASEBKGND:
                    Ui.RECT r;
                    Ui.GetClientRect(Handle, out r);
                    Ui.FillRect(wp, ref r, Ui.GetSysColorBrush(5 /* COLOR_WINDOW */));
                    result = new IntPtr(1);
                    return true;
                case WM_CTLCOLORSTATIC:
                case WM_CTLCOLORBTN:
                    result = Ui.GetSysColorBrush(5 /* COLOR_WINDOW */);
                    return true;
                case WM_COMMAND:
                    if (loading) return true;
                    int id = (int)(wp.ToInt64() & 0xFFFF), code = (int)((wp.ToInt64() >> 16) & 0xFFFF);
                    if (id == IdClose || id == IdCancel) Destroy();
                    else if (id >= IdStyle + Settings.Coloured && id <= IdStyle + Settings.MonoLowRed)
                    {
                        if (Settings.DotStyle != id - IdStyle) { Settings.DotStyle = id - IdStyle; changed(false); }
                    }
                    else if (id == IdList && code == LBN_SELCHANGE) UpdateButtons();
                    else if (id == IdUp) Move(-1);
                    else if (id == IdDown) Move(1);
                    else if (id == IdAutorun) App.SetAutorun(Checked(IdAutorun));
                    else if (id == IdAdvanced) { Settings.Advanced = Checked(IdAdvanced); Arrange(null); }
                    else if (id >= IdPoll && id <= IdRate && code == EN_CHANGE) NumberChanged(id);
                    else if (id == IdDefaults)
                    {
                        bool pollChanged = Settings.PollSeconds != Settings.DefaultPollSeconds;
                        Settings.PollSeconds = Settings.DefaultPollSeconds;
                        Settings.Thresholds = Settings.DefaultThresholds;
                        Settings.ChargeRate = Settings.DefaultChargeRate;
                        FillNumbers();
                        changed(pollChanged);
                    }
                    return true;
            }
            return false;
        }

        protected override void OnDestroyed()
        {
            Open = IntPtr.Zero;
            advanced.Clear();
            if (font != IntPtr.Zero) { Ui.DeleteObject(font); font = IntPtr.Zero; }
        }
    }

    // ---------------------------------------------------------------- window P/Invoke

    static class Ui
    {
        public const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000, WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_LAYERED = 0x00080000, WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080;

        public delegate IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr DefWindowProc(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc);
        [DllImport("user32.dll")] public static extern IntPtr SetCoalescableTimer(IntPtr h, IntPtr id, uint ms, IntPtr proc, uint toleranceMs);
        [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr h, IntPtr id);
        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr m, uint flags, IntPtr id, string text);
        [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr m, uint flags, int x, int y, IntPtr h, IntPtr tpm);
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr m);
        [DllImport("user32.dll")] public static extern bool CheckMenuRadioItem(IntPtr m, int first, int last, int check, uint flags);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsDialogMessage(IntPtr dlg, ref MSG msg);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetWindowText(IntPtr h, string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool EnableWindow(IntPtr h, bool on);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
        [DllImport("user32.dll")] public static extern IntPtr GetSysColorBrush(int index);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern bool AdjustWindowRectExForDpi(ref RECT r, int style, bool menu, int exStyle, uint dpi);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
                                               uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("wtsapi32.dll")] public static extern bool WTSRegisterSessionNotification(IntPtr h, int flags);
        [DllImport("wtsapi32.dll")] public static extern bool WTSUnRegisterSessionNotification(IntPtr h);

        [StructLayout(LayoutKind.Sequential)] struct INITCOMMONCONTROLSEX { public int dwSize, dwICC; }
        [DllImport("comctl32.dll")] static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);

        // Loads Common Controls 6 (per app.manifest) and registers the tooltip class.
        public static void InitTooltips()
        {
            var icc = new INITCOMMONCONTROLSEX { dwICC = 0x4 /* ICC_BAR_CLASSES: toolbar, status bar, tooltips */ };
            icc.dwSize = Marshal.SizeOf(icc);
            InitCommonControlsEx(ref icc);
        }
        public const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)]
        struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwnd; public uint dwHoverTime; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TOOLINFO
        {
            public int cbSize; public uint uFlags; public IntPtr hwnd; public IntPtr uId; public RECT rect;
            public IntPtr hinst; public string lpszText; public IntPtr lParam; public IntPtr lpReserved;
        }

        public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr inst, IntPtr id);
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDc, IntPtr dstPt, ref SIZE size, IntPtr srcDc, ref POINT srcPt, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref TOOLINFO l);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

        public static void TrackLeave(IntPtr h)
        {
            var e = new TRACKMOUSEEVENT { dwFlags = 0x2 /* TME_LEAVE */, hwnd = h };
            e.cbSize = Marshal.SizeOf(e);
            TrackMouseEvent(ref e);
        }

        // Hands a 32-bit ARGB bitmap to the compositor as the window's per-pixel-alpha surface.
        public static void PushLayered(IntPtr h, Bitmap bmp)
        {
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));   // premultiplied 32-bit DIB
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr old = SelectObject(dc, hbmp);
            try
            {
                var size = new SIZE { W = bmp.Width, H = bmp.Height };
                var src = new POINT();
                var blend = new BLENDFUNCTION { Op = 0 /* AC_SRC_OVER */, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(h, IntPtr.Zero, IntPtr.Zero, ref size, dc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                SelectObject(dc, old);
                DeleteObject(hbmp);
                DeleteDC(dc);
            }
        }

        // Needs Common Controls 6 (see app.manifest): the v3 TOOLINFO below is rejected by v5.
        const int TTM_ADDTOOL = 0x0432, TTM_SETTOOLINFO = 0x0436, TTM_SETMAXTIPWIDTH = 0x0418;
        const uint TTF_SUBCLASS = 0x10;

        public static IntPtr CreateTooltip(IntPtr owner, int tools)
        {
            IntPtr tt = CreateWindowEx(WS_EX_TOPMOST, "tooltips_class32", null, WS_POPUP | 0x01 /* TTS_ALWAYSTIP */ | 0x02 /* TTS_NOPREFIX */,
                                       0, 0, 0, 0, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            SendMessage(tt, TTM_SETMAXTIPWIDTH, IntPtr.Zero, new IntPtr(400));
            for (int i = 0; i < tools; i++)
            {
                var ti = Tool(owner, i, new Rectangle(), " ");
                SendMessage(tt, TTM_ADDTOOL, IntPtr.Zero, ref ti);
            }
            return tt;
        }

        public static void SetTooltip(IntPtr tt, IntPtr owner, int id, Rectangle r, string text)
        {
            if (tt == IntPtr.Zero) return;
            var ti = Tool(owner, id, r, text);
            SendMessage(tt, TTM_SETTOOLINFO, IntPtr.Zero, ref ti);
            SendMessage(tt, 0x041D /* TTM_UPDATE: redraw a tip that is open right now */, IntPtr.Zero, IntPtr.Zero);
        }

        static TOOLINFO Tool(IntPtr owner, int id, Rectangle r, string text)
        {
            var ti = new TOOLINFO
            {
                uFlags = TTF_SUBCLASS, hwnd = owner, uId = new IntPtr(id), lpszText = text,
                rect = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom }
            };
            ti.cbSize = Marshal.SizeOf(ti);
            return ti;
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;
        public const int SM_CXSMICON = 49;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage, UsagePage;
            public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps,
                NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll")]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle h, IntPtr buf, uint len, IntPtr read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle h, IntPtr buf, uint len, IntPtr written, IntPtr overlapped);
        [DllImport("kernel32.dll")]
        public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEvent(IntPtr sa, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

        // Efficiency mode (what Task Manager shows with the leaf): EcoQoS keeps the process on
        // efficient cores at low clocks and never asks for a turbo boost; idle priority on top.
        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
        [DllImport("kernel32.dll")] static extern bool SetPriorityClass(IntPtr process, uint priority);

        public static void EnterEfficiencyMode()
        {
            const int ProcessPowerThrottling = 4;
            const uint EXECUTION_SPEED = 0x1, IGNORE_TIMER_RESOLUTION = 0x4, IDLE_PRIORITY_CLASS = 0x40;
            var s = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = 1,
                ControlMask = EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION,
                StateMask = EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION
            };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref s, Marshal.SizeOf(s));
            SetPriorityClass(GetCurrentProcess(), IDLE_PRIORITY_CLASS);
        }

        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    }
}
