namespace VelaShell.Core.Ftp;

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

    /// <summary>关闭并释放一条 FTP 会话的全部连接;未知标识为空操作。</summary>
    Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
