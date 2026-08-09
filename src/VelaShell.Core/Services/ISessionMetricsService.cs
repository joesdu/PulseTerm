namespace VelaShell.Core.Services;

/// <summary>获取已连接会话的实时资源快照(资源面板 §11)。</summary>
public interface ISessionMetricsService
{
    /// <summary>
    /// 返回当前指标;当会话未连接或远端主机未暴露预期的探测项时返回 null。
    /// </summary>
    Task<SessionMetrics?> GetMetricsAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按指定范围采集指标。状态栏用 <see cref="MetricsScope.Basic" />(等价于无参重载);
    /// 资源监视窗口用 <see cref="MetricsScope.Full" /> 追加 CPU 细分、内存明细、磁盘 IO、GPU 与进程 Top。
    /// </summary>
    /// <param name="sessionId">目标会话。</param>
    /// <param name="scope">采集范围。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>指标快照;不可用时为 null。</returns>
    Task<SessionMetrics?> GetMetricsAsync(Guid sessionId, MetricsScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// 返回主机静态信息(CPU 型号与拓扑、磁盘型号、网卡属性、GPU 驱动)。
    /// 每个会话只探测一次并缓存,后续调用直接返回缓存值;不可用时返回 null。
    /// </summary>
    /// <param name="sessionId">目标会话。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>静态信息;不可用时为 null。</returns>
    Task<SessionStaticInfo?> GetStaticInfoAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
