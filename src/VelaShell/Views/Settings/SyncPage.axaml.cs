using Avalonia.Controls;
using Avalonia.Input;

namespace VelaShell.Views.Settings;

/// <summary>同步设置页:配置配置数据的云端同步与相关指引。</summary>
public partial class SyncPage : UserControl
{
    /// <summary>初始化同步设置页并加载 XAML 组件。</summary>
    public SyncPage()
    {
        InitializeComponent();
    }

    /// <summary>指引卡片中的链接:Tag 即 URL,点击在系统默认浏览器打开。</summary>
    private async void OpenLink_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: string url } &&
            TopLevel.GetTopLevel(this) is { } top &&
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    }
}
