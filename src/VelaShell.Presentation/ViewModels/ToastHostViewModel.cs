using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace VelaShell.Presentation.ViewModels;

/// <summary>
/// 浮层提示的堆栈:统一承接原先各自往状态栏那一个字符串里写的运行时反馈。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不顺手收进消息中心。</b>方案里提过"自动收进消息中心",但
/// <c>INotificationCenter</c> 的注释已经把分工写死了:那里存的是"要留存、可回看"的东西
/// (有新版本、公告),而"会话断了"这类运行时告警<b>刻意不进去</b> ——
/// 「塞进这里只会把真正要读的东西淹掉」。逐条自动归档等于推翻那个决定,
/// 所以这里只做即时通道,归档与否留给各调用点自己决定。
/// </para>
/// <para>
/// 延时由 <see cref="ToastDelay" /> 注入而不是直接用 <c>DispatcherTimer</c>:自动消失的时机
/// 是这个类唯一值得测的行为,挂在真实时钟上就只能靠 <c>Task.Delay</c> 去赌,
/// 那种用例迟早会成为下一条偶发失败。
/// </para>
/// <para>
/// 用一个窄委托而不是 <c>ISequencer</c>(状态栏用的那个抽象):本仓这一套里没有带虚拟时间的
/// 测试替身,自己实现一个要连 <c>IWorkItem</c> 一族一起实现 —— 为了一个"过 N 秒调一次"
/// 的需求不值得。委托的替身两行就写完了。
/// </para>
/// </remarks>
public sealed class ToastHostViewModel : ReactiveObject, IDisposable
{
    /// <summary>
    /// 安排一次延时回调;返回的句柄被释放即取消。
    /// </summary>
    /// <param name="delay">延时。</param>
    /// <param name="callback">到期时执行(必须在 UI 线程上)。</param>
    /// <returns>取消句柄。</returns>
    public delegate IDisposable ToastDelay(TimeSpan delay, Action callback);

    /// <summary>信息级的停留时长。</summary>
    public static readonly TimeSpan InfoLifetime = TimeSpan.FromSeconds(4);

    /// <summary>警告级的停留时长 —— 比信息久,因为它多半意味着还有下文。</summary>
    public static readonly TimeSpan WarningLifetime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 同时最多显示几条。
    /// </summary>
    /// <remarks>
    /// 超出时挤掉<b>最老的</b>那条。上限不设的话,一次批量操作失败能刷出几十条,
    /// 把整个窗口盖住 —— 那比看不见提示更糟。
    /// </remarks>
    public const int MaxVisible = 4;

    private readonly ToastDelay _delay;
    private readonly Dictionary<ToastViewModel, IDisposable> _timers = [];

    /// <summary>构造。</summary>
    /// <param name="delay">安排自动消失的延时器。</param>
    public ToastHostViewModel(ToastDelay delay)
    {
        ArgumentNullException.ThrowIfNull(delay);
        _delay = delay;
    }

    /// <summary>当前可见的提示,最新的在前。</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <summary>点掉一条(关闭按钮)。</summary>
    public ReactiveCommand<ToastViewModel, RxVoid> DismissCommand =>
        field ??= ReactiveCommand.Create<ToastViewModel>(Dismiss);

    /// <summary>
    /// 执行一条提示自带的操作,然后把它收掉。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 执行完就收掉,是因为那个按钮多半是一次性的(「立即重连」按完就没有意义了);
    /// 留在屏幕上只会让人怀疑到底点没点上。
    /// </para>
    /// <para>
    /// <b>异常必须在这里接住,不能让它逃进 Rx 管道。</b><see cref="ReactiveCommand" /> 遇到
    /// 未处理异常会把自己的管道<b>打断</b> —— 一个抛异常的操作会让**之后每一条提示**的按钮
    /// 全部失效,而现场只表现为"按钮点了没反应"。所以这里吞掉并留一行痕迹:
    /// 提示照常收掉,命令保持可用。
    /// </para>
    /// </remarks>
    public ReactiveCommand<ToastViewModel, RxVoid> InvokeCommand =>
        field ??= ReactiveCommand.Create<ToastViewModel>(toast =>
        {
            try
            {
                toast?.Action?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[VelaShell] Toast action failed: {ex}");
            }
            finally
            {
                Dismiss(toast);
            }
        });

