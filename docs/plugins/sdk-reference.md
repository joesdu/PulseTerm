# 插件 SDK 参考

> 适用版本:**SDK 1.4.0 / apiLevel 1**
> 相关文档:[开发指南](dev-guide.md)(教程式) · [CLI 手册](cli.md) · [打包与发布](publishing.md)

本篇是**面向查阅**的 SDK 全貌:包结构、契约表面、每个能力域能做什么/受什么限制、
版本演进与测试替身。想要按步骤走一遍的,先读[开发指南](dev-guide.md)。

---

## 1. 包结构

| 包 | 谁引用 | 内容 |
| --- | --- | --- |
| **`VelaShell.PluginSdk.Build`** | **插件工程(只引这一个)** | MSBuild props/targets、锁定版本的 Avalonia、随包分发的打包器、清单校验、`PackVpx` 目标 |
| `VelaShell.PluginSdk` | 由上者传递引入 | 契约程序集:入口接口、`IPluginContext` 与全部能力接口、DTO、清单模型、`.vpx` 容器、宿主注册表 |
| `VelaShell.PluginSdk.Testing` | 插件的**测试**工程 | `TestPluginContext` 与各能力的内存替身 |
| `VelaShell.Plugin.Cli` | 开发者机器(dotnet tool) | `vela-plugin`:开发内环、校验、打包、签名、体检 |
| `VelaShell.Plugin.Templates` | 开发者机器 | `dotnet new velaplugin` / `velaplugin-ui` |

```xml
<PackageReference Include="VelaShell.PluginSdk.Build" Version="1.4.0" />
```

**不要**再单独引用 `VelaShell.PluginSdk` 或 `Avalonia` —— 版本对不上会在构建期直接报
`VELA1001`,而不是等用户装上后在运行期表现为控件类型转换失败。

契约程序集只依赖 BCL。这是一条纪律:它是宿主与插件**唯一共享的类型来源**,
往里塞第三方依赖等于把那个依赖的版本强加给所有插件。

---

## 2. 入口契约

```csharp
using VelaShell.PluginSdk;

[VelaPlugin]                                  // 恰好一个公开、非抽象、无参构造的类
public sealed class DemoPlugin : IVelaPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken ct)
    {
        context.Log.Info("activated");
        return Task.CompletedTask;            // 必须快速返回(限时 10 秒)
    }

    public Task DeactivateAsync(CancellationToken ct) => Task.CompletedTask;  // 限时约 2 秒
}
```

| 约束 | 细节 |
| --- | --- |
| 激活时限 | 10 秒;**挂着调试器时自动放宽到 10 分钟** |
| 停用时限 | 约 2 秒(应用退出路径),超时被放弃 |
| 长任务 | 自己开后台任务,用 `context.Shutdown` 令牌响应停机 |
| 资源回收 | 经 SDK 注册的命令与事件订阅由宿主自动清理;别把自己的类型塞进宿主静态字段/长命事件,否则 ALC 无法回收 |

---

## 3. `IPluginContext` 能力一览

`IPluginContext` 是插件访问宿主的唯一入口。**全部接口都是传输无关的**(只有异步方法、
DTO 与不透明 id),所以同一份插件源码在 `inProcess` 与 `isolated` 两种模式下都能跑。

### 3.1 身份与基础设施

| 成员 | 说明 |
| --- | --- |
| `PluginId` / `PluginVersion` | 来自 `plugin.json` |
| `DataDirectory` | 插件私有目录(已创建)。**一切本地写入都应限于此**,卸载时整体删除 |
| `Host` (`IHostInfo`) | 宿主版本、apiLevel、当前语言与主题 |
| `Log` (`IPluginLogger`) | `Debug/Info/Warn/Error`,写进宿主日志管道(带插件 id 前缀) |
| `Shutdown` | 停机令牌:触发后能力调用可能开始失败,应尽快收尾 |

### 3.2 数据

