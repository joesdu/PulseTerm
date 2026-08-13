namespace VelaShell.Core.Ftp;

/// <summary>一条 FTP 会话的健康状态。</summary>
public enum FtpSessionState
{
    /// <summary>已建立连接。</summary>
    Connected,

    /// <summary>连接已失效(服务器断开、网络中断、重连失败);会话仍在,但下一次操作需要重连。</summary>
    Faulted,

    /// <summary>会话已被关闭并释放。</summary>
    Closed,
}

/// <summary>FTP 会话状态变化的事件数据。</summary>
/// <param name="SessionId">发生变化的会话标识。</param>
/// <param name="State">新的状态。</param>
public readonly record struct FtpSessionStateChange(Guid SessionId, FtpSessionState State);

/// <summary>
/// FTP 会话的生命周期管理。文件操作本身仍走 <see cref="Sftp.ISftpService" />
/// —— 那个接口以 <c>Guid sessionId</c> 为键、返回 <c>RemoteFileInfo</c>,本就与协议无关,
/// 因此 FTP 复用它,文件浏览器/传输管理器/限速/拖放全部零改动。
/// </summary>
public interface IFtpSessionService
{
    /// <summary>
    /// 建立一条 FTP 会话并返回其标识;后续用该标识调用 <see cref="Sftp.ISftpService" /> 的成员。
    /// </summary>
    /// <exception cref="VelaFtpAuthenticationException">登录被拒。</exception>
    /// <exception cref="VelaFtpCertificateException">FTPS 证书未通过校验且未被信任。</exception>
    /// <exception cref="VelaFtpConnectionException">连接失败。</exception>
    Task<Guid> OpenSessionAsync(FtpConnectionInfo info, CancellationToken cancellationToken = default);

    /// <summary>该会话标识是否由本服务持有(供文件服务路由分派)。</summary>
    bool OwnsSession(Guid sessionId);

    /// <summary>
    /// 会话状态发生变化(连上 / 断开失效 / 已关闭)。
    /// <para>
    /// FTP 没有 SSH 那种长驻的会话对象可供界面订阅状态,断线只会在下一次操作时暴露出来。
    /// 因此由本服务在操作失败时主动上报,资源管理器树的状态圆点才能自动从「活跃」变成「离线」,
    /// 而不是一直停在绿点上。可能在任意线程触发,订阅方自行切回 UI 线程。
    /// </para>
    /// </summary>
    event EventHandler<FtpSessionStateChange>? SessionStateChanged;

    /// <summary>关闭并释放一条 FTP 会话的全部连接;未知标识为空操作。</summary>
    Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
