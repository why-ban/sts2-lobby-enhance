# tail_v1 绑定与协议档位：决策记录（task-8）

> **当前状态（2026-11-13 收尾）：决策仍然有效；文中两处「当前是坏的」已修，读的时候请注意区分。**
>
> - **§6.1 的决策不变**：保留 tail_v1 绑定、不强制 `compat_4_5_v1`、不自研双轨。§6.2 的五条理由一条都没变，
>   其中「强制 compat 在客户端层面根本无法实现」是这份记录存在的主要价值 —— 想改档位策略的人先读 §4.2 的四层门禁。
> - **已修（结论先行第 2 条）**：`BindClient` 的 `ReadOnlySpan<byte>` 反射缺陷不再走那条路 ——
>   现在直接调上游私有的 `Bind(7 参)` 重载，绕开 span 形参（`JoinTakeoverFix.cs:360-380`，含原因注释）。
> - **已修（§6.3 实施清单 1–3）**：nonce 传递、保留「缺 `_protocolOffer`/`_protocolFlowNonce` 直接抛」、
>   `tail_v1 extension frame bound.` 自检日志（`JoinTakeoverFix.cs:382`）。
> - **仍然成立**：§3.3「跳过绑定会怎样」、§4.4 对未打补丁对端的影响、§5.1（#3/#4 只在错误路径触发）。

> **结论先行**
>
> 1. **v1 采用「保留 tail 绑定」**（= 补丁逐行复刻官方 `BeginAsync` 里的绑定行为，按房间冻结的档位自动分流）。
>    不采用「强制 compat」，也不新增自研「双轨」协商层。理由见 §6.2。
> 2. **当前 `JoinTakeoverFix` 的 tail 绑定实现是坏的**，但坏点不在策略、在反射写法：
>    `BindClient` 的第 4 个形参是 `ReadOnlySpan<byte>`，`MethodInfo.Invoke` 传 `byte[]` **必然抛
>    `ArgumentException`**（已实测，§5.3）。今天所有 tail_v1 房间的加入都会在绑定处失败。
>    修法见 §5.3（`DynamicMethod` 转换垫片，已实测可行）。
> 3. **#3/#4（`DisconnectClient` 3→4 参、`ConnectedPeers` 元素类型变更）只出现在错误路径**，
>    正常联机不触发；但错误路径上不修会把"只断一个 peer"变成"整个会话抛 `MissingMethodException`"，
>    所以 `TailErrorPathFix` 这种"重定向到 0.113 签名重写"的做法是**对的**，需要补两个缺陷（§6.3）。
> 4. **Lead 提供的证据 2 被误读了**：那次成功联机里，手机端在**线上是 tail_v1**（它发了扩展帧），
>    不是"compat 客户端连上了 tail 主机"。该日志**不能**用来证明"两端档位不同也能连"。详见 §3.3。

---

## 1. 事实基线：档位到底是什么

### 1.1 两个 profile 与线上差异

`LanConnectProtocolProfile`（`Scripts/Protocol/LanConnectProtocolProfile.cs:3-7`）只有两个值：

| profile | 线上形态 | 位宽（`LanConnectProtocolProfiles.cs:17-29`） |
|---|---|---|
| `compat_4_5_v1` | **无扩展帧**，但**改写了原版位宽** | `slotId = 4 bit`、大厅列表 = `5 bit`（`LanConnectConstants.cs:28,32`） |
| `tail_v1` | **原版位宽 + 额外一帧 native 扩展帧**（channel 0） | `slotId = 2 bit`、大厅列表 = `3 bit`（`LanConnectConstants.cs:26,30`） |

原版 `StartRunLobbyPlayer.Serialize` 写的是 `WriteInt(slotId, 2)`（`MegaCrit.Sts2.Core.Entities.Multiplayer/StartRunLobbyPlayer.cs`），
即 **`tail_v1` 的 vanilla 段就是原版字节**，而 `compat_4_5_v1` 是**另一种方言**。

> 这一点决定了一个常被说反的结论：`compat_4_5_v1` 并不是"原版兼容档"，它是"改位宽的 8 人档"。
> 所以"compat 客户端 ↔ tail 主机"不是"降级兼容"，而是**两套不兼容的编码**。

### 1.2 tail 的扩展帧机制（三个必须同时成立的环节）

`LanConnectTailMessageRuntime` 用三个环节把一帧矩阵消息（`InitialGameInfo` / `LobbyJoinRequest` /
`LobbyJoinResponse` / `LoadJoin*` / `Rejoin*` / `PlayerJoined` / `LobbyBeginRun`，见
`LanConnectTailMessageTypeMatrix.cs:20-33`）与一帧 native 扩展帧**配对**：

1. **发送端**：`NetMessageBus.SerializeMessage<T>` 的 prefix 调
   `TryPrepareConcreteOutgoing`（`LanConnectTailMessagePatches.cs:511-545`），把原版字节留在
   pending 表里（`LanConnectTailMessageRuntime.cs:299-347`）。
2. **发送端**：`ENetHost.SendMessageToClient` / `ENetClient.SendMessageToHost` 的 postfix 调
   `CompleteNativeTransport`（`LanConnectTailMessagePatches.cs:618-653`），按内容匹配 pending，
   再经 `LanConnectNativeBusSender.Send` 把扩展帧发出去。
3. **接收端**：`NetMessageBus.SendMessageToAllHandlers` 的 prefix 调 `TryEnterNativeDispatch`
   （`:647-712`）：矩阵帧先进 hold（`:811-835`），等配对扩展帧到达后再 `bypass` 分发原版 handler
   （`:798-806`）。

