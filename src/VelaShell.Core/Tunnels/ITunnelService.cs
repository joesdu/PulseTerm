using VelaShell.Core.Models;

namespace VelaShell.Core.Tunnels;

/// <summary>SSH 端口转发(隧道)服务:按会话创建、停止与查询本地/远程/动态转发。</summary>
public interface ITunnelService : IAsyncDisposable
{
    /// <summary>获取指定会话当前隧道的列表快照;每次读取返回独立副本。</summary>
    IReadOnlyList<TunnelInfo> GetActiveTunnels(Guid sessionId);

    /// <summary>
    /// 把各活动隧道底层转发句柄的连接数与流量读数同步到对应的 <see cref="TunnelInfo" />。
    /// <para>
    /// 界面持有的是同一批 <see cref="TunnelInfo" /> 实例,所以刷新完不必重新取快照;
    /// 由界面的状态时钟按秒级节奏调用,而不是让每个字节的搬运都去回调 UI。
    /// </para>
    /// </summary>
    void RefreshStatistics();
    /// <summary>创建本地端口转发(等价 ssh -L):监听本地端口并转发至远端目标。</summary>
    Task<TunnelInfo> CreateLocalForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);
    /// <summary>创建远程端口转发(等价 ssh -R):在远端监听端口并回转发至本地目标。</summary>
    Task<TunnelInfo> CreateRemoteForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);

    /// <summary>动态转发(SOCKS 代理,等价 ssh -D):监听本地端口,目标由客户端协商。</summary>
    Task<TunnelInfo> CreateDynamicForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default);

    /// <summary>停止指定隧道,释放其监听端口与连接,但保留隧道记录。</summary>
    Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);

    /// <summary>彻底移除一条隧道记录(活动中的先停止),用于删除或"停止后重建"前的清理。</summary>
    Task RemoveTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default);

    /// <summary>会话断开时停止其全部隧道并标记为已停止(尽力而为,不抛出)。</summary>
    Task StopAllForSessionAsync(Guid sessionId);
}
