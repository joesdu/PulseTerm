# VelaShell（桌面应用入口）

> 组合根 + XAML 视图 + 应用级服务 —— 把各层装配为可运行的跨平台桌面程序。

`VelaShell` 是解决方案的**可执行入口**（`OutputType=WinExe`，程序集名 `VelaShell`）。它是唯一的**组合根（Composition Root）**：所有依赖注入注册在此汇聚，所有窗口/对话框的 XAML 视图与主题样式在此定义，并托管托盘、自动更新、会话录制等应用级服务。

> 入口项目位于 `src/VelaShell`，程序集名为 `VelaShell`。

## 🗂️ 目录结构

| 路径 | 职责 |
|------|------|
| `Program.cs` | 进程入口：配置 Avalonia AppBuilder、单实例锁、自更新启动期收尾（清理 `*.old` / 崩溃回滚）、启动生命周期。 |
| `App.axaml` / `App.axaml.cs` | **组合根**：构建 DI 容器、注册所有层的服务、创建主窗口、加载主题与全局样式。 |
| `Views/` | 全部 XAML 视图：`MainWindow`、`SettingsView` 及 11 页设置（常规/外观/终端/密钥/快捷键/传输/安全审计/片段/云同步/关于/捐赠）、认证对话框、命令面板、SFTP 文档（本地 `LocalFilePaneView` + 远程 `FileBrowserView` 双栏）、文件传输浮窗、远程文件编辑器、隧道面板、录制回放、资源监视器（标签悬停面板 + `ResourceMonitorWindow` 图表窗口：总览/CPU/GPU/内存/磁盘/网络六页）、连接诊断、主机指纹提示等。 |
| `ViewModels/` | 与窗口强绑定的 ViewModel：`MainWindowViewModel`（热点文件）、`SettingsViewModel`、`TerminalTabViewModel`、`AuthenticationDialogViewModel`、`CommandPaletteViewModel`、`FileBrowser/FileTransferViewModel`、`SshKeyManagerViewModel`、`SyncViewModel`、`RecordingPlayerViewModel` 等。 |
| `Docking/` | 自研 VelaDock 可拖拽分屏系统：`DockWorkspace` 模型、拖拽控制器、放置覆盖层，以及 `TerminalDocument`（终端会话）与 `SftpDocument`（独立 SFTP 标签）两类文档宿主。 |
| `Services/` | 应用级服务：`SessionRecorder`/`SessionLogService`（会话录制）、`UpdateService`（便携式自更新，GitHub Releases 清单 + 原地换版，见 `Services/Update/`）、`TrayIconService`（托盘）、`KeyboardShortcutService`（快捷键）、`CommandHistoryService`/`CommandSuggestionProvider`（命令建议）、`StartupRegistration`（开机自启）、`ExternalEditSessionManager`（外部编辑器回传）、`SyncInputCoordinator`（多终端同步输入/广播）等。 |
| `Services/Syntax/` | 远程文件编辑器的语法高亮：`FileTypeDetector`（按扩展名/内容判定类型）+ `SyntaxHighlightingService`（供 AvaloniaEdit 使用）。 |
| `Services/ZModem/` | ZMODEM 的应用侧落地：`FileZModemFileSource`（上传源）、`FolderZModemFileSink`（下载落盘目录）、`ZModemTransferObserver`（进度上报到传输浮窗）、目录选择提示。 |
| `Themes/` | `DarkTheme`/`LightTheme`/`InputStyles`/`DockStyles`：应用级主题与样式覆盖。 |
| `Converters/` `Behaviors/` `Controls/` | XAML 值转换器、输入行为（`SecurePasswordBox`、`EnglishInputLocale`）、`ReparentingHost` 宿主控件。 |
| `Security/` `Localization/` | `SecureStringConvert` 安全字符串互转、`LocalizeExtension` XAML 本地化标记扩展。 |
| `Assets/` | 图标、捐赠二维码等资源。 |
| `app.manifest` `Info.plist` `*.desktop` | Windows / macOS / Linux 平台清单与桌面集成。 |

## 🔑 核心思路

- **单一组合根**：所有 DI 注册集中在 `App.axaml.cs`，各层通过各自的 `*ServiceCollectionExtensions` 贡献注册，依赖装配一目了然、可追溯。
- **MVVM + ReactiveUI**：视图（`Views/`）与 ViewModel（`ViewModels/` 及 `Presentation` 层）分离，编译时绑定（`AvaloniaUseCompiledBindingsByDefault`）保证性能与类型安全。
- **可拖拽分屏**：`Docking/` 实现多终端并行操作的标签重排、跨组并入与边缘五区分屏（浮动窗口按产品决策不实现）。
- **应用级横切**：托盘、自动更新、会话录制、开机自启等只在最外层装配，不污染领域与表现层。

## 🔗 依赖关系（依赖图顶点）

```text
VelaShell (App)
   ├─► VelaShell.Core
   ├─► VelaShell.Terminal
   ├─► VelaShell.Presentation
   ├─► VelaShell.Controls
   └─► VelaShell.Infrastructure
```

- **包**：`Avalonia.Desktop`、`Avalonia.Themes.Fluent`、`Avalonia.Fonts.Inter`、`Avalonia.AvaloniaEdit`、`ReactiveUI.Avalonia`、`Microsoft.Extensions.DependencyInjection`。
- **Release 发布**：`SelfContained`（摊开发布，刻意不用 `PublishSingleFile`——隔离插件的 `VelaShell.PluginHost` 需要磁盘上的真实可执行体），目标机器无需预装 .NET Runtime。
- `InternalsVisibleTo` 暴露给 [`tests/VelaShell.Tests`](../../tests/VelaShell.Tests)。

## 🚀 运行

```bash
# 开发模式（在仓库根目录）
dotnet run --project src/VelaShell/VelaShell.csproj

# 发布为 Windows 独立可执行文件
dotnet publish src/VelaShell/VelaShell.csproj -c Release -r win-x64 --self-contained true
```

> 更完整的构建/发布/数据目录说明见[仓库根 README](../../README.md)。
