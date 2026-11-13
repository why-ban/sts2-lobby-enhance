#Requires -Version 7.0
<#
.SYNOPSIS
    Rebuilds the patch and refreshes packaging/content/ so the workspace can be uploaded as-is.

.DESCRIPTION
    Steps:
      1. Build src/Sts2LobbyEnhance in the requested configuration (the game DLLs must be installed;
         the build only *reads* them).
      2. Replace the contents of packaging/content/ with exactly the four files the mod ships.
      3. Sanity-check the rest of the workspace (image.png size, workshop.json shape).

    Nothing under the game directory is written: the build uses -p:SkipModCopy=true, which disables the
    project's usual copy into the game's mods folder.

.PARAMETER Configuration
    Build configuration, Release by default.

.PARAMETER Sts2Root
    Slay the Spire 2 installation folder, i.e. the parent of data_sts2_windows_x86_64.

.PARAMETER SkipBuild
    Reuse the existing build output instead of rebuilding.

.EXAMPLE
    pwsh -File packaging/build-content.ps1

.EXAMPLE
    pwsh -File packaging/build-content.ps1 -Configuration Debug -SkipBuild
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Sts2Root = 'D:\SteamLibrary\steamapps\common\Slay the Spire 2',
    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspace = $PSScriptRoot
$companion = Split-Path -Parent $workspace
$projectDir = Join-Path $companion 'src\Sts2LobbyEnhance'
$projectFile = Join-Path $projectDir 'Sts2LobbyEnhance.csproj'
$contentDir = Join-Path $workspace 'content'
$docsDir = Join-Path $companion 'docs'

$modId = 'sts2_lobby_enhance'
$builtDll = Join-Path $projectDir "bin\$Configuration\net9.0\$modId.dll"
$sourceManifest = Join-Path $projectDir "$modId.json"

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host "== $Message" -ForegroundColor Cyan
}

function Assert-FileExists([string] $Path, [string] $What) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$What not found: $Path"
    }
}

# ---------------------------------------------------------------- prerequisites
Write-Step 'Checking inputs'

Assert-FileExists $projectFile 'Project file'

$gameDataDir = Join-Path $Sts2Root 'data_sts2_windows_x86_64'
Assert-FileExists (Join-Path $gameDataDir 'sts2.dll') "Game assembly (looked in $gameDataDir)"
Write-Host "  game data : $gameDataDir"

Assert-FileExists (Join-Path $workspace 'workshop.json') 'Workshop config'
Assert-FileExists (Join-Path $workspace 'image.png') 'Workshop preview image'
Assert-FileExists (Join-Path $docsDir 'LICENSE') 'docs/LICENSE'
Assert-FileExists (Join-Path $docsDir 'THIRD_PARTY_NOTICES') 'docs/THIRD_PARTY_NOTICES'

# ---------------------------------------------------------------------- build
if ($SkipBuild) {
    Write-Step "Reusing existing build (-Configuration $Configuration)"

    # -SkipBuild reuses bin/<Configuration>/net9.0/*.dll as-is. If a .cs file is newer than that DLL the
    # copy below silently ships the previous build, which is exactly how a "successful" run produced a
    # stale content/ before. This is a warning, not an error: another agent/terminal may be mid-edit and
    # a hard failure here would block unrelated work. tools/pack-release/pack-release.ps1 is the gate
    # that refuses to build a release zip out of a stale content/, so this warning is the early hint.
    $builtItem = Get-Item -LiteralPath $builtDll -ErrorAction SilentlyContinue
    $newestSource = Get-ChildItem -LiteralPath (Join-Path $projectDir 'Scripts') -Recurse -File -Filter *.cs |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -ne $builtItem -and $null -ne $newestSource -and $newestSource.LastWriteTime -gt $builtItem.LastWriteTime) {
        Write-Warning ("-SkipBuild: $($newestSource.Name) was last modified $($newestSource.LastWriteTime) but " +
            "$modId.dll was built $($builtItem.LastWriteTime). content/ may not contain the latest source " +
            'changes. Re-run without -SkipBuild if you changed src/.')
    }
} else {
    Write-Step "Building $Configuration"
    & dotnet build $projectFile -c $Configuration -p:SkipModCopy=true -p:Sts2Root=$Sts2Root --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
}

