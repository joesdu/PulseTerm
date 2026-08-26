# VelaShell.Core.Tests

> [`VelaShell.Core`](../../src/VelaShell.Core) 的单元测试。

验证领域层的纯逻辑，无需 UI、数据库或网络，全部以 Mock/内存实现驱动。

## 覆盖范围

| 目录 | 被测对象 |
|------|----------|
| `Models/` | 模型序列化、`TerminalColorScheme` 解析、`UserPathResolver`（相对路径以用户主目录为基准，不落到进程工作目录）。 |
| `Ssh/` | `SshConnectionService` 生命周期、凭据装配（`SshCredentialSetupTests`——默认 `Credentials` 非空且含 agent，必须整体替换）、私钥格式（`SshKeyServiceFormatTests`、`OpenSshPrivateKeyConverterTests`——Tmds.Ssh 只认 OpenSSH 格式），以及 Tmds.Ssh 包装与异常翻译（`TmdsSshClientWrapperTests`、`TmdsSftpEntryMappingTests`、`TmdsSshInteropTests`——经 Infrastructure 的 `InternalsVisibleTo` 白盒访问）。 |
| `Diagnostics/` | 路由追踪的逐跳判定（`TraceAnalysisTests`）。 |
| `Processes/` | 远端进程探针的解析与瞬时 CPU 计算（`RemoteProcessProbeTests`）。 |
| `Sftp/` | `SftpService`（含并发特征化与独立 SFTP 契约）、`SerializedSftpService`、`TransferManager` 传输逻辑与限速。 |
| `FileTransfer/` | 三种协议共用的测试替身：内存双工通道、内存文件源/汇。 |
| `ZModem/` | ZMODEM 协议引擎：CRC、ZDLE 转义、子包、帧编解码、收发端状态机、`ZModemHardeningTests`（文件名编码、ZEOF 长度校验、ZSINIT 应答、尾字节交还等回归），以及 **`LrzszInteropTests`**——其期望值按 lrzsz 的 `zm.c`/`zmodem.h` 定义**手工构造**，不经我们自己的编码器生成，避免「编码器与解码器一起错还全绿」的自证测试。 |
| `XYModem/` | XMODEM / YMODEM 引擎：块编解码（`XYModemBlockTests`）、按 `ymodem.txt` **手工拼线上字节**的互操作回归（`XYModemInteropTests`）、以及收发对接的回环保真测试（`XYModemLoopbackTests`）。 |
| `Tunnels/` | `TunnelService` 端口转发。 |
| `Services/` | `SessionMetrics` 指标计算、`SettingsPreviewService`。 |
| `Sync/` | `SyncCrypto` 云同步加密（PBKDF2 + AES-256-GCM）。 |
| `Resources/` | 本地化回退链（`zh-Hans`/`zh-Hant`/`ja`/`ko`）。 |

## 运行

```bash
dotnet test tests/VelaShell.Core.Tests/
```
