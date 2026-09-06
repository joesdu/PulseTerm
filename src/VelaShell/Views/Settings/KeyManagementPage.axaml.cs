using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using FireAndForget = VelaShell.Services.FireAndForget;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

/// <summary>密钥管理设置页:导入、复制与管理 SSH 密钥。</summary>
public partial class KeyManagementPage : UserControl
{
    /// <summary>初始化密钥管理设置页并加载 XAML 组件。</summary>
    public KeyManagementPage() => InitializeComponent();

    private void ImportKey_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
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
        if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } path)
        {
            await viewModel.SshKeys.ImportAsync(path);
        }
});

    /// <summary>
    /// 删除密钥前先确认,并把**将要删掉的实际文件**摆出来。
    /// </summary>
    /// <remarks>
    /// 这个按钮是列表行里一枚 24×24 的垃圾桶图标,挨着"复制公钥",误点非常容易;
    /// 而它删的是 <c>~/.ssh</c> 下真实的私钥和公钥 —— git、ansible、云控制台多半也在用
    /// 同一把,删掉不可撤销,也没有回收站。此前它直接绑命令,点下去当场就没了。
    /// </remarks>
    private void DeleteKey_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (sender is not Control { DataContext: SshKeyInfo key }
            || DataContext is not SettingsViewModel viewModel
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        bool confirmed = await MessageDialog.ConfirmAsync(
            owner,
            Strings.Get("SetKeys_DeleteKey"),
            Strings.Format("SetKeys_DeleteKeyConfirm", key.Name, key.PrivateKeyPath),
            kind: MessageDialogKind.Warning,
            danger: true);
        if (confirmed)
        {
            await viewModel.SshKeys.DeleteAsync(key);
        }
});

    private void CopyPublicKey_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (sender is Control { DataContext: SshKeyInfo key } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(key.PublicKeyLine ?? key.Fingerprint);
        }
});
}
