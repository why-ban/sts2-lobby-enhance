# 多人存档续玩「必须按原顺序加入」调查（task-11）

> **2026-11-14 结论更正（读本文前必看）**
>
> 本文是在**没有失败日志**的情况下、从现象倒推候选机制写成的，其中的 H1–H5 假设**已被代码级证据取代**。
>
> 真正的原因在 `LanConnectTailMessageRuntime.cs:1275`（线上 0.6.3 反编译）：
>
> ```csharp
> if (!alreadyConnected.Select(accessors.GetId).SequenceEqual(list.Select(accessors.GetId)))
>     throw new InvalidDataException("Loaded-lobby vanilla membership disagrees with the Tail roster.");
> ```
>
> 左边 `alreadyConnected` 来自消息体 `playersAlreadyConnected`，顺序是**加入顺序**；
> 右边 `list` 来自 tail 快照载体，按 `(RealSlotId, PlayerId)` 规范排序，即**存档顺序**
> （`BuildLoadJoinCarriers` 用 `savedPlayers.FindIndex(NetId)` 作 `RealSlotId`）。
> `SequenceEqual` 是逐位比较，同一集合不同顺序必然判为不一致。
>
> 这正好推出用户观察到的规律：第 k 个加入者看到「房主 + 前 k-1 人」；
> 第 1 个不按顺序的人只有 2 项，只要房主在存档里更靠前就能过；第 2 个有 3 项，一旦
> `saveIdx(X) > saveIdx(Y)` 就失败；全员按原顺序时每个前缀都单调，于是全过。
>
> 因此本文 §1.3「读档链上没有按顺序分配槽位的字段」与 §2.2「`ModMismatch` 与顺序无关」
> **在 tail_v1 房间上不成立**：它们只查了 vanilla 字段，漏掉了 tail 自造的 `RealSlotId` 与位置比较。
> H1–H5 作为历史假设保留，但已不是首选解释。
>
> 修复：`load-join-order`（`Scripts/Tail/LoadJoinOrderFix.cs`），在比较前按存档下标重排。
> 可证伪预测（尚未实测）：若房主不是存档里的第一位玩家，则该存档任何人都进不去。

> **当前状态（2026-11-13 收尾）：未修复。** 这是本项目仍在生效的 P2 未决项 ——「多人存档续玩时必须按
> 原玩家顺序加入」。它**阻塞在用户提供的信息**上：需要 §5.1 列出的 4 份日志，以及下面这 9 个问题
> （房间里有几个人、第一个不按顺序的人进去后大厅显示的
> 角色是否正确、错误提示在哪一端、房主是否一直是同一人…）。拿到日志后按 §5.2 的关键字对照表逐行判定，
> 再按 §7.4 的建议在**客户端 UI 层**修（刷新列表 + 可辨识选槽 + 取消自动接管）。
> 同一个 join 流程上还有另一个独立未决项，见 §8。

> **调查性质**：纯离线代码调查，**没有任何运行时日志**。本文所有"机制"都带 `文件:行号`；
> 凡是推不出来的，我明确写"推不出来，需要日志"，不编故事。
>
> **一句话结论**：用户回忆的两条文案（`wire_cache` / `ModMismatch`）**在代码上都不可能由"加入顺序"产生**（§2 有证明）；
> 真正能产生"第一个不按顺序的人能进、第二个进不去"的只有**续局槽位被占用**这一条链（§3 H1/H2），
> 但它弹出的文案是**槽位类**提示，不是"不匹配"。所以要么用户记错了文案，要么两件事同时发生。
> 区分方法见 §5（日志关键字）。

---

## 1. 候选文案全清单（含触发条件与"能否顺序相关"初判）

用户能看到的"不匹配"提示只有三个来源：**宿主模组自己的中文文案**、**游戏 `NErrorPopup` 的本地化 key**、
**大厅服务端错误码**。逐条列全：

### 1.1 宿主模组（LAN Connect）自己的文案

| # | 文案（原文） | 位置 | code / 触发条件 | 初判 |
|---|---|---|---|---|
| M1 | `联机数据版本不匹配，无法加入该房间。` | `LanConnectProtocolUiMessages.cs:37` | `wire_cache_mismatch`；来自大厅服务端 `409 wire_cache_signature_mismatch`（`LanConnectProtocolFailureMapper.cs:11-12` 做码名映射） | 见 §2.1 |
| M2 | `联机内容或 Mod 数据表不一致，网络编码不兼容，无法安全加入。…当前签名/房主签名/位宽…` | `LanConnectWireCacheHandshakeDecision.cs:BuildMismatchMessage` | 客户端握手期 `ValidateInitialMessage` → `ValidateWireCacheCompatibility` 判 `Mismatch`（`LanConnectLobbyManagedJoinFlow.cs:375-414`），上报 `ConnectionFailureReason.ModMismatch` | 见 §2.1 |
| M3 | `双方的内容/MOD 表产生了不同的网络编码，无法安全联机。…` | `LanConnectCompatibilityMatrix.cs:50-51` | 大厅服务端 `wire_cache_signature_mismatch` 的另一种呈现路径（`DescribeJoinFailureCode`） | 见 §2.1 |
| M4 | `MOD 不一致。 你缺少：… 房主缺少：…` | `LanConnectLobbyModMismatchFormatter.cs:31-34` | 服务端 `mod_mismatch` / `mod_version_mismatch`（`LanConnectLobbyOverlay.cs:7232-7233`），或客户端握手期 Mod 列表差集非空（`LanConnectLobbyManagedJoinFlow.cs:331-344`） | 见 §2.2 |
| M5 | `游戏版本不匹配，无法加入房间。房主版本：X；当前版本：Y。` | `LanConnectLobbyManagedJoinFlow.cs:436` | 客户端握手期版本号不等（`:416-427`） | 否（见 §2.2） |
| M6 | `LAN 协议版本不匹配，无法继续连接。` | `LanConnectProtocolUiMessages.cs:38` | `lan_protocol_version_mismatch`（档位/协议版本） | 否 |
| M7 | `当前客户端不支持该房间的联机协议。` | `LanConnectProtocolUiMessages.cs:14` | `protocol_profile_unsupported` | 否 |
| M8 | `所选续局角色已被其他玩家接管，或当前没有可接管角色。` | `LanConnectCompatibilityMatrix.cs:49` | 服务端 `save_slot_unavailable`（`store.ts:508,519`） | **是（§3 H1）** |
| M9 | `这是续局房间，需要先选择一个可接管角色。` | `LanConnectCompatibilityMatrix.cs:47` | 服务端 `save_slot_required`（`store.ts:521-523`） | 是（间接） |
| M10 | `所选续局角色不存在。` | `LanConnectCompatibilityMatrix.cs:48` | 服务端 `save_slot_invalid`（`store.ts:513-516`） | 是（间接） |
| M11 | `加入票据无效。` / `加入票据已使用。请重新加入房间获取新票据。` | `store.ts:922,931` | `invalid_ticket` / `ticket_already_redeemed` | **是（§3 H3）** |
| M12 | `该续局房间当前没有可接管角色。` | `store.ts:508` / `LanConnectLobbyOverlay.cs:4744` | 可用槽位为 0 | 是（§3 H1） |