**环节 1、2 全部要求"该 message bus 上有 binding"**：`:250` 与 `:666-669` 都是
`binding == null → 放行原版路径（不发扩展帧）`。

### 1.3 档位由谁决定

**房主 + 大厅服务决定，客户端只读**。链路：

```
host UI 选择 ──► LanConnectCreateRoomIntent ──► POST /rooms ──► 服务端冻结 selection
                                                                    │
                       房间列表 / join 响应把冻结的 ProtocolSelection 下发 ──► 客户端
                                                                    │
                              LanConnectLobbyJoinFlow: selection = selectionDto.ToValidatedValue(localOffer)
                                                                    │
                                        FreezeClient(selection) ──► _protocolSelection ──► BindClient（仅 tail）
```

- 房主侧：`LanConnectLobbyOverlay.cs:6777-6784` 用 UI 选出的 `protocolId` 造 intent；
  `LanConnectHostFlow.cs:184-195` 拿到服务端冻结的 selection 后**校验 profile 必须等于 intent**，
  不等就抛 `protocol_profile_unsupported`。
- 客户端侧：`LanConnectLobbyJoinFlow.cs:65-80` 直接用 `joinResponse.Room.ProtocolSelection`，
  **没有任何"本地选择档位"的入口**。
- 服务端：`lobby-service/src/store.ts:601-620` 冻结 + 门禁。

---

## 2. 全部档位决策点（穷举）

### 2.1 房主侧

| # | 位置 | 决定什么 | 备注 |
|---|---|---|---|
| H1 | `LanConnectLobbyOverlay.cs:6716-6772` | 建房对话框选 `TailV1` / `Compat4x5V1`；默认值 = compat（除非 RitsuLib 已装） | `GetDefaultCreateProtocolId` 先试 compat |
| H2 | `LanConnectCreateRoomIntent.Validate`（`LanConnectCreateRoomIntent.cs:8-33`） | tail 需 `Offer.Supports(1)`；compat 禁止 RitsuLib | |
| H3 | `LanConnectHostFlow.StartLobbyHostAsync:184-197` | 校验服务端冻结档位 == intent 档位 | 不一致直接失败 |
| H4 | `LanConnectHostFlow.StartLanHostAsync:69-75` | **LAN 直连建房 = 恒定 `CreateLocalCompat`** | 直连永远 compat |
| H5 | `LanConnectHostFlow.PublishExistingHostToLobbyAsync:300-400` | 把已在跑的 host 发布到大堂时重建 selection | |
| H6 | `LanConnectLobbyCapacityPatches.cs:241-323` | 未走大厅的 host 的"守卫冻结"，默认 `CreateLocalCompat` | |
| H7 | `LanConnectMultiplayerSaveRoomBinding.cs:252-330` | 续局/存档恢复时重建或还原 selection | 无存档档位时回落 compat |

### 2.2 客户端侧

| # | 位置 | 决定什么 | 备注 |
|---|---|---|---|
| C1 | `LanConnectLobbyJoinFlow.cs:65-80` | **唯一入口：用房间冻结的 selection** | 无本地选择 |
| C2 | `LanConnectLobbyJoinFlow.cs:69-79` | 本地无 tail runtime 且房间是 tail → **直接失败并提示"需要 0.111+ / 加入兼容旧版 Mod 房间"** | 硬门禁，不会挂起 |
| C3 | `LanConnectProtocolSelection.Validate`（`LanConnectProtocolSelection.cs:16-103`） | 逐字段 + capability digest 校验 | digest 不符 → `lan_protocol_version_mismatch` |
| C4 | `LanConnectDirectJoinFlow.cs:49-55` | **LAN 直连加入 = 恒定 `CreateLocalCompat`** | 且用 1 参构造 flow（`:61-62`）→ `_protocolSelection == null` |
| C5 | `LanConnectLobbyJoinFlow.cs:91-111` | tail 房才建立 pre-transport 控制通道，把 flow nonce 交给房主 | 与绑定配套，缺一不可 |

### 2.3 服务端

| # | 位置 | 决定什么 |
|---|---|---|
| S1 | `protocol-profile.ts:13-30` | 规范/旧版客户端的 profile 解析；`compat_0_3_0_5` 一律映射成 `compat_4_5_v1` |
| S2 | `store.ts:601-608` | 加入者 profile 必须等于房间冻结 profile，否则 **409 `protocol_profile_unsupported`** |
| S3 | `protocol-capabilities.ts:124-155` | `assertJoinerCompatible`：compat 房拒绝 RitsuLib；tail 房要求加入者 `lanProtocolMin ≤ 1 ≤ lanProtocolMax` |
| S4 | `store.ts:734-766` | tail 房额外门禁：注册表指纹 + `nativeBusTypeId` 必须齐备 |
| S5 | `protocol-capabilities.ts:112-121` | 冻结时把 `registryFingerprint`/`nativeBusTypeId` 写进 selection（compat 房不写） |

**关键**：客户端的"档位"不是自己选的，是 `C1` 从房间读来的。**任何"客户端强制 compat"都必须先绕过 C1/C3，
而 C3 会直接抛异常。** 这就是 §6.1 结论的技术根。

---

## 3. 问题 1：`BindClient` 的条件、时机与跳过后果

### 3.1 官方调用点

全仓只有 **2 个绑定调用点 + 1 个解绑点**（`grep BindClient|BindHost|Unbind`）：

