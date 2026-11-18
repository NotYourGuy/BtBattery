// BtBattery - shows the battery level of connected Bluetooth devices on the Windows taskbar,
// as "icon 90%" items right next to the tray's ^ arrow.
// Build with build.cmd (uses the C# compiler that ships with Windows, no SDK needed).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("BtBattery")]
[assembly: System.Reflection.AssemblyProduct("BtBattery")]
[assembly: System.Reflection.AssemblyDescription("Bluetooth battery levels on the Windows taskbar")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 Lucian Vasile. MIT License.")]
[assembly: System.Reflection.AssemblyVersion("1.7.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.7.0")]

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "BtBattery_SingleInstance_7f3c", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            using (var strip = new TaskbarStrip()) Application.Run();
        }
    }
}

enum DeviceKind { Other, Headphones, Speaker, Mouse, Keyboard, Phone, Gamepad }

class BtDevice
{
    public string Address;
    public string Name;
    public int Battery;
    public bool IsLE;
    public DeviceKind Kind;
}

// ---------------------------------------------------------------------------------------------
// Reading battery levels from Windows' device properties (SetupAPI).
// ---------------------------------------------------------------------------------------------
static class Bluetooth
{
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public int pid; public DEVPROPKEY(string g, int p) { fmtid = new Guid(g); pid = p; } }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
        out int propType, byte[] buffer, int bufferSize, out int requiredSize, int flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    const int DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    static DEVPROPKEY KeyBattery = new DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    static DEVPROPKEY KeyConnected = new DEVPROPKEY("83DA6326-97A6-4088-9453-A1923F573B29", 15);
    static DEVPROPKEY KeyFriendlyName = new DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
    static DEVPROPKEY KeyName = new DEVPROPKEY("B725F130-47EF-101A-A5F1-02608C9EBAC0", 10);
    static DEVPROPKEY KeyCategory = new DEVPROPKEY("78C34FC8-104A-4ACA-9EA4-524D52996E57", 90);
    static DEVPROPKEY KeyClassOfDevice = new DEVPROPKEY("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 10);

    static readonly Regex AddrRx = new Regex(@"(?:^|&|DEV_)([0-9A-F]{12})(?:_|$)", RegexOptions.IgnoreCase);
    static readonly Regex AddrEndRx = new Regex(@"([0-9A-F]{12})$", RegexOptions.IgnoreCase);
    static readonly string[] Suffixes = { " Hands-Free AG", " Hands-Free HF", " Hands-Free", " Avrcp Transport", " Stereo", " Headset" };

    class ScanState
    {
        public List<BtDevice> Found = new List<BtDevice>();
        public HashSet<string> Connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DeviceKind> KindByAddr = new Dictionary<string, DeviceKind>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DeviceKind> KindByName = new Dictionary<string, DeviceKind>(StringComparer.OrdinalIgnoreCase);
    }

    public static List<BtDevice> GetConnectedDevices()
    {
        var st = new ScanState();
        Scan("BTHLE", true, st);
        Scan("BTHENUM", false, st);

        // A profile entry (e.g. "Hands-Free AG") may not carry the connected flag itself;
        // any other entry with the same address that does is good enough.
        st.Found.RemoveAll(d => !st.Connected.Contains(d.Address));

        // One physical device can show up several times (LE + classic, or several profiles).
        // Merge by address first, then by cleaned-up name. Prefer the LE entry.
        var byKey = new Dictionary<string, BtDevice>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in st.Found)
        {
            string key;
            if (!byName.TryGetValue(d.Name, out key)) key = d.Address;
            BtDevice existing;
            if (byKey.TryGetValue(key, out existing))
            {
                if (!existing.IsLE && d.IsLE) { d.Address = existing.Address; byKey[key] = d; }
                continue;
            }
            byKey[key] = d;
            byName[d.Name] = key;
        }
        var result = new List<BtDevice>(byKey.Values);
        foreach (var d in result)
        {
            DeviceKind k;
            if (st.KindByAddr.TryGetValue(d.Address, out k)) d.Kind = k;
            else if (st.KindByName.TryGetValue(d.Name, out k)) d.Kind = k;
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    static void Scan(string enumerator, bool isLE, ScanState st)
    {
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return;
        try
        {
            var data = new SP_DEVINFO_DATA();
            data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                var sb = new StringBuilder(512);
                int req;
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, sb, sb.Capacity, out req)) continue;
                string id = sb.ToString();
                string last = id.Substring(id.LastIndexOf('\\') + 1);
                Match m = AddrRx.Match(last);
                if (!m.Success) m = AddrRx.Match(id);
                if (!m.Success) m = AddrEndRx.Match(id);
                if (!m.Success) continue;
                string addr = m.Groups[1].Value.ToUpperInvariant();

                byte[] con = GetProp(set, ref data, KeyConnected);
                if (con != null && con.Length > 0 && con[0] != 0) st.Connected.Add(addr);

                string name = GetString(set, ref data, KeyFriendlyName) ?? GetString(set, ref data, KeyName) ?? "Bluetooth device";
                foreach (var s in Suffixes)
                    if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { name = name.Substring(0, name.Length - s.Length); break; }
                name = name.Trim();

                DeviceKind kind = GetKind(set, ref data);
                if (kind != DeviceKind.Other)
                {
                    if (!st.KindByAddr.ContainsKey(addr)) st.KindByAddr[addr] = kind;
                    if (!st.KindByName.ContainsKey(name)) st.KindByName[name] = kind;
                }

                byte[] bat = GetProp(set, ref data, KeyBattery);
                if (bat == null || bat.Length < 1) continue;
                st.Found.Add(new BtDevice { Address = addr, Name = name, Battery = Math.Min(100, (int)bat[0]), IsLE = isLE });
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    static DeviceKind GetKind(IntPtr set, ref SP_DEVINFO_DATA data)
    {
        string cat = GetString(set, ref data, KeyCategory);
        if (cat != null)
        {
            cat = cat.ToLowerInvariant();
            if (cat.Contains("mouse") || cat.Contains("pointing") || cat.Contains("touchpad")) return DeviceKind.Mouse;
            if (cat.Contains("keyboard")) return DeviceKind.Keyboard;
            if (cat.Contains("gaming") || cat.Contains("gamepad") || cat.Contains("joystick")) return DeviceKind.Gamepad;
            if (cat.Contains("phone") && !cat.Contains("headphone")) return DeviceKind.Phone;
            if (cat.Contains("speaker")) return DeviceKind.Speaker;
            if (cat.Contains("headset") || cat.Contains("headphone") || cat.Contains("earbud") || cat.Contains("audio")) return DeviceKind.Headphones;
        }
        byte[] b = GetProp(set, ref data, KeyClassOfDevice);
        if (b != null && b.Length >= 4)
        {
            int cod = BitConverter.ToInt32(b, 0);
            int major = (cod >> 8) & 0x1F;
            if (major == 2) return DeviceKind.Phone;
            if (major == 4) return ((cod >> 2) & 0x3F) == 5 ? DeviceKind.Speaker : DeviceKind.Headphones;
            if (major == 5)
            {
                int periph = (cod >> 6) & 3;
                if (periph == 2) return DeviceKind.Mouse;
                if (periph == 1 || periph == 3) return DeviceKind.Keyboard;
                int sub = (cod >> 2) & 0xF;
                if (sub == 1 || sub == 2) return DeviceKind.Gamepad;
            }
        }
        return DeviceKind.Other;
    }

    static byte[] GetProp(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        int type, size;
        var buf = new byte[2048];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out type, buf, buf.Length, out size, 0)) return null;
        var r = new byte[size];
        Array.Copy(buf, r, size);
        return r;
    }

    // Works for both strings and string lists (list entries are joined with ';').
    static string GetString(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        byte[] b = GetProp(set, ref data, key);
        if (b == null || b.Length < 2) return null;
        string s = Encoding.Unicode.GetString(b).TrimEnd('\0').Replace('\0', ';');
        return s.Length == 0 ? null : s;
    }
}

