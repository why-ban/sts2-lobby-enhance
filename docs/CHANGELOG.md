# 更新日志

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 的结构，版本号遵循语义化版本。

## [2.3.0] - 2026-11-18

目标游戏 **0.113**；依赖 `sts2_lan_connect` **0.6.4**。修复项 **17 → 18**，Harmony 分组仍是 **9**。

相对 **2.2.0（已发布）** 的改动：

- **手机端彻底去掉选区与复制**：长按不再把消息变成选中态、也不复制任何文字；按住 400 ms 后抬手不再打开引用预览（更短的点击仍然打开）。消息行那条"白框画到聊天窗口外面"的焦点描边一并根治：焦点样式被清空，行仍然可以获得焦点。
- **新增：大厅频道聊天里点邀请码就能进房**：`STS2INV:` 开头的邀请码显示成彩色胶囊（房间名 · 模式 · 人数），点它弹一次确认框，确认后切到邀请指向的服务器、复核房间还在、再走大厅自己的加入流程；胶囊右边有独立的「复制」复制原始邀请码。胶囊状态完全读大厅自己的房间快照（不发任何请求），随快照自动更新；房间从快照里消失时显示「过时的房间（无法加入）」。
- **移除**：侧栏聊天的拖动滚动尝试。它没能在真机上站住，已回到上游行为——侧栏聊天只用滚动条，拖动不再接管指针。

**内部整理（不改变玩家可见行为）**：邀请胶囊的 `_Ready` 锚点改成必需锚点（缺失即 clean skip，不再打一行警告后继续装一半）；
点击加入时创建的 `LobbyApiClient` 改为在 `finally` 里释放；邀请加入补了重入检查；
`touch-chat-scroll` 的按下计时表补了残留清理与 5 秒上限。

## [2.2.0] - 2026-11-17

> 本版原计划编号 2.1.1，上传前改定为 2.2.0：这一轮的内容量已经超出补丁号。
> **2.1.1 从未上传工坊，也不存在对应的发布记录。**

相对 **2.1.0（已发布）** 的五处改动：

- **长按选区不再画出界**：长按进入选中时，我们注入的按压会把键盘焦点交给那条消息，主题给获得焦点的控件画一圈 2 px 描边；`clip_contents` 只裁子节点、不裁控件自己的绘制，所以这圈描边露在面板左沿与消息区下沿之外。现在进入选区前先关掉该行的 `FocusMode`、退出时恢复，桌面鼠标选择完全不受影响（它不走这条路径）。
- **掉线重连后开局被踢回主菜单**：实测根因在上游——tail 扩展帧被"写前 reset"清掉，客户端少收一次名册变更，下一次 mutation 的 revision 不是 +1 就被判协议失败，界面显示成「模组不匹配」（上游 issue #48）。客户端侧现在有三种容错：名册减员、revision 跳号、以及**重复快照**（rev 相等且成员/槽位一致的 Mutation），命中即重新基线而不是断开；相等但成员或槽位不同、以及 revision 倒退仍然会被拒。
- **掉线重连后开局次序错位（种子）**：有人掉线又重连后，房主的房间列表变成"移除再追加"的顺序，而 tail 层发出去的消息按 slot 重排过——房主按入座序跑、客户端按 slot 序跑。每人种子 = `hash(seed) + RunState.GetPlayerSlotIndex(player)`，而这个下标就是各自列表里的 `IndexOf`，于是房主在对种子时抛 `RngSet seed should not change during the run!`，全房 `InternalError`。现在开场前在主机侧把房间列表**按 (slotId, netId) 原地排好**（与 tail 层重排完全同键，所以它的重排在补丁主机上退化成恒等），只改主机、不需要两端都装。
- **主机端提示改口径**：房间里所有队友都没装本模组时，新开一局的提示改为「**队友均未安装游戏大厅增强，无法新开**」（原来写的是"兼容补丁"，那是 2.0.0 之前的说法）。
- **手机端右列不再塞满**：手机大厅右列比屏幕高，聊天输入框一直落在框外。现在收起「ROOM DETAILS」那张只读信息卡
  （房间/房主/人数/模式/版本/协议/状态七行，左边房间卡上本来就有），高度让给频道聊天：标题、消息区、输入框、发送按钮
  全部完整显示。卡里的状态提示（加入被拒、切服失败等）挪进 ACTIONS 卡，仍然可见。只作用于手机，桌面端不变。

**内部整理（不改变任何行为）**：删掉定位「掉线重连」两个缺陷时临时加的逐条诊断输出、一个已不可达的舞台重建计数器、封面脚本里两个不再使用的图标；
把 8 处重复的「只报一次」日志助手收敛到 `Entry.WarnOnce`；补齐冒烟门禁缺失的 `begin-run-order` 分组断言与覆盖率门禁漏掉的两条用例名单；
校正文档与注释里对不上的用例数（59→61）、版本指向（0.6.3→0.6.4）与一处把「GetValue 会在常量字段上抛」写反的注释。

## [2.1.0] - 2026-11-17（已发布 01:37）

目标游戏 **0.113**；依赖 `sts2_lan_connect` **0.6.4**。修复项 **5 → 16**，Harmony 分组 **4 → 9**。

这一版第一次做「扩展」而不只是「修补」：大厅列表不再只显示一台服务器的房间，聊天可以换服务器看，
手机能正常滑动聊天。原有的 5 项修复一项没少。

### 新增功能

#### 1. 服务器池（`server-pool-refresh` / `server-pool-join` / `server-pool-create` / `lobby-entry-direct`，组 `Pool`）

- 房间列表变成**所有服务器的池**：客户端并发拉每台的 `GET /rooms`，整轮结算后**一次性提交**（不画残缺的房间数）。
- 数据键是 `(baseUrl, roomId)`：`roomId` 是各服独立的 UUIDv4，没有服务器命名空间（`lobby-service/src/store.ts:361`），
  只存 roomId 必然 404。
- 排序：与本机游戏版本相同优先 → `currentPlayers` 降序 → 该服延迟升序 → 上游原顺序。
- 卡片第二行（`BuildRoomMetaRow:3943`，`HFlowContainer` 会换行）追加 muted 小字 `来源：{短名}`。
- **跨服加入**：点别的服务器的房间会先 `SwitchLobbyServerAsync`（上游邀请码同一条路，`LanConnectInviteServerJoinCoordinator.cs:63-150`），
  再重拉该服列表确认房间仍在，然后走原加入流程。切服是上游语义，会退房、重启公共频道、写 `config.json` 的默认服务器。
