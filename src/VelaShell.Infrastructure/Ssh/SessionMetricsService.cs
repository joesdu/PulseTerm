using System.Collections.Concurrent;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 在会话现有的 SSH 连接上运行指标探测(§11),并将连续采样转换为瞬时读数:CPU% 取自
/// /proc/stat 的 jiffies 增量(一次性的 loadavg 近似会滞后约一分钟),网速取自
/// /proc/net/dev 的字节计数器增量。
/// </summary>
public sealed class SessionMetricsService : ISessionMetricsService, IDisposable
{
    private readonly ISshConnectionService _connectionService;
    // 按 (会话, 采集范围) 分开存上一次采样。状态栏走 Basic、资源窗口走 Full,两者各跑各的轮询;
    // 共用一格会互相踩:Basic 那次不含磁盘 IO 计数,窗口下一次差分就永远算不出速率,
    // 而且两次采样间隔被压到几十毫秒后,CPU 细分会跳出"内核 100%"这种鬼值。
    private readonly ConcurrentDictionary<(Guid Session, MetricsScope Scope), Sample> _lastSamples = new();
    private readonly ConcurrentDictionary<Guid, SessionStaticInfo> _staticInfo = new();

    /// <summary>
    /// 构造并订阅断连事件,以便会话一关就把它的采样缓存丢掉。
    /// </summary>
    /// <remarks>
    /// 清理曾经只写在 <see cref="GetMetricsAsync(Guid, MetricsScope, CancellationToken)" />
    /// 里"发现连接已断"的那个分支上 —— 可关掉的会话正是**不会再被轮询**的会话,
    /// 那个分支永远走不到。于是每连一次、关一次,字典里就多留一份静态信息(CPU 型号、
    /// 磁盘型号、网卡属性)和最多五份采样,连开关几十次就再也不掉下去了。
    /// </remarks>
    /// <param name="connectionService">SSH 连接服务(探测在其现有连接上执行)。</param>
    public SessionMetricsService(ISshConnectionService connectionService)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _connectionService.SessionDisconnected += OnSessionDisconnected;
    }

    /// <summary>取消订阅。本服务是单例,与宿主同寿,这里只为不在测试之间互相拖住对方。</summary>
    public void Dispose() => _connectionService.SessionDisconnected -= OnSessionDisconnected;

    private void OnSessionDisconnected(SshSession session) => Forget(session.SessionId);

    /// <summary>丢掉一条会话的全部缓存(所有采集范围的采样 + 静态信息)。</summary>
    private void Forget(Guid sessionId)
    {
        foreach (MetricsScope scope in Enum.GetValues<MetricsScope>())
        {
            _lastSamples.TryRemove((sessionId, scope), out _);
        }
        _staticInfo.TryRemove(sessionId, out _);
    }

    /// <summary>当前缓存的会话数(回归用例读它:连开关 N 次后必须回到活跃规模)。</summary>
    internal int CachedSessionCountForTest =>
        _lastSamples.Keys.Select(k => k.Session).Concat(_staticInfo.Keys).Distinct().Count();

    /// <inheritdoc />
    public Task<SessionMetrics?> GetMetricsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        GetMetricsAsync(sessionId, MetricsScope.Basic, cancellationToken);

    /// <summary>
    /// 采集指定会话的一次实时指标:在其现有 SSH 连接上跑探测命令,解析后与上一采样做差分
    /// 得到瞬时 CPU%/网速。连接不存在或已断开、对端不是 POSIX shell、以及探测失败(超时、
    /// 非 Linux 主机)时返回 <c>null</c>。
    /// </summary>
    public async Task<SessionMetrics?> GetMetricsAsync(Guid sessionId, MetricsScope scope, CancellationToken cancellationToken = default)
    {
        ISshClientWrapper? client = _connectionService.GetClient(sessionId);
        if (client is null || !client.IsConnected)
        {
            // 兜底:断连事件没赶上(或压根没发,比如底层连接自己掉了)时仍能清掉。
            Forget(sessionId);
            return null;
        }
        if (!await IsPosixRemoteAsync(sessionId, client, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            string output = await client.RunCommandAsync(SessionMetrics.BuildCommand(scope), cancellationToken).ConfigureAwait(false);
            var metrics = SessionMetrics.Parse(output);
            if (metrics is not null)
            {
                ApplyDeltas(sessionId, scope, metrics);
            }
            return metrics;
        }
        catch
        {
            // 探测失败(超时、非 Linux 主机、会话断开)即为"数据不可用"。
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<SessionStaticInfo?> GetStaticInfoAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_staticInfo.TryGetValue(sessionId, out SessionStaticInfo? cached))
        {
            return cached;
        }
        ISshClientWrapper? client = _connectionService.GetClient(sessionId);
        if (client is null || !client.IsConnected)
        {
            return null;
        }
        if (!await IsPosixRemoteAsync(sessionId, client, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            string output = await client.RunCommandAsync(SessionMetrics.StaticCommand, cancellationToken).ConfigureAwait(false);
            SessionStaticInfo info = SessionMetrics.ParseStatic(output);
            _staticInfo[sessionId] = info;
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 对端是不是 POSIX shell。<b>不是就一条命令都不发</b>:探测命令通篇是 <c>/proc</c>、
    /// <c>nproc</c>、<c>df</c>,Windows 的 cmd.exe 既跑不动它,又不会安静 ——
    /// <c>echo __P__; nproc; …</c> 在 cmd 里是**一条** echo,它把整行原样打回来,于是
    /// <see cref="SessionMetrics.Parse" />(只在输出为空时返回 null)拿着这堆回声解出一份
    /// 全是 0 的假指标,状态栏一本正经地显示 CPU 0.00% / 内存 0.0%。
    /// 状态栏每秒轮询一次,不拦下来就是每秒在对端起一个 cmd.exe(#305 同源)。
    /// </summary>
    /// <remarks>
    /// 结论由 <see cref="RemoteShellProbe" /> 按主机缓存,除首次外只是一次字典查找;
    /// 拿不到会话信息时退回空缓存键(不缓存,但仍然照探)。
    /// </remarks>
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

    /// <summary>
    /// 针对同一会话的上一采样计算瞬时 CPU%/网速,然后保存本次采样。首个采样保留 loadavg 的
    /// CPU 兜底值,且不报告网速。
    /// </summary>
    private void ApplyDeltas(Guid sessionId, MetricsScope scope, SessionMetrics metrics)
    {
        DateTime now = DateTime.UtcNow;
        _lastSamples.TryGetValue((sessionId, scope), out Sample? prev);
        if (prev is not null)
        {
            double seconds = (now - prev.At).TotalSeconds;
            if (metrics.HasCpuCounters && metrics.CpuTotalJiffies > prev.CpuTotal)
            {
                long deltaTotal = metrics.CpuTotalJiffies - prev.CpuTotal;
                long deltaIdle = metrics.CpuIdleJiffies - prev.CpuIdle;
                metrics.CpuPercent = Math.Clamp(((deltaTotal - deltaIdle) * 100.0) / deltaTotal, 0, 100);
                metrics.Cpu = BuildBreakdown(metrics.CpuStatColumns, prev.CpuColumns, deltaTotal);
            }

            // 每核心占用:与上一采样按核名对齐做差分(状态栏 CPU 提示逐核显示)。
            if (metrics.CoreCounters.Count > 0 && prev.Cores.Count > 0)
            {
                var prevCores = prev.Cores.ToDictionary(c => c.Name);
                var percents = new List<double>(metrics.CoreCounters.Count);
                foreach (CpuCoreCounter core in metrics.CoreCounters)
                {
                    double percent = 0;
                    if (prevCores.TryGetValue(core.Name, out CpuCoreCounter? p) && core.TotalJiffies > p.TotalJiffies)
                    {
                        long dt = core.TotalJiffies - p.TotalJiffies;
                        long di = core.IdleJiffies - p.IdleJiffies;
                        percent = Math.Clamp(((dt - di) * 100.0) / dt, 0, 100);
                    }
                    percents.Add(percent);
                }
                metrics.CorePercents = percents;
            }
            if (metrics.HasNetCounters && seconds > 0.2)
            {
                // 计数器可能复位(网卡抖动、重启);将负值钳制为 0。
                metrics.NetRxBytesPerSec = Math.Max(0, (metrics.NetRxTotalBytes - prev.NetRx) / seconds);
                metrics.NetTxBytesPerSec = Math.Max(0, (metrics.NetTxTotalBytes - prev.NetTx) / seconds);
                metrics.HasNetRates = true;
            }

            // 每网卡速率:按接口名对齐做差分(状态栏网速提示逐网卡显示)。
            if (metrics.NicCounters.Count > 0 && prev.Nics.Count > 0 && seconds > 0.2)
            {
                var prevNics = prev.Nics.ToDictionary(n => n.Name);
                var rates = new List<NetInterfaceRate>(metrics.NicCounters.Count);
                foreach (NetInterfaceCounter nic in metrics.NicCounters)
                {
                    if (prevNics.TryGetValue(nic.Name, out NetInterfaceCounter? p))
                    {
                        rates.Add(new(nic.Name,
                            Math.Max(0, (nic.RxBytes - p.RxBytes) / seconds),
                            Math.Max(0, (nic.TxBytes - p.TxBytes) / seconds)));
                    }
                }
                metrics.NicRates = rates;
            }

            // 逐磁盘 IO:扇区数固定 512 字节;io_ticks 是设备忙的毫秒数,除以采样间隔即活动时间占比。
            if (metrics.DiskIoCounters.Count > 0 && prev.DiskIo.Count > 0 && seconds > 0.2)
            {
                var prevIo = prev.DiskIo.ToDictionary(d => d.Name);
                var ioRates = new List<DiskIoRate>(metrics.DiskIoCounters.Count);
                foreach (DiskIoCounter disk in metrics.DiskIoCounters)
                {
                    if (!prevIo.TryGetValue(disk.Name, out DiskIoCounter? p))
                    {
                        continue;
                    }
                    ioRates.Add(new(disk.Name,
                        Math.Max(0, (disk.ReadSectors - p.ReadSectors) * 512.0 / seconds),
                        Math.Max(0, (disk.WriteSectors - p.WriteSectors) * 512.0 / seconds),
                        Math.Clamp((disk.IoTicks - p.IoTicks) / (seconds * 10.0), 0, 100)));
                }
                metrics.DiskIoRates = ioRates;
            }

            if (metrics.ContextSwitches > 0 && prev.ContextSwitches > 0 && seconds > 0.2)
            {
                metrics.ContextSwitchesPerSec = Math.Max(0, (metrics.ContextSwitches - prev.ContextSwitches) / seconds);
            }

            // 逐连接速率:按"本地+对端"配对差分。连接是短命的,配不上的(新建/已关闭)直接跳过。
            if (metrics.Connections.Count > 0 && prev.Connections.Count > 0 && seconds > 0.2)
            {
                var prevConnections = new Dictionary<string, ConnectionCounter>(StringComparer.Ordinal);
                foreach (ConnectionCounter c in prev.Connections)
                {
                    prevConnections[c.Local + "|" + c.Peer] = c;
                }
                var connectionRates = new List<ConnectionRate>(metrics.Connections.Count);
                foreach (ConnectionCounter c in metrics.Connections)
                {
                    if (!prevConnections.TryGetValue(c.Local + "|" + c.Peer, out ConnectionCounter? p))
                    {
                        continue;
                    }
                    connectionRates.Add(new(c.Peer, c.Process,
                        Math.Max(0, (c.BytesReceived - p.BytesReceived) / seconds),
                        Math.Max(0, (c.BytesSent - p.BytesSent) / seconds)));
                }
                metrics.ConnectionRates = connectionRates;
            }
        }
        if (metrics.HasCpuCounters || metrics.HasNetCounters)
        {
            _lastSamples[(sessionId, scope)] = new(metrics.CpuTotalJiffies, metrics.CpuIdleJiffies,
                metrics.NetRxTotalBytes, metrics.NetTxTotalBytes, now,
                metrics.CoreCounters, metrics.NicCounters,
                metrics.CpuStatColumns, metrics.DiskIoCounters, metrics.ContextSwitches,
                metrics.Connections);
        }
    }

    /// <summary>
    /// 从 /proc/stat 聚合行的列增量算出各状态占比。列序为
    /// user nice system idle iowait irq softirq steal(其后的 guest 列已计入 user,不重复统计)。
    /// </summary>
    private static CpuBreakdown? BuildBreakdown(IReadOnlyList<long> current, IReadOnlyList<long> previous, long deltaTotal)
    {
        if (current.Count < 5 || previous.Count < 5 || deltaTotal <= 0)
        {
            return null;
        }
        int n = Math.Min(current.Count, previous.Count);
        double Delta(int i) => i < n ? Math.Max(0, current[i] - previous[i]) : 0;
        double scale = 100.0 / deltaTotal;
        return new(
            (Delta(0) + Delta(1)) * scale,
            (Delta(2) + Delta(5) + Delta(6)) * scale,
            Delta(4) * scale,
            Delta(7) * scale);
    }

    private sealed record Sample(
        long CpuTotal,
        long CpuIdle,
        long NetRx,
        long NetTx,
        DateTime At,
        IReadOnlyList<CpuCoreCounter> Cores,
        IReadOnlyList<NetInterfaceCounter> Nics,
        IReadOnlyList<long> CpuColumns,
        IReadOnlyList<DiskIoCounter> DiskIo,
        long ContextSwitches,
        IReadOnlyList<ConnectionCounter> Connections);
}
