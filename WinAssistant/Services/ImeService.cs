using System.Globalization;
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
    private volatile int _imeChangeMode;
    private long _imeChangeTimeTicks; // DateTime.UtcNow.Ticks, 用 Interlocked 读写

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
    private volatile bool _lastConversionInitialized;

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

    // ActivateKeyboardLayout flags
    private const uint KLF_RESET = 0x40000000;

    // TSF profile activation flags
    private const uint TF_PROFILETYPE_INPUTPROCESSOR = 0x0001;
    private const uint TF_PROFILETYPE_KEYBOARDLAYOUT = 0x0002;
    private const uint TF_IPPMF_FORPROCESS = 0x00000001;
    private const uint TF_IPPMF_FORSESSION = 0x00000002;
    private const uint TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE = 0x00000010;

    // COM CLSIDs/IIDs
    private static readonly Guid CLSID_TF_ThreadMgr = new("529A9E6B-6587-4F23-AB9E-9C7D683E3C50");
    private static readonly Guid CLSID_TF_InputProcessorProfiles = new("33C53A50-F456-4884-B049-85FD643ECFED");
    private static readonly Guid IID_ITfInputProcessorProfileMgr = new("71C6E74C-0F28-11D8-A82A-00065B84435C");
    private static readonly Guid IID_ITfThreadMgr = new("AA80E801-2021-11D2-93E0-0060B067B86E");
    private static readonly Guid IID_ITfCompartmentMgr = new("7DCF57AC-18AD-438B-824D-979BFFB74B7C");

    // TSF predefined compartments (used for CN/EN mode detection/toggling)
    private static readonly Guid GUID_COMPARTMENT_KEYBOARD_OPENCLOSE = new("58273AAD-01BB-4164-95C6-755BA0B5162D");
    private static readonly Guid GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION = new("CCF05DD8-4A87-11D7-A6E2-00065B84435C");

    // GUID_TFCAT_TIP_KEYBOARD — used with GetActiveProfile to query the active keyboard TIP
    private static readonly Guid GUID_TFCAT_TIP_KEYBOARD = new("34745C63-B2F0-4784-8B67-5E12C8701A31");

    // Window messages
    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
    private const uint WM_IME_CONTROL = 0x0283;
    private const uint WM_IME_KEYDOWN = 0x0290;
    private const uint WM_IME_KEYUP = 0x0291;
    private static readonly nint IMC_GETCONVERSIONSTATUS = 1;
    private static readonly nint IMC_SETCONVERSIONSTATUS = 2;

    // IME conversion mode flags
    private const uint IME_CMODE_ALPHANUMERIC = 0x0000;
    private const uint IME_CMODE_NATIVE = 0x0001;
    private const uint IME_CMODE_FULLSHAPE = 0x0008;
    private const uint IME_CMODE_ROMAN = 0x0010;

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
            if (ImmGetConversionStatus(himc, out uint conv, out _))
            {
                info.IsChineseMode = (conv & IME_CMODE_NATIVE) != 0;
                info.IsFullWidth = (conv & IME_CMODE_FULLSHAPE) != 0;
                Logger.Log("IME", $"GetCurrentStatus: HIMC ok conv=0x{conv:X8}");
            }
            else
            {
                Logger.Log("IME", "GetCurrentStatus: ImmGetConversionStatus failed");
            }
            ImmReleaseContext(hwnd, himc);
        }
        else
        {
            Logger.Log("IME", "GetCurrentStatus: no HIMC (TSF-only IME)");
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
    /// 清除最近一次 EVENT_IME_CHANGE 事件的时间戳（Win+Space 切换输入法后调用，防止误触 CN/EN toast）。
    /// </summary>
    public void ClearImeChangeEvent() => Interlocked.Exchange(ref _imeChangeTimeTicks, 0);

    /// <summary>
    /// 由 KeyboardHookService.ShiftToggled 调用：纯 Shift 键弹起时触发 CN/EN 切换检测。
    /// 对 TSF-only 输入法（ImmGetContext 返回 NULL），走内部状态翻转。
    /// IMM32 输入法由 CheckImeStateChanged（延迟 60ms）处理。
    /// </summary>
    public void OnShiftToggled()
    {
        if (!_running) return;
        var settings = App.SettingsService.Load();
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
                if (settings.IsCnEnToastEnabled)
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
        if (settings.IsCnEnToastEnabled)
            App.DispatcherQueue.TryEnqueue(() =>
                HotKeyToast.Show(isChinese ? "中文" : "英文"));
    }

    /// <summary>
    /// 由 KeyboardHookService 调用：输入法状态可能已变化，检查并显示 Toast。
    /// </summary>
    public void CheckImeStateChanged()
    {
        if (!_running) return;
        var settings = App.SettingsService.Load();
        if (_suppressChangeCounter > 0) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == nint.Zero) return;

        var threadId = GetWindowThreadProcessId(hwnd, out _);
        var currentHkl = GetKeyboardLayout(threadId);
        var himc = ImmGetContext(hwnd);

        if (himc == nint.Zero)
        {
            // TSF-only IME (Microsoft Pinyin, WeType, etc.) — use EVENT_IME_CHANGE to detect CN/EN toggles
            var lastChangeTicks = Interlocked.Read(ref _imeChangeTimeTicks);
            var elapsed = lastChangeTicks > 0
                ? DateTime.UtcNow - new DateTime(lastChangeTicks, DateTimeKind.Utc)
                : TimeSpan.FromDays(1);
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
                    if (settings.IsImeSwitchToastEnabled)
                        App.DispatcherQueue.TryEnqueue(() => HotKeyToast.Show("输入法", name));
                    return;
                }

                // Check Chinese/English mode change
                if ((tsfConv & IME_CMODE_NATIVE) != (_lastConversion & IME_CMODE_NATIVE))
                {
                    _lastConversion = tsfConv;
                    bool isChinese = (tsfConv & IME_CMODE_NATIVE) != 0;
                    Logger.Log("IME", $"EVENT_IME_CHANGE: CN/EN change → {(isChinese ? "中文" : "英文")}");
                    if (settings.IsCnEnToastEnabled)
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
                    if (settings.IsCnEnToastEnabled)
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
                if (settings.IsImeSwitchToastEnabled)
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
            if (settings.IsImeSwitchToastEnabled)
                App.DispatcherQueue.TryEnqueue(() => HotKeyToast.Show("输入法", name));
            return;
        }

        // Check Chinese/English mode change within same IME
        if (_lastConversionInitialized && (conv & IME_CMODE_NATIVE) != (_lastConversion & IME_CMODE_NATIVE))
        {
            _lastConversion = conv;
            bool isChinese = (conv & IME_CMODE_NATIVE) != 0;
            Logger.Log("IME", $"IMM32: CN/EN change → {(isChinese ? "中文" : "英文")}");
            if (settings.IsCnEnToastEnabled)
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
            if (settings.IsCnEnToastEnabled)
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
        // Auto-release after a longer window so that polling-based activation and
        // mode switching do not trigger our own toast feedback.
        _ = Task.Delay(2000).ContinueWith(_ =>
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

        // 3. Enumerate all installed layouts from HKLM so layouts that are installed
        //    but not currently loaded (e.g., WeType, Microsoft Pinyin before first use)
        //    are still selectable.
        try
        {
            using var hklmKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts");
            if (hklmKey != null)
            {
                var seenKlids = new HashSet<string>(result.Select(r => r.Klid));
                foreach (var subKeyName in hklmKey.GetSubKeyNames())
                {
                    if (subKeyName.Length != 8) continue;
                    if (!seenKlids.Add(subKeyName)) continue;
                    var name = GetLayoutDisplayName(subKeyName);
                    result.Add((subKeyName, name));
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
    /// 枚举当前系统已启用的 TSF 输入法（精确到输入法，如微软拼音、微信输入法）。
    /// </summary>
    public static List<(string Id, string Name, short LangId, Guid Clsid, Guid Profile)> EnumerateInputMethods()
    {
        var result = new List<(string Id, string Name, short LangId, Guid Clsid, Guid Profile)>();
        var hr = TF_CreateInputProcessorProfiles(out var profiles);
        if (hr != 0)
        {
            Logger.Log("IME", $"TF_CreateInputProcessorProfiles failed hr=0x{hr:X8}");
            return result;
        }

        try
        {
            hr = profiles.GetLanguageList(out var langPtr, out var count);
            if (hr != 0)
            {
                Logger.Log("IME", $"GetLanguageList failed hr=0x{hr:X8}");
                return result;
            }

            var langIds = new short[count];
            for (int i = 0; i < count; i++)
                langIds[i] = Marshal.ReadInt16(langPtr, i * sizeof(short));
            Marshal.FreeCoTaskMem(langPtr);

            foreach (var langId in langIds)
            {
                hr = profiles.EnumLanguageProfiles(langId, out var enumObj);
                if (hr != 0) continue;

                var enumProfiles = (IEnumTfLanguageProfiles)enumObj;
                try
                {
                    var profileArray = new TF_LANGUAGEPROFILE[1];
                    while (enumProfiles.Next(1, profileArray, out var fetched) == 0 && fetched > 0)
                    {
                        var p = profileArray[0];
                        if (profiles.IsEnabledLanguageProfile(ref p.clsid, p.langid, ref p.guidProfile, out var enabled) != 0 || !enabled)
                            continue;

                        if (profiles.GetLanguageProfileDescription(ref p.clsid, p.langid, ref p.guidProfile, out var descPtr) != 0)
                            continue;

                        var name = Marshal.PtrToStringBSTR(descPtr);
                        Marshal.FreeBSTR(descPtr);

                        var id = FormatInputMethodId(p.langid, p.clsid, p.guidProfile);
                        result.Add((id, name, p.langid, p.clsid, p.guidProfile));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(enumProfiles);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(profiles);
        }

        result.Sort((a, b) =>
        {
            bool aChinese = a.LangId is 0x0804 or 0x0C04 or 0x0404 or 0x1004 or 0x1404;
            bool bChinese = b.LangId is 0x0804 or 0x0C04 or 0x0404 or 0x1004 or 0x1404;
            if (aChinese != bChinese) return aChinese ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        return result;
    }

    public static string FormatInputMethodId(short langId, Guid clsid, Guid profile)
        => $"{langId:X4}:{clsid:N}:{profile:N}";

    public static (short LangId, Guid Clsid, Guid Profile)? ParseInputMethodId(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var parts = id.Split(':');
        if (parts.Length != 3) return null;
        if (!short.TryParse(parts[0], NumberStyles.HexNumber, null, out var langId)) return null;
        if (!Guid.TryParse(parts[1], out var clsid)) return null;
        if (!Guid.TryParse(parts[2], out var profile)) return null;
        return (langId, clsid, profile);
    }

    /// <summary>获取前台窗口句柄（供 UI 使用）。</summary>
    public static nint GetForegroundHwnd() => GetForegroundWindow();

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
        Interlocked.Exchange(ref _imeChangeTimeTicks, DateTime.UtcNow.Ticks);
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

        if (!QueryProcessName(hwnd, out var processName) || string.IsNullOrEmpty(processName))
            return;

        // Keep basic tracking so state detection stays consistent across window switches,
        // but no longer perform any automatic input method switching.
        if (!string.Equals(processName, _lastProcessName, StringComparison.OrdinalIgnoreCase))
        {
            _lastProcessName = processName;
            CaptureCurrentImeState();
        }

        _lastForegroundHwnd = hwnd;
    }

    /// <summary>
    /// Create an ITfInputProcessorProfileMgr instance via CoCreateInstance.
    /// Note: this interface lives on CLSID_TF_InputProcessorProfiles, not on TF_ThreadMgr.
    /// </summary>
    private static ITfInputProcessorProfileMgr? CreateProfileMgr()
    {
        try
        {
            // CLSCTX_INPROC_SERVER = 1
            var clsid = CLSID_TF_InputProcessorProfiles;
            var iid = IID_ITfInputProcessorProfileMgr;
            var hr = CoCreateInstance(
                ref clsid,
                nint.Zero,
                1,
                ref iid,
                out var obj);
            if (hr != 0 || obj == null)
            {
                Logger.Log("IME", $"CoCreateInstance ITfInputProcessorProfileMgr failed hr=0x{hr:X8}");
                return null;
            }
            return (ITfInputProcessorProfileMgr)obj;
        }
        catch (Exception ex)
        {
            Logger.Log("IME", $"CreateProfileMgr failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Query the HKL associated with a TSF input processor profile.
    /// Falls back to the substitute HKL if the primary HKL is zero.
    /// </summary>
    private static nint GetProfileHkl(short langId, Guid clsid, Guid profile)
    {
        try
        {
            var profileMgr = CreateProfileMgr();
            if (profileMgr == null) return nint.Zero;

            int hr = profileMgr.GetProfile(
                TF_PROFILETYPE_INPUTPROCESSOR,
                langId,
                clsid,
                profile,
                nint.Zero,
                out var p);
            if (hr != 0)
            {
                Logger.Log("IME", $"GetProfile failed hr=0x{hr:X8}");
                return nint.Zero;
            }

            var hkl = p.hkl != nint.Zero ? p.hkl : p.hklSubstitute;
            Logger.Log("IME", $"GetProfile hkl=0x{(ulong)p.hkl:X8} hklSubstitute=0x{(ulong)p.hklSubstitute:X8} selected=0x{(ulong)hkl:X8}");
            return hkl;
        }
        catch (Exception ex)
        {
            Logger.Log("IME", $"GetProfileHkl error: {ex.Message}");
            return nint.Zero;
        }
    }

    /// <summary>
    /// Attempt to activate a TSF keyboard-layout profile by language id.
    /// This is a best-effort fallback when ActivateKeyboardLayout fails for pure-TSF IMEs.
    /// </summary>
    private static bool TryActivateTsfProfile(short langId)
    {
        try
        {
            var profileMgr = CreateProfileMgr();
            if (profileMgr == null) return false;

            // TF_IPPMF_FORPROCESS is the only flag combination that succeeds on this system.
            // It affects the WinAssistant process; we pair it with PostInputLanguageChange
            // to actually switch the foreground window's layout.
            int hr = profileMgr.ActivateProfile(
                TF_PROFILETYPE_KEYBOARDLAYOUT,
                langId,
                Guid.Empty,
                Guid.Empty,
                nint.Zero,
                TF_IPPMF_FORPROCESS);
            Logger.Log("IME", $"TSF ActivateProfile langid=0x{langId:X4} hr=0x{hr:X8}");
            return hr >= 0;
        }
        catch (Exception ex)
        {
            Logger.Log("IME", $"TSF ActivateProfile error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attempt to activate a specific TSF input method profile in the current process.
    /// FORPROCESS only affects the calling process; callers pair this with
    /// ActivateKeyboardLayout / PostInputLanguageChange to propagate to the target window.
    /// </summary>
    private static bool TryActivateTsfInputMethod(short langId, Guid clsid, Guid profile, nint _)
    {
        try
        {
            var profileMgr = CreateProfileMgr();
            if (profileMgr == null) return false;

            int hr = profileMgr.ActivateProfile(
                TF_PROFILETYPE_INPUTPROCESSOR,
                langId,
                clsid,
                profile,
                nint.Zero,
                TF_IPPMF_FORPROCESS);

            Logger.Log("IME", $"ActivateProfile clsid={clsid} profile={profile} langid=0x{langId:X4} hr=0x{hr:X8}");
            return hr >= 0;
        }
        catch (Exception ex)
        {
            Logger.Log("IME", $"TSF ActivateProfile error: {ex.Message}");
            return false;
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

    /// <summary>
    /// Set the TSF conversion mode compartment directly.
    /// TSF compartments are strictly per-thread; when the target window belongs to
    /// another process we cannot affect its IME state from here.  In that case we
    /// return false so the caller falls back to key simulation.
    /// </summary>
    private static bool SetTsfConversionMode(bool englishMode, nint hwnd)
    {
        try
        {
            uint currentThreadId = GetCurrentThreadId();
            uint targetThreadId = hwnd != nint.Zero
                ? GetWindowThreadProcessId(hwnd, out _)
                : currentThreadId;

            // TSF compartment operations only affect the calling thread.
            if (targetThreadId != 0 && targetThreadId != currentThreadId)
            {
                Logger.Log("IME", "SetTsfConversionMode: target is cross-thread, skipping TSF compartment");
                return false;
            }

            var threadMgrObj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_TF_ThreadMgr)!);
            if (threadMgrObj is not ITfThreadMgr threadMgr)
            {
                Logger.Log("IME", "SetTsfConversionMode: failed to create ITfThreadMgr");
                return false;
            }

            // Try the focused context's compartment first; some IMEs (e.g. WeType)
            // ignore the global compartment and only react to per-context changes.
            ITfCompartmentMgr? compartmentMgr = null;
            try
            {
                if (threadMgr.GetFocus(out var docMgr) == 0 && docMgr != null)
                {
                    if (docMgr.GetTop(out var context) == 0 && context != null)
                    {
                        compartmentMgr = context as ITfCompartmentMgr;
                        if (compartmentMgr != null)
                        {
                            Logger.Log("IME", "SetTsfConversionMode: using focused context compartment");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("IME", $"SetTsfConversionMode: context compartment probe error: {ex.Message}");
            }

            // Fall back to the global compartment.
            if (compartmentMgr == null)
            {
                var hr = threadMgr.GetGlobalCompartment(out compartmentMgr);
                if (hr != 0)
                {
                    Logger.Log("IME", $"SetTsfConversionMode: GetGlobalCompartment failed hr=0x{hr:X8}");
                    return false;
                }
                Logger.Log("IME", "SetTsfConversionMode: using global compartment");
            }

            var compartmentGuid = GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION;
            var hr2 = compartmentMgr.GetCompartment(ref compartmentGuid, out var compartment);
            if (hr2 != 0 || compartment == null)
            {
                Logger.Log("IME", $"SetTsfConversionMode: GetCompartment failed hr=0x{hr2:X8}");
                return false;
            }

            // 0 = alphanumeric/English, 1 = native/Chinese
            object value = englishMode ? 0 : 1;
            hr2 = compartment.SetValue(0, ref value);
            if (hr2 != 0)
            {
                Logger.Log("IME", $"SetTsfConversionMode: SetValue failed hr=0x{hr2:X8}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.Log("IME", $"SetTsfConversionMode error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Read the current TSF conversion mode compartment value.
    /// Returns null if the value cannot be read; 0 for English/alphanumeric; 1 for Chinese/native.
    /// When the target window lives in another process this returns null because TSF
    /// compartments are thread-local and cannot be read across process boundaries.
    /// </summary>
    private static int? GetTsfConversionMode(nint hwnd)
    {
        try
        {
            uint currentThreadId = GetCurrentThreadId();
            uint targetThreadId = hwnd != nint.Zero
                ? GetWindowThreadProcessId(hwnd, out _)
                : currentThreadId;

            if (targetThreadId != 0 && targetThreadId != currentThreadId)
            {
                Logger.Log("IME", "GetTsfConversionMode: target is cross-thread, cannot read TSF compartment");
                return null;
            }

            var threadMgrObj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_TF_ThreadMgr)!);
            if (threadMgrObj is not ITfThreadMgr threadMgr)
                return null;

            // Prefer the focused context's compartment for accuracy.
            ITfCompartmentMgr? compartmentMgr = null;
            try
            {
                if (threadMgr.GetFocus(out var docMgr) == 0 && docMgr != null)
                {
                    if (docMgr.GetTop(out var context) == 0 && context != null)
                    {
                        compartmentMgr = context as ITfCompartmentMgr;
                    }
                }
            }
            catch { }

            if (compartmentMgr == null)
            {
                if (threadMgr.GetGlobalCompartment(out compartmentMgr) != 0 || compartmentMgr == null)
                    return null;
            }

            var compartmentGuid = GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION;
            if (compartmentMgr.GetCompartment(ref compartmentGuid, out var compartment) != 0 || compartment == null)
                return null;

            if (compartment.GetValue(out var value) != 0 || value == null)
                return null;

            if (value is int i) return i;
            if (value is short s) return s;
            if (value is uint u) return (int)u;
            if (value is ushort us) return us;
            if (value is long l) return (int)l;
            if (value is byte b) return b;
            if (value is bool bl) return bl ? 1 : 0;
            if (int.TryParse(value.ToString(), out var parsed)) return parsed;
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log("IME", $"GetTsfConversionMode error: {ex.Message}");
            return null;
        }
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
        // GetKeyboardLayoutNameW only returns the current thread's layout, not the given HKL.
        // For standard keyboard layouts the language id is in the low word of the HKL.
        var langId = (ushort)((nuint)hkl & 0xFFFF);
        return $"0000{langId:X4}";
    }

    private static nint ActivateKeyboardLayoutForWindow(nint hwnd, nint hkl)
    {
        var targetThreadId = GetWindowThreadProcessId(hwnd, out _);
        var currentThreadId = GetCurrentThreadId();

        bool attached = false;
        if (targetThreadId != currentThreadId)
        {
            attached = AttachThreadInput(currentThreadId, targetThreadId, true);
        }

        try
        {
            return ActivateKeyboardLayout(hkl, 0);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThreadId, targetThreadId, false);
        }
    }

    /// <summary>
    /// Posts WM_INPUTLANGCHANGEREQUEST to the focused window in the target thread.
    /// This is the preferred, non-blocking way to switch input language for another window.
    /// </summary>
    private static void PostInputLanguageChange(nint hwnd, nint hkl)
    {
        var targetThreadId = GetWindowThreadProcessId(hwnd, out _);
        var currentThreadId = GetCurrentThreadId();

        bool attached = false;
        if (targetThreadId != currentThreadId)
        {
            attached = AttachThreadInput(currentThreadId, targetThreadId, true);
        }

        try
        {
            var focusHwnd = attached ? GetFocus() : hwnd;
            if (focusHwnd == nint.Zero) focusHwnd = hwnd;
            PostMessageW(focusHwnd, WM_INPUTLANGCHANGEREQUEST, nint.Zero, hkl);
            Logger.Log("IME", $"PostMessage WM_INPUTLANGCHANGEREQUEST hkl=0x{(ulong)hkl:X8} focus=0x{(ulong)focusHwnd:X8}");
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThreadId, targetThreadId, false);
        }
    }

    private static bool IsEnglishLayout(string klid)
    {
        if (string.IsNullOrEmpty(klid) || klid.Length < 8) return false;
        try
        {
            var langId = Convert.ToUInt16(klid[4..], 16);
            // English language IDs in Windows use primary language 0x09.
            return (langId & 0xFF) == 0x09;
        }
        catch
        {
            return false;
        }
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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

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

    [DllImport("user32.dll")]
    private static extern nint ActivateKeyboardLayout(nint hkl, uint Flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyboardLayoutNameW(char[] pwszKLID);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    /// <summary>
    /// TSF ThreadMgr interface. Used to obtain the global compartment manager.
    /// </summary>
    [ComImport, Guid("AA80E801-2021-11D2-93E0-0060B067B86E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfThreadMgr
    {
        [PreserveSig]
        int Activate(out int clientId);

        [PreserveSig]
        int Deactivate();

        [PreserveSig]
        int CreateDocumentMgr([MarshalAs(UnmanagedType.Interface)] out object docMgr);

        [PreserveSig]
        int EnumDocumentMgrs([MarshalAs(UnmanagedType.Interface)] out object enumDocMgrs);

        [PreserveSig]
        int GetFocus([MarshalAs(UnmanagedType.Interface)] out ITfDocumentMgr docMgr);

        [PreserveSig]
        int SetFocus([MarshalAs(UnmanagedType.Interface)] object docMgr);

        [PreserveSig]
        int AssociateFocus(nint hwnd, [MarshalAs(UnmanagedType.Interface)] object newDocMgr,
            [MarshalAs(UnmanagedType.Interface)] out object prevDocMgr);

        [PreserveSig]
        int IsThreadFocus([MarshalAs(UnmanagedType.Bool)] out bool isFocus);

        [PreserveSig]
        int GetFunctionProvider(ref Guid classId, [MarshalAs(UnmanagedType.Interface)] out object funcProvider);

        [PreserveSig]
        int EnumFunctionProviders([MarshalAs(UnmanagedType.Interface)] out object enumProviders);

        [PreserveSig]
        int GetGlobalCompartment([MarshalAs(UnmanagedType.Interface)] out ITfCompartmentMgr compartmentMgr);
    }

    /// <summary>
    /// TSF DocumentMgr interface. Used to access the focused input context.
    /// </summary>
    [ComImport, Guid("AA80E802-2021-11D2-93E0-0060B067B86E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfDocumentMgr
    {
        [PreserveSig]
        int CreateContext(uint ecClient, uint dwFlags, [MarshalAs(UnmanagedType.IUnknown)] object punk,
            [MarshalAs(UnmanagedType.Interface)] out object ppic, out int ecTextStore);

        [PreserveSig]
        int Push([MarshalAs(UnmanagedType.Interface)] object pic);

        [PreserveSig]
        int Pop(uint dwFlags);

        [PreserveSig]
        int GetTop([MarshalAs(UnmanagedType.Interface)] out ITfContext ppic);

        [PreserveSig]
        int GetBase([MarshalAs(UnmanagedType.Interface)] out object ppic);

        [PreserveSig]
        int EnumContexts([MarshalAs(UnmanagedType.Interface)] out object ppEnum);
    }

    /// <summary>
    /// TSF Context interface marker. We only need it to QueryInterface for ITfCompartmentMgr.
    /// </summary>
    [ComImport, Guid("D978C1F0-4AEB-11D3-9C3C-00C04F7ADFB5"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfContext
    {
    }

    /// <summary>
    /// TSF Compartment interface. Used to read/write TSF compartment values (e.g. CN/EN mode).
    /// </summary>
    [ComImport, Guid("BB08F7A9-607A-4384-8623-056892B64371"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfCompartment
    {
        [PreserveSig]
        int SetValue(int tid, [MarshalAs(UnmanagedType.Struct)] ref object pvarValue);

        [PreserveSig]
        int GetValue([MarshalAs(UnmanagedType.Struct)] out object pvarValue);
    }

    /// <summary>
    /// TSF CompartmentMgr interface. Obtained from ITfThreadMgr to access compartments.
    /// </summary>
    [ComImport, Guid("7DCF57AC-18AD-438B-824D-979BFFB74B7C"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfCompartmentMgr
    {
        [PreserveSig]
        int GetCompartment(ref Guid rguid, [MarshalAs(UnmanagedType.Interface)] out ITfCompartment ppcomp);

        [PreserveSig]
        int ClearCompartment(int tid, ref Guid rguid);

        [PreserveSig]
        int EnumCompartments([MarshalAs(UnmanagedType.Interface)] out object ppEnum);
    }

    /// <summary>
    /// TSF InputProcessorProfileMgr interface. Used to activate profiles and query
    /// the active profile of any thread.
    /// </summary>
    [ComImport, Guid("71C6E74C-0F28-11D8-A82A-00065B84435C"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfileMgr
    {
        [PreserveSig]
        int ActivateProfile(
            uint dwProfileType,
            short langid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid clsid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidProfile,
            nint hkl,
            uint dwFlags);

        [PreserveSig]
        int DeactivateProfile(
            uint dwProfileType,
            short langid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid clsid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidProfile,
            nint hkl,
            uint dwFlags);

        [PreserveSig]
        int GetProfile(
            uint dwProfileType,
            short langid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid clsid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid guidProfile,
            nint hkl,
            out TF_INPUTPROCESSORPROFILE profile);

        [PreserveSig]
        int EnumProfiles(short langid, [MarshalAs(UnmanagedType.Interface)] out object enumProfiles);

        [PreserveSig]
        int ReleaseCleanup();

        // Windows 8+ addition; must be 6th slot in vtable.
        [PreserveSig]
        int GetActiveProfile(
            [MarshalAs(UnmanagedType.LPStruct)] Guid catid,
            out TF_INPUTPROCESSORPROFILE profile,
            uint dwThreadId);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TF_LANGUAGEPROFILE
    {
        public Guid clsid;
        public short langid;
        public Guid catid;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fActive;
        public Guid guidProfile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TF_INPUTPROCESSORPROFILE
    {
        public uint dwProfileType;
        public short langid;
        public Guid clsid;
        public Guid guidProfile;
        public Guid catid;
        public nint hklSubstitute;
        public uint dwCaps;
        public nint hkl;
        public uint dwFlags;
    }

    /// <summary>
    /// TSF InputProcessorProfiles interface. Used to enumerate installed input methods.
    /// </summary>
    [ComImport, Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfiles
    {
        [PreserveSig] int Register(ref Guid rclsid);
        [PreserveSig] int Unregister(ref Guid rclsid);
        [PreserveSig] int AddLanguageProfile(ref Guid rclsid, short langid, ref Guid guidProfile,
            [MarshalAs(UnmanagedType.LPWStr)] string pchDesc, uint cchDesc,
            [MarshalAs(UnmanagedType.LPWStr)] string pchIconFile, uint cchFile, uint uIconIndex);
        [PreserveSig] int RemoveLanguageProfile(ref Guid rclsid, short langid, ref Guid guidProfile);
        [PreserveSig] int EnumInputProcessorInfo([MarshalAs(UnmanagedType.Interface)] out object enumIPP);
        [PreserveSig] int GetDefaultLanguageProfile(short langid, ref Guid catid,
            out Guid pclsid, out Guid pguidProfile);
        [PreserveSig] int SetDefaultLanguageProfile(ref Guid rclsid, short langid, ref Guid guidProfile);
        [PreserveSig] int ActivateLanguageProfile(ref Guid rclsid, short langid, ref Guid guidProfile);
        [PreserveSig] int GetActiveLanguageProfile(ref Guid rclsid, out short plangid, out Guid pguidProfile);
        [PreserveSig] int GetLanguageProfileDescription(ref Guid rclsid, short langid,
            ref Guid guidProfile, out IntPtr pbstrProfile);
        [PreserveSig] int GetCurrentLanguage(out short plangid);
        [PreserveSig] int ChangeCurrentLanguage(short langid);
        [PreserveSig] int GetLanguageList(out IntPtr ppLangIds, out int pulCount);
        [PreserveSig] int EnumLanguageProfiles(short langid,
            [MarshalAs(UnmanagedType.Interface)] out object ppenum);
        [PreserveSig] int EnableLanguageProfile(ref Guid rclsid, short langid,
            ref Guid guidProfile, [MarshalAs(UnmanagedType.Bool)] bool fEnable);
        [PreserveSig] int IsEnabledLanguageProfile(ref Guid rclsid, short langid,
            ref Guid guidProfile, [MarshalAs(UnmanagedType.Bool)] out bool pfEnabled);
        [PreserveSig] int EnableLanguageProfileByDefault(ref Guid rclsid, short langid,
            ref Guid guidProfile, [MarshalAs(UnmanagedType.Bool)] bool fEnable);
        [PreserveSig] int SubstituteKeyboardLayout(ref Guid rclsid, short langid,
            ref Guid guidProfile, nint hKL);
    }

    [ComImport, Guid("3d61bf11-ac5f-42c8-a4cb-931bcc28c744"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumTfLanguageProfiles
    {
        [PreserveSig]
        int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumTfLanguageProfiles enumIPP);
        [PreserveSig]
        int Next(int count,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] TF_LANGUAGEPROFILE[] profiles,
            out int fetched);
        [PreserveSig] int Reset();
        [PreserveSig] int Skip(int count);
    }

    [DllImport("msctf.dll")]
    private static extern int TF_CreateInputProcessorProfiles(
        [MarshalAs(UnmanagedType.Interface)] out ITfInputProcessorProfiles profiles);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        [In] ref Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

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
    [DllImport("user32.dll")] static extern nint GetFocus();
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
