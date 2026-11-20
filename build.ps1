# Builds DisplayPrompt.exe into .\publish and installs it for all users.
# Run in an ADMIN PowerShell:  & "<this folder>\build.ps1"
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Please run this in an ADMIN PowerShell (right-click Start > Terminal (Admin))." }

Write-Host "`n[1/4] Checking files..." -ForegroundColor Cyan
foreach ($f in 'DisplayPrompt.csproj','PopupWindow.xaml','App.xaml.cs','app.ico','app.png','install.ps1','updater.ps1') {
    if (-not (Test-Path $f)) { throw "Missing $f in $PSScriptRoot" }
}
Write-Host "      All files present."

Write-Host "[2/4] Stopping running copy..." -ForegroundColor Cyan
$running = Get-Process DisplayPrompt -ErrorAction SilentlyContinue
if ($running) { $running | Stop-Process -Force; $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue }

Write-Host "[3/4] Building..." -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if ($LASTEXITCODE -ne 0) { throw "Build failed - see the errors above." }

Copy-Item install.ps1, uninstall.ps1, updater.ps1 publish\ -Force

Write-Host "[4/4] Installing..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'publish\install.ps1')

Write-Host "`nDone. Your exe: $(Join-Path $PSScriptRoot 'publish\DisplayPrompt.exe')" -ForegroundColor Green