    /// <summary>有没有提示要显示(浮层整体的可见性)。</summary>
    public bool HasToasts
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>推一条信息级提示。</summary>
    /// <param name="message">正文。</param>
    /// <returns>推出去的那一条。</returns>
    public ToastViewModel Info(string message) => Show(new(ToastSeverity.Info, message));

    /// <summary>推一条警告级提示。</summary>
    /// <param name="message">正文。</param>
    /// <param name="mergeKey">同类消息的合并键;非空时就地更新同键的那一条。</param>
    /// <returns>推出去的那一条。</returns>
    public ToastViewModel Warning(string message, string? mergeKey = null) =>
        Show(new(ToastSeverity.Warning, message) { MergeKey = mergeKey });

    /// <summary>推一条错误级提示(不自动消失)。</summary>
    /// <param name="message">正文。</param>
    /// <param name="actionLabel">操作按钮文案;null = 无按钮。</param>
    /// <param name="action">点击操作按钮时执行。</param>
    /// <returns>推出去的那一条。</returns>
    public ToastViewModel Error(string message, string? actionLabel = null, Action? action = null) =>
        Show(new(ToastSeverity.Error, message, actionLabel, action));

    /// <summary>推一条提示。</summary>
    /// <param name="toast">要显示的提示。</param>
    /// <returns>实际显示的那一条(命中合并时是既有的那一条)。</returns>
    public ToastViewModel Show(ToastViewModel toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        if (toast.MergeKey is { Length: > 0 } key
            && Toasts.FirstOrDefault(t => t.MergeKey == key) is { } existing)
        {
            // 就地更新而不是再堆一条:重连倒计时逐秒刷新,不合并的话十秒堆出十条。
            existing.Message = toast.Message;
            Restart(existing);
            return existing;
        }
        Toasts.Insert(0, toast);
        while (Toasts.Count > MaxVisible)
        {
            Dismiss(Toasts[^1]);
        }
        Restart(toast);
        HasToasts = Toasts.Count > 0;
        return toast;
    }

    /// <summary>撤掉一条。</summary>
    /// <param name="toast">要撤掉的提示;不在列表里时是空操作。</param>
    public void Dismiss(ToastViewModel? toast)
    {
        if (toast is null)
        {
            return;
        }
        if (_timers.Remove(toast, out IDisposable? timer))
        {
            timer.Dispose();
        }
        Toasts.Remove(toast);
        HasToasts = Toasts.Count > 0;
    }

    /// <summary>撤掉全部。</summary>
    public void DismissAll()
    {
        foreach (IDisposable timer in _timers.Values)
        {
            timer.Dispose();
        }
        _timers.Clear();
        Toasts.Clear();
        HasToasts = false;
    }

    /// <summary>某一分级的停留时长;错误级返回 null(不自动消失)。</summary>
    /// <param name="severity">分级。</param>
    /// <returns>停留时长,或 null。</returns>
    public static TimeSpan? LifetimeOf(ToastSeverity severity) =>
        severity switch
        {
            ToastSeverity.Info => InfoLifetime,
            ToastSeverity.Warning => WarningLifetime,
            // 错误不自动消失:一条转瞬即逝的错误与没报错没有区别。
            _ => null
        };

    /// <summary>(重新)开始这一条的自动消失计时。</summary>
    private void Restart(ToastViewModel toast)
    {
        if (_timers.Remove(toast, out IDisposable? old))
        {
            old.Dispose();
        }
        if (LifetimeOf(toast.Severity) is not { } lifetime)
        {
            return;
        }
        _timers[toast] = _delay(lifetime, () => Dismiss(toast));
    }

    /// <inheritdoc />
    public void Dispose() => DismissAll();
}