// User preferences, stored under HKCU\Software\BtBattery.
static class Settings
{
    const string Key = @"Software\BtBattery";

    public static bool GetBool(string name, bool fallback)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                object v = k == null ? null : k.GetValue(name);
                return v is int ? (int)v != 0 : fallback;
            }
        }
        catch { return fallback; }
    }

    public static void SetBool(string name, bool value)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord); }
        catch { }
    }

    public static string GetString(string name)
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k == null ? null : k.GetValue(name) as string; }
        catch { return null; }
    }

    public static void SetString(string name, string value)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value, RegistryValueKind.String); }
        catch { }
    }

    public static List<string> GetList(string name)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                var v = k == null ? null : k.GetValue(name) as string[];
                return v == null ? new List<string>() : new List<string>(v);
            }
        }
        catch { return new List<string>(); }
    }

    public static void SetList(string name, List<string> values)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, values.ToArray(), RegistryValueKind.MultiString); }
        catch { }
    }
}

// Low-battery alerts as Windows notifications. A tray icon is shown only while an alert is
// on screen (Windows needs one to post the notification), then removed again.
class LowBatteryAlerts : IDisposable
{
    public const int Warn = 20, Critical = 10, Reset = 25;

    readonly NotifyIcon notifier = new NotifyIcon();
    readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer();
    // Lowest threshold already alerted for each device (by address).
    readonly Dictionary<string, int> alerted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public LowBatteryAlerts()
    {
        try { notifier.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { notifier.Icon = SystemIcons.Information; }
        notifier.Text = "BtBattery";
        notifier.BalloonTipClicked += delegate
        {
            if (clickUrl != null) try { System.Diagnostics.Process.Start(clickUrl); } catch { }
        };
        hideTimer.Interval = 15000;
        hideTimer.Tick += delegate { hideTimer.Stop(); notifier.Visible = false; };
    }

    public bool Enabled
    {
        get { return Settings.GetBool("LowBatteryAlerts", true); }
        set { Settings.SetBool("LowBatteryAlerts", value); }
    }

    public void Check(List<BtDevice> devices)
    {
        var lines = new List<string>();
        bool critical = false;
        foreach (var d in devices)
        {
            int prev;
            alerted.TryGetValue(d.Address, out prev);
            if (d.Battery >= Reset) { alerted.Remove(d.Address); continue; }

            int threshold = d.Battery <= Critical ? Critical : d.Battery <= Warn ? Warn : 0;
            if (threshold == 0 || (prev != 0 && prev <= threshold)) continue;
            alerted[d.Address] = threshold;
            lines.Add(d.Name + " is at " + d.Battery + "%");
            if (threshold == Critical) critical = true;
        }
        if (lines.Count > 0 && Enabled)
            Show(critical ? "Bluetooth battery critically low" : "Bluetooth battery low",
                 string.Join("\n", lines.ToArray()) + "\nCharge soon to avoid it disconnecting.");
    }

    string clickUrl;

    public void Show(string title, string text) { Show(title, text, ToolTipIcon.Warning, null); }

    // Also used for update notices; url (optional) opens when the notification is clicked.
    public void Show(string title, string text, ToolTipIcon icon, string url)
    {
        clickUrl = url;
        notifier.Visible = true;
        notifier.ShowBalloonTip(10000, title, text, icon);
        hideTimer.Stop();
        hideTimer.Start();
    }

    public void Dispose()
    {
        hideTimer.Dispose();
        notifier.Visible = false;
        notifier.Dispose();
    }
}

// Asks GitHub for the latest release (at most once a day, or on demand) and compares it with
// this exe's version. The request runs on a worker thread; the UI picks up the result by polling.
class UpdateChecker : IDisposable
{
    const string Api = "https://api.github.com/repos/NotYourGuy/BtBattery/releases/latest";
    public const string ReleasesPage = "https://github.com/NotYourGuy/BtBattery/releases/latest";

    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    volatile string pendingTag, pendingUrl, pendingError;
    volatile bool done, busy, manual;

