# VelaShell 插件开发指南(v1)

> 状态:**已实现,随仓库可用**。本指南描述当前落地的插件系统(双宿主模式 +
> 完整 Avalonia UI);目录下编号 01–15 的文档是完整插件平台的长期设计蓝图
> (权限系统、插件商店等),未实现的部分以蓝图为准、按需分期落地。

## 1. 一分钟了解架构

- **双宿主模式**(manifest `hostMode` 选择):
  - `inProcess`(默认):插件在宿主进程内自己的**可收集 AssemblyLoadContext** 里,
    零 IPC 开销,面板可停靠进主窗口标签区;
  - `isolated`:插件在独立的 VelaShell.PluginHost 进程里(命名管道 RPC),
    崩溃/卡死不影响宿主;面板为独立卡片窗口(需真·dock 停靠请用 inProcess)。
  两种模式下**插件源码完全一致**。
- **依赖隔离**:插件自带依赖(任意 NuGet 包)按其 `deps.json` 在插件目录内解析,
  与宿主互不干扰;仅两类程序集强制与装载方共享保证类型同一:
  `VelaShell.PluginSdk` 与 `Avalonia*`(所以 Avalonia 版本必须与宿主一致,见 §5.9)。
- **契约细腰**:插件引用 `VelaShell.PluginSdk`(仅 BCL 依赖);UI 直接用完整的
  Avalonia(compile-only 引用)。SDK 与 Avalonia 程序集**永远不要**复制进插件目录。
- **故障隔离**:进程内模式靠全路径守卫(单插件失败只标自己 Failed,但防不了
  死循环/内存失控);隔离模式靠进程边界(硬故障也只损失该插件)。
- **零启动开销**:发现只读 `plugin.json` 不碰程序集,且整个发现+激活在主窗口
  显示后的后台线程执行;没有插件时启动路径只多两次目录存在性检查。

相关源码:

| 位置 | 内容 |
| --- | --- |
| `plugin-sdk/VelaShell.PluginSdk/` | 契约:入口接口、能力接口、DTO、manifest 模型 |
| `plugin-sdk/VelaShell.PluginSdk.Testing/` | 测试替身:`TestPluginContext` 与各能力内存实现 |
| `plugin-sdk/VelaShell.PluginSdk.Build/` | 插件工程引用的那一个 NuGet 包:MSBuild props/targets + 随包分发的打包器 |
| `tools/VelaShell.Plugin.Cli/` | `vela-plugin` 命令行工具(校验/打包/签名/开发挂载) |
| `templates/` | `dotnet new` 模板(`velaplugin` / `velaplugin-ui`) |
| `src/VelaShell.Infrastructure/Plugins/` | 宿主运行时:发现/装载/激活/停用、能力桥接 |
| `src/VelaShell.Presentation/Plugins/` | 命令能力对命令注册表的桥接 |
| `src/VelaShell.PluginHost/` | 隔离插件宿主进程:RPC 代理上下文、内建 Avalonia、停靠嵌入 |
| `plugins/` | 仓库内第一方插件(含 HelloWorld 示例) |

## 2. 快速上手

### 2.1 仓库内插件(第一方)

```text
plugins/VelaShell.Plugin.Demo/
├── VelaShell.Plugin.Demo.csproj
├── plugin.json
└── DemoPlugin.cs
```

csproj(`plugins/Directory.Build.props` 已统一 `EnableDynamicLoading` 与
plugin.json 输出;`VelaPluginId` 驱动构建后复制到应用输出目录):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <VelaPluginId>velashell.demo</VelaPluginId>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\plugin-sdk\VelaShell.PluginSdk\VelaShell.PluginSdk.csproj"
                      Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

plugin.json:

```jsonc
{
  "id": "velashell.demo",              // <发布者>.<名称>,小写 [a-z0-9.-]
  "version": "0.1.0",                  // semver
  "displayName": "Demo",
  "description": "示例",
  "entry": "VelaShell.Plugin.Demo.dll", // 相对插件目录;禁止绝对路径与 ".."
  "apiLevel": 1
}
```

入口类:

```csharp
using VelaShell.PluginSdk;

[VelaPlugin]
public sealed class DemoPlugin : IVelaPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken ct)
    {
        context.Log.Info("Demo activated.");
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken ct) => Task.CompletedTask;
}
```

**直接 F5 即可**:主程序对 `plugins/*/*.csproj` 建立了纯构建顺序引用
(`ReferenceOutputAssembly=false`),启动前仓库内插件总会重新构建并把输出
(含 `plugin.json`)镜像到 `src/VelaShell/bin/<配置>/net11.0/plugins/<id>/`。
改 manifest 或插件代码都不需要手动先构建插件项目。
最后把项目加进 `VelaShell.slnx` 的 `/plugins/` 文件夹(仅为 IDE 可见性)。

**要不要随安装包分发**,由 `<VelaPluginShip>` 决定(默认 `true`):

```xml
<!-- 只做范例、不进发行包:本机构建照常镜像进 bin,F5 能装载;dotnet publish 不收它 -->
<VelaPluginShip>false</VelaPluginShip>
```

`true` 的插件在 `dotnet publish` 时由 `src/VelaShell/VelaShell.csproj` 的
`AddVelaPluginsToPublish` 登记进 `ResolvedFileToPublish`,落到安装包的
`plugins/<目录名>/`(并标记 `ExcludeFromSingleFile`,保证是磁盘上的真实文件,
ALC 才能按 deps.json 装载)。官方示例插件 `velashell.hello-world` 就设了 `false`。

> **目录名 = id 把点换成短横**(`velashell.ai` → `velashell-ai`)。macOS 的 `codesign`
> 会把 `.app` 内带点号的目录当成嵌套 bundle 去解析,原样用 id 做目录名会让签名直接失败
> (`bundle format unrecognized, invalid, or unsuitable`)。目录名**不参与任何逻辑** ——
> 宿主是枚举子目录后从 `plugin.json` 读 id,因此这只是打包侧的命名约定。

### 2.2 仓库外插件(第三方)—— 从 `dotnet new` 到装上,五分钟

SDK 以 NuGet 包分发,**插件工程只需要引用一个包**。

```bash
# 一次性:装模板与命令行工具
dotnet new install VelaShell.Plugin.Templates
dotnet tool install -g VelaShell.Plugin.Cli      # 可选,见下

# 建工程(id = acme.snippets)
dotnet new velaplugin -n Snippets --publisher acme --authorName "Your Name"
#   velaplugin      基础:入口类 + 一条命令
#   velaplugin-ui   带一个 Avalonia 面板(AXAML)
#   --hostMode inProcess|isolated

cd Snippets
dotnet build -c Release -t:PackVpx               # → bin/vpx/acme.snippets-0.1.0.vpx
```

生成的 `.csproj` 只有一行依赖:

```xml
<PackageReference Include="VelaShell.PluginSdk.Build" Version="1.4.0" />
```

这一个包把插件工程需要的东西一并带到:契约程序集 `VelaShell.PluginSdk`、**与宿主版本
严格一致的 Avalonia**(含它的 AXAML 编译器)、`EnableDynamicLoading`、`plugin.json` 进
输出目录、共享程序集不落地、清单编译期校验,以及 `PackVpx` 目标。**不要**再单独引用
`VelaShell.PluginSdk` 或 `Avalonia` —— 版本对不上会在构建期直接报 `VELA1001`,而不是
等到用户装上插件才在运行期表现为控件类型转换失败。

| 包 | 谁引用 | 作用 |
| --- | --- | --- |
| `VelaShell.PluginSdk.Build` | **插件工程** | 上面这一整套。引它一个就够 |
| `VelaShell.PluginSdk` | (随上面传递引入) | 契约程序集,仅 BCL 依赖 |
| `VelaShell.PluginSdk.Testing` | 插件的**测试工程** | `TestPluginContext` 与各能力内存替身 |
| `VelaShell.Plugin.Cli`(`vela-plugin`) | 开发者机器 | dotnet tool:校验/打包/签名/开发挂载 |
| `VelaShell.Plugin.Templates` | 开发者机器 | `dotnet new` 模板 |

> **SDK 不是 dotnet tool**,`VelaShell.PluginSdk*` 三个包都是普通 NuGet 包,用
> `PackageReference` 引用;只有 `vela-plugin`(`VelaShell.Plugin.Cli`)是 dotnet tool。
> 而且**打包不需要装这个工具** —— 打包器随 `VelaShell.PluginSdk.Build` 一起分发,
> `dotnet build -t:PackVpx` 直接可用。装全局工具是为了在构建之外随手校验、签名、
> 查看包内容、体检(`vela-plugin doctor`),以及配开发内环(`vela-plugin dev init`)。

装上去有两种方式:

