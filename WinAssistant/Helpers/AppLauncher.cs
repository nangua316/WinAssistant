using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using WinAssistant.Services;

namespace WinAssistant.Helpers;

public static class AppLauncher
{
    /// <returns>"activate", "minimize", or "launch" indicating what action was taken.</returns>
    public static string LaunchOrActivate(string appPath, string arguments, string aumid = "")
    {
        try
        {
            Logger.Log("AppLauncher",$"LaunchOrActivate: {appPath} aumid={aumid}");

            // Directory — open in Explorer
            if (!string.IsNullOrEmpty(appPath) && Directory.Exists(appPath))
            {
                Process.Start(new ProcessStartInfo { FileName = appPath, UseShellExecute = true });
                return "launch";
            }

            // Background daemon/service/IME — can't be usefully activated
            if (!string.IsNullOrEmpty(appPath) && AppFilter.IsDaemonProcess(appPath))
            {
                Logger.Log("AppLauncher",$"Skipped daemon: {appPath}");
                HotKeyToast.Show("该应用为后台服务，无法直接启动");
                return "";
            }

            if (!string.IsNullOrEmpty(appPath) && File.Exists(appPath))
            {
                var appDir = Path.GetDirectoryName(appPath);
                nint hWnd = nint.Zero;

                if (!string.IsNullOrEmpty(appDir))
                {
                    // Step 0: if this app's process already owns the foreground window,
                    // find the best (highest-scored) window from this app and
                    // activate/minimise it.  Using the foreground window directly
                    // can pick up a compose or child window (e.g. FoxMail's
                    // "写邮件" popup) which minimises to the desktop corner
                    // instead of the taskbar.
                    var fg = GetForegroundWindow();
                    if (fg != nint.Zero && IsWindowOwnedByDir(fg, appDir))
                    {
                        var best = FindBestWindowByDir(appDir, null);
                        if (best != nint.Zero)
                            return ActivateOrMinimizeWindow(best);
                        return ActivateOrMinimizeWindow(fg);
                    }

                    // Step 1: find a visible window from this app's process
                    hWnd = FindWindowFromDir(appDir);

                    if (hWnd != nint.Zero)
                    {
                        var title = GetWindowText(hWnd);
                        if (!string.IsNullOrEmpty(title))
                        {
                            // Genuine window (has a title) — activate or minimize
                            return ActivateOrMinimizeWindow(hWnd);
                        }

                        // Empty title: likely a helper/splash, not the real window.
                        // Fall through to hidden window search for tray-restore.
                        Logger.Log("AppLauncher",$"Visible window empty title hWnd={hWnd}, trying hidden...");
                    }

                    // Step 2: try hidden windows (tray apps)
                    var hidden = FindHiddenWindowFromDir(appDir);
                    if (hidden != nint.Zero)
                    {
                        // Only try multi-strategy restore for packaged/UWP apps
                        // (e.g. Doubao). Classic Win32 apps (e.g. WeChat) get
                        // LaunchFresh to avoid white page / broken compositor.
                        if (IsWindowFromPackagedApp(hidden))
                            return ActivateHiddenWindow(hidden, appPath, arguments, aumid);
                        else
                            return LaunchFresh(appPath, arguments, aumid);
                    }

                    // Step 3: no hidden window found, but we had a visible empty-title
                    // window — try it as last resort (better than nothing).
                    if (hWnd != nint.Zero)
                        return ActivateOrMinimizeWindow(hWnd);
                }

                // Step 4: no window at all, launch fresh
                return LaunchFresh(appPath, arguments, aumid);
            }

            return LaunchFresh(appPath, arguments, aumid);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed: {ex.Message}");
            Logger.Log("AppLauncher",$"Exception: {ex}");
        }
        return "";
    }

