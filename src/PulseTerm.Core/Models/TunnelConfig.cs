namespace PulseTerm.Core.Models;

public sealed class TunnelConfig
{
    public required TunnelType Type { get; init; }
    public required string Name { get; init; }
    public required string LocalHost { get; init; }
    public required uint LocalPort { get; init; }
    public required string RemoteHost { get; init; }
    public required uint RemotePort { get; init; }
}
