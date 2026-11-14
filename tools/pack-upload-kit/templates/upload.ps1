#Requires -Version 5.1
<#
    游戏大厅增强 / Game Lobby Enhance —— Steam 创意工坊上传（公开）

    使用者只负责双击 upload.bat，所以本脚本的设计前提是：
      * 全程没有任何需要输入的选择题；
      * 任何一步失败都打印「原因 + 怎么办」，并写进同目录的 上传结果.txt；
      * 无论成功还是失败，窗口都会停住等回车，不会一闪而过。

    visibility 固定为 public：作者要求直接公开上传，不做「先私有再公开」的两段式。
    因此本脚本不会修改 workspace\workshop.json，校验哈希时也不会自己把自己弄脏。
#>

$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
$toolDir    = Join-Path $root 'ModUploader'
$exePath    = Join-Path $toolDir 'ModUploader.exe'
$workspace  = Join-Path $root 'workspace'
$configPath = Join-Path $workspace 'workshop.json'
$contentDir = Join-Path $workspace 'content'
$imagePath  = Join-Path $workspace 'image.png'
$logPath    = Join-Path $toolDir 'mod-uploader.log'
$prevLog    = Join-Path $toolDir 'mod-uploader.previous.log'
$resultPath = Join-Path $root '上传结果.txt'

$script:Outcome  = '失败（未完成）'
$script:Reason   = ''
$script:Advice   = @()
$script:ItemId   = ''
$script:Title    = ''
$script:Version  = ''
$script:Visible  = ''
$script:LogText  = ''
$script:PendingReason = ''
$script:PendingAdvice = @()

# ------------------------------------------------------------------ 输出小工具

function Write-Step {
    param([string]$Text)
    Write-Host ''
    Write-Host ('  [' + $Text + ']') -ForegroundColor Cyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host ('    OK  ' + $Text) -ForegroundColor Green
}

function Write-Note {
    param([string]$Text)
    Write-Host ('        ' + $Text) -ForegroundColor DarkGray
}

function Write-Warn2 {
    param([string]$Text)
    Write-Host ('    !!  ' + $Text) -ForegroundColor Yellow
}

# 不用 Get-FileHash：它在某些机器上会因为 PSModulePath 被改坏而「不是可识别的 cmdlet」。
# 直接走 .NET，任何 FullLanguage 的 PowerShell 都有。
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

# 抛出「已知原因 + 对应建议」的失败，由外层 catch 统一收口
function Stop-Step {
    param([string]$Reason, [string[]]$Advice)
    $script:PendingReason = $Reason
    $script:PendingAdvice = @($Advice)
    throw $Reason
}

function Get-LogTail {
    param([int]$Lines)
    if (-not (Test-Path $logPath)) { return '' }
    try {
        $all = [System.IO.File]::ReadAllLines($logPath, [System.Text.Encoding]::UTF8)
    } catch {
        return ''
    }
    if ($all.Count -eq 0) { return '' }
    $start = 0
    if ($all.Count -gt $Lines) { $start = $all.Count - $Lines }
    $sep = [Environment]::NewLine
    return ($all[$start..($all.Count - 1)] -join $sep)
}

# ------------------------------------------------------------------ 错误翻译

