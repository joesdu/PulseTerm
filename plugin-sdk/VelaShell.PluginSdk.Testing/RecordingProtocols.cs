using VelaShell.PluginSdk.Protocols;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="IProtocolsApi" /> 的记录实现:注册进内存表,测试可直接取出描述与文件系统
/// 驱动一次"连接 → 列目录"而不必拉起宿主。
/// <para>
/// 刻意复刻宿主 <c>Register</c> 的**全部**前置校验(id 合法性、默认端口区间、
/// 证书指纹字段必须真实存在)与注销语义:这几类失败都只在真实宿主的 <c>ActivateAsync</c>
/// 里才暴露 —— 替身放行就失去了单测的意义。
/// </para>
/// </summary>
public sealed class RecordingProtocols : IProtocolsApi
{
    private readonly Dictionary<string, (ProtocolDescriptor Descriptor, IProtocolFileSystem FileSystem)> _registered =
        new(StringComparer.Ordinal);

    /// <summary>默认构造(插件 id 为 <c>test.plugin</c>)。</summary>
    public RecordingProtocols()
    {
    }

    /// <summary>指定拥有这些注册的插件 id。</summary>
    /// <param name="pluginId">插件 id,用于前缀校验。</param>
    public RecordingProtocols(string pluginId) => PluginId = pluginId;

    /// <summary>拥有这些注册的插件 id(前缀校验依据);由 <see cref="TestPluginContext" /> 同步。</summary>
    public string PluginId { get; set; } = "test.plugin";

    /// <summary><see cref="GetTransferOptionsAsync" /> 返回的值;默认不限速、保留时间戳。</summary>
    public ProtocolTransferOptions TransferOptions { get; set; } = new(0, 0, PreserveTimestamps: true);

    /// <inheritdoc />
    public Task<ProtocolTransferOptions> GetTransferOptionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(TransferOptions);

    /// <summary>当前已注册的协议描述快照。</summary>
    public IReadOnlyList<ProtocolDescriptor> Registered => [.. _registered.Values.Select(entry => entry.Descriptor)];

    /// <summary>按 id 取回注册的文件系统实现;未注册时返回 <see langword="null" />。</summary>
    public IProtocolFileSystem? GetFileSystem(string protocolId) =>
        _registered.TryGetValue(protocolId, out (ProtocolDescriptor Descriptor, IProtocolFileSystem FileSystem) entry)
            ? entry.FileSystem
            : null;

    /// <summary>按 id 取回注册的描述;未注册时返回 <see langword="null" />。</summary>
    public ProtocolDescriptor? GetDescriptor(string protocolId) =>
        _registered.TryGetValue(protocolId, out (ProtocolDescriptor Descriptor, IProtocolFileSystem FileSystem) entry)
            ? entry.Descriptor
            : null;

    /// <inheritdoc />
    public IDisposable Register(ProtocolDescriptor descriptor, IProtocolFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (!PluginManifestReader.IsValidProtocolId(descriptor.Id, PluginId))
        {
            throw new ArgumentException(
                $"Protocol id '{descriptor.Id}' must be lowercase [a-z0-9.-], at most 128 chars, " +
                $"and be '{PluginId}' or start with '{PluginId}.'.", nameof(descriptor));
        }
        if (descriptor.DefaultPort is < 1 or > 65535)
        {
            throw new ArgumentException(
                $"Protocol '{descriptor.Id}' declares an out-of-range default port {descriptor.DefaultPort}.", nameof(descriptor));
        }
        if (descriptor.TrustedThumbprintSettingKey is { Length: > 0 } key
            && descriptor.Fields.All(field => !field.Key.Equals(key, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Protocol '{descriptor.Id}' points TrustedThumbprintSettingKey at '{key}', which is not one of its fields.",
                nameof(descriptor));
        }
        _registered[descriptor.Id] = (descriptor, fileSystem);
        return new Registration(this, descriptor.Id, fileSystem);
    }

    private sealed class Registration(RecordingProtocols owner, string id, IProtocolFileSystem fileSystem) : IDisposable
    {
        /// <summary>
        /// 与宿主同口径:同 id 被后来者替换过时,旧句柄是**空操作**。
        /// 盲删会让"注册 A、注册 B、释放 A 的句柄"在单测里删掉 B,而真机上 B 还活着。
        /// </summary>
        public void Dispose()
        {
            if (owner._registered.TryGetValue(id, out (ProtocolDescriptor Descriptor, IProtocolFileSystem FileSystem) current)
                && ReferenceEquals(current.FileSystem, fileSystem))
            {
                owner._registered.Remove(id);
            }
        }
    }
}
