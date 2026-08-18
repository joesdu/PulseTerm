using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="IWorkspacesApi" /> 的记录实现:注册进内存表,测试可直接取出描述与提供方,
/// 驱动一次"打开会话 → 拿到文档"而不必拉起宿主。
/// <para>
/// 刻意复刻宿主 <c>Register</c> 的**全部**前置校验(id 合法性、默认端口区间、
/// 证书指纹字段必须真实存在)与注销语义:这几类失败都只在真实宿主的 <c>ActivateAsync</c>
/// 里才暴露 —— 替身放行就失去了单测的意义。
/// </para>
/// </summary>
public sealed class RecordingWorkspaces : IWorkspacesApi
{
    private readonly Dictionary<string, (WorkspaceDescriptor Descriptor, IWorkspaceProvider Provider)> _registered =
        [with(StringComparer.Ordinal)];

    /// <summary>默认构造(插件 id 为 <c>test.plugin</c>)。</summary>
    public RecordingWorkspaces()
    {
    }

    /// <summary>指定拥有这些注册的插件 id。</summary>
    /// <param name="pluginId">插件 id,用于前缀校验。</param>
    public RecordingWorkspaces(string pluginId) => PluginId = pluginId;

    /// <summary>拥有这些注册的插件 id(前缀校验依据);由 <see cref="TestPluginContext" /> 同步。</summary>
    public string PluginId { get; set; } = "test.plugin";

    /// <summary>当前已注册的连接类型描述快照。</summary>
    public IReadOnlyList<WorkspaceDescriptor> Registered => [.. _registered.Values.Select(entry => entry.Descriptor)];

    /// <summary>按 id 取回注册的提供方;未注册时返回 <see langword="null" />。</summary>
    /// <param name="workspaceId">连接类型 id。</param>
    /// <returns>提供方,或 null。</returns>
    public IWorkspaceProvider? GetProvider(string workspaceId) =>
        _registered.TryGetValue(workspaceId, out (WorkspaceDescriptor Descriptor, IWorkspaceProvider Provider) entry)
            ? entry.Provider
            : null;

    /// <summary>按 id 取回注册的描述;未注册时返回 <see langword="null" />。</summary>
    /// <param name="workspaceId">连接类型 id。</param>
    /// <returns>描述,或 null。</returns>
    public WorkspaceDescriptor? GetDescriptor(string workspaceId) =>
        _registered.TryGetValue(workspaceId, out (WorkspaceDescriptor Descriptor, IWorkspaceProvider Provider) entry)
            ? entry.Descriptor
            : null;

    /// <inheritdoc />
    public IDisposable Register(WorkspaceDescriptor descriptor, IWorkspaceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(provider);
        if (!PluginManifestReader.IsValidProtocolId(descriptor.Id, PluginId))
        {
            throw new ArgumentException(
                $"Workspace id '{descriptor.Id}' must be lowercase [a-z0-9.-], at most 128 chars, " +
                $"and be '{PluginId}' or start with '{PluginId}.'.", nameof(descriptor));
        }
        if (descriptor.DefaultPort is < 1 or > 65535)
        {
            throw new ArgumentException(
                $"Workspace '{descriptor.Id}' declares an out-of-range default port {descriptor.DefaultPort}.", nameof(descriptor));
        }
        if (descriptor.TrustedThumbprintSettingKey is { Length: > 0 } key
            && descriptor.Fields.All(field => !field.Key.Equals(key, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Workspace '{descriptor.Id}' points TrustedThumbprintSettingKey at '{key}', which is not one of its fields.",
                nameof(descriptor));
        }
        _registered[descriptor.Id] = (descriptor, provider);
        return new Registration(this, descriptor.Id, provider);
    }

    /// <summary>收到的连接提议(按顺序)。测试据此验证探测流程提议了什么。</summary>
    public List<WorkspaceConnectionProposal> Proposals { get; } = [];

    /// <summary>
    /// <see cref="ProposeConnectionAsync" /> 的答案:模拟"用户按了保存 / 取消"。
    /// 默认 false —— 与真实宿主在 headless(没有对话框)下的行为一致。
    /// </summary>
    public bool ProposalAccepted { get; set; }

    /// <inheritdoc />
    public Task<bool> ProposeConnectionAsync(
        WorkspaceConnectionProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        // 复刻宿主的前置校验:插件只能提议**自己那种**连接类型。
        if (!PluginManifestReader.IsValidProtocolId(proposal.WorkspaceId, PluginId))
        {
            throw new ArgumentException(
                $"Workspace id '{proposal.WorkspaceId}' must be '{PluginId}' or start with '{PluginId}.'.",
                nameof(proposal));
        }
        if (proposal.Port is < 1 or > 65535)
        {
            throw new ArgumentException($"Proposed port {proposal.Port} is out of range.", nameof(proposal));
        }
        Proposals.Add(proposal);
        return Task.FromResult(ProposalAccepted);
    }

    private sealed class Registration(RecordingWorkspaces owner, string id, IWorkspaceProvider provider) : IDisposable
    {
        /// <summary>
        /// 与宿主同口径:同 id 被后来者替换过时,旧句柄是**空操作**。
        /// 盲删会让"注册 A、注册 B、释放 A 的句柄"在单测里删掉 B,而真机上 B 还活着。
        /// </summary>
        public void Dispose()
        {
            if (owner._registered.TryGetValue(id, out (WorkspaceDescriptor Descriptor, IWorkspaceProvider Provider) current)
                && ReferenceEquals(current.Provider, provider))
            {
                owner._registered.Remove(id);
            }
        }
    }
}