Assert-FileExists $builtDll 'Built patch assembly'
Assert-FileExists $sourceManifest 'Mod manifest'

# -------------------------------------------------------------------- content
Write-Step "Refreshing $contentDir"

if (-not (Test-Path -LiteralPath $contentDir -PathType Container)) {
    New-Item -ItemType Directory -Path $contentDir | Out-Null
}

# Start from an empty directory: a stale file left behind would be uploaded to the Workshop.
Get-ChildItem -LiteralPath $contentDir -Force | Remove-Item -Recurse -Force

$artifacts = @(
    @{ Source = $builtDll;                          Name = "$modId.dll" }
    @{ Source = $sourceManifest;                    Name = "$modId.json" }
    @{ Source = (Join-Path $docsDir 'LICENSE');     Name = 'LICENSE' }
    @{ Source = (Join-Path $docsDir 'THIRD_PARTY_NOTICES'); Name = 'THIRD_PARTY_NOTICES' }
)

foreach ($artifact in $artifacts) {
    Assert-FileExists $artifact.Source $artifact.Name
    Copy-Item -LiteralPath $artifact.Source -Destination (Join-Path $contentDir $artifact.Name) -Force
}

# ---------------------------------------------------------------- sanity checks
Write-Step 'Workspace contents'

$rows = Get-ChildItem -LiteralPath $contentDir -File |
    Sort-Object Name |
    ForEach-Object {
        [pscustomobject]@{
            File   = $_.Name
            Bytes  = $_.Length
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
$rows | Format-Table -AutoSize | Out-String | Write-Host

# Every copied file must be byte-identical to the file it came from. Printing the hash (as this script
# used to do) is not a check: nobody compares 16 hex digits by eye. A copy that "succeeded" but left an
# older DLL in content/ is the failure this project has already shipped once, so this is an assertion.
# tools/pack-release/pack-release.ps1 repeats the same comparison before it writes the release zip, so a
# stale content/ cannot reach dist/ even if this script is bypassed.
foreach ($artifact in $artifacts) {
    $target = Join-Path $contentDir $artifact.Name
    $sourceSha = (Get-FileHash -LiteralPath $artifact.Source -Algorithm SHA256).Hash
    $targetSha = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash

    if ($sourceSha -cne $targetSha) {
        throw "content/$($artifact.Name) is not byte-identical to its source. source=$($artifact.Source) sha256=$sourceSha, copy sha256=$targetSha"
    }
}

Write-Host '  all copies verified byte-identical to their sources'

$expected = @($artifacts | ForEach-Object { $_.Name } | Sort-Object)
$actual = @(Get-ChildItem -LiteralPath $contentDir -File | ForEach-Object { $_.Name } | Sort-Object)
if (Compare-Object -ReferenceObject $expected -DifferenceObject $actual) {
    throw "content/ does not match the expected file list. Expected: $($expected -join ', ')"
}

$image = Get-Item -LiteralPath (Join-Path $workspace 'image.png')
if ($image.Length -ge 1MB) {
    throw "image.png is $($image.Length) bytes; Steam rejects previews of 1 MiB or more."
}

$workshop = Get-Content -LiteralPath (Join-Path $workspace 'workshop.json') -Raw | ConvertFrom-Json
if (-not $workshop.title -or -not $workshop.description) {
    throw 'workshop.json is missing a title or description.'
}
if ($workshop.dependencies -notcontains 3749766330UL) {
    throw 'workshop.json must declare the official mod (3749766330) as a dependency.'
}

Write-Host ''
Write-Host "Workspace ready: $workspace" -ForegroundColor Green
Write-Host "  title      : $($workshop.title)"
Write-Host "  visibility : $($workshop.visibility)"
Write-Host "  content    : $($actual.Count) files"
Write-Host "  next       : ModUploader.exe upload -w `"$workspace`""
