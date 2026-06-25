Get-WmiObject Win32_Process -Filter "Name='WinAssistant.exe'" | Select-Object ProcessId, CommandLine, CreationDate | Format-List
