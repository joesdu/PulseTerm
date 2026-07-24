using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using VelaShell.Controls.Controls;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>远程文件浏览器视图:文件列表、可拖拽列、拖放上传,以及操作系统选择器/对话框集成。</summary>
public partial class FileBrowserView : UserControl
{
    private FileBrowserViewModel? _viewModel;

    /// <summary>
    /// 可拖拽列的列键,顺序同表头。末列“修改时间”吃 * 宽度、右侧没有拖拽条,故不在此列。
    /// 每根拖拽条调整的是它左边那一列(Tag 即该列的列键)。
    /// </summary>
    private static readonly string[] ResizableColumns =
    [
        "name",
        "size",
        "permissions",
        "owner",
        "group",
        "type",
    ];

    /// <summary>单根拖拽条的宽度(同 axaml 的拖拽条列定义)。</summary>
    private const double SplitterWidth = 6;

    /// <summary>表头/行的左右内边距合计(axaml 里 Padding="14,0")。</summary>
    private const double HorizontalPadding = 28;

    // 必须与 axaml 里渲染用的 VelaTerminalFont 令牌(内置 Cascadia Mono 打头)一致:
    // 列宽是用这个字体度量的,若度量字体与实际渲染字体不同(旧值以 JetBrains Mono 打头,
    // 且不含内置 Cascadia),字形步进不一致会导致列自适应/截断错位。
    private static readonly FontFamily TerminalFont = new("fonts:VelaShell#Cascadia Mono, JetBrains Mono, Consolas, monospace");
    private static readonly Typeface TerminalTypeface = new(TerminalFont);
    private string? _activeSplitter;
    private double _dragStartX;

    // 跨面板拖拽发起状态(SFTP 双栏模式)。
    private bool _isDragging;
    private Point _dragOrigin;
    private RemoteFileInfoViewModel? _dragRow;
    private PointerPressedEventArgs? _dragPointerArgs;
    private const double DragThreshold = 5;

    /// <summary>按下拖拽条那一刻的列宽快照(列键 → 像素),拖拽期间按位移量增量应用。</summary>
    private readonly Dictionary<string, double> _startWidths = [];

    /// <summary>初始化视图,为视图模型提供操作系统选择器/对话框,并挂接列拖拽条与拖放事件处理。</summary>
    public FileBrowserView()
    {
        InitializeComponent();

        // VM 无法访问 Avalonia 的存储 API;由视图提供操作系统的选择器。
        DataContextChanged += (_, _) =>
        {
            _viewModel?.DirectoryChanged -= OnDirectoryChanged;
            _viewModel = DataContext as FileBrowserViewModel;
            if (_viewModel is not { } vm)
            {
                return;
            }
            vm.DirectoryChanged += OnDirectoryChanged;
            vm.PickFilesForUpload = PickFilesAsync;
            vm.PickFolderForUpload = PickFolderAsync;
            vm.PickSavePathForDownload = PickSavePathAsync;
            vm.PickFolderForDownload = PickDownloadFolderAsync;
            vm.PromptForText = PromptForTextAsync;
            vm.CopyToClipboard = CopyToClipboardAsync;
            vm.ShowFileProperties = ShowFilePropertiesAsync;
            vm.ConfirmDelete = ConfirmAsync;
            vm.OpenLocalFile = OpenLocalFileAsync;
            vm.OpenInBuiltInEditor = OpenInBuiltInEditorAsync;
            vm.PromptConfigureEditor = PromptConfigureEditorAsync;
            vm.ConfirmOverwrite = ConfirmOverwriteAsync;
            vm.ConfirmRemoteOverwrite = ConfirmRemoteOverwriteAsync;
        };

        // 拖放接收:操作系统文件拖入(始终启用)+ 跨面板拖入。
        if (this.FindControl<ListBox>("FileList") is { } fileList)
        {
            DragDrop.SetAllowDrop(fileList, true);
            fileList.AddHandler(DragDrop.DragOverEvent, OnFileListDragOver);
            fileList.AddHandler(DragDrop.DropEvent, OnFileListDrop);

            // 跨面板拖拽发起(行 → 本地面板)。
            fileList.AddHandler(PointerMovedEvent, OnRemoteDragPointerMoved);
            fileList.AddHandler(PointerReleasedEvent, OnRemoteDragPointerReleased);
        }
        AddHandler(PointerMovedEvent, OnColumnSplitterPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnColumnSplitterReleased, RoutingStrategies.Tunnel);
    }