| 位置 | 角色 | 条件 |
|---|---|---|
| `LanConnectLobbyManagedJoinFlow.cs:77-86` | **客户端绑定** | `_protocolSelection?.Profile == TailV1` |
| `LanConnectLobbyRuntime.cs:962-977`（`AttachHostedRoom`） | 房主绑定 | `protocolSelection.Profile == TailV1` |
| `LanConnectLobbyManagedJoinFlow.cs:180` | 客户端解绑 | 仅异常路径 |

客户端的**时机**很关键：`CreateClient()` 之后、`ConnectAsync()` 之前（`:77` → `:80` → `:101`）。
必须在连接前绑定，因为房主可能在客户端发出 `LobbyJoinRequest` **之前**就先发 `InitialGameInfo` 扩展帧
（`LanConnectTailMessageRuntime.cs:1963-1976` 专门为这个顺序留了 `EnsureClientNativeFlowBound` 兜底）。

### 3.2 绑定做了什么

`BindClient`（`:132-155`）→ `Bind`（`:1432-1458`）：

1. `LanConnectNativeBusStartupCheck.EnsureReadyOrThrow()`（注册表/typeId 就绪）
2. `offer.Validate()`、`selection.Validate(offer)`（digest 复核）
3. `new Binding(...)`：反射取 `NetClientGameService._messageBus`（0.113 字段名未变，已核）、
   `NetMessageBus._writer`
4. 写两张表：`_bindings[bus] = binding`、`_writerBindings[writer] = binding`

之后每个矩阵帧的发送都会命中这两张表（`TryPrepareConcreteOutgoing:239-252`、
`CompleteConcreteTransport` 路径），每个入站矩阵帧会命中 `_bindings`（`TryEnterNativeDispatch:660-669`）。

### 3.3 跳过绑定会怎样（分档位）

| 对端 host 档位 | 跳过绑定的后果 |
|---|---|
| `compat_4_5_v1` | **无影响**。compat 房本来就不会走到 `BindClient`（`Profile != TailV1`），且 compat 房没有任何 hold 逻辑。 |
| `tail_v1` | **必挂**。房主 hold 住 `LobbyJoinRequest`（`:811-835`），等不到配对扩展帧 → 2000ms（`:94-95`）后 sweep（`:2083-2105`）→ `HandleIncomingFailure`（`:631-643`）→ `RejectAndDisconnect`（`:1399-1430`）→ `DisconnectClient(peer, ModMismatch)`。 |

tail 房主侧的完整时序（**与 Lead 给的日志逐行对应**）：

```
native flow bound for peer ...            ← PrepareHostNativeFlow（控制通道先于 ENet 建立）
native flow activated for peer ..., deferred extension=none
pending extension registered for InitialGameInfo (vanilla bytes=10)   ← 房主先发
sending native extension InitialGameInfo seq=1 ...
holding LobbyJoinRequest ... awaiting extension frame.                ← 屏障开始计时
extension frame received from peer ..., held=yes, invalid=none.        ← 客户端扩展帧到达
pending extension registered for LobbyJoinResponse (vanilla bytes=397)
sending native extension LobbyJoinResponse seq=2 ...
```

> **关于"两端档位不同也能连"（Lead 证据 2）的判定**
>
> 日志里 `extension frame received from peer ... held=yes, invalid=none` 与
> `sending native extension ... recipientTypeId=53` 证明：**该手机端在线上是 tail_v1**
> —— 它确实发了合法扩展帧，也正确接收了房主的扩展帧。
> 一个真正的 `compat_4_5_v1` 客户端**不可能**产生这一帧（compat 路径下 `TryPrepareConcreteOutgoing`
> 在 `:255-259` 就直接返回 false）。
>
> 因此那次成功只能说明：**"Mod 版本/构建标签不同"不影响联机，只要两端对同一房间实现同一个线上档位"**。
> 它**不能**证明"compat 客户端能加入 tail 房"。据此把"档位不必一致"当作设计前提，会直接把 tail 房做成不可加入。
>
> 对手机端"档位 = compat_4_5_v1"最可能的解释是：那是它的**本地构建/房间列表投影标签**
> （服务端对旧 generation 客户端会把 profile 投影成 `extended_8p`，见 `protocol-profile.ts:32-37`），
> 而不是它在那次会话里的**线上 profile**。若要坐实，只需在手机端日志里搜
> `tail: native flow bound`/`prepare skipped ... writer has no binding` 两行之一。

**最坏情况比"超时"更糟**：若房主跑的是**未打补丁的 0.6.3 二进制**，`RejectAndDisconnect` 在 0.113 上
不可 JIT（§4.1），异常被 `FailPeerForExpiredHold` 的 `catch` 吞掉（`:2119-2123`），
**房主根本不会断开该 peer**；而客户端侧 `JoinFlow.AttemptJoin` 对 join 响应**没有任何超时包装**
（`MegaCrit.Sts2.Core.Multiplayer.Game/JoinFlow.cs:185-200`；`LanConnectLobbyJoinFlow` 外层也没有
`Task.WhenAny` 超时，只有用户取消）→ 表现为**无限期卡在"正在加入"**，而不是一个干净的报错。
（ENet 传输层自身可能最终报错，但游戏层不保证，不能依赖。）

---

## 4. 问题 2：能不能"强制所有会话为 `compat_4_5_v1`"

### 4.1 先看 ABI：#3/#4 是真实存在的（实测）

用 `work/companion/tools/memberref-probe` 直接读三个 DLL 的 MemberRef/MethodDef（只读，未改任何文件）：

