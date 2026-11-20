# Installs Display Prompt for ALL users. Run in an elevated PowerShell from the folder containing DisplayPrompt.exe.
# Also registers the "\Ctrl+Alt+Elite\DisplayPrompt Update" scheduled task that keeps it up to date from GitHub.
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
Remove-Item "$exe.old" -Force -ErrorAction SilentlyContinue

# Copy the helper scripts next to the exe (the updater task runs updater.ps1 from here)
foreach ($f in 'updater.ps1', 'install.ps1', 'uninstall.ps1') {
    $p = Join-Path $PSScriptRoot $f
    if (Test-Path $p) {
        $target = Join-Path $dest $f
        if ((Resolve-Path $p).Path -ne $target) { Copy-Item $p $target -Force }
    }
}

# Start at sign-in for every user
Set-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'DisplayPrompt' -Value "`"$exe`""

# ---- Auto-update task (runs as SYSTEM; any signed-in user may start it, but not change it) ----
$updater = Join-Path $dest 'updater.ps1'
if (Test-Path $updater) {
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    $root = $svc.GetFolder('\')
    try { $folder = $root.GetFolder('\Ctrl+Alt+Elite') } catch { $folder = $root.CreateFolder('\Ctrl+Alt+Elite') }

    $def = $svc.NewTask(0)
    $def.RegistrationInfo.Description = 'Keeps Display Prompt up to date from its GitHub releases.'
    $def.Settings.Enabled = $true
    $def.Settings.StartWhenAvailable = $true
    $def.Settings.RunOnlyIfNetworkAvailable = $true
    $def.Settings.DisallowStartIfOnBatteries = $false
    $def.Settings.StopIfGoingOnBatteries = $false
    $def.Settings.MultipleInstances = 2          # if already running, ignore new requests
    $def.Settings.ExecutionTimeLimit = 'PT30M'
    $def.Settings.Hidden = $true

    $boot = $def.Triggers.Create(8)              # at startup
    $boot.Delay = 'PT3M'
    $daily = $def.Triggers.Create(2)             # every day
    $daily.StartBoundary = (Get-Date -Hour 3 -Minute 17 -Second 0).ToString('s')
    $daily.DaysInterval = 1
    $daily.RandomDelay = 'PT2H'                  # spread PCs out

    $action = $def.Actions.Create(0)
    $action.Path = 'powershell.exe'
    $action.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$updater`""

    $def.Principal.RunLevel = 1                  # highest

    # SYSTEM and Administrators: full control. Authenticated users: read + run only.
    $sddl = 'D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)'
    $folder.RegisterTaskDefinition('DisplayPrompt Update', $def, 6, 'SYSTEM', $null, 5, $sddl) | Out-Null
    Write-Host "Auto-update task registered (\Ctrl+Alt+Elite\DisplayPrompt Update)."
} else {
    Write-Host "updater.ps1 not found - installed without auto-update."
}

# Start the app as the signed-in user (not as administrator).
# Launching through Explorer drops the admin rights of this window.
try { Start-Process -FilePath "$env:WINDIR\explorer.exe" -ArgumentList "`"$exe`"" } catch { }

Write-Host "Installed to $exe and set to start for all users at sign-in."
