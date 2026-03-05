using System.Diagnostics;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using Renci.SshNet.Sftp;

namespace PulseTerm.Core.Sftp;

public class SftpService : ISftpService
{
    private readonly ISshConnectionService _connectionService;
    private readonly Dictionary<Guid, ISftpClientWrapper> _sftpClients = new();
    private readonly Func<ISftpClientWrapper>? _sftpClientFactory;
    private const int BufferSize = 256 * 1024;

    public SftpService(ISshConnectionService connectionService, Func<ISftpClientWrapper>? sftpClientFactory = null)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _sftpClientFactory = sftpClientFactory;
    }

    public async Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken);
        var files = await client.ListDirectoryAsync(path, cancellationToken);

        return files
            .Where(f => f.Name != "." && f.Name != "..")
            .Select(f => new RemoteFileInfo
            {
                Name = f.Name,
                FullPath = f.FullName,
                Size = f.Length,
                Permissions = FormatPermissions(f),
                IsDirectory = f.IsDirectory,
                LastModified = f.LastWriteTime,
                Owner = f.OwnerCanRead.ToString(),
                Group = f.GroupCanRead.ToString()
            })
            .ToList();
    }

    public async Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, 
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken);
        var fileInfo = new FileInfo(localPath);
        var totalBytes = fileInfo.Length;
        var fileName = Path.GetFileName(localPath);

        var stopwatch = Stopwatch.StartNew();
        long lastBytesTransferred = 0;

        await using var fileStream = File.OpenRead(localPath);
        
        await client.UploadAsync(fileStream, remotePath, bytesTransferred =>
        {
            if (progress != null)
            {
                var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                var speed = elapsedSeconds > 0 ? (long)bytesTransferred / elapsedSeconds : 0;
                var remainingBytes = totalBytes - (long)bytesTransferred;
                var estimatedTimeRemaining = speed > 0 
                    ? TimeSpan.FromSeconds(remainingBytes / speed) 
                    : TimeSpan.Zero;

                var transferProgress = new TransferProgress
                {
                    FileName = fileName,
                    BytesTransferred = (long)bytesTransferred,
                    TotalBytes = totalBytes,
                    Percentage = totalBytes > 0 ? (int)(bytesTransferred * 100 / (ulong)totalBytes) : 0,
                    SpeedBytesPerSecond = speed,
                    EstimatedTimeRemaining = estimatedTimeRemaining
                };

                progress.Report(transferProgress);
                lastBytesTransferred = (long)bytesTransferred;
            }
        }, cancellationToken);
    }

    public async Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, 
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken);
        var fileName = Path.GetFileName(remotePath);

        var fileInfo = await GetFileInfoAsync(sessionId, remotePath, cancellationToken);
        var totalBytes = fileInfo.Size;

        var stopwatch = Stopwatch.StartNew();

        await using var fileStream = File.Create(localPath);
        
        await client.DownloadAsync(remotePath, fileStream, bytesTransferred =>
        {
            if (progress != null)
            {
                var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                var speed = elapsedSeconds > 0 ? (long)bytesTransferred / elapsedSeconds : 0;
                var remainingBytes = totalBytes - (long)bytesTransferred;
                var estimatedTimeRemaining = speed > 0 
                    ? TimeSpan.FromSeconds(remainingBytes / speed) 
                    : TimeSpan.Zero;

                var transferProgress = new TransferProgress
                {
                    FileName = fileName,
                    BytesTransferred = (long)bytesTransferred,
                    TotalBytes = totalBytes,
                    Percentage = totalBytes > 0 ? (int)(bytesTransferred * 100 / (ulong)totalBytes) : 0,
                    SpeedBytesPerSecond = speed,
                    EstimatedTimeRemaining = estimatedTimeRemaining
                };

                progress.Report(transferProgress);
            }
        }, cancellationToken);
    }

    public async Task DeleteAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken);
        await Task.Run(() =>
        {
            var files = client.ListDirectory(remotePath).ToList();
            if (files.Any() && files.First().IsDirectory)
            {
                throw new InvalidOperationException("Cannot delete directories with DeleteAsync. Use recursive delete or remove directory method.");
            }
        }, cancellationToken);
    }

    public async Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken);
        await Task.Run(() =>
        {
        }, cancellationToken);
    }

    public async Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken);
        var parentDir = Path.GetDirectoryName(remotePath)?.Replace("\\", "/") ?? "/";
        var fileName = Path.GetFileName(remotePath);

        var files = await client.ListDirectoryAsync(parentDir, cancellationToken);
        var file = files.FirstOrDefault(f => f.Name == fileName);

        if (file == null)
        {
            throw new FileNotFoundException($"File not found: {remotePath}");
        }

        return new RemoteFileInfo
        {
            Name = file.Name,
            FullPath = file.FullName,
            Size = file.Length,
            Permissions = FormatPermissions(file),
            IsDirectory = file.IsDirectory,
            LastModified = file.LastWriteTime,
            Owner = file.OwnerCanRead.ToString(),
            Group = file.GroupCanRead.ToString()
        };
    }

    private async Task<ISftpClientWrapper> GetOrCreateSftpClientAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_sftpClients.TryGetValue(sessionId, out var existingClient))
        {
            if (existingClient.IsConnected)
            {
                return existingClient;
            }
        }

        var session = _connectionService.GetSession(sessionId);
        if (session == null)
        {
            throw new InvalidOperationException($"Session {sessionId} not found");
        }

        if (session.Status != SessionStatus.Connected)
        {
            throw new InvalidOperationException($"Session {sessionId} is not connected");
        }

        if (_sftpClientFactory == null)
        {
            throw new InvalidOperationException("SFTP client factory not configured");
        }

        var client = _sftpClientFactory();
        await client.ConnectAsync(cancellationToken);

        _sftpClients[sessionId] = client;
        return client;
    }

    private string FormatPermissions(ISftpFile file)
    {
        var perms = file.IsDirectory ? "d" : "-";
        
        perms += file.OwnerCanRead ? "r" : "-";
        perms += file.OwnerCanWrite ? "w" : "-";
        perms += file.OwnerCanExecute ? "x" : "-";
        
        perms += file.GroupCanRead ? "r" : "-";
        perms += file.GroupCanWrite ? "w" : "-";
        perms += file.GroupCanExecute ? "x" : "-";
        
        perms += file.OthersCanRead ? "r" : "-";
        perms += file.OthersCanWrite ? "w" : "-";
        perms += file.OthersCanExecute ? "x" : "-";

        return perms;
    }
}