### 1.2 游戏 `NErrorPopup` 的本地化 key（中文文案在 pck 里，离线看不到原文）

映射表在 `MegaCrit.Sts2.Core.Nodes.CommonUi/NErrorPopup.cs:247-290`（key 前缀 `main_menu_ui:NETWORK_ERROR.`）：

| # | loc key | 对应 `NetError` | 语义（注释） | 触发条件 | 初判 |
|---|---|---|---|---|---|
| G1 | `MOD_MISMATCH.body` | `NetError.ModMismatch` | 双方 Mod 列表不一致 | 握手 `HandshakeManager.cs:119-125`；或 tail 拒绝 `RejectAndDisconnect` 主动发 `ConnectionFailureReason.ModMismatch`（`LanConnectTailMessageRuntime.cs:1411-1423`） | 见 §2.2 |
| G2 | `VERSION_MISMATCH.body` | `VersionMismatch` / `InvalidHandshake` | 版本或 ModelDb hash 不一致 | `HandshakeManager.cs:114-118`、`:126-130` | 见 §2.2 |
| G3 | `NOT_IN_SAVE_GAME.body` | `NotInSaveGame` | **房主读档了，加入者不在存档里** | `LoadRunLobby.cs:185`、`:390-393` | **是（§3 H1）** |
| G4 | `INVALID_JOIN.body` | `InvalidJoin` | 非法的加入消息 | `LoadRunLobby.cs:234`（收到 `ClientLobbyJoinRequestMessage` 而不是 load-join） | 否 |
| G5 | `RUN_IN_PROGRESS.body` | `RunInProgress` | 局已开始 | `LoadRunLobby.cs:383-386` | 否 |
| G6 | `TIMEOUT.body` | `HandshakeTimeout` / `LobbyJoinTimeout` | 超时 | `LoadRunLobby.cs:418`、`HandshakeManager.cs:227` | 否 |

> 用户说的"联机数据不匹配"最贴近 **M1**（字面几乎一致）、"模组不匹配"最贴近 **M4 / G1**。
> 但注意：**中文原文在 pck 里，离线无法确认 G1/G2 的中文措辞**——这条不确定性必须在日志里消掉。

### 1.3 关键排除：`SerializablePlayer` 没有 `slotId`

`MegaCrit.Sts2.Core.Saves.Runs/SerializablePlayer.cs` 只有 `NetId`，**没有 slotId**；
`LoadRunLobby` 用的 `LoadRunLobbyPlayer`（`MegaCrit.Sts2.Core.Entities.Multiplayer/LoadRunLobbyPlayer.cs`）
同样只有 `id / isModded / isReady` 三个字段；`RunManager.SetUpSavedMultiplayer` 也只是把
`lobby.Players` 逐个映射成 `RunLobbyPlayer{id, isModded}`（`RunManager.cs:446-450`）。

**所以"读档续玩"这条链上根本不存在"按加入顺序分配槽位"的字段**。
顺序只能通过**"谁用了哪个 netId"**间接影响结果——这是后面所有假设的共同基础。

---

## 2. 两条候选文案各自的「顺序依赖」分析（重点）

### 2.1 `wire_cache_mismatch`（M1 / M2 / M3）

**结论：在代码上不可能与加入顺序相关。** 三条路径分别证明：

#### 路径 A：大厅服务端比对（→ M1 / M3）

`lobby-service/src/store.ts:658-676`：

```js
if (room.wireCacheSignatureV1 && requestedWireCacheSignatureV1) {
  if (room.wireCacheSignatureV1 !== requestedWireCacheSignatureV1) → 409 wire_cache_signature_mismatch
} else if (warnWhenSignatureMissing && (...)) { warn }   // 任一缺失 = 跳过比对，只告警
```

- 左边 `room.wireCacheSignatureV1` = 房主**建房时**冻结的签名：
  `LanConnectHostFlow.cs:530`（`ResolveCreateRoomProtocolIdentity`，取值 = 房主本机
  `LanConnectWireCacheDiagnostics.GetCurrentResult().Snapshot?.Signature`）；直连建房在 `:69-72`。
- 右边 = 加入者**自己上报**的签名：`LanConnectModPreflightCoordinator.cs:53`
  （同样是该机器本机的 `GetCurrentResult().Snapshot?.Signature`）。

两个值都只由 `ModelIdSerializationCache` 的静态表算出（见路径 C），**与人是谁、第几个进完全无关**。

#### 路径 B：客户端握手期比对（→ M2）

