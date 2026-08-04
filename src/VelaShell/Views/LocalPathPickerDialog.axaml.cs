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
        : this(new LocalFilePaneViewModel(transferOptions), loadInitial: true) { }

    /// <summary>用现成的面板视图模型创建选择器(测试用:可指定起始目录、跳过初始加载)。</summary>
    internal LocalPathPickerDialog(LocalFilePaneViewModel viewModel, bool loadInitial)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.SelectedEntries.CollectionChanged += (_, _) => UpdateSelectionSummary();
        UpdateSelectionSummary();

        // 框选。这里的行不发起拖放,不存在"拖行 = 拖文件"的歧义,所以放开从行上起框 ——
        // 行是撑满整行宽的,只准从空白起框等于文件一多就用不上,正是最需要它的时候。
        _marquee = MarqueeSelection.Attach(
            FileList,
            Marquee,
            item => item is LocalFileEntry { IsParentEntry: false },
                _ => true
        );

        if (loadInitial)
        {
            _ = ViewModel.LoadInitialAsync();
        }
    }

    private LocalFilePaneViewModel ViewModel { get; }

    private readonly MarqueeSelection _marquee;

    /// <summary>关窗时务必收起框选,否则自动滚动的计时器会拽着已关闭的窗口继续跑。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _marquee.End();
        base.OnClosed(e);
    }

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
        ConfirmButton?.IsEnabled = picked.Count > 0;
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
