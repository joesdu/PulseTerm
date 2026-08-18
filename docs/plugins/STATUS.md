# 插件系统进度总览

> 更新:2026-08-13。本页是实现进度的**单一权威来源**:按蓝图分项列出 已完成 / 部分完成 /
> 未开始,并给出验收证据(测试)与建议的下一步。写插件请读 [dev-guide.md](dev-guide.md)。

## 一、总体状态

**插件系统框架层已可投产**:双宿主模式、完整能力面(9 个能力域)、UI(完整 Avalonia,
inProcess 停靠 / 隔离独立窗口)、可靠性(心跳/自愈/回收)、数据层(SonnetDB 隔离存储 +
卸载清理)、管理页(启停/卸载/.vpx 安装)、SDK 测试替身与开发文档全部就绪。
**第一个第一方业务插件已落地:AI 助手插件**(`plugins/VelaShell.Plugin.Ai`,id `velashell.ai`):
多提供商流式对话(OpenAI Responses / OpenAI Chat Completions 兼容 / Anthropic Messages 三种线协议,
覆盖 OpenAI/Grok/Ollama/中转站,自填 Base URL + API Key,Key 走 Secrets 加密)+ Agent 模式
(Microsoft.Extensions.AI `FunctionInvokingChatClient` 工具循环,工具桥接 sessions/terminal/remoteExec/remoteFs,
危险操作面板内逐条审批)。**会话持久化(2026-08-13)**:对话落插件私有时序库 —— 顶栏历史按钮
列出全部历史会话(标题/时间/条数)、点击即切回并接着聊、可单条删除或整体清空;输入框 ↑↓ 调取
此前发过的消息;`@` 唤出所选会话的远端文件选择器(目录下钻、含空格/非 ASCII 路径自动加引号),
发送时把文件内容随消息附给模型,Agent 侧另有 `list_remote_directory` / `write_remote_file`(需审批)
完成"读—搜—改"闭环。onCommand 惰性激活,五语文案。验收:VelaShell.Plugin.Ai.Tests 44 项
(工具箱审批闸门/能力桥接语义/设置与机密存取/会话历史时序读写/@ 引用语法/面板 headless 交互)。
容器管理插件未开始。

质量基线(每轮全量回归):全仓构建 0 警告 0 错误;测试 1605 项全绿,其中插件专项
114 项(Infrastructure 侧 70 项 + AI 插件 44 项;含真实双进程 e2e:跨进程激活、杀进程自愈、
空闲回收再拉起、嵌入/流式/终端/时序 RPC 链路、.vpx 装卸;AI 面板另有 headless 装载与
历史/↑↓/@ 交互测试)。

## 二、分项进度

### ✅ 已完成

