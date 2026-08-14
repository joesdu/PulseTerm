# VelaShell.Core

> 领域核心层 —— 模型、服务契约、持久化抽象与本地化。**不依赖任何 UI 框架。**

`VelaShell.Core` 是整个解决方案的地基。它定义了「VelaShell 是什么」，而不关心「界面如何呈现」或「底层用哪个库实现」。所有跨层共享的领域概念、接口契约与不可变数据结构都集中在这里，因此它可以被独立编译、独立测试，并在未来整体替换上层框架而无需改动。

## 🎯 设计原则

- **无 UI 依赖**：唯一的包引用是 `ReactiveUI`（用于 `ReactiveObject` 数据模型），**不引用 Avalonia**。
- **接口优先**：对外只暴露接口（`I*Service`、`I*Store`、`I*Wrapper`），具体实现落在 `VelaShell.Infrastructure`。上层通过 DI 注入依赖，便于 Mock 与替换。
- **SSH 库中立**：Core **不引用任何具体 SSH 库**。SSH/SFTP 能力全部经 `Ssh/` 下的中立抽象（`ISshClientWrapper`、`ISftpClientWrapper`、`SftpEntry`、`VelaSshClientException` 等）访问，具体依赖（当前为 `Tmds.Ssh`）只存在于 Infrastructure。这条约束已被验证有效：从 SSH.NET 迁移到 Tmds.Ssh 时 Core 一行未改。

## 🗂️ 目录结构

| 目录 | 职责 |
|------|------|
| `Models/` | 领域模型与枚举：`ConnectionInfo`、`ConnectionType`、`ServerGroup`、`SessionProfile`、`SshSession`、`AppSettings`/`AppState`、`FtpSettings`、`KnownHost`、`QuickCommand`、`TunnelConfig`、`TransferTask`、`RemoteFileInfo`、`AuditEntry`、`TerminalColorScheme` 等，多为 `ReactiveObject` 或不可变记录。另有两个纯逻辑件：`UserPathResolver`（设置里路径取值的唯一权威解析器 —— **相对路径以用户主目录为基准，绝不落到进程工作目录上**）与 `CommonUsageCatalog`（命令补全的静态用法表）。 |
| `Data/` | 持久化契约（**纯接口**，实现全在 Infrastructure）：`IAppDataStore`、`ISessionRepository`、`ISettingsService`、`IRecentConnectionService`、`IAuditLogService`、`IQuickCommandRepository`、`ISecretProtector`（凭据加密）。 |
| `Ssh/` | SSH/SFTP 中立抽象层：客户端/Shell/SFTP 包装接口（`ISshClientWrapper`/`IShellStreamWrapper`/`ISftpClientWrapper`）、`ISshConnectionService`、`ISshKeyService`、`IHostKeyService`/`IHostKeyPrompt`、`HostKeyVerification`（主机指纹 TOFU 校验）、`PortForwarding`、`TerminalMode`、`SftpEntry`、`SecurityAlertService`、`VelaSshClientException` 异常族。 |
| `Sftp/` | 文件传输领域逻辑：`ISftpService`、`ITransferManager` 及其实现、`SerializedSftpService`（通道串行化）、`RemoteIdentityResolver`、`DragDropFormats`、`ThrottledStream`（限速流）。`ISftpService` 的每个成员都以 `Guid sessionId` 为键、返回协议无关的 `RemoteFileInfo`，因此文件浏览器/传输管理器对「后端是 SFTP 还是 FTP」完全无感（路由实现在 Infrastructure）。 |
| `Ftp/` | FTP/FTPS 会话契约：`IFtpSessionService`、`FtpConnectionInfo`、`VelaFtpException`。与 SSH 并列为一等协议，共用上面那套远程文件抽象。 |
| `Processes/` | 远端进程管理（任务管理器）契约：`IRemoteProcessService`、`RemoteProcessInfo`、`RemoteProcessProbe`（实现方持有相邻两次采样，快照给出**瞬时** CPU 占用而非生命周期平均值）。 |
| `Diagnostics/` | 路由追踪：`TraceRoute`（逐跳模型与 `HopVerdict` 判定 —— 着色与告警以判定为准，而不是直接看丢包率）、`IIpGeolocationService`（IP 归属地推断契约）。 |
| `Import/` | 从其他工具导入会话的契约与结果模型：`ISessionImportService`、`ImportedSession`、`SessionImportScan`、`SessionImportOutcome`（WinSCP / Xshell 的具体解密与解析在 Infrastructure）。 |
| `ZModem/` | **自研 ZMODEM 协议引擎**（传输无关）：`Protocol/`（帧读写、ZDLE 转义、CRC-16/32、`ZModemSender`/`ZModemReceiver`）、`Model/`、`Abstractions/`（`IByteDuplex` 与文件源/汇契约，为未来 Telnet/串口预留）、`Diagnostics/ZModemTrace`（置 `VELASHELL_ZMODEM_TRACE=1` 打开帧跟踪）。 |
| `Tunnels/` | 端口转发隧道契约 `ITunnelService`。 |
| `Sync/` | 云同步契约与载荷：`IGistSyncService`、`SyncModels`、`SyncCrypto`（PBKDF2 + AES-256-GCM 端到端加密）。 |
| `Services/` | 跨层服务契约与逻辑：`IThemeService`/`ThemeService`、`ISessionMetricsService` 与 `SessionMetrics`(`.Extras`/`Records`)（CPU/内存/磁盘/网络指标的解析与换算）、`SettingsPreviewService`。 |
| `Recording/` | 会话录制存储契约 `ISessionRecordingStore`。 |
| `Localization/` | `ILocalizationService` + `LocalizationService`，运行时语言切换。 |
| `Resources/` | 强类型本地化资源。`Strings.resx`（英文，开发语言）+ `zh-Hans`/`zh-Hant`/`ja`/`ko` 卫星资源。`Strings.cs` 为**手写**的强类型访问层（`Get`/`Format` + 常用键属性），非设计器生成。 |

## 🔑 核心思路

1. **稳定的领域词汇表**：连接、分组、会话、隧道、传输、审计等概念在此一次性定义，所有上层共用同一套模型，杜绝 DTO 转换与概念漂移。
2. **契约与实现分离**：Core 声明「需要什么能力」（接口），Infrastructure 提供「如何做到」（SonnetDB / Tmds.Ssh / AES）。这让核心逻辑可在无数据库、无网络的环境下单元测试。
3. **可替换的边界**：SSH 库、存储引擎、加密方案都被接口隔离在 Core 之外，替换成本被约束在单一实现项目内。

## 🌐 本地化回退链

`NeutralLanguage=en`，`CurrentUICulture=en` 时直取主程序集无需 en 卫星。中文卫星按脚本中性文化命名（`zh-Hans`/`zh-Hant`），使 `zh-CN`/`zh-SG` → `zh-Hans`、`zh-TW`/`zh-HK`/`zh-MO` → `zh-Hant` 沿 .NET 标准回退链自动命中。

## 🔗 依赖关系

```text
VelaShell.Core  ──(被依赖)──►  Terminal / Infrastructure / Presentation / App
        │
        └─ 仅依赖：ReactiveUI
```

> Core 是依赖图的汇点，不引用任何其他 VelaShell 项目。（`VelaShell.Controls` 是纯控件层，同样不引用 Core。）测试见 [`tests/VelaShell.Core.Tests`](../../tests/VelaShell.Core.Tests)。
