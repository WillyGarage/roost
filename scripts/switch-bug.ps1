# Regression test for "the switch palette picks a desktop but does not go there".
#
# Root cause: as the palette closed, Windows restored activation to whatever was focused
# when the hotkey fired. That window lives on the ORIGINAL desktop, and activating a
# window on another desktop makes Windows switch to it, so the close silently undid the
# switch. Move-and-follow survived by luck (the moved window was already on the
# destination, so the restoration pulled the right way), which is why only switching
# looked broken. Fix: close the palette first, then act at ApplicationIdle.
#
# The bug only showed with certain foreground windows, so this tests both a plain Win32
# window and a UWP one, which is what was focused when it was first reported.
#
#   .\switch-bug.ps1                       # both scratch types
#   .\switch-bug.ps1 -Scratch settings      # just the UWP case

param(
    [ValidateSet('charmap', 'settings', 'both')]
    [string]$Scratch = 'both',

    # Both default to whatever this machine has: home is the desktop you are on now, and
    # the target is any other one. Hardcoding names would tie the test to one layout.
    [string]$Target,
    [string]$HomeDesktop
)

. (Join-Path $PSScriptRoot 'common.ps1')

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$exe    = Join-Path $root 'dist\Roost.exe'
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$spike  = Join-Path $root 'spike\Roost.Spike'

function Get-Current { (& $dotnet run --project $spike --no-build -- --current 2>&1) -join ' ' }
function Set-Current([string]$name) { & $dotnet run --project $spike --no-build -- --switch $name | Out-Null }

Add-Type -Name W -Namespace Bug -MemberDefinition @'
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(System.IntPtr hWnd);

    public delegate bool EnumProc(System.IntPtr h, System.IntPtr p);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc cb, System.IntPtr p);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool IsWindowVisible(System.IntPtr h);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetClassNameW(System.IntPtr h, System.Text.StringBuilder s, int n);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetWindowTextW(System.IntPtr h, System.Text.StringBuilder s, int n);
'@

$WIN = 0x5B; $CTRL = 0x11; $RET = 0x0D; $UP = 2

function Send-Key([byte]$vk) {
    [Bug.W]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
    [Bug.W]::keybd_event($vk, 0, $UP, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
}

function Send-WinCtrl([byte]$key) {
    [Bug.W]::keybd_event($WIN,  0, 0,   [UIntPtr]::Zero)
    [Bug.W]::keybd_event($CTRL, 0, 0,   [UIntPtr]::Zero)
    [Bug.W]::keybd_event($key,  0, 0,   [UIntPtr]::Zero)
    [Bug.W]::keybd_event($key,  0, $UP, [UIntPtr]::Zero)
    [Bug.W]::keybd_event($CTRL, 0, $UP, [UIntPtr]::Zero)
    [Bug.W]::keybd_event($WIN,  0, $UP, [UIntPtr]::Zero)
}

function Send-Text([string]$t) {
    foreach ($c in $t.ToCharArray()) {
        if ($c -eq ' ') { Send-Key 0x20 }
        elseif ($c -match '[A-Za-z0-9]') { Send-Key ([byte][char]([string]$c).ToUpper()) }
    }
}

# Finds a visible top-level window by class and title fragment. Needed for UWP apps,
# whose window belongs to ApplicationFrameHost rather than the process we launched, so
# Process.MainWindowHandle is useless.
function Find-Window([string]$class, [string]$titlePart) {
    $found = [IntPtr]::Zero

    $cb = [Bug.W+EnumProc]{
        param($h, $p)
        if (-not [Bug.W]::IsWindowVisible($h)) { return $true }

        $cls = New-Object System.Text.StringBuilder 256
        [Bug.W]::GetClassNameW($h, $cls, 256) | Out-Null
        if ($cls.ToString() -ne $class) { return $true }

        $txt = New-Object System.Text.StringBuilder 512
        [Bug.W]::GetWindowTextW($h, $txt, 512) | Out-Null
        if ($txt.ToString() -notlike "*$titlePart*") { return $true }

        $script:found = $h
        return $false
    }

    [Bug.W]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    return $script:found
}

function Test-Switch([string]$kind, [byte]$chordKey) {
    Write-Host ""
    Write-Host "======== scratch: $kind ========"

    Set-Current $HomeDesktop
    Start-Sleep -Milliseconds 800

    $proc = $null
    $hwnd = [IntPtr]::Zero

    if ($kind -eq 'charmap') {
        $proc = Start-Process (Join-Path $env:SystemRoot 'System32\charmap.exe') -PassThru
        Start-Sleep -Seconds 2
        $hwnd = $proc.MainWindowHandle
    } else {
        # UWP: this is what was focused when the bug was first seen.
        Start-Process 'ms-settings:' | Out-Null
        Start-Sleep -Seconds 4
        $hwnd = Find-Window 'ApplicationFrameWindow' 'Settings'
    }

    if ($hwnd -eq [IntPtr]::Zero) {
        Write-Warning "could not find a $kind window, skipping"
        return
    }

    [Bug.W]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 800

    $before = Get-Current
    Write-Host "before         : $before"

    Send-WinCtrl $chordKey
    Start-Sleep -Milliseconds 1300
    Send-Text $Target
    Start-Sleep -Milliseconds 500
    Send-Key $RET

    Start-Sleep -Milliseconds 600
    $immediately = Get-Current
    Start-Sleep -Seconds 2
    $settled = Get-Current

    Write-Host "just after     : $immediately"
    Write-Host "two seconds on : $settled"

    $ok = ($settled -like "*$Target*")
    if ($ok) {
        Write-Host "PASS  switch stuck" -ForegroundColor Green
    } else {
        Write-Host "FAIL  switch did not stick (reverted to '$settled')" -ForegroundColor Red
    }

    if ($kind -eq 'charmap' -and $proc -and -not $proc.HasExited) { $proc.Kill() }
    if ($kind -eq 'settings') {
        Get-Process -Name 'SystemSettings' -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    }
}

Get-Process -Name 'Roost' -ErrorAction SilentlyContinue | ForEach-Object {
    $_.Kill(); $_.WaitForExit(3000) | Out-Null
}

Write-Host "starting Roost"
$roost = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4

$cfg   = Get-RoostConfig
$chord = $cfg.SwitchDesktopHotkey
$key   = Get-ChordKey $chord
Write-Host "switch chord   : $chord"

if (-not $HomeDesktop) { $HomeDesktop = Get-RoostCurrentDesktopName $root }
if (-not $Target)      { $Target      = Get-RoostOtherDesktopName $HomeDesktop }
Write-Host "home / target  : '$HomeDesktop' / '$Target'"

$kinds = if ($Scratch -eq 'both') { @('charmap', 'settings') } else { @($Scratch) }
foreach ($k in $kinds) { Test-Switch $k $key }

# Always leave the machine where we found it.
Write-Host ""
Set-Current $HomeDesktop
Start-Sleep -Milliseconds 800
Write-Host "restored to    : $(Get-Current)"

Write-Host ""
Write-Host "=== recent log ==="
Get-Content (Join-Path $env:LOCALAPPDATA "Roost\logs\roost-$(Get-Date -Format yyyyMMdd).log") -Tail 14

Stop-Process -Id $roost.Id -ErrorAction SilentlyContinue
