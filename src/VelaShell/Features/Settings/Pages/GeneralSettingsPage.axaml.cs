using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI.Primitives;
using VelaShell.Common;
using VelaShell.Core.Resources;

namespace VelaShell.Features.Settings.Pages;

/// <summary>常规设置页:提供导入/导出设置与清除历史等操作。</summary>
public partial class GeneralSettingsPage : UserControl
{
    /// <summary>初始化常规设置页并加载 XAML 组件。</summary>
    public GeneralSettingsPage()
    {
        InitializeComponent();
    }

    private async void ExportSettings_Click(object? sender, RoutedEventArgs e)
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
        if (file?.TryGetLocalPath() is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, viewModel.BuildExportJson());
        }
    }

    private async void ImportSettings_Click(object? sender, RoutedEventArgs e)
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
        if (files.AsParallel().FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path && File.Exists(path))
        {
            viewModel.TryApplyImportedJson(await File.ReadAllTextAsync(path));
        }
    }

    /// <summary>清除历史是破坏性操作:先确认再执行(设置审计 §12 破坏性操作需确认)。</summary>
    private async void ClearHistory_Click(object? sender, RoutedEventArgs e)
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
    }
}
