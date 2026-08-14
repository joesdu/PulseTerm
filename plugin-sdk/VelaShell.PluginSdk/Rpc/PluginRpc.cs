using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.PluginSdk.Rpc;

/// <summary>
/// 隔离进程模式的方法名与线协 DTO(apiLevel 1)。
/// 方向:插件 → 宿主为能力调用;宿主 → 插件为生命周期与事件推送。
/// 纪律与 SDK 相同:同 apiLevel 只增不改;DTO 全部为不可变记录。
/// </summary>
public static class PluginRpc
{
    // ---- 插件 → 宿主 ----

    /// <summary>握手(连接后的第一个请求;其它一切在握手完成前被拒)。</summary>
    public const string Handshake = "handshake";

    /// <summary>日志通知。</summary>
    public const string LogWrite = "log/write";

    /// <summary>会话:列表 / 单查。</summary>
    public const string SessionsList = "sessions/list";

    /// <summary>会话单查。</summary>
    public const string SessionsGet = "sessions/get";

    /// <summary>远程执行。</summary>
    public const string ExecRun = "exec/run";

    /// <summary>远程文件:列目录。</summary>
    public const string FsList = "fs/list";

    /// <summary>远程文件:stat(不存在返回 null)。</summary>
    public const string FsStat = "fs/stat";

    /// <summary>远程文件:存在性。</summary>
    public const string FsExists = "fs/exists";

    /// <summary>远程文件:工作目录。</summary>
    public const string FsWorkingDirectory = "fs/cwd";

    /// <summary>远程文件:下载到本地路径(同机文件系统)。</summary>
    public const string FsDownload = "fs/download";

    /// <summary>远程文件:上传本地路径。</summary>
    public const string FsUpload = "fs/upload";

    /// <summary>远程文件:读小文件(base64 内联)。</summary>
    public const string FsReadAll = "fs/readAll";

    /// <summary>远程文件:写小文件(base64 内联)。</summary>
    public const string FsWriteAll = "fs/writeAll";

    /// <summary>远程文件:删除。</summary>
    public const string FsDelete = "fs/delete";

    /// <summary>远程文件:建目录(已存在报错)。</summary>
    public const string FsCreateDirectory = "fs/mkdir";

    /// <summary>远程文件:确保目录(幂等)。</summary>
    public const string FsEnsureDirectory = "fs/ensureDir";

    /// <summary>远程文件:改名/移动。</summary>
    public const string FsRename = "fs/rename";

    /// <summary>传输进度通知(宿主 → 插件,带 progressToken)。</summary>
    public const string FsProgress = "fs/progress";

    /// <summary>流式读取:打开远端文件只读流(应答 streamId + 长度)。</summary>
    public const string FsOpenRead = "fs/openRead";

    /// <summary>流式读取:顺序拉取下一块(base64;Eof 标记结束)。</summary>
    public const string FsStreamRead = "fs/streamRead";

    /// <summary>流式读取:关闭并释放宿主侧流。</summary>
    public const string FsStreamClose = "fs/streamClose";

    /// <summary>命令注册 / 注销 / 执行宿主命令。</summary>
    public const string CommandsRegister = "commands/register";

    /// <summary>命令注销。</summary>
    public const string CommandsUnregister = "commands/unregister";

    /// <summary>执行宿主命令。</summary>
    public const string CommandsTryExecute = "commands/tryExecute";

    /// <summary>
    /// KV 存储:读 / 写 / 删 / 键列举。数据落宿主 SonnetDB(按插件 id 命名空间隔离,
    /// 卸载整体清除),隔离进程不落本地文件。
    /// </summary>
    public const string StorageGet = "storage/get";

    /// <summary>KV 写入。</summary>
    public const string StorageSet = "storage/set";

    /// <summary>KV 删除。</summary>
    public const string StorageRemove = "storage/remove";

    /// <summary>KV 键列举。</summary>
    public const string StorageKeys = "storage/keys";

    /// <summary>
    /// 时序:打开(必要时创建)measurement。宿主侧记住句柄,后续调用按短名寻址;
    /// 数据同样落宿主 SonnetDB(按插件 id 命名空间隔离,卸载整体清除)。
    /// </summary>
    public const string TimeSeriesOpen = "ts/open";

    /// <summary>时序:列出本插件的 measurement 短名。</summary>
    public const string TimeSeriesList = "ts/list";

    /// <summary>时序:删除 measurement 及其数据。</summary>
    public const string TimeSeriesDrop = "ts/drop";

    /// <summary>时序:批量写点。</summary>
    public const string TimeSeriesWrite = "ts/write";

    /// <summary>时序:按条件查询数据点。</summary>
    public const string TimeSeriesQuery = "ts/query";

    /// <summary>时序:按条件计数。</summary>
    public const string TimeSeriesCount = "ts/count";

    /// <summary>时序:列举某标签列的去重取值。</summary>
    public const string TimeSeriesDistinct = "ts/distinct";

    /// <summary>时序:按标签删除序列数据。</summary>
    public const string TimeSeriesDelete = "ts/delete";

