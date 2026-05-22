using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinAssistant.Helpers;

public static class AppLauncher
{
    /// <returns>"activate", "minimize", or "launch" indicating what action was taken.</returns>
    public static string LaunchOrActivate(string appPath, string arguments, string aumid = "")
    {
        try
        {
            Log($"LaunchOrActivate: {appPath} aumid={aumid}");

            // UWP/appx apps must launch via AUMID (shell:AppsFolder) to initialize properly.
            if (!string.IsNullOrEmpty(aumid))
            {
                Log($"Launch via AUMID: {aumid}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"shell:AppsFolder\\{aumid}",
                    UseShellExecute = true
                });
                return "launch";
            }

            if (!string.IsNullOrEmpty(appPath) && File.Exists(appPath))
            {
                var appDir = Path.GetDirectoryName(appPath);
                nint hWnd = nint.Zero;

                // Find existing visible window from this app.
                // Do NOT search hidden/tray windows — apps minimized to tray
                // (e.g. WeChat) need to be launched fresh so their own
                // initialization logic renders the UI properly.
                if (!string.IsNullOrEmpty(appDir))
                    hWnd = FindWindowFromDir(appDir);

                if (hWnd != nint.Zero)
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
                        Log($"Minimize hWnd={hWnd}");
                        ShowWindow(hWnd, SW_MINIMIZE);
                        return "minimize";
                    }

                    Log($"Activate hWnd={hWnd} visible={IsWindowVisible(hWnd)} title=\"{GetWindowText(hWnd)}\"");

                    var curFg = GetForegroundWindow();
                    Log($"Current foreground: hWnd={curFg} title=\"{GetWindowText(curFg)}\"");

                    GetWindowThreadProcessId(hWnd, out uint tgtPid);

                    // Restore from minimized/hidden state
                    ShowWindowAsync(hWnd, SW_SHOW);
                    ShowWindowAsync(hWnd, SW_RESTORE);

                    // PowerToys SendInput trick
                    var input = new INPUT { type = INPUT_MOUSE };
                    SendInput(1, [input], Marshal.SizeOf<INPUT>());

                    // Try SetForegroundWindow
                    SetForegroundWindow(hWnd);

                    // If still not foreground → AttachThreadInput (with null-safety)
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

                    // Verify
                    fgAfter = GetForegroundWindow();
                    if (fgAfter != nint.Zero)
                    {
                        GetWindowThreadProcessId(fgAfter, out uint fgPidAfter);
                        if (fgPidAfter != tgtPid)
                        {
                            Log($"Activation failed (fg={fgPidAfter} target={tgtPid}), fallback to launch");
                            Process.Start(new ProcessStartInfo { FileName = appPath, Arguments = arguments, UseShellExecute = true });
                            return "launch";
                        }
                    }
                    else
                    {
                        Log($"Activation failed (no foreground), fallback to launch");
                        Process.Start(new ProcessStartInfo { FileName = appPath, Arguments = arguments, UseShellExecute = true });
                        return "launch";
                    }

                    return "activate";
                }

                Log($"Launch new: {appPath}");
                Process.Start(new ProcessStartInfo { FileName = appPath, Arguments = arguments, UseShellExecute = true });
                return "launch";
            }

            Log($"App not found: {appPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed: {ex.Message}");
            Log($"Exception: {ex}");
        }
        return "";
    }

    private static nint FindWindowFromDir(string appDir)
    {
        return FindBestWindowByDir(appDir, requireVisible: true);
    }

    private static nint FindWindowFromDirFast(string? appDir)
    {
        if (string.IsNullOrEmpty(appDir))
            return nint.Zero;

        var fg = GetForegroundWindow();
        if (fg == nint.Zero) return nint.Zero;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0) return nint.Zero;

        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == nint.Zero) return nint.Zero;

        try
        {
            var sb = new StringBuilder(MAX_PATH_BUFFER);
            int size = sb.Capacity;
            if (QueryFullProcessImageNameW(hProcess, 0, sb, ref size))
            {
                var path = sb.ToString();
                appDir = appDir.TrimEnd('\\');
                if (path.StartsWith(appDir, StringComparison.OrdinalIgnoreCase) &&
                    path.Length > appDir.Length + 1 &&
                    (path[appDir.Length] == '\\' || path[appDir.Length] == '/'))
                {
                    return fg;
                }
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return nint.Zero;
    }

    private static nint FindHiddenWindowFromDir(string appDir)
    {
        return FindBestWindowByDir(appDir, requireVisible: false);
    }

    private static nint FindBestWindowByDir(string appDir, bool requireVisible)
    {
        nint best = nint.Zero;
        int bestScore = int.MinValue;
        appDir = appDir.TrimEnd('\\');

        EnumWindows((hWnd, _) =>
        {
            var visible = IsWindowVisible(hWnd);
            if (requireVisible && !visible) return true;
            if (!requireVisible && visible) return true;

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
                        int score = ScoreWindow(hWnd);
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

    private static int ScoreWindow(nint hWnd)
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

    private static readonly string LogPathValue =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinAssistant_dbg.txt");

    private static void Log(string msg)
    {
        try { System.IO.File.AppendAllText(LogPathValue, $"[{DateTime.Now:HH:mm:ss.fff}] AppLauncher: {msg}{Environment.NewLine}"); }
        catch { }
    }

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    private const uint INPUT_MOUSE = 0;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int MAX_PATH_BUFFER = 4096;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZE = 0x20000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
}
