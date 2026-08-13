# FTP 客户端支持：可行性调研

> 编写日期：2026-08-13　　基准代码：`main` @ `937d322`
>
> 用途：为后续实现 FTP / FTPS 会话类型提供**事实依据与改造清单**。
> 所有代码结论均有 `文件:行号` 依据；联网查证的部分标注了版本与许可证，未查实的一律标注"未核实"。
>
> 前置阅读：[`Telnet与串口可行性调研.md`](Telnet与串口可行性调研.md) —— 其第三节的"协议泛化"改造与本文完全共用，本文不重复展开。
>
> **落地情况（2026-08-13）**：本文的 P0 与 P1 已实现并合入 —— `ConnectionType.FTP` + `SessionProfile.Ftp`、
> `FtpFileService`（`ISftpService` 实现）+ `FtpConnectionPool`、按会话分派的 `RoutingRemoteFileService`、
> 连接弹窗的 FTP 页签与 FTPS 表单、证书指纹信任流程、导入器把 FTP/FTPS 转为受支持。
> **唯一仍待验证的是第五节风险 1（TLS 会话复用）**：需拿默认配置的 vsftpd 与 FileZilla Server 各跑一次真实连通性验证，
> 它决定是否必须引入 LGPL 的 `FluentFTP.GnuTLS`。本文其余内容保持原样，作为设计取舍的记录。

---

## 一、结论先行

**FTP 与 Telnet / 串口不是同一类问题。** 后两者是**终端协议**，能白捡现成的 `IByteDuplex` / `IShellStreamWrapper` 抽象（那份调研的结论是"传输层零改动"）。FTP 是**文件协议**，它要接的是文件面板那条线，而那条线上有一个 SSH 专用的实现。

**好消息：接缝已经存在，而且比预期的更靠上。**

正确的接缝是 **`ISftpService`**，不是 `ISftpClientWrapper`：

| 接口 | 形状 | 能否承载 FTP |
|---|---|---|
| `Core/Sftp/ISftpService.cs:21-88` | 全部以 `Guid sessionId` 为键，返回 `RemoteFileInfo`，**零 SSH 类型** | ✅ 可以 |
| `Core/Ssh/ISftpClientWrapper.cs:9-140` | 要求可 Seek 流、`posix-rename@openssh.com`、UID/GID 整数、`ResumeSafetyMargin` | ❌ 是 SFTP 的形状 |

决定性证据是**上层实际消费的模型**：`Core/Models/RemoteFileInfo.cs:26-30` 里 `Permissions` / `Owner` / `Group` 都是**字符串**（`rwxr-xr-x`、用户名、组名）——这正是 FTP 的 `LIST`/`MLSD` 能给出的东西。UID/GID 整数只存在于 `SftpEntry`（`Core/Ssh/SftpEntry.cs:25-28`）这一跳内部，且要靠 `RemoteIdentityResolver.cs:28,42` **跑 SSH exec 通道的 `getent passwd`** 才能翻译成名字。**FTP 走 `ISftpService` 这一层，可以整条绕开这个 SSH 依赖。**

**坏消息 ①：FluentFTP 的数据流不可 Seek，与 `ISftpClientWrapper` 的契约硬冲突。**

已从源码核实：`FtpSocketStream.CanSeek => false`，`Seek()` 直接 `throw new InvalidOperationException()`；`FtpDataStream.Position` 的 setter 明确抛异常（"You cannot modify the position of a FtpDataStream"）。而 `ISftpClientWrapper.cs:99-106` 的契约是"**实现必须返回可 Seek 的流**"，`SftpService.cs:583-586` 还会在续传校验时主动断言并抛错。

→ 这条从反面印证了接缝必须选在 `ISftpService`：**不要试图让 FTP 去实现 `ISftpClientWrapper`。**

**坏消息 ②：FTP 没有多路复用，而现有代码把"传输可并发"写进了设计前提。**

`SerializedSftpService.cs:7-12` 的注释原文：

> **传输（上传/下载/远端复制）不占串行闸**……底层 Tmds.Ssh 的 `SftpClient` 本身即为并发使用而设计，因此放行是安全的。

