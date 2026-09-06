using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 自动重连的退避节奏。
/// </summary>
/// <remarks>
/// 原先是固定间隔,两头都不对:网线刚插回来的那一瞬等满 30 秒是白等;
/// 服务器真宕了,每 30 秒敲一次门也只是徒劳地刷状态栏。
/// 现在 1、2、4、8… 起跳,封顶在用户配的间隔 —— 抖一下就好的情况几乎立刻恢复,
/// 长时间不可达则退回原来的节奏。
/// </remarks>
[TestClass]
[TestCategory("Reconnect")]
public sealed class ReconnectBackoffTests
{
    [TestMethod]
    public void FirstAttempt_WaitsOneSecond()
    {
        // 关键的一条:第一次重试必须"几乎立刻",否则网络抖一下也要等满配置间隔。
        Assert.AreEqual(1, MainWindowViewModel.ReconnectDelaySeconds(attempt: 1, configuredSeconds: 30));
    }

    [TestMethod]
    public void BackoffDoubles_UntilItReachesTheConfiguredCap()
    {
        int[] actual = [.. Enumerable.Range(1, 8)
            .Select(attempt => MainWindowViewModel.ReconnectDelaySeconds(attempt, configuredSeconds: 30))];

        Assert.AreSequenceEqual([1, 2, 4, 8, 16, 30, 30, 30], actual);
    }

    [TestMethod]
    public void ASmallConfiguredInterval_CapsImmediately()
    {
        // 用户把间隔配成 3 秒,就不该因为退避而等出 4、8 秒来。
        int[] actual = [.. Enumerable.Range(1, 5)
            .Select(attempt => MainWindowViewModel.ReconnectDelaySeconds(attempt, configuredSeconds: 3))];

        Assert.AreSequenceEqual([1, 2, 3, 3, 3], actual);
    }

    [TestMethod]
    public void OutOfRangeConfiguredValues_AreClamped()
    {
        // 设置文件被手改或损坏时不能算出 0 秒(死循环般重试)或天文数字。
        Assert.AreEqual(1, MainWindowViewModel.ReconnectDelaySeconds(1, configuredSeconds: 0));
        Assert.AreEqual(1, MainWindowViewModel.ReconnectDelaySeconds(1, configuredSeconds: -5));
        Assert.AreEqual(64, MainWindowViewModel.ReconnectDelaySeconds(20, configuredSeconds: 99999));
    }

    [TestMethod]
    public void LargeAttemptCounts_DoNotOverflow()
    {
        // attempt 很大时移位不能溢出成负数 —— 负的延时会让计时器立刻触发,变成疯狂重试。
        foreach (int attempt in (int[])[1, 10, 100, 1000, int.MaxValue])
        {
            int delay = MainWindowViewModel.ReconnectDelaySeconds(attempt, configuredSeconds: 30);
            Assert.IsGreaterThan(0, delay, $"attempt={attempt} 算出了非正的延时。");
            Assert.IsLessThanOrEqualTo(30, delay);
        }
    }

    [TestMethod]
    public void ZeroOrNegativeAttempt_IsTreatedAsTheFirstOne()
    {
        Assert.AreEqual(1, MainWindowViewModel.ReconnectDelaySeconds(0, configuredSeconds: 30));
        Assert.AreEqual(1, MainWindowViewModel.ReconnectDelaySeconds(-1, configuredSeconds: 30));
    }
}
