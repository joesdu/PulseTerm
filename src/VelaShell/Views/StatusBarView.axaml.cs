using Avalonia.Controls;

namespace VelaShell.Views;

/// <summary>状态栏视图,展示当前连接状态与相关运行时信息。</summary>
public partial class StatusBarView : UserControl
{
    /// <summary>初始化 <see cref="StatusBarView"/> 并加载 XAML 组件。</summary>
    public StatusBarView()
    {
        InitializeComponent();
    }
}
