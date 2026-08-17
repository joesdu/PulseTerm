using VelaShell.PluginSdk.Protocols;

namespace VelaShell.PluginSdk.Testing;

/// <summary>
/// <see cref="IProtocolsApi" /> 的记录实现:注册进内存表,测试可直接取出描述与实现
/// 驱动一次"连接 → 列目录"(文件协议)或"连接 → 读写字节"(终端协议)而不必拉起宿主。
/// <para>
/// 刻意复刻宿主 <c>Register</c> 的**全部**前置校验(id 合法性、默认端口区间、
/// 证书指纹字段必须真实存在)与注销语义:这几类失败都只在真实宿主的 <c>ActivateAsync</c>
/// 里才暴露 —— 替身放行就失去了单测的意义。
/// </para>
/// </summary>
public sealed class RecordingProtocols : IProtocolsApi
{
    /// <summary>一条记录下来的注册(文件与终端二选一)。</summary>
    private sealed record Entry(ProtocolDescriptor Descriptor, IProtocolFileSystem? FileSystem, IProtocolTerminal? Terminal)
    {
        /// <summary>注销时用于判定"这条还是不是自己那一份"的实现实例。</summary>
        public object Implementation => (object?)FileSystem ?? Terminal!;
    }

    private readonly Dictionary<string, Entry> _registered = [with(StringComparer.Ordinal)];

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

    /// <summary>按 id 取回注册的文件系统实现;未注册或注册的是终端协议时返回 <see langword="null" />。</summary>
    public IProtocolFileSystem? GetFileSystem(string protocolId) =>
        _registered.TryGetValue(protocolId, out Entry? entry) ? entry.FileSystem : null;

    /// <summary>按 id 取回注册的终端实现;未注册或注册的是文件协议时返回 <see langword="null" />。</summary>
    public IProtocolTerminal? GetTerminal(string protocolId) =>
        _registered.TryGetValue(protocolId, out Entry? entry) ? entry.Terminal : null;

    /// <summary>按 id 取回注册的描述;未注册时返回 <see langword="null" />。</summary>
    public ProtocolDescriptor? GetDescriptor(string protocolId) =>
        _registered.TryGetValue(protocolId, out Entry? entry) ? entry.Descriptor : null;

    /// <inheritdoc />
    public IDisposable Register(ProtocolDescriptor descriptor, IProtocolFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return Add(descriptor, fileSystem, terminal: null);
    }

    /// <inheritdoc />
    public IDisposable Register(ProtocolDescriptor descriptor, IProtocolTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return Add(descriptor, fileSystem: null, terminal);
    }

    private Registration Add(ProtocolDescriptor descriptor, IProtocolFileSystem? fileSystem, IProtocolTerminal? terminal)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
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
        var entry = new Entry(descriptor, fileSystem, terminal);
        _registered[descriptor.Id] = entry;
        return new Registration(this, descriptor.Id, entry.Implementation);
    }

    private sealed class Registration(RecordingProtocols owner, string id, object implementation) : IDisposable
    {
        /// <summary>
        /// 与宿主同口径:同 id 被后来者替换过时,旧句柄是**空操作**。
        /// 盲删会让"注册 A、注册 B、释放 A 的句柄"在单测里删掉 B,而真机上 B 还活着。
        /// </summary>
        public void Dispose()
        {
            if (owner._registered.TryGetValue(id, out Entry? current)
                && ReferenceEquals(current.Implementation, implementation))
            {
                owner._registered.Remove(id);
            }
        }
    }
}
