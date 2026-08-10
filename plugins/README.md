# plugins/ —— 仓库内插件

本目录存放随仓库开发的第一方插件,每个插件一个子目录(独立 csproj)。
SDK 契约见 [plugin-sdk/](../plugin-sdk/),开发文档见
[docs/plugins/dev-guide.md](../docs/plugins/dev-guide.md)。

## 现有插件

| 目录 | id | 说明 |
| --- | --- | --- |
| [VelaShell.Plugin.HelloWorld](VelaShell.Plugin.HelloWorld/) | `velashell.hello-world` | 官方示例:SDK 各能力的最小用法 |
| [VelaShell.Plugin.Ai](VelaShell.Plugin.Ai/) | `velashell.ai` | AI 助手:多提供商流式对话 + Agent 模式(读终端/执行命令带审批) |

## 规划中(尚未创建)

- **容器管理插件**:基于远程执行能力封装 docker/podman 常用操作。

## 新建插件

1. 复制 `VelaShell.Plugin.HelloWorld/` 为新目录,改 csproj 中的 `<VelaPluginId>` 与 `plugin.json`;
2. 把项目加入 `VelaShell.slnx` 的 `/plugins/` 文件夹(仅为 IDE 可见性);
3. 直接 F5:主程序按通配符对本目录所有插件建立了构建顺序引用,启动前
   插件自动重建并镜像到应用输出目录的 `plugins/<id>/`(含 `plugin.json`)。

本目录的 `Directory.Build.props/targets` 已统一处理:`EnableDynamicLoading`、
`plugin.json` 随构建输出、构建后复制到应用输出目录。SDK 引用必须保持
`Private="false" ExcludeAssets="runtime"`(契约程序集由宿主统一提供)。
