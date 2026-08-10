using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary><see cref="ISessionsApi" /> 的桥接实现:宿主会话列表的脱敏投影,不含任何凭据。</summary>
internal sealed class SessionsCapability(ISshConnectionService connections) : ISessionsApi
{
    /// <summary>把宿主会话对象投影为对插件安全的 DTO(仅连接元数据)。</summary>
    internal static SessionInfo Map(SshSession session) => new(
        session.SessionId.ToString(),
        session.ConnectionInfo.Host,
        session.ConnectionInfo.Port,
        session.ConnectionInfo.Username,
        session.Status switch
        {
            SessionStatus.Connecting => SessionState.Connecting,
            SessionStatus.Connected => SessionState.Connected,
            SessionStatus.Error => SessionState.Error,
            _ => SessionState.Disconnected
        },
        new DateTimeOffset(session.CreatedAt, TimeSpan.Zero),
        session.ConnectedAt is { } connectedAt ? new DateTimeOffset(connectedAt, TimeSpan.Zero) : null);

    public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SessionInfo> sessions = [.. connections.Sessions.Select(Map)];
        return Task.FromResult(sessions);
    }

    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        SessionInfo? result = Guid.TryParse(sessionId, out Guid id) && connections.GetSession(id) is { } session
            ? Map(session)
            : null;
        return Task.FromResult(result);
    }
}
