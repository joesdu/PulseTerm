# Redis 完整界面客户端:插件化调研与设计

> 编写日期：2026-08-17　基准代码：`main` @ `6daaf1a`
>
> 前置阅读：[`plugins/dev-guide.md`](plugins/dev-guide.md)（能力面与纪律）、
> [`S3协议插件化设计.md`](S3协议插件化设计.md)（第一个协议插件的取舍与教训）、
> [`../DESIGN.md`](../DESIGN.md)（设计令牌与组件规范）。
>
> 本文回答三个问题：**能不能以插件形态做一个完整的 Redis 界面客户端**；
> **要为此在宿主上开几个口子**；**界面怎么设计才配得上"键盘优先、信息密度高"这句话**。
> 第六节（界面设计）是重点，其余各节为它服务。

---

## 一、结论先行

**能做，而且应该做成插件。**但**不能照抄 S3 那条路** —— S3 插件走的是"协议能力域"
（`IProtocolFileSystem`），它的前提是"这个协议长得像文件系统"。Redis 不是：它没有目录、
没有文件、键空间是平的字节串到多种数据结构的映射，值有六种以上原生形态，
运维真正要看的东西（TTL、编码、内存占用、慢日志、连接数、复制状态）在文件模型里根本无处安放。
硬塞进双栏文件浏览器只会得到一个"能看不能用"的假客户端。

因此结论是三段：

| # | 结论 |
|---|---|
| 1 | **宿主开一个新贡献点:「工作台连接类型」**(`contributes.workspaces`)。它与现有协议贡献共用连接对话框、声明式表单、凭据加密、惰性激活的**全套既有机制**,唯一差别是打开会话时宿主问插件要一个 `Control` 挂成停靠文档,而不是打开双栏文件浏览器。改动面小(见 §三),换来的是 Redis 连接与 SSH/SFTP **同为一等公民**:进会话树、进最近连接、进命令面板、随 Gist 云同步、密码走 AES 加密落盘。 |
| 2 | **宿主再开一个「声明式 SSH 隧道」**:连接表单里选一条已保存的 SSH 配置,宿主负责建 SSH 会话 + 本地端口转发,把**改写过的本地端点**递给插件。插件因此一行 SSH 代码都不用写、一次凭据都不用见 —— 而"运维顺手查一下线上 Redis"这个最高频场景,从"手工开隧道再填 127.0.0.1"变成"选一条跳板机"。这个口子一旦开了,后面的 MySQL / PostgreSQL / MongoDB / Kafka 插件全部白得。 |
| 3 | **客户端库选 StackExchange.Redis。**调研阶段本文倾向自研 RESP(理由见 §四),**用户决策改为用库**:出成果的速度优先于那些边界上的完整性。已落地并验证;库带来的两处硬边界(`MONITOR`、阻塞命令)与实测踩到的坑记在 §四.1,不许当成疏忽。 |

**"完整"这个词在 Redis 上的正确定义**：S3 有 116 个操作，界面没覆盖到的操作就是**用户不可达**的，
所以那篇设计要逐项数覆盖率。Redis 约 240 条顶层命令（含子命令约 460 条），但客户端内置一个
**真·CLI 控制台**之后，**每一条命令天然可达** —— 界面的职责不再是"把 240 条都画出来"
（那是把 redis-cli 的 man 页画成按钮，没有价值），而是让**最高频的那 80 条零打字**、
让危险的那 20 条不打偏。这是本设计与"功能清单式"设计的根本分歧，也是第六节所有决定的出发点。

**工作量**：宿主侧约 3–5 天（两个贡献点 + 表单形态 + 测试）；插件侧约 10–11k 行、
四个里程碑（与 AI 助手插件 11.3k 行、S3 插件 7.4k 行同量级）。逐项见 §十一。

---

## 二、为什么是插件,以及为什么不是"协议插件"

### 2.1 为什么是插件

与 S3 同一套账，但更划算：

- **不用 Redis 的用户零成本**：Redis 面板、RESP 引擎、命令元数据、约 250 条文案全部随插件目录走；
  不打开 Redis 页签就一行代码不装载（`onWorkspace:` 惰性激活）。
- **迭代解耦**：Redis 生态自己在动（7.4 的哈希字段过期、8.0 起捆绑 JSON/Search/TimeSeries/Bloom
  模块、Valkey 分叉）。这些变化不该逼宿主发版。
- **它是"第二个业务插件"的最佳候选**：`docs/plugins/15-ecosystem-ideas.md` §1 把"数据库客户端"
  列在第一梯队且标注复杂度"高"。它比容器管理插件更能反哺框架缺口 ——
  容器管理只用 `RemoteExec`(现成能力),而 Redis 会一次性逼出"非文件型连接类型"与"隧道"
  这两个真正缺的扩展点,这些扩展点是**所有**后续数据库/中间件插件的公共前提。

### 2.2 为什么不能用协议能力域

把 Redis 塞进 `IProtocolFileSystem` 需要一串谎：

| 文件模型要求 | Redis 的真相 | 硬映射的后果 |
|---|---|---|
| 目录树 | 键名里的 `:` 只是普通字符（与 S3 的 `/` 同构，这一点倒是能对上） | 尚可 |
| 一个条目 = 一份字节流 | 值可能是 hash / list / set / zset / stream / JSON —— **不是字节流** | 双击一个 hash "下载"下来是什么？ |
| 大小、修改时间、权限 | 没有 mtime，没有权限位；有的是 **TTL、编码、内存占用**（文件模型无处安放） | 列名说谎（S3 那篇 §3.1 专门讲过"不拿存储类别去填属组"） |
| 读写 = 上传下载 | 写是 `HSET field value`、`ZADD score member`、`XADD` …**带结构语义** | 传输队列毫无意义 |
| 列目录 = 一次调用 | `SCAN` 是**游标式、不保证完整、可能空转多轮** | 进度条无从画，"目录列完了"是个谎 |

再往下还有更硬的：Redis 客户端的一半价值在**服务端状态**（INFO / SLOWLOG / CLIENT LIST /
内存分析 / Pub-Sub / MONITOR），这些东西在文件浏览器里没有任何落点。

**所以要的不是"再一种协议",而是"一种由插件全权渲染的会话文档"。**

---

## 三、宿主要开的三个口子

三处都是**纯增量**（apiLevel 仍为 1，与"同代际只增不改"纪律相容），且刻意复用既有机制。

### 3.1 口子一:工作台连接类型(`contributes.workspaces`)

清单声明（发现期即进连接对话框，**不碰程序集**，与协议贡献同一条路径）：

```jsonc
{
  "id": "velashell.redis",
  "contributes": {
    "workspaces": [
      { "id": "velashell.redis", "displayName": "Redis", "defaultPort": 6379 }
    ]
  },
  "activationEvents": [ "onWorkspace:velashell.redis" ]
  // hostMode 必须是 inProcess:宿主要向插件索取一个 Avalonia Control 挂进停靠区,
  // 原生控件无法跨进程嵌入(蓝图 08 已弃用 HWND 收养)。校验期直接拒绝 isolated,
  // 与协议贡献同样的口径、同样的理由。
}
```

SDK 侧（新增三个类型 + 一个能力域，**全部复用协议侧已有的字段声明与异常族**）：

```csharp
namespace VelaShell.PluginSdk.Workspaces;

/// <summary>一种由插件全权渲染的会话文档类型(Redis、MySQL、Kafka…)。</summary>
public sealed record WorkspaceDescriptor
{
    public required string Id { get; init; }              // = 插件 id 或以其为前缀,全小写,发布后不可改
    public required string DisplayName { get; init; }
    public int DefaultPort { get; init; }
    public string? HostLabel { get; init; }
    public string? UsernameLabel { get; init; }           // Redis:"ACL 用户名(留空为 default)"
    public string? PasswordLabel { get; init; }
    public IReadOnlyList<ProtocolSettingField> Fields { get; init; } = [];   // ← 复用协议侧的声明式表单
    public WorkspaceFeatures Features { get; init; }      // AnonymousAccess | CertificateTrust | SshTunnel
    public string? TrustedThumbprintSettingKey { get; init; }
}

/// <summary>宿主打开一条工作台会话时的请求。字段与 ProtocolConnectRequest 同构(凭据一次性)。</summary>
public sealed record WorkspaceConnectRequest
{
    public required string SessionId { get; init; }
    public required string Host { get; init; }            // 走隧道时这里已是本地转发端点
    public required int Port { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public IReadOnlyDictionary<string, string> Settings { get; init; } = ...;
    public string DisplayName { get; init; } = "";
    public WorkspaceTunnelInfo? Tunnel { get; init; }     // 非 null 表示宿主已代为建隧道(见 3.2)
}

/// <summary>一条已打开的工作台文档:插件持有连接与界面,宿主只管标签页与状态呈现。</summary>
public interface IWorkspaceDocument : IAsyncDisposable
{
    object CreateView();                                  // UI 线程调用,返回 Avalonia Control
    ProtocolSessionState State { get; }                   // ← 复用协议侧的状态枚举
    event EventHandler<WorkspaceStatus>? StatusChanged;   // 状态 + 一行状态栏文案 + 可选延迟(ms)
    Task ReconnectAsync(CancellationToken ct);            // 宿主的"重连"按钮
}

public interface IWorkspaceProvider
{
    Task<IWorkspaceDocument> OpenAsync(WorkspaceConnectRequest request, CancellationToken ct);
}

public interface IWorkspacesApi
{
    IDisposable Register(WorkspaceDescriptor descriptor, IWorkspaceProvider provider);
}
```

宿主侧改动（逐处，可数得清）：

