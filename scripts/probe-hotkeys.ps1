# Reports which candidate hotkey chords are actually available on this machine.
#
# Guessing is unreliable: Windows itself reserves a lot of Win+Ctrl+<letter> chords
# (Win+Ctrl+M opens Magnifier settings, Win+Ctrl+D/F4/arrows are virtual desktops), and
# PowerToys and vendor utilities claim more. RegisterHotKey returning error 1409 is the
# only authoritative answer.
#
# Stop Vdx before running this, or the chords it already holds will report as taken.
#
# Pass -Chords to test a specific set, e.g.
#   .\probe-hotkeys.ps1 -Chords 'Win+Ctrl+H','Win+Ctrl+T'

param([string[]]$Chords)

$ErrorActionPreference = 'Stop'

Add-Type -Name Hk -Namespace Probe -MemberDefinition @'
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(System.IntPtr hWnd, int id, uint mod, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(System.IntPtr hWnd, int id);
'@

$MOD = @{ Alt = 1; Ctrl = 2; Shift = 4; Win = 8 }

# Chord definitions as "Modifier+Modifier+KEY". Only letters and a few named keys.
$candidates = @(
    'Win+Ctrl+M', 'Win+Ctrl+K', 'Win+Ctrl+L', 'Win+Ctrl+J', 'Win+Ctrl+U',
    'Win+Ctrl+Y', 'Win+Ctrl+I', 'Win+Ctrl+O', 'Win+Ctrl+Space', 'Win+Ctrl+Enter',
    'Win+Alt+M', 'Win+Alt+K', 'Win+Alt+L', 'Win+Alt+J', 'Win+Alt+Space',
    'Win+Shift+M', 'Win+Shift+K', 'Win+Shift+L',
    'Ctrl+Alt+M', 'Ctrl+Alt+K', 'Ctrl+Alt+L', 'Ctrl+Alt+J', 'Ctrl+Alt+Space',
    'Ctrl+Shift+Alt+M', 'Ctrl+Shift+Alt+K', 'Ctrl+Shift+Alt+L'
)

if ($Chords) {
    # Invoked with -File, PowerShell hands array arguments over as one comma-joined
    # string, so split defensively rather than silently probing a nonsense chord.
    $candidates = $Chords | ForEach-Object { $_ -split ',' } | Where-Object { $_.Trim() } |
        ForEach-Object { $_.Trim() }
}

function Get-Vk([string]$key) {
    switch ($key.ToLower()) {
        'space'  { return 0x20 }
        'enter'  { return 0x0D }
        'tab'    { return 0x09 }
        default  {
            if ($key.Length -eq 1) { return [byte][char]$key.ToUpper() }
            throw "unsupported key '$key'"
        }
    }
}

$id = 9000
$results = @()

foreach ($chord in $candidates) {
    $parts = $chord.Split('+')
    $key   = $parts[-1]
    $mods  = $parts[0..($parts.Length - 2)]

    $mask = 0
    foreach ($m in $mods) { $mask = $mask -bor $MOD[$m] }

    $id++
    $vk = Get-Vk $key

    # MOD_NOREPEAT (0x4000) matches how the app registers, so the answer is comparable.
    $ok = [Probe.Hk]::RegisterHotKey([IntPtr]::Zero, $id, ($mask -bor 0x4000), $vk)

    if ($ok) {
        [Probe.Hk]::UnregisterHotKey([IntPtr]::Zero, $id) | Out-Null
        $results += [pscustomobject]@{ Chord = $chord; Available = $true;  Note = '' }
    } else {
        $err  = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $note = if ($err -eq 1409) { 'already registered' } else { "error $err" }
        $results += [pscustomobject]@{ Chord = $chord; Available = $false; Note = $note }
    }
}

Write-Host ""
Write-Host "AVAILABLE:"
$results | Where-Object Available | ForEach-Object { Write-Host "  $($_.Chord)" }

Write-Host ""
Write-Host "TAKEN:"
$results | Where-Object { -not $_.Available } | ForEach-Object {
    Write-Host ("  {0,-20} {1}" -f $_.Chord, $_.Note)
}