**方式一:`.vpx` 包(推荐)** —— 侧栏插件图标 → 插件管理页 → "安装 .vpx…" 选择文件即装
(校验容器与清单、zip-slip 与解压炸弹防护、解包进用户目录、同 id 覆盖旧版、按激活策略激活);
命令行等价物是 `vela-plugin install <包>`。卸载同样在管理页一键完成(删目录 + 清 DB 数据)。

`.vpx` 是 VelaShell 的**专属容器格式**,不是改了后缀的 zip —— 格式与签名见 §12。

**方式二:直接放目录**——把构建输出(入口 dll + deps.json + 自带依赖 + plugin.json)放进:

```text
~/.velashell/plugins/<插件id>/                    (Windows/Linux/macOS)
```

重启 VelaShell 即加载。再次强调:`VelaShell.PluginSdk.dll` 与 `Avalonia*.dll`
不要放进去(装载器强制共享自身那套,放了也只是徒增体积)。

> 应用自带插件(`<应用目录>/plugins`)是只读的,管理页不提供卸载;用户安装的
> (`.vpx` 或放进用户目录的)才可卸载。

### 2.3 开发内环与断点调试

仓库外插件不必"打包 → 安装 → 再看效果"。三步:

```bash
dotnet tool install -g VelaShell.Plugin.Cli   # 一次性
dotnet build
vela-plugin dev init                          # 配好 IDE 启动配置
```

然后在 IDE 里按 **F5**(启动配置名 `VelaShell`)。全套命令与开关见
[CLI 手册](cli.md);这里说清它到底做了什么、为什么这么做。

#### 宿主自己报家门

`dev init` 不去猜 VelaShell 装在哪 —— 三个平台三套安装位置,还有便携版与自更新换过位置,
探测逻辑既长又常年失准。改成**宿主每次启动把自己写进 `~/.velashell/host.json`**:
可执行文件路径、版本、apiLevel、内置 SDK 版本、Avalonia 版本、数据根、PluginHost 路径。

```bash
vela-plugin hosts     # 看本机登记了哪几份安装
```

因此前置条件只有一条:**本机至少完整启动过一次 VelaShell**。没有的话用
`vela-plugin dev init --exe <路径>` 直指可执行文件。多份安装并存(正式版 + 预览版)时,
默认取最近启动过的那份,`--host 1.5` 可以点名。

#### 生成的启动配置

```jsonc
"VelaShell": {
  "commandName": "Executable",
  "executablePath": "…/VelaShell.exe",
  "commandLineArgs": "--dev-root …/Snippets/bin/Debug --wait-debugger acme.snippets --data-root …/.velashell-dev"
}
```

| 参数 | 解决的问题 |
| --- | --- |
| `--dev-root` | 把工程输出挂进宿主。**跟着工程走**,不写机器级全局状态 —— 同时开两个插件工程、或在分支间切换都互不干扰 |
| `--wait-debugger` | 隔离插件的子进程在装载程序集**之前**挂起等你附加 |
| `--data-root` | 调试实例用独立数据根 |

第三条最容易被低估:开发者日常几乎肯定开着一份 VelaShell,而**共用数据根的第二个实例会
撞上单实例保护**(SonnetDB 对 WAL 持独占锁),弹一句"已在运行"就干净退出 —— 看起来像
启动配置写错了。换个数据根,两个实例并存,调试用的连接与设置也不会污染日常配置。
真要在日常配置里试,加 `--shared-data`(此时必须先退出日常实例)。

这三个参数都有等价的环境变量(`VELA_PLUGIN_DEV_ROOT` / `VELA_PLUGIN_WAIT_DEBUGGER`),
**参数优先**;第三个来源是 `~/.velashell/plugins.dev.txt`(`vela-plugin dev link` 写的,
适合"让日常实例长期带着这个插件跑")。三者叠加,开发根整体排在正式插件根**之后**,
同 id 先到先得 —— 本机开发中的插件不会顶掉用户已安装的同名插件。

#### 改完代码怎么办:重新加载,不重启

```bash
dotnet build
```

回到插件管理页,在该插件那一行点 **重新加载**:停用 → 卸载 ALC / 回收进程 → **重读清单** →
重新装载。清单也一起重读,所以两次构建之间改了版本、命令、协议页签都会跟着更新。

Windows 上这一步过去做不到:ALC 用 `LoadFromAssemblyPath` 装载,插件活着就锁着入口 dll,
于是内环退化成"关掉宿主 → 重编 → 再启动"。现在**开发期插件从影子副本装载**
(`~/.velashell/dev-shadow/<id>/gen-N`,每次装载换一代目录,旧代能删则删),
工程 `bin` 随时可以重编。生产路径不走影子拷贝,行为分文不动。

想连按钮都省掉:`vela-plugin dev init --watch`(即 `--dev-watch`),宿主监视开发根,
入口程序集的写入时间一变就自动重载(去抖 1.5 秒,等构建写完)。默认关 ——
文件监视器在网络盘/共享盘上会抖,不该是所有人默认承担的成本。

> 开发期插件的**禁用状态**记在 `~/.velashell/plugins.dev.disabled`,不写进构建产物目录 ——
> 否则 `.disabled` 标记会留在 `bin` 里,表现为"我明明重编了怎么还是禁用状态"。

#### 断点

| 插件形态 | 怎么调 |
| --- | --- |
| `inProcess` | F5 起来的宿主进程本身就附着着调试器,插件被装进这个进程,断点直接命中(包括 `ActivateAsync` 第一行)。宿主检测到调试器时会把激活超时从 10 秒放宽到 10 分钟 |
| `isolated` | 插件跑在 `VelaShell.PluginHost` 子进程里。`--wait-debugger <id>` 命中的插件,子进程在**装载插件程序集之前**挂起等你附加;pid 显示在插件管理页上、打进日志、并落在 `~/.velashell/logs/plugin-host-<id>.pid` |

`--wait-debugger` 命中的插件,宿主同时**放宽激活超时并停掉心跳** —— 否则断点冻住插件进程的
全部线程,心跳连续两次失败就把它强杀了,表现为"一下断点插件就没了"。

#### 出问题先问 doctor

```bash
vela-plugin doctor
```

一次性核对:宿主是否已登记、`apiLevel`/`minSdkVersion`/`minHostVersion` 三道兼容闸、
输出目录里有没有 `plugin.json` 与 `.deps.json`、有没有误把 `VelaShell.PluginSdk.dll` /
`Avalonia*.dll` 打进输出、启动配置是否还留着占位符。有阻断性问题时退出码为 1(可进 CI)。

不想启动整个宿主时,插件的业务逻辑可以用 `VelaShell.PluginSdk.Testing` 的
`TestPluginContext` 在普通单测里跑(见 §7)。

## 3. 清单(plugin.json)参考

| 字段 | 必需 | 说明 |
| --- | --- | --- |
| `id` | ✓ | 全局唯一,`[a-z0-9.-]`,首尾必须为字母/数字,≤64 字符。命令 id 都以它为前缀 |
| `version` | ✓ | semver(`1.2.0` / `1.2.0-beta.1`) |
| `displayName` | ✓ | 展示名称 |
| `entry` | ✓ | 入口程序集相对路径,必须 `.dll` 结尾;拒绝绝对路径与 `..` 段 |
| `description` | | 一句话描述 |
| `publisher` | | 发布者标识(将来与签名公钥绑定,参与信任判定) |
| `author` | | 作者,展示在插件管理页(如 `"Joe <joe@example.com>"`,≤128 字符、不许控制字符)。缺省时管理页退回显示 `publisher` |
| `apiLevel` | | 默认 1;高于宿主支持的代际 → 标记 Incompatible 拒载 |
| `minHostVersion` | | 要求的最低宿主版本;不满足 → Incompatible |
| `minSdkVersion` | | 要求的最低**插件 SDK** 版本(如 `1.1.0`);不满足 → Incompatible。**用到了新 SDK 面的插件必须声明它**:`apiLevel` 只在破坏性变更时才动,而新增的接口方法与 DTO 字段不算破坏性 —— 不声明的话老宿主会把插件装上、激活,然后在第一次调用新方法时抛 `MissingMethodException`。SDK 版本与宿主版本刻意解耦,所以 `minHostVersion` 表达不了这件事 |
| `activationEvents` | | 省略或含 `"onStartup"` = 启动激活;`"onCommand:<命令id>"` / `"onProtocol:<协议id>"` / `"onWorkspace:<连接类型id>"` = **惰性激活**(命中占位命令、或用户选中该页签才装载;须在对应的 `contributes.*` 里声明) |
| `contributes.commands` | | 声明式命令占位 `[{id,title,category}]`:发现期即进命令面板,id 必须以插件 id 为前缀;激活时插件应 `Register` 同 id 的真实处理器替换占位 |
| `contributes.protocols` | | 声明式协议页签 `[{id,displayName,defaultPort}]`:发现期即进连接配置页(**不装载程序集**),id 必须等于插件 id 或以其为前缀;设置表单在激活后由 `ProtocolDescriptor` 补齐。要求 `hostMode` 为 `inProcess` |
| `contributes.workspaces` | | 声明式**非文件型**连接页签 `[{id,displayName,defaultPort}]`(Redis、MySQL…):与协议页签同一条条带、同一套 id 规则,但打开会话时宿主向插件索取一个控件挂成停靠文档而不是打开文件浏览器。见 §5.14;同样要求 `inProcess`,且 id 不得与本清单里的协议 id 相撞 |
| `idlePolicy` | | `"keepAlive"`(默认)/ `"recyclable"`:隔离模式下连续空闲(无 RPC 且无打开面板,默认 15 分钟)即回收进程,占位命令留守待再触发 |
| `homepage` / `license` | | 元信息 |