| 位置 | 改动 |
|---|---|
| `PluginManifestReader` | 认 `contributes.workspaces` 与 `onWorkspace:` 事件；`isolated` + workspaces 组合拒绝 |
| `PluginProtocolRegistry` | 泛化成"连接类型注册表"：条目带 `Kind`（`FileSystem` / `Workspace`）。声明→注册→惰性激活→注销的两段式**原样复用** |
| `ConnectionProfileViewModel` / `ConnectionProfileView` | 页签来源改为合并后的注册表；表单渲染器零改动（同一套 `ProtocolSettingField`） |
| `MainWindowViewModel.OpenPluginDocumentForProfileAsync` | 按 `Kind` 分岔：`FileSystem` → 现有 `SftpDocument`；`Workspace` → 新的 `PluginWorkspaceDocument`（`IDockViewProvider` 包装插件的 `CreateView()`，标题=会话名，状态点绑 `StatusChanged`） |
| `SessionTreeNodeViewModel` | 图标/双击语义按 `Kind` 取（`IsDeclared` 是同步查询，不会因此装载插件） |
| `ConnectionType` | **不动**。仍是 `Plugin = 4`,`PluginProtocolId` 继续做区分键 —— "是文件协议还是工作台"属于**插件的声明**,不该写进用户配置里；用户那条配置只记"我用的是 `velashell.redis`"。插件卸载后两种类型都退化为同一句"提供该连接类型的插件不可用"，无需区分 |

> **为什么不新加一个 `ConnectionType.PluginWorkspace = 5`**:那会把同一件事(插件提供的连接)
> 拆成两个枚举值,而两者的存储形状、凭据处理、惰性激活完全一致;更糟的是它把 `Kind` 这个
> **插件侧的属性**冻进了用户的落盘配置 —— 插件哪天把一种连接从文件型改成工作台型(完全合法的演进),
> 老配置就会被读成另一种东西。这与 3 号退役值的教训是同一条。

### 3.2 口子二:声明式 SSH 隧道(`WorkspaceFeatures.SshTunnel`)

**这是整份设计里性价比最高的一处。** 线上 Redis 几乎从不裸露公网，运维的真实路径是
"SSH 到跳板机 → 再连 Redis"。现状下用户得：手工开一条隧道 → 记住本地端口 → 在 Redis 连接里填
`127.0.0.1:<那个端口>` → 关掉隧道时 Redis 连接莫名失效。四步全是人肉状态同步。

声明后，宿主在连接表单里自动追加一节（**插件不写一行界面代码**）：

```
┌ 经 SSH 隧道(可选) ─────────────────────────────────┐
│ 跳板会话   [ bastion-01 (10.0.0.9)            ▾ ] │  ← 已保存的 SSH 配置下拉
│ 目标地址   [ 10.0.3.12          ] : [ 6379 ]      │  ← 留空则用上面的主机/端口
└────────────────────────────────────────────────────┘
```

打开会话时宿主的动作：解析跳板配置 → 复用或建立 SSH 会话（**走宿主既有的两步认证、
指纹校验、ProxyJump 链路**）→ `ITunnelService.CreateLocalForwardAsync` 申请一个临时本地端口 →
把 `WorkspaceConnectRequest.Host/Port` 改写成 `127.0.0.1:<临时端口>`，并在 `Tunnel` 里附上
真实目标（仅供界面显示"↝ bastion-01"）→ 文档关闭或 SSH 断开时隧道随之拆除。

需要的 SDK 增量只有一个字段形态：

```csharp
public enum ProtocolSettingKind { Text, Password, Boolean, Integer, Choice, /* 新增 */ SshSession }
```

`SshSession` 形态的字段落盘的是**会话配置 id**，宿主渲染成"已保存的 SSH 配置"下拉。
它对协议插件（S3/WebDAV）同样有用，属于纯加法。

> **为什么不把 `ITunnelService` 直接开给插件**:那样插件就得处理"选哪条 SSH 配置、没连上怎么办、
> 要不要弹两步认证、指纹变了怎么办"这一整套 —— 而这些全是宿主的核心资产,凭据更是明令
> **永不出宿主**。让插件只接受"一个已经能连的本地端点",信任面最小、代码最少,
> 而且未来每个数据库插件都不必重写一遍。**方向对了,工作量才对。**

### 3.3 口子三:两个小挂载点(可选,不阻塞 v1)

- **状态栏贡献**：把"ops/sec · 内存 · 命中率"放进宿主状态栏（现有缺口，蓝图 08 已列）。
  v1 用文档内自绘的状态条顶着，视觉上区别不大。
- **`onSessionConnect` 激活事件**：让插件在用户连上某台机器时被唤起（用于 §7.1 的"探测远端 Redis"）。
  v1 可用命令面板命令代替（用户主动触发），体验只差一层。

---

## 四、客户端引擎:自研 RESP 还是 StackExchange.Redis

S3 那篇留下的教训是"**依赖取舍必须先量清楚协议面,再谈两百行 vs 一个包**"。
把同一把尺子量到 Redis 上，结论**与 S3 相反**——因为量的是协议面的**形状**，不是条数：

| 维度 | S3(116 个操作) | Redis(约 240 条命令) |
|---|---|---|
| 每个操作的编解码 | **各不相同**：每个操作一套 XML 请求/响应 + 专属错误码 | **完全一致**：一切都是 bulk string 数组进、五种（RESP2）/十四种（RESP3）类型出 |
| 新增一个操作的边际成本 | 一套序列化 + 一套错误映射 | **≈ 0**（多一行命令名） |
| SDK 替你写掉的量 | 数千行搬运代码 | 几乎没有；它替你写的是**连接管理**，不是编解码 |
| SDK 挡住你的东西 | 无 | 见下 |

StackExchange.Redis（MIT，许可相容）真正提供的是**多路复用连接池**——而这恰好是
GUI 客户端**最不需要、且会被它挡住**的东西：

1. **阻塞类命令明确不支持**。官方文档写得很直白：多路复用下 `BLPOP`/`BRPOP`/`BRPOPLPUSH`
   会卡死整个复用器，因此库不提供。但一个自称完整的客户端，控制台里用户敲 `BLPOP q 5`
   必须能跑。
2. **`MONITOR` 无对应 API**。它要求连接进入"只吐流、不再应答"的状态，与复用模型天然冲突。
   而 `MONITOR` 是排障场景的刚需。
3. **控制台需要"粘连接状态"**。`SELECT` / `MULTI`-`EXEC` / `WATCH` / `SUBSCRIBE` / `CLIENT NO-TOUCH`
   全是**连接级**状态。多路复用会把用户的命令派到任意物理连接上——用户敲完 `MULTI` 再敲 `EXEC`，
   两条可能不在同一根 socket 上。这不是"不方便"，是**错**。
4. **RESP 保真度**。控制台要像 redis-cli 一样如实呈现回复类型（`(integer) 3` / `(nil)` /
   `(empty array)` / `1) 1) "a"` 的嵌套缩进 / RESP3 的 map 与 set）。经一层
   `RedisResult` 抽象后再还原，是拿掉信息再猜回来。
5. **它会替你多说话**。连接握手时的 `CONFIG GET` / `CLIENT SETINFO` 之类，在禁用了
   `CONFIG`/`CLIENT` 的托管 Redis 上会变成"连不上"或一串警告——而这些实例恰是运维最常连的。

**自研的实际体量**（一次性，之后近乎零维护）：

| 组件 | 行数 | 说明 |
|---|---|---|
| RESP2/RESP3 读写器 | ~450 | 基于 `System.IO.Pipelines`；14 种类型 + 属性/推送帧 |
| 连接（TCP/TLS/AUTH/HELLO、独占命令轮、流水线） | ~500 | 一条 socket 一个 `SemaphoreSlim(1)`，不做复用 |
| 拓扑（CRC16 槽位、`MOVED`/`ASK`、`CLUSTER SHARDS`、Sentinel 发现与 `+switch-master`） | ~550 | CRC16 表 30 行；集群路由是纯算术 |
| 命令元数据（`COMMAND DOCS`/`COMMAND INFO` 缓存 + 低版本兜底表） | ~250 | 见 §6.4 —— 这份数据**来自服务端**，顺带解决了自研的"命令表要自己维护"疑虑 |
| 合计 | **~1750** | 零第三方依赖，插件包不增一个字节 |

**取舍与代价**（写下来，免得将来当成疏忽）：重连退避、集群拓扑刷新、Sentinel 故障切换
这些"库替你想过的边界"要自己想周全，并**靠环回服务器把它们钉住**（§十）。
但 GUI 客户端的并发模型比服务端应用简单得多——**没有争抢**：一个面板同时在飞的命令
个位数，且用户在等结果。这正是"自研的风险面"最小的那类场景。

> 与 §二的判断合起来看：S3 选 SDK、Redis（按调研结论）倾向自研，两个结论用的是同一条准则
> ——**协议面的形状决定依赖取舍**，而不是"能自己写就自己写"或"有库就用库"。

### 4.1 实际决策：用 StackExchange.Redis（2026-08-17，已落地）

**用户拍板改用库**，理由是"自研过于时间长，用库可以直接出成果"。这是一次明确的
速度换完整性的取舍，判断权在用户手上。落地后的实测账目如下（`StackExchange.Redis 3.1.13`，MIT）：

| 项 | 实际结果 |
|---|---|
| 交付速度 | 连接 / 探测 / 游标扫描 / 分页取值全部走通，插件侧约 2.4k 行（自研估算 1.75k 行只是编解码那一层，不含它自己要写的测试） |
| 包体 | 插件目录 2.5 MB：`StackExchange.Redis.dll` 2.0 MB + `RESPite.dll` 110 KB + `System.IO.Hashing.dll` 55 KB + 插件本体 165 KB。按需分发，不用 Redis 的用户不付这笔 |
| `MONITOR` | **确认不可用**：库在多路复用模型下不提供。控制台里将如实拒绝并说明原因，不假装能跑 |
| 阻塞命令（`BLPOP` 等） | **确认不可用**（官方文档明示）。同上，如实拒绝 |
| 控制台的粘连接状态（`MULTI`/`WATCH`/`SELECT`） | 仍是未解问题：多路复用会把命令派到任意物理连接。控制台落地时必须正面处理，可选路径见下 |
| `INFO`/`CONFIG`/`DBSIZE` | 需 `AllowAdmin = true`。危险命令仍由插件自己的护栏拦（依据 `COMMAND INFO` 的 flags），而不是靠库的这个总闸 |
| RESP3 | `Protocol = RedisProtocol.Resp3`，低版本自动回落；实际协商到的协议由裸 `HELLO` 探测后记在状态条上 |

