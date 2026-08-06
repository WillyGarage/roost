# Regression test for two palette behaviours that were wrong:
#
#   1. Ctrl+Enter ("move and stay") moved the window but then FOLLOWED it to the
#      destination. Cause was the same activation-follows-window trap behind the old
#      switch bug: as the palette closed, Windows restored focus to the captured window,
#      which now lived on the destination desktop, and activating a window that lives
#      elsewhere drags the view along. Fix: after a stay-move, anchor the foreground on a
#      window still on the source desktop before closing.
#
#   2. After switching desktops from the palette, the desktop you just LEFT should sit at
#      the top of the list, so "hotkey, Enter" jumps straight back to it (Alt+Tab style).
#
# Like switch-bug.ps1, this drives the real published exe with synthetic keystrokes, so it
# briefly takes over the screen. It always restores the starting desktop and closes the
# scratch windows it opens.
#
# Requires a build first (the spike is used to read/switch the current desktop):
#   dotnet build ; scripts\publish.ps1 ; scripts\stay-and-return.ps1
#
#   .\stay-and-return.ps1                       # auto-pick home and a target desktop
#   .\stay-and-return.ps1 -Target "Code"       # move/switch to a specific desktop

param(
    # Both default to whatever this machine has: home is the desktop you are on now, and
    # the target is any other one. Hardcoding names would tie the test to one layout.
    [string]$Target,
    [string]$HomeDesktop
)

. (Join-Path $PSScriptRoot 'common.ps1')

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$exe    = Join-Path $root 'dist\Roost.exe'
$log    = Join-Path $env:LOCALAPPDATA "Roost\logs\roost-$(Get-Date -Format yyyyMMdd).log"
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$spike  = Join-Path $root 'spike\Roost.Spike'

if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
if (-not (Test-Path $exe))    { throw "$exe not found. Run scripts\publish.ps1 first." }

