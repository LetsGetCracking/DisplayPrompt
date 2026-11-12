# Removes Display Prompt. Run in an elevated PowerShell.
$dest = Join-Path $env:ProgramFiles 'Ctrl+Alt+Elite\DisplayPrompt'

Get-Process DisplayPrompt -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'DisplayPrompt' -ErrorAction SilentlyContinue
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Display Prompt removed."
