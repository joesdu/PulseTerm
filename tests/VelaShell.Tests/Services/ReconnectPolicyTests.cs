using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 一条断掉的会话该不该自动重连、还能不能再试、以及第几次该等多久。
/// </summary>
/// <remarks>
/// 判定原先散在两处 —— 掉线后的排程与"睡眠唤醒 / 网络恢复"后的批量重连各写一份,
/// 而两份的条件必须一致:少一个条件就会出现"手动断开的标签自己又连上了"
/// 这种明确违背用户意图的行为。合成一处之后,那几个条件才有地方钉。
/// </remarks>
[TestClass]
[TestCategory("Reconnect")]
public sealed class ReconnectPolicyTests
{
    [TestMethod]
    public void ADroppedRemoteSessionReconnects() =>
        Assert.IsTrue(ReconnectPolicy.ShouldReconnect(
            autoReconnectEnabled: true, isDisconnected: true,
            userRequestedDisconnect: false, isLocalShell: false, remoteShellExited: false));

    [TestMethod]
    public void TheGlobalSwitchWins() =>
        Assert.IsFalse(ReconnectPolicy.ShouldReconnect(
            autoReconnectEnabled: false, isDisconnected: true,
            userRequestedDisconnect: false, isLocalShell: false, remoteShellExited: false));

    /// <summary>用户自己点了断开的不碰。</summary>
    /// <remarks>那是明确的意图,自动连回去等于跟用户较劲。</remarks>
    [TestMethod]
    public void AManualDisconnectIsRespected() =>
        Assert.IsFalse(ReconnectPolicy.ShouldReconnect(
            autoReconnectEnabled: true, isDisconnected: true,
            userRequestedDisconnect: true, isLocalShell: false, remoteShellExited: false));

    /// <summary>本地终端不自动重开。</summary>
    /// <remarks>shell 退出(<c>exit</c>)是用户意图,自动拉起会没完没了。</remarks>
    [TestMethod]
    public void ALocalShellIsNeverRelaunched() =>
        Assert.IsFalse(ReconnectPolicy.ShouldReconnect(
            autoReconnectEnabled: true, isDisconnected: true,
            userRequestedDisconnect: false, isLocalShell: true, remoteShellExited: false));

    /// <summary>在远端敲 <c>exit</c> 退出的不碰(#383)。</summary>
    /// <remarks>
    /// 那和点断开按钮是同一个意图,只是入口不同。漏掉这一条,用户就**退不掉**:
    /// exit 之后标签自己又连上了,而这正是 #383 报的现象。
    /// </remarks>
    [TestMethod]
    public void ARemoteShellThatExitedIsNotDraggedBack() =>
        Assert.IsFalse(ReconnectPolicy.ShouldReconnect(
            autoReconnectEnabled: true, isDisconnected: true,
            userRequestedDisconnect: false, isLocalShell: false, remoteShellExited: true));

    [TestMethod]
    public void AStillConnectedSessionIsLeftAlone() =>
        Assert.IsFalse(ReconnectPolicy.ShouldReconnect(
            autoReconnectEnabled: true, isDisconnected: false,
            userRequestedDisconnect: false, isLocalShell: false, remoteShellExited: false));

    [TestMethod]
    public void AttemptsRunOutAtTheConfiguredLimit()
    {
        Assert.IsTrue(ReconnectPolicy.HasAttemptsLeft(attemptsSoFar: 0, configuredMaxRetries: 3));
        Assert.IsTrue(ReconnectPolicy.HasAttemptsLeft(attemptsSoFar: 2, configuredMaxRetries: 3));
        Assert.IsFalse(ReconnectPolicy.HasAttemptsLeft(attemptsSoFar: 3, configuredMaxRetries: 3));
    }

    /// <summary>上限至少为 1。</summary>
    /// <remarks>
    /// 配置里写 0 的话一次都不试,而「开着自动重连、又一次都不试」不是一个说得通的组合 ——
    /// 多半是手改配置写坏了。
    /// </remarks>
    [TestMethod]
    public void AZeroLimitStillAllowsOneAttempt()
    {
        Assert.IsTrue(ReconnectPolicy.HasAttemptsLeft(attemptsSoFar: 0, configuredMaxRetries: 0));
        Assert.IsFalse(ReconnectPolicy.HasAttemptsLeft(attemptsSoFar: 1, configuredMaxRetries: 0));
    }

    /// <summary>退避从 1 秒起跳,逐次翻倍。</summary>
    /// <remarks>
    /// 固定间隔在两头都不对:网线刚插回来的那一瞬,等满 30 秒才试是白等;
    /// 从 1 秒起跳能抓住"抖一下就好"的绝大多数情况。
    /// </remarks>
    [TestMethod]
    public void TheBackoffDoubles()
    {
        Assert.AreEqual(1, ReconnectPolicy.DelaySeconds(1, 30));
        Assert.AreEqual(2, ReconnectPolicy.DelaySeconds(2, 30));
        Assert.AreEqual(4, ReconnectPolicy.DelaySeconds(3, 30));
        Assert.AreEqual(8, ReconnectPolicy.DelaySeconds(4, 30));
        Assert.AreEqual(16, ReconnectPolicy.DelaySeconds(5, 30));
    }

    [TestMethod]
    public void TheBackoffIsCappedByTheConfiguredInterval()
    {
        // 退到设置值之后与原行为一致 —— 服务器真宕了,每 30 秒敲一次门也只是徒劳。
        Assert.AreEqual(30, ReconnectPolicy.DelaySeconds(6, 30));
        Assert.AreEqual(30, ReconnectPolicy.DelaySeconds(99, 30));
    }

    [TestMethod]
    public void TheConfiguredIntervalIsClamped()
    {
        // 设置文件可以手改;0 会让重连变成忙等,一个巨大的值等于永远不重连。
        Assert.AreEqual(1, ReconnectPolicy.DelaySeconds(3, 0));
        Assert.AreEqual(64, ReconnectPolicy.DelaySeconds(99, 99_999));
    }

    [TestMethod]
    public void TheShiftNeverOverflows()
    {
        // 1 << 6 已经超过任何合理的上限;不夹住的话第 32 次尝试会把移位算成负数。
        foreach (int attempt in (int[])[0, 1, 7, 32, 64, int.MaxValue])
        {
            Assert.IsGreaterThan(0, ReconnectPolicy.DelaySeconds(attempt, 300));
        }
    }
}
