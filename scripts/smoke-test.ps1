# Automated smoke test: launches the published exe, drives every hotkey, and prints the
# log, so the whole path can be verified without anyone watching the screen.
#
# Default run only OPENS and dismisses palettes; nothing is moved or created.
# Pass -Commit to also perform a real move: it sends a scratch Character Map window to
# a named desktop, follows it, then switches back. Still creates nothing permanent.

param(
    [switch]$Commit,

    # Desktop to move the scratch window to during -Commit, matched by the same fuzzy
    # search the palette uses. Defaults to any desktop other than the current one, so the
    # test is not tied to one machine's layout.
    [string]$MoveTo,

    # Desktop to return to afterwards. Defaults to wherever you started.
    [string]$ReturnTo,

    # Also exercise the create-name-position-move path, then delete the desktop it made.
    [switch]$TestCreate
)

. (Join-Path $PSScriptRoot 'common.ps1')

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root 'dist\Vdx.exe'
$log  = Join-Path $env:LOCALAPPDATA "Vdx\logs\vdx-$(Get-Date -Format yyyyMMdd).log"

if (-not (Test-Path $exe)) { throw "$exe not found. Run scripts\publish.ps1 first." }

Add-Type -Name Keys -Namespace Smoke -MemberDefinition @'
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);
'@

$WIN = 0x5B; $CTRL = 0x11; $ALT = 0x12; $ESC = 0x1B; $RET = 0x0D
$UP  = 2

