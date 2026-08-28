using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelaShell.Core.Recording;
using VelaShell.Core.Resources;
using VelaShell.Terminal.Rendering;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 会话录制回放中心(设计 NceE6)。终端回放区挂载一个只读的
/// <see cref="VelaTerminalControl" />,由 VM 的 Feed/Reset 回调驱动。
/// </summary>
public partial class RecordingPlayerView : Window
{
    /// <summary>RIS(ESC c)完全重置:选择新录制/拖动时间轴时清屏重放。</summary>
    private static readonly byte[] RisResetSequence = [0x1B, (byte)'c'];

    /// <summary>清理时"仅保留最近"档位的保留天数。</summary>
    private const int RecentCleanupKeepDays = 7;

    private readonly VelaTerminalControl _terminal;
    private RecordingPlayerViewModel? _viewModel;

    /// <summary>构造回放中心窗口,挂载只读回放终端并订阅 VM 的 Feed/Reset 回调。</summary>
    public RecordingPlayerView()
    {
        InitializeComponent();

        // 只读回放终端:不接输入,不参与焦点。
        _terminal = new()
        {
            Focusable = false,
            IsHitTestVisible = false
        };
        TerminalHost.Child = _terminal;
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.FeedSink = null;
                _viewModel.ResetSink = null;
            }
            _viewModel = DataContext as RecordingPlayerViewModel;
            if (_viewModel is not null)
            {
                // VM 的播放定时器在 UI 线程(DispatcherTimer),直接喂给控件即可。
                _viewModel.FeedSink = data => _terminal.Feed(data);
                _viewModel.ResetSink = () => _terminal.Feed(RisResetSequence);
            }
        };
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    private void Header_DoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>右下角缩放手柄:无边框窗口经此拖拽调整大小。</summary>
    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginResizeDrag(WindowEdge.SouthEast, e);
        }
    }

    // 推迟关闭:同步 Close 会让本轮点击/按键的后续路由打到已销毁的窗口刷
    // "PlatformImpl is null" 警告(见 WindowCloseExtensions)。
    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <summary>关闭时停掉回放定时器,否则 VM 与整段录制数据会被调度器吊住(见 StopPlayback)。</summary>
    protected override void OnClosed(EventArgs e)
    {
        (DataContext as RecordingPlayerViewModel)?.StopPlayback();
        base.OnClosed(e);
    }

    /// <summary>Esc 关闭回放中心,与右上角关闭按钮同路径(回放终端不可聚焦,不会截获按键)。</summary>
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

    /// <summary>倍速按钮:按 VM 定义的档位循环(1x…16x)。</summary>
    private void CycleSpeed_Click(object? sender, RoutedEventArgs e) => _viewModel?.CycleSpeed();

    /// <summary>
    /// 清理录制数据:先摆出占用现状,再让用户选清理力度。
    /// 时序库的删除只写墓碑不腾空间,所以这里给的三档都会走 drop 重建把字节真正还回去。
    /// </summary>
    private async void Cleanup_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        RecordingStorageUsage usage;
        try
        {
            usage = await vm.GetStorageUsageAsync();
        }
        catch (Exception ex)
        {
            vm.SetStatus(Strings.Format("Recorder_CleanupFailed", ex.Message));
            return;
        }
        int choice = await MessageDialog.ChooseAsync(this,
            Strings.Get("Recorder_CleanupTitle"),
            Strings.Format("Recorder_CleanupMessage",
                usage.RecordingCount,
                FormatBytes(usage.LiveBytes),
                FormatBytes(usage.DiskBytes),
                FormatBytes(usage.ReclaimableBytes)),
            [
                Strings.Get("Recorder_CleanupKeepAll"),
                Strings.Get("Recorder_CleanupKeepRecent"),
                Strings.Get("Recorder_CleanupPurgeAll")
            ],
            0,
            -1,
            MessageDialogKind.Warning);

        // 保留全部 / 保留最近 7 天 / 一条不留;Esc 与关闭按钮回 -1。
        int keepDays = choice switch
        {
            0 => int.MaxValue,
            1 => RecentCleanupKeepDays,
            2 => 0,
            _ => -1
        };
        if (keepDays < 0)
        {
            return;
        }
        if (keepDays == 0 && !await MessageDialog.ConfirmAsync(this,
                Strings.Get("Recorder_CleanupTitle"),
                Strings.Get("Recorder_CleanupPurgeConfirm"),
                Strings.Get("Recorder_CleanupPurgeAll"),
                null,
                MessageDialogKind.Warning,
                true))
        {
            return;
        }

        CleanupButton.IsEnabled = false;
        vm.SetStatus(Strings.Get("Recorder_CleanupBusy"));
        try
        {
            RecordingCleanupResult result = await vm.CleanupAsync(keepDays);

            // 段文件走内存映射后,腾出的文件在本进程退出前删不掉(SonnetDB 下次开库才清),
            // 此时磁盘数字没降 —— 如实说"重启后释放",别让用户以为清了个寂寞。
            vm.SetStatus(result.DeferredToRestart
                ? Strings.Format("Recorder_CleanupDoneDeferred", result.RemovedRecordings)
                : Strings.Format("Recorder_CleanupDone", result.RemovedRecordings,
                    FormatBytes(result.DiskBytesBefore - result.DiskBytesAfter)));
        }
        catch (Exception ex)
        {
            vm.SetStatus(Strings.Format("Recorder_CleanupFailed", ex.Message));
        }
        finally
        {
            CleanupButton.IsEnabled = true;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
    };

    /// <summary>导出选中录制为 asciicast v2(.cast)文件。</summary>
    private async void ExportRecording_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { HasSelection: true } vm)
        {
            return;
        }
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Strings.Get("Recorder_Export"),
            SuggestedFileName = $"velashell-recording-{DateTime.Now:yyyyMMdd-HHmmss}.cast",
            SuggestedStartLocation = await StorageDefaults.DownloadsAsync(this),
            DefaultExtension = "cast"
        });
        if (file?.TryGetLocalPath() is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, vm.BuildAsciicast());
        }
    }
}
