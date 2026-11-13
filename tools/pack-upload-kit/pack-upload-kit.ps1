#Requires -Version 5.1
<#
.SYNOPSIS
    打「创意工坊一键上传包」：把模板 + 官方上传器 + packaging 工作区拼成一个自解压即用的文件夹，再压成 zip。

.DESCRIPTION
    产物：<dist>\sts2-lobby-enhance-upload-kit.zip
          <dist>\upload-kit\sts2-lobby-enhance-upload-kit\   （暂存，便于人工检查）

    设计目标只有一个：收到包的人只负责双击 upload.bat。
    所以这里会强制检查：
      * upload.ps1 必须以 UTF-8 BOM 落盘（PowerShell 5.1 按 ANSI 读无 BOM 脚本会整篇乱码）；
      * 中文文件名必须能被 Windows 资源管理器正确解出（zip 走 .NET，带 UTF-8 标志位）；
      * CHECKSUMS.txt 必须在解压后的副本里逐条对得上（防止打包时漏文件或改坏文件）。

    上传器的可见性写死在 packaging\workshop.json 里（public），本脚本不修改它。
#>
[CmdletBinding()]
param(
    # 官方上传器 v0.2.0 的解压目录（含 ModUploader.exe / steam_api64.dll / steam_appid.txt）
    [string] $UploaderSource = 'C:\Users\Administrator\AppData\Local\Temp\sts2uploader\extracted',

    # 产物输出目录，默认 <workspace>\dist
    [string] $OutDir = '',

    # 覆盖已存在的 zip
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Section {
    param([string]$Text)
    Write-Host ''
    Write-Host ('=== ' + $Text + ' ===') -ForegroundColor Cyan
}

function Write-Ok   { param([string]$Text) Write-Host ('  [OK]   ' + $Text) -ForegroundColor Green }
function Write-Info { param([string]$Text) Write-Host ('         ' + $Text) -ForegroundColor Gray }
function Write-Warn2{ param([string]$Text) Write-Host ('  [!!]   ' + $Text) -ForegroundColor Yellow }
function Fail {
    param([string]$Text)
    Write-Host ''
    Write-Host ('  [FAIL] ' + $Text) -ForegroundColor Red
    Write-Host ''
    exit 1
}

# 与 templates/upload.ps1 同一个实现：不依赖 Get-FileHash（PSModulePath 被改坏的机器上没有它）
function Get-Sha256Hex {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            $bytes = $sha.ComputeHash($stream)
        } finally {
            $stream.Dispose()
        }
    } finally {
        $sha.Dispose()
    }
    return ([System.BitConverter]::ToString($bytes) -replace '-', '')
}

$tplDir  = Join-Path $PSScriptRoot 'templates'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$packaging = Join-Path $repoRoot 'packaging'

if ($OutDir -eq '') {
    $workspaceRoot = Split-Path -Parent (Split-Path -Parent $repoRoot)
    $OutDir = Join-Path $workspaceRoot 'dist'
}

$stageRoot = Join-Path $OutDir 'upload-kit'
$stageName = 'sts2-lobby-enhance-upload-kit'
$stage     = Join-Path $stageRoot $stageName
$zipPath   = Join-Path $OutDir ($stageName + '.zip')

Write-Host ''
Write-Host '  打包：游戏大厅增强 创意工坊一键上传包' -ForegroundColor White

# ---------------------------------------------------------------- 1. 输入检查
Write-Section '1/6 检查输入'

$manifestPath = Join-Path $repoRoot 'src\Sts2LobbyEnhance\sts2_lobby_enhance.json'
if (-not (Test-Path $manifestPath)) { Fail ('找不到模组清单：' + $manifestPath) }
$manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$version = [string]$manifest.version
if ($version -notmatch '^\d+\.\d+\.\d+$') { Fail ('清单里的版本号不可用：' + $version) }
Write-Ok ('模组版本 ' + $version)