function Send-Key([byte]$vk) {
    [Smoke.Keys]::keybd_event($vk, 0, 0,   [UIntPtr]::Zero)
    [Smoke.Keys]::keybd_event($vk, 0, $UP, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 35
}

# Enter alone only switches desktops now; a real move needs Alt+Enter (move and follow).
function Send-AltKey([byte]$vk) {
    [Smoke.Keys]::keybd_event($ALT, 0, 0,   [UIntPtr]::Zero)
    [Smoke.Keys]::keybd_event($vk,  0, 0,   [UIntPtr]::Zero)
    [Smoke.Keys]::keybd_event($vk,  0, $UP, [UIntPtr]::Zero)
    [Smoke.Keys]::keybd_event($ALT, 0, $UP, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 35
}

function Send-WinCtrl([byte]$key) {
    [Smoke.Keys]::keybd_event($WIN,  0, 0,   [UIntPtr]::Zero)
    [Smoke.Keys]::keybd_event($CTRL, 0, 0,   [UIntPtr]::Zero)
    [Smoke.Keys]::keybd_event($key,  0, 0,   [UIntPtr]::Zero)
    [Smoke.Keys]::keybd_event($key,  0, $UP, [UIntPtr]::Zero)
    [Smoke.Keys]::keybd_event($CTRL, 0, $UP, [UIntPtr]::Zero)
    [Smoke.Keys]::keybd_event($WIN,  0, $UP, [UIntPtr]::Zero)
}

function Send-Text([string]$text) {
    foreach ($c in $text.ToCharArray()) {
        if ($c -eq ' ')             { Send-Key 0x20 }
        elseif ($c -match '[A-Za-z0-9]') { Send-Key ([byte][char]([string]$c).ToUpper()) }
        # Anything else is skipped: this helper only needs to drive the search box.
    }
}

Get-Process -Name 'Vdx' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "stopping existing instance (pid $($_.Id))"
    $_.Kill(); $_.WaitForExit(3000) | Out-Null
}

# Start from a clean log so the tail below covers only this run.
if (Test-Path $log) { Remove-Item $log -Force }

Write-Host "starting $exe"
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4

if ($proc.HasExited) { throw "Vdx exited immediately with code $($proc.ExitCode)" }
Write-Host "running as pid $($proc.Id)"

# The capture step needs a real foreground window to act on. charmap is a convenient
# volunteer: plain Win32, one window per process, and never tab-merges into anything.
Write-Host "opening a scratch Character Map window"
$scratch = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\charmap.exe') -PassThru
Start-Sleep -Seconds 2

# Read the chords from config rather than hardcoding them, so changing a default does
# not silently turn this test into a no-op.
$cfg       = Get-VdxConfig
$moveKey   = Get-ChordKey $cfg.MoveWindowHotkey
$switchKey = Get-ChordKey $cfg.SwitchDesktopHotkey

if (-not $ReturnTo) { $ReturnTo = Get-VdxCurrentDesktopName $root }
if (-not $MoveTo)   { $MoveTo   = Get-VdxOtherDesktopName $ReturnTo }
Write-Host "move to / back to: '$MoveTo' / '$ReturnTo'"

Write-Host "$($cfg.MoveWindowHotkey)  move palette, then Escape"
Send-WinCtrl $moveKey
Start-Sleep -Milliseconds 1400
Send-Key $ESC
Start-Sleep -Milliseconds 700

Write-Host "$($cfg.SwitchDesktopHotkey)  switch palette, then Escape"
Send-WinCtrl $switchKey
Start-Sleep -Milliseconds 1400
Send-Key $ESC
Start-Sleep -Milliseconds 700

if ($cfg.SendToLastCreatedHotkey) {
    $lastKey = Get-ChordKey $cfg.SendToLastCreatedHotkey
    Write-Host "$($cfg.SendToLastCreatedHotkey)  send to last created"
    Send-WinCtrl $lastKey
    Start-Sleep -Milliseconds 1200
} else {
    Write-Host "send-to-last-created is unbound, skipping"
}

if ($Commit) {
    Write-Host ""
    Write-Host "-- commit phase --"
    Write-Host "moving the scratch window to a desktop matching '$MoveTo'"

    # Bring charmap back to the front; the balloon above may have taken focus.
    if (-not $scratch.HasExited) {
        Add-Type -Name Fg -Namespace Smoke2 -MemberDefinition @'
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(System.IntPtr hWnd);
'@
        [Smoke2.Fg]::SetForegroundWindow($scratch.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 600
    }

    Send-WinCtrl $moveKey
    Start-Sleep -Milliseconds 1200
    Send-Text $MoveTo
    Start-Sleep -Milliseconds 600
    Send-AltKey $RET
    Start-Sleep -Seconds 2

    Write-Host "switching back to a desktop matching '$ReturnTo'"
    Send-WinCtrl $switchKey
    Start-Sleep -Milliseconds 1200
    Send-Text $ReturnTo
    Start-Sleep -Milliseconds 600
    Send-Key $RET
    Start-Sleep -Seconds 2
}

if ($TestCreate) {
    Write-Host ""
    Write-Host "-- create phase --"

    $before = Get-VdxDesktopCount
    Write-Host "desktops before: $before"

    if (-not $scratch.HasExited) {
        Add-Type -Name Fg2 -Namespace Smoke3 -MemberDefinition @'
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(System.IntPtr hWnd);
'@
        [Smoke3.Fg2]::SetForegroundWindow($scratch.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 600
    }

    $name = 'Vdx Smoke Test'
    Write-Host "creating desktop '$name' and moving the scratch window onto it"

    Send-WinCtrl $moveKey
    Start-Sleep -Milliseconds 1200
    Send-Text $name
    Start-Sleep -Milliseconds 600
    Send-AltKey $RET
    Start-Sleep -Seconds 3

    $after = Get-VdxDesktopCount
    Write-Host "desktops after: $after"

    # Names come straight from the registry in display order, so printing the whole
    # order shows both that the name landed and that it was positioned correctly:
    # the new desktop should appear immediately after the one we started on.
    Write-Host "order now: $((Get-VdxDesktopNames) -join ' | ')"

    # Clean up: we followed the window onto the new desktop, so Win+Ctrl+F4 closes that
    # one. Close the scratch window first, otherwise it gets relocated instead of closed.
    if (-not $scratch.HasExited) { $scratch.Kill(); Start-Sleep -Milliseconds 600 }

    Write-Host "closing the test desktop (Win+Ctrl+F4)"
    Send-WinCtrl 0x73
    Start-Sleep -Seconds 2

    $final = Get-VdxDesktopCount
    Write-Host "desktops after cleanup: $final  (expected $before)"

    if ($final -ne $before) {
        Write-Warning "Desktop count did not return to $before. Check for a leftover '$name' desktop."
    }
}

if (-not $scratch.HasExited) { $scratch.Kill() }

Write-Host ""
Write-Host "=== log: $log ==="
if (Test-Path $log) { Get-Content $log } else { Write-Host "NO LOG FILE WAS WRITTEN" }

Write-Host ""
Write-Host "Vdx is still running as pid $($proc.Id). Stop it with:  Stop-Process -Id $($proc.Id)"
