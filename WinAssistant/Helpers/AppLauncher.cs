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

            if (!string.IsNullOrEmpty(appPath) && File.Exists(appPath))
            {
                var appDir = Path.GetDirectoryName(appPath);
                nint hWnd = nint.Zero;

                // Step 1: Find a visible top-level window from this app directory.
                if (!string.IsNullOrEmpty(appDir))
                    hWnd = FindWindowFromDir(appDir);

                // Step 1b: If no visible window found, look for a hidden/minimized
                // window from the same app (e.g. apps minimized to system tray).
                if (hWnd == nint.Zero && !string.IsNullOrEmpty(appDir))
                    hWnd = FindHiddenWindowFromDir(appDir);

                // Step 2: If no window found by directory, check the foreground window
                // (it may have been missed if it was the active window during enumeration).
                if (hWnd == nint.Zero)
                    hWnd = FindWindowFromDirFast(appDir);

                if (hWnd != nint.Zero)
                {
                    var fg = GetForegroundWindow();

                    // Check if the app's window (or any of its child windows) is foreground
                    // by comparing process IDs. hWnd == fg fails for apps like WeChat
                    // whose main window handle doesn't match the foreground child window.
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

                    Log($"Activate hWnd={hWnd} visible={IsWindowVisible(hWnd)}");

                    // SC_RESTORE restores minimized windows (harmless for normal windows).
                    SendMessage(hWnd, WM_SYSCOMMAND, SC_RESTORE, 0);

                    // Bring to top of z-order.
                    SetWindowPos(hWnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

                    // Set foreground with thread attachment to bypass foreground lock.
                    var fgThreadId = GetWindowThreadProcessId(fg, out _);
                    var appThreadId = GetWindowThreadProcessId(hWnd, out _);
                    if (fgThreadId != appThreadId)
                        AttachThreadInput(appThreadId, fgThreadId, true);
                    SetForegroundWindow(hWnd);
                    if (fgThreadId != appThreadId)
                        AttachThreadInput(appThreadId, fgThreadId, false);

                    return "activate";
                }

                Log($"Launch new: {appPath}");
                Process.Start(new ProcessStartInfo { FileName = appPath, Arguments = arguments, UseShellExecute = true });
                return "launch";
            }

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

            Log($"App not found: {appPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed: {ex.Message}");
            Log($"Exception: {ex}");
        }
        return "";
    }

    /// <summary>
    /// Find a visible top-level window whose process image lives under appDir.
    /// Uses EnumWindows + QueryFullProcessImageName — much faster than Process.GetProcesses().
    /// Includes minimized (iconic) windows so toggle minimize works reliably.
    /// </summary>
    private static nint FindWindowFromDir(string appDir)
    {
        nint found = nint.Zero;
        appDir = appDir.TrimEnd('\\');

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);

            var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == nint.Zero)
                return true;

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
                        found = hWnd;
                        return false; // stop
                    }
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
            return true;
        }, nint.Zero);

        return found;
    }

    /// <summary>
    /// Quick fallback: check if the foreground window's process lives under appDir.
    /// </summary>
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
                    Log($"Fast fallback found fg hWnd={fg} pid={pid}");
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

    /// <summary>
    /// Find a hidden/minimized window from the same app directory.
    /// Used for apps that minimize to the system tray (Everything, QQ, etc.) where
    /// the window handle still exists but is hidden (IsWindowVisible=false).
    /// </summary>
    private static nint FindHiddenWindowFromDir(string appDir)
    {
        nint found = nint.Zero;
        appDir = appDir.TrimEnd('\\');

        EnumWindows((hWnd, _) =>
        {
            // Skip visible windows — we already searched those in FindWindowFromDir.
            if (IsWindowVisible(hWnd))
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);

            var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == nint.Zero)
                return true;

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
                        found = hWnd;
                        return false; // stop
                    }
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
            return true;
        }, nint.Zero);

        return found;
    }

    private static readonly string LogPathValue =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinAssistant_dbg.txt");

    private static void Log(string msg)
    {
        try { System.IO.File.AppendAllText(LogPathValue, $"[{DateTime.Now:HH:mm:ss.fff}] AppLauncher: {msg}{Environment.NewLine}"); }
        catch { }
    }

    // Constants
    private const int SW_MINIMIZE = 6;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int MAX_PATH_BUFFER = 4096;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint SC_RESTORE = 0xF120;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly nint HWND_TOP = (nint)0;

    // P/Invoke
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint hWnd, uint msg, uint wParam, int lParam);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
}