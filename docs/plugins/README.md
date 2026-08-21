# VelaShell 插件系统设计文档

> 📦 **仓库拆分注记(2026-08-21)**:插件 SDK、`dotnet new` 模板、`vela-plugin` CLI 与
> 全部第一方插件已迁到
> [joesdu/velashell-plugin-toolchain](https://github.com/joesdu/velashell-plugin-toolchain)。
> 本页(以及 01–15 蓝图)里出现的 `plugin-sdk/…`、`plugins/VelaShell.Plugin.…`、
> `tools/…`、`templates/…`、`tests/VelaShell.Plugin.*.Tests` 等路径,现在都指那个仓库;
> 主仓库这边只剩 `src/`(宿主实现)与 `tests/`(宿主测试,插件夹具在 `tests/fixtures/`)。
> 结论与验收证据本身不受影响,仅路径的仓库归属变了。

> 状态:**v1 简化版已实现**(2026-08:双宿主模式 + 完整 Avalonia UI);编号 01–15 的
> 文档保留为完整插件平台的**长期设计蓝图**(进程隔离、权限系统、打包分发/商店等
> 按需分期落地,分发系统已显式推迟)。实现过程中的任何决策变更都应回写到对应文档。
>
> ⚠️ **面向插件作者的四篇文档已随工具链搬走**(2026-08-21):
> `dev-guide.md` / `cli.md` / `publishing.md` / `sdk-reference.md` 现在在
> **[joesdu/velashell-plugin-toolchain](https://github.com/joesdu/velashell-plugin-toolchain/tree/main/docs)**,
> 与 SDK、模板、CLI、第一方插件在同一个仓库里 —— 它们描述的是插件作者的世界,
> 跟着代码走才不会漂。本目录留下的是**宿主侧**的设计蓝图。
>
> **写插件请直接读
> [dev-guide.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/dev-guide.md)** ——
> 它描述当前真实可用的 API;蓝图文档中的接口形态(每插件一进程、Broker 等)v1 并未实现。

## 一句话愿景

让 VelaShell 成为一个**可扩展的运维工作台**:插件运行在独立的 PluginHost
进程中,与主程序、以及插件彼此之间完全隔离——插件卡顿或崩溃不影响主程序;
插件通过 Android 式的显式授权获得远程文件、本地文件、终端等敏感能力;
开发者使用官方 SDK 与标准接口开发、打包、发布插件,用户可动态安装与卸载。

## 文档地图

| 文档 | 内容 | 读者 |
| --- | --- | --- |
| [dev-guide.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/dev-guide.md) | **开发指南(已实现)**:快速上手、清单、生命周期、能力 API、隔离模式、测试、部署、性能纪律 | 插件开发者(必读) |
| [cli.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/cli.md) | **`vela-plugin` 手册**:开发内环(`dev init`)、体检(`doctor`)、校验/打包/签名、宿主启动参数 | 插件开发者 |
| [publishing.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/publishing.md) | **打包与发布**:Release 构建、`.vpx`、签名与信任、发布到[插件商店](http://market.easilynet.top)、CI 出包 | 插件开发者 |
| [sdk-reference.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/sdk-reference.md) | **SDK 参考**:包结构、入口契约、能力域一览、SDK 版本历史、测试替身、装载模型 | 插件开发者 |
| [STATUS.md](STATUS.md) | **进度总览(单一权威)**:分项完成度、验收证据、刻意决策、下一步建议 | 所有人 |
| [01-vision-and-goals.md](01-vision-and-goals.md) | 愿景、目标/非目标、典型场景、与 VSCode 等系统的对比 | 所有人 |
| [02-architecture.md](02-architecture.md) | 总体架构、进程模型、组件划分、工程目录规划、关键决策记录 | 所有人 |
| [03-plugin-model.md](03-plugin-model.md) | 插件包格式(.vpx)、manifest 规范、激活事件、生命周期、贡献点 | 宿主开发者、插件开发者 |
| [04-plugin-host.md](04-plugin-host.md) | PluginHost 进程设计、装载/卸载、健康监控、崩溃恢复、资源控制 | 宿主开发者 |
| [05-ipc-protocol.md](05-ipc-protocol.md) | 传输层、JSON-RPC 协议、握手与版本协商、流式与大块数据通道 | 宿主开发者 |
| [06-permission-system.md](06-permission-system.md) | 权限清单、权限分级、授权交互、持久化与撤销、审计 | 所有人 |
| [07-capability-apis.md](07-capability-apis.md) | 各能力域 API:远程文件、本地文件、终端、会话、存储、网络等 | 宿主开发者、插件开发者 |
| [08-ui-extensions.md](08-ui-extensions.md) | UI 贡献点、VelaUI 远程界面树、图像/音频专用表面、主题与 i18n | 宿主开发者、插件开发者 |
| [09-sdk-and-tooling.md](09-sdk-and-tooling.md) | SDK NuGet 包、项目模板、vela-plugin CLI、调试体验、示例插件 | 插件开发者 |
| [10-packaging-and-distribution.md](10-packaging-and-distribution.md) | 打包、签名、安装/更新流程、插件源(Registry)设计 | 宿主开发者 |
| [11-automation-and-ai.md](11-automation-and-ai.md) | 自动化触发器/动作模型、AI 能力网关设计 | 宿主开发者、插件开发者 |
| [12-security-threat-model.md](12-security-threat-model.md) | 信任模型、威胁分析、攻击面与缓解措施、OS 级沙箱路线 | 所有人(必读) |
| [13-testing-strategy.md](13-testing-strategy.md) | 契约测试、宿主测试、插件测试工具、混沌测试 | 宿主开发者 |
| [14-roadmap.md](14-roadmap.md) | 总体开发计划:里程碑、任务分解、依赖关系、验收标准 | 所有人 |
| [15-ecosystem-ideas.md](15-ecosystem-ideas.md) | **提案**:插件构想清单、新扩展点评估(VFS/OSC 133/会话组等)、v1 简化项与增强项 | 所有人 |

各分项文档末尾均带有该分项自己的「开发计划」小节;
[14-roadmap.md](14-roadmap.md) 汇总所有分项计划并排出里程碑顺序。

## 术语表

| 术语 | 含义 |
| --- | --- |
| **宿主(Host)** | VelaShell 主进程,拥有全部 UI、SSH 连接与用户数据 |
| **PluginHost** | 随主程序分发的独立可执行程序,每个插件默认运行在自己的一个 PluginHost 进程内 |
| **插件(Plugin)** | 使用官方 SDK 开发、以 .vpx 包分发的第三方/第一方扩展 |
| **Manifest** | 插件包内的 `plugin.json`,声明身份、入口、激活事件、贡献点与权限 |
| **贡献点(Contribution)** | 插件以声明方式向宿主注册的 UI/行为扩展位:命令、菜单、侧栏视图、文档页、状态栏、设置页等 |
| **激活事件(Activation Event)** | 触发插件从"已安装"进入"已激活"(启动进程并调用入口)的条件 |
| **能力(Capability)** | 宿主经 RPC 暴露给插件的服务域,如 `vela.remoteFs`、`vela.terminal` |
| **权限(Permission)** | 使用某能力所需的授权项,分为普通权限与危险权限,危险权限需用户显式同意 |
| **Broker** | 主进程内的权限代理:所有能力调用的强制检查点 |
| **VelaUI** | 面向插件的声明式远程界面协议:插件描述控件树,宿主负责渲染,事件回传 |
| **.vpx** | VelaShell Plugin Package,zip 容器,含 manifest、程序集、资源与签名 |
| **apiLevel** | 插件 API 的整数版本号,宿主承诺同 apiLevel 内向后兼容 |

## 阅读顺序建议

- **写插件**:[dev-guide.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/dev-guide.md)(现状唯一权威)→ 手边备着
  [cli.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/cli.md) 与 [sdk-reference.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/sdk-reference.md);要发布时读 [publishing.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/publishing.md)。
- **了解进度**:[STATUS.md](STATUS.md)。
- **研究长期设计**:01 → 02 → 12(信任模型)→ 03 → 06 → 07,其余按需;
  注意各篇顶部的"实现注记"标注了现状与蓝图的偏离。
