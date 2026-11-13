# P1 深挖：手机闪退的触发条件与规避路径（task-12）

> **当前状态（2026-11-13 收尾）：未解决。** 手机端闪退（Godot `local_vector.h:201` 越界）没有修好，
> 也没有归因到本补丁（§6 逐条排除：10 项修复的 patch 目标里 0 个碰资产管线）。
> **下一步就是 §7 的 (a)–(g) 验证矩阵**：(e) 打开移植层预载诊断几乎零成本、建议先做；
> (d) `adb` 抓 tombstone 是唯一能拿到 native 栈的途径，缺它无法定位到具体调用者；
> (a)/(b)/(c) 是三个对照实验，(f)/(g) 需要改代码或启动参数。
> 本文 8 条结论全部带 `文件:行号`/`日志:行号`，未确证项与「缺什么证据、怎么补」见 §8。

> 调查者：p1-crash-deep。本文件是本任务唯一写入的文件；全程只读（read/grep），未改 `src/`/`tests/`/`tools/`/`packaging/`，未做 git，未跑 dotnet build/test。
> 标注：**【已确证】**=有 `文件:行号` 或 `日志:行号` 直接支撑；**【强推断】**=链条完整但缺最后一环直接打印；**【未确证】**=拿不到，并写明缺什么。

---

## 0. 资料与一个关键的新发现

