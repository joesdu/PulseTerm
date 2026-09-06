namespace VelaShell.Services;

/// <summary>
/// 一条断掉的会话该不该自动重连、以及第几次该等多久。
/// </summary>
/// <remarks>
/// <para>
/// 从 <c>MainWindowViewModel</c> 拆出来的一簇(Q-01)。判定原先散在两处 ——
/// 掉线后的排程与"睡眠唤醒/网络恢复"后的批量重连各写一份,而两份的条件必须一致:
/// 少一个条件就会出现"手动断开的标签自己又连上了"这种明确违背用户意图的行为。
/// </para>
/// <para>
/// 只做判断,不排程:定时器、倒计时提示与真正的重连动作都留在视图模型里。
/// </para>
/// </remarks>
public static class ReconnectPolicy
{
    /// <summary>退避序列的封顶指数(<c>1 &lt;&lt; 6 = 64</c> 秒)。</summary>
    /// <remarks>再往上移只会溢出,而 64 秒已经超过任何合理的重连间隔上限。</remarks>
    private const int MaxBackoffShift = 6;

    /// <summary>
    /// 这条会话是否符合自动重连的条件。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>用户自己点了断开的不碰</b> —— 那是明确的意图,自动连回去等于跟用户较劲。</item>
    /// <item><b>本地终端不自动重开</b> —— shell 退出(<c>exit</c>)是用户意图,
    /// 自动拉起会没完没了。</item>
    /// </list>
    /// </remarks>
    /// <param name="autoReconnectEnabled">设置里的自动重连开关。</param>
    /// <param name="isDisconnected">当前是否处于断开状态。</param>
    /// <param name="userRequestedDisconnect">是不是用户主动断开的。</param>
    /// <param name="isLocalShell">是不是本地终端。</param>
    /// <returns>符合条件时为 true。</returns>
    public static bool ShouldReconnect(
        bool autoReconnectEnabled,
        bool isDisconnected,
        bool userRequestedDisconnect,
        bool isLocalShell) =>
        autoReconnectEnabled && isDisconnected && !userRequestedDisconnect && !isLocalShell;

    /// <summary>
    /// 还能不能再试一次。
    /// </summary>
    /// <param name="attemptsSoFar">已经试过几次。</param>
    /// <param name="configuredMaxRetries">设置里的最大重试次数。</param>
    /// <returns>还有额度时为 true。</returns>
    /// <remarks>
    /// 上限至少为 1:配置里写 0 的话一次都不试,而那多半是手改配置写坏了而不是本意 ——
    /// 「开着自动重连、又一次都不试」不是一个说得通的组合。
    /// </remarks>
    public static bool HasAttemptsLeft(int attemptsSoFar, int configuredMaxRetries) =>
        attemptsSoFar < Math.Max(1, configuredMaxRetries);

    /// <summary>
    /// 第 <paramref name="attempt" /> 次自动重连之前该等多久(秒)。
    /// </summary>
    /// <remarks>
    /// 指数退避 1、2、4、8… 封顶在用户配的 <paramref name="configuredSeconds" />。
    /// 固定间隔在两头都不对:网线刚插回来的那一瞬,等满 30 秒才试是白等;
    /// 而服务器真的宕了,每 30 秒敲一次门也只是徒劳地刷状态栏。
    /// 从 1 秒起跳能抓住"抖一下就好"的绝大多数情况,退到设置值之后与原行为一致。
    /// </remarks>
    /// <param name="attempt">第几次尝试(从 1 起)。</param>
    /// <param name="configuredSeconds">设置里的重连间隔,作为退避上限。</param>
    /// <returns>等待秒数。</returns>
    public static int DelaySeconds(int attempt, int configuredSeconds)
    {
        int cap = Math.Clamp(configuredSeconds, 1, 300);
        int backoff = 1 << Math.Clamp(attempt - 1, 0, MaxBackoffShift);
        return Math.Min(backoff, cap);
    }
}