**控制台落地时的三条可选路径**（届时择一，不要含糊过去）：
① 只对无状态命令用库的 `Execute`，`MULTI`/`SUBSCRIBE`/`MONITOR` 另开一条裸 socket；
② 全部走裸 socket（等于把自研 RESP 补回来，但只补控制台这一处，量小得多）；
③ 如实标注这几类命令不支持。**倾向 ①** —— 它把库的价值留在数据面，把库挡住的东西
限制在一条独立连接上。

### 4.2 实测踩到的坑（Redis 3.0.504 真机）

本机恰好是一台 Redis 3.0，于是把降级路径全打了一遍，得到两条真实收获：

1. **`SCAN … TYPE` 是 6.0 才有的,老服务器回 `ERR syntax error`。** 第一版实现"退回不带
   TYPE 的扫描"就把结果交出去了 —— 于是界面会**一边显示"类型:hash"一边列出所有类型的键**。
   这是最坏的一种坏：界面在说谎，而用户没有任何线索。修法是能力探测（一次语法错误就是
   确定答案）+ 客户端批量 `TYPE` 补上过滤：代价是老服务器上每页多一个往返，
   换来"要 hash 就只给 hash"在任何版本上都成立。由 `ScanAsync_WithTypeFilter_OnlyReturnsThatType`
   钉住 —— 而这条测试正是在 3.0 上才会真的走进客户端收窄那条分支。
2. **`MEMORY USAGE` 是 4.0 才有的。** 取不到时必须报 `-1`（未知）而**不能是 0** ——
   0 会被界面读成"不占内存"。这正是 §6.8"空状态不是错误"那条在数值上的体现。

> 教训与 S3 那篇同源：**降级路径只有在真的降级过的机器上才算验证过。** 一台老服务器
> 比十条"理论上会回落"的注释更有用。

---

## 五、连接、拓扑与"连接分工"

### 5.1 连接表单(声明式,宿主渲染)

| 字段 | 形态 | 默认 | 高级 | 说明 |
|---|---|---|---|---|
| 主机 / 端口 | 宿主内建 | `6379` | | `HostLabel = "服务地址"` |
| 用户名 | 宿主内建 | 空 | | `UsernameLabel = "ACL 用户(留空=default)"` |
| 密码 | 宿主内建 | 空 | | AES 加密落盘，走"记住密码"既有开关 |
| 部署形态 | Choice | `独立` | | `独立` / `哨兵` / `集群` |
| 主节点名 | Text | — | | **仅哨兵形态显示**（`VisibleWhen = ("mode","sentinel")`）；`SENTINEL get-master-addr-by-name` 的参数 |
| 默认数据库 | Integer | `0` | | **集群形态下不显示**（集群只有 db0，填了不作数） |
| 使用 TLS | Boolean | `false` | | 自签走"提示→记指纹→重连"（与 FTPS/S3 同一套流程与文案） |
| 环境标记 | Choice | `开发` | | `生产` / `预发` / `开发` —— 驱动 §6.7 的护栏强度与标签配色 |
| 只读模式 | Boolean | 随环境 | | 生产默认开；见 §6.7 |
| 键分隔符 | Text | `:` | ✓ | 键树的层级分隔符 |
| SCAN 批量 | Integer | `500` | ✓ | `SCAN COUNT` |
| 分组折叠阈值 | Integer | `8` | ✓ | 同前缀的键达到几个才折成一行；`1` = 从不折叠（见 §6.2.1） |
| 单批上限 | Integer | `5000` | ✓ | 自动扫描的软上限，到顶即停并提示收窄 |
| 值预览上限 | Integer | `262144` | ✓ | 超过只取前 N 字节，界面明说被截断 |
| 客户端名 | Text | `velashell` | ✓ | `CLIENT SETNAME`，让 DBA 在 `CLIENT LIST` 里认得出是谁 |
| TLS 指纹 | Hidden | — | | `TrustedThumbprintSettingKey` 回写位 |

十五个字段里十个进"高级选项"折叠——这是 S3 插件用户反馈换来的纪律（连接对话框一列到底，
字段数一多就顶出屏幕，底部按钮够不着）；决定"连不连得上"的留在外面。

只在某种形态下有意义的两个字段用 `VisibleWhen` **声明**掉，而不是留一个框加一行小字解释。
它们原先都是"显示 + 小字"：主节点名下写"仅哨兵模式"，默认数据库下写"集群模式下忽略"——
用文案解释一个本该消失的字段，是把界面的活推给了文案。条件不成立时字段消失，
但**已存的值照常保留回传**：在哨兵下填过主节点名、切去独立瞄一眼再切回来，
不该发现自己填的东西被界面清掉了。

「测试」按钮对插件连接走的是插件自己的路（真开一次会话再关掉），不是 SSH ——
详见 §11.3 的第三行。

### 5.2 连接分工:一条会话最多四根 socket

多路复用是错的（§四），但"每个操作一根新连接"更错。分工按**连接级状态**切分：

| 连接 | 建立时机 | 承载 | 为什么不能合 |
|---|---|---|---|
| **控制** | 打开文档即建 | 键扫描、取值、写入、INFO 轮询 | — |
| **控制台** | 首次展开控制台 | 用户手敲的一切 | 用户 `SELECT 5` / `MULTI` / `BLPOP` 不能污染浏览器；反之浏览器的后台轮询不能插进用户的事务 |
| **订阅** | 首次订阅 | `SUBSCRIBE`/`PSUBSCRIBE`、键空间通知 | RESP2 下订阅态连接只接受订阅族命令（RESP3 可共用，但仍分开更可预测） |
| **MONITOR** | 用户明确开启 | 仅 `MONITOR` 流 | 进去只能靠 `RESET`（6.2+）或断开退出 |

集群模式下"控制"连接按节点惰性建立（一个节点一根），`SCAN`/`DBSIZE`/`INFO` 逐节点执行后聚合。
**空闲的订阅/MONITOR 连接在面板收起后自动关闭** —— 宿主是个对内存和文件描述符敏感的终端应用。

### 5.3 能力探测优于版本判断

`INFO server` 里的 `redis_version` 在托管实例与分叉上并不可信（Valkey / KeyDB / Dragonfly /
Upstash / 各家云版本号自成体系，且常禁用或改名命令）。因此一切可选能力都用**探测**：

```
COMMAND INFO scan client object memory      → 哪些命令存在、各自的 flags 与 key specs
COMMAND DOCS  (7.0+)                        → 控制台补全与参数提示的数据源
CONFIG GET databases                        → 拿不到就按 16 个库画,并标注"未能确认"
MODULE LIST / COMMAND INFO json.get         → 模块面板是否出现
```

这与 S3 那篇的第 4 条坑同源：**"没配过"与"不支持"不是错误,是空状态**。
在 Redis 上更常见——托管实例禁掉 `CONFIG`/`CLIENT`/`DEBUG`/`CLUSTER` 是常态，
当成错误会让概览页一打开就一片红。

---

## 六、界面设计(重点)

### 6.1 信息架构:一个停靠文档 + 一个底部抽屉

Redis 客户端的四类工作彼此**并行**而非串行：翻键、改值、敲命令、看服务器状态。
把它们做成互斥的顶层页签（RedisInsight 的 Browser / Workbench / Analysis 就是互斥的），
用户在"改完值想验证一下"这种最常见的动作上要来回切页，且切走就丢上下文。

本设计取"**主体左右分栏 + 底部抽屉**"——这正是本应用用户最熟的形状（终端在下、内容在上），
且与 VelaDock 天然相容：不够看时把整个文档拖成分栏，与终端并排。

