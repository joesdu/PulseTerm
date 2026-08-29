namespace VelaShell.Infrastructure.Ftp;

/// <summary>
/// 一个**上限可以在运行时调小**的并发闸(FIFO 排队,支持取消)。
/// <para>
/// 为什么不用 <see cref="SemaphoreSlim" />:它的许可数只增不减 —— <c>Release</c> 能加,
/// 没有任何 API 能减。而 FTP 池恰恰需要往下调:服务器只允许一条连接时(一次只能传一个文件),
/// 池必须从"最多 4 条"当场收成"最多 1 条",并让后来的请求老老实实排队复用那一条,
/// 而不是继续去开注定被拒的第二条连接。
/// </para>
/// <para>
/// 名额在唤醒等待者时**直接移交**(不先减后加),因此收紧上限不会被某个刚好插进来的
/// 新请求钻空子:排队的人拿到的是上一个人交回来的那一个名额。
/// </para>
/// </summary>
internal sealed class AdjustableConcurrencyGate(int limit)
{
    private readonly Lock _sync = new();
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();
    private int _limit = Math.Max(1, limit);
    private int _inUse;

    /// <summary>当前上限(至少 1)。</summary>
    public int Limit
    {
        get
        {
            lock (_sync)
            {
                return _limit;
            }
        }
    }

    /// <summary>当前已被占用的名额数。</summary>
    public int InUse
    {
        get
        {
            lock (_sync)
            {
                return _inUse;
            }
        }
    }

    /// <summary>取一个名额;没有空位就排队等。</summary>
    public Task AcquireAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }
        TaskCompletionSource<bool> waiter;
        lock (_sync)
        {
            if (_inUse < _limit)
            {
                _inUse++;
                return Task.CompletedTask;
            }
            waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }
        return WaitAsync(waiter, cancellationToken);
    }

    /// <summary>
    /// 交回一个名额。队列非空时把名额**直接交给**排在最前的等待者(<see cref="_inUse" /> 不变),
    /// 否则计数减一。
    /// <para>
    /// <b>超发时不移交</b>:上限刚被调小的那一刻,已经发出去的名额可能多于新上限
    /// (4 个传输在跑,上限却收成了 1)。这时候每回收一个就转手给下一个,占用数永远降不下来,
    /// 收紧等于没收 —— 服务器那边看到的仍是多个并发传输,照样报忙。
    /// 所以只有在"没有超发"时才移交,超出的部分收回来就地作废。
    /// </para>
    /// </summary>
    public void Release()
    {
        lock (_sync)
        {
            if (_inUse <= _limit)
            {
                while (_waiters.TryDequeue(out TaskCompletionSource<bool>? next))
                {
                    // TrySetResult 为 false = 这个等待者已经取消了,名额继续往下传。
                    if (next.TrySetResult(true))
                    {
                        return;
                    }
                }
            }
            if (_inUse > 0)
            {
                _inUse--;
            }
        }
    }

    /// <summary>
    /// 只把名额还回计数,**不**叫醒排队的人。
    /// <para>
    /// 用在"拿了名额却没能拿到资源"的失败路径上:此时并没有资源变空闲,把名额移交给下一个人
    /// 只会让他重复同一次失败(FTP 池里的表现就是又去开一条注定被 421 顶回来的连接)。
    /// 叫醒的时机交给真正归还资源的那一方(<see cref="Release" />)。
    /// </para>
    /// </summary>
    public void ReleaseWithoutHandoff()
    {
        lock (_sync)
        {
            if (_inUse > 0)
            {
                _inUse--;
            }
        }
    }

    /// <summary>
    /// 把上限调小到 <paramref name="newLimit" />(只减不增,最低 1),返回生效后的上限。
    /// 已经租出去的名额不会被强行收回 —— 它们归还时超出新上限的部分自然不再放行。
    /// </summary>
    public int LimitTo(int newLimit)
    {
        lock (_sync)
        {
            _limit = Math.Max(1, Math.Min(_limit, newLimit));
            return _limit;
        }
    }

    private static async Task WaitAsync(TaskCompletionSource<bool> waiter, CancellationToken cancellationToken)
    {
        await using CancellationTokenRegistration registration =
            cancellationToken.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), waiter);
        await waiter.Task.ConfigureAwait(false);
    }
}