`LanConnectLobbyManagedJoinFlow.cs:375-414` → `LanConnectWireCacheHandshakeDecision.Evaluate`：
拿「本机 capture」比「房主 `PeerVersionInfo.otherMods` 里的哨兵 token」。两者都是**每台机器固定的静态值**。

#### 路径 C：签名本身只由静态表决定（决定性证据）

- `LanConnectWireCacheSignatureV1.Compute`（`LanConnectWireCacheSignatureV1.cs:14-50`）的输入是
  `categoryTable / entryTable / epochTable / propertyTable` + 四个位宽，全部来自 `ModelIdSerializationCache`。
- `ModelIdSerializationCache.Init()`（`0.113 ModelIdSerializationCache.cs:68-132`）遍历的是
  **`ModelDb.All`（全部已注册模型类型）**，与"本局有谁、谁是哪个角色"无关；`Hash` 与四个位宽在此固定。
- `LanConnectWireCacheSnapshotCapture.Capture`（`:5-42`）还会做**双 pass 一致性检查**，
  两次不一致直接抛异常 → 该端变成 `LocalUnavailable` → `Evaluate` 判 `LocalUnavailable` →
  `IsAllowed: true`（`LanConnectWireCacheHandshakeDecision.cs` 第一个分支）。**不稳定只会放行，不会误判为 mismatch。**
- 房主广告 token 的位置：`LanConnectPeerVersionInfoPatches.cs:58-96`（`PeerVersionInfo.LocalDefault` postfix），
  同样是静态值，且**房主侧只记日志、不做拦截**（`:117-119` 注释明说 enforcement 在客户端）。

> **因此：如果用户真的看到 M1/M2/M3，那不是"顺序"造成的。**
> 只可能是某台机器的 Mod/内容集合与房主不同（真·不一致），而"总是第二个人失败"是巧合
> （比如总是同一个人第二个进，而他的 Mod 集合确实不同）。

### 2.2 `ModMismatch`（M4 / G1 / G2）

**结论：同样不可能与加入顺序相关。** 四条产生路径：

| 路径 | 位置 | 比的是什么 | 为什么与顺序无关 |
|---|---|---|---|
| P1 游戏握手 | `HandshakeManager.cs:114-130` | `PeerVersionInfo.version`、`gameplayAffectingMods` 差集、`idDatabaseHash` | 三个字段都来自 `PeerVersionInfo.LocalDefault()`（`PeerVersionInfo.cs`）：`version=NGame.GetGameVersion()`、`idDatabaseHash=ModelIdSerializationCache.Hash`、mods=`ModManager.GetGameplayRelevantModNameList()`。**全是本机静态属性** |
| P2 模组握手期自检 | `LanConnectLobbyManagedJoinFlow.cs:320-350` | `LanConnectBuildInfo.GetModList()` vs 房主 `GameplayAffectingMods` | 同上 |
| P3 ModelDb hash | `LanConnectLobbyManagedJoinFlow.cs:352-365` | 房主 `idDatabaseHash` vs `ModelIdSerializationCache.Hash` | 同上（见下"证伪 Lead 假设"） |
| P4 tail 拒绝 | `LanConnectTailMessageRuntime.cs:1399-1430` | 对端 offer/roster/frame 校验 | 只在 tail_v1 房、且是**协议帧**校验失败；与槽位无关 |

#### 特别验证 Lead 提出的"二次症状"链路：槽位错位 → 角色/模型不匹配 → ModelDb hash 失败

**这条链路在代码上不成立**，证据三条：

1. `ModelIdSerializationCache.Hash` 的输入是 **`ModelDb.All`（全部已注册模型类型）**
   （`ModelIdSerializationCache.cs:72`：`ContentSorter<ModelId>.Sort(ModelDb.All.Select(m => m.GetType()), ModelDb.GetId)`），
   以及这些类型的**属性名表**（`:96-106`）。**不含"本局玩家/角色"任何信息**。
2. 存档里的玩家数据是 `SerializablePlayer`（`NetId/CharacterId/…`），只在 `SerializableRun` 里；
   它在 `SetUpSavedMultiplayer`（`RunManager.cs:436-453`）才被使用，**晚于**握手（握手在 `InitialGameInfo` 阶段）。
3. 客户端 `ValidateInitialMessage` 的 ModelDb 校验发生在**收到 `InitialGameInfo` 之后、发 `ClientLoadJoinRequestMessage` 之前**
   （`LanConnectLobbyManagedJoinFlow.cs:109-126`）——**那时客户端还没拿到存档，更没被分配任何角色**。

> 也就是说：**"被塞进错误的人物槽"不可能改变 ModelDb hash 或 Mod 列表**。
> 若用户看到的是 G2（VersionMismatch），根因是**版本号或 ModelDb 内容真的不同**（例如一人装了内容 MOD、
> 或游戏版本不一致），与顺序无关。

### 2.3 §2 小结

| 候选 | 能否推出顺序依赖 | 结论 |
|---|---|---|
| M1 `wire_cache_mismatch` | ❌ 不能（§2.1） | 若真出现，是**真·内容不一致**，"第二个人"是巧合 |
| M2/M3 wire-cache 握手 | ❌ 不能（§2.1） | 同上 |
| M4/G1 `ModMismatch` | ❌ 不能（§2.2） | 同上 |
| G2 `VersionMismatch`/ModelDb | ❌ 不能（§2.2 + 证伪） | 同上 |
| **M8/M12 槽位类** | ✅ **能** | 见 §3 H1 |
| **M11 票据类** | ✅ 能 | 见 §3 H3 |
| **G3 `NotInSaveGame`** | ✅ 能 | 见 §3 H1/H4 |

**这正好回答 Lead 的判定标准**：两条候选文案都推不出顺序依赖 ⇒ 它们要么是**二次症状的误记**，
要么根本不是这次的提示。真正带顺序依赖的是**槽位/票据**这一族。

---

## 3. 能解释"第一个不按顺序的人能进、第二个不能"的机制

前提（读档续玩的正常流程）：

