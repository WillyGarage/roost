# End-to-end test of the desktop management verbs, driven through the real palette with
# synthetic keystrokes: create, rename, reorder, delete.
#
# Creates one scratch desktop and deletes it again, so the desktop list should be back to
# exactly what it was when this finishes. It asserts that, and asserts the window it moved
# was relocated rather than closed.
#
# Every step filters the palette by name before acting, so the operation always lands on
# the intended desktop rather than on whatever happened to be selected.

. (Join-Path $PSScriptRoot 'common.ps1')

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root 'dist\Vdx.exe'
$log  = Join-Path $env:LOCALAPPDATA "Vdx\logs\vdx-$(Get-Date -Format yyyyMMdd).log"

if (-not (Test-Path $exe)) { throw "$exe not found. Run scripts\publish.ps1 first." }

Add-Type -Name K -Namespace Mg -MemberDefinition @'
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(System.IntPtr hWnd);
'@

$WIN = 0x5B; $CTRL = 0x11; $ALT = 0x12; $RET = 0x0D; $ESC = 0x1B
$F2 = 0x71; $DEL = 0x2E; $DOWN = 0x28
$UP_FLAG = 2

function Key([byte]$vk) {
    [Mg.K]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
    [Mg.K]::keybd_event($vk, 0, $UP_FLAG, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 45
}

function Chord([byte]$key, [byte[]]$mods) {
    foreach ($m in $mods) { [Mg.K]::keybd_event($m, 0, 0, [UIntPtr]::Zero) }
    [Mg.K]::keybd_event($key, 0, 0, [UIntPtr]::Zero)
    [Mg.K]::keybd_event($key, 0, $UP_FLAG, [UIntPtr]::Zero)
    for ($i = $mods.Count - 1; $i -ge 0; $i--) {
        [Mg.K]::keybd_event($mods[$i], 0, $UP_FLAG, [UIntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 60
}

function Text([string]$t) {
    foreach ($c in $t.ToCharArray()) {
        if ($c -eq ' ') { Key 0x20 }
        elseif ($c -match '[A-Za-z0-9]') { Key ([byte][char]([string]$c).ToUpper()) }
    }
}

$pass = 0; $fail = 0
function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) {
        $script:pass++
        Write-Host "  PASS  $what" -ForegroundColor Green
    } else {
        $script:fail++
        Write-Host "  FAIL  $what  $detail" -ForegroundColor Red
    }
}

# ---------------------------------------------------------------------------

Get-Process -Name 'Vdx' -ErrorAction SilentlyContinue | ForEach-Object {
    $_.Kill(); $_.WaitForExit(3000) | Out-Null
}

Write-Host "starting Vdx"
$vdx = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4

$cfg     = Get-VdxConfig
$moveKey = Get-ChordKey $cfg.MoveWindowHotkey
Write-Host "palette hotkey: $($cfg.MoveWindowHotkey)"

$namesBefore = @(Get-VdxDesktopNames)
$countBefore = $namesBefore.Count
Write-Host "desktops before: $countBefore"

$created = 'Vdx Manage Test'
$renamed = 'Vdx Renamed Test'

Write-Host "opening a scratch Character Map window"
$scratch = Start-Process (Join-Path $env:SystemRoot 'System32\charmap.exe') -PassThru
Start-Sleep -Seconds 2
[Mg.K]::SetForegroundWindow($scratch.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 700

# ---- create ---------------------------------------------------------------
Write-Host ""
Write-Host "-- create --"

Chord $moveKey @($WIN, $CTRL)
Start-Sleep -Milliseconds 1300
Text $created
Start-Sleep -Milliseconds 500
Chord $RET @($ALT)          # Alt+Enter: create, move the scratch window, and follow
Start-Sleep -Seconds 3

$afterCreate = @(Get-VdxDesktopNames)
Check "desktop count went up by one" ($afterCreate.Count -eq $countBefore + 1) "got $($afterCreate.Count)"
Check "new desktop is named '$created'" ($afterCreate -contains $created)
Write-Host "  order: $($afterCreate -join ' | ')"

$createdAt = Get-VdxDesktopPosition $created

# ---- rename ---------------------------------------------------------------
Write-Host ""
Write-Host "-- rename (F2) --"

Chord $moveKey @($WIN, $CTRL)
Start-Sleep -Milliseconds 1300
Text $created            # filter down so F2 acts on the intended row
Start-Sleep -Milliseconds 600
Key $F2
Start-Sleep -Milliseconds 700
Text $renamed            # the field is pre-selected, so typing replaces
Start-Sleep -Milliseconds 400
Key $RET
Start-Sleep -Seconds 2
Key $ESC                 # palette stays open after a rename; dismiss it
Start-Sleep -Milliseconds 600

$afterRename = @(Get-VdxDesktopNames)
Check "renamed to '$renamed'" ($afterRename -contains $renamed)
Check "old name is gone" (-not ($afterRename -contains $created))
Check "count unchanged by rename" ($afterRename.Count -eq $countBefore + 1) "got $($afterRename.Count)"

# ---- reorder --------------------------------------------------------------
Write-Host ""
Write-Host "-- reorder (Ctrl+Down) --"

$before = Get-VdxDesktopPosition $renamed
Write-Host "  position before: $($before + 1)"

Chord $moveKey @($WIN, $CTRL)
Start-Sleep -Milliseconds 1300
Text $renamed
Start-Sleep -Milliseconds 600
Chord $DOWN @($CTRL)
Start-Sleep -Seconds 2
Key $ESC
Start-Sleep -Milliseconds 600

$after = Get-VdxDesktopPosition $renamed
Write-Host "  position after: $($after + 1)"
Check "moved one position later" ($after -eq $before + 1) "expected $($before + 2), got $($after + 1)"
Write-Host "  order: $((Get-VdxDesktopNames) -join ' | ')"

# ---- delete ---------------------------------------------------------------
# Alt+Delete now asks where the windows should go, so this also checks that the chosen
# destination is honoured rather than a neighbour being assumed.
Write-Host ""
Write-Host "-- delete (Alt+Delete, choose destination, confirm) --"

# Pick a destination that is deliberately NOT the neighbour, so a hardcoded fallback
# would fail this test.
$destination = @(Get-VdxDesktopNames) | Where-Object { $_ -ne $renamed } | Select-Object -Last 1
Write-Host "  sending its windows to '$destination'"

Chord $moveKey @($WIN, $CTRL)
Start-Sleep -Milliseconds 1300
Text $renamed
Start-Sleep -Milliseconds 600
Chord $DEL @($ALT)
Start-Sleep -Milliseconds 1200      # destination chooser
Text $destination                   # filter it down
Start-Sleep -Milliseconds 700
Key $RET
Start-Sleep -Seconds 3

$afterDelete = @(Get-VdxDesktopNames)
Check "desktop is gone" (-not ($afterDelete -contains $renamed))
Check "count back to $countBefore" ($afterDelete.Count -eq $countBefore) "got $($afterDelete.Count)"
Check "original desktops all still present" (
    ($namesBefore | Where-Object { $afterDelete -notcontains $_ }).Count -eq 0
)

# Deleting must relocate windows, never close them.
Check "scratch window survived the delete" (-not $scratch.HasExited)

$map = Get-VdxWindowMap $root
$landed = $map[$destination] | Where-Object { $_ -like '*Character Map*' }
Check "windows landed on the chosen destination '$destination'" ([bool]$landed) `
    "Character Map found on: $(($map.Keys | Where-Object { $map[$_] -like '*Character Map*' }) -join ', ')"

if (-not $scratch.HasExited) { $scratch.Kill() }

Write-Host ""
Write-Host "order now: $((Get-VdxDesktopNames) -join ' | ')"
Write-Host ""
Write-Host "=== log ==="
Get-Content $log -Tail 20

Write-Host ""
Write-Host "$pass passed, $fail failed" -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })

Stop-Process -Id $vdx.Id -ErrorAction SilentlyContinue
exit $fail
