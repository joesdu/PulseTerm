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
/// 只会让用户以为填上就能自动登录。工作台侧的同名能力位
/// (<see cref="WorkspaceFeatures.NoCredentials" />)一并生效。
/// </param>
/// <param name="ShowsPort">
/// 是否显示"端口"那一栏。声明了 <c>NoEndpoint</c> 的连接类型收起它 ——
/// 目标不是一个 <c>host:port</c> 时(SQLite 是磁盘上的一个文件、串口是一根线),
/// 那一栏填什么都不会被用上,而且还留着上一个协议的残值。
/// <para>
/// <b>只收端口,不收主机</b>:主机那一栏恰恰要留着装目标(文件路径 / 设备名)。
/// 端口的**取值**也照旧参与按钮可用性判定 —— 收起一栏不该顺手把按钮堵死。
/// </para>
/// </param>
/// <param name="HostKind">
/// "主机"那一栏的输入形态。默认 <see cref="ProtocolSettingKind.Text" />;
/// 取 <see cref="ProtocolSettingKind.DynamicChoice" /> 时渲染成可刷新的下拉
/// (串口:本机端口是可枚举、且会热插拔的)。
/// </param>
/// <param name="HostChoices">主机栏做成下拉时的候选项(动态形态下是兜底列表)。</param>
/// <param name="HostAllowsCustomValue">主机栏的下拉是否可手输。</param>
/// <param name="ChoiceSource">
/// 动态候选项的来源:协议实现自己(它同时实现了 <see cref="IProtocolChoiceSource" />)。
/// 没实现就是 null —— 此时动态下拉退化成只有兜底列表的普通下拉,不报错。
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
    bool ShowsCredentials,
    bool ShowsPort = true,
    ProtocolSettingKind HostKind = ProtocolSettingKind.Text,
    IReadOnlyList<ProtocolSettingChoice>? HostChoices = null,
    bool HostAllowsCustomValue = false,
    IProtocolChoiceSource? ChoiceSource = null)
{
    /// <summary>主机栏是否渲染成下拉(静态或动态)。</summary>
    public bool HostIsChoice => HostKind is ProtocolSettingKind.Choice or ProtocolSettingKind.DynamicChoice;

    /// <summary>主机栏的候选项是否要向插件现取(并给出刷新按钮)。</summary>
    public bool HostIsDynamic => HostKind == ProtocolSettingKind.DynamicChoice;

    /// <summary>从协议描述转换。</summary>
    /// <param name="descriptor">协议描述。</param>
    /// <param name="handler">
    /// 该协议的实现(文件系统或终端)。只用来问一件事:它认不认
    /// <see cref="IProtocolChoiceSource" /> —— 动态下拉的候选项由它现给。
    /// </param>
    /// <returns>统一表单模型。</returns>
    public static PluginConnectionForm From(ProtocolDescriptor descriptor, object? handler = null) =>
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
            ShowsCredentials: !descriptor.Features.HasFlag(ProtocolFeatures.NoCredentials),
            ShowsPort: !descriptor.Features.HasFlag(ProtocolFeatures.NoEndpoint),
            HostKind: descriptor.HostKind,
            HostChoices: descriptor.HostChoices,
            HostAllowsCustomValue: descriptor.HostAllowsCustomValue,
            ChoiceSource: handler as IProtocolChoiceSource);

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
            ShowsCredentials: !descriptor.Features.HasFlag(WorkspaceFeatures.NoCredentials),
            // SQLite / DuckDB 这类"就是磁盘上一个文件"的方言:端口那一栏收起,
            // 主机那一栏改标成「数据库文件」装路径。
            ShowsPort: !descriptor.Features.HasFlag(WorkspaceFeatures.NoEndpoint));
}
