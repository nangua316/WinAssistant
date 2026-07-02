# Publish WinAssistant Release package (self-contained, no .NET runtime required)
# Output folder: WinAssistant\bin\Release\net9.0-windows10.0.26100.0\win-x64\publish\

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Get-Process WinAssistant -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2

Write-Host "Publishing self-contained Release..." -ForegroundColor Cyan
dotnet publish WinAssistant\WinAssistant.csproj -c Release -r win-x64 -p:Platform=x64 --verbosity quiet

if ($LASTEXITCODE -eq 0) {
    $publishDir = "WinAssistant\bin\Release\net9.0-windows10.0.26100.0\win-x64\publish"
    $exe = Join-Path $publishDir "WinAssistant.exe"
    $version = (Get-Item $exe).VersionInfo.FileVersion
    $zipName = "WinAssistant-$version-win-x64.zip"
    $zipPath = Join-Path $PSScriptRoot $zipName

    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force

    $size = Get-ChildItem "$publishDir" -Recurse | Measure-Object Length -Sum | ForEach-Object { '{0:N1} MB' -f ($_.Sum / 1MB) }
    Write-Host "`nPublish succeeded." -ForegroundColor Green
    Write-Host "Folder: $publishDir"
    Write-Host "Zip:    $zipPath"
    Write-Host "Size:   $size"
} else {
    Write-Host "`nPublish failed." -ForegroundColor Red
    exit 1
}