允许 JSON 注释与尾逗号。校验失败的插件在日志中给出可读原因
(`[PluginManager] Rejected plugin at ...`)。

## 4. 生命周期

```text
Discovered ──激活(启动后后台批次)──▶ Active ──宿主退出──▶ Deactivated
    │                                   │
    ├─ .disabled 标记 → Disabled         └─ 装载/激活异常、激活超时(10s)→ Failed(卸载 ALC)
    ├─ 清单非法 / id 冲突 → Invalid
    └─ apiLevel / minHostVersion 不符 → Incompatible
```

契约要点:

- `ActivateAsync` **必须快速返回**(限时 10 秒):长任务自己开后台任务,
  用 `context.Shutdown` 令牌响应停机。
- `DeactivateAsync` 限时约 2 秒(应用退出路径),超时被放弃。经 SDK 注册的
  命令与事件订阅由宿主自动清理,只需收尾自己的资源。
- 入口类型:恰好一个公开、非抽象、带 `[VelaPlugin]` 且实现 `IVelaPlugin` 的类,
  要求公开无参构造。
- 停用/失败后 ALC 被 `Unload()`:不要把自己的类型塞进宿主的静态字段/长命事件,
  否则程序集无法回收。

## 5. 能力 API 参考(`IPluginContext`)

### 5.1 Log —— 日志

```csharp
context.Log.Info("hello");
context.Log.Error("failed", ex);
```

落宿主 Trace 管道,自动带 `[Plugin:<id>]` 前缀,线程安全。

### 5.2 Storage —— 私有键值存储

```csharp
int n = await context.Storage.GetAsync<int>("count", ct);
await context.Storage.SetAsync("count", n + 1, ct);
```

数据落宿主 **SonnetDB**(`plugin_data` 集合,主键 `<插件id>|kv|<键>`):

- **按插件强隔离**:能力实例只带自身 id 前缀,插件读不到别家的数据
  (插件 id 字符集不含分隔符 `|`,命名空间不可逃逸);隔离进程经 RPC 路由到
  同一实现,插件进程永不直连数据库;
- **卸载自动清理**:插件目录从 plugins/ 移除后,下次启动宿主整体清除其 DB
  命名空间与数据目录(`.disabled` 禁用 ≠ 卸载,数据保留);
- 单值建议 ≤256KB;大块数据直接写 `context.DataDirectory` 下的文件
  (卸载时同样被清扫)。无 DB 的 headless 宿主自动退回数据目录 JSON 文件。

### 5.2b TimeSeries —— 私有时序库

按时间追加、按标签检索的数据(会话记录、指标采样、事件流)用它;小配置仍用 Storage。

```csharp
ITimeSeries series = await context.TimeSeries.OpenAsync(new("chat_messages",
[
    TimeSeriesColumn.Tag("conv"),                                   // 标签 = 索引维度
    TimeSeriesColumn.Field("role", TimeSeriesValueKind.Text),
    TimeSeriesColumn.Field("seq",  TimeSeriesValueKind.Integer),
    TimeSeriesColumn.Field("text", TimeSeriesValueKind.Text)
]), ct);

var clock = new TimeSeriesClock();                                  // 严格递增时间戳
await series.WriteAsync(new(clock.Next(),
    new Dictionary<string, string> { ["conv"] = id },
    new Dictionary<string, TimeSeriesValue>
    {
        ["role"] = TimeSeriesValue.FromText("user"),
        ["seq"]  = TimeSeriesValue.FromInteger(0),
        ["text"] = TimeSeriesValue.FromText(message)
    }), ct);

IReadOnlyList<TimeSeriesPoint> latest = await series.QueryAsync(new()
{
    Tags = new Dictionary<string, string> { ["conv"] = id },
    Descending = false,
    Limit = 500
}, ct);
await series.DeleteAsync(new Dictionary<string, string> { ["conv"] = id }, ct);   // 删掉一整条序列
```

数据落宿主 **SonnetDB** 的时序 measurement,物理名 `pts_<插件命名空间>_<短名>`:

- **按插件强隔离**:命名空间由插件 id 派生(哈希兜底,`a.b` 与 `a-b` 不会撞),
  插件只能看到自己的 measurement;卸载时按前缀整体 drop;
- **同序列同毫秒 = 覆盖**:序列 = measurement + 完整标签组合。高频写入务必用
  `TimeSeriesClock.Next()` 取时间戳;反过来,这条语义也可以**故意**利用 ——
  用固定时间戳写"每个会话一条摘要",天然只保留最新一份(AI 插件的会话列表就是这么做的);
- **查询语义**:先按标签/时间过滤,再由宿主排序取 `Limit`。匹配点上万时扫描会被截断,
  请用 `Since`/`Until` 或更细的标签收窄;
- **配额**(`TimeSeriesLimits`):每插件 ≤8 个 measurement、每表 ≤32 列、标签值 ≤200 字符、
  文本字段 ≤1MB、单批 ≤1000 点、单查 ≤5000 条。名称限 `[a-z][a-z0-9_]*`;
- 无 DB 的 headless 宿主上 `OpenAsync` 抛 `InvalidOperationException`(**不会**静默丢数据),
  插件应据此降级。单测用 `InMemoryTimeSeries`(同一套校验与覆盖语义)。

### 5.3 Sessions —— 会话枚举(脱敏)

```csharp
IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(ct);
SessionInfo? one = await context.Sessions.GetAsync(sessionId, ct);
```

`SessionInfo` 只含连接元数据(host/port/username/状态/时间),**不含任何凭据**。
`SessionId` 是其它远程能力的第一参数。v1 插件不能发起连接。

### 5.4 RemoteFs —— 远程文件(SFTP)

```csharp
var entries = await context.RemoteFs.ListDirectoryAsync(sid, "/var/www", ct);
RemoteFileEntry? stat = await context.RemoteFs.StatAsync(sid, "/etc/nginx/nginx.conf", ct); // 不存在 → null
byte[] conf = await context.RemoteFs.ReadAllBytesAsync(sid, "/etc/nginx/nginx.conf", ct: ct);
await context.RemoteFs.DownloadFileAsync(sid, "/var/log/app.log", localPath, progress, ct);
```

- 复用用户已建立的会话通道,不重复认证;会话无效抛 `PluginSessionNotFoundException`。
- `StatAsync` 对不存在路径返回 `null`(与宿主语义一致,勿以异常判存在)。
- `ReadAllBytesAsync` 默认上限 16MB;大文件用 `OpenReadAsync`(**只读顺序流**,
  边读边处理、不落临时文件)或 `DownloadFileAsync`。隔离模式下 `OpenReadAsync`
  经 RPC 分块拉取(不支持 Seek)。
- 进度回调已由宿主节流(≥100ms),放心直接更新状态。

### 5.5 RemoteExec —— 远程命令(一次性 / 流式)

独立 exec 通道:不进用户终端、不污染 shell 历史与环境。两种形态:

**一次性**(探测类命令)——

```csharp
ExecResult r = await context.RemoteExec.RunAsync(sid, "docker ps --format json",
    new ExecOptions { Timeout = TimeSpan.FromSeconds(10) }, ct);

if (!r.IsSuccess)                     // 退出码非 0 **不抛异常** —— 命令跑失败是正常结果
{
    context.Log.Warn(r.FailureText);  // 优先取 stderr 首行,没有才退回 stdout / 退出码
}
Parse(r.Output);                      // stdout 与 stderr 是分开的两条流
```

- 默认超时 30s、上限 10min;超时抛 `TimeoutException`。
- **`Output` 只含标准输出**;`Error` 是标准错误,`ExitCode` 是退出码 —— 三样都在
  `ExecResult` 上。别再自己拼 `2>&1; echo $?` 那种哨兵:合并两条流会让解析
  `--format json` 的代码被一行 `WARNING:` 噎死,而哨兵在用户登录 shell 是 fish/csh 时就崩了。

**流式**(长驻命令:`docker logs -f`、`docker events`、`tail -F`)——

