# 游戏大厅增强 v2.0.0（sts2_lobby_enhance）

独立模组 `sts2_lobby_enhance`（**v2.0.0**）—— 配合社区模组 `sts2_lan_connect` **0.6.4** 使用，
为《Slay the Spire 2》**0.113** 的联机大厅补上它没有的几件事：局终一键新开一局、名册减员后的重基线、
读档加入不再挑顺序、建房不再让人二选一协议。

> **v2.0.0 是一次减法。** 上游 0.6.4 自己适配了 0.113 的加入流程、消息布局与 API 变化，
> 原来那 6 个元数据断点已经不存在，围绕它们写的 8 项修复（join-flow / join-screen-modes / host-wire /
> tail-error-paths / abandon-run / extra-game-modes / room-card-pill / unlock-extra-modes）全部删除。
> **本版不再提供解锁极速 / 恐惧的捷径**，也不再接管任何 0.113 接口适配。依据见下节。
>
> **2.1.0 起本补丁又开始做加法**（修复项 5 → 15，详见 [`CHANGELOG.md`](CHANGELOG.md) 的 2.1.0 段）：
> 服务器池（`server-pool-refresh` / `server-pool-join` / `server-pool-create`）、进大厅直达列表（`lobby-entry-direct`）、
> 「相同版本」筛选（`room-version-filter`）、公共频道换服务器（`chat-server-switch`）、公告跟随频道（`announcement-source`）、
> 手机滑聊天（`touch-chat-scroll`）、局内折叠提示不再挡鼠标（`room-chat-hint-passthrough`），
> 以及**建房面板里的服务器选择**（`server-pool-create-picker`：默认「自动（延迟最低）」，可展开看每台延迟并锁定一台，
> 锁定后只在那台建房）。下面「五项修复清单」描述的是 2.0.0 的 5 项，仍然全部保留。

发布包按 `dist/sts2-lobby-enhance-v2.0.0.zip` 命名（打包步骤见 [`UPLOAD.md`](UPLOAD.md)）。
`ReleasePackageTests.LatestReleaseZipShipsTheContentAssembly` 会断言 `dist/` 里那个包与
`packaging/content/` 逐条目一致（2026-11-13 出过一次「dist 里的包和 content 不是同一次构建」，
这条测试就是补那个洞的）。本次改动见 [`CHANGELOG.md`](CHANGELOG.md)。

