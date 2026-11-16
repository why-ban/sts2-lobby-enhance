# 游戏大厅增强 / Game Lobby Enhance

伴生模组（companion mod）：运行期用 Harmony 给社区模组 **「游戏大厅 / LAN Connect」（`sts2_lan_connect`）** 打补丁。
**不打包、不替换、不修改它的任何文件**，只在 manifest 里声明依赖，运行时在已加载的程序集里找到它。

- 目标游戏：**Slay the Spire 2 0.113.0 / 0.113.1**
- 依赖：**社区模组 0.6.4**（工坊 3749766330，已设为必需物品）
- 联机双方都要安装；`affects_gameplay` 保持 `false`（握手靠它判断两端是否一致）

## 它在做什么：15 项修复，9 个回滚分组

分组决定"一起失败的接缝"：组内任一项装不上就整组回滚（每组一个 Harmony id，`sts2_lobby_enhance.<组名小写>`）。

| # | id | 组 | 做什么 | 主要注入点（0.6.4 / 游戏 0.113；有的修复挂多处） |
|---|---|---|---|---|
| 1 | `roster-rebaseline` | Roster | 减员后把本地名册重新基线化 | `LanConnectRosterAuthorityState.Accept` |
| 2 | `load-join-order` | LoadOrder | 读档加入前按存档下标原地重排 | `LanConnectTailMessageRuntime.RestoreLoadJoin` |
| 3 | `new-run-button` | NewRun | 结算页给房主注入「新开一局」 | `NGameOverScreen._Ready` |
| 4 | `new-run-save-binding` | NewRun | 新一局开始时清空 `HostedRoomSession.BoundSaveKey` | `RunManager.SetUpNewMultiplayer` |
| 5 | `new-protocol-only` | Modes | 建房对话框不再显示「联机协议」一节，固定新协议 | `LanConnectLobbyOverlay.BuildCreateDialog` / `GetSelectedCreateProtocolId` |
| 6 | `room-version-filter` | Filter | 筛选弹窗加「相同游戏版本」chip | `LanConnectLobbyOverlay.GetFilteredRooms` |
| 7 | `touch-chat-scroll` | Touch | 手机：滑动=滚动，长按=框选，松手复制选区 | `LanConnectRichMessageView._Ready` / `RaiseReferencePressed` |
| 8 | `chat-server-switch` | Chat | 大厅聊天标题行：名称 + ⟳ + ‹ ›，切公共频道所连的服务器 | `LanConnectBasicChatPanel.UpdateAvailability` |
| 9 | `announcement-source` | Announce | 公告请求指向聊天频道当前那台服务器 | `LobbyApiClient.GetAnnouncementsAsync` |
| 10 | `server-pool-refresh` | Pool | 房间列表 = 全服池，完全接管刷新 | `LanConnectLobbyOverlay.RefreshRoomsAsync` |
| 11 | `server-pool-join` | Pool | 跨服加入：切服 → 复核房间仍在 → 走原加入流程 | `LanConnectLobbyOverlay.BeginJoinRoomAsync` |
| 12 | `server-pool-create` | Pool | 建房前选「延迟最低且能承载协议档位」的服务器 | `LanConnectLobbyOverlay.CreateRoomAsync` |
| 13 | `server-pool-create-picker` | Pool | 建房面板里可折叠的服务器选择（显示延迟，默认自动） | `LanConnectLobbyOverlay.BuildCreateDialog` |
| 14 | `lobby-entry-direct` | Pool | 点「游戏大厅」直达房间列表，不弹服务器选择器 | `LanConnectServerSelectionStartup.Show` |
| 15 | `room-chat-hint-passthrough` | Chat | 局内收起聊天时那行提示不再吞点击（仅电脑端） | `LanConnectRoomChatOverlay.RefreshFromSource` |