    private static string ActivateOrMinimizeWindow(nint hWnd)
    {
        var fg = GetForegroundWindow();
        bool isForeground;
        if (hWnd == fg)
        {
            isForeground = true;
        }
        else
        {
            GetWindowThreadProcessId(fg, out uint fgPid);
            GetWindowThreadProcessId(hWnd, out uint appPid);
            isForeground = fgPid == appPid && appPid != 0;
        }

        if (isForeground)
        {
            Logger.Log("AppLauncher",$"Minimize hWnd={hWnd}");
            // Use PostMessage WM_SYSCOMMAND SC_MINIMIZE instead of ShowWindow(SW_MINIMIZE)
            // so the message goes through the app's own WndProc (Electron apps need this
            // to properly minimise to the taskbar rather than the desktop corner).
            PostMessage(hWnd, WM_SYSCOMMAND, SC_MINIMIZE, nint.Zero);
            return "minimize";
        }

        Logger.Log("AppLauncher",$"Activate hWnd={hWnd} visible={IsWindowVisible(hWnd)} title=\"{GetWindowText(hWnd)}\"");

        var curFg = GetForegroundWindow();
        Logger.Log("AppLauncher",$"Current foreground: hWnd={curFg} title=\"{GetWindowText(curFg)}\"");

        ShowWindowAsync(hWnd, SW_SHOW);
        ShowWindowAsync(hWnd, SW_RESTORE);

        // PowerToys SendInput trick
        var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());

        SetForegroundWindow(hWnd);

        // AttachThreadInput fallback
        var fgAfter = GetForegroundWindow();
        if (fgAfter != hWnd && fgAfter != nint.Zero)
        {
            var tgtTid = GetWindowThreadProcessId(hWnd, out _);
            var fgTid = GetWindowThreadProcessId(fgAfter, out _);
            if (tgtTid != fgTid && fgTid != 0)
            {
                AttachThreadInput(tgtTid, fgTid, true);
                SetForegroundWindow(hWnd);
                AttachThreadInput(tgtTid, fgTid, false);
            }
        }