| DLL | `NetHostGameService.DisconnectClient` | `get_ConnectedPeers` 返回 |
|---|---|---|
| `releases/sts2_lan_connect/sts2_lan_connect.dll`（**发布版 0.6.3**，mvid `6fc377b1`） | `(uint64, NetError, bool)` → **3 参** | `IReadOnlyList`<元素 TypeDef 0x755> |
| `dist/lobby-port-v0.113/...`（0.113 移植版，mvid `76d9fe3d`） | `(uint64, NetError, bool, string)` → **4 参** | `IReadOnlyList`<元素 TypeDef 0x779> |
| 游戏 0.113 `sts2.dll` | `DisconnectClient(ulong, NetError, bool now = false, string? debugReason = null)`（`NetHostGameService.cs:242`） | `IReadOnlyList<NetClientData>`（`:40`） |

结论：**补丁要修的 0.6.3 二进制确实是 3 参 / 旧元素类型**，与 `CompatContract` 的注释
（"compiled against 0.111"）一致；#3/#4 成立。而 0.113 移植版本身没有这两个断点。

### 4.2 "强制 compat" 的 4 层门禁（逐层都过不去）

| 层 | 位置 | 行为 |
|---|---|---|
| L1 客户端预检 | `LanConnectLobbyJoinFlow.cs:69-79` | 房间 tail + 本地无 tail runtime → 直接失败（不连接） |
| L2 selection 校验 | `LanConnectProtocolSelection.cs:16-103` | 强行换成 compat selection → digest 与 `joinResponse` 不符 → `lan_protocol_version_mismatch` |
| L3 服务端档位门禁 | `protocol-capabilities.ts:145-154`（主门禁）+ `store.ts:601-608`（旧版客户端专用） | 加入者 offer 的 `lanProtocolMin..Max` 必须覆盖房间的 `selectedLanProtocolVersion(=1)`；compat-only 客户端 offer 是 `0..0`（`LanConnectProtocolOffer.cs:17-19`）→ **409 `lan_protocol_version_mismatch`**。旧 generation（`compat_0_3_0_5`）客户端会被解析成 compat，撞上 tail 房 → **409 `protocol_profile_unsupported`** |
| L4 wire | `LanConnectProtocolProfiles.cs:17-29` | 就算骗过前三层，compat 的 `slotId=4bit` 与 tail 的 `2bit` 不同 → 字节错位 |

也就是说，"强制 compat"**不是补丁能在客户端单方面完成的事**；它能做到的唯一形态是：
**让本 Mod 建的房都建成 compat，并让本 Mod 的客户端拒绝一切 tail 房**（把自己从 tail 生态里摘出去）。

### 4.3 两种方案的优缺点（逐条）

#### 方案 A：保留 tail 绑定（复刻官方行为）

| 优点 | 缺点 / 代价 |
|---|---|
| 与官方行为逐行等价，任何未来上游修复都能平滑跟进 | 必须正确复刻 `BindClient`（含 `ReadOnlySpan<byte>` 形参，见 §5.3） |
| tail 房（0.6.x 新协议档，RitsuLib 房主的唯一选择）与 compat 房**都能进** | 需要维护"档位 → 是否绑定"的分支（本质是 3 行 if） |
| 不需要改任何 wire 编码、不需要碰服务端 | 绑定失败时必须显式失败（否则变成 §3.3 的无限等待） |
| 与"未打补丁的房主"兼容性最好：房主是 tail 也能进 | 依赖上游内部成员（`_messageBus`/`_writer` 等），需启动自检 |

#### 方案 B：强制 compat

| 优点 | 缺点 / 代价 |
|---|---|
| 完全不进入 tail 路径，理论上绕开 #3/#4 与扩展帧 | **客户端做不到**（§4.2 L1-L4），只能靠"拒绝 tail 房"实现 |
| 少一条代码路径需要验证 | 丢掉全部 tail 房：玩家看到"该房间协议不支持"，且**房主无法在客户端侧被修复** |
| | compat 房禁止 RitsuLib（`LanConnectProtocolSelection.cs:48-51`），装了 RitsuLib 的房主只能建 tail → 直接互斥 |
| | 与官方 Mod 的默认行为分叉，未来上游更新会再次冲突 |

#### 方案 C：自研"双轨"（补丁自己再维护一套档位协商）

| 优点 | 缺点 / 代价 |
|---|---|
| 理论上可做"按对端能力协商" | 需要重写 `LanConnectProtocolSelection`/digest/服务端契约，等于分叉整个协议层 |
| | 服务端已冻结 profile，客户端无协商余地；自研双轨只会与官方协议打架 |
| | 与本补丁"最小修复"的定位冲突，维护成本远超收益 |

**结论：A。** C 是重复上游已有能力（上游本来就是双轨：compat 房不绑、tail 房绑），B 在客户端不可实现。

### 4.4 对"未打补丁对端"的影响

| 对端形态 | 现状（0.113 上） | 方案 A 的效果 | 方案 B 的效果 |
|---|---|---|---|
| 未打补丁的 0.6.3 **房主**（tail 房） | 能建房、能跑（host 侧不碰坏方法） | ✅ 我们能进 | ❌ 我们进不去（自己拒绝） |
| 未打补丁的 0.6.3 **房主**（compat 房） | 同上 | ✅ 我们能进 | ✅ 我们能进 |
| 未打补丁的 0.6.3 **客户端** | ❌ 完全无法加入（`BeginAsync` 状态机不可 JIT） | 与方案无关，它自己坏 | 同左 |
| 0.113 移植版对端（4 参 ABI） | 正常 | ✅ | 只在 compat 房可用 |

