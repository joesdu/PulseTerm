using System.Globalization;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.PluginSdk.Workspaces;

/// <summary>
/// 宿主代为建立的 SSH 隧道信息(仅当连接配置里选了跳板会话时非空)。
/// 插件**不需要**用它来连接 —— <see cref="WorkspaceConnectRequest.Host" /> 已是本地转发端点;
/// 它的用途是让插件在界面上如实显示"↝ bastion-01"这样的来路。
/// </summary>
/// <param name="TargetHost">隧道另一端的真实目标主机。</param>
/// <param name="TargetPort">真实目标端口。</param>
/// <param name="JumpDisplayName">跳板会话的展示名称。</param>
public sealed record WorkspaceTunnelInfo(string TargetHost, int TargetPort, string JumpDisplayName);

/// <summary>
/// 宿主打开一条工作台会话时递过来的全部参数。凭据是**一次性**的:宿主只在这一刻交出去,
/// 插件不应把它们落盘(要持久化自己的东西请用 <see cref="Secrets.ISecretsApi" />)。
/// </summary>
public sealed record WorkspaceConnectRequest
{
    /// <summary>宿主分配的不透明会话 id(一条用户会话一个)。</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// 要连接的主机。**走隧道时这里已是宿主建好的本地转发端点**(真实目标见
    /// <see cref="Tunnel" />),插件一律直接连它。
    /// </summary>
    public required string Host { get; init; }

    /// <summary>要连接的端口(同上,走隧道时为本地转发端口)。</summary>
    public required int Port { get; init; }

    /// <summary>用户名;匿名访问时为空串。</summary>
    public string Username { get; init; } = "";

    /// <summary>口令;匿名访问或无口令时为空串。</summary>
    public string Password { get; init; } = "";

    /// <summary>
    /// 专属设置:键为 <see cref="ProtocolSettingField.Key" />,值为用户所填
    /// (未填则为字段的 <see cref="ProtocolSettingField.DefaultValue" />)。
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>会话的展示名称(用户给这条配置起的名字),用于标签页标题与日志。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>宿主代为建立的隧道;未走隧道时为 <see langword="null" />。</summary>
    public WorkspaceTunnelInfo? Tunnel { get; init; }

    /// <summary>取某个设置的字符串值;缺失或空白时返回 <paramref name="fallback" />。</summary>
    public string GetString(string key, string fallback = "") =>
        Settings.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    /// <summary>取某个设置的布尔值;缺失或不可解析时返回 <paramref name="fallback" />。</summary>
    public bool GetBoolean(string key, bool fallback = false) =>
        Settings.TryGetValue(key, out string? value) && bool.TryParse(value, out bool parsed) ? parsed : fallback;

    /// <summary>取某个设置的 64 位整数值(不变文化);缺失或不可解析时返回 <paramref name="fallback" />。</summary>
    public long GetInt64(string key, long fallback = 0) =>
        Settings.TryGetValue(key, out string? value)
        && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : fallback;

    /// <summary>取某个设置的 32 位整数值(不变文化);缺失或不可解析时返回 <paramref name="fallback" />。</summary>
    public int GetInt32(string key, int fallback = 0) => (int)GetInt64(key, fallback);
}

/// <summary>
/// 一条工作台会话的状态快照。宿主用它画标签页上的状态圆点、会话树的状态与状态栏文案。
/// </summary>
/// <param name="State">健康状态(复用协议侧的三态语义)。</param>
/// <param name="Message">一行给人看的状态说明(如"已连接 · 7.2.4 · 12 ms");可为空。</param>
/// <param name="LatencyMs">最近一次探活往返延迟(毫秒);未知时为 <see langword="null" />。</param>
public readonly record struct WorkspaceStatus(ProtocolSessionState State, string? Message = null, int? LatencyMs = null);

/// <summary>
/// 一条已打开的工作台文档:**插件持有连接与界面**,宿主只负责标签页外壳与状态呈现。
/// <para>
/// 生命周期:<see cref="IWorkspaceProvider.OpenAsync" /> 造出来 →
/// 宿主在 UI 线程调 <see cref="CreateView" /> 取控件挂进停靠区 →
/// 用户关闭标签页或插件停用时 <see cref="IAsyncDisposable.DisposeAsync" />。
/// </para>
/// </summary>
public interface IWorkspaceDocument : IAsyncDisposable
{
    /// <summary>
    /// 创建文档的内容控件。**由宿主在 UI 线程调用一次**,必须返回一个
    /// <c>Avalonia.Controls.Control</c>;之后对控件的操作请经 <c>Dispatcher.UIThread</c> 封送。
    /// </summary>
    /// <returns>文档内容控件。</returns>
    object CreateView();

    /// <summary>当前状态。</summary>
    WorkspaceStatus Status { get; }

    /// <summary>状态变化(**可能在任意线程触发**,宿主自行封送)。</summary>
    event EventHandler<WorkspaceStatus>? StatusChanged;

    /// <summary>
    /// 重连(宿主标签页上的"重连"按钮)。失败时抛协议异常族里的对应类型,
    /// 由宿主翻成用户可读的原因。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    Task ReconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 插件实现的工作台提供方:按宿主的请求打开一条会话并交出一个文档。
/// <para>
/// 异常约定与协议能力完全一致:<see cref="ProtocolAuthenticationException" />(宿主重弹登录框)、
/// <see cref="ProtocolCertificateTrustException" />(宿主弹信任提示、记指纹后重连)、
/// <see cref="ProtocolConnectionException" />、<see cref="ProtocolUnsupportedException" />。
/// </para>
/// </summary>
public interface IWorkspaceProvider
{
    /// <summary>打开一条会话。**须在返回前完成首次连接**,以便宿主把失败原因呈现在连接流程里。</summary>
    /// <param name="request">连接请求(含一次性凭据)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的文档。</returns>
    Task<IWorkspaceDocument> OpenAsync(WorkspaceConnectRequest request, CancellationToken cancellationToken = default);
}