- **建房选服**：`CreateRoomAsync` 之前切到「延迟最低且 `createRoomGuardStatus == allow`」的一台，最多试 3 台；30 ms 迟滞避免抖动。
- 单台 **2 s** 硬顶（用 `CancellationToken`，不是 `HttpClient.Timeout`——后者写死 10 s 改不了），死节点静默跳过，全失败保留上一轮。
- **死节点冷却退避**：连续失败按 10 / 30 / 90 / 300 秒冷却（成功清零），冷却期内本轮直接跳过、不再吃它的超时。
  依据实测：同一时刻含 2 台死节点时整轮 **2,013–2,110 ms**，把它们剔出轮次后 **247–333 ms**。
- **整轮一次提交 + 稳定排序**：旧实现把「到达顺序」当最后排序键，一轮里列表内容会换 15–17 次（看起来像在跳）；
  现在每轮只提交一次，排序键固定（同版本 → 人数降序 → 延迟升序 → 地址），内容没变就一次都不重建。
- **刷新节奏保持 8 秒**：不做自适应加速。实测 4 秒节奏等于把服务器带宽压力翻倍（忙服 40 人同时开列表 ≈ 3.6 → 7.1 Mbps），
  而「秒开」的收益已经由死节点退出轮次拿到了。

#### 2. 「相同版本」筛选器（`room-version-filter`，组 `Filter`）

- 筛选弹窗新增「版本」节，一个 chip「相同游戏版本」；**只比游戏版本**（`LobbyRoomSummary.Version`），不比 Mod 版本、不比协议档位。
- 谓词落在 `GetFilteredRooms` 的 postfix（`:7036`，5 个调用点全覆盖），原地删元素，不换 List 实例。
- 上游没有排序（`Sort`/`OrderBy` 全仓 0 命中），筛选顺序即服务端返回顺序。
- 弹窗不需要为这一节改尺寸：它的 `CenterContainer` 会按内容的最小尺寸摆，实测 561×392 px（宽=卡片 560，高按内容走）。
  早期版本曾给 `MarginContainer` 加过 ±43 px，实测对布局毫无作用，已删除。

#### 3. 聊天换服务器（`chat-server-switch`，组 `Chat`）

- 聊天标题行右侧加 `‹ {服务器短名} ›`。切的是**公共频道连哪台**，不切大厅服务器、不退出房间、不写 config。
- 走 `LanConnectRotatingServerChatPort.ConnectAsync`（`:777-838`）：它会 dispose 旧 client 并接新 coordinator，
  `runtime.Chat.Server` 换成新对象，overlay 的 `EnsureCurrentLobbyServerChatBinding`（`:2185-2200`）自动 rebind。
- **切换前必须 `GET /probe` 预检 `capabilities.serverChatVersion == 1`**：连到不支持的目标会让客户端
  `MarkPermanentStop(Unsupported)`（`LanConnectServerChatClient.cs:181-189`）且重连循环对该异常直接 return
  （`:1048-1059`），一次误切 = 本次进程内公共频道彻底死掉。预检结果缓存 5 分钟。
- 候选顺序：当前服 → 置顶服（`LanConnectServerListBootstrap.FeaturedServerAddress`）→ 其余按延迟升序，只保留支持频道的。
- **复位按钮** `⟳`：切到别家之后点它直接回到**置顶服务器**的频道，不用按十几次箭头。
  （局内悬浮窗不提供频道切换与复位：那需要在切换时替换上游的 dual state，会连带重置房间聊天，房间聊天优先级更高。）

#### 4. 手机端聊天操作（`touch-chat-scroll`，组 `Touch`）

- 根因不是「开了选中」，是两件事叠加：
  `MouseFilter = Stop`（`LanConnectRichMessageView.cs:55`）让 Godot 在标签这层吞掉指针事件，外层
  `ScrollContainer` 收不到拖动；同时 `emulate_mouse_from_touch` 默认 true，触摸被合成鼠标，
  `RichTextLabel` 按下即落选区锚点、移动即扩选。
- 手机端：`SelectionEnabled = false` + `MouseFilter = Pass`（拖动回到滚动容器，日志能滑）；
  长按 **400 ms** 且位移 < **16 px** → 该条消息进入选中态（整条高亮），继续拖动用同一根手指收窄/扩大选区，
  **松手复制选区文本**并保留高亮；点消息区别处或再按一下退出选中，之后第一次拖动立刻恢复滚动。
- 选中态的实现要点：`RichTextLabel` 只认鼠标左键落锚点（`rich_text_label.cpp:2636-2674`），
  而每个 `ScreenDrag` 会被引擎再派发成带左键掩码的 mouse motion（`input.cpp:895-913`），
  所以进入选中时用 `Viewport.PushInput` 补一次 `DEVICE_ID_INTERNAL` 的合成按压（C# 侧收不到，引擎照常落锚点），
  选中期间标签回到 `Stop`，容器拿不到 motion 就不会在划选时滚动（`viewport.cpp:2079-2085`、`:2163-2165`）。
- 滚动条 `CustomMinimumSize` 加宽到 44 px。
- 总闸是 `OS.HasFeature("mobile")`：**PC 上一行属性都不写、一个回调都不挂**，拖拽仍是框选。
  不用 `DisplayServer.IsTouchscreenAvailable()`——Windows 触屏本也是 true，会把 PC 的框选关掉。

#### 5. 公告跟随频道（`announcement-source`，组 `Announce`）

- 公告条外观与上游完全一致：不加来源标签、不加服务器箭头、也不加自己的翻页手势。
- 内容跟随**聊天频道当前所连的服务器**：`chat-server-switch` 只换频道目标、不动 `LobbyServerBaseUrl`，
  所以给 `LobbyApiClient.GetAnnouncementsAsync`（两条公告路径的唯一收口）挂 prefix，把请求地址换成频道当前那台；
  同服时原样放行。替身客户端**不带本服 token**——这些凭据属于玩家所在的那台，而 `/announcements` 不需要鉴权。
  没有额外的公告缓存层：上游本来每次刷新都会拉。

#### 6. 局内折叠提示不再挡鼠标（`room-chat-hint-passthrough`，组 `Chat`）

- 症状：局内收起聊天后，右上角那行「按下 Enter 打开聊天窗口」压住游戏自己的按钮，点不动。
- 提示文字本身不是元凶——上游给它 `MouseFilter = Ignore`（`LanConnectRoomChatOverlay.cs:498`）。吃掉点击的是它所在的容器链：
  列容器 `stack` 被显式设成 `Stop`（`:484-485`），而行 `topRow`（`:489`）与根 `MarginContainer`（`:476`）是 `Pass`
  （Godot 所有 `Container` 默认 `PASS`，`container.cpp:231-234`）。`Pass` 不等于放行：拾取只取该点最上层、filter 不是 `Ignore` 的那个控件
  （`viewport.cpp:1835-1877`），随后交付沿它的父链向上走，遇到第一个 `Stop` 就吃掉并 `set_input_as_handled()`
  （`:1731-1774`，break 在 `:1755-1759`）。点击死在浮层里，后面的游戏按钮连事件都收不到。
