using Renci.SshNet;
using Renci.SshNet.Common;

namespace PulseTerm.Core.Ssh;

public class SshClientWrapper : ISshClientWrapper
{
    private readonly SshClient _client;

    public SshClientWrapper(SshClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool IsConnected => _client.IsConnected;

    public TimeSpan ConnectionTimeout
    {
        get => _client.ConnectionInfo.Timeout;
        set => _client.ConnectionInfo.Timeout = value;
    }

    public void Connect()
    {
        _client.Connect();
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        return _client.ConnectAsync(cancellationToken);
    }

    public void Disconnect()
    {
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
        _client.AddForwardedPort(port);
    }

    public void RemoveForwardedPort(ForwardedPort port)
    {
        _client.RemoveForwardedPort(port);
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}
