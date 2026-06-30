# 构建 + 重启 WinAssistant（开发用）
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Get-Process WinAssistant -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 3

dotnet build WinAssistant\WinAssistant.csproj -c Debug -p:Platform=x64 --verbosity quiet

if ($LASTEXITCODE -eq 0) {
    Start-Process -FilePath "WinAssistant\bin\x64\Debug\net9.0-windows10.0.26100.0\win-x64\WinAssistant.exe"
    Write-Host "✅ 构建成功，已启动" -ForegroundColor Green
} else {
    Write-Host "❌ 构建失败" -ForegroundColor Red
    exit 1
}
