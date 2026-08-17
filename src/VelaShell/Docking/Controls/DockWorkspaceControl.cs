using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VelaShell.Docking.Model;

namespace VelaShell.Docking.Controls;

/// <summary>
/// 终端工作区控件:按 <see cref="DockWorkspace" /> 的布局树渲染 —— 分栏 = Grid
/// (star 尺寸 ↔ Proportion,分割条拖完回写比例),标签组 = <see cref="DockGroupControl" />。
/// 结构变化(拆分/折叠/根更换)整树重建;文档视图按文档缓存,重建只是重新收养,
/// 终端控件全程只构建一次(取代原 Dock.Controls.Recycling)。
/// </summary>
public sealed class DockWorkspaceControl : Panel
{
    /// <summary><see cref="Workspace" /> 的 Avalonia 样式属性,承载待渲染的停靠布局树。</summary>
    public static readonly StyledProperty<DockWorkspace?> WorkspaceProperty =
        AvaloniaProperty.Register<DockWorkspaceControl, DockWorkspace?>(nameof(Workspace));

    private readonly Dictionary<DockDocument, Control> _views = [];
    private readonly List<DockSplit> _observedSplits = [];
    private readonly List<DockGroupControl> _groupControls = [];

    /// <summary>
    /// 已渲染的分栏轨道:节点 → 它那条 Column/RowDefinition。
    /// 用来让<b>代码改比例</b>也能立刻反映到界面上 —— 比例是构建时读一次的,
    /// 而程序性布局(如插件面板落位)总是"先动树、再定比例",顺序注定晚一步,
    /// 没有这层订阅就只能看到均分的结果。
    /// </summary>
    private readonly List<(DockNode Node, DefinitionBase Definition, bool Horizontal)> _observedTracks = [];

    static DockWorkspaceControl()
    {
        WorkspaceProperty.Changed.AddClassHandler<DockWorkspaceControl>((control, e) => control.OnWorkspaceChanged(e));
    }

    /// <summary>构造工作区控件,并初始化其标签拖拽控制器。</summary>
    public DockWorkspaceControl()
    {
        DragController = new DockDragController(this);
    }

    /// <summary>当前渲染的停靠布局树;赋值后整树重建视图。</summary>
    public DockWorkspace? Workspace
    {
        get => GetValue(WorkspaceProperty);
        set => SetValue(WorkspaceProperty, value);
    }

    internal DockDragController DragController { get; }

    internal DockDropOverlay Overlay { get; } = new() { IsHitTestVisible = false, ZIndex = 100 };

    /// <summary>当前可视树里的标签组控件(拖拽命中测试用)。</summary>
    internal IReadOnlyList<DockGroupControl> GroupControls => _groupControls;