function Get-Diagnosis {
    param([string]$Text, [int]$ExitCode)

    $rules = @(
        @{
            Key     = 'licence'
            Pattern = 'Failed to create workshop item! Result: k_EResultAccessDenied'
            Reason  = '这个 Steam 账号没有《杀戮尖塔 2》(AppID 2868840) 的许可，Steam 不允许它创建创意工坊条目。'
            Advice  = @(
                '换一个真正拥有《杀戮尖塔 2》的 Steam 账号登录，然后再双击一次 upload.bat。'
                '家庭共享借来的游戏不算拥有，同样会被拒绝。'
            )
        }
        @{
            Key     = 'legal'
            Pattern = 'You will need to accept the workshop legal agreement'
            Reason  = '这个账号还没接受过 Steam 创意工坊法律协议，所以条目没法设成公开。'
            Advice  = @(
                '用浏览器打开 https://steamcommunity.com/sharedfiles/workshoplegalagreement'
                '登录 Steam，找到《Slay the Spire 2》，点「接受」，然后关掉网页。'
                '回到这个文件夹，再双击一次 upload.bat 即可。'
            )
        }
        @{
            Key     = 'steam-offline'
            Pattern = 'Steam initialization failed! Result: k_EResultNoConnection'
            Reason  = 'Steam 处于离线状态，连不上 Steam 网络。'
            Advice  = @(
                '在 Steam 里点「Steam - 上线」切回在线模式，然后重新双击 upload.bat。'
            )
        }
        @{
            Key     = 'steam-nologin'
            Pattern = 'Steam initialization failed! Result: k_EResultNotLoggedOn'
            Reason  = 'Steam 客户端没有登录任何账号。'
            Advice  = @(
                '在 Steam 客户端里登录（用拥有《杀戮尖塔 2》的账号），然后重新双击 upload.bat。'
            )
        }
        @{
            Key     = 'steam-init'
            Pattern = 'Steam initialization failed!'
            Reason  = 'Steam 初始化失败，上传器拿不到 Steam 接口。'
            Advice  = @(
                '确认 Steam 客户端是开着的、已经登录，并且不是离线模式，然后重新双击 upload.bat。'
                '如果还不行，把 Steam 完全退出再打开一次。'
            )
        }
        @{
            Key     = 'content'
            Pattern = 'Failed to upload content!|Error occurred while uploading to the workshop!'
            Reason  = '内容文件没有成功传到 Steam（网络中断，或者文件正被别的程序占用）。'
            Advice  = @(
                '确认网络正常，然后重新双击 upload.bat 重试。'
                '如果重复失败，把 上传结果.txt 发回给作者。'
            )
        }
        @{
            Key     = 'props'
            Pattern = 'Failed to set (title|description|tags|preview image)'
            Reason  = '条目的标题 / 简介 / 标签 / 预览图没有写进 Steam。'
            Advice  = @(
                '多半是 Steam 网络抖动，重新双击 upload.bat 再试一次。'
                '连续失败就把 上传结果.txt 发回给作者。'
            )
        }
        @{
            Key     = 'workspace'
            Pattern = 'There is no file named|There is no .content. directory'
            Reason  = '上传文件夹不完整（缺 workshop.json / image.png / content 目录）。'
            Advice  = @(
                '请重新解压整个压缩包，注意不要只复制其中几个文件。'
            )
        }
        @{
            Key     = 'visibility'
            Pattern = 'Invalid visibility'
            Reason  = 'workshop.json 里的 visibility 被改坏了，上传器不接受。'
            Advice  = @(
                '重新解压整个压缩包，覆盖现在的 workspace 目录。'
            )
        }
        @{
            Key     = 'json'
            Pattern = 'Exception thrown while parsing the workshop config|returned null'
            Reason  = 'workshop.json 格式损坏，解析不了。'
            Advice  = @(
                '重新解压整个压缩包，覆盖现在的 workspace 目录。'
            )
        }
        @{
            Key     = 'missing-item'
            Pattern = 'but it doesn.t exist! If you wish to upload a new item'
            Reason  = 'workspace\mod_id.txt 指向的工坊条目已经不存在了（可能被删掉了）。'
            Advice  = @(
                '把 workspace\mod_id.txt 删掉，再双击一次 upload.bat（会新建一个条目）。'
                '然后把 上传结果.txt 发回给作者说明一下。'
            )
        }
        @{
            Key     = 'bad-mod-id'
            Pattern = 'could not be parsed as a mod ID'
            Reason  = 'workspace\mod_id.txt 的内容不是有效的条目 ID。'
            Advice  = @(
                '把 workspace\mod_id.txt 删掉，再双击一次 upload.bat。'
                '然后把 上传结果.txt 发回给作者说明一下。'
            )
        }
        @{
            Key     = 'denied'
            Pattern = 'k_EResultAccessDenied'
            Reason  = 'Steam 拒绝了这次操作（权限不足）。'
            Advice  = @(
                '最常见的原因是账号没有《杀戮尖塔 2》的许可；其次是没接受工坊法律协议。'
                '两者都确认过还是失败，就把 上传结果.txt 发回给作者。'
            )
        }
    )

    foreach ($rule in $rules) {
        if ($Text -match $rule.Pattern) {
            return @{ Key = $rule.Key; Reason = $rule.Reason; Advice = $rule.Advice }
        }
    }

    $reason = ''
    if ($Text -match 'Failed to|Error occurred|Invalid ') {
        $reason = '上传器报错了，但没有匹配到已知的错误类型。'
    } elseif ($ExitCode -ne 0) {
        $reason = '上传器以非 0 的退出码结束（退出码 ' + $ExitCode + '），但没有留下可识别的错误信息。'
    } else {
        $reason = '上传器没有报错，但也没有打印成功标志，无法确认条目是否真的传上去了。'
    }
    return @{ Key = 'unknown'; Reason = $reason; Advice = @('把 上传结果.txt 发回给作者，作者会看日志判断。') }
}

