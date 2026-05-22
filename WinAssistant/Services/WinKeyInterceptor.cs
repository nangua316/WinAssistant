using System.Runtime.InteropServices;

namespace WinAssistant.Services;

public class WinKeyInterceptor : IDisposable
{
    private nint _hookId;
    private LowLevelKeyboardProc? _proc;
    private bool _winHeld;
    private int _activeWinVk;
    private bool _comboUsed;
    private bool _synthesizing;
    private readonly HashSet<int> _blockedKeys = [];

    public event EventHandler? WinKeyPressed;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    public bool IsRunning => _hookId != nint.Zero;

    public void Start()
    {
        if (_hookId != nint.Zero) return;
        _proc = HookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var mainModule = curProcess.MainModule;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(mainModule!.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hookId == nint.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = nint.Zero;
        _proc = null;
        _winHeld = false;
        _activeWinVk = 0;
        _comboUsed = false;
        _blockedKeys.Clear();
    }

    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        // Let our own synthesized input pass through
        if (_synthesizing)
            return CallNextHookEx(nint.Zero, nCode, wParam, lParam);

        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            bool isWinKey = vkCode == VK_LWIN || vkCode == VK_RWIN;

            if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN)
            {
                if (isWinKey)
                {
                    // Track only the first Win key
                    if (!_winHeld)
                    {
                        _winHeld = true;
                        _activeWinVk = vkCode;
                        _comboUsed = false;
                    }
                    _blockedKeys.Add(vkCode);
                    return (nint)1; // BLOCK — Start Menu won't open
                }

                if (_winHeld && !_comboUsed)
                {
                    // Another key pressed while Win held — it's a system combo
                    _comboUsed = true;
                    _blockedKeys.Add(vkCode);
                    SendCombo(vkCode);
                    return (nint)1; // BLOCK — we're synthesizing it
                }
            }
            else if (wParam == WM_KEYUP || wParam == WM_SYSKEYUP)
            {
                if (isWinKey && _blockedKeys.Contains(vkCode))
                {
                    _blockedKeys.Remove(vkCode);
                    if (_winHeld && _activeWinVk == vkCode)
                    {
                        _winHeld = false;
                        _activeWinVk = 0;
                        if (!_comboUsed)
                            App.DispatcherQueue.TryEnqueue(() => WinKeyPressed?.Invoke(this, EventArgs.Empty));
                    }
                    return (nint)1; // BLOCK — matched the blocked down
                }

                // If a combo key's up comes while we're tracking
                if (_blockedKeys.Contains(vkCode))
                {
                    _blockedKeys.Remove(vkCode);
                    return (nint)1; // BLOCK — matched blocked down
                }
            }
        }

        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private void SendCombo(int otherVk)
    {
        _synthesizing = true;
        try
        {
            // Use the actual Win key that was pressed (left or right) instead of
            // hard-coding VK_LWIN, so apps that distinguish LWin/RWin behave correctly.
            var winVk = (ushort)_activeWinVk;
            if (winVk == 0) winVk = (ushort)VK_LWIN; // fallback

            Span<INPUT> inputs = stackalloc INPUT[4];

            inputs[0] = new() { type = INPUT_KEYBOARD };
            inputs[0].U.ki = new() { wVk = winVk, dwFlags = KEYEVENTF_KEYDOWN };

            inputs[1] = new() { type = INPUT_KEYBOARD };
            inputs[1].U.ki = new() { wVk = (ushort)otherVk, dwFlags = KEYEVENTF_KEYDOWN };

            inputs[2] = new() { type = INPUT_KEYBOARD };
            inputs[2].U.ki = new() { wVk = (ushort)otherVk, dwFlags = KEYEVENTF_KEYUP };

            inputs[3] = new() { type = INPUT_KEYBOARD };
            inputs[3].U.ki = new() { wVk = winVk, dwFlags = KEYEVENTF_KEYUP };

            SendInput(4, ref inputs[0], Marshal.SizeOf<INPUT>());
        }
        finally
        {
            _synthesizing = false;
        }
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

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    #endregion
}