    /// <summary>机密:读 / 写 / 删(机密只存宿主侧,值仅在本机管道上瞬时传输)。</summary>
    public const string SecretsGet = "secrets/get";

    /// <summary>机密写入。</summary>
    public const string SecretsSet = "secrets/set";

    /// <summary>机密删除。</summary>
    public const string SecretsDelete = "secrets/delete";

    /// <summary>剪贴板:读文本 / 写文本(经宿主执行)。</summary>
    public const string ClipboardGetText = "clipboard/getText";

    /// <summary>剪贴板写文本。</summary>
    public const string ClipboardSetText = "clipboard/setText";

    /// <summary>终端:读输出。</summary>
    public const string TerminalGetOutput = "terminal/getOutput";

    /// <summary>终端:搜索输出。</summary>
    public const string TerminalSearch = "terminal/search";

    /// <summary>终端:回写输入(需用户授权;拒绝时应答 permission-denied)。</summary>
    public const string TerminalWrite = "terminal/write";

    /// <summary>面板数变化通知(插件 → 宿主):驱动"无打开面板"的空闲回收条件。</summary>
    public const string UiSurfaces = "ui/surfaces";

    /// <summary>
    /// 停靠嵌入请求(插件 → 宿主,仅 Windows):把插件进程的无边框窗口(HWND)
    /// 收养进宿主的停靠文档区,用户可拖拽分栏。宿主经握手宣告是否支持。
    /// </summary>
    public const string UiEmbedPanel = "ui/embed";

    /// <summary>关闭嵌入面板(插件 → 宿主,程序性)。</summary>
    public const string UiClosePanel = "ui/close";

    // 注:界面不走 RPC —— 隔离插件的 UI 由其进程内自带的 Avalonia 直接呈现
    // (PluginHost 内建派发循环),原生控件无需也无法跨进程传输。

    // ---- 宿主 → 插件 ----

    /// <summary>激活插件(握手成功后宿主发起)。</summary>
    public const string PluginActivate = "plugin/activate";

    /// <summary>停用插件(应答后插件进程自行退出)。</summary>
    public const string PluginDeactivate = "plugin/deactivate";

    /// <summary>命令被用户触发(通知)。</summary>
    public const string CommandExecute = "command/execute";

    /// <summary>宿主事件通知:kind = sessionConnected / sessionDisconnected / themeChanged / localeChanged。</summary>
    public const string HostEvent = "host/event";

    /// <summary>嵌入面板已在宿主侧关闭(宿主 → 插件通知:用户关标签/插件停用)。</summary>
    public const string UiPanelClosed = "ui/closed";

    /// <summary>
    /// 主题令牌快照通知(握手后与每次主题切换时下发):PluginHost 把宿主的
    /// <c>Vela*</c> 资源令牌注入本进程 Application 资源,插件的
    /// <c>{DynamicResource VelaXxx}</c> 在隔离模式下同样生效。
    /// </summary>
    public const string ThemeTokens = "theme/tokens";
}

/// <summary>握手请求(插件 → 宿主)。</summary>
/// <param name="Token">宿主拉起进程时经环境变量下发的一次性令牌。</param>
/// <param name="PluginId">插件 id(必须与宿主拉起的目标一致)。</param>
/// <param name="PluginVersion">插件版本。</param>
/// <param name="ApiLevels">PluginHost 侧支持的 apiLevel 集合。</param>
public sealed record HandshakeRequest(string Token, string PluginId, string PluginVersion, int[] ApiLevels);

/// <summary>握手应答(宿主 → 插件)。</summary>
/// <param name="ApiLevel">协商结果(交集内取最高)。</param>
/// <param name="HostVersion">宿主版本。</param>
/// <param name="Locale">当前语言。</param>
/// <param name="Theme">当前主题。</param>
/// <param name="SupportsEmbedding">宿主是否支持停靠嵌入(HWND 收养,仅 Windows)。</param>
public sealed record HandshakeResponse(int ApiLevel, string HostVersion, string Locale, string Theme,
    bool SupportsEmbedding = false);

/// <summary>会话查询参数。</summary>
public sealed record SessionRef(string SessionId);

/// <summary>远程执行参数。</summary>
public sealed record ExecRunRequest(string SessionId, string Command, double TimeoutSeconds);

/// <summary>远程文件通用参数(路径类)。</summary>
public sealed record FsPathRequest(string SessionId, string Path);

/// <summary>远程文件传输参数(远端 ↔ 本地路径;同一台机器的文件系统)。</summary>
/// <param name="SessionId">会话 id。</param>
/// <param name="RemotePath">远端路径。</param>
/// <param name="LocalPath">本地路径。</param>
/// <param name="ProgressToken">进度通知令牌;null = 不要进度。</param>
public sealed record FsTransferRequest(string SessionId, string RemotePath, string LocalPath, string? ProgressToken);

/// <summary>传输进度通知载荷。</summary>
public sealed record FsProgressNotification(string ProgressToken, long TransferredBytes, long TotalBytes);

/// <summary>读小文件参数。</summary>
public sealed record FsReadAllRequest(string SessionId, string Path, int MaxBytes);

/// <summary>流式读取:打开应答(长度未知时为 -1)。</summary>
public sealed record FsOpenReadResponse(string StreamId, long Length);