> 注意"未打补丁的客户端"这一行：**补丁修的是客户端加入流程**，所以"我们能不能进别人的房"才是主场景。
> 而 tail 房并不是边缘档位：0.6.x 起它是官方主推的新协议，且 **compat 房禁止 RitsuLib**
> （`LanConnectProtocolSelection.cs:48-51`、`protocol-capabilities.ts:126-135`），
> 装了 RitsuLib 的房主**只能**建 tail 房。方案 B 等于把这些房全部变成不可加入。
> （建房默认值仍是 compat —— `LanConnectLobbyOverlay.cs:6760-6772` —— 所以两种房都会在线上长期共存。）

---

## 5. 问题 3：#3/#4 在正常流程里会不会被调用

### 5.1 调用图（源码级）

**`RejectAndDisconnect`（`:1399-1430`）** —— 唯一调用者 `HandleIncomingFailure`（`:631-643`）：

| 上游触发点 | 性质 |
|---|---|
| `LanConnectTailMessagePatches.cs:735 / 782 / 807` | 入站报文解码/校验失败（协议不匹配、typeId 不符、flow 校验失败） |
| `LanConnectTailMessageRuntime.cs:689-695` | 矩阵帧没有可信的传输上下文 |
| `:822-827` | 同 (sender, channel) 连续两条矩阵帧（不变量被破坏） |
| `:2107-2124` | 屏障 2000ms 超时（对端没发扩展帧） |

→ **全部是"已经出错"的路径。** 正常握手/正常对局不会进。

**`AbortActiveBinding`（`:1618-1681`）** —— 6 个调用点、5 个方法：

| 调用点 | 触发条件 | 正常会话会触发吗 |
|---|---|---|
| `:294` `TryPrepareConcreteOutgoing` catch | prepare 期间任何异常（binding 已终止 / selection 不一致 / `PrepareOutgoing` 抛） | 否 |
| `:344` `CompleteConcreteOutgoing` catch | 同一 writer 二次 publish、header 指纹不符 | 否 |
| `:406` `BeginNativeTransport` | 内容匹配到 **多个** pending（歧义） | 否（需要两个字节完全相同的待发矩阵帧） |
| `:448` `BeginNativeTransport` catch | `ThrowIfTerminated` / 传输绑定不符 / 无收件人 | 否 |
| `:528` `CompleteNativeTransport` catch | ① `vanillaPeerReachable == false`；② 同一 peer 二次消费；③ `RequireOutgoingFlow` 找不到 flow；④ `LanConnectNativeBusSender.Send` 抛 | ① **可能**（见下），②③④ 否 |
| `:549` `HandleNativeTransportFailure` | 原版传输 `SendMessageToClient/Host` 抛异常 | 否 |

> **唯一"非协议错误"的可达路径**是 ①：`AndroidHostTransportPostfix` 用
> `__instance.ConnectedPeerIds.Contains(peerId)` 判定可达性（`LanConnectTailMessagePatches.cs:629`）。
> 如果房主正在广播 `PlayerJoined` / `LobbyBeginRun`（会按 peer 循环发送），而某个 peer 恰好在
> prepare→postfix 之间掉线，`peerReachable=false` → `:466-470` 抛 → `AbortActiveBinding`。
> 这是**竞态**，不是常态，但它确实**不需要任何协议错误**。属于"玩家进进出出"的真实场景。

**`<>c__DisplayClass19_0::<ScheduleDelayedDisconnect>b__1`** —— 触发点是
`LanConnectRemoteLobbyPlayerPatches.cs:197-225` 的**房主踢人按钮**（以及
`LanConnectRoomManagementPanel.cs:427` 的房间管理面板踢人），1.5s 定时器后执行
`hostService.DisconnectClient(targetNetId, NetError.Quit, now: false)`（`:250-272`）。
→ **与加入/握手完全无关**，只在"房主主动踢人"时触发。

### 5.2 精确结论

- **正常联机（无协议错误、无传输失败）不会调用 `AbortActiveBinding` / `RejectAndDisconnect`。**
  `ScheduleDelayedDisconnect` 的闭包只在踢人时调用。
- 因此 #3/#4 的定位是：**"错误处理降级"，不是"功能缺失"**。
  - 不修：错误路径抛 `MissingMethodException`，且**顶替掉原本的异常**（`:294`/`:448`/`:528` 都是
    `catch { Abort(...); throw; }`，`Abort` 自己抛会盖掉原异常），最终异常冒泡到
    `NetMessageBus.SerializeMessage` / `ENetHost.SendMessageToClient` 的 Harmony 前后缀里 —— 位置极其糟糕。
  - 修了：行为与官方一致（终止 binding + 断开受影响 peer），代价是维护一份重写。
- **不需要**为了这两个方法去"重写 abort 语义"——需要的是**逐行移植 + 换签名**。
  重写语义（例如改成"只记日志不断开"）会让 0.113 上的行为与官方分叉，反而更难维护。

### 5.3 与本题直接相关的 P0 缺陷：`BindClient` 的 `ReadOnlySpan<byte>` 形参

发布版 0.6.3 二进制的 `BindClient` 定义（`memberref-probe --defs` 实测输出）：

