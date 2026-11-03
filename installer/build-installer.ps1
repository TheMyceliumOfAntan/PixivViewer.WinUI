# Build script: publish (unpackaged, self-contained) -> Inno Setup installer
# Usage: powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#        powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.1.0

param(
    [string]$Version = "1.2.0",
    [string]$Configuration = "Release",
    [string]$PublishDir = "D:\CSharp\Release\PixivViewer-win-x64",
    [string]$OutputDir = "D:\CSharp\Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "Pixiv Viewer WinUI\Pixiv Viewer WinUI\Pixiv Viewer WinUI.csproj"
$iss = Join-Path $PSScriptRoot "PixivViewer.iss"

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6 first: winget install JRSoftware.InnoSetup"
}

Write-Host "==> [1/2] Publishing app (unpackaged, self-contained)" -ForegroundColor Cyan
dotnet publish $csproj -p:PublishProfile=Unpackaged -c $Configuration -v:m
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> [2/2] Building installer" -ForegroundColor Cyan
& $iscc $iss "/DMyAppVersion=$Version" "/DMySourceDir=$PublishDir" "/DMyOutputDir=$OutputDir"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Join-Path $OutputDir "PixivViewer-Setup-$Version-x64.exe"
if (Test-Path $setup) {
    Write-Host ("DONE: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
} else {
    throw "Installer not found: $setup"
}
