using System;
using System.Runtime.InteropServices;
using System.Threading;

class ImeMonitor
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint idThread);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] static extern bool ImmGetConversionStatus(IntPtr hIMC, out uint conv, out uint sent);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
    [DllImport("user32.dll")] static extern short GetKeyState(int nVirtKey);

    const uint IME_CMODE_NATIVE = 0x0001;

    static string Label(IntPtr hkl, IntPtr himc, uint conv)
    {
        ulong h = (ulong)hkl;
        string ime = himc == IntPtr.Zero ? "NULL" : "OK";
        bool cn = (conv & IME_CMODE_NATIVE) != 0;
        return $"HKL=0x{h:X8} himc={ime} CN={cn}";
    }

    static (IntPtr hkl, IntPtr himc, uint conv) Snap()
    {
        var hwnd = GetForegroundWindow();
        var tid = GetWindowThreadProcessId(hwnd, out _);
        var hkl = GetKeyboardLayout(tid);
        var himc = ImmGetContext(hwnd);
        uint conv = 0;
        if (himc != IntPtr.Zero) { ImmGetConversionStatus(himc, out conv, out _); ImmReleaseContext(hwnd, himc); }
        return (hkl, himc, conv);
    }

    static void Main()
    {
        Console.WriteLine("Monitor running. Switch IME (Win+Space) or toggle CN/EN (Shift)...");
        Console.WriteLine("Press Ctrl+C to exit.");
        var (lh, lm, lc) = Snap();
        Console.WriteLine($"Initial: {Label(lh, lm, lc)}");

        while (true)
        {
            Thread.Sleep(300);
            var (ch, cm, cc) = Snap();
            if (ch != lh || cm != lm || cc != lc)
            {
                Console.WriteLine($"CHANGED: {Label(ch, cm, cc)}  (was {Label(lh, lm, lc)})");
                (lh, lm, lc) = (ch, cm, cc);
            }
        }
    }
}
