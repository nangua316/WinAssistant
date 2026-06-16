using System.Runtime.InteropServices;

namespace WinAssistant.Services;

public class MouseHookService : IDisposable
{
    private nint _hookId;
    private LowLevelMouseProc? _proc;
    private Thread? _hookThread;
    private int _hookThreadId;
    private bool _trackMiddle;
    private bool _trackX1;
    private bool _trackX2;
    private long _lastXButtonTick;
    private readonly object _lock = new();

    public event EventHandler? MiddleButtonClicked;
    public event EventHandler? XButton1Clicked;
    public event EventHandler? XButton2Clicked;

    public bool IsRunning
    {
        get { lock (_lock) return _hookId != nint.Zero; }
    }

    public long LastXButtonTick => _lastXButtonTick;

    public void Start(bool middle, bool x1, bool x2)
    {
        lock (_lock)
        {
            if (_hookId != nint.Zero) return;
            _trackMiddle = middle;
            _trackX1 = x1;
            _trackX2 = x2;
        }

        if (!middle && !x1 && !x2) return;

        _hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "MouseHook"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
    }

    private void HookThreadProc()
    {
        _proc = HookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var mainModule = curProcess.MainModule;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc,
            GetModuleHandle(mainModule?.ModuleName), 0);
        _hookThreadId = GetCurrentThreadId();

        while (GetMessageW(out MSG msg, nint.Zero, 0, 0)) { }

        if (_hookId != nint.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = nint.Zero;
        }
    }

    public void Stop()
    {
        lock (_lock) { _trackMiddle = false; _trackX1 = false; _trackX2 = false; }

        if (_hookId != nint.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = nint.Zero;
        }

        if (_hookThreadId != 0)
        {
            PostThreadMessageW(_hookThreadId, WM_QUIT, 0, 0);
            _hookThread?.Join(1000);
            _hookThreadId = 0;
        }
        _hookThread = null;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            switch ((uint)wParam)
            {
                case WM_LBUTTONDOWN:
                case WM_RBUTTONDOWN:
                case WM_MBUTTONDOWN:
                    if ((uint)wParam == WM_MBUTTONDOWN && _trackMiddle)
                        App.DispatcherQueue.TryEnqueue(() => MiddleButtonClicked?.Invoke(this, EventArgs.Empty));
                    break;

                case WM_XBUTTONDOWN:
                    var msll = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var button = (ushort)(msll.mouseData >> 16);
                    _lastXButtonTick = Environment.TickCount64;
                    if (button == XBUTTON1 && _trackX1)
                        App.DispatcherQueue.TryEnqueue(() => XButton1Clicked?.Invoke(this, EventArgs.Empty));
                    else if (button == XBUTTON2 && _trackX2)
                        App.DispatcherQueue.TryEnqueue(() => XButton2Clicked?.Invoke(this, EventArgs.Empty));
                    break;
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();

    #region P/Invoke

    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);

    private const int WH_MOUSE_LL = 14;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const ushort XBUTTON1 = 0x0001;
    private const ushort XBUTTON2 = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int ptX; public int ptY; }

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

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(int threadId, uint msg, nint wParam, nint lParam);

    #endregion
}
