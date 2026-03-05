namespace PulseTerm.Core.Models;

public class TransferTask
{
    public required Guid Id { get; init; }
    public required TransferType Type { get; init; }
    public required string LocalPath { get; init; }
    public required string RemotePath { get; init; }
    public required TransferStatus Status { get; set; }
    public TransferProgress? Progress { get; set; }
}
