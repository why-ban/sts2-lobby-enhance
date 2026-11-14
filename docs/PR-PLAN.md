# 上游 PR / issue 策略（task-23）

> 只读调研产出。本文所有结论都带 `文件:行号` 或 git 输出；拿不到证据的一律写「未确证」。
> 仓库缩写：**LOBBY** = `D:\zhuanban\sts2m\work\lobby\STS2-Game-Lobby\`（上游克隆），
> **COMPANION** = `D:\zhuanban\sts2m\work\companion\`，**WS** = `D:\zhuanban\sts2m`。
> 游戏 0.113 反编译源：`WS\work\compat\decompiled-v0.113.0`（下称 **DEC113**，commit `16ac0fe3`，见日志目录名 `v0.113.0-16ac0fe3`）。

> **⚠ 2026-11-14 事实更正（先看这条，再看全文）**
>
> 本文多处写「只有 Android 验证过 / PC 端未测」。那是**从 `lobby-port-v113.md` 抄来的、针对「12 文件源码移植」的说法**，
> 不能套到整个项目上。实际验证矩阵是：
>
> | 组合 | 状态 | 证据 |
> |---|---|---|
> | **Windows 桌面版房主 + Android 客户端** | **反复验证**（加入 / 开局 / 打完一层 / 局中重开 / 结算页新开一局） | 每次会话的 PC `godot.log` 有 `NetHostGameService` + `hosting …`，手机日志是 `NetClientGameService` |
> | **Android 房主** | 验证过（至少一次） | `WS\work\logs\2043\...\godot.log` 有 `hosting …` |
> | **PC 作为加入方（客户端）** | **未验证** | 所有日志里 PC 一直是房主 |
> | **12 文件源码移植在 Windows 上** | **未验证**（移植版只在 Android 跑过） | `lobby-port-v113.md:100`、`:184-188` |
>
> 也就是说：**「PC 端没测过」是错的；「PC 客户端没测过」「移植版没在 Windows 上跑过」是对的。**
> 对外（issue / PR）只能按上表写。

---

## 0. 结论摘要（先给答案）

用户的问题混了两件不同的东西，必须先拆开：

| 东西 | 在哪 | 它是什么 | 能不能提 PR 到上游 |
|---|---|---|---|
| **A. 12 文件 / +273−113 未提交改动** | `LOBBY` 工作区 | **上游自己源码的 0.113 移植**（改的全是 `sts2-lan-connect/Scripts/` 里上游的代码） | **能，而且这是它唯一正确的去处** |
| **B. 独立附属模组** | `COMPANION` | 另一个模组 `sts2_lan_connect_0113_fix` v1.6.1（`COMPANION\docs\README.md:3`），运行期给社区模组 0.6.3 DLL 打 Harmony 补丁 | **不能**。它不是上游代码，是独立产品 |

所以「我们做的是独立附属模组，不是移植版」这句话对 **B** 成立、对 **A** 不成立。**A 就是移植版**——
`git diff` 里改的 12 个文件全部是上游 `sts2-lan-connect/Scripts/` 下的文件，没有一个是 COMPANION 的文件。

**建议（明确）**：

1. **本轮先做 issue 评论，不提 PR。** 理由见 §6.1：只有 Android 侧真机验证、PC 端未测、产物本身缺文件、
   且无法确认上游是否已在别处修了（无网络）。
2. **PR 延后，等三个前置条件满足**（§6.3）：补齐缺失的新文件、修掉 `StandardButton` 节点名、PC 端跑完回归。
3. **PR 只放「上游代码的 0.113 兼容修复」**，把 `global.json`、`ReceiveDiagnostics` 临时诊断、
   以及 B 的任何东西全部排除。
4. **B（附属模组）不进上游仓库**，只在 issue 评论里用一句话附带说明「外部有临时可用方案」，
   发布放在我们自己的仓库/创意工坊。

---

## 1. 事实基线（git 输出）

````
$ git -C LOBBY rev-parse --short HEAD
216e9ad
$ git -C LOBBY log --oneline -3
216e9ad docs(release): publish v0.6.3 to the Steam Workshop and add the 0.6.3 announcements
$ git -C LOBBY rev-list --left-right --count origin/main...HEAD
0	0
$ git -C LOBBY status --porcelain
 D global.json
 M sts2-lan-connect/Scripts/LanConnectGameplayPatches.cs
 M sts2-lan-connect/Scripts/LanConnectHostFlow.cs
 M sts2-lan-connect/Scripts/LanConnectLobbyCapacityPatches.cs
 M sts2-lan-connect/Scripts/LanConnectSerializationPatches.cs
 M sts2-lan-connect/Scripts/Lobby/LanConnectLobbyJoinFlow.cs
 M sts2-lan-connect/Scripts/Lobby/LanConnectLobbyManagedJoinFlow.cs
 M sts2-lan-connect/Scripts/Lobby/LanConnectLobbyOverlay.cs
 M sts2-lan-connect/Scripts/Lobby/LanConnectMultiplayerSaveRoomBinding.cs
 M sts2-lan-connect/Scripts/Patches.HostSubmenu.cs
 M sts2-lan-connect/Scripts/Patches/DifficultyScalingPatches.cs
 M sts2-lan-connect/Scripts/Patches/TreasurePatches.cs
?? global.json.bak
?? sts2-lan-connect/Scripts/Protocol/Patches/LanConnectReceiveDiagnosticsPatches.cs
$ git -C LOBBY diff --stat
 12 files changed, 273 insertions(+), 113 deletions(-)
````

即：**11 改 + 1 删（global.json）+ 2 个未跟踪文件**。工作区改动从未提交，也从未 push（`0	0`）。

---

## 2. 那 12 个文件到底是什么

**定性：A 是「让上游 v0.6.3 在游戏 0.113.0 上编译并跑通」的源码移植**，逐条对得上 0.113 的真实 API 变更。
其中 **3 处不该进 PR**（表里标 ❌）。

| # | 文件 | 改了什么（工作区行号） | 对应 0.113 的哪个破坏点 | 进 PR？ |
|---|---|---|---|---|
| 1 | `global.json`（删除） | 删掉 SDK 版本 pin（9.0.311） | 与游戏无关，是**本机只有 dotnet 10** 的环境变通（`WS\reports\lobby-port-v113.md:70`） | ❌ 不进 |
| 2 | `LanConnectGameplayPatches.cs` | +1 行：注册新补丁组 `ReceiveDiagnostics`（:70） | 与 0.113 无关，是**入包诊断**（`lobby-port-v113.md:141-143` 明写「临时，不进 PR」） | ❌ 不进 |
| 3 | `LanConnectHostFlow.cs` | `StartLanHostAsync` / `PushHostScreen` 的 `GameMode` 参数改成模组自有枚举 `LanConnectLanHostMode`（:38、:145、:638、:642、:650） | **破坏点 #3**：0.113 删了 `GameMode.Custom`（`DEC113\...\JoinResult.cs:9,11` 改成 `gameMode` + `isCustom`） | ✅ |
| 4 | `LanConnectLobbyCapacityPatches.cs` | 反射找 `StartRunLobby` 构造函数时优先 5 参签名 `(GameMode,bool,INetGameService,IStartRunLobbyListener,int)`，失败回落旧 4 参（:70-78） | `StartRunLobby` ctor 新增 `bool isCustom`（`DEC113\...\Lobby\StartRunLobby.cs:124`）。不改则 8 人容量补丁被静默 skipped | ✅ |
| 5 | `LanConnectSerializationPatches.cs` | ① `SerializeJoinResponseBody` 补写 `gameMode`+`isCustom`（:597-598）；② 新增「旧版报文回退」finalizer（:186、:623、:673） | ① 0.113 把 `gameMode` 从 `InitialGameInfoMessage` **挪进** `ClientLobbyJoinResponseMessage` 并加 `isCustom`（`DEC113\...\ClientLobbyJoinResponseMessage.cs:32,37,79-80`）；② **可选增强**，不是编译必需 | ✅ ①，② 建议拆出去单独讨论 |
| 6 | `Lobby\LanConnectLobbyJoinFlow.cs` | `PushJoinedScreen` 改成「先判 `isCustom`，再按 `Standard/Daily/Blitz/Dread` 分派」，新增 `NBlitzScreen`/`NDreadScreen`（:443-510） | **破坏点 #3** + 0.113 新增 `GameMode.Blitz`/`Dread` | ✅ |
| 7 | `Lobby\LanConnectLobbyManagedJoinFlow.cs` | 模式不再从 `InitialGameInfoMessage.gameMode` 取，改从 join/load/rejoin 响应取（:128、:131、:146、:160）；`maxAscensionUnlocked` → `maxStandardAscension`+`maxBlitzAscension`（:490-491） | **破坏点 #1 + #2**（0.113 上这条就是 PC 端「加入必崩」的直接原因） | ✅ |
| 8 | `Lobby\LanConnectLobbyOverlay.cs` | `GetSelectedCreateGameMode()` → `GetSelectedCreateMode()` 返回 `LanConnectLanHostMode`（:4418、:5067、:5099） | **破坏点 #3** | ✅ |
| 9 | `Lobby\LanConnectMultiplayerSaveRoomBinding.cs` | 新增 `GetLobbyGameMode(LanConnectLanHostMode)` / `GetLobbyGameModeLabel(LanConnectLanHostMode)` 重载（:376、:387），`GameMode` 分支补 `Blitz`/`Dread`（:398） | **破坏点 #3** + 新模式 | ✅ |
| 10 | `Patches.HostSubmenu.cs` | 删掉 `LanConnectLanHostMode → GameMode` 的映射表，直接传 `mode`（:152） | **破坏点 #3** | ✅ |
| 11 | `Patches\DifficultyScalingPatches.cs` | 1 行：`using MegaCrit.Sts2.Core.Models.Singleton;` → `using MegaCrit.Sts2.Core.Models;`（:8） | `MultiplayerScalingModel` 换命名空间（`DEC113\...\Models\MultiplayerScalingModel.cs`）；**上游自身缺陷**，见 §5-A | ✅ |
| 12 | `Patches\TreasurePatches.cs` | 未认领遗物从 `List<RelicModel>` 改成 `List<int>` 索引，补齐 `relicIndex`（:581、:590、:599、:624、:629-635） | `RelicPickingResult.relicIndex` 变 `required`、`GenerateRelicFight` 多一个参数（`DEC113\...\TreasureRelicPicking\RelicPickingResult.cs:15,38`） | ✅（仅「能编译」，别动投票逻辑，见 §5-C） |

**关键判断**：这 12 个文件**没有一个**来自 COMPANION。所以「我们做的是独立附属模组，所以不该提 PR」这个推论
对 A 不成立——A 就是在上游自己的文件上做的改动。

---

## 3. 上游是否已经自己支持 0.113？→ **未确证（无网络）**

````
$ git -C LOBBY fetch origin
fatal: unable to access 'https://github.com/emptylower/STS2-Game-Lobby.git/':
  Failed to connect to 127.0.0.1 port 7890 after 2053 ms: Could not connect to server
FETCH_EXIT=128
$ git -C LOBBY config --get http.proxy
http://127.0.0.1:7890
````

代理 7890 没起，**fetch 失败**。所以「上游现在（2026-11-14）有没有 0.113 分支/提交」**未确证**。

能确定的是**本地这份克隆**（`origin/main` = `216e9ad`，2026-09-24 00:08:17 +0800）**没有 0.113 支持**：

- `LOBBY\CHANGELOG.md:7` 最新版本 `[0.6.3] - 2026-09-23`；`:26` 的验收环境写的是「游戏 **0.111.0**」。
- `LOBBY\docs\abi-reports\2026-09-01-native-bus\REPORT.md:6`：「本机 `sts2.dll` **v0.111.0**」。
- `LOBBY\docs\abi-reports` 下只有 `v0.107.1` 和 `v0.111.0` 两套目录，**没有 0.113**。
- 全仓（排除 `releases/`）grep `0\.113` 只命中测试用的保留 IP `203.0.113.x`，无一处指游戏版本。

**⇒ 结论：本地基线明确停在 0.111；远端最新状态未确证。提 PR 前必须先 `git fetch` 看一眼，否则可能白做。**

---

## 4. 6 处签名断裂（PC 端「加入必崩」的直接原因）

来源两条独立证据：① `COMPANION\docs\README.md:32-39` 的断点表（由 `BreakpointRegressionTests` 扫社区模组 DLL
22,774 个元数据候选、断言「恰好这 6 个不可解析」得出，`README.md:48-49`）；② 把 v0.6.3 源码直接对着
0.113 的 `sts2.dll` 编译得到的错误清单（`WS\reports\lobby-issue-body.md:21-34`）。

| # | 成员 | 0.111（0.6.3 编译时） | 0.113（实测） | 0.113 侧证据 | 上游引用点（HEAD 行号） |
|---|---|---|---|---|---|
| 1 | `InitialGameInfoMessage.gameMode` | 字段存在 | **字段已删除**（struct 只剩 `sessionState` + `connectionFailureReason`） | `DEC113\...\Lobby\InitialGameInfoMessage.cs:16,21` | `LanConnectLobbyManagedJoinFlow.cs:118,124,130,136`（CS1061 ×4） |
| 2 | `ClientLobbyJoinRequestMessage.maxAscensionUnlocked` | 字段存在 | 拆成 `unlockState` + `maxStandardAscension` + `maxBlitzAscension` | `...\ClientLobbyJoinRequestMessage.cs:13,15,17` | `LanConnectLobbyJoinFlow.cs:462` |
| 3 | `NetHostGameService.DisconnectClient` | `(ulong, NetError, bool)` | `(ulong, NetError, bool now = false, string? debugReason = null)` | `...\Multiplayer\NetHostGameService.cs:242` | `LanConnectTailMessageRuntime.cs:1423`、`:1658`、`LanConnectRemoteLobbyPlayerPatches.cs:255` |
| 4 | `NetHostGameService.ConnectedPeers` | `List<NetClientData>` | `IReadOnlyList<NetClientData>` | `...\NetHostGameService.cs:40` | `LanConnectTailMessageRuntime.cs:1643` |
| 5 | `RelicPickingResult.GenerateRelicFight` | 3 参数 | 4 参数，且 `relicIndex` 变 `required` | `...\TreasureRelicPicking\RelicPickingResult.cs:15,38` | `Patches\TreasurePatches.cs:594,603,628`（CS9035 / CS7036） |
| 6 | `SaveManager.UpdateProgressWithRunData` | `void` | 返回 `PostRunProgressUpdateResult` | `...\Saves\SaveManager.cs:338` | `LanConnectMultiplayerSaveCompatibility.cs:318` |

**重要区分**（决定 PR 要改多少）：

- **#1 #2 #5 是源码级断裂**——不写代码就编译不过，所以 §2 的表里有对应改动（文件 6/7/12）。
- **#3 #4 #6 是二进制级断裂**——源码里那几行**照样能编译**（`DisconnectClient` 新参数有默认值、
  `ConnectedPeers` 换成接口类型不影响 `.Select()`、`UpdateProgressWithRunData` 的返回值当语句丢弃合法），
  所以 12 文件 diff **一行都没碰它们**。只要**重新对着 0.113 编译**，这三处自动修好——这也正是
  「源码移植（A）」比「运行期打补丁（B）」更彻底的地方。
- 复核：`DisconnectClient|ConnectedPeers` 在 `LOBBY\sts2-lan-connect\Scripts` 命中 4 处
  （`LanConnectTailMessageRuntime.cs:1423,1643,1658`、`LanConnectRemoteLobbyPlayerPatches.cs:255`），
  `UpdateProgressWithRunData` 命中 1 处（`LanConnectMultiplayerSaveCompatibility.cs:318`），**均不在 12 文件 diff 内**。

---

## 5. 我们额外发现的 3 处「上游自己踩的 0.113 坑」

这三处**不在** 6 断点表里（6 断点表只覆盖「必须修才能跑」的元数据引用），是我们在真机上额外抓到的。

### A. `DifficultyScalingPatches` 的 `MultiplayerScalingModel` 不存在 —— **真不兼容，已实测**

- 上游代码：`LOBBY\sts2-lan-connect\Scripts\Patches\DifficultyScalingPatches.cs:8`
  （HEAD 是 `using MegaCrit.Sts2.Core.Models.Singleton;`），`:22`、`:28`、`:52` 用 `typeof(MultiplayerScalingModel)`。
- 0.113 实际位置：`DEC113\MegaCrit\sts2\Core\Models\MultiplayerScalingModel.cs`（在 `Models`，不在 `Models.Singleton`）。
- 真机实测（Android，0.113）：

````
WS\work\logs\180446\internal-files\instances\v0.113.0-16ac0fe3--d32b3370\logs\godot.log:136
[ERROR] sts2_lan_connect gameplay: DifficultyScaling patches failed:
  TypeLoadException: Could not resolve type with token 010002c2 from typeref
  (expected class 'MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel' ...)
````

  同一行在 `work\lead\crash2` 到 `crash8` 的 8 份日志里完全一致（如 `work\lead\crash3\...\godot.log:136`）。
- 后果：整组难度缩放补丁 **静默失效**（`LanConnectGameplayPatches` 的 `TryApplyGroup` 吞掉异常，
  `godot.log:183` 记 `patch groups applied=9, failed=1`）。
- 上游修法：改 1 行 `using`（§2 表第 11 行）。

### B. `HostSubmenuPatches` 找 `StandardButton` 失败 —— **真不兼容，已实测**

- 上游代码：三处硬编码**相对路径**取节点 ——
  `LOBBY\sts2-lan-connect\Scripts\Patches.HostSubmenu.cs:34`、`:111`、`:135-137`
  （`submenu.GetNode<NSubmenuButton>("StandardButton")`、`"DailyButton"`、`"CustomRunButton"`）。
- 真机实测（Android，0.113）：

````
WS\work\logs\180446\...\logs\godot.log:566547
ERROR: Node not found: "StandardButton"
  (relative to "/root/Game/RootSceneContainer/MainMenu/Submenus/MultiplayerHostSubmenu").
WS\work\logs\180446\...\logs\godot.log:566539
[ERROR] sts2_lan_connect failed to inject LAN host button: System.NullReferenceException
   at Sts2LanConnect.Scripts.HostSubmenuPatches.EnsureLanHostButton(...)   ← 栈帧见 :566530-566538
````

  同一现象在 `work\logs\174551` / `180446` / `1939` / `2043` 四份日志里都有。
- 0.113 自己怎么取：用**唯一名**前缀 `%` ——
  `DEC113\...\MainMenu\NMultiplayerHostSubmenu.cs:194`（`GetNode<NSubmenuButton>("%StandardButton")`）、
  `:198` `%DailyButton`、`:202` `%BlitzButton`、`:206` `%DreadButton`、`:210` `%CustomRunButton`；
  且这些按钮挂在 `%ButtonContainer` 下（`:234`）。即 0.113 的按钮**不再是 submenu 的直接子节点**。
- 后果：**上游的「LAN 调试建房」按钮在 0.113 上根本注入不出来**（被 `Patches.HostSubmenu.cs:49-52` 的
  try/catch 吞掉，只留一行 ERROR）。
- ⚠ **12 文件 diff 没有修这一处**：在 `WS\dist\lobby-port-v0.113\source-changes-v0.113.patch` 里
  grep `StandardButton` → **0 命中**。这是 PR 前必须补的一条（把 `"StandardButton"` 改成 `"%StandardButton"` 等）。

### C. `TreasurePatches` 自己跳过 OnPicked —— **不是缺陷，是上游自带的 0.113 guard**

- 上游代码（部署版 0.6.3 反编译）：`LOBBY\..\dec-official\Sts2LanConnect.Scripts\TreasurePatches.cs:122-127`、
  `:137-140`、`:163-167` —— 检测到 0.113 原生 `TreasureRoomRelicSynchronizer.OnPicked(Player, int?)`
  已支持「跳过」票时，**主动跳过自己的投票复刻补丁**。
- 真机实测（Android，0.113）：

````
WS\work\logs\180446\...\logs\godot.log:177
[INFO] sts2_lan_connect gameplay: treasure native nullable skip detected; legacy vote patches are not required.
WS\work\logs\180446\...\logs\godot.log:178
[INFO] sts2_lan_connect gameplay: treasure patches applied=9, skipped=2, failed=0.
````

- **准确说法**：这不是「0.113 破坏」，而是「0.113 上这条补丁本来就不需要」。所以：
  - 给作者的 issue 里**不要**把它写成 bug；
  - PR 里 **只补 `relicIndex` 让代码能编译**，**不要**去「修」投票逻辑
    （`COMPANION\docs\CHANGELOG.md:52-65` 记录了 v1.5 曾复刻一版、因与原生可观测不一致而在 v1.6 删除）。

---

## 6. 建议：提 PR 还是 issue 评论？

### 6.1 本轮：**先在原 issue 下补评论，不提 PR**

理由（每条都有据）：

1. **验证面不够**。只在 Android（Redmi K20 Pro / Android 11 / Vulkan）真机跑过，
   **PC 端未测**（`lobby-port-v113.md:100`、`:184-188` 的「待验证」仍空着）。上游 0.6.3 的验收基线是
   Mac + Android + 0.111（`LOBBY\CHANGELOG.md:26`），拿 Android-only 的结果去要求合并，作者没法验。
2. **产物本身不完整**。`WS\dist\lobby-port-v0.113\source-changes-v0.113.patch` 只有 **11** 个 `diff --git` 头，
   **没有** `LanConnectReceiveDiagnosticsPatches.cs` 这个新文件的 `new file mode` 段——而补丁第 9 行就在引用
   那个类。**把这份 patch 贴到干净树上会编译不过。**
3. **改动里混了 3 类不该进 PR 的东西**：`global.json` 删除（本机 SDK 变通）、`ReceiveDiagnostics` 临时诊断
   （作者明说「不进 PR」）、以及跨版本「旧报文回退」这种**需要作者拍板的设计决策**
   （`lobby-port-v113.md:154` 也写着临时项要移除）。
4. **可能重复劳动**。远端最新状态未确证（§3），先评论问一句成本最低。
5. **issue 已经开着、已经有清单**。`lobby-port-v113.md:101` 记「上游 issue：`emptylower/STS2-Game-Lobby#44`
   （已含编译错误清单）」，而 `WS\reports\lobby-issue-body.md` 就是那份稿子（63 行）。
   ⚠ 但 issue 号 **#44** 无法离线核实（无网络）→ **未确证**，发之前必须在网页上确认。
   ⚠ 另注意 `lobby-issue-body.md:62` 写「我没有改动本仓库代码」——**这句话现在不成立了**（工作区有 12 文件改动），
   评论里必须改成事实陈述，否则自相矛盾。

