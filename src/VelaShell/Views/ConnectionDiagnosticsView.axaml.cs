using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using FireAndForget = VelaShell.Services.FireAndForget;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>连接诊断窗口,打开时自动执行连通性检测并支持导出诊断报告。</summary>
public partial class ConnectionDiagnosticsView : Window
{
    /// <summary>初始化连接诊断窗口,并在打开时触发首轮诊断。</summary>
    public ConnectionDiagnosticsView()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    /// <summary>打开即自动执行一轮诊断;"重新检测"按钮可随时重跑。</summary>
    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is ConnectionDiagnosticsViewModel viewModel)
        {
            viewModel.RunCommand.Execute().Subscribe();
        }
    }

    /// <summary>无系统标题栏 —— 按住头部可拖动窗口。</summary>
    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    // 推迟关闭:同步 Close 会让本轮点击/按键的后续路由打到已销毁的窗口刷
    // "PlatformImpl is null" 警告(见 WindowCloseExtensions)。
    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <summary>Esc 关闭诊断窗口,与右上角关闭按钮同路径。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>导出诊断报告为文本文件(设计 RGXg1 exportDiag)。</summary>
    private void ExportReport_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not ConnectionDiagnosticsViewModel viewModel)
        {
            return;
        }
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Strings.Get("Diag_ExportReport"),
            SuggestedFileName = viewModel.SuggestedReportFileName,
            SuggestedStartLocation = await StorageDefaults.DownloadsAsync(this),
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new(Strings.Get("Main_FileTypeText")) { Patterns = ["*.txt"] }
            ]
        });
        string? path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(path, viewModel.BuildReportText());
        }
        catch (Exception ex)
        {
            await MessageDialog.ShowMessageAsync(this, Strings.Get("Main_ExportFailed"), ex.Message, MessageDialogKind.Error);
        }
});
}
