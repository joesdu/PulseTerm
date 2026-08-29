using System.Collections.Concurrent;
using VelaShell.Core.Models;
using VelaShell.Core.Processes;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 在会话现有的 SSH 连接上采集远端进程列表并执行管理动作(任务管理器的数据源)。
/// 与 <see cref="SessionMetricsService" /> 同样是"拉"模型:自身不持有定时器,只保存上一次
/// 采样以便把累计 CPU 滴答差分成瞬时占用率。
/// </summary>
public sealed class RemoteProcessService(ISshConnectionService connectionService) : IRemoteProcessService
{
    /// <summary>单次探测/动作的上限;远端卡住时不能把面板一起拖死。</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private readonly ISshConnectionService _connectionService =
        connectionService ?? throw new ArgumentNullException(nameof(connectionService));

    private readonly ConcurrentDictionary<Guid, Sample> _lastSamples = new();

    /// <inheritdoc />
    public async Task<RemoteProcessSnapshot?> GetSnapshotAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default
    )
    {
        if (GetLiveClient(sessionId) is not { } client)
        {
            _lastSamples.TryRemove(sessionId, out _);
            return null;
        }

        // 非 POSIX 远端(Windows 的 cmd.exe/PowerShell 等)直接判"不可用",一条命令都不发:
        // 探测命令读的是 /proc 与 ps,cmd 跑不动它却照样有输出(整行被 echo 原样打回),
        // 而 RemoteProcessProbe.Parse 只在输出为空时才返回 null —— 于是面板显示的不是
        // "需要一个已连接的 Linux 会话",而是一张 CPU 0.0%、0 个进程的空表(#305 同源)。
        if (!await IsPosixRemoteAsync(sessionId, client, cancellationToken).ConfigureAwait(false))
        {
            _lastSamples.TryRemove(sessionId, out _);
            return null;
        }
        try
        {
            string output = await RunAsync(client, RemoteProcessProbe.ProbeCommand, cancellationToken)
                .ConfigureAwait(false);
            RemoteProcessSnapshot? snapshot = RemoteProcessProbe.Parse(output);
            if (snapshot is not null)
            {
                ApplyDeltas(sessionId, snapshot);
            }
            return snapshot;
        }
        catch
        {
            // 探测失败(超时、非 Linux 主机、会话正在拆除)一律等同于"数据不可用"。
            return null;
        }
    }

    /// <inheritdoc />
    public Task<RemoteCommandOutcome> SignalAsync(
        Guid sessionId,
        IReadOnlyList<int> pids,
        ProcessSignal signal,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(pids);
        return pids.Count == 0
                   ? Task.FromResult(RemoteCommandOutcome.NoSession)
                   : RunActionAsync(
                       sessionId,
                       RemoteProcessProbe.BuildSignalCommand(pids, signal),
                       cancellationToken
                   );
    }

    /// <inheritdoc />
    public Task<RemoteCommandOutcome> ReniceAsync(
        Guid sessionId,
        int pid,
        int niceness,
        CancellationToken cancellationToken = default
    ) => RunActionAsync(sessionId, RemoteProcessProbe.BuildReniceCommand(pid, niceness), cancellationToken);

    /// <inheritdoc />
    public void ResetBaseline(Guid sessionId) => _lastSamples.TryRemove(sessionId, out _);

    private async Task<RemoteCommandOutcome> RunActionAsync(
        Guid sessionId,
        string command,
        CancellationToken cancellationToken
    )
    {
        if (GetLiveClient(sessionId) is not { } client)
        {
            return RemoteCommandOutcome.NoSession;
        }
        try
        {
            string output = await RunAsync(client, command, cancellationToken).ConfigureAwait(false);
            return RemoteProcessProbe.ParseOutcome(output);
        }
        catch (Exception ex)
        {
            // 动作失败必须让用户看见原因(权限不足是最常见的),不能像采集那样静默吞掉。
            return new(false, ex.Message);
        }
    }

    private ISshClientWrapper? GetLiveClient(Guid sessionId)
    {
        ISshClientWrapper? client = _connectionService.GetClient(sessionId);
        return client is { IsConnected: true } ? client : null;
    }

    /// <summary>
    /// 对端是不是 POSIX shell(结论由 <see cref="RemoteShellProbe" /> 按主机缓存,
    /// 除首次外只是一次字典查找)。
    /// </summary>
    private async Task<bool> IsPosixRemoteAsync(Guid sessionId, ISshClientWrapper client, CancellationToken cancellationToken)
    {
        ConnectionInfo? info = _connectionService.GetSession(sessionId)?.ConnectionInfo;
        return await RemoteShellProbe
            .IsPosixShellAsync(
                client,
                RemoteShellProbe.CacheKey(info?.Host, info?.Port ?? 22, info?.Username),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<string> RunAsync(
        ISshClientWrapper client,
        string command,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        return await client.RunCommandAsync(command, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 用上一次采样把累计量差分成瞬时占用率。两次采样之间的时长取远端 /proc/uptime 的增量
    /// 而非本地时钟:探测本身要走网络,本地时钟会把 RTT 抖动算进分母。
    /// </summary>
    private void ApplyDeltas(Guid sessionId, RemoteProcessSnapshot snapshot)
    {
        _lastSamples.TryGetValue(sessionId, out Sample? previous);
        _lastSamples[sessionId] = new(
            snapshot.UptimeSeconds,
            snapshot.CpuTotalJiffies,
            snapshot.CpuIdleJiffies,
            snapshot.Processes.ToDictionary(p => p.Pid, p => p.CpuTicks)
        );
        if (previous is null)
        {
            return;
        }
        double elapsed = snapshot.UptimeSeconds - previous.UptimeSeconds;
        if (elapsed <= 0)
        {
            // 主机重启或 uptime 不可用:基准作废,本轮不报瞬时值。
            return;
        }

        if (snapshot.CpuTotalJiffies > previous.CpuTotal)
        {
            long deltaTotal = snapshot.CpuTotalJiffies - previous.CpuTotal;
            long deltaIdle = Math.Max(0, snapshot.CpuIdleJiffies - previous.CpuIdle);
            snapshot.CpuPercent = Math.Clamp((deltaTotal - deltaIdle) * 100.0 / deltaTotal, 0, 100);
        }

        // 每进程:滴答增量 ÷ (时长 × 每秒滴答 × 核心数) —— 除以核心数是 Windows 任务管理器
        // 的口径(全核满载才是 100%),而非 top 的每核 100%。
        double capacity = elapsed * snapshot.ClockTicksPerSecond * snapshot.CpuCores;
        if (capacity <= 0)
        {
            return;
        }
        foreach (RemoteProcessInfo process in snapshot.Processes)
        {
            if (!previous.Ticks.TryGetValue(process.Pid, out long before))
            {
                // 新出现的进程没有基准,这一轮显示 0,下一轮才有真实读数。
                continue;
            }
            long delta = process.CpuTicks - before;
            if (delta > 0)
            {
                process.CpuPercent = Math.Clamp(delta * 100.0 / capacity, 0, 100);
            }
        }
    }

    /// <summary>一次采样中用于差分的累计量。</summary>
    private sealed record Sample(
        double UptimeSeconds,
        long CpuTotal,
        long CpuIdle,
        Dictionary<int, long> Ticks
    );
}
