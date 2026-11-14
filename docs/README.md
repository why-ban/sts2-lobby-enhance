# 游戏大厅增强 v1.7.0（sts2_lobby_enhance）

独立模组 `sts2_lobby_enhance`（**v1.7.0**）—— 让官方模组 `sts2_lan_connect` 0.6.3 在
《Slay the Spire 2》0.113.0 上继续可用，并补上 0.113 新增的极速（Blitz）/ 恐惧（Dread）模式的联机支持。

发布包按 `dist/sts2-lobby-enhance-v1.7.0.zip` 命名（打包步骤见 [`UPLOAD.md`](UPLOAD.md)）。
注意发布件**不在**自动化校验范围内：`tests/` 不读 `dist/`，打包后必须自行比对包内 DLL 与
`packaging/content/` 的 sha256。本次改动见 [`CHANGELOG.md`](CHANGELOG.md)。

**中文** | [English](#english)

---

## 中文

### 这个补丁包含三类内容

| 类别 | 数量 | 说明 |
|------|------|------|
| 兼容修复 · 元数据断点 | **6 个断点 / 4 项修复** | 0.113 改了成员签名或删了字段，官方 0.6.3 的引用直接解析不了（#5 不需要修复，见下） |
| 兼容修复 · 0.113 新模式 | **1 项** | 客户端加入极速 / 恐惧房间时，官方模组的推屏逻辑认不出新模式 |
| 便利功能 | **4 项** | 局终新开一局、局中重开本层、极速 / 恐惧建房、解锁这两个模式 |

合计 **10 项修复**，全部在启动日志里逐条可见（见「验证是否生效」）。

### 兼容修复：6 个元数据断点

0.113 改动了 6 个成员的签名或存在性。官方 0.6.3 是按 0.111 的形态编译的，这 6 处引用在新版游戏里
**无法解析**：方法既不能被 JIT，Harmony 连它的 IL 都读不出来（解析元数据 token 时直接抛异常），所以
不能简单地给这些方法打补丁，必须绕到它们可读的调用者或异步存根上去接管。

| # | 断点 | 0.111（官方 0.6.3 编译时） | 0.113（实测） |
|---|------|---------------------------|---------------|
| 1 | `InitialGameInfoMessage.gameMode` | 字段存在 | 字段已删除 |
| 2 | `ClientLobbyJoinRequestMessage.maxAscensionUnlocked` | 字段存在 | 拆成 `maxStandardAscension` + `maxBlitzAscension` |
| 3 | `NetHostGameService.DisconnectClient` | `(ulong, NetError, bool)` | `(ulong, NetError, bool, string debugReason)` |
| 4 | `NetHostGameService.ConnectedPeers` | `List<NetClientData>` | `IReadOnlyList<NetClientData>` |
| 5 | `RelicPickingResult.GenerateRelicFight` | 3 个参数 | 多了 `int relicIndex`，共 4 个参数 |
| 6 | `SaveManager.UpdateProgressWithRunData` | `void` | 返回 `PostRunProgressUpdateResult` |

> **断点 #5 不需要修复。** 0.113 原生的 `TreasureRoomRelicSynchronizer.OnPicked(Player, int?)` 已完整支持
> 「跳过」票（`TreasureRoomRelicSynchronizer.cs:204-251`、`:253-334`），官方 0.6.3 在 0.113 上自己就跳过了
> 那条补丁（`dec-official/TreasurePatches.cs:122-127`、`:137-140`、`:163-167`），所以
> `RelicPickingResult.GenerateRelicFight` 那条不可解析引用永远不会被 JIT。v1.5.0 曾在这里放一份复刻实现，
> 现已移除（复刻与原生在安慰奖配对、全员跳过时的结果集上有可观测差异），依据见
> [`CHANGELOG.md`](CHANGELOG.md) 的 `[Unreleased]` 一节。
> 启动探测仍把这一项算作判据（`Core/CompatProbe.cs:62-74`），所以上表仍是 6 项。
上表不是抄来的结论：`tests/` 里的 `BreakpointRegressionTests` 扫描官方 DLL 的 22,774 个元数据候选，
断言"恰好这 6 个无法解析"；`GameApiShapeTests` 则直接读 `sts2.dll` 的元数据，断言每个断点的新旧形态。

### 十项修复清单

| # | id | 类别 | 解决什么 | 用户看到什么 |
|---|----|------|----------|--------------|
| 1 | `join-flow` | 断点 #1 #2 | 被破坏的代码在 `LanConnectLobbyManagedJoinFlow.<BeginAsync>d__23.MoveNext` 里。给**异步存根** `BeginAsync` 加 Prefix 并返回 `false`，使状态机永远不会被编译；改为调用游戏自带的 `JoinFlow`，并把官方模组自己的网络服务工厂、`CancelToken` 与 tail_v1 绑定都接回去 | 客机能加入房间 |
| 2 | `join-screen-modes` | 0.113 新模式 | 官方 `LanConnectLobbyJoinFlow.PushJoinedScreen` 的 switch 只认 Standard / Daily / Custom，0.113 新增的极速 / 恐惧会落到末尾的 `throw new ArgumentOutOfRangeException("Unhandled multiplayer game mode.")`。接管该方法，按 `GameMode` 推 `NBlitzScreen` / `NDreadScreen`（自定义局优先判，保持原分派顺序） | 加入极速 / 恐惧房间不再弹 InternalError |
| 3 | `host-wire` | 断点 #1 同源报文 | 桌面房主有一条手工拼消息体的路径 `LanConnectSerializationPatches.SerializeJoinResponseBody`，按 0.111 布局写出 `ClientLobbyJoinResponseMessage`，缺 0.113 新增的 `gameMode`(3 bit) 与 `isCustom`(1 bit)。按 0.113 布局重写，列表位宽仍取自官方模组自己的档位逻辑 | 桌面房主建房后，客机不再错位/报 `IndexOutOfRangeException` |
| 4 | `tail-error-paths` | 断点 #3 #4 | `LanConnectTailMessageRuntime` 的拒绝 / 中止路径引用了改签名的两个 API，自身不可读。把调用点 IL 重定向到按 0.113 签名重写的方法，行为逐行对齐上游 | 踢人 / 拒绝 / 断线不再崩 |
| 5 | `abandon-run` | 断点 #6 | 被破坏的是 `LanConnectMultiplayerSaveCompatibility.<AbandonCurrentRunAsync>d__14.MoveNext`。同样接管异步存根，按 0.113 签名重跑同一流程（确认框、备份、进度写入、历史记录、每日分数上传、删存档、房间绑定清理、按钮刷新）；新的返回值会被检查，写入失败会记日志 | "备份并放弃本局"正常完成 |
| 6 | `new-run-button` | 功能 | 局终结算页由房主一键把全房带回准备页，见下节 | 不用重建房间、不用重新找队友 |
| 7 | `mid-run-restart` | 功能 | 局中回到本层开头，见下节 | 打错了不用退房重开 |
| 8 | `extra-game-modes` | 功能 | 建房选择器新增极速 / 恐惧；大厅房间卡片也像「自定义」一样标注「极速」「恐惧」，见下节 | 大厅能选这两个模式，房间列表里也看得出类型 |
| 9 | `unlock-extra-modes` | 功能 | 装补丁即可使用这两个模式，见下节 | 不必先打完时间线 |

### 功能详解

#### 局终「新开一局」（`new-run-button`）

一局结束（胜利或死亡）后，房主在结算页点「新开一局」，全房回到对应模式的准备页：**标准 / 每日 /
自定义 / 极速 / 恐惧** 五种模式。全程复用同一条连接，不重建房间。

- **客户端结算页提示**：队友侧会显示一行说明 —— 房主装了补丁时是「房主可以直接新开一局，无需退出
  游戏 —— 请稍候」；确认房主没装时是「房主未安装兼容补丁，本局结束后需退出并重新加入房间」；
  尚未确认时是「若房主安装了兼容补丁，可直接新开一局 —— 请稍候；否则请退出后重新加入」。
- **未安装本补丁的队友**会留在结算页，房主侧显示准确人数（只邀请装了补丁的已连接队友）。
- **不会留一个"点了没反应"的按钮**：会话已断开时根本不注入按钮，原位置改为显示「联机会话已断开，
  无法新开一局」；按钮已经出现而会话在结算页停留期间断开，会立刻把按钮收回（`NClickableControl.Disable()`
  → `OnDisable()` → `Visible = false`）。
- **任何拒绝都会说明原因**：本局尚未结束 / 游戏尚未就绪 / 队友都没装补丁，各自弹窗给出一句人话，
  不再静默失败。

#### 局中「重开本层」（`mid-run-restart`）

接管房间管理里的「重开一局」：局**未结束**时，把整房退回**当前楼层开头**继续打，不需要逐人确认，
不重建房间、不重新加入。

- 存档本来就是"本层开头"的状态（游戏在进房间前写 `current_run_mp.save`），所以本功能**只读存档**，
  不删存档、不删房间绑定。
- 游戏自己的「有队友未归队」确认框保留：缺人开局会让那个角色无人控制。
- **v1.5 起有前置探针**：拆局前（房主）与发 `LoadJoinRequest` 前（客户端）各检查一次 tail_v1 绑定，
  没绑定就立刻报错 + 弹窗，不再空等 2000ms / 15s 才失败。

#### 极速 / 恐惧模式建房（`extra-game-modes`）

大厅建房下拉框新增这两项。

大厅**房间卡片**也会标注模式。官方 0.6.3 的 `LanConnectLobbyOverlay.GetRoomGameModePill` 只认
`daily` 与 `custom`，其余一律显示 `STD`，所以这两种新模式的房间在列表里看不出类型。补丁给该方法加了
postfix：`blitz` → 「极速」（绿框）、`dread` → 「恐惧」（红框），其它输入原样返回。选中房间的摘要与
无障碍播报走的是同一个方法，因此一并生效。

技术注记：官方 0.6.3 编译时 `GameMode` 只有 4 个值，枚举成员被内联成常量；0.113 的枚举顺序变了
（`3` 现在是 Dread、`4` 是 Blitz）。补丁因此用 `4` → Blitz（正好也是游戏自己的值）、`5` → Dread
（哨兵值，只在本补丁接管的那几个方法之间流转；推给 `NDreadScreen` 时由该屏自己提供真正的
`GameMode.Dread`，哨兵不会进入游戏引擎）。

#### 解锁极速 / 恐惧模式（`unlock-extra-modes`）

装补丁即可使用这两个模式，不需要先打完时间线。

两处**只读**接缝：`SaveManager.GenerateUnlockStateFromProgress` 的 postfix（决定向房主上报什么解锁状态）
与 `ProgressState.IsEpochRevealed` 的 postfix（菜单按钮的闸门）。补丁**不写存档**：不碰
`ProgressState.Epochs`，只改变"读出来什么"和"上报什么"。

### ⚠ 公平性说明（请务必阅读）

解锁极速 / 恐惧模式是**真实的进度捷径，不只是界面解锁**。

游戏的 `GameModeHelper.AreAchievementsAndEpochsLocked` 只把「每日挑战」和「自定义局」排除在成就与
时间线进度之外，**极速与恐惧局完全计入**。因此：

- 在这两个模式里打完一局，**可以正常获得成就与时间线 epoch**，与正常解锁后的行为一致；
- 补丁**不修改进度存档**、不伪造「已获得」、不碰 `TotalUnlocks`；
- **本局存档**里的 `player.unlockState` 会带上这两个 id —— 这是游戏既有机制（本局各玩家解锁集合取并集），
  不是本补丁新增的写盘点。

介意提前获得这部分进度的玩家，请不要使用解锁功能，或等正常解锁后再用。

### 安装

**方式一：Steam 创意工坊（推荐）**

工坊条目：[**3839101755**](https://steamcommunity.com/sharedfiles/filedetails/?id=3839101755) —— 订阅即可。原模组 `sts2_lan_connect`（工坊物品 3749766330）
已声明为必需物品，Steam 会一并订阅。

> 安装或联机遇到问题，可以加本模组的 QQ 群 **1135970470**。

**方式二：手动安装**

1. 确认官方模组已安装（工坊订阅，或放在 `<游戏目录>\mods\sts2_lan_connect\`）。
2. 新建目录 `<游戏目录>\mods\sts2_lobby_enhance\`，把下面两个文件放进去：
   - `sts2_lobby_enhance.dll`
   - `sts2_lobby_enhance.json`

`<游戏目录>` 指 Steam 库中的 `steamapps\common\Slay the Spire 2`。模组清单必须与 DLL 同目录，
否则游戏不会把它识别为模组。

> **联机双方都要安装本补丁。** 房主校验的是加入方**自己上报**的解锁状态，只装一边不够；
> 局终「新开一局」也只把装了补丁的已连接队友带回准备页。

### 验证是否生效

启动游戏后，在日志里搜 `[lobby-enhance]`。0.113 上的正常输出形如：

```
[lobby-enhance] game probe: breaks[gameModeGone=True,maxAscensionUnlockedGone=True,disconnectClient4=True,
          connectedPeersIReadOnly=True,generateRelicFight4=True,updateProgressReturns=True]
[lobby-enhance] host mod: sts2_lan_connect 0.6.3.0 @ ...\sts2_lan_connect.dll
[lobby-enhance] applied: join-flow
[lobby-enhance] applied: join-screen-modes
[lobby-enhance] applied: host-wire
[lobby-enhance] applied: tail-error-paths
[lobby-enhance] applied: abandon-run
[lobby-enhance] applied: new-run-button
[lobby-enhance] applied: mid-run-restart
[lobby-enhance] applied: extra-game-modes
[lobby-enhance] applied: room-card-pill
[lobby-enhance] applied: unlock-extra-modes
[lobby-enhance] summary: applied=10[join-flow,join-screen-modes,host-wire,tail-error-paths,
          abandon-run,new-run-button,mid-run-restart,extra-game-modes,room-card-pill,
          unlock-extra-modes] skipped=0[] failed=0
```

判定标准：

- `applied=10 ... failed=0` → 10 项修复全部装上。
- `skipped=<n>` → 有 n 项判断为「这个构建不需要」，每一项都会单独打印原因
  （形如 `skipped: <id>: missing: <具体锚点>`）。
- 出现 `no repair reported itself as needed` → 这个构建不需要任何修复，什么都没装（正常状态）。
- 出现 `host mod 'sts2_lan_connect' is not loaded` → 官方模组没装或没被加载。
- 出现 `FAILED:` → 某项修复没装上；**只撤销它所在的那一组**，其它组照常生效。请把日志发给作者。

### 不启动游戏的验证

仓库自带几个不需要开游戏的控制台工具（需要 .NET 9 SDK 与已安装的游戏）：

```powershell
# 1) 只校验 6 个元数据断点的判定结果（不安装任何补丁）
dotnet run --project tools/probe-check -c Release

# 2) 跑 Entry.RunSilently()，确认 10 项修复真的挂上了
dotnet run --project tools/smoke-test -c Release

# 3) 补丁依赖的每一个上游反射成员（30 条契约）
dotnet run --project tools/contract-check -c Release
```

`smoke-test` 断言三件事：① `failed` 为空且 join 接管已挂上；② `applied` 的 **id 集合与顺序**等于内置清单
（`tools/smoke-test/Program.cs:187-206`）；③ **每一项修复至少挂上一处真实 Harmony 接缝**（`:243-264`），
只报 `applied` 却没接缝同样判红。所以日志里有 `applied: xxx` 不等于生效，判定要看这两组断言。
它不触碰 MegaCrit 的 `Log`（在非 Godot 宿主里会 0xC0000005 崩溃），因此可以安全地在命令行里跑。

### 回归测试

```powershell
dotnet test tests
```

**61 个测试 / 9 个测试类**（本机 Release 实跑：通过 61 / 失败 0 / 跳过 0），覆盖八类事实：

1. **断点回归**：官方 DLL 恰好 6 处引用解析不了，名称、token、表都与文档一致；360 条
   `TypeSpecification` 噪声被逐条证明是探针缺泛型实参导致的假阳性（成员在声明类型上确实存在），
   不是"过滤掉了看不见的东西"。
2. **游戏侧形态**：直接读 `sts2.dll` 元数据，断言每个断点的新旧签名，以及补丁依赖的替换 API
   （`ClientLobbyJoinResponseMessage.gameMode/isCustom`、`PostRunProgressUpdateResult`、`JoinFlow.Begin`）
   都还在。
3. **上游内部契约**：`TailRosterContractTests`（4 个用例）把 `TailRoster` 反射用到的 5 个上游锚点
   （`LanConnectRosterAuthorityState.Reset`、`LanConnectTailMessageRuntime.Shared` / `GetMessageBus` /
   `_bindings`、`Binding.RequireRoster`）钉在 0.6.3 二进制上：上游一改名，roster 复位只会静默返回
   false，在运行时表现为协议错位，所以必须让它在测试里变红。
4. **产物自身**：本补丁的 DLL 在 0.113 上零无法解析引用，并且带 `[ModInitializer("Init")]`；
   结算页注入的 UI 另有布局几何断言（两视口 × 六种矩形 × 两种形态，断言按钮/说明/提示互不相交且不越界）。
5. **发布工作区**：`packaging/` 结构符合官方上传器要求，`image.png` 是 <1 MiB 的正方形 PNG，
   `content/` 只有 4 个文件、绝不含官方模组的任何文件，且其中的 DLL 与当前构建逐字节一致。
6. **修复清单**：`FixRegistryTests`（4 个用例）断言 `Entry.cs` 里 `CompatFixes.Build()` 产出的 id
   **集合与顺序**就是那 10 项 —— 少写一行、改名或换序都必须有人同时改断言；另外单独覆盖
   `join-screen-modes` 的分派、`unlock-extra-modes` 的两个 epoch 与「不写盘」、`mid-run-restart` 的反射锚点。
7. **反假绿闸门**：`GameDependentCoverageTests`（1 个用例）检查「依赖游戏 DLL 的那些用例真的跑过了」——
   22 个用例在缺输入时会静默跳过，这条断言让「全绿但其实没跑」变成红色（可用
   `STS2_ALLOW_MISSING_GAME_INPUTS=1` 显式降级为跳过）。

游戏目录、官方模组 DLL、构建产物不存在时，相关用例会**跳过**并在输出里说明原因；但
`GameDependentCoverageTests` 会因为「该跑的没跑」判红 —— 这正是反假绿的那道闸门。开发者本机没有游戏时，
用 `STS2_ALLOW_MISSING_GAME_INPUTS=1` 显式降级为跳过（跳过信息会列出缺的每一项输入）。

### 已知限制

- **只针对 0.113.0 验证。** 开发与验证基于 0.113.0（`release_info.json` 中 commit `16ac0fe3`，
  2026-11-12）。其它版本没有验证过。
- **没有「总开关」，每项自己判断。** v1.8.0 起不再有「6 个断点全中才生效」的全局门槛：每项修复在
  `Applies(in CompatSnapshot, out string reason)` 里自己解析锚点（类型 / 方法 / 参数类型逐个比对），
  缺一个只跳过它自己，并在日志里打印 `skipped: <id>: missing: <具体锚点>`。
  所以将来游戏改了某个签名，**受影响的那几项会明确跳过，其余照常工作**：不会整模组失活，也不会出现
  「日志说 applied、功能其实没有」。
- **每项失败只回滚自己那一组。** 九个 Harmony id（`sts2_lobby_enhance.<组名>`）互相独立，
  `UnpatchAll` 只作用于出问题的那一组。
- **依赖官方模组 ≥ 0.6.3。** 未安装时补丁静默不生效（只留一条警告日志）。
- **联机双方都要安装本补丁。** 房主校验加入方上报的解锁状态；只装一边会在加入或新开一局时失败。
- **局终「新开一局」要求至少一名已连接队友装了补丁**，否则不显示按钮；未安装者会留在结算页。
- **自定义局里把大厅模式切成极速 / 恐惧的下拉框仍不提供这两项。** 官方那条路径用
  `new UnlockState(SaveManager.Instance.Progress)` 直接读存档，绕过了本补丁的两个接缝；直接建房时
  选极速 / 恐惧不受影响。
- **加入被拒时的提示会退化成笼统文案。** join 接管整体跳过了上游的四道校验，其中只剩「房主声明的失败原因」
  那道还活着（另外三道在 0.113 上是死分支）。所以满员房 / 已开局房 / 存档里没有你时，看到的是
  `Unexpectedly disconnected from host while joining. Reason: LobbyFull`，而不是游戏原本的「大厅已满」提示，
  并且会多一次无效往返。详见 [`research/join-order-bug.md`](research/join-order-bug.md) §8。
- **解锁极速 / 恐惧会顺带影响时间线导航。** 游戏把这两个 epoch 当「已揭示」，时间线的上一章 / 下一章跳页
  会经过它们。属副作用，未修。
- **日志里可能出现 `ERROR: System.InvalidOperationException` 而联机其实正常。** 上游
  `OnHostedControlEnvelope` 的 catch 过滤器只接 `InvalidDataException` / `LanConnectProtocolException`
  （部署版 `LanConnectLobbyRuntime.cs:2324`），而「协议未就绪」抛的是 `InvalidOperationException`，
  会逃逸成未观察的 Task 异常。不影响联机，但会干扰排障。
- **tail_v1 绑定在异常路径上仍可能超时**（正常联机不受影响；局中重开已加前置探针，不会再空等）。
- **平台范围**：已在 Windows 桌面版与 Android 移动端（第三方启动器）双机联机验证；其它平台或移植层
  未测试。
- **联机行为需要人工验证。** 自动化测试只覆盖静态元数据、进程内的补丁安装与 UI 几何；两人以上的
  实际联机请按下面的清单走一遍。

### 实机验证清单（双人，建议每次发布前做）

1. A 建房、B 加入：B 能进大厅，双方看到彼此的名字与角色。
2. 宝物室：两人各选一件遗物 → 有人选"跳过" → 结算不崩；遗物分配走 0.113 原生逻辑（本补丁已不再接管这段）。
3. 放弃本局：A 点"备份并放弃本局" → 确认框、备份、进度写入、存档删除全部完成，主菜单按钮恢复。
4. 房主主动踢人 / 网络中断：不崩溃，只断开对应 peer（这条走 `tail-error-paths`）。
5. 极速 / 恐惧：A 建房选这两个模式，B 加入 → 不再弹 InternalError，双方进入对应准备页。
6. 局中重开：打到第二层中途 → 房主点「重开一局」→ 全房回到本层开头，无需逐人确认。
7. 局终新开一局：打完一局 → 结算页点「新开一局」→ 全房回到对应模式的准备页；
   同时确认客户端结算页显示了提示文案。
8. 双方日志都没有 `FAILED:`，且都有 `applied=10`。

### 构建与发布

```powershell
# 编译（需要已安装游戏；SkipModCopy 表示不往游戏目录里复制）
dotnet build src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true

# 刷新创意工坊工作区的 content/（会重新构建并复制 DLL、清单、许可证）
pwsh -File packaging/build-content.ps1

# 重新生成工坊预览图
python packaging/make-image.py
```

发布前跑一遍：`dotnet test tests`（61 通过）+ `tools/smoke-test`（10 项 applied）+
`tools/contract-check`（30 条契约）。

> **官方模组升级后必须重跑 `tools/wire-verify`。** `host-wire` 用 `Priority.First` 覆盖宿主自己的
> `SerializeJoinResponseBody`，而它的 `Applies` 只判「游戏是不是 0.113」，不判「宿主是不是已经自己修好了」；
> 宿主若改了报文布局，我们的覆盖可能把新字段写丢。`ascension` 的 5 bit 也是照抄官方的字面常量，
> 游戏改位宽时两边都要改。

上传流程见 [`UPLOAD.md`](UPLOAD.md)（含"三处版本号必须同时改"的说明）。

### 延伸阅读

`research/` 下**只保留仍在生效或仍未解决**的议题（索引见 [`research/README.md`](research/README.md)）。
设计与事故复盘的结论已经落在代码注释里（改那段代码就会看到），不再单独留档：

- [`research/join-order-bug.md`](research/join-order-bug.md)：**未修复** —— 多人存档续玩必须按原玩家顺序加入（阻塞在用户日志）。
- [`research/p1-crash-deep.md`](research/p1-crash-deep.md)：**未解决** —— 手机端闪退的触发条件与 (a)–(g) 验证矩阵。
- [`research/tail-profile.md`](research/tail-profile.md)：tail_v1 绑定与协议档位的决策记录（为什么不能强制降级 `compat`）。

### 许可

GPL-3.0，全文见 [`LICENSE`](LICENSE)（与上游 `STS2-Game-Lobby` 的 LICENSE 逐字节一致）。
本模组不打包官方模组的任何文件，但源码中含从该 GPL-3.0 项目移植的逻辑，因此整体以 GPL-3.0 发布。
第三方声明见 [`THIRD_PARTY_NOTICES`](THIRD_PARTY_NOTICES)。

本项目是社区补丁，与 MegaCrit Games 及官方模组作者无关，未获其背书。

---

## English

### What this is

An independent mod, `sts2_lobby_enhance` (**v1.7.0**), that keeps the official mod
`sts2_lan_connect` 0.6.3 working after Slay the Spire 2 0.113.0, and adds multiplayer support for the
two game modes 0.113 introduced: Blitz (极速) and Dread (恐惧).

The release package is named `dist/sts2-lobby-enhance-v1.7.0.zip` (packaging steps in
[`UPLOAD.md`](UPLOAD.md)). The archive is **not** covered by automation — no test reads `dist/` — so after
packaging you must compare the DLL inside it against `packaging/content/` by sha256 yourself. What changed:
[`CHANGELOG.md`](CHANGELOG.md).

### Three kinds of content

| Kind | Count | What it is |
|------|-------|------------|
| Compatibility · metadata breaks | **6 breaks / 4 fixes** | 0.113 changed signatures or removed fields the official build references (break #5 needs no fix, see below) |
| Compatibility · new game modes | **1 fix** | the official mod's post-join screen dispatch cannot handle Blitz / Dread |
| Convenience features | **4 fixes** | new run from game over, mid-run restart, hosting Blitz / Dread, unlocking those modes |

Ten fixes in total, each one visible in the startup log (see "Checking that it works").

### The six metadata breaks

0.113 changed six members the official build depends on. The old member references cannot be resolved
any more, which means those methods can never be JITted — and Harmony cannot even read their IL,
because resolving the metadata token throws before any patch is built. The only way to repair them is
to take over a *readable* caller or an async stub.

| # | Member | Compiled against 0.111 | Actual 0.113 |
|---|--------|------------------------|--------------|
| 1 | `InitialGameInfoMessage.gameMode` | field exists | removed |
| 2 | `ClientLobbyJoinRequestMessage.maxAscensionUnlocked` | field exists | replaced by `maxStandardAscension` + `maxBlitzAscension` |
| 3 | `NetHostGameService.DisconnectClient` | `(ulong, NetError, bool)` | `(ulong, NetError, bool, string debugReason)` |
| 4 | `NetHostGameService.ConnectedPeers` | `List<NetClientData>` | `IReadOnlyList<NetClientData>` |
| 5 | `RelicPickingResult.GenerateRelicFight` | 3 parameters | 4 parameters (`int relicIndex` added) |
| 6 | `SaveManager.UpdateProgressWithRunData` | `void` | returns `PostRunProgressUpdateResult` |

> **Break #5 needs no repair.** 0.113's own `TreasureRoomRelicSynchronizer.OnPicked(Player, int?)` already
> resolves "skip" votes completely (`TreasureRoomRelicSynchronizer.cs:204-251`, `:253-334`), and the official
> 0.6.3 build skips that patch on 0.113 by itself (`dec-official/TreasurePatches.cs:122-127`, `:137-140`,
> `:163-167`), so the unresolvable `RelicPickingResult.GenerateRelicFight` reference is never JITted. v1.5.0
> carried a replica here; it is gone (the replica and the native path differ observably in consolation-relic
> pairing and in the result set when everyone skips). See the `[Unreleased]` section of
> [`CHANGELOG.md`](CHANGELOG.md). The startup probe still counts
> this break among its criteria (`Core/CompatProbe.cs:62-74`), which is why the table above still lists six.

`BreakpointRegressionTests` scans 22,774 metadata candidates in the shipped DLL and asserts that
exactly these six fail to resolve; `GameApiShapeTests` reads the game's metadata directly and asserts
each old/new shape.

### The ten fixes

| # | id | Kind | What it solves | What the player sees |
|---|----|------|----------------|----------------------|
| 1 | `join-flow` | breaks #1 #2 | The broken code is inside `LanConnectLobbyManagedJoinFlow.<BeginAsync>d__23.MoveNext`. A Prefix on the **async stub** returns `false`, so the state machine is never compiled; the game's own `JoinFlow` runs instead, with the host mod's net-service factory, cancellation token and tail_v1 binding wired back in | Clients can join |
| 2 | `join-screen-modes` | 0.113 modes | The official `LanConnectLobbyJoinFlow.PushJoinedScreen` only knows Standard / Daily / Custom and throws `ArgumentOutOfRangeException("Unhandled multiplayer game mode.")` for Blitz / Dread. The patch takes the method over and pushes `NBlitzScreen` / `NDreadScreen`, keeping custom-first dispatch | Joining a Blitz / Dread room no longer shows InternalError |
| 3 | `host-wire` | break #1's message | On desktop the host mod serialises `ClientLobbyJoinResponseMessage` by hand for 0.111, omitting the two fields 0.113 added (`gameMode`, `isCustom`). The patch emits the 0.113 layout, still taking the list bit width from the host mod's own profile logic | Desktop hosts no longer desync their clients |
| 4 | `tail-error-paths` | breaks #3 #4 | The tail_v1 reject/abort paths reference the two changed APIs and are unreadable. Their call sites are redirected at IL level to faithful ports written against the 0.113 signatures | Kicking / rejecting / disconnecting no longer crashes |
| 5 | `abandon-run` | break #6 | The broken call is inside `LanConnectMultiplayerSaveCompatibility.<AbandonCurrentRunAsync>d__14.MoveNext`. The async stub is taken over and the same flow is replayed against the 0.113 signature; the new return value is inspected and a failed write is logged | "Back up and abandon" completes |
| 6 | `new-run-button` | feature | One click on the game-over screen brings the whole room back to the lobby | No re-hosting, no re-joining |
| 7 | `mid-run-restart` | feature | Restart the current floor mid-run | No need to leave and re-create the room |
| 8 | `extra-game-modes` | feature | Blitz / Dread in the create-room picker, plus a 「极速」/「恐惧」 pill on each room card (the host mod only knows standard / daily / custom) | The lobby can pick them, and the room list shows which mode a room is |
| 9 | `unlock-extra-modes` | feature | Both modes usable as soon as the patch is installed | No need to finish the timeline first |

### Feature details

#### New run from the game-over screen (`new-run-button`)

After a run ends (win or death) the host presses "new run" and the whole room returns to the lobby for
the same mode: **standard / daily / custom / Blitz / Dread**. The same connection is reused; nobody
re-hosts or re-joins.

- **Client-side hint**: the teammate's game-over screen shows one line — "房主可以直接新开一局，无需退出
  游戏 —— 请稍候" when the host is known to have the patch, "房主未安装兼容补丁，本局结束后需退出并重新
  加入房间" when the host is known not to, and a neutral wording while it is still unknown.
- **Unpatched teammates** stay on the game-over screen; the host sees the accurate player count (only
  patched, still-connected peers are admitted).
- **No dead button**: if the session is already gone, the button is not injected at all and the slot
  shows "联机会话已断开，无法新开一局" instead. If the session drops while the screen is up, the button is
  withdrawn immediately (`NClickableControl.Disable()` → `OnDisable()` → `Visible = false`).
- **Every refusal explains itself** with a popup: run not finished, game not ready, or no teammate has
  the patch.

#### Mid-run restart of the current floor (`mid-run-restart`)

Takes over the room-management "restart" action: while the run is still going, the whole room goes back
to the **start of the current floor**, with no per-player confirmation and no re-hosting.

- The save already means "start of this floor" (the game writes `current_run_mp.save` before entering a
  room), so this feature only **reads** the save — it deletes neither the save nor the room binding.
- The game's own "players are missing" confirmation is kept on purpose: starting without a player
  leaves their character uncontrolled.
- **v1.5 adds a pre-flight probe**: before tearing the run down (host) and before sending
  `LoadJoinRequest` (client) the patch checks the tail_v1 binding and fails immediately with a popup
  instead of waiting out 2000 ms / 15 s.

#### Hosting Blitz / Dread (`extra-game-modes`)

The create-room picker gains both modes, and each **room card** in the lobby list now carries a mode pill.
The shipped 0.6.3 `LanConnectLobbyOverlay.GetRoomGameModePill` only knows `daily` and `custom` and
prints `STD` for everything else, so Blitz / Dread rooms were indistinguishable in the list. A postfix
maps `blitz` → 「极速」 (green border) and `dread` → 「恐惧」 (red border), leaving every other input
untouched; the selected-room summary and the accessibility announcement go through the same method.

Technical note: the shipped 0.6.3 build was compiled when
`GameMode` had four values, and enum members are inlined as constants; 0.113 reordered the enum (`3` is
now Dread, `4` is Blitz). The patch therefore uses `4` → Blitz (also the game's own value) and `5` →
Dread (a sentinel that only travels between the methods this patch owns; `NDreadScreen` supplies the
real `GameMode.Dread` itself, so the sentinel never reaches the engine).

#### Unlocking Blitz / Dread (`unlock-extra-modes`)

Both modes become usable as soon as the patch is installed, without finishing the timeline. Two
**read-only** seams: a postfix on `SaveManager.GenerateUnlockStateFromProgress` (what we report to the
host) and a postfix on `ProgressState.IsEpochRevealed` (the menu gates). The patch does **not** write to
the save: it never touches `ProgressState.Epochs`, it only changes what a read returns and what we
report.

### ⚠ Fairness notice (please read)

Unlocking Blitz / Dread is a **real progression shortcut, not just a UI unlock**.

The game's `GameModeHelper.AreAchievementsAndEpochsLocked` only excludes the daily challenge and custom
runs from achievements and timeline progress; **Blitz and Dread runs count in full**. Therefore:

- a run finished in these modes **earns achievements and timeline epochs** exactly as it would after a
  normal unlock;
- the patch **does not modify the progress save**, does not fake "obtained", and never touches
  `TotalUnlocks`;
- the **run save's** `player.unlockState` will contain the two ids — that is the game's existing
  behaviour (the union of each player's unlock set), not a new write site introduced here.

If you would rather earn that progress normally, do not use the unlock feature, or wait until the modes
are unlocked legitimately.

### Installation

**Steam Workshop (recommended):** subscribe to [workshop item 3839101755](https://steamcommunity.com/sharedfiles/filedetails/?id=3839101755). The original mod
(workshop item 3749766330) is declared as a required item, so Steam installs it too.

**Manual:** create `<game>\mods\sts2_lobby_enhance\` and put
`sts2_lobby_enhance.dll` and `sts2_lobby_enhance.json` in it. The official mod must also
be installed (workshop, or `<game>\mods\sts2_lan_connect\`).

> **Both sides need this patch.** The host validates the unlock state a joining client reports, and the
> new-run button only brings patched, still-connected teammates back to the lobby.

### Checking that it works

Search the game log for `[lobby-enhance]`. On 0.113 the expected summary line is:

```
[lobby-enhance] summary: applied=10[join-flow,join-screen-modes,host-wire,tail-error-paths,
          abandon-run,new-run-button,mid-run-restart,extra-game-modes,room-card-pill,
          unlock-extra-modes] skipped=0[] failed=0
```

- `applied=10 ... failed=0` — all ten fixes installed.
- `skipped=<n>` — n repairs decided this build does not need them; each prints its own reason
  (`skipped: <id>: missing: <anchor>`).
- `no repair reported itself as needed` — this build needs no repair at all; nothing was installed (normal).
- `host mod 'sts2_lan_connect' is not loaded` — the official mod is missing.
- `FAILED:` — a repair could not be installed; **only its own group** is unpatched, the others stay live.
  Send the log.

### Verifying without launching the game

```powershell
# 1) only the six-break detection, installs nothing
dotnet run --project tools/probe-check -c Release

# 2) runs Entry.RunSilently() and reports which repairs Harmony actually installed
dotnet run --project tools/smoke-test -c Release

# 3) every upstream reflection member the patch depends on (30 contracts)
dotnet run --project tools/contract-check -c Release
```

`smoke-test` asserts three things: the `failed` list is empty and the join takeover is installed; the
`applied` **id set and order** equal the built-in list (`tools/smoke-test/Program.cs:187-206`); and **every
fix really installed at least one Harmony seam** (`:243-264`) — reporting `applied` without a seam fails
too. So an `applied: xxx` line in the log is not proof of anything; these assertions are. It
never touches MegaCrit's `Log` class (which resolves `Godot.OS` in a static constructor and dies with
an access violation outside a Godot host), so it is safe to run from a terminal.

### Regression suite

```powershell
dotnet test tests
```

61 tests in nine classes (measured on this machine, Release: 61 passed / 0 failed / 0 skipped),
covering seven kinds of fact:

1. **Breakpoints** — the shipped DLL has exactly six unresolvable references, with the documented
   names, tables and metadata tokens; the 360 `TypeSpecification` references are individually proven to
   be probe artefacts, not silently filtered noise.
2. **Game shapes** — read straight from `sts2.dll` metadata: each old shape is gone, each new shape is
   present, and the replacement members the fixes write into still exist.
3. **Upstream internals** — `TailRosterContractTests` (4 cases) pins the five upstream anchors
   `TailRoster` reflects on (`LanConnectRosterAuthorityState.Reset`,
   `LanConnectTailMessageRuntime.Shared` / `GetMessageBus` / `_bindings`, `Binding.RequireRoster`) to the
   0.6.3 binary: if upstream renames one, the roster reset only returns false silently and shows up as a
   protocol mismatch at runtime, so it has to turn red here instead.
4. **The shipped artefact** — the patch DLL resolves completely against 0.113, carries
   `[ModInitializer("Init")]`, and the injected game-over layout has geometry assertions (two viewports
   × six rectangles × two layouts; button, caption and hint must not overlap or overflow).
5. **The release workspace** — `packaging/` matches what the uploader expects, `image.png` is a square
   PNG under 1 MiB, `content/` holds only this project's four files, and its DLL is byte-identical to
   the current build.
6. **The repair inventory** — `FixRegistryTests` (4 cases) pins the id **set and order** produced by
   `CompatFixes.Build()` in `Entry.cs` to those ten, so a repair cannot be added, removed, renamed or
   reordered without editing an expectation; it also covers `join-screen-modes` dispatch, the two
   `unlock-extra-modes` epochs and its "never writes to disk", and the `mid-run-restart` anchors.
7. **The anti-false-green gate** — `GameDependentCoverageTests` (1 case) checks that the game-dependent
   cases really ran. 22 of them skip silently when an input is missing, so without this gate "all green"
   can mean "never executed"; set `STS2_ALLOW_MISSING_GAME_INPUTS=1` to downgrade that to an explicit skip.

Missing inputs (game directory, official DLL, build output) make the affected cases *skip* with a reason,
but `GameDependentCoverageTests` then fails on purpose: "green" must not mean "never executed". Set
`STS2_ALLOW_MISSING_GAME_INPUTS=1` on a machine without the game to downgrade that to an explicit skip
(the skip message lists every missing input).

### Known limitations

- **Verified on 0.113.0 only** (commit `16ac0fe3`, 2026-11-12). Other builds are untested.
- **There is no global switch; every repair decides for itself.** Since v1.8.0 the "all six breaks
  present" gate is gone: each repair resolves its own anchors (types, methods, and exact parameter types)
  inside `Applies(in CompatSnapshot, out string reason)`. A missing anchor skips only that repair and logs
  `skipped: <id>: missing: <anchor>`, so a future build that changes one signature costs only the repairs
  touching it — not the whole mod, and never a log line that claims `applied` for a feature that is absent.
- **A failure rolls back only its own group.** The nine Harmony ids (`sts2_lobby_enhance.<group>`) are
  independent; `UnpatchAll` touches the failing group only.
- **Requires the official mod 0.6.3 or newer.** Without it the patch stays inert (one warning).
- **Both sides need the patch**; installing it on one side only fails at join time or at the new-run
  button.
- **The new-run button needs at least one connected patched teammate**, otherwise it is not shown;
  unpatched players stay on the game-over screen.
- **The custom-run lobby's mode dropdown still does not offer Blitz / Dread.** That path builds
  `new UnlockState(SaveManager.Instance.Progress)` directly and bypasses both seams. Hosting either mode
  directly is unaffected.
- **A rejected join degrades to a generic message.** The join takeover skips upstream's four validations;
  only "the reason the host declared" is still live (the other three are dead branches on 0.113). So a full
  lobby, a run already in progress, or a save that does not contain you now shows
  `Unexpectedly disconnected from host while joining. Reason: LobbyFull` instead of the game's own
  "lobby is full" popup, and costs one extra round trip. See
  [`research/join-order-bug.md`](research/join-order-bug.md) §8.
- **Unlocking Blitz / Dread also affects timeline navigation.** The game treats both epochs as revealed, so
  the timeline's previous/next-chapter paging passes through them. A side effect, not fixed.
- **`ERROR: System.InvalidOperationException` can appear in the log while multiplayer works fine.** Upstream's
  `OnHostedControlEnvelope` catch filter only takes `InvalidDataException` / `LanConnectProtocolException`
  (deployed `LanConnectLobbyRuntime.cs:2324`), while "protocol not ready" throws `InvalidOperationException`,
  which escapes as an unobserved task exception. Harmless for gameplay, misleading when reading logs.
- **tail_v1 binding can still time out on error paths** (normal play is unaffected; mid-run restart now
  probes first instead of waiting).
- **Platforms**: verified on the Windows desktop build and on an Android device through a third-party
  launcher; other platforms or ports are untested.
- **Multiplayer behaviour needs manual verification.** The automated suite covers static metadata,
  in-process patch installation and UI geometry, not two live clients.

### Manual verification checklist (two players, before every release)

1. A hosts, B joins: B reaches the lobby and both see each other's name and character.
2. Treasure room: both pick a relic, one picks "skip" — resolution must not crash; allocation follows the game's native logic (the patch no longer takes it over).
3. Abandon run: A uses "back up and abandon" — confirmation, backup, progress write, save deletion and
   the main-menu button refresh all complete.
4. Host kicks a client / the network drops: no crash, only that peer disconnects.
5. Blitz / Dread: A hosts one of them, B joins — no InternalError, both reach the matching lobby.
6. Mid-run restart: in the middle of floor 2, the host restarts — the whole room returns to the start of
   that floor with no per-player confirmation.
7. New run: finish a run, press "new run" on the game-over screen — the room returns to the lobby for
   the same mode, and the client-side hint is visible.
8. Neither log contains `FAILED:`, and both contain `applied=10`.

### Build and test

```powershell
dotnet build src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true
dotnet test tests
pwsh -File packaging/build-content.ps1
python packaging/make-image.py
```

Before a release run: `dotnet test tests` (61), `tools/smoke-test` (9 applied) and
`tools/contract-check` (30 contracts).

> **Re-run `tools/wire-verify` after every official-mod upgrade.** `host-wire` overwrites the host's own
> `SerializeJoinResponseBody` with `Priority.First`, and its `Applies` only asks "is the game 0.113" — not
> "has the host fixed it already". If the host changes the layout, our overwrite can drop the new fields.
> The `ascension` 5-bit width is a literal copied from upstream, so a game-side width change needs both sides
> updated.

Upload steps — including the "three version strings must change together" rule — are in
[`UPLOAD.md`](UPLOAD.md).

### Further reading

`research/` keeps only the topics that are **still in force or still unresolved** (index:
[`research/README.md`](research/README.md)). Design and post-mortem conclusions now live in code comments —
whoever touches that code sees them — instead of in separate records:

- [`research/join-order-bug.md`](research/join-order-bug.md) — **unfixed**: multiplayer save runs require joining in the original player order (blocked on user logs).
- [`research/p1-crash-deep.md`](research/p1-crash-deep.md) — **unresolved**: the phone-side crash and its (a)–(g) verification matrix.
- [`research/tail-profile.md`](research/tail-profile.md) — tail_v1 binding and protocol profiles (why forcing `compat` is not an option).

### Licence

GPL-3.0; see [`LICENSE`](LICENSE), which is byte-identical to the upstream `STS2-Game-Lobby` copy. No
file from the official mod is redistributed, but the source does contain logic ported from that
GPL-3.0 project, so this project is distributed under GPL-3.0 as a whole. See
[`THIRD_PARTY_NOTICES`](THIRD_PARTY_NOTICES).

Unofficial community patch. Not affiliated with, endorsed by, or supported by MegaCrit Games or the
STS2 LAN Connect author.