```csharp
sealed class LineSink(Action<string> onLine) : IProgress<ExecOutput>
{
    public void Report(ExecOutput o) => onLine(o.Line);   // 同步转发,顺序即到达顺序
}

ExecStreamResult done = await context.RemoteExec.StreamAsync(sid,
    "docker logs -f --tail 200 web",
    new ExecStreamOptions { Timeout = null },             // null = 不限时,靠取消令牌收尾
    new LineSink(AppendToView), panelClosedToken);
```

- 输出**按行**回调,宿主不节流(日志的价值就在于即时);要攒批请自己攒。
- **别传 `System.Progress<T>`**:它把每次回调 `Post` 到同步上下文或线程池,
  顺序与线程都不再保证 —— 对进度百分比无所谓,对一屏日志是灾难。
  宿主是在读行的那个线程上顺序调 `Report` 的,所以只要你的实现是同步的,行序就是保证的。
- 取消令牌触发 → 宿主给远端进程发 `TERM` 再关通道,方法抛 `OperationCanceledException`;
  `Timeout` 到点 → 抛 `TimeoutException`。**插件必须持有并触发那个令牌**。
- 每插件最多 `IRemoteExecApi.MaxConcurrentStreams`(4)条流同时在飞,超了抛
  `InvalidOperationException`。流不限时且各占一个 SSH 通道,没有上限的话一个忘了取消的
  插件能把对端的 `MaxSessions` 耗光 —— 那时坏的是用户的连接,不只是这个插件。
- **隔离模式下"不限时"做不到**:取消令牌不跨进程传播(§6),未指定 `Timeout` 的流会被
  补上两小时的死线。要即时取消的流式插件请用 `inProcess`。

> 交互式命令(要伪终端、要键盘输入)两者都不适用 —— 那是终端标签的事,
> 用 `context.Terminal.WriteAsync` 把命令敲进去(需用户授权)。

### 5.5b RemoteTunnel —— 到远端 socket / TCP 的裸字节流(SDK 1.2)

```csharp
await using Stream stream = await context.RemoteTunnel
    .OpenUnixSocketAsync(sessionId, "/var/run/docker.sock", cancellationToken: token);
// 也可以连远端的 TCP 端点(地址从**远端**的角度解析):
// await context.RemoteTunnel.OpenTcpAsync(sessionId, "127.0.0.1", 6379, cancellationToken: token);
```

**什么时候需要它。** 远程执行的两种形态都是**文本**:`RunAsync` 把整个输出 UTF-8 解码成
一个字符串,`StreamAsync` 按 `\n` 切行回调。承载二进制协议时这不是"慢一点",是**数据静默
损坏** —— UTF-8 解码会把非法字节换成 U+FFFD(不可逆),按行切分会在 `0x0A` 处把一帧劈成
两半。Docker Engine API 的分块传输、`/archive` 的 tar 流、`/exec` 的 8 字节多路复用帧
都属于这一类。要说这类协议就用隧道。

- 返回的 `Stream` 可读可写;`Dispose` 关闭 SSH 通道并归还配额 —— **调用方必须释放它**。
- 取消令牌只作用于**建立**阶段;通道建成之后读写由调用方自己的令牌与远端决定。
  隧道的正常形态就是 `docker events` 这种挂着不动的长连接,给它一个总时限只会让界面
  在第 N 分钟莫名其妙地断流。
- 每插件最多 `IRemoteTunnelApi.MaxConcurrentTunnels`(16)条,理由同流式执行。
- **只在 `inProcess` 可用**:它交出去的是一条活的流,跨进程代理除了把每个字节多搬一次
  之外得不到任何东西。隔离进程里调用抛 `NotSupportedException`。

> 与宿主的本地端口转发不同:隧道**不在本机开监听端口**,流只交给发起调用的插件。
> 对面是一个 root 等价的 socket 时,这个区别不是优化而是前提。

### 5.5c TerminalView —— 借宿主的终端仿真器(SDK 1.3)

插件想要一个**真终端**(能跑 `top` / `vim` / `less`),不必自己写 ANSI 解析:

```csharp
if (!context.TerminalView.IsAvailable)
{
    // 老宿主或隔离进程:退化成行式输出,别让按钮点下去炸。
    return;
}
// 必须在 UI 线程调用(视图构造本来就在 UI 线程上)。
IPluginTerminalView view = context.TerminalView.Create(new() { ScrollbackLines = 5000 });
var host = new ContentControl { Content = (Control)view.Control };

// 远端尺寸要跟着控件走,否则 vim 会照旧尺寸画,画出来是错位的。
view.Resized += (cols, rows) => _ = session.ResizeAsync(rows, cols);

// 一行接上双工流:读在后台、渲染回 UI 线程、用户按键串行写回 —— 都由宿主做掉。
await view.AttachAsync(session.Stream, token);
```

- 交出去的 `Control` 是 Avalonia 控件(以 `object` 出面 —— SDK 这一层刻意不认识 UI 框架,
  与 `IUiApi.ShowPanelAsync` 的内容工厂同一个约定)。
- 外观默认**跟随宿主的终端设置**(字体、字号、行高、配色、光标、Gutter)。
  用户调过一次终端字体,不该因为换到插件面板里就得再调一次。
- `AttachAsync` 同一时刻只接一条流;再接一条会先把前一条断掉。它**不**负责释放传入的流。
- `Dispose` 销毁控件并断开当前的流。
- **只在 `inProcess` 可用**:交出去的是活的原生控件,跨进程嵌不了。
  隔离进程里调用抛 `NotSupportedException` —— 先用 `IsAvailable` 问一句。

> 与 `ITerminalApi` 的分工:那一个是对**宿主已有会话**的旁路(读缓冲、搜输出、经授权回写);
> 这一个是插件**自己的**终端。前者操作别人的终端,后者拥有一个自己的。

### 5.6 Commands —— 命令面板

```csharp
context.Commands.Register(new(
    $"{context.PluginId}.refresh", "Demo: Refresh", "Demo",
    async ct => { /* 后台线程执行;异常自动记日志 */ }));
```

- id 必须以 `<pluginId>.` 为前缀(宿主强制,防插件间冒名)。
- 注册后出现在命令面板(Ctrl+P / Ctrl+K);标题本地化由插件自理
  (可按 `context.Host.Locale` 取词,并订阅 `LocaleChanged` 重注册)。
- 命令体在**后台线程**执行,不要触碰 UI;慢操作不会冻结界面。
- 插件停用时自动全部注销;`Register` 返回的句柄用于提前注销。

### 5.7 Events —— 宿主事件

```csharp
context.Events.SessionConnected += s => context.Log.Info($"{s.Host} connected");
context.Events.ThemeChanged     += theme => ...;
context.Events.LocaleChanged    += locale => ...;
```

处理器在非 UI 线程触发,必须**快速返回且不抛出**(异常被捕获记日志);
耗时工作转投自己的后台任务。停用时订阅自动拆除。

### 5.8 Host —— 宿主信息

`context.Host.AppVersion / ApiLevel / Locale / Theme`(后两者实时)。

### 5.9 Ui —— 面板(完整 Avalonia)

插件用**完整的 Avalonia** 自行设计界面:编译期 AXAML 或纯代码任选,自带样式、
资源、国际化,也可以引入任意第三方组件包(随插件目录分发,ALC 隔离互不干扰)。

**唯一硬约束:Avalonia 版本与宿主一致。** Avalonia 相关包必须 compile-only:

```xml
<!-- csproj:版本 = 宿主版本(当前 12.1.1);Avalonia dll 绝不复制进插件目录,
     运行时由装载方共享同一套(进程内 = 宿主的;隔离进程 = PluginHost 自带的)。 -->
<PackageReference Include="Avalonia" Version="12.1.1" ExcludeAssets="runtime" />
```

基于 Avalonia 的第三方组件包(如控件库)则**正常引用**(它们的 dll 要随插件
分发,只有 `Avalonia*` 本体被装载器强制共享)。

打开面板:内容工厂由宿主在 **UI 线程**调用,返回你的控件即可:

```csharp
IPluginPanel panel = await context.Ui.ShowPanelAsync(
    new() { Title = "My Panel", DisplayMode = PanelDisplayMode.Document },
    () => new MyPanelView(context));   // 编译期 AXAML 的 UserControl,或任意 Control
panel.Closed += () => ...;             // 用户关闭/插件停用时触发
await panel.ActivateAsync();           // 带到前台(窗口:还原最小化并激活;文档:切到该标签)
await panel.CloseAsync();              // 程序性关闭
```

显示模式由插件自己选:

- `PanelDisplayMode.Document` —— 停靠标签页:进主窗口标签区,用户可**拖拽到任意
  分栏位置**、右键拆分,与终端/SFTP 标签同等公民。**仅 inProcess 模式**为真停靠;
  isolated 模式一律独立卡片窗口(跨进程 dock 嵌入与切标签冲突,已弃用);
