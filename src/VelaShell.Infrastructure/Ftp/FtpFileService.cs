using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using FluentFTP.Proxy.AsyncProxy;
using VelaShell.Core.Ftp;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Sftp;
using VelaShell.Infrastructure.Net;
using CoreDataMode = VelaShell.Core.Models.FtpDataConnectionMode;
using CoreEncryption = VelaShell.Core.Models.FtpEncryptionMode;

namespace VelaShell.Infrastructure.Ftp;

/// <summary>
/// FTP / FTPS 的文件服务:同时实现 <see cref="ISftpService" />(文件操作,与协议无关的那套契约)
/// 与 <see cref="IFtpSessionService" />(会话生命周期)。
/// <para>
/// 接缝选在 <see cref="ISftpService" /> 而不是 <c>ISftpClientWrapper</c>,原因有二
/// (详见 docs/FTP客户端可行性调研.md §一):
/// 其一,上层真正消费的 <see cref="RemoteFileInfo" /> 里权限/属主/属组都是**字符串**,
/// 正是 FTP 的 LIST/MLSD 能给的,不必绕 UID/GID 与 SSH exec 查表;
/// 其二,<c>ISftpClientWrapper.OpenAsync</c> 硬性要求返回**可 Seek** 的流,
/// 而 FluentFTP 的数据流 <c>CanSeek == false</c>、<c>Seek()</c> 直接抛异常,根本满足不了。
/// </para>
/// </summary>
public sealed class FtpFileService(IProxyResolver? proxyResolver = null) : ISftpService, IFtpSessionService
{
    private readonly ConcurrentDictionary<Guid, FtpConnectionPool> _sessions = new();

    /// <inheritdoc />
    public event EventHandler<FtpSessionStateChange>? SessionStateChanged;

    /// <inheritdoc />
    public async Task<Guid> OpenSessionAsync(FtpConnectionInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        var probe = new CertificateProbe(info.Settings.TrustedCertificateThumbprint);
        var pool = new FtpConnectionPool(info, token => CreateConnectedClientAsync(info, probe, proxyResolver, token));
        try
        {
            // 第一次租借即完成 TCP 连接、TLS 握手与登录 —— 把失败暴露在「打开会话」这一步,
            // 而不是等用户点开目录才炸。
            using FtpConnectionPool.Lease lease = await pool.RentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await pool.DisposeAsync().ConfigureAwait(false);
            // 证书没过校验时,FluentFTP 抛出来的是一个笼统的连接失败;这里换成带指纹的专用异常,
            // 上层才能弹出「是否信任该证书」并把指纹写回配置。
            throw probe.Failure is { } failure
                ? new VelaFtpCertificateException(
                    $"The server certificate for {info.Host} is not trusted ({failure.PolicyErrors}).",
                    failure.Thumbprint, failure.Subject, failure.Issuer, failure.ExpiresOn, failure.PolicyErrors, ex)
                : FluentFtpInterop.Translate(ex, "connect");
        }
        var sessionId = Guid.NewGuid();
        _sessions[sessionId] = pool;
        SessionStateChanged?.Invoke(this, new(sessionId, FtpSessionState.Connected));
        return sessionId;
    }

    /// <inheritdoc />
    public bool OwnsSession(Guid sessionId) => _sessions.ContainsKey(sessionId);

