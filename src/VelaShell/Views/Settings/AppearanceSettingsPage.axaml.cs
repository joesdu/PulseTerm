using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

/// <summary>外观设置页:配置主题、字体、背景图片等界面外观相关选项。</summary>
public partial class AppearanceSettingsPage : UserControl
{
    /// <summary>初始化外观设置页并加载 XAML 组件。</summary>
    public AppearanceSettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>“浏览…”:选一张本地图片作为应用背景;写入 Appearance.BackgroundImagePath(触发即时预览与保存)。</summary>
    private async void PickBackgroundImage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("SetAppear_BackgroundImage"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"] }
            ]
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            vm.Appearance.BackgroundImagePath = path;
        }
    }

    /// <summary>“清除”:移除背景图,恢复纯色主题背景(路径置空即触发所有相关背景还原)。</summary>
    private void ClearBackgroundImage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.Appearance.BackgroundImagePath = "";
        }
    }
}