- `PanelDisplayMode.Window` —— 独立窗口:进程内为宿主同款自绘卡片窗口;
  隔离进程为插件进程自己的窗口(明暗主题自动跟随宿主)。

停靠标签页还可以选**落位**(`PanelOptions.Placement`,窗口模式忽略):

```csharp
new() { Title = "AI 助手", Placement = PanelPlacement.Right, PlacementRatio = 0.3 }
```

`Tabs`(默认)并入当前标签组;`Right`/`Left`/`Bottom`/`Top` 在标签区对应外沿拆出一栏,
宽度由 `PlacementRatio`(占标签区的比例,0.15–0.85,默认 0.3)决定。落位走的就是拖放停靠
那条路径,所以结果与用户手动拖过去完全一致 —— 之后拖回、再拆分、关闭都没有任何特殊分支。
`PlacementRatio` 只管"打开时多宽":用户拖分割条改过的宽度不会写回来,插件想记住用户偏好
就自己存一份、下次打开时传进来(AI 插件即如此,设置页里有"侧栏宽度(%)")。

窗口模式还可以在**标题栏上放动作按钮**(`PanelOptions.TitleActions`,停靠模式忽略):
紧挨最小化键左侧、按给出的顺序排列,与主窗体标题栏那排工具按钮同一套版式。
适合"这个窗口的附属设置"这类不值得占内容区的入口(AI 插件的模型配置窗口就用它开全局设置):

```csharp
new()
{
    Title = "模型配置", DisplayMode = PanelDisplayMode.Window,
    TitleActions = [new PanelTitleAction(GearPathData, "全局设置", OpenGlobalSettings)]
}
```

图标传的是 lucide 风格的 24×24 **SVG 路径数据**而不是资源键 —— 隔离进程里没有宿主的
`Icon.*` 资源字典;宿主按标题栏字号缩放描边。回调在 UI 线程调用。

**主题令牌:写 `{DynamicResource VelaXxx}` 就能贴宿主主题。** 宿主的全部
`Vela*` 设计令牌(语义画刷、字号阶梯、字体族)对插件可用,明暗切换即时跟随:

```xml
<Style Selector="TextBlock.title">
  <Setter Property="Foreground" Value="{DynamicResource VelaTextPrimary}" />
</Style>
<Border Background="{DynamicResource VelaBgInput}"
        BorderBrush="{DynamicResource VelaBorderPrimary}" />
<TextBlock Foreground="{DynamicResource VelaAccent}" />
```

- 进程内:控件在宿主可视树里,令牌天然可查;
- 隔离进程:宿主在握手后把令牌快照(按当前明暗变体解析)经 RPC 下发,
  PluginHost 注入其 Application 资源;主题切换时重发,DynamicResource 自动刷新。
  内嵌字体段(`fonts:...#`)不跨进程,字体令牌只携带系统回退链。
- 常用令牌:`VelaTextPrimary/Secondary/Muted/Tertiary`、`VelaBgSurface/Page/Input/Hover`、
  `VelaBorderPrimary/Secondary`、`VelaAccent`、`VelaWarning/Error/Info`、
  `VelaFontSize9..16`、`VelaUiFont/VelaUiMonoFont`。完整清单见
  `src/VelaShell.Controls/Themes/VelaTokens.axaml` 与 `src/VelaShell/Themes/{Dark,Light}Theme.axaml`。
- 未命中的令牌(拼写错误/宿主老版本)属性保持默认值,不报错 —— 给关键配色留意语义兜底。

纪律:

- 控件的事件/更新由插件直接操作(标准 Avalonia 写法,`await` 后自动回 UI 线程);
  从后台线程改控件请走 `Dispatcher.UIThread`。
- 同一控件实例只挂一个面板;面板是活控件,无"整树刷新"概念。
- **重复触发同一目标时用 `ActivateAsync` 而不是直接 return**:同尺寸的居中窗口会把前一扇
  像素级盖住,最小化之后更是完全看不见 —— 用户只会以为点了没反应。
- 国际化自理:按 `context.Host.Locale` 取词,订阅 `LocaleChanged` 热更新。
- 插件停用时其全部面板由宿主自动关闭。
- 完整示例(编译期 AXAML + 双语文案 + 会话/远程执行联动):
  `plugins/VelaShell.Plugin.HelloWorld`(DemoPanelView.axaml)。

### 5.10 Secrets —— 加密机密存储

```csharp
await context.Secrets.SetAsync("api-token", token);
string? saved = await context.Secrets.GetAsync("api-token");
await context.Secrets.DeleteAsync("api-token");
```

与 Storage 的区别:值经宿主机密保护器**加密后才入 SonnetDB**(Windows 为 DPAPI
包裹的本地密钥;主键 `<插件id>|secret|<名>`,与 KV 同样按插件隔离、卸载同清),
且隔离模式下机密只存宿主侧。API token、口令一律放这里,别放 Storage。
适合少量短字符串;保护器不可用时能力直接报错,绝不明文兜底。

### 5.11 Terminal —— 读取/搜索/授权回写

```csharp
string tail = await context.Terminal.GetOutputAsync(sid, maxLines: 500);
var hits = await context.Terminal.SearchOutputAsync(sid, "error");         // 子串,大小写不敏感
var rx   = await context.Terminal.SearchOutputAsync(sid, @"\d{3} error", isRegex: true);
await context.Terminal.WriteAsync(sid, "docker ps\n");                     // 触发授权弹窗
```

- 读取/搜索是**缓冲区快照**(滚回 + 当前屏,纯文本无颜色);正则带 1s 超时。
- **回写需用户授权**:宿主弹窗给出 仅本次 / 本次运行 / 始终 / 拒绝 四选一;
  "始终允许"按插件持久化到 SonnetDB,可在插件管理页撤销。被拒抛
  `PluginPermissionDeniedException`(插件应体面降级,别反复骚扰)。
- 回写走宿主既有的输入串行化队列("如同用户键入"),单次 ≤4KB;换行才执行命令。

### 5.12 Clipboard —— 系统剪贴板

```csharp
await context.Clipboard.SetTextAsync(text);
string? current = await context.Clipboard.GetTextAsync();
```

经宿主主窗口执行(隔离模式 RPC 路由,语义一致)。剪贴板常含用户密码:
读取的内容**不要记日志、不要外发**。

### 5.13 Protocols —— 自带协议(文件 / 终端)

让插件提供一种**新协议**,在连接配置页里与 SSH/SFTP/FTP 平起平坐。协议有两种形态,
共用同一份 `ProtocolDescriptor`(页签、默认端口、连接表单),差别只在注册时给的实现:

| 形态 | 注册 | 会话打开的是 | 例子 |
| --- | --- | --- | --- |
| 文件协议 | `Register(descriptor, IProtocolFileSystem)` | 双栏文件浏览器 | S3、WebDAV |
| 终端协议 | `Register(descriptor, IProtocolTerminal)` | 终端标签 | Telnet、串口、裸 TCP |

先看文件协议:

```csharp
context.Protocols.Register(
    new ProtocolDescriptor
    {
        Id = context.PluginId,               // 或 $"{context.PluginId}.<子协议>"
        DisplayName = "S3",
        DefaultPort = 443,
        HostLabel = "服务端点",              // 可改写主机/用户名/密码三格的标签
        Features = ProtocolFeatures.ServerSideCopy | ProtocolFeatures.AnonymousAccess,
        Fields = [ new() { Key = "region", Label = "区域", DefaultValue = "us-east-1" } ],
        Actions = [ new("share", "复制分享链接", ProtocolActionScope.File) ],
    },
    new MyFileSystem(context));
```

- **只实现 `IProtocolFileSystem` 就够了**:它的方法集与宿主内部的远程文件契约一一对应,
  因此双栏浏览器、传输队列、限速、拖放、冲突策略**全部零改动**地为你工作。
- **连接表单是声明式的**:`Fields` 描述键、标签、形态(文本/口令/布尔/整数/下拉)与默认值,
  宿主渲染控件并把用户填的值原样回传给 `ProtocolConnectRequest.Settings`。
  **插件不需要写一行连接对话框的界面代码。** 标为 `IsSecret` 的字段随口令一起加密落盘。
  字段一多就会把对话框顶出屏幕:调优类参数(分片大小、并发数之类有合理默认值、
  绝大多数人不会改的)标 `IsAdvanced`,宿主默认收进「高级选项」里折叠,
  并在页脚报出被收走的数量;编辑既有配置时,只要有高级字段的值不等于声明的默认值就自动展开。
