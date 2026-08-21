using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace VelaShell.Plugin.Sql.Ui;

/// <summary>
/// 最小可观察基类。
/// <para>
/// 刻意**不引 ReactiveUI**:它会随插件目录分发一整套(ReactiveUI + Splat + DynamicData),
/// 而且插件 ALC 里那份 <c>RxApp</c> 与宿主的是两个独立实例 —— 它的 <c>MainThreadScheduler</c>
/// 不会自动挂到 Avalonia 的调度器上,命令的 <c>CanExecuteChanged</c> 就会在后台线程上触发绑定更新。
/// 这里要的只是 <see cref="INotifyPropertyChanged" /> 与两个命令类型,自己写几十行更稳
/// (与 Redis 插件同一条决定)。
/// </para>
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>值变化时赋值并通知。</summary>
    /// <typeparam name="T">属性类型。</typeparam>
    /// <param name="field">后备字段。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名(自动填充)。</param>
    /// <returns>赋值后的值。</returns>
    protected T SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return field;
        }
        field = value;
        RaisePropertyChanged(propertyName);
        return field;
    }

    /// <summary>手动触发一次属性变更通知(派生属性用)。</summary>
    /// <param name="propertyName">属性名。</param>
    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (PropertyChanged is not { } handler)
        {
            return;
        }
        // 绑定只能在 UI 线程更新。加载逻辑不少跑在后台线程,统一在这里封送,
        // 免得每个调用点都记得 Dispatcher。
        if (Dispatcher.UIThread.CheckAccess())
        {
            handler(this, new(propertyName));
        }
        else
        {
            Dispatcher.UIThread.Post(() => handler(this, new(propertyName)));
        }
    }
}

/// <summary>同步命令。</summary>
/// <param name="execute">执行体。</param>
/// <param name="canExecute">可执行判据。</param>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => execute();

    /// <summary>重新求一次可执行状态。</summary>
    public void RaiseCanExecuteChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
        }
    }
}

/// <summary>
/// 异步命令。执行期间自动禁用自己 —— 数据库操作动辄几秒,不禁用的话用户会连点。
/// </summary>
/// <param name="execute">执行体。</param>
/// <param name="canExecute">可执行判据。</param>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>是不是正在执行。</summary>
    public bool IsRunning => _running;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 命令体自己负责把失败呈现给用户(状态栏/错误面板)。
            // 这里吞掉是为了不让一个 async void 把进程带走 —— 但**不做静默重试**:
            // 用户看到的必须是"这次失败了",而不是什么都没发生。
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>重新求一次可执行状态。</summary>
    public void RaiseCanExecuteChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
        }
    }
}
