using PulseTerm.Core.Models;

namespace PulseTerm.Core.Ssh;

public interface IHostKeyService
{
    Task<HostKeyVerification> VerifyHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default);
    
    Task TrustHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default);
    
    Task<List<KnownHost>> GetKnownHostsAsync(CancellationToken cancellationToken = default);
    
    Task RemoveKnownHostAsync(string host, int port, CancellationToken cancellationToken = default);
}
