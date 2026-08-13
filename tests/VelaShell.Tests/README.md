# VelaShell.Tests

> 应用入口项目 [`VelaShell`](../../src/VelaShell) 的单元测试、集成测试与冒烟测试。

覆盖面最广的测试集：窗口级 ViewModel、身份验证流程、应用服务，以及跨平台发布与 headless UI 的集成验证。经 `InternalsVisibleTo` 可访问 App 的 `internal` 成员。

## 覆盖范围

| 目录 | 被测对象 |
|------|----------|
| `ViewModels/` | `MainWindowViewModel`（含 `MainWindowSshFeatureTests`）、`SettingsViewModel`、`TerminalTabViewModel`、`CommandPaletteViewModel`、`ConnectionProfileViewModel`、`FileBrowser/FileTransfer/LocalFilePaneViewModel`、`StandaloneSftpDocumentBehaviorTests`（独立 SFTP 标签）、`QuickCommandsViewModel`、`TunnelPanelViewModel`、`SessionTreeViewModel` 等。 |
| `ViewModels/`（认证） | `AuthenticationDialogViewModelTests`、`InteractiveAuthFlowTests`、`InteractivePromptDetectionTests`、`SecretPromptDetectionTests`、`PromptCommandExtractionTests` —— 两步身份验证与交互式提示识别。 |
| `Services/` | `KeyboardShortcutService`、`CommandSuggestionProvider`、`ThemeService`、`InputLocaleSwitcher`、`ExternalEditSessionManager`、`SyntaxHighlighting`、`SyncDebounceLifecycle`、`PackageVersions`，以及自更新全链路（`UpdateService`/`UpdateApplier`/`UpdateManifest`/`UpdateVersion`/`GitHubReleaseSource`）。 |
| `Services/`（ZMODEM） | `FileZModemFileSourceTests`、`FolderZModemFileSinkTests` —— 上传文件源与下载落盘目录的边界与路径安全。 |
| `Docking/` | `DockWorkspace` 分屏模型。 |
| `Views/` | headless 下的视图行为与视觉回归：设置页、连接弹窗、会话导入弹窗（自动模式不摆勾选框、切自定义才展开）、文件浏览器列、隧道面板、侧栏快捷命令、Dock 动效、菜单字形与像素回归。 |
| `Localization/` | `LocalizedKeyUsageTests`：校验代码中引用的本地化键均存在。 |
| `Integration/` | `HeadlessUiTests`（无头 UI）、`SshIntegrationTests`（真实 SSH）、`CrossPlatformPublishTests`（跨平台发布）。 |
| `SmokeTest.cs` | 应用启动冒烟测试。 |

## 运行

```bash
dotnet test tests/VelaShell.Tests/

# 集成测试可能需要本地 SSH 测试服务器
docker-compose -f docker-compose.test.yml up
```
