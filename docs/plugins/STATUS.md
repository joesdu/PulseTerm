# 插件系统进度总览

> 📦 **仓库拆分注记(2026-08-21,2026-08-22 修订)**:插件 SDK、`dotnet new` 模板、
> `vela-plugin` CLI 在 [joesdu/velashell-plugin-toolchain](https://github.com/joesdu/velashell-plugin-toolchain);
> Redis / S3 / Telnet / HelloWorld 插件在
> [joesdu/velashell-plugins](https://github.com/joesdu/velashell-plugins)。
> 本页(以及 01–15 蓝图)里出现的 `plugin-sdk/…`、`tools/…`、`templates/…` 指前者,
> `tests/VelaShell.Plugin.{Redis,S3,Telnet,HelloWorld}.Tests` 与那几个插件的源码路径指后者。
>
> **例外:AI 插件(`plugins/VelaShell.Plugin.Ai`,测试在 `tests/VelaShell.Plugin.Ai.Tests`)
> 仍在主仓库**,随主程序一起构建、一起发布 —— 它与宿主是编译期耦合(借宿主的 AvaloniaEdit、
> 必须进程内装载、Avalonia 须逐字同版),理由见 [`plugins/README.md`](../../plugins/README.md)。
> 主仓库这边是 `src/`(宿主实现)、`plugins/`(AI 插件)与 `tests/`(宿主测试,
> 插件夹具在 `tests/fixtures/`)。
> 结论与验收证据本身不受影响,仅路径的仓库归属变了。

> 更新:2026-08-18(新增工作台能力域与 Redis 插件、SDK 产品化,均见文末各节)。本页是
> 实现进度的**单一权威来源**:按蓝图分项列出 已完成 / 部分完成 / 未开始,并给出验收证据
> (测试)与建议的下一步。写插件请读 [dev-guide.md](https://github.com/joesdu/velashell-plugin-toolchain/blob/main/docs/dev-guide.md)。

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
完成"读—搜—改"闭环。**边跑边补(2026-08-25)**:一轮还没答完也能接着发下一条 —— 消息排进插话
队列(输入框上方一枚可撤回的芯片),由垫在工具循环**之内**的 `SteeringChatClient` 在模型下一步
之前送进上下文,于是 Agent 正跑工具时补的"只看最近一小时的"能当场改变它后续的动作,不必按停止
重说一遍;送达后在那条回复里挂一张"你补充了"的卡,并进对话历史与库。一轮结束时还没赶上的插话:
答完了直接作为下一轮发出去,被停掉或出错了原样放回输入框。onCommand 惰性激活,五语文案。
验收:VelaShell.Plugin.Ai.Tests 44 项(工具箱审批闸门/能力桥接语义/设置与机密存取/会话历史时序读写/
@ 引用语法/面板 headless 交互),插话另有 13 项(拿真的 `FunctionInvokingChatClient` 验"下一步之前
真的进了上下文",以及排队/撤回/停止归还的面板接线)。容器管理插件未开始。

质量基线(每轮全量回归):全仓构建 0 警告 0 错误;**测试 2107 项通过**(2026-08-18,
另 79 项按环境跳过 —— 绝大多数是需要真机 Redis 的集成测试)。插件相关覆盖:
Infrastructure 侧 `TestCategory=Plugins` 97 项 + AI 插件 159 项 + S3 插件 102 项 +
Redis 插件 129 项;含真实双进程 e2e(跨进程激活、杀进程自愈、空闲回收再拉起、
嵌入/流式/终端/时序 RPC 链路、.vpx 装卸)与 `.vpx` 容器格式的地面真值断言;
AI / Redis 面板另有 headless 装载与交互测试。

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
| 示例与开发内环 | 09 | HelloWorld(AXAML 面板/双语/各能力演示);F5 自动重建镜像插件。**仓库外插件(SDK 1.4)**:宿主自登记 `host.json` → `vela-plugin dev init` 生成 IDE 启动配置(`--dev-root` / `--wait-debugger` / `--data-root` 独立调试实例);开发期插件走影子副本装载 + 管理页"重新加载" + `--dev-watch` 自动重载 | HelloWorldDemoPanelTests、DevInnerLoopTests、HostRegistryTests、VelaShellStartupArgumentsTests |
| 能力域:protocols(自带文件协议) | 07 | 声明 → 注册 → 惰性激活 → 注销;宿主的双栏浏览器/传输栈零改动复用;仅 `inProcess`。首个使用者:S3 插件 | PluginProtocolTests |
| 能力域:workspaces(自带非文件型连接) | 07 | 同一排页签、同一套声明式表单、同一条惰性激活链路;宿主向插件索取一个控件挂成停靠文档。含**声明式 SSH 隧道**与**连接提议**;仅 `inProcess`。首个使用者:Redis 插件 | PluginWorkspaceTests(25 项) |
| 开发文档 | 09 | dev-guide.md(唯一权威)+ cli.md(命令行手册)+ publishing.md(打包发布/商店)+ sdk-reference.md(SDK 参考);中英双份;01–15 蓝图已加实现注记 | — |

### ⏳ 部分完成

| 分项 | 已有 | 缺口 |
| --- | --- | --- |
| 激活事件 | onStartup / onCommand / onProtocol / onWorkspace | onSessionConnect、onFileOpen、onSchedule、onUri(蓝图 03 §4) |
| UI 挂载点 | 命令面板、停靠文档、独立窗口、插件管理页 | 侧栏视图、状态栏、设置页、右键菜单贡献点(蓝图 08) |
| 跨进程 dock 停靠 | RPC 协议层保留(EmbedRoutingTests),但**宿主 Win32 实现已移除**:跨进程窗口收养与 dock reparenting 根本冲突(卡顿/窗口飘出),**弃用** | 隔离插件一律独立卡片窗口;真·dock 标签用 inProcess;跨平台稳态 = 共享内存表面(蓝图 08 §4,远期) |
| 发布形态 | 目录即插件 + **.vpx 专属容器一键装/卸**;**SDK/工具/模板五个 NuGet 包**(见下节);**发布产物携带 `plugins/<目录名>/` 与 `VelaShell.PluginHost.*`**(前者按各插件 `<VelaPluginShip>` 取舍,示例插件不进包,目录名 = id 把点换成短横以避开 macOS codesign 的嵌套 bundle 误判;为让宿主进程在磁盘上有真实可执行体,主程序 2026-08-12 起改为摊开发布) | 插件源(registry)与发布者验证(蓝图 10 §3,分期推迟) |

### ❌ 未开始

| 分项 | 蓝图 | 说明 |
| --- | --- | --- |
| 权限系统 + Broker | 06 | **用户决策不做**(第一方/自装插件,信任即安装);若未来开放第三方生态需回访 |
| 插件商店 / 插件源 | 10 | 打包与签名已做(见下节);**商店与源索引分发**按用户决策仍推迟 |
| 能力域:localFs / audio / net / ai | 07/11 | 未开口。建议 `vela.ai` 随 AI 插件动工时定接口(terminal、timeSeries、protocols、workspaces 域已完成) |
| 插件管理页 日志查看 | 02/06 | 列表/启停/撤授权已做;查看每插件日志尾部待后续 |
| SonnetDB 高阶模型开放 | — | 时序/全文/向量等暂不对插件开口,按真实需求再议(apiLevel 只增纪律) |
| 第一方业务插件 | 15 | 容器管理插件尚未动工(框架已就绪);AI 插件与 Redis 插件已落地 |

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

1. **容器管理插件**(RemoteExec + AXAML 面板即可成型)——AI / S3 / Redis 三个插件已把
   命令、协议、工作台三条链路都跑过一遍,容器管理是纯 `RemoteExec` 场景,不需要新扩展点;
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

## 2026-08-23:装载链路提速与后台活动指示器

用户可感的问题有两条:插件装载慢(尤其首次触发惰性插件),以及**慢的时候界面上完全没有反馈** ——
一次点击看起来就像没反应。两条一起处理。

### 后台活动指示器(状态栏右下角)

- 新增 `Core/Services/BackgroundActivityService`:全局后台活动账本(开始 → 上报进度 → 释放)。
  只做登记,不做调度;结构性变化(开始/结束)立刻通知,纯进度变化按 120ms 窗口合并 ——
  紧循环里的逐文件上报不得灌爆 UI 调度器(大文件传输踩过的那个坑)。
- 新增 `Controls/CircularProgressRing`:自绘环形进度(JetBrains 风格)。不确定时定长圆弧绕圈,
  确定时从 12 点顺时针铺开;旋转由 `DispatcherTimer` 驱动,且**只在"确实在转 + 确实可见"时才跑**
  (每帧复核 `IsEffectivelyVisible`,`IsEffectivelyVisible` 在 Avalonia 12 不是 AvaloniaProperty,订阅不到)。
- 状态栏排在右侧组最左端:出现/消失时右边那几个定宽字段相对窗口右缘纹丝不动。
  悬停给出逐条明细,点击弹出清单。聚合规则:**只要有一条说不出进度,整个圆环就走不确定动画** ——
  把"不知道"按 0 混进平均值算出来的百分比是假的。
- 生产者目前是插件运行时(装载/校验/预热三种活动)。账本是通用的,云同步、SFTP 传输、
  GeoIP 下载都可以直接挂上来。

### 装载提速

1. **发现期不再做全量内容哈希**。`ValidateInstallReceipt` 要读遍每个已安装插件的每个字节,
   原先挂在 `Describe` 里,等于把启动堵在磁盘上。现在改为:
   - 发现只读清单 → 协议页签与占位命令**立刻**可用;
   - 校验转后台**并行**补做(并发度 4,磁盘密集型开多了只会互相打架),完成前 `StartAsync` 不返回,
     故"被改动过的插件已标红"这条对外契约不变;
   - **安全边界没有移动**:`EnsureActivatedAsync` 里新增一道闸,任何装载路径都必须先过校验。
     结果按插件记忆化,空闲回收后再激活不重复付这个代价。
2. **onStartup 插件并行激活**,不再串行排队让最慢的那个决定所有人的等待。
3. **清理挪到最后**:`PurgeOrphanShadows` / `PurgeUninstalledDataAsync` 与"插件能不能用"无关,
   排到后台家务链上。
4. **冷启动预读**(`PrewarmLazyPlugins`,默认开,`VELASHELL_DISABLE_PLUGIN_PREWARM=1` 急停):
   主窗口首帧画完 + 5 秒后,把惰性等待中的插件目录里的顶层 dll 顺序读一遍,
   只为抬进操作系统文件缓存。**不装载程序集、不创建 ALC、不跑 `ActivateAsync`** ——
   惰性激活的语义分毫不动,内存零增长(读进去的是内核页缓存)。刚被校验读过整个目录的插件直接跳过,
   已安装插件的预读实际上是免费搭了那遍哈希的车。

> 明确**没有**做的:隔离插件的宿主进程预热池。隔离模式启动慢的大头是 .NET 运行时 + Avalonia
> 初始化(300~800ms),要省掉它得把 PluginHost 的插件身份传递从环境变量改成握手后 RPC 下发,
> 协议改动面大,留作下一轮。

验收:`TestCategory=Plugins` 下新增 `PluginLoadingPipelineTests` 5 项(装载在账本上留痕且必定归零、
失败路径同样归零、多个 onStartup 插件并行后全部激活、预读只读文件绝不激活、关掉预读则账本无痕);
`BackgroundActivityServiceTests` 8 项(含并发 64 条活动后账本归零 —— 圆环不得一直转);
`StatusBarBackgroundActivityTests` 6 项(聚合规则);UI 侧 `StatusBar_BackgroundRing_*` 1 项
(收起 → 出现 → 确定弧 → 收起)。

## 2026-08-23(二):后台活动指示器接入更多生产者,构建卫生收口

圆环落地之后的第一轮"还本":把它接到用户真正会等的地方,并把镜像插件的构建目标改成
真正的镜像语义。

### 插件安装接入圆环

`InstallFromVpxAsync` 全程是纯等待的几秒 —— 验签 → 解压 → 对整个目录算 SHA256 落安装凭据
——此前界面上一动不动(`PluginManagerViewModel` 里只有一个"完了给条通知")。现在按阶段上报:
包文件名先做副标题,清单读出来后换成插件显示名。进度是粗刻度而非平滑曲线:每一步内部
都拿不到细粒度回调,与其编一个平滑的假进度,不如让弧一段一段地跳 —— 它至少每一跳都对应
真的完成了一件事。

> 注:插件商店(market.easilynet.top)是浏览器里开的链接,下载不在应用内 ——
> 慢的那几秒全在本地的验签/解压/哈希上。

**没有做的一件事**:装完随即激活会把 `SaveInstallReceiptAsync` 刚算过的目录哈希再算一遍,
看着是浪费。但不要预置校验结果去省它 —— 那等于把"落凭据 → 首次装载"之间的窗口排除在
校验之外,而省下的只是一次热缓存目录的哈希(几十毫秒)。这个仓库对插件信任面的态度一贯是
宁可多算一遍。代码里留了注释挡住下一个想"顺手优化"的人。

### 云同步接入圆环

`GistSyncService` 的四条出口(立即同步 / 仅推送 / 仅拉取 / 恢复到此版本)各开一条不确定态
活动,副标题直接复用设置页上同名按钮的文案(`SetSync_*`,五语已有,零新增)——
用户看到的措辞与他点下去的那个按钮一致。走不确定态是因为整段是网络往返,给不出有意义的
百分比,而这条活动要回答的问题本来也只有一个:"现在到底有没有在同步"。

自动同步一向静默(启动拉取、保存后防抖推送,失败都不打扰用户)。**"不打扰"不该等于
"什么都看不见"**:用户的连接配置正在被上传或覆盖,这件事值得有个去处。

### 构建卫生:镜像插件的目标改成真正的镜像

`CopyVelaPluginsToOutput`(把 `artifacts/plugins/` 暂存目录铺进宿主输出)此前只增不减,
且复制出去的文件没登记进 MSBuild 的清理台账。两个后果都真实咬过人:

- 从暂存目录删掉一个插件,输出里那份能一直留到手动删为止;
- `dotnet clean` 清不掉它们(台账上没名字),表现是"clean 完插件还在"。

改动:目标从 `AfterTargets="Build"` 挪到 `AfterTargets="CopyFilesToOutputDirectory"`
(赶在 `IncrementalClean` 之前),并把复制产物登记进 `FileWrites`。于是剪枝与 clean 都由
MSBuild 自己的增量清理机制免费提供。另加一道扫尾:`IncrementalClean` 剪的是文件,剪空的
目录会原地留下来 —— 判据用"没有 `plugin.json`"(对宿主而言与空目录等价)把它们一并清掉,
免得 `ls plugins` 看到一个早该消失的插件名。

只登记本目标复制的文件:自建插件(`velashell-ai`)由插件工程自己铺进同一个 `plugins/`,
不在这份台账里,因此绝不会被误剪 —— 这一条已实测。

验收(四个场景实测):暂存目录 → 输出的镜像;从暂存目录删掉插件后重建即从输出剪掉;
剪空的目录被扫掉;`dotnet clean` 能清动镜像出去的插件且不碰 `velashell-ai`。
新增回归:`InstallFromVpx_ReportsProgressToTheBackgroundLedger_AndClearsIt`、
`InstallFromVpx_WhenRejected_StillClearsTheBackgroundLedger`、
`SyncEntryPoints_ReportToTheBackgroundLedger_AndAlwaysClearIt`(四条出口各自可辨且必收干净)。

### 复核过、结论是暂不做的

- **隔离插件的宿主进程预热池**:实测一次完整激活周期约 1 秒(进程拉起 + Avalonia 初始化
  占大头),数字是真的。但当前 `velashell.ai` 与 `velashell.redis` 都必须 `inProcess`
  (前者借宿主的 AvaloniaEdit,后者要向宿主交 Avalonia 控件挂进停靠区),**一个隔离插件都没有**
  —— 这个池子今天谁也帮不上。等生态里真出现第三方隔离插件再做。
- **ReadyToRun**:发布是 `-p:SelfContained=true` 且**没有** `PublishReadyToRun`,
  主程序与 `VelaShell.PluginHost` 到用户机器上全靠 JIT 冷启。开了是所有人每次启动都受益,
  且能顺带吃掉一部分隔离宿主的冷启动、不用动 IPC 协议。代价是 R2R'd 程序集大 30~50%
  (self-contained 下包体增长可观)。**收益必须实测**,单独开一轮:改配置 → 三平台各测冷启动
  → 拿数据决定包体值不值。

## 2026-08-23(三):ReadyToRun 开启(附实测)

`src/VelaShell/VelaShell.csproj` 的 Release 属性组加一句:

```xml
<PublishReadyToRun Condition="'$(RuntimeIdentifier)' != ''">true</PublishReadyToRun>
```

**只在给了 RID 时打开**:R2R 必须按目标运行时编译,没有 RID 的 publish 会直接报 NETSDK1095。
发布流水线三个平台都传 `-r`(且各自跑在对应 OS 的 runner 上 —— 跨架构可以,跨 OS 不行),
本机不带 RID 的 publish 则安静地退回普通发布。插件工程不受影响:它们的 ProjectReference
早就在 `UndefineProperties` 里摘掉了 `PublishReadyToRun`。

不开 `PublishReadyToRunComposite`:复合模式启动更快,但把整个自包含运行时熔成一个巨大镜像,
包体与构建时间的涨幅都不是这一轮想付的。

### 实测(win-x64,自包含)

| | 基线 | 开 R2R | 变化 |
|---|---|---|---|
| 发布目录总大小 | 277 MB | 318 MB | **+41 MB(+15%)** |
| `VelaShell.dll` | 4.2 MB | 9.3 MB | ×2.2 |
| `VelaShell.PluginHost.dll` | 61 KB | 184 KB | ×3.0 |
| PluginHost 启动 + Avalonia 初始化(中位) | 387 ms | **355 ms** | −32 ms(−8%) |

包体只涨 15% 而不是单个程序集的 2~3 倍,是因为 277 MB 里绝大部分是自包含运行时,
而运行时包里的框架程序集**本来就是 R2R 的** —— 涨的只有本仓库自己的程序集与
Avalonia 这类第三方库。

启动那一栏的测法要说清楚:给 PluginHost 喂一个不存在的管道名,让它走完
"运行时引导 → `AppBuilder.SetupWithoutStarting()` → 10 秒连接超时 → 退出",
总耗时扣掉那个固定的 10 秒。除了中位数变快,**尾部抖动收敛得更明显**
(基线 373–1348 ms,开启后 348–385 ms)。

> 走过一次弯路,记下来免得再踩:第一次测的是"不给任何环境变量直接跑 PluginHost",
> 结果 R2R 反而慢 52 ms。原因是那条路径上 `Require()` 立刻抛异常退出,**根本没走到 JIT** ——
> 只吃到了 R2R 把文件撑大的代价,吃不到收益。测 R2R 必须让被测路径真的执行到有量的代码。

### 尚未验证

主程序自身的冷启动**没有实测**:它是 GUI 应用,没有"不开窗就退出"的启动路径,
量它必须真的把窗口拉起来。按理说主程序受益应当明显大于 PluginHost
(ReactiveUI、AvaloniaEdit、SonnetDB、整个 Shell 的代码量都在它这边),但这是推断不是数据。
同理,R2R 产物"能正常跑"目前只有 PluginHost 那一侧的旁证(它确实起来了、初始化了 Avalonia、
等到超时才退)。**发版前需要在三平台各拉起一次主程序**,确认 R2R + Avalonia + 插件 ALC 组合无恙。
