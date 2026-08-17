namespace VelaShell.Plugin.S3;

/// <summary>协议专属右键动作的 id。落进宿主的菜单里,改名等于让菜单项失灵。</summary>
public static class S3Actions
{
    /// <summary>复制分享链接(预签名 URL)。</summary>
    public const string CopyShareLink = "copy-share-link";

    /// <summary>打开对象检视器。</summary>
    public const string InspectObject = "inspect-object";

    /// <summary>打开桶管理器。</summary>
    public const string ManageBucket = "manage-bucket";
}

/// <summary>
/// 动作的落地处置。把它单列成接口,是为了让 <see cref="S3ProtocolFileSystem" /> 保持"只懂协议"——
/// 它不认识 Avalonia,也不该认识;开面板与写剪贴板由插件入口那一侧接上宿主能力。
/// 单测里给个假的实现即可验证"右键某个对象会带着正确的桶/键去开检视器"。
/// </summary>
public interface IS3ActionHandler
{
    /// <summary>把分享链接写入系统剪贴板。</summary>
    /// <param name="url">预签名 URL。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task CopyShareLinkAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>打开某个对象的检视器面板。</summary>
    /// <param name="sessionId">内部会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="key">对象键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task OpenObjectInspectorAsync(Guid sessionId, string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>打开某个桶的管理器面板。</summary>
    /// <param name="sessionId">内部会话标识。</param>
    /// <param name="bucket">桶名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task OpenBucketManagerAsync(Guid sessionId, string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// 关掉某条会话名下打开的全部面板。会话一关,面板里的每次调用都会撞上
    /// "session is not open" —— 留着一扇只会报错的窗口比直接关掉更糟。
    /// </summary>
    /// <param name="sessionId">内部会话标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task CloseSessionPanelsAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
