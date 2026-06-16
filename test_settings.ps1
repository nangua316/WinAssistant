Add-Type @"
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern nint FindWindowW(string c, string n);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")]
    public static extern int GetWindowTextW(nint h, System.Text.StringBuilder t, int n);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll")]
    public static extern nint SendMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);
}
"@

$MainWindow = [Win32]::FindWindowW("", "WinAssistant - 设置")
if ($MainWindow -ne 0) {
    $vis = [Win32]::IsWindowVisible($MainWindow)
    $sb = New-Object System.Text.StringBuilder 256
    [Win32]::GetWindowTextW($MainWindow, $sb, 256)
    $pid = 0
    [Win32]::GetWindowThreadProcessId($MainWindow, [ref]$pid)
    Write-Host "MainWindow: handle=0x$($MainWindow.ToString('X8')) visible=$vis title='$($sb.ToString())' pid=$pid"
} else {
    Write-Host "MainWindow not found"
}

# Check all windows belonging to WinAssistant process
$procs = Get-Process WinAssistant -ErrorAction SilentlyContinue
if ($procs) {
    Write-Host "WinAssistant PID(s): $($procs.Id -join ', ')"
    foreach ($p in $procs) {
        $p.MainWindowHandle
        $p.MainWindowTitle
    }
}