| 简称 | 路径 | 规模 |
|---|---|---|
| 手机日志 | `work\logs\new-report-2\internal-files\instances\v0.113.0-16ac0fe3--d32b3370\logs\godot.log` | 567380 行 |
| 手机 logcat | `work\logs\new-report-2\internal-files\logs\sts2.log` | 244 行 |
| 启动器日志 | `...\instances\v0.113.0-16ac0fe3--d32b3370\logs\android-launch.log` | 185 行 |
| 移植层 diag | `work\logs\new-report-2\internal-files\logs\compat-diag.log` | 9531 行 |
| PC 房主日志 | `C:\Users\Administrator\AppData\Roaming\SlayTheSpire2\logs\godot.log` | 2535 行 |
| 游戏 0.113 反编译 | `work\compat\decompiled-v0.113.0\` | — |
| **移植层反编译源码** | `work\lead\v8dec\`（`STS2Mobile.Patches\*`） | 83 文件 |

**【已确证】本任务最大的增量：移植层（`com.sts2mobile` / STS2Mobile.dll）的反编译源码就在本机 `work\lead\v8dec\`，不是「只有名字没有实现」。**
识别依据（6 条日志字符串逐一在该目录命中，且其它 5 个候选目录都不含 `RitsuFix`）：

| 日志原文 | 命中位置 |
|---|---|
| `Runtime resource sessions budgeted: 2ms ...` | `RuntimeAssetLoadingPatches.cs:143` |
| `Patched AssetCache.LoadAsset for Android preload diagnostics.` | `LifecycleAndPerformancePatches.cs:104` |
| `... UnloadAssets for Android protected warm cache.` | `LifecycleAndPerformancePatches.cs:110` |
| `... UnloadMissedCacheAssets for Android protected warm cache.` | `LifecycleAndPerformancePatches.cs:116` |
| `Android startup Harmony overrides disabled; ...` | `LifecycleAndPerformancePatches.cs:96` |
| `Patched RitsuLib PatchTargetMethodResolver.Resolve ...` | `RitsuLibCompatPatches.cs:99` |

**【已确证】compat-diag.log 与手机 godot.log 是同一次进程：**
`compat-diag.log:9024` = `===== session start 2026-11-13T14:38:36.5334617Z pid=31647 =====`；`sts2.log:1` = `Sts2Logcat collector started time=2026-11-13 22:38:33.022 ... pid=31647`（22:38 本地 = 14:38 UTC）；`android-launch.log:180` 最后一次启动 = `Fri Nov 13 22:38:33 GMT+08:00 2026`。
godot.log 内的 `at 14:42:32`（`567359`）就是崩溃前最后时间戳；`sts2.log:242` 记录 22:42:43.264 新进程 pid=8400 启动 → **崩溃发生在 22:42:32 之后约 11 秒内**。

---

## 1. 结论摘要（只有 8 条，全部带证据）

1. **【已确证】`count=N` 不是 encounter 的资产总数，而是「集合差之后仍需加载的条数」。** 所以 `count=10`/`count=11` 的差 1 **不能**解读为「两个 encounter 差 1 个资产」。见 §2.1。
2. **【已确证】第 1 场 = `SEAPUNK_WEAK`（UNDERDOCKS），`count=10`，`assets=10` 全部加载完成。** 手机 `566576→566581→566582`；PC `1552→1553→1554` 同值。
3. **【已确证】PC 与手机跑的是同一局同一会话**（两局种子在两边逐字相同），因此第 2 场的 encounter 在两边必然相同；PC `2419` 打出 `encounter=SLIMES_WEAK` 且 `count=11`。→ **【强推断】手机第 2 场（崩溃那次）= `SLIMES_WEAK`（OVERGROWTH）**，手机日志本身没有这一行（`SLIMES_WEAK` 全文件 0 命中，`Creating NCombatRoom` 全文件仅 1 条 `566582`）。
4. **【已确证】崩溃窗口 = 「session 构造完成」到「第一个资产产生任何可见副作用」之间。** 因为 `Preloading 'Combat Room' Complete`（`AssetLoadingSession.cs:116`）从未出现，且 `count=11` 之后一行资源相关输出都没有（对比第 1 场有 4 行 seapunk `.tres` UID 告警）。见 §5。
5. **【已确证】移植层的「2ms / 每 phase 最多 8 项」预算不会截断 session、不会让它提前完成、不会提前释放。** 预算检查插在 `TryDequeue` 之前，超额时 `ret` 退出**当前 phase 方法**，未处理的条目原样留在队列里下一帧继续；完成条件（`AssetLoadingSession.cs:114`）未被改动。见 §3。
6. **【已确证】移植层对 `NCombatRoom._Ready`/`SetUpBackground` 的 3 个 patch 全是 postfix，全部发生在「预载完成 → 建房」之后，且本次会话里动画 warmup 一次都没启动。** 见 §4。
7. **【已确证】本次崩溃不在 C# 层的 `[1]` 下标路径上。** `local_vector.h:201` 是 Godot C++ 容器断言；全日志 0 条 `Unhandled exception`/`Script Error`；且 `await` 从未返回，`CombatRoom.cs:204-227` 整段没执行。见 §5。
8. **【已确证】本补丁与本路径无交集（10 个 fix 的 patch 目标里 0 个碰资产管线）。** 见 §6。

**任务书里有两处前提需要纠正**（不是小事，直接影响验证矩阵能不能执行）：
- **(a) 不成立**：`Bootstrapper.cs:84` 属**调试场景**；正式版 `PreloadManager.Enabled` **没有任何玩家可见开关**（`IBootstrapSettingsSubtypes.Count => 0`，`BootstrapSettingsUtil.Get()` 恒返回 null，`NBlitzScreen.cs:391-396` 那段根本不会执行）。见 §7(a)。
- **「`Unloading 7 missed cache assets` 手机与 PC 相同」不能当等价证据**：移植层在 `UnloadMissedCacheAssets` 前会把受保护路径从 `_missedCacheAssets` 里**删掉**（`LifecycleAndPerformancePatches.cs:364-395`），所以手机打出的 7 是「删掉受保护项之后」的数字。见 §3.4。

---

## 2. 任务 1：count=10 / count=11 分别对应哪个 encounter

### 2.1 `count` 到底是什么【已确证】

`AssetLoadingSession` 构造函数的最后一句就是这条日志：

```
AssetLoadingSession.cs:78   Log.Info($"Preloading '{name}' assets... count={_toLoad.Count} vfx={_vfxScenes.Count}");
```

而 `_toLoad` 只装非 vfx 的待加载项（`AssetLoadingSession.cs:66-76`）。传进来的 `paths` 是 `needLoaded`，即**差集**：

```
PreloadManager.cs:181  IReadOnlySet<string> loadedCacheAssets = Cache.GetLoadedCacheAssets();
PreloadManager.cs:182  IEnumerable<string> assetsToUnloadSet = loadedCacheAssets.Except(hashSet2);
PreloadManager.cs:183  IEnumerable<string> needLoaded = hashSet2.Except(loadedCacheAssets);
PreloadManager.cs:190  return LoadAssets(needLoaded, name);
```

`hashSet2` = `CommonAssets ∪ RunSet ∪ Act ∪ {NCombatRoom.AssetPaths, encounter.GetAssetPaths(runState)}`（`PreloadManager.cs:150-163`、`242-253`）。
**结论**：`count` = 「本 encounter 全集」−「当前 AssetCache 里仍有效的条目」。它同时取决于 encounter 和**缓存状态**，两者混在一起，所以 10 vs 11 的差 1 无法归因到 encounter 差异。

### 2.2 两次战斗各是什么 encounter

| | 手机 | PC | 结论 |
|---|---|---|---|
| 第 1 场 | `566576` count=10 → `566581` `Complete: assets=10 time_elapsed=1,315ms` → `566582` `Creating NCombatRoom ... encounter=SEAPUNK_WEAK.` | `1552`/`1553`/`1554` 完全同值同 encounter | **SEAPUNK_WEAK**（UNDERDOCKS），成功 |
| 第 2 场 | `567377` `Unloading 7 missed cache assets` → `567378` count=11 → `567379` FATAL | `2416` `Unloading 7 ...` → `2417` count=11 → `2418` `Complete: assets=11` → `2419` `Creating NCombatRoom ... encounter=SLIMES_WEAK.` | **【强推断】SLIMES_WEAK**（OVERGROWTH） |

同一会话的直接证据（不是推测）：

```
手机 566405  Embarking on a multiplayer blitz. Players: Player 1, IRONCLAD,Player 5018570539104672532, IRONCLAD. Ascension: 0 Seed: 7V91WT6SKR9B
PC   1385    同上（逐字相同）
手机 567151  Embarking on a multiplayer blitz. ... Seed: 36SAVWNS1RM8
PC   2252    同上（逐字相同）
```

两局种子在两端逐字相同 + 同一份 encounter 选择逻辑（`EncounterModel.GenerateMonstersWithSlots` 用 `runState.Rng.Seed + TotalFloor + hash(Id.Entry)`，`EncounterModel.cs:264`）+ 同一个地图点（手机 `567357` 与 PC 都是 `MapCoord (3, 1)`）。
**为什么不能算【已确证】**：手机日志里 `SLIMES_WEAK` 出现 0 次（`grep SLIMES_WEAK` → 0），唯一能打 encounter 的地方是 `NCombatRoom.cs:366`，而那一行从未执行。**缺的证据**：在 `PreloadManager.LoadRoomCombatAssets`（`PreloadManager.cs:110-117`）里把 `encounter.Id.Entry` 打一行，或在移植层 `LoadAssetsPrefix` 里带上 `name`（现在只打 name，不带 encounter）。

### 2.3 两个 encounter 的资产集合差在哪（结构性，可确证）

资产全集公式（`EncounterModel.cs:324-339`）：`背景资产 ∪ 遭遇场景(若有) ∪ ExtraAssetPaths ∪ 每只怪的 AssetPaths`；再并上 `NCombatRoom.AssetPaths`（`PreloadManager.cs:250`）。

| 组成 | SEAPUNK_WEAK | SLIMES_WEAK |
|---|---|---|
| `NCombatRoom.AssetPaths` | `res://scenes/rooms/combat_room.tscn` 1 项 | 同（`NCombatRoom.cs:308`） |
| 怪物数量 | **1 只**（`SeapunkWeak.cs:18-21`：固定 `Seapunk`） | **3 只**（`SlimesWeak.cs:48-59`：从 {LeafSlimeS,TwigSlimeS} 取 2 只不同小史莱姆 + 从 {LeafSlimeM,TwigSlimeM} 取 1 只中史莱姆） |
| 每只怪的资产 | `VisualsPath` + 各 intent 的 `AssetPaths`（`MonsterModel.cs:81-98`），`VisualsPath = res://scenes/creature_visuals/<id>.tscn`（`MonsterModel.cs:79`） | 同规则，但 3 只怪 |
| 背景资产 | 由 Act 生成（`EncounterModel.cs:327`→`300`），UNDERDOCKS | OVERGROWTH，**不同 act 不同背景** |

