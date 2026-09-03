# VelaShell.Tests

> 应用入口项目 [`VelaShell`](../../src/VelaShell) 的单元测试、集成测试与冒烟测试。

覆盖面最广的测试集：窗口级 ViewModel、身份验证流程、应用服务，以及跨平台发布与 headless UI 的集成验证。经 `InternalsVisibleTo` 可访问 App 的 `internal` 成员。

## 覆盖范围

| 目录 | 被测对象 |
|------|----------|
| `ViewModels/` | `MainWindowViewModel`（含 `MainWindowSshFeatureTests`）、`SettingsViewModel`、`TerminalTabViewModel`、`CommandPaletteViewModel`、`ConnectionProfileViewModel`、`FileBrowser/FileTransfer/LocalFilePaneViewModel`、`FileConflictResolutionTests`（同名文件冲突策略）、`StandaloneSftpDocumentBehaviorTests`（独立 SFTP 标签）、`QuickCommandsViewModel`、`TunnelPanelViewModel`、`SessionTreeViewModel`（含拖动分组与空分组自动删除）、`ProcessManagerViewModel`（任务管理器）、`TraceRouteViewModel`（路由追踪）等。 |
| `ViewModels/`（认证） | `AuthenticationDialogViewModelTests`、`InteractiveAuthFlowTests`、`InteractivePromptDetectionTests`、`SecretPromptDetectionTests`、`PromptCommandExtractionTests` —— 两步身份验证与交互式提示识别。 |
| `Services/` | `KeyboardShortcutService`、`CommandSuggestionProvider`、`ThemeService`、`InputLocaleSwitcher`、`ExternalEditSessionManager`、`SyntaxHighlighting`、`SyncDebounceLifecycle`、`PackageVersions`，以及自更新全链路（`UpdateService`/`UpdateApplier`/`UpdateManifest`/`UpdateVersion`/`GitHubReleaseSource`）。 |
| `Services/`（终端内文件传输） | `PickedFilesTransferSourceTests`、`FolderTransferFileSinkTests` —— 上传文件源与下载落盘目录的边界与路径安全（ZMODEM / XMODEM / YMODEM 共用）。 |
| `Docking/` | `DockWorkspace` 分屏模型。 |
| `Behaviors/` | `EnglishInputLocaleUiTests`：终端聚焦时的输入法语言切换行为。 |
| `Views/` | headless 下的视图行为与视觉回归：设置页、连接弹窗（含 FTP 页签与协议下划线定位）、FTP 连接链路（`FtpConnectionFlowTests`：真连环回 FTP 服务器并列出远端文件）与 `FtpSessionStatusTests`、文件浏览器列与框选（`*MarqueeUiTests` + `MarqueeSelectionMathTests`）、文件传输面板、隧道面板、侧栏快捷命令与会话树分组菜单、会话导入、任务管理器、资源监视器、路由追踪、插件 UI（`PluginPanelUiTests`、`PluginPermissionDialogTests`、`PluginThemeTokensTests`）、Dock 动效与空白排查（`DockContentBlankHuntTests`）、字体接线（`EmbeddedFontTests`、`TerminalFontFallbackTests`、`UiFontSettingsTests`——界面字号/字体只走令牌，写死即失效）、卡片圆角/按钮/滚动条样式与像素回归。 |
| `Localization/` | `LocalizedKeyUsageTests`（代码引用的键必须存在）与 `UnusedLocalizedKeyTests`（资源里不许留孤儿键）。 |
| `Integration/` | `HeadlessUiTests`（无头 UI）、`SshIntegrationTests`（真实 SSH）、`ZModemRealChannelIntegrationTests`（真实通道上的 ZMODEM 收发）、`CrossPlatformPublishTests`（跨平台发布）。 |
| `SmokeTest.cs` | 应用启动冒烟测试。 |

## 运行

```bash
dotnet test tests/VelaShell.Tests/

# 集成测试可能需要本地 SSH 测试服务器
docker-compose -f docker-compose.test.yml up
```

> **两条会「跳过」而不是失败的用例**，缺了外部依赖时报 Inconclusive：
>
> - `ShortcutCatalogTests.Doc_ListsEveryCatalogEntry` 要拿快捷键总表比对
>   [velashell-docs](https://github.com/VelaShellLabs/velashell-docs) 的 `zh/host/快捷键参考.md`。把文档仓库检出到本仓库**同级目录**即可，
>   或用 `VELASHELL_DOCS_DIR` 指向它。
> - `PromptHookShellTests` 要起一个真正的 `bash` 跑 SSH 目录上报钩子（那段是 shell 语义，
>   C# 只断言得了字符串里有什么）。Linux/macOS 自带；Windows 上来自 Git for Windows。

> **headless UI 测试的两条硬约束**：全套共用一条 UI 线程 —— 测试体必须同步（`return Task.CompletedTask`，写成 `async` 会绑错重载导致断言一条不执行却全绿），且结束前必须关窗，否则整个套件永久卡死。排查用 `--blame-hang-timeout`。