- 折叠时这三个容器一起置 `Ignore`（只撤 `Stop` 没用，拾取会移到行上），展开时按第一帧抓到的原值（实测 `Pass / Stop / Pass`）逐个还原。
  拾取先递归子节点再判断自身（`:1849-1859` 与 `:1862`），所以气泡按钮在 `Ignore` 父节点下照样能点。
- 只做电脑端：手机端保持上游原样（`OS.HasFeature("mobile")` 在补丁体第一行判断）。理由是维护者在手机上确认过没有这个遮挡——
  手机上只有气泡，玩家拖动气泡就能开聊天，那行提示不构成问题；不是技术上不能做。
- 不动布局：提示文字的位置、宽度、字号一律不改，只改鼠标过滤。

#### 7. 建房时自己选服务器（`server-pool-create-picker`，组 `Pool`）

- 创建房间面板多一节**可折叠**的「服务器」，默认折叠、默认「自动（延迟最低）」。
- 折叠头一行：`服务器 · 自动（延迟最低）  ▸`；展开后列出池里每台服务器的**短名 + 延迟**（`28 ms`；没有探测数据就写「未测」，
  不编数字），首项「自动（延迟最低）」并默认选中；选中态是前缀 `●`/○ 加一条上游自己那套 primary 按钮样式
  （`ApplyButtonStyle(row, primary: true, ...)`），不用 `ToggleMode`——上游的按钮样式里没有 toggle 态，硬塞会和对话框里其它按钮不一致。
- 展开时刷新一次延迟：复用 `PoolServerCatalog` 的缓存与 `ProbeAsync`，整轮预算 **2 s**，在 main thread 回填文本，不阻塞弹窗、不抛异常。
- 选择**只存内存**，按 overlay 实例（`ConditionalWeakTable`），不写 `config.json`。
- `CreateRoomAsync` 的 prefix 读这个选择：自动 → 原逻辑不变；手动 → **只切到指定那台，切成功才建房；切失败不创建**，
  把原因写进对话框错误标签（`ShowCreateDialogError`，`:6845`）。
- 手动选中的服务器若 `/probe` 的协议能力（已有缓存时）不支持当前档位，展开区给一行 muted 提示，但**不阻止创建**。
- 手动选择**不记忆**：每次打开面板都回到「自动（延迟最低）」，折叠状态也回默认。复位挂在 `OpenCreateDialogInternal`（每次打开都会走的方法），
  不是 `BuildCreateDialog`（启动时只跑一次）。

#### 8. 建房对话框不再显示「联机协议」（`new-protocol-only`，组 `Modes`）

- 这一版只允许用新协议建房，那两行协议卡片（兼容旧版 / 新协议 + 说明）就是多出来的一道选择题，还占着服务器选择要用的高度。
- 做法：`BuildCreateDialog` 之后把整节从对话框里摘掉——只摘不释放，因为上游每次打开都会调 `RefreshProtocolProfileOptionAvailability`
  去写那两个按钮，释放掉会让它每次打开都抛；协议 ID 在读取点覆盖成 `301`（`GetSelectedCreateProtocolId` 的 postfix），
  因为上游每次打开都会把选择重置成它自己的默认值（无 RitsuLib 时是「兼容旧版」）。
- 新协议当前不可用时（没有独立 tail 运行时）不改上游的选择，房间仍按旧协议建出来——能建房比"只让新协议"重要，日志会说明这一次走的是哪种。
- 注入点：`BuildCreateDialog` 的 postfix（把整节摘下）+ `GetSelectedCreateProtocolId` 的 postfix（在读取点覆盖成 `301`）。

#### 9. 真机反馈的缺陷与选服面板的收尾

- **首进大厅频道不连接**：频道消息只来自 WebSocket，而 WebSocket 之前必须先拿 ticket；真机日志里第一次 `/chat/tickets` 总是出现在点了 ⟳ 之后。根因是「进大厅直达房间列表」直接调用了上游的 pick 回调并跳过 `Show` 本体，而上游唯一连频道的地方就在 `Show` 之后那次切换里。现在面板首次可见、且从未连过、且没有活动目标时，先 `/probe` 预检 v1，支持则走与 ⟳ 相同的连接段，不支持只落一行说明；每进程只自动试一次，失败仍可点 ⟳。
- **长按选区画出界**：不是选区高亮画出去了，而是我们注入的锚点按压把**键盘焦点**交给了消息行，主题给获得焦点的控件画一圈 2 px 的 focus 描边；而 `clip_contents` 只裁子节点、不裁控件自己的绘制，所以消息行、消息列表、面板哪一级都兜不住它。现在进入选区前先把该行的 `FocusMode` 关掉、退出时恢复原值，焦点框不再出现（局内 HUD 与大厅侧栏都是）。
- **手机选服列表不能滚动**：每行是 `Button`（`mouse_filter = Stop`），按下之后的拖动全部被行吃掉，外层 `ScrollContainer` 收不到，松手只触发 `pressed`——所以"按哪都是选中"。新增一层自己的滚动容器（仅手机，视口 38% 上限）+ 触摸拖动驱动，拖动超过 16px 吞掉那次激活，点按与 8px 抖动照常选中；同一套挂到选服标题行与聊天标题行的 `‹ › ⟳`，标题行不会在拖动时翻转。桌面不加任何容器，滚轮与点按保持原样。
- **选中服务器后自动折叠**：选完立刻收起，标题显示所选那台；与「每次打开复位为自动、折叠」不冲突。
- **滑动时两行同时发绿**：根因不是 pressed 残留——按下会把键盘焦点给那一行，上游把 `focus` 样式盒画成 `HoverBg`，pressed 盒在指针离开时引擎自己会清，focus 不会。现在拖动一开始就清掉那一行的焦点样式，拖动中最多一行高亮、松手不留、选中只留主色；顺带把"拖动起步跳一下"（阈值被一次性补齐）改成逐帧补齐。
- **标题显示 IP 而不是名字**：标题以前只拿 `baseUrl` 走 `ShortName`，名字在这一步丢了；现在用该行的显示名，「自动」则先按建房路径同一套谓词解析出具体那台再显示（`自动 · 名字`），只有拿不到名字才退回地址。
- **行尾人数**：该服本轮房间的 `currentPlayers` 求和；这一轮没有房间数据显示 `— 人`，房间列表为空显示 `0 人`。
- **顺序**：`自动（延迟最低）` 之后**固定是置顶服（上游 `FeaturedServerAddress`，魔仙堡）**，其余按延迟升序、未测垫底；置顶服延迟再差也不下沉。
- **手机上的三个聊天按钮**：`‹ › ⟳` 的 24 dp 是桌面尺寸，手指需要 44 dp（上游自己的触控下限）；手机上改为 44 dp，桌面不变。

