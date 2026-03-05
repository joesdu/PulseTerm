namespace PulseTerm.Core.Models;

public class RemoteFileInfo
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required long Size { get; init; }
    public required string Permissions { get; init; }
    public required bool IsDirectory { get; init; }
    public required DateTime LastModified { get; init; }
    public required string Owner { get; init; }
    public required string Group { get; init; }
}