    public string LatestTag { get; private set; }   // e.g. "v1.8.0", when newer than us
    public string LatestUrl { get; private set; }
    public event Action<string, string> Found;      // tag, url (new version found)
    public event Action<string> ManualResult;       // message for a manual check

    public static Version Current
    {
        get
        {
#if DEMO_OLD_VERSION
            return new Version(1, 0, 0);
#else
            return typeof(UpdateChecker).Assembly.GetName().Version;
#endif
        }
    }

    public bool Enabled
    {
        get { return Settings.GetBool("UpdateCheck", true); }
        set { Settings.SetBool("UpdateCheck", value); }
    }

    public UpdateChecker()
    {
        // Every few seconds: deliver a finished check; every so often: start a daily check.
        timer.Interval = 2000;
        int ticks = 0;
        timer.Tick += delegate
        {
            if (done) Deliver();
            // First automatic check ~20 s after start, then re-evaluated every 30 min.
            if (++ticks == 10 || ticks % 900 == 0) MaybeCheck();
        };
        timer.Start();
    }

    void MaybeCheck()
    {
        if (!Enabled) return;
        long last;
        if (long.TryParse(Settings.GetString("LastUpdateCheck"), out last) &&
            DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc) < TimeSpan.FromHours(24)) return;
        Start(false);
    }

    public void CheckNow() { Start(true); }

    void Start(bool isManual)
    {
        if (busy) return;
        busy = true; manual = isManual;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= (System.Net.SecurityProtocolType)3072; // TLS 1.2
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers.Add("User-Agent", "BtBattery-update-check");
                    wc.Headers.Add("Accept", "application/vnd.github+json");
                    string json = wc.DownloadString(Api);
                    var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    var url = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github.com/[^\"]+/releases/tag/[^\"]+)\"");
                    pendingTag = tag.Success ? tag.Groups[1].Value : null;
                    pendingUrl = url.Success ? url.Groups[1].Value : ReleasesPage;
                    pendingError = tag.Success ? null : "Couldn't read the latest release.";
                }
            }
            catch (Exception ex) { pendingError = "Couldn't check for updates (" + ex.Message + ")."; }
            done = true;
        });
    }

    void Deliver()
    {
        done = false; busy = false;
        if (pendingError != null)
        {
            if (manual && ManualResult != null) ManualResult(pendingError);
            return;
        }
        Settings.SetString("LastUpdateCheck", DateTime.UtcNow.Ticks.ToString());
        Version latest;
        bool newer = Version.TryParse(pendingTag.TrimStart('v', 'V'), out latest) && latest > Current;
        if (newer)
        {
            LatestTag = pendingTag; LatestUrl = pendingUrl;
            // Notify once per new version (always when the user asked).
            if (manual || Settings.GetString("NotifiedVersion") != pendingTag)
            {
                Settings.SetString("NotifiedVersion", pendingTag);
                if (Found != null) Found(pendingTag, pendingUrl);
            }
        }
        else if (manual && ManualResult != null)
            ManualResult("You're up to date (v" + Current.ToString(3) + ").");
    }

    public void Dispose() { timer.Dispose(); }
}

