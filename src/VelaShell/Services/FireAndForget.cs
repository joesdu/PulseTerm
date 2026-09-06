using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VelaShell.Services;

/// <summary>
/// 从同步的事件处理器里发起一段异步工作,并保证它的异常被记下来而不是掀翻进程。
/// </summary>
/// <remarks>
/// <para>
/// <c>async void</c> 的问题只有一个,但很致命:方法体里抛出的异常没有任何东西承接,
/// 它直接抛到同步上下文上 —— 在 Avalonia 里就是进程级未处理异常。一个点错的文件对话框
/// 能把整个应用带走,而用户看到的只是"点了一下就闪退"。
/// </para>
/// <para>
/// 事件处理器的签名是 <c>void</c>,改不了;能改的是**不要让 async 状态机成为唯一的
/// 异常出口**。写成 <c>private void X_Click(…) =&gt; FireAndForget.Run(() =&gt; XAsync(…));</c>
/// 之后,异常落进这里的 catch,写进诊断日志(<c>DiagnosticLog</c> 已把 Trace 全量落盘),
/// 界面继续活着。
/// </para>
/// </remarks>
public static class FireAndForget
{
    /// <summary>
    /// 跑一段异步工作;异常记入 <c>Trace</c>(进而落进日志文件),绝不外抛。
    /// </summary>
    /// <param name="action">要执行的异步工作。</param>
    /// <param name="callerName">调用点名字,自动填入,用于日志定位。</param>
    public static async void Run(Func<Task> action, [CallerMemberName] string? callerName = null)
    {
        // 参数校验也要在 try 里面。这是 async void:在第一个 await 之前抛出的异常同样
        // 没有任何东西承接,照样是进程级未处理异常 —— 一个"兜底用的 helper"自己把进程
        // 打崩,是最糟糕的结局。(写这条时的单元测试就是这么把测试宿主崩掉的。)
        try
        {
            ArgumentNullException.ThrowIfNull(action);
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 用户取消 / 会话取消:正常事件,不记录 —— 记了只会把日志淹掉。
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[FireAndForget] {callerName}: {ex}");
        }
    }
}
