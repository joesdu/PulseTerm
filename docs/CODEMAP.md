# CODEMAP —— 功能 → 代码位置速查

> 回答一个问题:「这个功能的代码在哪?」每行一个功能,指向其主目录与入口文件。
> 分层规则(一句话):**Core = 契约 + 纯逻辑;Infrastructure = I/O 实现;Terminal = VT 引擎 + 渲染控件;App 按功能分目录**。
> App 项目内:View、ViewModel、只服务于该功能的 Service 同住 `Features/<功能>/`;跨功能复用进 `Common/`;窗口壳进 `Shell/`。

## App(src/VelaShell)

| 功能 | 目录 | 主要入口 |
|---|---|---|
| 主窗口 / 标题栏 / 状态栏 / 托盘 | `Shell/` | `MainWindow.axaml`、`MainWindowViewModel.cs`(应用级编排中枢) |
| 会话:连接 / 认证 / 会话树 / 侧栏 / 终端标签 | `Features/Sessions/` | `ConnectionProfileView`、`TerminalTabView(+ViewModel)`、`SidebarView` |
| 会话:连接诊断 / 导入 / 主机密钥提示 | `Features/Sessions/` | `ConnectionDiagnosticsView`、`SessionImportView`、`HostKeyPromptView` |
| 会话:同步输入(多标签广播) | `Features/Sessions/` | `SyncInputCoordinator.cs`、`SyncInputChannels.cs` |
| SFTP:文件浏览器 / 本地面板 / 传输浮窗 | `Features/Sftp/` | `FileBrowserView(+ViewModel)`、`LocalFilePaneView`、`FileTransferView` |
| SFTP:远程编辑器(含语法高亮) | `Features/Sftp/` + `Syntax/` | `RemoteFileEditorView`、`Syntax/SyntaxHighlightingService.cs` |
| SFTP:ZModem(rz/sz)接线 | `Features/Sftp/ZModem/` | `ZModemTransferObserver.cs`(协议在 Core/ZModem) |
| 隧道(端口转发)面板 | `Features/Tunnels/` | `TunnelPanelView(+ViewModel)` |
| 监控:进程管理器 / 资源监视 / 路由追踪 | `Features/Monitoring/` | `ProcessManagerView`、`ResourceMonitorWindow`、`TraceRouteWindow` |
| 会话录制与回放 | `Features/Recording/` | `SessionRecorder.cs`、`RecordingPlayerView` |
| 命令面板(Ctrl+P/K) | `Features/CommandPalette/` | `CommandPaletteView(+ViewModel)` |
| 快捷命令(侧栏面板) | `Features/QuickCommands/` | `QuickCommandsView`(VM 在 Presentation) |
| 命令补全 / 历史建议 | `Features/Suggestions/` | `CommandSuggestionProvider.cs`、`CommandHistoryService.cs` |
| 设置(全部页面)/ 云同步 / SSH 密钥管理 | `Features/Settings/` | `SettingsView(+ViewModel)`、`Pages/*` |
| 自更新(检查 / 下载 / 换版重启) | `Features/Update/` | `UpdateService.cs`、`UpdateApplier.cs` |
| 停靠 / 分屏(自研 VelaDock) | `Docking/` | `DockWorkspace`(Model)、`Controls/DockTabItem` |
| 跨功能:对话框 / 转换器 / 行为 / i18n 标记扩展 | `Common/` | `MessageDialog`、`Converters/*`、`Localization/LocalizeExtension.cs` |
| 跨功能:快捷键映射服务 | `Common/Input/` | `KeyboardShortcutService.cs` |

## 底层项目

| 关注点 | 位置 |
|---|---|
| VT 解析 / 仿真 / 屏缓冲 / 输入编码 | `src/VelaShell.Terminal/Emulation/` |
| 终端自绘控件(渲染 / 选区 / IME / 滚动) | `src/VelaShell.Terminal/Rendering/VelaTerminalControl.cs` |
| 键盘路由(快捷键优先级 / 按键编码决策) | `src/VelaShell.Terminal/Input/TerminalKeyRouter.cs` |
| SSH / SFTP / 隧道 / 指标 的接口契约与中立异常 | `src/VelaShell.Core/Ssh/`、`Core/Sftp/`、`Core/Tunnels/` |
| SFTP 服务与传输队列(纯逻辑) | `src/VelaShell.Core/Sftp/` |
| ZModem 协议(传输无关) | `src/VelaShell.Core/ZModem/` |
| 领域模型(SessionProfile / AppSettings…) | `src/VelaShell.Core/Models/` |
| i18n 资源(五语言 resx)与本地化服务 | `src/VelaShell.Core/Resources/`、`Core/Localization/` |
| Tmds.Ssh 封装(库异常翻译只在这里) | `src/VelaShell.Infrastructure/Ssh/` |
| SonnetDB 持久化(会话/设置/known_hosts/审计) | `src/VelaShell.Infrastructure/Persistence/` |
| ConPTY 本地终端 | `src/VelaShell.Infrastructure/Pty/` |
| 侧栏/标签栏/快捷命令 VM、命令注册表、连接编排 | `src/VelaShell.Presentation/` |
| 通用控件库 + 设计 token / 主题字典 | `src/VelaShell.Controls/` |

## 测试

每个 src 项目对应 `tests/<项目名>.Tests`;App 的测试(`tests/VelaShell.Tests`)按 `Services/ViewModels/Views` 分目录(历史结构,文件名 = 被测类名,用文件名搜索即可)。