### 保留的 5 项（其中一项这一版行为变过）

`roster-rebaseline`、`load-join-order`、`new-run-button`、`new-run-save-binding`（`new-protocol-only` 这一版行为变过：协议一节整个摘掉，见第 8 节）。

### 说明

- 每个新修复都有自己的 `Shape.TryCapture` 锚点探针；任一锚点缺失就 clean skip 并把缺的成员写进日志，绝不半装。
- `Applies()` / `Apply()` 里**零 Godot 调用**（平台判断、节点操作全在补丁体内），所以 `tools/smoke-test` 能在无 Godot 的进程里验证全部 16 项。
- 已知限制：跨服加入会切服（退房 + 重启频道 + 写默认服务器），这是上游 `SwitchLobbyServerAsync` 的语义，我们没有绕开它；
  池刷新沿用上游 8 s 节拍（只在大厅页面可见时跑）。

## [2.0.0] - 2026-11-15

目标游戏 **0.113.0**；依赖 `sts2_lan_connect` **0.6.4**（原 0.6.3）。修复项 **13 → 5**，Harmony 分组 **10 → 4**。

### 移除（上游 0.6.4 已原生覆盖，共 8 项）

上游 0.6.4 发布说明明写「适配游戏 0.113 的加入流程、消息布局和 API 变化；支持已解锁的极速／恐惧模式建房及对应准备页」。
用当时仓库里的 `tools/tail-readability`（该工具已随本次删除一并移除）扫它的出货 DLL：「引用失效成员的方法」列表为空（`unexplainedBroken=0`）；
同一个扫描器扫 0.6.3 会列出 7 个方法 —— 6 个断点在 0.6.4 是真的没了，不是藏进兼容层。

- `join-flow`：0.6.4 自己重写了加入流程（`Lobby/LanConnectLobbyManagedJoinFlow.cs:64-143`）。
- `join-screen-modes`：Blitz / Dread 的推屏映射是原生的（`Lobby/LanConnectLobbyJoinFlow.cs:462-480`）。
- `host-wire`：0.6.4 已按 0.113 布局写 `gameMode` / `isCustom`（`LanConnectSerializationPatches.cs:603-615`）。
- `tail-error-paths`：拒绝 / 中止路径改走兼容层（`Protocol/Patches/LanConnectTailMessageRuntime.cs:1423,1643,1658`）。
- `abandon-run`：放弃本局已按 0.113 签名重做（`Lobby/LanConnectMultiplayerSaveCompatibility.cs:283-364`）。
- `extra-game-modes`：建房模式原生支持（`Lobby/LanConnectLobbyOverlay.cs:5183-5184`）。我们原来用的 `4→Blitz / 5→Dread`
  哨兵值与 0.6.4 的真实枚举撞车，留着不是多余，而是会覆盖原生分派。
- `room-card-pill`：房间卡片已含 `blitz` / `dread`（`Lobby/LanConnectLobbyOverlay.cs:3863`）。
- `unlock-extra-modes`：维护者决定不再提供进度捷径；上游也要求「已原生解锁」才可选。

### 保留（5 项，均为上游仍未覆盖）

| # | id | 依据 |
|---|---|---|
| 1 | `new-run-button` | 上游无此入口：0.6.4 全仓 `NGameOverScreen` 0 命中 |
| 2 | `new-run-save-binding` | 仍是「先拒绝后更新」：`LanConnectCurrentSaveBindingWriter.cs:37-43` + `Lobby/LanConnectLobbyRuntime.cs:2227-2231` |
| 3 | `roster-rebaseline` | `PlayerLeft` 仍 0 命中；`MembershipMutation` 只做 `Add`，没有减法 |
| 4 | `load-join-order` | `SequenceEqual` 逐位比较仍在（`TailMessageRuntime.cs:1259-1263`），`RestoreLoadJoinPlayers` 与 0.6.3 逐字相同 |
| 5 | `new-protocol-only` | 建房仍二选一（`Lobby/LanConnectLobbyOverlay.cs:646`、`:3278-3288`），旧协议仍禁 RitsuLib |

### 说明

- 这是一次**破坏性**升级：依赖下限从 0.6.3 提到 **0.6.4**，清单里的 `min_version` 同步改了；
  旧版大厅 + 2.0.0 的组合不会打上任何一项。
- 保留 4 个 `FixGroup`（NewRun / Roster / LoadOrder / Modes），每组一个 Harmony id 的隔离模型不变。
- 锚点是干净的：`RestoreLoadJoin`、`Accept` / `Reset`、`HostedRoomSession.BoundSaveKey` 在 0.6.4 里与 0.6.3 逐字相同，
  保留的修复项不需要跟着改签名。

## [1.8.1] - 2026-11-14

目标游戏 **0.113.0**；依赖 `sts2_lan_connect` **0.6.3**。修复项 **14 → 13**，Harmony 分组 **11 → 10**。

### 移除

- **`mid-run-restart`（局中「重开本层」）整项删除。** 维护者判定这个功能不成立：
  它的前提是让全房在**同一条会话**上重新 ready，而房间中途有人掉线时，房主那三个提前出口
  都不成立，只能等满 `HostBeginTimeoutMs = 150000`，期间面板文字不变、体感就是卡死 ——
  而那恰恰是最需要重开的场景。**它在最该起作用的时候失效**，收益抵不过复杂度和风险。
  删除范围：`Scripts/Restart/MidRunRestartFix.cs` 整个文件、`CompatFixes.Build()` 里的对应项、
  `FixGroup.Restart` 分组，以及 `tools/smoke-test` 与 `FixRegistryTests` 里的对应断言。
  房间管理面板里的「重开一局」回到社区模组自己的实现：我们不再接管它，也不对它的行为作任何承诺。
- **未受影响**：`new-run-button`、`new-run-save-binding`（同属 `FixGroup.NewRun`）、
  `Restart/GameOverLayout.cs` 与结算页那套 UI、以及其余 12 项修复，全部原样保留。

### 说明

- 这是**功能移除**，不是禁用：1.7.0 装的用户升级后，「房间管理」里那个由本补丁接管的重开按钮
  会回到「游戏大厅」原样（那条「重开一局」本来就在）。
- 版本号 1.8.0 → 1.8.1：1.8.0 已于当日发布到创意工坊（条目 3839101755），所以这次必须换号；
  本版相对 1.8.0 的**唯一**改动就是移除 `mid-run-restart`，其余 13 项与 1.8.0 完全一致。

## [1.8.0] - 2026-11-14

目标游戏 **0.113.0**；依赖 `sts2_lan_connect` **0.6.3**。修复项 **14**（1.7.0 的 9 项 + 新增 5 项：
`room-card-pill`、`new-protocol-only`、`roster-rebaseline`、`load-join-order`、`new-run-save-binding`）。

