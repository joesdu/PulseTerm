using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Ui;
using VelaShell.Views;

namespace VelaShell.Services.Plugins;

/// <summary>
/// <see cref="IPluginPanel" /> 的宿主实现(进程内插件):内容是插件自建的 Avalonia 控件,
/// 呈现为停靠文档或独立窗口。面板只管生命周期 —— 控件的事件与更新由插件直接操作,
/// 不经过宿主。关闭从任意线程封送到 UI 线程。
/// </summary>
internal sealed class PluginPanel : IPluginPanel
{
    /// <summary>侧栏比例的取值区间:太窄没法用,太宽就不叫侧栏了。</summary>
    private const double MinSideRatio = 0.15, MaxSideRatio = 0.85;

    /// <summary>插件给了非数(NaN/∞)时兜底用的比例。</summary>
    private const double DefaultSideRatio = 0.3;

    private readonly string _pluginId;
    private readonly IPluginLogger _log;
    private readonly PluginDocument? _document;
    private readonly DockWorkspace? _workspace;
    private readonly PluginPanelWindow? _window;
    private readonly Action<DockDocument>? _onDocumentRemoved;
    private DockGroup? _watchedGroup;
    private int _closed;

    public string PanelId { get; } = Guid.NewGuid().ToString("N");

    public bool IsOpen => _closed == 0;

    /// <inheritdoc />
    public double PlacementRatio { get; private set; } = double.NaN;

    public event Action? Closed;

    /// <inheritdoc />
    public event Action<double>? PlacementRatioChanged;

    /// <summary>停靠文档形态。调用方须在 UI 线程构造。</summary>
    public PluginPanel(string pluginId, IPluginLogger log, PanelOptions options, Control content, DockWorkspace workspace)
    {
        _pluginId = pluginId;
        _log = log;
        _workspace = workspace;
        _document = new(PanelId, options.Title, pluginId, content);
        // 用户关闭标签(CloseDocument)与程序撤除都会走 DocumentRemoved —— 单一挂点,
        // 面板生死与文档在树上的存在性严格一致。
        _onDocumentRemoved = removed =>
        {
            if (ReferenceEquals(removed, _document))
            {
                NotifyClosed();
            }
        };
        workspace.DocumentRemoved += _onDocumentRemoved;
        workspace.AddDocument(_document);
        ApplyPlacement(workspace, _document, options.Placement, options.PlacementRatio);
        WatchProportion();
    }