```
METHODDEF BindClient (class [NetClientGameService], class [LanConnectProtocolOffer],
                      class [LanConnectProtocolSelection],
                      genericinst valuetype [System.ReadOnlySpan`1]<uint8>,
                      genericinst valuetype [System.Nullable`1]<int32>) -> void   [params=5]
```

而当前 `JoinTakeoverFix.BindTailIfRequired`（`Scripts/Join/JoinTakeoverFix.cs:185`）是：

```csharp
bind.Invoke(shared, new[] { (object?)netService, offer, selection, nonce, peerNativeBusTypeId });
```

`nonce` 是 `byte[]`。`MethodInfo.Invoke` **不能**把 `byte[]` 变成 `ReadOnlySpan<byte>`（byref-like 无法装箱）。
实测（`work/companion/tools/reflect-span-probe`）：

```
=== 1) MethodInfo.Invoke with byte[] -> ReadOnlySpan<byte> ===
RESULT: invoke FAILED -> System.ArgumentException:
        Object of type 'System.Byte[]' cannot be converted to type 'System.ReadOnlySpan`1[System.Byte]'.
=== 3) DynamicMethod shim: byte[] -> ReadOnlySpan<byte> ===
RESULT: shim OK (Called=True, len=16)
=== 4) CreateDelegate with a concrete ReadOnlySpan delegate type ===
RESULT: typed delegate OK (Called=True, len=16)
```

→ **今天每一个 tail_v1 房间的加入都会在绑定处抛 `ArgumentException`**，被
`BindTailIfRequired` 外层（当前版本改成显式抛出）变成加入失败。

补充一个次生问题：`BindTailIfRequired` 在 `RunAsync` 里位于 `try` **之外**
（`JoinTakeoverFix.cs:102`，`try` 从 `:106` 开始），所以绑定抛异常时
`finally` 里的 `UnbindTail` / `netService.Disconnect` 都不会执行，`NetClientGameService`
被创建后无人回收。修 nonce 传递时应顺手把它挪进 `try`。

**修法（推荐，已实测）**：用 `DynamicMethod` 生成一个 `byte[] → ReadOnlySpan<byte>` 的调用垫片。
它只依赖 `BindClient` 一个成员（名字 + 5 参，已由 `Reflect.RequireMethodByName(..., 5)` 校验），
且保留上游 `BindClient` 内的 nonce 长度校验与 `selection.Validate`：

```csharp
private static readonly MethodInfo BindClientMethod = /* RequireMethodByName(runtimeType, "BindClient", 5) */;
private static readonly Lazy<Action<object, object, object, object, byte[], int?>> BindClientInvoker =
    new(() => CreateBindClientInvoker(BindClientMethod));

private static Action<object, object, object, object, byte[], int?> CreateBindClientInvoker(MethodInfo bind)
{
    ParameterInfo[] parameters = bind.GetParameters();
    if (parameters.Length != 5
        || parameters[3].ParameterType != typeof(ReadOnlySpan<byte>)
        || parameters[4].ParameterType != typeof(int?))
    {
        throw new MissingMethodException(bind.DeclaringType?.FullName, "BindClient(ReadOnlySpan<byte>, int?)");
    }

    var shim = new DynamicMethod(
        "LanConnectBindClientShim",
        typeof(void),
        new[] { typeof(object), typeof(object), typeof(object), typeof(object), typeof(byte[]), typeof(int?) },
        typeof(JoinTakeoverFix).Module,
        skipVisibility: true);

    ILGenerator il = shim.GetILGenerator();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Castclass, bind.DeclaringType!);                 // 接收者
    il.Emit(OpCodes.Ldarg_1);
    il.Emit(OpCodes.Castclass, parameters[0].ParameterType);          // NetClientGameService
    il.Emit(OpCodes.Ldarg_2);
    il.Emit(OpCodes.Castclass, parameters[1].ParameterType);          // LanConnectProtocolOffer
    il.Emit(OpCodes.Ldarg_3);
    il.Emit(OpCodes.Castclass, parameters[2].ParameterType);          // LanConnectProtocolSelection
    il.Emit(OpCodes.Ldarg_S, (byte)4);                                // byte[] nonce
    il.Emit(OpCodes.Ldc_I4_0);
    il.Emit(OpCodes.Ldarg_S, (byte)4);
    il.Emit(OpCodes.Ldlen);
    il.Emit(OpCodes.Conv_I4);
    il.Emit(OpCodes.Newobj,
        typeof(ReadOnlySpan<byte>).GetConstructor(new[] { typeof(byte[]), typeof(int), typeof(int) })!);
    il.Emit(OpCodes.Ldarg_S, (byte)5);                                // int?
    il.Emit(OpCodes.Call, bind);
    il.Emit(OpCodes.Ret);

    return (Action<object, object, object, object, byte[], int?>)shim.CreateDelegate(
        typeof(Action<object, object, object, object, byte[], int?>));
}
```

调用处改为：

```csharp
if (nonce is not byte[] nonceBytes)
{
    throw new InvalidOperationException("_protocolFlowNonce is not a byte[]; the tail contract changed.");
}

BindClientInvoker.Value(shared, netService, offer, selection, nonceBytes, peerNativeBusTypeId);
```

**备选（零 IL 生成，更短但多依赖一个私有成员）**：改调私有的 `Bind`（`:1432-1458`），它的
nonce 形参就是 `byte[]`，可直接反射调用；但需要再反射一次私有静态 `RequireMessageBus(Type)`
拿 `NetMessageBus`：

```csharp
MethodInfo requireBus = Reflect.RequireMethodByName(runtimeType, "RequireMessageBus", 1);
FieldInfo busField = (FieldInfo)requireBus.Invoke(null, new object[] { typeof(NetClientGameService) })!;
NetMessageBus bus = (NetMessageBus)busField.GetValue(netService)!;
Reflect.RequireMethodByName(runtimeType, "Bind", 7)
       .Invoke(shared, new object?[] { netService, bus, offer, selection, false, nonceBytes, peerNativeBusTypeId });