### 6.2 为什么不走「只提 PR、不评论」

因为 PR 的前置条件（PC 回归 + 补文件 + 补 `StandardButton`）本轮都满足不了；
而 issue 评论能立刻把**作者最缺的东西**给到：0.113 上的断点清单 + 真机日志 + 逐条修法。
**评论是 PR 的预告，不是 PR 的替代。**

### 6.3 PR 的三个前置条件（缺一不提）

1. **补齐产物**：`git add` 那个未跟踪的 `LanConnectReceiveDiagnosticsPatches.cs`（如果决定保留诊断）
   **或** 把 `LanConnectGameplayPatches.cs` 那 +1 行一起撤掉（推荐撤掉，作者不需要诊断）。
2. **修 `StandardButton`**：`Patches.HostSubmenu.cs:34/111/135-137` 改成 `%StandardButton` / `%DailyButton` /
   `%CustomRunButton`（依据 `DEC113\...\NMultiplayerHostSubmenu.cs:194-210`）。
3. **PC 端回归**：电脑建房 → 手机进；手机建房 → 电脑进；以及**开局进战斗**
   （`LobbyBeginRunMessage` 与玩家列表位宽要到开局才用到，`lobby-port-v113.md:186-187`）。

### 6.4 分支名与提交拆分（等前置条件满足后再用）