```
┌───────────────────────────────────────────────────────────────────────────────────────┐
│ ● prod-cache  10.0.3.12:6379 ↝ bastion-01  [db0 (1.2M) ▾]  [只读]  [生产]   ⟳   ✕    │ 36  VelaBgSidebar
├──────────────────────────┬────────────────────────────────────────────────────────────┤
│ ⌕ user:                  │ user:10086:profile          [hash]  TTL 29:58   1.4 KB     │ 36
│  ⟨前缀⟩ 包含  通配 [类型▾]│ listpack · 12 字段                    [改 TTL][重命名][删除]│
│  → SCAN MATCH user:* …    │────────────────────────────────────────────────────────────│ 26  VelaBgSurface
├──────────────────────────┤ 字段              值                                        │
│ 名称                    # │────────────────────────────────────────────────────────────│
│ ▾ user              (842)│ name              张三                                  ✎  │ 28
│   ▾ 10086             (3)│ age               32                                     ✎  │
│       profile       hash │ tags              ["vip","beta"]                         ✎  │
│       sessions      zset │ …                                                           │
│       lock        string │ ＋添加字段                     HSCAN 12/12 已读完            │ 28
│ ▸ order            (1.2k)│                                                             │
│ ▸ session          (3.1k)│                                                             │
├──────────────────────────┴────────────────────────────────────────────────────────────┤
│ 已扫描 5,000 / ~1,240,000 (0.4%)   ⟨继续扫描⟩ ⟨停止⟩        RESP3 · 7.2.4 · 12 ms     │ 24  VelaBgSidebar
├───────────────────────────────────────────────────────────────────────────────────────┤
│ 控制台 │ 概览 │ 慢日志 │ 客户端 │ 订阅 │ 内存分析                            ⌃ 收起   │ 28
│ 10.0.3.12:6379[db0]> hgetall user:10086:profile                                        │
│ 1) "name"                                                                              │
│ 2) "\xe5\xbc\xa0\xe4\xb8\x89"   张三                                                   │
│ 10.0.3.12:6379[db0]> ▊                                                                 │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

几何与配色全部取自 `DESIGN.md`，不发明新令牌：36px 头 / 26px 列头 / 28px 行 / 24px 状态条、
`VelaBgSidebar`→`VelaBgSurface`→`VelaBgTerminal` 三层、键名与值一律 `VelaUiMonoFont` 11px、
类型徽章用 `VelaAccentDim` 底 + `VelaAccent` 字（`CornerRadius:2`，与 StatusTag 同规格）。
环境标记为**唯一**允许改变主色的地方：生产 `VelaError`、预发 `VelaWarning`、开发 `VelaTextTertiary`
——它出现在标签页文字后缀与文档头，让"我现在在动线上"这件事无法被忽略。

### 6.2 键空间浏览器:扁平列表 + 三条纪律

**纪律一:永不 `KEYS`。** 一律 `SCAN`。这不是性能偏好——`KEYS *` 在百万级键上会**阻塞整个实例**，
一个 GUI 客户端做这件事等于给用户一把没有保险的枪。代价是"键总数"与"扫描完整性"变成了估计值，
于是有：

**纪律二:进度必须诚实。** 状态条上永远写清三件事：已扫描多少、`DBSIZE` 给出的估计总数、
游标是否已回到 0。**只有游标归零才敢说"已扫完"**，其余一切措辞都是"已扫描到"。
分组行上的计数同理——`demo:order:2026:*  40` 里的 40 是**已扫描到的**数量，鼠标悬停给出全句解释。
（这与 S3 那篇拒绝"拿存储类别去填属组"是同一条准则：**界面不许说谎**，哪怕真相更啰嗦。）

**纪律三:一批一个往返。** 每一轮：`SCAN <cur> MATCH <p> COUNT 500 [TYPE t]` 取回一页键，
然后把这一页的 `TYPE`/`TTL` 用**流水线**打成一个往返（Redis 6.0+ 有 `SCAN ... TYPE` 时
连 `TYPE` 都省掉）。内存占用（`MEMORY USAGE`）**不随扫描取** ——
它是一列可选项，打开时明确告知"每个键多一条命令"。

**过滤条的语义要看得见。** 三段式 ⟨前缀⟩/包含/通配 + 类型下拉，下面**实时回显真正要发的命令**：

```
⌕ user:            ⟨前缀⟩ 包含 通配   [全部类型 ▾]
→ SCAN 0 MATCH user:* COUNT 500
```

这一行小字解决了所有 Redis GUI 的头号困惑——"我明明有这个键，为什么搜不到"（答案通常是
用户以为是子串搜索，而 `MATCH` 是通配匹配）。**把生成的命令摊在用户眼前，比任何提示文案都有效**，
顺带教会了用户 `SCAN` 的用法。

**主视图是扁平列表,不是树。**（2026-08-18 从树改过来,理由见 §6.2.1）
SCAN 每回一页就并进已扫集合并整份重排,虚拟化渲染,永不等"全部加载完"。
自动扫描到软上限（默认 5000 键或 3 秒）即**停下**并提示"结果可能不完整，建议收窄前缀"，
把继续与否交给用户——而不是替他把生产库扫穿。

**键名是二进制安全的。** Redis 键是字节串，可能不是合法 UTF-8。列表按 redis-cli 的规矩转义显示
（`\xNN`），复制时给转义形式（可直接粘进控制台），而 `RENAME`/`DEL` 用**原始字节**。
这条要有往返测试钉住——多数 GUI 在这里静默改坏用户的键。

#### 6.2.1 为什么从树改成了扁平列表

第一版是前缀树（按 `:` 折叠）。真机上用起来"太难受"，而难受的原因不是手感，是**树在陈述一件
服务器没说过的事**：

| 树的问题 | 后果 |
|---|---|
| Redis 的键是**扁平字节串**，`:` 只是书写约定 | 树把约定画成了层级结构 |
| 每行只有本层片段（`profile`），看不到完整键名 | 要知道在看哪个键得脑内拼路径；复制键名尤其难受 |
| 单子节点深链（`demo` → `user` → `10086` → `profile`） | 看一个键点三次，每一层都不提供信息 |
| 缩进吃光宽度 | TTL / 规模 / 编码一个都放不下，只剩一个类型徽章 |
| 计数是"已扫描到的"，而树的外形像完整目录 | 用户把 `user (842)` 当成 `DBSIZE` 那样的确定值 |
| `a:b` 与 `a:b:c` 并存时 `b` 必须分裂成键节点 + 前缀节点 | 点它的语义含糊——这是树这个形态被迫付的代价 |

现在的形态：**一行一个完整键名**（与 redis-cli 所见、与代码里写的一致），类型 / TTL / 规模成列，
一屏 25~30 个键。层级降级成两样导航：

- **分组行**：同前缀的键**达到阈值**（默认 8，`groupThreshold` 可调）才折成一行
  `demo:order:2026:*  40`，点开**就地展开**、不嵌套。前缀取该批键的**最长公共段前缀**，
  所以折出来的是 `demo:order:2026:*` 而不是笼统的 `demo:order:*`。少量同前缀的键一律平铺
  ——**折叠是为了压噪音，不是制造点击**；三个键折成一行反而要多点一下才看得到。
- **面包屑**：`全部 › demo ›`，点某一段等于把过滤条设成该前缀重扫。下钻复用过滤条而**不另立
  一套导航状态**——否则"我现在看的是哪批键"就有两个互相打架的来源，而那行命令回显只认得其中一个。

配套的几处：类型徽章**按类型分色**（整列同色等于没有信息，四十个 `string` 排下来只能逐行读字）；
键名**中间省略**而非末尾省略（末尾省略会吃掉最有区分度的那一段）；左栏从 280px 放宽到 400px
**并可拖拽**（`session:v2:prod:user:…:token` 这类真实键名动辄 40 字）；分组行**不参与选中**
（它不是键，让它顶着高亮会和详情区各说各话）。

**整套几何与状态色对齐宿主的资源管理器**（会话树），逐项对表：

| | 资源管理器 | Redis 键列表 |
|---|---|---|
| 分组行高 / 缩进 | 30 / 12 | 30 / 12 |
| 分组箭头 | lucide `chevron-down`⁄`chevron-right` 12×12，`VelaTextTertiary` | 同 |
| 分组名 / 计数 | `FontSize12` Medium `VelaTextPrimary` / `FontSize10` `VelaTextTertiary` | 同 |
| 叶子行高 | 28 | 28 |
| 每层缩进 | 分组 12 → 组内会话 36（即 +24） | +24 |
| 叶子名 | `FontSize11` 等宽 `VelaTextSecondary` | 同 |
| 悬停 / 选中 | `VelaBgHover` / `VelaBgActive` | 同 |
| 选中叶子名 | `VelaAccent` + Medium | 同 |
| 徽章 | 圆角 2、内边距 6,1、`FontSize9` Medium | 同 |

箭头不用字符 `▸▾` 而是 lucide 描边路径：`Viewbox`（12）包 `Path`（24×24 视图框、
`StrokeThickness=2`、圆头圆角），笔宽随之等比缩放到 1 —— 这正是宿主 `LucideIcon` 的算法
（2/24 比例）。插件**不引用** `VelaShell.Controls`，几何经 `{DynamicResource Icon.chevron-*}`
从宿主主题取，与配色令牌走同一条路。

**一处刻意不对齐**：缩进只加在键名上，不推整行。资源管理器没有列，整行右移无所谓；
键列表后面三列是**表格列**，跟着缩进走就再也对不齐了 —— 层级看得见，不等于数字列可以歪。

折叠规则是纯函数（`RedisKeyLayout`），单测 12 条钉住——折错了不会抛异常，只会"看起来怪"。

### 6.3 类型编辑器:每种类型按它自己的样子来

右侧详情区的头部对所有类型一致（键名 / 类型徽章 / TTL / 大小 / `OBJECT ENCODING` / 操作按钮），
主体按类型换：

| 类型 | 主体 | 读取方式 | 关键交互 |
|---|---|---|---|
| **string** | 值编辑器 + 编码切换 | `GET`（超上限用 `GETRANGE` 取前 N 字节并标注截断） | 自动识别 JSON（缩进美化 + 折叠）/ gzip / MessagePack / Java 序列化 / 十六进制；**解码视图默认只读**，要改得显式勾"以解码形式回写"（否则一次保存就把二进制值变成它的文本形状——这是真会毁数据的一步） |
| **hash** | 字段表（虚拟化，内联编辑） | `HSCAN`（7.4+ 列字段时用 `NOVALUES`） | 字段级过滤走 `HSCAN MATCH`（服务端过滤，不是前端 filter）；7.4+ 显示字段级 TTL（`HTTL`/`HEXPIRE`） |
| **list** | 带索引的行表 | `LRANGE` 窗口分页 | 两端 push/pop、按索引 `LSET`、`LINSERT`；**明说"删除中间元素在 Redis 里没有原语"**（要 `LREM` 按值删），不假装有 |
| **set** | 成员表 | `SSCAN` | `SADD`/`SREM`、`SRANDMEMBER` 抽样看一眼 |
| **zset** | 成员 + 分值双列，可按分值排序 | `ZSCAN` / `ZRANGE ... WITHSCORES` | 分值直接改（`ZADD`）；按分值区间与字典序区间查询（`ZRANGEBYSCORE`/`BYLEX`） —— 这是 zset 的真正用法，光列成员没有意义 |
| **stream** | 条目表（ID / 字段），另一页签列消费组 | `XRANGE` 分页、`XINFO STREAM/GROUPS/CONSUMERS` | 看 pending（`XPENDING`）、`XACK`、`XADD`；消费组延迟一目了然 |
| **JSON**（模块） | 路径树 + 值编辑 | `JSON.GET $ ` / `JSON.OBJKEYS` | 按 JSONPath 局部读写，大文档不整取 |
| **未知/模块类型** | 原始视图 | `TYPE` 结果 + `OBJECT ENCODING` + `DUMP` 长度 | 明确说"该类型没有专用编辑器，可在控制台操作"，并给一个把键名填进控制台的按钮 |

**TTL 编辑器**（所有类型共用）：输入框接受 `900` / `15m` / `2h30m` / `2026-08-20 12:00` 三种写法，
下方实时回显"将于 08-20 12:00:00 过期（还剩 2 天 3 小时）"；`PERSIST` 是一个显式按钮而不是清空输入框
（清空的含义太模糊）。生产环境下给已无 TTL 的键**加**TTL，或给有 TTL 的键改成永久，都要过一次确认。


#### 6.3.1 二进制值:显示与回写走同一种可逆表示

Redis 的值和键一样是**二进制安全的字节串**。protobuf、msgpack、gzip、序列化对象 ——
存进 Redis 的东西经常不是文本。多数图形客户端在这里静默改坏数据:

> 按 UTF-8 解码显示 → 用户点保存 → 按 UTF-8 编码写回。
> 非法序列在**解码那一步**就已经被替换字符顶掉了,于是"保存"实际上是
> "用一段近似值覆盖原值",而界面全程正常。

**规矩:显示与回写必须走同一种可逆表示,并且原始字节全程留着。** 值编辑区给三种形态:

| 形态 | 什么时候能用 | 可编辑 | 说明 |
|---|---|---|---|
| **文本** | 字节是合法 UTF-8（允许换行/回车/制表） | ✓ | 保存 = `UTF8.GetBytes(草稿)` |
| **转义** | 始终 | ✓ | redis-cli 口径 `\xNN`,**可逆** —— 二进制值也能逐字节精确编辑 |
| **十六进制** | 始终 | ✗ | 偏移 + ASCII 侧栏的转储。它是**排版**不是表示,能在上面编辑就得去猜哪些字符是数据 |

几条刻意的决定:

- **非 UTF-8 的值,「文本」按钮是灰的。** 不是为了限制用户,而是那条路会经过一次有损解码:
  屏幕上的替换字符并不是值里真有的字节,顺手一保存就把原值换成了那段近似值。
  要把二进制值改写成一段纯文本,在「转义」里直接键入即可(转义模式照样接受直接输入的中文)。
- **认不出的转义一律报错,不猜。** 把一个不认识的转义当成字面量,就是在用户没察觉的情况下
  改动他要写的字节。写坏时**一个字节都不写**,并说清出错位置。
- **切换形态先把草稿解回字节再重渲染** —— 改到一半切形态,改动不会丢。
- **换行/制表算文本。** 键名那边一律转义(带换行的键名会把列表行高搞乱),值这边不能照抄:
  把一段多行 JSON 显示成 `{\n  "a": 1\n}` 会逼用户对着转义符编辑一段本可以直接读的文本。
- **被截断的值(超过预览上限)一律只读** —— 让用户编辑"前 256 KB"再整体写回,
  等于用一次保存把后面几 MB 静默删掉。这条早于本次改动就成立。

往返由 `RedisValueTextTests` 逐条钉住,其中一条**专门复现修复前的损坏路径**
(转义文本被当普通文本编码,10 字节的 gzip 头变成 40 字节的 ASCII);
另有打真实 Redis 的端到端:开二进制键 → 改一个字节 → 保存 → 直接问服务器要原始字节比对。

**成员表(哈希字段 / 列表项 / 集合成员)还没做这套形态。** 它那一路的读写至今是字符串,
因此暂时**明确挡住**:成员的字段名或值一旦看起来是转义产物,保存会被拒绝并提示改用控制台。
宁可暂时不能在成员表里改二进制成员,也绝不让一次保存把它换成一串反斜杠字面量。
把成员表也改成"字节 + 形态"是下一步,见 §11.5。

### 6.4 控制台:补全数据来自服务端

底部抽屉的第一个页签，一个真正的 redis-cli：

- **提示符如实反映连接状态**：`10.0.3.12:6379[db0]>`；进了事务变 `…[db0](TX)>`，
  订阅态变 `…(subscribed:2)>`，MONITOR 中变 `…(monitor)>` 并只留一个"停止"按钮。
  **状态可见 = 用户不会疑惑"我敲的命令怎么没反应"**（订阅态下 RESP2 只接受订阅族命令）。
- **补全与参数提示来自 `COMMAND DOCS` / `COMMAND INFO`**（7.0+，启动时取一次并缓存）。
  这一步同时解决三件事：命令表**永远匹配这台服务器的版本**、
  **自动包含模块命令**（`JSON.SET`、`FT.SEARCH`、`TS.ADD` 全都白得）、
  以及**不必在插件里维护一张 240 行的表**。低于 7.0 时回落到内嵌的精简表（约 120 条常用命令）
  并在提示里说明"该服务器不提供命令文档，补全为内置精简表"。
- **回复按 redis-cli 的格式渲染**：`(integer) 3`、`(nil)`、`(empty array)`、嵌套数组的
  `1) 1) "a"` 缩进、RESP3 的 map/set 各自记号。错误行 `VelaError` 前缀 `(error)`。
  二进制回复给转义 + 一列可展开的解码预览（如上面线框里的 `\xe5\xbc\xa0\xe4\xb8\x89  张三`）。
- **双向联动**：控制台输出里任何"像键"的 token 可点击 → 在左侧浏览器打开该键；
  反过来，浏览器里任何键的右键菜单有"在控制台里生成命令"（预填 `HGETALL <key>` 之类，
  光标停在参数处）。**这是把"点点点"与"敲命令"缝在一起的关键一针** ——
  Redis 的重度用户永远会回到命令行，客户端的价值在于**降低往返摩擦**，不是取代它。
- **历史**：`↑`/`↓` 调历史（与 AI 插件输入框同一手感），落插件私有时序库，重开文档仍在；
  `Ctrl+L` 清屏，`Ctrl+C` 中断长流（MONITOR/订阅）。
- **危险命令进闸门**（§6.7），且**不可用 `→` 回显糊弄**：闸门是弹窗，不是提示。

### 6.5 概览 / 慢日志 / 客户端 / 订阅 / 内存分析

底部抽屉的其余页签，也都可以"⤢ 拉成独立文档"（VelaDock 一拖就分栏，与终端并排看）。

**概览**（`INFO` 全段 + `CONFIG GET`，默认 5s 轮询，可关）：
四张迷你图（ops/sec、命中率、内存、连接数）+ 三块要点卡片：
持久化（最近一次 RDB/AOF 时间、`rdb_changes_since_last_save`、上次失败原因）、
复制（角色、从库列表与延迟字节、`master_link_status`）、
键空间（逐库键数与带 TTL 的键数，来自 `INFO keyspace`）。
每张图的数据点落插件私有时序库，**关掉文档再打开还能看到刚才那段曲线**
（宿主已有资源监视器的时序积累先例）。

> 数据库下拉里直接显示每库键数（`db0 (1.2M)` / `db1 (空)`），来自 `INFO keyspace`。
> 一个下拉就省掉"逐个库点进去看有没有东西"的盲测——小改动，高频受益。

**慢日志**：`SLOWLOG GET 128` 表格（时长 / 时间 / 客户端 / 命令），命令列可点 → 填进控制台；
`SLOWLOG RESET` 与 `CONFIG SET slowlog-log-slower-than` 就近提供（后者过写闸门）。
另附 `INFO commandstats` / `latencystats`（7.0+）的按命令耗时排行——比慢日志更能看出全局热点。

**客户端**：`CLIENT LIST` 表格（addr / name / age / idle / cmd / db），排序、过滤，
`CLIENT KILL` 走确认。自己那几条连接**高亮标出并禁止 kill**
（一个客户端把自己 kill 掉然后报"连接丢失"，是很蠢但很常见的 bug）。

**订阅**：上半区订阅管理（频道/模式/分片频道），下半区消息流（时间 / 频道 / 载荷，可暂停、可导出）。
另有"键空间通知"一键开关——它需要 `CONFIG SET notify-keyspace-events KEA`，
所以**明确告知这会改服务器配置**、显示改前的原值、并在关闭时还原。

**内存分析**（差异化功能，运维最需要的一页）：
以 `SCAN` 抽样 + 流水线 `MEMORY USAGE` 采一批键，按**前缀聚合**给出
"哪一类键吃掉了多少内存"的树，以及 Top-N 大键榜（含类型与元素个数）。
全程标注"抽样 N 键，占估计总数 x%"——**它是抽样结论，不是全量审计**，这一点必须写在页面上，
而不是藏在文档里。这页填的是 `redis-cli --bigkeys` / `--memkeys` 的坑：
命令行版本要么阻塞、要么只给类型级汇总，且看完就没了。

### 6.6 键盘优先(与宿主既有绑定不冲突)

| 键 | 动作 |
|---|---|
| `/` 或 `Ctrl+F` | 聚焦过滤框 |
| `↑`/`↓`、`←`/`→` | 树内移动、展开/折叠 |
| `Enter` | 打开选中键 |
| `F2` | 重命名（`RENAME`，目标已存在时明确问"覆盖还是取消"，对应 `RENAMENX`） |
| `Delete` | 删除（多选批量，`UNLINK` 优先于 `DEL`——异步释放不阻塞实例） |
| `Ctrl+C` | 复制键名（转义形式）；`Ctrl+Shift+C` 复制取值命令 |
| `Ctrl+R` | 重扫当前前缀 |
| `Alt+1..9` / `Alt+0` | 切数据库 |
| `` Ctrl+` `` | 展开/收起底部抽屉 |
| `Ctrl+Enter` | 控制台执行（多行时执行全部） |
| `Esc` | 关菜单 / 取消扫描 / 退出 MONITOR |

