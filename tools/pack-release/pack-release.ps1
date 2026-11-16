#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the release zip from packaging/content and proves the zip matches its sources.

.DESCRIPTION
    This is the ONLY supported way to produce dist/sts2-lobby-enhance-v<version>.zip.
    Hand-zipping is what produced the 2026-11-13 v1.5 incident: the zip carried a DLL from 22:36
    while packaging/content (and the manifest inside the same zip) were already from 23:21, i.e. an
    old binary shipped next to a new manifest, and nothing in the repository could notice.

    The script refuses to be talked out of any check. Steps, in order:

      1. The source directory is hard-coded to packaging/content. There is no parameter to point it
         somewhere else, because "point the packer at another staging folder" is exactly how a stale
         copy gets shipped. If packaging/content is not what you want to ship, rebuild it first with
         packaging/build-content.ps1.
      2. content/ must hold exactly the four files the mod ships - no extras (an extra file would be
         uploaded to the Workshop), no missing files.
      3. Every one of the four files must be byte-identical to the file it was generated from:
           content/sts2_lobby_enhance.dll  <- src/.../bin/Release/net9.0/....dll
           content/sts2_lobby_enhance.json <- src/.../sts2_lobby_enhance.json
           content/LICENSE                        <- LICENSE (repository root)
           content/THIRD_PARTY_NOTICES            <- docs/THIRD_PARTY_NOTICES
         The DLL comparison is the one that matters: it makes "content is the current build" true
         before the zip exists. It compares content against bin/, not source against binary, so a build
         that is older than its sources still passes here - run the build first, as the gate order says.
      4. The zip name comes from the manifest's "version" - never hard-coded, so a version bump
         cannot leave a misnamed package behind.
      5. The zip is written, then RE-OPENED and every entry is read back and hashed. Entry names,
         entry count, sizes and SHA-256 must all match the source files. A zip that "should" be
         right is not accepted; only a zip that has been read back is.
      6. Any other sts2-lobby-enhance-v*.zip already in dist/ is a hard failure: two candidate
         release packages in one folder is the same class of bug as a stale one. Delete the old zip
         deliberately, then run this again.
      7. Finally the zip's size and SHA-256 are printed, which is what goes into the release note.

    Exit codes:
        0  the zip was written and verified
        2  an input is wrong (missing/extra file, hash mismatch, stale sibling zip)
        3  the written zip did not read back identically

.PARAMETER DistDirectory
    Where to write the zip. Defaults to <repo>/dist, i.e. D:\zhuanban\sts2m\dist for this checkout
    (work/companion -> work -> sts2m). That is the folder docs/UPLOAD.md and docs/README.md refer to.

.PARAMETER Force
    Overwrite an existing zip with the same name (it is verified again afterwards either way).

.EXAMPLE
    pwsh -NoProfile -File tools/pack-release/pack-release.ps1
