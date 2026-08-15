using Avalonia.Controls;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 插件自己的对话框外壳:标题 + 内容 + 一个关闭按钮,可选地在标题行右侧挂几个动作按钮。
/// 设置页与"配置工具"共用它,免得两处各写一遍窗体骨架。
/// </summary>
/// <remarks>
/// 用系统标题栏而不是宿主那套自绘窗体:自绘是宿主的 <c>PluginPanelWindow</c> 的事,
/// 插件够不着它的 ControlTheme;内容区的配色全走 <c>Vela*</c> 令牌,明暗仍旧跟着宿主。
/// </remarks>
public partial class PluginDialog : Window
{
    /// <summary>XAML 装载器要的无参构造(设计时/热重载);运行时走带参那个。</summary>
    public PluginDialog() => InitializeComponent();

    /// <param name="title">标题栏与窗体标题。</param>
    /// <param name="content">内容控件。</param>
    /// <param name="closeText">关闭按钮的文案(通常是"关闭"或"确定")。</param>
    public PluginDialog(string title, Control content, string closeText) : this()
    {
        Title = title;
        TitleText.Text = title;
        Body.Content = content;
        CloseButton.Content = closeText;
        CloseButton.Click += (_, _) => Close();
    }

    /// <summary>在标题行右侧加一个动作按钮(如"更新工具库")。</summary>
    public void AddHeaderAction(Control control) => HeaderActions.Children.Add(control);

    /// <summary>
    /// 把内容控件从窗口上摘下来。调用方想复用同一个视图(它持有编辑中的状态)时必须先摘 ——
    /// 一个控件只能有一个父级,不摘下来下次挂不上去。
    /// </summary>
    public void DetachBody() => Body.Content = null;
}
