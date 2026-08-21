# VelaShell.Infrastructure.Tests

> [`VelaShell.Infrastructure`](../../src/VelaShell.Infrastructure) 具体实现的单元测试。

验证真实基础设施实现的行为：持久化、加密密钥、本地终端流与路径解析。

## 覆盖范围

| 文件 | 被测对象 |
|------|----------|
| `SonnetDbPersistenceTests` | SonnetDB 各存储服务的读写、时序/文档双模型持久化。 |
| `Plugins/` | **插件运行时全链路**：发现/装载/启停（`PluginManagerTests`、`PluginManagerEnableDisableTests`、`LazyActivationTests`）、安装卸载（`PluginInstallUninstallTests`）、权限闸门（`PluginPermissionGateTests`）、机密与存储（`ProtectedSecretsCapabilityTests`、`JsonFilePluginStorageTests`、`SonnetDbPluginDataStoreTests`）、**隔离进程**链路（`IsolatedPluginTests`、`RpcConnectionTests`、`StreamingRoutingTests`、`TimeSeriesRoutingTests`、`EmbedRoutingTests`），以及终端协议整链（`PluginTerminalProtocolEndToEndTests`）。需要真实插件程序集的用例统一用 [`tests/fixtures/`](../fixtures/README.md) 下的两个夹具驱动。<br/>注：`.vpx` 容器格式与 `plugin.json` 解析的用例（`VpxContainerTests` / `PluginManifestReaderTests`）已随 SDK 搬到 [插件工具链仓库](https://github.com/joesdu/velashell-plugin-toolchain) —— 那两块的实现在 SDK 里，地面真值也该跟着它走。 |
| `PluginTimeSeriesTests` | 插件时序数据的命名空间隔离与写入校验。 |
| `SshKeyServiceTests` | `~/.ssh` 密钥枚举、RSA 密钥对生成、公钥导入。 |
| `SshConnectionServiceTests` | 连接服务在真实包装类上的行为。 |
| `ConPtyShellStreamTests` | Windows ConPTY 本地终端流。 |
| `SessionMetricsServiceTests` | 会话 CPU/内存/网速采集。 |
| `VelaShellStoragePathsTests` | 数据目录与密钥文件路径解析。 |
| `FtpSupportTests` | FTP/FTPS：导入器协议映射、远程文件服务的会话路由、FluentFTP 异常翻译、连接参数回落。 |
| `Ftp/FtpFileServiceIntegrationTests` `Ftp/LoopbackFtpServer` | 对着进程内环回 FTP 服务器跑真实协议：登录（含匿名）、PASV/EPSV 数据连接、Unix LIST 解析、上传/下载往返、目录增删改、连接池并发。 |
| `WinScpImportTests` `XshellImportTests` | 会话导入:密码解码、INI/注册表解析、协议映射。 |

## 运行

```bash
dotnet test tests/VelaShell.Infrastructure.Tests/
```