另注册三条命令面板命令（`Ctrl+P`）：`Redis: 打开连接…` / `Redis: 执行命令…` /
`Redis: 从当前 SSH 会话探测 Redis`（见 §7.1）。

### 6.7 安全护栏:这是运维工具,不是玩具

Redis 没有事务回滚，`FLUSHALL` 之后没有回收站。护栏按"**误伤成本**"分三档：

| 档 | 命令 | 处置 |
|---|---|---|
| 写 | `SET`/`HSET`/`DEL`/`EXPIRE`/… | **只读模式**下直接拦住并说明（生产环境默认开启，标题栏有 `[只读]` 徽章，一键切换但要确认）。判定依据是 `COMMAND INFO` 返回的 `write` flag，**不是插件里手写的黑名单** —— 服务器说它是写命令，它就是 |
| 危 | `CONFIG SET`、`CLIENT KILL`、`SLAVEOF`/`REPLICAOF`、`SCRIPT FLUSH`、`ACL SETUSER`、`MONITOR`、`DEBUG` | 逐次确认弹窗，说明具体后果（`MONITOR` 那条要写明"会显著降低实例吞吐"，并带一个自动停止倒计时） |
| 毁 | `FLUSHDB`、`FLUSHALL`、`SHUTDOWN` | 要求**手打确认串**（键入 `prod-cache/db0`，与 GitHub 删仓同款）。生产环境标记下**默认整条禁用**，要去连接设置里显式解锁 |

