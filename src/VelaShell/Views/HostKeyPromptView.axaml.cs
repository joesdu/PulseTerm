using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using ReactiveUI.Primitives;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>主机密钥确认窗口,提示用户信任或拒绝服务器的主机密钥。</summary>
public partial class HostKeyPromptView : Window
{
    private HostKeyPromptViewModel? _viewModel;

    /// <summary>初始化主机密钥提示窗口,并订阅视图模型的结果以随决策关闭窗口。</summary>
    public HostKeyPromptView()
    {
        InitializeComponent();

        // VM 的信任/拒绝命令只落 Result;窗口无系统标题栏,由这里负责随 Result 关闭。
        DataContextChanged += (_, _) =>
        {
            _viewModel?.PropertyChanged -= OnViewModelPropertyChanged;

            _viewModel = DataContext as HostKeyPromptViewModel;
            _viewModel?.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostKeyPromptViewModel.Result) && _viewModel?.Result is { } decision)
        {
            // 信任/拒绝命令由按钮点击触发,仍在输入事件栈内:推迟关闭,避免后续路由
            // (PointerReleased/KeyUp)打到已销毁的窗口刷 "PlatformImpl is null" 警告。
            this.PostClose(decision);
        }
    }

    /// <summary>Esc 等价于点击拒绝:安全提示的取消必须落在保守分支(Reject),绝不隐式信任。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_viewModel is { } viewModel)
            {
                viewModel.CancelCommand.Execute().Subscribe();
            }
            else
            {
                this.PostClose();
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