```
房主读档 → StartLoadedRunAsLanHostAsync / AutoPublisher
        → 建房并把 savedRun 发到大厅服务
          slots = run.Players.OrderBy(NetId)          （LanConnectMultiplayerSaveRoomBinding.cs:431-434）
          connectedPlayerNetIds = [房主]              （:430）
        → 客户端点加入 → 选槽（或自动接管）→ 用该槽 netId 连 ENet
        → 房主 LoadRunLobby 校验 netId ∈ 存档          （LoadRunLobby.cs:182-187, :388-394）
        → 房主心跳每 10s 上报 connectedPlayerNetIds   （LanConnectLobbyRuntime.cs:1330-1344）
        → 服务端把槽位标记为已占用                     （store.ts:831-835）
```

### H1（最可能）：**续局槽位被"先到者"占用，真主找不到自己的槽**

**代码依据**

1. 可用槽位 = `slots` 里不在 `connectedPlayerNetIds` 的那些：
   `store.ts:1502-1509`（服务端）/ `LanConnectLobbyOverlay.cs:6898-6907`（客户端用 `slot.IsConnected`）。
2. `connectedPlayerNetIds` = **房主 netId + 所有 ENet 已连接的 peer**
   （`LanConnectLobbyRuntime.cs:3703-3718`）——**注意是"已连接"，不是"已成功进局"**。
3. 加入时若选中的槽已被占用 → `409 save_slot_unavailable`（`store.ts:518-520`）→ M8。
4. 若可用槽位已为 0 → `409 save_slot_unavailable`（`store.ts:507-509`）→ M12。
5. 客户端侧：可用槽位 = 1 时**自动接管、不弹窗**（`LanConnectLobbyOverlay.cs:4748-4767`），
   为 0 时直接拒绝（`:4742-4745`）。

**为什么能解释非对称**

- 谁先用某个槽的 netId 连上，谁就"占住"那个槽（心跳把它写进 `connectedPlayerNetIds`）。
- 房主侧**只校验 netId 是否在存档里**（`LoadRunLobby.cs:182-187`），**不校验"你是不是这个槽的主人"**
  —— 也就是说**抢别人的槽一定能进**。
- 真正的主人回来时，他的槽已被标记占用：要么被推到别的槽（拿别人的角色），
  要么在只剩 0 个槽时**被硬拒绝**（M8/M12）。
- 这正是"第一个不按顺序的人能进，第二个不能"。

**这条机制要成立，需要一个前置条件：先到者"选错了槽"。** 触发因素（都在代码里）：
- 槽位标签只有角色名+玩家名，名字缺失时**只剩角色名**（`LanConnectLobbyOverlay.cs:5411-5420`），
  同角色多人时完全无法区分；
- 玩家名来源是**内存目录** `LanConnectLobbyPlayerNameDirectory`（进程内静态表，重启即空），
  只能靠持久化的 `PlayerNames` 兜底（`LanConnectMultiplayerSaveRoomBinding.cs:599-624`）；
- 客户端看到的房间摘要来自**列表缓存**（刷新间隔 8s，`LanConnectConstants.cs:50`），
  而 `connectedPlayerNetIds` 只随**房主心跳 10s** 更新（`:52`）——**两人在 10s 内先后加入时，后者看到的是过期槽位列表**。

**证伪方法（日志）**
- 客户端：`sts2_lan_connect overlay: join requested roomId=…, desiredSavePlayerNetId=…`（`LanConnectLobbyOverlay.cs:4555`）
  → 看 B 选的是不是自己的 netId。
- 客户端：`sts2_lan_connect join_flow: using selected saved-run slot netId=…`（`LanConnectLobbyJoinFlow.cs:405`）
  → 看实际用于连线的 netId。
- 服务端/客户端错误码：`save_slot_unavailable` / `save_slot_invalid` / `save_slot_required`。
- 房主：`LoadRunLobby` 的 `Received ClientLoadJoinRequestMessage for {senderId}`（`LoadRunLobby.cs:188`）
  → 对照存档里的 4 个 netId，看 B 用的是不是 B 自己的。

### H2：**陈旧房间摘要 + 两人抢同一个槽**（H1 的加速版）

**代码依据**：`GetAvailableSavedRunSlots` 用的是**房间列表快照**里的 `slot.IsConnected`
（`LanConnectLobbyOverlay.cs:6898-6907`），不是实时查询；列表 8s 刷一次、心跳 10s 一次。

**为什么能解释非对称**：两人在同一窗口内加入 → 都看到同一个"空闲"槽 → 先到者拿票成功，
后到者被 `store.ts:518-520` 拒绝（M8）。**这是"第一个能进、第二个不能"的最直接复现。**

**与 H1 的区别**：H2 不需要"选错槽"，只需要"两人选同一个槽"。
若用户的四人每次都按"列表第一个"点，就等价于 H1+H2 同时成立。

**证伪方法**：两条 join 请求的 `desiredSavePlayerNetId` 是否相同；两条请求的时间差是否 < 10s。

### H3：**控制通道票据互废（同 netId 两人）**

**代码依据**：`store.ts:1276-1286` `invalidateOtherSlotTickets` —— 当某个 `playerNetId` 的控制通道绑定成功，
**同房间内同 netId 的其他未使用票据会被直接删除**；之后再用该票 → `401 invalid_ticket`（`store.ts:920-923`）。

**为什么能解释非对称**：先到者先兑换控制通道 → 后到者手里的票被作废 → 后到者拿不到连接计划。
**完全由先后顺序决定。**

**证伪方法**：日志里搜 `invalid_ticket` / `ticket_already_redeemed`；
或搜两条 `control` 连接是否用了同一个 `playerNetId`。

### H4：**唯一剩余槽自动接管，最后一人被静默改派**

**代码依据**：`LanConnectLobbyOverlay.cs:4748-4767` —— 可用槽位恰好 1 个时**不问用户直接接管**。

