using VelaShell.PluginSdk.Events;

namespace VelaShell.PluginSdk.Sessions;

/// <summary>会话状态(与宿主内部状态机的脱敏投影)。</summary>
public enum SessionState
{
    /// <summary>正在建立连接。</summary>
    Connecting,

    /// <summary>已连接。</summary>
    Connected,

    /// <summary>已断开。</summary>
    Disconnected,

    /// <summary>连接失败或异常断开。</summary>
    Error
}

/// <summary>
/// 一条 SSH 会话的脱敏信息。不含任何凭据(密码、私钥、口令永不出宿主核心)。
/// </summary>
/// <param name="SessionId">会话的不透明 id,作为其它能力(远程文件、远程执行)的第一参数。</param>
/// <param name="Host">主机名或 IP。</param>
/// <param name="Port">端口。</param>
/// <param name="Username">登录用户名。</param>
/// <param name="State">当前状态。</param>
/// <param name="CreatedAt">会话创建时间(UTC)。</param>
/// <param name="ConnectedAt">最近一次连接成功时间(UTC),未连接过为 <see langword="null" />。</param>
public sealed record SessionInfo(
    string SessionId,
    string Host,
    int Port,
    string Username,
    SessionState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConnectedAt);

/// <summary>
/// 会话能力:枚举与查询当前 SSH 会话。会话由用户在宿主 UI 中建立,
/// v1 插件不能自行发起连接。连接/断开的推送见 <see cref="IHostEvents" />。
/// </summary>
public interface ISessionsApi
{
    /// <summary>当前全部会话的快照。</summary>
    Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>按 id 查询会话;不存在时返回 <see langword="null" />。</summary>
    Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default);
}
