using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ReactiveUI.Primitives;
using VelaShell.Core.Import;
using VelaShell.Core.Resources;
using VelaShell.Presentation.ViewModels;

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

    private async void OnOpened(object? sender, EventArgs e)
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
    }

    /// <summary>Esc 等价于取消。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose(null);
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
            BeginMoveDrag(e);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => this.PostClose(null);

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionImportViewModel viewModel)
        {
            return;
        }
        string? picked = viewModel.BrowseKind switch
        {
            ImportBrowseKind.File => await PickFileAsync(),
            ImportBrowseKind.Folder => await PickFolderAsync(),
            _ => null
        };
        if (picked is { Length: > 0 })
        {
            viewModel.SourceText = picked;
            viewModel.ScanCommand.Execute().Subscribe();
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new()
        {
            Title = Strings.Get("XImport_Browse"),
            AllowMultiple = false
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickFileAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Strings.Get("XImport_Browse"),
            AllowMultiple = false
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
