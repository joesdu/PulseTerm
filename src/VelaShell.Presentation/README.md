# VelaShell.Presentation

> 表现逻辑层 —— 跨层 ViewModel、工作流编排与命令注册，**不含 XAML 视图**。

`VelaShell.Presentation` 承载那些「与具体窗口无关、可被多个视图复用」的表现逻辑：侧边栏、状态栏、标签页、会话树等 ViewModel，以及把领域服务编排成用户可见流程的工作流服务。它只依赖 `Core` 与 `Terminal`，**不引用 Avalonia UI 框架也不含 `.axaml`**，因此可在无窗口环境下对交互逻辑做单元测试。

> 说明：与单个窗口强绑定的 ViewModel（`MainWindowViewModel`、各设置页、对话框等）以及全部 XAML 视图，位于应用入口项目 [`VelaShell`](../VelaShell)。本项目只放**可复用、跨视图**的部分。

## 🗂️ 目录结构

| 路径 | 职责 |
|------|------|
| `ViewModels/SidebarViewModel.cs` | 侧边栏：资源管理器分组 + 最近连接聚合。 |
| `ViewModels/SessionTreeViewModel.cs` `SessionTreeNodeViewModel.cs` | 按分组维护连接配置的树形结构（新建/编辑/删除/双击直连）。 |
| `ViewModels/RecentConnectionsViewModel.cs` `RecentConnectionItemViewModel.cs` | 「最近连接」列表：名称-分组 + 相对时间，重启不丢失。 |
| `ViewModels/StatusBarViewModel.cs` | 实时状态栏：连接状态、延迟、时长、终端尺寸、编码、CPU/内存/网速。 |
| `ViewModels/TabBarViewModel.cs` `TabViewModel.cs` | 标签页栏与单标签状态。 |
| `ViewModels/QuickCommands*.cs` `QuickCommandRunnerViewModel.cs` 等 | 快捷命令：分组/条目模型、目标选择与执行请求分发。 |
| `ViewModels/TerminalTargetSelectorViewModel.cs` | 命令/传输的目标终端选择（多标签广播与定向发送共用）。 |
| `ViewModels/SessionImport*.cs` | 从其他工具导入会话：多来源自动扫描、跨来源去重、智能勾选与按来源写入（`SessionImportViewModel` + 来源/条目两级子 VM）。 |
| `Services/ConnectionWorkflowService.cs` | 连接工作流：把「校验 → 认证 → 建立会话」编排为一条可复用流程；跳板机链（`JumpHostProfileId`）的解析、环检测与跳数上限也在此。 |
| `Services/TunnelWorkflowService.cs` | 隧道工作流：统一封装隧道创建类型分派、快照读取、停止与移除。 |
| `Services/ConnectionDiagnosticsService.cs` `ConnectionTestResult.cs` | 连接诊断与测试结果模型。 |
| `Commands/CommandRegistry.cs` | 全局命令注册表，供命令面板与快捷键系统消费。 |
| `Plugins/PluginCommandsApi.cs` | 插件命令能力（SDK 的 `ICommandsApi`）到命令注册表的桥接：强制 `<pluginId>.` 前缀防冒名，命令体在后台线程执行且异常只记入插件日志，插件停用时注销其全部命令。 |
| `DependencyInjection/PresentationServiceCollectionExtensions.cs` | 本层 ViewModel 与服务的 DI 注册入口。 |

> `Services/` 下每个工作流都是「接口 + 实现」成对出现（`IConnectionWorkflowService`/`ConnectionWorkflowService` 等），视图与测试都只依赖接口。

## 🔑 核心思路

- **视图无关**：ViewModel 通过接口消费 Core 服务与 Terminal 引擎，不引用任何 UI 控件，保证可测试与可复用。
- **工作流编排**：`*WorkflowService` 把分散的领域调用（认证、诊断、建链、隧道）聚合成面向用户意图的单一入口，视图只需触发一个方法。
- **命令中枢**：`CommandRegistry` 是命令面板（`Ctrl+P`/`Ctrl+K`）与快捷键的共同数据源，命令定义一次、多处触发。

## 🔗 依赖关系

- **引用**：`VelaShell.Core`、`VelaShell.Terminal`、`VelaShell.PluginSdk`（仅为插件命令桥接；不引入任何重量级依赖）。**无任何 NuGet 包直接引用。**
- **被引用**：`VelaShell`（App）。

> 测试见 [`tests/VelaShell.Presentation.Tests`](../../tests/VelaShell.Presentation.Tests)。
