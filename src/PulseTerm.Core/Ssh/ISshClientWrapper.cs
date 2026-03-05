using Renci.SshNet;
using Renci.SshNet.Common;

namespace PulseTerm.Core.Ssh;

public interface ISshClientWrapper : IDisposable
{
    bool IsConnected { get; }
    TimeSpan ConnectionTimeout { get; set; }

    void Connect();
    Task ConnectAsync(CancellationToken cancellationToken);
    void Disconnect();

    IShellStreamWrapper CreateShellStream(
        string terminalName,
        uint columns,
        uint rows,
        uint width,
        uint height,
        int bufferSize,
        IDictionary<TerminalModes, uint>? terminalModeValues = null);

    void AddForwardedPort(ForwardedPort port);
    void RemoveForwardedPort(ForwardedPort port);
}
