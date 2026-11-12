# Installs Display Prompt for ALL users. Run in an elevated PowerShell from the folder containing DisplayPrompt.exe.
$ErrorActionPreference = 'Stop'

$src  = Join-Path $PSScriptRoot 'DisplayPrompt.exe'
$dest = Join-Path $env:ProgramFiles 'Ctrl+Alt+Elite\DisplayPrompt'
$exe  = Join-Path $dest 'DisplayPrompt.exe'

if (-not (Test-Path $src)) { throw "DisplayPrompt.exe not found next to this script." }

# Stop any running copy and wait until it has fully exited
$running = Get-Process DisplayPrompt -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null

# Retry the copy in case Windows is still releasing the file
for ($i = 1; $i -le 10; $i++) {
    try { Copy-Item $src $exe -Force; break }
    catch {
        if ($i -eq 10) { throw }
        Start-Sleep -Milliseconds 500
    }
}

# Start at sign-in for every user
Set-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'DisplayPrompt' -Value "`"$exe`""

Start-Process $exe
Write-Host "Installed to $exe and set to start for all users at sign-in."