### 修复（可靠性重构）

- **删掉全局「6 个断点全中才生效」的总开关。** 以前任意一个签名 flag 翻转，整个模组会静默不生效；
  现在每项修复在 `Applies(in CompatSnapshot, out string reason)` 里自己解析锚点（类型、方法、
  参数类型逐个比对），缺一个只跳过它自己，并在日志里打印 `skipped: <id>: missing: <具体锚点>`。
- **启动探测不再抛异常。** `CompatProbe.Capture()` 里 6 个探测各自 try/catch，游戏类型一律用字符串
  `Assembly.GetType` 查；以前它用 `typeof()`，类型一改名探测自己就抛，导致**一个修复项都装不上**。
- **失败只回滚自己那一组。** 十一个 Harmony id（`sts2_lobby_enhance.<组名>`）互相独立，
  `UnpatchAll` 只作用于出问题的那一组；以前任一项失败会把全部 9 项一起拆掉。
- **`Applies()` 移进 try/catch**，探测抛异常不再留下「无回滚、无记录、已装补丁还活着」的状态。
- **房间卡片模式标注拆成独立项 `room-card-pill`**（纯装饰）。以前一个颜色属性改名就能带走全部修复项。

### 行为变更

- **建房只提供「新协议」（`new-protocol-only`）。** 0.113 里 RitsuLib 只在 `tail_v1` 下允许
  （`LanConnectCreateRoomIntent.cs:17-19` 抛 `RitsuLibNotAllowedInCompat`），而新旧协议联机体验无差别，
  所以把旧协议入口隐藏掉。postfix 挂在 `RefreshProtocolProfileOptionAvailability` 上：仅当
  `IsCreateProtocolSelectable(301, offer, tailAvailable)` 为真时才动手，把 `_protocolCompatButton` 隐藏、
  `_selectedCreateProtocolId` 置 301、重跑两个刷新方法。**一行代码都没删**，加入旧协议房间完全不受影响
  （那条路读房间广播，根本不碰这两个按钮）。

### 修复（真实缺陷）

- **结算页「新开一局」拆局时会吞掉 tail 的 `ModMismatch` 断连。** `Disconnect` 前缀没有原因过滤，
  与局中重开已经修过的同一问题对齐：现在只抑制 `NetError.Quit`，其余放行并打印原因。
- **该功能的会话从静态槽读取**，重连后可能在新会话上发加入请求。改为注册时把会话捕获进闭包。
- **局中「重开」在「回退不可用且 run 已拆」分支只弹窗不关会话**，协议租约继续 Frozen，
  下次「游戏大厅」的安全读档会撞 `Protocol selection is already owned`。现在这条分支也会关会话。
- **房间管理面板接管失败不再静默**：日志以 `DEGRADED — the room management panel was NOT taken over` 开头。
- **`GetEffectiveMaxPlayers` 的反射调用移进 try**，取不到就用 4，不再把可降级项升级成整次重开失败。

### 修复（上游协议缺口，两项新增）

- **`roster-rebaseline`：开局前有人离开，会让整个房间的客户端"报废"。**
  上游 0.6.3 的 tail 名册**只能表达"有人加入"**（`PlayerJoined` → `MembershipMutation`），
  **完全没有"有人离开"的路径**（全树搜 `PlayerLeft` 零命中）。于是开局前只要有减员
  （被踢、自己退、掉线），客户端名册就停在旧状态，随后：开始游戏被 `StateTransition` 的成员校验拒、
  之后有人加入被 `MembershipMutation` 拒、续局/重开被 `CurrentState` 的逐字节校验拒。
  现场表现是「Mod 不匹配」并回主菜单——因为尾部的拒绝一律以 `ModMismatch` 收场，与真实原因无关。
  本修复在 `LanConnectRosterAuthorityState.Accept` 前置判断「我们手里有、房主快照里没有的成员」，
  命中就重新基线化（`Reset()` + 把 use 改为 `Bootstrap`，并把 `authoritativeMembership` 实参清空，
  后者是必需的：该校验在取锁前就会用旧 `Current` 推导出的集合比较）。
- **`load-join-order`：读档续玩不必再按最初加入顺序进房。**
  上游 `RestoreLoadJoinPlayers` 用 `SequenceEqual` **逐位**比较两张表，而左边是加入顺序、
  右边是存档顺序（快照按 `(RealSlotId, PlayerId)` 规范排序）。同一个集合、顺序不同就判为不一致。
  这正是「第一个不按顺序的人能进、第二个进不去」的成因：第 k 个加入者看到的列表只有
  「房主 + 前 k-1 人」，第 1 个不按顺序的人只有 2 项尚能通过，第 2 个就必然失败。
  本修复在比较前把已连接列表按存档下标重排。

- **`new-run-save-binding`：「新开一局」之后的存档完全没法续玩。**
  上游把房间的存档键钉在会话上（`HostedRoomSession.BoundSaveKey`，`LanConnectLobbyRuntime.cs:2150`）；
  而「新开一局」复用同一个房间，新局的存档键必然不同（`BuildSaveKey` 把 `StartTime` 算进哈希）。
  于是新存档的绑定写入被 `LanConnectCurrentSaveBindingWriter.cs:55-59` 以 `save_key_mismatch` 拒绝，
  而唯一能更新那个键的代码（`:2152`）只在**写入成功之后**才执行 —— 死锁。
  结果：新开一局打出来的存档没有任何房间绑定，下次「读档多人游戏」直接报
  「无法确认这个多人存档原先使用的联机协议」。
  本修复在新一局真正开始时（`RunManager.SetUpNewMultiplayer` 的 prefix）清空该键；
  上游对空键会直接采纳新 key（同一个 writer 的 keyless 分支）。
  只在「会话的 NetService 就是本局用的那个」时才动手，客户端开局、单人局、别的房间一概不碰。

### 修复（探测本身的三处假阴性）

探测过严会把**能用的功能判成不可用**，和原来的问题一样糟。smoke-test 现在会打印每项的跳过原因，
以下三处就是它抓出来的：

- `LanConnectLobbyOverlay.BuildCreateDialog` 是**实例**方法，探测却要求 static → 整个 `extra-game-modes` 被跳过。
- `GetLobbyGameMode` 有两个单参重载（`GameMode` 与 `SerializableRun`），按参数个数找会歧义 → 改为按参数类型选。
- `SerializableRun` 的命名空间是 `MegaCrit.Sts2.Core.Saves`，探测常量写成了 `.Runs` → `abandon-run` 三项锚点全空。

### 其它

- `ExtraGameModesFix` 的 availability 判断改为在替换后的方法体开头分支跳进上游原始指令
  （整段替换后再反射调原方法会无限递归）；下拉项按 id **和** 文本双重去重。
