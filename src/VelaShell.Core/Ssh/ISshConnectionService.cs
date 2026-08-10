using VelaShell.Core.Models;

namespace VelaShell.Core.Ssh;

/// <summary>
/// SSH 连接服务:管理活动会话的建立、断开与查询,并以列表快照对外暴露会话集合。
/// </summary>
public interface ISshConnectionService : IAsyncDisposable
{
    /// <summary>当前所有活动 SSH 会话的快照;每次读取返回独立副本,调用方持有期间不受后续增删影响。</summary>
    IReadOnlyList<SshSession> Sessions { get; }

    /// <summary>一条会话连接成功后触发(在建连的后台线程上,订阅方不得阻塞或抛出)。</summary>
    event Action<SshSession>? SessionConnected;

    /// <summary>一条会话断开(用户断开或拆除)后触发(线程同上)。</summary>
    event Action<SshSession>? SessionDisconnected;

    /// <summary>按给定连接信息建立一个新的 SSH 会话并返回。</summary>
    Task<SshSession> ConnectAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken = default);

    /// <summary>断开并移除指定 Id 的会话。</summary>
    Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>按 Id 获取会话;不存在时返回 <c>null</c>。</summary>
    SshSession? GetSession(Guid sessionId);

    /// <summary>按会话 Id 获取其底层 SSH 客户端包装器;不存在时返回 <c>null</c>。</summary>
    ISshClientWrapper? GetClient(Guid sessionId);
}