    /// <inheritdoc />
    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(sessionId, out FtpConnectionPool? pool))
        {
            await pool.DisposeAsync().ConfigureAwait(false);
            SessionStateChanged?.Invoke(this, new(sessionId, FtpSessionState.Closed));
        }
    }

    /// <inheritdoc />
    public async Task<List<RemoteFileInfo>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            FtpListItem[] items = await lease.Client.GetListing(NormalizePath(path), cancellationToken).ConfigureAwait(false);
            return [.. items.Where(static item => item is not null).Select(Map)];
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "list directory");
        }
    }

    /// <inheritdoc />
    public async Task UploadFileAsync(Guid sessionId,
        string localPath,
        string remotePath,
        IProgress<TransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        string fileName = Path.GetFileName(localPath);
        long totalBytes = new FileInfo(localPath).Length;
        await RunTransferAsync(sessionId, "upload", async client =>
        {
            await using FileStream source = File.OpenRead(localPath);
            // 续传交给 FluentFTP:Resume 模式下它自己 SIZE 远端、再把本地流 Seek 到同一偏移。
            // 不需要 SFTP 那套「回退一个在途写入窗口」—— FTP 单条数据连接顺序写,没有乱序空洞。
            FtpStatus status = await client.UploadStream(
                source,
                NormalizePath(remotePath),
                resumeOffset > 0 ? FtpRemoteExists.Resume : FtpRemoteExists.Overwrite,
                createRemoteDir: false,
                progress: MapProgress(progress, fileName, totalBytes),
                token: cancellationToken).ConfigureAwait(false);
            if (status == FtpStatus.Failed)
            {
                throw new VelaFtpOperationException($"FTP upload of {fileName} failed.");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DownloadFileAsync(Guid sessionId,
        string remotePath,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        string fileName = GetRemoteFileName(remotePath);
        await RunTransferAsync(sessionId, "download", async client =>
        {
            long totalBytes = await client.GetFileSize(NormalizePath(remotePath), -1, cancellationToken).ConfigureAwait(false);
            await using FileStream target = resumeOffset > 0
                ? new FileStream(localPath, FileMode.Append, FileAccess.Write, FileShare.None)
                : new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            bool ok = await client.DownloadStream(
                target,
                NormalizePath(remotePath),
                resumeOffset,
                MapProgress(progress, fileName, totalBytes),
                cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                throw new VelaFtpOperationException($"FTP download of {fileName} failed.");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid sessionId, string remotePath, IProgress<SftpDeleteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        AsyncFtpClient client = lease.Client;
        string path = NormalizePath(remotePath);
        try
        {
            FtpListItem? item = await client.GetObjectInfo(path, false, cancellationToken).ConfigureAwait(false);
            if (item is null || item.Type != FtpObjectType.Directory)
            {
                await client.DeleteFile(path, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(1, 1, path));
                return;
            }
            // 递归删:先数一遍再删,才能给出「已删 n / 共 m」的确定进度(与 SFTP 侧的语义一致)。
            List<string> files = [];
            List<string> directories = [];
            await CollectAsync(client, path, files, directories, cancellationToken).ConfigureAwait(false);
            int total = files.Count + directories.Count + 1;
            int done = 0;
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await client.DeleteFile(file, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(++done, total, file));
            }
            // 子目录按深度倒序删,保证先空后删。
            foreach (string directory in directories.OrderByDescending(static d => d.Count(static c => c == '/')))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await client.DeleteDirectory(directory, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(++done, total, directory));
            }
            await client.DeleteDirectory(path, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(++done, total, path));
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "delete");
        }
    }

    /// <inheritdoc />
    public async Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            await lease.Client.CreateDirectory(NormalizePath(remotePath), true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "create directory");
        }
    }

    /// <inheritdoc />
    public async Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            using var empty = new MemoryStream([]);
            await lease.Client.UploadStream(empty, NormalizePath(remotePath), FtpRemoteExists.Overwrite,
                createRemoteDir: false, token: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "create file");
        }
    }

    /// <inheritdoc />
    public Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default) =>
        CreateDirectoryAsync(sessionId, remotePath, cancellationToken);

    /// <inheritdoc />
    public async Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            await lease.Client.Rename(NormalizePath(oldPath), NormalizePath(newPath), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "rename");
        }
    }

    /// <inheritdoc />
    public async Task CopyAsync(Guid sessionId, string sourcePath, string destPath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // FTP 没有服务端复制命令(FXP 是站点间传输,不是同站复制),只能下行再上行。
        // 走临时文件而非内存:远端文件可能远大于可用内存。
        string temp = Path.Combine(Path.GetTempPath(), $"vela-ftp-copy-{Guid.NewGuid():N}");
        try
        {
            await DownloadFileAsync(sessionId, sourcePath, temp, progress, 0, cancellationToken).ConfigureAwait(false);
            await UploadFileAsync(sessionId, temp, destPath, progress, 0, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // 临时文件删不掉不影响结果。
            }
        }
    }

    /// <inheritdoc />
    public async Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            // SITE CHMOD 是可选命令,很多服务器(尤其 Windows/IIS 系)不实现 —— 失败会被翻译成
            // VelaFtpOperationException,由上层提示,而不是静默当成功。
            await lease.Client.Chmod(NormalizePath(remotePath), octalMode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "chmod");
        }
    }

    /// <inheritdoc />
    public async Task<RemoteFileInfo> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string path = NormalizePath(remotePath);
        try
        {
            FtpListItem? item = await lease.Client.GetObjectInfo(path, true, cancellationToken).ConfigureAwait(false);
            return item is null
                ? throw new VelaFtpPathNotFoundException($"FTP path not found: {path}")
                : Map(item);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "stat");
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            Stream inner = await lease.Client
                .OpenRead(NormalizePath(remotePath), FtpDataType.Binary, 0L, 0L, token: cancellationToken)
                .ConfigureAwait(false);
            // 数据连接的生命周期就是这个流的生命周期:必须等调用方释放流,才能把连接还回池子。
            return new LeasedStream(inner, lease);
        }
        catch (Exception ex)
        {
            lease.Dispose();
            throw Fault(sessionId, ex, "open");
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        string path = NormalizePath(remotePath);
        try
        {
            return await lease.Client.FileExists(path, cancellationToken).ConfigureAwait(false) ||
                   await lease.Client.DirectoryExists(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "exists");
        }
    }

    /// <inheritdoc />
    public async Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            string path = await lease.Client.GetWorkingDirectory(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(path) ? "/" : path;
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "pwd");
        }
    }

    /// <summary>释放全部 FTP 会话与其连接。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (Guid sessionId in _sessions.Keys.ToArray())
        {
            await CloseSessionAsync(sessionId).ConfigureAwait(false);
        }
    }

    // ---- 内部实现 ---------------------------------------------------------

    /// <summary>
    /// 跑一次传输。服务器以「忙 / 连接数超限 / 一次只能传一个」拒绝时,把该会话就地收成
    /// 单连接并**重试一次** —— 这一次会排队等那条唯一的连接空下来,于是批量传输自然变成串行。
    /// </summary>
    /// <remarks>
    /// 收紧是**整条会话**的一次性动作,而重试是**每个传输**各自的:一批并发传输往往同时撞上
    /// 这个拒绝,只有跑得最快的那个能把上限从 4 收到 1,其余的看到的是"已经是 1 了"——
    /// 所以重试的条件不能是"我收紧成功了",而是"这次拒绝是并发导致的,且会话还在"。
    /// 收紧之后重试会在池的闸门上排队,同一时刻只剩一个传输在跑,自然不会再撞。
    /// <para>
    /// 与 <see cref="FtpConnectionPool" /> 里那条自适应是两个场景:池管的是"第二条**连接**开不出来",
    /// 这里管的是"连接开得出来,但服务器不让同时跑第二个**传输**"(数据通道被拒)。
    /// </para>
    /// </remarks>
    private async Task RunTransferAsync(
        Guid sessionId,
        string operation,
        Func<AsyncFtpClient, Task> body,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using FtpConnectionPool.Lease lease = await RentAsync(sessionId, cancellationToken).ConfigureAwait(false);
                await body(lease.Client).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (
                attempt < MaxBusyRetries
                && !cancellationToken.IsCancellationRequested
                && FluentFtpInterop.IsConcurrencyRejection(ex)
                && LimitSessionToSingleConnection(sessionId))
            {
                // 落到下一轮循环重试:这一次会排队等那条唯一的连接空下来。
            }
            catch (Exception ex)
            {
                throw Fault(sessionId, ex, operation);
            }
        }
    }

    /// <summary>
    /// 单个传输被"忙/超限"顶回来后允许重试的次数。给 2 次是留一手:
    /// 收紧生效前抢跑的那几个传输可能连着撞两回,再多就该如实报错了。
    /// </summary>
    private const int MaxBusyRetries = 2;

    /// <summary>把会话收成单连接;会话还在就返回 true(上限本来就是 1 也算数,见调用点注释)。</summary>
    private bool LimitSessionToSingleConnection(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out FtpConnectionPool? pool))
        {
            return false;
        }
        pool.LimitToSingleConnection();
        return true;
    }

    private async Task<FtpConnectionPool.Lease> RentAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out FtpConnectionPool? pool))
        {
            throw new VelaFtpConnectionException($"FTP session {sessionId} is not open.");
        }
        try
        {
            return await pool.RentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 租不到连接(服务器没了 / 重连失败)同样是一次「会话掉线」,要上报出去,
            // 否则界面上的状态圆点会一直停在绿色。
            throw Fault(sessionId, ex, "connect");
        }
    }

    /// <summary>
    /// 把库异常翻译成中立异常;若属于连接级失败,顺带把该会话标记为已失效并广播出去
    /// —— 资源管理器树的状态圆点据此自动从「活跃」变「离线」。
    /// </summary>
    private Exception Fault(Guid sessionId, Exception ex, string operation)
    {
        Exception translated = FluentFtpInterop.Translate(ex, operation);
        if (FluentFtpInterop.IsConnectionLost(translated))
        {
            SessionStateChanged?.Invoke(this, new(sessionId, FtpSessionState.Faulted));
        }
        return translated;
    }

    private static async Task<AsyncFtpClient> CreateConnectedClientAsync(FtpConnectionInfo info, CertificateProbe probe, IProxyResolver? proxyResolver, CancellationToken cancellationToken)
    {
        ProxyRoute route = proxyResolver?.Resolve(info.Host, info.Port) ?? ProxyRoute.Direct;
        var config = new FtpConfig
        {
            EncryptionMode = MapEncryption(info.Settings.EncryptionMode),
            // 主动模式要求服务器反向连入本机,经代理无法到达;走代理时强制被动。
            DataConnectionType = route.Kind != ProxyKind.None
                || info.Settings.DataConnectionMode == CoreDataMode.Passive
                ? FtpDataConnectionType.AutoPassive
                : FtpDataConnectionType.AutoActive,
            // 绝不无条件信任证书:校验逻辑在 ValidateCertificate 里,未信任的指纹要能一路冒泡到 UI。
            ValidateAnyCertificate = false,
            ConnectTimeout = 15_000,
            ReadTimeout = 30_000,
            DataConnectionConnectTimeout = 15_000,
            DataConnectionReadTimeout = 30_000,
        };
        AsyncFtpClient client = await CreateClientAsync(info, route, config, cancellationToken).ConfigureAwait(false);
        client.ValidateCertificate += (_, e) => probe.Validate(e);
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 按统一代理路由实例化 FTP 客户端:直连用普通 <see cref="AsyncFtpClient" />,
    /// 走代理用 FluentFTP 的代理子类(控制与数据连接一并经代理)。
    /// 「不用代理做 DNS」时本地先解析成 IP 交给代理端。
    /// </summary>
    private static async Task<AsyncFtpClient> CreateClientAsync(
        FtpConnectionInfo info, ProxyRoute route, FtpConfig config, CancellationToken cancellationToken)
    {
        if (route.Kind == ProxyKind.None)
        {
            return new AsyncFtpClient(info.Host, info.EffectiveUsername, info.EffectivePassword, info.Port, config);
        }
        string ftpHost = route.ProxyDns
            ? info.Host
            : await ProxyStreamConnector.ResolveLocallyAsync(info.Host, cancellationToken).ConfigureAwait(false);
        var profile = new FtpProxyProfile
        {
            ProxyHost = route.Host,
            ProxyPort = route.Port,
            ProxyCredentials = route.ToCredential(),
            FtpHost = ftpHost,
            FtpPort = info.Port,
            FtpCredentials = new NetworkCredential(info.EffectiveUsername, info.EffectivePassword),
        };
        AsyncFtpClient client = route.Kind == ProxyKind.Http
            ? new AsyncFtpClientHttp11Proxy(profile)
            : new AsyncFtpClientSocks5Proxy(profile);
        client.Config = config;
        return client;
    }

    private static FluentFTP.FtpEncryptionMode MapEncryption(CoreEncryption mode) =>
        mode switch
        {
            CoreEncryption.None => FluentFTP.FtpEncryptionMode.None,
            CoreEncryption.Explicit => FluentFTP.FtpEncryptionMode.Explicit,
            CoreEncryption.Implicit => FluentFTP.FtpEncryptionMode.Implicit,
            _ => FluentFTP.FtpEncryptionMode.Auto,
        };

    /// <summary>把 FluentFTP 的进度回调翻译成 VelaShell 的传输进度快照。</summary>
    private static Progress<FtpProgress>? MapProgress(IProgress<TransferProgress>? progress, string fileName, long totalBytes) =>
        progress is null
            ? null
            : new Progress<FtpProgress>(p => progress.Report(new()
            {
                FileName = fileName,
                BytesTransferred = p.TransferredBytes,
                TotalBytes = totalBytes > 0 ? totalBytes : p.TransferredBytes,
                Percentage = p.Progress is >= 0 and <= 100 ? (int)p.Progress : 0,
                SpeedBytesPerSecond = p.TransferSpeed,
                EstimatedTimeRemaining = p.ETA,
            }));

    /// <summary>递归收集目录下的文件与子目录(不含目录自身)。</summary>
    private static async Task CollectAsync(AsyncFtpClient client, string path, List<string> files, List<string> directories, CancellationToken cancellationToken)
    {
        FtpListItem[] items = await client.GetListing(path, cancellationToken).ConfigureAwait(false);
        foreach (FtpListItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type == FtpObjectType.Directory)
            {
                directories.Add(item.FullName);
                await CollectAsync(client, item.FullName, files, directories, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                files.Add(item.FullName);
            }
        }
    }

    /// <summary>
    /// <see cref="FtpListItem" /> → <see cref="RemoteFileInfo" />。
    /// 属主/属组直接取服务器给的**名字**(FTP 没有 UID/GID),因此整条绕开了 SFTP 那边
    /// 靠 SSH exec 跑 <c>getent passwd</c> 的身份翻译。服务器不给时留空,由界面自行留白。
    /// </summary>
    private static RemoteFileInfo Map(FtpListItem item) =>
        new()
        {
            Name = item.Name ?? string.Empty,
            FullPath = item.FullName ?? string.Empty,
            Size = item.Size < 0 ? 0 : item.Size,
            IsDirectory = item.Type == FtpObjectType.Directory,
            LastModified = item.Modified,
            Permissions = FormatPermissions(item),
            Owner = item.RawOwner ?? string.Empty,
            Group = item.RawGroup ?? string.Empty,
        };

    /// <summary>
    /// 权限字符串:优先用服务器原样给的(Unix 风格 LIST 会给 <c>-rw-r--r--</c>),
    /// 去掉首位的类型字符;拿不到就用解析出的三组权限位拼,再拿不到就留空。
    /// </summary>
    private static string FormatPermissions(FtpListItem item)
    {
        string raw = item.RawPermissions ?? string.Empty;
        if (raw.Length >= 10)
        {
            return raw[1..10];
        }
        if (raw.Length == 9)
        {
            return raw;
        }
        if (item.OwnerPermissions == FtpPermission.None &&
            item.GroupPermissions == FtpPermission.None &&
            item.OthersPermissions == FtpPermission.None)
        {
            return string.Empty;
        }
        return string.Concat(
            Rwx(item.OwnerPermissions),
            Rwx(item.GroupPermissions),
            Rwx(item.OthersPermissions));
    }

    private static string Rwx(FtpPermission permission) =>
        string.Concat(
            permission.HasFlag(FtpPermission.Read) ? "r" : "-",
            permission.HasFlag(FtpPermission.Write) ? "w" : "-",
            permission.HasFlag(FtpPermission.Execute) ? "x" : "-");

    private static string NormalizePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? "/" : path.Replace('\\', '/');

    private static string GetRemoteFileName(string remotePath)
    {
        string normalized = NormalizePath(remotePath).TrimEnd('/');
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 && slash < normalized.Length - 1 ? normalized[(slash + 1)..] : normalized;
    }

    /// <summary>
    /// 服务器证书校验策略:指纹已被用户信任、或链路本身无误 → 放行;
    /// 否则记下证书信息并拒绝,由 <see cref="OpenSessionAsync" /> 换成带指纹的异常抛给上层。
    /// <para>
    /// 刻意**不**在这个同步事件里去弹 UI 等用户点确认 —— 那要把异步的对话框阻塞成同步,
    /// 极易死锁。改成「先拒绝 → 上层提示 → 记住指纹后重连」,流程干净且不阻塞。
    /// </para>
    /// </summary>
    private sealed class CertificateProbe(string? trustedThumbprint)
    {
        /// <summary>最近一次未通过校验的证书信息;没有失败时为 null。</summary>
        public CertificateFailure? Failure { get; private set; }

        public void Validate(FtpSslValidationEventArgs e)
        {
            string thumbprint = ComputeThumbprint(e.Certificate);
            if (e.PolicyErrors == SslPolicyErrors.None ||
                (trustedThumbprint is { Length: > 0 } trusted &&
                 string.Equals(trusted, thumbprint, StringComparison.OrdinalIgnoreCase)))
            {
                e.Accept = true;
                return;
            }
            Failure = new(
                thumbprint,
                e.Certificate?.Subject ?? string.Empty,
                e.Certificate?.Issuer ?? string.Empty,
                e.Certificate is X509Certificate2 cert ? cert.NotAfter : DateTimeOffset.MinValue,
                e.PolicyErrors.ToString());
            e.Accept = false;
        }

        private static string ComputeThumbprint(X509Certificate? certificate)
        {
            if (certificate is null)
            {
                return string.Empty;
            }
            try
            {
                return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
            }
            catch (CryptographicException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>未通过校验的证书信息。</summary>
    private sealed record CertificateFailure(string Thumbprint, string Subject, string Issuer, DateTimeOffset ExpiresOn, string PolicyErrors);

    /// <summary>
    /// 绑定了一次连接租借的只读流:调用方释放流时连同把连接还回池子。
    /// FTP 的数据连接与流同生共死,不能像 SFTP 那样先还连接再慢慢读。
    /// </summary>
    private sealed class LeasedStream(Stream inner, FtpConnectionPool.Lease lease) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException("FTP data streams are sequential.");
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("FTP data streams are not seekable.");

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                lease.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            lease.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
