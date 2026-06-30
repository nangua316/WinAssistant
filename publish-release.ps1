# 发布 WinAssistant Release 包（self-contained，用户无需安装 .NET 运行时）
# 输出目录：WinAssistant\bin\x64\Release\net9.0-windows10.0.26100.0\win-x64\publish\

$ErrorActionPreference = "Stop"

# 切到仓库根目录
Set-Location $PSScriptRoot

# 清理旧的发布
Remove-Item -Path "WinAssistant\bin\x64\Release" -Recurse -Force -ErrorAction SilentlyContinue

# 停止正在运行的进程
Get-Process WinAssistant -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2

# 构建 + 发布
Write-Host "正在打包（self-contained）..." -ForegroundColor Cyan
dotnet publish WinAssistant\WinAssistant.csproj -c Release -r win-x64 -p:Platform=x64 --verbosity quiet

if ($LASTEXITCODE -eq 0) {
    $publishDir = "WinAssistant\bin\x64\Release\net9.0-windows10.0.26100.0\win-x64\publish"
    Write-Host "`n✅ 打包完成！" -ForegroundColor Green
    Write-Host "输出目录：$publishDir"
    Write-Host "总大小：$(Get-ChildItem "$publishDir" -Recurse | Measure-Object Length -Sum | ForEach-Object { '{0:N1} MB' -f ($_.Sum / 1MB) })"
} else {
    Write-Host "`n❌ 打包失败，检查上面的错误" -ForegroundColor Red
    exit 1
}
