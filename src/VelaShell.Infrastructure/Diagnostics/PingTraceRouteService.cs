using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using VelaShell.Core.Diagnostics;

namespace VelaShell.Infrastructure.Diagnostics;

/// <summary>
/// 经典的 ICMP + TTL 递增追踪:对 TTL=1..n 各发一个 Echo,中间路由器回 TimeExceeded,
/// 目标回 EchoReply。用 <see cref="Ping" /> 而非原始套接字,三大平台都不需要管理员权限。
/// </summary>
/// <remarks>
/// Linux 上 .NET 的 <see cref="Ping" /> 在缺少 ping_group_range 权限时会退化为调用系统 ping,
/// 那条路径拿不到 TTL 语义,结果会变成"每一跳都直接命中目标"。<see cref="RunAsync" /> 因此在
/// 首轮结束后自检:若第 1 跳就报已到达目标且总跳数为 1,说明 TTL 没生效,抛出可读的异常而不是
/// 给出一张假表。
/// </remarks>
public sealed class PingTraceRouteService : ITraceRouteService
{
    /// <summary>探测载荷:32 字节,与系统 tracert 一致。</summary>
    private static readonly byte[] Payload = new byte[32];

    /// <summary>一轮之内同时在飞的探测数上限,避免一次放出 30 个 ICMP 触发限速。</summary>
    private const int MaxConcurrentProbes = 8;

    private readonly ConcurrentDictionary<IPAddress, string?> _hostNames = new();

