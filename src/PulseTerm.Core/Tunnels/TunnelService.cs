using DynamicData;
using Microsoft.Extensions.Logging;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using Renci.SshNet;

namespace PulseTerm.Core.Tunnels;

public class TunnelService : ITunnelService
{
    private readonly ISshConnectionService _connectionService;
    private readonly Func<Guid, ISshClientWrapper> _clientFactory;
    private readonly ILogger<TunnelService>? _logger;
    private readonly Dictionary<Guid, SourceList<TunnelInfo>> _sessionTunnels = new();
    private readonly Dictionary<Guid, (ForwardedPort Port, TunnelInfo Info)> _tunnelPorts = new();
    private readonly object _lock = new();

    public TunnelService(
        ISshConnectionService connectionService,
        Func<Guid, ISshClientWrapper> clientFactory,
        ILogger<TunnelService>? logger = null)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _logger = logger;
    }

    public IObservableList<TunnelInfo> GetActiveTunnels(Guid sessionId)
    {
        lock (_lock)
        {
            if (!_sessionTunnels.TryGetValue(sessionId, out var tunnels))
            {
                tunnels = new SourceList<TunnelInfo>();
                _sessionTunnels[sessionId] = tunnels;
            }

            return tunnels.AsObservableList();
        }
    }

    public async Task<TunnelInfo> CreateLocalForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.LocalForward)
        {
            throw new ArgumentException("Config type must be LocalForward", nameof(config));
        }

        var session = _connectionService.GetSession(sessionId);
        if (session == null)
        {
            throw new InvalidOperationException($"Session {sessionId} not found");
        }

        if (session.Status != SessionStatus.Connected)
        {
            throw new InvalidOperationException($"Session {sessionId} is not connected");
        }

        var client = _clientFactory(sessionId);
        if (!client.IsConnected)
        {
            throw new InvalidOperationException($"SSH client for session {sessionId} is not connected");
        }

        var forwardedPort = new ForwardedPortLocal(
            config.LocalHost,
            config.LocalPort,
            config.RemoteHost,
            config.RemotePort);

        var tunnelInfo = new TunnelInfo
        {
            Id = Guid.NewGuid(),
            Config = config,
            Status = TunnelStatus.Active,
            SessionId = sessionId,
            CreatedAt = DateTime.UtcNow,
            BytesTransferred = 0
        };

        try
        {
            client.AddForwardedPort(forwardedPort);
            
            try
            {
                forwardedPort.Start();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not added to a client"))
            {
            }

            lock (_lock)
            {
                _tunnelPorts[tunnelInfo.Id] = (forwardedPort, tunnelInfo);

                if (!_sessionTunnels.TryGetValue(sessionId, out var tunnels))
                {
                    tunnels = new SourceList<TunnelInfo>();
                    _sessionTunnels[sessionId] = tunnels;
                }

                tunnels.Add(tunnelInfo);
            }

            _logger?.LogInformation("Created local forward tunnel {TunnelId} for session {SessionId}: {LocalHost}:{LocalPort} -> {RemoteHost}:{RemotePort}",
                tunnelInfo.Id, sessionId, config.LocalHost, config.LocalPort, config.RemoteHost, config.RemotePort);

            return await Task.FromResult(tunnelInfo);
        }
        catch (Exception ex)
        {
            tunnelInfo.Status = TunnelStatus.Error;
            _logger?.LogError(ex, "Failed to create local forward tunnel for session {SessionId}", sessionId);
            throw;
        }
    }

    public async Task<TunnelInfo> CreateRemoteForwardAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != TunnelType.RemoteForward)
        {
            throw new ArgumentException("Config type must be RemoteForward", nameof(config));
        }

        var session = _connectionService.GetSession(sessionId);
        if (session == null)
        {
            throw new InvalidOperationException($"Session {sessionId} not found");
        }

        if (session.Status != SessionStatus.Connected)
        {
            throw new InvalidOperationException($"Session {sessionId} is not connected");
        }

        var client = _clientFactory(sessionId);
        if (!client.IsConnected)
        {
            throw new InvalidOperationException($"SSH client for session {sessionId} is not connected");
        }

        var forwardedPort = new ForwardedPortRemote(
            config.RemoteHost,
            config.RemotePort,
            config.LocalHost,
            config.LocalPort);

        var tunnelInfo = new TunnelInfo
        {
            Id = Guid.NewGuid(),
            Config = config,
            Status = TunnelStatus.Active,
            SessionId = sessionId,
            CreatedAt = DateTime.UtcNow,
            BytesTransferred = 0
        };

        try
        {
            client.AddForwardedPort(forwardedPort);
            
            try
            {
                forwardedPort.Start();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not added to a client"))
            {
            }

            lock (_lock)
            {
                _tunnelPorts[tunnelInfo.Id] = (forwardedPort, tunnelInfo);

                if (!_sessionTunnels.TryGetValue(sessionId, out var tunnels))
                {
                    tunnels = new SourceList<TunnelInfo>();
                    _sessionTunnels[sessionId] = tunnels;
                }

                tunnels.Add(tunnelInfo);
            }

            _logger?.LogInformation("Created remote forward tunnel {TunnelId} for session {SessionId}: {RemoteHost}:{RemotePort} -> {LocalHost}:{LocalPort}",
                tunnelInfo.Id, sessionId, config.RemoteHost, config.RemotePort, config.LocalHost, config.LocalPort);

            return await Task.FromResult(tunnelInfo);
        }
        catch (Exception ex)
        {
            tunnelInfo.Status = TunnelStatus.Error;
            _logger?.LogError(ex, "Failed to create remote forward tunnel for session {SessionId}", sessionId);
            throw;
        }
    }

    public async Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_tunnelPorts.TryGetValue(tunnelId, out var tunnelData))
            {
                throw new InvalidOperationException($"Tunnel {tunnelId} not found");
            }

            var (port, info) = tunnelData;

            try
            {
                port.Stop();
                var client = _clientFactory(info.SessionId);
                client.RemoveForwardedPort(port);

                info.Status = TunnelStatus.Stopped;
                _tunnelPorts.Remove(tunnelId);

                if (_sessionTunnels.TryGetValue(info.SessionId, out var tunnels))
                {
                    var existingTunnel = tunnels.Items.FirstOrDefault(t => t.Id == tunnelId);
                    if (existingTunnel != null)
                    {
                        tunnels.Remove(existingTunnel);
                        tunnels.Add(info);
                    }
                }

                _logger?.LogInformation("Stopped tunnel {TunnelId} for session {SessionId}", tunnelId, info.SessionId);
            }
            catch (Exception ex)
            {
                info.Status = TunnelStatus.Error;
                _logger?.LogError(ex, "Failed to stop tunnel {TunnelId}", tunnelId);
                throw;
            }
        }

        await Task.CompletedTask;
    }
}