    private void OnDirectoryChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                ScrollViewer? scrollViewer = FileList
                    .GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .FirstOrDefault();
                scrollViewer?.Offset = new(0, 0);
            },
            DispatcherPriority.Loaded
        );
    }

    private void OnColumnSplitterPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm || sender is not Control { Tag: string tag })
        {
            return;
        }

        // 这里用 ClickCount,这样即使指针处理/捕获激活时,自适应宽度仍能工作。
        if (e.ClickCount >= 2)
        {
            AutoFitColumnBySplitterTag(vm, tag);
            e.Handled = true;
            return;
        }
        _activeSplitter = tag;
        _dragStartX = e.GetPosition(this).X;
        _startWidths.Clear();
        foreach (string column in ResizableColumns)
        {
            _startWidths[column] = vm.GetColumnWidth(column).Value;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnColumnSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (
            _activeSplitter is null
            || DataContext is not FileBrowserViewModel vm
            || !_startWidths.TryGetValue(_activeSplitter, out double startWidth)
        )
        {
            return;
        }
        double delta = e.GetPosition(this).X - _dragStartX;
        vm.SetColumnWidth(
            _activeSplitter,
            ClampColumnWidth(
                startWidth + delta,
                FileBrowserViewModel.MinWidthFor(_activeSplitter),
                GetMaxWidth(vm, _activeSplitter)
            )
        );
        e.Handled = true;
    }

    private void OnColumnSplitterReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_activeSplitter is null)
        {
            return;
        }
        _activeSplitter = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void AutoFitColumnBySplitterTag(FileBrowserViewModel vm, string tag)
    {
        vm.SetColumnWidth(
            tag,
            ClampColumnWidth(
                EstimateAutoWidth(vm, tag),
                FileBrowserViewModel.MinWidthFor(tag),
                GetMaxWidth(vm, tag)
            )
        );
    }

    /// <summary>
    /// 双击拖拽条的自适应宽度:取表头与所有行里最宽的那条文本。“文件名”列还要
    /// 额外让出前导图标区(14px 图标 + 6px 间距)。上限防止一个超长名字把列撑爆。
    /// </summary>
    private static double EstimateAutoWidth(FileBrowserViewModel vm, string columnKey)
    {
        (
            string Header,
            Func<RemoteFileInfoViewModel, string> Cell,
            double Padding,
            double Max
        ) spec = columnKey switch
        {
            "size" => (Strings.Size, f => f.FormattedSize, 8d, 260d),
            "permissions" => (Strings.Permissions, f => f.Permissions, 8d, 300d),
            "owner" => (Strings.PermissionOwner, f => f.Owner, 8d, 300d),
            "group" => (Strings.PermissionGroup, f => f.Group, 8d, 300d),
            "type" => (Strings.FileType, f => f.FileTypeDisplay, 8d, 300d),
            _ => (Strings.FileName, f => f.DisplayName, 34d, 760d),
        };
        double headerWidth = MeasureTextWidth(spec.Header, 10);
        double rowsWidth = vm.Files.Any()
            ? vm.Files.Max(f => MeasureTextWidth(spec.Cell(f), 11))
            : 0;
        return Math.Clamp(
            Math.Max(headerWidth, rowsWidth) + spec.Padding,
            FileBrowserViewModel.MinWidthFor(columnKey),
            spec.Max
        );
    }

    /// <summary>
    /// 一列能撑到的最大宽度:面板宽度扣掉其余各列、各拖拽条(隐藏列两者都不占位)、
    /// 末列“修改时间”的下限,以及左右内边距。据此拖拽不会把末列挤没。
    /// </summary>
    private double GetMaxWidth(FileBrowserViewModel vm, string columnKey)
    {
        string[] visible = [.. ResizableColumns.Where(vm.IsColumnVisible)];
        double others = visible.Where(c => c != columnKey).Sum(c => vm.GetColumnWidth(c).Value);
        double splitters = visible.Length * SplitterWidth;
        double reserved = vm.IsColumnVisible("modified")
            ? FileBrowserViewModel.MinModifiedWidth
            : 0;
        return Math.Max(
            FileBrowserViewModel.MinWidthFor(columnKey),
            Bounds.Width - others - splitters - reserved - HorizontalPadding
        );
    }

    private static double MeasureTextWidth(string text, double fontSize)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            TerminalTypeface,
            fontSize,
            Brushes.White
        );
        return Math.Ceiling(ft.WidthIncludingTrailingWhitespace);
    }

    private static double ClampColumnWidth(double value, double min, double max)
    {
        if (max < min)
        {
            return min;
        }
        return Math.Clamp(value, min, max);
    }

    /// <summary>
    /// 双击一行进入目录,或将文件下载到临时文件夹并用操作系统默认程序打开(§6)。
    /// </summary>
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm)
        {
            return;
        }
        if ((e.Source as Control)?.DataContext is not RemoteFileInfoViewModel row)
        {
            return;
        }
        vm.ActivateCommand.Execute(row).Subscribe(_ => { }, _ => { });
    }

    /// <summary>用平台默认处理程序打开已下载的本地文件。</summary>
    private async Task OpenLocalFileAsync(string localPath)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }
        await top.Launcher.LaunchFileInfoAsync(new(localPath));
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return [];
        }
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(
            new() { Title = Strings.SelectFilesToUpload, AllowMultiple = true }
        );
        return
        [
            .. files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!),
        ];
    }

    private async Task<string?> PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }
        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(
            new()
            {
                Title = Strings.SelectFolderToUpload,
                AllowMultiple = false
            }
        );
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// 选择文件夹/批量下载的本地目标文件夹,起始位置为设置中的
    /// 本地下载目录 (设置 → 文件传输).
    /// </summary>
    private async Task<string?> PickDownloadFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }
        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(
            new()
            {
                Title = Strings.SelectDownloadFolder,
                AllowMultiple = false,
                SuggestedStartLocation = await ResolveDefaultDownloadFolderAsync(top),
            }
        );
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// 把设置中的下载目录("~" 展开)换成存储提供器的文件夹句柄;失败返回 null
    /// (选择器落在系统默认位置)。
    /// </summary>
    private async Task<IStorageFolder?> ResolveDefaultDownloadFolderAsync(TopLevel top)
    {
        if (DataContext is not FileBrowserViewModel vm)
        {
            return null;
        }
        string configured = vm.TransferOptions.LocalDownloadDirectory.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            return null;
        }
        if (configured.StartsWith('~'))
        {
            configured = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                configured.TrimStart('~', '/', '\\')
            );
        }
        try
        {
            return Directory.Exists(configured)
                ? await top.StorageProvider.TryGetFolderFromPathAsync(configured)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void OnFileListDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        // 接受操作系统文件拖入或跨面板的本地文件拖拽(VFTPL 文本标记)。
        IReadOnlyList<string> osPaths = ExtractLocalPaths(e);
        bool isCrossPane = !string.IsNullOrEmpty(e.DataTransfer.TryGetText())
            && e.DataTransfer.TryGetText()!.StartsWith(DragDropFormats.LocalPaths);
        e.DragEffects = (osPaths.Count > 0 || isCrossPane) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileListDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm)
        {
            return;
        }
        IReadOnlyList<string> paths = ExtractLocalPaths(e);
        if (paths.Count == 0)
        {
            // 检查跨面板本地文件拖拽(VFTPL 标记)。
            string? text = e.DataTransfer.TryGetText();
            if (!string.IsNullOrEmpty(text) && text.StartsWith(DragDropFormats.LocalPaths))
            {
                paths = text[DragDropFormats.LocalPaths.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
        }
        if (paths.Count == 0)
        {
            return;
        }
        await vm.UploadLocalPathsAsync(paths);
        e.Handled = true;
    }

    private static IReadOnlyList<string> ExtractLocalPaths(DragEventArgs e)
    {
        IStorageItem[]? items = e.DataTransfer.TryGetFiles();
        if (items is null || items.Length == 0)
        {
            return [];
        }
        return
        [
            .. items
                .Select(i => i.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    private async Task<string?> PickSavePathAsync(string suggestedName)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }
        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(
            new()
            {
                Title = Strings.SaveToLocal,
                SuggestedFileName = suggestedName,
                SuggestedStartLocation = await ResolveDefaultDownloadFolderAsync(top),
            }
        );
        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// 下载遇到本地同名文件且冲突策略为“询问”:覆盖 / 全部覆盖 / 跳过 / 全部跳过。
    /// 无宿主窗口(理论上不至于)时保守按覆盖处理,与旧行为一致。
    /// </summary>
    private Task<FileConflictResolution> ConfirmOverwriteAsync(string localPath) =>
        AskConflictAsync(Strings.Format("Sftp_LocalOverwriteBody", localPath));

    /// <summary>
    /// 上传遇到远端同名文件且冲突策略为“询问”:覆盖 / 全部覆盖 / 跳过 / 全部跳过。
    /// </summary>
    private Task<FileConflictResolution> ConfirmRemoteOverwriteAsync(string remotePath) =>
        AskConflictAsync(Strings.Format("Sftp_RemoteOverwriteBody", remotePath));

    /// <summary>
    /// 弹出四选一冲突弹窗(覆盖 / 全部覆盖 / 跳过 / 全部跳过),批量传输时“全部…”一次决定
    /// 沿用到本批次其余冲突,免去逐文件弹窗。Esc / 关闭 = 跳过该文件(不误覆盖)。
    /// </summary>
    private async Task<FileConflictResolution> AskConflictAsync(string body)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return FileConflictResolution.Overwrite;
        }
        int choice = await MessageDialog.ChooseAsync(
            owner,
            Strings.Get("Sftp_FileExistsTitle"),
            body,
            [
                Strings.Get("Sftp_ConflictOverwrite"),
                Strings.Get("Sftp_ConflictOverwriteAll"),
                Strings.Get("Sftp_ConflictSkip"),
                Strings.Get("Sftp_ConflictSkipAll"),
            ],
            primaryIndex: 0,
            cancelResult: 2, // Esc / 关闭 = 跳过该文件(下标 2)。
            kind: MessageDialogKind.Warning
        );
        return choice switch
        {
            0 => FileConflictResolution.Overwrite,
            1 => FileConflictResolution.OverwriteAll,
            3 => FileConflictResolution.SkipAll,
            _ => FileConflictResolution.Skip,
        };
    }

    /// <summary>
    /// 模态单行文本输入,用于新建文件夹 / 新建文件 / 重命名 / 移动。
    /// 返回用户输入的文本,用户取消则返回 null。
    /// </summary>
    private async Task<string?> PromptForTextAsync(string title, string initialValue)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }
        return await MessageDialog.PromptAsync(owner, title, initialValue);
    }

    /// <summary>用于危险操作(删除)的模态确认(是/否)。返回 true 表示继续。</summary>
    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }
        return await MessageDialog.ConfirmAsync(
            owner,
            Strings.ConfirmDeleteTitle,
            message,
            Strings.Delete,
            kind: MessageDialogKind.Warning,
            danger: true
        );
    }

    private async Task CopyToClipboardAsync(string text)
    {
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>未配置默认编辑器:弹窗说明配置位置,确认则直接打开设置窗口(文件传输页)。</summary>
    private async Task PromptConfigureEditorAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        bool openSettings = await MessageDialog.ConfirmAsync(
            owner,
            Strings.Get("Sftp_NoEditorTitle"),
            Strings.Get("Sftp_NoEditorBody"),
            Strings.Get("Sftp_OpenSettings"),
            kind: MessageDialogKind.Info
        );
        if (!openSettings)
        {
            return;
        }

        // 直达 设置 → 文件传输 页(索引 5,对应 SettingsView 的页序)。
        if (
            Application.Current is App app
            && app.Services?.GetService<SettingsViewModel>() is { } settingsViewModel
        )
        {
            settingsViewModel.SelectedSectionIndex = 5;
        }
        if (owner.DataContext is MainWindowViewModel mainViewModel)
        {
            mainViewModel.OpenSettingsCommand.Execute().Subscribe();
        }
    }

    /// <summary>「打开」:非模态弹出内置 AvaloniaEdit 编辑器,保存时经回调上传回服务器。</summary>
    private Task OpenInBuiltInEditorAsync(
        RemoteFileInfoViewModel file,
        string localPath,
        Func<Task> uploadAsync
    )
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return Task.CompletedTask;
        }
        var editor = new RemoteFileEditorView(file.Name, file.FullPath, localPath, uploadAsync);
        editor.Show(owner);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 右键弹菜单前把所指行并入选区(资源管理器惯例):已在多选内则保持
    /// 多选不变,否则改为仅选中该行 —— 否则"下载/删除"(作用于选中集合)会对着
    /// 旧选区操作。
    /// </summary>
    private void FileRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            // 左键:为可能拖往本地面板做准备(仅 SFTP 模式)。
            if (DataContext is FileBrowserViewModel { IsDragEnabled: true }
                && sender is Border { DataContext: RemoteFileInfoViewModel file }
                && !file.IsParentEntry)
            {
                _dragOrigin = e.GetPosition(this.FindControl<ListBox>("FileList"));
                _dragRow = file;
                _dragPointerArgs = e;
                _isDragging = false;
            }
            return;
        }
        if (
            sender is not Border { DataContext: RemoteFileInfoViewModel file2 }
            || this.FindControl<ListBox>("FileList") is not { } listBox
        )
        {
            return;
        }
        if (file2.IsParentEntry)
        {
            return;
        }
        if (listBox.SelectedItems is { } selection && !selection.Contains(file2))
        {
            selection.Clear();
            selection.Add(file2);
        }
    }

    private void OnRemoteDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging || _dragRow is null || _dragPointerArgs is null)
        {
            return;
        }
        Point current = e.GetPosition(this.FindControl<ListBox>("FileList"));
        if (Math.Abs(current.X - _dragOrigin.X) < DragThreshold && Math.Abs(current.Y - _dragOrigin.Y) < DragThreshold)
        {
            return;
        }

        _isDragging = true;
        _ = StartRemoteDragAsync(_dragRow, _dragPointerArgs);
    }

    private void OnRemoteDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        _dragRow = null;
        _dragPointerArgs = null;
    }

    private async Task StartRemoteDragAsync(RemoteFileInfoViewModel source, PointerPressedEventArgs pointerArgs)
    {
        // 收集所有选中的远程文件路径。
        var paths = new List<string>();
        if (DataContext is FileBrowserViewModel vm)
        {
            foreach (RemoteFileInfoViewModel item in vm.SelectedFiles)
            {
                if (!item.IsParentEntry) paths.Add(item.FullPath);
            }
        }
        if (paths.Count == 0) paths.Add(source.FullPath);

        var data = new DataTransfer();
        var dragItem = new DataTransferItem();
        dragItem.SetText(DragDropFormats.RemotePaths + string.Join("\n", paths));
        data.Add(dragItem);

        await DragDrop.DoDragDropAsync(pointerArgs, data, DragDropEffects.Copy);

        _isDragging = false;
        _dragRow = null;
        _dragPointerArgs = null;
    }

    /// <summary>
    /// 属性弹窗(参考 WinSCP):基本信息 + rwx 权限矩阵 + 八进制输入合并在一个界面。
    /// 文本着色一律走 MessageDialog 的 BodyHost 样式类(dim/mono/mono-accent)—— 代码里
    /// FindResource 取不到主题字典的画刷(会拿到 null 把文字画没)。
    /// 确定且权限有变化时返回新 mode(三位八进制按十进制书写,如 755),否则返回 null。
    /// </summary>
    private async Task<short?> ShowFilePropertiesAsync(RemoteFileInfoViewModel file)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }
        var content = new StackPanel { Spacing = 14, MinWidth = 360 };

        // ── 头部:类型图标 + 名称 ─────────────────────────────────────────────
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var typeIcon = new LucideIcon
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Data = this.FindResource(file.IsDirectory ? "Icon.folder" : "Icon.file") as Geometry,
        };
        typeIcon.Classes.Add(file.IsDirectory ? "folder" : "file");
        header.Children.Add(typeIcon);
        header.Children.Add(
            new TextBlock
            {
                Text = file.Name,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            }
        );
        content.Children.Add(header);

        // ── 基本信息 ────────────────────────────────────────────────────────
        var rows = new StackPanel { Spacing = 8 };

        void AddRow(string label, string value, bool mono = true)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            var grid = new Grid { ColumnDefinitions = [with("96,*")] };
            var labelText = new TextBlock { Text = label };
            labelText.Classes.Add("dim");
            grid.Children.Add(labelText);
            var valueText = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            if (mono)
            {
                valueText.Classes.Add("mono");
            }
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);
            rows.Children.Add(grid);
        }

        AddRow(Strings.FileType, file.IsDirectory ? Strings.Folder : Strings.File, false);
        AddRow(Strings.FilePath, file.FullPath);
        AddRow(Strings.Size, file.FormattedSize);
        AddRow(Strings.Modified, file.FormattedModifiedTime);
        AddRow(Strings.PermissionOwner, file.Owner);
        AddRow(Strings.PermissionGroup, file.Group);
        content.Children.Add(rows);

        // ── 权限矩阵("drwxr-xr-x" → 9 个 rwx 标志;异常串回退为全不勾) ────────
        bool[] flags = new bool[9];
        string perms = file.Permissions;
        if (perms.Length == 10)
        {
            for (int i = 0; i < 9; i++)
            {
                flags[i] = perms[i + 1] != '-';
            }
        }
        var grid = new Grid
        {
            ColumnDefinitions = [with("96,Auto,Auto,Auto")],
            RowDefinitions = [with("Auto,Auto,Auto,Auto")],
        };

        void Place(Control control, int row, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            if (column > 0)
            {
                control.Margin = new(0, 4, 24, 4);
            }
            else
            {
                control.Margin = new(0, 4);
            }
            grid.Children.Add(control);
        }

        string[] columnHeaders =
        [
            Strings.PermissionRead,
            Strings.PermissionWrite,
            Strings.PermissionExecute,
        ];
        string[] rowHeaders =
        [
            Strings.PermissionOwner,
            Strings.PermissionGroup,
            Strings.PermissionOthers,
        ];
        var permTitle = new TextBlock { Text = Strings.Permissions };
        permTitle.Classes.Add("dim");
        Place(permTitle, 0, 0);
        for (int c = 0; c < 3; c++)
        {
            var head = new TextBlock { Text = columnHeaders[c], FontWeight = FontWeight.Medium };
            Place(head, 0, c + 1);
        }
        var boxes = new CheckBox[9];

        short CurrentMode()
        {
            short mode = 0;
            for (int g = 0; g < 3; g++)
            {
                int digit =
                    (boxes[g * 3].IsChecked == true ? 4 : 0)
                    + (boxes[(g * 3) + 1].IsChecked == true ? 2 : 0)
                    + (boxes[(g * 3) + 2].IsChecked == true ? 1 : 0);
                mode = (short)((mode * 10) + digit);
            }
            return mode;
        }

        // 八进制输入与勾选矩阵双向同步(参考图中的"八进制"输入行)。
        var octalBox = new TextBox { Width = 90, MaxLength = 3 };
        bool syncing = false;

        void SyncOctalFromBoxes()
        {
            if (syncing)
            {
                return;
            }
            syncing = true;
            octalBox.Text = $"{CurrentMode():000}";
            syncing = false;
        }

        void SyncBoxesFromOctal()
        {
            if (syncing || octalBox.Text is not { Length: 3 } text)
            {
                return;
            }
            syncing = true;
            for (int g = 0; g < 3; g++)
            {
                if (text[g] is < '0' or > '7')
                {
                    continue;
                }
                int digit = text[g] - '0';
                boxes[g * 3].IsChecked = (digit & 4) != 0;
                boxes[(g * 3) + 1].IsChecked = (digit & 2) != 0;
                boxes[(g * 3) + 2].IsChecked = (digit & 1) != 0;
            }
            syncing = false;
        }

        for (int r = 0; r < 3; r++)
        {
            var rowLabel = new TextBlock
            {
                Text = rowHeaders[r],
                VerticalAlignment = VerticalAlignment.Center,
            };
            rowLabel.Classes.Add("dim");
            Place(rowLabel, r + 1, 0);
            for (int c = 0; c < 3; c++)
            {
                var box = new CheckBox
                {
                    IsChecked = flags[(r * 3) + c],
                    MinWidth = 0,
                    Padding = new(0),
                };
                box.IsCheckedChanged += (_, _) => SyncOctalFromBoxes();
                boxes[(r * 3) + c] = box;
                Place(box, r + 1, c + 1);
            }
        }
        short initialMode = CurrentMode();
        SyncOctalFromBoxes();
        octalBox.TextChanged += (_, _) => SyncBoxesFromOctal();
        content.Children.Add(grid);
        var octalRow = new Grid { ColumnDefinitions = [with("96,*")] };
        var octalLabel = new TextBlock
        {
            Text = Strings.Get("Sftp_Octal"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        octalLabel.Classes.Add("dim");
        octalRow.Children.Add(octalLabel);
        var octalHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        octalHost.Children.Add(octalBox);
        var chmodEcho = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        chmodEcho.Classes.Add("mono-accent");
        void RefreshEcho() => chmodEcho.Text = $"chmod {CurrentMode():000}";
        RefreshEcho();
        foreach (CheckBox box in boxes)
        {
            box.IsCheckedChanged += (_, _) => RefreshEcho();
        }
        octalBox.TextChanged += (_, _) => RefreshEcho();
        octalHost.Children.Add(chmodEcho);
        Grid.SetColumn(octalHost, 1);
        octalRow.Children.Add(octalHost);
        content.Children.Add(octalRow);
        bool confirmed = await MessageDialog.ShowCustomAsync(owner, Strings.Properties, content);
        if (!confirmed)
        {
            return null;
        }
        short newMode = CurrentMode();
        return newMode == initialMode ? null : newMode;
    }
}
