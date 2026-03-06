using PulseTerm.Core.Data;
using PulseTerm.Core.Models;

namespace PulseTerm.Core.Ssh;

public class HostKeyService : IHostKeyService
{
    private readonly JsonDataStore _dataStore;
    private readonly string _dataPath;
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public HostKeyService(JsonDataStore dataStore, string? dataPath = null)
    {
        _dataStore = dataStore;

        if (string.IsNullOrEmpty(dataPath))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _dataPath = Path.Combine(userProfile, ".pulseterm", "known_hosts.json");
        }
        else
        {
            _dataPath = dataPath;
        }
    }

    public async Task<HostKeyVerification> VerifyHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default)
    {
        var data = await LoadDataAsync(cancellationToken).ConfigureAwait(false);
        var existing = data.Hosts.Find(h => h.Host == host && h.Port == port);

        if (existing is null)
        {
            return HostKeyVerification.Unknown;
        }

        return existing.Fingerprint == fingerprint
            ? HostKeyVerification.Trusted
            : HostKeyVerification.Changed;
    }

    public async Task TrustHostKeyAsync(string host, int port, string keyType, string fingerprint, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataAsync(cancellationToken).ConfigureAwait(false);
            var existing = data.Hosts.Find(h => h.Host == host && h.Port == port);

            if (existing is not null)
            {
                existing.Fingerprint = fingerprint;
                existing.KeyType = keyType;
                existing.LastSeenAt = DateTime.UtcNow;
            }
            else
            {
                data.Hosts.Add(new KnownHost
                {
                    Host = host,
                    Port = port,
                    KeyType = keyType,
                    Fingerprint = fingerprint,
                    FirstSeenAt = DateTime.UtcNow,
                    LastSeenAt = DateTime.UtcNow
                });
            }

            await _dataStore.SaveAsync(_dataPath, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<List<KnownHost>> GetKnownHostsAsync(CancellationToken cancellationToken = default)
    {
        var data = await LoadDataAsync(cancellationToken).ConfigureAwait(false);
        return data.Hosts;
    }

    public async Task RemoveKnownHostAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadDataAsync(cancellationToken).ConfigureAwait(false);
            data.Hosts.RemoveAll(h => h.Host == host && h.Port == port);
            await _dataStore.SaveAsync(_dataPath, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<KnownHostData> LoadDataAsync(CancellationToken cancellationToken = default)
    {
        return await _dataStore.LoadAsync<KnownHostData>(_dataPath, cancellationToken).ConfigureAwait(false) ?? new KnownHostData();
    }
}

internal class KnownHostData
{
    public List<KnownHost> Hosts { get; set; } = new();
}
