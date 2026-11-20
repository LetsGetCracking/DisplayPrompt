# Removes Display Prompt and its auto-update task. Run in an elevated PowerShell.
$dest = Join-Path $env:ProgramFiles 'Ctrl+Alt+Elite\DisplayPrompt'

Get-Process DisplayPrompt -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'DisplayPrompt' -ErrorAction SilentlyContinue

# Remove the auto-update task
try {
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    $folder = $svc.GetFolder('\Ctrl+Alt+Elite')
    $folder.DeleteTask('DisplayPrompt Update', 0)
    if ($folder.GetTasks(1).Count -eq 0) { $svc.GetFolder('\').DeleteFolder('Ctrl+Alt+Elite', 0) }
} catch { }

Start-Sleep -Milliseconds 500
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'Ctrl+Alt+Elite\DisplayPrompt') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Display Prompt removed."
