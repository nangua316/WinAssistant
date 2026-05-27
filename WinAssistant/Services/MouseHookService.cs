using System.Runtime.InteropServices;

namespace WinAssistant.Services;

public class MouseHookService : IDisposable
{
    private nint _hookId;
    private LowLevelMouseProc? _proc;
    private bool _trackMiddle;
    private bool _trackX1;
    private bool _trackX2;

    public event EventHandler? MiddleButtonClicked;
    public event EventHandler? XButton1Clicked;
    public event EventHandler? XButton2Clicked;

    public bool IsRunning => _hookId != nint.Zero;

    public void Start(bool middle, bool x1, bool x2)
    {
        if (_hookId != nint.Zero) return;

        _trackMiddle = middle;
        _trackX1 = x1;
        _trackX2 = x2;

        if (!middle && !x1 && !x2) return;

        _proc = HookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var mainModule = curProcess.MainModule;
        if (mainModule != null)
            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc,
                GetModuleHandle(mainModule.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hookId == nint.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
        _proc = null;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            switch ((uint)wParam)
            {
                case WM_MBUTTONDOWN:
                    if (_trackMiddle)
                        App.DispatcherQueue.TryEnqueue(() => MiddleButtonClicked?.Invoke(this, EventArgs.Empty));
                    break;

                case WM_XBUTTONDOWN:
                    var msll = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var button = (ushort)(msll.mouseData >> 16);
                    if (button == XBUTTON1 && _trackX1)
                        App.DispatcherQueue.TryEnqueue(() => XButton1Clicked?.Invoke(this, EventArgs.Empty));
                    else if (button == XBUTTON2 && _trackX2)
                        App.DispatcherQueue.TryEnqueue(() => XButton2Clicked?.Invoke(this, EventArgs.Empty));
                    break;
            }
        }
        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();

    #region P/Invoke

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string lpModuleName);

    private const int WH_MOUSE_LL = 14;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const ushort XBUTTON1 = 0x0001;
    private const ushort XBUTTON2 = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    #endregion
}
