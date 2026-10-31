<#
.SYNOPSIS
  Builds DynaIsland as a single self-contained DynaIsland.exe in .\publish (no .NET runtime needed).
.DESCRIPTION
  Single-file keeps the app's code inside the exe instead of a loose DynaIsland.dll, which is what
  lets it run on PCs with Smart App Control enforced (a loose, unsigned DLL gets blocked).
.PARAMETER Install
  Also copy the exe to %LOCALAPPDATA%\Programs\DynaIsland (replacing a running copy) and start it.
  The app registers itself to start with Windows from wherever it runs, so installing gives the
  auto-start entry a stable path that later builds don't wipe.
#>
param(
    [string]$Configuration = 'Release',
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Set-Location $PSScriptRoot

$output = Join-Path $PSScriptRoot 'publish'
if (Test-Path $output) { Remove-Item $output -Recurse -Force }

dotnet publish DynaIsland.csproj -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $output 'DynaIsland.exe'
Write-Host ("Built {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))

if ($Install) {
    $target = Join-Path $env:LOCALAPPDATA 'Programs\DynaIsland'
    New-Item -ItemType Directory -Force $target | Out-Null
    Get-Process DynaIsland -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Copy-Item $exe $target -Force
    $installed = Join-Path $target 'DynaIsland.exe'
    Start-Process explorer.exe $installed
    Write-Host "Installed and started $installed"
}
