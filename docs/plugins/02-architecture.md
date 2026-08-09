# 02 · 总体架构

## 1. 进程模型

```text
┌─────────────────────────────────────────────────────────────┐
│ VelaShell 主进程(宿主)                                      │
│                                                             │
│  ┌───────────────┐  ┌──────────────┐  ┌──────────────────┐  │
│  │ PluginManager │  │ Permission   │  │ Capability       │  │
│  │ 发现/装载/卸载 │  │ Broker       │  │ Services         │  │
│  │ 生命周期/健康  │  │ 授权检查+UX  │  │ remoteFs/terminal│  │
│  └──────┬────────┘  └──────┬───────┘  │ ui/storage/...   │  │
│         │                  │          └────────┬─────────┘  │
│  ┌──────┴──────────────────┴───────────────────┴─────────┐  │
│  │ PluginConnection(每插件一条):JSON-RPC 复用器          │  │
│  └──────┬────────────────────┬───────────────────┬───────┘  │
└─────────┼────────────────────┼───────────────────┼──────────┘
     命名管道/UDS         命名管道/UDS         命名管道/UDS
          │                    │                   │
┌─────────┴────────┐ ┌─────────┴────────┐ ┌────────┴─────────┐
│ PluginHost 进程 A │ │ PluginHost 进程 B │ │ PluginHost 进程 C │
│ ┌──────────────┐ │ │                  │ │                  │
│ │ 收集式 ALC    │ │ │   (插件 B)       │ │   (插件 C)       │
│ │  插件 A 程序集│ │ │                  │ │                  │
│ └──────────────┘ │ │                  │ │                  │
└──────────────────┘ └──────────────────┘ └──────────────────┘
```

核心决策(每条的展开见对应文档):

| # | 决策 | 理由 | 详见 |
| --- | --- | --- | --- |
| D1 | **默认每插件一个 PluginHost 进程** | 插件间互隔离(G2);单插件崩溃/卡死只影响自己;卸载 = 结束进程,无程序集卸载残留风险 | 04 |
| D2 | PluginHost 内用**收集式 AssemblyLoadContext** 装插件程序集 | 开发期热重载(不重启进程换新程序集);宿主与插件的依赖版本冲突彻底解耦 | 04 |
| D3 | IPC 用 **Named Pipe(Win)/ Unix Domain Socket(mac/Linux)+ JSON-RPC 2.0(StreamJsonRpc + MessagePack 编码)** | 全托管、跨平台、双向调用与通知、成熟的取消/进度语义;不引 gRPC 的代码生成与 HTTP/2 复杂度 | 05 |
| D4 | 大块数据(文件内容、图像帧)走**侧通道**:分块流式 RPC + 共享内存(MemoryMappedFile)图像表面 | 避免大 payload 阻塞控制通道、避免 base64 膨胀 | 05 / 08 |
| D5 | **所有能力调用经主进程 Broker**,插件进程不持有任何凭据(SSH 密钥、密码、API key 永不出主进程) | 权限单点强制;凭据泄漏面最小化 | 06 / 12 |
| D6 | 插件 UI 由**宿主渲染**:声明式贡献点 + VelaUI 远程界面树,插件进程零 Avalonia 依赖 | 进程隔离下唯一不破坏一致性的 UI 方案;主题/i18n/DPI 天然统一 | 08 |
| D7 | 惰性激活:启动时只读 manifest 注册贡献点占位,**激活事件**命中才拉起进程 | 启动性能(G9);VSCode 验证过的模型 | 03 |
| D8 | 版本策略:**apiLevel(整数)+ SDK semver**,manifest 声明 `engines` 兼容区间 | 给"同级向后兼容、跨级允许破坏"一个清晰承诺 | 03 / 09 |
| D9 | 远程能力**复用宿主既有连接**(`Core.Ssh` 中立接口之上再包一层受权限约束的代理),插件不直接触碰 Tmds.Ssh | 不重复认证;库类型不泄漏(延续现有架构纪律) | 07 |

## 2. 组件与工程划分

新增工程(遵循现有 `Directory.Packages.props` 集中包管理与 `net11.0`):

```text
src/VelaShell.PluginProtocol/    -> IPC 契约:RPC 接口、DTO、错误码、apiLevel 常量。
                                    无 Avalonia 依赖,无 Tmds.Ssh 依赖;宿主与 SDK 共同引用。
src/VelaShell.PluginSdk/         -> 插件开发者引用的 NuGet 包:IPlugin 入口约定、
                                    能力代理(RemoteFs/Terminal/Ui/Storage/...)、
                                    VelaUI 虚拟树构建器、测试替身。依赖 PluginProtocol。
src/VelaShell.PluginHost/        -> 独立可执行(随主程序分发):建立 IPC、收集式 ALC
                                    装载插件、把 RPC 桥接到插件实例、心跳与自愈。
tools/vela-plugin/               -> dotnet tool:pack / sign / validate / install。
templates/velaplugin/            -> dotnet new 模板。
samples/plugins/                 -> 官方示例:image-viewer / mp3-player / auto-runner。
```

主程序侧(不新增顶层工程,按现有依赖方向落位):

