using System;
using System.Runtime.InteropServices;
using System.Text;

// ActivatorHelper.exe <hwnd> <pid>
// keybd_event Ctrl+Alt+W → hide tray window → activate main Qt window

var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinAssistant_dbg.txt");
void Log(string m) { try { System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] H: {m}{Environment.NewLine}"); } catch { } }

int pid = int.Parse(args[1]);
Log($"start pid={pid}");

Native.AllowSetForegroundWindow(0xFFFFFFFF);
Native.keybd_event(0x11, 0, 0, 0);
Native.keybd_event(0x12, 0, 0, 0);
Native.keybd_event(0x57, 0, 0, 0);
Native.keybd_event(0x57, 0, 2, 0);
Native.keybd_event(0x12, 0, 2, 0);
Native.keybd_event(0x11, 0, 2, 0);
System.Threading.Thread.Sleep(200);

nint mainHwnd = nint.Zero;
Native.EnumWindows((h, _) =>
{
    Native.GetWindowThreadProcessId(h, out uint p);
    if (p != pid) return true;
    var sb = new StringBuilder(256);
    Native.GetClassNameW(h, sb, sb.Capacity);
    var c = sb.ToString();
    if (c.Contains("Tray")) { Native.ShowWindow(h, 0); return true; }
    if (c.StartsWith("Qt") && mainHwnd == nint.Zero) mainHwnd = h;
    return true;
}, nint.Zero);

if (mainHwnd != nint.Zero) { Native.SetForegroundWindow(mainHwnd); Native.SwitchToThisWindow(mainHwnd, true); }
Log(mainHwnd != nint.Zero ? "ok" : "no window");

static class Native
{
    [DllImport("user32.dll")] public static extern void keybd_event(byte v, byte s, uint f, UIntPtr d);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void GetWindowThreadProcessId(nint h, out uint p);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint h, int c);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(nint h, bool a);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc f, nint p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint h, StringBuilder s, int n);
    public delegate bool EnumWindowsProc(nint h, nint p);
}