**【已确证】两个集合在结构上必然不同（1 怪 vs 3 怪 + 不同 act 背景）。**
**【未确证】`count=11` 那 11 条具体路径是哪 11 条。** 缺证据的原因很硬：`AssetLoadingSession` **在成功路径上不打印任何资产路径**（全类只有失败分支打 path：`AssetLoadingSession.cs:139/156/180/201/228/235/244`），所以从日志反推 11 条路径在原理上做不到。
补证据的两条路：① 移植层 `LoadAssetsPrefix`（`LifecycleAndPerformancePatches.cs:292-306`）加一行 `string.Join(",", needLoaded)`；② 见 §7(e) 的 `preload_debug_enabled`（只能拿到 name/allow，拿不到路径）。

---

## 3. 任务 2：compat-diag.log 的预算机制（逐行读完 9380-9531 + 对照实现）

### 3.1 那一行原文与上下文

```
compat-diag.log:9489  Combat VFX reuse enabled: per-room retained limits damage=16 hit=8 shiv=8 big-slash=2 fire-burst=2; overflow uses original allocation.
compat-diag.log:9490  Runtime resource sessions budgeted: 2ms cooperative frame budget, at most 8 items per phase; original completion and failures retained.
compat-diag.log:9491  [Diag] LoadProgress result: Status=Success, Success=True, HasData=True, Error=none
...
compat-diag.log:9510  Android common/main-menu preload begin: 828 resources.
compat-diag.log:9511  Android common/main-menu preload complete: visited=828/828 loaded_now=609 cached_before=219 null=0 failed=0 elapsed=21,349ms.
compat-diag.log:9515  [Startup] Resource stats (...): ... Resources=5,397 ... CachedAssets=828
```

9490 之后到 9531 全是**启动期**内容（显示参数、字体回退、shader 变体、reaction 按钮、设置页行），**没有任何运行期（进战斗房）条目**。也就是说：本文件里 9490 只是一条「已安装」公告，不含实现细节 —— 但实现能在 `work\lead\v8dec` 读到（§0）。

### 3.2 实现【已确证】

- 安装点：`RuntimeAssetLoadingPatches.cs:115-116` —— 挂在 `NGame._Ready` 的 postfix，仅 Android、只装一次。
- 被 patch 的 4 个方法：`RuntimeAssetLoadingPatches.cs:110` `Phases = {FinalizeLoading, ProcessLoadingQueue, CheckLoadingStatus, ProcessVfxQueue}`。
- 每个方法插一条 **Transpiler**（`:140`），插点 `FindDequeue(list) - 3`（`:191`，即 `TryDequeue` 调用前 3 条指令、循环体入口处），插入 5 条 IL（`:198-205`）：

