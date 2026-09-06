using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using FireAndForget = VelaShell.Services.FireAndForget;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

/// <summary>常规设置页:提供导入/导出设置与清除历史等操作。</summary>
public partial class GeneralSettingsPage : UserControl
{
    /// <summary>
    /// 导入文件的大小上限。设置 JSON 是几十 KB 量级的东西;超过这个数说明选错了文件
    /// (日志、数据库导出),整份读进内存再解析纯属白费,先挡下来。
    /// </summary>
    private const long MaxImportBytes = 4L * 1024 * 1024;

    /// <summary>初始化常规设置页并加载 XAML 组件。</summary>
    public GeneralSettingsPage() => InitializeComponent();

    private void ExportSettings_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Strings.Get("SetGeneral_ExportDialogTitle"),
            SuggestedFileName = "velashell-settings.json",
            SuggestedStartLocation = await StorageDefaults.DownloadsAsync(top),
            DefaultExtension = "json"
        });
        if (file?.TryGetLocalPath() is not { Length: > 0 } path)
        {
            return; // 用户取消了,不算失败,也不用回显。
        }
        try
        {
            await File.WriteAllTextAsync(path, viewModel.BuildExportJson());
            // 明说秘密没被导出:否则用户把文件搬到新机器上,会以为代理是配好的。
            viewModel.ImportExportStatus = Strings.Format("SetGeneral_ExportDone", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 只读目录、盘满、路径被占 —— 这些以前会静静地什么都不发生。
            viewModel.ImportExportStatus = Strings.Format("SetGeneral_ExportFailed", ex.Message);
        }
});

    private void ImportSettings_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("SetGeneral_ImportDialogTitle"),
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.DownloadsAsync(top)
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } path)
        {
            return; // 用户取消了。
        }
        string json;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                // 选完之后文件被删/被移走(网络盘、同步目录上并不罕见)。
                viewModel.ImportExportStatus = Strings.Format("SetGeneral_ImportFailed", path);
                return;
            }
            if (info.Length > MaxImportBytes)
            {
                viewModel.ImportExportStatus = Strings.Format(
                    "SetGeneral_ImportTooLarge",
                    (info.Length / (1024.0 * 1024)).ToString("F1"),
                    (MaxImportBytes / (1024.0 * 1024)).ToString("F0"));
                return;
            }
            json = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            viewModel.ImportExportStatus = Strings.Format("SetGeneral_ImportFailed", ex.Message);
            return;
        }

        // 返回值以前被整个丢掉:选了个不是设置的 JSON,界面毫无反应。
        viewModel.ImportExportStatus = viewModel.TryApplyImportedJson(json) switch
        {
            SettingsImportResult.Applied => Strings.Get("SetGeneral_ImportDone"),
            SettingsImportResult.AppliedNeedsSecrets => Strings.Get("SetGeneral_ImportDoneNeedsSecrets"),
            _ => Strings.Get("SetGeneral_ImportInvalid")
        };
});

    /// <summary>清除历史是破坏性操作:先确认再执行(设置审计 §12 破坏性操作需确认)。</summary>
    private void ClearHistory_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        bool confirmed = await MessageDialog.ConfirmAsync(owner, Strings.Get("SetGeneral_ClearHistory"), Strings.Get("SetGeneral_ClearHistoryConfirm"), danger: true);
        if (confirmed)
        {
            viewModel.ClearHistoryCommand.Execute().Subscribe();
        }
});
}