```

两者都可以；**推荐第一个**（只耦合 `BindClient`，语义最接近官方，且保留上游校验）。

> 顺带纠正旧稿的一处判断：`BindTailIfRequired` 的**策略**（`Profile == TailV1` 才绑、缺 offer/nonce 直接抛）
> 是对的；**错的只是"怎么把 nonce 传进去"**。修这一处即可，不要改策略。

---

## 6. 问题 4：明确建议

### 6.1 决策

> **v1 = 方案 A「保留 tail 绑定」**：
> 补丁按房间冻结的 `_protocolSelection.Profile` 决定是否复刻 `BindClient`；
> `tail_v1` 必绑且绑不上必须硬失败；`compat_4_5_v1` 不绑。
> 同时保留 `TailErrorPathFix`（重定向 #3/#4 到 0.113 签名重写），并补齐它的两个缺陷。

### 6.2 理由（按重要性）

1. **不保留 tail，就等于放弃所有 tail 房**：tail 是 0.6.x 新协议档，且是装了 RitsuLib 的房主的**唯一**
   可建档（compat 禁止 RitsuLib），两种房会长期共存；而"强制 compat"在客户端层面根本无法实现（§4.2），
   只能靠"拒绝 tail 房"来达成。
2. **官方本来就是"双轨"**：`Profile == TailV1` 才绑，compat 房不绑。补丁复刻这个 if 就是最小改动、
   零语义分叉；自研双轨（方案 C）是重复上游能力。
3. **#3/#4 与档位选择无关**：它们在 tail 的**错误路径**上，正常联机不触发（§5.1）。
   为了绕开它们而砍掉 tail，是把"错误处理降级"当成"功能缺失"来治，代价与收益完全不成比例。
4. **对未打补丁对端最友好**：未打补丁的 0.6.3 房主在 0.113 上**能建房、不能加入**，
   所以补丁用户的主要场景就是"进别人的房"。别人的房可能是 tail，方案 B 会直接堵死这条路。
5. **失败模式可控**：绑定失败时显式抛出，玩家看到明确错误，而不是 §3.3 的无限等待。

### 6.3 实施清单（交给下一位工程师照做）

**P0（不做则 tail 房全挂）**

1. 修 `JoinTakeoverFix.BindTailIfRequired` 的 nonce 传递：按 §5.3 的 `DynamicMethod` 垫片（或备选的 `Bind` 反射）；
   同时把该调用挪进 `RunAsync` 的 `try`（当前在 `:102`，`try` 从 `:106` 开始），保证失败时能清理 `netService`。
2. 保留"缺 `_protocolOffer` / `_protocolFlowNonce` 直接抛"的行为——不要退回静默降级。
3. 自检：启动日志必须能打印 `[lobby-enhance] join-flow: tail_v1 extension frame bound.`；
   tail 房加入时，客户端日志里必须出现
   `sts2_lan_connect tail: pending extension registered for LobbyJoinRequest`（`:340`），
   房主日志里必须出现 `holding LobbyJoinRequest ... awaiting extension frame`（`:831`）与紧随其后的
   `extension frame received ... held=yes`（`:722-724`）。三行齐了才说明扩展帧链路通了。

**P1（错误路径）**

4. `TailErrorPathFix` 保留；补 **客户端分支**：当前 `AbortActiveBinding` 重写只在
   `Service is NetHostGameService` 时断开，`else` 分支缺失，客户端 binding 被 abort 后
   **不会** `Disconnect(NetError.ModMismatch)`（上游 `:1668-1680` 有）。补上，否则客户端会在
   "binding 已终止但连接还在"的状态下继续跑。
5. 覆盖踢人闭包：`LanConnectRemoteLobbyPlayerPatches.ScheduleDelayedDisconnect` 的 lambda
   （`<>c__DisplayClass19_0::<ScheduleDelayedDisconnect>b__1`）在 0.113 上不可 JIT，
   当前只会打一条 `kick: delayed ENet disconnect failed: ...MissingMethodException`，
   ENet 层不会真断。修法二选一：
   - 用同样的 transpiler 思路，把该闭包里的 `DisconnectClient(ulong, NetError, bool)` 调用重定向到
     `DisconnectClient(ulong, NetError, bool, string?)`（同栈位，补一个 `ldnull`）；
   - 或者直接 patch `ScheduleDelayedDisconnect`（父方法可读），用 prefix 跳过原实现、
     由我们重写定时器逻辑。
6. `TailErrorPathFix.Apply` 目前对"扫不到调用点"抛异常（`TailErrorPathFix.cs:95-100`）——保留，
   这是正确的 fail-fast。

**P2（可观测性 / 不阻塞发布）**

7. 记录一次"档位决策快照"到日志：`profile`、`carrier`、`selectedLanProtocolVersion`、
   `peerNativeBusTypeId`、`flowNonce` 长度。tail 问题现场排查全靠这几项。
8. `JoinFlow` 接管后丢失了官方的结构化拒绝展示（`TryTakeTailRejection`，
   `LanConnectLobbyManagedJoinFlow.cs:568-580`）：房主明确拒绝时，我们只会拿到
   通用 `ClientConnectionFailedException`。属 UX 降级，可在 `JoinFlow.OnDisconnected` 上
   加一个 postfix 复刻该逻辑（可选）。

### 6.4 明确不要做的事

- ❌ 不要为了"少一条路径"而把 `_protocolSelection` 改写成 compat（会触发 §4.2 L2/L3，且字节错位）。
- ❌ 不要自己实现"档位协商"（服务端已冻结 profile，客户端无协商权）。
- ❌ 不要重写 abort 语义（例如改成只记日志）——会与官方行为分叉；只做"逐行移植 + 换签名"。
- ❌ 不要在没有 `_protocolFlowNonce` / `_peerNativeBusTypeId` 时静默降级——那会退化成 §3.3 的无限等待。

---

## 7. 证据索引

### 7.1 源码

| 主题 | 位置 |
|---|---|
| 客户端绑定点（官方） | `Scripts/Lobby/LanConnectLobbyManagedJoinFlow.cs:77-86`、`:180` |
| 房主绑定点 | `Scripts/Lobby/LanConnectLobbyRuntime.cs:962-977`（`AttachHostedRoom`） |
| `BindClient` / `Bind` / `Unbind` | `Scripts/Protocol/Patches/LanConnectTailMessageRuntime.cs:132-155`、`:1432-1458`、`:157-172` |
| 屏障 hold / 超时 / 释放 | 同上 `:647-712`、`:811-835`、`:94-95`、`:2083-2124`、`:714-809` |
| 拒绝与中止（#3/#4） | 同上 `:631-643`、`:1399-1430`、`:1618-1681` |
| 客户端 flow 绑定兜底 | 同上 `:1926-1944`、`:1963-1976` |
| 发送侧补丁 | `Scripts/Protocol/Patches/LanConnectTailMessagePatches.cs:511-545`、`:618-653`、`:665-689`、`:693-712` |
| 位宽 | `Scripts/LanConnectProtocolProfiles.cs:17-29`、`Scripts/LanConnectConstants.cs:16,26-32` |
| offer 的版本区间 | `Scripts/Protocol/LanConnectProtocolOffer.cs:13-19` |
| selection 校验 | `Scripts/Protocol/LanConnectProtocolSelection.cs:16-103`、`:105-121` |
| 档位决策点 | `Scripts/LanConnectCreateRoomIntent.cs:8-45`、`Scripts/LanConnectHostFlow.cs:69-75`、`:184-197`、`Scripts/Lobby/LanConnectLobbyJoinFlow.cs:65-111`、`Scripts/Lobby/LanConnectDirectJoinFlow.cs:49-62`、`Scripts/Lobby/LanConnectLobbyOverlay.cs:6716-6772` |
| 服务端门禁 | `lobby-service/src/store.ts:601-620`、`:734-766`、`lobby-service/src/protocol-capabilities.ts:124-155`、`lobby-service/src/protocol-profile.ts:13-37` |
| 0.113 侧 ABI | `work/lead/sts2-latest/MegaCrit.Sts2.Core.Multiplayer/NetHostGameService.cs:40,242`、`.../Multiplayer.Game/INetHostGameService.cs:18,45`、`.../Multiplayer.Game/JoinFlow.cs:185-200` |

### 7.2 复现命令（只读）

```powershell
# 1) 断点 #3/#4 的 ABI 实证
dotnet work/companion/tools/memberref-probe/bin/Release/net9.0/memberref-probe.dll `
  "work/lobby/STS2-Game-Lobby/releases/sts2_lan_connect/sts2_lan_connect.dll"

# 2) BindClient 形参含 ReadOnlySpan<byte>
dotnet work/companion/tools/memberref-probe/bin/Release/net9.0/memberref-probe.dll `
  "work/lobby/STS2-Game-Lobby/releases/sts2_lan_connect/sts2_lan_connect.dll" `
  --defs "Sts2LanConnect.Scripts.LanConnectTailMessageRuntime"

# 3) 反射传 span 必然失败 / 垫片可行
dotnet run --project work/companion/tools/reflect-span-probe -c Release
```

