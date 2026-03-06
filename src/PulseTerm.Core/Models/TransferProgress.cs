namespace PulseTerm.Core.Models;

public class TransferProgress
{
    public required string FileName { get; init; }
    public required long BytesTransferred { get; init; }
    public required long TotalBytes { get; init; }
    public required int Percentage { get; init; }
    public required double SpeedBytesPerSecond { get; init; }
    public required TimeSpan EstimatedTimeRemaining { get; init; }
}
