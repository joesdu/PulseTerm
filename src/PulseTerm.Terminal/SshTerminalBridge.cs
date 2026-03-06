using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using PulseTerm.Core.Ssh;

namespace PulseTerm.Terminal;

public class SshTerminalBridge : IDisposable
{
    private readonly ITerminalEmulator _terminal;
    private readonly IShellStreamWrapper _shellStream;
    private readonly CancellationTokenSource _cts;
    private Task? _readTask;
    private volatile bool _disposed;
    private int _started;

    public event Action<Exception>? Error;

    public SshTerminalBridge(ITerminalEmulator terminal, IShellStreamWrapper shellStream)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _shellStream = shellStream ?? throw new ArgumentNullException(nameof(shellStream));
        _cts = new CancellationTokenSource();

        _terminal.UserInput += OnUserInput;
    }

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("Bridge already started");

        _readTask = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];

        try
        {
            while (!_cts.Token.IsCancellationRequested && _shellStream.CanRead)
            {
                var bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, _cts.Token).ConfigureAwait(false);

                if (bytesRead == 0)
                    break;

                var data = new byte[bytesRead];
                Array.Copy(buffer, data, bytesRead);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _terminal.Feed(data);
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown — not an error
        }
        catch (ObjectDisposedException)
        {
            // Stream disposed during shutdown — not an error
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    private void OnUserInput(byte[] data)
    {
        if (_disposed || !_shellStream.CanWrite)
            return;

        // Fire-and-forget with error handling — this is an event handler so async void is acceptable
        _ = WriteUserInputAsync(data);
    }

    private async Task WriteUserInputAsync(byte[] data)
    {
        try
        {
            await _shellStream.WriteAsync(data, 0, data.Length, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Stream disposed — expected during teardown
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _terminal.UserInput -= OnUserInput;

        _cts.Cancel();

        try
        {
            _readTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Swallow faults from read task during dispose
        }

        _cts.Dispose();
        _shellStream.Dispose();
    }
}
