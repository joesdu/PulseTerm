using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 上传用的本地路径选择器:文件与文件夹在同一个列表里,可混选、可多选。
/// <para>
/// 为什么不用系统对话框:Avalonia 的 <c>IStorageProvider</c> 只有
/// <c>OpenFilePickerAsync</c>(纯文件)与 <c>OpenFolderPickerAsync</c>(纯文件夹)两个入口,
/// Windows/Linux 的系统对话框本身也做不到一次同时选文件和文件夹 —— 上传因此长期被拆成
/// "上传文件""上传文件夹"两个菜单项。自绘一个就没这个限制,三个平台行为还一致。
/// </para>
/// </summary>
public partial class LocalPathPickerDialog : Window
{
    /// <summary>无参构造仅供 XAML 设计期使用。</summary>
    public LocalPathPickerDialog()
        : this(new()) { }

    /// <summary>用给定的传输设置(决定起始目录)创建选择器。</summary>
    public LocalPathPickerDialog(TransferOptions transferOptions)
        : this(new LocalFilePaneViewModel(transferOptions), loadInitial: true) { }

    /// <summary>用现成的面板视图模型创建选择器(测试用:可指定起始目录、跳过初始加载)。</summary>
    internal LocalPathPickerDialog(LocalFilePaneViewModel viewModel, bool loadInitial)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.SelectedEntries.CollectionChanged += (_, _) => UpdateSelectionSummary();
        UpdateSelectionSummary();

