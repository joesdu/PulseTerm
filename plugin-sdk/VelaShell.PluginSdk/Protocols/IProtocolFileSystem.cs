using System.Globalization;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.PluginSdk.Protocols;

/// <summary>
/// 宿主发起一条协议会话时递过来的全部参数。凭据是**一次性**的:宿主只在这一刻交出去,
/// 插件不应把它们落盘(要持久化请用 <see cref="Secrets.ISecretsApi" /> 存自己的东西)。
/// </summary>
public sealed record ProtocolConnectRequest
{
    /// <summary>主机名 / 端点(连接配置页的"主机"字段)。</summary>
    public required string Host { get; init; }

    /// <summary>端口。</summary>
    public required int Port { get; init; }

    /// <summary>用户名;匿名访问时为空串。</summary>
    public string Username { get; init; } = "";

    /// <summary>口令;匿名访问或无口令时为空串。</summary>
    public string Password { get; init; } = "";

    /// <summary>
    /// 协议专属设置:键为 <see cref="ProtocolSettingField.Key" />,值为用户所填
    /// (未填则为字段的 <see cref="ProtocolSettingField.DefaultValue" />)。
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>会话的展示名称(用户给这条配置起的名字),仅用于日志与界面提示。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>取某个设置的字符串值;缺失或空白时返回 <paramref name="fallback" />。</summary>
    public string GetString(string key, string fallback = "") =>
        Settings.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    /// <summary>取某个设置的布尔值;缺失或不可解析时返回 <paramref name="fallback" />。</summary>
    public bool GetBoolean(string key, bool fallback = false) =>
        Settings.TryGetValue(key, out string? value) && bool.TryParse(value, out bool parsed) ? parsed : fallback;

    /// <summary>取某个设置的整数值(不变文化);缺失或不可解析时返回 <paramref name="fallback" />。</summary>
    public long GetInt64(string key, long fallback = 0) =>
        Settings.TryGetValue(key, out string? value)
        && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : fallback;

    /// <summary>取某个设置的整数值(不变文化);缺失或不可解析时返回 <paramref name="fallback" />。</summary>
    public int GetInt32(string key, int fallback = 0) => (int)GetInt64(key, fallback);
}

/// <summary>
/// 删除(可能递归)的进度。宿主用它给大目录的删除画进度条;
/// 总数未知时给 0,界面退化为"仅显示已删除数"。
/// </summary>
/// <param name="DeletedCount">已删除的条目数。</param>
/// <param name="TotalCount">预计删除的总条目数;未知时为 0。</param>
/// <param name="CurrentPath">最近一次被删除的路径。</param>
public readonly record struct ProtocolDeleteProgress(int DeletedCount, int TotalCount, string CurrentPath);

/// <summary>一条协议会话的健康状态。</summary>
public enum ProtocolSessionState
{
    /// <summary>可用。</summary>
    Connected,

    /// <summary>端点不可达或凭据失效;会话仍在,但下一次操作需要重连。</summary>
    Faulted,

    /// <summary>会话已关闭并释放。</summary>
    Closed
}

/// <summary>会话状态变化。</summary>
/// <param name="SessionId">发生变化的会话标识(即 <see cref="IProtocolFileSystem.ConnectAsync" /> 收到的那个)。</param>
/// <param name="State">新状态。</param>
public readonly record struct ProtocolSessionStateChange(string SessionId, ProtocolSessionState State);

