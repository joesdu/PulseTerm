# Hello World 示例插件

> VelaShell 官方最小示例：SDK 每个能力各演示一处最小用法。**不随包分发**（`VelaPluginShip=false`）—— 它是给开发者读的范例，不是给用户装的功能。

## 演示了什么

命令面板（`Ctrl+P` / `Ctrl+K`）里提供三条命令：

| 命令 | 演示的能力 |
|------|-----------|
| `Hello World: List Sessions` | `Sessions` 枚举会话 + `Logging` 写日志。 |
| `Hello World: Remote Uptime` | `RemoteExec` 在第一条已连接会话上执行 `uptime`。 |
| `Hello World: Open Panel (Tab / Window)` | `Ui` 打开 AXAML 面板 —— 完整 Avalonia，进程内模式可停靠拖拽；隔离进程下 `Tab` 自动回退为独立窗口。 |

此外还演示了插件生命周期（`ActivateAsync`/`DisposeAsync`）、`Storage` 存取与 `Events` 宿主事件订阅。

## 运行方式

`plugin.json` 声明 `"hostMode": "isolated"`，因此它跑在独立的 [`VelaShell.PluginHost`](../../src/VelaShell.PluginHost) 进程里 —— 顺带成了隔离链路的活体验证。改成 `inProcess` 即可对比两种模式（插件源码不用动，这正是 SDK 传输无关设计的意义）。

构建后自动镜像到 `src/VelaShell/bin/<Configuration>/net11.0/plugins/velashell-hello-world/`（目录名 = 插件 id 把点换成短横，原因见 [plugins/Directory.Build.targets](../Directory.Build.targets)），F5 启动应用即加载。

> 新建插件的推荐做法就是复制本目录，详见 [plugins/README.md](../README.md) 与 [docs/plugins/dev-guide.md](../../docs/plugins/dev-guide.md)。
