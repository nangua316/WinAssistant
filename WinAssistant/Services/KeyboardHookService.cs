using System.Runtime.InteropServices;
using WinAssistant.Helpers;

namespace WinAssistant.Services;

/// <summary>
/// 全局键盘钩子服务。
/// - CapsLock 检测
/// - Win+Space 输入法切换检测
/// - 任意按键后的 IME 状态变化检测（Shift 中英文切换、全角/半角切换等）
/// </summary>
public class KeyboardHookService : IDisposable
{
    private nint _hookId = nint.Zero;
    private LowLevelKeyboardProc? _proc;
    private Thread? _hookThread;

    // Key state tracking
    private bool _lastCapsState;
    private bool _leftWinDown, _rightWinDown, _altDown, _ctrlDown;

    // Throttle IME state checks
    private DateTime _lastImeCheckTime = DateTime.MinValue;
    private static readonly TimeSpan ImeCheckThrottle = TimeSpan.FromMilliseconds(150);

    private static readonly string LogPath = @"C:\Users\likan\AppData\Local\Temp\kb_hook.log";
    private static void Log(string m) { try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {m}\n"); } catch { } }

    /// <summary>
    /// 当检测到输入法状态可能变化时触发（供 ImeService 使用）。
    /// </summary>
    public event Action? ImeStateMayHaveChanged;

    /// <summary>
    /// 当检测到 Win+Space 输入法切换时触发。
    /// </summary>
    public event Action? WinSpaceDetected;

    /// <summary>
    /// 纯 Shift 按键（不含 Ctrl/Alt+Shift），用于 CN/EN 切换检测。
    /// </summary>
    public event Action? ShiftToggled;

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
            bool isKeyDown = (wParam == WM_KEYDOWN);
            bool isKeyUp = (wParam == WM_KEYUP);

            // Track modifier keys (handle both generic and left/right-specific VK codes)
            if (vkCode == 0x5B) _leftWinDown = isKeyDown;
            else if (vkCode == 0x5C) _rightWinDown = isKeyDown;
            else if (vkCode == 0x12 || vkCode == 0xA4 || vkCode == 0xA5) _altDown = isKeyDown;
            else if (vkCode == 0x11 || vkCode == 0xA2 || vkCode == 0xA3) _ctrlDown = isKeyDown;

            bool winDown = _leftWinDown || _rightWinDown;

            // ── CapsLock toggle ──
            if (vkCode == 0x14 && isKeyDown)
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

            // ── Win+Space: IME switch ──
            if (winDown && vkCode == 0x20 && isKeyUp)
            {
                Log("Win+Space detected");
                App.DispatcherQueue.TryEnqueue(() => WinSpaceDetected?.Invoke());
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // ── Ctrl+Space: IME switch (toggle last 2 IMEs) ──
            if (!winDown && _ctrlDown && !_altDown && vkCode == 0x20 && isKeyUp)
            {
                Log("Ctrl+Space detected");
                App.DispatcherQueue.TryEnqueue(() => WinSpaceDetected?.Invoke());
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            // ── Ctrl+Shift: IME switch (只有 Ctrl+Shift 会切输入法，Alt+Shift 不会) ──
            if (isKeyUp && (vkCode == 0x10 || vkCode == 0xA0 || vkCode == 0xA1) && _ctrlDown && !_altDown)
            {
                Log("Ctrl+Shift detected");
                App.DispatcherQueue.TryEnqueue(() => WinSpaceDetected?.Invoke());
            }
            else if (isKeyUp && (vkCode == 0x10 || vkCode == 0xA0 || vkCode == 0xA1) && !_ctrlDown && !_altDown)
            {
                // Plain Shift → CN/EN toggle
                App.DispatcherQueue.TryEnqueue(() => ShiftToggled?.Invoke());
            }

            // Debug: log Win/Space/Ctrl/Alt/Shift events
            if (vkCode == 0x5B || vkCode == 0x5C || vkCode == 0x20 || vkCode == 0x11 || vkCode == 0xA2 || vkCode == 0xA3 || vkCode == 0x12 || vkCode == 0xA4 || vkCode == 0xA5 || vkCode == 0x10 || vkCode == 0xA0 || vkCode == 0xA1)
                Log($"key vk=0x{vkCode:X2}({VkName(vkCode)}) down={isKeyDown} up={isKeyUp} win={winDown} alt={_altDown} ctrl={_ctrlDown}");

            // ── IME 状态变化检测（Shift 键已由 ShiftToggled/Ctrl+Shift 处理，不再重复触发）──
            // 在每次非 Shift 按键弹起时检测输入法状态（Shift 中英文切换、全半角等）
            // 用节流控制避免频繁检测
            if (isKeyUp && !(vkCode == 0x10 || vkCode == 0xA0 || vkCode == 0xA1))
            {
                var now = DateTime.UtcNow;
                if (now - _lastImeCheckTime >= ImeCheckThrottle)
                {
                    _lastImeCheckTime = now;
                    ScheduleImeCheck(60);
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void ScheduleImeCheck(int delayMs)
    {
        _ = Task.Delay(delayMs).ContinueWith(_ =>
        {
            try { ImeStateMayHaveChanged?.Invoke(); } catch { }
        });
    }

    private static bool IsCapsLockOn() => (GetKeyState(0x14) & 1) == 1;

    private static string VkName(int vk) => vk switch
    {
        0x5B => "LWIN", 0x5C => "RWIN",
        0x11 => "CTRL", 0xA2 => "LCTRL", 0xA3 => "RCTRL",
        0x12 => "ALT", 0xA4 => "LALT", 0xA5 => "RALT",
        0x10 => "SHIFT", 0xA0 => "LSHIFT", 0xA1 => "RSHIFT",
        0x20 => "SPACE",
        _ => "?"
    };

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
    private const nint WM_KEYUP = 0x0101;
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