// Hidden top-level window that hears WM_DEVICECHANGE, which Windows broadcasts when devices
// are added, removed or change state (e.g. a Bluetooth device connecting or disconnecting).
// Child windows like the strip never get these broadcasts, hence this separate window.
class DeviceChangeListener : NativeWindow, IDisposable
{
    const int WM_DEVICECHANGE = 0x219, DBT_DEVNODES_CHANGED = 0x7, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;
    public event EventHandler Changed;

    public DeviceChangeListener()
    {
        var cp = new CreateParams();
        cp.Caption = "BtBattery device listener";
        cp.ExStyle = 0x80; // WS_EX_TOOLWINDOW: never on the taskbar
        CreateHandle(cp);  // style 0 and never shown: an invisible top-level window
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE)
        {
            int ev = m.WParam.ToInt32();
            if ((ev == DBT_DEVNODES_CHANGED || ev == DBT_DEVICEARRIVAL || ev == DBT_DEVICEREMOVECOMPLETE) && Changed != null)
                Changed(this, EventArgs.Empty);
        }
        base.WndProc(ref m);
    }

    public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
}

// Right-click menu. Shown without an owner form, a plain ContextMenuStrip gets its own
// taskbar button (with a blank icon) while open; a tool window never does.
class ToolMenu : ContextMenuStrip
{
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;       // WS_EX_TOOLWINDOW
            cp.ExStyle &= ~0x40000;   // no WS_EX_APPWINDOW
            return cp;
        }
    }
}

// Small tooltip popup shown above the taskbar (WinForms' ToolTip can't attach to a raw window).
class TipPopup : Form
{
    readonly Label label = new Label();

    public TipPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(1);
        label.AutoSize = true;
        label.Font = new Font("Segoe UI", 9f);
        label.Padding = new Padding(8, 5, 8, 6);
        Controls.Add(label);
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000 | 0x20; // tool window, no activate, click-through
            return cp;
        }
    }

    public void ShowAt(string text, int centerX, int bottomY, bool light)
    {
        BackColor = light ? Color.FromArgb(200, 200, 200) : Color.FromArgb(70, 70, 70);
        label.BackColor = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(43, 43, 43);
        label.ForeColor = light ? Color.Black : Color.White;
        label.Text = text;
        Size pref = GetPreferredSize(Size.Empty);
        Rectangle wa = Screen.FromPoint(new Point(centerX, bottomY)).Bounds;
        int x = Math.Max(wa.Left, Math.Min(wa.Right - pref.Width, centerX - pref.Width / 2));
        Location = new Point(x, bottomY - pref.Height);
        if (!Visible) Show();
    }
}

// ---------------------------------------------------------------------------------------------
// A transparent window placed inside the taskbar, just left of the tray's ^ arrow.
// ---------------------------------------------------------------------------------------------
class TaskbarStrip : NativeWindow, IDisposable
{
    #region Win32
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public int cbSize, dwFlags; public IntPtr hwndTrack; public int dwHoverTime; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    const int SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2;
    const int ULW_ALPHA = 2;
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_MOUSELEAVE = 0x2A3, WM_MOUSEACTIVATE = 0x21;
    const int MA_NOACTIVATE = 3, TME_LEAVE = 2;
    const int WM_LBUTTONDBLCLK = 0x203, CS_DBLCLKS = 0x8;
    static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_TOPMOST = new IntPtr(-1);
    #endregion

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "BtBattery";

    readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer posTimer = new System.Windows.Forms.Timer();
    readonly ContextMenuStrip menu = new ToolMenu();
    readonly ToolStripMenuItem startupItem, alertsItem, devicesItem, compactItem, updateItem, autoUpdateItem;
    readonly ToolStripSeparator updateSeparator = new ToolStripSeparator();
    readonly UpdateChecker updates = new UpdateChecker();
    readonly TipPopup tip = new TipPopup();
    readonly LowBatteryAlerts alerts = new LowBatteryAlerts();
    readonly DeviceChangeListener deviceListener = new DeviceChangeListener();
    readonly System.Windows.Forms.Timer changeTimer = new System.Windows.Forms.Timer();
    int followUps; // extra refreshes after a device change, while Windows fills in battery info
    readonly string glyphFont, textFont;

