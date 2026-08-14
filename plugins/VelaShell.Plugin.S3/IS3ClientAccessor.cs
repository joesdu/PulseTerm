using Amazon.S3;

namespace VelaShell.Plugin.S3;

/// <summary>
/// 让同一条 S3 会话上的其它服务(目前是 <see cref="S3ManagementService" />)拿到底层客户端。
/// <para>
/// **刻意保持 <c>internal</c>**:它的成员暴露 <see cref="IAmazonS3" />,那是具体库的类型。
/// 插件边界上对外只有 SDK 的 <c>IProtocolFileSystem</c> 与中立的
/// <see cref="IS3ManagementService" /> —— 与宿主当年对 FluentFTP / Tmds.Ssh 的约束是同一条规矩,
/// 只是边界从 Infrastructure 挪到了插件程序集。
/// </para>
/// <para>
/// 之所以要共享而不是各建各的客户端:一条会话就该只有一份凭据、一份连接池、一份证书信任状态。
/// 让「管理」操作另开一个客户端,会在断线判定、证书提示、连接数上各自为政。
/// </para>
/// </summary>
internal interface IS3ClientAccessor
{
    /// <summary>取该会话的客户端;会话不存在时抛 <see cref="VelaS3ConnectionException" />。</summary>
    IAmazonS3 GetClient(Guid sessionId);

    /// <summary>取该会话的连接参数(区域、端点、默认桶等)。</summary>
    S3ConnectionInfo GetConnectionInfo(Guid sessionId);

    /// <summary>
    /// 按该会话的上下文翻译异常,并在属于连接级失败时把会话标记为离线。
    /// 管理操作与文件操作共用同一套「掉线 → 树上状态圆点变灰」的机制。
    /// </summary>
    Exception TranslateFault(Guid sessionId, Exception exception, string operation);
}
