# plugins/ —— 仓库内插件

本目录存放随仓库开发的第一方插件,每个插件一个子目录(独立 csproj)。
SDK 契约见 [plugin-sdk/](../plugin-sdk/),开发文档见
[docs/plugins/dev-guide.md](../docs/plugins/dev-guide.md)。

## 现有插件

| 目录 | id | 随包分发 | 装载模式 | 说明 |
| --- | --- | --- | --- | --- |
| [VelaShell.Plugin.HelloWorld](VelaShell.Plugin.HelloWorld/) | `velashell.hello-world` | 否 | 隔离进程 | 官方示例:SDK 各能力的最小用法 |
| [VelaShell.Plugin.Ai](VelaShell.Plugin.Ai/) | `velashell.ai` | 是 | 进程内 | AI 助手:多提供商流式对话 + Agent 模式(读终端/执行命令带审批)+ 自定义 MCP 服务器 |

装载模式由 `plugin.json` 的 `hostMode` 决定(`isolated` / `inProcess`,默认进程内)。
隔离插件跑在独立的 [`VelaShell.PluginHost`](../src/VelaShell.PluginHost) 进程里,崩溃不波及宿主;
AI 插件因为要用宿主的 AvaloniaEdit 作输入框(隔离进程里没有这个程序集)必须进程内装载。

"随包分发"由 csproj 的 `<VelaPluginShip>` 控制(默认 `true`)。示例插件设 `false`:
本机构建仍镜像进 `src/VelaShell/bin/<配置>/net11.0/plugins/`,F5 能装载它验证插件系统,
但 `dotnet publish` 出来的安装包里不会有它 —— 它是给开发者读的范例,不是给用户装的功能。

## 规划中(尚未创建)

- **容器管理插件**:基于远程执行能力封装 docker/podman 常用操作。

## 新建插件

1. 复制 `VelaShell.Plugin.HelloWorld/` 为新目录,改 csproj 中的 `<VelaPluginId>` 与 `plugin.json`;
2. 把项目加入 `VelaShell.slnx` 的 `/plugins/` 文件夹(仅为 IDE 可见性);
3. 直接 F5:主程序按通配符对本目录所有插件建立了构建顺序引用,启动前
   插件自动重建并镜像到应用输出目录的 `plugins/<目录名>/`(含 `plugin.json`)。
   目录名 = 插件 id 把点换成短横(`velashell.ai` → `velashell-ai`):macOS 的 `codesign`
   会把 `.app` 内带点号的目录当成嵌套 bundle 而签名失败。目录名不参与任何逻辑,
   宿主是枚举子目录后从 `plugin.json` 读 id。

本目录的 `Directory.Build.props/targets` 已统一处理:`EnableDynamicLoading`、
`plugin.json` 随构建输出、构建后复制到应用输出目录、发布期按 `VelaPluginShip`
交付进安装包(`GetVelaPluginPayload`)。SDK 引用必须保持
`Private="false" ExcludeAssets="runtime"`(契约程序集由宿主统一提供)。
