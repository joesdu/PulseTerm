using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>本地文件面板视图,含面包屑、上下文菜单与跨面板拖放。</summary>
public partial class LocalFilePaneView : UserControl
{
    // 拖拽状态
    private bool _isDragging;
    private Point _dragOrigin;
    private LocalFileEntry? _dragRow;
    private PointerPressedEventArgs? _dragPointerArgs;
    private readonly List<LocalFileEntry> _selectionAtPress = [];
    private bool _selectionRestoredForPendingDrag;
    private const double DragThreshold = 5;

    /// <summary>初始化本地文件面板视图,并接线 VM 委托。</summary>
    public LocalFilePaneView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LocalFilePaneViewModel vm)
            {
                vm.ConfirmDelete = ConfirmAsync;
                vm.PromptForText = PromptForTextAsync;
                vm.OpenLocalFile = OpenLocalFileAsync;
            }
        };

        // 框选与跨栏拖放使用不相交的按下表面:只有 XAML 标记的内容表面发起拖放。
        if (this.FindControl<ListBox>("FileList") is { } list
            && this.FindControl<Border>("MarqueeOverlay") is { } overlay)
        {
            MarqueeSelection.Attach(
                list,
                overlay,
                item => item is LocalFileEntry { IsParentEntry: false },
                e => !MarqueeSelection.IsDndSurface(e.Source, FileList, e.GetPosition(FileList))
            );
        }

        // 拖放:接收远端文件拖入 + 发起本地文件拖拽。
        if (this.FindControl<ListBox>("FileList") is { } fileList)
        {
            DragDrop.SetAllowDrop(fileList, true);
            fileList.AddHandler(DragDrop.DragOverEvent, OnLocalDropDragOver);
            fileList.AddHandler(DragDrop.DropEvent, OnLocalDrop);
            fileList.AddHandler(InputElement.PointerPressedEvent, OnLocalDragPointerPressedTunnel, RoutingStrategies.Tunnel, true);
            fileList.AddHandler(InputElement.PointerPressedEvent, OnLocalDragPointerPressedBubble, RoutingStrategies.Bubble, true);
            fileList.AddHandler(PointerMovedEvent, OnLocalDragPointerMoved);
            fileList.AddHandler(PointerReleasedEvent, OnLocalDragPointerReleased, RoutingStrategies.Bubble, true);
        }
    }

    private void OnRootSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is LocalFilePaneViewModel viewModel
            && sender is ComboBox comboBox
            && comboBox.SelectedItem is LocalRootEntry root
            && !ReferenceEquals(root, viewModel.SelectedRoot))
        {
            if (!root.IsAccessible)
            {
                comboBox.SelectedItem = viewModel.SelectedRoot;
                return;
            }
            viewModel.SwitchRootCommand.Execute(root).Subscribe();
        }
    }

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
            danger: true);
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is LocalFilePaneViewModel vm && sender is ListBox list && list.SelectedItem is LocalFileEntry entry)
        {
            vm.ActivateCommand.Execute(entry).Subscribe();
        }
    }

    private void OnFileRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            // 非父行上按左键:为拖往远端面板做准备。
            if (sender is Border { DataContext: LocalFileEntry entry }
                && !entry.IsParentEntry
                && MarqueeSelection.IsDndSurface(e.Source, (Visual)sender, e.GetPosition((Visual)sender)))
            {
                _dragOrigin = e.GetPosition(this.FindControl<ListBox>("FileList"));
                _dragRow = entry;
                _dragPointerArgs = e;
                _isDragging = false;
            }
            return;
        }
        // 右键:为上下文菜单选中该行。
        if (sender is not Border { DataContext: LocalFileEntry entry2 }
            || entry2.IsParentEntry
            || DataContext is not LocalFilePaneViewModel
            || FileList is not { } listBox)
        {
            return;
        }
        if (listBox.SelectedItems is not { } selection)
        {
            return;
        }
        if (!selection.Contains(entry2))
        {
            selection.Clear();
            selection.Add(entry2);
        }
    }

    private void OnLocalDragPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        _selectionAtPress.Clear();
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed
            || FileList.SelectedItems is not { } selected)
        {
            return;
        }

        foreach (object? item in selected)
        {
            if (item is LocalFileEntry entry && !entry.IsParentEntry)
            {
                _selectionAtPress.Add(entry);
            }
        }
    }

    private void OnLocalDragPointerPressedBubble(object? sender, PointerPressedEventArgs e)
    {
        if (_dragRow is not { } source
            || _selectionAtPress.Count <= 1
            || !_selectionAtPress.Contains(source)
            || e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            || DataContext is not LocalFilePaneViewModel vm)
        {
            return;
        }

        DragSelectionResolver.SynchronizeSelection(vm.SelectedEntries, _selectionAtPress);
        _selectionRestoredForPendingDrag = true;
    }

    private async Task<string?> PromptForTextAsync(string title, string initialValue)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }
        return await MessageDialog.PromptAsync(owner, title, initialValue);
    }

    private async Task OpenLocalFileAsync(string localPath)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }
        await top.Launcher.LaunchFileInfoAsync(new(localPath));
    }

    // ── 拖拽发起(本地 → 远端) ────────────────────────────────

    private void OnLocalDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging || _dragRow is null || _dragPointerArgs is null)
        {
            return;
        }
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed)
        {
            ResetLocalDragGesture();
            return;
        }
        Point current = e.GetPosition(this.FindControl<ListBox>("FileList"));
        if (Math.Abs(current.X - _dragOrigin.X) < DragThreshold && Math.Abs(current.Y - _dragOrigin.Y) < DragThreshold)
        {
            return;
        }

        _isDragging = true;
        _ = StartLocalDragAsync(_dragRow, _dragPointerArgs);
    }

    private void OnLocalDragPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging
            && _selectionRestoredForPendingDrag
            && _dragRow is { } source
            && DataContext is LocalFilePaneViewModel vm)
        {
            DragSelectionResolver.SynchronizeSelection(vm.SelectedEntries, [source]);
        }
        ResetLocalDragGesture();
    }

    private async Task StartLocalDragAsync(LocalFileEntry source, PointerPressedEventArgs pointerArgs)
    {
        if (DataContext is LocalFilePaneViewModel vm)
        {
            IReadOnlyList<LocalFileEntry> entries = DragSelectionResolver.ResolveAtDragStart(
                _selectionAtPress,
                vm.SelectedEntries,
                source,
                item => item.IsParentEntry,
                usePressSnapshot: !pointerArgs.KeyModifiers.HasFlag(KeyModifiers.Control)
                    && !pointerArgs.KeyModifiers.HasFlag(KeyModifiers.Shift));
            DragSelectionResolver.SynchronizeSelection(vm.SelectedEntries, entries);
            string[] paths = [.. entries.Select(item => item.FullPath)];
            await StartDragAsync(paths, pointerArgs, DragDropFormats.LocalPaths);
            return;
        }
        await StartDragAsync([source.FullPath], pointerArgs, DragDropFormats.LocalPaths);
    }

    private async Task StartDragAsync(
        IReadOnlyList<string> paths,
        PointerPressedEventArgs pointerArgs,
        string format)
    {
        var data = new DataTransfer();
        var dragItem = new DataTransferItem();
        dragItem.SetText(format + string.Join("\n", paths));
        data.Add(dragItem);

        try
        {
            await DragDrop.DoDragDropAsync(pointerArgs, data, DragDropEffects.Copy);
        }
        finally
        {
            ResetLocalDragGesture();
        }
    }

    private void ResetLocalDragGesture()
    {
        _isDragging = false;
        _dragRow = null;
        _dragPointerArgs = null;
        _selectionAtPress.Clear();
        _selectionRestoredForPendingDrag = false;
    }

    // ── 放置处理(远端 → 本地) ──────────────────────────────────

    private void OnLocalDropDragOver(object? sender, DragEventArgs e)
    {
        bool isRemoteDrag = !string.IsNullOrEmpty(e.DataTransfer.TryGetText())
            && e.DataTransfer.TryGetText()!.StartsWith(DragDropFormats.RemotePaths);
        bool hasFiles = e.DataTransfer.TryGetFiles()?.Length > 0;
        e.DragEffects = (isRemoteDrag || hasFiles) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnLocalDrop(object? sender, DragEventArgs e)
    {
        // 先检查跨面板的远端文件拖拽(VFTP 标记)。
        string? text = e.DataTransfer.TryGetText();
        if (!string.IsNullOrEmpty(text) && text.StartsWith(DragDropFormats.RemotePaths))
        {
            string[] remotePaths = text[DragDropFormats.RemotePaths.Length..].Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (remotePaths.Length == 0) return;

            // 用可视化树祖先查找,代替脆弱的 Parent.Parent 链式访问。
            if (this.FindAncestorOfType<SftpDocumentView>()?.DataContext is not SftpDocumentViewModel sftpVm) return;

            // 由路径解析远端条目,且不修改远端的选中集合。
            RemoteFileInfoViewModel[] entries = remotePaths
                .Select(p => sftpVm.RemoteFiles.Files.FirstOrDefault(f => f.FullPath == p))
                .Where(e => e is not null && !e.IsParentEntry)
                .ToArray()!;

            if (entries.Length > 0)
            {
                try
                {
                    await sftpVm.RemoteFiles.DownloadRemoteEntriesAsync(entries, sftpVm.LocalFiles.CurrentPath);
                    await sftpVm.LocalFiles.RefreshAsync();
                }
                catch (FileNotFoundException)
                {
                    // 文件可能已在列举与下载之间被删除;刷新远端列表。
                    sftpVm.RemoteFiles.RefreshCommand.Execute().Subscribe();
                }
            }
            e.Handled = true;
            return;
        }

        // 也支持将操作系统文件拖入本地面板(仅刷新)。
        IStorageItem[]? files = e.DataTransfer.TryGetFiles();
        if (files is { Length: > 0 })
        {
            if (DataContext is LocalFilePaneViewModel vm)
            {
                await vm.RefreshAsync();
            }
            e.Handled = true;
        }
    }
}
