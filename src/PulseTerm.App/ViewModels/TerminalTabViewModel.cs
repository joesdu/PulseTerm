using System;
using System.Reactive;
using PulseTerm.Core.Models;
using PulseTerm.Core.Resources;
using PulseTerm.Core.Ssh;
using PulseTerm.Terminal;
using ReactiveUI;

namespace PulseTerm.App.ViewModels;

public class TerminalTabViewModel : ReactiveObject, IDisposable
{
    private string _title;
    private SessionStatus _connectionStatus;
    private TimeSpan? _latency;
    private bool _isConnected;
    private int _reconnectAttempts;
    private bool _disposed;

    public TerminalTabViewModel(ITerminalEmulator terminalEmulator, IShellStreamWrapper shellStream)
    {
        TerminalEmulator = terminalEmulator ?? throw new ArgumentNullException(nameof(terminalEmulator));
        ShellStream = shellStream ?? throw new ArgumentNullException(nameof(shellStream));

        _title = Strings.NewTab;
        _connectionStatus = SessionStatus.Disconnected;

        Bridge = new SshTerminalBridge(terminalEmulator, shellStream);

        SearchCommand = ReactiveCommand.Create(() => { });
        CopyCommand = ReactiveCommand.Create(() => { });
        SplitCommand = ReactiveCommand.Create(() => { });
        ToggleBroadcastCommand = ReactiveCommand.Create(() => { });
        OpenTunnelCommand = ReactiveCommand.Create(() => { });
        OpenQuickCommandsCommand = ReactiveCommand.Create(() => { });
    }

    public Guid Id { get; } = Guid.NewGuid();

    public ITerminalEmulator TerminalEmulator { get; }

    public IShellStreamWrapper ShellStream { get; }

    public SshTerminalBridge Bridge { get; }

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public SessionStatus ConnectionStatus
    {
        get => _connectionStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _connectionStatus, value);
            IsConnected = value == SessionStatus.Connected;
        }
    }

    public TimeSpan? Latency
    {
        get => _latency;
        set => this.RaiseAndSetIfChanged(ref _latency, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public int ReconnectAttempts
    {
        get => _reconnectAttempts;
        private set => this.RaiseAndSetIfChanged(ref _reconnectAttempts, value);
    }

    public int MaxReconnectAttempts => 3;

    public bool CanReconnect => ReconnectAttempts < MaxReconnectAttempts;

    public ReactiveCommand<Unit, Unit> SearchCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> SplitCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleBroadcastCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenTunnelCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenQuickCommandsCommand { get; }

    public void IncrementReconnectAttempt()
    {
        ReconnectAttempts++;
    }

    public void ResetReconnectAttempts()
    {
        ReconnectAttempts = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        Bridge.Dispose();
        TerminalEmulator.Dispose();
    }
}
