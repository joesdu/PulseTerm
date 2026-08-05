using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Core.Models;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Features.Sessions;

/// <summary>侧边栏视图:承载资源管理器、快捷命令、最近连接与底部设置入口。</summary>
public partial class SidebarView : UserControl
{
    private const double CollapsedHeight = 36;
    private const double MinimumExpandedHeight = 100;
    private const double MaximumRememberedHeight = 1200;
    private SidebarViewModel? _viewModel;

    /// <summary>创建侧边栏视图并加载其可视组件。</summary>
    public SidebarView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        QuickCommandsSplitter.DragCompleted += (_, _) => CaptureQuickCommandsHeight();
        RecentConnectionsSplitter.DragCompleted += (_, _) => CaptureRecentConnectionsHeight();
    }

    /// <summary>用户请求打开“新建连接”配置弹窗时触发(顶部新建按钮)。</summary>
    public event EventHandler? OpenConnectionProfileRequested;

    /// <summary>由底部栏齿轮按钮触发,用于打开设置窗口。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>用户双击最近连接以重新连接时触发。</summary>
    public event EventHandler<RecentConnectionEntry>? RecentConnectRequested;

    /// <summary>用户在资源管理器「更多」菜单中选择「从 Xshell 导入」时触发。</summary>
    public event EventHandler? ImportXshellRequested;

    /// <summary>用户在资源管理器「更多」菜单中选择「从 WinSCP 导入」时触发。</summary>
    public event EventHandler? ImportWinScpRequested;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as SidebarViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        ApplyQuickCommandsVisibility();
        ApplyRecentConnectionsState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is nameof(SidebarViewModel.IsQuickCommandsVisible)
                or nameof(SidebarViewModel.QuickCommandsExpanded)
                or nameof(SidebarViewModel.QuickCommandsHeight)
        )
        {
            ApplyQuickCommandsVisibility();
        }
        if (
            e.PropertyName
            is nameof(SidebarViewModel.RecentConnectionsExpanded)
                or nameof(SidebarViewModel.RecentConnectionsHeight)
        )
        {
            ApplyRecentConnectionsState();
        }
    }

    private void OpenConnectionProfile_Click(object? sender, RoutedEventArgs e)
    {
        OpenConnectionProfileRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ImportXshell_Click(object? sender, RoutedEventArgs e)
    {
        ImportXshellRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ImportWinScp_Click(object? sender, RoutedEventArgs e)
    {
        ImportWinScpRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleQuickCommands_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        if (_viewModel.QuickCommandsExpanded)
        {
            CaptureQuickCommandsHeight();
        }
        _viewModel.QuickCommandsExpanded = !_viewModel.QuickCommandsExpanded;
    }

    private void ApplyQuickCommandsVisibility()
    {
        bool visible = _viewModel is { IsQuickCommandsVisible: true, QuickCommands: not null };
        RowDefinition splitterRow = SessionAndQuickGrid.RowDefinitions[1];
        RowDefinition quickCommandsRow = SessionAndQuickGrid.RowDefinitions[2];
        if (
            !visible
            && QuickCommandsSection.IsVisible
            && _viewModel?.QuickCommandsExpanded == true
            && quickCommandsRow.ActualHeight > CollapsedHeight
        )
        {
            CaptureQuickCommandsHeight();
        }
        QuickCommandsSection.IsVisible = visible;
        if (!visible)
        {
            splitterRow.Height = new(0);
            quickCommandsRow.MinHeight = 0;
            quickCommandsRow.Height = new(0);
            QuickCommandsDivider.IsVisible = false;
            QuickCommandsSplitter.IsVisible = false;
            return;
        }

        bool expanded = _viewModel?.QuickCommandsExpanded == true;
        QuickCommandsContent.IsVisible = expanded;
        QuickCommandsExpandedIcon.IsVisible = expanded;
        QuickCommandsCollapsedIcon.IsVisible = !expanded;
        if (expanded)
        {
            splitterRow.Height = new(5);
            quickCommandsRow.MinHeight = MinimumExpandedHeight;
            quickCommandsRow.Height = new(
                NormalizeHeight(_viewModel?.QuickCommandsHeight ?? 160, SessionAndQuickGrid, 160)
            );
            QuickCommandsDivider.IsVisible = true;
            QuickCommandsSplitter.IsVisible = true;
        }
        else
        {
            splitterRow.Height = new(0);
            quickCommandsRow.MinHeight = CollapsedHeight;
            quickCommandsRow.Height = new(CollapsedHeight);
            QuickCommandsDivider.IsVisible = false;
            QuickCommandsSplitter.IsVisible = false;
        }
    }

    private void ToggleRecentConnections_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }
        if (_viewModel.RecentConnectionsExpanded)
        {
            CaptureRecentConnectionsHeight();
        }
        _viewModel.RecentConnectionsExpanded = !_viewModel.RecentConnectionsExpanded;
    }

    private void ApplyRecentConnectionsState()
    {
        bool expanded = _viewModel?.RecentConnectionsExpanded ?? true;
        RowDefinition recentRow = SidebarSectionsGrid.RowDefinitions[2];
        RecentConnectionsContent.IsVisible = expanded;
        RecentConnectionsExpandedIcon.IsVisible = expanded;
        RecentConnectionsCollapsedIcon.IsVisible = !expanded;
        RowDefinition splitterRow = SidebarSectionsGrid.RowDefinitions[1];
        if (expanded)
        {
            splitterRow.Height = new(5);
            recentRow.MinHeight = MinimumExpandedHeight;
            recentRow.Height = new(
                NormalizeHeight(
                    _viewModel?.RecentConnectionsHeight ?? 180,
                    SidebarSectionsGrid,
                    180
                )
            );
            RecentConnectionsDivider.IsVisible = true;
            RecentConnectionsSplitter.IsVisible = true;
        }
        else
        {
            splitterRow.Height = new(0);
            recentRow.MinHeight = CollapsedHeight;
            recentRow.Height = new(CollapsedHeight);
            RecentConnectionsDivider.IsVisible = false;
            RecentConnectionsSplitter.IsVisible = false;
        }
    }

    private void CaptureQuickCommandsHeight()
    {
        if (_viewModel is null)
        {
            return;
        }
        double height = SessionAndQuickGrid.RowDefinitions[2].ActualHeight;
        if (height > CollapsedHeight)
        {
            _viewModel.QuickCommandsHeight = NormalizeHeight(height, SessionAndQuickGrid, 160);
        }
    }

    private void CaptureRecentConnectionsHeight()
    {
        if (_viewModel is null)
        {
            return;
        }
        double height = SidebarSectionsGrid.RowDefinitions[2].ActualHeight;
        if (height > CollapsedHeight)
        {
            _viewModel.RecentConnectionsHeight = NormalizeHeight(height, SidebarSectionsGrid, 180);
        }
    }

    private static double NormalizeHeight(double height, Grid owner, double fallback)
    {
        double value =
            double.IsFinite(height) && height >= MinimumExpandedHeight ? height : fallback;
        double maximum = MaximumRememberedHeight;
        if (owner.Bounds.Height > MinimumExpandedHeight + 85)
        {
            maximum = Math.Min(maximum, Math.Max(MinimumExpandedHeight, owner.Bounds.Height - 85));
        }
        return Math.Clamp(value, MinimumExpandedHeight, maximum);
    }

    private void RecentConnection_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: RecentConnectionItemViewModel item })
        {
            RecentConnectRequested?.Invoke(this, item.Entry);
        }
    }
}
