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
Write-Host "Run it directly, or run scripts\install-autostart.ps1 as administrator to"
Write-Host "start it at logon with the elevation needed to move admin-owned windows."