```text
VelaShell.Core/Plugins/            -> 领域模型:PluginDescriptor、PermissionId、
                                      PluginState、贡献点模型(纯数据,可测)。
VelaShell.Infrastructure/Plugins/  -> PluginManager(发现/安装/装卸)、进程管理、
                                      PluginConnection(RPC 端点)、PermissionBroker、
                                      PermissionStore、能力服务实现(桥到 Core.Ssh 等)。
VelaShell.Presentation/Plugins/    -> 插件管理页 VM、授权对话框 VM、贡献点→UI 映射、
                                      VelaUI 渲染协调。
VelaShell/(App)                    -> 插件管理视图、授权对话框视图、VelaUI 渲染器控件、
                                      贡献点挂载(命令面板/菜单/状态栏/侧栏/VelaDock 文档)。
```

依赖方向(延续 architecture.md 的纪律):

```text
Infrastructure/Plugins -> Core/Plugins, Core.Ssh, PluginProtocol
Presentation/Plugins   -> Core/Plugins
PluginHost             -> PluginProtocol(不依赖任何 VelaShell.* 主程序工程)
PluginSdk              -> PluginProtocol
```

`PluginProtocol` 是隔离边界上的"细腰":它是宿主与插件世界唯一共享的
程序集,必须保持零重量级依赖、严格的 apiLevel 兼容纪律。

## 3. 一次典型调用的全链路

以 S1(图片查看器读取远程文件)为例:

```text
插件代码:await vela.RemoteFs.ReadAsync(sessionId, "/var/www/a.png")
  → SDK 代理把调用编码为 JSON-RPC 请求 "remoteFs/read"
  → PluginHost 经管道发往主进程
  → PluginConnection 收到,查路由表 → RemoteFsCapabilityService
  → PermissionBroker.Demand(pluginId, "remote.files", scope: sessionId)
      · 已授权 → 放行
      · 未授权 → UI 线程弹授权对话框,await 用户决定;拒绝则抛 PermissionDeniedException(经 RPC 回插件)
  → RemoteFsCapabilityService 经 Core.Ssh 的 ISftpClient 读文件
  → 内容以分块流(见 05 §6)回传插件;进度/取消全程可用
```

关键性质:权限检查在**主进程**完成、凭据不出主进程、数据流不经过 UI 线程。

## 4. 生命周期状态机(概览)

```text
 Discovered ──manifest 校验通过──▶ Installed(贡献点占位已注册)
    Installed ──激活事件命中──▶ Starting(拉起 PluginHost,握手)
    Starting ──Activate() 返回──▶ Active
    Active ──空闲回收/用户禁用/卸载──▶ Deactivating(调用 Deactivate,限时)
    Deactivating ──进程退出──▶ Installed / Removed
    Active/Starting ──崩溃/心跳超时──▶ Crashed ──退避重启(≤3 次)──▶ Starting
                                        └─超过阈值──▶ Faulted(UI 标红,等待用户处置)
```

完整定义见 [03-plugin-model.md](03-plugin-model.md) 与
[04-plugin-host.md](04-plugin-host.md)。

## 5. 数据与目录布局(用户机器上)

```text
<AppData>/VelaShell/plugins/
  installed/<pluginId>/<version>/     -> 解包后的插件(只读使用)
  data/<pluginId>/                    -> 插件私有数据目录(storage 能力的根)
  permissions.json                    -> 授权决定持久化(见 06)
  registry-cache/                     -> 插件源索引缓存(见 10)
  logs/<pluginId>/                    -> 插件进程 stdout/stderr 与结构化日志
```

## 6. 横切关注点

- **日志**:PluginHost 的 stdout/stderr 重定向落盘;SDK 提供 `vela.Log`
  结构化日志通道,插件管理页可查看每插件日志尾部。
- **i18n**:贡献点文案 `%key%` 间接寻址,`plugin.nls.<locale>.json` 提供
  翻译,宿主按当前语言解析后再渲染;语言切换事件推送给活跃插件。
- **遥测**:不做(与主程序现状一致);预留崩溃计数本地统计用于退避决策。
- **升级兼容**:宿主升级时按 manifest `engines` 重新校验全部已安装插件,
  不兼容者标记禁用并提示,而不是静默失败。

## 7. 开发计划(本分项)

| 任务 | 说明 | 依赖 | 估算 |
| --- | --- | --- | --- |
| A-1 | 建立 `PluginProtocol` / `PluginSdk` / `PluginHost` 三工程骨架与 slnx 接入 | — | 1d |
| A-2 | Spike:进程拉起 + StreamJsonRpc 双向调用 + 收集式 ALC 装载/卸载往返验证(Win/mac/Linux 三平台) | A-1 | 3d |
| A-3 | Spike 报告回写本目录(通道选型、卸载残留、启动耗时基线数据) | A-2 | 1d |
| A-4 | `Core/Plugins` 领域模型(Descriptor/State/PermissionId/贡献点模型)+ 单元测试 | A-1 | 2d |

> Spike 是整个计划的第一道闸门:若 ALC 卸载或跨平台管道出现硬阻塞,
> 需回到本文档修订 D2/D3 再继续。
