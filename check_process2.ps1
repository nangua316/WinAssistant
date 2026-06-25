Get-WmiObject Win32_Process -Filter "Name='WinAssistant.exe'" | Select-Object ProcessId, CreationDate, @{Name='ExePath';Expression={$_.ExecutablePath}} | Format-List