    /// <summary>
    /// 盯着这一栏的比例:用户拖完分割条,<c>DockWorkspaceControl</c> 会把 star 值回写成
    /// <see cref="DockNode.Proportion" />(每次拖动一次,见它的 <c>SaveProportions</c>),
    /// 我们据此换算成"占所在分栏的比例"抛给插件。
    /// </summary>
    /// <remarks>
    /// 订阅的是<b>落位时</b>那个组。用户把标签拖去别处会换一个组,那之后就不再有通知了 ——
    /// 换个组也就意味着"这一栏"已经不是原来那一栏,继续汇报反而是错的。
    /// </remarks>
    private void WatchProportion()
    {
        if (_workspace?.FindGroup(_document!) is not { } group)
        {
            return;
        }
        _watchedGroup = group;
        group.PropertyChanged += OnGroupPropertyChanged;
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DockNode.Proportion) || _watchedGroup is not { Parent: { } parent })
        {
            return;
        }
        double own = Weight(_watchedGroup);
        double total = 0;
        foreach (DockNode child in parent.Children)
        {
            total += Weight(child);
        }
        if (total <= 0)
        {
            return;
        }
        double ratio = own / total;
        PlacementRatio = ratio;
        PlacementRatioChanged?.Invoke(ratio);
    }

    /// <summary>NaN(还没被分配过比例)等价于"与兄弟均分",按 1 星算 —— 与控件侧同一约定。</summary>
    private static double Weight(DockNode node) => double.IsNaN(node.Proportion) ? 1 : Math.Max(node.Proportion, 0);

    /// <summary>
    /// 把刚加进主组的文档挪到请求的那一侧,并按 <c>ratio</c>(占标签区的比例,
    /// 见 <see cref="PanelOptions.PlacementRatio" />)定初始宽度。走的就是拖放停靠那条路径
    /// (<see cref="DockWorkspace.DockTo" />),所以结果与用户手动拖过去一模一样,
    /// 后续拖回、拆分、关闭都不需要任何特殊处理。
    /// </summary>
    private static void ApplyPlacement(DockWorkspace workspace, DockDocument document, PanelPlacement placement, double ratio)
    {
        if (placement == PanelPlacement.Tabs)
        {
            return;
        }
        DockPosition position = placement switch
        {
            PanelPlacement.Left => DockPosition.Left,
            PanelPlacement.Bottom => DockPosition.Bottom,
            PanelPlacement.Top => DockPosition.Top,
            _ => DockPosition.Right
        };
        // 锚定最外侧的那个组,而不是主组:主组左右已经有分栏时,贴主组会插到夹缝里,
        // 而"拖到右边"的语义是贴着整片标签区的外沿。
        bool trailing = position is DockPosition.Right or DockPosition.Bottom;
        DockGroup anchor = trailing ? workspace.AllGroups().Last() : workspace.AllGroups().First();
        workspace.DockTo(document, anchor, position);

        // 新拆出来的一栏默认与兄弟平分,对侧栏面板太宽了 —— 按请求的比例收一收
        if (workspace.FindGroup(document) is { } placed)
        {
            ApplyRatio(placed, anchor, ratio);
        }
    }

    /// <summary>
    /// 把"占几成"落成分栏里的 star 权重。分两种情形,取决于 <c>DockTo</c> 走了哪条路:
    /// <list type="bullet">
    /// <item>
    /// 新建分栏(本栏比例还是 NaN):兄弟各算多少星就加多少,解
    /// <c>w / (w + 兄弟总星) = r</c> 得 <c>w = 兄弟总星 · r / (1 - r)</c>。
    /// </item>
    /// <item>
    /// 插进已有分栏(<c>InsertNeighbor</c> 已把锚定组的份额对半匀给了两边):
    /// 保持这一对占的总份额不变,只把它按 <paramref name="ratio" /> 重新切,
    /// 免得动到分栏里其它兄弟的宽度。
    /// </item>
    /// </list>
    /// </summary>
    private static void ApplyRatio(DockGroup placed, DockGroup anchor, double ratio)
    {
        double clamped = double.IsFinite(ratio) ? Math.Clamp(ratio, MinSideRatio, MaxSideRatio) : DefaultSideRatio;
        if (double.IsNaN(placed.Proportion))
        {
            double siblings = 0;
            if (placed.Parent is { } parent)
            {
                foreach (DockNode child in parent.Children)
                {
                    if (!ReferenceEquals(child, placed))
                    {
                        siblings += double.IsNaN(child.Proportion) ? 1 : child.Proportion;
                    }
                }
            }
            placed.Proportion = Math.Max(siblings, 1) * clamped / (1 - clamped);
            return;
        }
        // 锚定组可能已因清空被折叠出树,那就只剩下本栏可调
        if (!ReferenceEquals(anchor.Parent, placed.Parent) || double.IsNaN(anchor.Proportion))
        {
            placed.Proportion = clamped / (1 - clamped);
            return;
        }
        double pair = placed.Proportion + anchor.Proportion;
        placed.Proportion = pair * clamped;
        anchor.Proportion = pair * (1 - clamped);
    }

    /// <summary>独立窗口形态(宿主同款自绘卡片窗口)。调用方须在 UI 线程构造。</summary>
    public PluginPanel(string pluginId, IPluginLogger log, PanelOptions options, Control content, Window? owner)
    {
        _pluginId = pluginId;
        _log = log;
        _window = new PluginPanelWindow
        {
            Width = Math.Max(options.WindowWidth, 280),
            Height = Math.Max(options.WindowHeight, 200)
        };
        _window.SetTitle(options.Title, pluginId);
        _window.SetTitleActions(options.TitleActions);
        _window.SetContent(content);
        _window.Closed += (_, _) => NotifyClosed();
        if (owner is not null)
        {
            _window.Show(owner);
        }
        else
        {
            _window.Show();
        }
    }

    /// <inheritdoc />
    public async Task ActivateAsync()
    {
        if (!IsOpen)
        {
            return;
        }
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_document is not null)
                {
                    _workspace?.ActivateDocument(_document);
                }
                _window?.Activate();
            }).GetTask().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 应用退出:Dispatcher 关掉了排队作业。置前这种事没做成也无所谓。
        }
    }

    public async Task CloseAsync()
    {
        if (!IsOpen)
        {
            return;
        }
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_document is not null && _workspace is not null)
                {
                    _workspace.RemoveDocument(_document); // 程序性撤除;DocumentRemoved 挂点统一触发 NotifyClosed
                }
                _window?.Close();
            }).GetTask().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 应用退出:Dispatcher 关闭会取消排队作业,窗口/文档随进程消亡,无需噪声。
            NotifyClosed();
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    /// <summary>面板生命终点(用户关闭/程序关闭/插件停用),幂等。</summary>
    private void NotifyClosed()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }
        if (_workspace is not null && _onDocumentRemoved is not null)
        {
            _workspace.DocumentRemoved -= _onDocumentRemoved;
        }
        if (_watchedGroup is not null)
        {
            _watchedGroup.PropertyChanged -= OnGroupPropertyChanged;
            _watchedGroup = null;
        }
        PlacementRatioChanged = null;
        if (Closed is { } handlers)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    handlers();
                }
                catch (Exception ex)
                {
                    _log.Error($"Panel Closed handler threw (panel of {_pluginId}).", ex);
                }
            });
        }
        Closed = null;
    }
}
