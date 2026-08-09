# 07 · 能力 API(Capability APIs)

插件能做的一切都经由能力域;每个域对应:SDK 代理接口(插件侧)→ RPC
方法族(05)→ 宿主能力服务(权限 Demand → 桥接到既有子系统)。本文档
定义 apiLevel 1 的域清单与关键签名(C# 形式为 SDK 视角)。

设计纪律:

- 每个方法标注所需权限;无权限方法不存在于清单即不存在于协议。
- 全部异步、全部可取消;可能长跑的带 `IProgress<T>`。
- 路径、会话等标识使用宿主颁发的不透明 id,插件不接触内部对象。
- 桥接实现**只依赖 `Core.*` 中立接口**(如 `Core.Ssh.ISftpClient`),
  延续"库类型不出 Infrastructure"的纪律。

## 1. vela.sessions — 会话

权限:`sessions.observe.basic`(脱敏列表)/ `sessions.read`(完整)/
`sessions.create`(发起)。

```csharp
public interface ISessions
{
    Task<SessionInfo[]> ListAsync(CancellationToken ct);          // 字段随权限级别脱敏
    IAsyncEnumerable<SessionEvent> WatchAsync(CancellationToken ct); // 连接/断开/重连事件
    Task<string> ConnectAsync(string savedProfileId, CancellationToken ct); // sessions.create
}
```

## 2. vela.remoteFs — 远程文件(核心场景 S1/S2/S7)

权限:`remote.files.read` / `remote.files.write`,scope 支持会话与路径
前缀。实现桥接现有 SFTP 包装(注意既有语义:GetAttributes 对不存在路
径返回 null,不以异常判存在——SDK 的 `StatAsync` 同样返回可空)。

```csharp
public interface IRemoteFs
{
    Task<RemoteEntry[]> ListAsync(string sessionId, string path, CancellationToken ct);
    Task<RemoteEntry?> StatAsync(string sessionId, string path, CancellationToken ct);
    Task<Stream> OpenReadAsync(string sessionId, string path, CancellationToken ct);
    Task<Stream> OpenWriteAsync(string sessionId, string path, WriteMode mode, CancellationToken ct);
    Task DownloadAsync(string sessionId, string remotePath, string localPath,   // localPath 需同时有 fs.local.write
                       IProgress<TransferProgress>? progress, CancellationToken ct);
    Task UploadAsync(...);
    Task DeleteAsync/RenameAsync/MkdirAsync(...);
    IAsyncEnumerable<RemoteChange> PollWatchAsync(string sessionId, string path,
                       TimeSpan interval, CancellationToken ct);  // 轮询式 watch,间隔下限 2s(保护远端)
}
```

宿主侧保护:单插件并发 SFTP 操作上限(默认 4)、带宽占用与用户前台
传输错峰(插件流量入低优先级队列)、进度回调节流(≥100ms)——吸取
大文件传输卡顿的历史教训,插件流量不允许灌爆 UI 调度器。

## 3. vela.remoteExec — 远程执行(S5/S6)

权限:`remote.exec`(高敏)。走**独立 exec 通道**,不进用户终端、不污
染用户 shell 历史与环境。

```csharp
public interface IRemoteExec
{
    Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions opts, CancellationToken ct);
    // ExecOptions: 超时(默认 30s,上限 10min)、stdin、环境变量、最大输出(默认 4MB,截断标记)
    IAsyncEnumerable<ExecOutputChunk> StreamAsync(...);            // 长命令流式输出
}
```

审计:每次执行的完整命令文本入审计日志(§06-4),不可关闭。

## 4. vela.terminal — 终端(S3/S4)

权限:`terminal.read` / `terminal.write`(高敏)。

```csharp
public interface ITerminal
{
    IAsyncEnumerable<TerminalOutput> SubscribeAsync(string sessionId, CancellationToken ct);
        // 宿主侧 ≥16ms 合帧节流;订阅建立时 Demand 一次,撤销即断流
    Task<TerminalSnapshot> GetSnapshotAsync(string sessionId, CancellationToken ct);  // 屏幕+滚回尾部(行数上限)
    Task<string?> GetSelectionAsync(string sessionId, CancellationToken ct);
    Task WriteAsync(string sessionId, string input, CancellationToken ct);
        // 经现有输入串行化队列(勿直写流);默认单次 ≤4KB
    Task<string> CreateOutputChannelAsync(string title, CancellationToken ct);
    Task AppendToChannelAsync(string channelId, string text, CancellationToken ct);
        // 插件专属只读输出页(挂 VelaDock 文档),不需要 terminal 权限,仅 ui.contributions
}
```

`terminal.write` 附加护栏:注入的输入在终端内以角标短暂标识来源插件
(用户可在设置关闭);单插件写入频率限速(默认 20 次/min,防失控刷屏),
超限熔断并通知用户。

## 5. vela.localFs — 本地文件

权限:`fs.local.read` / `fs.local.write`(scope=目录前缀);
**免权限路径**:经 `PickFileAsync`/`PickFolderAsync`(宿主弹系统选择
器)获得的路径自动获得临时授权(用户亲手选择即授权,Android SAF 思
路)——鼓励插件优先用选择器而非申请宽目录。

```csharp
public interface ILocalFs
{
    Task<string?> PickFileAsync(FilePickerOptions opts, CancellationToken ct);
    Task<string?> PickFolderAsync(..., CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
    Task<Stream> OpenWriteAsync(string path, WriteMode mode, CancellationToken ct);
    Task<LocalEntry[]> ListAsync(string path, CancellationToken ct);
    // 常规 Stat/Delete/Move/Copy…
}
```

## 6. vela.storage / vela.secrets / vela.settings

```csharp
public interface IStorage      // 权限:storage.private(普通)
{
    Task<T?> GetAsync<T>(string key);   Task SetAsync<T>(string key, T value);
    Task RemoveAsync(string key);       string DataDirectory { get; }   // 直接文件读写也限于此目录
}
public interface ISecrets      // 权限:secrets(危险);命名空间强制为 <pluginId>/
{
    Task<string?> GetAsync(string name); Task SetAsync(string name, string value); Task DeleteAsync(string name);
}   // 底层走 OS 凭据库(DPAPI/Keychain/libsecret),与宿主凭据不同命名空间,互不可见
public interface IPluginSettings   // 权限:settings.own(普通)
{
    T Get<T>(string key);  Task SetAsync<T>(string key, T value);
    IAsyncEnumerable<SettingChange> WatchAsync(CancellationToken ct);   // 用户在设置页改动时推送
}
```

## 7. vela.ui — 界面(详见 08)

权限:`ui.contributions`(普通)。包含:命令/菜单/状态栏动态更新、
VelaUI 表面(面板与文档页)、对话框(Message/Confirm/Input/QuickPick)、
应用内通知(频控:每插件 ≤6 条/min)、进度指示(状态栏/通知内)。

## 8. vela.events — 宿主事件订阅(S5)

权限:事件本体多数随对应域权限(如会话事件随 `sessions.read`);
`appStartup/appShutdown/themeChanged/localeChanged` 免权限。

```csharp
public interface IEvents
{
    IAsyncEnumerable<HostEvent> SubscribeAsync(EventFilter filter, CancellationToken ct);
    // HostEvent:SessionConnected/Disconnected、TransferCompleted、ThemeChanged、
    //           LocaleChanged、AppShutdown(给插件 flush 机会)、ScheduleFired(见 11)
}
```

## 9. vela.audio — 音频输出(S2)

权限:`audio.playback`。设计取舍:**插件解码、宿主输出**——宿主不内置
编解码(避免编解码器依赖膨胀与授权问题),只提供 PCM 混音输出;MP3
解码由插件自带托管解码器(如 NLayer,官方示例演示)。

```csharp
public interface IAudio
{
    Task<IAudioTrack> CreateTrackAsync(AudioFormat fmt, CancellationToken ct); // fmt: 采样率/位深/声道
    // IAudioTrack: WriteAsync(pcm 分块,背压)、暂停/恢复/停止、音量、position 反馈
}
```

宿主输出后端:待选型 spike(候选:miniaudio 绑定 / OpenAL Soft /
平台原生 WASAPI+CoreAudio+ALSA 三实现);占位接口先行,选型不阻塞
其它域。同一时刻全局混音,插件音量受宿主主音量与静音开关约束;
插件被停用/崩溃时其音轨立即静音回收。

## 10. vela.net — 网络(声明制)

权限:`network` + manifest 声明域名列表。v1 无物理拦截(见 06 前提),
SDK 提供 `ctx.Http`(基于 HttpClient 的便捷封装,自动带插件 UA);
使用 SDK 封装的请求会经宿主审计通道记录目标域名。直接自建 HttpClient
在 v1 无法禁止——合规性靠审核与签名,文档如实告知。

## 11. vela.clipboard / 通知 / i18n

- `clipboard.read/write`:读写系统剪贴板(读为危险权限——剪贴板常含
  密码)。
- `notifications.system`:OS 级通知(应用内通知归 vela.ui,免此权限)。
- `i18n.read`:当前 locale、翻译好的宿主常用词条(按钮"确定/取消"等,
  帮插件文案与宿主一致)。

## 12. vela.ai — AI 网关(远期,详见 11-automation-and-ai)

权限:`ai.invoke`。占位于 apiLevel 1(接口预留、宿主可宣告
capability 不可用),完整落地见 [11-automation-and-ai.md](11-automation-and-ai.md)。

## 13. 开发计划(本分项)

批次划分与里程碑对应(见 14):

| 任务 | 内容 | 依赖 | 估算 |
| --- | --- | --- | --- |
| C-1 | 能力服务基座:注册表、Demand 基类、审计挂点、每插件限流器 | B-2, P-3 | 3d |
| C-2 | 第一批:storage / settings / secrets / events(基础域,示例插件最小依赖) | C-1 | 4d |
| C-3 | 第二批:sessions / remoteFs(桥 Core.Ssh、低优先级传输队列、进度节流) | C-1 | 5d |
| C-4 | 第三批:localFs(选择器临时授权模型)+ clipboard + notifications | C-1 | 3d |
| C-5 | 第四批:terminal(订阅合帧、快照、写入限速熔断)+ remoteExec(审计) | C-3 | 5d |
| C-6 | 音频输出 spike(三候选评估报告)→ IAudio 实现 | C-1 | 5d |
| C-7 | vela.net SDK 封装 + 审计 | C-1 | 1d |
| C-8 | 契约测试套件:每个域"权限矩阵 × 正常/边界/取消/断连"用例(接 13) | 各批次 | 持续 |

验收:S1 所需域(C-2/C-3)可支撑图片查看器示例端到端;C-5 完成后
S3/S4/S5 的域全部可用。
