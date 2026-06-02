using System.Runtime.InteropServices;
using WinAssistant.Helpers;

namespace WinAssistant.Services;

/// <summary>
/// 全局键盘钩子服务。
/// - CapsLock 检测
/// </summary>
public class KeyboardHookService : IDisposable
{
    private nint _hookId = nint.Zero;
    private LowLevelKeyboardProc? _proc;
    private Thread? _hookThread;

    // CapsLock
    private bool _lastCapsState;

    // Win/Alt key tracking
    private bool _leftWinDown, _rightWinDown, _altDown;

    private static readonly string LogPath = @"C:\Users\likan\AppData\Local\Temp\kb_hook.log";
    private static void Log(string m) { try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {m}\n"); } catch { } }

    public void Start()
    {
        _lastCapsState = IsCapsLockOn();
        Log("Hook Start()");

        _hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "KeyboardHook"
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
    }

    private void HookThreadProc()
    {
        _proc = HookCallback;
        using var module = System.Diagnostics.Process.GetCurrentProcess().MainModule;
        var handle = GetModuleHandleW(module?.ModuleName);
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, handle, 0);
        Log($"SetWindowsHookEx = 0x{_hookId:X8}");

        while (GetMessageW(out MSG msg, nint.Zero, 0, 0)) { }
        UnhookWindowsHookEx(_hookId);
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);

            // Track modifier keys
            if (vkCode == 0x5B) _leftWinDown = (wParam == WM_KEYDOWN);
            else if (vkCode == 0x5C) _rightWinDown = (wParam == WM_KEYDOWN);
            else if (vkCode == 0x12) _altDown = (wParam == WM_KEYDOWN);

            // CapsLock toggle
            if (vkCode == 0x14 && wParam == WM_KEYDOWN)
            {
                bool newState = IsCapsLockOn();
                if (newState != _lastCapsState)
                {
                    _lastCapsState = newState;
                    var msg = newState ? "大写锁定已开启" : "大写锁定已关闭";
                    App.DispatcherQueue.TryEnqueue(() => HotKeyToast.Show("CapsLock", msg));
                }
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // Note: Shift standalone IME toggle was removed.
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static bool IsCapsLockOn() => (GetKeyState(0x14) & 1) == 1;

    public void Dispose()
    {
        if (_hookId != nint.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = nint.Zero;
        }
    }

    private const int WH_KEYBOARD_LL = 13;
    private const nint WM_KEYDOWN = 0x0100;
    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern bool GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
}
