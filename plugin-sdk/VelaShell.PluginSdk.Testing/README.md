# VelaShell.PluginSdk.Testing

> 插件单元测试替身包：`TestPluginContext` + 全部能力接口的内存实现。

插件项目引用本包后，**无需启动宿主、无需数据库、不发一次网络请求**即可对业务逻辑做纯内存单测。

## 🗂️ 替身清单

| 类型 | 替代的能力 |
|------|-----------|
| `TestPluginContext` | `IPluginContext` 本体，聚合下列全部替身并暴露为强类型字段。 |
| `InMemoryStorage` | `IPluginStorage`（KV 存储）。 |
| `FakeSecretsAndClipboard` | `ISecretsApi`（机密）与 `IClipboardApi`（剪贴板）。 |
| `InMemoryTimeSeries` | `ITimeSeriesApi`（时序写入与查询）。 |
| `FakeSessions` | `ISessionsApi`：`AddConnected(...)` 造出一条已连接会话。 |
| `FakeRemoteExec` | `IRemoteExecApi`：用 `Handler` 委托按命令文本给定输出。 |
| `FakeRemoteFs` | `IRemoteFsApi`（远程文件读写与列目录）。 |
| `FakeTerminal` | `ITerminalApi`（读终端尾部输出、回写终端）。 |
| `FakeUi` | `IUiApi`（面板与对话框）。 |
| `RecordingCommands` | `ICommandsApi`：记录注册的命令，可用 `RunAsync(id)` 直接触发。 |
| `TestHostEvents` | `IHostEvents`：手动投递宿主事件。 |
| `CollectingLogger` | `IPluginLogger`：收集日志行供断言。 |

## 用法

```csharp
using var ctx = new TestPluginContext();
SessionInfo session = ctx.FakeSessions.AddConnected(host: "prod-1");
ctx.FakeRemoteExec.Handler = (_, cmd) => cmd == "docker ps" ? "CONTAINER ID ..." : "";

var plugin = new MyPlugin();
await plugin.ActivateAsync(ctx, CancellationToken.None);
await ctx.RecordingCommands.RunAsync("my.plugin.refresh");
```

真实用例见 [`tests/VelaShell.Plugin.Ai.Tests`](../../tests/VelaShell.Plugin.Ai.Tests) —— AI 插件的工具审批闸门、设置/机密存取与会话历史全部靠这套替身覆盖。

> 用法详解见 [docs/plugins/dev-guide.md](../../docs/plugins/dev-guide.md)，测试策略见 [13-testing-strategy.md](../../docs/plugins/13-testing-strategy.md)。