$contentDir = Join-Path $packaging 'content'
$contentFiles = @('sts2_lobby_enhance.dll', 'sts2_lobby_enhance.json', 'LICENSE', 'THIRD_PARTY_NOTICES')
foreach ($name in $contentFiles) {
    if (-not (Test-Path (Join-Path $contentDir $name))) {
        Fail ('packaging\content 缺少 ' + $name + '，先跑 packaging\build-content.ps1')
    }
}
$actualContent = @(Get-ChildItem -LiteralPath $contentDir -File | ForEach-Object { $_.Name } | Sort-Object)
$expectedContent = @($contentFiles | Sort-Object)
if (($actualContent -join '|') -ne ($expectedContent -join '|')) {
    Fail ('packaging\content 里的文件不是预期的 4 个：' + ($actualContent -join ', '))
}
Write-Ok 'packaging\content 正好 4 个文件'

$workshopJson = Join-Path $packaging 'workshop.json'
$coverPng     = Join-Path $packaging 'image.png'
if (-not (Test-Path $workshopJson)) { Fail 'packaging\workshop.json 不存在' }
if (-not (Test-Path $coverPng))     { Fail 'packaging\image.png 不存在' }

$ws = [System.IO.File]::ReadAllText($workshopJson, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
if ([string]$ws.visibility -ne 'public') {
    Fail ('workshop.json 的 visibility 是「' + $ws.visibility + '」，不是 public。上传包要求直接公开上传。')
}
Write-Ok 'workshop.json visibility = public'

$uploaderFiles = @('ModUploader.exe', 'steam_api64.dll', 'steam_appid.txt')
foreach ($name in $uploaderFiles) {
    if (-not (Test-Path (Join-Path $UploaderSource $name))) {
        Fail ('上传器目录缺少 ' + $name + '：' + $UploaderSource)
    }
}
$appId = ([System.IO.File]::ReadAllText((Join-Path $UploaderSource 'steam_appid.txt'), [System.Text.Encoding]::ASCII)).Trim()
if ($appId -ne '2868840') { Fail ('steam_appid.txt 的内容是「' + $appId + '」，必须是 2868840') }
Write-Ok '上传器三件套齐全，steam_appid.txt = 2868840'

foreach ($name in @('upload.bat', 'upload.ps1', 'README-上传说明.md')) {
    if (-not (Test-Path (Join-Path $tplDir $name))) { Fail ('模板缺失：' + $name) }
}
Write-Ok '模板三件齐全'

# ---------------------------------------------------------------- 2. 暂存
Write-Section '2/6 拼装暂存目录'

if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# upload.bat 是纯 ASCII，原样复制
Copy-Item -LiteralPath (Join-Path $tplDir 'upload.bat') -Destination (Join-Path $stage 'upload.bat')

# 含中文的两个文件必须带 BOM 落盘，否则 PowerShell 5.1 / 旧记事本会按 ANSI 解出乱码
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
foreach ($name in @('upload.ps1', 'README-上传说明.md')) {
    $text = [System.IO.File]::ReadAllText((Join-Path $tplDir $name), (New-Object System.Text.UTF8Encoding($false)))
    # 版本号只从清单来，模板里不留死数字（否则发版时会忘掉这一处）
    $text = $text.Replace('{VERSION}', $version)
    if ($text.Contains('{VERSION}')) { Fail ($name + ' 里还有没被替换的 {VERSION} 占位符') }
    [System.IO.File]::WriteAllText((Join-Path $stage $name), $text, $utf8Bom)
}
Write-Ok '模板已落盘（含中文的文件写成 UTF-8 带 BOM，版本号已填入）'

$toolDir = Join-Path $stage 'ModUploader'
New-Item -ItemType Directory -Path $toolDir -Force | Out-Null
foreach ($name in $uploaderFiles) {
    Copy-Item -LiteralPath (Join-Path $UploaderSource $name) -Destination (Join-Path $toolDir $name)
}
Write-Ok '上传器三件套已复制'

$wsDir = Join-Path $stage 'workspace'
$wsContent = Join-Path $wsDir 'content'
New-Item -ItemType Directory -Path $wsContent -Force | Out-Null
Copy-Item -LiteralPath $workshopJson -Destination (Join-Path $wsDir 'workshop.json')
Copy-Item -LiteralPath $coverPng     -Destination (Join-Path $wsDir 'image.png')
foreach ($name in $contentFiles) {
    Copy-Item -LiteralPath (Join-Path $contentDir $name) -Destination (Join-Path $wsContent $name)
}

# 条目 ID 必须跟着包走。没有它，上传器会新建第二个工坊条目，旧条目的订阅者迁不过去，
# 而且条目一旦创建就无法把订阅者转过去 —— 这是整个流程里唯一不可逆的错误。
$modIdPath = Join-Path $packaging 'mod_id.txt'
$hasModId = Test-Path $modIdPath
if ($hasModId) {
    $modId = ([System.IO.File]::ReadAllText($modIdPath, [System.Text.Encoding]::UTF8)).Trim()
    if ($modId -notmatch '^\d+$') { Fail ('packaging\mod_id.txt 的内容不是十进制条目 ID：' + $modId) }
    # 上传器只接受「数字 + 换行」，这里重写一遍，避免 BOM 或空格混进去
    # LF 而不是 CRLF：包里这份要与仓库里的 packaging\mod_id.txt 逐字节一致
    [System.IO.File]::WriteAllText((Join-Path $wsDir 'mod_id.txt'), ($modId + [string][char]10), (New-Object System.Text.UTF8Encoding($false)))
    Write-Ok ('workspace 带上了条目 ID ' + $modId + '（上传是更新，不会新建条目）')
} else {
    Write-Warn2 'packaging\mod_id.txt 不存在：这次上传会新建一个工坊条目'
}
Write-Ok 'workspace 已复制（workshop.json / image.png / content×4）'

# ---------------------------------------------------------------- 3. CHECKSUMS
Write-Section '3/6 生成 CHECKSUMS.txt'

$sep = [Environment]::NewLine
$lines = New-Object System.Collections.Generic.List[string]
$staged = Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName
foreach ($file in $staged) {
    $rel = $file.FullName.Substring($stage.Length + 1) -replace '\\', '/'
    $hash = Get-Sha256Hex $file.FullName
    $lines.Add($hash + '  ' + $rel)
}
[System.IO.File]::WriteAllText((Join-Path $stage 'CHECKSUMS.txt'), (($lines -join $sep) + $sep), (New-Object System.Text.UTF8Encoding($false)))
Write-Ok ('已记录 ' + $lines.Count + ' 个文件的 sha256')

# ---------------------------------------------------------------- 4. 目录自检
Write-Section '4/6 暂存目录自检'

$expected = @(
    'CHECKSUMS.txt',
    'README-上传说明.md',
    'upload.bat',
    'upload.ps1',
    'ModUploader\ModUploader.exe',
    'ModUploader\steam_api64.dll',
    'ModUploader\steam_appid.txt',
    'workspace\image.png',
    'workspace\workshop.json',
    'workspace\content\LICENSE',
    'workspace\content\THIRD_PARTY_NOTICES',
    'workspace\content\sts2_lobby_enhance.dll',
    'workspace\content\sts2_lobby_enhance.json'
)
if ($hasModId) { $expected += 'workspace\mod_id.txt' }
$actual = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { $_.FullName.Substring($stage.Length + 1) } | Sort-Object)
$expectedSorted = @($expected | Sort-Object)
if (($actual -join '|') -ne ($expectedSorted -join '|')) {
    Fail ('暂存目录的文件清单不符。实际：' + ($actual -join ', '))
}
Write-Ok ('目录结构正好 ' + $expected.Count + ' 个文件')