- `UnlockExtraModesFix` 去掉对宿主模组的假依赖（它只碰游戏 API）。
- `tools/smoke-test`、`tools/wire-verify` 改为遍历 `Entry.AllHarmonyIds()` 枚举补丁（分组后单 id 枚举必然为空）。

### 已知限制

- 仍**只针对 0.113.0 验证**。上游 `sts2_lan_connect` 的形态变化只在「参数类型 / 方法名对不上时跳过并说明」
  这一层做了防御；无法预知未来版本会怎么改。

## [1.7.0] - 2026-11-14

目标游戏 **0.113.0**；依赖 `sts2_lan_connect` **0.6.3**。修复项仍为 **9**。

### 更名

- 模组 id 由 `sts2_lan_connect_0113_fix` 改为 **`sts2_lobby_enhance`**，显示名改为 **「游戏大厅增强」**，
  程序集与命名空间改为 `Sts2LobbyEnhance`，日志前缀由 `[0113fix]` 改为 `[lobby-enhance]`。
  原因：功能早已不止「兼容 0.113」，而 id 里带死版本号会在游戏后续更新时变得荒谬。
- **联机双方必须同时换成新版本**：握手报文里报的是 `id-version`，新旧 id 会互相认不出，
  旧对端会被判「没装补丁」而走保守分支（不崩，但「新开一局」等功能不对等）。
  注意这条**只管「换 id」这一次**：`ContainsOurId` 按 id 前缀匹配、**不比较版本号**
  （`NewRunFromGameOverFix.cs:59-64`），所以同一 id 下 1.7.0 与 1.8.0 可以互通，不需要双方同版本。
- 游戏按 `manifest.id + ".dll"` 找程序集，所以 DLL / 清单 / 安装目录同步改名：
  `sts2_lobby_enhance.dll`、`sts2_lobby_enhance.json`、`mods/sts2_lobby_enhance/`。

### 新增

- **大厅房间卡片标注「极速」「恐惧」模式。** 社区模组 0.6.3 的 `GetRoomGameModePill` 只认
  `daily` 与 `custom`，其余一律显示 `STD`，所以这两种新模式的房间在列表里看不出类型。
  现在给该方法加了 postfix：`blitz` → 「极速」（绿框）、`dread` → 「恐惧」（红框），
  其余输入原样返回；选中摘要与无障碍播报走的是同一个方法，因此一并生效。

### 改进

- **创意工坊封面重做**：512×512，暗色木纹/血色底 + 金色标题 + 五条功能卡，尺寸 177 KB（Steam 上限 1 MiB）。
  生成脚本 `packaging/make-image.py` 只用 Pillow，连续两次输出 sha256 相同。

## [1.6.1] - 2026-11-14

目标游戏 **0.113.0**；依赖 `sts2_lan_connect` **0.6.3**。修复项仍为 **9**。
1.6.0 真机测试通过后收尾的三处 UI 细节。

### 修复

- **重开成功后自动关闭「房间管理」面板。** 那个面板是独立的 CanvasLayer 浮层，不属于局内场景也不属于暂停菜单，
  所以「回到主菜单」并不会销毁它 —— 重开成功后它仍盖在新加载的局面上，文字停在「正在准备重开并通知队友...」。
  现在重开成功即调用面板自己的 `ClosePanel()`（与它的「关闭」按钮同一条路径）。
- **「新开一局」按钮改为点「继续」之后才出现。** 之前它在结算屏一出现就注入；现在挪到
  `NGameOverScreen.OpenSummaryScreen` 之后（即本局数据页），并在 `HideSummary` 时隐藏。注入幂等，
  反复开关不会叠出多份节点。客户端提示仍留在第一屏（玩家需要知道要不要等）。
- **客户端提示按会话状态分支。** 之前只看「房主有没有装补丁」，所以房主**放弃游戏**后客户端仍显示
  「房主可以直接新开一局」—— 而那时 `[RunLobby] Disconnected. Reason: HostAbandoned` 已经发生。
  现在按 `service.IsConnected` 分四种情况，并把文案压到一行内（标签宽 520 px、字号 24，约 21 汉字上限）：
  断连「房主已结束联机，请退出后重新加入」、房主有补丁「房主可以新开一局，不着急退出游戏」、
  房主无补丁「房主未装补丁，本局结束需退出重进」、未知「房主补丁未知，可能直接新开或需重进」。
  断连发生在提示注入**之后**也会就地更新，不再留陈旧文案。

## [1.6.0] - 2026-11-14

目标游戏 **0.113.0**；依赖 `sts2_lan_connect` **0.6.3**。修复项 **10 → 9**：移除 `treasure-vote`。
本版重点是让「局中重开」真正跑通 —— 在此之前它必定在客户端被 Tail 的 roster 校验拒掉，
然后两端各自空等到超时。

### 修复

- **局中「重开」在客户端被协议校验拒绝（本版主因）。** 客户端报
  `lan_protocol_version_mismatch: Current-state snapshots must repeat the accepted revision byte-for-byte.`
  原因是重开复用同一条会话，客户端 Tail 的 roster 权威仍持有上一纪元的 `StartRunLobbyPlayer` 快照，
  而房主发的是 `LoadRunLobbyPlayer` 快照 —— 载体类型不同、长度不同，`CurrentState` 的逐字节比对必然失败
  （`LanConnectRosterAuthorityState.cs:86`）。现在客户端在发重开请求前先丢弃自己的 roster 纪元
  （`Core/TailRoster.cs`），房主侧保持单调递增 —— 未打补丁的对端仍按旧行为工作。
- **重开 / 新开一局不再空等。** 客户端等 begin 的上限 15s → 180s、房主 30s → 150s（客户端必须长于房主，
  否则客户端先断开会让房主必然超时）；所有回来的玩家都离开时房主立刻失败，不再干等满额。
- **重开失败不再留下关不掉的僵尸房间。** 自动重开与原生确认屏都失败时关闭房主会话，
  而不是把「没有局、没有大厅、但会话还活着」的状态留给玩家。
- **协议拒绝不再被拆局抑制吞掉。** 拆局时只抑制 `NetError.Quit` 那一次断连；
  `NetError.ModMismatch` 之类的协议断连照常执行，不再留下「绑定未摘、传输仍在」的半拆解状态。
- **回退路径不再向已断开的 peer 发送。** 重开回退前逐个校验 peer 仍在线；否则一次发往死连接
  会被放大成「终止整个 Tail 绑定并踢掉所有 peer」。
- **标准模式建房恢复邀请按钮**（补回漏调的 `ScheduleEnsureInviteButton`）。
- **加入「已开局」房间时给正确提示**：按游戏自己的行为弹 `RunInProgress` 并断开，而不是抛异常。
- **加入失败时上报 Tail 的真实拒绝码**，不再一律显示笼统的 `ModMismatch`。
- **任何一项修复安装失败时整体回滚**，不再让半套补丁继续运行。

