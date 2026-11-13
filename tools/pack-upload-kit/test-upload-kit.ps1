#Requires -Version 5.1
<#
.SYNOPSIS
    对「创意工坊一键上传包」做端到端验收：解压 → 双击 upload.bat → 看窗口和 上传结果.txt。

.DESCRIPTION
    这个包里跑的是别人（帮忙代传的朋友）的电脑，出问题我们看不到现场，所以这里把
    每一条已知失败路径都真的跑一遍，断言脚本给出的「原因」和「怎么办」是人话、是对的。

    覆盖 5 个场景：
      1. 干净解压 + 真实 Steam（本机账号无许可 → 必须翻译成「换账号」）
      2. 解压不完整（缺 content\LICENSE → 必须点名缺哪个文件，且不去连 Steam）
      3. 文件被改坏（image.png 多一个字节 → 必须报哈希不符，且不去连 Steam）
      4. visibility 被改成 private（必须在上传前拦住）
      5. 假上传器模拟成功（必须打印条目链接、写对 上传结果.txt）

    场景 1 会真的调用 Steam 接口（在无许可账号上只会得到 k_EResultAccessDenied，不会创建条目）。
    不想碰 Steam 就加 -SkipLive。

.PARAMETER Zip
    要验收的 zip，默认 dist\sts2-lobby-enhance-upload-kit.zip。

.PARAMETER SkipLive
    跳过场景 1（唯一会真的和 Steam 通信的一个）。
#>
[CmdletBinding()]
param(
    [string] $Zip = '',
    [switch] $SkipLive
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($Zip -eq '') {
    $workspaceRoot = Split-Path -Parent (Split-Path -Parent $repoRoot)
    $Zip = Join-Path (Join-Path $workspaceRoot 'dist') 'sts2-lobby-enhance-upload-kit.zip'
}
if (-not (Test-Path $Zip)) { throw ('找不到上传包：' + $Zip) }

$tmpBase = Join-Path ([System.IO.Path]::GetTempPath()) ('kittest-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $tmpBase -Force | Out-Null

$script:pass = 0
$script:fail = 0

function Get-Sha256Hex {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try { $bytes = $sha.ComputeHash($stream) } finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
    return ([System.BitConverter]::ToString($bytes) -replace '-', '')
}

function New-Kit {
    param([string]$Tag)
    $dir = Join-Path $tmpBase $Tag
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Expand-Archive -LiteralPath $Zip -DestinationPath $dir -Force
    return (Join-Path $dir 'sts2-lobby-enhance-upload-kit')
}

# 模拟「双击 upload.bat 然后按回车」：stdin 接 NUL，Read-Host 立刻返回空串
function Invoke-Kit {
    param([string]$Kit)
    $out = Join-Path $tmpBase ('out-' + [Guid]::NewGuid().ToString('N') + '.txt')
    cmd /c ('cd /d "' + $Kit + '" && upload.bat < nul > "' + $out + '" 2>&1')
    $code = $LASTEXITCODE
    $text = ''
    if (Test-Path $out) { $text = [System.IO.File]::ReadAllText($out, [System.Text.Encoding]::UTF8) }
    return @{ Code = $code; Text = $text }
}

function Get-ResultText {
    param([string]$Kit)
    $p = Join-Path $Kit '上传结果.txt'
    if (-not (Test-Path $p)) { return '' }
    return [System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8)
}

function Assert-Contains {
    param([string]$Name, [string]$Haystack, [string]$Needle)
    if ($Haystack.Contains($Needle)) {
        $script:pass++
        Write-Host ('  PASS  ' + $Name) -ForegroundColor Green
    } else {
        $script:fail++
        Write-Host ('  FAIL  ' + $Name + '   缺少「' + $Needle + '」') -ForegroundColor Red
    }
}

function Assert-NotContains {
    param([string]$Name, [string]$Haystack, [string]$Needle)
    if (-not $Haystack.Contains($Needle)) {
        $script:pass++
        Write-Host ('  PASS  ' + $Name) -ForegroundColor Green
    } else {
        $script:fail++
        Write-Host ('  FAIL  ' + $Name + '   不该出现「' + $Needle + '」') -ForegroundColor Red
    }
}

function Assert-True {
    param([string]$Name, [bool]$Cond)
    if ($Cond) { $script:pass++; Write-Host ('  PASS  ' + $Name) -ForegroundColor Green }
    else { $script:fail++; Write-Host ('  FAIL  ' + $Name) -ForegroundColor Red }
}

function Update-Checksums {
    param([string]$Kit)
    $sep = [Environment]::NewLine
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($f in (Get-ChildItem -LiteralPath $Kit -Recurse -File | Sort-Object FullName)) {
        $rel = $f.FullName.Substring($Kit.Length + 1) -replace '\\', '/'
        if ($rel -eq 'CHECKSUMS.txt') { continue }
        $lines.Add((Get-Sha256Hex $f.FullName) + '  ' + $rel)
    }
    [System.IO.File]::WriteAllText((Join-Path $Kit 'CHECKSUMS.txt'), (($lines -join $sep) + $sep), (New-Object System.Text.UTF8Encoding($false)))
}

# 造一个假的 ModUploader.exe：打印成功日志、写出 mod_id.txt。用来验收「成功分支」。
function New-MockUploader {
    $src = Join-Path $tmpBase 'mock'
    New-Item -ItemType Directory -Path $src -Force | Out-Null
    $csproj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>ModUploader</AssemblyName>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
'@
    $program = @'
using System;
using System.IO;

internal static class Program
{
    private static int Main(string[] args)
    {
        string ws = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-w" || args[i] == "--workspace") { ws = args[i + 1]; }
        }
        if (ws == null) { Console.WriteLine("mock: no -w"); return 2; }

        const string id = "3761234567";
        File.WriteAllText(Path.Combine(ws, "mod_id.txt"), id);

        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "mod-uploader.log"),
            "Initializing Steam\n" +
            "Logged in as user 'MockTester'.\n" +
            "Creating new workshop item...\n" +
            "Successfully uploaded 'sts2_lobby_enhance' to the workshop with id " + id + "\n");

        Console.WriteLine("Successfully uploaded to the workshop with id " + id);
        return 0;
    }
}
'@
    [System.IO.File]::WriteAllText((Join-Path $src 'ModUploader.csproj'), $csproj, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText((Join-Path $src 'Program.cs'), $program, (New-Object System.Text.UTF8Encoding($false)))

    $out = Join-Path $src 'out'
    & dotnet publish (Join-Path $src 'ModUploader.csproj') -c Release -o $out -v:q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '假上传器编译失败，场景 5 跳过不了，直接失败。' }
    return $out
}

