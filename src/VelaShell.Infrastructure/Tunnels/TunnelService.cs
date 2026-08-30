using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;

namespace VelaShell.Infrastructure.Tunnels;

/// <summary>
/// 端口转发通道管理服务:在指定 SSH 会话上创建本地/远程/动态转发,跟踪各通道的活动状态,
/// 并在停止或会话拆除时释放底层监听端口。以 <see cref="Guid" /> 会话为单位维护可观察的通道列表。
/// </summary>
/// <param name="connectionService">会话查询,用于确认目标会话仍然连着。</param>
/// <param name="clientFactory">按会话取 SSH 客户端。</param>
/// <param name="logger">可选日志。</param>
/// <param name="isLocalPortInUse">
/// 本地端口占用探测,默认查询系统的 TCP 监听表。做成可注入是为了让测试不必看
/// 运行机器上恰好有没有 PostgreSQL 在监听 5432 —— 那种依赖会让测试时灵时不灵。
/// </param>
public class TunnelService(
    ISshConnectionService connectionService,
    Func<Guid, ISshClientWrapper> clientFactory,
    ILogger<TunnelService>? logger = null,
    Func<string, uint, bool>? isLocalPortInUse = null) : ITunnelService
{
    private readonly Func<Guid, ISshClientWrapper> _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    private readonly Func<string, uint, bool> _isLocalPortInUse = isLocalPortInUse ?? IsLocalPortInUse;
    private readonly ISshConnectionService _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    /// <summary>
    /// 会话 → 该会话的通道列表。每个 <see cref="List{T}" /> 实例自身即为其内容的锁对象:
    /// 增删与快照读取都必须 <c>lock</c> 住它,字典本身的并发由 ConcurrentDictionary 保证。
    /// </summary>
    private readonly ConcurrentDictionary<Guid, List<TunnelInfo>> _sessionTunnels = new();

    private readonly ConcurrentDictionary<Guid, (IPortForwardHandle Handle, TunnelInfo Info)> _tunnelPorts = new();

    /// <summary>获取指定会话当前所有转发通道的列表快照(锁内复制,遍历期间不受并发增删影响)。</summary>
    public IReadOnlyList<TunnelInfo> GetActiveTunnels(Guid sessionId)
    {
        RefreshStatistics();
        List<TunnelInfo> tunnels = _sessionTunnels.GetOrAdd(sessionId, _ => []);
        lock (tunnels)
        {
            return [.. tunnels];
        }
    }

    /// <summary>把各活动转发句柄的连接数与流量读数同步到对应的 <see cref="TunnelInfo" />。</summary>
    public void RefreshStatistics()
    {
        foreach ((IPortForwardHandle handle, TunnelInfo info) in _tunnelPorts.Values)
        {
            // 停止的隧道其句柄已被移出 _tunnelPorts,读数就停在最后一次同步的值上。
            info.BytesTransferred = handle.BytesTransferred;
            info.TotalConnections = handle.TotalConnections;
            info.ActiveConnections = handle.ActiveConnections;
        }
    }

    /// <summary>在指定会话上创建本地端口转发通道(本地监听端口 → 远端目标)。</summary>
    public async Task<TunnelInfo> CreateLocalForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.LocalForward)
        {
            throw new ArgumentException(@"Config type must be LocalForward", nameof(config));
        }
        EnsureLocalPortAvailable(config.LocalHost, config.LocalPort);
        return await CreateForwardAsync(sessionId,
                   config,
                   new(PortForwardKind.Local, config.LocalHost, config.LocalPort, config.RemoteHost, config.RemotePort),
                   "local",
                   cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在指定会话上创建远程端口转发通道(远端监听端口 → 本地目标)。</summary>
    public async Task<TunnelInfo> CreateRemoteForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.RemoteForward)
        {
            throw new ArgumentException(@"Config type must be RemoteForward", nameof(config));
        }
        return await CreateForwardAsync(sessionId,
                   config,
                   new(PortForwardKind.Remote, config.RemoteHost, config.RemotePort, config.LocalHost, config.LocalPort),
                   "remote",
                   cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在指定会话上创建动态转发通道(本地 SOCKS 代理端口),按连接动态选择远端目标。</summary>
    public async Task<TunnelInfo> CreateDynamicForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.DynamicForward)
        {
            throw new ArgumentException(@"Config type must be DynamicForward", nameof(config));
        }
        EnsureLocalPortAvailable(config.LocalHost, config.LocalPort);
        return await CreateForwardAsync(sessionId,
                   config,
                   new(PortForwardKind.Dynamic, config.LocalHost, config.LocalPort),
                   "dynamic",
                   cancellationToken).ConfigureAwait(false);
    }

    /// <summary>移除指定转发通道:若仍在活动则先停止,随后将其从会话列表中删除。</summary>
    public async Task RemoveTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default)
    {
        // 活动中的先停止(同时把记录状态置为 Stopped)。
        if (_tunnelPorts.ContainsKey(tunnelId))
        {
            await StopTunnelAsync(tunnelId, cancellationToken).ConfigureAwait(false);
        }
        foreach ((Guid _, List<TunnelInfo> tunnels) in _sessionTunnels)
        {
            lock (tunnels)
            {
                TunnelInfo? existing = tunnels.Find(t => t.Id == tunnelId);
                if (existing is null)
                {
                    continue;
                }
                tunnels.Remove(existing);
            }
            if (logger is not null && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Removed tunnel {TunnelId}", tunnelId);
            }
            return;
        }
    }

    /// <summary>停止指定会话下的所有活动转发通道,常用于会话断开/拆除时的批量清理。</summary>
    public async Task StopAllForSessionAsync(Guid sessionId)
    {
        foreach ((Guid tunnelId, (IPortForwardHandle _, TunnelInfo info)) in _tunnelPorts)
        {
            if (info.SessionId != sessionId)
            {
                continue;
            }
            try
            {
                await StopTunnelAsync(tunnelId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 会话断开时底层端口可能已经随客户端一起失效;记录后继续。
                logger?.LogWarning(ex, "Failed to stop tunnel {TunnelId} on session teardown", tunnelId);
                _tunnelPorts.TryRemove(tunnelId, out _);
                info.Status = TunnelStatus.Stopped;
            }
        }
    }

    /// <summary>停止指定转发通道:释放底层监听端口并将其状态置为 <see cref="TunnelStatus.Stopped" />;找不到通道时抛出异常。</summary>
    public async Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default)
    {
        if (!_tunnelPorts.TryRemove(tunnelId, out (IPortForwardHandle Handle, TunnelInfo Info) tunnelData))
        {
            throw new InvalidOperationException($"Tunnel {tunnelId} not found");
        }
        (IPortForwardHandle handle, TunnelInfo info) = tunnelData;
        try
        {
            // 先把最后一次读数取下来:句柄一释放,这条隧道这辈子搬了多少字节就再也问不到了,
            // 而界面在"已停止"状态下仍要显示它跑过的总量。
            info.BytesTransferred = handle.BytesTransferred;
            info.TotalConnections = handle.TotalConnections;
            info.ActiveConnections = 0;

            // Stop 幂等且自带"客户端已随会话释放"的容错(见 IPortForwardHandle 契约),
            // 且只做取消令牌 + 关监听这类同步收尾,直接调用即可。
            handle.Dispose();
            info.Status = TunnelStatus.Stopped;
            if (_sessionTunnels.TryGetValue(info.SessionId, out List<TunnelInfo>? tunnels))
            {
                lock (tunnels)
                {
                    TunnelInfo? existingTunnel = tunnels.Find(t => t.Id == tunnelId);
                    if (existingTunnel != null)
                    {
                        tunnels.Remove(existingTunnel);
                        tunnels.Add(info);
                    }
                }
            }
            if (logger is not null && logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Stopped tunnel {TunnelId} for session {SessionId}", tunnelId, info.SessionId);
            }
        }
        catch (Exception ex)
        {
            info.Status = TunnelStatus.Error;
            logger?.LogError(ex, "Failed to stop tunnel {TunnelId}", tunnelId);
            throw;
        }
    }

    /// <summary>释放服务:停止并释放所有会话下的转发通道与可观察列表资源。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach ((Guid tunnelId, (IPortForwardHandle handle, TunnelInfo info)) in _tunnelPorts)
        {
            try
            {
                await Task.Run(handle.Dispose).ConfigureAwait(false);
                info.Status = TunnelStatus.Stopped;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to dispose tunnel {TunnelId}", tunnelId);
            }
        }
        _tunnelPorts.Clear();
        _sessionTunnels.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 创建本机监听前的端口占用预检:命中就抛 <see cref="TunnelPortInUseException" />,
    /// 让用户看到"27017 被占用"而不是底层套接字的错误码。
    /// <para>
    /// 预检与真正的绑定之间存在竞态(检查完到绑定前端口可能刚被抢走),这不是问题:
    /// 预检负责把最常见的情形讲清楚,真绑不上时底层异常仍会照常抛出。
    /// </para>
    /// </summary>
    private void EnsureLocalPortAvailable(string host, uint port)
    {
        if (_isLocalPortInUse(host, port))
        {
            throw new TunnelPortInUseException(Strings.Format("TunnelSvc_LocalPortInUse", port), port);
        }
    }

    /// <summary>默认的占用探测:比对系统 TCP 监听表。</summary>
    private static bool IsLocalPortInUse(string host, uint port)
    {
        IPAddress requested;
        try
        {
            requested = ParseBindAddress(host);
        }
        catch (FormatException)
        {
            // 监听地址本身不合法,交给真正的绑定去报错,预检不越俎代庖。
            return false;
        }
        IPEndPoint[] listeners;
        try
        {
            listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        }
        catch (NetworkInformationException)
        {
            // 平台不给监听表(部分容器/受限环境)时跳过预检,退回底层异常。
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        return listeners.Any(listener => listener.Port == port && Overlaps(listener.Address, requested));
    }

    /// <summary>
    /// 两个监听地址是否会撞车:任一方绑在"所有接口"上就与同端口的一切监听冲突,
    /// 否则只有地址完全相同才算冲突(不同网卡的同一端口可以并存)。
    /// </summary>
    private static bool Overlaps(IPAddress existing, IPAddress requested) =>
        IsAnyAddress(existing) || IsAnyAddress(requested) || existing.Equals(requested);

    private static bool IsAnyAddress(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    /// <summary>把配置里的监听主机翻译成绑定地址(与转发句柄的解析保持一致)。</summary>
    private static IPAddress ParseBindAddress(string host) =>
        host is "0.0.0.0" or "*" ? IPAddress.Any :
        host == "::" ? IPAddress.IPv6Any :
        host is "localhost" or "127.0.0.1" ? IPAddress.Loopback :
        IPAddress.Parse(host);

    /// <summary>
    /// 把转发通道异常翻译成用户可理解的提示;最常见的是把目标填成了服务器的
    /// 公网地址,而服务只监听 127.0.0.1。
    /// </summary>
    private static string DescribeForwardError(Exception ex)
    {
        SocketException? socket = ex as SocketException ?? ex.InnerException as SocketException;
        switch (socket?.SocketErrorCode)
        {
            case SocketError.ConnectionRefused:
                return Strings.Get("TunnelSvc_TargetRefused");
            case SocketError.TimedOut or SocketError.HostUnreachable:
                return Strings.Get("TunnelSvc_TargetUnreachable");
        }
        if (ex.Message.Contains("administratively prohibited", StringComparison.OrdinalIgnoreCase))
        {
            return Strings.Get("TunnelSvc_ForwardProhibited");
        }
        return ex.Message;
    }

    private async Task<TunnelInfo> CreateForwardAsync(
        Guid sessionId,
        TunnelConfig config,
        PortForwardRequest request,
        string direction,
        CancellationToken cancellationToken)
    {
        SshSession? session = _connectionService.GetSession(sessionId) ?? throw new InvalidOperationException($"Session {sessionId} not found");
        if (session.Status != SessionStatus.Connected)
        {
            throw new InvalidOperationException($"Session {sessionId} is not connected");
        }
        ISshClientWrapper client = _clientFactory(sessionId);
        if (!client.IsConnected)
        {
            throw new InvalidOperationException($"SSH client for session {sessionId} is not connected");
        }
        var tunnelInfo = new TunnelInfo
        {
            Id = Guid.NewGuid(),
            Config = config,
            Status = TunnelStatus.Active,
            SessionId = sessionId,
            CreatedAt = DateTime.UtcNow,
            BytesTransferred = 0
        };
        try
        {
            // StartPortForwardAsync 建立并启动监听,失败时不留下半挂的端口(见接口契约)。
            IPortForwardHandle handle = await client.StartPortForwardAsync(request, cancellationToken).ConfigureAwait(false);

            // 转发通道错误(目标拒绝连接等)不会让监听端口停摆,但每个经过的连接都会失败;
            // 记到 LastError 供界面展示,否则用户只看到"运行中"却连不上。
            handle.ChannelError += ex =>
            {
                tunnelInfo.LastError = DescribeForwardError(ex);
                logger?.LogWarning(ex, "Tunnel {TunnelId} channel error", tunnelInfo.Id);
            };
            List<TunnelInfo> tunnels = _sessionTunnels.GetOrAdd(sessionId, _ => []);
            _tunnelPorts[tunnelInfo.Id] = (handle, tunnelInfo);
            lock (tunnels)
            {
                tunnels.Add(tunnelInfo);
            }
            if (logger is not null && logger.IsEnabled(LogLevel.Information))
            {
                logger?.LogInformation("Created {Direction} forward tunnel {TunnelId} for session {SessionId}: {LocalHost}:{LocalPort} <-> {RemoteHost}:{RemotePort}",
                    direction, tunnelInfo.Id, sessionId, config.LocalHost, config.LocalPort, config.RemoteHost, config.RemotePort);
            }
            return tunnelInfo;
        }
        catch (Exception ex)
        {
            tunnelInfo.Status = TunnelStatus.Error;
            logger?.LogError(ex, "Failed to create {Direction} forward tunnel for session {SessionId}", direction, sessionId);
            throw;
        }
    }
}
