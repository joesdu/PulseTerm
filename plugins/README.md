# plugins/ —— 主仓库自建的第一方插件

本目录存放**与主程序同仓构建、同版发布**的第一方插件，每个插件一个子目录（独立 csproj）。

| 目录 | id | 随包分发 | 装载模式 | 说明 |
| --- | --- | --- | --- | --- |
| [VelaShell.Plugin.Ai](VelaShell.Plugin.Ai/) | `velashell.ai` | 是 | 进程内 | AI 助手：多提供商流式对话 + Agent 模式（读终端/执行命令带审批）+ 自定义 MCP 服务器 |

其余第一方插件（Redis / S3 / Telnet 与 HelloWorld 示例）住在
**[VelaShellLabs/velashell-plugins](https://github.com/VelaShellLabs/velashell-plugins)**，
以 Release 资产 `velashell-plugins-<版本>.zip` 交付。本机想连它们一起跑，把插件目录铺进
`artifacts/plugins/` 即可（或用 `-p:VelaPluginsStageDir=<目录>` 指别处），构建与发布都从那里取件。

插件契约 SDK、`dotnet new` 模板与 `vela-plugin` CLI 分别在
**[velashell-plugin-sdk](https://github.com/VelaShellLabs/velashell-plugin-sdk)**、
**[velashell-plugin-templates](https://github.com/VelaShellLabs/velashell-plugin-templates)**、
**[velashell-plugin-cli](https://github.com/VelaShellLabs/velashell-plugin-cli)**，各按自己的节奏
以 NuGet 包交付。全部文档（含插件系统设计蓝图）集中在
**[velashell-docs](https://github.com/VelaShellLabs/velashell-docs)**。

## 为什么 AI 插件留在主仓库

它是与宿主耦合最紧的一个，而那些耦合点都是**编译期**的：

- 输入框用宿主的 **AvaloniaEdit**（Markdown 高亮 + `@` 引用芯片，纯 `TextBox` 给不了内联富文本）。
  隔离进程里没有这个程序集，因此它**只能进程内装载**；
- 进程内装载意味着 `Avalonia*` 一律回落到宿主那一份（见 `PluginAssemblyLoadContext` 的
  `SharedPrefixes`），插件编译时引用的 Avalonia 必须与宿主**逐字同版**，否则是跨 ALC
  类型对不上——而且要等用户点开面板那一刻才炸；
- 面板要跟着宿主的主题、语言、字体设置走，UI 改动几乎每次都同时落在两侧。

分在两个仓库时，上面每一条都意味着"改一行 UI → 先发一次工具链 Release → 回主仓库抬 pin
→ 才能看到效果"。留在同仓，`dotnet build` 一次就够。

代价是主仓库多背了 AI 栈那几个 NuGet 依赖；但它们只进 `plugins/velashell-ai/` 这个子目录，
由插件自己的 `deps.json` 在独立 ALC 里解析，不污染宿主。

## 契约与依赖：一律走 NuGet

SDK 契约程序集取 nuget.org 上的正式包，**不做工程引用**：

```xml
<PackageReference Include="VelaShell.PluginSdk" Version="1.4.0"
                  PrivateAssets="all" ExcludeAssets="runtime" />
```

`PrivateAssets="all" ExcludeAssets="runtime"` 是硬要求——契约程序集必须与宿主是同一份，
复制进插件输出目录就成了两份类型。同理，`Avalonia` / `Avalonia.AvaloniaEdit` 也只要编译期资产
（`ExcludeAssets="runtime"`），版本须与 `src/Directory.Packages.props` 里宿主引的那两条一致。

本目录不参与中央包版本管理（仓库根目录没有 `Directory.Packages.props`），依赖版本直接写在
各插件的 csproj 里。

## 构建与分发

- **F5 开发内环**：`src/VelaShell/VelaShell.csproj` 对 `plugins/*/*.csproj` 建了一条纯构建顺序
  引用，构建宿主时插件自动重建；[`Directory.Build.targets`](Directory.Build.targets) 的
  `CopyVelaPluginToAppOutput` 把产物镜像进 `src/VelaShell/bin/<配置>/net11.0/plugins/<目录名>/`，
  启动即装载。想顺手也铺一份到某个已安装的应用目录，设环境变量 `VELASHELL_DEV_APP_DIR`。
- **发布**：`AddVelaPluginsToPublish` 调各插件的 `GetVelaPluginPayload`，把它们登记进安装包的
  `plugins/<目录名>/`（排除 pdb/xml）。是否进包由 csproj 的 `<VelaPluginShip>` 控制，默认 `true`。
- **目录名**：插件 id 把点换成短横（`velashell.ai` → `velashell-ai`）。macOS 的 `codesign` 会把
  `.app` 内带点号的目录当成嵌套 bundle 而签名失败（1.2.0 踩过）。id 本身不改——它是插件数据与
  机密的命名空间，改了等于让已有用户的配置（AI 插件的 API key 就在里面）全部失联。
  目录名不参与任何逻辑：宿主枚举子目录后从 `plugin.json` 读 id。

## 测试

插件的测试工程在 [`tests/VelaShell.Plugin.Ai.Tests/`](../tests/VelaShell.Plugin.Ai.Tests/)，
随全仓 `dotnet test` 一起跑。面板用例在 headless 会话里真装载一次 XAML——`Popup`、资源引用
这类"编译得过、运行才炸"的问题只有这样才拦得住。

## 新建一个同仓插件

1. 复制 `VelaShell.Plugin.Ai/` 为新目录，改 csproj 里的 `<VelaPluginId>` 与 `plugin.json`；
2. 把项目加进 [`VelaShell.slnx`](../VelaShell.slnx) 的 `/plugins/` 文件夹（仅为 IDE 可见性；
   构建顺序由上面那条通配 `ProjectReference` 负责，不必单独登记）；
3. `dotnet build src/VelaShell` —— 插件会跟着重建并铺进宿主输出目录。

先想清楚它该不该在这里：**只有编译期就绑死宿主的插件才值得同仓**。功能独立、只用 SDK 契约的，
放工具链仓库更合适——那边有模板、CLI 与 `.vpx` 打包，且不会把它的依赖背进主仓库的还原图。
