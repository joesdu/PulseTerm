namespace VelaShell.PluginSdk.Protocols;

/// <summary>
/// 宿主当前的文件传输设置(设置 → 文件传输)。协议实现应在**每次传输开始时**取一次
/// 而不是在连接时快照:用户中途调了限速,正在跑的传输就该跟着变。
/// </summary>
/// <param name="UploadBytesPerSecond">上传限速(字节/秒);0 = 不限。</param>
/// <param name="DownloadBytesPerSecond">下载限速(字节/秒);0 = 不限。</param>
/// <param name="PreserveTimestamps">下载后是否把远端修改时间写回本地文件(<c>scp -p</c> 语义)。</param>
public readonly record struct ProtocolTransferOptions(
    long UploadBytesPerSecond,
    long DownloadBytesPerSecond,
    bool PreserveTimestamps);

/// <summary>
/// 协议能力:把插件实现的远程文件协议接进宿主,使它在连接配置页里与 SSH/SFTP/FTP 同为一等公民。
/// <para>
/// 用法(通常在 <see cref="IVelaPlugin.ActivateAsync" /> 里一次注册完):
/// </para>
/// <code>
/// context.Protocols.Register(
///     new ProtocolDescriptor
///     {
///         Id = context.PluginId,          // 或 $"{context.PluginId}.&lt;子协议&gt;"
///         DisplayName = "S3",
///         DefaultPort = 443,
///         Fields = [ new() { Key = "region", Label = "区域", DefaultValue = "us-east-1" } ],
///         Features = ProtocolFeatures.ServerSideCopy | ProtocolFeatures.AnonymousAccess
///     },
///     new MyFileSystem(context));
/// </code>
/// <para>
/// 纪律与边界:
/// </para>
/// <list type="bullet">
///   <item>协议 id 必须等于插件 id 或以 <c>&lt;插件id&gt;.</c> 开头,否则注册被拒。</item>
///   <item>要让协议在**装载插件之前**就出现在连接配置页,须在 <c>plugin.json</c> 的
///     <c>contributes.protocols</c> 里同时声明;配 <c>onProtocol:&lt;协议id&gt;</c>
///     激活事件即可做到"用户点到这个页签才装载插件"。</item>
///   <item>本能力**仅 <c>inProcess</c> 宿主模式可用**:协议是宿主反向调用插件的高频通道
///     (含流式读),隔离进程的 RPC 目前只承载插件→宿主的请求。声明了
///     <c>contributes.protocols</c> 却又要 <c>isolated</c> 的清单会被直接拒绝。</item>
///   <item>停用插件时全部注册自动撤销,其上已建立的会话由宿主关闭。</item>
/// </list>
/// </summary>
public interface IProtocolsApi
{
    /// <summary>
    /// 注册一种协议;释放返回值即注销(同 id 重复注册按替换处理)。
    /// </summary>
    /// <param name="descriptor">协议描述(页签名称、默认端口、设置表单、右键动作、能力位)。</param>
    /// <param name="fileSystem">该协议的文件系统实现。</param>
    /// <returns>注销句柄。</returns>
    /// <exception cref="ArgumentException">协议 id 非法或未以插件 id 为前缀。</exception>
    IDisposable Register(ProtocolDescriptor descriptor, IProtocolFileSystem fileSystem);

    /// <summary>
    /// 读取宿主当前的传输设置(限速与时间戳策略)。
    /// <para>
    /// 限速为什么由宿主给而不是插件自己开一个设置:它是**全局的**用户偏好,
    /// 对 SFTP/FTP/插件协议一视同仁 —— 让每个协议插件各配一份,用户就得配三遍,
    /// 而且"总带宽上限"这件事从此再也说不清。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前传输设置;宿主未提供设置服务时为"不限速、保留时间戳"。</returns>
    Task<ProtocolTransferOptions> GetTransferOptionsAsync(CancellationToken cancellationToken = default);
}