$ps1Bytes = [System.IO.File]::ReadAllBytes((Join-Path $stage 'upload.ps1'))
if (-not ($ps1Bytes.Length -gt 3 -and $ps1Bytes[0] -eq 0xEF -and $ps1Bytes[1] -eq 0xBB -and $ps1Bytes[2] -eq 0xBF)) {
    Fail 'upload.ps1 没有 UTF-8 BOM，PowerShell 5.1 会把它读成乱码'
}
Write-Ok 'upload.ps1 带 UTF-8 BOM'

$coverBytes = [System.IO.File]::ReadAllBytes((Join-Path $wsDir 'image.png'))
if ($coverBytes.Length -ge 1048576) { Fail ('image.png 超过 1 MiB：' + $coverBytes.Length + ' B') }
Write-Ok ('image.png ' + $coverBytes.Length + ' B（Steam 上限 1 MiB）')

# ---------------------------------------------------------------- 5. 打 zip
Write-Section '5/6 打包 zip'

if ((Test-Path $zipPath) -and (-not $Force)) {
    Fail ('已存在 ' + $zipPath + '，确认要覆盖请加 -Force')
}
if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true
)
Write-Ok ('已写出 ' + $zipPath)

# ---------------------------------------------------------------- 6. 解包复核
Write-Section '6/6 解包复核（模拟收到包的人）'