        // 框选:必须 handledEventsToo。ListBoxItem 会把 PointerPressed 标记为已处理
        // (它要用这一下做选中),不带这个标志就永远收不到按下,框选也就无从起头。
        FileList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Bubble, true);
        FileList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Bubble, true);
        FileList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Bubble, true);

        // 捕获被系统抢走(切窗口、拖出窗体等)时也要收摊,否则矩形留在屏幕上、
        // 自动滚动的计时器还在空转。
        FileList.PointerCaptureLost += (_, _) => EndMarquee();

        if (loadInitial)
        {
            _ = ViewModel.LoadInitialAsync();
        }
    }

    private LocalFilePaneViewModel ViewModel { get; }

    /// <summary>
    /// 打开选择器,返回用户选中的本地路径(文件与文件夹混合);取消则返回空列表。
    /// </summary>
    public static async Task<IReadOnlyList<string>> PickAsync(Window owner, TransferOptions transferOptions)
    {
        var dialog = new LocalPathPickerDialog(transferOptions);
        return await dialog.ShowDialog<IReadOnlyList<string>?>(owner) ?? [];
    }

    /// <summary>当前选中的真实条目(排除合成的 ".." 行)。</summary>
    private List<LocalFileEntry> PickedEntries() =>
        [.. ViewModel.SelectedEntries.Where(entry => !entry.IsParentEntry)];

    private void UpdateSelectionSummary()
    {
        if (SelectionSummary is null)
        {
            return;
        }
        List<LocalFileEntry> picked = PickedEntries();
        int folders = picked.Count(entry => entry.IsDirectory);
        SelectionSummary.Text = picked.Count == 0
            ? Strings.Get("Sftp_PickUploadHint")
            : Strings.Format("Sftp_PickUploadSelected", picked.Count - folders, folders);

        // 没选东西时不给按,免得开一个空批次。
        if (ConfirmButton is not null)
        {
            ConfirmButton.IsEnabled = picked.Count > 0;
        }
    }

    /// <summary>双击:目录(含 "..")进去,文件直接当作选定并确认 —— 单选是最常见的用法。</summary>
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: LocalFileEntry entry })
        {
            return;
        }
        if (entry.IsDirectory)
        {
            ViewModel.ActivateCommand.Execute(entry).Subscribe();
            return;
        }
        this.PostClose(new[] { entry.FullPath });
    }

    private void OnRootSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox
            && comboBox.SelectedItem is LocalRootEntry root
            && !ReferenceEquals(root, ViewModel.SelectedRoot))
        {
            if (!root.IsAccessible)
            {
                comboBox.SelectedItem = ViewModel.SelectedRoot;
                return;
            }
            ViewModel.SwitchRootCommand.Execute(root).Subscribe();
        }
    }

    // 推迟关闭:同步 Close 会让本轮点击/按键的后续路由打到已销毁的窗口刷
    // "PlatformImpl is null" 警告(见 WindowCloseExtensions)。
    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        List<LocalFileEntry> picked = PickedEntries();
        if (picked.Count > 0)
        {
            this.PostClose(picked.Select(entry => entry.FullPath).ToArray());
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => this.PostClose(null);

    /// <summary>Esc 取消。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose(null);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // ── 框选 ─────────────────────────────────────────────────────────
    //
    // 像资源管理器那样按住左键拖出一个矩形,划过的行成片选中。列表里的行是撑满整行宽的,
    // 空白区只在最后一行下面才有 —— 只允许"从空白处起框"等于文件一多就用不上,正是最需要它的时候。
    // 所以这里也允许从行上起框:按下先照常选中那一行,指针移动超过阈值才升级成框选。
    // 这个对话框里的行不发起拖放,不存在"拖行 = 拖文件"的歧义,可以这么做。

    /// <summary>升级成框选所需的拖动距离,低于此距离仍按普通点击处理。</summary>
    private const double MarqueeThreshold = 4;

    /// <summary>行高兜底值:与 XAML 行模板里的 Height 一致,取不到实测值时用它。</summary>
    private const double FallbackRowHeight = 28;

    private bool _pointerDown;
    private bool _marqueeActive;

    /// <summary>按下点(视口坐标)。</summary>
    private Point _marqueeOrigin;

    /// <summary>按下时的滚动偏移:自动滚动会改变偏移,起点必须锚在内容坐标上。</summary>
    private double _marqueeOriginOffset;

    /// <summary>按住 Ctrl 起框时保留的原有选中项。</summary>
    private readonly List<LocalFileEntry> _marqueeBaseSelection = [];

    /// <summary>拖到列表上下边界外时的自动滚动。</summary>
    private DispatcherTimer? _autoScroll;
    private double _autoScrollDelta;

    /// <summary>
    /// 自动滚动期间的当前指针位置。必须放在字段里让计时器每次读:
    /// 若把它闭包进计时器,指针继续移动时矩形就会卡在起框那一刻的位置不再跟手。
    /// </summary>
    private Point _autoScrollPointer;

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _pointerDown = true;
        _marqueeOrigin = e.GetPosition(FileList);
        _marqueeOriginOffset = ScrollOffsetY;
        _marqueeBaseSelection.Clear();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _marqueeBaseSelection.AddRange(ViewModel.SelectedEntries);
        }
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }
        Point current = e.GetPosition(FileList);
        if (!_marqueeActive)
        {
            if (Math.Abs(current.X - _marqueeOrigin.X) < MarqueeThreshold
                && Math.Abs(current.Y - _marqueeOrigin.Y) < MarqueeThreshold)
            {
                return;
            }
            _marqueeActive = true;
            e.Pointer.Capture(FileList);
            Marquee.IsVisible = true;
        }
        UpdateMarquee(current);

        // 拖出上下边界就持续滚动,否则一屏之外的行永远框不到。
        double overshoot = current.Y < 0
            ? current.Y
            : current.Y > FileList.Bounds.Height ? current.Y - FileList.Bounds.Height : 0;
        SetAutoScroll(overshoot, current);
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pointerDown = false;
        if (!_marqueeActive)
        {
            return;
        }
        EndMarquee();
        e.Pointer.Capture(null);

        // 框选拖动不该被当成一次点击落到行上(否则松手瞬间选中集会被那一行顶掉)。
        e.Handled = true;
    }

    /// <summary>收起框选:停自动滚动、藏掉矩形。重复调用无害。</summary>
    private void EndMarquee()
    {
        _pointerDown = false;
        _marqueeActive = false;
        StopAutoScroll();
        if (Marquee is not null)
        {
            Marquee.IsVisible = false;
        }
    }

    /// <summary>画出矩形,并把它覆盖到的行同步进选中集。</summary>
    private void UpdateMarquee(Point current)
    {
        double originY = _marqueeOrigin.Y + _marqueeOriginOffset - ScrollOffsetY;
        double left = Math.Max(Math.Min(_marqueeOrigin.X, current.X), 0);
        double top = Math.Max(Math.Min(originY, current.Y), 0);
        double right = Math.Min(Math.Max(_marqueeOrigin.X, current.X), FileList.Bounds.Width);
        double bottom = Math.Min(Math.Max(originY, current.Y), FileList.Bounds.Height);
        Marquee.Margin = new(left, top, 0, 0);
        Marquee.Width = Math.Max(right - left, 0);
        Marquee.Height = Math.Max(bottom - top, 0);

        // 命中判定走内容坐标(视口坐标 + 滚动偏移),这样滚动过程中已划过的行不会掉出来。
        (int first, int last) = MarqueeSelectionMath.RowsInBand(
            _marqueeOrigin.Y + _marqueeOriginOffset,
            current.Y + ScrollOffsetY,
            RowHeight(),
            ViewModel.Entries.Count
        );
        ApplyMarqueeSelection(first, last);
    }

    private void ApplyMarqueeSelection(int first, int last)
    {
        var target = new HashSet<LocalFileEntry>(_marqueeBaseSelection);
        for (int i = first; i >= 0 && i <= last; i++)
        {
            LocalFileEntry entry = ViewModel.Entries[i];
            if (!entry.IsParentEntry)
            {
                target.Add(entry);
            }
        }

        // 逐项增删而不是清空重建:清空会让绑定过去的选中集合抖动一次,
        // 列表跟着重画,拖动过程中肉眼可见地闪。
        for (int i = ViewModel.SelectedEntries.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(ViewModel.SelectedEntries[i]))
            {
                ViewModel.SelectedEntries.RemoveAt(i);
            }
        }
        foreach (LocalFileEntry entry in target)
        {
            if (!ViewModel.SelectedEntries.Contains(entry))
            {
                ViewModel.SelectedEntries.Add(entry);
            }
        }
    }

    /// <summary>实测行高(取第一个已实现的容器),取不到时用模板里的固定值。</summary>
    private double RowHeight()
    {
        foreach (Control container in FileList.GetRealizedContainers())
        {
            if (container.Bounds.Height > 0)
            {
                return container.Bounds.Height;
            }
        }
        return FallbackRowHeight;
    }

    private double ScrollOffsetY => FileList.Scroll?.Offset.Y ?? 0;

    private void SetAutoScroll(double overshoot, Point current)
    {
        _autoScrollPointer = current;
        if (overshoot == 0)
        {
            StopAutoScroll();
            return;
        }

        // 越界越多滚得越快,但压在一行的量级上,免得一冲到底。
        _autoScrollDelta = Math.Clamp(overshoot / 2, -RowHeight(), RowHeight());
        if (_autoScroll is not null)
        {
            return;
        }
        _autoScroll = new(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (_, _) =>
        {
            if (FileList.Scroll is not { } scroll)
            {
                return;
            }
            double y = Math.Clamp(
                scroll.Offset.Y + _autoScrollDelta,
                0,
                Math.Max(scroll.Extent.Height - scroll.Viewport.Height, 0)
            );
            scroll.Offset = scroll.Offset.WithY(y);
            UpdateMarquee(_autoScrollPointer);
        });
        _autoScroll.Start();
    }

    private void StopAutoScroll()
    {
        _autoScroll?.Stop();
        _autoScroll = null;
    }

    /// <summary>关窗时务必停掉自动滚动的计时器,否则它会拽着已关闭的窗口继续跑。</summary>
    protected override void OnClosed(EventArgs e)
    {
        EndMarquee();
        base.OnClosed(e);
    }
}