### 移除

- **`treasure-vote`（宝物室投票复刻）已删除**，连同 `src/Sts2LobbyEnhance/Scripts/Gameplay/TreasureVoteFix.cs`
  与 `tools/treasure-vote-test/`。理由与证据：
  1. 0.113 原生的 `TreasureRoomRelicSynchronizer.OnPicked(Player, int?)` + `AwardRelics` 已完整支持「跳过」票
     （`TreasureRoomRelicSynchronizer.cs:204-251`、`:253-334`）；
  2. 社区模组 0.6.3 在 0.113 上自己就跳过了那条补丁（`dec-official/TreasurePatches.cs:122-127`、`:137-140`、
     `:163-167`），所以本补丁原本要修的「断点 #5」在 0.113 上并不需要修；
  3. 复刻与原生在含跳过票时可观测不同：安慰奖配对（同一份票、同一颗种子，p1 拿到索引 1 而原生是 3）、
     全员跳过时的结果集（原生每条遗物一条 `Skipped`，复刻是空结果，并因此少一轮 `MoveToFallback`）。
- 启动探测**不变**：`GenerateRelicFight` 多一个 `relicIndex` 仍是「这是不是 0.113」的判据之一
  （`Core/CompatProbe.cs:62-74`），所以日志里 `game probe` 那行仍带 `generateRelicFight4=True`。
- `tools/contract-check` 里 3 条 `TreasurePatches` 契约随之删除（补丁源码已不再引用该类型），
  契约总数 **33 → 30**。

### 你会看到的变化

- 启动日志的 `applied` 少一项：`summary: applied=9[...] skipped=0[] failed=0`（不再有 `treasure-vote`）。
- 宝物室投票完全回到 0.113 原生实现：单机点「跳过」不再立刻关箱，而是等 `OnRoomExited` 收尾
  （`TreasureRoomRelicSynchronizer.cs:223-227`、`:340-346`）。
- 局中「重开」会多一行 `mid-run-restart: dropped the accepted Tail roster epoch on this session
  (before the reload join request).` —— 看到它就说明客户端已经按新逻辑处理，不再是被拒后空等。
- 重开失败时屏幕上给的是具体原因（房主没回应 / 有人离开 / 存档读不出），不再是「点了没反应」。

### 验证

- `tools/smoke-test`：`applied` **9 项**，不含 `treasure-vote`；现在它加载的是
  `packaging/content` 里的**出货 DLL**（不再是源码），并逐项断言每处修复至少有一处真实 Harmony 接缝。
- `tools/contract-check`：**30 条**契约全部满足。
- `dotnet test tests\Sts2LobbyEnhance.Tests\Sts2LobbyEnhance.Tests.csproj -c Release`：
  **通过 61 / 失败 0 / 跳过 0**（本机实跑输出，非引用历史数字）。
- 新增 `RosterEpochRegressionTests`：用**真实**的 0.6.3 类型与 0.113 类型离线复现了本版主因 ——
  复现出的异常消息与真机日志逐字相同，并证明 `Reset()` 之后同一帧能被接受（`T1`–`T4`）。
- 新增 `FixRegistryTests`（修复清单与分派表断言）、`GameDependentCoverageTests`（反假绿闸门）、
  `tools/pack-release`（发布包闸门：输入硬编码、逐文件比对 sha256、打完重新解包复核）。

## [1.5.0] - 2026-11-13

目标游戏 **0.113.0**；依赖 `sts2_lan_connect` **0.6.3**。修复项从 9 项增加到 **10 项**。
发布包：`sts2-lan-connect-0113-fix-v1.5.zip`（v1.5 发行时的模组名；v1.6.1 起模组改名为 `sts2_lobby_enhance`，本行保留当时的真实包名）。

### 你会看到的变化

1. **可以加入极速 / 恐惧房间了。** 之前客户端加入这两种房间会立刻弹 InternalError —— 加入握手其实
   已经成功，卡在"加入后该推哪个准备页"：社区模组的 `PushJoinedScreen` 只认标准 / 每日 / 自定义，
   遇到 0.113 新增的模式直接抛 `ArgumentOutOfRangeException: Unhandled multiplayer game mode.`。
   现在按 `GameMode` 推对应的准备页（自定义局仍优先判定，分派顺序与原实现一致）。
2. **局中「重开本层」第一次就能成功。** v1.4 真机反馈的"重开没成功"，根因不在重开逻辑，而在
   **加入成功后 tail_v1 绑定被提前解绑**：补丁原来在 `finally` 里无条件解绑，而上游只在**失败**时
   解绑（`LanConnectLobbyManagedJoinFlow.BeginAsync` 的 `catch` 里 `Unbind`，`finally` 只注销自己的
   handler）。结果是每个客户端加入成功后都处于"扩展帧发不出去、也收不到"的状态（日志表现为
   `prepare skipped ... writerBindings=0`，入站 `LanConnectNativeBusMessage` 无 handler），
   局中重开需要的两次扩展帧握手因此全部丢失。现在绑定会随会话存活到房间真正结束。
3. **重开失败不再空等。** 拆局前（房主）与发 `LoadJoinRequest` 前（客户端）各加一次 tail 绑定探针，
   没绑定就立刻报错 + 弹窗，不再等满 2000ms / 15s。
4. **结算页的「新开一局」不会点了没反应。** 会话已断开时不再注入按钮，原位置改为显示
   「联机会话已断开，无法新开一局」；按钮已出现而会话在结算页停留期间断开，会立刻收回
   （`NClickableControl.Disable()` → `OnDisable()` → `Visible = false`）；本局未结束 / 游戏未就绪 /
   队友都没装补丁，各自弹窗说明原因，不再静默。

### 修复项变动

| id | 变动 | 说明 |
|----|------|------|
| `join-screen-modes` | **新增** | 客户端加入极速 / 恐惧房间时的准备页推屏 |
| `join-flow` | 修改 | tail_v1 绑定生命周期：只在加入失败时解绑，成功则保留到会话结束 |
| `mid-run-restart` | 修改 | 增加 tail 绑定前置探针（房主拆局前 / 客户端发 `LoadJoinRequest` 前） |
| `new-run-button` | 修改 | 按钮可见性（会话断开即收回）+ 拒绝原因弹窗 |

