using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// SSH 连接服务的默认实现:管理 SSH 会话的建连、断开与生命周期,
/// 通过客户端工厂创建底层连接,并以并发方式处理多条会话以避免相互阻塞。
/// </summary>
public class SshConnectionService(
    Func<ConnectionInfo, ISshClientWrapper> clientFactory,
    ILogger<SshConnectionService>? logger = null) : ISshConnectionService
{
    private readonly Func<ConnectionInfo, ISshClientWrapper> _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    private readonly ConcurrentDictionary<Guid, ISshClientWrapper> _clients = new();
    private readonly List<SshSession> _sessions = [];

    /// <summary>
    /// 只保护 <see cref="_sessions" /> 列表的增删/读取(微秒级、无网络 I/O)。
    /// 握手不在此锁内进行,因此一条高延迟连接不再阻塞其它并发连接。
    /// </summary>
    private readonly Lock _sessionsGate = new();

    /// <inheritdoc />
    public event Action<SshSession>? SessionConnected;

    /// <inheritdoc />
    public event Action<SshSession>? SessionDisconnected;

    /// <summary>
    /// 逐订阅方安全触发会话事件:单个订阅方(如某个插件)抛出不影响其它订阅方,
    /// 更不允许把异常带回建连/断开路径。
    /// </summary>
    private void RaiseSessionEvent(Action<SshSession>? handlers, SshSession session)
    {
        if (handlers is null)
        {
            return;
        }
        foreach (Action<SshSession> handler in handlers.GetInvocationList().Cast<Action<SshSession>>())
        {
            try
            {
                handler(session);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Session event subscriber threw for session {SessionId}", session.SessionId);
            }
        }
    }

    /// <summary>
    /// 当前所有 SSH 会话的快照:在锁内复制,调用方遍历期间不会受并发增删影响。
    /// </summary>
    public IReadOnlyList<SshSession> Sessions
    {
        get
        {
            lock (_sessionsGate)
            {
                return [.. _sessions];
            }
        }
    }

    /// <summary>
    /// 根据连接信息异步建立一条新的 SSH 会话。建连过程在线程池中执行,
    /// 多条连接可并发建立,单条慢连接不会阻塞其它连接。
    /// </summary>
    public Task<SshSession> ConnectAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken = default) =>
        // Tmds.Ssh 建连前的同步前缀(设置构建、凭据包装)均为纯内存操作(无 I/O),
        // 无需 Task.Run 调度;真正的网络 I/O 在 ConnectInternalAsync 的 await 里。
        // Task.Run(action, cancellationToken) 会导致外层任务取消时内层仍运行,
        // 产生大量未观察的异常并造成调试器输出洪流。
        ConnectInternalAsync(connectionInfo, cancellationToken);

    /// <summary>
    /// 异步断开指定标识的 SSH 会话,拆除底层网络连接并将会话状态置为已断开。
    /// </summary>
    public async Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        // 断开一个已经不存在的会话是幂等的无操作,不是错误:关闭应用时标签关闭与会话拆除
        // 是两条并发路径,后到的那条必然找不到会话。此前这里抛 InvalidOperationException,
        // 调用方一律 catch 吞掉,只在调试器里留下一条噪声异常。
        if (GetSession(sessionId) is not { } session || session.Status == SessionStatus.Disconnected)
        {
            _clients.TryRemove(sessionId, out _);
            return;
        }
        if (_clients.TryRemove(sessionId, out ISshClientWrapper? client))
        {
            try
            {
                // Disconnect/Dispose 为同步 socket 关闭,通道已断开时可能抛出清理噪声。
                client.Disconnect();
            }
            catch { }
            try
            {
                client.Dispose();
            }
            catch { }
        }
        session.Status = SessionStatus.Disconnected;
        if (logger is not null && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("SSH session {SessionId} disconnected", sessionId);
        }
        RaiseSessionEvent(SessionDisconnected, session);
    }

    /// <summary>
    /// 按会话标识查找并返回对应的 SSH 会话,未找到时返回 <c>null</c>。
    /// </summary>
    public SshSession? GetSession(Guid sessionId)
    {
        lock (_sessionsGate)
        {
            return _sessions.Find(s => s.SessionId == sessionId);
        }
    }

    /// <summary>
    /// 获取指定会话对应的底层 SSH 客户端包装器,会话不存在或未建连时返回 <c>null</c>。
    /// </summary>
    public ISshClientWrapper? GetClient(Guid sessionId)
    {
        _clients.TryGetValue(sessionId, out ISshClientWrapper? client);
        return client;
    }

    /// <summary>
    /// 异步释放服务持有的全部资源:并发拆除所有会话的网络连接并释放会话列表。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        KeyValuePair<Guid, ISshClientWrapper>[] clientEntries = [.. _clients];
        _clients.Clear();

        // 并发断开每个会话:每个 Disconnect() 都是阻塞式网络拆除,顺序循环会让应用退出耗时
        // 达到(会话数 × 拆除耗时),并在任一无响应连接上卡住。
        IEnumerable<Task> teardowns = clientEntries.Select(entry => Task.Run(() =>
        {
            (Guid sessionId, ISshClientWrapper client) = entry;
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
                client.Dispose();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Error disposing SSH client for session {SessionId}", sessionId);
            }
        }));
        await Task.WhenAll(teardowns).ConfigureAwait(false);
        lock (_sessionsGate)
        {
            _sessions.Clear();
        }
        GC.SuppressFinalize(this);
    }

    private async Task<SshSession> ConnectInternalAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken)
    {
        var session = new SshSession
        {
            ConnectionInfo = connectionInfo,
            Status = SessionStatus.Connecting
        };
        lock (_sessionsGate)
        {
            _sessions.Add(session);
        }
        ISshClientWrapper? client = null;
        try
        {
            client = _clientFactory(connectionInfo);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!client.IsConnected)
            {
                client.Dispose();
                client = null;
                throw new InvalidOperationException("Client connection failed without exception");
            }
            _clients[session.SessionId] = client;
            session.Status = SessionStatus.Connected;
            session.ConnectedAt = DateTime.UtcNow;
            if (logger is not null && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("SSH session {SessionId} connected to {Host}:{Port}",
                    session.SessionId, connectionInfo.Host, connectionInfo.Port);
            }
            RaiseSessionEvent(SessionConnected, session);
            return session;
        }
        // 调用方主动取消(关标签 / 退出应用 / 用户在凭据框上取消):原样上抛 OperationCanceledException。
        // 曾经这里把所有取消一律翻成 TimeoutException,于是上层那条
        // `catch (OperationCanceledException)`(安静撤掉"连接中"标签的路径)永远命中不了,
        // 用户取消反而会看到"连接超时"的失败覆盖层并留下一个连不上的死标签。
        // 只有调用方没取消却收到取消(= 底层库内部超时)才翻成 TimeoutException,
        // 与 TmdsSshInterop.Translate 的取消语义保持一致。
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            client?.Dispose();
            session.Status = SessionStatus.Error;
            session.ErrorMessage = $"Connection to {connectionInfo.Host}:{connectionInfo.Port} was cancelled.";
            lock (_sessionsGate)
            {
                _sessions.Remove(session);
            }
            // 先问级别再拼参数:Debug 关闭时不为这条日志创建 object[]。
            if (logger?.IsEnabled(LogLevel.Debug) == true)
            {
                logger.LogDebug("SSH session {SessionId} to {Host}:{Port} was cancelled by the caller",
                    session.SessionId, connectionInfo.Host, connectionInfo.Port);
            }
            throw;
        }
        catch (OperationCanceledException)
        {
            client?.Dispose();
            session.Status = SessionStatus.Error;
            session.ErrorMessage = $"Connection to {connectionInfo.Host}:{connectionInfo.Port} timed out. Please check the host and port, then retry.";
            lock (_sessionsGate)
            {
                _sessions.Remove(session);
            }
            logger?.LogWarning("SSH session {SessionId} to {Host}:{Port} timed out",
                session.SessionId, connectionInfo.Host, connectionInfo.Port);
            throw new TimeoutException(session.ErrorMessage);
        }
        catch (Exception ex)
        {
            client?.Dispose();
            session.Status = SessionStatus.Error;
            session.ErrorMessage = ex.Message;
            lock (_sessionsGate)
            {
                _sessions.Remove(session);
            }
            if (logger is not null)
            {
                string diagnostic = TmdsSshInterop.GetFailureDiagnostic(ex);
                logger.LogError(ex, "Failed to connect SSH session {SessionId} to {Host}:{Port}, reason: {Reason}",
                    session.SessionId, connectionInfo.Host, connectionInfo.Port, diagnostic);
            }
            throw;
        }
    }
}
