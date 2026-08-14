# VelaShell.Presentation.Tests

> [`VelaShell.Presentation`](../../src/VelaShell.Presentation) 跨层 ViewModel 与工作流的单元测试。

在无窗口环境下验证表现逻辑，依赖注入以 Mock 服务驱动。

## 覆盖范围

| 文件 | 被测对象 |
|------|----------|
| `ViewModels/StatusBarViewModelTests` `StatusBarNetworkTests` | 状态栏指标与网速展示。 |
| `ViewModels/TabBarViewModelTests` | 标签页栏行为。 |
| `Services/ConnectionWorkflowServiceTests` | 连接工作流（校验 → 认证 → 建链）。 |
| `Services/TunnelWorkflowServiceTests` | 隧道工作流（类型分派、快照、停止、移除）。 |
| `ViewModels/SessionImportViewModelTests` | 会话导入的全自动行为（多来源自动扫描、跨来源去重、智能勾选、按来源写入）。 |
| `ViewModels/RecentConnectionsViewModelTests` | 最近连接的刷新与清除（含存储失败时不得清空列表、无服务时为空操作）。 |
| `CommandRegistryTests` | 全局命令注册表。 |
| `Plugins/PluginCommandsApiTests` | 插件命令桥接：`<pluginId>.` 前缀强制、异常只记日志、停用时注销本插件全部命令。 |

## 运行

```bash
dotnet test tests/VelaShell.Presentation.Tests/
```