```
ldarg.0                    // session
ldc.i4  <phase 0..3>
call   Allow(session, phase)
brtrue label               // 允许 → 跳到原第一条指令，正常出队
ret                        // 不允许 → 直接 return（退出整个 phase 方法）
```

- 预算：`Budget.Take`（`:70-101`）按 `Engine.GetProcessFrames()` 每帧重置，单 phase 上限 **8 项**（`:95`），另有 `FrameBudget` 总 2000µs / 24 项（`:37`、`:68`）。
- 校验：`Validate`（`:163-175`）要求方法返回 void、无异常处理子句、循环形状严格匹配，否则抛异常 → `GameReadyPostfix` catch 后打 `Runtime resource session budget unavailable`（`:147`）。**本机日志打的是 143 行那句成功文案，说明 4 个方法全部通过校验并装上。**

### 3.3 会不会「一次 session 被截断」→ 不会【已确证】

1. 预算检查在 `TryDequeue` **之前**（插点 `FindDequeue - 3`），被拒绝时**没有出队任何条目**，条目仍在 `_toLoad`/`_loading`/`_finalizing` 里。
2. `ret` 退出的是**当前 phase 方法**，不是 session。`AssetLoadingSession.Process()`（`AssetLoadingSession.cs:104-120`）每帧被 `NAssetLoader._Process`（`NAssetLoader.cs:68-85`）调用，下一帧继续。
3. 完成条件未被改动：`AssetLoadingSession.cs:114` 要求 `toLoad==0 && loading==0 && finalizing==0 && vfxScenes==0 && !vfxLoading` 才 `TrySetResult`。节流只会让 session **更慢**，不会让它**更早**完成。
4. 同理，`ProcessVfxQueue` 的 `VfxPrefix`（`:209-212`）返回 false 时只是跳过本帧，队列不动。

→ **「预载被截断/提前完成/提前释放」这三条假设，用移植层源码可以排除。**
唯一副作用是**变慢**：11 项至少跨 2 帧（8+3）。这与「崩溃发生在 `count=11` 之后几乎无间隔」并不矛盾 —— 慢不慢取决于有没有下一帧，见 §5。

### 3.4 与预载相关的其它移植层改动（会改变缓存状态，但都朝着「更少释放」方向）【已确证】

| patch 目标 | 源码 | 行为 |
|---|---|---|
| `PreloadManager.LoadCommonAndMainMenuAssets` | `LifecycleAndPerformancePatches.cs:98,278-290` | 满足条件时整段换成移植层自己的启动预载（`LoadCommonAndMainMenuAssetsAndroidAsync`） |
| `PreloadManager.LoadAssets` | `:99,292-306` | 若 `ShouldAllowAssetSetPreload(name)==false` → `__result = AssetLoadingSession.Empty()` 并打 `Android preload skipped by preload settings: {name}` |
| `AssetCache.LoadAsset` | `:103-104,308-338` | 只加诊断日志（cache miss path/category），不改行为 |
| `AssetCache.UnloadAssets` | `:109-110,340-362` | 从待卸载集合里**剔除**受保护路径（少卸载） |
| `AssetCache.UnloadMissedCacheAssets` | `:115-116,364-395` | 先把受保护路径从 `_missedCacheAssets` 里**Remove**，再让原方法跑（少卸载） |

**【已确证】崩溃那一次的 `LoadAssets` 走的是原版：** 如果 `LoadAssetsPrefix` 返回 false，只会打 `Android preload skipped by preload settings: Combat Room` 且 `567378` 那行根本不会出现；而 `567378` 正是原版构造函数 `AssetLoadingSession.cs:78` 的输出。→ **`ShouldAllowAssetSetPreload("Combat Room")` 当时为 true，即 `preload_enabled` 与 `preload_runtime_enabled` 都开着**（`:1510-1529`、`:1396-1399`、`:1371-1374`）。
**【已确证】同时可知 `PreloadManager.Enabled == true`**：若为 false，`PreloadManager.cs:186-189` 会在调用 `LoadAssets` **之前**返回 `Empty()`，同样不会有 `567378`。

---

## 4. 任务 3：`NCombatRoom._Ready` / `SetUpBackground` 的 patch 具体做了什么

compat-diag 里确实只有名字（`112`/`130`/`131`/`212`），但**实现可读**（§0 的识别）。三个 patch，全部是 **postfix**：

