using System.Runtime.InteropServices;

namespace WinAssistant.Services;

/// <summary>
/// Detects when a specific key is pressed and released alone (no other key
/// pressed while it is held). Used for "single Ctrl press" trigger.
/// </summary>
public class SingleKeyInterceptor : IDisposable
{
    private nint _hookId;
    private LowLevelKeyboardProc? _proc;
    private int _targetVk;
    private bool _keyHeld;
    private bool _comboUsed;

    public event EventHandler? Triggered;

    public bool IsRunning => _hookId != nint.Zero;

    public void Start(int virtualKey)
    {
        if (_hookId != nint.Zero) return;

        _targetVk = virtualKey;
        _proc = HookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var mainModule = curProcess.MainModule;
        if (mainModule != null)
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
                GetModuleHandle(mainModule.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hookId == nint.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
        _proc = null;
        _keyHeld = false;
        _comboUsed = false;
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            bool isTarget = IsTargetKey(vkCode);

            if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN)
            {
                if (isTarget)
                {
                    if (_keyHeld)
                        _comboUsed = true;
                    _keyHeld = true;
                }
                else if (_keyHeld)
                {
                    _comboUsed = true;
                }
            }
            else if (wParam == WM_KEYUP || wParam == WM_SYSKEYUP)
            {
                if (isTarget && _keyHeld)
                {
                    _keyHeld = false;
                    if (!_comboUsed)
                        App.DispatcherQueue.TryEnqueue(() => Triggered?.Invoke(this, EventArgs.Empty));
                }
            }
        }

        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private bool IsTargetKey(int vkCode)
    {
        if (vkCode == _targetVk) return true;
        if (_targetVk == (int)Windows.System.VirtualKey.Control)
            return vkCode == VK_LCONTROL || vkCode == VK_RCONTROL;
        if (_targetVk == (int)Windows.System.VirtualKey.Menu)
            return vkCode == VK_LMENU || vkCode == VK_RMENU;
        if (_targetVk == (int)Windows.System.VirtualKey.Shift)
            return vkCode == VK_LSHIFT || vkCode == VK_RSHIFT;
        return false;
    }

    public void Dispose() => Stop();

    #region P/Invoke

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string lpModuleName);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;

    #endregion
}
