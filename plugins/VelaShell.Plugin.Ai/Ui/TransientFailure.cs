namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 一次失败值不值得自动重来。
/// </summary>
/// <remarks>
/// <para>
/// 从 <c>ChatPanelView</c> 拆出来的一簇(Q-01)。这是**行为决策**,不是格式化:
/// 判错了的两种后果都不轻 —— 把参数错当成瞬时故障会白白多打一次(还多花一次钱),
/// 把网络抖动当成永久失败则让用户在明明能成的时候看到一条红字。
/// </para>
/// <para>
/// 只认"再试一次可能就好了"的那些:网络层故障、超时,以及服务端的 408 / 429 / 5xx。
/// 参数错、鉴权失败重试一万次也一样。
/// </para>
/// </remarks>
public static class TransientFailure
{
    /// <summary>这个异常值不值得重来一次。</summary>
    /// <remarks>
    /// <b>逐层看 InnerException</b>:HTTP 客户端与 SDK 会把真实原因包上一两层,
    /// 只看最外层那个的话,绝大多数可重试的失败都会被判成永久失败。
    /// </remarks>
    /// <param name="exception">捕获到的异常。</param>
    /// <returns>可重试时为 true。</returns>
    public static bool IsTransient(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException:
                case IOException:
                case TimeoutException:
                    return true;
                case System.ClientModel.ClientResultException { Status: 408 or 429 or >= 500 and < 600 }:
                    return true;
            }
        }
        return false;
    }
}
