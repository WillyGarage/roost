# Shared helpers, dot-sourced by the other scripts:  . (Join-Path $PSScriptRoot 'common.ps1')

$script:VdxRegRoot = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops'

<#
Reads Vdx's config.

Vdx writes its config as JSON documented with // comments. Windows PowerShell 5.1's
ConvertFrom-Json rejects those outright ("Invalid JSON primitive"), and powershell.exe is
still 5.1 on this machine, so the comments have to come out before parsing.

Only whole-line comments are dropped, never anything after a value, so a desktop name that
happens to contain "//" survives.
#>
function Get-VdxConfig {
    $path = Join-Path $env:APPDATA 'Vdx\config.json'

    if (-not (Test-Path $path)) {
        throw "Vdx config not found at $path. Has the app run yet?"
    }

    $clean = Get-Content $path | Where-Object { $_ -notmatch '^\s*//' }
    return ($clean -join "`n") | ConvertFrom-Json
}

<#
Turns a chord like "Win+Ctrl+H" into the virtual-key code of its final key, which is what
keybd_event needs. Modifiers are sent separately by the caller.
#>
function Get-ChordKey([string]$chord) {
    if ([string]::IsNullOrWhiteSpace($chord)) { return $null }

    $key = $chord.Split('+')[-1].Trim()

    switch ($key.ToLower()) {
        'space' { return [byte]0x20 }
        'enter' { return [byte]0x0D }
        'tab'   { return [byte]0x09 }
        default {
            if ($key.Length -eq 1) { return [byte][char]$key.ToUpper() }
            throw "Get-ChordKey does not handle the key '$key'"
        }
    }
}

<#
Desktop GUIDs in display order, read the way the app reads them: from the ordered blob,
never by enumerating the Desktops subkeys (deleted desktops leave their name entries
behind, so enumerating those invents desktops that no longer exist).
#>
function Get-VdxDesktopGuids {
    $blob = (Get-ItemProperty $script:VdxRegRoot -Name VirtualDesktopIDs).VirtualDesktopIDs

    0..([int]($blob.Length / 16) - 1) | ForEach-Object {
        [guid]::new([byte[]]($blob[($_ * 16)..($_ * 16 + 15)]))
    }
}

<# Desktop names in display order, with a placeholder for any that are unnamed. #>
function Get-VdxDesktopNames {
    Get-VdxDesktopGuids | ForEach-Object {
        $n = (Get-ItemProperty (Join-Path $script:VdxRegRoot "Desktops\{$_}") `
                -Name Name -ErrorAction SilentlyContinue).Name
        if ($n) { $n } else { '(unnamed)' }
    }
}

function Get-VdxDesktopCount { (Get-VdxDesktopGuids | Measure-Object).Count }

<#
Desktop name -> list of window titles, via the spike's --list --windows. Used to assert
where windows actually ended up, which the registry cannot tell us.
#>
function Get-VdxWindowMap([string]$RepoRoot) {
    $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

    $out = & $dotnet run --project (Join-Path $RepoRoot 'spike\Vdx.Spike') --no-build -- `
        --list --windows 2>&1

    $map = @{}
    $current = $null

    foreach ($line in $out) {
        # "  3. Some Desktop                2 window(s)"
        if ($line -match '^\s*\d+\.\s+(.+?)\s\s+(?:\d+ window\(s\)|empty)\s*$') {
            $current = $Matches[1].Trim()
            $map[$current] = @()
        }
        # "       - Character Map"
        elseif ($current -and $line -match '^\s+-\s+(.+?)\s*$') {
            $map[$current] += $Matches[1]
        }
    }

    return $map
}

<#
Name of the desktop currently on screen, straight from the shell via the spike.

Test scripts use this instead of a hardcoded desktop name so they run on any machine and
leave it where they found it, whatever the local desktop layout happens to be.
#>
function Get-VdxCurrentDesktopName([string]$RepoRoot) {
    $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

    $out = (& $dotnet run --project (Join-Path $RepoRoot 'spike\Vdx.Spike') --no-build -- `
        --current 2>&1) -join "`n"

    # "  1. Some Desktop  {aacb37cc-...}"
    if ($out -match '^\s*\d+\.\s+(.+?)\s\s+\{') { return $Matches[1].Trim() }

    throw "could not determine the current desktop from: $out"
}

<#
Name of some desktop that is not $Exclude. Used by tests that need a second desktop to
move to without caring which one it is.
#>
function Get-VdxOtherDesktopName([string]$Exclude) {
    $other = @(Get-VdxDesktopNames) | Where-Object { $_ -ne $Exclude } | Select-Object -First 1

    if (-not $other) { throw "need at least two desktops; only found '$Exclude'" }

    return $other
}

<# Zero-based position of a desktop by exact name, or -1. #>
function Get-VdxDesktopPosition([string]$name) {
    $all = @(Get-VdxDesktopNames)

    for ($i = 0; $i -lt $all.Count; $i++) {
        if ($all[$i] -eq $name) { return $i }
    }

    return -1
}