    private void OnWorkspaceChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is DockWorkspace oldWorkspace)
        {
            oldWorkspace.PropertyChanged -= OnWorkspaceModelChanged;
            oldWorkspace.DocumentRemoved -= OnDocumentRemoved;
            _views.Clear();
        }
        if (e.NewValue is DockWorkspace newWorkspace)
        {
            newWorkspace.PropertyChanged += OnWorkspaceModelChanged;
            newWorkspace.DocumentRemoved += OnDocumentRemoved;
        }
        Rebuild();
    }

    private void OnWorkspaceModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DockWorkspace.Root))
        {
            Rebuild();
        }
    }

    private void OnDocumentRemoved(DockDocument document) => _views.Remove(document);

    private void OnSplitChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    /// <summary>某个分栏子节点的比例被改了(代码改的;拖分割条走的是另一条路,见 SaveProportions)。</summary>
    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DockNode.Proportion) || sender is not DockNode node)
        {
            return;
        }
        foreach ((DockNode tracked, DefinitionBase definition, bool horizontal) in _observedTracks)
        {
            if (!ReferenceEquals(tracked, node))
            {
                continue;
            }
            var length = new GridLength(WeightOf(node), GridUnitType.Star);
            if (horizontal)
            {
                ((ColumnDefinition)definition).Width = length;
            }
            else
            {
                ((RowDefinition)definition).Height = length;
            }
            return;
        }
    }

    /// <summary>比例 → star 权重;NaN 表示与兄弟均分,下限防止一栏被压到看不见。</summary>
    private static double WeightOf(DockNode node)
        => double.IsNaN(node.Proportion) ? 1 : Math.Max(node.Proportion, 0.05);

    private void Rebuild()
    {
        foreach (DockSplit split in _observedSplits)
        {
            split.Children.CollectionChanged -= OnSplitChildrenChanged;
        }
        foreach ((DockNode node, _, _) in _observedTracks)
        {
            node.PropertyChanged -= OnNodeChanged;
        }
        _observedTracks.Clear();
        _observedSplits.Clear();
        _groupControls.Clear();
        // 先清空:旧的组控件在脱离可视树时释放对缓存视图的引用,新宿主才能收养。
        Children.Clear();
        if (Workspace is not { } workspace)
        {
            return;
        }
        Children.Add(BuildNode(workspace, workspace.Root));
        Children.Add(Overlay);
    }

    private Control BuildNode(DockWorkspace workspace, DockNode node) => node switch
    {
        DockGroup group => BuildGroup(workspace, group),
        DockSplit split => BuildSplit(workspace, split),
        _ => new Panel()
    };

    private DockGroupControl BuildGroup(DockWorkspace workspace, DockGroup group)
    {
        var control = new DockGroupControl { DataContext = group };
        control.Initialize(workspace, this, ViewFor);
        _groupControls.Add(control);
        return control;
    }

    private Grid BuildSplit(DockWorkspace workspace, DockSplit split)
    {
        split.Children.CollectionChanged += OnSplitChildrenChanged;
        _observedSplits.Add(split);

        var grid = new Grid();
        bool horizontal = split.Orientation == DockOrientation.Horizontal;
        var tracks = new List<(DefinitionBase Definition, DockNode Node)>();
        int trackIndex = 0;
        for (int i = 0; i < split.Children.Count; i++)
        {
            if (i > 0)
            {
                AddSplitterTrack(grid, horizontal, trackIndex, () => SaveProportions(tracks, horizontal));
                trackIndex++;
            }
            DockNode child = split.Children[i];
            double weight = WeightOf(child);
            if (horizontal)
            {
                var definition = new ColumnDefinition(weight, GridUnitType.Star) { MinWidth = 100 };
                grid.ColumnDefinitions.Add(definition);
                tracks.Add((definition, child));
                _observedTracks.Add((child, definition, true));
            }
            else
            {
                var definition = new RowDefinition(weight, GridUnitType.Star) { MinHeight = 60 };
                grid.RowDefinitions.Add(definition);
                tracks.Add((definition, child));
                _observedTracks.Add((child, definition, false));
            }
            child.PropertyChanged += OnNodeChanged;
            Control view = BuildNode(workspace, child);
            if (horizontal)
            {
                Grid.SetColumn(view, trackIndex);
            }
            else
            {
                Grid.SetRow(view, trackIndex);
            }
            grid.Children.Add(view);
            trackIndex++;
        }
        return grid;
    }

    /// <summary>
    /// 5px 分割条轨道:1px 主题线 + 透明 GridSplitter(与主窗口侧栏/文件面板分割条同款,
    /// 视觉轻、抓取区宽)。拖动结束把 star 值回写为各子节点的 Proportion。
    /// </summary>
    private static void AddSplitterTrack(Grid grid, bool horizontal, int trackIndex, Action onDragCompleted)
    {
        var line = new Border();
        line.Bind(Border.BackgroundProperty, line.GetResourceObservable("VelaBorderPrimary"));
        var splitter = new GridSplitter
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows
        };
        splitter.DragCompleted += (_, _) => onDragCompleted();
        if (horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
            line.Width = 1;
            line.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(line, trackIndex);
            Grid.SetColumn(splitter, trackIndex);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(5, GridUnitType.Pixel));
            line.Height = 1;
            line.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(line, trackIndex);
            Grid.SetRow(splitter, trackIndex);
        }
        grid.Children.Add(line);
        grid.Children.Add(splitter);
    }

    private static void SaveProportions(List<(DefinitionBase Definition, DockNode Node)> tracks, bool horizontal)
    {
        foreach ((DefinitionBase definition, DockNode node) in tracks)
        {
            GridLength length = horizontal
                                    ? ((ColumnDefinition)definition).Width
                                    : ((RowDefinition)definition).Height;
            if (length.IsStar)
            {
                node.Proportion = length.Value;
            }
        }
    }

    /// <summary>文档视图缓存:每个文档只构建一次,文档离开工作区时释放。</summary>
    internal Control? ViewFor(DockDocument document)
    {
        if (_views.TryGetValue(document, out Control? view))
        {
            return view;
        }
        if (document is IDockViewProvider provider)
        {
            view = provider.CreateView();
            _views[document] = view;
            return view;
        }
        return null;
    }
}
