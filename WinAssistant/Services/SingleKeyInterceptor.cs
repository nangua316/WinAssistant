using System.Runtime.InteropServices;

namespace WinAssistant.Services;

/// <summary>
/// Detects when a specific key is pressed and released alone (no other key
/// pressed while it is held). Uses GetAsyncKeyState polling instead of a
/// low-level keyboard hook to avoid Windows silently removing the hook.
/// </summary>
public class SingleKeyInterceptor : IDisposable
{
    private Thread? _pollThread;
    private volatile bool _running;
    private volatile bool _keyWasDown;
    private bool _confirmDown;
    private bool _comboUsed;
    private int _targetVk;

    public event EventHandler? Triggered;

    public bool IsRunning => _running;

    public void Start(int virtualKey)
    {
        if (_running) return;
        _targetVk = virtualKey;
        _running = true;
        _keyWasDown = false;
        _confirmDown = false;
        _comboUsed = false;
        _pollThread = new Thread(PollLoop) { IsBackground = true };
        _pollThread.Start();
    }

    public void Stop()
    {
        _running = false;
        _pollThread = null;
        _keyWasDown = false;
        _confirmDown = false;
        _comboUsed = false;
    }

    private void PollLoop()
    {
        while (_running)
        {
            bool isDown = IsKeyDown(_targetVk);

            if (isDown && !_keyWasDown)
            {
                if (_confirmDown)
                {
                    _keyWasDown = true;
                    _confirmDown = false;
                }
                else
                {
                    _confirmDown = true;
                }
            }
            else if (isDown && _keyWasDown)
            {
                // Ctrl is held — detect if any other key is also pressed (combo)
                _confirmDown = false;
                if (!_comboUsed && IsAnyComboKeyDown())
                    _comboUsed = true;
            }
            else if (!isDown && _keyWasDown)
            {
                _keyWasDown = false;
                _confirmDown = false;
                bool wasCombo = _comboUsed;
                _comboUsed = false;
                if (!wasCombo && !IsKeyDown(VK_MENU) && !IsKeyDown(VK_SHIFT) &&
                    !IsKeyDown(VK_LWIN) && !IsKeyDown(VK_RWIN))
                {
                    App.DispatcherQueue?.TryEnqueue(() => Triggered?.Invoke(this, EventArgs.Empty));
                }
            }
            else
            {
                _confirmDown = false;
                _comboUsed = false;
            }

            Thread.Sleep(30);
        }
    }

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Check if any key other than Ctrl is pressed (prevents false triggers
    /// when Ctrl is used in regular shortcuts like Ctrl+Backspace, Ctrl+Space, etc.).</summary>
    private static bool IsAnyComboKeyDown()
    {
        // Skip VK_CONTROL itself (0x11), and modifiers (Alt/Shift/Win) since
        // those are checked separately in the release logic.
        for (int vk = 0x08; vk <= 0xFE; vk++)
        {
            if (vk == 0x11 || vk == VK_MENU || vk == VK_SHIFT ||
                vk == VK_LWIN || vk == VK_RWIN) continue;
            if (IsKeyDown(vk)) return true;
        }
        return false;
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // Virtual-key codes for modifier keys
    private const int VK_MENU = 0x12;    // Alt
    private const int VK_SHIFT = 0x10;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
}
