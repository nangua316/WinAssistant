using System.Runtime.InteropServices;
using System.Text;
using WinAssistant.Helpers;
using WinAssistant.Models;

namespace WinAssistant.Services;

/// <summary>
/// 输入法管理服务。
/// - 通过 WinEventHook 监听窗口切换，按规则自动切换输入法
/// - 查询当前输入法状态
/// - 提供输入法状态变化通知（供 KeyboardHookService 调用）
/// </summary>
public class ImeService : IDisposable
{
    #region WinEvent hooks

    private nint _foregroundHook;
    private WinEventDelegate? _foregroundDelegate;

    // EVENT_IME_CHANGE tracking (for TSF-only IMEs like Microsoft Pinyin/WeType)
    private nint _imeChangeHook;
    private WinEventDelegate? _imeChangeDelegate;
    private int _imeChangeMode;
    private DateTime _imeChangeTime = DateTime.MinValue;

    private bool _running;

    #endregion

    #region State tracking

    private string _lastProcessName = "";
    private nint _lastForegroundHwnd;
    private nint _pendingHwnd;

    // Last-known IME state (for change detection)
    private nint _lastHkl;
    private uint _lastConversion;
    private bool _lastCapsState;

    // Debounce
    private Timer? _debounceTimer;
    private readonly object _debounceLock = new();

    // Suppress change detection during our own switches
    private volatile int _suppressChangeCounter;
    private bool _lastConversionInitialized;

    #endregion

    #region Constants

    // WinEvent
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_IME_CHANGE = 0x0026;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // Process access
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // HKL flags
    private const uint KLF_ACTIVATE = 0x00000001;
    private const uint KLF_NOTELLSHELL = 0x00000080;
    private const uint KLF_SETFORPROCESS = 0x00000100;

    // Window messages
    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    private const uint WM_IME_CONTROL = 0x0283;
    private static readonly nint IMC_GETCONVERSIONSTATUS = 1;

    // IME conversion mode flags
    private const uint IME_CMODE_ALPHANUMERIC = 0x0000;
    private const uint IME_CMODE_NATIVE = 0x0001;
    private const uint IME_CMODE_FULLSHAPE = 0x0008;

    // Keyboard layout name length
    private const int KL_NAMELENGTH = 9;

    // Language IDs
    private const ushort LANG_CHINESE_SIMPLIFIED = 0x0804;
    private const ushort LANG_ENGLISH_US = 0x0409;

    #endregion

    #region Public API

    /// <summary>
    /// 启动窗口焦点监听。
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;

        // Query initial foreground
        var initialHwnd = GetForegroundWindow();
        if (initialHwnd != nint.Zero)
        {
            _lastForegroundHwnd = initialHwnd;
            _pendingHwnd = initialHwnd;
            QueryProcessName(initialHwnd, out _lastProcessName);
        }

        // Save initial IME state
        CaptureCurrentImeState();