| compat-diag 行 | 目标 | 实现 | 具体做什么 |
|---|---|---|---|
| `112`/`130` | `NCombatRoom.SetUpBackground` | `AndroidParticlePreprocessPatches.cs:14` → `BackgroundReadyPostfix`（`:18-36`） | 递归遍历该节点子树，把 `GpuParticles2D` 的 `Preprocess` 收敛到 `lifetime + preprocess % lifetime`（`:73-80`）；只改粒子预热时长，**不加载/不释放任何资源** |
| `112`/`130` 的另一条 | `NCombatRoom.SetUpBackground` | `CombatBackgroundPatches.cs:18` → `SetUpBackgroundPostfix`（`:23-36`） | 仅当 `AspectRatioSetting==5` 且视口宽高比 < 2.1333 时缩放 `%BgContainer`（`:71`、`:82-89`） |
| `131`/`212` | `NCombatRoom._Ready` | `CombatBackgroundPatches.cs:19` → `CombatRoomReadyPostfix`（`:38-57`） | `CallDeferred` 一次 `AdjustCreatureScaleForAspectRatio` + 注册 `UiScalePatches.ObserveScale` 回调 |
| （同上） | `NCombatRoom._Ready` | `CombatAnimationWarmupPatches.cs:41` → `CombatRoomReadyPostfix`（`:44-77`） | 受 `preload_combat_animation_warmup_mode`（默认 `"off"`）与 `Mode>0` 双重门控；通过才 `CallDeferred` 一个异步动画预热 |

**【已确证】本次会话里动画预热一次都没启动**：`WarmupCombatAnimationsAsync` 的第一句就是 `CombatAnimationWarmupPatches.cs:100` `Combat animation warmup begin: ...`，而手机日志 `warmup` 关键词 **0 命中** → `:56-61` 的早退分支被走到（mode=="off" 且 hitEffectWarmup==false）。
**【已确证】三个 patch 都不可能参与本次崩溃**：`_Ready` 与 `SetUpBackground` 都发生在 `NCombatRoom.Create` 之后（`CombatRoom.cs:216`、`NCombatRoom.cs:332-342`、`439-445` 由 `_Ready` 内 `:430` 调用），而崩溃时连 `Preloading 'Combat Room' Complete` 都没有，房节点根本没被创建。

---

## 5. 任务 4：C# 层有没有 `[1]` 下标能解释 `p_index=1 / count=1`

### 5.1 先证伪这个方向的前提【已确证】

1. `local_vector.h:201` 的 `operator[]` 是 **Godot C++ 容器**的断言（`LocalVector`），C# 侧无法构造、无法直接索引。
2. 若真是 C# 越界，会是托管异常（`IndexOutOfRangeException`）并带托管栈。全日志 `Unhandled exception` **0** 条、`Script Error` **0** 条、`NullReference` **0** 条（`grep` 实测）。对比：普通 WARNING 是有 C# 栈的（`567178-567212`）。
3. **最关键**：`await PreloadManager.LoadRoomCombatAssets(...)`（`CombatRoom.cs:202`）只有 session 完成才会返回，而完成信号在 `AssetLoadingSession.cs:116-118`（先打 `Complete` 再 `TrySetResult`）。`Complete` 没打 → **`CombatRoom.cs:204-227` 整段（含 `NCombatRoom.Create`）根本没有执行**。

### 5.2 穷举（即使它执行了，也找不到 `[1]`）

`CombatRoom.StartCombat`（`CombatRoom.cs:194-228`）全段只有：`if (!Encounter.HaveMonstersBeenGenerated)`（196）、`await` 预载（202）、`foreach (var (monsterModel, slot) in Encounter.MonstersWithSlots)`（204）、`CombatState.CreateCreature(...)`（209）、`CombatState.AddCreature(...)`（210）、`Rooms.Last().MonsterIds.Add(...)`（212）、`NRun.Instance?.SetCurrentRoom(NCombatRoom.Create(...))`（216）、`CombatManager.Instance.SetUpCombat(...)`（222）、`Hook.AfterRoomEntered`（225）、`AfterCombatRoomLoaded`（227）。**没有任何显式下标访问**；唯一的「取最后一个」是 `Rooms.Last()`（空集合会抛 `InvalidOperationException`，不是索引越界）。
`NCombatRoom.Create`（`NCombatRoom.cs:332-342`）= `GetScene(...).Instantiate<T>()` + 两个字段赋值；`NCombatRoom` 没有自定义构造函数（无 `public NCombatRoom(` 声明）。
`NCombatRoom._Ready`（`NCombatRoom.cs:344-387`）全是 `GetNode<T>(%...)` 与 `CreateAllyNodes/CreateEnemyNodes`，同样无下标。

### 5.3 结论

**【已确证】排除。** 本次 FATAL 不是 C# 的 `[1]` 下标，也不能用「`CombatRoom` 在预载完成后访问了长度 1 的集合」来解释 —— 那段代码没有运行。
**顺带排除一个字面陷阱**：`NCombatRoom.AssetPaths` 的实现类型是编译器生成的 `_003C_003Ez__ReadOnlySingleElementList<string>`（`NCombatRoom.cs:308`），字面上也是「count=1 的列表」，但它是托管类型，越界同样抛托管异常，不是 LocalVector。
**【未确证】真正的越界点。** 仍缺 native 栈（见 §8）。

