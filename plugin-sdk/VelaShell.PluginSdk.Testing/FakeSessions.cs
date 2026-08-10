using VelaShell.PluginSdk.Sessions;

namespace VelaShell.PluginSdk.Testing;

/// <summary><see cref="ISessionsApi" /> 的测试替身:测试直接摆放会话列表。</summary>
public sealed class FakeSessions : ISessionsApi
{
    /// <summary>当前会话列表;测试可直接增删。</summary>
    public List<SessionInfo> Sessions { get; } = [];

    /// <summary>便捷构造一条已连接会话并加入列表。</summary>
    public SessionInfo AddConnected(string host = "test-host", int port = 22, string username = "tester")
    {
        var session = new SessionInfo(Guid.NewGuid().ToString(), host, port, username,
            SessionState.Connected, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Sessions.Add(session);
        return session;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SessionInfo>>([.. Sessions]);

    /// <inheritdoc />
    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult(Sessions.Find(s => s.SessionId == sessionId));
}
