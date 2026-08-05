using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Common;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Features.Settings.Pages;

/// <summary>传输设置分页视图,配置下载目录、外部编辑器等文件传输选项。</summary>
public partial class TransferSettingsPage : UserControl
{
    /// <summary>初始化 <see cref="TransferSettingsPage"/> 并加载 XAML 组件。</summary>
    public TransferSettingsPage()
    {
        InitializeComponent();
    }

    private async void BrowseDownloadDir_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = Strings.Get("SelectDownloadFolder"),
            AllowMultiple = false,
            // 起点用当前已配置的下载目录:改目录时从旧值出发比从头翻更顺手。
            SuggestedStartLocation = await StorageDefaults.FolderAsync(
                top,
                UserPathResolver.ResolveOrHome(DownloadDirBox.Text)
            ) ?? await StorageDefaults.HomeAsync(top)
        });
        if (folders.AsParallel().FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            // 直接写控件,由 TwoWay 绑定回写 POCO —— Transfer 引用不变时仅靠
            // RaisePropertyChanged 刷新,绑定可能因引用相同跳过重读(输入框不回显)。
            DownloadDirBox.Text = path;
            viewModel.RaisePropertyChangedForTransfer();
        }
    }

    private async void BrowseEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        var options = new FilePickerOpenOptions
        {
            Title = Strings.Get("SetTransfer_SelectEditorTitle"),
            AllowMultiple = false,
            // 挑的是编辑器可执行文件:已填过就从它所在目录出发,否则从程序安装目录出发。
            SuggestedStartLocation = await StorageDefaults.FolderAsync(top, Path.GetDirectoryName(EditorPathBox.Text))
                                     ?? await StorageDefaults.FolderAsync(
                                         top,
                                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                                     )
        };
        if (OperatingSystem.IsWindows())
        {
            options.FileTypeFilter =
            [
                new(Strings.Get("SetTransfer_ExecutableFilter")) { Patterns = ["*.exe", "*.bat", "*.cmd"] },
                FilePickerFileTypes.All
            ];
        }
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(options);
        if (files.AsParallel().FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            EditorPathBox.Text = path;
            viewModel.RaisePropertyChangedForTransfer();
        }
    }
}