    /// <inheritdoc />
    public async IAsyncEnumerable<TraceUpdate> RunAsync(
        TraceOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        IPAddress target = await ResolveAsync(options.Target, cancellationToken).ConfigureAwait(false);

        Dictionary<int, TraceHop> hops = [];
        int maxTtlSeen = options.MaxHops;
        bool reached = false;
        for (int round = 1; options.Rounds == 0 || round <= options.Rounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int limit = Math.Min(maxTtlSeen, options.MaxHops);
            IReadOnlyList<TraceProbe> probes = await ProbeRoundAsync(target, limit, options, cancellationToken)
                .ConfigureAwait(false);

            foreach (TraceProbe probe in probes)
            {
                if (!hops.TryGetValue(probe.Ttl, out TraceHop? hop))
                {
                    hop = new(probe.Ttl);
                    hops[probe.Ttl] = hop;
                }
                hop.Add(probe);
                if (probe.Reached)
                {
                    reached = true;
                    // 到达目标后不再探更大的 TTL —— 后面全是超时,白白拖长每一轮。
                    maxTtlSeen = Math.Min(maxTtlSeen, probe.Ttl);
                }
            }

            // 首轮是按 maxHops 全量探的,目标之后那些 TTL 也会命中目标本身,留着会让
            // 列表出现一长串重复的目标行(29 跳其实只有 19 跳)。收敛后一次性清掉。
            foreach (int extra in hops.Keys.Where(ttl => ttl > maxTtlSeen).ToArray())
            {
                hops.Remove(extra);
            }

            List<TraceHop> ordered = [.. hops.Values.OrderBy(h => h.Ttl)];
            TrimTrailingSilence(ordered, reached);
            TraceAnalysis.ApplyVerdicts(ordered);
            if (options.ResolveHostNames)
            {
                await ResolveHostNamesAsync(ordered, cancellationToken).ConfigureAwait(false);
            }

            if (round == 1)
            {
                AssertTtlHonoured(ordered, target);
            }
            yield return new(ordered, reached, round, target);

            if (options.Rounds != 0 && round >= options.Rounds)
            {
                break;
            }
            await Task.Delay(Math.Max(0, options.IntervalMs), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 首轮自检:TTL 若没生效,第一跳就会直接命中目标,整条链路只有一跳。
    /// </summary>
    /// <remarks>
    /// 只在非 Windows 上、且目标是公网地址时才判定为异常:Windows 的 IcmpSendEcho 一直尊重 TTL;
    /// 而同网段的目标(SSH 连内网机器是常态)本来就只有一跳,把它当成"TTL 失效"是误报 ——
    /// 第一版就是这么错的,连内网服务器直接报错。
    /// </remarks>
    private static void AssertTtlHonoured(IReadOnlyList<TraceHop> hops, IPAddress target)
    {
        if (OperatingSystem.IsWindows() || IsPrivate(target) || hops is not [{ Ttl: 1, IsTarget: true }])
        {
            return;
        }
        throw new PlatformNotSupportedException(
            "TTL 探测未生效:本机的 ICMP 实现忽略了 TTL(Linux 上常见于缺少 CAP_NET_RAW 或 "
            + "net.ipv4.ping_group_range 未放开,此时 .NET 会退化为调用系统 ping)。"
        );
    }

    /// <summary>判断是否为私有/环回/链路本地地址(这类目标一跳可达属正常)。</summary>
    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return true; // IPv6 的私网判定另说,保守起见不报错
        }
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => true,
            127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] is >= 16 and <= 31 => true,
            192 when bytes[1] == 168 => true,
            100 when bytes[1] is >= 64 and <= 127 => true, // CGNAT
            _ => false
        };
    }

    /// <summary>
    /// 砍掉尾部那串"从未回应过"的跳。到达目标之前它们可能只是暂时不回,到达之后就纯属噪声了。
    /// </summary>
    private static void TrimTrailingSilence(List<TraceHop> hops, bool reached)
    {
        if (!reached)
        {
            return;
        }
        int last = hops.FindLastIndex(h => h.Received > 0);
        if (last >= 0 && last < hops.Count - 1)
        {
            hops.RemoveRange(last + 1, hops.Count - last - 1);
        }
    }

    private static async Task<IReadOnlyList<TraceProbe>> ProbeRoundAsync(
        IPAddress target,
        int maxTtl,
        TraceOptions options,
        CancellationToken cancellationToken
    )
    {
        List<TraceProbe> results = [with(maxTtl)];
        using SemaphoreSlim gate = new(MaxConcurrentProbes);
        var tasks = new Task<TraceProbe>[maxTtl];
        for (int ttl = 1; ttl <= maxTtl; ttl++)
        {
            int captured = ttl;
            tasks[ttl - 1] = ProbeAsync(captured);
        }
        // 全部等到底,再统一看取消。两个理由:
        // ① 逐个 await 时第一个抛出之后,剩下的任务就没人观察了;
        // ② 取消一轮时,门后排队的每个探测都会各自抛一个 TaskCanceledException
        //    (15 跳 ÷ 8 个并发 = 停止时一次冒出 7 个,调试器里刷一屏)。
        //    现在它们各自安静返回,取消只在这里抛一次。
        results.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        return results;

        async Task<TraceProbe> ProbeAsync(int ttl)
        {
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 整轮马上就作废了,占个"没回应"的位子即可
                return new(ttl, null, null, false, false);
            }
            try
            {
                using Ping ping = new();
                PingReply reply = await ping
                    .SendPingAsync(target, options.TimeoutMs, Payload, new PingOptions(ttl, true))
                    .ConfigureAwait(false);
                return reply.Status switch
                {
                    IPStatus.TtlExpired => new(ttl, reply.Address, TimeSpan.FromMilliseconds(reply.RoundtripTime), false, false),
                    IPStatus.Success => new(ttl, reply.Address, TimeSpan.FromMilliseconds(reply.RoundtripTime), true, false),
                    IPStatus.DestinationHostUnreachable
                        or IPStatus.DestinationNetworkUnreachable
                        or IPStatus.DestinationPortUnreachable
                        or IPStatus.DestinationUnreachable => new(ttl, reply.Address, null, false, true),
                    _ => new(ttl, null, null, false, false)
                };
            }
            catch (Exception ex) when (ex is PingException or InvalidOperationException)
            {
                // 单跳探测失败等同于该跳超时,不能拖垮整轮。
                return new(ttl, null, null, false, false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task ResolveHostNamesAsync(IReadOnlyList<TraceHop> hops, CancellationToken cancellationToken)
    {
        foreach (TraceHop hop in hops)
        {
            if (hop.HostName is not null || hop.Addresses.Count == 0)
            {
                continue;
            }
            IPAddress address = hop.Addresses[0];
            if (_hostNames.TryGetValue(address, out string? cached))
            {
                hop.HostName = cached;
                continue;
            }
            string? name = await LookupAsync(address, cancellationToken).ConfigureAwait(false);
            _hostNames[address] = name;
            hop.HostName = name;
        }
    }

    private static async Task<string?> LookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            IPHostEntry entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(entry.HostName) || entry.HostName == address.ToString()
                       ? null
                       : entry.HostName;
        }
        catch
        {
            // 没有 PTR 记录是常态,不值得声张。
            return null;
        }
    }

    private static async Task<IPAddress> ResolveAsync(string target, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target, out IPAddress? parsed))
        {
            return parsed;
        }
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(target, cancellationToken).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
    }
}
