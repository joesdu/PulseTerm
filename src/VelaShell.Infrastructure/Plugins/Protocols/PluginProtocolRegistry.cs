using System.Diagnostics;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Infrastructure.Plugins.Protocols;

/// <summary>插件提供的连接类型有两种形态,决定宿主打开会话时画什么。</summary>
public enum PluginConnectionKind
{
    /// <summary>远程文件协议:宿主打开既有的双栏文件浏览器(S3、WebDAV…)。</summary>
    FileSystem = 0,

    /// <summary>工作台:宿主向插件索取一个控件挂成停靠文档(Redis、MySQL…)。</summary>
    Workspace = 1
}

/// <summary>
/// 连接配置页上的一个插件页签。<see cref="IsReady" /> 为 false 表示它还只是清单里的声明
/// —— 页签画得出来,但设置表单要等插件激活后才补齐。
/// </summary>
/// <param name="Id">连接类型 id。</param>
/// <param name="DisplayName">页签名称。</param>
/// <param name="DefaultPort">新建配置时的默认端口。</param>
/// <param name="PluginId">提供者插件 id。</param>
/// <param name="IsReady">插件是否已激活并完成注册。</param>
/// <param name="Kind">形态:文件协议还是工作台。</param>
public sealed record PluginProtocolTab(
    string Id,
    string DisplayName,
    int DefaultPort,
    string PluginId,
    bool IsReady,
    PluginConnectionKind Kind = PluginConnectionKind.FileSystem);

/// <summary>
/// 一次已完成的协议注册。<see cref="FileSystem" /> 与 <see cref="Terminal" /> 至少有一个非空:
/// 前者接进双栏文件浏览器,后者接进终端标签(Telnet / 串口这类没有文件系统的协议只给后者)。
/// </summary>
/// <param name="PluginId">提供者插件 id。</param>
/// <param name="Descriptor">协议描述。</param>
/// <param name="FileSystem">文件系统实现;终端协议为 null。</param>
/// <param name="Terminal">终端实现;文件协议为 null。</param>
public sealed record PluginProtocolRegistration(
    string PluginId,
    ProtocolDescriptor Descriptor,
    IProtocolFileSystem? FileSystem,
    IProtocolTerminal? Terminal = null);

/// <summary>一次已完成的工作台注册。</summary>
/// <param name="PluginId">提供者插件 id。</param>
/// <param name="Descriptor">连接类型描述。</param>
/// <param name="Provider">工作台实现。</param>
public sealed record PluginWorkspaceRegistration(string PluginId, WorkspaceDescriptor Descriptor, IWorkspaceProvider Provider);

/// <summary>
/// 宿主侧的插件连接类型注册表:把「清单声明的页签」与「插件激活后注册的实现」合成一张表,
/// 供连接配置页画页签、供文件服务与工作台文档路由分派。
/// <para>
/// 两段式(声明 → 注册)是刻意的:页签必须在**不装载任何插件程序集**的前提下就能画出来,
/// 否则"启动零开销"这条就破了 —— 用户不开连接对话框、不选那个页签,S3 插件连同它的
/// AWSSDK 依赖就一行代码都不会被装进内存。
/// </para>
/// <para>
/// 类名留作 <c>PluginProtocolRegistry</c> 而未随内容改名:它现在同时承载
/// <see cref="PluginConnectionKind.FileSystem" /> 与 <see cref="PluginConnectionKind.Workspace" />
/// 两种形态。两者在连接配置页上是同一排页签、落进用户配置的是同一个
/// <c>PluginProtocolId</c> 字段、共用同一条惰性激活链路 —— 拆成两张表只会让调用方
/// 到处写"先问这张、再问那张"。
/// </para>
/// </summary>
public sealed class PluginProtocolRegistry
{
    /// <summary>一条注册及其事件订阅句柄(拆订阅要拿原委托,不能靠 sender 反查)。</summary>
    private sealed record Entry(PluginProtocolRegistration Registration, EventHandler<ProtocolSessionStateChange> Handler);

