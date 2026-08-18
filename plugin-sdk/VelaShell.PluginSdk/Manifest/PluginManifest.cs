using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaShell.PluginSdk;

/// <summary>插件的宿主模式。</summary>
public enum PluginHostMode
{
    /// <summary>进程内(默认):宿主进程内可收集 ALC 装载,零 IPC 开销,面板原生停靠进主窗口标签区。</summary>
    InProcess,

    /// <summary>
    /// 隔离进程:插件运行在独立的 VelaShell.PluginHost 进程内,经命名管道 RPC 调用能力,
    /// 崩溃/卡死不影响宿主。UI 由插件进程内建的 Avalonia 呈现(Windows 上停靠面板经
    /// HWND 收养嵌入宿主标签区,其余平台回退独立窗口)。
    /// </summary>
    Isolated
}

/// <summary>插件的空闲回收策略(仅隔离模式生效)。</summary>
public enum PluginIdlePolicy
{
    /// <summary>常驻(默认):激活后不因空闲被回收。</summary>
    KeepAlive,

    /// <summary>
    /// 可回收:连续空闲(无 RPC 往来且无打开的面板)超过宿主设定时长后,
    /// 插件被停用、进程被回收;声明的贡献命令保持占位,再次触发即重新激活。
    /// 要求至少声明一条 <see cref="PluginContributes.Commands" />(否则视同 KeepAlive)。
    /// </summary>
    Recyclable
}

/// <summary>一条声明式命令贡献:发现期即出现在命令面板,无需装载插件程序集。</summary>
public sealed record CommandContribution
{
    /// <summary>命令 id,必须以 <c>&lt;pluginId&gt;.</c> 为前缀。</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>面向用户的显示名称。</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>分组标签。</summary>
    [JsonPropertyName("category")]
    public string Category { get; init; } = "";
}

/// <summary>
/// 一条声明式协议贡献:发现期即出现在连接配置页的协议页签上,无需装载插件程序集。
/// <para>
/// 这里只声明"页签怎么画"三件事;设置表单、右键动作与能力位在插件激活后由
/// <c>context.Protocols.Register</c> 给出的 <c>ProtocolDescriptor</c> 提供 ——
/// 用户点到这个页签即触发 <c>onProtocol:&lt;id&gt;</c> 激活,表单随即补齐。
/// 之所以不把整份 descriptor 塞进清单:那些字段要本地化,而清单是静态 JSON。
/// </para>
/// </summary>
public sealed record ProtocolContribution
{
    /// <summary>协议 id,必须等于插件 id 或以 <c>&lt;pluginId&gt;.</c> 为前缀。</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>协议页签上的名称。</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>新建配置时的默认端口。</summary>
    [JsonPropertyName("defaultPort")]
    public int DefaultPort { get; init; } = 22;
}

/// <summary>
/// 一条声明式工作台贡献:发现期即出现在连接配置页上,无需装载插件程序集。
/// <para>
/// 与 <see cref="ProtocolContribution" /> 的分工:协议类型长得像文件系统(宿主打开双栏浏览器),
/// 工作台类型不是(宿主向插件索取一个 Avalonia 控件挂成停靠文档)。两者在连接配置页上
/// 是同一排页签,声明形状也一致 —— 表单、能力位在激活后由
/// <c>context.Workspaces.Register</c> 的 <c>WorkspaceDescriptor</c> 补齐。
/// </para>
/// </summary>
public sealed record WorkspaceContribution
{
    /// <summary>连接类型 id,必须等于插件 id 或以 <c>&lt;pluginId&gt;.</c> 为前缀。</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>页签上的名称。</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>新建配置时的默认端口。</summary>
    [JsonPropertyName("defaultPort")]
    public int DefaultPort { get; init; } = 22;
}

/// <summary>声明式贡献点(纯数据,发现期注册占位)。</summary>
public sealed record PluginContributes
{
    /// <summary>命令贡献。</summary>
    [JsonPropertyName("commands")]
    public CommandContribution[] Commands { get; init; } = [];

    /// <summary>远程文件协议贡献。</summary>
    [JsonPropertyName("protocols")]
    public ProtocolContribution[] Protocols { get; init; } = [];

    /// <summary>工作台连接类型贡献(插件全权渲染的会话文档)。</summary>
    [JsonPropertyName("workspaces")]
    public WorkspaceContribution[] Workspaces { get; init; } = [];
}

/// <summary>
/// 插件清单(<c>plugin.json</c>)模型。字段名采用 camelCase;允许注释与尾逗号。
/// 校验规则见 <see cref="PluginManifestReader" />。
/// </summary>
public sealed record PluginManifest
{
    /// <summary>插件 id:<c>&lt;发布者&gt;.&lt;名称&gt;</c> 形式,小写,仅 <c>[a-z0-9.-]</c>,全局唯一。</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>插件版本(semver,如 <c>1.2.0</c> 或 <c>1.2.0-beta.1</c>)。</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>面向用户的显示名称。</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>一句话描述。</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>发布者名称。</summary>
    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    /// <summary>入口程序集,相对插件目录的路径(如 <c>MyPlugin.dll</c>)。</summary>
    [JsonPropertyName("entry")]
    public required string Entry { get; init; }

    /// <summary>插件编译目标的 apiLevel;宿主拒绝加载高于自身代际的插件。</summary>
    [JsonPropertyName("apiLevel")]
    public int ApiLevel { get; init; } = VelaPluginApi.Level;

    /// <summary>宿主模式:<c>"inProcess"</c>(默认)或 <c>"isolated"</c>(独立进程,大小写不敏感)。</summary>
    [JsonPropertyName("hostMode")]
    public PluginHostMode HostMode { get; init; } = PluginHostMode.InProcess;

    /// <summary>
    /// 激活事件:省略或含 <c>"onStartup"</c> = 启动即激活(默认);
    /// 仅含 <c>"onCommand:&lt;命令id&gt;"</c> = 惰性激活 —— 发现期只注册
    /// <see cref="Contributes" /> 里的占位命令,首次触发才装载/拉起插件。
    /// </summary>
    [JsonPropertyName("activationEvents")]
    public string[]? ActivationEvents { get; init; }

    /// <summary>声明式贡献点:发现期即生效,无需装载插件程序集。</summary>
    [JsonPropertyName("contributes")]
    public PluginContributes? Contributes { get; init; }

    /// <summary>空闲回收策略(仅隔离模式生效),默认常驻。</summary>
    [JsonPropertyName("idlePolicy")]
    public PluginIdlePolicy IdlePolicy { get; init; } = PluginIdlePolicy.KeepAlive;

    /// <summary>是否在宿主启动时激活(省略 activationEvents 或显式含 onStartup)。</summary>
    [JsonIgnore]
    public bool ActivatesOnStartup =>
        ActivationEvents is not { Length: > 0 } events
        || events.Contains("onStartup", StringComparer.OrdinalIgnoreCase);

    /// <summary>要求的最低宿主版本(可选,如 <c>0.1.0</c>);不满足时插件被标记为不兼容。</summary>
    [JsonPropertyName("minHostVersion")]
    public string? MinHostVersion { get; init; }

    /// <summary>主页/仓库地址(可选)。</summary>
    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }

    /// <summary>许可证标识(可选,如 <c>MIT</c>)。</summary>
    [JsonPropertyName("license")]
    public string? License { get; init; }
}

/// <summary>STJ 源生成上下文:manifest 解析走编译期生成的序列化器,启动路径零反射。</summary>
[JsonSourceGenerationOptions(
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PluginManifest))]
internal sealed partial class PluginManifestJsonContext : JsonSerializerContext;
