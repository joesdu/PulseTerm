using System.Net;

namespace VelaShell.Core.Diagnostics;

/// <summary>一跳的判定结论。着色与告警都以此为准,而不是直接看丢包率。</summary>
public enum HopVerdict
{
    /// <summary>正常。</summary>
    Ok,

    /// <summary>整跳无响应(* * *),但后续跳有响应 —— 该节点不回 ICMP,不是断链。</summary>
    NoResponse,

    /// <summary>本跳丢包但后续跳不丢:路由器对 ICMP 回包限速,转发面正常。</summary>
    IcmpRateLimited,

    /// <summary>丢包从本跳一直延续到最后一跳,才是真的可疑。</summary>
    SuspectedLoss,

    /// <summary>目标不可达(收到 Destination Unreachable)。</summary>
    Unreachable
}

/// <summary>单次探测的结果。</summary>
/// <param name="Ttl">本次探测使用的 TTL。</param>
/// <param name="Address">回应该探测的节点地址;超时为 null。</param>
/// <param name="Rtt">往返时延;超时为 null。</param>
/// <param name="Reached">是否已经到达目标(收到 EchoReply 而非 TimeExceeded)。</param>
/// <param name="Unreachable">是否收到不可达。</param>
public readonly record struct TraceProbe(
    int Ttl,
    IPAddress? Address,
    TimeSpan? Rtt,
    bool Reached,
    bool Unreachable
);

/// <summary>链路上的一跳,累计多轮探测的统计量(口径对齐 mtr)。</summary>
public sealed class TraceHop(int ttl)
{
    private readonly List<IPAddress> _addresses = [];
    private readonly List<TimeSpan?> _recent = [];
    private double _sumMs;
    private double _sumSquaresMs;
    private double _jitterSumMs;
    private double? _previousMs;

    /// <summary>最近样本的保留条数(迷你折线图用)。</summary>
    public const int RecentCapacity = 60;

    /// <summary>本跳的 TTL,从 1 开始。</summary>
    public int Ttl { get; } = ttl;

    /// <summary>
    /// 本跳观测到的全部地址。ECMP 多路径下同一 TTL 会返回多个 IP,只留最后一个会让链路看起来在跳变。
    /// </summary>
    public IReadOnlyList<IPAddress> Addresses => _addresses;

    /// <summary>反解出的主机名(PTR);未解析或失败为 null。</summary>
    public string? HostName { get; set; }

    /// <summary>已发出的探测数。</summary>
    public int Sent { get; private set; }

    /// <summary>收到回应的探测数。</summary>
    public int Received { get; private set; }

    /// <summary>本跳是否就是目标。</summary>
    public bool IsTarget { get; private set; }

    /// <summary>是否收到过不可达。</summary>
    public bool SawUnreachable { get; private set; }

    /// <summary>最近一次的 RTT。</summary>
    public TimeSpan? Last { get; private set; }

    /// <summary>最快的一次 RTT。</summary>
    public TimeSpan? Best { get; private set; }

    /// <summary>最慢的一次 RTT。</summary>
    public TimeSpan? Worst { get; private set; }

    /// <summary>判定结论,由 <see cref="TraceAnalysis.ApplyVerdicts" /> 统一填。</summary>
    public HopVerdict Verdict { get; internal set; }

    /// <summary>最近若干次 RTT(超时为 null),给迷你折线图用。</summary>
    public IReadOnlyList<TimeSpan?> Recent => _recent;

    /// <summary>丢包率(0-100)。</summary>
    public double LossPercent => Sent == 0 ? 0 : (Sent - Received) * 100.0 / Sent;

    /// <summary>平均 RTT;从未收到回应时为 null。</summary>
    public TimeSpan? Average =>
        Received == 0 ? null : TimeSpan.FromMilliseconds(_sumMs / Received);

    /// <summary>RTT 的标准差(毫秒);样本不足 2 个时为 0。</summary>
    public double StdDevMs
    {
        get
        {
            if (Received < 2)
            {
                return 0;
            }
            double mean = _sumMs / Received;
            double variance = Math.Max(0, (_sumSquaresMs / Received) - (mean * mean));
            return Math.Sqrt(variance);
        }
    }

    /// <summary>抖动:相邻两次 RTT 差的平均绝对值(毫秒)。</summary>
    public double JitterMs => Received < 2 ? 0 : _jitterSumMs / (Received - 1);

    /// <summary>并入一次探测结果。</summary>
    /// <param name="probe">探测结果。</param>
    public void Add(in TraceProbe probe)
    {
        Sent++;
        if (probe.Unreachable)
        {
            SawUnreachable = true;
        }
        if (probe.Reached)
        {
            IsTarget = true;
        }
        if (probe.Address is { } address && !_addresses.Any(a => a.Equals(address)))
        {
            _addresses.Add(address);
        }
        if (probe.Rtt is not { } rtt)
        {
            Push(null);
            return;
        }
        Received++;
        double ms = rtt.TotalMilliseconds;
        _sumMs += ms;
        _sumSquaresMs += ms * ms;
        if (_previousMs is { } previous)
        {
            _jitterSumMs += Math.Abs(ms - previous);
        }
        _previousMs = ms;
        Last = rtt;
        Best = Best is { } best && best <= rtt ? best : rtt;
        Worst = Worst is { } worst && worst >= rtt ? worst : rtt;
        Push(rtt);
    }

