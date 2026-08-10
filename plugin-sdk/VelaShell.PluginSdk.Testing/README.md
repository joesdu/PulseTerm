# VelaShell.PluginSdk.Testing

插件单元测试替身包:`TestPluginContext` 与全部能力接口的内存实现
(存储、会话、远程文件、远程执行、命令、事件、日志)。

插件项目引用本包后,无需启动宿主即可对业务逻辑做纯内存单测:

```csharp
using var ctx = new TestPluginContext();
SessionInfo session = ctx.FakeSessions.AddConnected(host: "prod-1");
ctx.FakeRemoteExec.Handler = (_, cmd) => cmd == "docker ps" ? "CONTAINER ID ..." : "";

var plugin = new MyPlugin();
await plugin.ActivateAsync(ctx, CancellationToken.None);
await ctx.RecordingCommands.RunAsync("my.plugin.refresh");
```

用法详见 [docs/plugins/dev-guide.md](../../docs/plugins/dev-guide.md)。