    // 协议 id 由 PluginManifestReader.IsValidProtocolId 强制全小写,因此一律 Ordinal ——
    // 这里曾经用 IgnoreCase 而界面用 Ordinal,同一个 id 在两处会得出不同结论。
    private readonly Dictionary<string, PluginProtocolTab> _declared = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, Entry> _registered = [with(StringComparer.Ordinal)];
    private readonly Dictionary<string, PluginWorkspaceRegistration> _workspaces = [with(StringComparer.Ordinal)];
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

    /// <summary>
    /// 连接提议处理器(由界面层注入):打开宿主的「新建连接」对话框并按提议预填,
    /// 返回用户是否保存。缺席时提议一律返回 false(headless 宿主没有对话框)。
    /// <para>
    /// 与 <see cref="ActivationRequested" /> / <see cref="TransferOptionsProvider" /> 同一个套路:
    /// 注册表是宿主单例,界面层能拿到的东西经可变钩子注进来 —— 让 Infrastructure 去认识
    /// 一扇窗口是反过来的依赖方向。
    /// </para>
    /// </summary>
    public Func<VelaShell.PluginSdk.Workspaces.WorkspaceConnectionProposal, Task<bool>>? ConnectionProposalHandler { get; set; }

    /// <summary>把一条连接提议转交界面层;没有处理器时返回 false。</summary>
    /// <param name="proposal">提议。</param>
    /// <returns>用户是否保存。</returns>
    public async Task<bool> ProposeConnectionAsync(VelaShell.PluginSdk.Workspaces.WorkspaceConnectionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (ConnectionProposalHandler is not { } handler)
        {
            return false;
        }
        try
        {
            return await handler(proposal).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 对话框自爆不该把插件的探测流程也带崩:如实返回"没保存"。
            Trace.WriteLine($"[PluginProtocols] Connection proposal for '{proposal.WorkspaceId}' failed: {ex.Message}");
            return false;
        }
    }

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
                        entry.Registration.PluginId, IsReady: true, PluginConnectionKind.FileSystem);
                }
                foreach ((string id, PluginWorkspaceRegistration registration) in _workspaces)
                {
                    WorkspaceDescriptor descriptor = registration.Descriptor;
                    merged[id] = new(id, descriptor.DisplayName, descriptor.DefaultPort,
                        registration.PluginId, IsReady: true, PluginConnectionKind.Workspace);
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
                    new(protocol.Id, protocol.DisplayName, protocol.DefaultPort, pluginId, IsReady: false,
                        PluginConnectionKind.FileSystem));
            }
        }
        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>
    /// 把某插件清单里声明的工作台页签登记进注册表(发现期调用,不碰程序集)。
    /// <para>
    /// 刻意**不**与 <see cref="Declare(string, IEnumerable{VelaShell.PluginSdk.ProtocolContribution})" />
    /// 重载同名:两个重载只在集合元素类型上有别,而调用方普遍写
    /// <c>Declare(id, [new() { … }])</c> —— 目标类型化的 <c>new()</c> 在两个候选之间无从推断,
    /// 编译期就是一句二义调用。名字分开,调用点一眼看得出登记的是哪一种。
    /// </para>
    /// </summary>
    /// <param name="pluginId">插件 id。</param>
    /// <param name="workspaces">清单里的工作台贡献。</param>
    public void DeclareWorkspaces(string pluginId, IEnumerable<VelaShell.PluginSdk.WorkspaceContribution> workspaces)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        bool changed = false;
        lock (_gate)
        {
            foreach (VelaShell.PluginSdk.WorkspaceContribution workspace in workspaces)
            {
                changed |= _declared.TryAdd(workspace.Id,
                    new(workspace.Id, workspace.DisplayName, workspace.DefaultPort, pluginId, IsReady: false,
                        PluginConnectionKind.Workspace));
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
            foreach (string id in _workspaces.Where(entry => entry.Value.PluginId == pluginId).Select(static entry => entry.Key).ToList())
            {
                _workspaces.Remove(id);
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
        ArgumentNullException.ThrowIfNull(fileSystem);
        return Register(pluginId, descriptor, fileSystem, terminal: null);
    }

    /// <summary>插件激活后注册一种**终端**协议的实现(Telnet / 串口…);释放返回值即注销。</summary>
    /// <param name="pluginId">插件 id。</param>
    /// <param name="descriptor">协议描述。</param>
    /// <param name="terminal">终端实现。</param>
    /// <returns>注销句柄。</returns>
    public IDisposable Register(string pluginId, ProtocolDescriptor descriptor, IProtocolTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        return Register(pluginId, descriptor, fileSystem: null, terminal);
    }

    private IDisposable Register(
        string pluginId,
        ProtocolDescriptor descriptor,
        IProtocolFileSystem? fileSystem,
        IProtocolTerminal? terminal)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        string protocolId = descriptor.Id;
        // 订阅委托按注册捕获协议 id:插件触发事件时的 sender 是什么由插件决定,不能拿它反查。
        void Handler(object? _, ProtocolSessionStateChange change) =>
            SessionStateChanged?.Invoke(this, new(protocolId, change));
        var entry = new Entry(new(pluginId, descriptor, fileSystem, terminal), Handler);
        Entry? replaced;
        lock (_gate)
        {
            _registered.TryGetValue(protocolId, out replaced);
            _registered[protocolId] = entry;
        }
        // 订阅与拆订阅都在锁外:见 RemovePlugin 处的说明。
        // 终端协议没有会话状态事件(标签的连接状态由桥的 EOF 驱动),因此只有文件协议要订阅。
        fileSystem?.SessionStateChanged += Handler;
        if (replaced is not null)
        {
            Detach(replaced);
            // 换成了**另一个**实现:旧实现名下的会话已无人应答,得让文件服务收尾。
            // 换成同一个实例(插件为刷新文案而重注册)则不能发 —— 那会把用户正开着的
            // 标签页全部掐掉,而它本来只是想换个字段标签。
            if (!ReferenceEquals(replaced.Registration.FileSystem, fileSystem)
                || !ReferenceEquals(replaced.Registration.Terminal, terminal))
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

    /// <summary>插件激活后注册一种工作台连接类型;释放返回值即注销。</summary>
    /// <param name="pluginId">插件 id。</param>
    /// <param name="descriptor">连接类型描述。</param>
    /// <param name="provider">工作台实现。</param>
    /// <returns>注销句柄。</returns>
    public IDisposable RegisterWorkspace(string pluginId, WorkspaceDescriptor descriptor, IWorkspaceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(provider);
        var registration = new PluginWorkspaceRegistration(pluginId, descriptor, provider);
        string id = descriptor.Id;
        bool replacedByAnother;
        lock (_gate)
        {
            replacedByAnother = _workspaces.TryGetValue(id, out PluginWorkspaceRegistration? previous)
                                && !ReferenceEquals(previous.Provider, provider);
            _workspaces[id] = registration;
        }
        if (replacedByAnother)
        {
            // 换成了**另一个**实现:旧实现名下的文档已无人应答,得让宿主收尾关闭。
            // 换成同一个实例(插件为刷新文案而重注册)则不发 —— 那会把用户正开着的标签页全掐掉。
            RaiseUnregistered(id);
        }
        RaiseChanged();
        return new UnregisterWorkspace(this, id, registration);
    }

    /// <summary>按 id 取回已注册的工作台;未注册时返回 <see langword="false" />。</summary>
    /// <param name="workspaceId">连接类型 id。</param>
    /// <param name="registration">已注册的工作台。</param>
    /// <returns>是否已注册。</returns>
    public bool TryGetWorkspace(string workspaceId, out PluginWorkspaceRegistration registration)
    {
        lock (_gate)
        {
            if (_workspaces.TryGetValue(workspaceId, out PluginWorkspaceRegistration? found))
            {
                registration = found;
                return true;
            }
        }
        registration = null!;
        return false;
    }

    /// <summary>
    /// 某个连接类型 id 的形态;未声明也未注册时返回 <see langword="null" />。
    /// <para>
    /// **同步查询,不会装载插件** —— 会话树画图标、双击决定"开文件浏览器还是开工作台"都要在
    /// 装配集未装载的前提下答得出来。
    /// </para>
    /// </summary>
    /// <param name="connectionTypeId">连接类型 id。</param>
    /// <returns>形态,或 null。</returns>
    public PluginConnectionKind? KindOf(string? connectionTypeId)
    {
        if (string.IsNullOrWhiteSpace(connectionTypeId))
        {
            return null;
        }
        lock (_gate)
        {
            if (_workspaces.ContainsKey(connectionTypeId))
            {
                return PluginConnectionKind.Workspace;
            }
            if (_registered.ContainsKey(connectionTypeId))
            {
                return PluginConnectionKind.FileSystem;
            }
            return _declared.TryGetValue(connectionTypeId, out PluginProtocolTab? tab) ? tab.Kind : null;
        }
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
        if (!await EnsureActivatedAsync(protocolId).ConfigureAwait(false))
        {
            return null;
        }
        return TryGet(protocolId, out PluginProtocolRegistration activated) ? activated : null;
    }

    /// <summary>
    /// 取回工作台实现;未注册时先尝试惰性激活其提供者插件,仍拿不到则返回 <see langword="null" />。
    /// </summary>
    /// <param name="workspaceId">连接类型 id。</param>
    /// <returns>已注册的工作台,或 null。</returns>
    public async Task<PluginWorkspaceRegistration?> ResolveWorkspaceAsync(string? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return null;
        }
        if (TryGetWorkspace(workspaceId, out PluginWorkspaceRegistration registration))
        {
            return registration;
        }
        if (!await EnsureActivatedAsync(workspaceId).ConfigureAwait(false))
        {
            return null;
        }
        return TryGetWorkspace(workspaceId, out PluginWorkspaceRegistration activated) ? activated : null;
    }

    /// <summary>惰性激活声明了该 id 的插件;未声明或没有激活钩子时直接返回 false。</summary>
    private async Task<bool> EnsureActivatedAsync(string id)
    {
        bool declared;
        lock (_gate)
        {
            declared = _declared.ContainsKey(id);
        }
        if (!declared || ActivationRequested is not { } activate)
        {
            return false;
        }
        try
        {
            // **必须 Task.Run**:激活链上没有任何真正的让出点 —— PluginAssemblyLoadContext 的
            // 装配集加载、Activator.CreateInstance、插件那句 `return Task.CompletedTask` 全是同步的。
            // 两个调用点(连接页选中页签、打开一条会话)都在 UI 线程 await 这里,
            // 不推到线程池就是"点一下 S3 页签,界面连同 IsPluginLoading 的转圈一起冻住"。
            return await Task.Run(() => activate(id)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PluginProtocols] Activation for connection type '{id}' failed: {ex.Message}");
            return false;
        }
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
        if (entry.Registration.FileSystem is not { } fileSystem)
        {
            return; // 终端协议没订阅过,无从拆起。
        }
        try
        {
            fileSystem.SessionStateChanged -= entry.Handler;
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

    private sealed class UnregisterWorkspace(PluginProtocolRegistry owner, string id, PluginWorkspaceRegistration registration)
        : IDisposable
    {
        public void Dispose()
        {
            bool removed;
            lock (owner._gate)
            {
                // 只有仍是自己那一份才撤:同 id 被后来者替换过时不能把别人的注册删掉。
                removed = owner._workspaces.TryGetValue(id, out PluginWorkspaceRegistration? current)
                          && ReferenceEquals(current, registration);
                if (removed)
                {
                    owner._workspaces.Remove(id);
                }
            }
            if (removed)
            {
                owner.RaiseUnregistered(id);
                owner.RaiseChanged();
            }
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
