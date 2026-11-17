# 游戏大厅增强 / Game Lobby Enhance

《杀戮尖塔 2》**v0.113.0 / 0.113.1** 的联机大厅增强 —— 社区模组
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

**2.2.0 一共 16 项修复、9 个回滚分组。** 社区模组 0.6.4 已经自己适配了游戏 0.113，所以兼容层、
极速 / 恐惧的建房与准备页、房间卡片的模式标注都不再由本模组接手（1.x 里这些接管在 2.0.0 全部删除）。

2.1.0 新加的 10 项：

1. **服务器池** —— 所有服务器的房间摆在同一个列表里，按「同版本 → 人多 → 延迟低」排序，卡片标出来源服务器；
2. **跨服加入** —— 点别家服务器的房间会先切服、复核房间仍在，再走原来的加入流程；
3. **建房自动选服** —— 建房前挑「延迟最低、允许建房、且能承载当前协议档位」的那台；
4. **建房手动选服** —— 面板里可折叠的一节，每行「名字 | 延迟 | 人数」，自动之后固定是置顶服（魔仙堡）；默认自动，选中后只在那台建房、切不过去就不建，选完自动收起；手机上可拖动滚动；
5. **进大厅直达房间列表** —— 不再先弹服务器选择器（右上角 SERVER 仍能打开）；
6. **「相同版本」筛选** —— 只显示与本机游戏版本相同的房间；
7. **聊天换服务器** —— 标题右边的 `‹ ›` 换着看不同服务器的公共频道，`⟳` 一键回置顶服；不切服、不退房、不写配置；
8. **公告跟随频道** —— 公告内容跟着聊天频道当前那台服务器走，外观与上游完全一致；
9. **手机滑动聊天** —— 滑动即滚动、长按 0.4 秒进入选区并拖动框选、松手复制选区；PC 上拖拽仍是框选；
10. **局内收起聊天不再挡鼠标** —— 那行「按下 Enter 打开聊天窗口」不再吞掉游戏右上角按钮的点击（仅电脑端）。

大厅自己没做的 6 项（11–15 在 2.0.0 就有，其中 15 在 2.1.0 改成了只保留新协议；16 是本版新补的）：

11. **结算页一键新开一局** —— 房主点一下，全房回到准备页；
12. **新开一局之后存档仍可续玩** —— 不会因为房间还记着上一局的存档键而读不回来；
13. **踢人 / 掉线之后照样能开局** —— 大厅的名册只能表达"有人加入"，减员后客户端会被判「Mod 不匹配」；
14. **读档不必按最初加入顺序进房** —— 大厅按"加入序 vs 存档序"逐位比较，同队换序就失败；
15. **建房只按新协议** —— 这一版把建房对话框里「联机协议」那一节整个摘掉：只剩新协议一条路，那一节是多余的选择题；
16. **掉线重连后开局不再报错** —— 有人离开又回来时，房主与队友的房间列表顺序不同，随机数种子按各自的列表下标分配，开局会直接抛错中断；现在房主开场前按座位把列表排好，三端种子一致。

完整清单见 [技术文档](docs/README.md)（16 项，每项都有 id、分组与注入点），
逐版本改动见 [更新日志](docs/CHANGELOG.md)。

## 可靠性设计

每个版本、每次社区模组更新，最常见的失败模式不是崩溃，而是**补丁悄悄没装上**。
所以这一版把「版本开关」整个拆掉了：

- 16 项修复各自实现 `ICompatFix`，在 `Applies()` 里一次性解析全部锚点（类型 / 方法 / 字段名逐个比对），缺哪个报哪个；
- 9 个 `FixGroup`，每组一个 Harmony id，某组失败只回滚自己那组，其它照常工作；
- 对不上时启动日志明确写 `skip <id>: missing: <具体锚点>`，而不是整体静默失效。

启动日志里能看到一行汇总：

`@
[lobby-enhance] summary: applied=16[...] skipped=0[] failed=0
`@

## 环境要求

| 项 | 要求 |
|---|---|
| 游戏 | Slay the Spire 2 **0.113.0 / 0.113.1**（Windows 桌面版 / Android 第三方启动器） |
| 社区模组「游戏大厅」 | `sts2_lan_connect` **0.6.4 或更新**（创意工坊必需物品，订阅本模组会自动装上） |

> **谁需要装**：本机想看到这些改动就得装（服务器池、筛选、聊天、选服、手机操作都是本机补丁）。
> 其中三项与对局另一侧有关：「踢人 / 掉线后照样开局」「读档免排队」要求加入的那一端也装，
> 「一键新开一局」要求房主与加入方都装，否则只有装的那一边会回准备页。

> **0.6.4 起与更早的 0.6.3 房间不互通**（上游自己改了消息布局），房间里所有人要一起升。

## 安装

创意工坊订阅即可（必需物品会自动订阅）。手动安装：

1. 把 `sts2_lobby_enhance.dll` 与 `sts2_lobby_enhance.json` 放进
   `<游戏目录>/mods/sts2_lobby_enhance/`（游戏按 `manifest.id + ".dll"` 找程序集，文件名不能改）；
2. 从旧版（`sts2_lan_connect_0113_fix`）升级时**先删掉旧目录**，两个 id 同时加载会打两套补丁；
3. 启动后在日志里搜 `[lobby-enhance]`，看到 `applied=` 那行就说明生效。

## 从源码构建

需要 .NET 9 SDK，以及已经安装的游戏（构建只**读取**游戏程序集）。