分支：`fix/game-0.113-compat`（不用 `port/`，因为这不是重写，是跟上游戏版本）

建议拆 5 个提交，一个提交一个可解释的语义，方便作者逐个 review / 只挑一部分：

| 序 | 提交信息 | 文件 |
|---|---|---|
| 1 | `fix(join): read game mode and isCustom from join responses on game 0.113` | `Lobby\LanConnectLobbyManagedJoinFlow.cs` |
| 2 | `fix(wire): write gameMode/isCustom in the hand-built join-response body` | `LanConnectSerializationPatches.cs`（只含 :597-598 那两行） |
| 3 | `refactor(host): express mod-owned room modes with LanConnectLanHostMode` | `LanConnectHostFlow.cs`、`Patches.HostSubmenu.cs`、`Lobby\LanConnectLobbyOverlay.cs`、`Lobby\LanConnectMultiplayerSaveRoomBinding.cs` |
| 4 | `feat(join): route Blitz/Dread lobbies to their ready screens` | `Lobby\LanConnectLobbyJoinFlow.cs`（+ 上表的 `Blitz`/`Dread` 分支） |
| 5 | `fix(gameplay): follow 0.113 API changes (StartRunLobby ctor, MultiplayerScalingModel, RelicPickingResult)` | `LanConnectLobbyCapacityPatches.cs`、`Patches\DifficultyScalingPatches.cs`、`Patches\TreasurePatches.cs` |
| （6） | `fix(ui): resolve host submenu buttons by unique name on 0.113` | `Patches.HostSubmenu.cs`（§6.3 第 2 条） |