- **互斥的字段用 `VisibleWhen` 声明,不要用一行小字解释**:

  ```csharp
  new() { Key = "masterName", Label = "主节点名", VisibleWhen = new("mode", "sentinel") },
  new() { Key = "database",   Label = "默认数据库", VisibleWhen = new("mode", ["standalone", "sentinel"]) },
  ```

  条件的形状**刻意封闭**:只有"某个键 ∈ 某个值集合"这一种判据,没有表达式、没有与或非。
  一旦引入表达式,校验、本地化与"这字段为什么不见了"的可解释性就一起失控 ——
  那就成了通用声明式 UI,而那件事蓝图 08 明确不做;真需要多条件的插件应当拆成两种连接类型。
  条件不成立时字段从表单上消失,但**已存的值照常保留并回传**(与 `IsHidden` 同一套存取语义):
  用户在哨兵模式下填过主节点名,切去独立瞄一眼再切回来,不该发现自己填的东西被界面清掉了。
  `VisibleWhen` 与 `IsAdvanced` 是**与**关系,且"条件不成立"优先 ——
  当前不适用的字段,展开高级选项也不该出现。
- **右键菜单也是声明式的**:`Actions` 在按下右键那一帧就画得出来,点击后宿主调
  `InvokeActionAsync`,你自己决定做什么(通常是开一个 `Ui` 面板)。
- **进度不必自己节流**:宿主已按 ≥100ms 收敛并做了并发乱序下的单调处理,放心每读一块就报一次。
- **限速由宿主给**:`await context.Protocols.GetTransferOptionsAsync()` 拿到全局带宽上限
  与时间戳策略(它是用户偏好,不该每个协议各配一份),在**每次传输开始时**读一次。
- **激活跑在线程池上**:宿主用 `Task.Run` 把 `onProtocol` 的惰性激活推离 UI 线程
  (装配集加载与 `ActivateAsync` 都是同步段),所以 `ActivateAsync` 里做点阻塞初始化不至于冻界面
  —— 但 10 秒激活时限照旧。
- **异常有约定**:`ProtocolAuthenticationException`(宿主重弹登录框)、
  `ProtocolCertificateTrustException`(宿主弹信任提示、记指纹后重连)、
  `ProtocolConnectionException`、`ProtocolUnsupportedException`(呈现为"功能不适用"而非失败)。
- **要在装载插件之前就出现在连接页**,须在 `plugin.json` 里同时声明:

```jsonc
{
  "contributes": { "protocols": [{ "id": "acme.storage", "displayName": "Acme", "defaultPort": 443 }] },
  "activationEvents": [ "onProtocol:acme.storage" ]   // 用户点到这个页签才装载
}
```

> **硬约束:协议能力仅 `inProcess`。** 协议是宿主反向调用插件的高频通道(含流式读),
> 隔离进程的 RPC 只承载插件→宿主方向。声明了 `contributes.protocols` 又要 `isolated`
> 的清单会在发现期被拒绝并给出原因。
>
> **协议 id 发布后不可更改** —— 它落在用户的会话配置里,改名等于让老配置认不出自己的协议。
> id 必须**全小写** `[a-z0-9.-]`、≤128 字符、等于插件 id 或以 `<插件id>.` 开头;
> 清单校验与运行期 `Register` 共用同一个判定(`PluginManifestReader.IsValidProtocolId`)。
> 强制小写是为了消灭大小写歧义 —— 这个 id 在注册表、界面、落盘配置三处被比较,
> 只要允许大写,`Foo.Bar` 与 `foo.bar` 就会在不同环节被判成"是"和"不是"同一个。

完整示例:`plugins/VelaShell.Plugin.S3`(协议 + 两个管理面板 + 22 项桶配置)。

#### 5.13.1 终端协议(`IProtocolTerminal`)

Telnet、串口、裸 TCP 这类协议没有文件系统,有的是一条**字节双工通道**。注册它:

```csharp
context.Protocols.Register(
    new ProtocolDescriptor
    {
        Id = context.PluginId,
        DisplayName = "Telnet",
        DefaultPort = 23,
        // 登录发生在带内(对端自己打印 login:):让宿主收起用户名/口令两栏。
        Features = ProtocolFeatures.AnonymousAccess | ProtocolFeatures.NoCredentials,
        Fields = [ new() { Key = "enterMode", Label = "回车键发送", Kind = ProtocolSettingKind.Choice, /* … */ } ],
    },
    new MyTerminal(context));      // IProtocolTerminal
```

实现两个接口就够了:

```csharp
Task<IProtocolTerminalSession> ConnectAsync(ProtocolConnectRequest request, ProtocolTerminalOptions options, CancellationToken ct);

// IProtocolTerminalSession
ValueTask<int>  ReadAsync(Memory<byte> buffer, CancellationToken ct);        // 0 = 会话结束
ValueTask       WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
ValueTask       ResizeAsync(int columns, int rows, CancellationToken ct);
```

- **只搬字节**:VT 解析、回滚、搜索、会话日志、会话录制、ZMODEM 全在宿主侧,
  插件多解释一层只会与宿主的终端引擎打架。
- **掉线归一化成 EOF**(`ReadAsync` 返回 0),**不要抛异常** —— 返回 0 才会走到
  "标签置为已断开、按 Enter 即重连"那条路上。宿主侧也兜了一层:插件真抛了也当 EOF。
- **写侧要自己串行化**:用户按键、尺寸上报(Telnet 的 NAWS)、协商应答都是写,
  交织会把一个帧撕成两半。读侧则由宿主的桥独占,不会并发。
- **`ResizeAsync` 是即发即忘**:宿主不等它返回(窗口缩放每帧都可能来一次);
  协议没有对应机制就实现成空操作,别抛异常。
- **`DisposeAsync` 必须能唤醒挂着的读**:宿主关标签时先取消读令牌再后台调它,
  但插件自己也不该在关闭路径上无限阻塞(串口 `Close()` 在硬件流控卡住时可以永久挂住)。
- 终端协议**没有** SessionId:SFTP 面板、任务管理器、资源监视器、隧道对它自动灰掉;
  "连接后执行命令"也不会发(Telnet 连上先看到的是 `login:`,注入命令等于打进登录提示符)。

完整示例:`plugins/VelaShell.Plugin.Telnet`(RFC 854 协商 + NAWS + 8 位透明,零第三方依赖)。

### 5.14 Workspaces —— 自带非文件型连接(Redis / MySQL / Kafka …)

协议能力(§5.13)的前提是"这个协议长得像文件系统"。**不像**的那些 —— 键值库、消息队列、
数据库 —— 用工作台能力:连接对话框、凭据加密落盘、登录弹窗、云同步、会话树与最近连接
全部复用宿主既有机制,**插件只负责交出一个 Avalonia 控件**。

```csharp
context.Workspaces.Register(
    new WorkspaceDescriptor
    {
        Id = context.PluginId,               // 或 $"{context.PluginId}.<子类型>"
        DisplayName = "Redis",
        DefaultPort = 6379,
        HostLabel = "服务地址",              // 可改写主机/用户名/密码三格的标签
        Fields = [ new() { Key = "mode", Label = "部署形态", Kind = ProtocolSettingKind.Choice, … } ],
        Features = WorkspaceFeatures.AnonymousAccess | WorkspaceFeatures.CertificateTrust,
        TrustedThumbprintSettingKey = "trustedThumbprint"   // 须是 Fields 里一个 IsHidden 字段
    },
    new MyWorkspaceProvider(context));
```

提供方在宿主打开会话时被调用,交出一个文档:

```csharp
internal sealed class MyWorkspaceProvider(IPluginContext context) : IWorkspaceProvider
{
    public async Task<IWorkspaceDocument> OpenAsync(WorkspaceConnectRequest request, CancellationToken ct)
    {
        // request.Host/Port/Username/Password 是**一次性**凭据;Settings 已按声明补齐默认值。
        MyConnection connection = await MyConnection.ConnectAsync(request, ct);   // 须在返回前连上
        return new MyDocument(connection);      // CreateView() 由宿主在 UI 线程调一次
    }
}
```

- **表单与协议能力共用同一套声明**(`ProtocolSettingField`):文本/口令/布尔/整数/下拉/
  **已保存的 SSH 配置**六种形态,调优类字段标 `IsAdvanced` 收进「高级选项」,
  只在某种形态下有意义的字段用 `VisibleWhen` 声明(见 §5.13)。
  **插件没有一行连接对话框的界面代码。**
- **「测试」按钮走插件自己的路**:插件连接的握手不是 SSH,宿主不会拿 SSH 去撞你的端口。
  它按声明分流 —— 工作台形态真开一次 `OpenAsync` 再立刻 `DisposeAsync`(隧道一并建好又拆掉),
  文件系统形态开一次会话再关掉。所以 `OpenAsync` **必须能被反复调用且不留副作用**;
  抛出的异常消息会原样出现在对话框里,写得让用户能照着改配置。
