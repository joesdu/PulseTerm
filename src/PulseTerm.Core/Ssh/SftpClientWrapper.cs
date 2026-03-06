using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace PulseTerm.Core.Ssh;

public class SftpClientWrapper : ISftpClientWrapper
{
    private readonly SftpClient _client;
    private bool _disposed;

    public SftpClientWrapper(SftpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public bool IsConnected
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client.IsConnected;
        }
    }

    public TimeSpan ConnectionTimeout
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client.ConnectionInfo.Timeout;
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _client.ConnectionInfo.Timeout = value;
        }
    }

    public string WorkingDirectory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _client.WorkingDirectory;
        }
    }

    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.Connect();
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _client.ConnectAsync(cancellationToken);
    }

    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.Disconnect();
    }

    public IEnumerable<ISftpFile> ListDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _client.ListDirectory(path);
    }

    public Task<IEnumerable<ISftpFile>> ListDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => _client.ListDirectory(path), cancellationToken);
    }

    public void UploadFile(Stream input, string path, bool canOverride = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.UploadFile(input, path, canOverride);
    }

    public Task UploadAsync(Stream input, string path, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => _client.UploadFile(input, path, true, uploadCallback), cancellationToken);
    }

    public void DownloadFile(string path, Stream output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.DownloadFile(path, output);
    }

    public Task DownloadAsync(string path, Stream output, Action<ulong>? downloadCallback = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(() => _client.DownloadFile(path, output, downloadCallback), cancellationToken);
    }

    public void DeleteFile(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.DeleteFile(path);
    }

    public void DeleteDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.DeleteDirectory(path);
    }

    public void CreateDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _client.CreateDirectory(path);
    }

    public bool Exists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _client.Exists(path);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _client.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