**不要放进 PR**：`global.json`、`LanConnectGameplayPatches.cs` 的 `ReceiveDiagnostics` 注册、
`LanConnectReceiveDiagnosticsPatches.cs`、`RecoverLegacyJoinResponseBody`（旧报文回退，单独开 issue 讨论）、
以及 COMPANION 的任何文件。

### 6.5 PR 标题 / 正文草稿（中英双语）

标题：

````
fix: restore compatibility with game 0.113.0 / 修复在游戏 0.113.0 上的兼容性
````

正文：

````markdown
> Submitted by an AI agent on behalf of a user. / 本 PR 由 AI 代理代表用户提交。

## What breaks on game 0.113.0

Game 0.113.0 (`16ac0fe3`) changes several members this mod was compiled against (0.6.3 targets 0.111.0,
see `CHANGELOG.md`). Rebuilding `sts2-lan-connect` against 0.113 produces 16 errors across 7 API changes;
at runtime the old DLL throws before it can join anything.

### Source-level breaks (must be edited)

| # | Member | 0.111 | 0.113.0 |
|---|---|---|---|
| 1 | `InitialGameInfoMessage.gameMode` | present | removed; only `sessionState` + `connectionFailureReason` remain |
| 2 | `ClientLobbyJoinRequestMessage.maxAscensionUnlocked` | present | `unlockState` + `maxStandardAscension` + `maxBlitzAscension` |
| 3 | `GameMode.Custom` | enum value | removed; replaced by `JoinResult.isCustom` / `SerializableRun.IsCustomGame` |
| 4 | `GameMode.Blitz` / `GameMode.Dread` | absent | new, with `NBlitzScreen` / `NDreadScreen` |
| 5 | `RelicPickingResult.relicIndex` | absent | `required`; `GenerateRelicFight(players, relic, relicIndex, generateMove)` |
| 6 | `StartRunLobby` ctor | `(GameMode, INetGameService, IStartRunLobbyListener, int)` | `(GameMode, bool isCustom, INetGameService, IStartRunLobbyListener, int)` |
| 7 | `MultiplayerScalingModel` namespace | `...Models.Singleton` | `...Models` |

