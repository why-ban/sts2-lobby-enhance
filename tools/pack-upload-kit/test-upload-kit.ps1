#Requires -Version 5.1
<#
.SYNOPSIS
    对「创意工坊一键上传包」做端到端验收：解压 → 双击 upload.bat → 看窗口和 上传结果.txt。

.DESCRIPTION
    这个包里跑的是别人（帮忙代传的朋友）的电脑，出问题我们看不到现场，所以这里把
    每一条已知失败路径都真的跑一遍，断言脚本给出的「原因」和「怎么办」是人话、是对的。

    覆盖 6 个场景：
      1. 干净解压 + 上传器返回 AccessDenied（失败必须被翻译成人话，而不是「未知错误」）
      2. 解压不完整（缺 content\LICENSE → 必须点名缺哪个文件，且不去连 Steam）
      3. 文件被改坏（image.png 多一个字节 → 必须报哈希不符，且不去连 Steam）
      4. visibility 被改成 private（必须在上传前拦住）
      5. 假上传器模拟成功（必须打印条目链接、写对 上传结果.txt）
      6. 覆盖解压造成的「新脚本 + 旧内容」混合版本（必须拦住，不许上传）

    这个脚本**从不联系 Steam**：需要上传器输出时一律换成假上传器（-Fail 产出与真实 AccessDenied 同样的
    文本）。以前场景 1 会真跑上传器、并且先删掉 workspace\mod_id.txt 来复现「首次上传」，那等于在一个有许可的
    账号上真的新建一个工坊条目、再把条目 ID 随临时目录一起删掉；现在那条路径不存在了，场景 1 还额外断言
    workspace\mod_id.txt 没有被删、也没有被改写。

.PARAMETER Zip
    要验收的 zip，默认 dist\sts2-lobby-enhance-upload-kit.zip。

