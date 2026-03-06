using Renci.SshNet;

namespace PulseTerm.Core.Ssh;

public class ShellStreamWrapper : IShellStreamWrapper
{
    private readonly ShellStream _stream;

    public ShellStreamWrapper(ShellStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public bool DataAvailable => _stream.DataAvailable;

    public bool CanRead => _stream.CanRead;

    public bool CanWrite => _stream.CanWrite;

    public string? Expect(string regex, TimeSpan timeout)
    {
        return _stream.Expect(regex, timeout);
    }

    public void WriteLine(string line)
    {
        _stream.WriteLine(line);
    }

    public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _stream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return _stream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public void Flush()
    {
        _stream.Flush();
    }

    public void Dispose()
    {
        _stream?.Dispose();
    }
}