| 分项 | 蓝图 | 落地形态 | 验收证据 |
| --- | --- | --- | --- |
| SDK 契约与清单 | 03/09 | `plugin-sdk/VelaShell.PluginSdk`(仅 BCL);manifest 全字段校验(20+ 坏例可读拒绝) | PluginManifestReaderTests、LazyActivationTests |
| 进程内宿主 | 02 | 可收集 ALC、故障守卫、后台激活零启动开销 | PluginManagerTests(真实 ALC e2e) |
| 隔离进程宿主 | 02/04/05 | PluginHost 进程 + 命名管道自研轻量 RPC(有意不用 StreamJsonRpc,见 05 注记);令牌握手、父进程守望、凭据不出主进程 | IsolatedPluginTests、RpcConnectionTests |
| 可靠性 | 04 | 心跳(30s×2 失败强杀)、崩溃退避自动重启(1s/5s/30s,窗口超限 Faulted)、空闲回收(recyclable) | 杀进程自愈 e2e、回收再拉起 e2e |
| 惰性激活 | 03 | `onStartup` / `onCommand:<id>` + `contributes.commands` 占位命令 | LazyActivationTests |
| UI(完整 Avalonia) | 08(改道) | VelaUI 声明式树**按用户决策不做**;插件直接用完整 Avalonia(编译期 AXAML/自带样式/i18n/第三方包),约束仅 Avalonia 版本与宿主一致(ALC 强制共享) | PluginPanelUiTests |
| 停靠/窗口双形态 | 08 | 进程内:dock 标签可拖拽分栏 + 自绘卡片窗口;隔离:独立卡片窗口(PluginHostShellWindow,与资源监视同规格) | UiApi 全路径测试 |
| 主题令牌 | 08 | `{DynamicResource Vela*}` 双模式生效(隔离经 RPC 快照下发 + 切换重推) | PluginThemeTokensTests |
| 能力域:sessions/remoteFs/remoteExec | 07 | 复用宿主连接、进度节流、Stat 缺路径返回 null 等语义纪律 | 契约级测试 + e2e |
| 能力域:commands/events | 07 | 命令面板注册(前缀强制/自动清理)、会话/主题/语言事件 | PluginCommandsApiTests |
| 能力域:storage/secrets/clipboard | 06/07 | SonnetDB `plugin_data` 单集合复合主键,**按插件强隔离**;机密 DPAPI 加密落库;卸载自动清扫(禁用≠卸载) | SonnetDbPluginDataStoreTests、清扫测试 |
| 能力域:timeSeries(私有时序库) | 07 | SonnetDB measurement,物理名 `pts_<插件命名空间>_<短名>`(命名空间由 id 派生 + 哈希兜底,插件不可指定);建表/写/查/计数/去重/删,取值全参数化,配额见 `TimeSeriesLimits`;卸载按前缀整体 drop;隔离模式经 `ts/*` 路由 | PluginTimeSeriesTests、TimeSeriesRoutingTests |
| 能力域:terminal(读/搜/授权回写) | 07 | 缓冲快照读取+正则搜索;回写经授权闸(仅本次/本会话/始终/拒绝,始终持久 SonnetDB)+ 输入串行化队列 | PluginPermissionGateTests、HelloWorldTerminalTests |
| 能力域:remoteFs 流式读取 | 07 | `OpenReadAsync` 顺序流;隔离模式 RPC 分块(openRead/streamRead/close,EOF 自动释放) | StreamingRoutingTests |
| 插件管理页 | 02/06 | 侧栏插件图标 → 自绘卡片窗口(与资源监视同规格:min/max/close+缩放):列表/状态/启停/**卸载**/**从 .vpx 安装**/撤销终端授权;Changed 自动刷新 | PluginManagerEnableDisableTests、PluginInstallUninstallTests |
| SDK 测试替身 | 09/13 | `TestPluginContext` + 全能力内存替身,插件无宿主可单测 | 被 HelloWorld 测试 dogfood |
| 示例与开发内环 | 09 | HelloWorld(AXAML 面板/双语/各能力演示);F5 自动重建镜像插件 | HelloWorldDemoPanelTests |
| 开发文档 | 09 | dev-guide.md(唯一权威);01–15 蓝图已加实现注记 | — |

### ⏳ 部分完成

| 分项 | 已有 | 缺口 |
| --- | --- | --- |
| 激活事件 | onStartup / onCommand | onSessionConnect、onFileOpen、onSchedule、onUri(蓝图 03 §4) |
| UI 挂载点 | 命令面板、停靠文档、独立窗口、插件管理页 | 侧栏视图、状态栏、设置页、右键菜单贡献点(蓝图 08) |
| 跨进程 dock 停靠 | RPC 协议层保留(EmbedRoutingTests),但**宿主 Win32 实现已移除**:跨进程窗口收养与 dock reparenting 根本冲突(卡顿/窗口飘出),**弃用** | 隔离插件一律独立卡片窗口;真·dock 标签用 inProcess;跨平台稳态 = 共享内存表面(蓝图 08 §4,远期) |
| 发布形态 | 目录即插件 + **.vpx 专属容器一键装/卸**;**SDK/工具/模板五个 NuGet 包**(见下节);**发布产物携带 `plugins/<目录名>/` 与 `VelaShell.PluginHost.*`**(前者按各插件 `<VelaPluginShip>` 取舍,示例插件不进包,目录名 = id 把点换成短横以避开 macOS codesign 的嵌套 bundle 误判;为让宿主进程在磁盘上有真实可执行体,主程序 2026-08-12 起改为摊开发布) | 插件源(registry)与发布者验证(蓝图 10 §3,分期推迟) |

### ❌ 未开始

