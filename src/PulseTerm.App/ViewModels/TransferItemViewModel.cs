using System;
using System.IO;
using PulseTerm.Core.Models;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class TransferItemViewModel : ReactiveObject
{
    private readonly TransferTask _task;

    private int _progress;
    private string _speed = string.Empty;
    private string _timeRemaining = string.Empty;
    private TransferStatus _status;
    private long _totalSize;
    private long _transferredBytes;

    public TransferItemViewModel(TransferTask task)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
        _status = task.Status;

        if (task.Progress != null)
        {
            _totalSize = task.Progress.TotalBytes;
            _transferredBytes = task.Progress.BytesTransferred;
            _progress = task.Progress.Percentage;
            _speed = FormatSpeed(task.Progress.SpeedBytesPerSecond);
            _timeRemaining = FormatTimeRemaining(task.Progress.EstimatedTimeRemaining);
        }
    }

    public Guid Id => _task.Id;

    public string FileName => Path.GetFileName(_task.RemotePath);

    public string Direction => _task.Type == TransferType.Upload ? "↑" : "↓";

    public int Progress
    {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public string Speed
    {
        get => _speed;
        private set => this.RaiseAndSetIfChanged(ref _speed, value);
    }

    public string TimeRemaining
    {
        get => _timeRemaining;
        private set => this.RaiseAndSetIfChanged(ref _timeRemaining, value);
    }

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
        }
    }

    public bool IsActive => _status == TransferStatus.InProgress || _status == TransferStatus.Queued;
    public bool IsFailed => _status == TransferStatus.Failed;
    public bool IsCompleted => _status == TransferStatus.Completed;

    public long TotalSize
    {
        get => _totalSize;
        private set => this.RaiseAndSetIfChanged(ref _totalSize, value);
    }

    public long TransferredBytes
    {
        get => _transferredBytes;
        private set => this.RaiseAndSetIfChanged(ref _transferredBytes, value);
    }

    public void UpdateProgress(TransferProgress progress)
    {
        Progress = progress.Percentage;
        TransferredBytes = progress.BytesTransferred;
        TotalSize = progress.TotalBytes;
        Speed = FormatSpeed(progress.SpeedBytesPerSecond);
        TimeRemaining = FormatTimeRemaining(progress.EstimatedTimeRemaining);
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0 B/s";

        string[] units = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
        int i = (int)Math.Floor(Math.Log(bytesPerSecond, 1024));
        i = Math.Min(i, units.Length - 1);

        double value = bytesPerSecond / Math.Pow(1024, i);
        return i == 0
            ? $"{(int)value} {units[0]}"
            : $"{value:F1} {units[i]}";
    }

    public static string FormatTimeRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return string.Empty;
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        if (remaining.TotalMinutes >= 1) return $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s";
        return $"{remaining.Seconds}s";
    }
}
