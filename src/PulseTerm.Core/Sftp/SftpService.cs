using System.Collections.Concurrent;
using System.Diagnostics;
using PulseTerm.Core.Models;
using PulseTerm.Core.Ssh;
using Renci.SshNet.Sftp;

namespace PulseTerm.Core.Sftp;

public class SftpService : ISftpService
{
    private readonly ISshConnectionService _connectionService;
    private readonly ConcurrentDictionary<Guid, ISftpClientWrapper> _sftpClients = new();
    private readonly Func<ISftpClientWrapper>? _sftpClientFactory;

    public SftpService(ISshConnectionService connectionService, Func<ISftpClientWrapper>? sftpClientFactory = null)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _sftpClientFactory = sftpClientFactory;
    }

    public async Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var files = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);

        return files
            .Where(f => f.Name != "." && f.Name != "..")
            .Select(MapToRemoteFileInfo)
            .ToList();
    }

    public async Task UploadFileAsync(Guid sessionId, string localPath, string remotePath, 
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var fileInfo = new FileInfo(localPath);
        var totalBytes = fileInfo.Length;
        var fileName = Path.GetFileName(localPath);

        var stopwatch = Stopwatch.StartNew();

        await using var fileStream = File.OpenRead(localPath);
        
        await client.UploadAsync(fileStream, remotePath, bytesTransferred =>
        {
            ReportProgress(progress, fileName, (long)bytesTransferred, totalBytes, stopwatch);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadFileAsync(Guid sessionId, string remotePath, string localPath, 
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var fileName = GetUnixFileName(remotePath);

        var fileInfo = await GetFileInfoAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
        var totalBytes = fileInfo.Size;

        var stopwatch = Stopwatch.StartNew();

        await using var fileStream = File.Create(localPath);
        
        await client.DownloadAsync(remotePath, fileStream, bytesTransferred =>
        {
            ReportProgress(progress, fileName, (long)bytesTransferred, totalBytes, stopwatch);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            if (!client.Exists(remotePath))
            {
                throw new FileNotFoundException($"Remote path not found: {remotePath}");
            }

            var parentDir = GetUnixParentDirectory(remotePath);
            var name = GetUnixFileName(remotePath);
            var entries = client.ListDirectory(parentDir);
            var entry = entries.FirstOrDefault(f => f.Name == name);

            if (entry != null && entry.IsDirectory)
            {
                client.DeleteDirectory(remotePath);
            }
            else
            {
                client.DeleteFile(remotePath);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            client.CreateDirectory(remotePath);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        var client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var parentDir = GetUnixParentDirectory(remotePath);
        var fileName = GetUnixFileName(remotePath);

        var files = await client.ListDirectoryAsync(parentDir, cancellationToken).ConfigureAwait(false);
        var file = files.FirstOrDefault(f => f.Name == fileName);

        if (file == null)
        {
            throw new FileNotFoundException($"File not found: {remotePath}");
        }

        return MapToRemoteFileInfo(file);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _sftpClients)
        {
            try
            {
                if (kvp.Value.IsConnected)
                {
                    kvp.Value.Disconnect();
                }

                kvp.Value.Dispose();
            }
            catch
            {
                // Best-effort cleanup during disposal
            }
        }

        _sftpClients.Clear();

        await ValueTask.CompletedTask.ConfigureAwait(false);
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
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        _sftpClients[sessionId] = client;
        return client;
    }

    private static RemoteFileInfo MapToRemoteFileInfo(ISftpFile file)
    {
        return new RemoteFileInfo
        {
            Name = file.Name,
            FullPath = file.FullName,
            Size = file.Length,
            Permissions = FormatPermissions(file),
            IsDirectory = file.IsDirectory,
            LastModified = file.LastWriteTime,
            Owner = file.UserId.ToString(),
            Group = file.GroupId.ToString()
        };
    }

    private static void ReportProgress(IProgress<TransferProgress>? progress, string fileName, long bytesTransferred, long totalBytes, Stopwatch stopwatch)
    {
        if (progress == null) return;

        var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        var speed = elapsedSeconds > 0 ? bytesTransferred / elapsedSeconds : 0;
        var remainingBytes = totalBytes - bytesTransferred;
        var estimatedTimeRemaining = speed > 0
            ? TimeSpan.FromSeconds(remainingBytes / speed)
            : TimeSpan.Zero;

        var transferProgress = new TransferProgress
        {
            FileName = fileName,
            BytesTransferred = bytesTransferred,
            TotalBytes = totalBytes,
            Percentage = totalBytes > 0 ? (int)(bytesTransferred * 100 / totalBytes) : 0,
            SpeedBytesPerSecond = speed,
            EstimatedTimeRemaining = estimatedTimeRemaining
        };

        progress.Report(transferProgress);
    }

    private static string GetUnixParentDirectory(string remotePath)
    {
        var lastSlash = remotePath.LastIndexOf('/');
        return lastSlash > 0 ? remotePath[..lastSlash] : "/";
    }

    private static string GetUnixFileName(string remotePath)
    {
        var lastSlash = remotePath.LastIndexOf('/');
        return lastSlash >= 0 ? remotePath[(lastSlash + 1)..] : remotePath;
    }

    private static string FormatPermissions(ISftpFile file)
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
