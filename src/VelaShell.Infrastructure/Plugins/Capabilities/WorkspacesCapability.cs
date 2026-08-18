using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// 每插件的工作台能力:把注册转交全局 <see cref="PluginProtocolRegistry" />,并强制 id 前缀
/// (id 会落进用户的会话配置,冒名等于劫持别家的连接配置)。
/// 释放时撤销该插件的全部注册 —— 这是可收集 ALC 能真正回收的前提。
/// </summary>
/// <param name="pluginId">插件 id。</param>
/// <param name="registry">全局连接类型注册表。</param>
/// <param name="log">插件日志。</param>
internal sealed class WorkspacesCapability(string pluginId, PluginProtocolRegistry registry, IPluginLogger log)
    : IWorkspacesApi, IDisposable
{
    private readonly List<IDisposable> _registrations = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <inheritdoc />
    public IDisposable Register(WorkspaceDescriptor descriptor, IWorkspaceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(provider);
        // 与清单校验共用同一个判定(含"必须全小写")——不写清单、只在 ActivateAsync 里注册的
        // 连接类型也得过同一关,否则大写 id 会绕过清单溜进注册表,而注册表查找与界面比对的
        // 大小写口径一旦不一致,用户的会话配置就再也认不出自己的连接类型。
        if (!PluginManifestReader.IsValidProtocolId(descriptor.Id, pluginId))
        {
            throw new ArgumentException(
                $"Workspace id '{descriptor.Id}' must be lowercase [a-z0-9.-], at most 128 chars, " +
                $"and be '{pluginId}' or start with '{pluginId}.'.", nameof(descriptor));
        }
        if (descriptor.DefaultPort is < 1 or > 65535)
        {
            throw new ArgumentException(
                $"Workspace '{descriptor.Id}' declares an out-of-range default port {descriptor.DefaultPort}.", nameof(descriptor));
        }
        // 指纹要写回的字段必须真的存在,否则"信任证书"点了等于没点 —— 这类失败在真机上
        // 才暴露,代价是用户对着同一个弹窗点三次都连不上。
        if (descriptor.TrustedThumbprintSettingKey is { Length: > 0 } key
            && descriptor.Fields.All(field => !field.Key.Equals(key, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Workspace '{descriptor.Id}' points TrustedThumbprintSettingKey at '{key}', which is not one of its fields.",
                nameof(descriptor));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IDisposable handle = registry.RegisterWorkspace(pluginId, descriptor, provider);
            _registrations.Add(handle);
            log.Info($"Registered workspace '{descriptor.Id}' ({descriptor.DisplayName}).");
            return handle;
        }
    }

    /// <inheritdoc />
    public Task<bool> ProposeConnectionAsync(
        WorkspaceConnectionProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        // 只能提议自己那种连接类型:否则一个插件就能借宿主的对话框去替别家建配置。
        if (!PluginManifestReader.IsValidProtocolId(proposal.WorkspaceId, pluginId))
        {
            throw new ArgumentException(
                $"Workspace id '{proposal.WorkspaceId}' must be '{pluginId}' or start with '{pluginId}.'.",
                nameof(proposal));
        }
        if (proposal.Port is < 1 or > 65535)
        {
            throw new ArgumentException($"Proposed port {proposal.Port} is out of range.", nameof(proposal));
        }
        log.Info($"Proposing a '{proposal.WorkspaceId}' connection to {proposal.Host}:{proposal.Port}.");
        return registry.ProposeConnectionAsync(proposal);
    }

    /// <summary>撤销该插件的全部工作台注册。</summary>
    public void Dispose()
    {
        List<IDisposable> handles;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            handles = [.. _registrations];
            _registrations.Clear();
        }
        foreach (IDisposable handle in handles)
        {
            try
            {
                handle.Dispose();
            }
            catch
            {
                // 注销失败不阻断停用流程。
            }
        }
    }
}

/// <summary>工作台能力缺席时的退化实现(headless / 单测宿主):注册即报不可用。</summary>
internal sealed class UnavailableWorkspaces : IWorkspacesApi
{
    /// <inheritdoc />
    public IDisposable Register(WorkspaceDescriptor descriptor, IWorkspaceProvider provider) =>
        throw new InvalidOperationException("Workspace capability is unavailable in this host.");

    /// <inheritdoc />
    public Task<bool> ProposeConnectionAsync(
        WorkspaceConnectionProposal proposal,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}
