using System.Diagnostics;
using System.Runtime.InteropServices;
using WinAssistant.Models;

namespace WinAssistant.Services;

public class HotKeyService : IDisposable
{
    private nint _hwnd = nint.Zero;
    private nint _oldWndProc = nint.Zero;
    private WndProcDelegate? _wndProcDelegate;
    private readonly Dictionary<int, HotKeyBinding> _hotKeyMap = [];
    private int _nextId = 1;
    private bool _initialized;

    private const int WM_HOTKEY = 0x0312;
    private const int GWLP_WNDPROC = -4;

    public event EventHandler<HotKeyBinding>? HotKeyPressed;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    public void Initialize(nint parentHwnd)
    {
        if (_initialized) return;

        _hwnd = parentHwnd;
        _wndProcDelegate = WndProcHook;
        var newProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        SetLastError(0);
        var prev = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, newProc);
        if (prev == nint.Zero && Marshal.GetLastWin32Error() != 0)
        {
            // SetWindowLongPtr genuinely failed — don't mark as initialized
            _wndProcDelegate = null;
            return;
        }
        _oldWndProc = prev;
        _initialized = true;
    }

    private nint WndProcHook(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_HOTKEY)
        {
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
            }
        }

        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
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

    /// <summary>
    /// Find a registered binding already using this key combination, excluding a given ID.
    /// Returns null if no conflict.
    /// </summary>
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
        if (_initialized && _hwnd != nint.Zero && _oldWndProc != nint.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldWndProc);
        }
        _initialized = false;
        _hwnd = nint.Zero;
        _wndProcDelegate = null;
    }

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint dwErrCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
