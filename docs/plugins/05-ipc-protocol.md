# 05 · IPC 协议

## 1. 分层

```text
应用层   IHostEndpoint / IPluginEndpoint(强类型接口,定义于 PluginProtocol)
协议层   JSON-RPC 2.0(StreamJsonRpc),MessagePack 二进制编码
帧层     StreamJsonRpc 内建 length-prefixed framing
传输层   Windows: NamedPipeServerStream(每插件一条,双工,异步)
         macOS/Linux: UnixDomainSocket(socket 文件位于用户运行时目录,0600)
侧通道   ① 流式数据通道(同一 RPC 连接上的 marshaled stream / 分块协议)
         ② 共享内存(MemoryMappedFile,仅图像表面等高带宽场景,见 08)
```

选型理由(D3 的展开):

- StreamJsonRpc:双向调用、通知、`CancellationToken` 与 `IProgress<T>`
  自动跨进程、异常序列化、成熟稳定(VS/VSCode 家族在用);MessagePack
  编码避免 JSON 文本膨胀。
- 不用 gRPC:不需要 HTTP/2、TLS、跨机;不想引 protoc 代码生成进插件
  开发者的内环。协议本身仍是语言中立的(JSON-RPC over pipe),远期非
  .NET SDK 可自行实现帧层与编码(届时提供 JSON 编码协商选项)。

## 2. 连接建立与认证

```text
1. 宿主创建管道/socket(随机名)→ 拉起 PluginHost(环境变量携带名字与令牌)
2. PluginHost 连接;10s 未连上 → 宿主判启动失败
3. 首帧(认证,原始帧,不走 JSON-RPC):magic "VELA" + authToken
   校验失败 → 立即断开并记安全日志
4. JSON-RPC 挂接,进入握手
```

## 3. 握手(handshake)

```jsonc
// PluginHost → 宿主  host/hello
{ "protocolVersion": 1,
  "apiLevels": [1],                  // PluginHost 侧支持的 apiLevel 集合
  "pluginId": "acme.image-viewer",
  "pluginVersion": "1.2.0",
  "sdkVersion": "1.3.0",
  "encodings": ["messagepack", "json"] }

// 宿主 → PluginHost  应答
{ "apiLevel": 1,                     // 协商结果(交集内取最高;空集 → 拒绝并给出双方版本)
  "encoding": "messagepack",
  "hostVersion": "0.3.0",
  "locale": "zh-Hans",
  "theme": "dark",
  "capabilities": ["remoteFs","terminal","ui","storage", ...] }  // 宿主实际具备的能力域
```

握手完成前除认证与 hello 外一切调用拒绝。之后宿主调
`plugin/activate(reason)` 进入插件生命周期。

## 4. 端点接口(应用层,节选)

```csharp
// 宿主暴露给插件(经 PluginHost 转发)。方法名 = "<域>/<动作>"。
public interface IHostEndpoint
{
    // —— 能力域调用(完整清单见 07,全部第一参数为隐式插件身份,由连接绑定,不可伪造)——
    Task<RemoteEntry[]> RemoteFsListAsync(string sessionId, string path, CancellationToken ct);
    Task<Stream> RemoteFsOpenReadAsync(string sessionId, string path, CancellationToken ct);
    Task TerminalWriteAsync(string sessionId, string input, CancellationToken ct);
    Task UiPatchAsync(string surfaceId, UiPatch[] patches, CancellationToken ct);
    Task<PermissionState> PermissionQueryAsync(string permissionId);
    // ... 通知(宿主→插件,无应答):事件推送
}

// 插件侧暴露给宿主
public interface IPluginEndpoint
{
    Task ActivateAsync(ActivationReason reason, CancellationToken ct);
    Task DeactivateAsync(string reason, CancellationToken ct);
    Task ExecuteCommandAsync(string commandId, JsonElement? args, CancellationToken ct);
    Task UiEventAsync(string surfaceId, UiEvent e, CancellationToken ct);   // VelaUI 事件回传
    Task OnHostEventAsync(HostEvent e);                                     // 订阅的事件(通知)
    Task<string> PingAsync();                                               // 心跳
    Task OnEnvironmentChangedAsync(EnvChange e);                            // 语言/主题切换
}
```

