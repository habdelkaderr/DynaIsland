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

# No EnableCompressionInSingleFile: compressed bundles are unpacked into private memory at start-up
# (~310 MB vs ~150 MB private bytes). Uncompressed, the libraries are mapped straight from the exe.
dotnet publish DynaIsland.csproj -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $output 'DynaIsland.exe'
Write-Host ("Built {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))

if ($Install) {
    # Smart App Control judges every new build separately. Check Windows will run this one before
    # touching the installed copy, so a blocked build never replaces a working one.
    try {
        $probe = Start-Process $exe -ArgumentList '--self-test' -PassThru -Wait
        if ($probe.ExitCode -ne 0) { throw "self-test exited with $($probe.ExitCode)" }
    }
    catch {
        Write-Warning "Windows blocked this build (Smart App Control?): $($_.Exception.Message)"
        Write-Warning 'The installed copy was left untouched and is still running.'
        exit 1
    }

    $target = Join-Path $env:LOCALAPPDATA 'Programs\DynaIsland'
    New-Item -ItemType Directory -Force $target | Out-Null
    # Wait for the old copy to exit fully, or the new one sees its single-instance lock and quits.
    Get-Process DynaIsland -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
    Copy-Item $exe $target -Force
    $installed = Join-Path $target 'DynaIsland.exe'
    Start-Process explorer.exe $installed
    Write-Host "Installed and started $installed"
}
