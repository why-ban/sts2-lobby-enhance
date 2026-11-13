# docs/research —— 议题索引

本目录**只保留仍在生效或仍未解决的议题**。2026-11-13 那一轮的一次性审计记录已删除：结论已落在
**代码注释**（改那段代码就会看到）与 [`../README.md`](../README.md)、[`../CHANGELOG.md`](../CHANGELOG.md) 里；复现手段看 `tests/` 与 `tools/`。

| 文档 | 解决什么问题 | 什么时候读 |
|---|---|---|
| [`join-order-bug.md`](join-order-bug.md) | **未修复**的 P2：多人存档续玩「必须按原玩家顺序加入」。§3 是 5 个候选机制，§5 是要用户提供的日志与关键字对照表，§7 是修复设计；§8 是同一流程上另一个未决项（join 接管跳过了上游 `ValidateInitialMessage`，拒绝提示因此退化）。 | 用户再报「顺序进房失败」、或要动 join 流程时 |
| [`p1-crash-deep.md`](p1-crash-deep.md) | **未解决**的手机闪退（Godot `local_vector.h:201` 越界）。8 条结论都带证据，§7 是 (a)–(g) 验证矩阵，§8 写明每项缺什么证据；§10 是手机侧的两个前提（没有本地多人存档、移植层自己也 patch 了资产管线）。 | 要收手机崩溃证据、或决定要不要关预载时 |
| [`tail-profile.md`](tail-profile.md) | 架构决策记录：**为什么必须保留 tail_v1 绑定、不能强制降级 `compat_4_5_v1`**。§4.2 是「强制 compat」过不去的四层门禁，§5.1 证明 #3/#4 只在错误路径触发。 | 任何人想改协议档位策略、或怀疑「两端档位不同也能连」时 |

## 维护者须知（踩过的坑）

1. **官方 0.6.3 有两份，不是同一个东西。** 游戏实际加载的是部署版（工坊 `3749766330`），
   `work/lobby/dec-official/` 是它的反编译；`work/lobby/STS2-Game-Lobby/` 的源码树已与它分叉
   （例：`LanConnectLobbyOverlay.GameModeFromCreateId` 只在部署版里存在）。写反射代码以**部署版反编译**为准。
2. **`manifest.affects_gameplay` 必须保持 `false`。** 游戏按该字段把「影响玩法」的模组筛进
   `GetGameplayRelevantModNameList()`（`ModManager.cs:989-996`），翻成 `true` 会让装/不装补丁的两端
   在握手时互判模组不一致。
3. **`applied: xxx` 不等于生效。** 它只表示 `Apply()` 没抛异常；判定「真的生效」要看 Harmony 接缝清单
   与那条修复自己的动作日志（`tools/smoke-test` 现在就是这么查的）。
