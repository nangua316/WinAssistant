$p = Get-Process WinAssistant -ErrorAction SilentlyContinue
if ($p) {
    Write-Host "Running: PID=$($p.Id)"
} else {
    Write-Host 'Not running'
}

$log = 'C:\Users\likan\AppData\Local\Temp\WinAssistant_dbg.txt'
if (Test-Path $log) {
    Get-Content $log -Tail 50
} else {
    Write-Host 'Debug log not found'
}