另外三条纪律：

- **`CLIENT NO-TOUCH on`**（7.4+，探测到就发）。浏览键会更新 LRU/LFU 元数据，
  把客户端的翻页行为混进服务器的淘汰统计里 —— 一个只读浏览器不该改变被观测对象。
- **不代为清空**。删前缀是批量 `UNLINK`（如实报进度、可中断），但绝不把
  "删这个前缀"悄悄升级成"清空这个库"（与 S3 那篇"桶只删空的，绝不代为清空"同一条准则）。
- **服务端才是最后一道闸**。设置页与文档里都要写明：真正的只读靠 ACL
  （`ACL SETUSER viewer on >pw ~* +@read`），客户端的只读模式挡的是**手滑**，不是恶意。
  说清边界比暗示"我们保证安全"负责。

### 6.8 空状态、错误与降级

| 情形 | 呈现 |
|---|---|
| 库是空的 | `VelaTextMuted` 一句"db0 没有键"+ 一个"新建键"按钮，**不是**空表格 |
| 前缀扫不到 | "已扫描 12 万键，未匹配 `user:*`" + 回显真实 `MATCH` 模式 + "改用包含匹配"的一键切换 |
| 命令被禁用 / 无权限（托管实例） | **空状态,不是错误**：例如客户端页写"该服务器未开放 `CLIENT LIST`"，灰掉页签而不是红条报错（S3 §六.4 的教训直接搬过来） |
| 集群下选数据库 | 下拉禁用，旁注"集群模式只有 db0" |
| 键在查看期间过期 | 详情区就地变成"该键已过期或被删除"，**不弹错误弹窗**（这是正常生命周期，不是故障） |
| 值超出预览上限 | 顶部黄条"仅显示前 256 KB（共 4.2 MB）"+ "另存为文件"按钮；编辑器置只读，避免截断内容被整体写回 |
| 连接断开 | 复用宿主既有的断连覆盖层规格（`VelaStatusDisconnected` + 重连按钮）；重连成功后**恢复到原来的键与滚动位置** |

---

## 七、只有 VelaShell 能做的三件事

功能对齐 RedisInsight / Another Redis Desktop Manager 是及格线（§6 覆盖了它们的主体功能面）。
真正的差异化来自**它长在一个 SSH 客户端里**：

### 7.1 从 SSH 会话一键接上 Redis(杀手级)

用户已经 SSH 在那台机器上。此时插件能做独立 Redis GUI 永远做不到的事：

```
Ctrl+P → "Redis: 从当前 SSH 会话探测 Redis"
  ├ RemoteExec:  ss -lntp | grep redis   → 找到 127.0.0.1:6379 与 :6380
  ├ RemoteExec:  redis-server --version / redis-cli -v
  ├ RemoteFs:    读 /etc/redis/redis.conf → requirepass / port / bind / tls-port
  └ 弹出「发现 2 个 Redis 实例」→ 勾选 → 自动创建连接配置(隧道走当前会话)、直接打开
```

**零打字。**没有主机名要抄、没有端口要记、没有密码要翻配置、没有隧道要开。
这一步用的全是现成能力（`RemoteExec` + `RemoteFs` + §3.2 的隧道），
是"插件长在终端客户端里"这件事第一次产生**乘数效应**而不只是并列摆放。

> 纪律：读到的 `requirepass` **不得写日志**、不得进任何 Trace，只经宿主
> `Secrets`/会话凭据路径落盘（与宿主口径一致）。探测命令一次 exec 批量执行，不逐条敲远端。

### 7.2 与终端并排

VelaDock 一拖：左边 Redis 键树，右边 `redis-cli --stat` 或应用日志的 `tail -f`。
Electron 客户端做不到（它不知道你的终端在哪），Web 客户端更不行。
"改一个键 → 立刻在日志里看到应用的反应"是排障的实际动作，而不是两个窗口来回 Alt+Tab。

### 7.3 命令历史可审计

控制台的每一条命令落插件私有时序库（`redis_console` measurement，标签=连接 id）。
配合宿主既有的审计文化，"谁在什么时候对生产库敲了什么"是可回溯的。
这对团队场景（也是商业授权场景）的价值高于任何单机 GUI。

---

## 八、数据、文案与持久化

| 数据 | 落点 | 说明 |
|---|---|---|
| 连接配置（主机/端口/形态/TLS/环境标记/…） | **宿主** `session_profiles` | 走 `ConnectionType.Plugin` + `PluginSettings`；因此白得 Gist 云同步、会话树、最近连接、WinSCP 式导入 |
| 密码 / TLS 口令 | **宿主** AES-256-GCM | `IsSecret` 字段与宿主口令同路径，插件只在 `OpenAsync` 那一刻见到 |
| 面板偏好（分栏比例、抽屉高度、列宽、可见列、默认解码器） | 插件 `Storage` | 每连接一份 |
| 控制台历史、指标采样 | 插件 `TimeSeries` | `redis_console` / `redis_metrics` 两个 measurement（配额上限 8 个，够用） |
| 收藏的键与命令 | 插件 `Storage` | "常看的那几个键"置顶，运维的真实习惯 |

**文案**：随插件自带 `Loc.cs`（中英双语，缺则回落英文），估约 250 条。
理由与 S3 一致：让宿主替一个它并不认识的领域背五语词典是本末倒置。

---

## 九、兼容性矩阵与已知坑

| 目标 | 状态 | 注意 |
|---|---|---|
| Redis 6.0 – 8.x 独立 | 主要目标 | `HELLO 3` 拿 RESP3；6.0 以下回落 RESP2（`SCAN TYPE` 不可用时补 `TYPE` 流水线） |
| Redis Cluster | 支持 | 只有 db0；`SCAN`/`DBSIZE`/`INFO` 逐节点；`MOVED`/`ASK` 重定向；跨槽命令（`MGET`/`DEL` 多键）要按槽分组，或如实报 `CROSSSLOT` |
| Sentinel | 支持 | 主节点地址来自 `SENTINEL get-master-addr-by-name`；订阅 `+switch-master` 感知切换并自动重连 |
| Valkey / KeyDB / Dragonfly | 尽力而为 | 命令面基本兼容，但 `INFO` 字段、`OBJECT ENCODING` 取值、模块支持各不相同 → §5.3 的能力探测；`INFO` 缺字段一律呈现"—"而不是 0 |
| 托管服务（各家云 / Upstash） | 尽力而为 | `CONFIG`/`CLIENT`/`DEBUG`/`SLOWLOG`/`MONITOR` 常被禁用或改名 → 空状态降级（§6.8） |
| TLS（含自签） | 支持 | 走 FTPS/S3 同一套"提示→记指纹→重连"；**不在 TLS 回调里同步等用户点按钮**（会把异步对话框阻塞成同步，极易死锁——S3 那篇踩过） |
| Redis 5 及更早 | 不承诺 | 没有 ACL、`COMMAND DOCS`、`MEMORY USAGE` 语义不同；能连上就连，功能按探测降级 |

**十条必踩的坑**（写进实现清单，逐条对应测试）：

1. **`KEYS` 一次都不许用**——包括"就统计一下总数"这种诱惑（用 `DBSIZE`）。
2. **`SCAN` 会返回重复键**（rehash 期间），也可能连续多轮返回空页；游标为 0 才是结束。UI 去重。
3. **`SCAN COUNT` 是提示不是保证**，返回条数可以是 0 到远大于 COUNT。
4. **键与值都是二进制**，不能假定 UTF-8；转义显示 + 原字节操作。
5. **`MEMORY USAGE` 对集合类型是抽样估计**，界面标注"约"。
6. **`MONITOR` 只能靠 `RESET`（6.2+）或断开退出**，且吞吐损失显著，必须有自动停止。
7. **订阅态连接（RESP2）只接受订阅族命令**——所以订阅要独立 socket，或确认已在 RESP3。
8. **`RENAME` 会静默覆盖目标**；`RENAMENX` 才不会。GUI 必须问。
9. **`DEL` 大集合会阻塞**——一律优先 `UNLINK`（4.0+）。
10. **`SELECT` 在集群上直接报错**；`db` 概念在集群下不存在，UI 要提前禁用而不是等报错。

