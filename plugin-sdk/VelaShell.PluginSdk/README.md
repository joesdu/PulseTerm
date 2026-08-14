# VelaShell.PluginSdk

> 隔离边界上的「细腰」—— 宿主与插件世界**唯一共享**的契约程序集。仅依赖 BCL。

插件入口契约（`IVelaPlugin` / `IPluginContext`）、全部能力接口、`plugin.json` 清单模型与 RPC 线协议都在这里。宿主引用它来提供能力，插件引用它来消费能力，双方不共享任何其他类型。

## 🗂️ 结构

| 路径 | 内容 |
|------|------|
| `IVelaPlugin.cs` `IPluginContext.cs` `VelaPluginAttribute.cs` | 插件入口契约与激活/停用生命周期。 |
| `VelaPluginApi.cs` | `apiLevel` 版本常量（当前 = **1**）。 |
| `Sessions/` `RemoteFs/` `RemoteExec/` `Terminal/` | 远程侧能力：会话枚举、远程文件读写、远程命令执行、终端读写。 |
| `Commands/` `Ui/` `Clipboard/` | 宿主侧能力：命令注册、面板/对话框、剪贴板。 |
| `Storage/` `Secrets/` `TimeSeries/` | 持久化能力：KV 存储、加密机密、时序数据（含 `TimeSeriesModel` 与写入校验）。 |
| `Logging/` `Events/` | 插件日志与宿主事件订阅。 |
| `Manifest/` | `plugin.json` 的模型、读取器与错误类型（`PluginManifest`/`PluginManifestReader`/`PluginManifestException`）。 |
| `Hosting/` | 装载侧工具：`PluginAssemblyLoadContext`（可收集 ALC，`Avalonia*` 前缀回落到装载方以保证类型同一）、`PluginEntryLocator`、`JsonFilePluginStorage`。 |
| `Rpc/` | 隔离模式的线协议：`PluginRpc`（方法名常量与载荷记录）、`RpcConnection`、`RpcMessage`。 |
| `PluginPermissionDeniedException.cs` `PluginSessionNotFoundException.cs` | 跨边界的异常类型。 |

## 🔑 纪律

- **零重量级依赖**：切勿引入 Avalonia / Tmds.Ssh / ReactiveUI。一旦这个程序集变胖，隔离进程和插件包都会跟着变胖，「细腰」就不成立了。
- **传输无关**：能力接口不假设调用方式。同一份插件源码既能被**进程内**装载（宿主直调），也能跑在 [`VelaShell.PluginHost`](../../src/VelaShell.PluginHost) 里（RPC 代理）—— 由 `plugin.json` 的 `isolated` 决定，插件代码一行不改。
- **同 apiLevel 内只增不改不删**：破坏性变更必须提升 `VelaPluginApi.Level` 并回写设计文档。

## 🔗 依赖关系

- **引用**：无（仅 BCL）。
- **被引用**：`VelaShell.Infrastructure`（能力实现与插件运行时）、`VelaShell.Presentation`（命令桥接）、`VelaShell.PluginHost`（隔离进程）、以及全部插件项目。

> 开发文档见 [docs/plugins/dev-guide.md](../../docs/plugins/dev-guide.md)；能力清单与权限模型见 [07-capability-apis.md](../../docs/plugins/07-capability-apis.md) 与 [06-permission-system.md](../../docs/plugins/06-permission-system.md)。测试替身见 [`VelaShell.PluginSdk.Testing`](../VelaShell.PluginSdk.Testing)。