    private void Push(TimeSpan? sample)
    {
        _recent.Add(sample);
        if (_recent.Count > RecentCapacity)
        {
            _recent.RemoveAt(0);
        }
    }
}

/// <summary>
/// 把逐跳统计翻译成判定结论。单独抽出来是因为这是整个功能里最容易做错的一段:
/// 中间跳的丢包绝大多数不是故障,而是路由器对 ICMP 回包限速,直接按丢包率标红会误导人。
/// </summary>
public static class TraceAnalysis
{
    /// <summary>
    /// 按"只有末跳丢包才算端到端丢包"的规则给每一跳定性。规则:
    /// <list type="bullet">
    /// <item>整跳零回应,但后面还有跳有回应 → <see cref="HopVerdict.NoResponse" />(节点不回 ICMP)。</item>
    /// <item>本跳丢包,但其后存在不丢包的跳 → <see cref="HopVerdict.IcmpRateLimited" />(限速,转发正常)。</item>
    /// <item>丢包一直延续到最后一跳 → <see cref="HopVerdict.SuspectedLoss" />。</item>
    /// </list>
    /// </summary>
    /// <param name="hops">按 TTL 升序排列的跳列表;就地写入 <see cref="TraceHop.Verdict" />。</param>
    public static void ApplyVerdicts(IReadOnlyList<TraceHop> hops)
    {
        ArgumentNullException.ThrowIfNull(hops);
        for (int i = 0; i < hops.Count; i++)
        {
            TraceHop hop = hops[i];
            if (hop.SawUnreachable)
            {
                hop.Verdict = HopVerdict.Unreachable;
                continue;
            }
            if (hop.Sent == 0 || hop.LossPercent <= 0)
            {
                hop.Verdict = HopVerdict.Ok;
                continue;
            }

            // 本跳之后只要还有一跳是完好的,本跳的丢包就不影响转发,只是它自己不爱回 ICMP。
            bool healthyDownstream = false;
            for (int j = i + 1; j < hops.Count; j++)
            {
                if (hops[j].Received > 0 && hops[j].LossPercent <= 0)
                {
                    healthyDownstream = true;
                    break;
                }
            }
            hop.Verdict = healthyDownstream
                              ? hop.Received == 0 ? HopVerdict.NoResponse : HopVerdict.IcmpRateLimited
                              : hop.Received == 0
                                  ? HopVerdict.NoResponse
                                  : HopVerdict.SuspectedLoss;

            // 末跳(或其后全是无响应跳)自己丢包,才是真正的端到端丢包。
            if (!healthyDownstream && hop.Received > 0)
            {
                hop.Verdict = HopVerdict.SuspectedLoss;
            }
        }
    }
}

/// <summary>一次链路追踪的参数。</summary>
/// <param name="Target">目标主机名或 IP。</param>
/// <param name="MaxHops">最大跳数。</param>
/// <param name="TimeoutMs">单次探测超时(毫秒)。</param>
/// <param name="IntervalMs">两轮之间的间隔(毫秒)。</param>
/// <param name="Rounds">总轮数;0 表示持续追踪直到取消(mtr 风格)。</param>
/// <param name="ResolveHostNames">是否对每跳做 PTR 反解。</param>
public sealed record TraceOptions(
    string Target,
    int MaxHops = 30,
    int TimeoutMs = 1000,
    int IntervalMs = 1000,
    int Rounds = 0,
    bool ResolveHostNames = true
);

/// <summary>一轮追踪结束后推送的快照。</summary>
/// <param name="Hops">按 TTL 升序的跳列表(与上一轮是同一批对象,统计量已累加)。</param>
/// <param name="TargetReached">是否已经探到目标。</param>
/// <param name="Round">已完成的轮数。</param>
/// <param name="ResolvedAddress">目标解析出的地址。</param>
public sealed record TraceUpdate(
    IReadOnlyList<TraceHop> Hops,
    bool TargetReached,
    int Round,
    IPAddress? ResolvedAddress
);

/// <summary>链路追踪服务:逐跳探测目标,流式推送每轮结果。</summary>
public interface ITraceRouteService
{
    /// <summary>
    /// 开始追踪。每完成一轮推送一个快照;<paramref name="options" /> 的 Rounds 为 0 时持续到取消。
    /// 目标解析失败会抛 <see cref="System.Net.Sockets.SocketException" />。
    /// </summary>
    /// <param name="options">追踪参数。</param>
    /// <param name="cancellationToken">取消标记。</param>
    IAsyncEnumerable<TraceUpdate> RunAsync(TraceOptions options, CancellationToken cancellationToken = default);
}