/// <summary>
/// 插件实现的远程文件系统。方法集与宿主内部的文件服务契约一一对应,
/// 因此实现它就等于让自己的协议**完整接入**双栏浏览器、传输队列、限速、拖放与冲突策略。
/// <para>
/// 会话以宿主分配的不透明 <c>sessionId</c> 为键(一条用户会话一个),生命周期由
/// <see cref="ConnectAsync" /> / <see cref="DisconnectAsync" /> 界定。收到未知
/// sessionId 时抛 <see cref="PluginSessionNotFoundException" />。
/// </para>
/// <para>
/// 纪律:全部方法都可能被并发调用(浏览器与传输队列各跑各的),实现须自行保证线程安全;
/// 进度回调可以放心地高频上报 —— 宿主侧已按 ≥100ms 节流并做了单调收敛。
/// </para>
/// </summary>
public interface IProtocolFileSystem
{
    /// <summary>
    /// 建立一条会话。应在这一步就把"地址写错 / 凭据不对 / 证书不可信"暴露出来,
    /// 而不是等用户点开目录才炸。
    /// </summary>
    /// <exception cref="ProtocolAuthenticationException">凭据无效;宿主会重新弹登录框。</exception>
    /// <exception cref="ProtocolCertificateTrustException">证书未通过校验;宿主会弹信任提示后重连。</exception>
    Task ConnectAsync(string sessionId, ProtocolConnectRequest request, CancellationToken cancellationToken = default);

    /// <summary>关闭并释放一条会话;未知 sessionId 为空操作(关闭路径不该再抛)。</summary>
    Task DisconnectAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>会话打开后落脚的初始路径(SFTP 的 home、S3 的桶列表或默认桶)。</summary>
    Task<string> GetHomePathAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>列举目录。</summary>
    Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>获取单个条目的元数据;不存在时返回 <see langword="null" />(勿以异常判存在)。</summary>
    Task<RemoteFileEntry?> StatAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>路径是否存在(文件或目录)。</summary>
    Task<bool> ExistsAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>打开只读顺序流(调用方负责释放)。</summary>
    Task<Stream> OpenReadAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 上传本地文件。<paramref name="resumeOffset" /> 仅在声明了
    /// <see cref="ProtocolFeatures.ResumeUpload" /> 时由宿主给出非零值。
    /// </summary>
    Task UploadFileAsync(string sessionId, string localPath, string remotePath,
        IProgress<RemoteTransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default);

    /// <summary>下载到本地文件;续传语义同上,见 <see cref="ProtocolFeatures.ResumeDownload" />。</summary>
    Task DownloadFileAsync(string sessionId, string remotePath, string localPath,
        IProgress<RemoteTransferProgress>? progress = null, long resumeOffset = 0, CancellationToken cancellationToken = default);

    /// <summary>删除文件,或递归删除目录及其全部内容,逐条回报进度。</summary>
    Task DeleteAsync(string sessionId, string path,
        IProgress<ProtocolDeleteProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>创建目录(已存在时报错)。</summary>
    Task CreateDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>创建一个空文件。</summary>
    Task CreateFileAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>确保目录存在(幂等);上传目录树时使用。</summary>
    Task EnsureDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default);

    /// <summary>重命名或移动。</summary>
    Task RenameAsync(string sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default);

    /// <summary>在同一服务器内复制文件或目录树。</summary>
    Task CopyAsync(string sessionId, string sourcePath, string destinationPath,
        IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改权限;<paramref name="octalMode" /> 是以十进制书写的三位八进制数(如 755)。
    /// 未声明 <see cref="ProtocolFeatures.Permissions" /> 的协议可直接抛
    /// <see cref="ProtocolUnsupportedException" />(宿主本就不会调它)。
    /// </summary>
    Task SetPermissionsAsync(string sessionId, string path, short octalMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行一条 <see cref="ProtocolDescriptor.Actions" /> 声明的协议专属动作。
    /// 在后台线程调用;要弹界面请走 <see cref="Ui.IUiApi" />(它会自己回 UI 线程)。
    /// </summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="actionId">动作 id。</param>
    /// <param name="path">用户右键的那个路径;<see cref="ProtocolActionScope.Background" /> 时为当前目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task InvokeActionAsync(string sessionId, string actionId, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 会话状态变化。无连接的协议(HTTP 系)没有可供订阅的长驻连接对象,
    /// 掉线只在下一次操作时暴露 —— 由实现在操作失败时主动上报,
    /// 资源管理器树上的状态圆点才能自动变灰。可在任意线程触发,宿主自行切回 UI 线程。
    /// </summary>
    event EventHandler<ProtocolSessionStateChange>? SessionStateChanged;
}
