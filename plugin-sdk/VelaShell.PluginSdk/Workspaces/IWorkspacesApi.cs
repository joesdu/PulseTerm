namespace VelaShell.PluginSdk.Workspaces;

/// <summary>
/// 工作台能力:把插件自建的**非文件型**连接(Redis、MySQL、Kafka…)接进宿主,
/// 使它在连接配置页里与 SSH/SFTP/FTP 同为一等公民。
/// <para>
/// 用法(通常在 <see cref="IVelaPlugin.ActivateAsync" /> 里一次注册完):
/// </para>
/// <code>
/// context.Workspaces.Register(
///     new WorkspaceDescriptor
///     {
///         Id = context.PluginId,
///         DisplayName = "Redis",
///         DefaultPort = 6379,
///         Features = WorkspaceFeatures.SshTunnel | WorkspaceFeatures.AnonymousAccess,
///         Fields = [ new() { Key = "mode", Label = "部署形态", Kind = ProtocolSettingKind.Choice, ... } ]
///     },
///     new RedisWorkspaceProvider(context));
/// </code>
/// <para>
/// 纪律与边界:
/// </para>
/// <list type="bullet">
///   <item>连接类型 id 必须等于插件 id 或以 <c>&lt;插件id&gt;.</c> 开头,否则注册被拒。</item>
///   <item>要让页签在**装载插件之前**就出现在连接配置页,须在 <c>plugin.json</c> 的
///     <c>contributes.workspaces</c> 里同时声明;配 <c>onWorkspace:&lt;id&gt;</c>
///     激活事件即可做到"用户点到这个页签才装载插件"。</item>
///   <item>本能力**仅 <c>inProcess</c> 宿主模式可用**:宿主要向插件索取一个 Avalonia 控件
///     挂进停靠区,而原生控件无法跨进程嵌入(蓝图 08 已弃用跨进程收养)。声明了
///     <c>contributes.workspaces</c> 却又要 <c>isolated</c> 的清单会被直接拒绝。</item>
///   <item>停用插件时全部注册自动撤销,其上已打开的文档由宿主关闭。</item>
/// </list>
/// </summary>
public interface IWorkspacesApi
{
    /// <summary>
    /// 注册一种工作台连接类型;释放返回值即注销(同 id 重复注册按替换处理)。
    /// </summary>
    /// <param name="descriptor">连接类型描述(页签名称、默认端口、设置表单、能力位)。</param>
    /// <param name="provider">该连接类型的实现。</param>
    /// <returns>注销句柄。</returns>
    /// <exception cref="ArgumentException">id 非法或未以插件 id 为前缀。</exception>
    IDisposable Register(WorkspaceDescriptor descriptor, IWorkspaceProvider provider);

    /// <summary>
    /// 提议一条连接:宿主打开自己的「新建连接」对话框并按提议预填。
    /// <para>
    /// 用于"插件探测到了某个服务,想把它变成一条连接"的场景(如从 SSH 会话里发现一个
    /// Redis 实例)。**插件不能自己写宿主的会话库** —— 那是用户数据,凭据也在里面;
    /// 它只能提议,由用户在宿主的对话框里过一眼再按保存。这条边界让"零打字建连"
    /// 与"插件不碰用户配置"两件事同时成立。
    /// </para>
    /// </summary>
    /// <param name="proposal">提议内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>用户是否保存了这条连接;宿主没有界面时为 <see langword="false" />。</returns>
    /// <exception cref="ArgumentException">提议的连接类型 id 不属于本插件。</exception>
    Task<bool> ProposeConnectionAsync(WorkspaceConnectionProposal proposal, CancellationToken cancellationToken = default);
}
