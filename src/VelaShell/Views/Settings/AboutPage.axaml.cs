using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VelaShell.Views.Settings;

/// <summary>关于页:展示版本、依赖项目及其许可证链接。</summary>
public partial class AboutPage : UserControl
{
    /// <summary>初始化关于页并加载 XAML 组件。</summary>
    public AboutPage()
    {
        InitializeComponent();
    }

    /// <summary>点击依赖项目名/许可证时,用系统默认浏览器打开对应链接(URL 存放在控件 Tag)。</summary>
    private async void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url } || string.IsNullOrWhiteSpace(url))
        {
            return;
        }
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    }
}
