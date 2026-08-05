using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Views;

/// <summary>会话树视图:以分组树形式展示会话,支持展开折叠、双击连接与右键菜单。</summary>
public partial class SessionTreeView : UserControl
{
    private SessionTreeViewModel? _viewModel;

    /// <summary>初始化会话树视图并加载 XAML 组件。</summary>
    public SessionTreeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as SessionTreeViewModel;
        _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionTreeViewModel.SelectedNode))
        {
            Dispatcher.UIThread.Post(BringSelectedSessionIntoView, DispatcherPriority.Loaded);
        }
    }

    private void BringSelectedSessionIntoView()
    {
        if (_viewModel?.SelectedNode is not { } selected)
        {
            return;
        }
        Border? row = this.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border =>
                border.Classes.Contains("session") && ReferenceEquals(border.DataContext, selected)
            );
        row?.BringIntoView();
    }

    /// <summary>单击分组行即切换展开/折叠(设计 FrJPu:chevron 随之翻转)。</summary>
    private void Group_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: true } node })
        {
            node.IsExpanded = !node.IsExpanded;
        }
    }

    /// <summary>双击会话行直接连接(分组行仅展开/折叠)。</summary>
    private void Session_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (
            sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: false } node }
            && DataContext is SessionTreeViewModel viewModel
        )
        {
            viewModel.SelectedNode = node;
            viewModel.RequestConnect(node.Id);
        }
    }

    /// <summary>
    /// 右键弹菜单前先选中所指行:菜单里的命令都作用于 SelectedNode,不选中会
    /// 对着上一次选择的会话执行。
    /// </summary>
    private void Session_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            return;
        }
        if (
            sender is Control { DataContext: SessionTreeNodeViewModel { IsGroup: false } node }
            && DataContext is SessionTreeViewModel viewModel
        )
        {
            viewModel.SelectedNode = node;
        }
    }
}
