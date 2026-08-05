using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Common;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.Features.Settings.Pages;

/// <summary>密钥管理设置页:导入、复制与管理 SSH 密钥。</summary>
public partial class KeyManagementPage : UserControl
{
    /// <summary>初始化密钥管理设置页并加载 XAML 组件。</summary>
    public KeyManagementPage()
    {
        InitializeComponent();
    }

    private async void ImportKey_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("SetKeys_ImportDialogTitle"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.SshAsync(top)
        });
        if (files.AsParallel().FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            await viewModel.SshKeys.ImportAsync(path);
        }
    }

    private async void CopyPublicKey_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SshKeyInfo key } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(key.PublicKeyLine ?? key.Fingerprint);
        }
    }
}