    IntPtr taskbar = IntPtr.Zero;
    bool childMode;
    List<BtDevice> allDevices = new List<BtDevice>(); // every connected device, in the user's order
    List<BtDevice> devices = new List<BtDevice>();    // the ones shown on the strip
    bool hovered;
    bool tracking;
    Size size = new Size(1, 1);
    Rectangle lastBounds;
    bool lastLight;
    float lastScale;

    public TaskbarStrip()
    {
        glyphFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        // Semibold strokes hold up much better than regular with grayscale anti-aliasing.
        textFont = FontExists("Segoe UI Semibold") ? "Segoe UI Semibold" : "Segoe UI";

        // Shown at the top only when a newer release exists.
        updateItem = new ToolStripMenuItem("Update available", null, delegate { OpenUrl(updates.LatestUrl ?? UpdateChecker.ReleasesPage); });
        updateItem.Font = new Font(updateItem.Font, FontStyle.Bold);
        updateItem.Visible = updateSeparator.Visible = false;
        menu.Items.Add(updateItem);
        menu.Items.Add(updateSeparator);
        updates.Found += (tag, url) =>
            alerts.Show("BtBattery " + tag + " is available", "Click to open the download page.", ToolTipIcon.Info, url);
        updates.ManualResult += msg => alerts.Show("BtBattery", msg, ToolTipIcon.Info, null);

        menu.Items.Add("Refresh now", null, delegate { RefreshDevices(); });
        devicesItem = new ToolStripMenuItem("Devices");
        // The strip sits at the right of the screen: open submenus leftwards so they stay on
        // this monitor instead of spilling onto a second monitor to the right.
        devicesItem.DropDownDirection = ToolStripDropDownDirection.Left;
        devicesItem.DropDownItems.Add("(none)"); // placeholder so the submenu arrow shows; rebuilt on open
        menu.Items.Add(devicesItem);
        menu.Items.Add("Bluetooth settings", null, delegate { OpenBluetoothSettings(); });
        compactItem = new ToolStripMenuItem("Compact mode", null, delegate
        {
            Settings.SetBool("Compact", !Settings.GetBool("Compact", false));
            Render();
        });
        menu.Items.Add(compactItem);
        menu.Items.Add(new ToolStripSeparator());
        startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
        menu.Items.Add(startupItem);
        alertsItem = new ToolStripMenuItem("Low battery alerts", null, delegate { alerts.Enabled = !alerts.Enabled; });
        menu.Items.Add(alertsItem);
        var updatesMenu = new ToolStripMenuItem("Updates");
        updatesMenu.DropDownDirection = ToolStripDropDownDirection.Left;
        updatesMenu.DropDownItems.Add("Check now", null, delegate { updates.CheckNow(); });
        autoUpdateItem = new ToolStripMenuItem("Check automatically (daily)", null, delegate { updates.Enabled = !updates.Enabled; });
        updatesMenu.DropDownItems.Add(autoUpdateItem);
        updatesMenu.DropDownItems.Add("Open releases page", null, delegate { OpenUrl(UpdateChecker.ReleasesPage); });
        updatesMenu.DropDownOpening += delegate { autoUpdateItem.Checked = updates.Enabled; };
        menu.Items.Add(updatesMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { Application.Exit(); });
        menu.Opening += delegate
        {
            startupItem.Checked = IsStartupEnabled();
            alertsItem.Checked = alerts.Enabled;
            compactItem.Checked = Settings.GetBool("Compact", false);
            bool hasUpdate = updates.LatestTag != null;
            updateItem.Visible = updateSeparator.Visible = hasUpdate;
            if (hasUpdate) updateItem.Text = "Update available (" + updates.LatestTag + ")…";
        };
        devicesItem.DropDownOpening += delegate { BuildDevicesMenu(); };

        refreshTimer.Interval = 30000;
        refreshTimer.Tick += delegate { RefreshDevices(); };
        posTimer.Interval = 500;
        posTimer.Tick += delegate { KeepInPlace(); };

        // A device change usually arrives as a burst of messages: wait for it to settle (1 s),
        // refresh, then refresh twice more 4 s apart because the battery level can lag behind.
        changeTimer.Tick += delegate
        {
            RefreshDevices();
            if (--followUps <= 0) changeTimer.Stop();
            else changeTimer.Interval = 4000;
        };
        deviceListener.Changed += delegate
        {
            followUps = 3;
            changeTimer.Stop();
            changeTimer.Interval = 1000;
            changeTimer.Start();
        };

        EnsureWindow();
        RefreshDevices();
        refreshTimer.Start();
        posTimer.Start();
    }

