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
                if (!wasCombo &&
                    !IsKeyDown(VK_MENU) && !IsKeyDown(VK_SHIFT) &&
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

    /// <summary>Check if a key is currently down OR was pressed recently.
    /// Uses the 0x0001 bit (key has been pressed since last read) to catch
    /// fast combos where the key is released between 30ms polls.</summary>
    private static bool IsKeyDownOrWasPressed(int vk) =>
        (GetAsyncKeyState(vk) & 0x8001) != 0;

    /// <summary>Check if any key other than Ctrl is pressed (prevents false triggers
    /// when Ctrl is used in regular shortcuts like Ctrl+C, Ctrl+Space, etc.).</summary>
    private static bool IsAnyComboKeyDown()
    {
        // Common keys used with Ctrl shortcuts: A-Z, 0-9, F1-F12
        for (int vk = 0x30; vk <= 0x5A; vk++) // 0-9, A-Z
            if (IsKeyDownOrWasPressed(vk)) return true;
        for (int vk = 0x70; vk <= 0x7B; vk++) // F1-F12
            if (IsKeyDownOrWasPressed(vk)) return true;
        // Additional keys frequently combined with Ctrl in text editing
        if (IsKeyDownOrWasPressed(0x20)) return true; // Space
        if (IsKeyDownOrWasPressed(0x08)) return true; // Backspace
        if (IsKeyDownOrWasPressed(0x2E)) return true; // Delete
        if (IsKeyDownOrWasPressed(0x09)) return true; // Tab
        if (IsKeyDownOrWasPressed(0x0D)) return true; // Enter
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
