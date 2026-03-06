using Renci.SshNet.Sftp;

namespace PulseTerm.Core.Ssh;

public interface ISftpClientWrapper : IDisposable
{
    bool IsConnected { get; }
    TimeSpan ConnectionTimeout { get; set; }
    string WorkingDirectory { get; }

    void Connect();
    Task ConnectAsync(CancellationToken cancellationToken);
    void Disconnect();

    IEnumerable<ISftpFile> ListDirectory(string path);
    Task<IEnumerable<ISftpFile>> ListDirectoryAsync(string path, CancellationToken cancellationToken);

    void UploadFile(Stream input, string path, bool canOverride = true);
    Task UploadAsync(Stream input, string path, Action<ulong>? uploadCallback = null, CancellationToken cancellationToken = default);

    void DownloadFile(string path, Stream output);
    Task DownloadAsync(string path, Stream output, Action<ulong>? downloadCallback = null, CancellationToken cancellationToken = default);

    void DeleteFile(string path);
    void DeleteDirectory(string path);
    void CreateDirectory(string path);
    bool Exists(string path);
}
