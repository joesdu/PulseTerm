using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;

namespace VelaShell.Views;

/// <summary>
/// 终端回写授权弹窗:展示插件、目标终端与内容预览,给出
/// 仅本次 / 本次运行 / 始终 / 拒绝 四种选择(蓝图 06 的最小授权流)。
/// </summary>
public partial class PluginPermissionDialog : Window
{
    /// <summary>设计器构造。</summary>
    public PluginPermissionDialog() => InitializeComponent();

    private PluginPermissionDialog(string pluginId, string sessionLabel, string inputPreview) : this()
    {
        TitleText.Text = Strings.Get("PluginPerm_Title");
        MessageText.Text = Strings.Format("PluginPerm_Message", pluginId, sessionLabel);
        PreviewText.Text = inputPreview;
        OnceButton.Content = Strings.Get("PluginPerm_AllowOnce");
        SessionButton.Content = Strings.Get("PluginPerm_AllowSession");
        AlwaysButton.Content = Strings.Get("PluginPerm_AllowAlways");
        DenyButton.Content = Strings.Get("PluginPerm_Deny");
    }

    /// <summary>在主窗口上模态弹出,返回用户裁决(取消/关闭 = 拒绝)。</summary>
    public static async Task<PluginPermissionDecision> AskAsync(string pluginId, string sessionLabel, string inputPreview)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => AskAsync(pluginId, sessionLabel, inputPreview));
        }
        if ((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow is not { } owner)
        {
            return PluginPermissionDecision.Deny;
        }
        var dialog = new PluginPermissionDialog(pluginId, sessionLabel, inputPreview);
        return await dialog.ShowDialog<PluginPermissionDecision>(owner);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
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