| 能力 | 关键方法 | 说明 |
| --- | --- | --- |
| `Storage` (`IPluginStorage`) | `GetAsync<T>` / `SetAsync<T>` / `RemoveAsync` / `GetKeysAsync` | 按插件 id 命名空间化的 KV,落 SonnetDB(headless 时退回 JSON 文件) |
| `Secrets` (`ISecretsApi`) | `GetAsync` / `SetAsync` / `DeleteAsync` | 加密落盘的私有键值。**没有明文兜底**:后端缺席时直接报不可用 |
| `TimeSeries` (`ITimeSeriesApi`) | `OpenAsync` / `ListAsync` / `DropAsync`;series 上 `WriteAsync` / `QueryAsync` / `CountAsync` / `DistinctTagValuesAsync` / `DeleteAsync` | 插件私有的嵌入式时序库(按时间追加 + 按标签检索) |

### 3.3 会话与远程

| 能力 | 关键方法 | 说明 |
| --- | --- | --- |
| `Sessions` (`ISessionsApi`) | `ListAsync` / `GetAsync` | 枚举当前 SSH 会话,**脱敏,不含任何凭据** |
| `RemoteFs` (`IRemoteFsApi`) | 目录/属性/读写/传输/重命名/删除 | 基于既有会话的 SFTP |
| `RemoteExec` (`IRemoteExecApi`) | `RunAsync`(整段结果)/ `StreamAsync`(按行回调) | 独立通道,**不进用户终端** |
| `RemoteTunnel` (`IRemoteTunnelApi`) | `OpenUnixSocketAsync` / `OpenTcpAsync` | 到远端端点的**裸字节双工流**(Docker Engine API、tar 流这类二进制协议)。仅 `inProcess` |
| `Terminal` (`ITerminalApi`) | `GetOutputAsync` / `SearchOutputAsync` / `WriteAsync` | 读取/搜索会话输出;**回写输入需要用户授权**(管理页可撤销) |

### 3.4 界面与扩展点

| 能力 | 关键方法 | 说明 |
| --- | --- | --- |
| `Commands` (`ICommandsApi`) | `Register` / `TryExecute` | 命令 id 必须以插件 id 为前缀;清单里声明的占位命令在激活时被真实处理器替换 |
| `Ui` (`IUiApi`) | `ShowPanelAsync(options, contentFactory)` | 呈现插件自建的 Avalonia 控件:`inProcess` 可停靠成主窗口标签页,`isolated` 为独立卡片窗口 |
| `TerminalView` (`ITerminalViewApi`) | `Create(...)` | **出借宿主的终端仿真器**(VT 解析、屏幕缓冲、选区、IME、键盘编码),插件拿到一个可嵌进自己界面的真终端。仅 `inProcess` |
| `Protocols` (`IProtocolsApi`) | 注册协议实现 | 插件自带的远程**文件**协议,与 SSH/SFTP/FTP 同为连接配置页的一等公民。仅 `inProcess` |
| `Workspaces` (`IWorkspacesApi`) | 注册工作区提供者 | **非文件型**连接类型(Redis、MySQL…),由插件全权渲染会话文档。仅 `inProcess` |
| `Clipboard` (`IClipboardApi`) | 文本读写 | 系统剪贴板 |
| `Events` (`IHostEvents`) | 会话连接/断开、主题与语言切换 | 订阅由宿主在停用时自动清理 |

> **`inProcess` 专属的四项**(`RemoteTunnel` / `TerminalView` / `Protocols` / `Workspaces`)
> 在隔离模式下调用会抛"能力不可用"。它们交出去的都是活的原生对象或裸流,
> 跨进程边界没有等价物 —— 需要它们就把 `hostMode` 设成 `inProcess`。

---

## 4. 清单(`plugin.json`)

字段全表见[开发指南 §3](dev-guide.md)。发布相关的三个版本闸见[打包与发布 §1.2](publishing.md)。
这里只强调最容易出事的三条:

1. `id` 发布后不可变(命令前缀 + 数据命名空间)。
2. 用了新 SDK 面就必须声明 `minSdkVersion`,否则老宿主上是运行期 `MissingMethodException`。
3. 声明式贡献点(`contributes.commands` / `protocols` / `workspaces`)在**发现期**就生效,
   不装载任何程序集 —— 这是"启动零开销 + 惰性激活"的基础,别把它们留空然后指望激活后再补。

---

