namespace VelaShell.Services;

/// <summary>
/// 管理同步防抖定时器的 <see cref="CancellationTokenSource" /> 生命周期,
/// 并带一个终端关闭闸门。调用 <see cref="Shutdown" /> 之后,
/// <see cref="TrySwapNew" /> 会以原子方式拒绝创建新的 CTS 对象,
/// 从而任何防抖工作在释放边界之后都无法再启动。
/// </summary>
internal sealed class SyncDebounceLifecycle
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private bool _shutdown;

    /// <summary>
    /// 以原子方式检查关闭闸门,若仍处于活动状态,则用一个新的 CTS 替换当前
    /// 的 CTS(取消并释放原先持有的对象)。成功时返回 <c>true</c> 并提供有效的
    /// <paramref name="token" />;在 <see cref="Shutdown" /> 已被调用后返回 <c>false</c>
    /// \u2014 此情况下调用方不得启动防抖工作。
    /// </summary>
    public bool TrySwapNew(out CancellationToken token)
    {
        lock (_gate)
        {
            if (_shutdown)
            {
                token = CancellationToken.None;
                return false;
            }

            var next = new CancellationTokenSource();
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = next;
            token = next.Token;
            return true;
        }
    }

    /// <summary>
    /// 防抖延迟结束后,以原子方式核实关闭闸门以及 <paramref name="token" /> 是否仍是
    /// 当前的防抖令牌,然后在持有闸门的情况下调用 <paramref name="start" />,
    /// 使得 <see cref="Shutdown" /> 无法在检查与委托调用之间完成。成功时返回
    /// <c>true</c> 并附带已启动的任务;若生命周期已关闭或令牌已被取代,
    /// 则返回 <c>false</c>(task 为 null)。
    /// </summary>
    public bool TryStartCurrent(Func<Task> start, CancellationToken token, out Task? task)
    {
        lock (_gate)
        {
            if (_shutdown || _cts is null || _cts.Token != token)
            {
                task = null;
                return false;
            }

            // 持锁调用委托 \u2014 Shutdown 无法在检查与委托调用之间完成。
            task = start();

            // 清空 CTS \u2014 本次防抖已被消费。
            _cts.Dispose();
            _cts = null;

            return true;
        }
    }

    /// <summary>
    /// 终端关闭:取消并释放当前的 CTS(若有),并阻止此后任何
    /// <see cref="TrySwapNew" /> 或 <see cref="TryStartCurrent" /> 调用成功。
    /// 可多次调用,也可在从未创建过 CTS 时调用。
    /// </summary>
    public void Shutdown()
    {
        lock (_gate)
        {
            _shutdown = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
