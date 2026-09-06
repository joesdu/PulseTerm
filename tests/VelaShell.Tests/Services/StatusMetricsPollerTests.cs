using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Terminal;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Services;

/// <summary>
/// 状态栏指标采样循环。
/// </summary>
/// <remarks>
/// 这段逻辑原先埋在 5000 行的 <c>MainWindowViewModel</c> 里,一条用例都没有 ——
/// 它依赖的东西(定时器、活动标签、指标服务)全都得先把那个巨类构造出来才够得着。
/// 拆成独立协作者之后,重入闸、失焦降频、写回前重认标签这三条时序规矩才第一次可测。
/// </remarks>
[TestClass]
[TestCategory("StatusMetrics")]
public sealed class StatusMetricsPollerTests
{
    private static TerminalTabViewModel ConnectedTab(Guid? sessionId = null) =>
        new(Substitute.For<ITerminalEmulator>())
        {
            Profile = new() { Name = "host", Host = "10.0.0.1" },
            SessionId = sessionId ?? Guid.NewGuid(),
            ConnectionStatus = SessionStatus.Connected,
        };

    [TestMethod]
    public void UnfocusedPollingSlowsDownButNeverSpeedsUp()
    {
        // 失焦是"没在看,慢一点";每次采样对远端都是一次 fork/exec + 一条 SSH 通道的
        // 建立与拆除,降频是实打实的省。
        StatusMetricsPoller poller = new(new StatusBarViewModel(), () => null, null)
        {
            ConfiguredIntervalSeconds = 2
        };
        Assert.AreEqual(TimeSpan.FromSeconds(2), poller.CurrentInterval());

        poller.SetReduced(true);
        Assert.AreEqual(TimeSpan.FromSeconds(10), poller.CurrentInterval());

        poller.SetReduced(false);
        Assert.AreEqual(TimeSpan.FromSeconds(2), poller.CurrentInterval());
    }

    [TestMethod]
    public void AnIntervalAlreadySlowerThanTheUnfocusedFloorIsLeftAlone()
    {
        // 用户自己配了 30 秒,失焦时不该被"加速"到 10 秒。
        StatusMetricsPoller poller = new(new StatusBarViewModel(), () => null, null)
        {
            ConfiguredIntervalSeconds = 30
        };

        poller.SetReduced(true);

        Assert.AreEqual(TimeSpan.FromSeconds(30), poller.CurrentInterval());
    }

    [TestMethod]
    public void TheConfiguredIntervalIsClamped()
    {
        // 配置文件可以手改;一个 0 会让定时器疯转,一个 99999 等于关掉了状态栏。
        StatusMetricsPoller poller = new(new StatusBarViewModel(), () => null, null)
        {
            ConfiguredIntervalSeconds = 0
        };
        Assert.AreEqual(TimeSpan.FromSeconds(1), poller.CurrentInterval());

        poller.ConfiguredIntervalSeconds = 99_999;
        Assert.AreEqual(TimeSpan.FromSeconds(60), poller.CurrentInterval());
    }

    [TestMethod]
    public async Task WithoutAConnectedTabTheMetricsAreCleared()
    {
        ISessionMetricsService metrics = Substitute.For<ISessionMetricsService>();
        StatusBarViewModel statusBar = new() { CpuUsage = "42%" };
        StatusMetricsPoller poller = new(statusBar, () => null, metrics);

        await poller.PollMetricsAsync();

        Assert.AreEqual("--", statusBar.CpuUsage, "没有已连接会话时不该留着上一台机器的读数。");
        await metrics.DidNotReceive().GetMetricsAsync(Arg.Any<Guid>());
    }

    [TestMethod]
    public async Task ADisconnectedTabIsTreatedAsNoTab()
    {
        ISessionMetricsService metrics = Substitute.For<ISessionMetricsService>();
        TerminalTabViewModel tab = ConnectedTab();
        tab.ConnectionStatus = SessionStatus.Disconnected;
        StatusBarViewModel statusBar = new() { CpuUsage = "42%" };
        StatusMetricsPoller poller = new(statusBar, () => tab, metrics);

        await poller.PollMetricsAsync();

        Assert.AreEqual("--", statusBar.CpuUsage);
        await metrics.DidNotReceive().GetMetricsAsync(Arg.Any<Guid>());
    }

    [TestMethod]
    public async Task ASampleLandsOnTheStatusBar()
    {
        TerminalTabViewModel tab = ConnectedTab();
        ISessionMetricsService metrics = Substitute.For<ISessionMetricsService>();
        // 用 Parse 造样本,与生产路径同源 —— SessionMetrics 的字段是 private init,
        // 硬塞值既做不到、也会让用例与真实的探测输出脱节。
        SessionMetrics sample = SessionMetrics.Parse(
            string.Join('\n', "__P__", "4", "__M__", "1000 250 0 0", "__SS__", ""))!;
        sample.CpuPercent = 12.5;
        metrics.GetMetricsAsync(tab.SessionId).Returns(sample);
        StatusBarViewModel statusBar = new();
        StatusMetricsPoller poller = new(statusBar, () => tab, metrics);

        await poller.PollMetricsAsync();

        Assert.AreEqual("12.50%", statusBar.CpuUsage);
        Assert.AreEqual("25.0%", statusBar.MemUsage);
        Assert.AreEqual("--", statusBar.SwapUsage, "没有交换分区时显示占位符,而不是 0.0%。");
        Assert.AreEqual("--", statusBar.DiskUsage);
    }