        // Set up WinEvent hook for foreground changes
        _foregroundDelegate = OnWinEventProc;
        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            nint.Zero, _foregroundDelegate,
            0, 0, WINEVENT_OUTOFCONTEXT);

        if (_foregroundHook == nint.Zero)
            Logger.Log("IME", $"SetWinEventHook failed: 0x{Marshal.GetLastWin32Error():X8}");
        else
            Logger.Log("IME", "Foreground hook installed");

        // Set up WinEvent hook for IME mode changes (TSF-only IMEs)
        _imeChangeDelegate = OnImeChangeEvent;
        _imeChangeHook = SetWinEventHook(
            EVENT_IME_CHANGE, EVENT_IME_CHANGE,
            nint.Zero, _imeChangeDelegate,
            0, 0, WINEVENT_OUTOFCONTEXT);
        if (_imeChangeHook == nint.Zero)
            Logger.Log("IME", $"EVENT_IME_CHANGE hook failed: 0x{Marshal.GetLastWin32Error():X8}");
        else
            Logger.Log("IME", "EVENT_IME_CHANGE hook installed");
    }

    /// <summary>
    /// 停止服务。
    /// </summary>
    public void Stop()
    {
        _running = false;

        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        if (_foregroundHook != nint.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = nint.Zero;
        }
        if (_imeChangeHook != nint.Zero)
        {
            UnhookWinEvent(_imeChangeHook);
            _imeChangeHook = nint.Zero;
        }
    }

    /// <summary>
    /// 重新加载规则（从当前设置）。
    /// </summary>
    public void ReloadRules()
    {
        // Rules are loaded on-demand from settings
        Logger.Log("IME", "Rules reloaded");
    }

    /// <summary>
    /// 获取当前输入法状态信息（UI 线程安全）。
    /// </summary>
    public ImeStatusInfo GetCurrentStatus()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero)
            return new ImeStatusInfo { Error = "无法获取前台窗口" };

        var info = new ImeStatusInfo();
        QueryProcessName(hwnd, out var processName);
        info.ProcessName = processName;

        // Get window title
        var title = new StringBuilder(512);
        GetWindowTextW(hwnd, title, title.Capacity);
        info.WindowTitle = title.ToString();

        // Keyboard layout
        var threadId = GetWindowThreadProcessId(hwnd, out _);
        var hkl = GetKeyboardLayout(threadId);
        info.Hkl = hkl;

        var langId = (ushort)((nuint)hkl & 0xFFFF);
        info.LanguageId = langId;

        // Get KLID string
        var klid = GetKlidFromHkl(hkl);
        info.Klid = klid;

        // Get display name
        info.ImeDisplayName = GetLayoutDisplayName(klid);

        // IME conversion status
        var himc = ImmGetContext(hwnd);
        if (himc != nint.Zero)
        {
            ImmGetConversionStatus(himc, out uint conv, out _);
            info.IsChineseMode = (conv & IME_CMODE_NATIVE) != 0;
            info.IsFullWidth = (conv & IME_CMODE_FULLSHAPE) != 0;
            ImmReleaseContext(hwnd, himc);
        }

        // CapsLock
        info.IsCapsLock = (GetKeyState(0x14) & 1) == 1;

        // Language name
        info.LanguageName = langId switch
        {
            LANG_CHINESE_SIMPLIFIED => "中文",
            LANG_ENGLISH_US => "英文",
            _ => $"0x{langId:X4}"
        };

        return info;
    }

    /// <summary>
    /// 立即对指定窗口应用匹配的规则（手动触发）。
    /// </summary>
    public void ApplyRuleForWindow(nint hwnd)
    {
        ProcessForegroundChange(hwnd);
    }

    /// <summary>
    /// 清除最近一次 EVENT_IME_CHANGE 事件的时间戳（Win+Space 切换输入法后调用，防止误触 CN/EN toast）。
    /// </summary>
    public void ClearImeChangeEvent() => _imeChangeTime = DateTime.MinValue;

    /// <summary>
    /// 由 KeyboardHookService.ShiftToggled 调用：纯 Shift 键弹起时触发 CN/EN 切换检测。
    /// 对 TSF-only 输入法（ImmGetContext 返回 NULL），走内部状态翻转。
    /// IMM32 输入法由 CheckImeStateChanged（延迟 60ms）处理。
    /// </summary>
    public void OnShiftToggled()
    {
        if (!_running) return;
        if (!App.SettingsService.Load().IsImeToastEnabled) return;
        // 不检查 _suppressChangeCounter：Shift CN/EN 切换是用户主动行为，不应被抑制

        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero) return;

        var currentHkl = GetKeyboardLayout(GetWindowThreadProcessId(hwnd, out _));

        var himc = ImmGetContext(hwnd);
        if (himc != nint.Zero)
        {
            // IMM32 IME — read conversion status directly (CheckImeStateChanged 不再响应 Shift 键)
            ImmGetConversionStatus(himc, out uint conv, out _);
            ImmReleaseContext(hwnd, himc);

            Logger.Log("IME", $"IMM32 shift: conv=0x{conv:X8} lastInit={_lastConversionInitialized} lastConv=0x{_lastConversion:X8}");

            if (!_lastConversionInitialized)
            {
                _lastConversion = conv;
                _lastConversionInitialized = true;
                _lastHkl = currentHkl;
                Logger.Log("IME", "IMM32 shift: initialized");
                return;
            }

            if ((conv & IME_CMODE_NATIVE) != (_lastConversion & IME_CMODE_NATIVE))
            {
                _lastConversion = conv;
                bool cn = (conv & IME_CMODE_NATIVE) != 0;
                Logger.Log("IME", $"IMM32 shift: CN/EN → {(cn ? "中文" : "英文")}");
                App.DispatcherQueue.TryEnqueue(() =>
                    HotKeyToast.Show(cn ? "中文" : "英文"));
                return;
            }

            _lastConversion = conv;
            return;
        }

        // TSF-only IME — use internal state tracking
        if (!_lastConversionInitialized)
        {
            // First time: try to read initial state via IME window
            var initConv = TryGetConversionViaImeWindow(hwnd);
            if (initConv.HasValue)
            {
                _lastConversion = initConv.Value;
                _lastConversionInitialized = true;
                _lastHkl = currentHkl;
                Logger.Log("IME", $"TSF track: initialized to 0x{_lastConversion:X8}");
            }
            else
            {
                // Fallback: assume Chinese mode
                _lastConversion = IME_CMODE_NATIVE;
                _lastConversionInitialized = true;
                _lastHkl = currentHkl;
                Logger.Log("IME", "TSF track: initialized (default Chinese)");
            }
            return;
        }

        // Toggle CN/EN state internally
        _lastConversion ^= IME_CMODE_NATIVE; // flip the native bit
        bool isChinese = (_lastConversion & IME_CMODE_NATIVE) != 0;
        Logger.Log("IME", $"TSF track: CN/EN → {(isChinese ? "中文" : "英文")}");
        App.DispatcherQueue.TryEnqueue(() =>
            HotKeyToast.Show(isChinese ? "中文" : "英文"));
    }

    /// <summary>
    /// 由 KeyboardHookService 调用：输入法状态可能已变化，检查并显示 Toast。
    /// </summary>
    public void CheckImeStateChanged()
    {
        if (!_running) return;
        if (!App.SettingsService.Load().IsImeToastEnabled)
        {
            Logger.Log("IME", "Toast disabled in settings");
            return;
        }
        if (_suppressChangeCounter > 0) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero) return;

        var threadId = GetWindowThreadProcessId(hwnd, out _);
        var currentHkl = GetKeyboardLayout(threadId);
        var himc = ImmGetContext(hwnd);

        if (himc == nint.Zero)
        {
            // TSF-only IME (Microsoft Pinyin, WeType, etc.) — use EVENT_IME_CHANGE to detect CN/EN toggles
            var elapsed = DateTime.UtcNow - _imeChangeTime;
            if (elapsed.TotalMilliseconds < 500)
            {
                uint tsfConv = (uint)_imeChangeMode;
                Logger.Log("IME", $"EVENT_IME_CHANGE: mode=0x{tsfConv:X8} elapsed={elapsed.TotalMilliseconds:F0}ms lastInit={_lastConversionInitialized} lastConv=0x{_lastConversion:X8}");

                if (!_lastConversionInitialized)
                {
                    _lastConversion = tsfConv;
                    _lastConversionInitialized = true;
                    _lastHkl = currentHkl;
                    Logger.Log("IME", "EVENT_IME_CHANGE: initialized");
                    return;
                }

                // Check keyboard layout change
                if (currentHkl != _lastHkl)
                {
                    _lastHkl = currentHkl;
                    _lastConversion = tsfConv;
                    var klid = GetKlidFromHkl(currentHkl);
                    var name = GetLayoutDisplayName(klid);
                    Logger.Log("IME", $"EVENT_IME_CHANGE: HKL changed → {name}");
                    App.DispatcherQueue.TryEnqueue(() => HotKeyToast.Show("输入法", name));
                    return;
                }

                // Check Chinese/English mode change
                if ((tsfConv & IME_CMODE_NATIVE) != (_lastConversion & IME_CMODE_NATIVE))
                {
                    _lastConversion = tsfConv;
                    bool isChinese = (tsfConv & IME_CMODE_NATIVE) != 0;
                    Logger.Log("IME", $"EVENT_IME_CHANGE: CN/EN change → {(isChinese ? "中文" : "英文")}");
                    App.DispatcherQueue.TryEnqueue(() =>
                        HotKeyToast.Show(isChinese ? "中文" : "英文"));
                    return;
                }

                // Check full/half-width change
                if ((tsfConv & IME_CMODE_FULLSHAPE) != (_lastConversion & IME_CMODE_FULLSHAPE))
                {
                    _lastConversion = tsfConv;
                    bool isFull = (tsfConv & IME_CMODE_FULLSHAPE) != 0;
                    Logger.Log("IME", $"EVENT_IME_CHANGE: full/half change → {(isFull ? "全角" : "半角")}");
                    App.DispatcherQueue.TryEnqueue(() =>
                        HotKeyToast.Show(isFull ? "全角" : "半角"));
                    return;
                }

                _lastConversion = tsfConv;
                return;
            }

            // No recent EVENT_IME_CHANGE — check if HKL changed
            Logger.Log("IME", "TSF: no recent EVENT_IME_CHANGE");
            if (currentHkl != _lastHkl)
            {
                _lastHkl = currentHkl;
                var klid = GetKlidFromHkl(currentHkl);
                var name = GetLayoutDisplayName(klid);
                App.DispatcherQueue.TryEnqueue(() => HotKeyToast.Show("输入法", name));
            }
            return;
        }

        ImmGetConversionStatus(himc, out uint conv, out _);
        ImmReleaseContext(hwnd, himc);

        Logger.Log("IME", $"IMM32 conv=0x{conv:X8} lastInit={_lastConversionInitialized} lastConv=0x{_lastConversion:X8}");

        // 首次成功获取转换状态时静默初始化（兜底：Start 时可能没 IME 上下文）
        if (!_lastConversionInitialized)
        {
            _lastConversion = conv;
            _lastConversionInitialized = true;
            _lastHkl = currentHkl;
            Logger.Log("IME", "IMM32: initialized");
            return;
        }

        // Check keyboard layout change
        if (currentHkl != _lastHkl)
        {
            _lastHkl = currentHkl;
            _lastConversion = conv;
            var klid = GetKlidFromHkl(currentHkl);
            var name = GetLayoutDisplayName(klid);
            Logger.Log("IME", $"IMM32: HKL changed → {name}");
            App.DispatcherQueue.TryEnqueue(() => HotKeyToast.Show("输入法", name));
            return;
        }

        // Check Chinese/English mode change within same IME
        if (_lastConversionInitialized && (conv & IME_CMODE_NATIVE) != (_lastConversion & IME_CMODE_NATIVE))
        {
            _lastConversion = conv;
            bool isChinese = (conv & IME_CMODE_NATIVE) != 0;
            Logger.Log("IME", $"IMM32: CN/EN change → {(isChinese ? "中文" : "英文")}");
            App.DispatcherQueue.TryEnqueue(() =>
                HotKeyToast.Show(isChinese ? "中文" : "英文"));
            return;
        }

        // Check full/half-width change
        if (_lastConversionInitialized && (conv & IME_CMODE_FULLSHAPE) != (_lastConversion & IME_CMODE_FULLSHAPE))
        {
            _lastConversion = conv;
            bool isFull = (conv & IME_CMODE_FULLSHAPE) != 0;
            Logger.Log("IME", $"IMM32: full/half change → {(isFull ? "全角" : "半角")}");
            App.DispatcherQueue.TryEnqueue(() =>
                HotKeyToast.Show(isFull ? "全角" : "半角"));
            return;
        }

        _lastConversion = conv;
    }

    /// <summary>
    /// 临时抑制状态变化检测（在自动切换后调用）。
    /// </summary>
    public void SuppressChangeDetection()
    {
        Interlocked.Increment(ref _suppressChangeCounter);
        // Auto-release after 500ms
        _ = Task.Delay(500).ContinueWith(_ =>
        {
            if (Interlocked.Decrement(ref _suppressChangeCounter) < 0)
                Interlocked.Exchange(ref _suppressChangeCounter, 0);
        });
    }

    /// <summary>
    /// 枚举当前系统可用的键盘布局。
    /// </summary>
    public static List<(string Klid, string DisplayName)> EnumerateKeyboardLayouts()
    {
        var result = new List<(string Klid, string DisplayName)>();

        // 1. Enumerate loaded layouts
        var count = GetKeyboardLayoutList(0, null);
        if (count > 0)
        {
            var layouts = new nint[count];
            GetKeyboardLayoutList(count, layouts);
            var seen = new HashSet<string>();

            foreach (var hkl in layouts)
            {
                if (hkl == nint.Zero) continue;
                var klid = GetKlidFromHkl(hkl);
                if (string.IsNullOrEmpty(klid) || !seen.Add(klid)) continue;
                var name = GetLayoutDisplayName(klid);
                result.Add((klid, name));
            }
        }

        // 2. Also enumerate from registry preload list
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Keyboard Layout\Preload");
            if (key != null)
            {
                var seenKlids = new HashSet<string>(result.Select(r => r.Klid));
                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is string klid && !string.IsNullOrEmpty(klid))
                    {
                        klid = klid.PadLeft(8, '0');
                        if (seenKlids.Add(klid))
                        {
                            var name = GetLayoutDisplayName(klid);
                            result.Add((klid, name));
                        }
                    }
                }
            }
        }
        catch { }

        // Sort: Chinese layouts first, then others
        result.Sort((a, b) =>
        {
            bool aChinese = a.Klid.Contains("0804") || a.Klid.Contains("0C04");
            bool bChinese = b.Klid.Contains("0804") || b.Klid.Contains("0C04");
            if (aChinese != bChinese) return aChinese ? -1 : 1;
            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
        });

        return result;
    }

    /// <summary>
    /// 枚举有可见窗口的运行中进程。
    /// </summary>
    public static List<(string ProcessName, string WindowTitle)> EnumRunningProcesses()
    {
        var result = new List<(string ProcessName, string WindowTitle)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var hwnd = nint.Zero;
        EnumWindows((hw, _) =>
        {
            if (!IsWindowVisible(hw)) return true;
            var len = GetWindowTextLengthW(hw);
            if (len == 0) return true;

            var sb = new StringBuilder(512);
            GetWindowTextW(hw, sb, sb.Capacity);
            var title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hw, out uint pid);
            if (pid == 0) return true;

            var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == nint.Zero) return true;

            try
            {
                var exeName = new char[1024];
                var size = (uint)exeName.Length;
                if (QueryFullProcessImageNameW(hProcess, 0, exeName, ref size))
                {
                    var path = new string(exeName, 0, (int)size);
                    var fn = Path.GetFileName(path);
                    if (!string.IsNullOrEmpty(fn) && seen.Add(fn))
                        result.Add((fn, title));
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }

            return true;
        }, nint.Zero);

        result.Sort((a, b) => string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    #endregion

    #region Foreground change handling

    private void OnWinEventProc(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!_running) return;
        if (hwnd == nint.Zero || hwnd == _lastForegroundHwnd) return;

        // Debounce: reset timer on each foreground change
        lock (_debounceLock)
        {
            _pendingHwnd = hwnd;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => OnDebounceElapsed(), null, 300, Timeout.Infinite);
        }
    }

    private void OnImeChangeEvent(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // idObject = new conversion mode (IME_CMODE_* flags), idChild = sentence mode
        _imeChangeMode = idObject;
        _imeChangeTime = DateTime.UtcNow;
        Logger.Log("IME", $"EVENT_IME_CHANGE: obj=0x{idObject:X8} child=0x{idChild:X8}");
    }

    private void OnDebounceElapsed()
    {
        nint hwnd;
        lock (_debounceLock)
        {
            hwnd = _pendingHwnd;
            _pendingHwnd = nint.Zero;
        }

        if (hwnd == nint.Zero || hwnd == _lastForegroundHwnd) return;
        ProcessForegroundChange(hwnd);
    }

    private void ProcessForegroundChange(nint hwnd)
    {
        if (!_running) return;
        if (hwnd == nint.Zero) return;

        // Get process name
        if (!QueryProcessName(hwnd, out var processName) || string.IsNullOrEmpty(processName))
            return;

        // Same-process check: don't switch between windows of the same program
        if (string.Equals(processName, _lastProcessName, StringComparison.OrdinalIgnoreCase))
        {
            _lastForegroundHwnd = hwnd;
            return;
        }

        _lastForegroundHwnd = hwnd;
        _lastProcessName = processName;

        // Try to find a matching rule
        var settings = App.SettingsService.Load();
        if (!settings.IsImeAutoSwitchEnabled) return;

        var rule = settings.ImeRules.FirstOrDefault(r =>
            r.IsEnabled &&
            string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

        if (rule == null) return;

        Logger.Log("IME", $"Match: {processName} -> {rule.ImeDisplayName} (Klid={rule.Klid})");
        ApplyRule(rule, hwnd);
    }

    private void ApplyRule(ImeRule rule, nint hwnd)
    {
        try
        {
            // Suppress change detection during our own switches
            SuppressChangeDetection();

            // 1. Switch keyboard layout
            if (!string.IsNullOrEmpty(rule.Klid))
            {
                var hkl = LoadKeyboardLayoutW(rule.Klid, KLF_ACTIVATE | KLF_NOTELLSHELL);
                if (hkl != nint.Zero)
                {
                    // Request the foreground window to switch input
                    PostMessageW(hwnd, WM_INPUTLANGCHANGEREQUEST, nint.Zero, hkl);
                }
            }

            // 2. Set Chinese/English mode within the IME
            var himc = ImmGetContext(hwnd);
            if (himc != nint.Zero)
            {
                ImmGetConversionStatus(himc, out uint conv, out uint sent);

                if (rule.UseEnglishMode)
                    conv &= ~IME_CMODE_NATIVE;
                else
                    conv |= IME_CMODE_NATIVE;

                if (rule.UseFullWidth)
                    conv |= IME_CMODE_FULLSHAPE;
                else
                    conv &= ~IME_CMODE_FULLSHAPE;

                ImmSetConversionStatus(himc, conv, sent);
                ImmReleaseContext(hwnd, himc);
            }

            // 3. Set CapsLock
            bool currentCaps = (GetKeyState(0x14) & 1) == 1;
            if (rule.CapsLockState != currentCaps)
            {
                // Simulate CapsLock key press via SendInput
                SimulateCapsLockPress();
            }

            // Update tracked state
            CaptureCurrentImeState();
        }
        catch (Exception ex)
        {
            Logger.Log("IME", $"ApplyRule error: {ex.Message}");
        }
    }

    #endregion

    #region IME state helpers

    private void CaptureCurrentImeState()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero) return;

        var threadId = GetWindowThreadProcessId(hwnd, out _);
        _lastHkl = GetKeyboardLayout(threadId);

        var himc = ImmGetContext(hwnd);
        if (himc != nint.Zero)
        {
            ImmGetConversionStatus(himc, out _lastConversion, out _);
            ImmReleaseContext(hwnd, himc);
            _lastConversionInitialized = true;
        }

        _lastCapsState = (GetKeyState(0x14) & 1) == 1;
    }

    private void SimulateCapsLockPress()
    {
        var inputs = new INPUT[2];

        // Key down
        inputs[0].type = 1; // INPUT_KEYBOARD
        inputs[0].ki.wVk = 0x14; // VK_CAPITAL

        // Key up
        inputs[1].type = 1; // INPUT_KEYBOARD
        inputs[1].ki.wVk = 0x14;
        inputs[1].ki.dwFlags = 2; // KEYEVENTF_KEYUP

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    #endregion

    #region Helper methods

    private static bool QueryProcessName(nint hwnd, out string processName)
    {
        processName = "";
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;

        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == nint.Zero) return false;

        try
        {
            var exeName = new char[1024];
            var size = (uint)exeName.Length;
            if (QueryFullProcessImageNameW(hProcess, 0, exeName, ref size))
            {
                processName = Path.GetFileName(new string(exeName, 0, (int)size));
                return !string.IsNullOrEmpty(processName);
            }
            return false;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static string GetKlidFromHkl(nint hkl)
    {
        // HKL layout ID is in the low word of the HKL value
        var klid = new char[KL_NAMELENGTH];
        var result = GetKeyboardLayoutNameW(klid);
        if (result > 0)
        {
            var s = new string(klid).TrimEnd('\0');
            if (!string.IsNullOrEmpty(s)) return s;
        }

        // Fallback: construct from HKL low word
        var langId = (ushort)((nuint)hkl & 0xFFFF);
        return $"0000{langId:X4}";
    }

    private static string GetLayoutDisplayName(string klid)
    {
        if (string.IsNullOrEmpty(klid)) return "未知";

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{klid}");
            if (key != null)
            {
                // Try Layout Display Name first (preferred on Win 8+)
                var displayName = key.GetValue("Layout Display Name") as string;
                if (!string.IsNullOrEmpty(displayName) && displayName.StartsWith("@"))
                {
                    // It's an indirect string - try to resolve it
                    var resolved = TryResolveIndirectString(displayName);
                    if (!string.IsNullOrEmpty(resolved)) return resolved;
                }

                // Fallback to Layout Text
                var layoutText = key.GetValue("Layout Text") as string;
                if (!string.IsNullOrEmpty(layoutText)) return layoutText;
            }
        }
        catch { }

        // Known KLIDs
        return klid.ToUpperInvariant() switch
        {
            "00000804" or "E0080804" => "中文(简体) 微软拼音",
            "00000409" => "美式键盘",
            "00000411" => "日语",
            "00000412" => "韩语",
            _ => $"布局 {klid}"
        };
    }

    private static uint? TryGetConversionViaImeWindow(nint hwnd)
    {
        try
        {
            var iw = ImmGetDefaultIMEWnd(hwnd);
            if (iw != nint.Zero)
            {
                // IME window exists — trust its response (0 = English mode IS valid)
                var r = SendMessageW(iw, WM_IME_CONTROL, IMC_GETCONVERSIONSTATUS, nint.Zero);
                Logger.Log("IME", $"TryGetViaImeWnd: iw=0x{iw:X8} ret=0x{(ulong)r:X8}");
                return (uint)(long)r;
            }
            // No default IME window — try direct to foreground window
            var r2 = SendMessageW(hwnd, WM_IME_CONTROL, IMC_GETCONVERSIONSTATUS, nint.Zero);
            Logger.Log("IME", $"TryGetViaImeWnd: direct ret=0x{(ulong)r2:X8}");
            return (uint)(long)r2;
        }
        catch { }
        return null;
    }

    private static string TryResolveIndirectString(string indirect)
    {
        try
        {
            var sb = new StringBuilder(1024);
            var result = SHLoadIndirectString(indirect, sb, sb.Capacity, nint.Zero);
            if (result == 0 && sb.Length > 0)
                return sb.ToString();
        }
        catch { }
        return "";
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Stop();
    }

    #endregion

    #region P/Invoke

    private delegate void WinEventDelegate(nint hWinEventHook, uint eventType,
        nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax,
        nint hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int nBuff, nint[]? lpList);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadKeyboardLayoutW(string pwszKLID, uint Flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyboardLayoutNameW(char[] pwszKLID);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags,
        char[] lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("imm32.dll")]
    private static extern nint ImmGetContext(nint hWnd);

    [DllImport("imm32.dll")]
    private static extern bool ImmGetConversionStatus(nint hIMC, out uint lpfdwConversion, out uint lpfdwSentence);

    [DllImport("imm32.dll")]
    private static extern bool ImmSetConversionStatus(nint hIMC, uint fdwConversion, uint fdwSentence);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(nint hWnd, nint hIMC);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf,
        int cchOutBuf, nint ppvReserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
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

    [DllImport("user32.dll")] static extern nint SendMessageW(nint h, uint m, nint w, nint l);
    [DllImport("imm32.dll")] static extern nint ImmGetDefaultIMEWnd(nint h);

    #endregion
}

/// <summary>
/// 当前输入法状态信息（用于设置页面展示）。
/// </summary>
public class ImeStatusInfo
{
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public nint Hkl { get; set; }
    public string Klid { get; set; } = "";
    public string ImeDisplayName { get; set; } = "";
    public ushort LanguageId { get; set; }
    public string LanguageName { get; set; } = "";
    public bool IsChineseMode { get; set; }
    public bool IsFullWidth { get; set; }
    public bool IsCapsLock { get; set; }
    public string? Error { get; set; }
}
