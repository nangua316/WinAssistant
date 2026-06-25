Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}
'@

$proc = Get-Process WinAssistant -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    Write-Host 'No WinAssistant process'
    exit 1
}
$targetPid = [uint32]$proc.Id
Write-Host "WinAssistant PID: $targetPid"

$callback = [Win32+EnumWindowsProc] {
    param($hWnd, $lParam)
    $winPid = [uint32]0
    [Win32]::GetWindowThreadProcessId($hWnd, [ref]$winPid) | Out-Null
    if ($winPid -ne $targetPid) { return $true }
    $visible = [Win32]::IsWindowVisible($hWnd)
    $sbTitle = New-Object System.Text.StringBuilder(512)
    [Win32]::GetWindowText($hWnd, $sbTitle, 512) | Out-Null
    $sbClass = New-Object System.Text.StringBuilder(256)
    [Win32]::GetClassName($hWnd, $sbClass, 256) | Out-Null
    Write-Host "HWND=$hWnd Visible=$visible Title='$($sbTitle.ToString())' Class='$($sbClass.ToString())'"
    return $true
}
[Win32]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
