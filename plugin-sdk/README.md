# plugin-sdk/ —— 插件 SDK

VelaShell 插件开发 SDK 的源代码。对外以 NuGet 包分发,版本走 `VelaSdkVersion`,
**与宿主版本解耦**(宿主发 1.2.3 不代表插件契约变了)。

| 工程 / 包 | 说明 |
| --- | --- |
| [VelaShell.PluginSdk](VelaShell.PluginSdk/) | 契约程序集:`IVelaPlugin` / `IPluginContext`、能力接口、`plugin.json` 模型、`.vpx` 容器读写、装载工具与 RPC 线协议。仅依赖 BCL |
| [VelaShell.PluginSdk.Testing](VelaShell.PluginSdk.Testing/) | 测试替身:`TestPluginContext` 与全部能力的内存实现 |
| [VelaShell.PluginSdk.Build](VelaShell.PluginSdk.Build/) | **插件工程引用的那一个包**:MSBuild props/targets(依赖锁、共享程序集不落地、清单校验、`PackVpx`)+ 随包分发的打包器 |
| [../tools/VelaShell.Plugin.Cli](../tools/VelaShell.Plugin.Cli/) | dotnet tool `vela-plugin`:校验 / 打包 / 签名 / 装机 / 开发挂载 |
| [../templates](../templates/) | `dotnet new` 模板:`velaplugin`、`velaplugin-ui` |

同一份插件源码有两种运行方式,由 `plugin.json` 的 `hostMode` 决定:进程内(宿主直调)
或隔离进程([`VelaShell.PluginHost`](../src/VelaShell.PluginHost) + RPC 代理)。能力接口
传输无关,插件代码不因此改动一行。

- 开发文档:[docs/plugins/dev-guide.md](../docs/plugins/dev-guide.md)
- 示例插件:[plugins/VelaShell.Plugin.HelloWorld](../plugins/VelaShell.Plugin.HelloWorld/)
- 兼容纪律:同 apiLevel(当前 = 1)内**只增不改不删**;破坏性变更必须提升
  `VelaPluginApi.Level` 并回写设计文档。
- 契约程序集必须保持零重量级依赖 —— 切勿引入 Avalonia / Tmds.Ssh / ReactiveUI
  (`.vpx` 的签名因此用 BCL 自带的 ECDSA P-256,而不是要引第三方库的 Ed25519)。
- 三个版本号各司其职,别混:
  - `AssemblyVersion` = `<主版本>.0.0.0`,**只随主版本动**。它是插件编译期绑定的标识,
    补丁版跟着变等于每发一次都要所有已编译插件重新绑定,毫无收益。
  - `FileVersion` = 完整数字版(资源管理器属性页看到的);
    `InformationalVersion` = 完整版本含预发布后缀(`vela-plugin` 报的就是它)。
    面向人的两个都跟着 `VelaSdkVersion` 涨,不存在"升级了还显示 1.0.0"。
- **纪律:SDK 主版本 == `apiLevel`**。主版本变意味着契约破了,那一刻 `VelaPluginApi.Level`
  必须同步 +1 —— 老宿主于是在**发现期**按 apiLevel 干净拒载(可读原因 + Incompatible 状态),
  而不是等装载时抛一个看不懂的程序集绑定异常。

## 发布

打标签 `sdk-v<版本>` 推上去即可,由 [`.github/workflows/nuget.yml`](../.github/workflows/nuget.yml)
打全部五个包、跑打包相关测试与**模板端到端冒烟**,再推 nuget.org。

推送走 **NuGet Trusted Publishing(OIDC)**,不存 API Key。一次性配置:nuget.org 的
Trusted Publishing 策略里 **Workflow 字段要填 `nuget.yml`**(现有那条填的是 `release.yml`,
那是应用发布流水线、不发包)。仓库机密只剩 `STRONG_NAME_KEY`(SDK 程序集的强名称签名)。