`@powershell
dotnet build src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true
`@

打发布件（写入 `packaging/content/` 与 `dist/`）：

`@powershell
pwsh -File packaging/build-content.ps1 -SkipBuild
pwsh -File tools/pack-release/pack-release.ps1
`@

`-p:Sts2Root=` 可以指向别的游戏安装目录。构建默认**不会**把产物复制进游戏的 `mods/`。

## 验证

`@powershell
dotnet test tests/Sts2LobbyEnhance.Tests/Sts2LobbyEnhance.Tests.csproj -c Release
dotnet run --project tools/smoke-test/smoke-test.csproj -c Release
dotnet run --project tools/contract-check/contract-check.csproj -c Release
`@

- `dotnet test`：61 个用例。其中 32 个需要游戏 / 官方 DLL / 构建产物，另有 6 个需要打包工作区或发布包，
  缺输入时**不是静默跳过**——覆盖率门禁会把缺的输入点名报出来。
- `smoke-test`：加载**出货的** `packaging/content/sts2_lobby_enhance.dll`，跑 `Entry.RunSilently()`，
  断言 16 项 id、顺序、分组，以及每项至少有一处真实 Harmony 接缝。**它证明的是"补丁挂上了"，不证明行为正确**。
- `tools/` 下另有离线复现工具（`roster-probe` / `loadjoin-probe` / `savebinding-probe`），
  对着真实的社区模组与游戏程序集复现对应缺陷；发版前手工各跑一次（见 [上传流程](docs/UPLOAD.md)）。
- 上传包验收 `tools/pack-upload-kit/test-upload-kit.ps1` 有 35 条断言，**全程不联系 Steam**（需要上传器输出时用假上传器）。

**测试覆盖的边界（诚实说明）**：Windows 桌面版作房主 + Android 作客户端是反复验证过的；
Android 作房主验证过一次；**PC 作为加入方（客户端）尚未验证**。

## 目录结构

| 路径 | 内容 |
|---|---|
| `src/Sts2LobbyEnhance/` | 模组本体（`Scripts/` 按注入点分目录：Core / Restart / Tail / Filter / Pool / Chat / Touch / Ui） |
| `tests/` | xUnit 套件：契约、断点扫描、发布件、UI 几何、覆盖率门禁 |
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

代码里对每一处上游行为都用**方法名与行为描述**标出来（不写行号：行号会随上游每次提交漂移，
而且本项目自己的注释规范不允许把行号写进源码）。默认基准是「游戏大厅」**0.6.4 的 GitHub 源码**；
需要引用具体位置时以该版本为准。如果有用得上的地方，按 GPL-3.0 直接取用即可，不需要额外授权。

## 关于 AI 参与

**这个项目的代码、文档与测试基本由 AI 编程代理编写**，由维护者负责定义需求、指定约束、
提供真机环境并逐项验收。所有对外结论都要求能指到「文件:行号」或日志行，未验证的部分会明确标注。

因为 AI 生成内容需要人来担保，所以：

- 每一次发版前必须跑通门禁（构建 → 内容 → 发布包 → 冒烟 → 测试 → 上传包验收），顺序见 [技术文档](docs/README.md)；
- 提交给上游的 issue / PR 都会在正文开头注明是 AI 提交；
- 发现 AI 写错的地方，会在对应文档里留下更正记录，而不是改掉历史。

## 许可

GPL-3.0，全文见 [`LICENSE`](LICENSE)（与上游 `STS2-Game-Lobby` 的 LICENSE 逐字节一致）。
本模组不打包社区模组的任何文件，但源码是对该 GPL-3.0 项目的衍生作品（所有修复都针对它的行为编写，
并通过反射调用它的内部成员），因此整体以 GPL-3.0 发布。
第三方声明见 [`docs/THIRD_PARTY_NOTICES`](docs/THIRD_PARTY_NOTICES)。

---

## English

A **companion mod** for the Slay the Spire 2 lobby mod
[`sts2_lan_connect` / LAN Connect](https://steamcommunity.com/sharedfiles/filedetails/?id=3749766330).
It ships no file of the official mod and replaces nothing: at runtime it Harmony-patches the loaded
official mod, which keeps updating from the Workshop as usual. `affects_gameplay: false`, so a patched
and an unpatched machine still join each other.

**2.1.0 is fifteen repairs in nine rollback groups.** The lobby mod's own 0.6.4 already supports game
0.113, so the compatibility layer, Blitz / Dread hosting and room-card labels are no longer ours (all of
that was removed in 2.0.0). Ten of the fifteen are new in 2.1.0: a server pool that lists every server's
rooms in one place (joining across servers switches the lobby, as the lobby itself does), straight-to-list
lobby entry, a same-game-version filter, switching which server the public chat channel points at, a
collapsible server picker inside the create-room dialog (defaults to the lowest-latency server, never
remembers a manual pick), and touch scrolling with long-press selection and copy. The other five were
already in 2.0.0: one-click "new run" from the game-over screen, keeping that new run's save loadable,
re-baselining a shrunken roster, reordering a save-load join, and creating rooms on the new protocol only.

Build with `dotnet build src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true`,
verify with `dotnet test` plus `tools/smoke-test`. Details in [`docs/README.md`](docs/README.md).

**AI disclosure**: this project is written largely by AI coding agents under human review; every
published claim is expected to trace to a file and line, and unverified parts are marked as such.

GPL-3.0 — see [`LICENSE`](LICENSE).
