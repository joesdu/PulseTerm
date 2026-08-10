# VelaShell.PluginSdk

VelaShell 插件开发 SDK:插件入口契约(`IVelaPlugin` / `IPluginContext`)、能力接口
(会话、远程文件、远程执行、命令、存储、日志、事件)与 `plugin.json` 清单模型。

- 仅依赖 BCL,零 Avalonia / SSH 库依赖 —— 这是宿主与插件世界唯一共享的程序集。
- 全部能力接口为传输无关设计;当前宿主为进程内直调实现,未来迁移到进程外
  PluginHost 时插件源码不变。
- 开发文档见仓库 [docs/plugins/dev-guide.md](../../docs/plugins/dev-guide.md)。
