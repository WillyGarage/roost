# Registers Vdx to start at logon, elevated.
#
# MUST BE RUN AS ADMINISTRATOR (registering a highest-privileges task requires it).
#
# Why a scheduled task rather than the Run registry key or a Startup shortcut:
# moving a window owned by an elevated process requires this app to be elevated too
# (UIPI blocks it otherwise). The Run key would show a UAC prompt at every single logon.
# A scheduled task with RunLevel Highest starts it elevated and silently.

$ErrorActionPreference = 'Stop'

$taskName = 'Vdx'
$root     = Split-Path -Parent $PSScriptRoot
$exe      = Join-Path $root 'dist\Vdx.exe'

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    throw "Run this in an elevated PowerShell. Right-click PowerShell > Run as administrator."
}

if (-not (Test-Path $exe)) {
    throw "$exe not found. Run scripts\publish.ps1 first."
}

if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Write-Host "Removing existing '$taskName' task..."
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

$action  = New-ScheduledTaskAction  -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

# RunLevel Highest is the whole point. LogonType Interactive is required for a task that
# shows UI (the palette) and puts an icon in the notification area.
$principal = New-ScheduledTaskPrincipal `
    -UserId $env:USERNAME `
    -LogonType Interactive `
    -RunLevel Highest

# Defaults fight a long-running tray app: it must not be killed after 3 days, must not
# be stopped on battery, and must restart if it dies.
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -DontStopOnIdleEnd `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask `
    -TaskName    $taskName `
    -Action      $action `
    -Trigger     $trigger `
    -Principal   $principal `
    -Settings    $settings `
    -Description 'Move the active window to another virtual desktop from a search palette.' `
    | Out-Null

Write-Host "Registered scheduled task '$taskName' -> $exe"
Write-Host "Starting it now..."

Start-ScheduledTask -TaskName $taskName
Start-Sleep -Seconds 2

$state = (Get-ScheduledTask -TaskName $taskName).State
Write-Host "Task state: $state"
Write-Host ""
Write-Host "Look for the Vdx icon in the notification area. Right-click it for the"
Write-Host "hotkey list, the config file, and the logs."