FTP 一条控制连接同一时刻只能跑一条命令（FluentFTP 内部加锁，社区 issue [#1499](https://github.com/robinrodricks/FluentFTP/issues/1499) 即"传输期间 `GetListing` 报错"）。**FTP 后端必须自带连接池**：至少一条控制连接跑元数据、每个并发传输各占一条，否则要么退化成全串行、要么随机炸。

**总判断：可行，工作量中等偏大。** 核心是"造一个 `FtpFileService : ISftpService` + 一个按会话分派的路由器"，外加 Telnet 调研里已列过的那批协议泛化改造。**真正的不确定性不在代码，而在 FTPS 的 TLS 会话复用（见第五节风险 1），那一条决定"能不能连上真实生产服务器"，必须先验证再动工。**

---

## 二、可直接复用（零改动）

| 组件 | 位置 | 说明 |
|---|---|---|
| 文件浏览器双栏 | `ViewModels/FileBrowserViewModel.cs:89`、`LocalFilePaneViewModel.cs` | 只依赖 `ISftpService` + `Guid sessionId` |
| 传输管理器与限速 | `Core/Sftp/TransferManager.cs`、`ThrottledStream.cs` | 协议无关 |
| 传输浮窗、冲突策略、拖放 | `ViewModels/FileTransferViewModel.cs`、`Core/Sftp/DragDropFormats.cs` | 协议无关 |
| 串行化装饰器 | `Core/Sftp/SerializedSftpService.cs` | `ISftpService → ISftpService`，可复用；但**闸门策略需为 FTP 调整**（见 §3.2） |
| 独立 SFTP 双栏文档的生命周期 | `ViewModels/SftpDocumentViewModel.cs:42` | 只吃 `ISftpService` |
| SonnetDB 持久化管线 | `Infrastructure/Persistence/*` | 见 §3.7 的字段同步代价 |
| 会话导入框架 | `Infrastructure/Import/*` | 见下 |

> **导入侧白捡**：`WinScpImportService.cs:183` 现在把 `FSProtocol=5` 映射成 `(SSH, false, "FTP")`。
> 一旦 `ConnectionType` 有了 `FTP`，这里改一个枚举值就能翻成"支持"，
> 而刚落地的全自动导入（PR #157）会**自动勾选**这些会话——WinSCP 用户迁过来即可用。
> Xshell 侧 `XshellImportService.cs:101-108` 的 `_ =>` 兜底也要补 FTP/FTPS 分支。

---

## 三、必须改造的地方

### 3.1 协议泛化（与 Telnet / 串口完全共用）

四处枚举钳制、两个主分派点、校验谓词、树节点判据——**逐条清单见 [`Telnet与串口可行性调研.md`](Telnet与串口可行性调研.md) §3.1–3.4，此处不重复**。先做 Telnet 还是先做 FTP，这批改造都要做完一次；第二个协议的成本约为第一个的 40%。

FTP 额外要撞的两处（Telnet 调研未覆盖）：

| 位置 | 问题 |
|---|---|
| `Views/ConnectionProfileView.axaml:174-198` | 协议标签条只有 SSH/SFTP 可点 + Telnet/串口两个禁用 Border，**FTP 连禁用占位都没有**；滑动下划线 `ConnectionProfileView.axaml.cs:41` 是二元三目 |
| `tests/VelaShell.Tests/Views/ConnectionProfileViewUiTests.cs:46,54` | 硬断言"恰好 2 个 proto-tab Button + 恰好 2 个禁用 Border"，**加 FTP 页签必然打红这两条** |

### 3.2 文件服务的路由（本文的核心新增）

`ISftpService` 是**单例**，每个方法都以 `sessionId` 为参数（`ISftpService.cs:24-87`），DI 里唯一装配点在 `InfrastructureServiceCollectionExtensions.cs:95-114`。加入 FTP 后需要一个按会话协议分派的路由器：

```
ISftpService（对外唯一契约，UI 侧零改动）
  └── RoutingRemoteFileService            ← 新增：按 sessionId 查协议后转发
        ├── SftpService  (现有，SSH 会话)
        └── FtpFileService (新增，FTP 会话) ── FtpConnectionPool ── AsyncFtpClient × N
```

要点：

- `SftpService.cs:731-732` 通过 `ISshConnectionService.GetSession(sessionId)` 解析会话并要求 `Status == Connected`；FTP 侧需要对等的 `IFtpConnectionService`，**且两者的会话 id 必须来自同一个命名空间**，否则路由器无从判断。
- DI 里那句 `wrapper is not TmdsSshClientWrapper → throw "SFTP requires Tmds.Ssh backend."`（`InfrastructureServiceCollectionExtensions.cs:102-103`）是**硬向下转型**，路由器落地后应改成按 `ConnectionType` 选工厂。
- `SerializedSftpService` 的闸门策略要按协议分化：SSH 侧维持"传输不占闸"，**FTP 侧要么让传输也占闸（退化成全串行）、要么由 `FtpFileService` 内部的连接池保证每条传输独占一条连接**。推荐后者，并把并发上限与用户设置里的"最大并发传输数"对齐。

### 3.3 不要复用 `ConnectionWorkflowService` / `ConnectionInfo`

`ConnectionWorkflowService.cs:64-85` 无条件调 `_sshConnectionService.ConnectAsync(...)`，`ConnectionInfo.cs:11-48` 是 `required` + `init` 的**纯 SSH 传输参数**（`Port=22`、`PrivateKeyPath`、`JumpHost`）。与 Telnet 调研同样的结论：**不要扩展它**，FTP 走自己的连接服务，但要纳入同一套会话生命周期（连接/断开/事件/状态栏/会话树）。

另需注意 `ConnectionWorkflowService.ValidateProfile:239-270` 强制 `Username` 非空——**FTP 匿名登录会被挡死**，校验谓词必须按协议分派。

### 3.4 元数据映射（三个模型的对照）

| `RemoteFileInfo`（UI 消费） | SFTP 现路径 | FTP 新路径（FluentFTP `FtpListItem`） |
|---|---|---|
| `Name` / `FullPath` | `SftpEntry.Name/FullName` | `Name` / `FullName` |
| `Size` | `Length` | `Size` |
| `IsDirectory` | `IsDirectory` | `Type == FtpObjectType.Directory` |
| `LastModified` | `LastWriteTime` | `Modified`（时区转换后）／`RawModified`（原始） |
| `Permissions`（字符串） | 9 个 bool 位拼出 | `RawPermissions`，或由 `OwnerPermissions`/`GroupPermissions`/`OthersPermissions` 拼 |
| `Owner` / `Group` | `UserId`/`GroupId` → **SSH exec `getent passwd`** 翻译（`SftpService.cs:785-786`、`RemoteIdentityResolver.cs:28,42`） | `RawOwner` / `RawGroup` **直接就是名字** |

**结论：FTP 侧不经过 `SftpEntry`，直接产出 `RemoteFileInfo`，并整条绕开 `RemoteIdentityResolver`。**
代价是这些字段在很多 FTP 服务器上是空的（Windows/IIS 风格 LIST 无属主属组），UI 需要容忍空值——见 §3.6。

### 3.5 续传语义要重新定义

现有续传依赖两件 SFTP 特有的东西：

1. `ResumeSafetyMargin`（`ISftpClientWrapper.cs:129-139`，`TmdsSftpClientWrapper.cs:23` = 64×32KB）——为的是 Tmds.Ssh 多写缓冲区乱序完成留下的空洞。**FTP 单条数据连接顺序写，此值应为 0。**
2. 尾部比对要求可 Seek 流（`SftpService.cs:520-586`）——FTP 做不到。

**替代方案（可行且已核实）**：FluentFTP 的
`DownloadStream(Stream outStream, string remotePath, long restartPosition = 0, IProgress<FtpProgress> progress = null, CancellationToken token = default, long stopPosition = 0)`
带 `restartPosition` + `stopPosition`，等于**区间读**——尾部校验用它读远端最后 N 字节即可，不需要 Seek。上传侧用 `FtpRemoteExists.Resume` 或带偏移的写入。

### 3.6 能力位：不能再假设"什么都能做"

| 能力 | SFTP | FTP |
|---|---|---|
| chmod | 原生 `setstat` | 依赖 `SITE CHMOD`（FluentFTP 支持），**很多服务器不实现** |
| 属主/属组 | UID/GID + 查表 | 视 LIST 方言，可能为空 |
| 保留时间戳 | `setstat`（`ISftpClientWrapper.cs:94`） | `MFMT`/`MDTM`，非强制 |
| 符号链接 | 原生 | `MLSD` 有 type=OS.unix=symlink，方言不一 |
| 原子重命名 | `posix-rename` 扩展（`:77`） | `RNFR`/`RNTO`，跨目录行为因服务器而异 |

建议给 `ISftpService`（或其上层）加一个能力查询，让 `FileBrowserViewModel` 按能力隐藏 chmod 菜单与属主/属组列，而不是无条件显示后抛错。

### 3.7 `SessionProfile` 加 `FtpSettings?`

沿用 Telnet 调研 §3.5 的手法——可空嵌套对象，缺失即 null，旧数据零影响：

```csharp
public FtpSettings? Ftp { get; set; }
// EncryptionMode(None|Explicit|Implicit|Auto) / DataConnectionType(PASV|EPSV|PORT|EPRT)
// / Anonymous / Encoding(UTF8|Auto|指定) / MaxConnections / ValidateCertificate 策略
```

**代价照旧**：`SessionProfile` 全仓是逐字段手写拷贝，新增字段必须五处同步——
`SonnetDbSessionRepository.cs:131-151`、`ConnectionWorkflowService.cs:113-131`、
`SessionTreeViewModel.cs:341`、`ConnectionProfileViewModel.cs:520-542`（`BuildProfile`）、`MainWindowViewModel.cs:2553-2555`。

> Telnet 调研里这五处的行号已随后续提交漂移，上面是 `937d322` 上重新核过的位置。

### 3.8 本地化

五个 resx 现无任何 FTP 键（连"暂未支持"的占位都没有）。新增协议名、FTPS 加密模式、被动/主动模式、匿名登录、证书信任提示等，**五语言必须同步**（有 `LocalizedKeyUsageTests` 与键集一致性测试守着）。

---

## 四、技术选型

### 4.1 推荐：FluentFTP 54.2.0（MIT）

| 项 | 值 |
|---|---|
| 版本 / 发布 | **54.2.0 / 2026-05-26** |
| 许可 | **MIT** —— 与本仓库 AGPL-3.0 + 商业双许可相容 |
| 依赖 | **无** |
| 下载量 | 56.9M（累计） |
| TFM | net462、net472、netstandard2.0/2.1、net7.0、net8.0、**net9.0** |

**TFM 注意**：本仓库是 `net11.0`（`Directory.Build.props`），FluentFTP 尚无 net10/net11 资产，会解析到 **net9.0** 那份。这在功能上没问题，但值得写进 CPM 注释。发布形态上无风险：`VelaShell.csproj` 是 `SelfContained=true` + `PublishTrimmed=false` + **刻意不用 PublishSingleFile**，纯托管 DLL 不涉及裁剪或原生资源打包。

**API 对照（`ISftpService` → `AsyncFtpClient`）**：

| `ISftpService` 成员 | FluentFTP |
|---|---|
| `ListDirectoryAsync` | `GetListing(path, FtpListOption, token)` → `FtpListItem[]`（另有 `GetListingAsyncEnumerable`） |
| `UploadFileAsync`（含 `resumeOffset`） | `UploadStream` / `UploadFile` + `FtpRemoteExists.Resume`，`IProgress<FtpProgress>` |
| `DownloadFileAsync`（含 `resumeOffset`） | `DownloadStream(out, path, restartPosition, progress, token, stopPosition)` |
| `DeleteAsync`（递归） | `DeleteFile` / `DeleteDirectory`（递归需自行按列举展开以回报进度） |
| `CreateDirectoryAsync` / `EnsureDirectoryAsync` | `CreateDirectory(path, force)` |
| `RenameAsync` | `Rename` / `MoveFile` / `MoveDirectory` |
| `SetPermissionsAsync` | `Chmod`（`SITE CHMOD`，需能力检测） |
| `GetFileInfoAsync` | `GetObjectInfo` |
| `OpenReadAsync` | `OpenRead`（**流不可 Seek**，`ISftpService` 该成员的注释本就写明"顺序读取"，兼容） |
| `ExistsAsync` | `FileExists` / `DirectoryExists` |
| `GetWorkingDirectoryAsync` | `GetWorkingDirectory` |
| 校验（可选增强） | 内建 MD5 / CRC32 / SHA-1/256/512 |

另外它自带 30+ 种服务器类型的 LIST 方言解析、自动能力探测、限速与断线重连——**这些正是自研最耗时且最容易出错的部分**。

### 4.2 不推荐的选项

| 方案 | 结论 |
|---|---|
| `FtpWebRequest`（BCL 内建） | **已废弃**（SYSLIB0014，自 .NET 6 起标注 obsolete），无 MLSD、FTPS 控制粒度不足。不可用 |
| 自研 FTP 客户端 | 协议面比 Telnet 大一个数量级：控制/数据双连接、PASV/EPSV/PORT/EPRT、RFC 4217 FTPS 的 AUTH/PBSZ/PROT、RFC 3659 的 MLSD/REST/SIZE/MDTM，再加各家 LIST 方言。Telnet 那次"自研更划算"的判断**不适用于此** |
| 其他 .NET FTP 库 | *未核实*——未逐个查证维护状态与许可证。FluentFTP 在活跃度、许可、下载量上已明显占优 |

### 4.3 ⚠️ `FluentFTP.GnuTLS` 是 LGPL，不要随手引入

| 项 | 值 |
|---|---|
| 版本 / 发布 | 1.0.40 / 2026-05-05 |
| 许可 | **LGPL-2.1-only** |
| 用途 | "Adds support for TLS1.3 streams into FluentFTP using a .NET wrapper of GnuTLS" |
| 依赖 | FluentFTP ≥ 48.0.3 |

它是第五节风险 1（TLS 会话复用）的主流绕过方案，但本仓库有 `LICENSE-COMMERCIAL.md`，**LGPL 依赖在商业授权分发路径下需要法务确认**（与 Telnet 调研对 `RJCP.SerialPortStream` 的 MS-PL 提醒同理）。且它带原生 GnuTLS，与 `SelfContained` 三平台发布的打包需另行验证（*未核实：该包的原生二进制覆盖哪些 RID*）。

**建议：先用纯 .NET SslStream 路径验证目标服务器；只有在确认踩到会话复用问题、且无服务端配置余地时，才把 GnuTLS 作为可选插件引入。**

---

## 五、主要风险点

### 1. FTPS 的 TLS 会话复用（**最高风险，先验证再动工**）

大量生产 FTPS 服务器要求**数据连接复用控制连接的 TLS 会话**：vsftpd 的 `require_ssl_reuse` **默认开启**，FileZilla Server 在协商到 TLS 1.3 时也强制复用。不满足时服务器直接回 `522 SSL connection failed; session reuse required`。

这是 FluentFTP 上长期存在的一类问题（[#236](https://github.com/robinrodricks/FluentFTP/issues/236)、[#347](https://github.com/robinrodricks/FluentFTP/issues/347)、[#773](https://github.com/robinrodricks/FluentFTP/issues/773)、[#951](https://github.com/robinrodricks/FluentFTP/issues/951)、[#1283](https://github.com/robinrodricks/FluentFTP/issues/1283)、[#1738](https://github.com/robinrodricks/FluentFTP/issues/1738)），根因在 .NET `SslStream` 侧，社区方案是切到 `FluentFTP.GnuTLS`（`Config.CustomStream = typeof(GnuTlsStream)`）——即 §4.3 那个 LGPL 包。

> **这不是"实现细节"，而是选型前提**：如果目标用户的 FTPS 服务器普遍要求会话复用，而 LGPL 又不可接受，那么 FTPS 这一块就要另找方案（或只支持明文 FTP + SFTP，价值大打折扣）。
> **建议动工前先用真实/Docker 的 vsftpd（默认配置）与 FileZilla Server（TLS 1.3）各跑一次连通性验证。**

### 2. 单连接串行 vs 并发传输

见 §1、§3.2。表现形式是"传输期间刷新目录报错"（[#1499](https://github.com/robinrodricks/FluentFTP/issues/1499)）。不做连接池就只能全串行，用户设置里的"最大并发传输数"会被悄悄压回 1——与 `SerializedSftpService.cs:7-12` 明确要避免的那个退化完全一样。

### 3. 安全语义缺失（与 Telnet 同源）

明文 FTP 无加密、无主机身份验证；现有 `IHostKeyService` / known-hosts / `SecurityAlertService` 整条链路**只适用于 SSH host key，对 X.509 完全不适用**。需要：
- FTPS 的证书校验与信任 UI（FluentFTP 的 `ValidateCertificate` 事件）——这是一套**新的**信任链，不要硬套 host key 那套；
- 明文 FTP 在会话树/标签/状态栏上的显著"不加密"标识，避免用户误以为与 SSH 同等安全；
- 凭据仍走现有 `ISecretProtector`，无需新增。

### 4. LIST 方言与时区

`MLSD`（RFC 3659）才给可靠的 UTC 时间戳与结构化 facts；老服务器只有 `LIST`，返回的是本地时间且格式因服务器而异（一年以上的文件甚至只有年份，没有时分）。FluentFTP 提供了 `Modified`（转换后）与 `RawModified`（原始）两个字段——**"保留时间戳"和"按时间排序/比对"两处必须明确用哪一个**，否则会出现跨时区的假差异。

### 5. 被动模式与网络环境

PASV/EPSV 在 NAT 与防火墙后的失败模式很多（服务器回私网地址、端口范围未放行）。需要在 `FtpSettings` 暴露开关，并把这类失败翻译成用户能看懂的提示，而不是裸抛 socket 异常——参照 `TmdsSshInterop.Translate` 把库异常收敛为 `VelaSsh*Exception` 族的既有做法（[`架构设计.md`](架构设计.md) §3「分层与依赖方向」，:78 起：库异常只在 Infrastructure 一处翻译）。

### 6. 好消息：可自动化验证

与串口需要真实硬件不同，FTP 可以用 Docker 起 vsftpd / pure-ftpd 做集成测试，仓库已有 `docker-compose.test.yml` 的先例（现用于 SSH 集成测试）。**建议直接建一个矩阵：明文 / 显式 FTPS / 隐式 FTPS × 会话复用开关 × MLSD 有无。**

---

## 六、实施建议

### 工作量

| 维度 | 估计 |
|---|---|
| 新增文件 | 8–10（连接服务、连接池、`FtpFileService`、路由器、`FtpSettings`、异常翻译、证书信任对话框、端口/模式表单） |
| 修改文件 | ~18（§3.1 协议泛化全表 + §3.2 DI 装配 + §3.7 五处拷贝 + 5 个 resx + 导入器 2 处） |
| 新增测试 | 5–7（Docker FTP 集成矩阵、LIST 方言解析映射、续传区间读、能力位降级、导入器 FTP 转支持） |

> §3.1 的协议泛化与 Telnet/串口**共用**。若 Telnet 先落地，本项成本可减约 40%。

### 分期建议

| 阶段 | 范围 | 价值 |
|---|---|---|
| **P0** | 明文 FTP + 匿名/口令登录 + 浏览 / 上传 / 下载 / 删除 / 建目录 / 改名，连接池，路由器，协议泛化 | 打通全链路，可测 |
| **P1** | FTPS（显式优先）+ 证书信任 UI + 断点续传（区间读校验）+ 导入器把 FTP 翻成支持 | 达到可用于生产的水平 |
| **P2** | `SITE CHMOD`、保留时间戳、能力位驱动的 UI 降级、MLSD 时区精确化、校验和比对 | 与 SFTP 体验对齐 |

**P0 之前插一个 P-1：拿 Docker vsftpd（默认配置，即 `require_ssl_reuse=YES`）与 FileZilla Server 各做一次 FTPS 连通性验证。** 这一步的结论直接决定 P1 是否需要引入 LGPL 依赖，进而影响整个特性的商业授权可行性。

### 需要新写的文件

**Core**
- `Core/Models/FtpSettings.cs`（加密模式 / 数据连接模式 / 匿名 / 编码 / 并发数）
- `Core/Ftp/IFtpConnectionService.cs`、`FtpSession.cs`（与 `SshSession` 对等，纳入统一会话生命周期）
- `Core/Sftp/IRemoteFileCapabilities.cs`（能力位查询，供 UI 降级）

**Infrastructure**
- `Infrastructure/Ftp/FluentFtpConnectionService.cs`（连接、认证、状态事件）
- `Infrastructure/Ftp/FtpConnectionPool.cs`（元数据连接 + 传输连接，上限对齐"最大并发传输数"）
- `Infrastructure/Ftp/FtpFileService.cs`（`ISftpService` 实现，`FtpListItem → RemoteFileInfo` 映射）
- `Infrastructure/Ftp/FluentFtpInterop.cs`（库异常 → Core 异常族，对标 `TmdsSshInterop`）
- `Infrastructure/Sftp/RoutingRemoteFileService.cs`（按 `ConnectionType` 分派）

**App / UI**
- 协议页签与 FTP 表单（`ConnectionProfileView.axaml`）、证书信任对话框
- 五个 resx 新键

---

## 七、一句话总结

**技术上完全可行，且接缝（`ISftpService`）比想象中干净——文件浏览器、传输管理器、限速、拖放全部零改动。真正要做的是"造一个带连接池的 FTP 后端 + 把 SSH 从会话/枚举/分派里解耦"。唯一可能推翻方案的是 FTPS 的 TLS 会话复用问题：它把"要不要引入一个 LGPL 依赖"这个许可证问题摆到了技术决策前面，必须先验证。**
