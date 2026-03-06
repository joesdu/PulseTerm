using DynamicData;
using PulseTerm.Core.Models;

namespace PulseTerm.Core.Ssh;

public interface ISshConnectionService : IAsyncDisposable
{
    IObservableList<SshSession> Sessions { get; }

    Task<SshSession> ConnectAsync(Models.ConnectionInfo connectionInfo, CancellationToken cancellationToken = default);
    Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default);
    SshSession? GetSession(Guid sessionId);
    ISshClientWrapper? GetClient(Guid sessionId);
}
