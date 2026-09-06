using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI.Primitives;
using VelaShell.Core.Import;
using VelaShell.Presentation.ViewModels;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views;

/// <summary>会话导入对话框:扫描来源(Xshell / WinSCP)、勾选预览并一键导入到 VelaShell。</summary>
public partial class SessionImportView : Window
{
    /// <summary>初始化导入对话框并接线打开时的扫描与命令回调。</summary>
    public SessionImportView()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not SessionImportViewModel viewModel)
        {
            return;
        }
        // 导入成功后关闭并把结果回传给宿主(用于刷新会话树)。
        viewModel.ImportCommand.Subscribe(outcome =>
        {
            if (outcome is not null)
            {
                this.PostClose(outcome);
            }
        });
        await viewModel.InitializeAsync();
});

    /// <summary>Esc 等价于取消;Enter 直接执行默认(全自动)导入。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose(null);
            e.Handled = true;
            return;
        }
        // CanExecute 为 false 时执行 ReactiveCommand 会把异常抛进订阅链,这里先问一句再执行。
        if (e.Key == Key.Enter && DataContext is SessionImportViewModel viewModel &&
            ((ICommand)viewModel.ImportCommand).CanExecute(null))
        {
            viewModel.ImportCommand.Execute().Subscribe();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>无系统标题栏 —— 按住头部拖动窗口。</summary>
    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => this.PostClose(null);

    /// <summary>为某一个来源手动指定配置文件/会话目录,并立即重扫该来源。</summary>
    private void Browse_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (sender is not Control { DataContext: SessionImportSourceViewModel source })
        {
            return;
        }
        string title = $"{source.SourceKey} · {source.SourceLabel}";
        string? picked = source.BrowseKind switch
        {
            ImportBrowseKind.File => await PickFileAsync(title),
            ImportBrowseKind.Folder => await PickFolderAsync(title),
            _ => null
        };
        if (picked is { Length: > 0 })
        {
            source.SourceText = picked;
            source.ScanCommand.Execute().Subscribe();
        }
});

    private async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.HomeAsync(this)
        });
        return folders.AsParallel().FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickFileAsync(string title)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StorageDefaults.HomeAsync(this)
        });
        return files.AsParallel().FirstOrDefault()?.TryGetLocalPath();
    }
}
