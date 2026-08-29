using NSubstitute;
using VelaShell.Core.Services;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests;

[TestClass]
[TestCategory("Metrics")]
public class SessionMetricsServiceTests
{
    /// <summary>
    /// 让替身像一台 Linux 主机那样回答 POSIX shell 探针。采集前先探一句是 #305 之后的前置动作:
    /// 不答这一句,服务就认定对端跑不了 /proc 探测,一条命令都不会发。
    /// </summary>
    private static void StubPosixShell(ISshClientWrapper client) =>
        client.RunCommandDetailedAsync(RemoteShellProbe.ProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult(RemoteShellProbe.PosixMarker + "\n", "", 0)));

    private static string Probe(long cpuBusy, long cpuIdle, long rx, long tx) =>
        "__P__\n4\n" +
        "__L__\n0.5 0.4 0.3 1/100 200\n" +
        "__M__\n1000 500\n" +
        "__D__\n2000 1000\n" +
        "__O__\nDebian\n" +
        "__K__\n6.1\n" +
        $"__S__\ncpu  {cpuBusy} 0 0 {cpuIdle} 0 0 0\n" +
        $"__N__\n{rx} {tx}\n";

    [TestMethod]
    public async Task SecondSample_ComputesInstantCpuAndNetRates()
    {
        var sessionId = Guid.NewGuid();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        StubPosixShell(client);
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        var service = new SessionMetricsService(connections);

        // Sample 1: busy=100 idle=900; rx=1_000_000 tx=500_000.
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(100, 900, 1_000_000, 500_000));
        SessionMetrics? first = await service.GetMetricsAsync(sessionId);

        Assert.IsNotNull(first);
        Assert.IsFalse(first.HasNetRates);           // no previous sample yet
        Assert.AreEqual(12.5, first.CpuPercent, 0.1); // loadavg fallback: 0.5 / 4 cores

        // Sample 2 (+~0.5s wall time): busy +300, idle +700 → 30% instantaneous;
        // rx +2 MB, tx +0.5 MB over the elapsed interval.
        await Task.Delay(500);
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(400, 1600, 3_097_152, 1_024_288));
        SessionMetrics? second = await service.GetMetricsAsync(sessionId);

        Assert.IsNotNull(second);
        Assert.AreEqual(30.0, second.CpuPercent, 0.1); // (dTotal-dIdle)/dTotal = 300/1000
        Assert.IsTrue(second.HasNetRates);
        Assert.IsGreaterThan(0, second.NetRxBytesPerSec, "rx rate should be positive");
        Assert.IsGreaterThan(0, second.NetTxBytesPerSec, "tx rate should be positive");
        Assert.IsGreaterThan(second.NetTxBytesPerSec, second.NetRxBytesPerSec,
            "rx moved 4x the bytes of tx, so its rate must dominate");
    }

    [TestMethod]
    public async Task CounterReset_ClampsRatesToZeroInsteadOfNegative()
    {
        var sessionId = Guid.NewGuid();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        StubPosixShell(client);
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        var service = new SessionMetricsService(connections);

        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(100, 900, 5_000_000, 5_000_000));
        await service.GetMetricsAsync(sessionId);

        await Task.Delay(300);
        // Interface bounced: counters restarted from near zero.
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(200, 1800, 1_000, 1_000));
        SessionMetrics? second = await service.GetMetricsAsync(sessionId);

        Assert.IsNotNull(second);
        Assert.AreEqual(0, second.NetRxBytesPerSec);
        Assert.AreEqual(0, second.NetTxBytesPerSec);
    }

    [TestMethod]
    public async Task SecondSample_ComputesPerCorePercents_AndPerNicRates()
    {
        var sessionId = Guid.NewGuid();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        StubPosixShell(client);
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        var service = new SessionMetricsService(connections);

        static string Extras(long c0Busy, long c0Idle, long c1Busy, long c1Idle, long rx0, long tx0, long rx1, long tx1) =>
            $"__C__\ncpu0 {c0Busy} 0 0 {c0Idle} 0 0 0\ncpu1 {c1Busy} 0 0 {c1Idle} 0 0 0\n" +
            $"__NI__\neth0 {rx0} {tx0}\neth1 {rx1} {tx1}\n";

        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(100, 900, 1000, 1000) + Extras(50, 450, 50, 450, 500, 500, 500, 500));
        SessionMetrics? first = await service.GetMetricsAsync(sessionId);

        Assert.IsNotNull(first);
        Assert.IsNull(first.CorePercents); // 首个采样没有差分基准
        Assert.IsNull(first.NicRates);

        await Task.Delay(300);
        // cpu0: +90 busy / +10 idle → 90%;cpu1: +10 busy / +90 idle → 10%。
        // eth0 收发各 +100_000;eth1 不动。
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(200, 1800, 201_000, 201_000)
                + Extras(140, 460, 60, 540, 100_500, 100_500, 500, 500));
        SessionMetrics? second = await service.GetMetricsAsync(sessionId);

        Assert.IsNotNull(second);
        Assert.IsNotNull(second.CorePercents);
        Assert.HasCount(2, second.CorePercents);
        Assert.AreEqual(90.0, second.CorePercents[0], 0.5);
        Assert.AreEqual(10.0, second.CorePercents[1], 0.5);

        Assert.IsNotNull(second.NicRates);
        Assert.HasCount(2, second.NicRates);
        Assert.AreEqual("eth0", second.NicRates[0].Name);
        Assert.IsGreaterThan(0, second.NicRates[0].RxBytesPerSec, "eth0 应有下行速率");
        Assert.AreEqual(0, second.NicRates[1].RxBytesPerSec, 0.01); // eth1 无流量
    }

    [TestMethod]
    public async Task DisconnectedSession_ReturnsNullAndForgetsHistory()
    {
        var sessionId = Guid.NewGuid();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        StubPosixShell(client);
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        var service = new SessionMetricsService(connections);

        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(100, 900, 1000, 1000));
        await service.GetMetricsAsync(sessionId);

        client.IsConnected.Returns(false);
        Assert.IsNull(await service.GetMetricsAsync(sessionId));

        // Reconnected: history was dropped, so the next sample is a fresh "first" one.
        client.IsConnected.Returns(true);
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Probe(500, 1500, 9000, 9000));
        SessionMetrics? afterReconnect = await service.GetMetricsAsync(sessionId);

        Assert.IsNotNull(afterReconnect);
        Assert.IsFalse(afterReconnect.HasNetRates);
    }

    /// <summary>
    /// 非 POSIX 远端(Windows 的 cmd.exe)必须**一条采集命令都不发**,并如实报告"无数据"。
    /// 以前不拦:cmd 把 <c>echo __P__; nproc; …</c> 整行原样回显,Parse 只在输出为空时才返回
    /// null,于是状态栏拿着这堆回声显示 CPU 0.00% 的假数据,而且每秒重来一次(#305 同源)。
    /// </summary>
    [TestMethod]
    public async Task NonPosixRemote_SendsNoProbeCommand_AndReportsNoData()
    {
        var sessionId = Guid.NewGuid();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        // cmd.exe 的回答:printf 不是内部或外部命令。
        client.RunCommandDetailedAsync(RemoteShellProbe.ProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("", "'printf' 不是内部或外部命令", 1)));
        // 万一门没关住,让采集命令返回 cmd 那种"整行回声",以便断言能抓到假数据。
        client.RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("__P__; nproc ; echo __L__; cat /proc/loadavg\n");
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        var service = new SessionMetricsService(connections);

        Assert.IsNull(await service.GetMetricsAsync(sessionId));
        Assert.IsNull(await service.GetStaticInfoAsync(sessionId));

        await client.DidNotReceive().RunCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
