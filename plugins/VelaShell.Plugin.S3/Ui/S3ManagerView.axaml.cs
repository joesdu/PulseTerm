using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.S3.Ui;

/// <summary>
/// S3 桶管理器面板。窗口外壳由宿主的 <c>PanelDisplayMode.Window</c> 提供,
/// 这里只是内容控件 —— 插件面板本就该长得像宿主的一部分。
/// </summary>
public sealed partial class S3ManagerView : UserControl
{
    /// <summary>用视图模型构造(由 <c>ShowPanelAsync</c> 的内容工厂在 UI 线程调用)。</summary>
    /// <param name="viewModel">视图模型。</param>
    /// <param name="loc">插件文案表(留作后续界面文案本地化)。</param>
    public S3ManagerView(S3ManagerViewModel viewModel, Loc loc)
    {
        _ = loc;
        InitializeComponent();
        DataContext = viewModel;
        // 首屏加载放到构造之后:内容工厂跑在 UI 线程上,不能在里面 await。
        _ = viewModel.InitializeAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
