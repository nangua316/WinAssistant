# Kill all WinAssistant processes
Get-Process WinAssistant -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Killing PID $($_.Id)..."
    $_.Kill()
}
Start-Sleep 4
$remain = Get-Process WinAssistant -ErrorAction SilentlyContinue
if ($remain) {
    Write-Host "Still running:"
    $remain | Format-Table Id, StartTime
} else {
    Write-Host "All cleared"
}