### Binary-level breaks (fixed automatically by rebuilding)

`NetHostGameService.DisconnectClient` gained `bool now = false, string? debugReason = null`;
`ConnectedPeers` now returns `IReadOnlyList<NetClientData>`; `SaveManager.UpdateProgressWithRunData`
now returns `PostRunProgressUpdateResult`. No source edit needed — but a **source** rebuild is required,
the 0.6.3 DLL cannot be made to work by patching around these.

## What this PR does

- Reads `gameMode` / `isCustom` from the join / load-join / rejoin responses instead of the removed
  `InitialGameInfoMessage.gameMode`; sends `maxStandardAscension` + `maxBlitzAscension`.
- Replaces the mod's own three host modes with the already-existing `LanConnectLanHostMode` enum
  instead of borrowing `GameMode` to express "custom".
- Routes Blitz / Dread lobbies to `NBlitzScreen` / `NDreadScreen` (dispatch order mirrors
  `NJoinFriendScreen.JoinGameAsync`: `isCustom` first).
- Adds `isCustom` to the hand-built join-response body (`SerializeJoinResponseBody`).
- Follows `StartRunLobby`'s new ctor, `MultiplayerScalingModel`'s namespace and
  `RelicPickingResult.relicIndex`.

## What this PR deliberately does NOT do