    /// <summary>探测期间用户切了标签,结果不能写到别的会话上。</summary>
    /// <remarks>
    /// 一次采样要在远端 fork/exec 一趟,慢于采样间隔是常态,所以"采完发现人已经走了"
    /// 不是边缘情况而是日常。写回前不重认一次标签,状态栏上就会出现另一台机器的读数 ——
    /// 而那种错误数据看起来完全正常。
    /// </remarks>
    [TestMethod]
    public async Task ASampleThatFinishesAfterATabSwitchIsDiscarded()
    {
        TerminalTabViewModel first = ConnectedTab();
        TerminalTabViewModel second = ConnectedTab();
        TerminalTabViewModel active = first;

        var gate = new TaskCompletionSource<SessionMetrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ISessionMetricsService metrics = Substitute.For<ISessionMetricsService>();
        metrics.GetMetricsAsync(first.SessionId).Returns(_ => gate.Task);

        StatusBarViewModel statusBar = new();
        StatusMetricsPoller poller = new(statusBar, () => active, metrics);

        Task polling = poller.PollMetricsAsync();
        active = second;                       // 用户切走了
        SessionMetrics late = SessionMetrics.Parse(
            string.Join('\n', "__P__", "1", "__M__", "1000 250 0 0", "__SS__", ""))!;
        late.CpuPercent = 99;
        gate.SetResult(late);
        await polling;

        Assert.AreNotEqual("99.00%", statusBar.CpuUsage,
            "采样期间切了标签,这一份结果属于上一台机器,不能写到状态栏上。");
    }

    /// <summary>上一次采样还没回来时,不再压第二次进去。</summary>
    /// <remarks>
    /// 一台负载高的机器采一次要好几秒,而定时器每两秒响一次。不挡住重入的话,
    /// 探测会越堆越多,把那台已经很吃力的机器彻底压垮 —— 而用户看到的只是"状态栏卡住了"。
    /// </remarks>
    [TestMethod]
    public async Task ASecondSampleIsNotStartedWhileTheFirstIsStillRunning()
    {
        TerminalTabViewModel tab = ConnectedTab();
        var gate = new TaskCompletionSource<SessionMetrics?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ISessionMetricsService metrics = Substitute.For<ISessionMetricsService>();
        metrics.GetMetricsAsync(tab.SessionId).Returns(_ => gate.Task);
        StatusMetricsPoller poller = new(new StatusBarViewModel(), () => tab, metrics);

        Task first = poller.PollMetricsAsync();
        await poller.PollMetricsAsync();       // 第二次:应当立刻返回,不发起探测

        await metrics.Received(1).GetMetricsAsync(tab.SessionId);
        gate.SetResult(null);
        await first;
    }

    [TestMethod]
    public async Task AFailedProbeDoesNotEscapeIntoTheUiLoop()
    {
        // 探测失败是常态(网络抖动、对端没有 /proc)。让它抛出去会走到未观察异常,
        // 而状态栏只要等下一个 tick 重试就好。
        TerminalTabViewModel tab = ConnectedTab();
        ISessionMetricsService metrics = Substitute.For<ISessionMetricsService>();
        metrics.GetMetricsAsync(tab.SessionId).Returns<Task<SessionMetrics?>>(_ => throw new InvalidOperationException("boom"));
        StatusMetricsPoller poller = new(new StatusBarViewModel(), () => tab, metrics);

        await poller.PollMetricsAsync();

        // 闸必须复位,否则一次失败之后再也不会采样了。
        await poller.PollMetricsAsync();
        await metrics.Received(2).GetMetricsAsync(tab.SessionId);
    }

    [TestMethod]
    public async Task LatencyIsProbedOnlyEveryThirdTick()
    {
        // ping 比指标采样便宜得多,但也没必要每次都发 —— 延迟不会两秒一变。
        TerminalTabViewModel tab = ConnectedTab();
        tab.ConnectionStatus = SessionStatus.Disconnected; // 走"清空延迟"那一支,不真的发 ICMP
        tab.Latency = TimeSpan.FromMilliseconds(42);
        StatusMetricsPoller poller = new(new StatusBarViewModel(), () => tab, null);

        await poller.PollLatencyAsync();       // 第 0 次:执行
        Assert.IsNull(tab.Latency);

        tab.Latency = TimeSpan.FromMilliseconds(42);
        await poller.PollLatencyAsync();       // 第 1 次:跳过
        await poller.PollLatencyAsync();       // 第 2 次:跳过
        Assert.AreEqual(TimeSpan.FromMilliseconds(42), tab.Latency);

        await poller.PollLatencyAsync();       // 第 3 次:执行
        Assert.IsNull(tab.Latency);
    }
}