        Logger.Log("AppLauncher",$"Activate done fg={fgAfter}");
        return "activate";
    }

    private static string ActivateHiddenWindow(nint hWnd, string appPath, string arguments, string aumid)
    {
        Logger.Log("AppLauncher",$"Activate packaged hidden hWnd={hWnd} title=\"{GetWindowText(hWnd)}\"");

        // 1. DWM uncloak — Windows 10/11 may cloak tray windows via DWM
        try
        {
            int cloaked = 0;
            int hr = DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, ref cloaked, sizeof(int));
            if (hr == 0 && cloaked != 0)
            {
                int falseVal = 0;
                DwmSetWindowAttribute(hWnd, DWMWA_CLOAK, ref falseVal, sizeof(int));
                Logger.Log("AppLauncher",$"Uncloaked window (was cloaked={cloaked})");
            }
        }
        catch { }

        // 2. UI Automation SetWindowVisualState — goes through app's own UIA provider
        //    (runs inside the target process, proper initialization path)
        try
        {
            Logger.Log("AppLauncher",$"Trying UIA SetWindowVisualState (COM interop)...");
            var uia = new CUIAutomation();
            var element = uia.ElementFromHandle(hWnd);
            if (element != null)
            {
                var patternObj = element.GetCurrentPattern(10024); // UIA_WindowPatternId
                if (patternObj is IUIAutomationWindowPattern wp)
                {
                    wp.SetWindowVisualState(WindowVisualState.WindowVisualState_Normal);
                    Thread.Sleep(200);
                    if (IsWindowVisible(hWnd))
                    {
                        Logger.Log("AppLauncher",$"UIA activation succeeded");
                        goto Foreground;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log("AppLauncher",$"UIA activation failed: {ex.Message}");
        }

        // 3. SC_RESTORE through app's WndProc (synchronous)
        SendMessageTimeout(hWnd, WM_SYSCOMMAND, SC_RESTORE, 0,
            SMTO_ABORTIFHUNG | SMTO_NORMAL, 500, out _);
        Thread.Sleep(100);
        if (IsWindowVisible(hWnd))
        {
            Logger.Log("AppLauncher",$"SC_RESTORE succeeded");
            goto Foreground;
        }

        // 4. PowerToys approach — SendInput + SetForegroundWindow
        var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
        SetForegroundWindow(hWnd);

        var fgAfter = GetForegroundWindow();
        if (fgAfter != hWnd && fgAfter != nint.Zero)
        {
            var tgtTid = GetWindowThreadProcessId(hWnd, out _);
            var fgTid = GetWindowThreadProcessId(fgAfter, out _);
            if (tgtTid != fgTid && fgTid != 0)
            {
                AttachThreadInput(tgtTid, fgTid, true);
                SetForegroundWindow(hWnd);
                AttachThreadInput(tgtTid, fgTid, false);
            }
        }

        Thread.Sleep(100);
        if (IsWindowVisible(hWnd))
        {
            Logger.Log("AppLauncher",$"PowerToys approach succeeded");
            goto Foreground;
        }

        // Fallback: launch fresh synchronously
        Logger.Log("AppLauncher",$"All hidden window strategies failed, launching fresh");
        LaunchFresh(appPath, arguments, aumid);
        return "launch";

    Foreground:
        // PowerToys approach to bring window to foreground
        var fgInput = new INPUT { type = INPUT_MOUSE };
        SendInput(1, [fgInput], Marshal.SizeOf<INPUT>());
        SetForegroundWindow(hWnd);

        var fgCurrent = GetForegroundWindow();
        if (fgCurrent != hWnd && fgCurrent != nint.Zero)
        {
            var tgtTid = GetWindowThreadProcessId(hWnd, out _);
            var fgTid = GetWindowThreadProcessId(fgCurrent, out _);
            if (tgtTid != fgTid && fgTid != 0)
            {
                AttachThreadInput(tgtTid, fgTid, true);
                SetForegroundWindow(hWnd);
                AttachThreadInput(tgtTid, fgTid, false);
            }
        }

        return "activate";
    }

    private static string LaunchFresh(string appPath, string arguments, string aumid)
    {
        if (!string.IsNullOrEmpty(aumid))
        {
            Logger.Log("AppLauncher",$"Launch via AUMID: {aumid}");
            Process.Start(new ProcessStartInfo
            {
                FileName = $"shell:AppsFolder\\{aumid}",
                UseShellExecute = true
            });
            return "launch";
        }

        if (!string.IsNullOrEmpty(appPath) && File.Exists(appPath))
        {
            var exeName = Path.GetFileNameWithoutExtension(appPath.AsSpan());
            bool isChrome = exeName.Equals("chrome", StringComparison.OrdinalIgnoreCase)
                || exeName.Equals("msedge", StringComparison.OrdinalIgnoreCase)
                || exeName.Equals("brave", StringComparison.OrdinalIgnoreCase);

            if (isChrome)
            {
                // Use default profile to avoid the multi-user picker dialog.
                Logger.Log("AppLauncher",$"Launch Chrome with default profile: {appPath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = appPath,
                    Arguments = "--profile-directory=\"Default\"",
                    UseShellExecute = true
                });
            }
            else
            {
                Logger.Log("AppLauncher",$"Launch new: {appPath}");
                Process.Start(new ProcessStartInfo { FileName = appPath, Arguments = arguments, UseShellExecute = true });
            }
            return "launch";
        }

        Logger.Log("AppLauncher",$"App not found: {appPath}");
        return "";
    }

    private static nint FindWindowFromDir(string appDir)
    {
        return FindBestWindowByDir(appDir, true);
    }

    private static nint FindHiddenWindowFromDir(string appDir)
    {
        return FindBestWindowByDir(appDir, false);
    }

    private static bool IsWindowOwnedByDir(nint hWnd, string appDir)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == nint.Zero) return false;
        try
        {
            var sb = new StringBuilder(MAX_PATH_BUFFER);
            int size = sb.Capacity;
            if (QueryFullProcessImageNameW(hProcess, 0, sb, ref size))
            {
                var path = sb.ToString();
                var dir = appDir.TrimEnd('\\');
                return path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) &&
                       path.Length > dir.Length + 1 &&
                       (path[dir.Length] == '\\' || path[dir.Length] == '/');
            }
        }
        finally { CloseHandle(hProcess); }
        return false;
    }

    private static nint FindBestWindowByDir(string appDir, bool? requireVisible)
    {
        nint best = nint.Zero;
        int bestScore = int.MinValue;
        appDir = appDir.TrimEnd('\\');

        EnumWindows((hWnd, _) =>
        {
            if (requireVisible.HasValue)
            {
                var visible = IsWindowVisible(hWnd);
                if (requireVisible.Value && !visible) return true;
                if (!requireVisible.Value && visible) return true;
            }

            GetWindowThreadProcessId(hWnd, out uint pid);
            var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == nint.Zero) return true;

            try
            {
                var sb = new StringBuilder(MAX_PATH_BUFFER);
                int size = sb.Capacity;
                if (QueryFullProcessImageNameW(hProcess, 0, sb, ref size))
                {
                    var path = sb.ToString();
                    if (path.StartsWith(appDir, StringComparison.OrdinalIgnoreCase) &&
                        path.Length > appDir.Length + 1 &&
                        (path[appDir.Length] == '\\' || path[appDir.Length] == '/'))
                    {
                        int score = ScoreWindow(hWnd, path);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = hWnd;
                        }
                    }
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
            return true;
        }, nint.Zero);

        return best;
    }

    private static int ScoreWindow(nint hWnd, string appPath = "")
    {
        var title = GetWindowText(hWnd);
        var className = GetWindowClassName(hWnd);
        bool hasTitle = !string.IsNullOrEmpty(title);

        int score = 0;

        if (hasTitle) score += 10;
        if (hasTitle && title.Length > 1) score += 5;

        // Size check
        GetWindowRect(hWnd, out var rect);
        int w = rect.right - rect.left;
        int h = rect.bottom - rect.top;
        if (w > 200 && h > 200) score += 8;
        if (w < 80 || h < 80) score -= 10;

        // Style
        int style = GetWindowLong(hWnd, GWL_STYLE);
        if ((style & WS_CAPTION) == WS_CAPTION) score += 5;
        if ((style & WS_THICKFRAME) != 0) score += 3;
        if ((style & WS_MINIMIZE) != 0) score -= 5;

        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) score -= 8;
        if ((exStyle & WS_EX_APPWINDOW) != 0) score += 3;

        // Penalize WebView2 / XAML helper windows
        if (className.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)) score -= 20;
        if (className.Contains("Chrome_RenderWidget", StringComparison.OrdinalIgnoreCase)) score -= 20;
        if (className.Contains("Windows.UI.Composition", StringComparison.OrdinalIgnoreCase)) score -= 10;

        // App-specific rule adjustments
        if (!string.IsNullOrEmpty(appPath))
        {
            var adj = AppWindowRules.GetScoreAdjustment(appPath, className, hasTitle);
            if (adj.HasValue) score += adj.Value;
        }

        return score;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    private static string GetWindowClassName(nint hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hWnd, sb, sb.Capacity);
        return sb.ToString().TrimEnd('\0');
    }

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int MAX_PATH_BUFFER = 4096;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZE = 0x20000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    // DWM cloaking
    private const uint DWMWA_CLOAK = 14;
    private const uint DWMWA_CLOAKED = 15;

    // Window messages
    private const uint WM_SYSCOMMAND = 0x0112;
    private const nint SC_RESTORE = 0xF120;
    private const nint SC_MINIMIZE = 0xF020;
    private const uint SMTO_NORMAL = 0x0000;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, StringBuilder lpString, int nMaxCount);

    private static string GetWindowText(nint hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(hWnd, sb, sb.Capacity);
        return sb.ToString().TrimEnd('\0');
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumPos, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hWnd, uint Msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, uint attr, ref int attrValue, int attrSize);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attr, ref int attrValue, int attrSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetPackageFamilyName(nint hProcess, ref uint bufferLength, StringBuilder? packageFamilyName);

    private static bool IsWindowFromPackagedApp(nint hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == nint.Zero) return false;
        try
        {
            uint length = 256;
            var sb = new StringBuilder(256);
            int result = GetPackageFamilyName(hProcess, ref length, sb);
            // 0=success, 122=ERROR_INSUFFICIENT_BUFFER (packaged, buffer too small),
            // 15700=APPMODEL_ERROR_NO_PACKAGE (not a packaged app)
            return result == 0 || result == 122;
        }
        finally { CloseHandle(hProcess); }
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
}
