namespace VelaShell.Core.Services;

/// <summary>
/// 一条后台活动的只读快照。视图层拿到的永远是这个不可变记录而不是活动本身,
/// 于是 UI 线程枚举与后台线程上报之间不需要任何同步。
/// </summary>
/// <param name="Id">活动的进程内唯一序号(按开始顺序递增,供 UI 稳定排序/去重)。</param>
/// <param name="Title">面向用户的活动名称(已本地化),例如"正在加载插件"。</param>
/// <param name="Detail">副标题:当前正在处理的具体对象,可为空。</param>
/// <param name="Progress">0~1 的确定进度;<see langword="null" /> 表示进度不可知(圆环走不确定动画)。</param>
public sealed record BackgroundActivitySnapshot(long Id, string Title, string? Detail, double? Progress);

/// <summary>
/// 一条进行中的后台活动的句柄。<see cref="IDisposable.Dispose" /> 即结束该活动 ——
/// 因此调用方必须用 <c>using</c>,异常路径下指示器才不会永远转下去。
/// </summary>
public interface IBackgroundActivityScope : IDisposable
{
    /// <summary>上报进度与当前处理对象。高频调用安全:服务内部对通知做了合并节流。</summary>
    /// <param name="progress">0~1 的确定进度;<see langword="null" /> 表示回到不确定状态。</param>
    /// <param name="detail">当前处理对象;<see langword="null" /> 表示不改动。</param>
    void Report(double? progress, string? detail = null);

    /// <summary>改写活动名称与副标题(一条活动分阶段推进时用)。</summary>
    /// <param name="title">新的活动名称。</param>
    /// <param name="detail">新的副标题;<see langword="null" /> 表示清空。</param>
    void Describe(string title, string? detail = null);
}

/// <summary>
/// 全局后台活动账本:任何耗时超过一瞬的后台工作在此登记,状态栏右下角的圆环据此显示。
/// <para>
/// 只做登记,不做任何调度或 UI 决策 —— 聚合成"一个圆环"是视图模型的事。
/// 全部成员线程安全,可从任意线程调用;<see cref="Changed" /> 在调用方线程触发,
/// 订阅方(视图模型)负责切到 UI 线程。
/// </para>
/// </summary>
public interface IBackgroundActivityService
{
    /// <summary>当前进行中的活动快照(按开始顺序)。无活动时为空表。</summary>
    IReadOnlyList<BackgroundActivitySnapshot> Activities { get; }

    /// <summary>活动集合或其中任一条的进度发生变化时触发(进度变化经节流合并)。</summary>
    event Action? Changed;

    /// <summary>开始一条后台活动;返回的句柄必须被释放。</summary>
    /// <param name="title">面向用户的活动名称(已本地化)。</param>
    /// <param name="detail">副标题,可为空。</param>
    /// <param name="progress">初始进度;<see langword="null" />(默认)表示进度不可知。</param>
    /// <returns>活动句柄。</returns>
    IBackgroundActivityScope Begin(string title, string? detail = null, double? progress = null);
}

/// <summary>
/// <see cref="IBackgroundActivityService" /> 的默认实现:锁保护的 List + 不可变快照。
/// <para>
/// 进度上报可能来自紧循环(逐文件预热、逐字节传输),因此结构性变化(开始/结束)立刻通知,
/// 而纯进度变化按 <see cref="ProgressCoalesceWindow" /> 合并 —— 否则一个圆环就能把
/// UI 调度器灌满,这正是大文件传输曾经踩过的坑。
/// </para>
/// </summary>
public sealed class BackgroundActivityService : IBackgroundActivityService, IDisposable
{
    /// <summary>纯进度通知的合并窗口:约 8fps,肉眼连续,又不足以压垮调度器。</summary>
    private static readonly TimeSpan ProgressCoalesceWindow = TimeSpan.FromMilliseconds(120);

    private readonly List<Entry> _entries = [];
    private readonly Lock _gate = new();
    private readonly Timer _coalesce;
    private IReadOnlyList<BackgroundActivitySnapshot> _snapshot = [];
    private long _nextId;
    private bool _coalescePending;
    private bool _disposed;

    /// <summary>构造后台活动账本。</summary>
    public BackgroundActivityService() =>
        _coalesce = new(static state => ((BackgroundActivityService)state!).OnCoalesceElapsed(), this,
            Timeout.Infinite, Timeout.Infinite);

    /// <inheritdoc />
    public IReadOnlyList<BackgroundActivitySnapshot> Activities
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public IBackgroundActivityScope Begin(string title, string? detail = null, double? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var entry = new Entry(this, Interlocked.Increment(ref _nextId), title, detail, Normalize(progress));
        lock (_gate)
        {
            if (_disposed)
            {
                return entry; // 已释放:句柄仍可用可释放,只是不再进账本。
            }
            _entries.Add(entry);
            Rebuild();
        }
        Raise();
        return entry;
    }

    /// <summary>停止节流计时器并清空账本(应用退出路径)。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _entries.Clear();
            _snapshot = [];
        }
        _coalesce.Dispose();
    }

    private static double? Normalize(double? progress) =>
        progress is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : null;

    /// <summary>在锁内重建不可变快照。</summary>
    private void Rebuild() =>
        _snapshot = [.. _entries.Select(e => new BackgroundActivitySnapshot(e.Id, e.Title, e.Detail, e.Progress))];

    private void Remove(Entry entry)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.Remove(entry))
            {
                return;
            }
            Rebuild();
        }
        Raise();
    }

    /// <summary>
    /// 在锁内改写一条活动并重建快照,通知按窗口合并。
    /// <para>
    /// 变更必须与 <see cref="Rebuild" /> 同处一把锁:<c>double?</c> 是 16 字节结构,
    /// 锁外赋值 + 锁内读取会读到"有值、值却是上一次的"这种撕裂状态。
    /// </para>
    /// </summary>
    private void Touch(Entry entry, Action<Entry> mutate)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.Contains(entry))
            {
                return;
            }
            mutate(entry);
            Rebuild();
            if (_coalescePending)
            {
                return;
            }
            _coalescePending = true;
        }
        try
        {
            _coalesce.Change(ProgressCoalesceWindow, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 与 Dispose 竞态:通知丢掉即可,账本已清空。
        }
    }

    private void OnCoalesceElapsed()
    {
        lock (_gate)
        {
            _coalescePending = false;
            if (_disposed)
            {
                return;
            }
        }
        Raise();
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // 订阅方(视图层)的异常绝不回灌到上报活动的后台工作里。
        }
    }

    /// <summary>一条活动的可变状态;对外只以不可变快照示人。</summary>
    private sealed class Entry(BackgroundActivityService owner, long id, string title, string? detail, double? progress)
        : IBackgroundActivityScope
    {
        private int _disposed;

        public long Id { get; } = id;

        public string Title { get; private set; } = title;

        public string? Detail { get; private set; } = detail;

        public double? Progress { get; private set; } = progress;

        public void Report(double? progress, string? detail = null)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            double? normalized = Normalize(progress);
            owner.Touch(this, entry =>
            {
                entry.Progress = normalized;
                if (detail is not null)
                {
                    entry.Detail = detail;
                }
            });
        }

        public void Describe(string title, string? detail = null)
        {
            if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrWhiteSpace(title))
            {
                return;
            }
            owner.Touch(this, entry =>
            {
                entry.Title = title;
                entry.Detail = detail;
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return; // 幂等:重复释放不会把别人的活动挤掉。
            }
            owner.Remove(this);
        }
    }
}