**为什么能解释非对称**：若前面有人选错槽，剩下的人会被**静默塞进别人的角色**（不报错），
直到最后一人的可用槽为 0 → M12 硬拒绝。表现为"最后一个人完全进不去"。

**证伪方法**：日志里看最后一人是否没有 `OpenResumeSlotDialog` 而直接 `using selected saved-run slot netId=…`，
且该 netId 不是他本人的。

### H5：**"已连接但没进局"的 peer 也占槽**

**代码依据**：`GetConnectedPlayerNetIds()` 统计的是 `_connectedPeerIds`（ENet 层，`LanConnectLobbyRuntime.cs:3703-3718`），
一个 peer 只要 ENet 连上（还没发 `ClientLoadJoinRequestMessage`）就会被算作已占用。

**为什么能解释非对称**：某个玩家尝试失败/重试时，**失败的尝试也会短暂占槽**，
把后来者挤到别的槽或挤成 0 槽。

**证伪方法**：`connectedPlayerNetIds` 是否出现"没有成功进局的人"的 netId；
房主日志里是否有 `ClientDisconnected` 紧跟 `ClientConnected`。

---

## 4. 我明确没能证明的部分（诚实边界）

1. **用户到底看到的是哪条文案** —— 中文原文在 pck 里，离线读不到 `MOD_MISMATCH.body` /
   `NOT_IN_SAVE_GAME.body` / `VERSION_MISMATCH.body` 的中文措辞。**必须靠日志里的 `NetError` 名字确认。**
2. **`ModelIdSerializationCache.Init()` 是否可能被调用两次** —— `Init()` 没有重入保护
   （`ModelIdSerializationCache.cs:68-132`），`CachePropertiesForType` 是否去重我没读完（`:157-190`）。
   若可能重入且属性表会增长，签名会漂移；但 `LanConnectWireCacheSnapshotCapture` 的双 pass 检查会把
   不稳定状态变成 `LocalUnavailable`（**放行**），所以**这条不会产生"不匹配"提示**，最多让校验失效。
   结论：**不影响本次判断**，但值得单独记一笔。
3. **`LoadRunLobby` 是否接受"抢槽"** —— 代码只看 `netId ∈ Run.Players`（`LoadRunLobby.cs:182-187`），
   不看角色归属。这是**代码事实**，但"用户是否真的这么操作"需要日志。
4. **同一台机器上"第二个不匹配"是否可能是 ENet 层重复 netId 被拒** —— 我查了
   `HandshakeManager`（`0.113`）没有重复 netId 检查；`ENetHost` 侧如何处理同 netId 两个 peer 我没读完。
   若要确认，日志里看第二条连接是否在 `handshake` 阶段就断。

---

## 5. 需要用户提供的日志 + 关键字对照表

### 5.1 要哪些文件

1. **失败那台机器（B）** 的 `godot.log` / `sts2.log`（`user://logs/`）；
2. **房主（A）** 的同一份日志（关键：房主侧才能看到 `ConnectedPlayerNetIds` 与 load-join 校验）；
3. 如果能导出：LAN Connect 的「调试报告」（`LanConnectDebugReport` 会打印
   `persistent_lan_client_net_id`、`selected_room_connected_player_net_ids`，见 `LanConnectDebugReport.cs:60,99`）。

### 5.2 关键字 → 判定表（拿到日志直接对照）

| 关键字（原样搜） | 出处 | 说明 / 判定 |
|---|---|---|
| `join_flow: using selected saved-run slot netId=` | `LanConnectLobbyJoinFlow.cs:405` | **最关键**：这一行说明 B 用哪个 netId 连线。与存档 netId 对照即可判 H1/H4 |
| `join_flow: no saved-run slot selected` | `:410-411` | 说明**没走槽位选择**，用的是持久 netId → 若不在存档里必然 G3 |
| `overlay: join requested roomId=` | `LanConnectLobbyOverlay.cs:4555` | 含 `desiredSavePlayerNetId`，判 H1/H2 |
| `save_slot_unavailable` / `save_slot_invalid` / `save_slot_required` | `store.ts:508,515,519,522` | 命中即 **H1/H2**（槽位类），不是"不匹配" |
| `invalid_ticket` / `ticket_already_redeemed` | `store.ts:922,931` | 命中即 **H3**（票据被同 netId 抢先兑换） |
| `Received ClientLoadJoinRequestMessage for` | `LoadRunLobby.cs:188` | 房主侧确认 B 的 netId 已通过"在存档里"校验 |
| `sent ClientLoadJoinRequestMessage but they are not in the loaded run` | `LoadRunLobby.cs:184` | 命中即 **G3 NotInSaveGame**（netId 不在存档） |
| `not in the loaded game` | `LoadRunLobby.cs:392` | 同上（更早的 `OnConnectedToClientAsHost` 分支） |
| `wire_handshake join:` / `wire_handshake host:` / `wire_handshake advertise:` | `LanConnectLobbyManagedJoinFlow.cs:397`、`LanConnectWireCacheHandshakeGate.cs:56`、`LanConnectPeerVersionInfoPatches.cs:68` | 命中且 `decision=mismatch` → **M2**（真·内容不一致，与顺序无关） |
| `Mod mismatch. MissingOnLocal=` | `LanConnectLobbyManagedJoinFlow.cs:339-340` | 命中即 **M4/G1**（真·Mod 差异） |
| `ModelDb hash mismatch. Host=` | `:356-357` | 命中即 **G2**（真·ModelDb 差异） |
| `Version mismatch. Remote:` / `Our version … but our Model ID hash does not!` | `HandshakeManager.cs:116,128` | 命中即游戏原生版本/hash 不一致 |
| `Mismatch in gameplay-relevant mods with the remote!` | `HandshakeManager.cs:123` | 同上（原生 Mod 校验） |
| `Wire mismatch` / `duplicate slotId` | —— | 我**没有**在代码里找到这样的字符串；若日志出现说明另有来源 |
| `save_binding: persisted` / `continue_run_publish` | `LanConnectMultiplayerSaveRoomBinding.cs:247`、`LanConnectContinueRunLobbyAutoPublisher.cs:335` | 确认房间确实是"续局房间"、`saveKey` 与槽位来源 |
| `starting loaded multiplayer run via ENet override. players=[…]` | `LanConnectMultiplayerSaveCompatibility.cs:209-210` | **房主存档里的 4 个 netId 原始值**，与 B 选的 netId 对照 |

