<#
.SYNOPSIS
  Packages the portable DynaIsland download and (optionally) publishes it as a GitHub release.
.DESCRIPTION
  Everything is built and packaged on this PC; nothing is built in the cloud.
    1. Uses .\publish\DynaIsland.exe (run .\build.ps1 first; the exe is not rebuilt here, so the file
       you tested, and that Smart App Control allowed, is exactly the one that ships).
    2. Creates artifacts\DynaIsland-<version>-win-x64\ (exe, README, START HERE.txt), a zip and a .sha256.
    3. With -Publish: creates the GitHub release for tag v<version> and uploads the zip and checksum,
       authenticating with the GitHub login git already has saved (Git Credential Manager).
       The tag must already be pushed. The token is never printed or written to disk.
.EXAMPLE
  ./build.ps1; ./release.ps1            # package only
  ./release.ps1 -Publish                # package and publish v<version>
#>
param([switch]$Publish)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$project = Get-Content (Join-Path $PSScriptRoot 'DynaIsland.csproj')
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$tag = "v$version"
$name = "DynaIsland-$version-win-x64"
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$folder = Join-Path $artifacts $name
$zip = Join-Path $artifacts "$name.zip"
$exe = Join-Path $PSScriptRoot 'publish\DynaIsland.exe'

if (-not (Test-Path $exe)) { throw 'publish\DynaIsland.exe not found. Run .\build.ps1 first.' }
$exeVersion = (Get-Item $exe).VersionInfo.ProductVersion
if ($exeVersion -notlike "$version*") { throw "publish\DynaIsland.exe is version $exeVersion, project is $version. Rebuild first." }

# ---- Package ---------------------------------------------------------------------------------
foreach ($path in $folder, $zip, "$zip.sha256") { if (Test-Path $path) { Remove-Item $path -Recurse -Force } }
New-Item -ItemType Directory -Force $folder | Out-Null
Copy-Item $exe, (Join-Path $PSScriptRoot 'README.md') $folder
Set-Content -Path (Join-Path $folder 'START HERE.txt') -Encoding utf8 -Value @"
DynaIsland $version - portable

1. Move this folder somewhere permanent (for example Documents or %LOCALAPPDATA%\Programs),
   then double-click DynaIsland.exe. No installation and no .NET runtime are needed.
2. The island appears at the top of your screen, and an icon appears next to the clock (look
   under the ^ arrow on Windows 11). Right-click the icon or the island for settings.
3. DynaIsland starts with Windows from the folder you ran it from. You can turn this off in the
   menu (Start with Windows) or in Task Manager > Startup apps.

DynaIsland is not code-signed. Windows may show "Windows protected your PC": choose More info >
Run anyway. On PCs with Smart App Control turned on, Windows decides per file whether an unsigned
app may run, so it may be blocked there.

Everything runs locally: no network access, no telemetry, no accounts.

To remove it: right-click the tray icon, turn off Start with Windows, choose Quit, then delete
this folder. Settings are kept in %APPDATA%\DynaIsland.
"@

Compress-Archive -Path $folder -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Encoding ascii -Value "$hash  $name.zip"
$exeHash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ("Packaged {0} ({1:N0} MB)`nSHA-256 zip: {2}`nSHA-256 exe: {3}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash, $exeHash)

if (-not $Publish) { return }

# ---- Publish ---------------------------------------------------------------------------------
$remote = git remote get-url origin
if ($remote -notmatch 'github\.com[:/](?<repo>[^/]+/[^/.]+)') { throw "origin is not a GitHub repository: $remote" }
$repo = $Matches.repo

$commit = (git rev-parse "$tag^{commit}" 2>$null)
if (-not $commit) { throw "Tag $tag does not exist locally. Create and push it first." }
if (-not (git ls-remote --tags origin "refs/tags/$tag")) { throw "Tag $tag is not on GitHub yet: git push origin $tag" }

$env:GIT_TERMINAL_PROMPT = '0'
$credential = "protocol=https`nhost=github.com`n`n" | git credential fill
$token = ($credential | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
if (-not $token) { throw 'No saved GitHub login found for git.' }

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'DynaIsland-release-script'
}

$highlightsFile = Join-Path $PSScriptRoot "docs\release-notes\$tag.md"
$notes = @(
    if (Test-Path $highlightsFile) { Get-Content $highlightsFile -Raw -Encoding utf8; '' }
    '**Download:** `' + "$name.zip" + '` below, right-click it > Extract All, then run `DynaIsland.exe`. Portable, no installation and no .NET runtime needed.'
    ''
    'Requires 64-bit Windows 10 (2004+) or Windows 11.'
    ''
    "Built and packaged locally from commit $commit (tag ``$tag``)."
    ''
    'SHA-256 fingerprints:'
    "- ``$name.zip``: ``$hash``"
    "- ``DynaIsland.exe``: ``$exeHash``"
    ''
    'DynaIsland is not code-signed. Windows may show "Windows protected your PC" (More info > Run anyway). With Smart App Control turned on, Windows judges each unsigned file separately, so it may be blocked on some PCs.'
) -join "`n"

$body = @{ tag_name = $tag; name = "DynaIsland $tag"; body = $notes; draft = $false; prerelease = $false } | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$repo/releases" -Headers $headers `
    -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))

foreach ($asset in @(@{ Path = $zip; Type = 'application/zip' }, @{ Path = "$zip.sha256"; Type = 'text/plain' })) {
    $assetName = [IO.Path]::GetFileName($asset.Path)
    Write-Host "Uploading $assetName..."
    Invoke-RestMethod -Method Post -Headers $headers -ContentType $asset.Type -InFile $asset.Path `
        -Uri "https://uploads.github.com/repos/$repo/releases/$($release.id)/assets?name=$([uri]::EscapeDataString($assetName))" | Out-Null
}
$token = $null
Write-Host "Published: $($release.html_url)"