try {
    Write-Host ''
    Write-Host ('验收：' + $Zip) -ForegroundColor White

    # ---------------------------------------------------------------- 0
    Write-Host ''
    Write-Host '=== 0. 包内凭据（条目 ID 必须跟着包走） ===' -ForegroundColor Cyan
    $k = New-Kit 'credential'
    $idPath = Join-Path $k 'workspace\mod_id.txt'
    Assert-True '包内带 workspace\mod_id.txt' (Test-Path $idPath)
    if (Test-Path $idPath) {
        $idBytes = [System.IO.File]::ReadAllBytes($idPath)
        $idText = ([System.IO.File]::ReadAllText($idPath, [System.Text.Encoding]::UTF8)).Trim()
        Assert-True     '条目 ID 是 3839101755' ($idText -eq '3839101755')
        Assert-True     'mod_id.txt 没有 BOM' (-not ($idBytes.Length -ge 3 -and $idBytes[0] -eq 0xEF -and $idBytes[1] -eq 0xBB))
        Assert-True     'mod_id.txt 只有数字和换行' ($idBytes.Length -eq 11)
    }

    # ---------------------------------------------------------------- 1
    if (-not $SkipLive) {
        Write-Host ''
        Write-Host '=== 1. 干净解压 + 真实 Steam（本机账号无许可） ===' -ForegroundColor Cyan
        $k = New-Kit 'clean'
        # 删掉条目 ID 才能复现「首次上传」路径：否则本机会去尝试更新真实条目 3839101755。
        # 这一步本来就会被 Steam 拒（没有许可），但没必要真的对线上条目发写请求。
        Remove-Item -LiteralPath (Join-Path $k 'workspace\mod_id.txt') -Force -ErrorAction SilentlyContinue
        # mod_id.txt 也在 CHECKSUMS.txt 里，删掉它必须重算哈希，否则会先被自检拦住
        Update-Checksums $k
        $run = Invoke-Kit $k
        $res = Get-ResultText $k
        Assert-True     '退出码 0（脚本自己停住，不会被 bat 二次 pause）' ($run.Code -eq 0)
        Assert-True     '生成了 上传结果.txt' (Test-Path (Join-Path $k '上传结果.txt'))
        Assert-Contains '结果文件判定为失败' $res '结果        : 失败'
        Assert-Contains '失败原因翻译成「换账号」' $res '没有《杀戮尖塔 2》'
        Assert-Contains '结果文件带上了标题' $res '游戏大厅增强 / Game Lobby Enhance'
        # 版本号从包内清单读，不写死：写死的话每次发版都要改这一行，漏改就是假红。
        $manifestVersion = ([System.IO.File]::ReadAllText(
            (Join-Path $k 'workspace\content\sts2_lobby_enhance.json'),
            [System.Text.Encoding]::UTF8) | ConvertFrom-Json).version
        Assert-Contains '结果文件带上了版本' $res ('模组版本    : ' + $manifestVersion)
        Assert-Contains '结果文件带上了上传器日志' $res 'k_EResultAccessDenied'
        Assert-Contains '窗口里也打印了原因' $run.Text '这个 Steam 账号没有《杀戮尖塔 2》'
        Assert-Contains '窗口里指明了要发回哪个文件' $run.Text '请把这个文件发回给作者'
    }

    # ---------------------------------------------------------------- 2
    Write-Host ''
    Write-Host '=== 2. 解压不完整（缺 content\LICENSE） ===' -ForegroundColor Cyan
    $k = New-Kit 'missing'
    Remove-Item -LiteralPath (Join-Path $k 'workspace\content\LICENSE') -Force
    $run = Invoke-Kit $k
    $res = Get-ResultText $k
    Assert-Contains    '点名缺了哪个文件' $res 'workspace\content 里缺少文件：LICENSE'
    Assert-Contains    '建议重新解压' $res '重新解压'
    Assert-NotContains '没有去连 Steam' $run.Text 'Initializing Steam'

    # ---------------------------------------------------------------- 3
    Write-Host ''
    Write-Host '=== 3. 文件被改坏（image.png 多一个字节） ===' -ForegroundColor Cyan
    $k = New-Kit 'corrupt'
    $img = Join-Path $k 'workspace\image.png'
    $bytes = [System.IO.File]::ReadAllBytes($img)
    $grown = New-Object byte[] ($bytes.Length + 1)
    [Array]::Copy($bytes, $grown, $bytes.Length)
    $grown[$bytes.Length] = 0x41
    [System.IO.File]::WriteAllBytes($img, $grown)
    $run = Invoke-Kit $k
    $res = Get-ResultText $k
    Assert-Contains    '报出哈希不符' $res '文件校验没通过'
    Assert-Contains    '点名是 image.png' $res 'workspace\image.png'
    Assert-NotContains '没有去连 Steam' $run.Text 'Initializing Steam'

    # ---------------------------------------------------------------- 4
    Write-Host ''
    Write-Host '=== 4. visibility 被改成 private（并重算哈希） ===' -ForegroundColor Cyan
    $k = New-Kit 'private'
    $cfgPath = Join-Path $k 'workspace\workshop.json'
    $raw = [System.IO.File]::ReadAllText($cfgPath, [System.Text.Encoding]::UTF8)
    $raw = $raw -replace '"visibility"\s*:\s*"public"', '"visibility": "private"'
    [System.IO.File]::WriteAllText($cfgPath, $raw, (New-Object System.Text.UTF8Encoding($false)))
    Update-Checksums $k
    $run = Invoke-Kit $k
    $res = Get-ResultText $k
    Assert-Contains    '拦住非 public' $res '不是 public'
    Assert-NotContains '没有去连 Steam' $run.Text 'Initializing Steam'

    # ---------------------------------------------------------------- 5
    Write-Host ''
    Write-Host '=== 5. 假上传器模拟成功（验收成功分支） ===' -ForegroundColor Cyan
    $mockOut = New-MockUploader
    $k = New-Kit 'success'
    Remove-Item -LiteralPath (Join-Path $k 'ModUploader') -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $k 'ModUploader') -Force | Out-Null
    Copy-Item -Path (Join-Path $mockOut '*') -Destination (Join-Path $k 'ModUploader') -Force
    Remove-Item -LiteralPath (Join-Path $k 'CHECKSUMS.txt') -Force
    $run = Invoke-Kit $k
    $res = Get-ResultText $k
    Assert-Contains    '判定成功' $res '结果        : 成功（已公开）'
    Assert-Contains    '抓到条目 ID' $res '条目 ID     : 3761234567'
    Assert-Contains    '拼出条目链接' $res 'https://steamcommunity.com/sharedfiles/filedetails/?id=3761234567'
    Assert-Contains    '窗口里打印了链接' $run.Text 'https://steamcommunity.com/sharedfiles/filedetails/?id=3761234567'
    Assert-Contains    '窗口里说了上传成功' $run.Text '上传成功，条目已经公开'
    Assert-True        '上传器把 mod_id.txt 写进了 workspace' (Test-Path (Join-Path $k 'workspace\mod_id.txt'))
    Assert-NotContains '没把成功误判成失败' $run.Text '上传没有成功'

    Write-Host ''
    if ($script:fail -eq 0) {
        Write-Host ('  全部通过：PASS ' + $script:pass + ' / FAIL 0') -ForegroundColor Green
    } else {
        Write-Host ('  PASS ' + $script:pass + ' / FAIL ' + $script:fail) -ForegroundColor Red
    }
    Write-Host ''
}
finally {
    Remove-Item -LiteralPath $tmpBase -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:fail -ne 0) { exit 1 }
exit 0