**中文** | [English](#english)

---

## 中文

### 为什么不再有「兼容 6 个断点」这件事

0.6.3 是按 0.111 的接口编译出来的：`InitialGameInfoMessage.gameMode`、`ClientLobbyJoinRequestMessage.maxAscensionUnlocked`、
`NetHostGameService.DisconnectClient`、`ConnectedPeers`、`RelicPickingResult.GenerateRelicFight`、
`SaveManager.UpdateProgressWithRunData` 这 6 处在 0.113 上无法解析，当年本补丁就是为它们而生的。

**0.6.4 把这件事自己做完了**，而且不是「把旧引用藏进兼容层」，是能真的在 0.113 上跑：

- 加入流程重写并按需读模式：`Lobby/LanConnectLobbyManagedJoinFlow.cs:64-143`、`Lobby/LanConnectLobbyHandshakeCompatibility.cs:78-89`；
- 加入后推哪个准备页：`Lobby/LanConnectLobbyJoinFlow.cs:462-480`（Blitz / Dread 原生映射）；
- 桌面房主的加入响应报文：`LanConnectSerializationPatches.cs:603-615`（0.113 布局，含 `gameMode` / `isCustom`）；
- `DisconnectClient` / `ConnectedPeers` 两种形状自适应：`LanConnectNetHostCompatibility.cs:11-40`、`:42-65`；
- 放弃本局：`Lobby/LanConnectMultiplayerSaveCompatibility.cs:283-348`，进度写入按名反射（`:350-364`）。

实测证据（记录在 [`CHANGELOG.md`](CHANGELOG.md) 的 2.0.0 段）：扫过 0.6.4 的出货 DLL，
**引用失效成员的方法列表为空**（`examined=192 probeLimited=20 unexplainedBroken=0`）；同一份扫描
在 0.6.3 上会列出 7 个方法。扫出这个结果的一次性工具已随那批修复一起从仓库删除，
所以现在没有「启动探测」可跑 —— 这条结论由 `BreakpointRegressionTests` 在测试里继续守着。
所以这 6 处不再是我们的工作，继续留着只会盖住上游已经修好的实现。

### 这个补丁现在包含什么

| 类别 | 数量 | 说明 |
|------|------|------|
| 局终「新开一局」 | **2 项** | 结算页把全房带回准备页，并让新一局的存档重新绑定房间 |
| 协议稳健性 | **2 项** | 名册减员后重基线、读档加入不再挑顺序 |
| 建房 | **1 项** | 建房只保留「新协议」 |

合计 **5 项修复**，全部在启动日志里逐条可见（见「验证是否生效」）。

### 五项修复清单

| # | id | 依据（0.6.4 源码） | 用户看到什么 |
|---|----|-------------------|--------------|
| 1 | `roster-rebaseline` | 上游仍无 `PlayerLeft` 处理（0.6.4 全树 0 命中）；`Protocol/LanConnectRosterAuthorityState.cs:59` 的 `Accept` 要求「当前态逐字节重复」，而 `CommitHostSnapshot` 的 4 个调用点（`Protocol/Patches/LanConnectTailMessageRuntime.cs:1001,1017,1057,1074`）没有一个会减人 | 开局前踢人 / 有人掉线，不再让剩下的人卡死在旧名册上 |
| 2 | `load-join-order` | `Protocol/Patches/LanConnectTailMessageRuntime.cs:1259-1263` 仍用 `SequenceEqual` **逐位**比较「已连接列表（加入序）」与「Tail 名册（存档序）」；`:1253` 还要求存档下标与 `RealSlotId` 相等 | 读档续玩不必再按最初加入顺序进房 |
| 3 | `new-run-button` | 0.6.4 没有结算页新开一局的入口（全树无 `NGameOverScreen` 引用；发布说明 `docs/RELEASE_NOTES_V0.6.4_ZH.md:29` 明写「本次未新增……结算页新开一局」） | 打完一局不用重建房间、重新找人 |
| 4 | `new-run-save-binding` | 上游仍「先拒绝后更新」：`Lobby/LanConnectCurrentSaveBindingWriter.cs:37-43` 对不上 `ExpectedSaveKey` 就拒写，唯一更新键的地方是成功回调（`:71-72` → `Lobby/LanConnectLobbyRuntime.cs:2227-2231`） | 新开一局之后的存档仍能正常续玩 |
| 5 | `new-protocol-only` | `Lobby/LanConnectLobbyOverlay.cs:646` 仍并列「兼容旧版 Mod / 新协议」，`:3278-3288` 建两个按钮；旧协议仍禁 RitsuLib（`Protocol/LanConnectCreateRoomIntent.cs:28`） | 建房不用再在两种协议之间做选择 |

### 功能详解

#### 建房只用新协议（`new-protocol-only`）

社区模组建房时会让房主在「兼容旧版 Mod」（协议 id 300）和「新协议」（id 301）之间二选一。本补丁把旧协议
那个**入口隐藏**，只留「新协议」，并默认选中它。

**为什么**：RitsuLib 是很多模组的依赖库，而社区模组明确不允许在旧协议房间里启用它
（`Protocol/LanConnectCreateRoomIntent.cs:28` 直接抛 `RitsuLibNotAllowedInCompat`）。两种协议在联机体验上
没有区别，所以同时摆出两个选项只会让人选错。

**没有删任何东西**：按钮、说明文字、协议 id、以及背后所有处理旧协议的代码都原样保留。
**加入旧协议房间完全不受影响**——那条路径读的是房间广播的协议，和这个对话框无关。

**会自己让位**：只有当上游仍然同时提供两个协议按钮、并且它自己的判定认为「新协议当前可选」时才动手。
如果联机消息注册表没就绪（此时新协议不可用），旧按钮是唯一能建房的方式，就什么都不隐藏。

#### 局终「新开一局」（`new-run-button` + `new-run-save-binding`）

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
- **存档绑定跟着换**：房间会一直记着第一局的存档键（`HostedRoomSession.BoundSaveKey`），新局的键必然不同
  （`BuildSaveKey` 含 `StartTime`），写绑定会被上游以 `save_key_mismatch` 拒掉，而更新键的那行只在写入
  成功后才执行——死锁。本补丁在新一局真正开始时清空该键（上游对空键直接采纳新 key），所以新开一局的存档
  下次仍然能「读档多人游戏」。

### 公平性

**本版不再提供解锁捷径。** 极速 / 恐惧模式沿用游戏自己的解锁条件（0.6.4 亦如此，
`docs/RELEASE_NOTES_V0.6.4_ZH.md:24`），未解锁时不能建房选这两个模式，也不再有任何注入 epoch 的行为。

### 安装

**方式一：Steam 创意工坊（推荐）**

工坊条目：[**3839101755**](https://steamcommunity.com/sharedfiles/filedetails/?id=3839101755) —— 订阅即可。社区模组 `sts2_lan_connect`（工坊物品 3749766330）
已声明为必需物品，Steam 会一并订阅。

> 安装或联机遇到问题，可以加本模组的 QQ 群 **1135970470**。

**方式二：手动安装**

1. 确认社区模组已安装（工坊订阅，或放在 `<游戏目录>\mods\sts2_lan_connect\`），且版本 **≥ 0.6.4**。
2. 新建目录 `<游戏目录>\mods\sts2_lobby_enhance\`，把下面两个文件放进去：
   - `sts2_lobby_enhance.dll`
   - `sts2_lobby_enhance.json`

`<游戏目录>` 指 Steam 库中的 `steamapps\common\Slay the Spire 2`。模组清单必须与 DLL 同目录，
否则游戏不会把它识别为模组。

> **建议联机双方都装。** 只装一边仍能联机（本版不再校验解锁状态），但局终「新开一局」只把装了补丁的
> 已连接队友带回准备页，没装的人会留在结算页。

### 验证是否生效

启动游戏后，在日志里搜 `[lobby-enhance]`。0.113 上的正常输出形如：

```
[lobby-enhance] host mod: sts2_lan_connect 0.6.4.0 @ ...\sts2_lan_connect.dll
[lobby-enhance] applied: roster-rebaseline
[lobby-enhance] applied: load-join-order
[lobby-enhance] applied: new-run-button
[lobby-enhance] applied: new-run-save-binding
[lobby-enhance] applied: new-protocol-only
[lobby-enhance] summary: applied=5[roster-rebaseline,load-join-order,new-run-button,
          new-run-save-binding,new-protocol-only] skipped=0[] failed=0
```

本版**不再打印任何探测行**：2.0.0 删掉了「启动探测 6 个断点」的整套机制和读它的 8 项修复，日志里
只剩下面这些。判定标准：

- `applied=5 ... failed=0` → 5 项修复全部装上。
- `skipped=<n>` → 有 n 项判断为「这个构建不需要」，每一项都会单独打印原因
  （形如 `skipped: <id>: missing: <具体锚点>`）。
- 出现 `no repair reported itself as needed` → 这个构建不需要任何修复，什么都没装（正常状态）。
- 出现 `host mod 'sts2_lan_connect' is not loaded` → 社区模组没装或没被加载。
- 出现 `FAILED:` → 某项修复没装上；**只撤销它所在的那一组**，其它组照常生效。请把日志发给作者。

### 不启动游戏的验证

仓库自带几个不需要开游戏的控制台工具（需要 .NET 9 SDK 与已安装的游戏）：

```powershell
# 1) 跑 Entry.RunSilently()，确认 5 项修复真的挂上了
dotnet run --project tools/smoke-test -c Release

# 2) 补丁依赖的上游反射成员（3 条契约，只覆盖 Core/TailRoster.cs）
dotnet run --project tools/contract-check -c Release
```

`smoke-test` 断言三件事：① `failed` 为空；② `applied` 的 **id 集合与顺序**等于内置清单；
③ **每一项修复至少挂上一处真实 Harmony 接缝**。只报 `applied` 却没接缝同样判红。
所以日志里有 `applied: xxx` 不等于生效，判定要看这两组断言。
它不触碰 MegaCrit 的 `Log`（在非 Godot 宿主里会 0xC0000005 崩溃），因此可以安全地在命令行里跑。

`contract-check` 只剩 3 条契约（`LanConnectTailMessageRuntime.Shared` / `GetMessageBus` + 该类型本身），
服务的是 `Core/TailRoster.cs`；其余 4 项的反射锚点没有契约检查，覆盖靠 `smoke-test` 的接缝断言。

### 回归测试

```powershell
dotnet test tests
```

**59 个测试 / 9 个测试类**（Release 实跑基线：通过 59 / 失败 0 / 跳过 0），覆盖七类事实：

1. **上游断点已消失**：`BreakpointRegressionTests` 钉住 0.6.4 的出货 DLL（size + sha256），断言
   **0 处不可解析成员引用**，并把 0.6.3 那 6 处历史断点逐个断言为「已解决」——上游若回退，对应修复
   该回来这件事会立刻变红。
2. **游戏侧形态**：直接读 `sts2.dll` 元数据，记录 0.113 到底改了什么（这是游戏事实，不再是被删修复的
   需求）。`GameApiShapeTests`。
3. **上游内部契约**：`TailRosterContractTests`（4 个用例）把 `TailRoster` 反射用到的 5 个上游锚点
   （`LanConnectRosterAuthorityState.Reset`、`LanConnectTailMessageRuntime.Shared` / `GetMessageBus` /
   `_bindings`、`Binding.RequireRoster`）钉在二进制上：上游一改名，roster 复位只会静默返回
   false，在运行时表现为协议错位，所以必须让它在测试里变红。
4. **产物自身**：本补丁的 DLL 在 0.113 上零无法解析引用，并且带 `[ModInitializer("Init")]`；
   结算页注入的 UI 另有布局几何断言（两视口 × 六种矩形 × 两种形态，断言按钮/说明/提示互不相交且不越界）。
5. **发布工作区**：`packaging/` 结构符合官方上传器要求，`image.png` 是 <1 MiB 的正方形 PNG，
   `content/` 只有 4 个文件、绝不含社区模组的任何文件，且其中的 DLL 与当前构建逐字节一致。
6. **修复清单**：`FixRegistryTests`（1 个用例）断言 `Entry.cs` 里 `CompatFixes.Build()` 产出的 id
   **集合与顺序**就是那 5 项 —— 少写一行、改名或换序都必须有人同时改断言。
7. **反假绿闸门**：`GameDependentCoverageTests`（1 个用例）检查「依赖游戏 DLL 的那些用例真的跑过了」——
   缺输入时会静默跳过的用例，被它变成红色（可用 `STS2_ALLOW_MISSING_GAME_INPUTS=1` 显式降级为跳过）。
   0.6.4 的出货 DLL 也在它的输入清单里。

游戏目录、社区模组 DLL（0.6.3 与 0.6.4 各一份）、构建产物不存在时，相关用例会**跳过**并在输出里说明原因；
但 `GameDependentCoverageTests` 会因为「该跑的没跑」判红 —— 这正是反假绿的那道闸门。开发者本机没有游戏时，
用 `STS2_ALLOW_MISSING_GAME_INPUTS=1` 显式降级为跳过（跳过信息会列出缺的每一项输入）。

### 已知限制

- **只针对 0.113 验证。** 开发与验证基于 0.113（0.113.0 commit `16ac0fe3`；0.113.1 的 mod 元数据
  形状与之一致）。其它大版本没有验证过。
- **没有「总开关」，每项自己判断。** 每项修复在 `Applies(out string reason)` 里自己
  解析锚点（类型 / 方法 / 参数类型逐个比对），缺一个只跳过它自己，并在日志里打印
  `skipped: <id>: missing: <具体锚点>`。所以将来上游改了某个东西，**受影响的那一项会明确跳过，其余照常工作**。
- **每项失败只回滚自己那一组。** 四个 Harmony id（`sts2_lobby_enhance.<组名>`：`newrun` / `modes` /
  `roster` / `loadorder`）互相独立，`UnpatchAll` 只作用于出问题的那一组。
- **依赖社区模组 ≥ 0.6.4。** 未安装时补丁静默不生效（只留一条警告日志）。0.6.3 不在支持范围：
  本版的 5 项锚点都对着 0.6.4 校验过。
- **局终「新开一局」要求至少一名已连接队友装了补丁**，否则不显示按钮；未安装者会留在结算页。
- **日志里可能出现 `ERROR: System.InvalidOperationException` 而联机其实正常。** 上游
  `OnHostedControlEnvelope` 的 catch 过滤器只接 `InvalidDataException` / `LanConnectProtocolException`，
  而「协议未就绪」抛的是 `InvalidOperationException`，会逃逸成未观察的 Task 异常。不影响联机，
  但会干扰排障。
- **tail_v1 绑定在异常路径上仍可能超时**（正常联机不受影响）。
- **平台范围**：已在 Windows 桌面版与 Android 移动端（第三方启动器）双机联机验证；其它平台或移植层
  未测试。
- **联机行为需要人工验证。** 自动化测试只覆盖静态元数据、进程内的补丁安装与 UI 几何；两人以上的
  实际联机请按下面的清单走一遍。

### 实机验证清单（双人，建议每次发布前做）

1. A 建房、B 加入：B 能进大厅，双方看到彼此的名字与角色。
2. 建房协议：A 打开建房对话框 → 只看到「新协议」一项，且默认选中；建出来的房 B 能正常加入。
3. 踢人后开局：A 在大厅踢掉 B（或 B 掉线）→ 剩下的人仍能正常开始游戏，不卡在旧名册上。
4. 读档顺序：多人存档读档 → 让玩家故意不按原加入顺序进房 → 仍然能进，开局不报错。
5. 局终新开一局：打完一局 → 结算页点「新开一局」→ 全房回到对应模式的准备页；同时确认客户端结算页
   显示了提示文案，且新开的那一局打完后存档仍能在「读档多人游戏」里续上。
6. 双方日志都没有 `FAILED:`，且都有 `applied=5`。

> 以下两条是**游戏/上游原生行为**的回归观察，不是本补丁的功能：
> 宝物室「跳过」投票结算正常；「备份并放弃本局」流程完整（确认框、备份、进度写入、存档删除、按钮恢复）。

### 构建与发布

```powershell
# 编译（需要已安装游戏；SkipModCopy 表示不往游戏目录里复制）
dotnet build src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true

# 刷新创意工坊工作区的 content/（会重新构建并复制 DLL、清单、许可证）
pwsh -File packaging/build-content.ps1

# 重新生成工坊预览图
python packaging/make-image.py
```

发布前跑一遍：`dotnet test tests`（59 通过）+ `tools/smoke-test`（5 项 applied）+
`tools/contract-check`（3 条契约）。

上传流程见 [`UPLOAD.md`](UPLOAD.md)（含"三处版本号必须同时改"的说明）。

### 延伸阅读

`research/` 下**只保留仍在生效或仍未解决**的议题（索引见 [`research/README.md`](research/README.md)）。
设计与事故复盘的结论已经落在代码注释里（改那段代码就会看到），不再单独留档：

- [`research/join-order-bug.md`](research/join-order-bug.md)：读档加入顺序的现场记录。本版的 `load-join-order` 覆盖的是 Tail 名册那次逐位比较（`LanConnectTailMessageRuntime.cs:1259-1263`）；该文记录的其余现象是否同源，**本版未复核**。
- [`research/p1-crash-deep.md`](research/p1-crash-deep.md)：**未解决** —— 手机端闪退的触发条件与 (a)–(g) 验证矩阵。
- [`research/tail-profile.md`](research/tail-profile.md)：tail_v1 绑定与协议档位的决策记录（为什么不能强制降级 `compat`）。

### 许可

GPL-3.0，全文见 [`LICENSE`](../LICENSE)（与上游 `STS2-Game-Lobby` 的 LICENSE 逐字节一致）。
本模组不打包社区模组的任何文件，但源码中含从该 GPL-3.0 项目移植的逻辑，因此整体以 GPL-3.0 发布。
第三方声明见 [`THIRD_PARTY_NOTICES`](THIRD_PARTY_NOTICES)。

本项目是社区补丁，与 MegaCrit Games 及社区模组作者无关，未获其背书。

---

## English

> **2.1.0 adds features again** (fixes 5 → 15; see the 2.1.0 section of [`CHANGELOG.md`](CHANGELOG.md)):
> the server pool (`server-pool-refresh` / `server-pool-join` / `server-pool-create`), straight-to-list lobby entry
> (`lobby-entry-direct`), the "same game version" filter (`room-version-filter`), switching which server the public chat
> channel points at (`chat-server-switch`), announcements that follow that channel (`announcement-source`), touch
> scrolling for the chat panel (`touch-chat-scroll`), the collapsed in-run hint no longer eating clicks
> (`room-chat-hint-passthrough`), and **a server picker inside the create-room dialog**
> (`server-pool-create-picker`: defaults to "automatic (lowest latency)", expands to show each server's latency and lets
> you pin one — a pinned server is then the only one the room is created on). The "five fixes" list below describes the
> 2.0.0 set, which is still shipped in full.

### Why there is no "six metadata breaks" story any more

The shipped 0.6.3 build referenced six members 0.113 no longer provides
(`InitialGameInfoMessage.gameMode`, `ClientLobbyJoinRequestMessage.maxAscensionUnlocked`,
`NetHostGameService.DisconnectClient`, `ConnectedPeers`, `RelicPickingResult.GenerateRelicFight`,
`SaveManager.UpdateProgressWithRunData`). That set is what this patch was originally written for.

**0.6.4 adapts all six itself**, and not by hiding the old references: it really runs on 0.113.

- join flow rewritten and the mode read on demand: `Lobby/LanConnectLobbyManagedJoinFlow.cs:64-143`,
  `Lobby/LanConnectLobbyHandshakeCompatibility.cs:78-89`;
- post-join screen dispatch: `Lobby/LanConnectLobbyJoinFlow.cs:462-480` (Blitz / Dread mapped natively);
- desktop host's join-response body: `LanConnectSerializationPatches.cs:603-615` (0.113 layout with
  `gameMode` / `isCustom`);
- `DisconnectClient` / `ConnectedPeers` both shapes: `LanConnectNetHostCompatibility.cs:11-40`, `:42-65`;
- abandon run: `Lobby/LanConnectMultiplayerSaveCompatibility.cs:283-348`, progress write resolved by name
  (`:350-364`).

Measured (recorded in the 2.0.0 section of [`CHANGELOG.md`](CHANGELOG.md)): a scan of the shipped 0.6.4 DLL
lists **no method that references an unresolvable member** (`examined=192 probeLimited=20
unexplainedBroken=0`); the same scan on 0.6.3 lists seven. The one-off scanner that produced that number
was deleted along with those repairs, so there is no startup probe to run any more —
`BreakpointRegressionTests` keeps the conclusion guarded instead. Those six breaks are no longer our work;
keeping the fixes would only cover up upstream's own, working implementation.

### What this patch contains now

| Kind | Count | What it is |
|------|-------|------------|
| New run from the game-over screen | **2 fixes** | bring the room back to the lobby, and re-bind the new run's save to the room |
| Protocol robustness | **2 fixes** | roster re-baselining after a departure, and load-join order |
| Room creation | **1 fix** | 新协议 only |

**Five fixes in total**, each one visible in the startup log (see "Checking that it works").

### The five fixes

| # | id | Evidence (0.6.4 source) | What the player sees |
|---|----|-------------------------|----------------------|
| 1 | `roster-rebaseline` | Upstream still has no `PlayerLeft` handling (zero hits in 0.6.4), `Protocol/LanConnectRosterAuthorityState.cs:59` demands a byte-for-byte repeat for the current state, and none of `CommitHostSnapshot`'s four call sites (`Protocol/Patches/LanConnectTailMessageRuntime.cs:1001,1017,1057,1074`) removes a member | A kick or a drop before the run starts no longer strands the remaining clients on a stale roster |
| 2 | `load-join-order` | `Protocol/Patches/LanConnectTailMessageRuntime.cs:1259-1263` still compares "already connected (join order)" with the Tail roster (save order) using `SequenceEqual`; `:1253` also requires the save index to equal `RealSlotId` | A loaded save can be joined in any order |
| 3 | `new-run-button` | 0.6.4 has no game-over "new run" entry (no `NGameOverScreen` reference in the tree; `docs/RELEASE_NOTES_V0.6.4_ZH.md:29` states it was not added) | No re-hosting and no re-finding teammates after a run |
| 4 | `new-run-save-binding` | Upstream still refuses first and updates later: `Lobby/LanConnectCurrentSaveBindingWriter.cs:37-43` rejects a different `ExpectedSaveKey`, and the only update is the success callback (`:71-72` → `Lobby/LanConnectLobbyRuntime.cs:2227-2231`) | A run started from the game-over screen can still be resumed later |
| 5 | `new-protocol-only` | `Lobby/LanConnectLobbyOverlay.cs:646` still offers 「兼容旧版 Mod / 新协议」, `:3278-3288` builds both buttons, and the legacy profile still refuses RitsuLib (`Protocol/LanConnectCreateRoomIntent.cs:28`) | No protocol choice to get wrong when hosting |

### Feature details

#### 新协议 only when hosting (`new-protocol-only`)

Upstream's create-room dialog asks the host to choose between 「兼容旧版 Mod」 (protocol id 300) and
「新协议」 (id 301). This patch hides the legacy entry, keeps 新协议 and preselects it.

**Why**: RitsuLib is a dependency of many mods and upstream refuses to host it on the legacy profile
(`Protocol/LanConnectCreateRoomIntent.cs:28` throws `RitsuLibNotAllowedInCompat`). The two profiles are
otherwise identical in play, so offering both only invites the wrong pick.

**Nothing was deleted**: the button, its description, the protocol ids and every legacy-profile code path
stay untouched. **Joining a legacy room is unaffected** — that path reads the profile the room advertises.

**It steps aside**: the postfix only acts while upstream still offers both buttons *and* considers 新协议
selectable. If the message registry is not ready (新协议 unavailable), the legacy button is the only way to
host, and nothing is hidden.

#### New run from the game-over screen (`new-run-button` + `new-run-save-binding`)

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
- **The save binding follows**: the room pins `HostedRoomSession.BoundSaveKey` to the first run, a new run
  always has a different key (`BuildSaveKey` hashes `StartTime`), the binding write is refused with
  `save_key_mismatch`, and the line that would update the key only runs after a successful write — a
  deadlock. This fix clears the key when the new run actually starts (upstream adopts any key when the
  expected one is empty), so the new run's save can still be resumed through "读档多人游戏".

### Fairness

**This version no longer ships an unlock shortcut.** Blitz / Dread follow the game's own unlock conditions
(0.6.4 does the same — `docs/RELEASE_NOTES_V0.6.4_ZH.md:24`); while they are locked you cannot host them,
and nothing injects epochs any more.

### Installation

**Steam Workshop (recommended):** subscribe to [workshop item 3839101755](https://steamcommunity.com/sharedfiles/filedetails/?id=3839101755). The original mod
(workshop item 3749766330) is declared as a required item, so Steam installs it too.

**Manual:** the official mod must be installed (workshop, or `<game>\mods\sts2_lan_connect\`) at
**0.6.4 or newer**. Then create `<game>\mods\sts2_lobby_enhance\` and put
`sts2_lobby_enhance.dll` and `sts2_lobby_enhance.json` in it.

> **Installing it on both sides is recommended.** One side is still enough to play (this version no longer
> validates unlock state), but the new-run button only brings patched, still-connected teammates back to
> the lobby; everyone else stays on the game-over screen.

### Checking that it works

Search the game log for `[lobby-enhance]`. On 0.113 the expected summary line is:

```
[lobby-enhance] summary: applied=5[roster-rebaseline,load-join-order,new-run-button,
          new-run-save-binding,new-protocol-only] skipped=0[] failed=0
```

This version prints **no probe line at all**: 2.0.0 deleted the six-break startup probe together with the
eight repairs that read it, so the log contains only the lines below.

- `applied=5 ... failed=0` — all five fixes installed.
- `skipped=<n>` — n repairs decided this build does not need them; each prints its own reason
  (`skipped: <id>: missing: <anchor>`).
- `no repair reported itself as needed` — this build needs no repair at all; nothing was installed (normal).
- `host mod 'sts2_lan_connect' is not loaded` — the official mod is missing.
- `FAILED:` — a repair could not be installed; **only its own group** is unpatched, the others stay live.
  Send the log.

### Verifying without launching the game

```powershell
# 1) runs Entry.RunSilently() and reports which repairs Harmony actually installed
dotnet run --project tools/smoke-test -c Release

# 2) the upstream reflection members the patch depends on (3 contracts, Core/TailRoster.cs only)
dotnet run --project tools/contract-check -c Release
```

`smoke-test` asserts three things: the `failed` list is empty; the `applied` **id set and order** equal the
built-in list; and **every fix really installed at least one Harmony seam** — reporting `applied` without a
seam fails too. So an `applied: xxx` line in the log is not proof of anything; these assertions are. It
never touches MegaCrit's `Log` class (which resolves `Godot.OS` in a static constructor and dies with
an access violation outside a Godot host), so it is safe to run from a terminal.

`contract-check` is down to 3 contracts (`LanConnectTailMessageRuntime.Shared` / `GetMessageBus` plus the
type itself) and covers only `Core/TailRoster.cs`; the other four fixes' anchors are covered by
`smoke-test`'s seam assertions, not by contracts.

### Regression suite

```powershell
dotnet test tests
```

59 tests in nine classes (Release baseline: 59 passed / 0 failed / 0 skipped), covering seven kinds of fact:

1. **The upstream breaks are gone** — `BreakpointRegressionTests` pins the shipped 0.6.4 DLL (size +
   sha256), asserts **zero** unresolvable member references, and asserts each of the six 0.6.3 breaks by
   name to be resolved now: if upstream ever regresses one, the fix that used to cover it has to come back.
2. **Game shapes** — read straight from `sts2.dll` metadata: what 0.113 actually changed. Game facts
   only; no remaining fix binds to them.
3. **Upstream internals** — `TailRosterContractTests` (4 cases) pins the five upstream anchors
   `TailRoster` reflects on (`LanConnectRosterAuthorityState.Reset`,
   `LanConnectTailMessageRuntime.Shared` / `GetMessageBus` / `_bindings`, `Binding.RequireRoster`): if
   upstream renames one, the roster reset only returns false silently and shows up as a protocol mismatch
   at runtime, so it has to turn red here instead.
4. **The shipped artefact** — the patch DLL resolves completely against 0.113, carries
   `[ModInitializer("Init")]`, and the injected game-over layout has geometry assertions (two viewports
   × six rectangles × two layouts; button, caption and hint must not overlap or overflow).
5. **The release workspace** — `packaging/` matches what the uploader expects, `image.png` is a square
   PNG under 1 MiB, `content/` holds only this project's four files, and its DLL is byte-identical to
   the current build.
6. **The repair inventory** — `FixRegistryTests` (1 case) pins the id **set and order** produced by
   `CompatFixes.Build()` in `Entry.cs` to those five, so a repair cannot be added, removed, renamed or
   reordered without editing an expectation.
7. **The anti-false-green gate** — `GameDependentCoverageTests` (1 case) checks that the game-dependent
   cases really ran; a missing input makes them skip, which this gate turns red. The 0.6.4 DLL is one of
   the inputs it checks.

Missing inputs (game directory, either official DLL, build output) make the affected cases *skip* with a
reason, but `GameDependentCoverageTests` then fails on purpose: "green" must not mean "never executed".
Set `STS2_ALLOW_MISSING_GAME_INPUTS=1` on a machine without the game to downgrade that to an explicit skip
(the skip message lists every missing input).

### Known limitations

- **Verified on 0.113 only** (0.113.0 commit `16ac0fe3`; the 0.113.1 mod metadata shape matches). Other
  major versions are untested.
- **There is no global switch; every repair decides for itself.** Each repair resolves its own anchors
  (types, methods, exact parameter types) inside `Applies(out string reason)`. A missing
  anchor skips only that repair and logs `skipped: <id>: missing: <anchor>`, so a future upstream change
  costs only the repairs touching it.
- **A failure rolls back only its own group.** The four Harmony ids (`sts2_lobby_enhance.newrun` /
  `.modes` / `.roster` / `.loadorder`) are independent; `UnpatchAll` touches the failing group only.
- **Requires the official mod 0.6.4 or newer.** Without it the patch stays inert (one warning). 0.6.3 is
  out of scope: all five anchors were checked against 0.6.4.
- **The new-run button needs at least one connected patched teammate**, otherwise it is not shown;
  unpatched players stay on the game-over screen.
- **`ERROR: System.InvalidOperationException` can appear in the log while multiplayer works fine.**
  Upstream's `OnHostedControlEnvelope` catch filter only takes `InvalidDataException` /
  `LanConnectProtocolException`, while "protocol not ready" throws `InvalidOperationException`, which
  escapes as an unobserved task exception. Harmless for gameplay, misleading when reading logs.
- **tail_v1 binding can still time out on error paths** (normal play is unaffected).
- **Platforms**: verified on the Windows desktop build and on an Android device through a third-party
  launcher; other platforms or ports are untested.
- **Multiplayer behaviour needs manual verification.** The automated suite covers static metadata,
  in-process patch installation and UI geometry, not two live clients.

### Manual verification checklist (two players, before every release)

1. A hosts, B joins: B reaches the lobby and both see each other's name and character.
2. Room creation: A opens the create-room dialog — only 新协议 is offered and preselected, and B can join
   the room A creates.
3. Kick, then start: A kicks B (or B drops) in the lobby — the remaining players can still start the run
   instead of being stranded on a stale roster.
4. Load order: load a multiplayer save and deliberately join out of the original order — everyone still
   gets in and the run starts.
5. New run: finish a run, press "new run" on the game-over screen — the room returns to the lobby for the
   same mode, the client-side hint is visible, and the run started that way can still be resumed from
   "读档多人游戏".
6. Neither log contains `FAILED:`, and both contain `applied=5`.

> Two further checks cover **game/upstream behaviour**, not features of this patch: treasure-room "skip"
> votes resolve normally, and "back up and abandon" completes (confirmation, backup, progress write, save
> deletion, button refresh).

### Build and test

```powershell
dotnet build src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true
dotnet test tests
pwsh -File packaging/build-content.ps1
python packaging/make-image.py
```

Before a release run: `dotnet test tests` (59), `tools/smoke-test` (5 applied) and
`tools/contract-check` (3 contracts).

Upload steps — including the "three version strings must change together" rule — are in
[`UPLOAD.md`](UPLOAD.md).

### Further reading

`research/` keeps only the topics that are **still in force or still unresolved** (index:
[`research/README.md`](research/README.md)). Design and post-mortem conclusions now live in code comments —
whoever touches that code sees them — instead of in separate records:

- [`research/join-order-bug.md`](research/join-order-bug.md) — a field record of load-join ordering. `load-join-order` covers the Tail-roster comparison (`LanConnectTailMessageRuntime.cs:1259-1263`); whether the rest of that record shares the same cause was **not re-verified for this release**.
- [`research/p1-crash-deep.md`](research/p1-crash-deep.md) — **unresolved**: the phone-side crash and its (a)–(g) verification matrix.
- [`research/tail-profile.md`](research/tail-profile.md) — tail_v1 binding and protocol profiles (why forcing `compat` is not an option).

### Licence

GPL-3.0; see [`LICENSE`](../LICENSE), which is byte-identical to the upstream `STS2-Game-Lobby` copy. No
file from the official mod is redistributed, but the source does contain logic ported from that
GPL-3.0 project, so this project is distributed under GPL-3.0 as a whole. See
[`THIRD_PARTY_NOTICES`](THIRD_PARTY_NOTICES).

Unofficial community patch. Not affiliated with, endorsed by, or supported by MegaCrit Games or the
STS2 LAN Connect author.