纪律:

- 接口只出现在 `PluginProtocol`;DTO 全部为不可变记录,MessagePack 键
  用整数 key 显式标注(改名安全),**新增字段只能追加可选项**(apiLevel
  兼容承诺的机械保障,CI 里跑二进制兼容检查)。
- 插件身份绑定在连接上:能力实现读连接元数据获得 pluginId,**协议里
  没有"以某插件身份调用"的参数**,从根上防冒充。

## 5. 错误模型

统一错误码空间(JSON-RPC error.code):

| 段 | 含义 | 例 |
| --- | --- | --- |
| -32xxx | JSON-RPC 保留 | 方法不存在、参数错误 |
| 1xxx | 通用宿主错误 | 1001 HostShuttingDown、1002 CapabilityUnavailable |
| 2xxx | 权限 | 2001 PermissionDenied(含 permissionId)、2002 PermissionPromptDismissed |
| 3xxx | 远程会话 | 3001 SessionNotFound、3002 SessionDisconnected、3003 SftpError(内嵌 VelaSsh 错误分类) |
| 4xxx | 文件系统 | 4001 NotFound、4002 AccessOutsideScope |
| 5xxx | UI | 5001 SurfaceClosed、5002 InvalidPatch |

SDK 把错误码还原为类型化异常(`PermissionDeniedException` 等);未知码
落到 `VelaPluginException` 基类。宿主内部异常细节(堆栈、路径)**不跨
进程下发**,只给分类码与安全的 message(防信息泄漏)。

## 6. 大块数据与流

三档策略,按数据量与形态选择:

1. **小(≤256KB)**:直接作为 MessagePack `byte[]` 内联在应答里
   (如读取小配置文件)。
2. **流式(文件传输、终端输出订阅)**:StreamJsonRpc 的 stream
   marshaling(管道上多路复用的子流),天然背压;取消即断流。
   终端输出订阅额外做**宿主侧节流合并**(≥16ms 窗口合帧),吸取
   perf-pass 的教训——高频上报必带节流,防止插件订阅拖慢终端热路径。
3. **高带宽表面(图像帧)**:MemoryMappedFile 共享内存 + RPC 只传帧
   元数据(mmf 名、尺寸、stride、序号),见 08 §4。共享内存段由宿主
   创建并限定当前用户访问。

## 7. 排序、并发与重入

- 同一连接上请求并发执行(StreamJsonRpc 默认),**不做全局串行**;
  需要顺序语义的域(如 `terminal/write`)在宿主能力实现内部按
  sessionId 排队——与现有"终端输入只准入队勿直写流"的纪律对齐。
- 宿主 → 插件的调用一律带超时(默认 5s,UI 事件 2s);插件慢不阻塞
  宿主任何路径(fire-and-forget + 超时观察)。
- 取消:`CancellationToken` 跨进程传播;连接断开等价于该连接全部
  未完成调用取消。

## 8. 开发计划(本分项)

| 任务 | 说明 | 依赖 | 估算 |
| --- | --- | --- | --- |
| P-1 | 传输层封装:管道/UDS 双实现 + 认证帧 + 连接生命周期(断开事件、半开检测) | A-2 | 3d |
| P-2 | 握手与 apiLevel 协商 + 编码协商;拒绝路径的可读错误 | P-1 | 2d |
| P-3 | 端点接口与 DTO 首批定稿(activate/deactivate/command/ping/事件);MessagePack 整数键纪律 + CI 二进制兼容检查 | P-2 | 3d |
| P-4 | 错误码空间 + SDK 异常映射 | P-3 | 1d |
| P-5 | 流式通道:stream marshaling 封装、终端订阅节流合并、背压测试 | P-3 | 3d |
| P-6 | 共享内存表面通道(创建/授权/回收协议) | P-3 | 2d |
| P-7 | 协议模糊测试:乱序帧、超长帧、非法 MessagePack、握手期调用(接 H-8 混沌组) | P-1..P-4 | 2d |

验收:双进程回环基准——空调用 P95 < 1ms,1MB 流吞吐 ≥ 500MB/s(本机
管道);协议模糊测试宿主侧零崩溃零挂起。