---

## 6. 任务 6：本补丁是否可能影响这条路径

### 6.1 逐个 fix 的 patch 目标【已确证】

补丁清单：`Entry.cs:271-283` `CompatFixes.Build()` 恰好 10 个；手机侧全部装上：`565011-565020` `applied: <id>`，`565021` `summary: applied=10[...] skipped=0[] failed=0`。

| # | Id | 手机日志里的 patch 声明 | 碰资产预载/战斗房/每帧回调? |
|---|---|---|---|
| 1 | join-flow | `564998` `LanConnectLobbyManagedJoinFlow.BeginAsync` | 否 |
| 2 | join-screen-modes | `564999` `LanConnectLobbyJoinFlow.PushJoinedScreen` | 否 |
| 3 | host-wire | `565000` `LanConnectSerializationPatches.SerializeJoinResponseBody` | 否 |
| 4 | tail-error-paths | `565001-565002` 6 个 tail 调用点 + kick disconnect | 否（一次性 `SceneTreeTimer`） |
| 5 | treasure-vote | `565003` `TreasureRoomRelicSynchronizer.OnPicked` | 否 |
| 6 | abandon-run | `565004` `LanConnectMultiplayerSaveCompatibility.AbandonCurrentRunAsync` | 否 |
| 7 | new-run-button | `565005` 游戏结束屏 + session teardown | 否（`_Ready` 一次性） |
| 8 | mid-run-restart | `565006` restart 入口 + client reassemble handlers | 否（async pump，非节点回调） |
| 9 | extra-game-modes | `565007` 建房模式选择器 | 否 |
| 10 | unlock-extra-modes | `565008` `SaveManager.GenerateUnlockStateFromProgress` / `ProgressState.IsEpochRevealed` | 否 |

**独立复核（不复用前情结论）**：在 `work\companion\src\**\*.cs` 里检索 `PreloadManager|AssetCache|AssetLoadingSession|NCombatRoom|ResourceLoader` → **0 命中**。本补丁不引用、不 patch 这条链上的任何类型。

### 6.2 每帧回调 / 场景树回调【已确证】

- 本补丁没有注册任何节点 `_Process`/`_PhysicsProcess`/`_EnterTree`/`_ExitTree`/`_Notification`。唯一节点回调是 `NewRunFromGameOverFix` 对 `NGameOverScreen._Ready` 的 postfix（`565005` 已声明），一次性。
- 两个 restart fix 里的「每帧 pump」是 **async 循环**（`await tree.ToSignal(tree, ProcessFrame)`），带 deadline；本次在 `566701` `client restart failed: TimeoutException ... 15000 ms` 处已结束，循环不再存在。
- 「抑制 teardown」标志是一次性的，手机日志只在 `566660`/`566679` 命中 2 次，之后没有第 3 次。

### 6.3 结论

**【已确证】本补丁不可能以「patch 目标 / 每帧回调 / 场景树回调 / 资源释放」四种方式影响这条路径**：它既没有 hook 这些类型，也没有留下运行期钩子。
**【无法排除的一小块】** 补丁间接改变了「会话/大厅状态机」的时序（例如抑制 teardown、重启流程超时后回主菜单），这可能改变移植层与引擎的资源缓存状态。但要构成因果，必须解释「为什么崩溃点是 `local_vector.h` 的 C++ 容器」，本补丁在 C# 侧无法触碰该容器。**在拿到 native 栈之前，这条只能写「无法排除」，且不是首选假设。**

---

## 7. 任务 5：可执行的规避 / 验证矩阵

> 每条都写「怎么做 / 预期看到什么 / 能证明什么」。凡是需要改代码的都标了「需改代码」，本任务未执行。

### (a) 关掉预载再打同一场战斗

**先纠正任务书前提**：`Bootstrapper.cs:84` 在 `src/Core/Debug/Bootstrapper.cs`，是调试场景；`NBlitzScreen.cs:391-396`/`NDreadScreen.cs:412` 虽然也写 `PreloadManager.Enabled = _settings.DoPreloading`，但 `BootstrapSettingsUtil.Get()` 依赖 `IBootstrapSettingsSubtypes.Count`，而它是 `=> 0`、`_subtypes = Array.Empty<Type>()`（`IBootstrapSettingsSubtypes.cs:9,11`）→ **正式版恒返回 null，这段永不执行**。`PreloadManager.Enabled` 的默认值仍是 `true`（`PreloadManager.cs:34`）。

**手机上真正可执行的开关**：游戏内「移植层设置」页的 **「跑图 / 房间预加载」**（`AndroidInGameSettingsPatches.cs:401`，键 `preload_runtime_enabled`，默认 true）。
- 做法：设置 → 移植层页 → 关掉「跑图 / 房间预加载」→ 重进同一局/同一种子，走到第 2 场战斗。
- 预期看到：godot.log 里**不再出现** `Preloading 'Combat Room' assets... count=11`，而是移植层 compat-diag.log 打 `Android preload skipped by preload settings: Combat Room`（`LifecycleAndPerformancePatches.cs:304`）；战斗房仍能进（资产改为按需加载，会看到多条 `Asset not cached`）。
- 能证明：**崩溃是否落在「预载 session 生命周期」内**。若不崩 → 崩溃与 session 强相关（候选 A/B 方向）；若照旧崩 → 崩溃在别处（例如渲染/音频/粒子），预载只是时间上的巧合。
- 注意：这条会改变整体时序（房间资产变同步加载），只用于**定位**，不作为长期规避。

