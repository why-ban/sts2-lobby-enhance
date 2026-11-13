# work/companion —— 产品工程

> **本目录只是工作区的一部分。完整背景、用户要求、发布流水线、已验证的技术事实、踩过的坑、未解决事项，
> 全部在上一级的 [`../../AGENTS.md`](../../AGENTS.md)（工作区根 `D:\zhuanban\sts2m\AGENTS.md`）。**
>
> 新会话如果是从本目录打开的，**先读那份**，再动手。
>
> 三条最容易忘的：
> 1. 官方模组有两个「0.6.3」：核对签名用 `work\lobby\dec-official`（实际加载的 DLL 反编译），
>    读设计意图用 `work\lobby\STS2-Game-Lobby`（已被改成 0.113 的源码树）。**别搞混。**
> 2. 发布顺序不可颠倒：build → build-content → pack-release → test。
> 3. 本模组是**伴生模组**：不包含、不替换官方 `sts2_lan_connect` 的任何文件。
