using Renci.SshNet;
using Renci.SshNet.Common;

namespace PulseTerm.Core.Ssh;

public class SshClientWrapper : ISshClientWrapper
{
    private readonly SshClient _client;
    private bool _disposed;

    public SshClientWrapper(SshClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool IsConnected
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client.IsConnected;
        }
    }

    public TimeSpan ConnectionTimeout
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client.ConnectionInfo.Timeout;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _client.ConnectionInfo.Timeout = value;
        }
    }

    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.Connect();
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _client.ConnectAsync(cancellationToken);
    }

    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.Disconnect();
    }

    public IShellStreamWrapper CreateShellStream(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IDictionary<TerminalModes, uint>? terminalModeValues = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var shellStream = _client.CreateShellStream(
            terminalName,
            columns,
            rows,
            width,
            height,
            bufferSize,
            terminalModeValues);

        return new ShellStreamWrapper(shellStream);
    }

    public void AddForwardedPort(ForwardedPort port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.AddForwardedPort(port);
    }

    public void RemoveForwardedPort(ForwardedPort port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.RemoveForwardedPort(port);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _client.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
