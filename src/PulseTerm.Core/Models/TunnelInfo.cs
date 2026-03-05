namespace PulseTerm.Core.Models;

public sealed class TunnelInfo
{
    public required Guid Id { get; init; }
    public required TunnelConfig Config { get; init; }
    public required TunnelStatus Status { get; set; }
    public required Guid SessionId { get; init; }
    public required DateTime CreatedAt { get; init; }
    public long BytesTransferred { get; set; }
}