    static bool FontExists(string name)
    {
        using (var f = new Font(name, 10)) return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    // Create (or re-create after Explorer restarts) our window as a child of the taskbar.
    void EnsureWindow()
    {
        IntPtr tb = FindWindow("Shell_TrayWnd", null);
        if (Handle != IntPtr.Zero && tb == taskbar && (!childMode || IsWindow(taskbar))) return;
        if (tb == IntPtr.Zero && Handle != IntPtr.Zero) return; // Explorer is restarting; keep what we have.
        if (Handle != IntPtr.Zero) DestroyHandle();
        taskbar = tb;

        var cp = new CreateParams();
        cp.Caption = "BtBattery";
        cp.ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        cp.ClassStyle = CS_DBLCLKS; // deliver double-clicks
        cp.Width = 1; cp.Height = 1;
        childMode = false;
        if (taskbar != IntPtr.Zero)
        {
            cp.Parent = taskbar;
            cp.Style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
            try { CreateHandle(cp); childMode = true; } catch { }
        }
        if (Handle == IntPtr.Zero)
        {
            // Fallback: a topmost popup that sits over the taskbar.
            cp.Parent = IntPtr.Zero;
            cp.Style = WS_POPUP | WS_VISIBLE;
            cp.ExStyle |= WS_EX_TOPMOST;
            CreateHandle(cp);
        }
        lastBounds = Rectangle.Empty;
        lastScale = 0;
    }

    void RefreshDevices()
    {
        try { allDevices = Bluetooth.GetConnectedDevices(); }
        catch { allDevices = new List<BtDevice>(); }
        ApplyPreferences();
        Render();
        if (hovered) ShowTip();
        alerts.Check(devices);
    }

    // Sort by the saved order (unknown devices go last, by name) and drop hidden devices.
    void ApplyPreferences()
    {
        var order = Settings.GetList("Order");
        var hidden = new HashSet<string>(Settings.GetList("Hidden"), StringComparer.OrdinalIgnoreCase);
        allDevices.Sort((a, b) =>
        {
            int ia = order.FindIndex(n => string.Equals(n, a.Name, StringComparison.OrdinalIgnoreCase));
            int ib = order.FindIndex(n => string.Equals(n, b.Name, StringComparison.OrdinalIgnoreCase));
            if (ia < 0) ia = int.MaxValue;
            if (ib < 0) ib = int.MaxValue;
            return ia != ib ? ia.CompareTo(ib) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        devices = allDevices.FindAll(d => !hidden.Contains(d.Name));
    }

    void BuildDevicesMenu()
    {
        var old = new List<ToolStripItem>();
        foreach (ToolStripItem it in devicesItem.DropDownItems) old.Add(it);
        devicesItem.DropDownItems.Clear();
        foreach (var it in old) it.Dispose();
        if (allDevices.Count == 0)
        {
            devicesItem.DropDownItems.Add(new ToolStripMenuItem("No devices connected") { Enabled = false });
            return;
        }
        var hidden = new HashSet<string>(Settings.GetList("Hidden"), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < allDevices.Count; i++)
        {
            var d = allDevices[i];
            string name = d.Name;
            var item = new ToolStripMenuItem(name + "   " + d.Battery + "%");
            item.Checked = !hidden.Contains(name);

            var show = new ToolStripMenuItem("Show on taskbar", null, delegate { ToggleHidden(name); });
            show.Checked = item.Checked;
            var left = new ToolStripMenuItem("Move left", null, delegate { Move(name, -1); });
            left.Enabled = i > 0;
            var right = new ToolStripMenuItem("Move right", null, delegate { Move(name, +1); });
            right.Enabled = i < allDevices.Count - 1;
            item.DropDownItems.AddRange(new ToolStripItem[] { show, left, right });
            item.DropDownDirection = ToolStripDropDownDirection.Left;
            devicesItem.DropDownItems.Add(item);
        }
    }

    void ToggleHidden(string name)
    {
        var hidden = Settings.GetList("Hidden");
        int i = hidden.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) hidden.RemoveAt(i); else hidden.Add(name);
        Settings.SetList("Hidden", hidden);
        ApplyPreferences();
        Render();
    }

    void Move(string name, int delta)
    {
        var names = allDevices.ConvertAll(d => d.Name);
        int i = names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        int j = i + delta;
        if (i < 0 || j < 0 || j >= names.Count) return;
        names[i] = names[j];
        names[j] = name;
        // Keep the saved position of devices that aren't connected right now.
        foreach (var n in Settings.GetList("Order"))
            if (!names.Exists(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) names.Add(n);
        Settings.SetList("Order", names);
        ApplyPreferences();
        Render();
    }

    void KeepInPlace()
    {
        EnsureWindow();
        bool light = LightTaskbar();
        float scale = Scale();
        if (light != lastLight || scale != lastScale) Render();
        else Place();
        if (hovered && !Bounds().Contains(Cursor.Position)) { hovered = false; tip.Hide(); Render(); }
    }

    Rectangle Bounds()
    {
        RECT r;
        GetWindowRect(Handle, out r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    float Scale()
    {
        try
        {
            if (taskbar != IntPtr.Zero) { uint dpi = GetDpiForWindow(taskbar); if (dpi > 0) return dpi / 96f; }
        }
        catch { }
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
    }

    // Put the window just left of the tray area (which starts at the ^ arrow).
    void Place()
    {
        RECT tb, tray;
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out tb)) return;
        IntPtr trayWnd = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        int trayLeft = (trayWnd != IntPtr.Zero && GetWindowRect(trayWnd, out tray)) ? tray.Left : tb.Right - (int)(200 * Scale());

        int h = tb.Bottom - tb.Top;
        int x = trayLeft - size.Width - (int)(2 * Scale());
        int y = tb.Top + (h - size.Height) / 2;
        var bounds = new Rectangle(x, y, size.Width, size.Height);
        if (bounds == lastBounds)
        {
            // Keep ourselves above the taskbar's own content.
            SetWindowPos(Handle, childMode ? HWND_TOP : HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return;
        }
        lastBounds = bounds;
        if (childMode) { x -= tb.Left; y -= tb.Top; }
        SetWindowPos(Handle, childMode ? HWND_TOP : HWND_TOPMOST, x, y, size.Width, size.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    static bool LightTaskbar()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                return v is int && (int)v == 1;
            }
        }
        catch { return false; }
    }

    static string Glyph(DeviceKind k)
    {
        switch (k)
        {
            case DeviceKind.Headphones: return "";
            case DeviceKind.Speaker: return "";
            case DeviceKind.Mouse: return "";
            case DeviceKind.Keyboard: return "";
            case DeviceKind.Phone: return "";
            case DeviceKind.Gamepad: return "";
            default: return ""; // Bluetooth
        }
    }

    void Render()
    {
        if (Handle == IntPtr.Zero) return;
        float s = Scale();
        bool light = LightTaskbar();
        lastScale = s; lastLight = light;

        RECT tb;
        int height = (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out tb)) ? tb.Bottom - tb.Top : (int)(48 * s);
        height = Math.Max(1, height - (int)(8 * s)); // match the height of the taskbar's hover highlights

        Color fg = light ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);
        Color low = light ? Color.FromArgb(255, 157, 93, 0) : Color.FromArgb(255, 255, 185, 0);       // amber, <= 20%
        Color critical = light ? Color.FromArgb(255, 196, 43, 28) : Color.FromArgb(255, 255, 99, 71); // red, <= 10%
        bool compact = Settings.GetBool("Compact", false);

        // Whole-pixel sizes keep glyph stems on the pixel grid (sharper than fractional sizes).
        using (var gf = new Font(glyphFont, (float)Math.Round(20f * s), GraphicsUnit.Pixel))
        using (var tf = new Font(textFont, (float)Math.Round(15f * s), GraphicsUnit.Pixel))
        using (var measureBmp = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(measureBmp))
        {
            var fmt = StringFormat.GenericTypographic;
            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            int pad = (int)(10 * s), gap = (int)(6 * s), between = (int)((compact ? 12 : 14) * s);
            int barH = Math.Max(2, (int)Math.Round(3 * s)), barGap = Math.Max(1, (int)Math.Round(3 * s));

            // One continuous strip: [pad] glyph gap text [between] glyph gap text ... [pad]
            // Compact mode drops the text and puts a small level bar under each glyph.
            var items = new List<BtDevice>(devices);
            var widths = new List<int>();
            int total = 2 * pad;
            foreach (var d in items)
            {
                int w = (int)Math.Ceiling(mg.MeasureString(Glyph(d.Kind), gf, 1000, fmt).Width);
                if (!compact) w += gap + (int)Math.Ceiling(mg.MeasureString(d.Battery + "%", tf, 1000, fmt).Width);
                widths.Add(w);
                total += w;
            }
            if (items.Count > 1) total += between * (items.Count - 1);
            if (items.Count == 0) total = (int)(32 * s);
            size = new Size(Math.Max(1, total), height);

            using (var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    // Alpha 1 everywhere: invisible, but still catches the mouse.
                    g.Clear(Color.FromArgb(1, 0, 0, 0));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    // Hovering highlights the whole strip as one button.
                    if (hovered)
                        using (var hb = new SolidBrush(light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255)))
                        using (var path = RoundRect(new Rectangle(Point.Empty, size), (int)(4 * s)))
                            g.FillPath(hb, path);

                    if (items.Count == 0)
                    {
                        using (var dim = new SolidBrush(Color.FromArgb(110, fg)))
                        {
                            SizeF gs = g.MeasureString(Glyph(DeviceKind.Other), gf, 1000, fmt);
                            g.DrawString(Glyph(DeviceKind.Other), gf, dim, (float)Math.Round((size.Width - gs.Width) / 2f), (float)Math.Round((size.Height - gs.Height) / 2f), fmt);
                        }
                    }

                    int x = pad;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var d = items[i];
                        Color c = d.Battery <= LowBatteryAlerts.Critical ? critical : d.Battery <= LowBatteryAlerts.Warn ? low : fg;
                        using (var brush = new SolidBrush(c))
                        {
                            string glyph = Glyph(d.Kind), text = d.Battery + "%";
                            SizeF gs = g.MeasureString(glyph, gf, 1000, fmt);
                            // Snap every position to a whole pixel; fractional offsets smear the anti-aliasing.
                            float gx = x;
                            if (compact)
                            {
                                // Glyph and bar centred together as one block.
                                float block = gs.Height + barGap + barH;
                                float gy = (float)Math.Round((size.Height - block) / 2f);
                                g.DrawString(glyph, gf, brush, gx, gy, fmt);
                                float by = (float)Math.Round(gy + gs.Height + barGap);
                                int bw = widths[i];
                                using (var track = new SolidBrush(Color.FromArgb(70, fg)))
                                    g.FillRectangle(track, gx, by, bw, barH);
                                g.FillRectangle(brush, gx, by, (float)Math.Max(1, Math.Round(bw * d.Battery / 100f)), barH);
                            }
                            else
                            {
                                SizeF ts = g.MeasureString(text, tf, 1000, fmt);
                                float tx = (float)Math.Round(gx + gs.Width + gap);
                                g.DrawString(glyph, gf, brush, gx, (float)Math.Round((size.Height - gs.Height) / 2f), fmt);
                                g.DrawString(text, tf, brush, tx, (float)Math.Round((size.Height - ts.Height) / 2f), fmt);
                            }
                        }
                        x += widths[i] + between;
                    }
                }
                Apply(bmp);
            }
        }
        lastBounds = Rectangle.Empty;
        Place();
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d - 1, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void Apply(Bitmap bmp)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(mem, hbmp);
        try
        {
            var sz = new SIZE(bmp.Width, bmp.Height);
            var src = new POINT(0, 0);
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref sz, mem, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    string TipText()
    {
        if (devices.Count == 0)
            return allDevices.Count > 0 ? "All devices are hidden (right-click → Devices)"
                                        : "No connected Bluetooth devices with battery info";
        var sb = new StringBuilder();
        foreach (var d in devices)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(d.Name).Append(": ").Append(d.Battery).Append('%');
        }
        return sb.ToString();
    }

    void ShowTip()
    {
        var r = Bounds();
        RECT tbr;
        int top = GetWindowRect(taskbar, out tbr) ? tbr.Top : r.Top;
        tip.ShowAt(TipText(), r.Left + r.Width / 2, top - (int)(8 * Scale()), LightTaskbar());
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = new IntPtr(MA_NOACTIVATE);
                return;
            case WM_MOUSEMOVE:
            {
                if (!tracking)
                {
                    var t = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = TME_LEAVE, hwndTrack = Handle };
                    TrackMouseEvent(ref t);
                    tracking = true;
                }
                if (!hovered)
                {
                    hovered = true;
                    Render();
                    ShowTip();
                }
                break;
            }
            case WM_MOUSELEAVE:
                tracking = false;
                if (hovered) { hovered = false; Render(); }
                tip.Hide();
                break;
            case WM_LBUTTONUP:
                RefreshDevices();
                break;
            case WM_LBUTTONDBLCLK:
                tip.Hide();
                OpenBluetoothSettings();
                break;
            case WM_RBUTTONUP:
                tip.Hide();
                // A popup menu only behaves (submenus open on hover, clicking elsewhere closes it)
                // when its app is in the foreground, so activate our hidden window first.
                SetForegroundWindow(deviceListener.Handle);
                menu.Show(Cursor.Position);
                return;
        }
        base.WndProc(ref m);
    }

    static void OpenBluetoothSettings() { OpenUrl("ms-settings:bluetooth"); }

    static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(url); } catch { }
    }

    static bool IsStartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunName) != null;
    }

    static void ToggleStartup()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (k.GetValue(RunName) != null) k.DeleteValue(RunName, false);
            else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
        }
    }

    public void Dispose()
    {
        refreshTimer.Dispose();
        posTimer.Dispose();
        changeTimer.Dispose();
        deviceListener.Dispose();
        tip.Dispose();
        alerts.Dispose();
        updates.Dispose();
        menu.Dispose();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