### 5.3 一句话判定法

> 日志里只要出现 `save_slot_*` 或 `invalid_ticket` → **是槽位/票据问题（H1/H2/H3）**，与"联机数据不匹配"无关，
> 属于 UX 与自动选槽缺陷。
> 日志里出现 `wire_handshake … decision=mismatch` / `Mod mismatch` / `ModelDb hash mismatch` → **是真·内容差异**，
> 与顺序无关，"第二个人失败"是巧合（查该玩家的 Mod 列表 / 游戏版本）。
> 两者都没有 → 需要更完整的日志（含 `senderId` 与存档 netId 对照）。

---

## 6. 结论

1. **Lead 指出的两条候选文案（`wire_cache_mismatch`、`ModMismatch`）在代码上都无法由"加入顺序"产生**（§2 三条证据）。
   特别是 Lead 提出的"槽位错位 → 角色/模型不匹配 → ModelDb hash 失败"链路**不成立**：
   `ModelIdSerializationCache.Hash` 只覆盖 `ModelDb.All` 静态注册表，且校验发生在客户端拿到存档**之前**（§2.2）。
2. **唯一能解释该非对称性的机制是续局槽位占用链**（H1/H2/H3/H4）：
   先到者用某个槽 netId 连上即占住该槽，房主只校验"netId 在存档里"而不校验归属，
   真主人回来时槽位已满/已占 → 硬拒绝。
3. **最可能的真实情况**：用户看到的提示是**槽位类**（M8/M12，或 G3 `NOT_IN_SAVE_GAME`），
   记忆里被归并成"不匹配"；根因是**槽位选择靠人眼 + 列表/心跳有 8~10s 陈旧窗口 + 名字缺失时无法区分**。
4. **确认它只需要一份日志**：`join_flow: using selected saved-run slot netId=` + 是否有 `save_slot_*`。
   两条一起看即可定性，不需要复现。

---

## 7. 修复设计

> **状态：待日志确认。** 本节基于 §3 的机制假设（H1/H2/H3/H4）给出设计，尚未被真机日志证实。
> §7.6 给出"日志命中哪一行 ⇒ 本设计是否直接可用"的判定表。**未修改任何代码。**

### 7.0 先纠正一个会改变设计方向的事实：系统里没有"槽主"这个身份

这一条必须先说清楚，否则后面的层选会选错。

1. **身份就是 netId。** 房主对加入者的全部认知只有 `senderId`：
   `LoadRunLobby.cs:182-187` 只做 `Run.Players.FindIndex(p => p.NetId == senderId)`。
2. **房主拿不到 `desiredSavePlayerNetId`。** 该字段只发给大厅服务端（`POST rooms/:id/join`，
   `LanConnectLobbyApiClient.cs:164`），而房主收到的 `ClientLoadJoinRequestMessage`
   是一个**空结构体**（`[StructLayout(Size = 1)]`，无任何字段，见
   `MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby/ClientLoadJoinRequestMessage.cs`）。
   → **"房主侧收到 `desiredSavePlayerNetId` 时校验"这条在协议上做不到。**
3. 由此可得：**"占用槽 X" 等价于 "以 netId X 连上"**，
   而"主人在线" 等价于 "netId X 在 `connectedPlayerNetIds` 里"。

> **所以「主人在线则不可被抢」这条规则其实已经实现了**（`store.ts:518-520` 用
> `connectedPlayerNetIds` 判重，`store.ts:1502-1509` 算可用槽）。
> 它缺的不是"规则"，而是两点：
> - **原子性**：票签发时**不占用**槽位（`joinRoom` 全程只读，`store.ts:497-567`），
>   而票的默认有效期是 **120s**（`config.ts:118` `TICKET_TTL_SECONDS=120`）；
> - **可辨识性**：选槽靠人眼看标签（`FormatSlotLabel` 名字缺失时只剩角色名）。
>
> 这两点才是要修的东西。

### 7.1 三层逐层评估

#### L1 客户端侧：过滤"主人当前在线"的槽

**现状：已经做了。** `GetAvailableSavedRunSlots` 就是按 `slot.IsConnected` 过滤
（`LanConnectLobbyOverlay.cs:6898-6907`），`IsConnected` 由服务端按 `connectedPlayerNetIds` 重算
（`store.ts:831-835`）。**单纯"再加一遍过滤"没有增量收益。**

真正有增量的客户端改动（治标，但有实效）：

| 改动 | 位置 | 改动量 | 说明 |
|---|---|---|---|
| C1 加入前强制刷新一次房间列表 | `BeginJoinRoomAsync`（`LanConnectLobbyOverlay.cs:4730-4739`）里先 `GetRoomsAsync` 并按 `RoomId` 取最新 | ~15 行 | 把 **8s 列表窗口降到 0**（接口已存在：`LanConnectLobbyApiClient.cs:110`；无单房间 GET，只能拉全表） |
| C2 选槽框显示可辨识信息 + 高亮"我的槽" | `OpenResumeSlotDialog`（`:5283-5338`）、`FormatSlotLabel`（`:5411-5420`） | ~30 行 | 标签加 **netId 后 4 位**；本机持久 netId（`LanConnectConfig.GetOrCreateClientNetId`）匹配的槽**默认选中并标注"这是你"** |
| C3 取消"只剩 1 槽自动接管" | `:4748-4767` | ~10 行 | 见 §7.3 |

