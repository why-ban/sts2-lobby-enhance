# 上传到 Steam 创意工坊

面向本项目的维护者。AppID：**2868840**（Slay the Spire 2）；
本模组工坊条目：[**3839101755**](https://steamcommunity.com/sharedfiles/filedetails/?id=3839101755)（2026-11-14 上传，公开）。

> **更新必须由创建它的那个 Steam 账号上传**（2026-11-14 用的是维护者朋友的账号，昵称从略）。
> Steam 工坊条目不能转让作者，换账号上传只会得到一个"没有权限"或一条新条目。

上传工作区是 [`../packaging/`](../packaging/)，它已经是一个合法的工作区，不需要用上传器重新生成。

---

## 1. 准备

### 1.1 上传器

官方上传器：[`MegaCrit/sts2-mod-uploader`](https://github.com/MegaCrit/sts2-mod-uploader) v0.2.0，
下载 `ModUploader-win-x64.zip`（约 6.0 MB）：

```
https://github.com/MegaCrit/sts2-mod-uploader/releases/download/v0.2.0/ModUploader-win-x64.zip
```

解压后的目录结构（实测 v0.2.0）：

```
ModUploader.exe        # 上传器本体
ModUploader.pdb
steam_api64.dll        # Steam 原生接口
steam_appid.txt        # 内容为 2868840
template/              # 新工作区模板，本步骤用不到
```

`steam_api64.dll` 与 `steam_appid.txt` **必须和 `ModUploader.exe` 放在同一目录**，并且要在该目录里
打开命令行再运行（官方说明也是这么写的）。`steam_appid.txt` 的内容就是 `2868840`，不要改。

### 1.2 环境

- Windows x64。
- **Steam 客户端正在运行且已登录**（上传走 Steam 客户端接口，未登录会初始化失败）。
- 该 Steam 账号对 AppID 2868840 有上传权限（普通玩家账号即可上传工坊物品）。

### 1.3 生成工作区内容

`packaging/content/` 是生成出来的，不要手工复制文件：

```powershell
pwsh -File packaging/build-content.ps1
```

它会重新编译 `src/`，然后把 4 个文件放进 `content/`：

```
content/sts2_lobby_enhance.dll
content/sts2_lobby_enhance.json
content/LICENSE
content/THIRD_PARTY_NOTICES
```

上传前先跑一遍回归测试，确认工作区没有被改坏：

```powershell
dotnet test tests
```

### 1.4 打包发布件（唯一合法入口）

`packaging/content/` 是**唯一合法的发布来源**。发布包只能由下面这一条命令生成：

```powershell
pwsh -NoProfile -File tools/pack-release/pack-release.ps1
```

脚本把输入写死为 `packaging/content`，**没有参数可以指向别的目录**，然后依次断言：

1. `content/` 恰好 4 个文件 —— 多一个会被上传到工坊，少一个会缺件；
2. 每个文件与它的来源逐字节相同：DLL 对比 `src/.../bin/Release/net9.0/`，清单对比
   `src/.../sts2_lobby_enhance.json`，`LICENSE` 对比仓库根目录，`THIRD_PARTY_NOTICES` 对比 `docs/`；
3. zip 名由清单里的 `version` 拼出（`dist/sts2-lobby-enhance-v<version>.zip`），不写死；
4. 写完 zip 后**重新打开 zip**，逐个条目读回来算 sha256 与源文件比对，条目名集合也必须完全一致；
5. `dist/` 里不允许出现第二个 `sts2-lobby-enhance-v*.zip` —— 两个候选包就是上传错包的前置条件。

任何一步不一致，脚本 exit 非 0，并且不会留下一个"看起来成功"的包。成功时它会打印 zip 的
**字节数与 sha256**，这两个数就是发布记录里要写的东西。

> **禁止用 `zip-stage/`、桌面新建文件夹或任何人工暂存目录打包。**
> 2026-11-13 的 v1.5 事故就是这么产生的：包里的 DLL 是 22:36 的旧构建（158,208 B），
> 而同一个包里的清单已经是最新的，仓库里没有任何东西能发现这件事。
> `zip-stage/` 已在本轮删除且不再重建；需要解包检查时用 `Expand-Archive` 解到临时目录，
> 不要在工作区里养一个常驻暂存目录。

---

## 2. 上传前检查

| 检查项 | 要求 | 谁在把关 |
|--------|------|----------|
| `packaging/workshop.json` 存在 | 必须 | 上传器 |
| `packaging/content/` 存在且非空 | 必须 | 上传器 |
| `packaging/image.png` 存在 | 必须 | 上传器 |
| `image.png` < 1 MiB | 必须（Steam 后端限制） | 本仓库测试 |
| `content/` 只有 4 个文件，且不含社区模组任何文件 | 必须 | 本仓库测试 |
| `content/*.dll` 与当前构建逐字节一致 | 必须 | 本仓库测试 |
| `dist/` 里的发布包与 `content/` 逐字节一致（且只有一个候选包） | 必须 | 本仓库测试（`dist/` 存在时）+ `tools/pack-release` |
| `dependencies` 含 `3749766330` | 必须 | 本仓库测试 |
| `workshop.json` 的 `visibility` | **必须是 `public`**（一次性公开上传，不做先私有再公开） | 本仓库测试 + `tools/pack-upload-kit` |
| 版本号 | `content/sts2_lobby_enhance.json` 与 `CHANGELOG.md` 一致 | 人工 |

`dotnet test tests` 通过即表示表格里"本仓库测试"那几行都成立。

---

## 3. 首次上传

> **真实上传不由本机执行。** 本机的 Steam 账号没有 AppID 2868840 的许可，创建工坊条目会被
> `k_EResultAccessDenied` 拒绝（见第 7 节最后一行）。实际流程是：打「一键上传包」交给拥有游戏的
> 账号，对方双击一次（第 9 节）。下面这段是上传器本身的行为说明，用来理解到底发生了什么。

```powershell
# 1) 进入上传器所在目录（steam_appid.txt 必须在这里）
cd <解压目录>

# 2) 上传，-w 指向本项目的工作区
.\ModUploader.exe upload -w "D:\zhuanban\sts2m\work\companion\packaging"
```

过程说明：

1. 上传器先校验 `image.png`、`content/`、`workshop.json` 是否存在，缺任何一个会直接退出。
2. 初始化 Steam，打印当前登录账号。
3. **因为没有 `mod_id.txt`，它会创建一个新的工坊物品**，然后把 `content/` 整个上传、设置预览图、
   写入标题/描述/可见性/标签/依赖。
4. 成功后会在工作区目录写一个 `mod_id.txt`（内容是这个新物品的数字 ID），并在 Steam 覆盖层里
   打开该物品页面。

> **上传器没有 dry-run。** 命令一旦通过校验就会真的创建/更新工坊条目，无法"只检查不上传"。
> 上传前的所有静态检查请交给 `dotnet test tests`（见第 2 节），不要用真实上传做试探。
> 另外：本机若已开着 Steam 并登录，运行上面的命令就会立即产生真实条目。

上传完成后检查 `packaging/mod_id.txt` 已经生成，并把它纳入版本管理（它是更新既有条目的唯一凭据）。

### 关于 mod_id.txt

`mod_id.txt` 是一个只含数字的文件，例如：

```
3761234567
```

它的作用是让下一次 `upload` 变成**更新**而不是**新建**：

- 文件存在 → 解析成物品 ID → 更新该条目。若该 ID 不存在（例如物品已被删除），上传器会报错并提示
  "If you wish to upload a new item, delete 'mod_id.txt' from your mod directory."。
- 文件不存在 → 新建条目，成功后再写回 `mod_id.txt`。
- 想强制指定某个条目，可用 `-i/--id`：`.\ModUploader.exe upload -w <工作区> -i 3761234567`。
  命令行指定的 ID 同样会被校验是否存在。

> **不要删掉 `mod_id.txt`。** 删掉后再上传会创建一个新的工坊条目，旧条目的订阅者拿不到更新。
> 条目一旦创建就无法把订阅者迁移过去。

---

## 4. 更新既有版本

### 4.1 版本号：两处手工改，其余全部自动跟随（踩过的坑）

发一次新版本，**只有下面 #1、#2 需要手工改**，其余位置都会自动跟随：

| # | 位置 | 怎么变 | 漏改的后果 |
|---|------|--------|-----------|
| 1 | `src/Sts2LobbyEnhance/sts2_lobby_enhance.json` 的 `"version"` | **手工** | 游戏与工坊内容里仍是旧版本号 |
| 2 | `tests/Sts2LobbyEnhance.Tests/ReleasePackageTests.cs` 中 `ContentShipsThePatchManifest` 的 `Assert.Equal("2.0.0", …)` | **手工**（唯一被机器强制的一处） | `dotnet test` 直接失败 |
| 3 | `packaging/content/sts2_lobby_enhance.json` | 自动：`build-content.ps1` 从 #1 复制 | — |
| 4 | `src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj` 的 `Version`/`FileVersion`/`AssemblyVersion`/`InformationalVersion` | 自动：csproj 在求值期从 #1 读取 `version` | 出货 DLL 的 FileVersion 与发布版本脱节（1.5.0 之前就是这样：清单 1.5.0、DLL 自报 1.0.0.0，崩溃报告会指向一个从未发布的版本） |
| 5 | 发布包文件名 `dist/sts2-lobby-enhance-v<version>.zip` | 自动：`tools/pack-release` 用 #1 的版本拼名 | — |
| 6 | `docs/CHANGELOG.md` 与 `packaging/workshop.json` 的 `changeNote` | **手工**（人写） | 用户看不到更新说明（测试不查这一项） |

> **#4 是 1.5.0 之后新增的：** 以前 csproj 里写死 `1.0.0`，于是每份出货 DLL 都自报
> `FileVersion 1.0.0.0`。游戏和工坊显示的是清单版本，所以用户看到的号是对的，但文件属性、
> 崩溃转储、进程内程序集列表都指向一个从未发布的版本。现在 csproj 从清单读取，
> **不要再往 csproj 里写版本号**（写了也会被清单覆盖）。
>
> 只改 #1、忘了 #2，是本项目最容易犯的错：清单是对的、包是对的，但 `dotnet test` 报
> `Assert.Equal() Failure: Strings differ`。看到这条错误先查版本号，不要去查代码。

#### `changeNote` 只写「相对上一次已发布版本」的增量

工坊上那条更新说明是**给已经装着旧版的人**看的，不是版本说明书。写之前必须先确认线上现在是哪个版本，
否则会把上一版的内容再讲一遍（1.8.1 就犯过：把 1.8.0 的全部内容重述了一遍，而 1.8.0 当时已经发布）。

确认线上版本（公开接口，不需要 key）：

```powershell
(Invoke-RestMethod -Uri 'https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/' `
  -Method Post -Body 'itemcount=1&publishedfileids[0]=3839101755' `
  -ContentType 'application/x-www-form-urlencoded').response.publishedfiledetails[0] |
  Select-Object title, time_updated, file_size, description
```

`description` 里带 `[h1]游戏大厅增强 vX.Y.Z[/h1]`，就是要对比的当前线上版本；`file_size` 可以核对
它是否已经换成你刚打的那个包。

> **这台机器上直连 `api.steampowered.com` 是不通的**（curl 21 秒超时），必须走代理，并且 curl 要加
> `--ssl-no-revoke`（本机证书吊销检查离线）：
>
> ```powershell
> curl.exe -sS --ssl-no-revoke -x http://127.0.0.1:7890 -X POST `
>   -H "Content-Type: application/x-www-form-urlencoded" `
>   -d "itemcount=1&publishedfileids[0]=3839101755" `
>   https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/
> ```
>
> 2026-11-15 实测基线：**线上是 2.0.0**，`time_updated = 2026-11-15T06:38:05Z`，`file_size = 160,841 B`。

**已发布的更新说明改不了**：Steam 把它绑在那一次提交上，没有「编辑历史更新说明」的接口。
要更正只能在**下一次**提交时写对；如果把同一份内容再提交一次，会多出一条更新记录。

### 4.2 发版步骤

1. 改代码 → 按 4.1 改 #1、#2 两处版本号 + 写 `docs/CHANGELOG.md` + 写 `packaging/workshop.json` 的
   `changeNote`（会显示在工坊的更新记录里）。
2. 重新生成内容并跑测试与冒烟：

   ```powershell
   pwsh -File packaging/build-content.ps1
   dotnet test tests
   dotnet run --project tools/smoke-test -c Release
   ```

   `dotnet test` 必须是 **0 skipped**。有跳过就说明游戏依赖型用例没跑（`GameDependentCoverageTests`
   会因为这一点直接失败），"全绿"必须在"全跑过"的前提下才成立。

   另外**手工**跑三个离线探针（各几秒，需要游戏目录；不放进 `dotnet test`，因为它们要加载真实二进制）：

   - `dotnet run --project tools/roster-probe -c Release` —— 期望 `OK: unpatched rejects …, patched accepts …`
   - `dotnet run --project tools/loadjoin-probe -c Release` —— 期望 `PASS: join order … rejected before, accepted after`
   - `dotnet run --project tools/savebinding-probe -c Release` —— 期望五种情形与发布记录一致

   它们验的是这几项修复的**行为**，而 `smoke-test` 只验接缝挂没挂上——这是两者互补的地方。
3. 打包发布件 —— **只准用这一条命令**（输入写死为 `packaging/content`，会自校验并打印 size/sha256）：

   ```powershell
   pwsh -NoProfile -File tools/pack-release/pack-release.ps1
   ```

   产物：`dist/sts2-lobby-enhance-v<version>.zip`，内含
   `sts2_lobby_enhance/{dll,json,LICENSE,THIRD_PARTY_NOTICES}` 四个文件。
4. 上传。**本机账号没有 AppID 2868840 的许可，所以不由本机上传** —— 走第 9 节的一键上传包，
   由拥有游戏的账号双击一次。`packaging/mod_id.txt` 已经存在（`3839101755`），打出来的包会
   带上它，所以那次上传是**更新**这条条目，不会新建第二条。

上传器只做增量设置：它不会删除 `content/` 里已不存在的远端文件（远端内容以本次上传为准，整体替换），
但**会**按 `workshop.json` 里的 `dependencies` 逐项增删必需物品——见下一节。

### 4.3 `mod_id.txt`（**更新前必读**）

**事实：`packaging/mod_id.txt` 已存在，内容 `3839101755`。**
11 字节 = 10 个数字 + 换行，无 BOM。它是 2026-11-14 首次上传成功后由上传器写出、随即纳入版本管理的。

**它的作用：** 上传器在**工作区**里找 `mod_id.txt` —— 找到就更新那条条目，找不到就**新建**一条。
工坊条目一旦创建，订阅者**无法**迁移到另一条，所以这个文件丢了就等于永久分裂成两个条目。

**一键上传包会带上它：** `tools/pack-upload-kit` 把 `packaging/mod_id.txt` 复制成
`workspace/mod_id.txt`，并断言内容是纯数字；打包日志会打印
`workspace 带上了条目 ID 3839101755（上传是更新，不会新建条目）`。
`tests` 里的 `WorkshopItemIdIsRecordedSoAnUpdateNeverCreatesASecondItem` 把这条钉死。

**依据强度（诚实标注）：** 本机现在**有**上传器副本（`%TEMP%\sts2uploader\extracted`，
v0.2.0）。"没有 `mod_id.txt` 就新建条目"这条行为已从**二进制里的命令行帮助原文**核对过，不是推测：

```
The ID of the workshop item to update. If this is not specified, we'll look for mod_id.txt in the
workspace. If it is also not present, a new item is created.
```

仍**未确证**的一点：条目创建成功、但后续某一步（例如设置可见性）失败时，`mod_id.txt` 会不会
已经被写出。上传器的用户可见输出里没有"新建成功"这一行，只有最后那句
`Successfully uploaded '<name>' to the workshop with id <id>`。所以一键上传包在"条目可能只建了一半"
时会提示先别反复重试（第 9 节）。

**格式要求：** 就是一个十进制数字加换行，不要有 BOM、空格、注释或第二行。

```
3839101755
```

拿不到文件但知道物品 ID 时，可以用 `-i/--id` 显式指定（见第 3 节）：
`.\ModUploader.exe upload -w <工作区> -i 3839101755`，但上传成功后仍要把它写回
`packaging/mod_id.txt` 并提交。

**如果哪天必须重新建条目**（例如条目被 Steam 删掉）：删掉 `packaging/mod_id.txt`、
改掉 `WorkshopItemIdIsRecordedSoAnUpdateNeverCreatesASecondItem` 里写死的 ID、重新走一遍上传，
并在第 9.4 节补一行记录。这是一次性的破坏性操作，**订阅者带不过去**。

---

## 5. workshop.json 各字段的实际行为

这些行为来自上传器 v0.2.0 二进制里的命令行帮助、运行日志与用户可见文案，不是推测
（本机副本：`%TEMP%\sts2uploader\extracted`）：

| 字段 | 行为 |
|------|------|
| `title` / `description` | 直接写入条目。`description` 支持 BBCode（`[h1]` `[list]` `[*]` `[b]`）。 |
| `visibility` | 接受 `private` / `public` / `unlisted` / `friends_only`（也接受 `friends`、`friendsonly`）。写错会在上传前直接报错退出。 |
| `changeNote` | 本次更新说明。 |
| `tags` | 原样提交。**注意**：写成 `[]` 会提交空标签列表，等于清空网页上手工加过的标签。本项目当前没有设置标签；如果以后在网页上加了标签，记得同步写回这里。 |
| `dependencies` | 工坊物品 ID 的数组（`ulong`）。**写了就按它做增删对账**：缺的加上，不在列表里的移除。**整个键省略（`null`）则完全不动**已有必需物品。本项目固定写 `[3749766330]`。 |
| `contentDescriptors` | 接受 `nudity` / `frequent_violence` / `adult_only` / `gratuitous_nudity` / `general_mature`。本项目为空。 |
| `minBranch` / `maxBranch` | 省略时上传器提交空串，表示"支持所有分支"。写错会把条目在某个分支上隐藏，所以本项目刻意不写。 |
| `previews/` 目录 | 可选。目录不存在时，远端的附加预览图保持不变；存在时按文件名增改删。 |

---

## 6. 上传之后

1. 打开条目页面，确认：
   - 标题、描述（BBCode 是否正常渲染）、预览图正确；
   - "必需物品"里能看到 `sts2_lan_connect`（3749766330）；
   - 可见性是 **Public** —— `packaging/workshop.json` 写的就是 `public`，**一次上传即公开**，
     没有"先私有看一眼再公开"这一步（作者 2026-11-14 明确要求直接公开）。
2. 用另一个账号或本机订阅一次，验证能正常下载并加载（日志出现 `[lobby-enhance] ... applied=5 ... failed=0`）。
3. 如果 Steam 提示需要先接受工坊法律协议，请在浏览器里接受后重新上传：
   `https://steamcommunity.com/sharedfiles/workshoplegalagreement`
5. **上传账号必须拥有本游戏。** 2026-11-14 实测：在没有 AppID 2868840 许可的账号上，
   上传器能登录、能初始化 Steam，但创建条目直接返回 `k_EResultAccessDenied`，
   Steam 日志写明 `Create new workshop item ... (Access Denied)`。这不是协议问题，换账号才能解决。
   工作区与发布包本身不受影响，任何拥有游戏的账号拿同一份 `packaging/` 目录都能上传。

---

## 7. 故障排查

| 现象 | 原因 | 处理 |
|------|------|------|
| `There is no file named image.png in the workspace!` | 预览图缺失 | 运行 `python packaging/make-image.py` |
| `There is no 'content' directory inside the workspace!` | 没生成内容 | 运行 `pwsh -File packaging/build-content.ps1` |
| `Exception thrown while parsing the workshop config!` | `workshop.json` 格式错误 | 用 JSON 校验器检查；注意不能有注释和尾随逗号 |
| `Invalid visibility ...` | 可见性拼写错误 | 只使用 private / public / unlisted / friends_only |
| `Steam initialization failed!` | Steam 没开、没登录，或 `steam_appid.txt` 不在工作目录 | 启动 Steam 并登录；在 `ModUploader.exe` 所在目录运行命令 |
| `Failed to upload content!` | `content/` 路径不可读或文件被占用 | 关闭占用 DLL 的进程后重试 |
| `Tried to upload to workshop item with ID ... but it doesn't exist!` | `mod_id.txt` 指向已删除的条目 | 确认要新建条目后删除 `mod_id.txt`，或用 `-i` 指定正确 ID |
| 上传成功但条目仍是隐藏 | 未接受工坊法律协议 | 接受协议后重新上传 |
| `Failed to create workshop item! Result: k_EResultAccessDenied` | **该 Steam 账号没有本游戏（AppID 2868840）的许可**。创建工坊条目要求账号拥有该 AppID，不是"协议没接受" | 换用**拥有本游戏**的账号上传。判据：Steam 日志 `E:\steam\logs\workshop_log.txt` 里会出现 `Create new workshop item of type Community for AppID 2868840 : ... (Access Denied)`；游戏自己的日志里也能看到 `Steam is enabled, we will write saves to steam storage. Enabled for account: True, app: False`（`app: False` 就是没有该 AppID 的许可） |
| 需要更多信息 | 上传器会在其所在目录写 `mod-uploader.log` | 查看该日志 |
| 一键上传包（交给朋友跑的那个）报错 | 包里的 `upload.ps1` 会把已知错误翻译成「原因 + 怎么办」 | 让对方把 `上传结果.txt` 发回来；**不要让对方自己判断或反复重试**（条目可能只建了一半） |

---

## 8. 撤销与回滚

- **想下架**：把 `visibility` 改成 `private` 再上传，或直接在网页上改为 Private。
- **想彻底删除条目**：`.\ModUploader.exe remove -w <工作区>`（本地工作区不受影响）。
  删除后 `mod_id.txt` 仍指向旧 ID，下次上传会报"不存在"——如果确实要重新开始，删掉 `mod_id.txt`。
- **回滚内容**：从版本库取回上一版的 `content/`（或直接切回上一个提交重新构建），再上传一次即可；
  工坊内容以最近一次上传为准。

---

## 9. 一键上传包（交给拥有游戏的账号代传）

因为本机账号没有 AppID 2868840 的许可，真实上传走这条路：**打一个包，对方双击一次**。

### 9.1 打与验收

```powershell
# 打包（产物：dist/sts2-lobby-enhance-upload-kit.zip + dist/upload-kit/ 暂存目录）
pwsh -NoProfile -File tools/pack-upload-kit/pack-upload-kit.ps1 -Force

# 验收（解压 → 双击 upload.bat → 断言窗口与 上传结果.txt 的每一句话）
pwsh -NoProfile -File tools/pack-upload-kit/test-upload-kit.ps1
```

`pack-upload-kit.ps1` 会强制检查：`workshop.json` 的 `visibility` 是 `public`、`content/` 正好 4 个文件、
上传器 `steam_appid.txt` 是 `2868840`、`upload.ps1` 落盘时带 **UTF-8 BOM**（PS 5.1 没有 BOM 会整篇乱码）、
打完后**重新解包**逐文件比对 sha256 与 `CHECKSUMS.txt`。任何一条不满足直接 exit 非 0。

`test-upload-kit.ps1` 覆盖 6 组场景共 **34 条**断言：包内是否带 `workspace/mod_id.txt`（无 BOM、纯数字）、
上传器返回 `k_EResultAccessDenied` 时失败必须被翻译成「换账号」、缺文件、文件被改坏、
`visibility` 被改回 `private`、以及用假上传器验证**成功分支**（条目链接、`上传结果.txt`、`mod_id.txt`）。

**这个脚本从不联系 Steam**：需要上传器输出时一律换成假上传器（`-Fail` 产出与真实 AccessDenied 同样的文本）。
以前的场景 1 会真跑上传器、并且先删掉 `mod_id.txt` 复现"首次上传"——那等于在一个有许可的账号上真的新建
一个工坊条目，随后临时目录被删，条目 ID 也就丢了。现在场景 1 还额外断言 `mod_id.txt` 没被删、没被改写。

### 9.2 包里是什么

```
sts2-lobby-enhance-upload-kit/
  upload.bat            双击入口（纯 ASCII，chcp 65001）
  upload.ps1            主逻辑（UTF-8 带 BOM）
  README-上传说明.md      给朋友看的一页说明
  CHECKSUMS.txt         13 个文件的 sha256，脚本自检用
  ModUploader/          官方上传器 v0.2.0 三件套
  workspace/            workshop.json（public）+ mod_id.txt + image.png + content×4
```

### 9.3 脚本行为（没有任何需要输入的选择题）

1. 自检：文件齐不齐、哈希对不对、`visibility` 是不是 `public`。
2. 检查 Steam 进程在不在。
3. 把上一次的 `ModUploader\mod-uploader.log` 改名成 `mod-uploader.previous.log`，
   然后在 `ModUploader\` 目录里跑 `ModUploader.exe upload -w <workspace>`。
4. 按日志把失败翻译成人话（无许可 / 未接受法律协议 / 离线 / 没登录 / 内容上传失败 / 条目已被删 …），
   并把「原因 + 怎么办 + 完整日志」写进同目录的 **`上传结果.txt`**。
5. 无论成功失败都 `Read-Host` 停住窗口；脚本自己 `exit 0`，避免 `upload.bat` 二次 pause。

朋友只需要：解压 → 双击 `upload.bat` → 把 `上传结果.txt` 发回来。
`workspace/mod_id.txt` 是**包里自带**的，所以对方的每一次上传都是更新同一条目；
`上传结果.txt` 里也会回显条目 ID 和链接。

> **不要改 `upload.ps1` 的交互模型。** 它的全部价值在于"对方不用做任何判断"：
> 任何需要输入的分支都会在现场变成一次沟通成本，而现场是没有日志的。

### 9.4 发布记录

| 项 | 值 |
|---|---|
| 工坊条目 | **3839101755** · <https://steamcommunity.com/sharedfiles/filedetails/?id=3839101755> |
| 首次上传 | 2026-11-14 02:30:39（+08:00） |
| 上传账号 | 维护者朋友的 Steam 账号（昵称从略；维护者本机账号没有 AppID 2868840 的许可） |
| 首次上传用的包 | 6,439,500 B · sha256 `72FEFE34…17365D`（**不含** `mod_id.txt`，只能用于这一次新建） |
| 2.0.0 上传包 | 6,418,628 B · sha256 `43234A0E…6457422190`（**含** `mod_id.txt`，上传即更新同一条目） |
| **2.0.0 已上线** | 2026-11-15T06:38:05Z（本地 14:38），线上 `file_size = 160,841 B`，描述首行 `v2.0.0` |
| **2.1.0 上传包（当前，待上传）** | 6,500,540 B · sha256 `B1BF58A3C3C80859A8CF3DD41C3F53A37C7556B13F88C6420093F94FA6EEBB46` |
| 2.1.0 发布包 | 141,476 B · sha256 `821574DC73B1197595F2A5555C8656CFC72D277EF70A31ACBD83CD0C5C91F34F` |
| 2.1.0 内容 | 364,768 B = dll 324,096 + 清单 939 + LICENSE 35,825 + NOTICES 3,908 |
| 首次上传内容（1.7.0） | 226,838 B = dll 186,368 + 清单 651 + LICENSE 35,825 + NOTICES 3,994 |
| 2.0.0 内容 | 160,841 B = dll 120,320 + 清单 702 + LICENSE 35,825 + NOTICES 3,994 |
| 可见性 | public（页面匿名可访问、有订阅按钮） |
| 必需物品 | 3749766330（游戏大厅 / STS2 LAN Connect） |

**以后每次更新都必须由同一个账号上传。** 工坊条目不支持转让作者；换账号只会得到
"没有权限"或者第二条新条目。
