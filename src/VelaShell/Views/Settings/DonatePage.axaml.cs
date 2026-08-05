using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace VelaShell.Views.Settings;

/// <summary>捐赠页:展示 Wise 付款链接并提供打开/复制操作。</summary>
public partial class DonatePage : UserControl
{
    private const string WiseLink = "https://wise.com/pay/me/yud162";

    /// <summary>初始化捐赠页并加载 XAML 组件。</summary>
    public DonatePage()
    {
        InitializeComponent();
    }

    /// <summary>点击链接文本:在系统默认浏览器中打开 Wise 付款页。</summary>
    private async void WiseLink_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top && Uri.TryCreate(WiseLink, UriKind.Absolute, out Uri? uri))
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    }

    /// <summary>复制 Wise 付款链接,按钮文案短暂切为“已复制”作为反馈。</summary>
    private async void CopyWiseLink_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }
        await clipboard.SetTextAsync(WiseLink);
        if (sender is Button button && button.Content is string original && original != "已复制")
        {
            button.Content = "已复制";
            DispatcherTimer.RunOnce(() => button.Content = original, TimeSpan.FromSeconds(1.5));
        }
    }
}
