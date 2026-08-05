using System.Collections.Concurrent;
using Avalonia.Threading;
using VelaShell.Core.Models;
using VelaShell.Core.ZModem.Abstractions;
using VelaShell.Core.ZModem.Model;
using VelaShell.ViewModels;

namespace VelaShell.Services.ZModem;

/// <summary>
/// 把 ZMODEM 会话进度桥接到既有的文件传输面板(toast):每个 <see cref="ZModemTransferItem" />
/// 映射为一个 <see cref="TransferTask" /> 并驱动 <see cref="FileTransferViewModel" />。所有回调都从
/// ZMODEM 引擎的后台线程经 <see cref="Dispatcher.UIThread" /> 编组到 UI 线程后再改动视图模型。
/// </summary>
internal sealed class ZModemTransferObserver(FileTransferViewModel fileTransfer) : IZModemSessionObserver
{
    private readonly FileTransferViewModel _fileTransfer =
        fileTransfer ?? throw new ArgumentNullException(nameof(fileTransfer));

    // 每个文件项的测速起点(用于估算速率与剩余时间);后台线程读写,用并发字典。
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _startedAt = [];

    /// <summary>进度上报的最小间隔:引擎每个 subpacket(默认 1KB)回调一次,
    /// 不节流的话 100MB 传输就是约十万次 UI 线程 Post + 闭包/进度对象/字符串分配风暴,
    /// 高吞吐时肉眼可见地卡 UI(SFTP 路径早有同款节流 ProgressThrottle,此处补齐)。</summary>
    private const int ProgressMinIntervalMs = 100;

    private readonly ConcurrentDictionary<Guid, long> _lastProgressTick = [];

    /// <inheritdoc />
    public void OnFileStarted(ZModemTransferItem item)
    {
        _startedAt[item.Id] = DateTimeOffset.UtcNow;
        Guid id = item.Id;
        string fileName = item.FileName;
        string? localPath = item.LocalPath;
        long bytes = item.BytesTransferred;
        long? size = item.Size;

        Dispatcher.UIThread.Post(() =>
        {
            // RemotePath 决定 UI 显示的文件名(TransferItemViewModel.FileName 取其 GetFileName)。
            var task = new TransferTask
            {
                Id = id,
                Type = TransferType.Download,
                LocalPath = localPath ?? string.Empty,
                RemotePath = fileName,
                Status = TransferStatus.InProgress,
                Progress = BuildProgress(fileName, bytes, size, 0)
            };
            _fileTransfer.AddTransfer(task);
        });
    }

    /// <inheritdoc />
    public void OnFileProgress(ZModemTransferItem item)
    {
        // 时间片节流:片内的 tick 直接丢弃(完成回调必带 100% 的末次刷新,不会丢终值)。
        long now = Environment.TickCount64;
        long last = _lastProgressTick.GetOrAdd(item.Id, now - ProgressMinIntervalMs - 1);
        if (now - last < ProgressMinIntervalMs || !_lastProgressTick.TryUpdate(item.Id, now, last))
        {
            return;
        }
        double speed = ComputeSpeed(item);
        Guid id = item.Id;
        long bytes = item.BytesTransferred;
        long? size = item.Size;
        string fileName = item.FileName;

        Dispatcher.UIThread.Post(() =>
        {
            TransferItemViewModel? vm = _fileTransfer.FindTransfer(id);
            if (vm is null)
            {
                return;
            }
            vm.UpdateProgress(BuildProgress(fileName, bytes, size, speed));
            if (vm.Status != TransferStatus.InProgress)
            {
                vm.Status = TransferStatus.InProgress;
            }
        });
    }

    /// <inheritdoc />
    public void OnFileCompleted(ZModemTransferItem item)
    {
        _startedAt.TryRemove(item.Id, out _);
        _lastProgressTick.TryRemove(item.Id, out _);
        Guid id = item.Id;
        long bytes = item.BytesTransferred;
        long? size = item.Size;
        string fileName = item.FileName;

        Dispatcher.UIThread.Post(() =>
        {
            TransferItemViewModel? vm = _fileTransfer.FindTransfer(id);
            if (vm is null)
            {
                return;
            }
            long total = size ?? bytes;
            vm.UpdateProgress(new TransferProgress
            {
                FileName = fileName,
                BytesTransferred = bytes,
                TotalBytes = Math.Max(total, bytes),
                Percentage = 100,
                SpeedBytesPerSecond = 0,
                EstimatedTimeRemaining = TimeSpan.Zero
            });
            vm.Status = TransferStatus.Completed;
            _fileTransfer.NotifyTaskSettled();
        });
    }

    /// <inheritdoc />
    public void OnFileSkipped(ZModemTransferItem item)
    {
        _startedAt.TryRemove(item.Id, out _);
        _lastProgressTick.TryRemove(item.Id, out _);
        Guid id = item.Id;
        Dispatcher.UIThread.Post(() =>
        {
            TransferItemViewModel? vm = _fileTransfer.FindTransfer(id);
            if (vm is not null)
            {
                vm.Status = TransferStatus.Cancelled;
                _fileTransfer.NotifyTaskSettled();
            }
        });
    }

    /// <inheritdoc />
    public void OnSessionFailed(ZModemSession session, Exception? error)
    {
        _ = error;
        // 快照会话内所有项 Id(session.Items 可能在后台被改动)。
        Guid[] ids = [.. session.Items.Select(i => i.Id)];
        Dispatcher.UIThread.Post(() =>
        {
            foreach (Guid id in ids)
            {
                TransferItemViewModel? vm = _fileTransfer.FindTransfer(id);
                if (vm is { Status: TransferStatus.InProgress or TransferStatus.Queued })
                {
                    vm.Status = TransferStatus.Failed;
                }
            }
            _fileTransfer.NotifyTaskSettled();
        });
    }

    private double ComputeSpeed(ZModemTransferItem item)
    {
        if (!_startedAt.TryGetValue(item.Id, out DateTimeOffset started))
        {
            return 0;
        }
        double elapsed = Math.Max((DateTimeOffset.UtcNow - started).TotalSeconds, 0.001);
        return item.BytesTransferred / elapsed;
    }

    /// <summary>
    /// 构造进度快照。已知大小时给出百分比与剩余时间;大小未知时用已传字节作总量占位、
    /// 百分比置 0(当前 toast 模型不支持真正的不确定态),保持"传输中"语义。
    /// </summary>
    private static TransferProgress BuildProgress(string fileName, long bytesTransferred, long? size, double speed)
    {
        bool known = size is > 0;
        long total = known ? size!.Value : Math.Max(bytesTransferred, 1);
        int percentage = known ? (int)Math.Clamp(bytesTransferred * 100L / total, 0, 100) : 0;
        TimeSpan eta = known && speed > 0
            ? TimeSpan.FromSeconds(Math.Max(total - bytesTransferred, 0) / speed)
            : TimeSpan.Zero;
        return new TransferProgress
        {
            FileName = fileName,
            BytesTransferred = bytesTransferred,
            TotalBytes = total,
            Percentage = percentage,
            SpeedBytesPerSecond = speed,
            EstimatedTimeRemaining = eta
        };
    }
}