- **声明式 SSH 隧道**:连接类型声明 `WorkspaceFeatures.SshTunnel`、并给一个
  `ProtocolSettingKind.SshSession` 形态的字段,宿主就会在打开会话**之前**建好 SSH 会话
  与本地端口转发,把改写过的本地端点递给 `OpenAsync`(真实目标在
  `WorkspaceConnectRequest.Tunnel` 里,仅供界面显示来路);文档关闭时隧道自动拆除。
  **插件因此一行 SSH 代码都不用写、一次凭据都不用见** —— 建会话要走宿主既有的两步认证、
  指纹校验与 ProxyJump 链路,而"凭据永不出宿主"是硬规则。
- **提议一条连接**:`context.Workspaces.ProposeConnectionAsync(...)` 让宿主打开自己的
  「新建连接」对话框并预填(用于"插件探测到了某个服务"的场景)。
  **插件不能自己写宿主的会话库** —— 那是用户数据、凭据也在里面;它只能提议,
  由用户过一眼再按保存。提议的连接类型 id 必须属于本插件。
- **异常约定与协议能力完全一致**:`ProtocolAuthenticationException`(宿主重弹登录框)、
  `ProtocolCertificateTrustException`(弹信任提示、记指纹后重连)、`ProtocolConnectionException`、
  `ProtocolUnsupportedException`。**认证失败一定要单独认出来** —— 宿主看到它才会重弹登录框,
  而"连不上"对"密码打错了"是最无用的反馈。
- **文档生命周期**:`CreateView()` 由宿主在 UI 线程调用一次(返回 `Control`;抛异常或返回非控件
  只会让那一个标签页显示一行说明,不会带走宿主);`StatusChanged` 驱动标签页与会话树的状态圆点;
  `ReconnectAsync` 接标签页上的"重连";用户关闭标签页或插件停用时 `DisposeAsync`。
- **语言切换要重注册但必须复用同一个 provider 实例**:注册表把"同 id 换成另一个实现"视为旧实现
  失效,会通知宿主关掉该类型名下所有已打开的文档 —— 用户只是切了个语言,标签页不该全没了。
- **要在装载插件之前就出现在连接页**,须在 `plugin.json` 里同时声明:

```jsonc
{
  "contributes": { "workspaces": [{ "id": "acme.cache", "displayName": "Acme", "defaultPort": 6379 }] },
  "activationEvents": [ "onWorkspace:acme.cache" ]   // 用户点到这个页签才装载
}
```

> **硬约束:工作台能力仅 `inProcess`。** 宿主要向插件索取一个 Avalonia 控件挂进停靠区,
> 而原生控件无法跨进程嵌入(蓝图 08 已弃用 HWND 收养)。声明了 `contributes.workspaces`
> 又要 `isolated` 的清单会在发现期被拒绝并给出原因。
>
> **id 发布后不可更改**,规则与协议 id 完全相同(全小写、以插件 id 为前缀、≤128 字符),
> 两者**共用同一个判定与同一个页签条带**,因此同一份清单里工作台 id 与协议 id 也不得相撞。

完整示例:`plugins/VelaShell.Plugin.Redis`(键空间浏览器 + 类型详情 + 声明式连接表单)。

## 6. 隔离进程模式(isolated)

在 manifest 里声明 `"hostMode": "isolated"`,插件即运行在独立的
**VelaShell.PluginHost** 进程中(每插件一个进程,设计稿 02/04/05 的落地):

```jsonc
{ "id": "acme.my-plugin", ..., "hostMode": "isolated" }
```

- **插件源码零改动**:`IPluginContext` 的能力接口在 PluginHost 侧换成 RPC 代理,
  这正是 SDK 传输无关设计兑现的地方。重新声明 hostMode 即可切换。
- **传输**:命名管道(随机名 + 一次性令牌 + 仅当前用户可连;.NET 命名管道在
  macOS/Linux 底层即 Unix Domain Socket,天然跨平台)。协议为长度前缀 + JSON
  的轻量双向 RPC(详见 05 §实现注记;刻意未引入 StreamJsonRpc/MessagePack 依赖树)。
- **隔离收益**:插件崩溃/卡死只影响自己 —— 意外退出按退避序列**自动重启**
  (默认 1s→5s→30s,5 分钟窗口内超过 3 次判 Failed 放弃自愈);心跳(默认 30s)
  连续两次无应答判挂死强杀重启;宿主没了插件进程自动退场(父进程守望),绝不孤儿常驻。
- **凭据不出主进程**:插件进程只拿到管道名与令牌,SSH 密钥/密码永不跨进程。
- **能力差异**(相对进程内):

| 能力 | 隔离模式 |
| --- | --- |
| Sessions / RemoteExec / RemoteFs | ✅ RPC 代理,语义一致(传输走同机文件路径,进度经通知回流) |
| Ui(完整 Avalonia) | ✅ 插件进程内建 Avalonia,窗口全功能(默认软件渲染省内存,`VELA_PLUGIN_GPU=1` 放开);`Vela*` 主题令牌经 RPC 下发同样可用 |
| Commands / Events | ✅ 注册表在宿主,触发/事件经通知回流 |
| Storage / Log | ✅ KV 经 RPC 落宿主 SonnetDB(插件进程不落本地文件);日志转发宿主并本地 Trace 兜底 |
| Secrets / Clipboard | ✅ RPC 路由到宿主执行(机密只存宿主侧加密落盘) |
| 停靠进主窗口标签区 | ❌ **一律独立卡片窗口**(稳定,与主程序统一)。跨进程 dock 嵌入(HWND 收养)与 dock 切标签的 reparenting 有根本张力(卡顿/窗口飘出),**已弃用**;真·dock 标签页请用 inProcess。跨平台稳态方案 = 蓝图 08 共享内存表面(远期) |
| `CancellationToken` 跨进程传播 | ⚠️ 不传播;以两侧超时兜底(exec 随 `ExecOptions.Timeout`;流式 exec 未指定 `Timeout` 时补两小时死线,见 §5.5) |

- **选型建议**:第一方/可信插件用默认 `inProcess`(零 IPC 开销、面板可停靠拖拽);
  第三方或实验性插件用 `isolated`(多一个进程换崩溃隔离;面板为独立卡片窗口)。

## 7. 测试插件

引用 `VelaShell.PluginSdk.Testing`,无需宿主即可纯内存单测:

```csharp
using VelaShell.PluginSdk.Testing;

[TestMethod]
public async Task Refresh_ListsContainers()
{
    using var ctx = new TestPluginContext();
    SessionInfo session = ctx.FakeSessions.AddConnected(host: "prod-1");
    ctx.FakeRemoteExec.Handler = (_, cmd) => cmd.StartsWith("docker ps") ? "abc123 nginx" : "";

    var plugin = new DemoPlugin();
    await plugin.ActivateAsync(ctx, CancellationToken.None);
    await ctx.RecordingCommands.RunAsync("velashell.demo.refresh");

    Assert.IsTrue(ctx.CollectingLog.Entries.Any(e => e.Message.Contains("nginx")));
}
```

替身清单:`CollectingLogger`、`InMemoryStorage`、`FakeSessions`、`RecordingProtocols`
(协议注册,含与宿主一致的 id 前缀校验)、`FakeRemoteFs`
(内存路径树,语义与真实实现对齐)、`FakeRemoteExec`(脚本化应答,含退出码/标准错误与流式逐行)、
`RecordingCommands`(可 `RunAsync` 驱动命令体)、`TestHostEvents`(Raise 方法)、
`TestHostInfo`、`FakeUi`(记录面板与惰性内容工厂)、`FakeSecrets`、`FakeClipboard`。

## 8. 部署、禁用与排障

| 事项 | 位置/方法 |
| --- | --- |
| 应用自带插件 | `<应用目录>/plugins/<id 把点换成短横>/`(如 `plugins/velashell-ai/`,见 §2.1 的说明) |
| 用户手动安装插件 | `~/.velashell/plugins/<id>/`(这里不在 `.app` 内,不参与签名,故仍按 id 建目录) |
| 插件数据 | KV/机密在宿主 SonnetDB(`plugin_data` 集合);文件在 `<数据根>/plugin-data/<id>/` |
| 卸载清理 | 从 plugins/ 删除插件目录 → 下次启动自动整体清除其 DB 数据与数据目录(`.disabled` 只禁用,数据保留) |
| 禁用单个插件 | 插件目录内放一个空的 `.disabled` 文件 |
| 全局禁用(排障) | 环境变量 `VELASHELL_DISABLE_PLUGINS=1` |
| 同 id 冲突 | 按根目录顺序先到先得(应用目录优先),后者标 Invalid |
| 日志 | 调试输出/Trace,前缀 `[PluginManager]` 与 `[Plugin:<id>]` |

## 9. 性能与行为纪律

宿主是一个对内存和延迟极度敏感的终端应用,插件必须遵守:

