# 在 Windows 10 上安装 WinAssistant 所需运行时
# 方式一: winget (推荐，自动处理依赖)
# 方式二: 手动下载安装（备用）

$ErrorActionPreference = "Stop"

# --- 方式一: 用 winget 装 ---
if (Get-Command winget -ErrorAction SilentlyContinue) {
    Write-Host "正在通过 winget 安装 VCLibs..." -ForegroundColor Cyan
    winget install "Microsoft.VCLibs.Desktop.140" --accept-package-agreements 2>&1 | Out-Null

    Write-Host "正在通过 winget 安装 Windows App Runtime..." -ForegroundColor Cyan
    winget install "Windows App Runtime" --accept-package-agreements 2>&1 | Out-Null

    Write-Host "安装完成！" -ForegroundColor Green
    exit 0
}

# --- 方式二: 手动下载安装（备用，winget 不可用时）---
Write-Host "未检测到 winget，使用手动安装方式..." -ForegroundColor Yellow

$tmp = "$env:TEMP\WinAssistantSetup"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

Write-Host "[1/2] 下载并安装 VCLibs..." -ForegroundColor Cyan
try {
    $vclibsUrl = "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx"
    $vclibsOut = "$tmp\VCLibs.appx"
    Invoke-WebRequest -Uri $vclibsUrl -OutFile $vclibsOut -UseBasicParsing
    Add-AppxPackage -Path $vclibsOut
    Write-Host "  VCLibs 安装成功" -ForegroundColor Green
} catch {
    Write-Host "  VCLibs 安装失败: $_" -ForegroundColor Red
    Write-Host "  请手动下载安装:" -ForegroundColor Yellow
    Write-Host "  $vclibsUrl" -ForegroundColor Yellow
}

Write-Host "[2/2] 下载并安装 Windows App Runtime..." -ForegroundColor Cyan
try {
    $runtimeUrl = "https://aka.ms/windowsappsdk/2.0/2.0.1/windowsappruntimeinstall-x64.exe"
    $runtimeOut = "$tmp\WASRuntime.exe"
    Invoke-WebRequest -Uri $runtimeUrl -OutFile $runtimeOut -UseBasicParsing
    Start-Process -FilePath $runtimeOut -ArgumentList "--quiet" -Wait
    Write-Host "  Windows App Runtime 安装成功" -ForegroundColor Green
} catch {
    Write-Host "  Windows App Runtime 安装失败: $_" -ForegroundColor Red
    Write-Host "  请手动下载安装:" -ForegroundColor Yellow
    Write-Host "  $runtimeUrl" -ForegroundColor Yellow
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "如果仍有问题:" -ForegroundColor Cyan
Write-Host "  1. 确保 Windows 10 已更新到最新（设置 → Windows 更新）" -ForegroundColor Gray
Write-Host "  2. 以管理员身份运行此脚本" -ForegroundColor Gray
Write-Host "  3. 最低支持 Windows 10 1809 (build 17763)" -ForegroundColor Gray
