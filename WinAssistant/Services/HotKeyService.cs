using System.Diagnostics;
using System.Runtime.InteropServices;
using WinAssistant.Models;

namespace WinAssistant.Services;

public class HotKeyService : IDisposable
{
    private nint _hwnd = nint.Zero;
    private readonly Dictionary<int, HotKeyBinding> _hotKeyMap = [];
    private int _nextId = 1;
    private bool _initialized;

    private const int WM_HOTKEY = 0x0312;

    public event EventHandler<HotKeyBinding>? HotKeyPressed;

    public void Initialize(nint parentHwnd)
    {
        if (_initialized) return;
        _hwnd = parentHwnd;
        _initialized = true;
    }

    /// <summary>
    /// Called from MainWindow's WndProc when it receives a WM_HOTKEY message.
    /// Returns true if the message was handled.
    /// </summary>
    internal bool OnWindowMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg != WM_HOTKEY) return false;

        var hotKeyId = wParam.ToInt32();
        if (_hotKeyMap.TryGetValue(hotKeyId, out var binding))
        {
            try
            {
                HotKeyPressed?.Invoke(this, binding);
            }
            catch
            {
                // Swallow — WndProc must never throw
            }
            return true;
        }
        return false;
    }

    public int Register(HotKeyBinding binding)
    {
        if (_hwnd == nint.Zero) return -1;

        var id = _nextId++;
        var success = RegisterHotKey(_hwnd, id, binding.Modifiers, binding.VirtualKey);
        if (success)
        {
            binding.HotKeyId = id;
            _hotKeyMap[id] = binding;
            Debug.WriteLine($"HotKey registered: ID={id}, App={binding.Name}, Combo={binding.HotKeyDisplay}");
            return id;
        }

        Debug.WriteLine($"Failed to register hotkey for {binding.Name} (conflict or invalid key)");
        return -1;
    }

    public bool Unregister(int hotKeyId)
    {
        if (_hotKeyMap.Remove(hotKeyId))
        {
            return UnregisterHotKey(_hwnd, hotKeyId);
        }
        return false;
    }

    public void UnregisterAll()
    {
        foreach (var id in _hotKeyMap.Keys.ToArray())
        {
            UnregisterHotKey(_hwnd, id);
        }
        _hotKeyMap.Clear();
    }

    public HotKeyBinding? FindConflict(uint modifiers, uint virtualKey, int excludeHotKeyId = -1)
    {
        foreach (var binding in _hotKeyMap.Values)
        {
            if (binding.HotKeyId != excludeHotKeyId &&
                binding.Modifiers == modifiers &&
                binding.VirtualKey == virtualKey)
            {
                return binding;
            }
        }
        return null;
    }

    public void Refresh(List<HotKeyBinding> bindings)
    {
        UnregisterAll();
        foreach (var binding in bindings.Where(b => b.IsEnabled && b.Modifiers != 0 && b.VirtualKey != 0))
        {
            Register(binding);
        }
    }

    public void Dispose()
    {
        UnregisterAll();
        _initialized = false;
        _hwnd = nint.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
