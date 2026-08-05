# Removes the logon task and stops any running instance.
# Run as administrator, same as install-autostart.ps1.

$ErrorActionPreference = 'Stop'

$taskName = 'Vdx'

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    throw "Run this in an elevated PowerShell."
}

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "Removed scheduled task '$taskName'."
} else {
    Write-Host "No scheduled task '$taskName' found."
}

Get-Process -Name 'Vdx' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping Vdx (pid $($_.Id))..."
    $_.Kill()
}

Write-Host "Done. Config and logs are left in place:"
Write-Host "  $env:APPDATA\Vdx"
Write-Host "  $env:LOCALAPPDATA\Vdx\logs"