- **改动量**：小（全在 overlay 内，约 50–80 行，不碰协议）。
- **对未升级节点 / 未打补丁对端**：**零影响**。纯本地请求与 UI；不改 wire、不改服务端契约。
  未打补丁的对端看到的是同样的房间数据，行为不受影响。
- **效果边界**：C1 只能消除 **列表** 陈旧；**心跳**陈旧（10s，`LanConnectConstants.cs:52`）依然存在
  ——刷新列表拿到的仍是服务端 10s 前的 `connectedPlayerNetIds`。**所以 L1 治不了并发抢槽。**

#### L2 房主侧：校验"该槽主人是否仍连接"

**按 Lead 原方案：不可行**（§7.0 第 2 条：房主收不到 `desiredSavePlayerNetId`）。

退而求其次能做的只有"**重复 netId 拒绝**"：房主在 `HandleClientLoadJoinRequestMessage`
（`LoadRunLobby.cs:173-224`）与 `OnConnectedToClientAsHost`（`:374-406`）里加一句
"`Players` 里已有该 netId ⇒ 拒绝"。但：

- 它**救不回真主人**：抢槽者已经以该 netId 占位，被拒的是后来的真主人 —— 与用户诉求相反；
- 若改成"踢掉先来的"，等于给恶意抢槽开后门；
- 它唯一的价值是**防御性**：避免 roster 出现重复 netId 后，`RunLobby.Players`（`RunLobby.cs:68`）
  与 `RunState.Players` 出现一对多映射，把"静默错乱"变成"明确报错"。

- **改动量**：小（一个 Harmony patch，~20 行）。
- **兼容性**：这是**行为变更**（上游没有此校验），且**只在我们补丁安装的房主上生效**；
  未打补丁房主无此保护。文档/发布说明必须标注，否则会被当成"补丁引入了新拒绝"。
- **结论：不作为主修层。**

#### L3 大厅服务端：槽位原子预留

**这是唯一能真正治本的层**，因为"票的签发点"是系统里唯一能**原子**决定"这个槽给谁"的地方。

设计要点（待日志确认后实施）：

1. `joinRoom` 校验通过后**立即预留**该槽：新增
   `room.savedRun.reservedSlots: Map<netId, {ticketId, expiresAt}>`。
2. 可用槽判定改为 `!connectedPlayerNetIds.has(netId) && !reservedSlots.has(netId)`
   （`store.ts:1502-1509` 与服务端 join 校验 `store.ts:505-523` 同步改口径）。
3. 预留的**释放条件**（缺一不可，否则会永久占槽）：
   - 票过期（`expiresAt`，默认 120s，`config.ts:118`）；
   - 该 netId 首次 ENet 连接成功（转为 `connectedPlayerNetIds` 后释放预留）；
   - 房间关闭 / 房主踢人 / 控制通道票据被 `invalidateOtherSlotTickets`（`store.ts:1276-1286`）作废。

- **改动量**：中（`store.ts` + `app.ts` 少量接线 + 3~4 个测试文件；**无 wire 改动**）。
- **对未升级节点的兼容性**：协议不变，**新旧客户端都能用**；
  未升级节点自动退回现状（只靠 10s 心跳），**不报错、不劣化** —— 属优雅降级。
- **对未打补丁对端的兼容性**：零影响（服务端行为对客户端透明）。
- **现实性评估（关键）**：需要**房主与加入者所连的那个节点**升级才生效
  —— 因为双方连的是同一个 `LobbyServerBaseUrl`，**只要用户自己这一侧的节点升级即可**，
  不要求"全网节点升级"。
  ⚠️ 但若用户连的是**公共节点**（别人的服务器），这条就落不了地。
  **判定依据**：日志/调试报告里的 `LobbyServerBaseUrl`（`LanConnectDebugReport`）——
  是 `127.0.0.1`/自建域名还是公共节点。**这是 L3 能否落地的唯一决定因素。**

### 7.2 有没有更简单的"一次性消除陈旧窗口"的办法

**没有一次性消除的办法。** 逐条说明为什么：

| 手段 | 能否消除窗口 | 依据 |
|---|---|---|
| 加入前强制刷新房间列表 | 只消除 **8s 列表**窗口 | 见 §7.1 C1；拉到的仍是服务端 10s 前的数据 |
| 房主在"有人连上/断开"时**立即补发一次心跳** | 能把 10s 心跳窗口压到 ~RTT，但**不是 0** | `SendHeartbeatAsync` 是 `LanConnectLobbyRuntime.cs:1330` 的私有方法，受 `_heartbeatInFlight` 保护，mod 内可加事件触发复用；但"连接成功 → 心跳到达服务端"之间仍有几十毫秒，两人同刻选槽仍会撞 |
| 把 8s 与 10s 对齐 | ❌ 无意义 | 服务端数据滞后由心跳决定，与列表刷新频率无关 |
| 客户端把 `slot.IsConnected` 与"本地已知占用的 netId"取并集 | 只能覆盖**本机自己**看到的，跨机器无效 | 每台机器的本地状态互不可见 |

> **结论**：窗口只能缩小，**不能消除**；消除必须靠 L3 的**原子预留**（在票签发那一刻占住）。

### 7.3 H4（只剩 1 槽不问用户直接接管）要不要改

**要改。** 依据：

1. 它把"选错槽"从**可察觉的错误**变成**静默的角色错配**：
   `availableSlots.Count > 1` 才弹框，恰好 1 个时直接 `JoinRoomAsync(..., availableSlots[0].NetId, ...)`
   （`LanConnectLobbyOverlay.cs:4748-4767`）。用户会以别人的角色进游戏，打到中途才发现。
2. 它让"最后一人的报错"看起来随机：其实是被前面的人挤掉了。
3. 它是**放大器**：前面一旦有人选错，后面所有人都会被动改派，直到 0 槽时硬失败。

**建议**：任何情况下都弹选择框（1 个槽也弹，默认选中它并显示"你将以 {角色}（{玩家名}）加入"），
只有 0 槽才直接拒绝。改动量极小（去掉 `Count > 1` 的判断），兼容性零影响。

