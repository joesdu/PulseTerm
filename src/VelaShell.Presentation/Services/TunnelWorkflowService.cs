using VelaShell.Core.Models;
using VelaShell.Core.Tunnels;

namespace VelaShell.Presentation.Services;

/// <inheritdoc />
public sealed class TunnelWorkflowService(ITunnelService tunnelService) : ITunnelWorkflowService
{
    private readonly ITunnelService _tunnelService = tunnelService ?? throw new ArgumentNullException(nameof(tunnelService));

    /// <inheritdoc />
    public IReadOnlyList<TunnelInfo> GetActiveTunnels(Guid sessionId) => [.. _tunnelService.GetActiveTunnels(sessionId)];

    /// <inheritdoc />
    public void RefreshStatistics() => _tunnelService.RefreshStatistics();

    /// <inheritdoc />
    public Task<TunnelInfo> CreateTunnelAsync(Guid sessionId, TunnelConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Type switch
        {
            TunnelType.LocalForward => _tunnelService.CreateLocalForwardAsync(sessionId, config, cancellationToken),
            TunnelType.RemoteForward => _tunnelService.CreateRemoteForwardAsync(sessionId, config, cancellationToken),
            TunnelType.DynamicForward => _tunnelService.CreateDynamicForwardAsync(sessionId, config, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(config), config.Type, "Unsupported tunnel type.")
        };
    }

    /// <inheritdoc />
    public Task StopTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default) => _tunnelService.StopTunnelAsync(tunnelId, cancellationToken);

    /// <inheritdoc />
    public Task RemoveTunnelAsync(Guid tunnelId, CancellationToken cancellationToken = default) => _tunnelService.RemoveTunnelAsync(tunnelId, cancellationToken);
}
