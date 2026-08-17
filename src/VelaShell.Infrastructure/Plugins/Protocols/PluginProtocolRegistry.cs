using System.Diagnostics;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Infrastructure.Plugins.Protocols;

/// <summary>
/// 连接配置页上的一个协议页签。<see cref="IsReady" /> 为 false 表示它还只是清单里的声明
/// —— 页签画得出来,但设置表单要等插件激活后才补齐。
/// </summary>
/// <param name="Id">协议 id。</param>
/// <param name="DisplayName">页签名称。</param>
/// <param name="DefaultPort">新建配置时的默认端口。</param>
/// <param name="PluginId">提供该协议的插件 id。</param>
/// <param name="IsReady">插件是否已激活并完成注册。</param>
public sealed record PluginProtocolTab(string Id, string DisplayName, int DefaultPort, string PluginId, bool IsReady);

/// <summary>一次已完成的协议注册。</summary>
/// <param name="PluginId">提供者插件 id。</param>
/// <param name="Descriptor">协议描述。</param>
/// <param name="FileSystem">协议实现。</param>
public sealed record PluginProtocolRegistration(string PluginId, ProtocolDescriptor Descriptor, IProtocolFileSystem FileSystem);

/// <summary>
/// 宿主侧的插件协议注册表:把「清单声明的页签」与「插件激活后注册的实现」合成一张表,
/// 供连接配置页画页签、供文件服务路由分派。
/// <para>
/// 两段式(声明 → 注册)是刻意的:页签必须在**不装载任何插件程序集**的前提下就能画出来,
/// 否则"启动零开销"这条就破了 —— 用户不开连接对话框、不选那个页签,S3 插件连同它的
/// AWSSDK 依赖就一行代码都不会被装进内存。
/// </para>
/// </summary>
public sealed class PluginProtocolRegistry
{
    /// <summary>一条注册及其事件订阅句柄(拆订阅要拿原委托,不能靠 sender 反查)。</summary>
    private sealed record Entry(PluginProtocolRegistration Registration, EventHandler<ProtocolSessionStateChange> Handler);

    // 协议 id 由 PluginManifestReader.IsValidProtocolId 强制全小写,因此一律 Ordinal ——
    // 这里曾经用 IgnoreCase 而界面用 Ordinal,同一个 id 在两处会得出不同结论。
    private readonly Dictionary<string, PluginProtocolTab> _declared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _registered = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// 惰性激活钩子(由 <see cref="PluginManager" /> 注入):按协议 id 找到提供者插件并激活它,
    /// 返回激活后该协议是否可用。
    /// </summary>
    public Func<string, Task<bool>>? ActivationRequested { get; set; }

    /// <summary>
    /// 传输设置提供者(由装配处注入,读宿主的"设置 → 文件传输"):限速与时间戳策略是全局用户偏好,
    /// 对 SFTP/FTP/插件协议一视同仁。缺席时协议拿到"不限速、保留时间戳"。
    /// </summary>
    public Func<CancellationToken, Task<ProtocolTransferOptions>>? TransferOptionsProvider { get; set; }

    /// <summary>按当前设置给出传输选项;未注入提供者时给不限速的默认值。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>传输选项。</returns>
    public async Task<ProtocolTransferOptions> GetTransferOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (TransferOptionsProvider is not { } provider)
        {
            return new(0, 0, PreserveTimestamps: true);
        }
        try
        {
            return await provider(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 读设置失败不该把一次传输也带崩:退化成不限速。
            Trace.WriteLine($"[PluginProtocols] Reading transfer options failed: {ex.Message}");
            return new(0, 0, PreserveTimestamps: true);
        }
    }

    /// <summary>
    /// 协议集合发生变化(声明增删、注册增删):连接配置页据此刷新页签。
    /// <para>
    /// **可能在任意线程触发** —— 发现期跑在后台线程,惰性激活跑在线程池
    /// (见 <see cref="ResolveAsync" /> 里的 Task.Run)。订阅方若要动界面集合,
    /// 必须自行封送回 UI 线程。
    /// </para>
    /// </summary>
    public event Action? Changed;

    /// <summary>某个协议的会话状态变化(由插件实现上报,转发给文件服务)。</summary>
    public event EventHandler<PluginProtocolSessionEvent>? SessionStateChanged;

    /// <summary>某个协议被注销(插件停用/卸载):其上的会话已无处可去,由文件服务收尾关闭。</summary>
    public event Action<string>? Unregistered;

