using System.Collections.Concurrent;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.S3;

/// <summary>
/// S3 兼容对象存储的协议实现:把宿主的 <see cref="IProtocolFileSystem" /> 落到 AWSSDK 上。
/// <para>
/// 接缝与 FTP 后端当年选的是同一处(宿主的远程文件契约,见
/// <c>docs/FTP客户端可行性调研.md §一</c>):全部以会话标识为键、返回协议无关的条目,
/// 因此双栏浏览器、传输队列、限速、拖放对"这是个插件协议"完全无感。
/// </para>
/// <para>
/// **本类只负责「文件管理器」那一层语义**:把平的键空间翻译成目录树。S3 协议其余近百个操作
/// (版本控制、生命周期、策略、ACL、对象锁定、复制、标签……)在
/// <see cref="IS3ManagementService" /> 里,两者共用同一条会话与同一个 <see cref="IAmazonS3" />。
/// </para>
/// <para>
/// 平的键空间 → 目录树的四条规则:
/// </para>
/// <list type="bullet">
/// <item>「子目录」来自带 <c>delimiter=/</c> 列举时返回的 <c>CommonPrefixes</c>;</item>
/// <item>各家工具建的「空目录」是一个以 <c>/</c> 结尾的零字节对象(目录占位符),
/// 本实现建目录时也造它,列举时则把它隐掉;</item>
/// <item>重命名/移动没有原语,一律是「服务端复制 + 删除」;</item>
/// <item>权限(chmod)没有对应语义,明确抛 <see cref="VelaS3UnsupportedOperationException" />。</item>
/// </list>
/// <para>
/// 内部一律以 <see cref="Guid" /> 为会话键(<see cref="IS3ManagementService" /> 与两个面板都按它写的),
/// 宿主给的不透明字符串键只在 <see cref="IProtocolFileSystem" /> 的显式实现那一层做映射 ——
/// 显式实现同时避免了「同名不同键类型」的重载歧义。
/// </para>
/// </summary>
/// <param name="protocols">协议能力(用于读取宿主当前的传输限速设置)。</param>
/// <param name="actions">协议动作的处置器(打开面板 / 复制分享链接);为 null 时动作静默忽略。</param>
public sealed class S3ProtocolFileSystem(IProtocolsApi? protocols = null, IS3ActionHandler? actions = null)
    : IProtocolFileSystem, IS3ClientAccessor
{
    /// <summary>本地文件流的缓冲区大小,与宿主的 SFTP 后端取同一个值。</summary>
    private const int LocalStreamBufferSize = 1024 * 1024;

    /// <summary>一次批量删除的最大键数(协议上限)。</summary>
    private const int DeleteBatchSize = 1000;

    /// <summary>一页列举的键数(协议上限)。</summary>
    private const int ListPageSize = 1000;

    private readonly ConcurrentDictionary<Guid, S3Session> _sessions = new();

    /// <summary>宿主的不透明会话键 → 内部 Guid。</summary>
    private readonly ConcurrentDictionary<string, Guid> _keys = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public event EventHandler<ProtocolSessionStateChange>? SessionStateChanged;

    // ---- 会话生命周期 -------------------------------------------------------

    /// <summary>
    /// 建立一条会话。<paramref name="key" /> 是宿主给的不透明会话键;内部仍以新铸的
    /// <see cref="Guid" /> 为主键(管理服务与两个面板都按它写的)。
    /// </summary>
    /// <param name="key">宿主的会话键。</param>
    /// <param name="info">连接参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>内部会话标识。</returns>
    public async Task<Guid> OpenSessionAsync(string key, S3ConnectionInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrWhiteSpace(info.Endpoint))
        {
            throw new VelaS3ConnectionException("The S3 endpoint is empty.");
        }
        var probe = new S3CertificateProbe(info.Settings.TrustedCertificateThumbprint);
        AmazonS3Client client = S3ClientFactory.Create(info, probe);
        try
        {
            // 主动探一次:把「端点写错 / 凭据不对 / 证书不可信 / 区域不对」暴露在打开会话这一步,
            // 而不是等用户点开目录才炸(与 FTP 侧「第一次租借即完成登录」同一个取舍)。
            if (info.Settings.DefaultBucket is { Length: > 0 } bucket)
            {
                // 只授予单桶权限的账号调 ListBuckets 会被拒 —— 配了默认桶就只探那个桶。
                await client.ListObjectsV2Async(
                    new() { BucketName = bucket.Trim(), MaxKeys = 1, Delimiter = "/" },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.ListBucketsAsync(new ListBucketsRequest(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw S3Interop.Translate(ex, "connect", probe);
        }
        var sessionId = Guid.NewGuid();
        _sessions[sessionId] = new(client, info, probe, key);
        _keys[key] = sessionId;
        // 这里**不**发 Connected:宿主要等 ConnectAsync 返回之后才把会话登进表,
        // 此刻上报没有接收方,是一次必然落空的通知。会话建立本身就是"已连接"。
        return sessionId;
    }

    /// <summary>该会话标识是否由本实现持有。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <returns>是否持有。</returns>
    public bool OwnsSession(Guid sessionId) => _sessions.ContainsKey(sessionId);

    /// <summary>关闭并释放一条会话;未知标识为空操作。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(sessionId, out S3Session? session))
        {
            return;
        }
        _keys.TryRemove(session.Key, out _);
        // 先收面板再断会话:桶管理器/对象检视器都握着这条会话的 sessionId,
        // 留着它们只会让用户对着一扇每次操作都报 "session is not open" 的窗口发呆。
        if (actions is not null)
        {
            try
            {
                await actions.CloseSessionPanelsAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 关窗失败不该拖住会话关闭。
            }
        }
        session.Dispose();
        SessionStateChanged?.Invoke(this, new(session.Key, ProtocolSessionState.Closed));
    }

    /// <summary>为某个对象生成预签名 URL(GET),有效期上限 7 天(协议硬限制)。</summary>
    /// <param name="sessionId">会话标识。</param>
    /// <param name="remotePath">对象路径。</param>
    /// <param name="expiry">有效期。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>预签名 URL。</returns>
    public Task<string> CreatePresignedUrlAsync(Guid sessionId, string remotePath, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        var path = S3ObjectPath.Parse(remotePath);
        if (path.IsRoot || path.Key.Length == 0)
        {
            throw new VelaS3OperationException("A presigned URL requires an object path, not a bucket or the root.");
        }
        try
        {
            // 上限 7 天是协议硬限制;下限 1 秒是防手滑传 0/负数。
            double seconds = Math.Clamp(expiry.TotalSeconds, 1, TimeSpan.FromDays(7).TotalSeconds);
            return Task.FromResult(session.Client.GetPreSignedURL(new()
            {
                BucketName = path.Bucket,
                Key = path.Key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.AddSeconds(seconds),
                // **必须显式给**:SDK 的预签名一律按 HTTPS 出 URL,不看 config.UseHttp。
                // 明文 HTTP 的自建端点(MinIO 常态)会因此拿到一条 https:// 的链接,
                // 粘到浏览器里连不上,而错误看起来完全像是"这个功能坏了"。
                Protocol = session.Client.Config.UseHttp ? Amazon.S3.Protocol.HTTP : Amazon.S3.Protocol.HTTPS,
            }));
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "presign");
        }
    }

    // 显式接口实现:IS3ClientAccessor 的成员暴露 IAmazonS3,那是 AWSSDK 的类型。
    // 显式实现让它们不出现在本类的公开面上 —— 插件对外只有 IProtocolFileSystem
    // 与中立的 IS3ManagementService,与宿主对 FluentFTP / Tmds.Ssh 是同一条规矩。
    IAmazonS3 IS3ClientAccessor.GetClient(Guid sessionId) => Resolve(sessionId).Client;

    S3ConnectionInfo IS3ClientAccessor.GetConnectionInfo(Guid sessionId) => Resolve(sessionId).Info;

    Exception IS3ClientAccessor.TranslateFault(Guid sessionId, Exception exception, string operation) =>
        Fault(sessionId, exception, operation);

    /// <summary>释放全部 S3 会话。</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (Guid sessionId in _sessions.Keys.ToArray())
        {
            await CloseSessionAsync(sessionId).ConfigureAwait(false);
        }
    }

    // ---- IProtocolFileSystem(宿主契约的显式实现) ---------------------------
    //
    // 显式实现而不是直接把上面那些方法改签名:内部一律以 Guid 为键(管理服务与两个面板
    // 都按它写的),这一层只做「不透明字符串键 → Guid」与「插件异常族 → SDK 异常族」两件事。
    // 显式实现顺带避免了同名方法在两种键类型上的重载歧义。

    async Task IProtocolFileSystem.ConnectAsync(string sessionId, ProtocolConnectRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await OpenSessionAsync(sessionId, S3ConnectionInfo.FromRequest(request), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw ToProtocol(ex);
        }
    }

    Task IProtocolFileSystem.DisconnectAsync(string sessionId, CancellationToken cancellationToken) =>
        // 关闭路径不抛:未知会话是空操作(宿主可能已经先一步收掉了)。
        _keys.TryGetValue(sessionId, out Guid id) ? CloseSessionAsync(id, cancellationToken) : Task.CompletedTask;

    Task<string> IProtocolFileSystem.GetHomePathAsync(string sessionId, CancellationToken cancellationToken) =>
        Guard(() => GetWorkingDirectoryAsync(Resolve(sessionId), cancellationToken));

    async Task<IReadOnlyList<RemoteFileEntry>> IProtocolFileSystem.ListDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken)
    {
        List<S3FileEntry> entries = await Guard(() => ListDirectoryAsync(Resolve(sessionId), path, cancellationToken)).ConfigureAwait(false);
        return [.. entries.Select(static entry => entry.ToRemoteEntry())];
    }

    async Task<RemoteFileEntry?> IProtocolFileSystem.StatAsync(string sessionId, string path, CancellationToken cancellationToken)
    {
        // 契约差异:宿主的 Stat 用 null 表示"不存在",而内部实现是抛 VelaS3PathNotFoundException。
        // 翻译放这里,内部实现保持"找不到就抛"的直白语义。
        try
        {
            return (await GetFileInfoAsync(Resolve(sessionId), path, cancellationToken).ConfigureAwait(false)).ToRemoteEntry();
        }
        catch (VelaS3PathNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            throw ToProtocol(ex);
        }
    }

    Task<bool> IProtocolFileSystem.ExistsAsync(string sessionId, string path, CancellationToken cancellationToken) =>
        Guard(() => ExistsAsync(Resolve(sessionId), path, cancellationToken));

    Task<Stream> IProtocolFileSystem.OpenReadAsync(string sessionId, string path, CancellationToken cancellationToken) =>
        Guard(() => OpenReadAsync(Resolve(sessionId), path, cancellationToken));

    Task IProtocolFileSystem.UploadFileAsync(string sessionId, string localPath, string remotePath,
        IProgress<RemoteTransferProgress>? progress, long resumeOffset, CancellationToken cancellationToken) =>
        Guard(() => UploadFileAsync(Resolve(sessionId), localPath, remotePath, progress, resumeOffset, cancellationToken));

    Task IProtocolFileSystem.DownloadFileAsync(string sessionId, string remotePath, string localPath,
        IProgress<RemoteTransferProgress>? progress, long resumeOffset, CancellationToken cancellationToken) =>
        Guard(() => DownloadFileAsync(Resolve(sessionId), remotePath, localPath, progress, resumeOffset, cancellationToken));

    Task IProtocolFileSystem.DeleteAsync(string sessionId, string path,
        IProgress<ProtocolDeleteProgress>? progress, CancellationToken cancellationToken) =>
        Guard(() => DeleteAsync(Resolve(sessionId), path, progress, cancellationToken));

    Task IProtocolFileSystem.CreateDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken) =>
        Guard(() => CreateDirectoryAsync(Resolve(sessionId), path, cancellationToken));

    Task IProtocolFileSystem.CreateFileAsync(string sessionId, string path, CancellationToken cancellationToken) =>
        Guard(() => CreateFileAsync(Resolve(sessionId), path, cancellationToken));

    Task IProtocolFileSystem.EnsureDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken) =>
        Guard(() => EnsureDirectoryAsync(Resolve(sessionId), path, cancellationToken));

    Task IProtocolFileSystem.RenameAsync(string sessionId, string oldPath, string newPath, CancellationToken cancellationToken) =>
        Guard(() => RenameAsync(Resolve(sessionId), oldPath, newPath, cancellationToken));

    Task IProtocolFileSystem.CopyAsync(string sessionId, string sourcePath, string destinationPath,
        IProgress<RemoteTransferProgress>? progress, CancellationToken cancellationToken) =>
        Guard(() => CopyAsync(Resolve(sessionId), sourcePath, destinationPath, progress, cancellationToken));

    Task IProtocolFileSystem.SetPermissionsAsync(string sessionId, string path, short octalMode, CancellationToken cancellationToken) =>
        Guard(() => SetPermissionsAsync(Resolve(sessionId), path, octalMode, cancellationToken));

    /// <summary>
    /// 协议专属动作:复制分享链接(预签名 URL)、打开对象检视器、打开桶管理器。
    /// 面板由 <see cref="IS3ActionHandler" /> 打开 —— 这个类只管协议,不认识 Avalonia。
    /// </summary>
    async Task IProtocolFileSystem.InvokeActionAsync(string sessionId, string actionId, string path, CancellationToken cancellationToken)
    {
        Guid id = Resolve(sessionId);
        if (actions is null)
        {
            return;
        }
        try
        {
            switch (actionId)
            {
                case S3Actions.CopyShareLink:
                    // 7 天是预签名 URL 协议允许的最长有效期。
                    string url = await CreatePresignedUrlAsync(id, path, TimeSpan.FromDays(7), cancellationToken).ConfigureAwait(false);
                    await actions.CopyShareLinkAsync(url, cancellationToken).ConfigureAwait(false);
                    break;
                case S3Actions.InspectObject:
                    S3ObjectPath target = RequireObject(path, "inspect");
                    await actions.OpenObjectInspectorAsync(id, target.Bucket, target.Key, cancellationToken).ConfigureAwait(false);
                    break;
                case S3Actions.ManageBucket:
                    // 在根(桶列表)上右键选中的那一行,或桶内任意位置 —— 两种情形下
                    // 用户的意图都是"管理我现在看到的这个桶",取路径第一段即可。
                    var bucket = S3ObjectPath.Parse(path);
                    if (!bucket.IsRoot)
                    {
                        await actions.OpenBucketManagerAsync(id, bucket.Bucket, cancellationToken).ConfigureAwait(false);
                    }
                    break;
                default:
                    throw new VelaS3UnsupportedOperationException($"Unknown S3 action '{actionId}'.");
            }
        }
        catch (Exception ex)
        {
            throw ToProtocol(ex);
        }
    }

    /// <summary>宿主的不透明会话键 → 内部 Guid;未知键按 SDK 约定抛。</summary>
    private Guid Resolve(string sessionId) =>
        _keys.TryGetValue(sessionId, out Guid id)
            ? id
            : throw new PluginSessionNotFoundException(sessionId);

    private static async Task Guard(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw ToProtocol(ex);
        }
    }

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw ToProtocol(ex);
        }
    }

    /// <summary>
    /// 插件内部异常族 → SDK 异常族。两族并存不是重复:内部这族要区分
    /// 「没配过 / 不支持 / 权限不足 / 不存在」给桶管理器用,而宿主只关心
    /// 「认证失败 / 证书不可信 / 连不上 / 不支持」这四类。认不出的原样放行。
    /// </summary>
    private static Exception ToProtocol(Exception ex) =>
        ex switch
        {
            OperationCanceledException or PluginSessionNotFoundException => ex,
            VelaS3AuthenticationException auth => new ProtocolAuthenticationException(auth.Message, auth),
            VelaS3CertificateException cert => new ProtocolCertificateTrustException(
                cert.Message, cert.Subject, cert.Issuer, cert.ExpiresOn, cert.Thumbprint, cert.PolicyErrors),
            VelaS3ConnectionException conn => new ProtocolConnectionException(conn.Message, conn),
            VelaS3UnsupportedOperationException unsupported => new ProtocolUnsupportedException(unsupported.Message),
            _ => ex,
        };

    // ---- 列举 ---------------------------------------------------------------

    /// <summary>列举目录(内部形态;对外经 IProtocolFileSystem 的显式实现转成 RemoteFileEntry)。</summary>
    internal async Task<List<S3FileEntry>> ListDirectoryAsync(Guid sessionId, string path, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        var target = S3ObjectPath.Parse(path);
        try
        {
            // 根 = 桶列表。每个桶显示为一个目录,双击进去就是这个桶的对象。
            if (target.IsRoot)
            {
                ListBucketsResponse buckets = await session.Client
                    .ListBucketsAsync(new ListBucketsRequest(), cancellationToken)
                    .ConfigureAwait(false);
                return
                [
                    .. (buckets.Buckets ?? []).Select(b => new S3FileEntry
                    {
                        Name = b.BucketName ?? string.Empty,
                        FullPath = "/" + b.BucketName,
                        Size = 0,
                        IsDirectory = true,
                        LastModified = ToLocalDateTime(b.CreationDate),
                        Permissions = string.Empty,
                        Owner = string.Empty,
                        Group = string.Empty,
                    })
                ];
            }

            string prefix = target.Prefix;
            List<S3FileEntry> entries = [];
            HashSet<string> seen = [with(StringComparer.Ordinal)];
            string? token = null;
            do
            {
                ListObjectsV2Response page = await session.Client.ListObjectsV2Async(new()
                {
                    BucketName = target.Bucket,
                    Prefix = prefix,
                    Delimiter = "/",
                    MaxKeys = ListPageSize,
                    ContinuationToken = token,
                    // 不取 fetch-owner:多数 S3 兼容实现忽略它,而 AWS 上它会显著拖慢列举。
                }, cancellationToken).ConfigureAwait(false);

                foreach (string commonPrefix in page.CommonPrefixes ?? [])
                {
                    string name = TrimPrefix(commonPrefix, prefix).TrimEnd('/');
                    if (name.Length > 0 && seen.Add("d:" + name))
                    {
                        entries.Add(new()
                        {
                            Name = name,
                            FullPath = "/" + target.Bucket + "/" + commonPrefix.TrimEnd('/'),
                            Size = 0,
                            IsDirectory = true,
                            // S3 的「目录」是虚构出来的,没有修改时间。给 MinValue 让界面留空,
                            // 而不是给 DateTime.Now —— 那会让排序和「最近修改」彻底失真。
                            LastModified = DateTime.MinValue,
                            Permissions = string.Empty,
                            Owner = string.Empty,
                            Group = string.Empty,
                        });
                    }
                }

                foreach (S3Object item in page.S3Objects ?? [])
                {
                    string key = item.Key ?? string.Empty;
                    // 当前目录自己的占位符对象(键正好等于前缀):它是这个目录,不是目录里的东西。
                    if (string.Equals(key, prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string name = TrimPrefix(key, prefix);
                    if (name.Length == 0)
                    {
                        continue;
                    }
                    // 以 / 结尾的零字节对象是别的工具造的目录占位符;默认隐掉,
                    // 否则每个目录旁边都会多出一个同名的空文件。
                    if (name.EndsWith('/') && !session.Info.Settings.ShowFolderMarkers)
                    {
                        continue;
                    }
                    if (!seen.Add("f:" + name))
                    {
                        continue;
                    }
                    entries.Add(new()
                    {
                        Name = name,
                        FullPath = "/" + target.Bucket + "/" + key,
                        Size = item.Size ?? 0,
                        IsDirectory = false,
                        LastModified = ToLocalDateTime(item.LastModified),
                        // S3 既没有 POSIX 权限位,也没有「组」的概念。这两列留空,
                        // 而不是拿存储类别之类的东西去填 —— 填进去只会让列名说谎。
                        Permissions = string.Empty,
                        Owner = item.Owner?.DisplayName ?? string.Empty,
                        Group = string.Empty,
                    });
                }
                token = (page.IsTruncated ?? false) ? page.NextContinuationToken : null;
            }
            while (token is { Length: > 0 });
            return entries;
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "list directory");
        }
    }

    // ---- 元数据 -------------------------------------------------------------

    /// <summary>取单个条目;不存在时抛 VelaS3PathNotFoundException(对外的 StatAsync 会把它翻成 null)。</summary>
    internal async Task<S3FileEntry> GetFileInfoAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        var path = S3ObjectPath.Parse(remotePath);
        try
        {
            if (path.IsRoot)
            {
                return DirectoryEntry("/", "/");
            }
            if (path.Key.Length == 0)
            {
                return await BucketExistsAsync(session, path.Bucket, cancellationToken).ConfigureAwait(false)
                    ? DirectoryEntry(path.Bucket, "/" + path.Bucket)
                    : throw new VelaS3PathNotFoundException($"S3 bucket not found: {path.Bucket}");
            }
            if (await TryHeadAsync(session, path.Bucket, path.Key, cancellationToken).ConfigureAwait(false) is { } metadata)
            {
                return new()
                {
                    Name = path.Name,
                    FullPath = path.ToString(),
                    Size = metadata.ContentLength,
                    IsDirectory = false,
                    LastModified = ToLocalDateTime(metadata.LastModified),
                    Permissions = string.Empty,
                    Owner = string.Empty,
                    Group = string.Empty,
                };
            }
            return await IsPrefixAsync(session, path, cancellationToken).ConfigureAwait(false)
                ? DirectoryEntry(path.Name, path.ToString())
                : throw new VelaS3PathNotFoundException($"S3 path not found: {path}");
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "stat");
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        var path = S3ObjectPath.Parse(remotePath);
        try
        {
            if (path.IsRoot)
            {
                return true;
            }
            if (path.Key.Length == 0)
            {
                return await BucketExistsAsync(session, path.Bucket, cancellationToken).ConfigureAwait(false);
            }
            return await TryHeadAsync(session, path.Bucket, path.Key, cancellationToken).ConfigureAwait(false) is not null ||
                   await IsPrefixAsync(session, path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "exists");
        }
    }

    /// <inheritdoc />
    public Task<string> GetWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        // 配了默认桶就直接落在桶内;否则落在根(桶列表)。
        return Task.FromResult(session.Info.Settings.DefaultBucket is { Length: > 0 } bucket
            ? "/" + bucket.Trim().Trim('/')
            : "/");
    }

    /// <summary>
    /// S3 没有 POSIX 权限位。明确抛「不支持」而不是静默成功:
    /// 后者会让用户以为自己刚刚改成功了一个根本不存在的权限。对象/桶的访问控制
    /// 走 ACL 与桶策略,见 <see cref="IS3ManagementService" />。
    /// </summary>
    public Task SetPermissionsAsync(Guid sessionId, string remotePath, short octalMode, CancellationToken cancellationToken = default)
    {
        _ = Resolve(sessionId);
        throw new VelaS3UnsupportedOperationException("S3 objects have no POSIX permissions; use ACLs or bucket policies instead.");
    }

    // ---- 读 -----------------------------------------------------------------

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        S3ObjectPath path = RequireObject(remotePath, "open");
        try
        {
            ObjectSource source = await OpenObjectAsync(session, path, offset: 0, totalBytes: null, cancellationToken).ConfigureAwait(false);
            // 响应与流同生共死:必须等调用方释放流,才能释放响应(它持有连接)。
            return new ResponseBoundStream(source.Stream, source.Owner, source.Length);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, $"open of {path}");
        }
    }

    /// <inheritdoc />
    public async Task DownloadFileAsync(Guid sessionId,
        string remotePath,
        string localPath,
        IProgress<RemoteTransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        S3ObjectPath path = RequireObject(remotePath, "download");
        try
        {
            // HEAD 只是来取总大小与 mtime(报进度、算续传起点、对齐时间戳),**不是下载的前提**。
            // 因此它失败也照样往下走,让 GET 去决定这次下载成不成:
            // ① 有的授权只放行 GetObject 而不放行 HeadObject(把对象设成公共读、
            //    或前面挂了 CDN 时尤其常见),这种情况下强制 HEAD 会把本来能下的文件挡死;
            // ② 更要紧的是错误质量 —— HEAD 的 403 **响应体是空的**,SDK 只能报一句
            //    「Error Code Forbidden … No further error information was returned by the service」,
            //    用户完全无从下手;同一个对象的 GET 若同样被拒,会带回正经的
            //    <Error><Code>AccessDenied</Code> 连同 Key/BucketName/RequestId。
            GetObjectMetadataResponse? metadata = await TryHeadForDownloadAsync(session, path, cancellationToken).ConfigureAwait(false);
            // 以本地残留文件的实际长度重新核实续传起点(与 SftpService 同一条原则:
            // 上层记的偏移可能已经过期,以此刻的实际状态为准)。没拿到 HEAD 就整份重下 ——
            // 不知道总长度时发 Range 只会撞上 416。
            long offset = metadata is null ? 0 : ResolveDownloadResume(localPath, metadata.ContentLength, resumeOffset);
            (_, long downloadBps, bool preserveTimestamps) = await GetTransferTuningAsync().ConfigureAwait(false);

            using ObjectSource source = await OpenObjectAsync(session, path, offset, metadata?.ContentLength, cancellationToken).ConfigureAwait(false);
            // 请求了 Range 却拿回 200(整份内容)—— 服务端不支持 Range。此时必须从头写,
            // 否则会把整份内容追加在已有片段后面,得到一个长度翻倍的坏文件。
            if (offset > 0 && !source.IsPartial)
            {
                offset = 0;
            }
            // 总长度优先信 HEAD;没有就用响应的 —— 206 时它只是本次区间的长度,要加回起点。
            long totalBytes = metadata?.ContentLength ?? Math.Max(0, offset + source.Length);
            var reporter = new S3ProgressReporter(progress, path.Name, totalBytes);

            await using (FileStream target = OpenLocalWrite(localPath, offset))
            {
                Stream sink = downloadBps > 0 ? new ThrottledStream(target, downloadBps) : target;
                try
                {
                    await CopyStreamAsync(source.Stream, sink, offset, reporter, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (!ReferenceEquals(sink, target))
                    {
                        await sink.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            reporter.ReportFinal(totalBytes);

            // 保留时间戳(设置 → 文件传输,scp -p 语义)。S3 不允许客户端设置对象的 mtime,
            // 因此只有下载方向能对等实现。尽力而为 —— 一次时间戳设置失败不该把已完成的下载标成失败。
            if (preserveTimestamps && (metadata?.LastModified ?? source.LastModified) is { } modified)
            {
                try
                {
                    File.SetLastWriteTimeUtc(localPath, modified.ToUniversalTime());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                {
                    // 时间戳只是尽力而为。
                }
            }
        }
        catch (Exception ex)
        {
            // 带上对象路径:一次多选下载里失败的可能只是其中一个,
            // 光说一句「S3 download failed」用户不知道是哪一个出的问题。
            throw Fault(sessionId, ex, $"download of {path}");
        }
    }

    // ---- 写 -----------------------------------------------------------------

    /// <summary>
    /// 上传一个文件。交给 <see cref="TransferUtility" />:它按阈值自动在单次 PUT 与分片上传之间选择,
    /// 分片并发发出,失败时中止残留的分片上传。
    /// <para>
    /// <paramref name="resumeOffset" /> 在 S3 上**没有意义,会被忽略**:S3 的写入是原子的,
    /// 失败的 PUT 不会在服务端留下半个对象,失败的分片上传则会被中止 ——
    /// 也就是说压根不存在「可以续在上面」的残留。这与 SFTP/FTP 那种「文件已经写了一半」的模型
    /// 是根本不同的,因此这里如实地整份重传,而不是假装续传。
    /// </para>
    /// </summary>
    public async Task UploadFileAsync(Guid sessionId,
        string localPath,
        string remotePath,
        IProgress<RemoteTransferProgress>? progress = null,
        long resumeOffset = 0,
        CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        S3ObjectPath path = RequireObject(remotePath, "upload");
        // 见方法文档:S3 的写入是原子的,服务端不存在可续的半个对象。
        _ = resumeOffset;
        try
        {
            var fileInfo = new FileInfo(localPath);
            long totalBytes = fileInfo.Length;
            var reporter = new S3ProgressReporter(progress, fileInfo.Name, totalBytes);
            (long uploadBps, _, _) = await GetTransferTuningAsync().ConfigureAwait(false);
            long partSize = ResolvePartSize(session.Info.Settings.EffectivePartSize, totalBytes);

            using var transfer = new TransferUtility(session.Client, new TransferUtilityConfig
            {
                ConcurrentServiceRequests = session.Info.Settings.EffectiveConcurrency,
                MinSizeBeforePartUpload = partSize,
            });
            var request = new TransferUtilityUploadRequest
            {
                BucketName = path.Bucket,
                Key = path.Key,
                PartSize = partSize,
                // 内容类型按扩展名推断:S3 会把它原样存下来并在下载时回给浏览器,
                // 全传 application/octet-stream 会让分享出去的图片/网页变成下载文件。
                ContentType = GuessContentType(path.Name),
            };
            if (session.Info.Settings.StorageClass is { Length: > 0 } storageClass)
            {
                request.StorageClass = S3StorageClass.FindValue(storageClass.Trim());
            }
            if (session.Info.Settings.ServerSideEncryption is { Length: > 0 } encryption)
            {
                request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.FindValue(encryption.Trim());
            }
            // **喂路径还是喂流,决定了分片能不能并发。**
            // TransferUtility 只在 IsSetFilePath() 为真时才按 ConcurrentServiceRequests 并发发分片;
            // 喂 InputStream 时它无法并行读同一个流,只能串行 —— 实测 40 MiB / 5 MiB 分片 /
            // 并发 8:喂流峰值并发 1、耗时 3536ms,喂路径峰值并发 8、耗时 420ms。
            // 所以只有开了限速才用包装流(那时瓶颈本就是速率整形,并发没有意义)。
            FileStream? source = null;
            if (uploadBps > 0)
            {
                source = OpenLocalRead(localPath);
                request.InputStream = new ThrottledStream(source, uploadBps);
                request.AutoCloseStream = false;
            }
            else
            {
                request.FilePath = localPath;
            }
            request.UploadProgressEvent += (_, args) => reporter.Report(args.TransferredBytes);
            try
            {
                await transfer.UploadAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (source is not null)
                {
                    // 包装流由 request 持有,随它一起走;这里只需收掉自己开的本地文件流。
                    await source.DisposeAsync().ConfigureAwait(false);
                }
            }
            reporter.ReportFinal(totalBytes);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "upload");
        }
    }

    /// <inheritdoc />
    public async Task CreateFileAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        S3ObjectPath path = RequireObject(remotePath, "create file");
        try
        {
            await session.Client.PutObjectAsync(new()
            {
                BucketName = path.Bucket,
                Key = path.Key,
                ContentBody = string.Empty,
                ContentType = GuessContentType(path.Name),
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "create file");
        }
    }

    /// <summary>
    /// 建目录。在桶这一层即「新建桶」;桶内则写一个以 <c>/</c> 结尾的零字节占位对象
    /// —— 那是 S3 生态里表达空目录的既成约定(AWS 控制台、s3fs、各家客户端都认它)。
    /// </summary>
    public async Task CreateDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        var path = S3ObjectPath.Parse(remotePath);
        try
        {
            if (path.IsRoot)
            {
                throw new VelaS3OperationException("Cannot create the S3 root.");
            }
            if (path.Key.Length == 0)
            {
                // 显式写出请求类型:目标类型推断在 PutBucketAsync(string,…) 与
                // PutBucketAsync(PutBucketRequest,…) 之间有二义性。
                await session.Client.PutBucketAsync(new PutBucketRequest { BucketName = path.Bucket }, cancellationToken).ConfigureAwait(false);
                return;
            }
            await session.Client.PutObjectAsync(new()
            {
                BucketName = path.Bucket,
                Key = path.Prefix,
                ContentBody = string.Empty,
                ContentType = "application/x-directory",
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "create directory");
        }
    }

    /// <inheritdoc />
    public async Task EnsureDirectoryAsync(Guid sessionId, string remotePath, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        var path = S3ObjectPath.Parse(remotePath);
        if (path.IsRoot)
        {
            return;
        }
        // 建桶不是幂等的(桶已存在会被拒),因此这一层先探再建;
        // 写占位对象本身是幂等的,直接走 CreateDirectoryAsync 即可。
        if (path.Key.Length == 0)
        {
            try
            {
                if (await BucketExistsAsync(session, path.Bucket, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                throw Fault(sessionId, ex, "ensure directory");
            }
        }
        await CreateDirectoryAsync(sessionId, remotePath, cancellationToken).ConfigureAwait(false);
    }

    // ---- 删除 ---------------------------------------------------------------

    /// <inheritdoc />
    public async Task DeleteAsync(Guid sessionId, string remotePath, IProgress<ProtocolDeleteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        var path = S3ObjectPath.Parse(remotePath);
        try
        {
            if (path.IsRoot)
            {
                throw new VelaS3OperationException("Cannot delete the S3 root.");
            }

            // 桶:只删空桶,**绝不代为清空**。
            //
            // S3 的 DeleteBucket 本身就要求桶是空的,这不是接口设计的疏漏,而正是它的安全属性:
            // 桶是顶层的、常被多方共享的资源,名字一旦释放可能被别人抢占。而在文件浏览器里,
            // 根视图下的桶与普通目录长得一模一样,删除确认也只会说一句「删除文件夹 xxx?」——
            // 若在这里代为清空,用户一次误点就会连带删掉桶内的全部对象,且确认框完全没有提示这一点。
            //
            // 因此这里如实把 S3 的 BucketNotEmpty 透出去。要删非空桶,用户需要先进桶里
            // 全选删除(那一步会给出准确的「删除 N 项」确认),再回来删桶。
            if (path.Key.Length == 0)
            {
                await session.Client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = path.Bucket }, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(1, 1, path.ToString()));
                return;
            }

            // 是对象就只删这一个;不是对象才按目录递归 —— 这样「删一个文件」绝不会
            // 顺手带走同名前缀下的东西。
            if (await TryHeadAsync(session, path.Bucket, path.Key, cancellationToken).ConfigureAwait(false) is not null)
            {
                await session.Client.DeleteObjectAsync(new() { BucketName = path.Bucket, Key = path.Key }, cancellationToken).ConfigureAwait(false);
                progress?.Report(new(1, 1, path.ToString()));
                return;
            }

            int deleted = await DeletePrefixAsync(session, path.Bucket, path.Prefix, progress, cancellationToken).ConfigureAwait(false);
            if (deleted == 0)
            {
                throw new VelaS3PathNotFoundException($"S3 path not found: {path}");
            }
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "delete");
        }
    }

    // ---- 复制 / 移动 --------------------------------------------------------

    /// <inheritdoc />
    public async Task RenameAsync(Guid sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        var source = S3ObjectPath.Parse(oldPath);
        var destination = S3ObjectPath.Parse(newPath);
        try
        {
            if (source.IsRoot || destination.IsRoot)
            {
                throw new VelaS3OperationException("Cannot rename the S3 root.");
            }
            // 桶不能改名 —— 这是 S3 的硬限制,不是本实现偷懒。
            if (source.Key.Length == 0 || destination.Key.Length == 0)
            {
                throw new VelaS3UnsupportedOperationException("S3 buckets cannot be renamed; create a new bucket and copy the objects.");
            }
            await CopyCoreAsync(session, source, destination, null, cancellationToken).ConfigureAwait(false);
            // 复制成功后才删源。顺序反过来的话,一次复制失败就等于永久丢数据。
            await DeleteAsync(sessionId, oldPath, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "rename");
        }
    }

    /// <inheritdoc />
    public async Task CopyAsync(Guid sessionId, string sourcePath, string destPath, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        S3Session session = Resolve(sessionId);
        var source = S3ObjectPath.Parse(sourcePath);
        var destination = S3ObjectPath.Parse(destPath);
        try
        {
            if (source.IsRoot || destination.IsRoot || source.Key.Length == 0 || destination.Key.Length == 0)
            {
                throw new VelaS3OperationException("S3 copy requires an object or prefix path on both sides.");
            }
            await CopyCoreAsync(session, source, destination, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Fault(sessionId, ex, "copy");
        }
    }

    // ---- 内部实现 -----------------------------------------------------------

    /// <summary>
    /// 复制一个对象或整棵前缀树。**全程服务端复制**,数据不经本地 —— 这是 S3 相对
    /// FTP 的一个实打实的优势(FTP 没有同站复制命令,只能下行再上行)。
    /// </summary>
    private static async Task CopyCoreAsync(
        S3Session session,
        S3ObjectPath source,
        S3ObjectPath destination,
        IProgress<RemoteTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (await TryHeadAsync(session, source.Bucket, source.Key, cancellationToken).ConfigureAwait(false) is { } metadata)
        {
            await CopySingleAsync(session, source, destination, metadata.ContentLength, cancellationToken).ConfigureAwait(false);
            progress?.Report(CompletedCopy(source.Name, metadata.ContentLength));
            return;
        }

        string sourcePrefix = source.Prefix;
        string destinationPrefix = destination.Prefix;
        int copied = 0;
        // 进度按**整棵树**的字节数报,不能每个对象各报一次 (size, size):
        // 宿主的节流器在第一次上报时就把 totalBytes 定死了,后面更大的对象会算出 >100%、
        // 更小的又被单调收敛钳住不动。因此先跑一趟只累加大小的列举拿总量。
        //
        // 两趟**流式**,而不是把整棵树物化进 List:一个百万对象的前缀那样要占几百 MB,
        // 而且必须列举完才能动第一个对象。多出的那趟 LIST 是 ceil(N/1000) 次请求,
        // 相对随后 N 次串行 CopyObject 可以忽略(同文件的 DeletePrefixAsync 也是流式的)。
        // 没人要进度时(重命名/移动传的是 null)连这趟都省掉。
        long totalBytes = 0;
        if (progress is not null)
        {
            await foreach (S3Object sizing in EnumerateAsync(session, source.Bucket, sourcePrefix, cancellationToken).ConfigureAwait(false))
            {
                totalBytes += sizing.Size ?? 0;
            }
        }
        long copiedBytes = 0;
        await foreach (S3Object entry in EnumerateAsync(session, source.Bucket, sourcePrefix, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = entry.Key ?? string.Empty;
            string relative = TrimPrefix(key, sourcePrefix);
            var target = new S3ObjectPath(destination.Bucket, destinationPrefix + relative);
            await CopySingleAsync(session, new(source.Bucket, key), target, entry.Size ?? 0, cancellationToken).ConfigureAwait(false);
            copied++;
            copiedBytes += entry.Size ?? 0;
            progress?.Report(new(copiedBytes, totalBytes));
        }
        if (copied == 0)
        {
            throw new VelaS3PathNotFoundException($"S3 path not found: {source}");
        }
    }

    /// <summary>
    /// 复制单个对象。超过 5 GiB 的对象**不能**用 CopyObject(协议硬限制),
    /// 必须改用分片复制:发起一个分片上传,每片以 <c>CopyPart</c> 从源对象取一段。
    /// </summary>
    private static async Task CopySingleAsync(
        S3Session session,
        S3ObjectPath source,
        S3ObjectPath destination,
        long size,
        CancellationToken cancellationToken)
    {
        if (size <= S3Settings.MaxSinglePutBytes)
        {
            await session.Client.CopyObjectAsync(new()
            {
                SourceBucket = source.Bucket,
                SourceKey = source.Key,
                DestinationBucket = destination.Bucket,
                DestinationKey = destination.Key,
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        long partSize = ResolvePartSize(session.Info.Settings.EffectivePartSize, size);
        InitiateMultipartUploadResponse initiated = await session.Client.InitiateMultipartUploadAsync(new()
        {
            BucketName = destination.Bucket,
            Key = destination.Key,
            ContentType = GuessContentType(destination.Name),
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            List<PartETag> parts = [];
            int partNumber = 1;
            for (long offset = 0; offset < size; offset += partSize, partNumber++)
            {
                long end = Math.Min(offset + partSize, size) - 1;
                CopyPartResponse part = await session.Client.CopyPartAsync(new()
                {
                    SourceBucket = source.Bucket,
                    SourceKey = source.Key,
                    DestinationBucket = destination.Bucket,
                    DestinationKey = destination.Key,
                    UploadId = initiated.UploadId,
                    PartNumber = partNumber,
                    FirstByte = offset,
                    LastByte = end,
                }, cancellationToken).ConfigureAwait(false);
                parts.Add(new(partNumber, part.ETag));
            }
            await session.Client.CompleteMultipartUploadAsync(new()
            {
                BucketName = destination.Bucket,
                Key = destination.Key,
                UploadId = initiated.UploadId,
                PartETags = parts,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 中止失败只会在服务端留下残片,不该覆盖用户真正需要看到的那个错误。
            try
            {
                await session.Client.AbortMultipartUploadAsync(new()
                {
                    BucketName = destination.Bucket,
                    Key = destination.Key,
                    UploadId = initiated.UploadId,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 收尾期的失败无人可报。
            }
            throw;
        }
    }

    /// <summary>递归删掉某个前缀下的全部对象,按批回报进度;返回删除的对象数。</summary>
    private static async Task<int> DeletePrefixAsync(
        S3Session session,
        string bucket,
        string prefix,
        IProgress<ProtocolDeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        int deleted = 0;
        List<KeyVersion> batch = [with(DeleteBatchSize)];
        string lastKey = string.Empty;
        await foreach (S3Object entry in EnumerateAsync(session, bucket, prefix, cancellationToken).ConfigureAwait(false))
        {
            batch.Add(new() { Key = entry.Key });
            lastKey = entry.Key ?? lastKey;
            if (batch.Count < DeleteBatchSize)
            {
                continue;
            }
            deleted += await FlushAsync().ConfigureAwait(false);
        }
        if (batch.Count > 0)
        {
            deleted += await FlushAsync().ConfigureAwait(false);
        }
        return deleted;

        async Task<int> FlushAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 一次往返删 1000 个,而不是 1000 次 DELETE —— 这是删大目录能不能用的关键。
            DeleteObjectsResponse response = await session.Client.DeleteObjectsAsync(new()
            {
                BucketName = bucket,
                Objects = [.. batch],
            }, cancellationToken).ConfigureAwait(false);
            if (response.DeleteErrors is { Count: > 0 } errors)
            {
                DeleteError first = errors[0];
                throw new VelaS3OperationException($"S3 delete failed for {first.Key}: {first.Code} {first.Message}")
                {
                    ErrorCode = first.Code ?? string.Empty,
                };
            }
            int count = batch.Count;
            // 总数未知(列举与删除是流式交错的),用「已删数」同时充当总数,
            // 进度条因此表现为持续推进而不是一个会跳变的百分比。
            progress?.Report(new(deleted + count, deleted + count, lastKey));
            batch.Clear();
            return count;
        }
    }

    /// <summary>流式枚举某个前缀下的全部对象(不带分隔符,即递归)。</summary>
    private static async IAsyncEnumerable<S3Object> EnumerateAsync(
        S3Session session,
        string bucket,
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? token = null;
        do
        {
            ListObjectsV2Response page = await session.Client.ListObjectsV2Async(new()
            {
                BucketName = bucket,
                Prefix = prefix,
                MaxKeys = ListPageSize,
                ContinuationToken = token,
            }, cancellationToken).ConfigureAwait(false);
            foreach (S3Object entry in page.S3Objects ?? [])
            {
                yield return entry;
            }
            token = (page.IsTruncated ?? false) ? page.NextContinuationToken : null;
        }
        while (token is { Length: > 0 });
    }

    /// <summary>该路径下是否有任何东西(即它是否可以被当作一个目录)。</summary>
    private static async Task<bool> IsPrefixAsync(S3Session session, S3ObjectPath path, CancellationToken cancellationToken)
    {
        ListObjectsV2Response page = await session.Client.ListObjectsV2Async(new()
        {
            BucketName = path.Bucket,
            Prefix = path.Prefix,
            Delimiter = "/",
            MaxKeys = 1,
        }, cancellationToken).ConfigureAwait(false);
        return (page.S3Objects?.Count ?? 0) > 0 || (page.CommonPrefixes?.Count ?? 0) > 0;
    }

    /// <summary>取对象元数据;不存在时返回 null(而不是抛异常)。</summary>
    private static async Task<GetObjectMetadataResponse?> TryHeadAsync(S3Session session, string bucket, string key, CancellationToken cancellationToken)
    {
        try
        {
            return await session.Client
                .GetObjectMetadataAsync(new() { BucketName = bucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// 取一个对象的内容流:先直取(带 Authorization 头的 GetObject),被拒就改用**预签名 URL** 再取一次。
    /// <para>
    /// 为什么值得多试这一次:预签名把凭证放在**查询串**里而不是 Authorization 头里,
    /// 两者在服务端过的常常不是同一条路 —— 桶策略可以只放行预签名形式;
    /// 端点前挂的 CDN / 网关也普遍会剥掉或改写 Authorization 头(那会连带毁掉签名),
    /// 却对查询串照单放行。现实里"直接下载不给、预签名下载给"的桶是存在的,
    /// 遇到这种桶就报个 403 收工,对用户来说就是"这文件下不了",而它明明下得了。
    /// </para>
    /// <para>
    /// 只在 401/403 上回退:404 再试一遍还是没有,5xx 由 SDK 自己重试过了,
    /// 网络错误换条 URL 也一样不通 —— 那些情况下多打一趟只是白等。
    /// </para>
    /// </summary>
    private static async Task<ObjectSource> OpenObjectAsync(S3Session session,
        S3ObjectPath path,
        long offset,
        long? totalBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new GetObjectRequest { BucketName = path.Bucket, Key = path.Key };
            if (offset > 0)
            {
                // SDK 的 ByteRange 必须给终点。知道总长就用真实终点,别拿一个天文数字去赌
                // 网关按 RFC 把越界的 last-byte-pos 截到 EOF —— 有的实现会直接回 416。
                request.ByteRange = new(offset, (totalBytes ?? long.MaxValue) - 1);
            }
            GetObjectResponse response = await session.Client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return new(response.ResponseStream, response, response.ContentLength,
                response.HttpStatusCode == HttpStatusCode.PartialContent, response.LastModified);
        }
        catch (AmazonS3Exception denied) when (denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return await OpenPresignedAsync(session, path, offset, denied, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>用预签名 URL 直取对象内容(不带 Authorization 头,凭证在查询串里)。</summary>
    private static async Task<ObjectSource> OpenPresignedAsync(S3Session session,
        S3ObjectPath path,
        long offset,
        AmazonS3Exception denied,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            // 有效期只要覆盖到"服务端受理这次请求"那一刻:S3 在请求开始时校验过期,
            // 之后传多久都不影响。给一小时是留给排队中的传输,不是给传输本身。
            string url = session.Client.GetPreSignedURL(new()
            {
                BucketName = path.Bucket,
                Key = path.Key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.AddHours(1),
                // 见 CreatePresignedUrlAsync:不给这一条,明文端点会被签成 https:// 而连不上。
                Protocol = session.Client.Config.UseHttp ? Amazon.S3.Protocol.HTTP : Amazon.S3.Protocol.HTTPS,
            });
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0)
            {
                // 预签名默认只签 host 头,Range 不在签名内,可以照常带上。
                request.Headers.Range = new(offset, null);
            }
            response = await session.Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 两条路都不通:报**直取**那次的错。预签名是我们自作主张多试的一次,
                // 拿它的失败去解释"为什么下不了"只会把人往错误的方向带。
                throw denied;
            }
            Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var source = new ObjectSource(content, response, response.Content.Headers.ContentLength ?? -1,
                response.StatusCode == HttpStatusCode.PartialContent,
                response.Content.Headers.LastModified?.UtcDateTime);
            response = null; // 所有权移交给 source
            return source;
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// 下载专用的 HEAD:**任何**失败都只当作「没问到元数据」返回 null,由随后的 GET 定成败。
    /// <para>
    /// 与 <see cref="TryHeadAsync" /> 只吞 404 的口径不同,是因为两者的用途不同 ——
    /// 那个的返回值就是判定结果(存在/不存在),吞掉 403 会把「没权限看」误判成「不存在」;
    /// 这里的返回值只是**优化用的附加信息**(总长度、mtime、能否续传),缺了它下载照样能做。
    /// </para>
    /// <para>
    /// 取消要原样抛出:那是用户按了取消,不是「HEAD 没问到」。
    /// </para>
    /// </summary>
    private static async Task<GetObjectMetadataResponse?> TryHeadForDownloadAsync(S3Session session, S3ObjectPath path, CancellationToken cancellationToken)
    {
        try
        {
            return await session.Client
                .GetObjectMetadataAsync(new() { BucketName = path.Bucket, Key = path.Key }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AmazonServiceException)
        {
            return null;
        }
        catch (AmazonClientException)
        {
            return null;
        }
    }

    private static async Task<bool> BucketExistsAsync(S3Session session, string bucket, CancellationToken cancellationToken)
    {
        try
        {
            // HeadBucket 是最便宜的探测,且只授予单桶权限的账号也能调。
            await session.Client.ListObjectsV2Async(
                new() { BucketName = bucket, MaxKeys = 1 }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// 把分片大小调整到「既不小于配置值,又能让分片数不超过 10000」。
    /// 少了这一步,一个 100GB 的文件在 8MiB 分片下需要 12800 片,会在第 10001 片上被服务端拒掉。
    /// </summary>
    private static long ResolvePartSize(long configured, long totalBytes)
    {
        long minimum = totalBytes / S3Settings.MaxPartCount + 1;
        return Math.Clamp(Math.Max(configured, minimum), S3Settings.MinPartSizeBytes, S3Settings.MaxPartSizeBytes);
    }

    /// <summary>以本地残留文件的实际长度核实续传起点;对不上就从头下。</summary>
    private static long ResolveDownloadResume(string localPath, long totalBytes, long requested)
    {
        if (requested <= 0)
        {
            return 0;
        }
        var info = new FileInfo(localPath);
        if (!info.Exists)
        {
            return 0;
        }
        long usable = Math.Min(requested, info.Length);
        // 已经下完了就别再发一个越界的 Range —— 服务端会回 416。
        return usable >= totalBytes ? 0 : Math.Max(0, usable);
    }

    private static async Task CopyStreamAsync(Stream source, Stream target, long alreadyDone, S3ProgressReporter reporter, CancellationToken cancellationToken)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long transferred = alreadyDone;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                transferred += read;
                reporter.Report(transferred);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static FileStream OpenLocalRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, LocalStreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream OpenLocalWrite(string path, long offset)
    {
        if (offset <= 0)
        {
            return new(path, FileMode.Create, FileAccess.Write, FileShare.None, LocalStreamBufferSize, FileOptions.Asynchronous);
        }
        // 显式截断到续传起点再定位过去:FileMode.Append 追加在「文件实际末尾」,
        // 与核实过的起点未必一致,对不上就会在文件里留下空隙(SftpService 踩过同一个坑)。
        var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, LocalStreamBufferSize, FileOptions.Asynchronous);
        stream.SetLength(offset);
        stream.Seek(offset, SeekOrigin.Begin);
        return stream;
    }

    /// <summary>
    /// 带宽限制与时间戳策略。这些是**宿主的全局设置**(设置 → 文件传输),因此每次传输前
    /// 现问一次宿主而不是在连接时快照 —— 用户中途调了限速,当前正跑着的传输就该跟着变。
    /// 宿主没提供这项能力时按"不限速、保留时间戳"退化,而不是让传输失败。
    /// </summary>
    private async Task<(long UploadBps, long DownloadBps, bool PreserveTimestamps)> GetTransferTuningAsync()
    {
        if (protocols is null)
        {
            return (0, 0, true);
        }
        try
        {
            ProtocolTransferOptions options = await protocols.GetTransferOptionsAsync().ConfigureAwait(false);
            return (Math.Max(0, options.UploadBytesPerSecond), Math.Max(0, options.DownloadBytesPerSecond), options.PreserveTimestamps);
        }
        catch
        {
            return (0, 0, true);
        }
    }

    private S3Session Resolve(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out S3Session? session)
            ? session
            : throw new VelaS3ConnectionException($"S3 session {sessionId} is not open.");

    /// <summary>
    /// 把异常翻译成中立异常;若属于连接级失败,顺带把该会话标记为已失效并广播出去
    /// —— 资源管理器树的状态圆点据此自动从「活跃」变「离线」(与 FTP 侧同一套机制)。
    /// </summary>
    private Exception Fault(Guid sessionId, Exception ex, string operation)
    {
        _sessions.TryGetValue(sessionId, out S3Session? session);
        Exception translated = S3Interop.Translate(ex, operation, session?.Probe);
        if (S3Interop.IsConnectionLost(translated) && session is not null)
        {
            SessionStateChanged?.Invoke(this, new(session.Key, ProtocolSessionState.Faulted));
        }
        return translated;
    }

    private static S3ObjectPath RequireObject(string remotePath, string operation)
    {
        var path = S3ObjectPath.Parse(remotePath);
        return path.IsRoot || path.Key.Length == 0
            ? throw new VelaS3OperationException($"S3 {operation} requires an object path (/bucket/key), got '{remotePath}'.")
            : path;
    }

    /// <summary>
    /// 服务端复制没有"传输中"这一说:它要么整份完成,要么失败。因此按 100% 一次报完
    /// (名字宿主自己从路径取,SDK 的进度只带字节数)。
    /// </summary>
    private static RemoteTransferProgress CompletedCopy(string name, long size)
    {
        _ = name;
        return new(size, size);
    }

    private static S3FileEntry DirectoryEntry(string name, string fullPath) =>
        new()
        {
            Name = name,
            FullPath = fullPath,
            Size = 0,
            IsDirectory = true,
            LastModified = DateTime.MinValue,
            Permissions = string.Empty,
            Owner = string.Empty,
            Group = string.Empty,
        };

    private static string TrimPrefix(string key, string prefix) =>
        prefix.Length > 0 && key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;

    /// <summary>
    /// 缺失或极小的时间戳直接给 <see cref="DateTime.MinValue" />:
    /// 走本地时区换算会在 UTC+n 时区上溢出。
    /// </summary>
    private static DateTime ToLocalDateTime(DateTime? value) =>
        value is not { } instant || instant == DateTime.MinValue
            ? DateTime.MinValue
            : instant.Kind == DateTimeKind.Utc
                ? instant.ToLocalTime()
                : instant;

    /// <summary>
    /// 按扩展名猜内容类型。S3 会把这个值原样存下来并在下载时回给浏览器 ——
    /// 全传 <c>application/octet-stream</c> 会让直接分享出去的图片/网页变成下载文件。
    /// </summary>
    internal static string GuessContentType(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.ToLowerInvariant() switch
        {
            ".txt" or ".log" or ".md" => "text/plain; charset=utf-8",
            ".htm" or ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".xml" => "application/xml; charset=utf-8",
            ".yaml" or ".yml" => "application/yaml; charset=utf-8",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".gz" or ".tgz" => "application/gzip",
            ".tar" => "application/x-tar",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream",
        };
    }

    /// <summary>一条 S3 会话持有的东西。</summary>
    private sealed class S3Session(AmazonS3Client client, S3ConnectionInfo info, S3CertificateProbe probe, string key) : IDisposable
    {
        public AmazonS3Client Client { get; } = client;

        public S3ConnectionInfo Info { get; } = info;

        /// <summary>与该会话的 HTTP 客户端共用的证书探针。</summary>
        public S3CertificateProbe Probe { get; } = probe;

        /// <summary>宿主给的不透明会话键(上报会话状态时要用它,宿主认不出内部 Guid)。</summary>
        public string Key { get; } = key;

        /// <summary>
        /// 取预签名 URL 用的 HTTP 客户端(**不经 SDK**,因此不会带上 Authorization 头)。
        /// <para>
        /// 按会话懒建一份:证书信任回调必须和 SDK 那份是同一个探针,否则用户在连接时
        /// 点过"信任该证书"的自签端点,到了预签名这条路上又会被判为不可信。
        /// 走到这里的会话本就是遇上过 403 的少数,不必给每条会话都预先造一个。
        /// </para>
        /// </summary>
        public HttpClient Http
        {
            get
            {
                lock (_httpGate)
                {
                    if (_http is not null)
                    {
                        return _http;
                    }
                    var handler = new SocketsHttpHandler
                    {
                        // 预签名把凭证放在查询串里,跟着跳转会把它原样送去另一个主机。
                        // 与 SDK 那份保持一致:不自动跟随。
                        AllowAutoRedirect = false,
                        AutomaticDecompression = DecompressionMethods.None,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                        ConnectTimeout = TimeSpan.FromSeconds(15),
                    };
                    if (Info.Settings.UseTls)
                    {
                        handler.SslOptions.RemoteCertificateValidationCallback = Probe.Validate;
                    }
                    // 单次请求超时交给调用方的 CancellationToken:大文件要传几十分钟。
                    _http = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
                    return _http;
                }
            }
        }

        private readonly Lock _httpGate = new();
        private HttpClient? _http;

        public void Dispose()
        {
            Client.Dispose();
            lock (_httpGate)
            {
                _http?.Dispose();
                _http = null;
            }
        }
    }

    /// <summary>
    /// 一次对象读取的数据源。两条来路(SDK 的 GetObject 响应、预签名 URL 的 HTTP 响应)
    /// 在这里被抹平成同一副样子,下载与预览因此只写一遍拷贝逻辑。
    /// </summary>
    /// <param name="stream">内容流。</param>
    /// <param name="owner">持有底层连接的响应对象,与流同生共死。</param>
    /// <param name="length">**本次响应体**的长度(206 时只是区间长度,不是对象总长)。</param>
    /// <param name="isPartial">服务端是否真的按 Range 回了 206。</param>
    /// <param name="lastModified">对象的最后修改时间;响应没带则为 null。</param>
    private sealed class ObjectSource(Stream stream, IDisposable owner, long length, bool isPartial, DateTime? lastModified) : IDisposable
    {
        public Stream Stream { get; } = stream;

        public IDisposable Owner { get; } = owner;

        public long Length { get; } = length;

        public bool IsPartial { get; } = isPartial;

        public DateTime? LastModified { get; } = lastModified;

        public void Dispose()
        {
            Stream.Dispose();
            Owner.Dispose();
        }
    }

    /// <summary>
    /// 绑定了一次响应的只读流:调用方释放流时连同释放响应(它持有底层连接)。
    /// 与 FTP 侧的 <c>LeasedStream</c> 同样的理由 —— 网络流的生命周期就是这个流的生命周期。
    /// </summary>
    private sealed class ResponseBoundStream(Stream inner, IDisposable response, long length) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => throw new NotSupportedException("S3 response streams are sequential.");
            set => throw new NotSupportedException("S3 response streams are sequential.");
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("S3 response streams are not seekable.");

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
