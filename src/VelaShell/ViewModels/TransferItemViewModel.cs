using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>单个文件传输任务的视图模型,封装进度、速度、剩余时间及状态展示逻辑。</summary>
public class TransferItemViewModel : ReactiveObject
{
    private readonly TransferTask _task;

    private int _progress;
    private string _speed = string.Empty;
    private TransferStatus _status;
    private string _timeRemaining = string.Empty;
    private long _totalSize;
    private long _transferredBytes;

    /// <summary>基于给定的传输任务初始化视图模型,并从任务的初始进度快照填充展示字段。</summary>
    /// <param name="task">要展示的底层传输任务。</param>
    public TransferItemViewModel(TransferTask task)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
        _status = task.Status;
        if (task.Progress == null)
        {
            return;
        }
        _totalSize = task.Progress.TotalBytes;
        _transferredBytes = task.Progress.BytesTransferred;
        _progress = task.Progress.Percentage;
        _speed = FormatSpeed(task.Progress.SpeedBytesPerSecond);
        _timeRemaining = FormatTimeRemaining(task.Progress.EstimatedTimeRemaining);
    }

    /// <summary>传输任务的唯一标识。</summary>
    public Guid Id => _task.Id;

    /// <summary>从远程路径中提取的文件名,用于列表展示。</summary>
    public string FileName => Path.GetFileName(_task.RemotePath);

    /// <summary>传输方向(上传/下载/远端复制),供重试与历史持久化使用。</summary>
    public TransferType Type => _task.Type;

    /// <summary>本地路径(远端复制时为远端源路径),供重试与历史持久化使用。</summary>
    public string LocalPath => _task.LocalPath;

    /// <summary>远端路径,供重试与历史持久化使用。</summary>
    public string RemotePath => _task.RemotePath;

    /// <summary>
    /// 失败后的重试动作,由发起传输的浏览器视图模型在创建时挂上(闭包捕获正确的会话与路径,
    /// 重试会重新探测续传起点)。为 null 时不可重试 —— 例如重启后从历史恢复的记录,
    /// 其会话已不存在,只能由用户重新发起同一操作(续传素材仍在,会自动接上)。
    /// </summary>
    public Func<Task>? RetryAsync { get; set; }

    /// <summary>失败且有重试动作时,面板显示"重试"按钮。</summary>
    public bool CanRetry => IsFailed && RetryAsync is not null;

    /// <summary>传输方向指示符:上传为 "↑",下载为 "↓",远端复制为 "⧉"。</summary>
    public string Direction => _task.Type switch
    {
        TransferType.Upload => "↑",
        TransferType.Download => "↓",
        TransferType.Copy => "⧉",
        _ => "?"
    };

    /// <summary>传输进度百分比(0-100)。</summary>
    public int Progress
    {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    /// <summary>当前传输速度的格式化文本(如 "4.2 MB/s")。</summary>
    public string Speed
    {
        get => _speed;
        private set => this.RaiseAndSetIfChanged(ref _speed, value);
    }

    /// <summary>预计剩余时间的格式化文本(如 "2m 30s")。</summary>
    public string TimeRemaining
    {
        get => _timeRemaining;
        private set => this.RaiseAndSetIfChanged(ref _timeRemaining, value);
    }

    /// <summary>传输任务的当前状态,设置时会同步回底层任务并刷新相关派生属性。</summary>
    public TransferStatus Status
    {
        get => _status;
        set
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            _task.Status = value;
            this.RaisePropertyChanged(nameof(IsActive));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(IsCompleted));
            this.RaisePropertyChanged(nameof(CanRetry));
            this.RaisePropertyChanged(nameof(ProgressText));
            this.RaisePropertyChanged(nameof(InfoLine));
        }
    }

    /// <summary>指示任务是否处于活动状态(进行中或排队中或续传中)。</summary>
    public bool IsActive => _status is TransferStatus.InProgress or TransferStatus.Queued or TransferStatus.Resuming;

    /// <summary>指示任务是否已失败。</summary>
    public bool IsFailed => _status == TransferStatus.Failed;

    /// <summary>指示任务是否已完成。</summary>
    public bool IsCompleted => _status == TransferStatus.Completed;

    /// <summary>要传输的文件总字节数。</summary>
    public long TotalSize
    {
        get => _totalSize;
        private set => this.RaiseAndSetIfChanged(ref _totalSize, value);
    }

    /// <summary>已传输的字节数。</summary>
    public long TransferredBytes
    {
        get => _transferredBytes;
        private set => this.RaiseAndSetIfChanged(ref _transferredBytes, value);
    }

    /// <summary>Right-hand status: "67%" while running, "完成" when done, "失败" on error.</summary>
    public string ProgressText => _status switch
    {
        TransferStatus.Completed => Strings.Get("Msg_Done"),
        TransferStatus.Failed => Strings.Get("Msg_Failed"),
        _ => $"{_progress}%"
    };

    /// <summary>Detail line per design 9Ralg: "142 MB / 212 MB  •  4.2 MB/s  •  ↑ 上传中".</summary>
    public string InfoLine
    {
        get
        {
            string action = _task.Type == TransferType.Upload ? $"↑ {Strings.Get("Msg_Uploading")}" : $"↓ {Strings.Get("Msg_Downloading")}";
            if (_status == TransferStatus.Completed)
            {
                action = _task.Type == TransferType.Upload ? $"↑ {Strings.Get("Msg_Uploaded")}" : $"↓ {Strings.Get("Msg_Downloaded")}";
            }
            else if (_status == TransferStatus.Failed)
            {
                action = Strings.Get("Msg_Failed");
            }
            return _status == TransferStatus.Completed
                       ? $"{FormatBytes(_totalSize)}  •  {Strings.Get("Msg_Completed")}  •  {action}"
                       : $"{FormatBytes(_transferredBytes)} / {FormatBytes(_totalSize)}  •  {_speed}  •  {action}";
        }
    }

    /// <summary>根据最新的进度快照更新进度、已传字节、总大小、速度与剩余时间,并通知界面刷新。</summary>
    /// <param name="progress">最新的传输进度信息。</param>
    public void UpdateProgress(TransferProgress progress)
    {
        // 只有承载值真变了才广播派生属性:节流后的进度流里仍可能出现同值 tick
        // (小文件、暂停的连接),白发一次 InfoLine/ProgressText 就是白做一次字符串拼装与重绑。
        string speed = FormatSpeed(progress.SpeedBytesPerSecond);
        string remaining = FormatTimeRemaining(progress.EstimatedTimeRemaining);
        bool changed = _progress != progress.Percentage
                       || _transferredBytes != progress.BytesTransferred
                       || _totalSize != progress.TotalBytes
                       || _speed != speed
                       || _timeRemaining != remaining;
        Progress = progress.Percentage;
        TransferredBytes = progress.BytesTransferred;
        TotalSize = progress.TotalBytes;
        Speed = speed;
        TimeRemaining = remaining;
        if (changed)
        {
            this.RaisePropertyChanged(nameof(InfoLine));
            this.RaisePropertyChanged(nameof(ProgressText));
        }
    }

    /// <summary>将字节数格式化为带单位(B/KB/MB/GB/TB)的可读文本。</summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>格式化后的大小文本,如 "142.0 MB"。</returns>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = Math.Min((int)Math.Floor(Math.Log(bytes, 1024)), units.Length - 1);
        double value = bytes / Math.Pow(1024, i);
        return i == 0 ? $"{bytes} B" : $"{value:F1} {units[i]}";
    }

    /// <summary>将每秒字节速率格式化为带单位的速度文本。</summary>
    /// <param name="bytesPerSecond">每秒传输的字节数。</param>
    /// <returns>格式化后的速度文本,如 "4.2 MB/s"。</returns>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return "0 B/s";
        }
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
        int i = (int)Math.Floor(Math.Log(bytesPerSecond, 1024));
        i = Math.Min(i, units.Length - 1);
        double value = bytesPerSecond / Math.Pow(1024, i);
        return i == 0
                   ? $"{(int)value} {units[0]}"
                   : $"{value:F1} {units[i]}";
    }

    /// <summary>将剩余时间跨度格式化为紧凑的可读文本(小时/分钟/秒)。</summary>
    /// <param name="remaining">预计剩余时间;小于等于零时返回空字符串。</param>
    /// <returns>格式化后的剩余时间文本,如 "1h 20m"、"2m 30s" 或 "45s"。</returns>
    public static string FormatTimeRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return string.Empty;
        }
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        }
        if (remaining.TotalMinutes >= 1)
        {
            return $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s";
        }
        return $"{remaining.Seconds}s";
    }
}