## 5. SDK 版本历史

`apiLevel` 只在**破坏性**变更时才动(至今仍是 `1`);只增不改的新面靠 `minSdkVersion` 拦。

| SDK | 新增 | 插件需要声明 `minSdkVersion` 吗 |
| --- | --- | --- |
| 1.0 | 首版契约 | — |
| 1.1 | `ExecResult` 加标准错误与退出码;远程执行的流式形态 | 用到就要 |
| 1.2 | `IRemoteTunnelApi`(裸字节双工流) | 用到就要(`1.2.0`) |
| 1.3 | `ITerminalViewApi`(出借宿主终端控件) | 用到就要(`1.3.0`) |
| 1.3.1 | 工作区**变体**:`WorkspaceVariant`、`VariantKey`/`Variants`、`NoCredentials`/`NoEndpoint` | 用到就要(`1.3.1`) |
| **1.4** | `HostRegistry`(宿主自我登记,供 `vela-plugin` 定位安装与核对版本) | **不需要** —— 这是工具链面,插件代码不调用 |

---

## 6. 测试:不启动宿主也能跑

`VelaShell.PluginSdk.Testing` 提供 `TestPluginContext` 与各能力的内存替身:

```csharp
using VelaShell.PluginSdk.Testing;

[TestMethod]
public async Task Activate_RegistersCommand()
{
    using var context = new TestPluginContext();
    var plugin = new DemoPlugin();

    await plugin.ActivateAsync(context, CancellationToken.None);

    Assert.Contains("acme.demo.run", context.RecordingCommands.Registered);
    await plugin.DeactivateAsync(CancellationToken.None);
}
```

可用替身:`CollectingLogger`、`InMemoryStorage`、`InMemoryTimeSeries`、`FakeSessions`、
`FakeRemoteFs`、`FakeRemoteExec`、`FakeRemoteTunnel`、`FakeTerminal`、`FakeTerminalViewApi`、
`FakeUi`、`FakeSecrets`、`FakeClipboard`、`RecordingCommands`、`RecordingProtocols`、
`RecordingWorkspaces`、`TestHostEvents`、`TestHostInfo`。

单测覆盖不了的部分(真实 UI、真实会话、真实协议页签)走开发内环:
`vela-plugin dev init` → F5,见 [CLI 手册](cli.md)。

---

## 7. 装载模型:三条必须知道的规则

1. **每插件一个可收集 ALC**,插件自带依赖按其 `deps.json` 在插件目录内解析 ——
   你可以引任意 NuGet 包,与宿主的版本互不干扰。
2. **两类程序集强制共享**:`VelaShell.PluginSdk` 与 `Avalonia*` 一律回落到装载方那一份。
   所以插件的 Avalonia 版本必须与宿主一致(SDK 包已锁定),而且名字以 `Avalonia` 开头的
   第三方包用不了(会被当成共享程序集去宿主里找,找不到)。
3. **开发期插件从影子副本装载**(`~/.velashell/dev-shadow/<id>/gen-N`),因此宿主运行时
   不锁你的 `bin`,可以边跑边重编,改完在管理页点"重新加载"即可。生产路径不走影子拷贝。

---

## 8. 宿主注册表(`HostRegistry`,SDK 1.4)

`VelaShell.PluginSdk.Hosting.HostRegistry` 读写 `~/.velashell/host.json`:宿主每次启动登记
自己的可执行文件路径、版本、apiLevel、内置 SDK 版本、Avalonia 版本与数据根。

面向的是**工具链**(`vela-plugin dev init` / `doctor` / `hosts`),插件运行时用不到它。
如果你要写自己的构建脚本或 IDE 插件,可以这样拿到本机宿主:

```csharp
HostRegistryEntry? host = HostRegistry.Resolve();          // 最近启动过的那份
HostRegistryEntry? preview = HostRegistry.Resolve("1.5");  // 按版本挑
IReadOnlyList<HostRegistryEntry> all = HostRegistry.List();
```

所有读路径对文件缺失/损坏都返回空表而不是抛异常 —— 这个文件是加速用的缓存,
坏掉的后果应当是"让你手动指一下路径",而不是任何一侧起不来。
