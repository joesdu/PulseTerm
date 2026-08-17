using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// 每插件的协议能力:把注册转交全局 <see cref="PluginProtocolRegistry" />,并强制 id 前缀
/// (协议 id 会落进用户的会话配置,冒名等于劫持别家的连接配置)。
/// 释放时撤销该插件的全部注册 —— 这是可收集 ALC 能真正回收的前提。
/// </summary>
/// <param name="pluginId">插件 id。</param>
/// <param name="registry">全局协议注册表。</param>
/// <param name="log">插件日志。</param>
internal sealed class ProtocolsCapability(string pluginId, PluginProtocolRegistry registry, IPluginLogger log)
    : IProtocolsApi, IDisposable
{
    private readonly List<IDisposable> _registrations = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <inheritdoc />
    public Task<ProtocolTransferOptions> GetTransferOptionsAsync(CancellationToken cancellationToken = default) =>
        registry.GetTransferOptionsAsync(cancellationToken);

    /// <inheritdoc />
    public IDisposable Register(ProtocolDescriptor descriptor, IProtocolFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(fileSystem);
        // 与清单校验共用同一个判定(含"必须全小写")。不写清单、只在 ActivateAsync 里
        // Register 的协议也得过同一关 —— 否则大写 id 会绕过清单溜进注册表,
        // 而注册表查找与界面比对的大小写口径一旦不一致,用户的会话配置就再也认不出自己的协议。
        if (!PluginManifestReader.IsValidProtocolId(descriptor.Id, pluginId))
        {
            throw new ArgumentException(
                $"Protocol id '{descriptor.Id}' must be lowercase [a-z0-9.-], at most 128 chars, " +
                $"and be '{pluginId}' or start with '{pluginId}.'.", nameof(descriptor));
        }
        if (descriptor.DefaultPort is < 1 or > 65535)
        {
            throw new ArgumentException(
                $"Protocol '{descriptor.Id}' declares an out-of-range default port {descriptor.DefaultPort}.", nameof(descriptor));
        }
        // 指纹要写回的字段必须真的存在,否则"信任证书"点了等于没点 —— 这类失败在真机上
        // 才暴露,代价是用户对着同一个弹窗点三次都连不上。
        if (descriptor.TrustedThumbprintSettingKey is { Length: > 0 } key
            && descriptor.Fields.All(field => !field.Key.Equals(key, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Protocol '{descriptor.Id}' points TrustedThumbprintSettingKey at '{key}', which is not one of its fields.",
                nameof(descriptor));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IDisposable handle = registry.Register(pluginId, descriptor, fileSystem);
            _registrations.Add(handle);
            log.Info($"Registered protocol '{descriptor.Id}' ({descriptor.DisplayName}).");
            return handle;
        }
    }

    /// <summary>撤销该插件的全部协议注册。</summary>
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

/// <summary>协议能力缺席时的退化实现(headless / 单测宿主):注册即报不可用。</summary>
internal sealed class UnavailableProtocols : IProtocolsApi
{
    /// <inheritdoc />
    public IDisposable Register(ProtocolDescriptor descriptor, IProtocolFileSystem fileSystem) =>
        throw new InvalidOperationException("Protocol capability is unavailable in this host.");

    /// <inheritdoc />
    public Task<ProtocolTransferOptions> GetTransferOptionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProtocolTransferOptions(0, 0, PreserveTimestamps: true));
}
