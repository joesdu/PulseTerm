# VelaShell 插件系统设计文档

> 状态:设计阶段(未开始编码)。本目录是插件系统的唯一权威设计来源,
> 实现过程中的任何决策变更都应回写到对应文档。

## 一句话愿景

让 VelaShell 成为一个**可扩展的运维工作台**:插件运行在独立的 PluginHost
进程中,与主程序、以及插件彼此之间完全隔离——插件卡顿或崩溃不影响主程序;
插件通过 Android 式的显式授权获得远程文件、本地文件、终端等敏感能力;
开发者使用官方 SDK 与标准接口开发、打包、发布插件,用户可动态安装与卸载。

## 文档地图

| 文档 | 内容 | 读者 |
| --- | --- | --- |
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

首次阅读:01 → 02 → 12(先建立信任模型认知)→ 03 → 06 → 07,其余按需。
只想了解"怎么写一个插件":03 → 07 → 08 → 09。