### 7.4 最终建议

**分两步，主修在客户端 UI 层，服务端原子预留作为可选治本。**

**第一步（建议 v1 就做，零兼容风险）—— L1 + H4：**
1. C1 加入前强制刷新房间列表（消除 8s 列表窗口）；
2. C2 选槽框显示 `角色名（玩家名）+ netId 后 4 位`，本机持久 netId 匹配的槽默认选中并标注"这是你"；
3. C3/§7.3 取消"只剩 1 槽自动接管"，1 个槽也弹框确认。

- 效果：把"必然出错"降为"极窄并发竞态"；**接管缺席玩家角色的功能完整保留**
  （主人不在线 ⇒ 该槽不在 `connectedPlayerNetIds` ⇒ 仍可选）。
- 风险：几乎为零（不改协议、不依赖节点升级）。

**第二步（可选，治本）—— L3 服务端原子预留：**
- 仅当确认用户**能升级自己那一侧的节点**时才有意义；
- 协议不变，未升级节点优雅降级；
- 若用户连的是公共节点，**这一步直接放弃**，只做第一步。

**明确不做 L2（房主侧校验）作为主修层**：
协议上拿不到 `desiredSavePlayerNetId`，且身份即 netId、无法区分"抢槽者"与"真主人"，
强行做只会把被拒的人从"真主人"换成"先到者"，方向反了。

### 7.5 设计前提的脆弱点（必须随日志复核）

1. **"选错槽"是前提**：若日志显示每个人的 `desiredSavePlayerNetId` 都等于本人 netId，
   则 H1/H2 不成立，本设计的收益接近 0 —— 必须回到 §2 的真·内容差异方向。
2. **"本机持久 netId == 存档槽 netId"是 C2 高亮的前提**：LAN 起源的存档成立
   （建房时用的就是各人持久 netId），Steam 起源的存档不成立。若日志显示不匹配，C2 退化为"只显示 netId 尾号"。
3. **H4 改动的副作用**：多一次点击，且当只剩 1 槽时用户可能困惑"为什么只有一个选项"——
   需要文案说明（"其余角色已被其他玩家接管"）。

### 7.6 与日志的对应关系（拿到日志后如何判定本设计是否可用）

| 日志命中（§5.2） | 判定 |
|---|---|
| `save_slot_unavailable` / `save_slot_invalid` / `save_slot_required` | ✅ **本设计（L1+H4）直接可用**，优先做 C2/C3 |
| `join_flow: using selected saved-run slot netId=` 且该 netId ≠ 本人 | ✅ 本设计直接可用，C2 是主修 |
| `invalid_ticket` / `ticket_already_redeemed` | ✅ 属**同 netId 并发**，**L3 原子预留**优先级高于 L1 |
| `wire_handshake … decision=mismatch` / `Mod mismatch` / `ModelDb hash mismatch` | ❌ **本设计不适用**，转 §2 的真·内容差异排查 |
| 以上都没有 | ⚠️ 本设计仍可做（消除"可解释的一半"），但剩余部分必须等更完整日志 |

### 7.7 一句话总结

> **建议在客户端 UI 层修（刷新列表 + 可辨识选槽 + 取消自动接管），不需要服务端配合；
> 服务端原子预留是可选治本，只有用户能升级自己那一侧节点时才值得做；
> 房主侧校验在协议上不成立，不做。**

---

## 8. 相邻未决项：join 接管把上游 `ValidateInitialMessage` 整体跳过了

> **未修。** 本文之外唯一还在生效的 join 侧缺陷；下一次改 join 流程前先读这一节。
> （2026-11-13 收尾时由一份一次性审计并入本文。）

- `JoinTakeoverFix` 在 `LanConnectLobbyManagedJoinFlow.BeginAsync` 上装 prefix 并 `return false`，
  上游方法体一次都不执行，其中 `ValidateInitialMessage(initialMessage, hostInfo)` 也就一次都不执行。
- 那四道校验里**只剩第一道还活着**：`connectionFailureReason`（部署版 `LanConnectLobbyManagedJoinFlow.cs:266-282`）。
  其余三道（wire-cache / 游戏版本 / Mod 差集 / ModelDb 哈希）在 `if (!hostInfo.HasVersionMetadata) { …; return; }`
  之后，而 0.113 的 `InitialGameInfoMessage` 只有 `sessionState` 与 `connectionFailureReason` 两个字段，
  读快照时拿不到 `versionInfo` → `HasVersionMetadata` 恒为 false → **补了也是死代码**。
- 后果：满员房 / 已开局房 / 存档里没有你的房，客户端不再弹游戏为 `LobbyFull`/`RunInProgress`/`NotInSaveGame`
  准备好的本地化错误，而是等房主断开后拿到笼统的 `Unexpectedly disconnected from host while joining. Reason: …`，
  或者等到超时；并且比原版多一次无效往返。**真机未复现，属未确证。**
- 最小修法（方案 A，约 15 行）：在 `RunAsync` 里 `await joinFlow.Begin(...)` **之前**自己注册一个
  `InitialGameInfoMessage` 处理器，只判 `connectionFailureReason`：值 ∉ {VersionMismatch, ModMismatch} 就
  记下 `ClientConnectionFailedException`，在 `catch` 里当作失败原因优先抛出（与已有的 `TryTakeTailRejection`
  共用同一优先级槽位），`finally` 里注销处理器。**不要**在处理器里直接抛 —— 委托跑在收包线程，
  抛出的异常进不了 `Begin` 返回的 Task。
- 更忠实的方案 B：反射调上游 `ValidateInitialMessage`（约 40 行、2 个新反射锚点），上游将来补回版本元数据
  时自动全活。
- 现状核对：`src/` 里 `connectionFailureReason` 与 `ValidateInitialMessage` **零命中**，即至今未实施。
