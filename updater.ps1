# Display Prompt updater. Runs as SYSTEM from the "\Ctrl+Alt+Elite\DisplayPrompt Update" scheduled task.
# Checks the latest GitHub release and, if it's newer than the installed exe, downloads it,
# verifies its SHA-256 checksum and swaps in the new exe. The running app notices the new
# version within a minute and restarts into it.
#
# Turn updates off for a PC:  HKLM\Software\Ctrl+Alt+Elite\DisplayPrompt  AutoUpdate = 0 (DWORD)

$ErrorActionPreference = 'Stop'
$Repo    = 'LetsGetCracking/DisplayPrompt'
$AppDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$Exe     = Join-Path $AppDir 'DisplayPrompt.exe'
$DataDir = Join-Path $env:ProgramData 'Ctrl+Alt+Elite\DisplayPrompt'
$LogFile = Join-Path $DataDir 'update.log'
$Work    = Join-Path $DataDir 'update'

function Write-Log($msg) {
    try {
        New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
        if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 256KB) { Remove-Item $LogFile -Force }
        Add-Content -Path $LogFile -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $msg)
    } catch { }
}

function Get-NormalizedVersion([string]$text) {
    $v = [version]($text.Trim().TrimStart('v', 'V'))
    return New-Object System.Version $v.Major, $v.Minor, ([Math]::Max($v.Build, 0))
}

try {
    # Respect the "updates off" setting
    $policy = Get-ItemProperty 'HKLM:\Software\Ctrl+Alt+Elite\DisplayPrompt' -Name AutoUpdate -ErrorAction SilentlyContinue
    if ($policy -and $policy.AutoUpdate -eq 0) { Write-Log 'Updates are turned off (AutoUpdate=0).'; exit 0 }

    if (-not (Test-Path $Exe)) { Write-Log "Installed exe not found at $Exe"; exit 1 }

    # Tidy up the previous version left behind by the last update
    Remove-Item "$Exe.old" -Force -ErrorAction SilentlyContinue

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    # Find the latest release tag from the github.com redirect (not the API, so many PCs
    # behind one campus IP don't hit the API rate limit)
    $req = [Net.HttpWebRequest]::Create("https://github.com/$Repo/releases/latest")
    $req.AllowAutoRedirect = $false
    $req.UserAgent = 'DisplayPrompt-Updater'
    $req.Timeout = 30000
    $resp = $req.GetResponse()
    $location = $resp.Headers['Location']
    $resp.Close()
    if (-not $location -or $location -notmatch '/releases/tag/(v?[\d\.]+)$') {
        Write-Log "No release found (redirect: '$location')."
        exit 0
    }
    $tag = $Matches[1]
    $latest  = Get-NormalizedVersion $tag
    $current = Get-NormalizedVersion ((Get-Item $Exe).VersionInfo.FileVersion)

    if ($latest -le $current) { Write-Log "Up to date (installed $current, latest $latest)."; exit 0 }
    Write-Log "Update available: $current -> $latest"

    # Download the release zip and its checksum
    $name    = "DisplayPrompt-$latest-win-x64.zip"
    $base    = "https://github.com/$Repo/releases/download/$tag"
    if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Work | Out-Null
    $zip = Join-Path $Work $name
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -UseBasicParsing -Uri "$base/$name" -OutFile $zip -UserAgent 'DisplayPrompt-Updater'
    $sumText = (Invoke-WebRequest -UseBasicParsing -Uri "$base/$name.sha256" -UserAgent 'DisplayPrompt-Updater').Content
    if ($sumText -is [byte[]]) { $sumText = [Text.Encoding]::ASCII.GetString($sumText) }

    $expected = ($sumText -split '\s+')[0].Trim().ToLower()
    $actual   = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($expected -ne $actual) { Write-Log "Checksum mismatch - update cancelled (expected $expected, got $actual)."; exit 1 }

    Expand-Archive -Path $zip -DestinationPath $Work -Force
    $newExe = Get-ChildItem $Work -Recurse -Filter 'DisplayPrompt.exe' | Select-Object -First 1
    if (-not $newExe) { Write-Log 'DisplayPrompt.exe missing from the download - update cancelled.'; exit 1 }

    $newVersion = Get-NormalizedVersion $newExe.VersionInfo.FileVersion
    if ($newVersion -ne $latest) { Write-Log "Downloaded exe is v$newVersion, expected v$latest - update cancelled."; exit 1 }

    # Swap: a running exe can be renamed (not overwritten), so move it aside first
    Move-Item $Exe "$Exe.old" -Force
    try {
        Copy-Item $newExe.FullName $Exe -Force
    } catch {
        Move-Item "$Exe.old" $Exe -Force   # put the old one back
        throw
    }

    # Keep the helper scripts current too
    foreach ($f in 'updater.ps1', 'install.ps1', 'uninstall.ps1') {
        $src = Get-ChildItem $Work -Recurse -Filter $f | Select-Object -First 1
        if ($src) { Copy-Item $src.FullName (Join-Path $AppDir $f) -Force }
    }

    Remove-Item $Work -Recurse -Force -ErrorAction SilentlyContinue
    Write-Log "Updated $current -> $latest. Running copies restart into the new version within a minute."
    exit 0
}
catch {
    Write-Log ("Update failed: " + $_.Exception.Message)
    exit 1
}
