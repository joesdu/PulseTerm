namespace VelaShell.Core.Processes;

/// <summary>
/// 远端进程管理(任务管理器)的采集与操作入口。实现方持有相邻两次采样,
/// 因而返回的快照带有瞬时 CPU 占用率而非生命周期平均值。
/// </summary>
public interface IRemoteProcessService
{
    /// <summary>
    /// 采集一次远端进程列表。会话不存在、未连接或远端不是 Linux 时返回 null
    /// (探测失败等同于"没有数据",不抛异常)。
    /// </summary>
    /// <param name="sessionId">目标 SSH 会话标识。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<RemoteProcessSnapshot?> GetSnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>向一组远端进程投递信号。</summary>
    /// <param name="sessionId">目标 SSH 会话标识。</param>
    /// <param name="pids">目标进程号集合,不能为空。</param>
    /// <param name="signal">要投递的信号。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<RemoteCommandOutcome> SignalAsync(
        Guid sessionId,
        IReadOnlyList<int> pids,
        ProcessSignal signal,
        CancellationToken cancellationToken = default
    );

    /// <summary>调整远端进程的 nice 值(任务管理器的"设置优先级")。</summary>
    /// <param name="sessionId">目标 SSH 会话标识。</param>
    /// <param name="pid">目标进程号。</param>
    /// <param name="niceness">目标 nice 值,-20 到 19;超出范围会被夹取。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<RemoteCommandOutcome> ReniceAsync(
        Guid sessionId,
        int pid,
        int niceness,
        CancellationToken cancellationToken = default
    );

    /// <summary>丢弃某个会话的差分基准(会话断开或面板关闭时调用),避免基准悬挂。</summary>
    /// <param name="sessionId">目标 SSH 会话标识。</param>
    void ResetBaseline(Guid sessionId);
}