1. **不阻塞**:`ActivateAsync` 秒回;事件处理器与命令体不做同步 I/O 等待。
2. **不轮询**:能用事件就不用定时器;确需定时任务,间隔 ≥5s 并在
   `Shutdown` 触发时立即停止。
3. **UI 只走 `Ui` 能力**:面板内容工厂由宿主在 UI 线程调用;命令体/事件处理器
   在后台线程,改控件走 `Dispatcher.UIThread`。反射宿主内部 UI 不受兼容承诺保护。
4. **内存自律**:大文件走 Download 到磁盘而非读进内存;缓存加上限;
   停用时释放一切(否则 ALC 无法回收,内存净增长)。
5. **远端友好**:探测类命令合并执行(一次 exec 多个输出段),不要每秒敲远端。

## 10. 版本与兼容承诺

- **apiLevel(当前 = 1)**:同代际内 SDK 只增不改不删(接口方法、DTO 字段、
  清单 schema)。破坏性变更才提升 apiLevel,宿主拒载更高代际的插件并给出
  明确提示。
- **minHostVersion**:插件依赖较新宿主能力时声明,旧宿主标 Incompatible
  而非运行时爆炸。
- **SDK 包版本与程序集版本**:包版本(`VelaShell.PluginSdk` 等五个包)与宿主版本**解耦**,
  按 SDK 自己的节奏发。程序集这一侧:
  - `AssemblyVersion` = `<主版本>.0.0.0`,**只随主版本动** —— 插件是编译期绑到这个标识上的,
    补丁版跟着变等于每发一次就要所有已编译插件重新绑定;
  - `FileVersion` / `InformationalVersion` 是完整版本(后者含预发布后缀),
    资源管理器属性页与 `vela-plugin` 报的都是真实版本,不会停在 1.0.0。
  - 纪律:**SDK 主版本 == apiLevel**。主版本变意味着契约破了,那一刻 `apiLevel` 同步 +1,
    老宿主于是在**发现期**就按 apiLevel 拒载并给出可读原因,而不是等装载时抛程序集绑定异常。
- **宿主模式无关**:能力接口传输无关 —— `hostMode` 在 inProcess/isolated 间切换
  无需改插件源码(已兑现);唯一例外是隔离模式下部分行为差异,见 §6 能力差异表。

## 11. 与长期蓝图的差距(有意为之)

| 蓝图能力 | v1 状态 |
| --- | --- |
| 每插件独立进程 + IPC(02/04/05) | **已实现**(`hostMode: "isolated"`,见 §6):命名管道 + 轻量 RPC + 心跳 + 崩溃退避自动重启 |
| 权限系统 + Broker(06) | 未做:v1 面向第一方/自装插件,信任即安装 |
| UI 贡献点 / VelaUI(08) | 已有:命令面板命令 + 完整 Avalonia 面板(inProcess 可停靠标签页;隔离进程一律独立卡片窗口)+ 插件管理页。VelaUI 声明式树按用户决策**不做**;跨进程 dock 嵌入弃用(见 08 注记);侧栏/状态栏挂载点待后续 |
| `.vpx` 打包 / 签名 / 商店(03/10) | **打包与签名已实现**(专属容器 + ECDSA 签名,见 §12);**商店/插件源仍显式推迟** |
| 激活事件 / 惰性激活(03) | **已实现**:`onStartup` / `onCommand:<id>` + `contributes.commands` 占位;其余事件类型(onSessionConnect/onFileOpen 等)待后续 |
| 空闲回收(04) | **已实现**(隔离模式 + `idlePolicy: "recyclable"`) |
| secrets / clipboard 能力域(07) | **已实现**(§5.10/§5.11;无权限系统,信任即安装口径) |
| protocols 能力域(07) | **已实现**(§5.13):插件可自带远程文件协议,复用宿主的浏览器/传输栈;仅 `inProcess`。首个使用者是官方 S3 插件 |
| workspaces 能力域(07) | **已实现**(§5.14):插件可自带**非文件型**连接类型,界面由插件全权渲染而连接配置/凭据/会话树复用宿主;仅 `inProcess`。首个使用者是官方 Redis 插件 |
| localFs / audio / ai 等能力域(07) | 未开口;开新能力域必须回写蓝图并只增不改 |

新增能力时的纪律:先在本文件与对应蓝图文档登记,接口进 `VelaShell.PluginSdk`
且同 apiLevel 内只增不改。

## 12. `.vpx` 包格式与签名

`.vpx` 是 VelaShell 的**专属容器格式**,不是改了后缀的 zip:通用解压工具打不开它,
宿主也拒装裸 zip。实现见 `plugin-sdk/VelaShell.PluginSdk/Packaging/VpxContainer.cs`,
读(宿主装包)与写(`vela-plugin pack`)是同一份代码,不存在"工具打得出、宿主装不上"的缝。

### 12.1 布局

小端;头部固定 64 字节:

```text
偏移  长度  内容
0     4    魔数 56 50 58 1A("VPX" + 0x1A)
4     2    容器格式版本(当前 1)
6     2    标志位(bit0 = 载荷已掩码,bit1 = 带签名块)
8     8    载荷字节数
16    32   载荷 SHA-256
48    8    掩码随机数
56    4    头部 CRC32(前 56 字节)
60    4    保留
64    N    载荷:zip 字节流(掩码开启时经变换)
64+N  4+M  可选签名块:int32 长度 + UTF-8 JSON
```

- 尾部 `0x1A` 是 DOS 文件结束符,沿用 PNG 的老办法:`type` / `cat` 误看包体时会在此停住。
- **掩码**按 32 字节分块与 `SHA-256(nonce ‖ 块号)` 异或,自反且可随机定位 ——
  于是载荷流仍然可 Seek(`ZipArchive` 读模式的硬要求),而包体里嗅不到 `PK\x03\x04`。

> 说清楚边界:**魔数与掩码是格式标识与防手滑,不是安全边界**。插件是本机可执行代码,
> 任何"解密"所需的信息都必然在客户端,认真的人照样能把载荷剥出来。真正的完整性与来源
> 保证来自 SHA-256(挡损坏与截断)与签名(挡篡改与冒名)。

### 12.2 签名

算法是 **ECDSA P-256 + SHA-256**,签的是那 64 字节头部 —— 头部内含载荷长度与摘要,
因此等同于对全包签名。刻意不用 Ed25519:BCL 里没有,引第三方库会破掉"契约程序集零重量级
依赖"这条纪律(设计文档 10 §1 原先写的是 Ed25519,已按此改)。

```bash
vela-plugin keygen -o acme.pem                    # 生成密钥对,打印公钥(Base64 SPKI)
vela-plugin pack bin/Release/net11.0 -k acme.pem  # 打包并签名
# 或在构建里:dotnet build -c Release -t:PackVpx -p:VelaSigningKey=acme.pem
vela-plugin verify pkg.vpx -k <公钥Base64>         # 校验载荷摘要与签名
vela-plugin info   pkg.vpx                        # 看头部、签名状态与清单
```

宿主侧的四档结论与处置:

| 结论 | 含义 | 宿主处置 |
| --- | --- | --- |
| `Unsigned` | 没有签名块 | 默认放行(第一方/自装插件场景,信任即安装) |
| `Trusted` | 签名有效,且公钥在信任集合内(未配置信任集合时任何有效签名都算) | 放行并记一条日志 |
| `Untrusted` | 签名有效但公钥不在信任集合内 | 默认放行;`RequireTrustedPackageSignature` 打开时拒装 |
| `Invalid` | 签名块损坏或验签失败 | **一律拒装**,不受策略宽松与否影响 —— 那是篡改,比"未签名"严重得多 |

信任集合与强制开关在 `PluginManagerOptions`(`TrustedPackageKeys` /
`RequireTrustedPackageSignature`),默认都不启用。插件源(registry)与发布者验证仍未做,
按蓝图 10 分期。

### 12.3 安装期的其它闸门

- **zip-slip**:任何解出后落在目标目录之外的条目一律拒绝。
- **解压炸弹**:条目数上限 10 000、解压后总字节上限 512 MB,且按**实际写出的字节**记账 ——
  中央目录里的长度是包自己写的,炸弹包大可以谎报 1 KB 再吐出 10 GB。
- **载荷上限**:单包载荷 512 MB,挡住损坏头部里的天文数字长度。

### 12.4 没有"裸 zip 兼容模式"

宿主**只认容器**:改了后缀的 zip 一律拒装,错误信息直接给出补救办法
(`this is a plain zip archive - repack it with vela-plugin pack`)。

刻意不留兼容开关:容器格式定型之前没有任何 `.vpx` 包发出去过,没有要照顾的存量;
而留一条"看起来像插件包就装"的旁路,等于把这个格式最主要的价值(拒绝来路不明的包体)
自己开个口子,还得一直维护两条解包路径。
