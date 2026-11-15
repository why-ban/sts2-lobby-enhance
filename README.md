# 游戏大厅增强 / Game Lobby Enhance

《杀戮尖塔 2》**v0.113.0** 的联机大厅增强 —— 社区模组
[`sts2_lan_connect`（游戏大厅 / LAN Connect）](https://steamcommunity.com/sharedfiles/filedetails/?id=3749766330)
的**伴生模组**（companion mod）。

**创意工坊**：<https://steamcommunity.com/sharedfiles/filedetails/?id=3839101755> ·
**QQ 群**：1135970470 ·
[更新日志](docs/CHANGELOG.md) ·
[技术文档](docs/README.md)

---

## 这是什么

它是一个**运行期补丁**，不是替代品：

- 不含、也不替换社区模组的任何文件，只在 manifest 里声明依赖；
- 启动时用 Harmony 给已经加载的社区模组打补丁，社区模组照常从创意工坊更新；
- 清单里 `affects_gameplay: false` —— 装与不装的两台机器**不会**因为模组列表不一致而互相拒绝进房；
- 只读取游戏自身的公开 / 内部 API，**不改存档格式、不改协议、不伪造进度**。

**2.1.0 做 9 件事：4 项扩展 + 5 项大厅自己没做的修复。**

社区模组 0.6.4 已经自己适配了游戏 0.113，所以兼容层、极速 / 恐惧的建房与准备页、房间卡片的模式标注都不再由
本模组接手（1.x 里这些接管在 2.0.0 全部删除）。

扩展（2.1.0 新增）：

1. **服务器池** —— 所有服务器的房间摆在同一个列表里，按「同版本 → 人多 → 延迟低」排序，卡片标出来源服务器；
2. **「相同版本」筛选** —— 只显示与本机游戏版本相同的房间；
3. **聊天换服务器** —— 标题右边的 `‹ ›` 换着看不同服务器的公共频道，不切服、不退房、不写配置；
4. **手机滑动聊天** —— 滑动即滚动、长按 0.4 秒复制整条；PC 上拖拽仍是框选。

修复（大厅仍未覆盖）：

5. **结算页一键新开一局** —— 房主点一下，全房回到准备页；
6. **新开一局之后存档仍可续玩** —— 不会因为房间还记着上一局的存档键而读不回来；
7. **踢人 / 掉线之后照样能开局** —— 大厅的名册只能表达"有人加入"，减员后客户端会被判「Mod 不匹配」；
8. **读档不必按最初加入顺序进房** —— 大厅按"加入序 vs 存档序"逐位比较，同队换序就失败；
9. **建房只留「新协议」** —— 0.113 里只有新协议支持 RitsuLib，新旧协议联机体验无差别。

另有两处顺带改动：公告标出红字来源并支持滚轮翻页。

完整清单见 [技术文档](docs/README.md)（13 项修复，每项都有 id、注入点与替代方案），
逐版本改动见 [更新日志](docs/CHANGELOG.md)。

## 可靠性设计

每个版本、每次社区模组更新，最常见的失败模式不是崩溃，而是**补丁悄悄没装上**。
所以这一版把「版本开关」整个拆掉了：

- 13 项修复各自实现 `ICompatFix`，自己解析锚点（类型 / 方法 / 参数类型逐个比对）；
- 9 个 `FixGroup`，每组一个 Harmony id，某组失败只回滚自己那组，其它照常工作；
- 对不上时日志里明确写 `skipped: <id>: missing: <具体锚点>`，而不是整体静默失效。

启动日志里能看到一行汇总：

```
[lobby-enhance] summary: applied=13[...] skipped=0[] failed=0
```

## 环境要求

| 项 | 要求 |
|---|---|
| 游戏 | Slay the Spire 2 **0.113.0**（Windows 桌面版 / Android 第三方启动器） |
| 社区模组「游戏大厅」 | `sts2_lan_connect` **0.6.4 或更新**（创意工坊必需物品，订阅本模组会自动装上） |
| 联机双方 | **都要装**本模组。只装一边能用，但「新开一局」等新功能不对等 |

> **2.0.0 依赖「游戏大厅」0.6.4。** 从 1.x 升级时请把大厅一并更新，否则本补丁打不上任何一项。
> 大厅 0.6.4 与更早的 0.6.3 房间不互通（上游自己改了消息布局），所以房间里所有人要一起升。

## 安装

创意工坊订阅即可（必需物品会自动订阅）。手动安装：

1. 把 `sts2_lobby_enhance.dll` 与 `sts2_lobby_enhance.json` 放进
   `<游戏目录>/mods/sts2_lobby_enhance/`（游戏按 `manifest.id + ".dll"` 找程序集，文件名不能改）；
2. 从旧版（`sts2_lan_connect_0113_fix`）升级时**先删掉旧目录**，两个 id 同时加载会打两套补丁；
3. 启动后在日志里搜 `[lobby-enhance]`，看到 `applied=` 那行就说明生效。

## 从源码构建

需要 .NET 9 SDK，以及已经安装的游戏（构建只**读取**游戏程序集）。

```powershell
dotnet build src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true
```

打发布件（写入 `packaging/content/` 与 `dist/`）：

```powershell
pwsh -File packaging/build-content.ps1 -SkipBuild
pwsh -File tools/pack-release/pack-release.ps1
```

`-p:Sts2Root=` 可以指向别的游戏安装目录。构建默认**不会**把产物复制进游戏的 `mods/`。

## 验证

```powershell
dotnet test tests/Sts2LobbyEnhance.Tests/Sts2LobbyEnhance.Tests.csproj -c Release
dotnet run --project tools/smoke-test/smoke-test.csproj -c Release
```

`tools/` 下另有几个离线复现工具（`roster-probe` / `loadjoin-probe` / `savebinding-probe` /
`contract-check`），每个都能对着社区模组与游戏的真实程序集复现对应的上游缺陷——
它们对应的正是仍然保留的那 5 项修复。
详见 [`tests/README.md`](tests/README.md) 与 [`docs/README.md`](docs/README.md)。

**测试覆盖的边界（诚实说明）**：Windows 桌面版作房主 + Android 作客户端是反复验证过的；
Android 作房主验证过一次；**PC 作为加入方（客户端）尚未验证**。

## 目录结构

| 路径 | 内容 |
|---|---|
| `src/Sts2LobbyEnhance/` | 模组本体（`Scripts/` 按注入点分目录：Join / Wire / Tail / Gameplay / Restart / Ui / Unlock / Core） |
| `tests/` | xUnit 套件：契约、断点、发布件、UI 布局 |
| `tools/` | 离线复现与验证工具（每个都是独立可运行项目） |
| `packaging/` | 创意工坊上传工作区（`workshop.json`、封面、内容文件、条目 ID） |
| `docs/` | [技术文档](docs/README.md)、[更新日志](docs/CHANGELOG.md)、[上传流程](docs/UPLOAD.md)、调研记录 |

## 给「游戏大厅」维护者的说明

这个仓库存在的另一个目的，是让社区模组在跟进新游戏版本时**有现成的对照实现**可以抄。
相关背景已经通过 issue 提交给上游：

- [emptylower/STS2-Game-Lobby#44](https://github.com/emptylower/STS2-Game-Lobby/issues/44) ——
  0.113 的签名断裂清单与编译错误对照；
- [emptylower/STS2-Game-Lobby#45](https://github.com/emptylower/STS2-Game-Lobby/issues/45) ——
  结算页「新开一局」的设计与实现要点（该 issue 最初还提过「局中重开本层」，我们后来撤回了，
  原因见 issue 里的评论：房间中途有人掉线时它本来就失效，而那正是最需要它的场景）。

代码里对每一处上游行为都标了 `文件:行号`（以「游戏大厅」**0.6.4 的 GitHub 源码**为基准；0.6.3 时代引用的
反编译行号已随 2.0.0 一并清理）。
如果有用得上的地方，按 GPL-3.0 直接取用即可，不需要额外授权。

## 关于 AI 参与

**这个项目的代码、文档与测试基本由 AI 编程代理编写**，由维护者负责定义需求、指定约束、
提供真机环境并逐项验收。所有对外结论都要求能指到「文件:行号」或日志行，未验证的部分会明确标注。

因为 AI 生成内容需要人来担保，所以：

- 每一次发版前必须跑通 `dotnet test` + `tools/smoke-test`，且 `smoke-test` 会**真的调用**
  关键方法（不只是检查补丁挂上没有）；
- 提交给上游的 issue / PR 都会在正文开头注明是 AI 提交；
- 发现 AI 写错的地方，会在对应文档里留下更正记录，而不是改掉历史。

## 许可

GPL-3.0，全文见 [`LICENSE`](LICENSE)（与上游 `STS2-Game-Lobby` 的 LICENSE 逐字节一致）。
本模组不打包社区模组的任何文件，但源码中含从该 GPL-3.0 项目移植的逻辑，因此整体以 GPL-3.0 发布。
第三方声明见 [`docs/THIRD_PARTY_NOTICES`](docs/THIRD_PARTY_NOTICES)。

---

## English

A **companion mod** for the Slay the Spire 2 lobby mod
[`sts2_lan_connect` / LAN Connect](https://steamcommunity.com/sharedfiles/filedetails/?id=3749766330).
It ships no file of the official mod and replaces nothing: at runtime it Harmony-patches the loaded
official mod, which keeps updating from the Workshop as usual. `affects_gameplay: false`, so a patched
and an unpatched machine still join each other.

**Since 2.1.0 it does nine things**, because the lobby mod's own 0.6.4 already supports game 0.113: the
compatibility layer, Blitz / Dread hosting and room-card labels are no longer ours (all of that was removed
in 2.0.0). What remains is what the lobby does not do yet: one-click "new run" from the game-over screen,
keeping that new run's save loadable, re-baselining a shrunken roster, reordering a save-load join, and
offering only the new protocol when creating a room. Five repairs across four isolated Harmony groups;
anything that no longer resolves disables only itself and says so in the log.

Build with `dotnet build src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true`,
verify with `dotnet test` plus `tools/smoke-test`. Details in [`docs/README.md`](docs/README.md).

**AI disclosure**: this project is written largely by AI coding agents under human review; every
published claim is expected to trace to a file and line, and unverified parts are marked as such.

GPL-3.0 — see [`LICENSE`](LICENSE).