### (b) 单机同种子连打两场同 encounter

- 做法：单机开局（同角色/同 ascension），把第 1 场也刷成 SLIMES_WEAK（Overgrowth 弱遭遇），连打第 2 场。
- 预期看到：第 2 场若崩，`count` 应与 `567378` 接近（同为 11 或更小），且仍停在 `Preloading 'Combat Room' assets... count=N` 之后。
- 能证明：崩溃是否与「联机/重开」无关（纯进程内第 2 次进战斗房）。**这是把样本从 n=1 变成可复现的最便宜实验。**

### (c) 联机但不重开，直接连打两场

- 做法：与 (b) 同一局，但走联机；**全程不触发重开**，连续进 2 个战斗房。
- 预期看到：若第 2 场崩 → 与「重开失败」无因果关系，只是「同进程第 2 次进战斗房」；若不崩 → 重开/重进留下的进程态（warm cache 828 项、抑制 teardown）才是必要条件。
- 能证明：把前情里的 H1（进程/缓存态）与 H2（重启残留）分开。

### (d) adb 抓 tombstone（唯一能拿到 native 栈的途径）

**先说清楚为什么必须用 adb**：应用自带的 logcat 收集器拿不到 tombstone —— `sts2.log:3` 自己写着 `Android only exposes logcat entries visible to this app UID; full device logs still require adb logcat`，而 `sts2.log:4-241`（崩溃那次进程 pid=31647 的全部内容）**只有 AudioTrack 行**，`Fatal signal`/`tombstone`/`libgodot`/`SIGABRT` 全部 0 命中。

- 手机侧要做的事：设置 → 开发者选项 → 打开 USB 调试；用数据线连电脑；**先**在电脑上跑下面的 logcat 命令，**再**在手机上复现崩溃。
- 电脑侧命令（PowerShell，先确认 `adb devices` 能看到设备）：

```
adb logcat -c
adb logcat -b crash -v threadtime            # 只抓 crash buffer，崩溃后 10 秒内最全
# 另开一个窗口同时抓全量，过滤关键字：
adb logcat -v threadtime | Select-String -Pattern "Fatal signal|libgodot|sts2re|DEBUG   :|backtrace|Abort message"
# 崩溃后补一刀（dropbox 里会有 data_app_crash）：
adb shell dumpsys dropbox --print data_app_crash | Select-String -Pattern "sts2|godot|signal"
# 有 root（或 debuggable 包）时，tombstone 文件最完整：
adb shell ls -l /data/tombstones/
adb pull /data/tombstones/tombstone_00
```

- 要抓哪几行：`Fatal signal 6 (SIGABRT)` 那一行、`Abort message`（这里会带 `Index p_index = 1 is out of bounds (count = 1)`）、以及 `backtrace:` 里的 `#00 pc ... libgodot_android.so` 到 `#08` 那几帧。**`#00`/`#01` 的符号名就是答案。**
- 怎么过滤：先 `-b crash` 拿干净的；如果 crash buffer 被系统清掉，用 `dumpsys dropbox` 里的历史条目；再不行就 `/data/tombstones/` 按时间戳取最新一个。
- 能证明：`local_vector.h:201` 到底是哪个 `LocalVector`、由谁索引 —— 这是目前唯一缺失的一环（§8）。

### (e) 打开移植层的预载诊断（几乎零成本，强烈建议先做）

- 做法：把 `preload_debug_enabled` 设为 true。它在移植层设置文件 `settings.save`（`AppPaths.cs:31`，路径 `/data/user/0/com.megacrit.sts2re/files/default/1/settings.save`，见 `compat-diag.log:89` 的 `AccountRoot`）；当前**没有**游戏内开关行（`AndroidInGameSettingsPatches.cs:386-409` 里没有该键），需手工写入或由 DevTools 写。
- 预期看到（compat-diag.log）：
  - `[PreloadDiag] LoadAssets name=Combat Room allow=True phase=... settings=master=True startupCommon=... runtime=True ...`（`LifecycleAndPerformancePatches.cs:297`、`1539-1542`）→ **直接把 name/allow/全部开关状态打出来**；
  - `[PreloadDiag] cache miss path=<每条> category=... phase=...`（`:331`）→ 逐条列出所有「预载没覆盖、按需加载」的资产；
  - `[PreloadDiag] settings at <context>: ...`（`:1535`）。
