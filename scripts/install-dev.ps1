# Dev-install the Astral Warden Beacon plugin into the local NINA.
# Usage: powershell -File scripts\install-dev.ps1 [-Configuration Debug]
param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\AstralWarden.Nina.Beacon\AstralWarden.Nina.Beacon.csproj"

if (Get-Process NINA -ErrorAction SilentlyContinue) {
    Write-Warning "NINA is running - the installed plugin DLL is locked. Close NINA, then re-run this script."
    exit 1
}

dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$pluginsRoot = Join-Path $env:LOCALAPPDATA "NINA\Plugins"
$versionDir = Get-ChildItem $pluginsRoot -Directory | Sort-Object Name | Select-Object -Last 1
if (-not $versionDir) { Write-Error "No NINA plugin version folder found under $pluginsRoot"; exit 1 }

$dest = Join-Path $versionDir.FullName "Astral Warden Beacon"
New-Item -ItemType Directory -Force $dest | Out-Null

$outDir = Join-Path $root "src\AstralWarden.Nina.Beacon\bin\$Configuration"
Copy-Item (Join-Path $outDir "AstralWarden.Nina.Beacon.dll") $dest -Force
Copy-Item (Join-Path $outDir "AstralWarden.Nina.Beacon.pdb") $dest -Force -ErrorAction SilentlyContinue

Write-Host "Installed to $dest"
Write-Host "Start NINA and check Options > Plugins for 'Astral Warden Beacon'."
