# 09 · SDK 与开发者工具链

> **实现注记(2026-08)**:S-1 / S-2 / S-3 / S-4 / S-5 已落地,与下文蓝图的出入以实现为准:
>
> - 交付物是**五个包**:`VelaShell.PluginSdk`(契约)、`VelaShell.PluginSdk.Testing`(替身)、
>   `VelaShell.PluginSdk.Build`(**插件工程只需引这一个**:MSBuild props/targets、依赖锁、
>   随包分发的打包器)、`VelaShell.Plugin.Cli`(dotnet tool `vela-plugin`)、
>   `VelaShell.Plugin.Templates`(`dotnet new`)。没有 `VelaShell.PluginProtocol` 这个包 ——
>   RPC 线协议就在契约程序集里(自研轻量协议,不用 StreamJsonRpc,见 05 注记)。
> - 模板是 **`velaplugin` / `velaplugin-ui`** 两个;`velaplugin-automation` 待自动化能力域动工再说。
> - 测试替身叫 **`TestPluginContext`** 而不是 `FakePluginContext`;VelaUI 声明式树按用户决策不做,
>   因此没有 `VelaUiAssert`。
> - `vela-plugin` 的子命令:`validate` / `pack` / `sign` / `verify` / `info` / `unpack` /
>   `keygen` / `install` / `hosts` / `doctor` / `dev init|run|list|prune|link|unlink`
>   (`dev-link` / `dev-unlink` 保留为旧名别名)。完整手册见 [cli.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/cli.md)。
> - **开发内环(SDK 1.4)**:宿主每次启动把自己登记进 `~/.velashell/host.json`,
>   `vela-plugin dev init` 据此生成 IDE 启动配置(`--dev-root` 挂载工程输出、
>   `--wait-debugger` 等待附加、`--data-root` 用独立数据根从而与日常实例并存);
>   开发期插件从影子副本装载,因此宿主运行时不锁工程 `bin`,管理页的"重新加载"
>   即可跑上新代码,`--dev-watch` 还能重编后自动重载。见 [dev-guide.md §2.3](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/dev-guide.md)。
> - 发布走 `.github/workflows/nuget.yml`(标签 `sdk-v<版本>`),含模板端到端冒烟。

目标(G5):从 `dotnet new` 到插件在 VelaShell 里跑起来 ≤ 5 分钟;
F5 断点调试开箱即用。开发者体验是插件生态成败的第一因素,本分项按
"产品"标准对待。

## 1. 交付物清单

| 交付物 | 形式 | 内容 |
| --- | --- | --- |
| `VelaShell.PluginSdk` | NuGet 包 | 入口约定、`IPluginContext` 与全部能力代理、VelaUI 构建器、测试替身(`FakePluginContext`) |
| `VelaShell.PluginProtocol` | NuGet 包(SDK 依赖自动引入) | RPC 契约与 DTO;插件项目一般不直接引用 |
| `VelaShell.Plugin.Templates` | `dotnet new` 模板包 | `velaplugin`(基础)、`velaplugin-ui`(带 VelaUI 面板)、`velaplugin-automation` |
| `vela-plugin` | dotnet tool | `new/pack/validate/sign/install/dev` 子命令 |
| 文档站 | docs 站点(先仓库内 markdown) | 快速开始、API 参考(DocFX 由 XML 注释生成)、场景教程 ×3(对应三个官方示例) |
| `samples/plugins/` | 仓库源码 | image-viewer / mp3-player / auto-runner,持续随 SDK 编译,是活文档也是回归测试 |

## 2. 项目模板形态

```text
dotnet new velaplugin -n MyPlugin --publisher acme
MyPlugin/
├── MyPlugin.csproj          # Sdk 风格;引用 PluginSdk;PackVpx 目标已接好
├── plugin.json              # 已填好 id/entry/最小权限,带注释指引
├── plugin.nls.json          # 默认(英文)文案 + 五语言空模板
├── src/MyPluginMain.cs      # [VelaPlugin] 入口 + 一个示例命令
├── .vscode/ + Properties/launchSettings.json   # F5 = vela-plugin dev --wait-debugger
└── README.md
```

`.csproj` 关键机制(由 SDK 包内 MSBuild targets 提供):

- `PluginProtocol/PluginSdk/StreamJsonRpc` 引用标记 `Private=false`
  (不进输出目录,运行时由 PluginHost 提供,见 03 §1);
- `dotnet build -t:PackVpx` 一步出 `.vpx`;
- 编译期校验:manifest 与代码不一致(如命令 id 未注册)给 MSBuild 警告。

## 3. vela-plugin CLI

| 命令 | 功能 |
| --- | --- |
| `vela-plugin validate` | manifest schema、NLS 完整性(五语言缺漏表)、权限清单合法性、包结构 |
| `vela-plugin pack` | 构建 + 剔除共享程序集 + zip + 生成未签名 .vpx |
| `vela-plugin sign --key <pfx>` | 开发者签名(见 10) |
| `vela-plugin install <vpx>` | 装入本机 VelaShell(走与 UI 相同的安装管线) |
| `vela-plugin dev` | 开发模式:watch 构建 + 热重载装载(见 04 §6) |
| `vela-plugin new keypair` | 生成开发者签名密钥对 |

## 4. 测试支持(插件作者视角)

- `FakePluginContext`:全部能力接口的内存实现(内存文件系统、脚本化
  会话应答、录制/断言 UI 树),插件逻辑可在普通单测里跑,不需要
  VelaShell 实例。
- `VelaUiAssert`:对 Build 产出的虚拟树做快照断言(五语言/明暗主题
  双维度)。
- 集成测试 runner(远期):无头宿主模式,加载真插件跑冒烟。

## 5. 兼容与发布纪律(宿主团队自律项)

- SDK XML 注释 100% 覆盖公开面;每个能力方法注明所需权限与错误码。
- PublicApiAnalyzer 锁公开面;apiLevel 内破坏性变更 CI 直接红。
- SDK 版本与宿主版本解耦发布;发布说明按"新增能力/新增贡献点/行为
  变化"分节,面向插件作者而非宿主开发者书写。

## 6. 开发计划(本分项)

| 任务 | 说明 | 依赖 | 估算 |
| --- | --- | --- | --- |
| S-1 | PluginSdk 包骨架:入口约定、上下文、代理生成方式定稿(手写 vs 源生成)| P-3 | 2d |
| S-2 | MSBuild targets:Private=false 机制、PackVpx、编译期校验 | S-1 | 2d |
| S-3 | vela-plugin CLI:validate/pack/install(sign 待 10;dev 待 H-7)| M-1 | 3d |
| S-4 | dotnet new 模板 ×3 + launchSettings 调试链路打通 | S-2, H-7 | 3d |
| S-5 | FakePluginContext + VelaUiAssert | C-2, U-7 | 4d |
| S-6 | 快速开始文档 + 三篇场景教程(随示例插件写)| 示例插件完成 | 4d |
| S-7 | 官方示例:image-viewer(验收 S1)| C-3, U-3, U-9 | 3d |
| S-8 | 官方示例:mp3-player(验收 S2)| C-4, C-6, U-2, U-7 | 4d |
| S-9 | 官方示例:auto-runner(验收 S5,详见 11)| 11 的 T-* | 3d |

验收:新人(未参与开发的同事)按快速开始文档独立完成"Hello 命令 +
一个 VelaUI 面板"插件,全程 ≤ 30 分钟且无需口头求助;三个官方示例
在 CI 随每次 SDK 变更编译并跑冒烟。
