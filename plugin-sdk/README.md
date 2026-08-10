# plugin-sdk/ —— 插件 SDK

VelaShell 插件开发 SDK 的源代码:

| 项目 | 说明 |
| --- | --- |
| [VelaShell.PluginSdk](VelaShell.PluginSdk/) | 契约程序集:`IVelaPlugin` / `IPluginContext`、能力接口、`plugin.json` 模型。仅依赖 BCL |
| [VelaShell.PluginSdk.Testing](VelaShell.PluginSdk.Testing/) | 测试替身:`TestPluginContext` 与全部能力的内存实现 |

- 开发文档:[docs/plugins/dev-guide.md](../docs/plugins/dev-guide.md)
- 示例插件:[plugins/VelaShell.Plugin.HelloWorld](../plugins/VelaShell.Plugin.HelloWorld/)
- 兼容纪律:同 apiLevel(当前 = 1)内**只增不改不删**;破坏性变更必须提升
  `VelaPluginApi.Level` 并回写设计文档。
- 契约程序集必须保持零重量级依赖 —— 切勿引入 Avalonia / Tmds.Ssh / ReactiveUI。