# ------------------------------------------------------------------ 结果文件

function Write-ResultFile {
    $sep = [Environment]::NewLine
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('游戏大厅增强 / Game Lobby Enhance —— Steam 创意工坊上传结果')
    $lines.Add('=========================================================')
    $lines.Add('')
    $lines.Add('结果        : ' + $script:Outcome)
    $lines.Add('时间        : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
    $lines.Add('计算机      : ' + $env:COMPUTERNAME)
    $lines.Add('上传目录    : ' + $root)
    $lines.Add('模组版本    : ' + $script:Version)
    $lines.Add('条目标题    : ' + $script:Title)
    $lines.Add('可见性      : ' + $script:Visible)
    $lines.Add('条目 ID     : ' + $script:ItemId)
    if ($script:ItemId -ne '') {
        $lines.Add('条目链接    : https://steamcommunity.com/sharedfiles/filedetails/?id=' + $script:ItemId)
    } else {
        $lines.Add('条目链接    : （没有拿到条目 ID）')
    }
    if ($script:Reason -ne '') {
        $lines.Add('')
        $lines.Add('失败原因    : ' + $script:Reason)
    }
    if ($script:Advice.Count -gt 0) {
        $lines.Add('')
        $lines.Add('建议        :')
        foreach ($a in $script:Advice) { $lines.Add('  - ' + $a) }
    }
    $lines.Add('')
    $lines.Add('---------------------------------------------------------')
    $lines.Add('上传器日志（ModUploader/mod-uploader.log）')
    $lines.Add('---------------------------------------------------------')
    if ($script:LogText -ne '') {
        foreach ($l in ($script:LogText -split $sep)) { $lines.Add($l) }
    } else {
        $lines.Add('（这次没有跑到上传那一步，所以没有日志）')
    }

    $text = $lines -join $sep
    try {
        [System.IO.File]::WriteAllText($resultPath, $text, (New-Object System.Text.UTF8Encoding($true)))
    } catch {
        Write-Warn2 ('结果文件写不进：' + $resultPath)
        Write-Warn2 '请直接把这个窗口截图发回给作者。'
    }
}

# ------------------------------------------------------------------ 结束画面

function Show-Success {
    Write-Host ''
    Write-Host '  ========================================================' -ForegroundColor Green
    Write-Host '   上传成功，条目已经公开' -ForegroundColor Green
    Write-Host '  ========================================================' -ForegroundColor Green
    Write-Host ''
    if ($script:ItemId -ne '') {
        Write-Host ('   条目链接：https://steamcommunity.com/sharedfiles/filedetails/?id=' + $script:ItemId) -ForegroundColor White
    } else {
        Write-Host '   条目链接：没有拿到条目 ID，请在 Steam 个人主页的「创意工坊物品」里查看。' -ForegroundColor Yellow
    }
    Write-Host ('   可见性  ：' + $script:Visible) -ForegroundColor White
    Write-Host ('   版本    ：' + $script:Version) -ForegroundColor White
}

function Show-Failure {
    Write-Host ''
    Write-Host '  ========================================================' -ForegroundColor Red
    Write-Host '   上传没有成功' -ForegroundColor Red
    Write-Host '  ========================================================' -ForegroundColor Red
    Write-Host ''
    Write-Host ('   原因：' + $script:Reason) -ForegroundColor Yellow
    if ($script:Advice.Count -gt 0) {
        Write-Host ''
        Write-Host '   怎么办：' -ForegroundColor Yellow
        foreach ($a in $script:Advice) {
            Write-Host ('     - ' + $a) -ForegroundColor Yellow
        }
    }
    if ($script:LogText -ne '') {
        Write-Host ''
        Write-Host '   ---- 上传器日志 ----' -ForegroundColor DarkGray
        $sep = [Environment]::NewLine
        foreach ($l in ($script:LogText -split $sep)) {
            Write-Host ('   ' + $l) -ForegroundColor DarkGray
        }
    }
}

function Show-SendBack {
    Write-Host ''
    Write-Host '  --------------------------------------------------------' -ForegroundColor DarkGray
    Write-Host '   请把这个文件发回给作者（里面已经包含全部信息）：' -ForegroundColor White
    Write-Host ('     ' + $resultPath) -ForegroundColor White
    Write-Host ''
    Write-Host '   这个文件夹请完整保留，不要删除、不要移动、不要改名：' -ForegroundColor White
    Write-Host '   workspace\mod_id.txt 是以后更新同一个条目的唯一凭据。' -ForegroundColor Gray
    Write-Host '  --------------------------------------------------------' -ForegroundColor DarkGray
    Write-Host ''
    Read-Host ' 按回车关闭窗口'
}

# ------------------------------------------------------------------ 主流程

Write-Host ''
Write-Host '  ========================================================' -ForegroundColor DarkGray
Write-Host '   游戏大厅增强 / Game Lobby Enhance' -ForegroundColor White
Write-Host '   Steam 创意工坊上传（公开）' -ForegroundColor White
Write-Host '  ========================================================' -ForegroundColor DarkGray

try {
    # ---------------------------------------------------------- 1/3 自检
    Write-Step '1/3 检查上传包是否完整'

    if (-not (Test-Path $exePath))    { Stop-Step '找不到 ModUploader\ModUploader.exe，上传包不完整。' @('请重新解压整个压缩包，再双击 upload.bat。') }
    if (-not (Test-Path $workspace))  { Stop-Step '找不到 workspace 目录，上传包不完整。' @('请重新解压整个压缩包，再双击 upload.bat。') }
    if (-not (Test-Path $configPath)) { Stop-Step '找不到 workspace\workshop.json，上传包不完整。' @('请重新解压整个压缩包，再双击 upload.bat。') }
    if (-not (Test-Path $imagePath))  { Stop-Step '找不到 workspace\image.png，上传包不完整。' @('请重新解压整个压缩包，再双击 upload.bat。') }

    $required = @('sts2_lobby_enhance.dll', 'sts2_lobby_enhance.json', 'LICENSE', 'THIRD_PARTY_NOTICES')
    foreach ($name in $required) {
        if (-not (Test-Path (Join-Path $contentDir $name))) {
            Stop-Step ('workspace\content 里缺少文件：' + $name) @('请重新解压整个压缩包，不要只复制其中几个文件。')
        }
    }
    Write-Ok 'workspace\content 四个文件齐全'

    # 先把配置读出来，这样后面任何一步失败，结果文件里都能带上标题和版本
    $cfg = [System.IO.File]::ReadAllText($configPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $script:Title   = [string]$cfg.title
    $script:Visible = [string]$cfg.visibility
    $manifest = [System.IO.File]::ReadAllText((Join-Path $contentDir 'sts2_lobby_enhance.json'), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $script:Version = [string]$manifest.version

    # 可见性是包本身的性质，不是上传时才成立的条件：先查它，才能在还没碰 Steam 的时候就把
    # 「这个包配错了」说出来。排在 Steam 检查之后会让「Steam 没开」掩盖掉真正的配置问题。
    if ($script:Visible -ne 'public') {
        Stop-Step ('workshop.json 里的可见性是「' + $script:Visible + '」，不是 public。') @(
            '上传包被改动过，请重新解压整个压缩包。'
        )
    }

    # 混合版本检测。覆盖解压最容易出的错是「新脚本 + 旧内容」：资源管理器逐项问是否替换，
    # 误点一次「否」就会留下这种文件夹，然后它会安安静静地把上一版传上去。
    # README 里的版本号由打包脚本从模组清单填入，两者不一致就说明这个文件夹混了两个版本。
    $readmePath = Join-Path $root 'README-上传说明.md'
    if (Test-Path $readmePath) {
        $readmeText = [System.IO.File]::ReadAllText($readmePath, [System.Text.Encoding]::UTF8)
        $declared = [regex]::Match($readmeText, '版本：\*\*(\d+\.\d+\.\d+)\*\*')
        if ($declared.Success -and $declared.Groups[1].Value -ne $script:Version) {
            Stop-Step ('这个文件夹里混了两个版本：说明文件写的是 ' + $declared.Groups[1].Value + '，内容清单是 ' + $script:Version + '。') @(
                '把整个文件夹删掉，重新解压新的压缩包。',
                '不要在旧文件夹上覆盖解压：资源管理器会逐项询问是否替换，漏掉一次就会变成这种混合状态。'
            )
        }
    }

    $sumPath = Join-Path $root 'CHECKSUMS.txt'
    if (Test-Path $sumPath) {
        $bad = New-Object System.Collections.Generic.List[string]
        foreach ($line in [System.IO.File]::ReadAllLines($sumPath, [System.Text.Encoding]::UTF8)) {
            $trimmed = $line.Trim()
            if ($trimmed -eq '') { continue }
            $parts = $trimmed -split '\s+', 2
            if ($parts.Count -ne 2) { continue }
            $expected = $parts[0].ToUpperInvariant()
            $rel = $parts[1].Trim() -replace '/', '\'
            $full = Join-Path $root $rel
            if (-not (Test-Path $full)) { $bad.Add($rel + '（缺失）'); continue }
            $actual = Get-Sha256Hex $full
            if ($actual -ne $expected) { $bad.Add($rel + '（内容被改动）') }
        }
        if ($bad.Count -gt 0) {
            Stop-Step ('文件校验没通过：' + ($bad -join '；')) @('压缩包可能没解压完整，或者文件被改动过。请删掉这个文件夹，重新解压一次。')
        }
        Write-Ok '文件哈希全部一致'
    } else {
        Write-Note '没有 CHECKSUMS.txt，跳过哈希校验。'
    }

    # ---------------------------------------------------------- 2/3 Steam
    Write-Step '2/3 检查 Steam'

    # 逃生口只给仓库自带的验收脚本用（tools/pack-upload-kit/test-upload-kit.ps1 会用假上传器验成功分支，
    # 那条路径不需要 Steam）。真实使用者永远不会设置这个变量；即使设置了，下面也会打印一行明确提示，
    # 不会有任何一步在"看起来正常"的情况下继续。
    if ($env:LOBBY_ENHANCE_KIT_SKIP_STEAM -eq '1') {
        Write-Warn2 '注意：已跳过 Steam 进程检查（LOBBY_ENHANCE_KIT_SKIP_STEAM=1，仅供自动化验收使用）'
    } else {
        $steam = Get-Process -Name steam -ErrorAction SilentlyContinue
        if (-not $steam) {
            Stop-Step 'Steam 没有运行。' @('先打开 Steam 客户端并登录（用拥有《杀戮尖塔 2》的账号），然后再双击 upload.bat。')
        }
        Write-Ok 'Steam 正在运行'
    }

    # ---------------------------------------------------------- 3/3 上传
    Write-Step '3/3 上传到创意工坊'

    Write-Note ('标题    : ' + $script:Title)
    Write-Note ('版本    : ' + $script:Version)
    Write-Note ('可见性  : ' + $script:Visible)

    $existingId = ''
    foreach ($candidate in @((Join-Path $workspace 'mod_id.txt'), (Join-Path $toolDir 'mod_id.txt'))) {
        if (Test-Path $candidate) {
            $raw = ([System.IO.File]::ReadAllText($candidate, [System.Text.Encoding]::UTF8)).Trim()
            if ($raw -match '^\d+$') { $existingId = $raw; break }
        }
    }
    if ($existingId -ne '') {
        Write-Note ('这次会更新已有条目：' + $existingId)
        Write-Note ('https://steamcommunity.com/sharedfiles/filedetails/?id=' + $existingId)
    } else {
        Write-Note '这次会新建一个工坊条目。'
    }

    # 把上一次的日志挪走，保证日志里只有这一次的记录（诊断和成功判定都靠它）
    if (Test-Path $logPath) {
        try { Move-Item -LiteralPath $logPath -Destination $prevLog -Force } catch { }
    }

    Write-Host ''
    Write-Host '   正在上传，可能要一两分钟，请不要关掉这个窗口……' -ForegroundColor White
    Write-Host ''

    $exitCode = 0
    Push-Location $toolDir
    try {
        & $exePath upload -w $workspace
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    $script:LogText = Get-LogTail 80

    if ($exitCode -eq 0 -and $script:LogText -match 'Successfully uploaded') {
        foreach ($candidate in @((Join-Path $workspace 'mod_id.txt'), (Join-Path $toolDir 'mod_id.txt'))) {
            if (Test-Path $candidate) {
                $raw = ([System.IO.File]::ReadAllText($candidate, [System.Text.Encoding]::UTF8)).Trim()
                if ($raw -match '^\d+$') { $script:ItemId = $raw; break }
            }
        }
        if ($script:ItemId -eq '' -and $existingId -ne '') { $script:ItemId = $existingId }
        $script:Outcome = '成功（已公开）'
    } else {
        $diag = Get-Diagnosis -Text $script:LogText -ExitCode $exitCode
        $script:Reason = $diag.Reason
        $script:Advice = @($diag.Advice)

        # 条目只建了一半时，mod_id.txt 可能没写出来；此时盲目重试会多建一个条目
        if ($diag.Key -eq 'legal' -and -not (Test-Path (Join-Path $workspace 'mod_id.txt'))) {
            $script:Advice = @($script:Advice) + @('注意：这次没有生成 mod_id.txt，条目可能只建了一半。先别反复双击重试，把 上传结果.txt 发回给作者确认。')
        }
        $script:Outcome = '失败'
    }
} catch {
    if ($script:PendingReason -ne '') {
        $script:Reason = $script:PendingReason
        $script:Advice = @($script:PendingAdvice)
    } else {
        $script:Reason = $_.Exception.Message
        $script:Advice = @('把 上传结果.txt 发回给作者，作者会看日志判断。')
    }
    $script:Outcome = '失败（没走到上传）'
}

if ($script:Outcome -like '成功*') {
    Show-Success
} else {
    Show-Failure
}

Write-ResultFile
Show-SendBack
exit 0
