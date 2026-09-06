using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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

    /// <summary>键盘导航后把选中项滚入可视区。</summary>
    /// <remarks>
    /// <para>
    /// <b>必须走 <see cref="ItemsControl.ScrollIntoView(object)" />,不能自己去可视树里找容器。</b>
    /// 结果列表是<b>虚拟化</b>的(摊平成单个 ListBox 就是为了这个),可视区之外的条目
    /// 根本没有实例化 —— 旧写法按 <c>Classes="pal-item"</c> + DataContext 去找 Border,
    /// 找到的永远只是"已经看得见的那些",于是:向下走时靠虚拟化预留的那点缓冲一顿一顿地挪,
    /// 向上走则完全不动(上方的容器已被回收),选中态就这么走出了可视区。
    /// </para>
    /// <para>
    /// <c>ScrollIntoView</c> 按<b>下标</b>定位,不需要容器已经存在,虚拟化面板会自己滚过去
    /// 再实例化。
    /// </para>
    /// </remarks>
    private void ScrollSelectedIntoView()
    {
        // 本视图的 InitializeComponent 是手写的(AvaloniaXamlLoader.Load),不走代码生成,
        // 所以 x:Name 不会变成字段 —— 只能 FindControl(同文件里的 SearchBox 也是这么取的)。
        if (_vm?.SelectedItem is { } selected && this.FindControl<ListBox>("ResultsList") is { } list)
        {
            list.ScrollIntoView(selected);
        }
    }

    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is not null && sender is Control { DataContext: CommandPaletteItem item })
        {
            _vm.Activate(item);
        }
    }
}
