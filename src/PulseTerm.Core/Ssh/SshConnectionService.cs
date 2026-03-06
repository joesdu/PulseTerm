using System.Collections.Concurrent;
using DynamicData;
using Microsoft.Extensions.Logging;
using PulseTerm.Core.Models;

namespace PulseTerm.Core.Ssh;

public class SshConnectionService : ISshConnectionService
{
    private readonly ILogger<SshConnectionService>? _logger;
    private readonly Func<Models.ConnectionInfo, ISshClientWrapper> _clientFactory;
    private readonly SourceList<SshSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, ISshClientWrapper> _clients = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    public SshConnectionService(
        Func<Models.ConnectionInfo, ISshClientWrapper> clientFactory,
        ILogger<SshConnectionService>? logger = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _logger = logger;
    }

    public IObservableList<SshSession> Sessions => _sessions.AsObservableList();

    public async Task<SshSession> ConnectAsync(Models.ConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ConnectInternalAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task<SshSession> ConnectInternalAsync(Models.ConnectionInfo connectionInfo, CancellationToken cancellationToken)
    {
        var session = new SshSession
        {
            ConnectionInfo = connectionInfo,
            Status = SessionStatus.Connecting
        };

        _sessions.Add(session);

        ISshClientWrapper? client = null;
        try
        {
            client = _clientFactory(connectionInfo);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            if (!client.IsConnected)
            {
                client.Dispose();
                client = null;
                throw new InvalidOperationException("Client connection failed without exception");
            }

            _clients[session.SessionId] = client;

            session.Status = SessionStatus.Connected;
            session.ConnectedAt = DateTime.UtcNow;

            _logger?.LogInformation("SSH session {SessionId} connected to {Host}:{Port}",
                session.SessionId, connectionInfo.Host, connectionInfo.Port);

            return session;
        }
        catch (OperationCanceledException)
        {
            client?.Dispose();

            session.Status = SessionStatus.Error;
            session.ErrorMessage = $"Connection to {connectionInfo.Host}:{connectionInfo.Port} timed out. Please check the host and port, then retry.";
            _sessions.Remove(session);

            _logger?.LogWarning("SSH session {SessionId} to {Host}:{Port} timed out or was cancelled",
                session.SessionId, connectionInfo.Host, connectionInfo.Port);

            throw new TimeoutException(session.ErrorMessage);
        }
        catch (Exception ex)
        {
            client?.Dispose();

            session.Status = SessionStatus.Error;
            session.ErrorMessage = ex.Message;
            _sessions.Remove(session);

            _logger?.LogError(ex, "Failed to connect SSH session {SessionId} to {Host}:{Port}",
                session.SessionId, connectionInfo.Host, connectionInfo.Port);

            throw;
        }
    }

    public async Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = GetSession(sessionId);
            if (session == null)
            {
                throw new InvalidOperationException($"Session {sessionId} not found");
            }

            if (session.Status == SessionStatus.Disconnected)
            {
                return;
            }

            if (_clients.TryRemove(sessionId, out var client))
            {
                await Task.Run(() =>
                {
                    client.Disconnect();
                    client.Dispose();
                }, cancellationToken).ConfigureAwait(false);
            }

            session.Status = SessionStatus.Disconnected;

            _logger?.LogInformation("SSH session {SessionId} disconnected", sessionId);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public SshSession? GetSession(Guid sessionId)
    {
        return _sessions.Items.FirstOrDefault(s => s.SessionId == sessionId);
    }

    public ISshClientWrapper? GetClient(Guid sessionId)
    {
        _clients.TryGetValue(sessionId, out var client);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        var clientEntries = _clients.ToArray();
        _clients.Clear();

        foreach (var (sessionId, client) in clientEntries)
        {
            try
            {
                if (client.IsConnected)
                {
                    await Task.Run(() => client.Disconnect()).ConfigureAwait(false);
                }

                client.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error disposing SSH client for session {SessionId}", sessionId);
            }
        }

        _sessions.Dispose();
        GC.SuppressFinalize(this);
    }
}