- No wire-format change: field order, `tail` frame layout and the lobby HTTP/WS DTOs are untouched.
  Hosts and clients therefore stay byte-compatible with an unpatched 0.6.3 peer **on the same game build**.
- No feature work. The mod's own extras (mid-run restart, post-run "new run", mode unlocks) are not here.
- `TreasurePatches`' vote logic is left alone: on 0.113 the game's native
  `TreasureRoomRelicSynchronizer.OnPicked(Player, int?)` already supports the skip vote, and 0.6.3
  already guards that patch off (`TreasurePatches.cs:122-127`, `:137-140`, `:163-167`).

## Testing

- Built against `sts2.dll` 0.113.0: **16 errors → 0 errors**, one pre-existing CS8604 warning remains.
- Real-device for **this port**: Android only (Redmi K20 Pro, Android 11, Vulkan).
  - host on Android / join from Android: OK (`Lobby join accepted. Mode=Standard IsCustom=False`)
  - host on Android / join from an unpatched PC 0.6.3 client: still fails, because that client itself
    reads the removed `InitialGameInfoMessage.gameMode` — nothing the host can do.
- **This port has not been run on Windows.** (A separate runtime-patch companion mod, which does not
  touch your files, is used daily with a Windows host + Android client — but that is not this PR.)

## Known gap (not in this PR)

The LAN debug host button still fails to inject on 0.113:
`Node not found: "StandardButton" (relative to ".../MultiplayerHostSubmenu")`. 0.113 resolves these
buttons through their unique names (`%StandardButton` / `%DailyButton` / `%CustomRunButton`), so the
plain relative paths in `Patches.HostSubmenu.cs` no longer resolve. Happy to add that one-liner if
you want it in this PR.
````

---

## 7. 可直接粘贴的 issue 评论草稿

> 用法：把 `#44` 换成实际 issue 号（**未确证**，见 §6.1-5）；「本评论由 AI 整理」这句要保留。
> 下面整段可直接复制（外层 4 个反引号只是本文档的包裹，复制时不要带上）。

````
> 说明：本条评论由 AI 代理根据用户提供的真机日志整理。我们只在 **Android**（Redmi K20 Pro / Android 11 / Vulkan）
> 上跑过，**PC 端未做验证**；标「实测」的有日志，标「编译验证」的是对着 0.113 的 `sts2.dll` 编译得到的，
> 其余为代码层面的推断。

## 1. 环境

- 游戏 **0.113.0**（`16ac0fe3`）
- 客户端 **0.6.3**（创意工坊 `3749766330`）
- 现象：**建房正常**，**加入任何 0.113 房间必崩**（100% 复现）

