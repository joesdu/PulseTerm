using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VelaShell.Views;

/// <summary>SSH 隧道帮助对话框,静态本地化内容,无 ViewModel。</summary>
public partial class TunnelHelpDialog : Window
{
    /// <summary>初始化可视化组件。</summary>
    public TunnelHelpDialog()
    {
        InitializeComponent();
    }

    // 推迟关闭:同步 Close 会让本轮点击/按键的后续路由打到已销毁的窗口刷
    // "PlatformImpl is null" 警告(见 WindowCloseExtensions)。
    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <summary>Esc 关闭对话框。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose();
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
