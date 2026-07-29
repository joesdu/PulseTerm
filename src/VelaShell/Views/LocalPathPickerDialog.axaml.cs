using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 上传用的本地路径选择器:文件与文件夹在同一个列表里,可混选、可多选。
/// <para>
/// 为什么不用系统对话框:Avalonia 的 <c>IStorageProvider</c> 只有
/// <c>OpenFilePickerAsync</c>(纯文件)与 <c>OpenFolderPickerAsync</c>(纯文件夹)两个入口,
/// Windows/Linux 的系统对话框本身也做不到一次同时选文件和文件夹 —— 上传因此长期被拆成
/// "上传文件""上传文件夹"两个菜单项。自绘一个就没这个限制,三个平台行为还一致。
/// </para>
/// </summary>
public partial class LocalPathPickerDialog : Window
{
    /// <summary>无参构造仅供 XAML 设计期使用。</summary>
    public LocalPathPickerDialog()
        : this(new()) { }

    /// <summary>用给定的传输设置(决定起始目录)创建选择器。</summary>
    public LocalPathPickerDialog(TransferOptions transferOptions)
    {
        InitializeComponent();
        ViewModel = new(transferOptions);
        DataContext = ViewModel;
        ViewModel.SelectedEntries.CollectionChanged += (_, _) => UpdateSelectionSummary();
        UpdateSelectionSummary();
        _ = ViewModel.LoadInitialAsync();
    }

    private LocalFilePaneViewModel ViewModel { get; }

    /// <summary>
    /// 打开选择器,返回用户选中的本地路径(文件与文件夹混合);取消则返回空列表。
    /// </summary>
    public static async Task<IReadOnlyList<string>> PickAsync(Window owner, TransferOptions transferOptions)
    {
        var dialog = new LocalPathPickerDialog(transferOptions);
        return await dialog.ShowDialog<IReadOnlyList<string>?>(owner) ?? [];
    }

    /// <summary>当前选中的真实条目(排除合成的 ".." 行)。</summary>
    private List<LocalFileEntry> PickedEntries() =>
        [.. ViewModel.SelectedEntries.Where(entry => !entry.IsParentEntry)];

    private void UpdateSelectionSummary()
    {
        if (SelectionSummary is null)
        {
            return;
        }
        List<LocalFileEntry> picked = PickedEntries();
        int folders = picked.Count(entry => entry.IsDirectory);
        SelectionSummary.Text = picked.Count == 0
            ? Strings.Get("Sftp_PickUploadHint")
            : Strings.Format("Sftp_PickUploadSelected", picked.Count - folders, folders);

        // 没选东西时不给按,免得开一个空批次。
        if (ConfirmButton is not null)
        {
            ConfirmButton.IsEnabled = picked.Count > 0;
        }
    }

    /// <summary>双击:目录(含 "..")进去,文件直接当作选定并确认 —— 单选是最常见的用法。</summary>
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: LocalFileEntry entry })
        {
            return;
        }
        if (entry.IsDirectory)
        {
            ViewModel.ActivateCommand.Execute(entry).Subscribe();
            return;
        }
        this.PostClose(new[] { entry.FullPath });
    }

    private void OnRootSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox
            && comboBox.SelectedItem is LocalRootEntry root
            && !ReferenceEquals(root, ViewModel.SelectedRoot))
        {
            if (!root.IsAccessible)
            {
                comboBox.SelectedItem = ViewModel.SelectedRoot;
                return;
            }
            ViewModel.SwitchRootCommand.Execute(root).Subscribe();
        }
    }

    // 推迟关闭:同步 Close 会让本轮点击/按键的后续路由打到已销毁的窗口刷
    // "PlatformImpl is null" 警告(见 WindowCloseExtensions)。
    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        List<LocalFileEntry> picked = PickedEntries();
        if (picked.Count > 0)
        {
            this.PostClose(picked.Select(entry => entry.FullPath).ToArray());
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => this.PostClose(null);

    /// <summary>Esc 取消。</summary>
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

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
