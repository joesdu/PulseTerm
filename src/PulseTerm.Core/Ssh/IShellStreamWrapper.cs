namespace PulseTerm.Core.Ssh;

public interface IShellStreamWrapper : IDisposable
{
    bool DataAvailable { get; }
    bool CanRead { get; }
    bool CanWrite { get; }

    string? Expect(string regex, TimeSpan timeout);
    void WriteLine(string line);
    Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
    Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
    void Flush();
}