每个修复都在 `Applies()` 里一次性解析全部反射锚点，缺哪个报哪个（clean skip）；补丁体各自 try/catch 包住节点读写，
只有取参与前置判断在 try 外。`Applies()` / `Apply()` 里没有任何 Godot 调用，所以冒烟测试能在没有 Godot 的进程里跑它们。

## 玩家看到的变化（2.1.0）

- **服务器池**：打开「游戏大厅」看到的是所有服务器的房间，卡片标出来源；点别的服务器的房间会切服加入。
- **「相同版本」筛选**：只比游戏版本，不比 Mod 版本、不比协议档位。
- **聊天换服务器**：标题行 ‹ › 切公共频道所连的服务器，⟳ 回置顶服；公告跟着频道走。
- **建房选服务器**：面板里可折叠的一节，显示每台延迟；默认「自动（延迟最低）」，手动选中后只在那台建房，切不过去就不建；
  选择**不记忆**，每次打开都回到自动。
- **建房对话框不再显示联机协议**：默认按新协议建房（没有独立 tail 运行时的那一侧仍保留上游选择，能建房优先）。
- **手机**：滑动即滚动；长按 0.4 秒让这条消息进入选中态，接着拖动就是框选，松手复制选区。
- **局内**：收起聊天后那行提示不再挡住游戏右上角的按钮（只改鼠标过滤，不动布局）。
- **结算页**：一键新开一局、全房回准备页；新开之后存档仍可续；踢人/掉线后照样能开局；读档不必按原顺序进房。

## 门禁（顺序不可颠倒，工作目录 `work/companion`）

`@
dotnet build src\Sts2LobbyEnhance\Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true
pwsh -NoProfile -File packaging\build-content.ps1 -SkipBuild
pwsh -NoProfile -File tools\pack-release\pack-release.ps1 -Force
dotnet run --project tools\smoke-test\smoke-test.csproj -c Release
dotnet test tests\Sts2LobbyEnhance.Tests\Sts2LobbyEnhance.Tests.csproj -c Release
pwsh -NoProfile -File tools\pack-upload-kit\pack-upload-kit.ps1 -Force
pwsh -NoProfile -File tools\pack-upload-kit\test-upload-kit.ps1
`@

期望值：build 0 警告 0 错误；smoke 打印 `OK (15 fixes applied)` 且 15 项 id / 分组 / 接缝全绿；
`dotnet test` 61 通过 0 跳过；上传包验收 35 条全过（**不联系 Steam**）。

- `build-content` 必须紧跟 `build`：测试断言"出货 DLL 与当前构建逐字节一致"。
- `pack-release` 必须在 `dotnet test` 之前：那条「dist 里的 zip 与 content 逐字节一致」的测试在 dist 为空时会跳过，
  跳过的测试仍然算绿——先打包再跑测试，它才真的在断言。
- 测试里的"反假绿"门禁会核对 30 个游戏依赖型用例**真的执行过**（不是被 skip 掉）。

## 为什么没有兼容层

2.0.0 起上游 0.6.4 自己适配了游戏 0.113，原先 8 项接口适配（join-flow / join-screen-modes / host-wire /
tail-error-paths / abandon-run / extra-game-modes / room-card-pill / unlock-extra-modes）全部删除。
**本版不接管任何 0.113 接口适配，也不提供解锁极速 / 恐惧的捷径**；那些是上游自己的事。

## 不做什么

- 不打包、不替换、不修改上游 `sts2_lan_connect` 的任何文件
- 不写 `config.json`（切服/建房是上游自己的语义）
- 不接管客户端与房间握手、不伪造协议 offer
- 不给游戏版本加闸门：`min_game_version` 是 `0.113.0`，0.113.1 也能跑

## 许可

GPL-3.0，全文见 `../LICENSE`（与上游 STS2-Game-Lobby 的 LICENSE 逐字节一致）。
本项目的源码是对该 GPL-3.0 项目的衍生作品：所有修复都针对它的行为编写，并通过反射调用它的内部成员。
第三方组件与不分发声明见 `THIRD_PARTY_NOTICES`。