| 分项 | 蓝图 | 说明 |
| --- | --- | --- |
| 权限系统 + Broker | 06 | **用户决策不做**(第一方/自装插件,信任即安装);若未来开放第三方生态需回访 |
| 插件商店 / 插件源 | 10 | 打包与签名已做(见下节);**商店与源索引分发**按用户决策仍推迟 |
| 能力域:localFs / audio / net / ai | 07/11 | 未开口。建议 `vela.ai` 随 AI 插件动工时定接口(terminal、timeSeries 域已完成) |
| 插件管理页 日志查看 | 02/06 | 列表/启停/撤授权已做;查看每插件日志尾部待后续 |
| SonnetDB 高阶模型开放 | — | 时序/全文/向量等暂不对插件开口,按真实需求再议(apiLevel 只增纪律) |
| 第一方业务插件 | 15 | AI 插件、容器管理插件尚未动工(框架已就绪) |

## 三、刻意的架构决策(勿"纠正")

1. **VelaUI-lite 声明式树已删除**——插件 UI = 完整 Avalonia(用户决策,历三轮收敛)。
2. **RPC 自研轻量协议**而非 StreamJsonRpc+MessagePack(零依赖纪律,05 注记)。
3. **双宿主模式共存**,manifest `hostMode` 选择;插件源码两模式零改动。
4. **插件永不直连 SonnetDB**:能力实例按插件 id 命名空间化;隔离进程一切数据走 RPC。
5. **PluginHost 默认软件渲染**(每进程省下 GPU 驱动映射的大头内存),`VELA_PLUGIN_GPU=1` 放开。
6. **隔离插件一律独立卡片窗口**(PluginHostShellWindow,与资源监视窗口同规格);inProcess 才用 dock 标签页。跨进程 dock 嵌入弃用(与 dock reparenting 根本冲突),Win32 实现已移除,仅留 RPC 协议供未来共享内存表面复用。

## 四、已知待验证项

- 终端授权对话框、插件管理窗口、隔离插件独立窗口(PluginHostShellWindow)的**观感**
  只有逻辑测试,未 F5 目视复核(对话框字段填充已有回归测试锁定;隔离面板底色已绑
  `VelaBgSurface` 令牌)。
- CI(GitHub Actions)上隔离 e2e 依赖 runner 可拉起子进程与 Avalonia Win32 平台,
  本机全绿,CI 首跑需留意。

## 五、建议的下一步(按价值排序)

1. **写第一个业务插件**(容器管理:RemoteExec + AXAML 面板即可成型)——用真实需求
   反哺框架缺口;
2. F5 真机验收观感(授权对话框 / 管理窗口 / 隔离插件独立窗口),修视觉毛刺;
3. 真机验收**打包版的隔离模式**:2026-08-12 已把主程序改为摊开发布(`plugins/` 与
   `VelaShell.PluginHost.*` 都随包交付),自更新的换版也从"移动"改为"复制"、更新器改为
   就地跑在解包目录里;三平台各走一遍"装第三方隔离插件 → 用 → 自更新换版 → 重启"的全流程;
4. 随 AI 插件定 `vela.ai` 能力域接口(蓝图 11);
5. 侧栏/状态栏挂载点(做 UI 生态前的最后一块贡献点)。

## 2026-08:协议能力域落地

- SDK 新增 `VelaShell.PluginSdk.Protocols`(apiLevel 仍为 1,纯增量):
  `IProtocolFileSystem` / `IProtocolsApi` / `ProtocolDescriptor` / 协议异常族;
  清单新增 `contributes.protocols` 与 `onProtocol:` 激活事件。
- 宿主新增 `PluginProtocolRegistry`(声明 → 注册 → 惰性激活 → 注销)与
  `PluginProtocolFileService`(把 `IProtocolFileSystem` 适配成宿主的远程文件契约,
  并承担进度节流与异常翻译)。
- 首个使用者:官方 **S3 插件**(`plugins/VelaShell.Plugin.S3`)。内建 S3 支持整体移出宿主 ——
  AWSSDK 依赖、22 项桶配置界面、149 条文案全部随插件走;`ConnectionType` 里不再有具体协议,
  只有一个 `Plugin`。详见 `docs/S3协议插件化设计.md`。
- 已知边界:协议能力仅 `inProcess`(隔离进程 RPC 尚不支持宿主→插件的请求方向)。

## 2026-08:SDK 产品化(NuGet 包 / 模板 / 专属包格式 / 调试内环)

插件 SDK 从"仓库内工程 + ProjectReference"变成真正对外分发的 SDK。

