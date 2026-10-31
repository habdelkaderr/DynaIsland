# Publishes a framework-dependent single-file build to .\publish
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet publish DynaIsland.csproj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
Write-Host "Built: $PSScriptRoot\publish\DynaIsland.exe"
