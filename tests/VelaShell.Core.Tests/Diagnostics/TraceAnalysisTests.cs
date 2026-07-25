using System.Net;
using VelaShell.Core.Diagnostics;

namespace VelaShell.Core.Tests.Diagnostics;

/// <summary>
/// 逐跳判定规则的回归测试。这是整个链路追踪里最容易做反的一段:中间跳丢包绝大多数不是故障,
/// 而是路由器对 ICMP 回包限速,直接按丢包率标红会把人引到错误的方向。
/// </summary>
[TestClass]
[TestCategory("Trace")]
public class TraceAnalysisTests
{
    [TestMethod]
    public void MidPathLoss_WithHealthyDownstream_IsRateLimitingNotFailure()
    {
        // 第 2 跳丢一半,但第 3 跳完好 —— 转发正常,只是那台路由器不爱回 ICMP。
        List<TraceHop> hops = [Hop(1, 2, 2), Hop(2, 4, 2), Hop(3, 2, 2)];
        TraceAnalysis.ApplyVerdicts(hops);

        Assert.AreEqual(HopVerdict.Ok, hops[0].Verdict);
        Assert.AreEqual(HopVerdict.IcmpRateLimited, hops[1].Verdict);
        Assert.AreEqual(HopVerdict.Ok, hops[2].Verdict);
    }

    [TestMethod]
    public void SilentHop_WithHealthyDownstream_IsNoResponse()
    {
        // 整跳一个都没回(* * *),但后面还有跳能回。
        List<TraceHop> hops = [Hop(1, 2, 2), Hop(2, 3, 0), Hop(3, 2, 2)];
        TraceAnalysis.ApplyVerdicts(hops);

        Assert.AreEqual(HopVerdict.NoResponse, hops[1].Verdict);
    }

    [TestMethod]
    public void LossThatReachesTheLastHop_IsSuspectedRealLoss()
    {
        // 丢包从第 2 跳一直延续到末跳,后面没有一个完好的跳 —— 这才可能是真的丢包。
        List<TraceHop> hops = [Hop(1, 4, 4), Hop(2, 4, 2), Hop(3, 4, 2)];
        TraceAnalysis.ApplyVerdicts(hops);

        Assert.AreEqual(HopVerdict.Ok, hops[0].Verdict);
        Assert.AreEqual(HopVerdict.SuspectedLoss, hops[1].Verdict);
        Assert.AreEqual(HopVerdict.SuspectedLoss, hops[2].Verdict);
    }

    [TestMethod]
    public void LastHopLossAlone_IsSuspectedRealLoss()
    {
        List<TraceHop> hops = [Hop(1, 4, 4), Hop(2, 4, 4), Hop(3, 4, 3)];
        TraceAnalysis.ApplyVerdicts(hops);

        Assert.AreEqual(HopVerdict.Ok, hops[1].Verdict);
        Assert.AreEqual(HopVerdict.SuspectedLoss, hops[2].Verdict);
    }

    [TestMethod]
    public void Unreachable_OverridesLossReasoning()
    {
        var hop = new TraceHop(1);
        hop.Add(new(1, IPAddress.Parse("10.0.0.1"), null, false, true));
        List<TraceHop> hops = [hop];
        TraceAnalysis.ApplyVerdicts(hops);

        Assert.AreEqual(HopVerdict.Unreachable, hops[0].Verdict);
    }

    [TestMethod]
    public void Statistics_TrackLossBestWorstAverageAndJitter()
    {
        var hop = new TraceHop(1);
        var address = IPAddress.Parse("10.0.0.1");
        hop.Add(new(1, address, TimeSpan.FromMilliseconds(10), false, false));
        hop.Add(new(1, address, TimeSpan.FromMilliseconds(30), false, false));
        hop.Add(new(1, null, null, false, false)); // 超时
        hop.Add(new(1, address, TimeSpan.FromMilliseconds(20), false, false));

        Assert.AreEqual(4, hop.Sent);
        Assert.AreEqual(3, hop.Received);
        Assert.AreEqual(25.0, hop.LossPercent, 0.001);
        Assert.AreEqual(10.0, hop.Best!.Value.TotalMilliseconds, 0.001);
        Assert.AreEqual(30.0, hop.Worst!.Value.TotalMilliseconds, 0.001);
        Assert.AreEqual(20.0, hop.Average!.Value.TotalMilliseconds, 0.001);
        Assert.AreEqual(20.0, hop.Last!.Value.TotalMilliseconds, 0.001);
        // 抖动 = |30-10| 与 |20-30| 的平均 = 15
        Assert.AreEqual(15.0, hop.JitterMs, 0.001);
    }

    [TestMethod]
    public void MultipleAddresses_AreAllKept_ForEcmpPaths()
    {
        // 同一 TTL 返回多个地址是 ECMP 的正常现象,只留最后一个会让链路看起来在跳变。
        var hop = new TraceHop(3);
        hop.Add(new(3, IPAddress.Parse("10.0.0.1"), TimeSpan.FromMilliseconds(5), false, false));
        hop.Add(new(3, IPAddress.Parse("10.0.0.2"), TimeSpan.FromMilliseconds(6), false, false));
        hop.Add(new(3, IPAddress.Parse("10.0.0.1"), TimeSpan.FromMilliseconds(5), false, false));

        Assert.HasCount(2, hop.Addresses);
    }

    [TestMethod]
    public void Recent_IsCappedSoLongRunsDoNotGrowWithoutBound()
    {
        var hop = new TraceHop(1);
        var address = IPAddress.Parse("10.0.0.1");
        for (int i = 0; i < TraceHop.RecentCapacity + 25; i++)
        {
            hop.Add(new(1, address, TimeSpan.FromMilliseconds(i), false, false));
        }
        Assert.HasCount(TraceHop.RecentCapacity, hop.Recent);
    }

    /// <summary>造一跳:发 <paramref name="sent" /> 次、回 <paramref name="received" /> 次。</summary>
    private static TraceHop Hop(int ttl, int sent, int received)
    {
        var hop = new TraceHop(ttl);
        var address = IPAddress.Parse($"10.0.0.{ttl}");
        for (int i = 0; i < sent; i++)
        {
            hop.Add(i < received
                        ? new(ttl, address, TimeSpan.FromMilliseconds(10 + ttl), false, false)
                        : new(ttl, null, null, false, false));
        }
        return hop;
    }
}
