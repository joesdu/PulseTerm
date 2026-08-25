namespace VelaShell.Infrastructure.Startup;

/// <summary>
/// 拉起请求的收件箱:把「请求到达」与「界面能处理请求」这两件事解耦。
/// <para>
/// 时序上二者必然错开 —— 命令行里的那条请求在 <c>Main</c> 里就解析出来了,那时 Avalonia 还没起、
/// 主窗口更不存在;而单实例转发来的请求可能在任意时刻从管道线程冒出来。所以先入队,
/// 等 <see cref="Attach" /> 把处理器接上再一并放行。没有它,冷启动带 <c>-url</c> 的那次登录会直接丢掉。
/// </para>
/// </summary>
public static class ExternalLaunchInbox
{
    private static readonly Lock Gate = new();
    private static readonly List<ExternalLaunchRequest> Pending = [];
    private static Action<ExternalLaunchRequest>? _handler;

    /// <summary>投递一条请求:已接上处理器就直接转交,否则先攒着。</summary>
    public static void Publish(ExternalLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Action<ExternalLaunchRequest>? handler;
        lock (Gate)
        {
            handler = _handler;
            if (handler is null)
            {
                Pending.Add(request);
                return;
            }
        }
        handler(request);
    }

    /// <summary>接上处理器(主窗口就绪后),并把攒下的请求按到达顺序补发。</summary>
    public static void Attach(Action<ExternalLaunchRequest> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ExternalLaunchRequest[] backlog;
        lock (Gate)
        {
            _handler = handler;
            backlog = [.. Pending];
            Pending.Clear();
        }
        foreach (ExternalLaunchRequest request in backlog)
        {
            handler(request);
        }
    }

    /// <summary>摘掉处理器(退出时);此后到达的请求重新入队,不会打到已销毁的窗口上。</summary>
    public static void Detach()
    {
        lock (Gate)
        {
            _handler = null;
        }
    }
}
