using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 基于 SSH 会话的 SFTP 文件操作服务:按会话缓存并复用 SFTP 客户端,提供目录浏览、
/// 上传/下载、删除、重命名、权限设置等远端文件系统操作。
/// </summary>
public class SftpService(
    ISshConnectionService connectionService,
    Func<SshSession, ISftpClientWrapper>? sftpClientFactory = null,
    ISettingsService? settingsService = null) : ISftpService
{
    private readonly ISshConnectionService _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
    private readonly ConcurrentDictionary<Guid, ISftpClientWrapper> _sftpClients = new();

    /// <summary>属主/属组的数字 id → 名称翻译(按会话缓存,见 RemoteIdentityResolver)。</summary>
    private readonly RemoteIdentityResolver _identities = new(connectionService);

    /// <summary>SFTP 客户端创建的按会话单飞闸(见 GetOrCreateSftpClientAsync)。</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _clientGates = new();

    /// <summary>列出指定会话下远端目录的条目(自动剔除 "." 与 ".." )。</summary>
    public async Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        IEnumerable<SftpEntry> files = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);

        // 属主/属组名要查远端 passwd 库(SFTP 只报数字 id):每会话查一次,查不到回退数字。
        RemoteIdentityMap identities = await _identities.GetAsync(sessionId).ConfigureAwait(false);
        return [.. files.Where(f => f.Name is not "." and not "..").Select(f => MapToRemoteFileInfo(f, identities))];
    }

    /// <summary>将本地文件上传到远端路径,可选限速与进度回报,支持取消。</summary>
    /// <summary>将本地文件上传到远端路径,可选限速与进度回报,支持断点续传(resumeOffset > 0 时追加上传)。</summary>
    public async Task UploadFileAsync(Guid sessionId,
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var fileInfo = new FileInfo(localPath);
        long totalBytes = fileInfo.Length;
        string fileName = Path.GetFileName(localPath);
        var reporter = new ProgressThrottle(progress, fileName, totalBytes);
        Action<ulong>? onBytes = reporter.IsEnabled ? bytes => reporter.Report((long)bytes) : null;
        (long uploadBps, _, bool preserveTimestamps) = await GetTransferTuningAsync().ConfigureAwait(false);

        // 以此刻的远端状态重新核实续传起点;核实不通过会抛错,核实为"无可续"则整份重传。
        if (resumeOffset > 0)
        {
            resumeOffset = await ResolveUploadResumeAsync(client, remotePath, localPath, totalBytes, resumeOffset, cancellationToken).ConfigureAwait(false);
        }

        // 续传与全新上传只差一个偏移量参数,其余(限速包装、收尾上报)完全一致。
        Stream source = OpenLocalRead(localPath);
        Stream fileStream = uploadBps > 0 ? new ThrottledStream(source, uploadBps) : source;
        try
        {
            if (resumeOffset > 0)
            {
                await client.UploadAsync(fileStream, remotePath, resumeOffset, onBytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.UploadAsync(fileStream, remotePath, onBytes, cancellationToken).ConfigureAwait(false);
            }

            // 节流会丢弃最后一个时间片内的上报,不强制收尾进度条会停在 99%。
            reporter.ReportFinal(totalBytes);

            // 保留时间戳(设置 → 文件传输,scp -p 语义):把远端 mtime 设回本地源文件的
            // mtime(下载方向的对等实现见 DownloadFileAsync)。尽力而为——个别服务器
            // 禁 setstat,不能让一次时间戳设置失败把已完成的上传标成失败。
            if (preserveTimestamps)
            {
                try
                {
                    await client.SetLastWriteTimeAsync(remotePath, fileInfo.LastWriteTimeUtc, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // 时间戳只是尽力而为。
                }
            }
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // 兜底:取消后连接被撕掉之类的场景可能先冒出 IO 错误,统一归一为取消。
            // 正常路径不会走到这里 —— 底层库自己就响应 CancellationToken 并抛 OperationCanceledException。
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            await fileStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>将远端文件下载到本地路径,可选限速与进度回报;按设置可保留文件修改时间戳。</summary>
    public async Task DownloadFileAsync(Guid sessionId,
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string fileName = GetUnixFileName(remotePath);
        RemoteFileInfo fileInfo = await GetFileInfoAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
        long totalBytes = fileInfo.Size;
        var reporter = new ProgressThrottle(progress, fileName, totalBytes);
        (_, long downloadBps, bool preserveTimestamps) = await GetTransferTuningAsync().ConfigureAwait(false);

        // 以此刻本地残留文件的实际长度重新核实续传起点(理由同上传侧)。
        if (resumeOffset > 0)
        {
            resumeOffset = await ResolveDownloadResumeAsync(client, remotePath, localPath, totalBytes, resumeOffset, cancellationToken).ConfigureAwait(false);
        }

        if (resumeOffset > 0)
        {
            // 断点续传下载:从核实过的起点续写。
            // 不能用 FileMode.Append —— 它追加在"文件实际末尾",而续传起点已经回退过一个在途
            // 写入窗口,两者对不上就会在文件里留下空隙。这里显式截断到起点再定位过去,
            // 顺便丢掉起点之后那段可能含空洞的可疑残留。
            await using var localStream = new FileStream(localPath, FileMode.Open, FileAccess.Write, FileShare.None,
                LocalStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            localStream.SetLength(resumeOffset);
            localStream.Seek(resumeOffset, SeekOrigin.Begin);
            // 从偏移处分块拷贝下载远端内容。远端流用 await using:同步 Dispose 会阻塞在网络关闭上。
            await using Stream remoteStream = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
            remoteStream.Seek(resumeOffset, SeekOrigin.Begin);

            // 32KB 一次往返对高延迟链路太小;续传路径是自己搬字节,块大些能显著减少往返次数。
            byte[] buffer = new byte[256 * 1024];
            long bytesRead = 0;
            int read;
            while ((read = await remoteStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await localStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesRead += read;
                reporter.Report(resumeOffset + bytesRead);
            }
            reporter.ReportFinal(resumeOffset + bytesRead);
        }
        else
        {
            Stream sink = OpenLocalWrite(localPath);
            Stream fileStream = downloadBps > 0 ? new ThrottledStream(sink, downloadBps) : sink;
            Action<ulong>? onBytes = reporter.IsEnabled ? bytes => reporter.Report((long)bytes) : null;

            try
            {
                await client.DownloadAsync(remotePath, fileStream, onBytes, cancellationToken).ConfigureAwait(false);
                reporter.ReportFinal(totalBytes);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
            {
                // 兜底,理由同 UploadFileAsync。
                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                await fileStream.DisposeAsync().ConfigureAwait(false);
            }
        }

        // 保留文件时间戳(设置 → 文件传输):下载完成后把远端修改时间写到本地副本。
        if (preserveTimestamps && fileInfo.LastModified != default)
        {
            try
            {
                File.SetLastWriteTime(localPath, fileInfo.LastModified);
            }
            catch
            {
                // 时间戳只是尽力而为。
            }
        }
    }

    /// <summary>删除远端文件或目录;目录按深度优先递归删除子项,并逐条回报删除进度。</summary>
    public async Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // 一次 stat 同时回答"存在吗"和"是不是目录";旧实现是 Exists + 列举整个父目录两趟。
        SftpEntry entry = await client.GetEntryAsync(remotePath, cancellationToken).ConfigureAwait(false)
                          ?? throw new FileNotFoundException($"Remote path not found: {remotePath}");
        bool isDirectory = entry.IsDirectory;
        int total = await CountEntriesAsync(client, remotePath, isDirectory, cancellationToken).ConfigureAwait(false);

        // 先发出一个 "0 / total" 的进度点,使 UI 能立即切换到确定型进度。
        progress?.Report(new(0, total, remotePath));
        var counter = new DeleteCounter();
        await DeleteEntryAsync(client, remotePath, isDirectory, total, counter, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在远端创建指定路径的目录。</summary>
    public async Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await client.CreateDirectoryAsync(remotePath, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>在远端创建一个空文件。</summary>
    public async Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        using var empty = new MemoryStream();
        await client.UploadAsync(empty, remotePath, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>幂等地确保远端目录存在:已存在直接返回,否则创建。</summary>
    public async Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // 先 Exists 探测再创建。与直觉相反,Tmds.Ssh 的 CreateDirectory 对"已存在的目录"会抛
        // SftpException(并非无声成功):重复上传同一文件夹树(与服务端冲突的常见场景)时,
        // 每个已存在子目录都会甩出一条首发异常,上千文件的文件夹重传即刷出上百条异常噪声。
        // ExistsAsync 走 GetAttributes(不存在返回 null、零异常控制流),先探测即可对"已存在"
        // 零异常返回;仅真正缺失的目录才 CreateDirectory(此时创建必然成功,同样不产生异常)。
        // 代价是新建目录多一次 stat 往返,但目录数远少于文件数,相对整批传输可忽略。
        if (await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await client.CreateDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false);
        }
        catch (VelaSshClientException)
        {
            // 探测与创建之间被他方(并发上传/别的客户端)抢先创建的竞态:目录现已存在即幂等
            // 成功;其余失败(权限/父目录缺失)照常抛出。
            if (!await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    /// <summary>重命名或移动远端文件/目录;普通 rename 被服务器拒绝时回退到 posix-rename 扩展。</summary>
    public async Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.RenameFileAsync(oldPath, newPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VelaSftpOperationException or NotSupportedException)
        {
            // 部分 SFTP 服务器以 SSH_FX_BAD_MESSAGE(表现为"bad message")拒绝普通的 SSH_FXP_RENAME,
            // 跨目录移动时常见。改用被广泛支持的 posix-rename@openssh.com 扩展重试;若该路径也不可用,
            // 则抛出原本更具信息量的错误。
            try
            {
                await client.PosixRenameFileAsync(oldPath, newPath, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                throw ex;
            }
        }
    }

    /// <summary>
    /// 将远端文件或目录复制到同一服务器的另一路径。
    /// 单个文件经由临时本地文件复制(不在内存中缓冲大文件)。
    /// 目录则递归遍历并做循环检测。
    /// </summary>
    public async Task CopyAsync(Guid sessionId,
        string sourcePath,
        string destPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // 通过 stat 判断源是否为目录(旧实现名为 stat 实为列举整个父目录)。
        SftpEntry? entry = await client.GetEntryAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        bool isDir = entry is { IsDirectory: true };

        if (!isDir)
        {
            await CopySingleFileAsync(sessionId, sourcePath, destPath, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await CopyDirectoryAsync(sessionId, sourcePath, destPath, [with(StringComparer.Ordinal)], 0, progress, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 经由临时本地文件复制单个远端文件,绝不在内存中缓冲整个文件。
    /// 复用 DownloadFileAsync/UploadFileAsync 以进行限速与取消。
    /// </summary>
    private async Task CopySingleFileAsync(
        Guid sessionId,
        string sourcePath,
        string destPath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "VelaShell", "copy");
        Directory.CreateDirectory(tempDir);
        string tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));

        try
        {
            // 远端下载 → 临时文件
            await DownloadFileAsync(sessionId, sourcePath, tempPath, progress, cancellationToken: cancellationToken).ConfigureAwait(false);

            // 临时文件上传 → 远端目标
            await UploadFileAsync(sessionId, tempPath, destPath, progress, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* 尽力而为 */ }
        }
    }

    /// <summary>
    /// 递归复制远端目录,通过已访问路径集合做循环检测,
    /// 并设置深度上限以防止符号链接环导致的栈溢出。
    /// </summary>
    private async Task CopyDirectoryAsync(
        Guid sessionId,
        string sourcePath,
        string destPath,
        HashSet<string> visited,
        int depth,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int maxDepth = 64;
        if (depth > maxDepth)
        {
            throw new InvalidOperationException($"Copy depth exceeded {maxDepth} levels — possible symlink cycle at {sourcePath}");
        }

        string canonical = sourcePath.TrimEnd('/');
        if (!visited.Add(canonical))
        {
            throw new InvalidOperationException($"Cycle detected copying {sourcePath} — a directory contains a link to itself.");
        }

        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await EnsureDirectoryAsync(sessionId, destPath, cancellationToken).ConfigureAwait(false);

        IEnumerable<SftpEntry> children = await client.ListDirectoryAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        foreach (SftpEntry child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Name is "." or "..") continue;

            string childSource = CombineUnixPath(sourcePath, child.Name);
            string childDest = CombineUnixPath(destPath, child.Name);

            if (child.IsDirectory)
            {
                await CopyDirectoryAsync(sessionId, childSource, childDest, visited, depth + 1, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await CopySingleFileAsync(sessionId, childSource, childDest, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>以三位八进制模式(000-777)设置远端文件/目录的权限。</summary>
    public async Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default)
    {
        if (octalMode < 0 || octalMode > 777 || octalMode % 10 > 7 || (octalMode / 10) % 10 > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(octalMode), octalMode, @"Mode must be three octal digits (000-777).");
        }
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await client.ChangePermissionsAsync(remotePath, octalMode, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>打开远端文件的只读流(顺序读取,调用方负责释放)。</summary>
    public async Task<Stream> OpenReadAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>获取远端指定路径文件/目录的详细信息;路径不存在时抛出 <see cref="FileNotFoundException" />。</summary>
    public async Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // 一次 stat 即可。旧实现是列举整个父目录再从中挑一条:父目录上万条时代价极高,
        // 而且每次下载都会先走这里 —— 批量传 N 个文件就是 N 次全目录列举。
        SftpEntry file = await client.GetEntryAsync(remotePath, cancellationToken).ConfigureAwait(false)
                         ?? throw new FileNotFoundException($"File not found: {remotePath}");
        RemoteIdentityMap identities = await _identities.GetAsync(sessionId).ConfigureAwait(false);
        return MapToRemoteFileInfo(file, identities);
    }

    /// <summary>判断远端路径是否存在。</summary>
    public async Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return await client.ExistsAsync(remotePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>返回该会话 SFTP 客户端的当前工作目录。</summary>
    public async Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        ISftpClientWrapper client = await GetOrCreateSftpClientAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return client.WorkingDirectory;
    }

    /// <summary>关闭并清理指定会话缓存的 SFTP 客户端与单飞闸,断开连接并释放资源。</summary>
    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_clientGates.TryRemove(sessionId, out SemaphoreSlim? gate))
        {
            gate.Dispose();
        }

        // 在下面的早退之前丢弃:即使本会话从未建过 SFTP 客户端,查表缓存也可能已存在。
        _identities.Invalidate(sessionId);
        if (!_sftpClients.TryRemove(sessionId, out ISftpClientWrapper? client))
        {
            return;
        }
        await Task.Run(() =>
        {
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
                client.Dispose();
            }
            catch
            {
                // 尽力拆解;标签页已经不在了。
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>断开并释放所有缓存的 SFTP 客户端,尽力清理全部会话资源。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (KeyValuePair<Guid, ISftpClientWrapper> kvp in _sftpClients)
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
                // 释放期间的尽力清理
            }
        }
        _sftpClients.Clear();
        await ValueTask.CompletedTask.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>大文件传输用的本地流缓冲区(1MB):把 GB 级文件的系统调用次数压到千级。</summary>
    private const int LocalStreamBufferSize = 1024 * 1024;

    /// <summary>续传前比对的尾部字节数:够识别"同名不同文件",又只值一次往返。</summary>
    private const int ResumeVerifyBytes = 64 * 1024;

    /// <summary>
    /// 核实一次上传的续传起点。
    /// <para>
    /// 调用方给出的 <paramref name="claimedOffset" /> 是更早之前探测远端大小得到的,从探测到
    /// 真正开始写之间隔着冲突对话框和传输队列,远端文件完全可能已经变了 —— 直接照着旧偏移
    /// 追加会静默产出损坏文件。这里以"此刻的远端长度"为准,并比对尾部字节确认远端那半截
    /// 确实是本地文件的前缀。
    /// </para>
    /// </summary>
    /// <returns>经核实的续传偏移量;返回 0 表示没有可续的半截,应整份重传。</returns>
    private static async Task<long> ResolveUploadResumeAsync(ISftpClientWrapper client,
        string remotePath,
        string localPath,
        long localLength,
        long claimedOffset,
        CancellationToken cancellationToken)
    {
        long remoteLength = await client.GetFileSizeAsync(remotePath, cancellationToken).ConfigureAwait(false);

        // 远端不存在/为空,或已不短于本地:都不构成"传了一半",退化为整份重传(覆盖写)。
        if (remoteLength <= 0 || remoteLength >= localLength)
        {
            return 0;
        }

        // 回退一整个在途写入窗口:文件长度只是"已确认的最高偏移",它之前可能还留着未落盘的空洞
        // (见 ISftpClientWrapper.ResumeSafetyMargin)。不回退的话尾部比对会落在已写入的那段上
        // 顺利通过,却从一个带洞的位置接着传 —— 那正是"续传出来的文件是坏的"的成因。
        long candidate = remoteLength - client.ResumeSafetyMargin;
        if (candidate <= 0)
        {
            return 0;
        }
        await using Stream remote = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
        await using Stream local = OpenLocalRead(localPath);
        if (!await TailMatchesAsync(remote, local, candidate, cancellationToken).ConfigureAwait(false))
        {
            throw new VelaSftpResumeMismatchException(Strings.Format("SftpSvc_ResumeUploadMismatch", remotePath));
        }
        return candidate;
    }

    /// <summary>
    /// 核实一次下载的续传起点,理由同 <see cref="ResolveUploadResumeAsync" />:
    /// 以"此刻本地文件的实际长度"为准,并比对尾部确认本地那半截确实是远端文件的前缀。
    /// </summary>
    /// <returns>经核实的续传偏移量;返回 0 表示应整份重下(覆盖本地残留)。</returns>
    private static async Task<long> ResolveDownloadResumeAsync(ISftpClientWrapper client,
        string remotePath,
        string localPath,
        long remoteLength,
        long claimedOffset,
        CancellationToken cancellationToken)
    {
        var local = new FileInfo(localPath);
        if (!local.Exists)
        {
            return 0;
        }
        long localLength = local.Length;
        if (localLength <= 0 || localLength >= remoteLength)
        {
            return 0;
        }

        // 同样回退在途写入窗口:下载侧的本地文件也是底层库并发写出来的,中断后尾部一样可能有空洞。
        long candidate = localLength - client.ResumeSafetyMargin;
        if (candidate <= 0)
        {
            return 0;
        }
        await using Stream remoteStream = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
        await using Stream localStream = OpenLocalRead(localPath);
        if (!await TailMatchesAsync(remoteStream, localStream, candidate, cancellationToken).ConfigureAwait(false))
        {
            throw new VelaSftpResumeMismatchException(Strings.Format("SftpSvc_ResumeDownloadMismatch", remotePath));
        }
        return candidate;
    }

    /// <summary>
    /// 比对两个流在 <c>[offset - N, offset)</c> 区间的内容是否一致(N 最多
    /// <see cref="ResumeVerifyBytes" />)。两个流都会被定位,调用方不应依赖其原位置。
    /// </summary>
    private static async Task<bool> TailMatchesAsync(Stream first, Stream second, long offset, CancellationToken cancellationToken)
    {
        // 契约兜底:不可 Seek 的流会让下面的定位抛出底层库的裸 NotSupportedException,
        // 排查时完全看不出是"打开选项没开 Seekable"。这里提前把话说清楚。
        // (见 ISftpClientWrapper.OpenAsync 的实现必须返回可 Seek 的流。)
        if (!first.CanSeek || !second.CanSeek)
        {
            throw new InvalidOperationException(
                "Resume verification requires seekable streams; ISftpClientWrapper.OpenAsync must return a stream with CanSeek == true.");
        }
        int length = (int)Math.Min(ResumeVerifyBytes, offset);
        if (length <= 0)
        {
            return true;
        }
        long start = offset - length;
        byte[] firstBuffer = ArrayPool<byte>.Shared.Rent(length);
        byte[] secondBuffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            first.Seek(start, SeekOrigin.Begin);
            await first.ReadExactlyAsync(firstBuffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            second.Seek(start, SeekOrigin.Begin);
            await second.ReadExactlyAsync(secondBuffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            return firstBuffer.AsSpan(0, length).SequenceEqual(secondBuffer.AsSpan(0, length));
        }
        catch (EndOfStreamException)
        {
            // 说明某一侧在核实期间又变短了 —— 同样属于"不可信的续传起点"。
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(firstBuffer);
            ArrayPool<byte>.Shared.Return(secondBuffer);
        }
    }

    /// <summary>
    /// 打开本地文件供上传读取。
    /// <para>
    /// 必须用 <see cref="FileOptions.Asynchronous" />:<c>File.OpenRead</c> 返回的是同步句柄,
    /// 其上的每次 <c>ReadAsync</c> 都会真正阻塞一个线程池线程。GB 级文件意味着几十万次这样的
    /// 阻塞读,线程池只能靠每秒注入一两个线程来补偿,表现就是传输跑一阵后长时间停顿再恢复。
    /// <see cref="FileOptions.SequentialScan" /> 则让系统预读策略匹配顺序整文件读取。
    /// </para>
    /// </summary>
    private static FileStream OpenLocalRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, LocalStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>打开本地文件供下载写入(截断已有内容);异步句柄的理由同 <see cref="OpenLocalRead" />。</summary>
    private static FileStream OpenLocalWrite(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.None, LocalStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>带宽限制(设置 → 文件传输):返回字节/秒,0 = 不限速。</summary>
    private async Task<(long UploadBps, long DownloadBps, bool PreserveTimestamps)> GetTransferTuningAsync()
    {
        if (settingsService is null)
        {
            return (0, 0, true);
        }
        try
        {
            TransferOptions t = (await settingsService.GetSettingsAsync().ConfigureAwait(false)).Transfer;
            long up = t.BandwidthLimitEnabled ? (long)Math.Max(0, t.UploadLimitMBps) * 1024 * 1024 : 0;
            long down = t.BandwidthLimitEnabled ? (long)Math.Max(0, t.DownloadLimitMBps) * 1024 * 1024 : 0;
            return (up, down, t.PreserveTimestamps);
        }
        catch
        {
            return (0, 0, true);
        }
    }

    /// <summary>异步递归的已删除计数(async 方法不允许 ref 参数)。</summary>
    private sealed class DeleteCounter
    {
        public int Deleted;
    }

    /// <summary>
    /// 深度优先删除:先移除目录的子项再移除目录自身,因为 SFTP 的 <c>rmdir</c> 仅对空目录成功。
    /// 每移除一个条目回报一次进度。
    /// </summary>
    private static async Task<int> CountEntriesAsync(ISftpClientWrapper client, string path, bool isDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isDirectory)
        {
            return 1;
        }
        int total = 1;
        IEnumerable<SftpEntry> children = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (SftpEntry child in children)
        {
            if (child.Name is "." or "..")
            {
                continue;
            }
            total += await CountEntriesAsync(client, child.FullName, child.IsDirectory, cancellationToken).ConfigureAwait(false);
        }
        return total;
    }

    private static async Task DeleteEntryAsync(ISftpClientWrapper client,
        string path,
        bool isDirectory,
        int total,
        DeleteCounter counter,
        IProgress<SftpDeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isDirectory)
        {
            IEnumerable<SftpEntry> children = await client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            foreach (SftpEntry child in children)
            {
                if (child.Name is "." or "..")
                {
                    continue;
                }
                await DeleteEntryAsync(client, child.FullName, child.IsDirectory, total, counter, progress, cancellationToken).ConfigureAwait(false);
            }
            await client.DeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false);
        }
        counter.Deleted++;
        progress?.Report(new(counter.Deleted, total, path));
    }

    private async Task<ISftpClientWrapper> GetOrCreateSftpClientAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (TryGetUsableClient(sessionId, out ISftpClientWrapper? existingClient))
        {
            return existingClient;
        }

        // 单飞:面板加载与文件操作可能并发首次触达同一会话,不加闸时会各自握手出
        // 一条 SFTP 连接(SSH 握手 × N),后到者还会把先到者从字典里顶掉造成泄漏。
        SemaphoreSlim gate = _clientGates.GetOrAdd(sessionId, static _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetUsableClient(sessionId, out existingClient))
            {
                return existingClient;
            }
            SshSession? session = _connectionService.GetSession(sessionId) ?? throw new InvalidOperationException($"Session {sessionId} not found");
            if (session.Status != SessionStatus.Connected)
            {
                throw new InvalidOperationException($"Session {sessionId} is not connected");
            }
            if (sftpClientFactory == null)
            {
                throw new InvalidOperationException("SFTP client factory not configured");
            }
            ISftpClientWrapper client = sftpClientFactory(session);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _sftpClients[sessionId] = client;
            return client;
        }
        finally
        {
            try
            {
                gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // CloseSessionAsync 与首次创建赛跑时会把闸释放掉;创建结果本身已无所谓。
            }
        }
    }

    private bool TryGetUsableClient(Guid sessionId, [NotNullWhen(true)] out ISftpClientWrapper? client)
    {
        if (_sftpClients.TryGetValue(sessionId, out client))
        {
            try
            {
                return client.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                // 会话关闭把客户端释放了,当作缺失重建。
            }
        }
        client = null;
        return false;
    }

    private static RemoteFileInfo MapToRemoteFileInfo(SftpEntry file, RemoteIdentityMap identities)
    {
        return new()
        {
            Name = file.Name,
            FullPath = file.FullName,
            Size = file.Length,
            Permissions = FormatPermissions(file),
            IsDirectory = file.IsDirectory,
            LastModified = file.LastWriteTime,
            Owner = identities.UserName(file.UserId),
            Group = identities.GroupName(file.GroupId)
        };
    }

    /// <summary>
    /// 传输进度节流器。
    /// <para>
    /// 底层 SFTP 库按分块(约 32KB)触发进度回调,一个 7.7GB 的文件会产生二十多万次回调。
    /// 上层的 <see cref="Progress{T}" /> 是在 UI 线程上构造的,每次 Report 都会 Post 一个
    /// 工作项到 Avalonia 调度器,并在其中触发多个 PropertyChanged + 字符串格式化。
    /// 网络产出速度远高于 UI 线程的消费速度,队列只增不减 —— 表现就是传到 1GB 左右界面
    /// 长时间卡死、随后又"追上"继续。这里在源头按时间片收敛上报频率。
    /// </para>
    /// <para>
    /// 分块回调可能并发到达且乱序,因此已传字节数取单调最大值,避免进度条回退。
    /// </para>
    /// </summary>
    private sealed class ProgressThrottle(IProgress<TransferProgress>? sink, string fileName, long totalBytes)
    {
        /// <summary>两次上报之间的最小间隔:每秒最多刷新 10 次界面,足够顺滑且成本可忽略。</summary>
        private const long MinIntervalMs = 100;

        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _lastReportMs = -MinIntervalMs;
        private long _maxBytes;

        /// <summary>是否需要上报(sink 为空时全链路短路,连对象分配都省掉)。</summary>
        public bool IsEnabled => sink is not null;

        /// <summary>按节流策略上报一次进度;间隔不足则丢弃(下一次仍会带上累计值)。</summary>
        public void Report(long bytesTransferred)
        {
            if (sink is null)
            {
                return;
            }
            long observed = Monotonic(bytesTransferred);
            long nowMs = _stopwatch.ElapsedMilliseconds;
            long last = Volatile.Read(ref _lastReportMs);
            if (nowMs - last < MinIntervalMs)
            {
                return;
            }

            // CAS 抢占本时间片的上报权:并发回调下只有一个线程真正 Report,其余直接返回。
            if (Interlocked.CompareExchange(ref _lastReportMs, nowMs, last) != last)
            {
                return;
            }
            Emit(observed, nowMs);
        }

        /// <summary>无视节流强制上报一次,用于收尾 —— 否则进度会永远停在最后一个时间片的值上。</summary>
        public void ReportFinal(long bytesTransferred)
        {
            if (sink is null)
            {
                return;
            }
            Emit(Monotonic(bytesTransferred), _stopwatch.ElapsedMilliseconds);
        }

        private long Monotonic(long bytesTransferred)
        {
            long current = Volatile.Read(ref _maxBytes);
            while (bytesTransferred > current)
            {
                long previous = Interlocked.CompareExchange(ref _maxBytes, bytesTransferred, current);
                if (previous == current)
                {
                    return bytesTransferred;
                }
                current = previous;
            }
            return current;
        }

        private void Emit(long bytesTransferred, long elapsedMs)
        {
            double elapsedSeconds = elapsedMs / 1000d;
            double speed = elapsedSeconds > 0 ? bytesTransferred / elapsedSeconds : 0;
            long remainingBytes = totalBytes - bytesTransferred;
            TimeSpan estimatedTimeRemaining = speed > 0 && remainingBytes > 0
                                                  ? TimeSpan.FromSeconds(remainingBytes / speed)
                                                  : TimeSpan.Zero;
            sink!.Report(new()
            {
                FileName = fileName,
                BytesTransferred = bytesTransferred,
                TotalBytes = totalBytes,
                Percentage = totalBytes > 0 ? (int)((bytesTransferred * 100) / totalBytes) : 0,
                SpeedBytesPerSecond = speed,
                EstimatedTimeRemaining = estimatedTimeRemaining
            });
        }
    }

    private static string CombineUnixPath(string directory, string name) =>
        directory == "/" ? "/" + name : directory.TrimEnd('/') + "/" + name;

    private static string GetUnixFileName(string remotePath)
    {
        int lastSlash = remotePath.LastIndexOf('/');
        return lastSlash >= 0 ? remotePath[(lastSlash + 1)..] : remotePath;
    }

    private static string FormatPermissions(SftpEntry file)
    {
        string perms = file.IsDirectory ? "d" : "-";
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