#>
[CmdletBinding()]
param(
    [string] $DistDirectory,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$companion = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$contentDir = Join-Path $companion 'packaging\content'
$docsDir = Join-Path $companion 'docs'
$projectDir = Join-Path $companion 'src\Sts2LobbyEnhance'
$builtDll = Join-Path $projectDir 'bin\Release\net9.0\sts2_lobby_enhance.dll'
$sourceManifest = Join-Path $projectDir 'sts2_lobby_enhance.json'

# This script compares content/ against bin/, which cannot see a build that is older than its sources. The gate
# order builds first, but a hand-run of this script must not silently ship the previous build either, so the
# comparison against the sources happens here.
$newestSource = Get-ChildItem -LiteralPath (Join-Path $projectDir 'Scripts') -Recurse -File -Filter *.cs |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (Test-Path -LiteralPath $builtDll -PathType Leaf) {
    $builtItem = Get-Item -LiteralPath $builtDll

}

if ([string]::IsNullOrWhiteSpace($DistDirectory)) {
    # work/companion -> work -> <repo>; the release folder lives at the repo root, not inside work/.
    $repoRoot = Split-Path -Parent (Split-Path -Parent $companion)
    $DistDirectory = Join-Path $repoRoot 'dist'
}

# The folder name inside the zip. The game loads mods from mods/<this name>/.
$zipRoot = 'sts2_lobby_enhance'

# Declared, not discovered: a file appearing in content/ that is not listed here is an error, and a
# listed file that is missing is an error. Keep in sync with packaging/build-content.ps1.
$sources = [ordered]@{
    'sts2_lobby_enhance.dll'  = $builtDll
    'sts2_lobby_enhance.json' = $sourceManifest
    'LICENSE'                        = (Join-Path $companion 'LICENSE')
    'THIRD_PARTY_NOTICES'            = (Join-Path $docsDir 'THIRD_PARTY_NOTICES')
}

function Fail([string] $Message, [int] $Code) {
    Write-Host ''
    Write-Host "FAILED: $Message" -ForegroundColor Red
    exit $Code
}

function Step([string] $Message) {
    Write-Host ''
    Write-Host "== $Message" -ForegroundColor Cyan
}

function Get-Sha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

# A build older than its sources is the one staleness content/ cannot reveal: this script compares content against
# bin/, and the gate order builds first, so anything reaching here with a newer .cs is a hand-run mistake.
if ($null -ne $newestSource -and (Test-Path -LiteralPath $builtDll -PathType Leaf)) {
    $builtItem = Get-Item -LiteralPath $builtDll
    if ($newestSource.LastWriteTime -gt $builtItem.LastWriteTime) {
        Fail ('the build is older than its sources: ' + $newestSource.Name + ' is newer than the DLL. Build first.') 2
    }
}

# ---------------------------------------------------------------- 1. fixed source
Step 'Packaging source'

Write-Host "  source   : $contentDir"
Write-Host '  (hard-coded: packaging/content is the only legal input)'

if (-not (Test-Path -LiteralPath $contentDir -PathType Container)) {
    Fail "packaging/content does not exist. Run packaging/build-content.ps1 first." 2
}

# ------------------------------------------------------- 2. exact file set in content
Step 'Content file set'

$expectedNames = @($sources.Keys | Sort-Object)
$actualNames = @(Get-ChildItem -LiteralPath $contentDir -Force |
    ForEach-Object { $_.Name } |
    Sort-Object)

$extra = @($actualNames | Where-Object { $expectedNames -notcontains $_ })
$missing = @($expectedNames | Where-Object { $actualNames -notcontains $_ })

if ($extra.Count -gt 0) {
    Fail ("packaging/content contains files that are not part of the release: " +
        ($extra -join ', ') + '. Everything in content/ is uploaded to the Workshop; remove them.') 2
}

if ($missing.Count -gt 0) {
    Fail ("packaging/content is missing: " + ($missing -join ', ') +
        '. Run packaging/build-content.ps1 to regenerate it.') 2
}

foreach ($name in $expectedNames) {
    $path = Join-Path $contentDir $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Fail "packaging/content/$name is not a regular file." 2
    }
}

Write-Host ("  {0} files, exactly as expected" -f $expectedNames.Count)

# ------------------------------------------ 3. content must be byte-identical to its sources
Step 'Content matches its sources (sha256)'

$hashes = @{}

foreach ($name in $expectedNames) {
    $contentPath = Join-Path $contentDir $name
    $sourcePath = $sources[$name]

    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        Fail ("the source of content/$name is missing: $sourcePath. " +
            'Build src/ and re-run packaging/build-content.ps1.') 2
    }

    $contentSha = Get-Sha256 $contentPath
    $sourceSha = Get-Sha256 $sourcePath
    $hashes[$name] = $contentSha

    $verdict = if ($contentSha -ceq $sourceSha) { 'OK' } else { 'STALE' }
    Write-Host ("  {0,-32} {1,10} B  {2}  {3}" -f $name, (Get-Item -LiteralPath $contentPath).Length, ($contentSha.Substring(0, 16) + '...'), $verdict)

    if ($contentSha -cne $sourceSha) {
        Fail ("packaging/content/$name is stale: sha256 $contentSha but $sourcePath is $sourceSha. " +
            'Run packaging/build-content.ps1 (without -SkipBuild) and try again.') 2
    }
}

# ------------------------------------------------------- 4. zip name comes from the manifest
Step 'Version'

$manifest = Get-Content -LiteralPath (Join-Path $contentDir 'sts2_lobby_enhance.json') -Raw | ConvertFrom-Json
$version = [string]$manifest.version

if ([string]::IsNullOrWhiteSpace($version)) {
    Fail 'packaging/content/sts2_lobby_enhance.json has no "version".' 2
}

$zipName = "sts2-lobby-enhance-v$version.zip"
Write-Host "  manifest version : $version"
Write-Host "  zip name         : $zipName"

