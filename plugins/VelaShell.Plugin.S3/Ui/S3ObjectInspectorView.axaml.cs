using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.S3.Ui;

/// <summary>S3 对象检视器面板(窗口外壳由宿主面板提供)。</summary>
public sealed partial class S3ObjectInspectorView : UserControl
{
    /// <summary>用视图模型构造(由 <c>ShowPanelAsync</c> 的内容工厂在 UI 线程调用)。</summary>
    /// <param name="viewModel">视图模型。</param>
    /// <param name="loc">插件文案表(留作后续界面文案本地化)。</param>
    public S3ObjectInspectorView(S3ObjectInspectorViewModel viewModel, Loc loc)
    {
        _ = loc;
        InitializeComponent();
        DataContext = viewModel;
        _ = viewModel.InitializeAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
