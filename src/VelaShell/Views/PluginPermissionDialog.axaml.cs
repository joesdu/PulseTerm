using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;

namespace VelaShell.Views;

/// <summary>
/// 插件敏感能力的授权弹窗:展示插件、目标与内容预览,给出
/// 仅本次 / 本次运行 / 始终 / 拒绝 四种选择(蓝图 06 的最小授权流)。
/// 两种请求共用这一个窗口:终端回写(预览 = 要敲进去的那行)与
/// 按已保存配置开会话(预览 = 插件给的理由)。
/// </summary>
public partial class PluginPermissionDialog : Window
{
    /// <summary>设计器构造。</summary>
    public PluginPermissionDialog() => InitializeComponent();

    private PluginPermissionDialog(string title, string message, string preview, string iconKey) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        PreviewText.Text = preview;
        if (Avalonia.Application.Current?.TryFindResource(iconKey, out object? icon) == true
            && icon is Avalonia.Media.Geometry geometry)
        {
            HeaderIcon.Data = geometry;
        }
        OnceButton.Content = Strings.Get("PluginPerm_AllowOnce");
        SessionButton.Content = Strings.Get("PluginPerm_AllowSession");
        AlwaysButton.Content = Strings.Get("PluginPerm_AllowAlways");
        DenyButton.Content = Strings.Get("PluginPerm_Deny");
    }

    /// <summary>终端回写:在主窗口上模态弹出,返回用户裁决(取消/关闭 = 拒绝)。</summary>
    public static Task<PluginPermissionDecision> AskAsync(string pluginId, string sessionLabel, string inputPreview)
        => ShowAsync(Strings.Get("PluginPerm_Title"),
            Strings.Format("PluginPerm_Message", pluginId, sessionLabel), inputPreview, "Icon.terminal");

    /// <summary>
    /// 按已保存配置开会话:同一个窗口,预览框里放插件给的<b>理由</b>。
    /// </summary>
    /// <remarks>
    /// 理由原样显示,一个字都不改写 —— 用户就是照着它决定点不点同意的。
    /// 插件写成"插件需要连接"这种废话,后果由它自己承担:确认框会照实显示,
    /// 而不是由宿主替它补一句像样的说明。
    /// </remarks>
    public static Task<PluginPermissionDecision> AskSessionOpenAsync(string pluginId, string target, string reason)
        => ShowAsync(Strings.Get("PluginPermSession_Title"),
            Strings.Format("PluginPermSession_Message", pluginId, target), reason, "Icon.plug");

    private static async Task<PluginPermissionDecision> ShowAsync(string title, string message, string preview,
        string iconKey)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(title, message, preview, iconKey));
        }
        if ((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow is not { } owner)
        {
            return PluginPermissionDecision.Deny;
        }
        var dialog = new PluginPermissionDialog(title, message, preview, iconKey);
        return await dialog.ShowDialog<PluginPermissionDecision>(owner);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    private void Once_Click(object? sender, RoutedEventArgs e) => Close(PluginPermissionDecision.AllowOnce);
    private void Session_Click(object? sender, RoutedEventArgs e) => Close(PluginPermissionDecision.AllowSession);
    private void Always_Click(object? sender, RoutedEventArgs e) => Close(PluginPermissionDecision.AllowAlways);
    private void Deny_Click(object? sender, RoutedEventArgs e) => Close(PluginPermissionDecision.Deny);

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(PluginPermissionDecision.Deny);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
