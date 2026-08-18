using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.ViewModels;

/// <summary>
/// 连接配置页需要从插件描述里知道的**全部**东西,与它是文件协议还是工作台无关。
/// <para>
/// 两种连接类型(<see cref="ProtocolDescriptor" /> / <see cref="WorkspaceDescriptor" />)在
/// 连接对话框上的表现完全一致:同一排页签、同一套声明式字段、同样的三格标签改写、
/// 同样的匿名判定。把差异收在这个转换器里,视图模型就只有一条路径 ——
/// 否则每个用到描述符的地方都要写一次"要么问这个、要么问那个"。
/// </para>
/// </summary>
/// <param name="Kind">形态(宿主打开会话时据此决定画文件浏览器还是工作台文档)。</param>
/// <param name="HostLabel">"主机"输入框的标签改写;null 用宿主默认。</param>
/// <param name="HostPlaceholder">"主机"输入框的占位提示改写。</param>
/// <param name="UsernameLabel">"用户名"输入框的标签改写。</param>
/// <param name="PasswordLabel">"密码"输入框的标签改写。</param>
/// <param name="Fields">声明式设置字段。</param>
/// <param name="AllowsAnonymous">是否允许不填凭据直接连(决定要不要弹登录框、要不要灰掉按钮)。</param>
/// <param name="SupportsSshTunnel">是否支持经 SSH 隧道抵达(决定是否渲染"经 SSH 隧道"一节)。</param>
/// <param name="ShowsCredentials">
/// 是否显示用户名/口令两栏。声明了 <see cref="ProtocolFeatures.NoCredentials" /> 的协议
/// (Telnet 这种登录发生在带内的)一律收起 —— 摆着两个填了也发不出去的框,
/// 只会让用户以为填上就能自动登录。工作台形态目前都有凭据(ACL 用户 + 口令)。
/// </param>
internal sealed record PluginConnectionForm(
    PluginConnectionKind Kind,
    string? HostLabel,
    string? HostPlaceholder,
    string? UsernameLabel,
    string? PasswordLabel,
    IReadOnlyList<ProtocolSettingField> Fields,
    bool AllowsAnonymous,
    bool SupportsSshTunnel,
    bool ShowsCredentials)
{
    /// <summary>从文件协议描述转换。</summary>
    /// <param name="descriptor">协议描述。</param>
    /// <returns>统一表单模型。</returns>
    public static PluginConnectionForm From(ProtocolDescriptor descriptor) =>
        new(PluginConnectionKind.FileSystem,
            descriptor.HostLabel,
            descriptor.HostPlaceholder,
            descriptor.UsernameLabel,
            descriptor.PasswordLabel,
            descriptor.Fields,
            // NoCredentials 蕴含匿名:没有凭据这回事(Telnet),就不能拿"用户名没填"
            // 把连接按钮堵死。
            descriptor.Features.HasFlag(ProtocolFeatures.AnonymousAccess)
            || descriptor.Features.HasFlag(ProtocolFeatures.NoCredentials),
            // 文件协议侧暂不支持声明式隧道:它的会话由宿主的文件服务持有,
            // 隧道租约要跟着那条会话走,与工作台文档的生命周期不是一回事。
            SupportsSshTunnel: false,
            ShowsCredentials: !descriptor.Features.HasFlag(ProtocolFeatures.NoCredentials));

    /// <summary>从工作台描述转换。</summary>
    /// <param name="descriptor">连接类型描述。</param>
    /// <returns>统一表单模型。</returns>
    public static PluginConnectionForm From(WorkspaceDescriptor descriptor) =>
        new(PluginConnectionKind.Workspace,
            descriptor.HostLabel,
            descriptor.HostPlaceholder,
            descriptor.UsernameLabel,
            descriptor.PasswordLabel,
            descriptor.Fields,
            descriptor.Features.HasFlag(WorkspaceFeatures.AnonymousAccess),
            descriptor.Features.HasFlag(WorkspaceFeatures.SshTunnel),
            ShowsCredentials: true);
}