---

## 十、测试策略

沿用仓库已验证的路子（`LoopbackS3Server` 的做法在这里同样成立，且更便宜——RESP 比 XML 好造）：

| 测试 | 数量（估） | 守住什么 |
|---|---|---|
| `RespCodecTests` | ~40 | RESP2/RESP3 全部 14 种类型的读写往返、分片到达（每次只喂一个字节也要正确）、超长 bulk、null 的三种写法 |
| `LoopbackRedisServer` + `KeyspaceServiceTests` | ~35 | 端到端：SCAN 分页/重复键/空页/游标归零、`SCAN TYPE` 有无两条路、树增量构建、二进制键往返、批量 `UNLINK` 进度 |
| `ClusterRoutingTests` | ~20 | CRC16 槽位（拿 Redis 官方样例对拍）、hashtag、`MOVED`/`ASK`+`ASKING` 重试、跨槽命令分组 |
| `SentinelTests` | ~10 | 主地址发现、`+switch-master` 触发重连 |
| `SafetyGateTests` | ~20 | 只读模式按 `COMMAND INFO` flags 拦写命令、三档闸门、确认串校验、生产标记下 `FLUSHALL` 整条禁用 |
| `ValueCodecTests` | ~20 | JSON/gzip/MessagePack/Java 序列化识别；**解码视图不得把二进制值改成它的文本形状**（对应 §6.3 那条真会毁数据的路径） |
| `DegradationTests` | ~15 | 命令被禁用/改名/无权限时一律走空状态而非错误（模拟托管实例） |
| 面板 headless UI | ~20 | 键树虚拟化、过滤条命令回显、TTL 输入解析、抽屉页签切换 |
| 宿主侧 `PluginWorkspaceTests` | ~15 | 声明→注册→惰性激活→注销、`isolated` 组合被拒、文档生命周期、隧道租约随文档关闭而拆除 |

真机验收（环回服务器验证不了的部分）：Docker 起 Redis 7.4 单机 + 3 主 3 从集群 + Sentinel，
各跑一遍连接/扫描/写入/故障切换；另拿一台托管实例过一遍降级路径。
`docker-compose.test.yml` 已有先例，加两个 service 即可。

---

## 十一、里程碑与工作量

| 里程碑 | 内容 | 估算 |
|---|---|---|
| **M0 宿主扩展** ✅ | `contributes.workspaces` + `IWorkspacesApi` + `PluginWorkspaceDocument` + 注册表/惰性激活/异常翻译 + 25 项宿主侧测试。**声明式 SSH 隧道(`ProtocolSettingKind.SshSession`)未做,顺延到 M4** | 3–5 d |
| **M1 能连能看** ✅ | 连接(SE.Redis)、能力探测、游标 SCAN 键树、五种类型只读视图 + 分页、状态条、中英文案、72 项测试(含 21 项打真机、7 项 headless 面板) | 5–7 d |
| **M2 能改能敲** ✅ | 全类型编辑器、TTL 编辑(带实时换算回显)、重命名(不静默覆盖)/删除、控制台(服务端补全/跨会话历史/redis-cli 口径的回复渲染)、安全护栏三档、只读开关 | 6–8 d |
| **M3 运维面** ✅ | 概览(INFO 五组指标)、慢日志、客户端(自己的连接禁 kill)、Pub-Sub、内存抽样分析。**指标时序图与键空间通知开关未做**(见 §11.2) | 6–8 d |
| **M4 差异化与收尾** ✅ | 声明式 SSH 隧道(M0 顺延项)、§7.1 SSH 探测 + 连接提议、`DUMP`/`RESTORE` 跨库复制原语、收藏、控制台历史落时序库、真机验收。**JSON 模块专用编辑器未做**(见 §11.2) | 6–8 d |

合计约 **5–6 周**（单人、含测试与文档），代码量约 10–11k 行，与 AI 助手插件同量级。
M1 结束即可自用（"看"占日常 Redis 操作的绝大多数）；M2 结束可发布 preview。

**建议的切入顺序**：先做 M0 并**同期把 M1 的连接层跑通**——因为 M0 的设计正确性只有被一个真实使用者
验证过才算数（这也是 `docs/plugins/STATUS.md` §五 "写第一个业务插件反哺框架缺口"的原意）。

### 11.1 M0 + M1 已落地(2026-08-17)

按上面那条建议同期做的,事后看确实是对的:M0 的两处设计缺陷都是被 M1 这个真实使用者顶出来的
—— `Declare` 的重载在 `[new() { … }]` 处二义(改名 `DeclareWorkspaces`)、
以及"语言切换重注册会掐掉已打开标签页"(provider 必须复用同一实例)。
纯设计阶段这两条都想不到。

| 落地物 | 位置 |
|---|---|
| SDK 工作台能力域 | `plugin-sdk/VelaShell.PluginSdk/Workspaces/`(描述 / 请求 / 文档 / 提供方 / 能力面),复用协议侧的 `ProtocolSettingField` 与 `Protocol*` 异常族 |
| 清单与激活 | `contributes.workspaces` + `onWorkspace:` + `isolated` 组合拒绝 + 与协议 id 撞名拒绝 |
| 宿主注册表 | `PluginProtocolRegistry` 泛化为连接类型注册表(带 `Kind`);两种形态共用页签条带与惰性激活链路 |
| 宿主会话 | `PluginWorkspaceLauncher`(解析 → 组装请求 → 异常翻译 → 会话登记)+ `PluginWorkspaceDocument`(停靠标签页外壳) |
| Redis 插件 | `plugins/VelaShell.Plugin.Redis`(约 2.4k 行):连接、能力探测、游标扫描、键树、五种类型详情、声明式表单、中英文案 |
| 测试 | 宿主 25 项 + 插件 72 项(51 项纯单测 + 21 项打真机 + 7 项 headless 面板);全仓 1989 项全绿、0 警告 |

**M1 的边界**:值只读、抽屉未开工、护栏只呈现徽章 —— 这三条已由 M2/M3 补齐,见 §11.2。

### 11.2 M2 + M3 + M4 已落地(2026-08-17,同日)

| 落地物 | 位置 |
|---|---|
| 命令闸门 | `RedisCommandGuard`:三档分级**依据 `COMMAND INFO` 的 flags**(未知即写);"毁"档按名字定死 |
| 写入路径 | `RedisConnection.Writes.cs`:字符串(默认 `KEEPTTL`)、哈希/列表/集合/有序集合成员、`EXPIRE`/`PERSIST`、`RENAME`(默认 `RENAMENX`)、批量 `UNLINK`、`DUMP`+`RESTORE` 跨库复制 |
| 控制台 | `RedisConnection.Console.cs` + `RedisCommandLine` + `RedisReplyFormatter` + `Ui/RedisConsoleViewModel`:redis-cli 口径的回复渲染、服务端补全、跨会话历史、不可用命令**敲之前就说清** |
| 确认闸门 | `Ui/RedisConfirmation`:贴在本面板上;"毁"档要求手打端点串 |
| 运维面 | `RedisConnection.Ops.cs` + `Ui/…Drawer.cs`:概览五组、慢日志、客户端(自己的连接禁 kill)、Pub-Sub(库的专用订阅连接)、内存抽样(按前缀聚合 + Top-N) |
| 宿主:声明式 SSH 隧道 | `ProtocolSettingKind.SshSession` + `MainWindowViewModel.EstablishWorkspaceTunnelAsync`:复用已连的同目标会话,否则经 `IConnectionWorkflowService` 连上;本地端口 bind-0 借取;文档关闭即拆隧道 |
| 宿主:连接提议 | `IWorkspacesApi.ProposeConnectionAsync` → `PluginProtocolRegistry.ConnectionProposalHandler` → 宿主的新建连接对话框预填。**插件不能自己写会话库** |
| SSH 探测 | `RedisDiscovery`:一次 exec 取监听端口/进程/版本/配置路径,经 `RemoteFs` 读 `requirepass`,逐个提议连接 |
| 私有持久化 | `RedisStore`:收藏走 `Storage`、控制台历史走 `TimeSeries`(兑现 §7.3 的可审计);无 DB 时静默降级 |
| 测试 | 插件 179 项 + 宿主 25 项;全仓 **2096 项全绿、0 警告** |

**这一轮又被测试逮到三个真 bug**(都不是编译期能看出的):
① `INFO everything` 在 3.x 上**不报错**地回一份空内容 —— 回落条件必须看"解出几个字段"而不是"结果是否为 null";
② 生产环境关只读的文案承诺了手打确认串,而代码没有要求 —— **文案在骗人**比没有这个保护更糟;
③ `Loc` 表里有一个重复键,会在**静态构造时**抛 `ArgumentException` —— 编译器不管,一炸就是整个插件不可用(现已有 `LocTests` 守住)。

**明确未做(不是遗漏)**:

| 项 | 说明 |
|---|---|
| 概览的指标时序图 | 数值都在,但没有画曲线/落时序采样。做它要先决定采样频率对生产实例的礼貌程度 |
| 键空间通知开关 | 它要 `CONFIG SET notify-keyspace-events`,即改服务器配置 —— 值得单独一轮设计(改前原值、关闭时还原) |
| JSON / Search / TimeSeries 模块专用编辑器 | 目前经控制台可达;概览里也没有列已加载模块 |
| 流的消费组页签 | 流的条目能看,`XINFO GROUPS`/`XPENDING` 还没进界面 |
| 集群 / 哨兵 | 代码路径写了,**只在单机上验证过** —— 上线前需要 3 主 3 从 + Sentinel 各跑一遍 |
| 面板偏好持久化 | 分栏比例、抽屉高度、列宽尚未记住(§8 曾计划) |

### 11.3 真机跑一遍界面之后修掉的四处(2026-08-18)

