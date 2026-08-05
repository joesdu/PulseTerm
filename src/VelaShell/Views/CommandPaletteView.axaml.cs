using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 命令面板视图:承载搜索框与结果列表,并通过隧道路由拦截方向键/回车/Esc,
/// 在搜索框消费键盘事件前完成上下导航、执行与关闭。
/// </summary>
public partial class CommandPaletteView : UserControl
{
    private CommandPaletteViewModel? _vm;

    /// <summary>初始化命令面板视图,注册键盘隧道处理器与数据上下文变更监听。</summary>
    public CommandPaletteView()
    {
        InitializeComponent();
        // 用隧道(tunnel)拦截,使方向键/回车/Esc 在搜索 TextBox 消费这些按键之前被截获。
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vm?.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as CommandPaletteViewModel;
        _vm?.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && _vm?.IsOpen == true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                TextBox? box = this.FindControl<TextBox>("SearchBox");
                box?.Focus();
                box?.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Down:
                _vm.MoveDown();
                ScrollSelectedIntoView();
                e.Handled = true;
                break;
            case Key.Up:
                _vm.MoveUp();
                ScrollSelectedIntoView();
                e.Handled = true;
                break;
            case Key.Enter:
                _vm.ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 键盘导航后把选中项滚入可视区:结果区是嵌套 ItemsControl(非 ListBox),
    /// 没有内建的选中跟随滚动,超出可视范围后选中项会不可见,需手动 BringIntoView。
    /// </summary>
    private void ScrollSelectedIntoView()
    {
        if (_vm?.SelectedItem is not { } selected)
        {
            return;
        }

        // 面板未虚拟化(StackPanel 容器),条目已全部实例化,按 DataContext 定位容器即可。
        Border? container = this.GetVisualDescendants()
                                .OfType<Border>()
                                .FirstOrDefault(b => b.Classes.Contains("pal-item") && ReferenceEquals(b.DataContext, selected));
        container?.BringIntoView();
    }

    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is not null && sender is Control { DataContext: CommandPaletteItem item })
        {
            _vm.Activate(item);
        }
    }
}
