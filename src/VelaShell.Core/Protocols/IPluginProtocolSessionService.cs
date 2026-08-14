using VelaShell.Core.Models;

namespace VelaShell.Core.Protocols;

/// <summary>一条插件协议会话的健康状态。</summary>
public enum PluginProtocolSessionState
{
    /// <summary>可用。</summary>
    Connected,

    /// <summary>端点不可达或凭据失效;会话仍在,但下一次操作需要重连。</summary>
    Faulted,

    /// <summary>会话已关闭并释放。</summary>
    Closed
}

/// <summary>插件协议会话状态变化的事件数据。</summary>
/// <param name="SessionId">发生变化的会话标识。</param>
/// <param name="State">新状态。</param>
public readonly record struct PluginProtocolSessionStateChange(Guid SessionId, PluginProtocolSessionState State);

/// <summary>
/// 由插件提供的远程文件协议的会话生命周期。文件操作本身仍走
/// <see cref="Sftp.ISftpService" /> —— 那个接口以 <c>Guid sessionId</c> 为键、
/// 返回协议无关的 <see cref="RemoteFileInfo" />,因此双栏浏览器、传输管理器、限速与拖放
/// 对「协议来自插件」这件事完全无感(与 FTP 当年接进来是同一条路子)。
/// </summary>
public interface IPluginProtocolSessionService
{
    /// <summary>
    /// 按会话配置建立一条插件协议会话并返回其标识。若对应插件尚未激活,
    /// 这一步会先触发它的惰性激活。
    /// </summary>
    /// <param name="profile">会话配置(<see cref="SessionProfile.PluginProtocolId" /> 决定用哪种协议)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>会话标识。</returns>
    /// <exception cref="PluginProtocolUnavailableException">协议未注册(插件未安装/被禁用/激活失败)。</exception>
    /// <exception cref="PluginProtocolAuthenticationException">凭据无效。</exception>
    /// <exception cref="PluginProtocolCertificateException">服务器证书未通过校验且未被信任。</exception>
    Task<Guid> OpenSessionAsync(SessionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>关闭并释放一条会话;未知标识为空操作。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>该会话标识是否由插件协议持有(供文件服务路由分派)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <returns>是否持有。</returns>
    bool OwnsSession(Guid sessionId);

    /// <summary>执行一条协议专属的右键动作(由插件自行处置,通常是打开它自己的面板)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="actionId">动作 id。</param>
    /// <param name="path">用户右键的路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task InvokeActionAsync(Guid sessionId, string actionId, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 会话状态变化(连上 / 失效 / 已关闭)。可能在任意线程触发,订阅方自行切回 UI 线程。
    /// 资源管理器树上的状态圆点靠它自动变灰 —— 无连接的协议没有可订阅的长驻会话对象。
    /// </summary>
    event EventHandler<PluginProtocolSessionStateChange>? SessionStateChanged;
}
