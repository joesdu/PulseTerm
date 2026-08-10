# 插件系统进度总览

> 更新:2026-08-11(二)。本页是实现进度的**单一权威来源**:按蓝图分项列出 已完成 / 部分完成 /
> 未开始,并给出验收证据(测试)与建议的下一步。写插件请读 [dev-guide.md](dev-guide.md)。

## 一、总体状态

**插件系统框架层已可投产**:双宿主模式、完整能力面(9 个能力域)、UI(完整 Avalonia,
inProcess 停靠 / 隔离独立窗口)、可靠性(心跳/自愈/回收)、数据层(SonnetDB 隔离存储 +
卸载清理)、管理页(启停/卸载/.vpx 安装)、SDK 测试替身与开发文档全部就绪。
**尚未开始写真正的第一方业务插件**(AI / 容器管理)。

质量基线(每轮全量回归):全仓构建 0 警告 0 错误;测试 ~1280 项全绿,其中插件专项
75+ 项(含真实双进程 e2e:跨进程激活、杀进程自愈、空闲回收再拉起、嵌入/流式/终端 RPC 链路、.vpx 装卸)。

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
| 发布形态 | 目录即插件 + **.vpx 包一键装/卸**(zip,zip-slip 防护);SDK 以 ProjectReference 使用 | SDK NuGet 包发布、`dotnet new` 模板、.vpx 签名/校验、发布打包脚本携带 PluginHost 产物(蓝图 09/10) |

### ❌ 未开始

| 分项 | 蓝图 | 说明 |
| --- | --- | --- |
| 权限系统 + Broker | 06 | **用户决策不做**(第一方/自装插件,信任即安装);若未来开放第三方生态需回访 |
| .vpx 签名 / 商店 | 10 | .vpx 安装/卸载已做(见管理页);**签名校验与商店分发**按用户决策仍推迟 |
| 能力域:localFs / audio / net / ai | 07/11 | 未开口。建议 `vela.ai` 随 AI 插件动工时定接口(terminal 域已完成) |
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
3. 发布打包脚本携带 `VelaShell.PluginHost.*` 与 `plugins/`;
4. 随 AI 插件定 `vela.ai` 能力域接口(蓝图 11);
5. 侧栏/状态栏挂载点(做 UI 生态前的最后一块贡献点)。