把程序真启动起来、按正常路径连一次本机 Redis 3.0.504,四个只在真机上才会露头的问题:

| 症状(用户看到的) | 根因 | 修法 |
|---|---|---|
| 标签页写着 `VelaShell.Docking.PluginWorkspaceDocument` | `DockGroupControl` 里没有 `PluginWorkspaceDocument` 的 `DataTemplate`,Avalonia **不报错**,退回 `ToString()` | 补 `WorkspaceDockTabItem`(与 SFTP 标签同一套状态圆点/强调色/右键菜单)+ 注册模板;文档暴露可观察的 `Status`。回归测试:`WorkspaceDockTabUiTests` |
| 键树一片空白,状态条一句 `A target database is required for SCAN` | 集群路径走 `IServer.ExecuteAsync("SCAN", …)`,**那条路不带库号** | 改用 `IServer.KeysAsync(database, pattern, pageSize, cursor)`(库为"在这个节点上 SCAN"提供的正路,显式收库号,枚举器实现 `IScanningCursor`);代价是没有 `TYPE` 选项,故集群一律把类型过滤降级到客户端。回归测试:`ClusterDeploymentAgainstStandalone_StillScans…`(单机上也能钉住) |
| 按「测试」等几秒,报 `The connection could not be established - Timeout` | 宿主的 `TestConnectionAsync` **无条件开 SSH 连接** —— 拿 SSH 去连 6379,TCP 通了却卡在版本交换。S3 同样中招 | `IConnectionWorkflowService.PluginProbe`:插件连接改由界面层真开一次插件会话再关掉(工作台形态连隧道一起建/拆,文件系统形态开关一次会话);没挂探针时明说"这种连接类型测不了",**不给假原因** |
| 按「测试」界面像什么都没发生 | 测试结论渲染在**滚动区最底部**,而按钮在页脚 —— Redis 十来个字段一定会滚,结论落在视野外 | 反馈条移出 `ScrollViewer`,钉在按钮那一行上方(`HasFeedback` 控制占位) |

另外把一处"用文案补界面"的地方改成了声明:`ProtocolSettingField.VisibleWhen`。
「主节点名」原先在独立/集群形态下照样显示,靠字段下一行小字写"仅哨兵模式";
「默认数据库」在集群下填了不作数,也靠小字解释"集群只有 db0"。现在两个字段各自声明显示条件,
不适用时直接不出现(值仍保留回传)。判据形状刻意封闭到"某键 ∈ 某值集合",理由见 §十二。

**这一轮又被测试逮到一个我自己刚写的 bug**:形态不符的提示原先写进 `StatusMessage`,
而那一行每次扫描都会被清空 —— 提示刚写上就被下一次扫描擦掉,等于没说。
改成常驻的 `DeploymentWarning` 提示条(配置错误不会自己好,它得有自己的地方)。

全仓 **2102 项全绿、0 警告**。

### 11.4 键浏览器改成扁平列表(2026-08-18)

用户反馈"左边 key 的展示用得太难受"。诊断与新形态见 §6.2.1 —— 简短说:树在陈述一件
服务器没说过的事(Redis 的键是扁平字节串,`:` 只是约定),而它换来的代价是看不到完整键名、
深链点击税、以及 TTL/规模没地方放。

| 落地物 | 位置 |
|---|---|
| 折叠规则(纯函数) | `Ui/RedisKeyLayout.cs`:按下一段分区 → 少于阈值平铺、达到阈值折一行;前缀取最长公共**段**前缀;顶层公共前缀永不折(它是面包屑);展开时对成员递归套同一套规则 |
| 行模型 | `Ui/RedisKeyRow.cs`:键行/分组行两态,带类型、TTL、规模、缩进、按类型上色的布尔位 |
| 增量同步 | `RedisKeyLayout.Sync`:按 id 复用行对象,扫描中途重排不打掉选中项与滚动位置 |
| 批量度量 | `RedisConnection.MeasureAsync`:一页键的 TTL 与规模各一次流水线往返(**不含** `MEMORY USAGE` —— 它是抽样遍历且 4.0 以下没有) |
| 面包屑 | `Ui/RedisBreadcrumbSegment.cs` + `NavigateCommand`:下钻复用过滤条,不另立导航状态 |
| 新设置 | `groupThreshold`(默认 8,高级) |
| 样式对齐 | 行高/缩进/字号/箭头/悬停选中色逐项对齐宿主资源管理器(见 §6.2.1 的对表);箭头走 `{DynamicResource Icon.chevron-*}` + `Viewbox`+`Path`,复刻 `LucideIcon` 的 2/24 笔宽比例,**不引用** `VelaShell.Controls` |
| 测试 | `RedisKeyLayoutTests` 12 条(纯函数)+ `KeyList_FoldsTheNoisyPrefix…` 1 条(打真实 Redis 的端到端) |

**删掉的**:`Ui/RedisKeyNode.cs` 与 `RedisKeyTreeTests.cs`(树没了,它的模型跟着走);
`Redis_TreeCountTip` / `Redis_ColumnCount` 两条文案。

真机上又抓到两处只有跑起来才看得见的:
① 点分组行时 ListBox 把高亮抢了过去,而详情区还停在原来那个键上 —— 视图模型否决还不够,
   `ListBox` 在自己的赋值过程里不理会通知,得排到下一轮再把选中态按回去;
② 按回去之后列表**跟着滚**到选中项,表现成"点顶部的分组、画面跳到底部" ——
   关掉 `AutoScrollToSelectedItem`,只在"跳到收藏的键"那条路上显式 `ScrollIntoView`。

全仓 **2112 项全绿、0 警告**。

> **未验证**:集群 SCAN 的修法只在单机上跑过(本机没有集群环境,也刻意不拿线上集群做实验)。
> 上线前仍需 3 主 3 从真机复跑一遍 —— 逐节点游标推进、跨节点续扫、`node|cursor` 游标编码。

---


### 11.5 值编辑区的二进制处理(2026-08-18)

起因是一个问题:"值编辑区遇到二进制怎么办"。查下去发现的不是缺功能,是**一条正在生效的
数据损坏路径**:值按 UTF-8 解码显示、保存时再按 UTF-8 编码写回。设计见 §6.3.1。

| 落地物 | 位置 |
|---|---|
| 字节 ↔ 文本(纯函数) | `RedisValueText`:`IsTextSafe` / `Escape` / `TryUnescape` / `HexDump` / `Detect` |
| 三种形态 | 视图模型持有 `_valueBytes` 原始字节;`ValueFormat` 决定渲染;保存按当前形态解回字节 |
| 拒绝写入 | 转义写坏时 **一个字节都不写**,提示出错位置;十六进制只读 |
| 成员表 | 暂时挡住二进制成员的编辑(见下),不做半吊子的写回 |
| 测试 | `RedisValueTextTests` 14 条(含 0..255 全字节往返、以及**复现损坏路径**那条)+ 打真实 Redis 的端到端 3 条 |

**顺带修掉的一个日常问题**:任何含换行的字符串值(多行 JSON、日志片段)此前都被显示成
`\n` 字面量 —— 因为值复用了**键名**的文本判定,而那条规则把所有控制字符都当作不可显示。
键名那么判是对的(带换行的键名会把列表行高搞乱),值这边不是。现在换行/回车/制表算文本。

**明确未做**:成员表(哈希字段 / 列表项 / 集合与有序集合成员)的读写仍是字符串。
`RedisConnection.Writes` 那一层的签名全是 `string`,`RedisElement` 也只带文本。
要做对得把这两处都改成字节,并给成员表配一套同样的形态开关 —— 是一轮独立的改动。
在那之前,成员表对二进制成员**拒绝写入**并提示改用控制台:少让改一次用户能自己发现,
多写坏一次用户根本看不见。

全仓 **2129 项全绿、0 警告**。

---

## 十二、明确不做的边界

| 项 | 为什么不做 |
|---|---|
| **RDB / AOF 文件解析** | 那是离线取证工具的活（`redis-rdb-tools` 一类），与"连上一个活实例操作它"是两种产品。真要看 RDB，先 `redis-server` 起来再连 |
| **数据迁移 / 同步作业** | 长时任务、断点续传、一致性校验——那是 `redis-shift`/`RIOT` 的战场。客户端只提供"选中这些键复制到另一个连接"（`DUMP`+`RESTORE`，同步、可中断、量级明确） |
| **`SYNC` / `PSYNC` 全量复制** | 客户端假装自己是从库会拉走整个数据集，一次误点就是一次生产事故 |
| **可视化 Lua 脚本 IDE** | 控制台的 `EVAL` 够用；做成 IDE 是另一个产品的体量 |
| **RedisSearch 全功能查询构建器** | v1 只做"看得见索引、能敲 `FT.SEARCH`"；查询构建器等真实需求 |
| **隔离进程模式** | 与协议能力同一条硬约束：宿主要向插件索取 Avalonia `Control` 挂进停靠区，原生控件跨进程嵌入已被弃用。代价与 S3 一致（插件崩溃影响宿主），对第一方同源构建的插件可接受 |
| **老数据迁移** | 无历史数据可迁——这是全新连接类型 |

---

## 十三、待决事项(需要拍板)

1. **M0 是否与本插件同期做**（本文建议：是，理由见 §十一末）。
2. **只读模式在生产标记下默认开启**——会不会让第一次使用的用户觉得"怎么改不了"？
   本文倾向默认开 + 标题栏显眼徽章 + 一键切换（带确认）；反向的代价是一次误删生产键。
3. **控制台与浏览器的 `SELECT` 是否联动**。本文倾向**联动并显式提示**
   （"控制台已切到 db3，浏览器已跟随"），因为静默分叉是更差的失败模式。
4. **模块支持的首发范围**：建议只做 JSON（用得最多、编辑器价值最大），
   Search/TimeSeries/Bloom 仅在控制台可达 + 概览里显示已加载模块列表。
