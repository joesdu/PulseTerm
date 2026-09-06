namespace VelaShell.Presentation.Tests.Fakes;

/// <summary>
/// 可手动推进的假时钟,用来驱动"过 N 秒之后做点什么"这类行为。
/// </summary>
/// <remarks>
/// 挂在真实时钟上的用例只能靠 <c>Task.Delay</c> 去赌,而那种用例迟早会成为下一条偶发失败 ——
/// 本仓已经为此吃过两次亏(<c>AreSequenceEqualAwaitTests</c> 的引信、插件的二维码用例)。
/// <para>
/// 刻意不用 ReactiveUI 那套 <c>ISequencer</c>:它没有带虚拟时间的测试替身,
/// 自己实现一个要连 <c>IWorkItem</c> 一族一起实现。被测代码那边只需要一个窄委托。
/// </para>
/// </remarks>
public sealed class FakeClock
{
    private readonly List<Entry> _pending = [];

    /// <summary>当前的虚拟时刻(自零点起)。</summary>
    public TimeSpan Now { get; private set; }

    /// <summary>还没到期、也没被取消的回调数(用来确认取消确实生效了)。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>安排一次延时回调;释放返回值即取消。</summary>
    /// <param name="delay">延时。</param>
    /// <param name="callback">到期时执行。</param>
    /// <returns>取消句柄。</returns>
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Entry entry = new(Now + delay, callback);
        _pending.Add(entry);
        return new Cancellation(() => _pending.Remove(entry));
    }

    /// <summary>把时间往前推,按到期顺序执行期间到点的回调。</summary>
    /// <param name="by">推进多久。</param>
    public void Advance(TimeSpan by)
    {
        TimeSpan target = Now + by;
        // 逐个取最早到期的那条来跑,而不是先筛一批再执行:回调自己可能再安排新的延时
        // (合并刷新就会),先筛的话新排的那条会被当作"这一轮之后"而漏掉。
        while (true)
        {
            Entry? next = null;
            foreach (Entry entry in _pending)
            {
                if (entry.Due <= target && (next is null || entry.Due < next.Due))
                {
                    next = entry;
                }
            }
            if (next is null)
            {
                break;
            }
            _pending.Remove(next);
            Now = next.Due;
            next.Callback();
        }
        Now = target;
    }

    private sealed record Entry(TimeSpan Due, Action Callback);

    private sealed class Cancellation(Action onDispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            onDispose();
        }
    }
}