if (-not (Test-Path -LiteralPath $DistDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $DistDirectory -Force | Out-Null
    Write-Host "  created          : $DistDirectory"
}

$zipPath = Join-Path $DistDirectory $zipName

# --------------------------------------- 5. no other candidate release package in the folder
Step 'Sibling release packages'

$siblings = @(Get-ChildItem -LiteralPath $DistDirectory -Filter 'sts2-lobby-enhance-v*.zip' -File |
    Where-Object { $_.FullName -ne (Join-Path $DistDirectory $zipName) })

if ($siblings.Count -gt 0) {
    Write-Host '  other release packages found:' -ForegroundColor Yellow
    foreach ($sibling in $siblings) {
        Write-Host ("    {0,-52} {1,10} B  {2}" -f $sibling.Name, $sibling.Length, $sibling.LastWriteTime) -ForegroundColor Yellow
    }

    Fail ("dist/ already holds " + $siblings.Count + ' other release package(s). Two candidate packages ' +
        'in one folder is how the wrong one gets uploaded. Delete them deliberately, then run this again.') 2
}

Write-Host '  none'

# ---------------------------------------------------------------- 6. write the zip
Step "Writing $zipPath"

if ((Test-Path -LiteralPath $zipPath) -and -not $Force) {
    Fail "$zipPath already exists. Pass -Force to overwrite it." 2
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

$archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $expectedNames) {
        $contentPath = Join-Path $contentDir $name
        $entryName = "$zipRoot/$name"
        $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)

        # Stamp the entry with the source mtime so two runs over identical inputs produce an
        # identical archive; that is what makes the printed sha256 comparable across runs.
        $entry.LastWriteTime = [DateTimeOffset](Get-Item -LiteralPath $contentPath).LastWriteTime

        $input = [System.IO.File]::OpenRead($contentPath)
        try {
            $output = $entry.Open()
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
            }
        }
        finally {
            $input.Dispose()
        }

        Write-Host ("  + {0}" -f $entryName)
    }
}
finally {
    $archive.Dispose()
}

# --------------------------------------- 7. read the zip back and hash every entry
Step 'Verifying the written zip (read back, not assumed)'

$readBack = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entryNames = @($readBack.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    $expectedEntryNames = @($expectedNames | ForEach-Object { "$zipRoot/$_" } | Sort-Object)

    $unexpected = @($entryNames | Where-Object { $expectedEntryNames -notcontains $_ })
    $absent = @($expectedEntryNames | Where-Object { $entryNames -notcontains $_ })

    if ($unexpected.Count -gt 0 -or $absent.Count -gt 0) {
        $readBack.Dispose()
        Fail ("the zip does not contain the expected entries. unexpected=[" + ($unexpected -join ', ') +
            '] missing=[' + ($absent -join ', ') + ']') 3
    }

    foreach ($name in $expectedNames) {
        $entryName = "$zipRoot/$name"
        $entry = $readBack.GetEntry($entryName)

        $stream = $entry.Open()
        try {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                $entryHash = [BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', ''
            }
            finally {
                $sha.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }

        $sourceLength = (Get-Item -LiteralPath (Join-Path $contentDir $name)).Length
        $sizeOk = $entry.Length -eq $sourceLength
        $hashOk = $entryHash -ceq $hashes[$name]

        $entryVerdict = if ($sizeOk -and $hashOk) { 'MATCH' } else { 'MISMATCH' }
        Write-Host ("  {0,-34} {1,10} B  {2}  {3}" -f $entryName, $entry.Length, ($entryHash.Substring(0, 16) + '...'), $entryVerdict)

        if (-not $sizeOk) {
            $readBack.Dispose()
            Fail "$entryName is $($entry.Length) B in the zip but $sourceLength B in content/." 3
        }

        if (-not $hashOk) {
            $readBack.Dispose()
            Fail "$entryName in the zip has sha256 $entryHash but content/ has $($hashes[$name])." 3
        }
    }
}
finally {
    $readBack.Dispose()
}

# ---------------------------------------------------------------- 8. report
$zipFile = Get-Item -LiteralPath $zipPath
$zipSha = Get-SHA256 $zipPath

Write-Host ''
Write-Host 'Release package verified' -ForegroundColor Green
Write-Host "  path    : $zipPath"
Write-Host "  entries : $($expectedNames.Count) (all re-read and hashed)"
Write-Host ("  size    : {0} bytes" -f $zipFile.Length)
Write-Host "  sha256  : $zipSha"
Write-Host ''
Write-Host '  upload  : ModUploader.exe upload -w "<companion>\packaging"' -ForegroundColor Gray
Write-Host '            (the uploader reads packaging/content, not the zip; the zip is the release artifact)'

exit 0