### 7.3 旧稿（本文件上一版）的订正

| 旧稿说法 | 订正 |
|---|---|
| "compat 安卓端默认 / tail 桌面端默认" | **错**。默认值是 compat（`GetDefaultCreateProtocolId` 优先 compat，`LanConnectLobbyOverlay.cs:6760-6772`），与平台无关。 |
| "当前 tail 绑定实现正确" | **错**。策略对，反射写法必抛 `ArgumentException`（§5.3）。 |
| "客户端日志 `protocolAligned` 不一致直接不进连接阶段" | **错**。`protocolAligned` 只是 `LanConnectCompatibilityMatrix.DescribeRoomCompatibility` 的诊断串（`LanConnectCompatibilityMatrix.cs:31-35`，日志点在 `LanConnectLobbyJoinFlow.cs:120`），不参与任何门禁。真正的门禁是 §4.2 的 L1-L4。 |
| "`ScheduleDelayedDisconnect` 是拒绝流程里的延迟断开" | **错**。它是**房主踢人**（`LanConnectRemoteLobbyPlayerPatches.cs:197-225`、`LanConnectRoomManagementPanel.cs:427`）。 |
| "两端档位不同也能连（证据 2）" | **误读**。该日志证明对端在线上是 tail_v1（§3.3）。 |
| "`AbortActiveBinding` 触发条件全部是 native 传输失败" | 需补充：`vanillaPeerReachable == false` 可由"广播期间 peer 掉线"触发，不需要协议错误（§5.1）。 |
