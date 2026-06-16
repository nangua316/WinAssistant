using System.Runtime.InteropServices;
using System.Text;
using WinAssistant.Helpers;
using System.Linq;

namespace WinAssistant.Services;

/// <summary>
/// Monitors IME switches (Win+Space / Ctrl+Shift / Alt+Shift) for toast notifications.
/// CN/EN toggle detection is handled by ImeService via KeyboardHookService.
/// HKL polling detects cross-language layout switches.
/// </summary>
public class TsfImeMonitorService : IDisposable
{
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;

    private Dictionary<Guid, string> _profileMap = new();
    private readonly List<Guid> _profileList = new();
    private readonly Dictionary<Guid, string> _profileIcons = new();

    // 复用后台 STA 线程执行 COM 查询
    private readonly AutoResetEvent _querySignal = new(false);
    private volatile TaskCompletionSource<(string Name, string? Icon)>? _queryTcs;

    public bool IsRunning => _running;
    public event Action<string>? ImeProfileChanged;

    private ManualResetEvent? _stopEvent;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _stopEvent = new ManualResetEvent(false);

        BuildProfileMap();
        Logger.Log("IME2", $"Loaded {_profileMap.Count} profiles, ordered {_profileList.Count}");

        _thread = new Thread(Run) { IsBackground = true, Name = "ImeMon" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _stopEvent?.Set(); // 通知 Run 线程退出等待
    }

    public void OnWinSpaceDetected()
    {
        // 在后台 STA 线程上查询真实激活的输入法（通过 TSF COM）
        var tcs = new TaskCompletionSource<(string Name, string? Icon)>();
        // 用 Interlocked 避免并发覆盖导致前一个请求丢失
        var old = Interlocked.Exchange(ref _queryTcs, tcs);
        old?.TrySetCanceled();
        _querySignal.Set();

        _ = tcs.Task.ContinueWith(t =>
        {
            if (!t.IsFaulted && !t.IsCanceled)
            {
                var (name, icon) = t.Result;
                Logger.Log("IME2", $"IME → {name}");
                App.DispatcherQueue.TryEnqueue(() =>
                    HotKeyToast.Show("输入法", name, icon));
            }
        }, TaskContinuationOptions.NotOnFaulted);
    }

    /// <summary>
    /// 查询前台窗口当前激活的输入法 Profile GUID（通过 TSF COM 接口，真实状态）。
    /// </summary>
    private Guid? QueryActiveProfileGuid()
    {
        try
        {
            var clsid = new Guid("33C53A50-F456-4884-B049-85FD643ECFED"); // CLSID_TF_InputProcessorProfiles
            var iid = new Guid("F0B8F830-312C-4A7F-A323-0DF367EB3276");  // IID_ITfInputProcessorProfileMgr

            int hr = CoCreateInstance(ref clsid, nint.Zero, 1 /*CLSCTX_INPROC_SERVER*/, ref iid, out nint pUnk);
            if (hr < 0) { Logger.Log("IME2", $"COM: CoCreateInstance failed hr=0x{hr:X8}"); return null; }

            var mgr = (ITfInputProcessorProfileMgr)Marshal.GetObjectForIUnknown(pUnk);
            // 注意：GetObjectForIUnknown 转移了引用所有权给 RCW，不能 Marshal.Release(pUnk)

            var nullGuid = Guid.Empty;
            var catid = new Guid("34745C63-B2F0-4784-8B67-5E12C8701A31"); // GUID_TFCAT_TIP_KEYBOARD
            var profile = new TF_INPUTPROCESSORPROFILE();
            hr = mgr.GetActiveLanguageProfile(ref nullGuid, ref catid, out profile);
            if (hr < 0) { Logger.Log("IME2", $"COM: GetActiveLanguageProfile failed hr=0x{hr:X8}"); return null; }
            if (profile.guidProfile == Guid.Empty) { Logger.Log("IME2", "COM: guidProfile is empty"); return null; }

            Logger.Log("IME2", $"COM: active profile guid={profile.guidProfile}");
            return profile.guidProfile;
        }
        catch (Exception ex) { Logger.Log("IME2", $"COM: exception {ex.Message}"); return null; }
    }

    // Cycling fallback for when TSF COM fails
    private int _profileIndex;

    private static string MapDisplayName(string rawName, Guid profGuid)
    {
        if (profGuid == new Guid("FA550B04-5AD7-411F-A5AC-CA038EC515D7")) return "微软拼音";
        if (profGuid == new Guid("607FDF85-FCC8-4DBD-A365-41296F980C9C")) return "微信输入法";
        if (rawName.Contains("Pinyin", StringComparison.OrdinalIgnoreCase) ||
            rawName.Contains("MSPY", StringComparison.OrdinalIgnoreCase))
            return "微软拼音";
        if (rawName.Contains("WeType", StringComparison.OrdinalIgnoreCase) ||
            rawName.Contains("WeChat", StringComparison.OrdinalIgnoreCase))
            return "微信输入法";
        return rawName;
    }

