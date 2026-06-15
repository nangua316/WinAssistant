using System;
using System.Runtime.InteropServices;
using System.Threading;
class QI {
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    static void Main() {
        var fg = GetForegroundWindow();
        var iw = ImmGetDefaultIMEWnd(fg);
        Console.WriteLine("imeWnd=0x{0:X8}", (ulong)iw);
        if (iw != IntPtr.Zero) {
            var r = SendMessageW(iw, 0x0283, 1, IntPtr.Zero);
            Console.WriteLine("conv=0x{0:X8} ({1})", (ulong)r, ((int)r & 1) != 0 ? "CN" : "EN");
        }
        Console.WriteLine("Press Ctrl+C to stop. Switch IME now...");
        int last = -1;
        while (true) {
            Thread.Sleep(150);
            fg = GetForegroundWindow();
            iw = ImmGetDefaultIMEWnd(fg);
            if (iw == IntPtr.Zero) continue;
            int r = (int)SendMessageW(iw, 0x0283, 1, IntPtr.Zero);
            if (r >= 0 && r != last) { last = r; Console.WriteLine("{0} (0x{1:X})", ((r & 1) != 0 ? "CN" : "EN"), r); }
        }
    }
}
