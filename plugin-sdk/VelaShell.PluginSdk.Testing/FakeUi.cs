using VelaShell.PluginSdk.Ui;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="IPluginPanel" /> 的测试替身。内容工厂**惰性**调用:测试环境未必装载
/// Avalonia 运行时,默认只记录工厂;需要断言内容时显式调 <see cref="CreateContent" />。
/// </summary>
public sealed class FakePanel(PanelOptions options, Func<object> contentFactory) : IPluginPanel
{
    private object? _content;

    /// <summary>打开面板时的选项。</summary>
    public PanelOptions Options { get; } = options;

    /// <summary>内容工厂(未调用)。</summary>
    public Func<object> ContentFactory { get; } = contentFactory;

    /// <summary>调用内容工厂并缓存结果(测试断言用;需要 Avalonia 时请在 UI/headless 环境调)。</summary>
    public object CreateContent() => _content ??= ContentFactory();

    /// <inheritdoc />
    public string PanelId { get; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public bool IsOpen { get; private set; } = true;

    /// <inheritdoc />
    public event Action? Closed;

    /// <summary>被要求置前的次数(面板已关闭时不计)。</summary>
    public int ActivateCount { get; private set; }

    /// <inheritdoc />
    public double PlacementRatio { get; private set; } = double.NaN;

    /// <inheritdoc />
    public event Action<double>? PlacementRatioChanged;

    /// <summary>替身:模拟"用户拖完分割条",让测试能验证插件有没有把新宽度记下来。</summary>
    public void RaisePlacementRatioChanged(double ratio)
    {
        PlacementRatio = ratio;
        PlacementRatioChanged?.Invoke(ratio);
    }

    /// <inheritdoc />
    public Task ActivateAsync()
    {
        if (IsOpen)
        {
            ActivateCount++;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CloseAsync()
    {
        if (IsOpen)
        {
            IsOpen = false;
            Closed?.Invoke();
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(CloseAsync());
}

/// <summary><see cref="IUiApi" /> 的测试替身:面板进内存列表。</summary>
public sealed class FakeUi : IUiApi
{
    /// <summary>全部已打开(含已关闭)的面板,按打开顺序。</summary>
    public List<FakePanel> Panels { get; } = [];

    /// <summary>最近打开的面板;尚未打开过时抛出。</summary>
    public FakePanel LastPanel => Panels[^1];

    /// <inheritdoc />
    public Task<IPluginPanel> ShowPanelAsync(PanelOptions options, Func<object> contentFactory, CancellationToken cancellationToken = default)
    {
        var panel = new FakePanel(options, contentFactory);
        Panels.Add(panel);
        return Task.FromResult<IPluginPanel>(panel);
    }
}