/// <summary>流式读取:拉块参数(顺序读,单块上限由宿主钳制)。</summary>
public sealed record FsStreamReadRequest(string StreamId, int MaxBytes);

/// <summary>流式读取:块应答(<see cref="Eof" /> 为 true 时流已尽,宿主侧已自动释放)。</summary>
public sealed record FsStreamReadResponse(string DataBase64, bool Eof);

/// <summary>流 id 载荷。</summary>
public sealed record FsStreamRef(string StreamId);

/// <summary>写小文件参数(内容 base64)。</summary>
public sealed record FsWriteAllRequest(string SessionId, string Path, string ContentBase64);

/// <summary>改名参数。</summary>
public sealed record FsRenameRequest(string SessionId, string OldPath, string NewPath);

/// <summary>命令注册参数(回调留在插件进程,宿主触发时发 <see cref="PluginRpc.CommandExecute" /> 通知)。</summary>
public sealed record CommandRegistration(string Id, string Title, string Category);

/// <summary>命令 id 载荷。</summary>
public sealed record CommandRef(string Id);

/// <summary>日志通知载荷。</summary>
public sealed record LogNotification(PluginLogLevel Level, string Message, string? Exception);

/// <summary>宿主事件通知载荷(kind 决定有效字段)。</summary>
/// <param name="Kind">sessionConnected / sessionDisconnected / themeChanged / localeChanged。</param>
/// <param name="Session">会话事件的载荷。</param>
/// <param name="Value">主题/语言事件的载荷。</param>
public sealed record HostEventNotification(string Kind, SessionInfo? Session, string? Value);

/// <summary>激活参数(预留激活原因;v1 恒为启动激活)。</summary>
public sealed record ActivateRequest(string Reason);

/// <summary>
/// 一个主题令牌的已解析值(按宿主当前明暗变体解析)。
/// Kind:brush(#AARRGGBB)/ color(#AARRGGBB)/ double(不变体区域格式)/ font(字体族回退串)。
/// </summary>
public sealed record ThemeTokenDto(string Key, string Kind, string Value);

/// <summary>主题令牌快照通知载荷。</summary>
public sealed record ThemeTokensNotification(ThemeTokenDto[] Tokens);

/// <summary>KV 键载荷。</summary>
public sealed record StorageKeyRef(string Key);

/// <summary>KV 写入载荷(值为任意 JSON)。</summary>
public sealed record StorageSetRequest(string Key, System.Text.Json.JsonElement Value);

/// <summary>时序:打开 measurement 的载荷。</summary>
public sealed record TimeSeriesOpenRequest(TimeSeries.TimeSeriesDefinition Definition);

/// <summary>时序:measurement 短名载荷(drop / distinct 等)。</summary>
public sealed record TimeSeriesNameRef(string Name);

/// <summary>时序:批量写入载荷。</summary>
public sealed record TimeSeriesWriteRequest(string Name, TimeSeries.TimeSeriesPoint[] Points);

/// <summary>时序:查询载荷。</summary>
public sealed record TimeSeriesQueryRequest(string Name, TimeSeries.TimeSeriesQuery Query);

/// <summary>时序:计数载荷。</summary>
public sealed record TimeSeriesCountRequest(string Name, string Field, TimeSeries.TimeSeriesQuery Query);

/// <summary>时序:标签去重取值载荷。</summary>
public sealed record TimeSeriesDistinctRequest(string Name, string Tag);

/// <summary>时序:按标签删除载荷(标签为空 = 清空该 measurement 的数据)。</summary>
public sealed record TimeSeriesDeleteRequest(string Name, Dictionary<string, string>? Tags);

/// <summary>机密名载荷。</summary>
public sealed record SecretRef(string Name);

/// <summary>机密写入载荷(仅在本机管道上瞬时传输,宿主落盘前加密)。</summary>
public sealed record SecretSetRequest(string Name, string Value);

/// <summary>剪贴板写文本载荷。</summary>
public sealed record ClipboardSetRequest(string Text);

/// <summary>终端读输出载荷。</summary>
public sealed record TerminalGetOutputRequest(string SessionId, int MaxLines);

/// <summary>终端搜索载荷。</summary>
public sealed record TerminalSearchRequest(string SessionId, string Pattern, bool IsRegex, int MaxMatches);

/// <summary>终端搜索命中(线协)。</summary>
public sealed record TerminalMatchDto(int Line, string Text);

/// <summary>终端回写载荷。</summary>
public sealed record TerminalWriteRequest(string SessionId, string Input);

/// <summary>面板数变化通知载荷。</summary>
public sealed record UiSurfacesNotification(int Count);

/// <summary>停靠嵌入请求载荷。</summary>
/// <param name="Title">标签标题。</param>
/// <param name="Hwnd">插件进程无边框窗口的原生句柄。</param>
public sealed record UiEmbedRequest(string Title, long Hwnd);

/// <summary>停靠嵌入应答载荷。</summary>
public sealed record UiEmbedResponse(string PanelId);

/// <summary>面板 id 载荷。</summary>
public sealed record UiPanelRef(string PanelId);