- 能证明：崩溃那次 `allow` 是否为 True（本任务已由「`567378` 存在」反证为 True）、以及当时**完整的开关快照**。**不能**证明那 11 条路径（该 hook 只在 cache miss 时打 path）。
- 备注：本次 `preload_debug_enabled` 是关的（`PreloadDiag` 在 godot.log 与 compat-diag.log 里各 0 命中）。

### (f) 补一条 `encounter` 与逐条路径日志（需改代码，本任务未做）

- 位置：`PreloadManager.LoadRoomCombatAssets`（`PreloadManager.cs:110-117`）打 `encounter.Id.Entry` + `needLoaded` 列表；或在移植层 `LoadAssetsPrefix`（`LifecycleAndPerformancePatches.cs:292-306`）里打 `string.Join(",", ...)`。
- 能证明：① 手机第 2 场到底是哪个 encounter（补 §2.2 的最后一环）；② 崩溃前第一个被请求的 path 是哪一个（若崩在 `LoadThreadedRequest`）。

### (g) 把 `LogType.Generic` 的 Debug 打开（补 §5 的观测缺口，需启动参数）

godot.log 里 `Preloading '<name>' Process: toLoad=... loading=...` （`AssetLoadingSession.cs:113`，`Log.Debug` → `LogType.Generic`，`Log.cs:7`）**全文件 0 条**，而其它类型标签的 `[DEBUG]`/`[VERYDEBUG]` 大量存在。同时 `Logger.cs:106` 那句 `Log level for <type> set to <level>` 也 0 条，尽管启动命令带了 5 组 `-log`（`android-launch.log:71`）。
**【未确证】为什么 Generic 的 Debug 没落盘**（可能是 Logger 静态构造早于日志文件就绪）。**含义**：不能用「没有 Process 行」推断 session 没有推进；反过来，若能拿到这些行，就能直接看出崩溃前处理了几项、卡在哪个 phase。

---

## 8. 未确证清单（每条都写清缺什么、怎么补）

| # | 未确证项 | 缺的证据 | 补法 |
|---|---|---|---|
| 1 | 越界的具体 `LocalVector` 与调用者 | native 栈（`CRASH_BAD_INDEX` 后立刻 abort，Godot 没打 C# 栈；全日志 `Fatal signal`/`libgodot`/`tombstone` 0 命中） | §7(d) |
| 2 | 崩溃那一刻是否已发起第一个 `ResourceLoader.LoadThreadedRequest` | 逐 path 日志（成功路径不打印 path，见 §2.3） | §7(f)、§7(g) |
| 3 | 手机第 2 场 encounter 的**日志级**直接证据 | `NCombatRoom.cs:366` 那行从未执行 | §7(f) 第一项 |
| 4 | Generic 类型 Debug 级别为何未生效 | `Log level for ...` 行为 0 条，原因不明 | 在 `Logger` 静态构造后补一条自检日志 |
| 5 | `work\lead\v8dec` 与实装 DLL 是否字节级一致 | 只做了 6 条日志字符串匹配（§0），没有构建号 | 从 `payloads` 目录取 `STS2Mobile.dll` 做哈希对比 |
| 6 | `Unloading 7` 两边是否同一集合 | 移植层先删受保护项再打印（§3.4），数字不可比 | §7(e) 的 `cache miss path` 列表 |

---

## 9. 一句话回答「能不能规避」

- **能规避，而且有一条现成的开关**：关掉移植层「跑图 / 房间预加载」（`AndroidInGameSettingsPatches.cs:401`）→ `LoadAssetsPrefix` 直接返回 `AssetLoadingSession.Empty()`（`LifecycleAndPerformancePatches.cs:292-306`），整条 session 生命周期被跳过。代价是房间资产改为按需同步加载（进房间会卡一下）。
- **但这只是「绕过」，不是「修好」**：崩溃点仍是 Godot 4.5.1 的 `local_vector.h:201`，具体调用者需要 §7(d) 的 tombstone 才能定位。

---

## 10. 相邻观察（早期崩溃调查的越界发现，2026-11-13 并入）

1. **手机端当时没有多人存档**：`current_run_mp.save` 不存在（手机日志 567137/567142/566686-566696）。
   这与崩溃无关（崩溃在战斗房资产预载），但说明第三方移植层的存档布局与 PC 不同 ——
   任何「回到本层开头」类功能在手机侧都必须由房主下发状态，不能假设客户端本地有存档。
2. **手机端是第三方 Android 移植层**（`com.sts2mobile`）：它自己 patch 了资产管线、`NCombatRoom`、
   逐帧节点与着色器（`compat-diag.log:218-222`、`:112/131/212`、`:188/211`、`:9505/9509/9517-9525`）。
   任何「手机 vs PC」的对比都必须先扣掉这一层，见 §0 与 §3.4。
3. **那一轮 mid-run-restart 实际没有生效**（房主 1684 失败后走原生回退，最终是新开一局新种子：
   PC 2252 / 手机 567151）。该缺陷已在 v1.5.0 修掉（tail 绑定生命周期 + 前置探针），
   本节只保留「当时手机端是在一个失败的重开之后崩溃」这一背景。