## 2. 根因：0.113 改了 6 个我们引用的成员

把 `sts2-lan-connect` 源码直接对着 0.113 的 `sts2.dll` 编译
（`-p:Sts2DataDir=<0.113 data 目录>`），**16 个错误**，归并为 6 处签名断裂（编译验证）：

| # | 成员 | 0.111 | 0.113.0 |
|---|---|---|---|
| 1 | `InitialGameInfoMessage.gameMode` | 字段存在 | **已删除**，struct 只剩 `sessionState` + `connectionFailureReason` |
| 2 | `ClientLobbyJoinRequestMessage.maxAscensionUnlocked` | 字段存在 | `unlockState` + `maxStandardAscension` + `maxBlitzAscension` |
| 3 | `NetHostGameService.DisconnectClient` | `(ulong, NetError, bool)` | `(ulong, NetError, bool now = false, string? debugReason = null)` |
| 4 | `NetHostGameService.ConnectedPeers` | `List<NetClientData>` | `IReadOnlyList<NetClientData>` |
| 5 | `RelicPickingResult.GenerateRelicFight` | 3 参数 | 4 参数，且 `relicIndex` 变 `required` |
| 6 | `SaveManager.UpdateProgressWithRunData` | `void` | 返回 `PostRunProgressUpdateResult` |

对应的编译错误（节选）：

```
Scripts/Lobby/LanConnectLobbyManagedJoinFlow.cs(118,124,130,136): CS1061  InitialGameInfoMessage 未包含 gameMode
Scripts/Lobby/LanConnectLobbyJoinFlow.cs(462): CS0117  ClientLobbyJoinRequestMessage 未包含 maxAscensionUnlocked
Scripts/Patches/TreasurePatches.cs(594,628): CS9035   RelicPickingResult.relicIndex 为必需成员
Scripts/Patches/TreasurePatches.cs(603): CS7036       GenerateRelicFight 需要 generateMove 参数
Scripts/Patches/HostSubmenu.cs(156): CS0117           GameMode 未包含 Custom
```

**PC 端「加入必崩」的直接原因**是第 1 条：客户端在 `LanConnectLobbyManagedJoinFlow.BeginAsync` 里读
`initialMessage.gameMode`。电脑端日志（实测）：

```
MissingFieldException: Field not found:
  'MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.InitialGameInfoMessage.gameMode'
  at LanConnectLobbyManagedJoinFlow.BeginAsync
```

注意这条**与房主是谁无关**：只要客户端跑的是 0.6.3，它自己就会抛。

## 3. 另外 3 处「不是 6 断点、但 0.113 上会踩」的地方

### 3.1 `DifficultyScalingPatches` 的 `MultiplayerScalingModel` 找不到（实测）

0.113 把这个类型从 `MegaCrit.Sts2.Core.Models.Singleton` 挪到了 `MegaCrit.Sts2.Core.Models`。
`Patches/DifficultyScalingPatches.cs:8` 的 `using` 还是旧命名空间，于是整组补丁在 JIT 时就炸：

```
[ERROR] sts2_lan_connect gameplay: DifficultyScaling patches failed:
  TypeLoadException: Could not resolve type with token 010002c2 from typeref
  (expected class 'MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel' ...)
...
[INFO]  sts2_lan_connect gameplay: patch groups applied=9, failed=1.
```

只影响「超过 4 人时的难度缩放」，不崩游戏，但**补丁是静默失效的**（被 `TryApplyGroup` 吞掉）。
改 1 行 `using` 即可。

### 3.2 `Patches.HostSubmenu.cs` 找不到 `StandardButton`（实测）

0.113 把 Host 子菜单里的按钮改成用**唯一名**访问，而模组用的是相对路径：

```
ERROR: Node not found: "StandardButton"
  (relative to "/root/Game/RootSceneContainer/MainMenu/Submenus/MultiplayerHostSubmenu")
[ERROR] sts2_lan_connect failed to inject LAN host button: System.NullReferenceException
  at Sts2LanConnect.Scripts.HostSubmenuPatches.EnsureLanHostButton(...)
```

0.113 自己的取法是 `GetNode<NSubmenuButton>("%StandardButton")`（`%DailyButton` / `%BlitzButton` /
`%DreadButton` / `%CustomRunButton` 同理，都在 `%ButtonContainer` 下）。
所以 `Patches/HostSubmenu.cs` 里三处 `"StandardButton"` / `"DailyButton"` / `"CustomRunButton"`
需要加 `%` 前缀。后果只是「LAN 调试建房」按钮注入不出来，不影响游戏大厅联机。

### 3.3 `TreasurePatches` 的投票复刻在 0.113 上**不需要**（实测，不是 bug）

这条请**不要**当成缺陷：0.113 原生 `TreasureRoomRelicSynchronizer.OnPicked(Player, int?)` 已经支持
「跳过」票，0.6.3 自己就 guard 住了：

```
[INFO] sts2_lan_connect gameplay: treasure native nullable skip detected; legacy vote patches are not required.
[INFO] sts2_lan_connect gameplay: treasure patches applied=9, skipped=2, failed=0.
```

我们在自己的外部补丁里也验证了这一点：0.113 上**只需要**让 `RelicPickingResult` 那段能编译
（补 `relicIndex`），**不要**去重写投票逻辑——重写版和原生在「安慰奖配对」和「全员跳过时的结果集」
上可观测不一致。