Add-Type -Name K -Namespace Stay -MemberDefinition @'
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(System.IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern System.IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool BringWindowToTop(System.IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint pid);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
'@

$WIN = 0x5B; $CTRL = 0x11; $RET = 0x0D; $UP = 2

function Send-Key([byte]$vk) {
    [Stay.K]::keybd_event($vk, 0, 0,   [UIntPtr]::Zero)
    [Stay.K]::keybd_event($vk, 0, $UP, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 45
}

# Ctrl held across a single key: this is the "move and stay" chord inside the palette.
function Send-CtrlKey([byte]$vk) {
    [Stay.K]::keybd_event($CTRL, 0, 0,   [UIntPtr]::Zero)
    [Stay.K]::keybd_event($vk,   0, 0,   [UIntPtr]::Zero)
    [Stay.K]::keybd_event($vk,   0, $UP, [UIntPtr]::Zero)
    [Stay.K]::keybd_event($CTRL, 0, $UP, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
}

function Send-WinCtrl([byte]$key) {
    [Stay.K]::keybd_event($WIN,  0, 0,   [UIntPtr]::Zero)
    [Stay.K]::keybd_event($CTRL, 0, 0,   [UIntPtr]::Zero)
    [Stay.K]::keybd_event($key,  0, 0,   [UIntPtr]::Zero)
    [Stay.K]::keybd_event($key,  0, $UP, [UIntPtr]::Zero)
    [Stay.K]::keybd_event($CTRL, 0, $UP, [UIntPtr]::Zero)
    [Stay.K]::keybd_event($WIN,  0, $UP, [UIntPtr]::Zero)
}

function Send-Text([string]$t) {
    foreach ($c in $t.ToCharArray()) {
        if ($c -eq ' ') { Send-Key 0x20 }
        elseif ($c -match '[A-Za-z0-9]') { Send-Key ([byte][char]([string]$c).ToUpper()) }
    }
}

function Set-Current([string]$name) {
    & $dotnet run --project $spike --no-build -- --switch $name | Out-Null
    Start-Sleep -Milliseconds 800
}

# Force a window to the foreground and confirm it took. Windows' foreground-steal guard
# refuses a bare SetForegroundWindow when another app (e.g. a busy Chrome) holds focus. The
# reliable, keystroke-free way around it is to attach our input queue to the current
# foreground thread for the duration of the call, so Windows treats us as the same input
# context. Deliberately injects NO keys: the palette is keyboard-driven and reads modifier
# state on Enter, so a stray Alt would corrupt the very action under test.
function Ensure-Foreground([IntPtr]$hwnd) {
    for ($i = 0; $i -lt 10; $i++) {
        if ([Stay.K]::GetForegroundWindow() -eq $hwnd) { return $true }

        [Stay.K]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
        $fg = [Stay.K]::GetForegroundWindow()

        $fgPid = [uint32]0
        $fgThread = [Stay.K]::GetWindowThreadProcessId($fg, [ref]$fgPid)
        $myThread = [Stay.K]::GetCurrentThreadId()

        $attached = $false
        if ($fgThread -ne 0 -and $fgThread -ne $myThread) {
            $attached = [Stay.K]::AttachThreadInput($myThread, $fgThread, $true)
        }

        [Stay.K]::BringWindowToTop($hwnd) | Out-Null
        [Stay.K]::SetForegroundWindow($hwnd) | Out-Null

        if ($attached) { [Stay.K]::AttachThreadInput($myThread, $fgThread, $false) | Out-Null }

        Start-Sleep -Milliseconds 200
        if ([Stay.K]::GetForegroundWindow() -eq $hwnd) { return $true }
    }
    return $false
}

function New-Scratch {
    $p = Start-Process (Join-Path $env:SystemRoot 'System32\charmap.exe') -PassThru
    Start-Sleep -Seconds 2
    $ok = Ensure-Foreground $p.MainWindowHandle
    return [pscustomobject]@{ Proc = $p; Hwnd = $p.MainWindowHandle; Focused = $ok }
}

$script:pass = 0
$script:fail = 0
function Check([string]$label, [bool]$ok) {
    if ($ok) { $script:pass++; Write-Host "PASS  $label" -ForegroundColor Green }
    else     { $script:fail++; Write-Host "FAIL  $label" -ForegroundColor Red }
}

# -----------------------------------------------------------------------------
# setup
# -----------------------------------------------------------------------------

Get-Process -Name 'Roost' -ErrorAction SilentlyContinue | ForEach-Object {
    $_.Kill(); $_.WaitForExit(3000) | Out-Null
}

if (Test-Path $log) { Remove-Item $log -Force }

Write-Host "starting Roost"
$roost = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4
if ($roost.HasExited) { throw "Roost exited immediately with code $($roost.ExitCode)" }

$cfg     = Get-RoostConfig
$moveKey = Get-ChordKey $cfg.MoveWindowHotkey
if (-not $moveKey) { throw "MoveWindowHotkey is unbound; nothing to drive the palette with." }

if (-not $HomeDesktop) { $HomeDesktop = Get-RoostCurrentDesktopName $root }
if (-not $Target)      { $Target      = Get-RoostOtherDesktopName $HomeDesktop }
Write-Host "move hotkey   : $($cfg.MoveWindowHotkey)"
Write-Host "home / target : '$HomeDesktop' / '$Target'"

# -----------------------------------------------------------------------------
# Test A: Ctrl+Enter moves the window but keeps us on the source desktop.
# -----------------------------------------------------------------------------

Write-Host ""
Write-Host "======== Test A: move-and-stay (Ctrl+Enter) ========"

Set-Current $HomeDesktop
$scratch = New-Scratch

$before = Get-RoostCurrentDesktopName $root
Write-Host "before        : $before"

# Re-assert focus immediately before the hotkey: the palette captures whatever is
# foreground when the chord fires, and if that is not our scratch we must NOT press the
# move chord, or we would relocate a real window. Abort the move instead.
if (-not (Ensure-Foreground $scratch.Hwnd)) {
    Check "scratch window held the foreground (skipped: could not focus it, refusing to move a real window)" $false
} else {
    Send-WinCtrl $moveKey
    Start-Sleep -Milliseconds 1300
    Send-Text $Target
    Start-Sleep -Milliseconds 500
    Send-CtrlKey $RET

    # Check immediately and again after a beat: the old follow was sometimes delayed by a
    # UWP window re-activating itself, exactly like the original switch bug.
    Start-Sleep -Milliseconds 700
    $immediately = Get-RoostCurrentDesktopName $root
    Start-Sleep -Seconds 2
    $settled = Get-RoostCurrentDesktopName $root
    Write-Host "just after    : $immediately"
    Write-Host "two seconds on: $settled"

    Check "stayed on '$HomeDesktop' (did not follow to '$Target')" ($settled -eq $HomeDesktop)

    $map = Get-RoostWindowMap $root
    $landed = @($map[$Target]) -contains 'Character Map'
    Check "the window actually moved to '$Target'" ($landed)
}

if (-not $scratch.Proc.HasExited) { $scratch.Proc.Kill(); Start-Sleep -Milliseconds 500 }
Set-Current $HomeDesktop

# -----------------------------------------------------------------------------
# Test B: after switching, Enter returns to the desktop we left.
# -----------------------------------------------------------------------------

Write-Host ""
Write-Host "======== Test B: switch, then Enter returns to previous ========"

Set-Current $HomeDesktop
$scratch2 = New-Scratch
Ensure-Foreground $scratch2.Hwnd | Out-Null

# Switch home -> target the normal way: open, type the name, Enter (go there).
Send-WinCtrl $moveKey
Start-Sleep -Milliseconds 1300
Send-Text $Target
Start-Sleep -Milliseconds 500
Send-Key $RET
Start-Sleep -Seconds 2

$mid = Get-RoostCurrentDesktopName $root
Write-Host "after switch  : $mid"
Check "switched to '$Target'" ($mid -eq $Target)

# Reopen and press Enter with an empty query. The desktop we just left should be the
# default row, so this jumps straight back to it.
Send-WinCtrl $moveKey
Start-Sleep -Milliseconds 1300
Send-Key $RET
Start-Sleep -Seconds 2

$back = Get-RoostCurrentDesktopName $root
Write-Host "after Enter   : $back"
Check "Enter returned to the previous desktop '$HomeDesktop'" ($back -eq $HomeDesktop)

if ($scratch2 -and -not $scratch2.Proc.HasExited) { $scratch2.Proc.Kill(); Start-Sleep -Milliseconds 500 }

# -----------------------------------------------------------------------------
# teardown
# -----------------------------------------------------------------------------

Write-Host ""
Set-Current $HomeDesktop
Write-Host "restored to   : $(Get-RoostCurrentDesktopName $root)"

Write-Host ""
Write-Host "=== recent log ==="
if (Test-Path $log) { Get-Content $log -Tail 18 } else { Write-Host "NO LOG FILE WAS WRITTEN" }

Write-Host ""
Write-Host "==== $script:pass passed, $script:fail failed ===="

Stop-Process -Id $roost.Id -ErrorAction SilentlyContinue

if ($script:fail -gt 0) { exit 1 }