**五个 NuGet 包**(版本走 `VelaSdkVersion`,与宿主版本解耦;`AssemblyVersion` = `<主版本>.0.0.0`
只随主版本动 —— 它是插件的绑定标识,补丁版跟着变等于每次都要已编译插件重新绑定;
`FileVersion` 与 `InformationalVersion` 照常跟着涨。纪律:**SDK 主版本 == `apiLevel`**,
主版本一变就同步提 `VelaPluginApi.Level`,老宿主于是在发现期按 apiLevel 干净拒载,
而不是装载时抛绑定异常):

| 包 | 谁引用 | 内容 |
| --- | --- | --- |
| `VelaShell.PluginSdk.Build` | **插件工程(只需引这一个)** | MSBuild props/targets + 随包分发的打包器;传递引入下面两项与**精确锁死宿主版本的 Avalonia** |
| `VelaShell.PluginSdk` | 传递引入 | 契约程序集(仅 BCL) |
| `VelaShell.PluginSdk.Testing` | 插件测试工程 | `TestPluginContext` 与能力替身 |
| `VelaShell.Plugin.Cli` | dotnet tool | `vela-plugin`:validate / pack / sign / verify / info / unpack / keygen / install / dev-link |
| `VelaShell.Plugin.Templates` | dotnet new | `velaplugin`、`velaplugin-ui` |

Build 包替插件工程处理掉四件事:`EnableDynamicLoading` 与 `plugin.json` 输出、
共享程序集(`VelaShell.PluginSdk` + `Avalonia*`,口径与 `PluginAssemblyLoadContext` 一致)
不落插件目录、Avalonia 版本一致性(NU1608 升为错误 + `VELA1001` 构建期核对)、
清单编译期校验与 `dotnet build -t:PackVpx`。

**`.vpx` 改为专属容器**(`VelaShell.PluginSdk/Packaging/VpxContainer.cs`,宿主与工具同一份实现):
64 字节头部(魔数 `56 50 58 1A` + 格式版本 + 标志位 + 载荷长度 + SHA-256 + 掩码随机数 + 头部 CRC32)
+ 掩码后的 zip 载荷 + 可选 ECDSA P-256 签名尾。魔数与掩码只挡"改后缀解压",
完整性与来源靠摘要与签名;坏签名一律拒装(哪怕没开强制签名),未签名默认放行。
安装期另加解压炸弹上限(10 000 条目 / 512 MB,按**实际写出字节**记账)。
**不留裸 zip 兼容旁路**:容器定型前没有任何 `.vpx` 发出去过,没有存量要照顾,
改后缀的 zip 一律拒装并在错误里给出重新打包的办法。

**调试内环**:`plugins.dev.txt` / `VELA_PLUGIN_DEV_ROOT` 把插件工程输出目录直接挂进宿主
(管理页 DEV 角标,同 id 让位于已安装的);`VELA_PLUGIN_WAIT_DEBUGGER=<id>|*` 让隔离插件
子进程在装载程序集前等调试器,并**同步放宽激活超时、停掉心跳**(否则断点一停就被当成挂死强杀);
inProcess 侧只要 `Debugger.IsAttached` 就自动放宽激活超时。

**清单新增 `author`**(展示用,与作为信任标识的 `publisher` 分工;≤128 字符、拒控制字符),
插件管理页展示,缺省时退回 `publisher`。

**CI**:`.github/workflows/nuget.yml`,打 `sdk-v<版本>` 标签即发。发布前跑打包相关测试与
**模板端到端冒烟**(装模板 → 生成工程 → 还原 → 构建 → 出 .vpx → 核对容器 → 确认共享程序集
没泄漏进插件输出)。那一步不是形式:它先后发现了"SDK 带 `RequiresPreviewFeatures`,
插件工程不开预览开关就全线 CA2252"与"NuGet 默认不传递 build 资产,Avalonia 的 AXAML 编译器
到不了插件工程"两个只在仓库外才暴露的问题。

验收:VpxContainerTests 12 项(格式/掩码/篡改/截断/签名四态)、PluginInstallUninstallTests 9 项
(含真容器安装、裸 zip 兼容与拒收、篡改拒装、容器内 zip-slip)、DevPluginRootTests 6 项、
清单 author 校验 4 项。