## 4. 我们这边的规避（供参考，不必照抄）

我们做了一个**独立的运行期补丁模组**（不改你们的仓库、不重新编译你们的 DLL），用 Harmony 接管被破坏的
异步存根与调用点，让 0.6.3 在 0.113 上继续可用。这条路能跑，但**明显不如源码移植干净**：
断点 3/4/6 得靠 IL 重定向绕过，断点 1/2 得整个接管 `BeginAsync`。

**所以如果你们愿意收，我们更希望提源码级修复，而不是让大家装第二个模组。** 具体改动范围：

1. `LanConnectLobbyManagedJoinFlow`：模式改从 join/load/rejoin 响应取（`JoinResult.gameMode` +
   `isCustom`、`SerializableRun.GameMode` + `IsCustomGame`），`maxAscensionUnlocked` 换成
   `maxStandardAscension` + `maxBlitzAscension`。
2. `LanConnectLobbyJoinFlow.PushJoinedScreen`：先判 `isCustom`，再按 `Standard/Daily/Blitz/Dread` 分派
   （与 `NJoinFriendScreen.JoinGameAsync` 同序）。
3. 模组自有的三种建房模式改用已有的 `LanConnectLanHostMode` 枚举，不再借 `GameMode.Custom`。
4. `LanConnectSerializationPatches.SerializeJoinResponseBody`：补写 `gameMode`（3 bit）+
   `isCustom`（1 bit），否则**桌面房主**发出去的 `ClientLobbyJoinResponseMessage` 会短两个字段，
   接收方越界。
5. `StartRunLobby` 的反射构造改成 5 参签名 + 旧 4 参回退。
6. `DifficultyScalingPatches` 的 `using`、`TreasurePatches` 的 `relicIndex`。

**wire 层我们一行没动**（字段顺序、`tail` 帧格式、lobby 的 HTTP/WS DTO 都没碰），
所以同一个游戏版本上，打过补丁的一端和未打过的一端仍然能互连。

## 5. 给作者的两个问题

1. 0.113 的 `InitialGameInfoMessage` 只剩 `sessionState` + `connectionFailureReason`，而版本 /
   ModelDb 哈希 / Mod 列表的校验搬到了传输层 `HandshakeManager`（`PeerVersionInfo.LocalDefault()`）。
   你们原本靠 `InitialGameInfoMessage` 做的「无元数据降级」在 0.113 上会一直走到降级分支——
   这是**有意为之**，还是需要改成读 `PeerVersionInfo`？
2. `GameMode.Custom` 变成 `isCustom` 布尔之后，模组自己的「自定义房」语义要不要跟着改成
   `StartRunLobby(gameMode, isCustom: true, ...)`（而不是继续把 `custom` 当成一种 `GameMode`）？
   我们按「保留 `LanConnectLanHostMode` 三态、下发时映射成 `isCustom`」处理，
   想确认这是不是你们想要的方向。

## 6. 附

- 完整编译错误清单（16 条）与逐条改法我们可以整理成 PR；但**请先告诉我们上面两个问题的方向**，
  免得白改。
- 我们只在 Android 侧做了真机验证，**PC 侧未测**，PR 正文里会如实写明。
````

---

## 8. 未确证项 / 风险（发之前必须逐条处理）

| # | 事项 | 状态 | 怎么补 |
|---|---|---|---|
| 1 | 上游远端最新状态（是否已有 0.113 分支/提交） | **未确证**：`git fetch` 失败（代理 127.0.0.1:7890 不通） | 通网后 `git fetch origin && git log origin/main --oneline -20`，再决定要不要提 |
| 2 | issue 号 `#44` | **未确证**：只有 `WS\reports\lobby-port-v113.md:101` 一处记载 | 打开仓库 Issues 页确认；若不对，按标题「0.113 加入崩溃」搜 |
| 3 | `HostSubmenuPatches` 失败在 0.111 上是否也存在 | **未确证**：手上没有 0.111 的游戏反编译（只有 `DEC113` 与 `work\lead\sts2-latest`，两者都是 0.113） | 找 0.111 的 `sts2.dll` 反编译 `NMultiplayerHostSubmenu`，确认 0.111 用的是相对路径还是 `%` |
| 4 | 12 文件改动能否**干净应用**到 216e9ad | **不成立**：`dist` 里的 patch 只有 11 个 diff，缺 `LanConnectReceiveDiagnosticsPatches.cs` 的新文件段 | PR 前用 `git diff` 重新生成，或撤掉诊断注册 |
| 5 | PC 端（Windows）行为 | **未测** | 按 `lobby-port-v113.md:184-188` 的三项跑完 |
| 6 | 0.113 上「开局进战斗」是否正常 | **未验证**：`LobbyBeginRunMessage` 与玩家列表位宽要到开局才用到（`lobby-port-v113.md:186-187`） | 同上 |

## 9. 一句话回答用户

**「提 PR」这个说法对那份 12 文件改动是准确的（那就是上游自己的代码）；对独立附属模组不准确
（它不该进上游仓库）。本轮先在原 issue 下补评论 —— 因为我们只在 Android 侧验过、产物还缺文件、
也还没确认上游是否已经自己修了；等 PC 回归跑完、`StandardButton` 补上、缺的文件补齐，
再把那份改动拆成 5 个提交提 PR。**

