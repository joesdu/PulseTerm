# VelaShell.PluginHost（隔离插件宿主进程）

> 一个可执行体，一次承载**一个**插件 —— 经命名管道 RPC 连回主程序，在可收集 ALC 中装载并运行它。

`VelaShell.PluginHost` 是插件系统的「隔离模式」落地：清单里声明 `isolated` 的插件不在主程序里装载，而由主程序按需拉起本进程。插件崩溃、卡死、内存泄漏都被关在这个进程里，进程退出即彻底卸载，主程序毫发无损。

进程内模式与隔离模式**共用同一份插件源码** —— 插件拿到的仍是 `IPluginContext`，只是这里的远程能力是 RPC 代理。这正是 SDK 坚持「能力接口传输无关」的兑现处。

## 🗂️ 文件结构

| 文件 | 职责 |
|------|------|
| `Program.cs` | 进程入口：读环境变量 → 连管道 → 握手 → 装载插件 → 应答激活/停用 → 随连接断开或父进程退出而终结。主线程跑本进程内建的 Avalonia 派发循环，RPC 与插件逻辑在后台线程。 |
| `RemotePluginContext.cs` | 隔离侧的 `IPluginContext`：远程能力（会话/远程执行/远程文件/机密/命令/时序/事件）为 RPC 代理，存储、日志、界面在本进程直接执行。 |
| `RemoteCapabilities.cs` | 各能力接口的 RPC 代理实现，以及把插件日志转发回宿主日志管道的 `RpcLogger`。 |
| `PluginHostApp.cs` | 本进程的 Avalonia `Application`：只提供 Fluent 基础主题让标准控件有模板可用，明暗跟随宿主（握手快照 + `themeChanged` 事件）。 |
| `PluginHostUi.cs` | 隔离插件的界面能力：面板呈现为**独立卡片窗口**。`Document` 与 `Window` 两种展示模式在隔离下都落到窗口 —— 跨进程窗口收养（`SetParent`）与 dock 的单宿主 reparenting 有根本张力，切标签反复摘挂会卡顿甚至让窗口飘出。 |
| `PluginHostShellWindow.cs` | 自绘卡片窗壳（与主程序资源监视/任务管理器同规格）：透明窗口 + 8px 圆角 + 40px 标题栏 + 三连按钮 + 自绘缩放抓取区。纯代码构建 —— 本项目不引用 `VelaShell.Controls`。 |
| `PluginHostThemeTokens.cs` | 把宿主下发的 `Vela*` 令牌快照注入本进程 `Application` 资源，插件里的 `{DynamicResource VelaXxx}` 在隔离模式下与进程内一样生效，主题切换时宿主重发、自动刷新。 |

## 🔑 核心思路

- **依赖纪律**：只引用 `VelaShell.PluginSdk`，**严禁引用任何 `VelaShell.*` 主程序工程**（设计稿 [02 §2](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/02-architecture.md)）。宿主的领域模型、SSH 库、持久化引擎一概不进这个进程。
- **一进程一插件**：进程即隔离边界，退出即卸载，不存在程序集卸载残留与 ALC 泄漏。
- **凭据不出主进程**：启动参数（管道名、一次性令牌、插件 id/版本/入口/数据目录）全部经**环境变量**传递，不进命令行 —— 命令行会出现在进程列表里。管道随机命名且仅当前用户可连；握手完成前除 `handshake` 外一切调用拒绝。
- **父进程守望**：主程序崩溃或被杀（来不及发 deactivate）时，本进程按 `VELA_PARENT_PID` 自行退出，绝不孤儿常驻。
- **默认软件渲染**：插件面板是轻量界面，不值得每个插件进程各自映射一整套显卡驱动模块（GPU 后端单进程可多占 ~170MB 常驻）。需要 GPU 的插件设 `VELA_PLUGIN_GPU=1` 放开。
- **完整的 Avalonia**：内建与宿主同版的 Avalonia（版本由 CPM 钉住），插件可用 AXAML、样式、国际化与第三方组件包；插件程序集对 Avalonia 的引用经 ALC 回落到本进程这套，保证类型同一。

## 🔌 启动契约（环境变量）

| 变量 | 含义 |
|------|------|
| `VELA_PLUGIN_PIPE` | 命名管道名（宿主随机生成）。 |
| `VELA_PLUGIN_TOKEN` | 一次性握手令牌。 |
| `VELA_PLUGIN_ID` / `VELA_PLUGIN_VERSION` | 插件标识与版本。 |
| `VELA_PLUGIN_ENTRY` | 插件入口程序集的绝对路径。 |
| `VELA_PLUGIN_DATA_DIR` | 该插件的私有数据目录。 |
| `VELA_PARENT_PID` | 主程序 PID，用于父进程守望（可选）。 |
| `VELA_PLUGIN_GPU` | 置 `1` 启用 GPU 渲染（默认软件渲染）。 |

宿主侧对应实现见 [`VelaShell.Infrastructure/Plugins/Isolated/`](../VelaShell.Infrastructure/Plugins/Isolated)（`PluginProcessClient` 拉起进程与握手，`PluginCapabilityRouter` 把 RPC 分发到与进程内插件同一套能力实现）。

## 🔗 依赖关系

```text
VelaShell.PluginHost
   └─► VelaShell.PluginSdk        （唯一的项目引用）
   └─► Avalonia / Avalonia.Desktop / Avalonia.Themes.Fluent
```

- **被引用**：无。它是第二个可执行体，由 [`VelaShell`](../VelaShell) 以 `ProjectReference` 建立构建顺序并随包分发（`VelaShell.PluginHost.exe` 与主程序同目录）。
- **发布形态约束**：正因为需要磁盘上的真实可执行体，应用**刻意不用 `PublishSingleFile`**（摊开发布，两个可执行体共用同一份运行时）；否则打包版里隔离插件必然拉不起宿主。详见 [`VelaShell.csproj`](../VelaShell/VelaShell.csproj) 顶部注释。

## 📚 相关文档

- [04-plugin-host.md](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/04-plugin-host.md) —— 宿主进程设计
- [05-ipc-protocol.md](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/05-ipc-protocol.md) —— RPC 协议
- [06-permission-system.md](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/plugins/06-permission-system.md) —— 权限模型
- [dev-guide.md](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md) —— 插件开发指南

> 隔离链路的测试在 [`tests/VelaShell.Infrastructure.Tests/Plugins/`](../../tests/VelaShell.Infrastructure.Tests/Plugins)（`IsolatedPluginTests`、`RpcConnectionTests`、`StreamingRoutingTests`、`EmbedRoutingTests` 等）。
