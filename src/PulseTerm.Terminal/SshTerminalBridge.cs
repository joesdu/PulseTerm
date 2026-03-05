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
    private readonly Utf8StreamDecoder _decoder;
    private readonly CancellationTokenSource _cts;
    private Task? _readTask;
    private bool _disposed;

    public SshTerminalBridge(ITerminalEmulator terminal, IShellStreamWrapper shellStream)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _shellStream = shellStream ?? throw new ArgumentNullException(nameof(shellStream));
        _decoder = new Utf8StreamDecoder();
        _cts = new CancellationTokenSource();

        _terminal.UserInput += OnUserInput;
    }

    public void Start()
    {
        if (_readTask != null)
            throw new InvalidOperationException("Bridge already started");

        _readTask = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];

        try
        {
            while (!_cts.Token.IsCancellationRequested && _shellStream.CanWrite)
            {
                var bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);

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
        }
        catch (Exception)
        {
        }
    }

    private void OnUserInput(byte[] data)
    {
        if (_disposed || !_shellStream.CanWrite)
            return;

        try
        {
            _shellStream.WriteAsync(data, 0, data.Length, CancellationToken.None).Wait();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _terminal.UserInput -= OnUserInput;

        _cts.Cancel();

        _readTask?.Wait(TimeSpan.FromSeconds(2));

        _cts.Dispose();
    }
}
