# 游戏大厅增强 / Game Lobby Enhance

《杀戮尖塔 2》**v0.113.0** 的联机大厅增强 —— 官方模组
[`sts2_lan_connect`（游戏大厅 / LAN Connect）](https://steamcommunity.com/sharedfiles/filedetails/?id=3749766330)
的**伴生模组**（companion mod）。

**创意工坊**：<https://steamcommunity.com/sharedfiles/filedetails/?id=3839101755> ·
**QQ 群**：1135970470 ·
[更新日志](docs/CHANGELOG.md) ·
[技术文档](docs/README.md)

---

## 这是什么

它是一个**运行期补丁**，不是替代品：

- 不含、也不替换官方模组的任何文件，只在 manifest 里声明依赖；
- 启动时用 Harmony 给已经加载的官方模组打补丁，官方模组照常从创意工坊更新；
- 清单里 `affects_gameplay: false` —— 装与不装的两台机器**不会**因为模组列表不一致而互相拒绝进房；
- 只读取游戏自身的公开 / 内部 API，**不改存档格式、不改协议、不伪造进度**。

解决的问题分两类：

1. **兼容**：官方 0.6.3 是按游戏 0.111 编译的，在 0.113 上有 6 处成员签名断裂 —— 加入房间、
   建房、放弃本局等流程会抛异常或直接崩。这些被逐个接管修好。
2. **功能**：局中「重开本层」、结算页「新开一局」、极速 / 恐惧模式建房与加入、房间列表标注模式。

完整清单见 [技术文档](docs/README.md)（14 项修复，每项都有 id、注入点与替代方案），
逐版本改动见 [更新日志](docs/CHANGELOG.md)。

## 可靠性设计

每个版本、每次官方模组更新，最常见的失败模式不是崩溃，而是**补丁悄悄没装上**。
所以这一版把「版本开关」整个拆掉了：

- 14 项修复各自实现 `ICompatFix`，自己解析锚点（类型 / 方法 / 参数类型逐个比对）；
- 11 个 `FixGroup`，每组一个 Harmony id，某组失败只回滚自己那组，其它照常工作；
- 对不上时日志里明确写 `skipped: <id>: missing: <具体锚点>`，而不是整体静默失效。

启动日志里能看到一行汇总：

```
[lobby-enhance] summary: applied=14[...] skipped=0[] failed=0
```

## 环境要求

| 项 | 要求 |
|---|---|
| 游戏 | Slay the Spire 2 **0.113.0**（Windows 桌面版 / Android 第三方启动器） |
| 官方模组 | `sts2_lan_connect` **0.6.3 或更新**（创意工坊必需物品，订阅本模组会自动装上） |
| 联机双方 | **都要装**本模组。只装一边能用，但「重开 / 新开一局」等功能不对等 |

> **1.7.0 与 1.8.0 可以一起联机。** 对端识别只比对模组 id 前缀、不比较版本号
> （`NewRunFromGameOverFix.cs:59-64`），所以不需要双方同时更新；只是没升级的那一端享受不到新修的几项。

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
`contract-check` / `tail-readability`），每个都能对着官方模组与游戏的真实程序集复现对应的上游缺陷。
详见 [`tests/README.md`](tests/README.md) 与 [`docs/README.md`](docs/README.md)。

**测试覆盖的边界（诚实说明）**：Windows 桌面版作房主 + Android 作客户端是反复验证过的；
Android 作房主验证过一次；**PC 作为加入方（客户端）尚未验证**。

## 目录结构

| 路径 | 内容 |
|---|---|
| `src/Sts2LobbyEnhance/` | 模组本体（`Scripts/` 按修复分组：Join / Wire / Tail / Gameplay / Restart / Ui / Unlock / Core） |
| `tests/` | xUnit 套件：契约、断点、发布件、UI 布局 |
| `tools/` | 离线复现与验证工具（每个都是独立可运行项目） |
| `packaging/` | 创意工坊上传工作区（`workshop.json`、封面、内容文件、条目 ID） |
| `docs/` | [技术文档](docs/README.md)、[更新日志](docs/CHANGELOG.md)、[上传流程](docs/UPLOAD.md)、调研记录 |

## 给「游戏大厅」维护者的说明

这个仓库存在的另一个目的，是让官方模组在跟进新游戏版本时**有现成的对照实现**可以抄。
相关背景已经通过 issue 提交给上游：

- [emptylower/STS2-Game-Lobby#44](https://github.com/emptylower/STS2-Game-Lobby/issues/44) ——
  0.113 的签名断裂清单与编译错误对照；
- [emptylower/STS2-Game-Lobby#45](https://github.com/emptylower/STS2-Game-Lobby/issues/45) ——
  「重开本层 / 新开一局」的设计与实现要点。

代码里对每一处上游行为都标了 `文件:行号`（以部署版 0.6.3 的反编译为基准）。
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
本模组不打包官方模组的任何文件，但源码中含从该 GPL-3.0 项目移植的逻辑，因此整体以 GPL-3.0 发布。
第三方声明见 [`docs/THIRD_PARTY_NOTICES`](docs/THIRD_PARTY_NOTICES)。

---

## English

A **companion mod** for the Slay the Spire 2 lobby mod
[`sts2_lan_connect` / LAN Connect](https://steamcommunity.com/sharedfiles/filedetails/?id=3749766330).
It ships no file of the official mod and replaces nothing: at runtime it Harmony-patches the loaded
official mod, which keeps updating from the Workshop as usual. `affects_gameplay: false`, so a patched
and an unpatched machine still join each other.

Two jobs: (1) keep the official mod usable on game **0.113.0**, where six of its member signatures no
longer resolve, and (2) add the multiplayer features players kept asking for — restart the current floor,
start a new run from the game-over screen, host Blitz / Dread rooms, and label the room type in the lobby
list. 14 repairs across 11 isolated Harmony groups; anything that no longer resolves disables only itself
and says so in the log.

Build with `dotnet build src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true`,
verify with `dotnet test` plus `tools/smoke-test`. Details in [`docs/README.md`](docs/README.md).

**AI disclosure**: this project is written largely by AI coding agents under human review; every
published claim is expected to trace to a file and line, and unverified parts are marked as such.

GPL-3.0 — see [`LICENSE`](LICENSE).