    /// <summary>当前全部协议页签(声明的 + 已注册的),按显示名排序。</summary>
    public IReadOnlyList<PluginProtocolTab> Tabs
    {
        get
        {
            lock (_gate)
            {
                var merged = new Dictionary<string, PluginProtocolTab>(_declared, StringComparer.Ordinal);
                foreach ((string id, Entry entry) in _registered)
                {
                    ProtocolDescriptor descriptor = entry.Registration.Descriptor;
                    merged[id] = new(id, descriptor.DisplayName, descriptor.DefaultPort,
                        entry.Registration.PluginId, IsReady: true);
                }
                return [.. merged.Values.OrderBy(static tab => tab.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
            }
        }
    }

    /// <summary>登记某插件在清单里声明的协议页签(发现期调用,不碰程序集)。</summary>
    /// <param name="pluginId">插件 id。</param>
    /// <param name="protocols">清单里的协议贡献。</param>
    public void Declare(string pluginId, IEnumerable<VelaShell.PluginSdk.ProtocolContribution> protocols)
    {
        ArgumentNullException.ThrowIfNull(protocols);
        bool changed = false;
        lock (_gate)
        {
            foreach (VelaShell.PluginSdk.ProtocolContribution protocol in protocols)
            {
                // 同 id 先到先得,与插件 id 冲突处置一致:后来者不覆盖已在表里的声明。
                changed |= _declared.TryAdd(protocol.Id,
                    new(protocol.Id, protocol.DisplayName, protocol.DefaultPort, pluginId, IsReady: false));
            }
        }
        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>撤下某插件的全部声明与注册(禁用/卸载)。</summary>
    /// <param name="pluginId">插件 id。</param>
    public void RemovePlugin(string pluginId)
    {
        List<string> dropped = [];
        List<Entry> detaching = [];
        lock (_gate)
        {
            foreach (string id in _declared.Where(entry => entry.Value.PluginId == pluginId).Select(static entry => entry.Key).ToList())
            {
                _declared.Remove(id);
                dropped.Add(id);
            }
            foreach (string id in _registered.Where(entry => entry.Value.Registration.PluginId == pluginId).Select(static entry => entry.Key).ToList())
            {
                detaching.Add(_registered[id]);
                _registered.Remove(id);
                dropped.Add(id);
            }
        }
        // Detach 调的是**插件自己写的**事件访问器(Detach 的 try/catch 已承认它不可信)。
        // 持锁调用等于把一把 UI 线程也会持有的锁交到插件手里 —— 它在访问器里做一次
        // 阻塞式 Dispatcher.Invoke 就是死锁。锁内只动字典,回调一律挪到锁外。
        foreach (Entry entry in detaching)
        {
            Detach(entry);
        }
        foreach (string id in dropped.Distinct(StringComparer.Ordinal))
        {
            RaiseUnregistered(id);
        }
        if (dropped.Count > 0)
        {
            RaiseChanged();
        }
    }

    /// <summary>插件激活后注册一种协议的实现;释放返回值即注销。</summary>
    /// <param name="pluginId">插件 id。</param>
    /// <param name="descriptor">协议描述。</param>
    /// <param name="fileSystem">协议实现。</param>
    /// <returns>注销句柄。</returns>
    public IDisposable Register(string pluginId, ProtocolDescriptor descriptor, IProtocolFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(fileSystem);
        string protocolId = descriptor.Id;
        // 订阅委托按注册捕获协议 id:插件触发事件时的 sender 是什么由插件决定,不能拿它反查。
        void Handler(object? _, ProtocolSessionStateChange change) =>
            SessionStateChanged?.Invoke(this, new(protocolId, change));
        var entry = new Entry(new(pluginId, descriptor, fileSystem), Handler);
        Entry? replaced;
        lock (_gate)
        {
            _registered.TryGetValue(protocolId, out replaced);
            _registered[protocolId] = entry;
        }
        // 订阅与拆订阅都在锁外:见 RemovePlugin 处的说明。
        fileSystem.SessionStateChanged += Handler;
        if (replaced is not null)
        {
            Detach(replaced);
            // 换成了**另一个**实现:旧实现名下的会话已无人应答,得让文件服务收尾。
            // 换成同一个实例(插件为刷新文案而重注册)则不能发 —— 那会把用户正开着的
            // 标签页全部掐掉,而它本来只是想换个字段标签。
            if (!ReferenceEquals(replaced.Registration.FileSystem, fileSystem))
            {
                RaiseUnregistered(protocolId);
            }
        }
        RaiseChanged();
        return new Unregister(this, protocolId, entry);
    }

    /// <summary>按 id 取回已注册的协议;未注册时返回 <see langword="false" />。</summary>
    /// <param name="protocolId">协议 id。</param>
    /// <param name="registration">已注册的协议。</param>
    /// <returns>是否已注册。</returns>
    public bool TryGet(string protocolId, out PluginProtocolRegistration registration)
    {
        lock (_gate)
        {
            if (_registered.TryGetValue(protocolId, out Entry? found))
            {
                registration = found.Registration;
                return true;
            }
        }
        registration = null!;
        return false;
    }

    /// <summary>
    /// 取回协议实现;未注册时先尝试惰性激活其提供者插件,仍拿不到则返回 <see langword="null" />。
    /// </summary>
    /// <param name="protocolId">协议 id。</param>
    /// <returns>已注册的协议,或 null。</returns>
    public async Task<PluginProtocolRegistration?> ResolveAsync(string? protocolId)
    {
        if (string.IsNullOrWhiteSpace(protocolId))
        {
            return null;
        }
        if (TryGet(protocolId, out PluginProtocolRegistration registration))
        {
            return registration;
        }
        bool declared;
        lock (_gate)
        {
            declared = _declared.ContainsKey(protocolId);
        }
        if (!declared || ActivationRequested is not { } activate)
        {
            return null;
        }
        try
        {
            // **必须 Task.Run**:激活链上没有任何真正的让出点 —— PluginAssemblyLoadContext 的
            // 装配集加载、Activator.CreateInstance、插件那句 `return Task.CompletedTask` 全是同步的。
            // 两个调用点(连接页选中协议、打开一条会话)都在 UI 线程 await 这里,
            // 不推到线程池就是"点一下 S3 页签,界面连同 IsPluginLoading 的转圈一起冻住"。
            if (!await Task.Run(() => activate(protocolId)).ConfigureAwait(false))
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginProtocols] Activation for protocol '{protocolId}' failed: {ex.Message}");
            return null;
        }
        return TryGet(protocolId, out PluginProtocolRegistration activated) ? activated : null;
    }

    /// <summary>某个协议 id 是否至少被声明过(用于区分"插件没装"与"插件装了但还没激活")。</summary>
    /// <param name="protocolId">协议 id。</param>
    /// <returns>是否已声明。</returns>
    public bool IsDeclared(string protocolId)
    {
        lock (_gate)
        {
            return _declared.ContainsKey(protocolId) || _registered.ContainsKey(protocolId);
        }
    }

    private void Detach(Entry entry)
    {
        try
        {
            entry.Registration.FileSystem.SessionStateChanged -= entry.Handler;
        }
        catch (Exception ex)
        {
            // 插件的事件访问器自爆不该拖垮停用流程。
            Trace.WriteLine($"[PluginProtocols] Detaching '{entry.Registration.Descriptor.Id}' threw: {ex.Message}");
        }
    }

    private void RaiseUnregistered(string protocolId)
    {
        try
        {
            Unregistered?.Invoke(protocolId);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginProtocols] Unregister handler for '{protocolId}' threw: {ex.Message}");
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // 通知订阅方异常不回灌运行时(与 PluginManager.RaiseChanged 同口径)。
        }
    }

    private sealed class Unregister(PluginProtocolRegistry owner, string id, Entry entry) : IDisposable
    {
        public void Dispose()
        {
            bool removed;
            lock (owner._gate)
            {
                // 只有仍是自己那一份才撤:同 id 被后来者替换过时不能把别人的注册删掉。
                removed = owner._registered.TryGetValue(id, out Entry? current) && ReferenceEquals(current, entry);
                if (removed)
                {
                    owner._registered.Remove(id);
                }
            }
            if (removed)
            {
                owner.Detach(entry); // 锁外拆订阅,理由同 RemovePlugin
                owner.RaiseUnregistered(id);
                owner.RaiseChanged();
            }
        }
    }
}

/// <summary>协议会话状态变化事件的载荷(带上是哪种协议)。</summary>
/// <param name="ProtocolId">协议 id。</param>
/// <param name="Change">插件上报的状态变化。</param>
public readonly record struct PluginProtocolSessionEvent(string ProtocolId, ProtocolSessionStateChange Change);