#>
[CmdletBinding()]
param(
    [string] $Zip = ''
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

# 造一个假的 ModUploader.exe。默认产出成功日志并写出 mod_id.txt；-Fail 产出与真实 AccessDenied 一样的文本。
# 两种情况都不连 Steam —— 验收脚本不允许有能力真的改动线上条目。
function New-MockUploader {
    param([switch]$Fail)
    $src = Join-Path $tmpBase ('mock-' + $(if ($Fail) { 'fail' } else { 'ok' }))
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
    $failLiteral = if ($Fail) { 'true' } else { 'false' }
    $program = @"
using System;
using System.IO;

internal static class Program
{
    private static int Main(string[] args)
    {
        bool fail = $failLiteral;
        string ws = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-w" || args[i] == "--workspace") { ws = args[i + 1]; }
        }
        if (ws == null) { Console.WriteLine("mock: no -w"); return 2; }

        if (fail)
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "mod-uploader.log"),
                "Initializing Steam\n" +
                "Logged in as user 'MockTester'.\n" +
                "Creating new workshop item...\n" +
                "Failed to create workshop item! Result: k_EResultAccessDenied\n");

            Console.WriteLine("Failed to create workshop item! Result: k_EResultAccessDenied");
            return 1;
        }

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
"@
    [System.IO.File]::WriteAllText((Join-Path $src 'ModUploader.csproj'), $csproj, (New-Object System.Text.UTF8Encoding($false)))
    [System.IO.File]::WriteAllText((Join-Path $src 'Program.cs'), $program, (New-Object System.Text.UTF8Encoding($false)))

    $out = Join-Path $src 'out'
    & dotnet publish (Join-Path $src 'ModUploader.csproj') -c Release -o $out -v:q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '假上传器编译失败，验收无法继续。' }
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
    #
    # 验的是「上传器报错之后，失败被翻译成人话」。上传器换成会返回 AccessDenied 的假上传器：
    # 真上传器在这条路径上会新建一个工坊条目，而脚本随后把临时目录整个删掉，条目 ID 也就丢了。
    Write-Host ''
    Write-Host '=== 1. 干净解压 + 上传器 AccessDenied（失败必须被翻译成人话） ===' -ForegroundColor Cyan
    $failOut = New-MockUploader -Fail
    $env:LOBBY_ENHANCE_KIT_SKIP_STEAM = '1'
    $k = New-Kit 'clean'
    Remove-Item -LiteralPath (Join-Path $k 'ModUploader') -Recurse -Force
    New-Item -ItemType Directory -Path (Join-Path $k 'ModUploader') -Force | Out-Null
    Copy-Item -Path (Join-Path $failOut '*') -Destination (Join-Path $k 'ModUploader') -Force
    # 条目 ID 必须原样保留：以前这里先删掉它来复现「首次上传」，那等于允许脚本真的新建条目
    Assert-True '跑之前条目 ID 还在' (Test-Path (Join-Path $k 'workspace\mod_id.txt'))
    Update-Checksums $k
        $run = Invoke-Kit $k
        $res = Get-ResultText $k
        Assert-True     '退出码 0（脚本自己停住，不会被 bat 二次 pause）' ($run.Code -eq 0)
        Assert-True     '生成了 上传结果.txt' (Test-Path (Join-Path $k '上传结果.txt'))
        Assert-Contains '结果文件判定为失败' $res '结果        : 失败'
        # 失败原因必须是「人话」，不能落到兜底文案。具体是哪一种取决于跑验收这台机器的 Steam 状态
        # （没登录 / 没许可 / 不是条目作者），所以这里钉的是「已经被翻译过」，不是某个特定句子。
        Assert-NotContains '失败原因不是兜底文案' $res '没有匹配到已知的错误类型'
        Assert-Contains    '结果文件给出了怎么办' $res '建议        :'
        Assert-Contains '结果文件带上了标题' $res '游戏大厅增强 / Game Lobby Enhance'
        # 版本号从包内清单读，不写死：写死的话每次发版都要改这一行，漏改就是假红。
        $manifestVersion = ([System.IO.File]::ReadAllText(
            (Join-Path $k 'workspace\content\sts2_lobby_enhance.json'),
            [System.Text.Encoding]::UTF8) | ConvertFrom-Json).version
        Assert-Contains '结果文件带上了版本' $res ('模组版本    : ' + $manifestVersion)
        Assert-Contains '结果文件带上了上传器日志' $res 'Initializing Steam'
        # 窗口里必须打印同一句原因：从结果文件里把原因抽出来，再要求窗口里也出现它
        $reasonLine = (($res -split "`n") | Where-Object { $_ -match '失败原因' } | Select-Object -First 1)
        $reasonText = ([string]$reasonLine -split ':', 2)[1].Trim()
        $probe = $reasonText.Substring(0, [Math]::Min(10, $reasonText.Length))
        Assert-Contains '窗口里也打印了原因' $run.Text $probe
    Assert-Contains '窗口里指明了要发回哪个文件' $run.Text '请把这个文件发回给作者'
    $keptId = ([System.IO.File]::ReadAllText((Join-Path $k 'workspace\mod_id.txt'), [System.Text.Encoding]::UTF8)).Trim()
    Assert-True '条目 ID 没有被删、也没有被改写' ($keptId -eq '3839101755')
    $env:LOBBY_ENHANCE_KIT_SKIP_STEAM = $null

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
    # 场景 5 换掉了真上传器，Steam 不参与；逃生口只作用在「Steam 进程存在」这一条前置检查上。
    $env:LOBBY_ENHANCE_KIT_SKIP_STEAM = '1'
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
    $env:LOBBY_ENHANCE_KIT_SKIP_STEAM = $null

    # ---------------------------------------------------------------- 6
    Write-Host ''
    Write-Host '=== 6. 覆盖解压留下的混合版本（新脚本 + 旧内容） ===' -ForegroundColor Cyan
    $k = New-Kit 'mixed'
    $mf = Join-Path $k 'workspace\content\sts2_lobby_enhance.json'
    $raw = [System.IO.File]::ReadAllText($mf, [System.Text.Encoding]::UTF8)
    $raw = $raw -replace '"version"\s*:\s*"[^"]*"', '"version": "9.9.9"'
    [System.IO.File]::WriteAllText($mf, $raw, (New-Object System.Text.UTF8Encoding($false)))
    $run = Invoke-Kit $k
    $res = Get-ResultText $k
    Assert-Contains    '认出文件夹混了两个版本' $res '混了两个版本'
    Assert-Contains    '给出「删掉重解」的指令' $res '把整个文件夹删掉'
    Assert-NotContains '没有去连 Steam' $run.Text 'Initializing Steam'

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