    // ── Background thread: HKL + profile switch detection ──

    private void Run()
    {
        var tok = _cts?.Token ?? CancellationToken.None;
        var stopEvent = _stopEvent;
        nint lastHkl = nint.Zero;

        try
        {
            CoInitializeEx(nint.Zero, 2);
            lastHkl = GetFgHkl();
            Logger.Log("IME2", $"BG start hkl=0x{(ulong)lastHkl:X8}");

            var waitHandles = new WaitHandle[] { stopEvent!, _querySignal };

            while (!tok.IsCancellationRequested)
            {
                var signalled = WaitHandle.WaitAny(waitHandles, 500);
                if (signalled == 0) break; // cancellation
                if (signalled == 1)
                {
                    // COM 查询请求
                    var tcs = Interlocked.Exchange(ref _queryTcs, null);
                    if (tcs != null && !tok.IsCancellationRequested)
                    {
                        var result = QueryActiveProfileInternal();
                        tcs.TrySetResult(result);
                    }
                    continue;
                }

                // timeout — HKL polling
                try
                {
                    var hkl = GetFgHkl();
                    if (hkl != nint.Zero && hkl != lastHkl)
                    {
                        lastHkl = hkl;
                        var name = ResolveHklName(hkl);
                        Logger.Log("IME2", $"HKL={name}");
                        App.DispatcherQueue.TryEnqueue(() => ImeProfileChanged?.Invoke(name));
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { Logger.Log("IME2", $"BG error: {ex.Message}"); }
        finally
        {
            CoUninitialize();
        }
    }

    /// <summary>
    /// 在后台 STA 线程上同步执行 COM 查询（必须在 STA 线程调用）。
    /// </summary>
    private (string Name, string? Icon) QueryActiveProfileInternal()
    {
        var guid = QueryActiveProfileGuid();
        if (guid.HasValue && _profileMap.TryGetValue(guid.Value, out var n))
        {
            var icon = _profileIcons.TryGetValue(guid.Value, out var i) ? i : null;
            return (MapDisplayName(n, guid.Value), icon);
        }

        // TSF COM 失败 — fallback 循环
        if (_profileList.Count > 0)
        {
            _profileIndex = (_profileIndex + 1) % _profileList.Count;
            var prof = _profileList[_profileIndex];
            var name = _profileMap.TryGetValue(prof, out var n2) ? MapDisplayName(n2, prof) : "输入法";
            var icon = _profileIcons.TryGetValue(prof, out var i2) ? i2 : null;
            Logger.Log("IME2", $"COM fallback: cycle to {name}");
            return (name, icon);
        }

        return ("输入法", null);
    }

    // ── Build profile maps ──

    private void BuildProfileMap()
    {
        _profileMap.Clear();
        _profileList.Clear();
        _profileIcons.Clear();
        try
        {
            using var tipKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\CTF\TIP");
            if (tipKey != null)
            {
                foreach (var clsidStr in tipKey.GetSubKeyNames())
                {
                    if (!Guid.TryParse(clsidStr, out _)) continue;
                    using var clsidKey = tipKey.OpenSubKey(clsidStr);
                    if (clsidKey == null) continue;

                    foreach (var subKey in clsidKey.GetSubKeyNames())
                    {
                        if (!subKey.StartsWith("LanguageProfile", StringComparison.OrdinalIgnoreCase))
                            continue;
                        using var langProfileKey = clsidKey.OpenSubKey(subKey);
                        if (langProfileKey == null) continue;

                        foreach (var langId in langProfileKey.GetSubKeyNames())
                        {
                            using var langIdKey = langProfileKey.OpenSubKey(langId);
                            if (langIdKey == null) continue;

                            foreach (var profileGuidStr in langIdKey.GetSubKeyNames())
                            {
                                if (!Guid.TryParse(profileGuidStr, out var profileGuid))
                                    continue;
                                using var profKey = langIdKey.OpenSubKey(profileGuidStr);
                                if (profKey == null) continue;

                                var desc = profKey.GetValue("Description") as string;
                                if (!string.IsNullOrEmpty(desc) && !_profileMap.ContainsKey(profileGuid))
                                    _profileMap[profileGuid] = desc;
                            }
                        }
                    }
                }
            }

            // Ordered profile list from HKCU SortOrder
            var catTip = new Guid("34745C63-B2F0-4784-8B67-5E12C8701A31");
            using var sortRoot = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\CTF\SortOrder\AssemblyItem");
            if (sortRoot != null)
            {
                foreach (var langKeyName in sortRoot.GetSubKeyNames())
                {
                    using var langKey = sortRoot.OpenSubKey(langKeyName);
                    if (langKey == null) continue;
                    using var catKey = langKey.OpenSubKey($"{{{catTip.ToString().ToUpperInvariant()}}}");
                    if (catKey == null) continue;

                    foreach (var orderKeyName in catKey.GetSubKeyNames().OrderBy(n => n))
                    {
                        using var orderKey = catKey.OpenSubKey(orderKeyName);
                        if (orderKey == null) continue;
                        var profileVal = orderKey.GetValue("Profile") as string;
                        if (string.IsNullOrEmpty(profileVal)) continue;
                        if (Guid.TryParse(profileVal, out var profGuid) && !_profileList.Contains(profGuid))
                            _profileList.Add(profGuid);
                    }
                }
            }

            // Known IME icon paths
            _profileIcons[new Guid("607FDF85-FCC8-4DBD-A365-41296F980C9C")] = @"C:\Windows\system32\wetype_tip.dll";
            _profileIcons[new Guid("FA550B04-5AD7-411F-A5AC-CA038EC515D7")] = @"C:\Windows\System32\InputMethod\CHS\ChsIME.exe";

            if (_profileList.Count == 0)
                _profileList.AddRange(_profileMap.Keys);
        }
        catch { }
    }

    // ── Foreground HKL → display name ──

    private static nint GetFgHkl()
    {
        var fg = GetForegroundWindow();
        if (fg == nint.Zero) return nint.Zero;
        var tid = GetWindowThreadProcessId(fg, out _);
        return GetKeyboardLayout(tid);
    }

    private static string ResolveHklName(nint hkl)
    {
        var klid = GetKlidFromHkl(hkl);
        if (string.IsNullOrEmpty(klid)) return "输入法";

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{klid}");
            if (key != null)
            {
                var displayName = key.GetValue("Layout Display Name") as string;
                if (!string.IsNullOrEmpty(displayName) && displayName.StartsWith("@"))
                {
                    var resolved = ResolveIndirectString(displayName);
                    if (!string.IsNullOrEmpty(resolved)) return resolved;
                }
                var layoutText = key.GetValue("Layout Text") as string;
                if (!string.IsNullOrEmpty(layoutText)) return layoutText;
            }
        }
        catch { }
        return klid.ToUpperInvariant() switch
        {
            "00000804" or "E0080804" => "中文输入法",
            "00000409" => "美式键盘",
            "00000411" => "日语",
            "00000412" => "韩语",
            _ => $"布局 {klid}"
        };
    }

    private static string GetKlidFromHkl(nint hkl)
    {
        var klid = new char[KL_NAMELENGTH];
        var result = GetKeyboardLayoutNameW(klid);
        if (result > 0)
        {
            var s = new string(klid).TrimEnd('\0');
            if (!string.IsNullOrEmpty(s)) return s;
        }
        var langId = (ushort)((nuint)hkl & 0xFFFF);
        return $"0000{langId:X4}";
    }

    private static string ResolveIndirectString(string indirect)
    {
        try
        {
            var sb = new StringBuilder(1024);
            var hr = SHLoadIndirectString(indirect, sb, sb.Capacity, nint.Zero);
            if (hr == 0 && sb.Length > 0) return sb.ToString();
        }
        catch { }
        return "";
    }

    public void Dispose() => Stop();

    const int KL_NAMELENGTH = 9;

    // ── TSF COM interop for querying the real active IME profile ──

    [ComImport, Guid("F0B8F830-312C-4A7F-A323-0DF367EB3276")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfileMgr
    {
        int ActivateProfile(uint dwProfileType, uint dwFlags, ref Guid rclsid, ref Guid guidProfile, ref Guid catid, nint hkl, int dwCpu);
        int DeactivateProfile(uint dwProfileType, uint dwFlags, ref Guid rclsid, ref Guid guidProfile, ref Guid catid);
        int GetActiveLanguageProfile(ref Guid rclsid, ref Guid catid, out TF_INPUTPROCESSORPROFILE pProfile);
        int GetProfileInfo(nint a, nint b, nint c, nint d, out TF_INPUTPROCESSORPROFILE pProfile);
        int EnumProfiles(uint dwFlags, out nint ppEnum);
        int GetSubstituteKeyboardLayout(nint a, nint b, nint c, int d, out nint phkl);
        int ReleaseInputProcessor(ref Guid rclsid, ref Guid guidProfile);
        int RegisterProfile(nint a, nint b, nint c, nint d, nint e, nint f, nint g, nint h, nint i, nint j, nint k, nint l, nint m, nint n);
        int UnregisterProfile(ref Guid rclsid, ref Guid guidProfile, ref Guid catid);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct TF_INPUTPROCESSORPROFILE
    {
        public uint dwProfileType;
        public ushort langid;
        public Guid clsid;
        public Guid guidProfile;
        public Guid catid;
        public nint hklSubstitute;
        public uint dwCaps;
        public nint hkl;
        public uint dwFlags;
    }

    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint r, uint f);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid rclsid, nint pUnkOuter, uint dwClsContext, ref Guid riid, out nint ppv);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern nint GetKeyboardLayout(uint idThread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyboardLayoutNameW(char[] pwszKLID);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, nint ppvReserved);
}