完整 10 项清单见 [`README.md`](README.md#九项修复清单)（该小节现为「九项修复清单」——`treasure-vote` 已在 `[Unreleased]` 移除）。

### 验证

- 构建：**0 warning / 0 error**。
- `dotnet test tests`：**47 通过**。
- `tools/smoke-test`：**10 项**修复全部挂载成功。
- `tools/treasure-vote-test`：**19/19** 通过（与 0.6.3 语义逐字段差分）。

### 已知限制

- **自定义局里把大厅模式切成极速 / 恐惧的下拉框仍不提供这两项**：社区模组那条路径用
  `new UnlockState(SaveManager.Instance.Progress)` 直接读存档，绕过了本补丁的两个接缝。
  直接建房时选极速 / 恐惧不受影响。
- 其余限制不变：联机双方都要装本补丁；局终「新开一局」要求至少一名已连接队友也装了补丁；
  tail_v1 绑定在异常路径上仍可能超时（正常联机不受影响，局中重开已加前置探针）。

## [1.4.0] - 2026-11-13

> **历史条目重建说明（本节原标题为 `[1.0.0]`）。**
> 本节记录的状态是 **9 项修复 / 47 个测试 / smoke-test 9 项 applied**，正是 `[1.5.0]` 的直接前驱；
> 而 `[1.5.0]` 正文（「v1.4 真机反馈」那一条）把这个前驱称作 **v1.4**，所以标题由 `[1.0.0]`
> 改为 `[1.4.0]`。
> `[1.0.0]`–`[1.3.0]` 的条目**缺失**：本仓库不是 git 仓库，工作区内（`docs/`、`packaging/`、`tests/`、
> `tools/`、`src/`）也搜不到 v1.1 / v1.2 / v1.3 的任何版本痕迹（全量 grep 零命中）。因此这里
> **不补写、不推测** —— 可追溯的版本记录从本节开始。

目标游戏：Slay the Spire 2 **0.113.0**（`release_info.json` 中 commit `16ac0fe3`，
2026-11-12）；依赖：`sts2_lan_connect` **0.6.3**。

### 修复

0.113 改动了 6 个成员的签名或存在性，社区模组 0.6.3 的对应引用全部无法解析。这 6 个方法既不能 JIT，
Harmony 也读不出它们的 IL，因此修复都落在可读的调用者或异步存根上。

| # | 断点 | 修复项 | 用户可见的效果 |
|---|------|--------|----------------|
| 1 | `InitialGameInfoMessage.gameMode` 被删除 | `join-flow` | 客机可以加入房间（改用游戏自带的 `JoinFlow`） |
| 2 | `ClientLobbyJoinRequestMessage.maxAscensionUnlocked` 被拆分 | `join-flow` | 同上；加入流程不再读取该字段 |
| 3 | `NetHostGameService.DisconnectClient` 增加第 4 个参数 | `tail-error-paths` | 拒绝/踢人/中止路径不再崩溃 |
| 4 | `NetHostGameService.ConnectedPeers` 改为 `IReadOnlyList` | `tail-error-paths` | 同上 |
| 5 | `RelicPickingResult.GenerateRelicFight` 增加 `relicIndex` | `treasure-vote` | 宝物室"跳过"投票的结算恢复，行为与 0.6.3 一致 |
| 6 | `SaveManager.UpdateProgressWithRunData` 改为有返回值 | `abandon-run` | "备份并放弃本局"流程恢复；进度写入失败会记日志 |

### 新增功能

| 功能 | 修复项 | 说明 |
|------|--------|------|
| 局终「新开一局」 | `new-run-button` | 一局结束（胜利或死亡）后，房主一键让全房回到准备页，保持同一房间与同一批人。覆盖标准 / 每日 / 自定义 / 极速 / 恐惧五种模式。未安装补丁的队友会留在结算页，房主侧显示准确人数。 |
| 局中「重开」 | `mid-run-restart` | 接管房间管理里的「重开一局」，全房直接回到**本层开头**继续（零确认）。不删存档、不删房间绑定。 |
| 极速 / 恐惧模式建房 | `extra-game-modes` | 大厅建房下拉框新增这两项。 |
| 解锁极速 / 恐惧模式 | `unlock-extra-modes` | 装补丁即可使用这两个模式（两处接缝：上报状态 + 菜单闸门）。 |

另含工程性改动：`host-wire`（桌面房主加入响应报文按 0.113 布局重写）、启动探测（6 个签名变化不全时完全静默）、
`tools/smoke-test` / `tools/probe-check` / `tools/contract-check`（无 Godot 冒烟与契约校验）、
`packaging/`（创意工坊上传工作区）。

### ⚠ 公平性说明

解锁极速 / 恐惧模式是**真实的进度捷径，不只是界面解锁**。

游戏的 `GameModeHelper.AreAchievementsAndEpochsLocked` 只把「每日挑战」与「自定义局」排除在成就和时间线进度之外，
**极速与恐惧局完全计入**。因此：

- 在这两个模式里打完一局，可以正常获得成就与时间线 epoch，与正常解锁后的行为一致；
- 补丁**不修改进度存档**、不伪造「已获得」、不碰 `TotalUnlocks`（离线验证：`IsEpochObtained == false`、
  `TotalUnlocks == 0`、`Epochs.Count == 0`）；
- **本局存档**的 `player.unlockState` 会带上这两个 id —— 这是游戏既有机制（本局各玩家解锁集合取并集），
  不是新增写盘点。

介意提前获得这部分进度的玩家，可以不使用解锁功能，或等正常解锁后再用。

### 验证

- `dotnet test tests`：**47 通过**。包含断点回归（社区模组 DLL 的 22,774 个元数据候选中**恰好 6 个**无法解析）、
  游戏侧签名形态、产物自身可解析性、发布工作区合规性、以及 **12 条 UI 布局几何断言**（两视口 × 六种矩形 × 两种形态，
  断言按钮/说明/提示互不相交且不越界）。
- `tools/smoke-test`：不启动游戏即可确认 **9 项修复**全部挂载成功。
- `tools/contract-check`：**33 条上游契约**全部满足（补丁依赖的每个反射成员逐个校验）。
- 真机：Windows 桌面版 + Android 移动端（第三方启动器）双机联机验证，双向加入、开局、宝物室、局终重开均已跑过。

### 已知限制

- 只针对 0.113.0 验证；探测只识别这 6 个断点，不识别未来版本新增的断点。
- 必须安装社区模组 0.6.3 或更高版本，否则补丁静默不生效。
- **联机双方都要安装本补丁**：房主校验的是加入方上报的解锁状态，只装一边不够。
- 「新开一局」要求至少一名已连接队友装了补丁，否则不显示按钮；未安装者会留在结算页。
- 自定义局里把大厅模式切成极速 / 恐惧的下拉框仍不提供这两项（房主那条解锁状态不经补丁接缝）。
  直接建房时选极速 / 恐惧不受影响。
- tail_v1 会话绑定在异常路径上仍可能超时（正常联机不受影响）。

### 许可

GPL-3.0。本项目不打包社区模组的任何文件，但源码含从该 GPL-3.0 项目移植的逻辑，详见
[`THIRD_PARTY_NOTICES`](THIRD_PARTY_NOTICES) 与 [`LICENSE`](../LICENSE)。
