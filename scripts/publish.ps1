# Builds a single self-contained Vdx.exe into dist\.
#
# Self-contained on purpose: the app autostarts at logon, and a framework-dependent
# build would break the moment a .NET runtime update removed the version it wanted.

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
$dist   = Join-Path $root 'dist'

if (-not (Test-Path $dotnet)) {
    # Fall back to whatever is on PATH, e.g. a machine-wide install.
    $dotnet = 'dotnet'
}

# A running instance holds dist\Vdx.exe open and the publish would fail partway with a
# file-in-use error. Once the logon task owns the process this happens on every rebuild,
# so stop it here rather than making it a thing to remember.
$running = Get-Process -Name 'Vdx' -ErrorAction SilentlyContinue

if ($running) {
    Write-Host "Stopping running Vdx (pid $($running.Id -join ', '))..."
    $running | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
    Start-Sleep -Milliseconds 500
}

$wasScheduled = [bool](Get-ScheduledTask -TaskName 'Vdx' -ErrorAction SilentlyContinue)

Write-Host "Publishing to $dist ..."

& $dotnet publish (Join-Path $root 'src\Vdx.App\Vdx.App.csproj') `
    -c Release `
    -o $dist `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $dist 'Vdx.exe'
if (-not (Test-Path $exe)) { throw "expected $exe to exist after publish" }

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Built $exe ($size MB)"

if ($wasScheduled) {
    # The task points at this exact path, so it just needs starting again.
    Write-Host "Restarting the Vdx logon task..."
    Start-ScheduledTask -TaskName 'Vdx'
    Start-Sleep -Seconds 2
    Write-Host "Task state: $((Get-ScheduledTask -TaskName 'Vdx').State)"
} else {
    Write-Host "Run it directly, or run scripts\install-autostart.ps1 as administrator to"
    Write-Host "start it at logon with the elevation needed to move admin-owned windows."
}
