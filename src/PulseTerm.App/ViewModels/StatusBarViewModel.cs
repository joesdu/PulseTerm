using System;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using PulseTerm.Core.Resources;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class StatusBarViewModel : ReactiveObject, IDisposable
{
    private readonly IScheduler _scheduler;
    private readonly CompositeDisposable _disposables = new();

    private string _statusText;
    private string _connectionInfo;
    private string _status;
    private string _latency;
    private string _terminalType;
    private string _windowSize;
    private string _encoding;
    private string _uptime;
    private bool _isConnected;

    private IDisposable? _uptimeSubscription;
    private DateTimeOffset _uptimeStart;

    public StatusBarViewModel()
        : this(DefaultScheduler.Instance)
    {
    }

    public StatusBarViewModel(IScheduler scheduler)
    {
        _scheduler = scheduler;
        _statusText = Strings.Ready;
        _connectionInfo = string.Empty;
        _status = Strings.Disconnected;
        _latency = string.Empty;
        _terminalType = "xterm-256color";
        _windowSize = "80\u00D724";
        _encoding = "UTF-8";
        _uptime = string.Empty;
        _isConnected = false;
    }

    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public string ConnectionInfo
    {
        get => _connectionInfo;
        set => this.RaiseAndSetIfChanged(ref _connectionInfo, value);
    }

    public string Status
    {
        get => _status;
        set
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            IsConnected = value == Strings.Connected;
        }
    }

    public string Latency
    {
        get => _latency;
        set => this.RaiseAndSetIfChanged(ref _latency, value);
    }

    public string TerminalType
    {
        get => _terminalType;
        set => this.RaiseAndSetIfChanged(ref _terminalType, value);
    }

    public string WindowSize
    {
        get => _windowSize;
        set => this.RaiseAndSetIfChanged(ref _windowSize, value);
    }

    public string Encoding
    {
        get => _encoding;
        set => this.RaiseAndSetIfChanged(ref _encoding, value);
    }

    public string Uptime
    {
        get => _uptime;
        set => this.RaiseAndSetIfChanged(ref _uptime, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public void StartUptimeTimer()
    {
        StopUptimeTimer();
        _uptimeStart = _scheduler.Now;

        _uptimeSubscription = Observable
            .Interval(TimeSpan.FromSeconds(1), _scheduler)
            .Subscribe(_ =>
            {
                var elapsed = _scheduler.Now - _uptimeStart;
                Uptime = elapsed.ToString(@"hh\:mm\:ss");
            });

        _disposables.Add(_uptimeSubscription);
    }

    public void StopUptimeTimer()
    {
        _uptimeSubscription?.Dispose();
        _uptimeSubscription = null;
    }

    public void ResetUptime()
    {
        StopUptimeTimer();
        Uptime = string.Empty;
        StartUptimeTimer();
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _uptimeSubscription = null;
    }
}