$verify = Join-Path ([System.IO.Path]::GetTempPath()) ('kit-verify-' + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $zipPath -DestinationPath $verify -Force

    $extracted = Join-Path $verify $stageName
    if (-not (Test-Path $extracted)) { Fail '解包后找不到顶层目录 ' + $stageName }

    $roundTrip = @(Get-ChildItem -LiteralPath $extracted -Recurse -File | ForEach-Object { $_.FullName.Substring($extracted.Length + 1) } | Sort-Object)
    if (($roundTrip -join '|') -ne ($expectedSorted -join '|')) {
        Fail ('解包后的文件清单不符。实际：' + ($roundTrip -join ', '))
    }
    Write-Ok ('解包后正好 ' + $expected.Count + ' 个文件，中文文件名正常')

    $bad = New-Object System.Collections.Generic.List[string]
    foreach ($line in [System.IO.File]::ReadAllLines((Join-Path $extracted 'CHECKSUMS.txt'), [System.Text.Encoding]::UTF8)) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '') { continue }
        $parts = $trimmed -split '\s+', 2
        if ($parts.Count -ne 2) { continue }
        $expectedHash = $parts[0].ToUpperInvariant()
        $rel = $parts[1].Trim() -replace '/', '\'
        $full = Join-Path $extracted $rel
        if (-not (Test-Path $full)) { $bad.Add($rel + '（缺失）'); continue }
        $actualHash = Get-Sha256Hex $full
        if ($actualHash -ne $expectedHash) { $bad.Add($rel + '（哈希不符）') }
    }
    if ($bad.Count -gt 0) { Fail ('解包后哈希校验失败：' + ($bad -join '；')) }
    Write-Ok '解包后逐文件 sha256 与 CHECKSUMS.txt 完全一致'

    $wsCheck = [System.IO.File]::ReadAllText((Join-Path $extracted 'workspace\workshop.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    if ([string]$wsCheck.visibility -ne 'public') { Fail ('解包后的 workshop.json visibility = ' + $wsCheck.visibility) }
    Write-Ok '解包后的 workshop.json 仍然是 public'
}
finally {
    if (Test-Path $verify) { Remove-Item -LiteralPath $verify -Recurse -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- 汇总
$zipItem = Get-Item -LiteralPath $zipPath
$zipHash = Get-Sha256Hex $zipPath

Write-Host ''
Write-Host '  打包完成' -ForegroundColor Green
Write-Host ('    zip     : ' + $zipItem.FullName)
Write-Host ('    大小    : ' + $zipItem.Length + ' B')
Write-Host ('    sha256  : ' + $zipHash)
Write-Host ('    暂存    : ' + $stage)
Write-Host ''
Write-Host '  收到包的人只需要：解压 → 双击 upload.bat → 把 上传结果.txt 发回来。' -ForegroundColor Gray
Write-Host ''
