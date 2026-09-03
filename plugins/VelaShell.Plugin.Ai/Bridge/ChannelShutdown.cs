using System.Net.WebSockets;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>
/// 长连接渠道收摊时的共用动作。
/// </summary>
/// <remarks>
/// 这里的每一个方法都在做同一件事:<b>别拿异常当收摊的通知</b>。
///
/// <para>把宿主的取消令牌直接交给 <c>ClientWebSocket.ReceiveAsync</c> 看着省事,代价却不小:
/// 取消一次挂起的接收,库内走的是 <c>Abort</c> —— 底层 TCP 直接掐断,于是一次退出要连锁抛出
/// 套接字 <c>IOException</c>、TLS 层 <c>IOException</c>、<c>HttpClient</c> 的
/// <c>TaskCanceledException</c>,最后才轮到我们自己这层。对端看到的也不是"它下线了",
/// 而是"连接莫名其妙断了"。</para>
///
/// <para>正确的收法是发一个 Close 帧,让读循环从对端的 Close 应答里自然返回 —— 零异常,
/// 且平台侧能干净地摘掉这条长连接。对端不应答时才 <c>Abort</c> 兜底,不为一个已经失联的
/// 服务端无限等下去。</para>
/// </remarks>
internal static class ChannelShutdown
{
    /// <summary>优雅关闭的宽限:发 Close 帧、以及等对端回 Close 各给这么多时间。</summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 可取消的等待:等满返回 true,被取消返回 false —— 不抛。
    /// </summary>
    /// <remarks>
    /// 与 <c>PluginManager.DelayObservedAsync</c> 同一个套路:经 <c>ContinueWith</c> 观察取消。
    /// 直接 <c>await Task.Delay(delay, token)</c> 会在每次停机时给调试输出添一条
    /// <c>TaskCanceledException</c>,而调用方要的信息只是一个"要不要接着跑"的布尔值。
    /// </remarks>
    public static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var wait = Task.Delay(delay, cancellationToken);
        await wait.ContinueWith(static _ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
        return !wait.IsCanceled;
    }

    /// <summary>
    /// 等到任务结束、或等到令牌取消,先到先返回;两条路都不抛。
    /// </summary>
    /// <remarks>
    /// 任务本身出的错留给调用方之后 <c>await</c> 时去接 —— 这里只回答"该收摊了吗"。
    /// </remarks>
    public static async Task WhenCompletedOrCancelledAsync(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            return;
        }
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);
        await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
    }

    /// <summary>
    /// 优雅关闭一条 WebSocket:发 Close 帧,等读循环收到对端的 Close 应答后自行返回。
    /// </summary>
    /// <param name="socket">要关的连接。</param>
    /// <param name="receiveLoop">还挂在这条连接上的读循环。</param>
    public static async Task CloseAsync(ClientWebSocket socket, Task receiveLoop)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var deadline = new CancellationTokenSource(CloseGrace);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, deadline.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 对端已经不在了,Close 帧发不出去 —— 直接走下面的 Abort 兜底。
        }
        if (await Task.WhenAny(receiveLoop, Task.Delay(CloseGrace)).ConfigureAwait(false) != receiveLoop)
        {
            // 对端不回 Close。退出不能因此挂住,只能掐断 —— 这是唯一一条会制造收摊异常的路,
            // 也只有在平台确实失联时才会走到。
            socket.Abort();
        }
    }
}
