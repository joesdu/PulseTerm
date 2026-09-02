using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Core.Resources;
using VelaShell.Core.Services;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.Infrastructure.Plugins.Isolated;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Hosting;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Manifest;
using VelaShell.PluginSdk.Packaging;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.RemoteTunnel;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// 进程内插件运行时:发现(只读 manifest,不碰程序集)→ 装载(每插件一个可收集 ALC)
/// → 激活 → 停用/卸载。故障完全隔离:单个插件的任何异常只把它自己标记为 Failed,
/// 绝不影响宿主与其它插件。发现与激活都应在后台线程调用(<see cref="StartAsync" />
/// 不碰 UI),启动路径零开销 —— 没有插件时只有两次目录存在性检查。
/// </summary>
public sealed class PluginManager(PluginManagerOptions options) : IAsyncDisposable
{
    private readonly HashSet<string> _trustedPackageKeys = new(options.TrustedPackageKeys ?? [], StringComparer.Ordinal);
    private readonly Lock _trustedPackageKeysGate = new();
    private readonly SemaphoreSlim _trustStateGate = new(1, 1);
    private PluginTrustState? _trustState;
    private string? _trustLoadError;
    private bool _trustInitialized;
    private sealed class PluginRuntime
    {
        public required PluginDescriptor Descriptor { get; init; }
        public PluginAssemblyLoadContext? LoadContext { get; set; }
        public IVelaPlugin? Instance { get; set; }
        public PluginContext? Context { get; set; }

        /// <summary>隔离模式的进程句柄;进程内模式为 null。</summary>
        public PluginProcessClient? Process { get; set; }

        /// <summary>滑动窗口内的崩溃时间戳(TickCount64),驱动退避重启上限。</summary>
        public List<long> CrashTimes { get; } = [];

        /// <summary>每插件的日志(占位命令与上下文共用,先于激活存在)。</summary>
        public TracePluginLogger? Logger { get; set; }

        /// <summary>
        /// 每插件的命令能力实例:占位命令与激活后的真实注册共用同一实例,
        /// 真实注册按 id 替换占位。停用/失败后置空,下次激活/回挂重建。
        /// </summary>
        public ICommandsApi? CommandsApi { get; set; }

        /// <summary>激活/回收互斥闸:惰性触发、崩溃重启与空闲回收不并发换态。</summary>
        public SemaphoreSlim ActivationGate { get; } = new(1, 1);

        /// <summary>最近一次 RPC 往来(TickCount64),空闲回收依据(仅隔离模式)。</summary>
        public long LastActivityTicks { get; set; }

        /// <summary>插件进程当前打开的面板数(插件上报);非零时不回收。</summary>
        public int OpenSurfaces { get; set; }

        /// <summary>
        /// 是否需要安装凭据校验(用户目录下的已安装插件)。自带插件与开发期插件不在此列。
        /// </summary>
        public bool NeedsVerification { get; set; }

        /// <summary>
        /// 安装凭据校验的记忆化结果:<see langword="null" /> 结果表示通过,非空字符串是拒绝原因。
        /// 一次进程生命周期内只算一遍 —— 它要读遍插件目录的每个字节,
        /// 而"回收后再激活"这种循环里重复付这个代价是纯浪费。
        /// </summary>
        public Task<string?>? Verification { get; set; }

        /// <summary>校验过程是否已把该插件的文件读了一遍(读过就等于预读过,不必再预热)。</summary>
        public bool ContentRead { get; set; }
    }

    private readonly List<PluginRuntime> _plugins = [];
    /// <summary>
    /// 主题快照的共享采集点。惰性:一个插件都没有的宿主不该为此订阅主题服务、
    /// 更不该去遍历资源树。见 <see cref="HostThemeSource" />。
    /// </summary>
    private readonly Lazy<HostThemeSource> _themeSource = new(() =>
        new(options.Theme, options.SystemPrefersDark, options.ThemeTokensProvider));
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<FileSystemWatcher> _devWatchers = [];
    private readonly Lock _devDisabledGate = new();
    private Timer? _devWatchDebounce;
    private bool _started;
    private bool _disposed;

    /// <summary>全部已发现插件的描述快照(含失败/禁用者及其原因)。</summary>
    public IReadOnlyList<PluginDescriptor> Plugins
    {
        get
        {
            lock (_gate)
            {
                return [.. _plugins.Select(p => p.Descriptor)];
            }
        }
    }

    /// <summary>插件集合或某插件状态变化时触发(供管理页刷新);在后台线程触发。</summary>
    public event Action? Changed;

    /// <summary>
    /// 某个隔离插件的宿主进程正挂起等待调试器附加时触发(插件 id,进程 id)。
    /// 界面据此提示开发者"附加到哪个进程" —— 这个数字只打进日志的话,
    /// 等于要求开发者在最需要专注的时刻去翻日志。在后台线程触发。
    /// </summary>
    public event Action<string, int>? DebugAttachRequested;

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // 通知订阅方异常不回灌运行时。
        }
    }

    /// <summary>
    /// 禁用插件(管理页):落 <c>.disabled</c> 标记(重启后仍禁用),运行中的即刻停用,
    /// 惰性占位命令撤下。数据保留(禁用 ≠ 卸载)。
    /// </summary>
    public async Task DisableAsync(string pluginId)
    {
        PluginRuntime? runtime;
        lock (_gate)
        {
            runtime = _plugins.FirstOrDefault(p => p.Descriptor.Id == pluginId);
        }
        if (runtime is null || runtime.Descriptor.State == PluginState.Disabled)
        {
            return;
        }
        await runtime.ActivationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SetDisabledMarker(runtime.Descriptor, disabled: true);
            if (runtime.Descriptor.State == PluginState.Active)
            {
                await DeactivateAsync(runtime).ConfigureAwait(false);
            }
            (runtime.CommandsApi as IDisposable)?.Dispose();
            runtime.CommandsApi = null;
            // 先写终态再撤注册,顺序固定:反过来的话,撤注册与写状态之间的窗口里
            // 若有激活线程完成,它会把 State 覆盖回 Active 并重新注册协议。
            runtime.Descriptor.State = PluginState.Disabled;
            runtime.Descriptor.Error = null;
            // 连声明一起撤:禁用后连接页不该还留着一个点了就报"协议不可用"的页签。
            options.ProtocolRegistry?.RemovePlugin(pluginId);
            Log($"Disabled plugin '{pluginId}'.");
        }
        finally
        {
            runtime.ActivationGate.Release();
        }
        RaiseChanged();
    }

    /// <summary>
    /// 启用插件(管理页):移除 <c>.disabled</c> 标记;按其激活策略立即激活(onStartup)
    /// 或重挂占位命令(惰性)。
    /// </summary>
    public async Task EnableAsync(string pluginId)
    {
        PluginRuntime? runtime;
        lock (_gate)
        {
            runtime = _plugins.FirstOrDefault(p => p.Descriptor.Id == pluginId);
        }
        if (runtime is null || runtime.Descriptor.State != PluginState.Disabled || runtime.Descriptor.Manifest is null)
        {
            return;
        }
        await runtime.ActivationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (await EnsureVerifiedAsync(runtime).ConfigureAwait(false) is { } rejection)
            {
                RejectAsTampered(runtime, rejection);
                return;
            }
            SetDisabledMarker(runtime.Descriptor, disabled: false);
            runtime.Descriptor.State = PluginState.Discovered;
            runtime.Descriptor.Error = null;
            DeclareProtocols(runtime);
            if (runtime.Descriptor.Manifest.ActivatesOnStartup)
            {
                await ActivateAsync(runtime, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                RegisterActivationTriggers(runtime);
            }
            Log($"Enabled plugin '{pluginId}'.");
        }
        finally
        {
            runtime.ActivationGate.Release();
        }
        RaiseChanged();
    }

    /// <summary>是否支持 <c>.vpx</c> 安装(存在可写用户插件目录)。</summary>
    public bool IsInstallSupported => options.UserPluginRoot is not null;

    /// <summary>
    /// 该插件是否为用户安装(位于 <see cref="PluginManagerOptions.UserPluginRoot" /> 下),
    /// 从而可卸载。应用自带插件(只读安装目录)不可卸载。
    /// </summary>
    public bool IsUninstallable(string pluginId)
    {
        if (options.UserPluginRoot is null)
        {
            return false;
        }
        lock (_gate)
        {
            return _plugins.FirstOrDefault(p => p.Descriptor.Id == pluginId) is { } runtime
                   && IsUserPluginDirectory(runtime.Descriptor.Directory);
        }
    }

    /// <summary>
    /// 卸载插件(仅用户安装的):停用 → 删除插件目录 → 清除其 DB 数据与数据目录 →
    /// 从集合移除。应用自带插件拒绝(返回 false)。
    /// </summary>
    public async Task<bool> UninstallAsync(string pluginId)
    {
        if (!IsUninstallable(pluginId))
        {
            return false;
        }
        PluginRuntime? runtime;
        lock (_gate)
        {
            runtime = _plugins.FirstOrDefault(p => p.Descriptor.Id == pluginId);
        }
        if (runtime is null)
        {
            return false;
        }
        await runtime.ActivationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (runtime.Descriptor.State == PluginState.Active)
            {
                await DeactivateAsync(runtime).ConfigureAwait(false);
            }
            (runtime.CommandsApi as IDisposable)?.Dispose();
            runtime.CommandsApi = null;
            options.ProtocolRegistry?.RemovePlugin(pluginId);
            TryDeleteDirectory(runtime.Descriptor.Directory);
            await PurgePluginDataAsync(pluginId).ConfigureAwait(false);
            await RemoveInstallReceiptAsync(pluginId).ConfigureAwait(false);
            lock (_gate)
            {
                _plugins.Remove(runtime);
            }
            Log($"Uninstalled plugin '{pluginId}'.");
        }
        finally
        {
            runtime.ActivationGate.Release();
        }
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 从 <c>.vpx</c> 包安装插件(专属容器:魔数头 + SHA-256 + 可选签名,内含 zip 载荷)。
    /// 解包到用户插件目录(zip-slip 与解压炸弹防护),校验清单;同 id 已存在则先卸载旧版。
    /// 安装后按激活策略激活。返回安装的插件 id。
    /// </summary>
    /// <exception cref="InvalidOperationException">无用户插件目录、清单校验失败或签名策略拒绝。</exception>
    /// <exception cref="VpxFormatException">包不是合法的 <c>.vpx</c> 容器,或已损坏/被篡改。</exception>
    public async Task<string> InstallFromVpxAsync(
        string vpxPath,
        bool allowUntrustedPackage = false,
        CancellationToken cancellationToken = default)
    {
        if (options.UserPluginRoot is not { } userRoot)
        {
            throw new InvalidOperationException("Plugin installation is unavailable (no writable plugin directory).");
        }
        ArgumentException.ThrowIfNullOrEmpty(vpxPath);
        if (!File.Exists(vpxPath))
        {
            throw new InvalidOperationException($"Package not found: {vpxPath}");
        }

        // 先解到临时目录并校验,校验过再原子搬进最终位置 —— 避免坏包污染插件目录。
        string staging = Path.Combine(Path.GetTempPath(), "velashell-vpx", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        PluginManifest? manifest = null;
        // 安装是纯等待的几秒:验签 → 解压 → 对整个目录算哈希落安装凭据。
        // 用户从商店下完包点"安装"之后,这段时间界面上必须有东西在动。
        // 进度是按阶段给的粗刻度 —— 每一步内部都拿不到细粒度回调,与其编一个平滑的假进度,
        // 不如让弧一段一段地跳:它至少每一跳都对应真的完成了一件事。
        using IBackgroundActivityScope? activity = options.Activity?.Begin(
            Strings.Get("Msg_PluginInstalling"), Path.GetFileName(vpxPath), progress: 0);
        try
        {
            await EnsureTrustInitializedAsync(cancellationToken).ConfigureAwait(false);
            activity?.Report(0.1);
            VpxPackageInfo packageInfo = ExtractPackage(vpxPath, staging, allowUntrustedPackage);
            activity?.Report(0.5);
            string manifestPath = Path.Combine(staging, PluginManifestReader.FileName);
            if (!File.Exists(manifestPath))
            {
                throw new InvalidOperationException("Package has no plugin.json at its root.");
            }
            manifest = PluginManifestReader.Load(manifestPath); // 坏清单在此抛 PluginManifestException
            // 清单读出来了,副标题就从包文件名换成插件的显示名 —— 那才是用户认得的东西。
            activity?.Report(0.55, DisplayNameOf(manifest));
            string entryPath = Path.Combine(staging, manifest.Entry);
            if (!File.Exists(entryPath))
            {
                throw new InvalidOperationException($"Entry assembly '{manifest.Entry}' is missing from the package.");
            }

            // 同 id 已装 → 先卸载旧版(用户目录的)或拒绝(应用自带的,避免覆盖只读自带件)。
            lock (_gate)
            {
                if (_plugins.FirstOrDefault(p => p.Descriptor.Id == manifest.Id) is { } existing
                    && !IsUserPluginDirectory(existing.Descriptor.Directory))
                {
                    throw new InvalidOperationException(
                        $"A built-in plugin with id '{manifest.Id}' already exists and cannot be replaced.");
                }
            }
            if (_plugins.Any(p => p.Descriptor.Id == manifest.Id))
            {
                await UninstallAsync(manifest.Id).ConfigureAwait(false);
            }

            string target = Path.Combine(userRoot, manifest.Id);
            Directory.CreateDirectory(userRoot);
            TryDeleteDirectory(target);
            Directory.Move(staging, target);
            staging = target; // 已搬走,finally 不再删
            activity?.Report(0.7);

            try
            {
                await SaveInstallReceiptAsync(manifest.Id, target, packageInfo, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 没有受保护收据的目录绝不能留下来被下次启动误认为已安装。
                TryDeleteDirectory(target);
                throw;
            }

            activity?.Report(0.9);
            PluginDescriptor installed = Describe(target, Path.Combine(target, PluginManifestReader.FileName), [], false,
                out bool needsVerification);
            // 装完随即激活会把 SaveInstallReceiptAsync 刚算过的目录哈希再算一遍。看着是浪费,
            // 但**不要**在这里预置校验结果去省它:那等于把"落凭据 → 首次装载"之间的窗口
            // 排除在校验之外,而省下的只是一次热缓存目录的哈希(几十毫秒)。
            // 这个仓库对插件信任面的态度一贯是宁可多算一遍,别为这点时间换窗口。
            var runtime = new PluginRuntime { Descriptor = installed, NeedsVerification = needsVerification };
            lock (_gate)
            {
                _plugins.Add(runtime);
            }
            if (runtime.Descriptor.State == PluginState.Discovered)
            {
                DeclareProtocols(runtime);
                if (manifest.ActivatesOnStartup)
                {
                    await EnsureActivatedAsync(runtime, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    RegisterActivationTriggers(runtime);
                }
            }
            activity?.Report(1);
            Log($"Installed plugin '{manifest.Id}' v{manifest.Version} from package.");
        }
        finally
        {
            if (Directory.Exists(staging) && !string.Equals(staging, Path.Combine(userRoot, manifest?.Id ?? ""), StringComparison.Ordinal))
            {
                TryDeleteDirectory(staging);
            }
        }
        RaiseChanged();
        return manifest!.Id;
    }

    /// <summary>解压 zip,拒绝绝对路径与 <c>..</c> 逃逸(zip-slip 防护)。</summary>
    /// <summary>单包条目数上限。</summary>
    private const int MaxPackageEntries = 10_000;

    /// <summary>单包解压后总字节上限(解压炸弹防护:压缩比可以做到上千倍)。</summary>
    private const long MaxUnpackedBytes = 512L * 1024 * 1024;

    /// <summary>
    /// 打开插件包并安全解包。只认 <c>.vpx</c> 专属容器(魔数 + 摘要 + 可选签名,见
    /// <see cref="VpxContainer" />)—— 改了后缀的 zip 一律拒绝,拒绝原因里带重新打包的办法。
    /// </summary>
    private VpxPackageInfo ExtractPackage(string packagePath, string destination, bool allowUntrustedPackage)
    {
        // 格式非法(裸 zip / 截断 / 头部损坏 / 根本不是包)在此抛出并带可读原因。
        using Stream payload = VpxContainer.OpenPayload(packagePath, out VpxPackageInfo info);
        CheckSignature(packagePath, info, allowUntrustedPackage);
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        ExtractZipSafely(archive, destination);
        return info;
    }

    /// <summary>
    /// 签名闸。坏签名一律拒绝(哪怕没开强制签名)—— 签名对不上意味着内容被动过,
    /// 这比"未签名"严重得多,不该因为策略宽松就放行。
    /// </summary>
    private void CheckSignature(string packagePath, VpxPackageInfo info, bool allowUntrustedPackage)
    {
        VpxSignatureState state = GetSignatureState(info);
        string name = Path.GetFileName(packagePath);
        switch (state)
        {
            case VpxSignatureState.Invalid:
                throw new VpxFormatException(
                    $"'{name}' carries an invalid signature: the package was modified after it was signed.");
            case VpxSignatureState.Untrusted when options.RequireTrustedPackageSignature:
                throw new InvalidOperationException(
                    $"'{name}' is signed by an unknown publisher and this host only installs packages from trusted keys.");
            case VpxSignatureState.Untrusted when !allowUntrustedPackage:
                throw new InvalidOperationException(
                    $"'{name}' is signed by an unknown publisher. Explicit approval is required to install it.");
            case VpxSignatureState.Unsigned when options.RequireTrustedPackageSignature:
                throw new InvalidOperationException(
                    $"'{name}' is not signed and this host only installs packages from trusted keys.");
            case VpxSignatureState.Unsigned when !allowUntrustedPackage:
                throw new InvalidOperationException(
                    $"'{name}' is not signed. Explicit approval is required to install it.");
            case VpxSignatureState.Trusted:
                Log($"Package '{name}' carries a trusted signature.");
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// 校验容器摘要并返回发布者信任状态,供交互安装入口在落盘前显示准确警告。
    /// 坏包会像正式安装一样抛出 <see cref="VpxFormatException" />。
    /// </summary>
    public VpxSignatureState InspectPackageSignature(string packagePath)
    {
        using Stream _ = VpxContainer.OpenPayload(packagePath, out VpxPackageInfo info);
        return GetSignatureState(info);
    }

    /// <summary>读取包签名及可供用户通过独立渠道核对的 SHA-256 公钥指纹。</summary>
    public PluginPackageTrustInfo InspectPackageTrust(string packagePath)
    {
        using Stream _ = VpxContainer.OpenPayload(packagePath, out VpxPackageInfo info);
        return new(GetSignatureState(info), Fingerprint(info.Signature?.PublicKey));
    }

    /// <summary>
    /// 把一个签名有效但发布者未知的公钥加入本机信任库。未签名、坏签名或已经可信的包拒绝走此入口。
    /// </summary>
    public async Task<string> TrustPackagePublisherAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        await EnsureTrustInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (options.TrustRepository is not null && _trustState is null)
        {
            throw new InvalidOperationException($"Plugin trust store is unavailable: {_trustLoadError ?? "unknown error"}");
        }
        using Stream _ = VpxContainer.OpenPayload(packagePath, out VpxPackageInfo info);
        if (info.Signature is not { PublicKey: { Length: > 0 } publicKey }
            || GetSignatureState(info) != VpxSignatureState.Untrusted)
        {
            throw new InvalidOperationException("Only a valid package from an unknown publisher can establish publisher trust.");
        }
        string fingerprint = VpxContainer.PublicKeyFingerprint(publicKey);
        await _trustStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool added;
            lock (_trustedPackageKeysGate)
            {
                added = _trustedPackageKeys.Add(publicKey);
            }
            TrustedPluginPublisher? publisher = null;
            try
            {
                if (options.TrustRepository is not null && _trustState is not null
                    && !_trustState.Publishers.Any(p => p.PublicKey == publicKey))
                {
                    publisher = new(publicKey, fingerprint, DateTimeOffset.UtcNow);
                    _trustState.Publishers.Add(publisher);
                    await options.TrustRepository.SaveAsync(_trustState, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                if (publisher is not null)
                {
                    _trustState!.Publishers.Remove(publisher);
                }
                if (added)
                {
                    lock (_trustedPackageKeysGate)
                    {
                        _trustedPackageKeys.Remove(publicKey);
                    }
                }
                throw;
            }
        }
        finally
        {
            _trustStateGate.Release();
        }
        return fingerprint;
    }

    private VpxSignatureState GetSignatureState(VpxPackageInfo info)
    {
        lock (_trustedPackageKeysGate)
        {
            return VpxContainer.VerifySignature(info, _trustedPackageKeys);
        }
    }

    private static string? Fingerprint(string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return null;
        }
        try
        {
            return VpxContainer.PublicKeyFingerprint(publicKey);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task EnsureTrustInitializedAsync(CancellationToken cancellationToken)
    {
        if (_trustInitialized)
        {
            return;
        }
        await _trustStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_trustInitialized)
            {
                return;
            }
            if (options.TrustRepository is null)
            {
                _trustInitialized = true;
                return;
            }
            try
            {
                _trustState = await options.TrustRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
                lock (_trustedPackageKeysGate)
                {
                    foreach (TrustedPluginPublisher publisher in _trustState.Publishers)
                    {
                        _trustedPackageKeys.Add(publisher.PublicKey);
                    }
                }
                if (PruneReceiptsWithoutDirectory(_trustState))
                {
                    await options.TrustRepository.SaveAsync(_trustState, cancellationToken).ConfigureAwait(false);
                }
                _trustInitialized = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _trustState = null;
                _trustLoadError = ex.Message;
                _trustInitialized = true;
                lock (_trustedPackageKeysGate)
                {
                    _trustedPackageKeys.Clear();
                    foreach (string configuredKey in options.TrustedPackageKeys ?? [])
                    {
                        _trustedPackageKeys.Add(configuredKey);
                    }
                }
                Log($"Plugin trust store unavailable; user plugins will be rejected: {ex.Message}");
            }
        }
        finally
        {
            _trustStateGate.Release();
        }
    }

    /// <summary>
    /// 丢掉目录已经不在了的收据。返回是否真的改动过状态。
    /// <para>
    /// 卸载有两条路:管理页那条会顺手删收据,命令行 <c>vela-plugin uninstall</c> 只删目录 ——
    /// 它够不着宿主进程里的信任库。留着那份孤儿收据的后果是:同一个 id 下次再装回来时,
    /// 内容哈希必然与旧收据对不上,插件会以"文件被改过"被拒,而用户什么都没改过。
    /// 目录都不在了,收据保护的东西也就不在了,启动时清掉是安全的。
    /// </para>
    /// </summary>
    private bool PruneReceiptsWithoutDirectory(PluginTrustState state)
    {
        if (options.UserPluginRoot is not { } root || !Directory.Exists(root))
        {
            return false;
        }
        string[] orphans = [.. state.Receipts.Keys.Where(id => !Directory.Exists(Path.Combine(root, id)))];
        foreach (string id in orphans)
        {
            state.Receipts.Remove(id);
        }
        return orphans.Length > 0;
    }

    private async Task SaveInstallReceiptAsync(
        string pluginId,
        string directory,
        VpxPackageInfo packageInfo,
        CancellationToken cancellationToken)
    {
        if (options.TrustRepository is null)
        {
            return;
        }
        if (_trustState is null)
        {
            throw new InvalidOperationException($"Plugin trust store is unavailable: {_trustLoadError ?? "unknown error"}");
        }
        await _trustStateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InstalledPluginReceipt? previous = _trustState.Receipts.GetValueOrDefault(pluginId);
            _trustState.Receipts[pluginId] = new(pluginId, ComputePluginContentSha256(directory),
                packageInfo.PayloadSha256, packageInfo.Signature?.PublicKey, LegacyAdopted: false, DateTimeOffset.UtcNow);
            try
            {
                await options.TrustRepository.SaveAsync(_trustState, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (previous is null)
                {
                    _trustState.Receipts.Remove(pluginId);
                }
                else
                {
                    _trustState.Receipts[pluginId] = previous;
                }
                throw;
            }
        }
        finally
        {
            _trustStateGate.Release();
        }
    }

    private async Task RemoveInstallReceiptAsync(string pluginId)
    {
        if (options.TrustRepository is null || _trustState is null)
        {
            return;
        }
        await _trustStateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_trustState.Receipts.Remove(pluginId, out InstalledPluginReceipt? removed))
            {
                return;
            }
            try
            {
                await options.TrustRepository.SaveAsync(_trustState).ConfigureAwait(false);
            }
            catch
            {
                _trustState.Receipts[pluginId] = removed;
                throw;
            }
        }
        finally
        {
            _trustStateGate.Release();
        }
    }

    private static string ComputePluginContentSha256(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Plugin root is a symbolic link.");
        }
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.TryPop(out string? directory))
        {
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Plugin directory contains a symbolic link: {Path.GetRelativePath(fullRoot, child)}");
                }
                pending.Push(child);
            }
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (directory == fullRoot && Path.GetFileName(file).Equals(".disabled", StringComparison.Ordinal))
                {
                    continue;
                }
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Plugin directory contains a symbolic link: {Path.GetRelativePath(fullRoot, file)}");
                }
                files.Add(file);
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> frame = stackalloc byte[8];

        // 读缓冲在循环外开一次并跨文件复用:原先每个文件都新开 64 KB,一个两百来个文件的
        // 插件光在这里就扔掉十几 MB 垃圾,而这条路径是安装/校验时的启动开销。
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (string file in files.OrderBy(f => Path.GetRelativePath(fullRoot, f).Replace('\\', '/'), StringComparer.Ordinal))
            {
                byte[] path = Encoding.UTF8.GetBytes(Path.GetRelativePath(fullRoot, file).Replace('\\', '/'));
                BinaryPrimitives.WriteInt32LittleEndian(frame, path.Length);
                hash.AppendData(frame[..4]);
                hash.AppendData(path);
                using FileStream stream = File.OpenRead(file);
                BinaryPrimitives.WriteInt64LittleEndian(frame, stream.Length);
                hash.AppendData(frame);
                int read;
                while ((read = stream.Read(buffer)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, read));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void ExtractZipSafely(ZipArchive archive, string destination)
    {
        string root = Path.GetFullPath(destination + Path.DirectorySeparatorChar);
        if (archive.Entries.Count > MaxPackageEntries)
        {
            throw new InvalidOperationException(
                $"Rejected package: it has {archive.Entries.Count} entries (limit {MaxPackageEntries}).");
        }
        long budget = MaxUnpackedBytes;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string targetPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!targetPath.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Rejected unsafe package entry (path escape): {entry.FullName}");
            }
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using Stream source = entry.Open();
            using FileStream target = File.Create(targetPath);
            // 按实际写出的字节数记账,而不是信 entry.Length —— 中央目录里的长度是包自己写的,
            // 炸弹包大可以谎报 1 KB 再吐出 10 GB。
            budget -= CopyBounded(source, target, budget, entry.FullName);
        }
    }

    /// <summary>把条目内容拷进目标流,超出预算即中止并抛出。返回实际写出的字节数。</summary>
    /// <remarks>本方法按 zip 条目逐个调用,缓冲走池:上千条目的包不必扔掉上千个 80 KB 数组。</remarks>
    private static long CopyBounded(Stream source, Stream destination, long budget, string entryName)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long written = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > budget)
                {
                    throw new InvalidOperationException(
                        $"Rejected package: unpacked size exceeds {MaxUnpackedBytes} bytes (while extracting '{entryName}').");
                }
                destination.Write(buffer, 0, read);
            }
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>删除某插件的 DB 命名空间与数据目录(卸载/覆盖安装时)。</summary>
    private async Task PurgePluginDataAsync(string pluginId)
    {
        if (options.DataStore is { } dataStore)
        {
            try
            {
                await dataStore.PurgeAsync(pluginId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"Could not purge database data of '{pluginId}': {ex.Message}");
            }
        }
        TryDeleteDirectory(Path.Combine(options.DataRootDirectory, pluginId));
    }

    private static bool IsUnder(string path, string root)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison)
               || string.Equals(full, rootFull, comparison);
    }

    private static void TryDeleteDirectory(string directory)
    {
        // 进程内插件的入口 dll 可能仍被刚 Unload() 的可收集 ALC 锁着(卸载是异步的,
        // 要等 GC 回收才释放文件句柄)。删失败就逼一轮 GC 再试,几轮后仍不成才放弃。
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 3)
                {
                    Trace.WriteLine($"[PluginManager] Could not delete '{directory}': {ex.Message}");
                    return;
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    /// <summary>
    /// 落/清禁用标记。已安装插件写插件目录里的 <c>.disabled</c>;
    /// **开发期插件写数据根一侧的登记文件** —— 它的"插件目录"是工程的构建产物目录,
    /// 标记写进去既会被 <c>dotnet clean</c> 顺手抹掉,又不会被 <c>dotnet build</c> 清除,
    /// 于是表现为"我明明重编了怎么还是禁用状态"。
    /// </summary>
    private void SetDisabledMarker(PluginDescriptor descriptor, bool disabled)
    {
        if (descriptor.IsDevelopment)
        {
            SetDevDisabled(descriptor.Id, disabled);
            return;
        }
        string marker = Path.Combine(descriptor.Directory, ".disabled");
        try
        {
            if (disabled)
            {
                File.WriteAllText(marker, "");
            }
            else if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 应用目录只读(商店版)时标记写不进:运行时状态仍已切换,只是重启不持久。
        }
    }

    /// <summary>开发期插件的禁用集合(懒加载;登记文件缺席或读不出时为空集)。</summary>
    private HashSet<string> DevDisabled
    {
        get
        {
            lock (_devDisabledGate)
            {
                if (field is not null)
                {
                    return field;
                }
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (options.DevDisabledStateFile is { } file && File.Exists(file))
                    {
                        foreach (string line in File.ReadAllLines(file))
                        {
                            string id = line.Trim();
                            if (id.Length > 0 && !id.StartsWith('#'))
                            {
                                set.Add(id);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"Could not read the development disable list: {ex.Message}");
                }
                return field = set;
            }
        }
    }

    /// <summary>登记/撤销某开发期插件的禁用状态(持久化到数据根一侧的登记文件)。</summary>
    private void SetDevDisabled(string pluginId, bool disabled)
    {
        lock (_devDisabledGate)
        {
            HashSet<string> set = DevDisabled;
            if (disabled ? !set.Add(pluginId) : !set.Remove(pluginId))
            {
                return; // 状态没变,不必落盘。
            }
            if (options.DevDisabledStateFile is not { } file)
            {
                return; // 没配登记文件:仅本次运行有效(headless 测试路径)。
            }
            try
            {
                string? directory = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllLines(file, set.Order(StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"Could not persist the development disable list: {ex.Message}");
            }
        }
    }

    /// <summary>发现并激活全部可用插件。幂等:重复调用为空操作。应在后台线程调用。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }
            _started = true;
        }
        if (options.ProtocolRegistry is { } registry)
        {
            registry.ActivationRequested = ActivateForProtocolAsync;
        }
        await EnsureTrustInitializedAsync(cancellationToken).ConfigureAwait(false);
        Discover();
        List<PluginRuntime> discovered;
        lock (_gate)
        {
            discovered = [.. _plugins.Where(p => p.Descriptor.State == PluginState.Discovered)];
        }

        // 惰性触发器先挂:注册占位命令是纯内存操作,把它排在启动激活之前,
        // 命令面板与协议页签就在最慢那个 onStartup 插件还在装载时已经可用了。
        PluginRuntime[] startup = [.. discovered.Where(r => r.Descriptor.Manifest!.ActivatesOnStartup)];
        foreach (PluginRuntime runtime in discovered.Except(startup))
        {
            // 惰性激活(蓝图 D7):只注册清单声明的占位命令,不碰程序集/不拉进程。
            RegisterActivationTriggers(runtime);
        }

        // 内容校验并行开跑,但**不在这里等** —— 它要读遍每个已安装插件的每个字节,
        // 而下面的 onStartup 激活只关心自己那一个插件校验完没有(经同一份记忆化结果,
        // 见 EnsureVerifiedAsync,不会重复算)。等在 StartAsync 末尾即可。
        Task verification = VerifyDiscoveredAsync(discovered);

        // onStartup 插件并行激活:它们互不相干(每个都有自己的 ALC / 进程与激活闸),
        // 串行只是让最慢的那一个决定所有人的等待。共享的命令注册表是线程安全的。
        if (startup.Length > 0 && !cancellationToken.IsCancellationRequested)
        {
            using IBackgroundActivityScope? activity = options.Activity?.Begin(
                Strings.Get("Msg_PluginActivating"), progress: startup.Length > 1 ? 0 : null);
            int done = 0;
            await Task.WhenAll(startup.Select(async runtime =>
            {
                // 走激活闸而不是直调:启动激活跑在后台线程上、耗时可达数秒,
                // 这期间用户完全来得及在管理页把它禁用掉。
                await EnsureActivatedAsync(runtime, cancellationToken).ConfigureAwait(false);
                if (startup.Length > 1)
                {
                    activity?.Report((double)Interlocked.Increment(ref done) / startup.Length);
                }
            })).ConfigureAwait(false);
        }

        StartDevWatchers();
        _ = IdleMonitorAsync();
        // 陈旧数据清理与冷启动预读都与"插件现在能不能用"无关,排到后台链上 ——
        // 尤其预读,它得等主窗口把首帧画完才开始,绝不与启动争磁盘。
        _ = HousekeepAsync(discovered);
        // 校验在这里收口:StartAsync 返回即意味着"被改动过的插件已经标红了"。
        // 它全程跑在后台线程上(见 App 的 Task.Run),等它不占用户任何可见时间。
        await verification.ConfigureAwait(false);
    }

    /// <summary>
    /// 启动后的后台家务:清理孤儿影子副本与已卸载插件的残留数据,随后做冷启动预读。
    /// 全程失败不上抛:家务做不成不该把插件运行时拖下水。
    /// </summary>
    private async Task HousekeepAsync(IReadOnlyList<PluginRuntime> discovered)
    {
        try
        {
            PurgeOrphanShadows();
            await PurgeUninstalledDataAsync().ConfigureAwait(false);
            await PrewarmAsync(discovered).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停机,正常路径。
        }
        catch (Exception ex)
        {
            Log($"Plugin housekeeping failed: {ex.Message}");
        }
    }

    /// <summary>把清单声明的命令注册为占位:首次触发即激活插件并转交真实处理器。</summary>
    private void RegisterActivationTriggers(PluginRuntime runtime)
    {
        PluginManifest manifest = runtime.Descriptor.Manifest!;
        CommandContribution[] contributions = manifest.Contributes?.Commands ?? [];
        int protocols = (manifest.Contributes?.Protocols.Length ?? 0) + (manifest.Contributes?.Workspaces.Length ?? 0);
        if (contributions.Length == 0 && protocols == 0)
        {
            Log($"Plugin '{manifest.Id}' has no onStartup activation and no contributed commands, protocols or workspaces; it will never activate.");
            return;
        }
        if (contributions.Length == 0)
        {
            // 只贡献连接类型的插件(S3、Redis 都是):触发器是"用户在连接页选中这个页签",
            // 由注册表经 ActivationRequested 回调驱动,这里没有占位命令要挂。
            Log($"Plugin '{manifest.Id}' waiting lazily behind {protocols} connection tab(s).");
            return;
        }
        ICommandsApi commands = GetOrCreateCommandsApi(runtime);
        foreach (CommandContribution contribution in contributions)
        {
            string commandId = contribution.Id;
            try
            {
                commands.Register(new(commandId, contribution.Title, contribution.Category,
                    _ => OnTriggerCommandAsync(runtime, commandId)));
            }
            catch (Exception ex)
            {
                Log($"Failed to register placeholder command '{commandId}': {ex.Message}");
            }
        }
        Log(protocols == 0
            ? $"Plugin '{manifest.Id}' waiting lazily behind {contributions.Length} command trigger(s)."
            : $"Plugin '{manifest.Id}' waiting lazily behind {contributions.Length} command trigger(s) and {protocols} protocol tab(s).");
    }

    /// <summary>占位命令命中:激活插件,然后把这次触发转交激活期间注册的真实处理器。</summary>
    private async Task OnTriggerCommandAsync(PluginRuntime runtime, string commandId)
    {
        if (runtime.Descriptor.State == PluginState.Active)
        {
            // 已激活还进到占位回调 = 插件激活时没有按清单重注册该命令。
            ((IPluginLogger?)runtime.Logger)?.Warn($"Command '{commandId}' is declared in the manifest but was not registered during activation.");
            return;
        }
        if (await EnsureActivatedAsync(runtime).ConfigureAwait(false))
        {
            runtime.CommandsApi?.TryExecute(commandId); // 此刻已被插件的真实注册替换
        }
    }

    /// <summary>
    /// 串行化的按需激活;返回激活后是否 Active(Failed/Disabled 等不由命令救活)。
    /// <para>
    /// **所有激活入口都必须走这里**,而不是直接调 <see cref="ActivateAsync" />。
    /// 闸内会复核 <c>State == Discovered</c>,于是"装载途中用户在管理页点了禁用"这类
    /// check-then-act 被挡住 —— 否则 DisableAsync 会因为此刻 State 还不是 Active 而
    /// 跳过 DeactivateAsync,随后激活线程再把 State 覆盖回 Active:磁盘上写着已禁用、
    /// 进程里却仍在提供协议,注册表里还留下一份永远卸不掉的幽灵注册。
    /// </para>
    /// </summary>
    private async Task<bool> EnsureActivatedAsync(PluginRuntime runtime, CancellationToken cancellationToken = default)
    {
        await runtime.ActivationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runtime.Descriptor.State == PluginState.Active)
            {
                return true;
            }
            if (runtime.Descriptor.State != PluginState.Discovered || _shutdown.IsCancellationRequested)
            {
                return false;
            }
            // 安全边界就在这一句:发现期不再做内容校验,但**任何**装载路径都得先过这道闸。
            if (await EnsureVerifiedAsync(runtime).ConfigureAwait(false) is { } rejection)
            {
                RejectAsTampered(runtime, rejection);
                return false;
            }
            await ActivateAsync(runtime, cancellationToken).ConfigureAwait(false);
            return runtime.Descriptor.State == PluginState.Active;
        }
        finally
        {
            runtime.ActivationGate.Release();
        }
    }

    /// <summary>
    /// 已安装插件的内容校验(记忆化):比对目录内容 SHA256 与安装凭据。
    /// 通过返回 <see langword="null" />,否则返回面向用户的拒绝原因。
    /// <para>
    /// 一次进程生命周期内每个插件只算一遍 —— 校验要读遍插件目录的每个字节,
    /// 而"空闲回收 → 再触发激活"这种循环会反复走到这里。
    /// 目录内容中途被改的场景由重载路径负责(见 <see cref="ReloadAsync" />,它清掉记忆)。
    /// </para>
    /// </summary>
    private Task<string?> EnsureVerifiedAsync(PluginRuntime runtime)
    {
        if (!runtime.NeedsVerification)
        {
            return Task.FromResult<string?>(null);
        }
        lock (_gate)
        {
            return runtime.Verification ??= Task.Run(async () =>
            {
                // 校验读的是信任仓储里的凭据,仓储没初始化就无从比对。
                await EnsureTrustInitializedAsync(_shutdown.Token).ConfigureAwait(false);
                string? error = await VerifyOrAdoptInstallReceiptAsync(
                    runtime.Descriptor.Id, runtime.Descriptor.Directory).ConfigureAwait(false);
                runtime.ContentRead = true; // 刚把整个目录读了一遍 —— 预热不必再来一次。
                return error;
            });
        }
    }

    /// <summary>
    /// 把校验未通过的插件打成 Invalid,并撤下发现期已经挂出去的协议页签与占位命令。
    /// <para>
    /// 撤注册这一步不能省:校验推迟到发现之后,意味着页签与占位命令在校验有结论之前
    /// 就已经摆在用户面前了。留着它们,用户点下去只会得到一次静默的无反应
    /// (激活闸看到 Invalid 直接返回 false),而真正的原因写在管理页上没人会去看。
    /// </para>
    /// </summary>
    private void RejectAsTampered(PluginRuntime runtime, string rejection)
    {
        // 先写终态再撤注册(与 DisableAsync 同一顺序):反过来的话,两步之间的窗口里
        // 若有激活线程完成,它会把 State 覆盖回 Active 并重新注册协议。
        runtime.Descriptor.State = PluginState.Invalid;
        runtime.Descriptor.Error = rejection;
        (runtime.CommandsApi as IDisposable)?.Dispose();
        runtime.CommandsApi = null;
        options.ProtocolRegistry?.RemovePlugin(runtime.Descriptor.Id);
        Log($"Refusing to load '{runtime.Descriptor.Id}': {rejection}");
        RaiseChanged();
    }

    /// <summary>
    /// 后台内容校验巡检:把发现期省下的那遍全量哈希并行补上,发现被改动的插件即刻标红。
    /// <para>
    /// 这不是安全边界(边界在 <see cref="EnsureActivatedAsync" /> 的闸上),而是"提前告知" ——
    /// 让管理页在用户点开它之前就把问题摆出来。并行度按 CPU 收敛:这活儿是磁盘密集型,
    /// 开太多线程只会让磁头/队列互相打架。
    /// </para>
    /// </summary>
    private async Task VerifyDiscoveredAsync(IReadOnlyList<PluginRuntime> candidates)
    {
        PluginRuntime[] pending = [.. candidates.Where(r =>
            r.NeedsVerification && r.Descriptor.State == PluginState.Discovered)];
        if (pending.Length == 0)
        {
            return;
        }
        using IBackgroundActivityScope? activity = options.Activity?.Begin(
            Strings.Get("Msg_PluginVerifying"), progress: 0);
        int done = 0;
        try
        {
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions
                {
                    // 这活儿是磁盘密集型:线程开多了只会让 IO 队列互相打架,4 条足够压住 SSD。
                    MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
                    CancellationToken = _shutdown.Token
                },
                async (runtime, _) =>
                {
                    string? rejection = await EnsureVerifiedAsync(runtime).ConfigureAwait(false);
                    // 状态复核:校验期间用户完全来得及禁用/卸载它,别把终态覆盖回去。
                    if (rejection is not null && runtime.Descriptor.State == PluginState.Discovered)
                    {
                        RejectAsTampered(runtime, rejection);
                    }
                    activity?.Report((double)Interlocked.Increment(ref done) / pending.Length,
                        DisplayNameOf(runtime.Descriptor));
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停机:未校验完的插件保持 Discovered,下次启动重来 ——
            // 装载路径上另有一道同样的闸,漏掉这一遍不构成放行。
        }
    }

    /// <summary>
    /// 惰性插件的冷启动预读:把插件目录里的程序集顺序读一遍,只为把它们抬进操作系统的文件缓存。
    /// <para>
    /// **不装载程序集、不创建 ALC、不跑 <c>ActivateAsync</c>** —— 惰性激活的语义分毫不动,
    /// 内存也不多占一个字节(读进去的是内核页缓存,吃紧时系统自己会回收)。
    /// 省掉的是用户按下命令那一刻的磁盘时间:那是首次激活里唯一一段与插件代码无关、
    /// 却又完全可以提前付掉的成本。
    /// </para>
    /// <para>
    /// 内容校验刚刚读过整个目录的插件直接跳过(<see cref="PluginRuntime.ContentRead" />) ——
    /// 已安装插件的预读实际上是免费搭了那遍哈希的车。
    /// </para>
    /// </summary>
    private async Task PrewarmAsync(IReadOnlyList<PluginRuntime> discovered)
    {
        if (!options.PrewarmLazyPlugins)
        {
            return;
        }
        // 起手先让路:主窗口的首帧、会话恢复与自动连接都排在预读前面。
        if (await DelayObservedAsync(options.PrewarmDelay).ConfigureAwait(false))
        {
            return;
        }
        PluginRuntime[] targets = [.. discovered.Where(r =>
            !r.ContentRead && r.Descriptor.State == PluginState.Discovered)];
        if (targets.Length == 0)
        {
            return;
        }
        using IBackgroundActivityScope? activity = options.Activity?.Begin(
            Strings.Get("Msg_PluginPrewarming"), progress: 0);
        long budget = options.PrewarmByteBudget;
        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < targets.Length && budget > 0; i++)
        {
            PluginRuntime runtime = targets[i];
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }
            // 逐个复核而不是一次性快照:预读期间插件完全可能已被触发激活,那就没必要再读了。
            if (runtime.Descriptor.State != PluginState.Discovered)
            {
                continue;
            }
            activity?.Report((double)i / targets.Length, DisplayNameOf(runtime.Descriptor));
            budget -= await PrewarmDirectoryAsync(runtime.Descriptor.Directory, budget).ConfigureAwait(false);
        }
        Log($"Prewarmed {targets.Length} lazily waiting plugin(s) in {stopwatch.ElapsedMilliseconds}ms " +
            $"({(options.PrewarmByteBudget - budget) / 1024}KB read into the file cache).");
    }

    /// <summary>顺序读一个插件目录下的程序集(只读不留);返回实际读取的字节数。</summary>
    private async Task<long> PrewarmDirectoryAsync(string directory, long budget)
    {
        long read = 0;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            // 只读顶层 dll:runtimes/ 下是按 RID 分叉的原生库,当前平台用得上的那几个
            // 会随首次 P/Invoke 自己进缓存,提前把所有平台的都读一遍纯属浪费。
            foreach (string file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                if (_shutdown.IsCancellationRequested || read >= budget)
                {
                    break;
                }
                try
                {
                    // SequentialScan 明确告诉内核这是"读一遍就走"的顺序访问;
                    // Asynchronous 让这条链不占线程池线程(预读是背景工作,不该挤占前台)。
                    await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                        buffer.Length, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    int chunk;
                    while ((chunk = await stream.ReadAsync(buffer, _shutdown.Token).ConfigureAwait(false)) > 0)
                    {
                        read += chunk;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 预读失败没有任何后果:该文件在真正装载时照常会被打开。
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
        return read;
    }

    /// <summary>面向用户的插件名:清单里的显示名,缺失时退回插件 id。</summary>
    private static string DisplayNameOf(PluginDescriptor descriptor) =>
        descriptor.Manifest is { } manifest ? DisplayNameOf(manifest) : descriptor.Id;

    /// <inheritdoc cref="DisplayNameOf(PluginDescriptor)" />
    private static string DisplayNameOf(PluginManifest manifest) =>
        manifest is { DisplayName: { Length: > 0 } name } ? name : manifest.Id;

    /// <summary>
    /// 空闲巡检(蓝图 04 §资源控制,仅隔离模式):可回收插件连续空闲
    /// (无 RPC 往来且无打开面板)超时即停用回收进程,占位命令留守等待再触发。
    /// </summary>
    private async Task IdleMonitorAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            if (await DelayObservedAsync(options.IdleCheckInterval).ConfigureAwait(false))
            {
                return; // 停机
            }
            List<PluginRuntime> idle;
            long now = Environment.TickCount64;
            lock (_gate)
            {
                idle = [.. _plugins.Where(r =>
                    r is { Process: not null, OpenSurfaces: 0 }
                    && r.Descriptor.State == PluginState.Active
                    && r.Descriptor.Manifest is { IdlePolicy: PluginIdlePolicy.Recyclable } manifest
                    && (manifest.Contributes?.Commands.Length ?? 0) > 0 // 没有回程触发器就不回收
                    && now - r.LastActivityTicks >= options.IdleTimeout.TotalMilliseconds)];
            }
            foreach (PluginRuntime runtime in idle)
            {
                await RecycleAsync(runtime).ConfigureAwait(false);
            }
        }
    }

    /// <summary>回收一个空闲的隔离插件:干净停用 → 回到 Discovered → 占位命令回挂。</summary>
    private async Task RecycleAsync(PluginRuntime runtime)
    {
        await runtime.ActivationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (runtime.Descriptor.State != PluginState.Active || runtime.Process is null || _shutdown.IsCancellationRequested)
            {
                return;
            }
            Log($"Recycling idle plugin '{runtime.Descriptor.Id}' (no RPC activity and no open panels).");
            await DeactivateAsync(runtime).ConfigureAwait(false);
            runtime.Descriptor.State = PluginState.Discovered;
            runtime.Descriptor.Error = null;
            RegisterActivationTriggers(runtime);
        }
        finally
        {
            runtime.ActivationGate.Release();
        }
    }

    /// <summary>可取消延时,经 ContinueWith 观察取消(不制造首发异常);返回是否已停机。</summary>
    private async Task<bool> DelayObservedAsync(TimeSpan delay)
    {
        var wait = Task.Delay(delay, _shutdown.Token);
        await wait.ContinueWith(static _ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
        return wait.IsCanceled;
    }

    /// <summary>
    /// 卸载清理(蓝图 03 §7):插件目录已不存在(≠ 被 .disabled 禁用,禁用者仍在盘上、
    /// 数据保留)即视为已卸载,其 SonnetDB 命名空间与数据目录整体删除。
    /// </summary>
    private async Task PurgeUninstalledDataAsync()
    {
        HashSet<string> installed;
        lock (_gate)
        {
            // 一切仍在盘上的插件都算"在装"(含 Invalid/Disabled/Incompatible —— 修好清单
            // 或重新启用后数据应仍在);Invalid 无清单者以目录名兜底。
            installed = [.. _plugins.Select(p => p.Descriptor.Id)];
        }
        if (options.DataStore is { } dataStore)
        {
            try
            {
                foreach (string orphan in await dataStore.ListPluginIdsAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    if (!installed.Contains(orphan))
                    {
                        await dataStore.PurgeAsync(orphan, _shutdown.Token).ConfigureAwait(false);
                        Log($"Purged database data of uninstalled plugin '{orphan}'.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Uninstalled-plugin database sweep failed: {ex.Message}");
            }
        }
        try
        {
            if (Directory.Exists(options.DataRootDirectory))
            {
                foreach (string dir in Directory.EnumerateDirectories(options.DataRootDirectory))
                {
                    string id = Path.GetFileName(dir);
                    if (installed.Contains(id))
                    {
                        continue;
                    }
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        Log($"Purged data directory of uninstalled plugin '{id}'.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log($"Could not purge data directory of '{id}': {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Uninstalled-plugin directory sweep failed: {ex.Message}");
        }
    }

    /// <summary>隔离插件的子进程 id(测试与诊断用);非隔离/未运行返回 null。</summary>
    internal int? GetIsolatedProcessId(string pluginId)
    {
        lock (_gate)
        {
            return _plugins.FirstOrDefault(p => p.Descriptor.Id == pluginId)?.Process?.ProcessId;
        }
    }

    /// <summary>
    /// 扫描插件根目录:每个含 <c>plugin.json</c> 的一级子目录是一个候选插件。
    /// 只读清单不碰程序集,坏清单给出可读拒绝原因。internal 供单测直接驱动。
    /// </summary>
    internal void Discover()
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<PluginRuntime>();
        // 开发根排在正式根之后:同 id 先到先得,于是本机开发中的插件绝不会顶掉
        // 用户已安装的同名插件(反过来才是意外)。
        IEnumerable<(string Root, bool IsDev)> roots =
            options.PluginRoots.Select(r => (r, false)).Concat(options.DevPluginRoots.Select(r => (r, true)));
        foreach ((string root, bool isDev) in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            foreach (string dir in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                string manifestPath = Path.Combine(dir, PluginManifestReader.FileName);
                if (!File.Exists(manifestPath))
                {
                    continue;
                }
                PluginDescriptor descriptor = Describe(dir, manifestPath, seenIds, isDev, out bool needsVerification);
                descriptor.IsDevelopment = isDev;
                found.Add(new() { Descriptor = descriptor, NeedsVerification = needsVerification });
            }
        }
        lock (_gate)
        {
            _plugins.AddRange(found);
        }
        // 协议页签在**发现期**就登记:连接配置页因此在不装载任何插件程序集的前提下
        // 也能画出 S3 之类的页签,用户点到它才触发惰性激活(启动零开销那条不能破)。
        foreach (PluginRuntime runtime in found)
        {
            DeclareProtocols(runtime);
        }
    }

    /// <summary>把某插件清单里声明的协议/工作台页签登记进注册表(仅对可用状态的插件)。</summary>
    private void DeclareProtocols(PluginRuntime runtime)
    {
        if (options.ProtocolRegistry is not { } registry || runtime.Descriptor.State != PluginState.Discovered)
        {
            return;
        }
        if (runtime.Descriptor.Manifest?.Contributes?.Protocols is { Length: > 0 } protocols)
        {
            registry.Declare(runtime.Descriptor.Id, protocols);
        }
        if (runtime.Descriptor.Manifest?.Contributes?.Workspaces is { Length: > 0 } workspaces)
        {
            registry.DeclareWorkspaces(runtime.Descriptor.Id, workspaces);
        }
    }

    /// <summary>
    /// 注册表的惰性激活回调:按协议 id 找到声明它的插件并激活。
    /// 幂等且串行(走 <see cref="EnsureActivatedAsync" /> 的激活闸),同一协议被并发请求也只装载一次。
    /// </summary>
    private async Task<bool> ActivateForProtocolAsync(string protocolId)
    {
        PluginRuntime? runtime;
        lock (_gate)
        {
            // 协议与工作台共用这一条惰性激活链路:两者在连接配置页上是同一排页签,
            // 用户点中哪个都只是"按 id 找到声明它的插件并激活"。
            runtime = _plugins.FirstOrDefault(p =>
                p.Descriptor.Manifest?.Contributes is { } contributes
                && (contributes.Protocols.Any(c => c.Id.Equals(protocolId, StringComparison.OrdinalIgnoreCase))
                    || contributes.Workspaces.Any(c => c.Id.Equals(protocolId, StringComparison.OrdinalIgnoreCase))));
        }
        return runtime is not null && await EnsureActivatedAsync(runtime).ConfigureAwait(false);
    }

    /// <summary>
    /// 读一份清单并给出描述。**不做安装凭据校验** —— 那要读遍插件目录的每个字节,
    /// 挂在发现路径上就是把启动堵在磁盘上。校验推迟到后台巡检与激活闸
    /// (见 <see cref="EnsureVerifiedAsync" />),安全边界不变:仍是"不校验不装载"。
    /// </summary>
    /// <param name="dir">插件目录。</param>
    /// <param name="manifestPath">清单文件路径。</param>
    /// <param name="seenIds">已见过的插件 id(用于查重)。</param>
    /// <param name="isDevelopment">是否来自开发期插件根。</param>
    /// <param name="needsVerification">输出:该插件是否仍待安装凭据校验。</param>
    /// <returns>插件描述。</returns>
    private PluginDescriptor Describe(string dir, string manifestPath, HashSet<string> seenIds, bool isDevelopment,
        out bool needsVerification)
    {
        needsVerification = false;
        PluginManifest manifest;
        try
        {
            manifest = PluginManifestReader.Load(manifestPath);
        }
        catch (PluginManifestException ex)
        {
            Log($"Rejected plugin at '{dir}': {ex.Message}");
            return new() { Directory = dir, State = PluginState.Invalid, Error = ex.Message };
        }
        var descriptor = new PluginDescriptor { Manifest = manifest, Directory = dir };
        needsVerification = !isDevelopment && options.TrustRepository is not null && IsUserPluginDirectory(dir);
        if (!seenIds.Add(manifest.Id))
        {
            descriptor.State = PluginState.Invalid;
            descriptor.Error = $"Duplicate plugin id '{manifest.Id}' (already provided by an earlier plugin root).";
        }
        else if (isDevelopment
                     ? DevDisabled.Contains(manifest.Id)
                     : File.Exists(Path.Combine(dir, ".disabled")))
        {
            descriptor.State = PluginState.Disabled;
        }
        else if (manifest.ApiLevel > VelaPluginApi.Level)
        {
            descriptor.State = PluginState.Incompatible;
            descriptor.Error = $"Plugin targets apiLevel {manifest.ApiLevel}, host supports up to {VelaPluginApi.Level}. Update VelaShell.";
        }
        else if (manifest.MinHostVersion is { } minHost && IsHostOlderThan(minHost))
        {
            descriptor.State = PluginState.Incompatible;
            descriptor.Error = $"Plugin requires host >= {minHost}, current host is {options.HostVersion}.";
        }
        else if (manifest.MinSdkVersion is { } minSdk && IsOlder(VelaPluginApi.SdkVersion, minSdk))
        {
            // apiLevel 拦不住这一档:它只在**破坏性**变更时才动,而新增的接口方法 / DTO 字段
            // 不算破坏性。不在这里拦,插件就会装上、激活、然后在第一次调用新方法时
            // 抛一个 MissingMethodException —— 正是 apiLevel 当初要消灭的那种异常。
            descriptor.State = PluginState.Incompatible;
            descriptor.Error =
                $"Plugin requires plugin SDK >= {minSdk}, this host ships {VelaPluginApi.SdkVersion}. Update VelaShell.";
        }
        return descriptor;
    }

    private bool IsUserPluginDirectory(string directory) =>
        options.UserPluginRoot is { } root && IsUnder(directory, root);

    /// <summary>
    /// 比对插件目录内容与安装收据;没有收据的旁装目录在这里被**收养**成基线。
    /// 通过返回 <see langword="null" />,否则返回面向用户的拒绝原因。
    /// <para>
    /// 收据分两种,给出的保证也不同:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// 管理页装的(<c>LegacyAdopted == false</c>):收据是宿主亲手在解包之后立刻落下的,
    /// 它确实等于"这个目录出自那个包"。此后内容变了就是被别的程序动过,一律拒装载。
    /// </item>
    /// <item>
    /// 旁装的(命令行 <c>vela-plugin install</c>,或者直接把目录放进插件根):
    /// 宿主第一次看见它时目录已经在那儿了,没有任何东西能证明它出自哪个包 ——
    /// 此时建立的基线只是"我第一次见到它时长这样",TOFU 而已。
    /// 这类收据不作为拒装载的依据(命令行手册写明旁装换来的代价就是没有事后防篡改),
    /// 内容变了就重新记一遍并留一条日志。
    /// </item>
    /// </list>
    /// <para>
    /// 之所以要收养而不是直接拒绝:装载前的把关(签名、清单、apiLevel、容器摘要)命令行一条不少,
    /// 拒绝旁装目录并不能挡住"能往插件目录写文件的进程"—— 那种进程本来就以用户身份在跑 ——
    /// 却会把文档里写明支持的两条安装路径全部堵死。真正值钱的是第一种收据,它没有被削弱。
    /// </para>
    /// </summary>
    private async Task<string?> VerifyOrAdoptInstallReceiptAsync(string pluginId, string directory)
    {
        if (_trustLoadError is not null)
        {
            return $"Plugin trust store is unavailable; refusing to load user plugin ({_trustLoadError}).";
        }
        if (options.TrustRepository is null || _trustState is null)
        {
            return "Plugin trust store is unavailable; refusing to load user plugin.";
        }
        string actual;
        try
        {
            actual = ComputePluginContentSha256(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"Installed plugin files could not be verified: {ex.Message}";
        }

        // 读与写都在同一把锁里:写收据的那几条路径(装、卸、这里)会就地改同一个字典。
        await _trustStateGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            InstalledPluginReceipt? receipt = _trustState.Receipts.GetValueOrDefault(pluginId);
            if (receipt is not null && ContentMatches(actual, receipt.ContentSha256))
            {
                return null;
            }
            if (receipt is { LegacyAdopted: false })
            {
                return "Installed plugin files changed after installation. Reinstall the original package "
                       + "from the plugin manager.";
            }
            _trustState.Receipts[pluginId] = new(pluginId, actual, null, null,
                LegacyAdopted: true, DateTimeOffset.UtcNow);
            try
            {
                await options.TrustRepository.SaveAsync(_trustState, _shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (receipt is null)
                {
                    _trustState.Receipts.Remove(pluginId);
                }
                else
                {
                    _trustState.Receipts[pluginId] = receipt;
                }
                // 记不下基线就不放行:否则每次启动都要重新收养,等于这份保护根本不存在。
                return $"Installation baseline for this plugin could not be recorded: {ex.Message}";
            }
            Log(receipt is null
                ? $"Adopted side-loaded plugin '{pluginId}' as its own installation baseline "
                  + "(installed outside the plugin manager; no post-install tamper detection)."
                : $"Side-loaded plugin '{pluginId}' changed on disk; installation baseline re-recorded.");
            return null;
        }
        finally
        {
            _trustStateGate.Release();
        }
    }

    private static bool ContentMatches(string actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));

    /// <summary>宿主版本是否低于要求(数字段比较,忽略预发布后缀;不可解析时不拦)。</summary>
    private bool IsHostOlderThan(string minHostVersion) => IsOlder(options.HostVersion, minHostVersion);

    /// <summary>
    /// <paramref name="actual" /> 是否比 <paramref name="required" /> 老。
    /// 预发布后缀被忽略(<c>1.1.0-beta</c> 视作 <c>1.1.0</c>):把 beta 判成"不够新"
    /// 会让预览版宿主装不上为它写的插件,而那正是预览版存在的意义。
    /// 任一侧解析不出版本号就放行 —— 拦下一个只是版本号写得怪的插件,损失大于收益。
    /// </summary>
    /// <param name="actual">实际版本。</param>
    /// <param name="required">要求的最低版本。</param>
    /// <returns>实际版本更老时为 true。</returns>
    private static bool IsOlder(string actual, string required)
    {
        static Version? ParseNumeric(string v)
        {
            string numeric = v.Split('-', 2)[0];
            if (!numeric.Contains('.'))
            {
                numeric += ".0";
            }
            return Version.TryParse(numeric, out Version? parsed) ? parsed : null;
        }
        return ParseNumeric(actual) is { } left && ParseNumeric(required) is { } right && left < right;
    }

    private async Task ActivateAsync(PluginRuntime runtime, CancellationToken cancellationToken)
    {
        PluginDescriptor descriptor = runtime.Descriptor;
        PluginManifest manifest = descriptor.Manifest!;
        var stopwatch = Stopwatch.StartNew();
        // 用户按下命令到插件真正就绪之间可以有好几秒(装载 + JIT,隔离模式还要拉进程),
        // 这段时间界面上必须有东西在转 —— 否则那次点击看起来就是没反应。
        using IBackgroundActivityScope? activity = options.Activity?.Begin(
            Strings.Get("Msg_PluginLoading"), DisplayNameOf(descriptor));
        try
        {
            // 开发期插件先落一份影子副本再装载:否则运行中的插件锁住工程 bin 里的 dll,
            // Windows 上就没法边跑边重编(见 PrepareLoadDirectory)。
            string sourceEntry = Path.GetFullPath(Path.Combine(descriptor.Directory, manifest.Entry));
            if (!File.Exists(sourceEntry))
            {
                throw new FileNotFoundException($"Entry assembly '{manifest.Entry}' not found in plugin directory.");
            }
            descriptor.EntryTimestampUtc = TryGetWriteTimeUtc(sourceEntry);
            string loadDirectory = PrepareLoadDirectory(descriptor);
            descriptor.LoadDirectory = loadDirectory;
            string entryPath = Path.GetFullPath(Path.Combine(loadDirectory, manifest.Entry));
            if (!File.Exists(entryPath))
            {
                throw new FileNotFoundException($"Entry assembly '{manifest.Entry}' not found in plugin directory.");
            }
            PluginContext context = CreateContext(manifest, runtime);
            runtime.Context = context;

            if (manifest.HostMode == PluginHostMode.Isolated)
            {
                await ActivateIsolatedAsync(runtime, manifest, entryPath, context, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ActivateInProcessAsync(runtime, manifest, entryPath, context, cancellationToken).ConfigureAwait(false);
            }
            descriptor.State = PluginState.Active;
            Log($"Activated '{manifest.Id}' v{manifest.Version} ({manifest.HostMode}) in {stopwatch.ElapsedMilliseconds}ms.");
        }
        catch (Exception ex)
        {
            descriptor.State = PluginState.Failed;
            descriptor.Error = ex is OperationCanceledException
                ? $"Activation timed out after {options.ActivationTimeout.TotalSeconds:0}s."
                : ex.Message;
            Log($"Failed to activate '{manifest.Id}': {descriptor.Error}");
            await CleanupRuntimeAsync(runtime).ConfigureAwait(false);
        }
        RaiseChanged();
    }

    private async Task ActivateInProcessAsync(PluginRuntime runtime, PluginManifest manifest, string entryPath,
        PluginContext context, CancellationToken cancellationToken)
    {
        var loadContext = new PluginAssemblyLoadContext(manifest.Id, entryPath);
        runtime.LoadContext = loadContext;
        Assembly assembly = loadContext.LoadFromAssemblyPath(entryPath);
        Type entryType = PluginEntryLocator.FindEntryType(assembly);
        var instance = (IVelaPlugin)Activator.CreateInstance(entryType)!;
        runtime.Instance = instance;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        // 挂着调试器时放宽激活超时:断点停住的是整个进程,而 CancelAfter 的计时按墙钟走 ——
        // 不放宽的话,在 ActivateAsync 里下个断点、看两眼变量,恢复执行时激活已经被判超时。
        cts.CancelAfter(Debugger.IsAttached ? DebugActivationTimeout : options.ActivationTimeout);
        // WaitAsync:即使插件无视取消令牌,宿主也不陪它挂着。
        await instance.ActivateAsync(context, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>调试期的激活超时:够人看完一屏变量,又不至于真挂死时永远等下去。</summary>
    private static readonly TimeSpan DebugActivationTimeout = TimeSpan.FromMinutes(10);

    /// <summary>该插件是否处于调试目标集合(见 <see cref="PluginManagerOptions.DebugPluginIds" />)。</summary>
    private bool IsDebugTarget(string pluginId) =>
        options.DebugPluginIds.Count > 0
        && (options.DebugPluginIds.Contains("*")
            || options.DebugPluginIds.Contains(pluginId, StringComparer.OrdinalIgnoreCase));

    /// <summary>隔离模式:拉起 PluginHost 进程并经 RPC 激活(设计稿 02/04/05)。</summary>
    private async Task ActivateIsolatedAsync(PluginRuntime runtime, PluginManifest manifest, string entryPath,
        PluginContext context, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        bool debug = IsDebugTarget(manifest.Id);
        if (debug)
        {
            Log($"Plugin '{manifest.Id}' starts in debug mode: the host process waits for a debugger, " +
                "activation timeout is relaxed and the heartbeat is off.");
        }
        PluginProcessClient client = await PluginProcessClient.StartAsync(manifest, entryPath, context,
            options.HostVersion, context.DataDirectory,
            debug ? DebugActivationTimeout : options.ActivationTimeout, cts.Token,
            options.ThemeTokensProvider, options.EmbedHost, debug).ConfigureAwait(false);
        runtime.Process = client;
        runtime.LastActivityTicks = Environment.TickCount64;
        runtime.OpenSurfaces = 0;
        if (debug)
        {
            // pid 既进日志也落文件:IDE 的"附加到进程"要的是这个数字,
            // 而在日志里翻它是本可以省掉的一步(同时挂了两个隔离插件时按进程名还会挑错)。
            WriteDebugPidFile(manifest.Id, client.ProcessId);
            Log($"Plugin host for '{manifest.Id}' is waiting for a debugger: pid {client.ProcessId}.");
            try
            {
                DebugAttachRequested?.Invoke(manifest.Id, client.ProcessId);
            }
            catch
            {
                // 通知订阅方异常不回灌运行时。
            }
        }
        client.Crashed += () => OnIsolatedCrashed(runtime);
        client.Activity += () => runtime.LastActivityTicks = Environment.TickCount64;
        client.SurfacesChanged += count => runtime.OpenSurfaces = count;
        // 调试目标不发心跳:断点会冻住插件进程的全部线程,ping 必然连续失败,
        // 而心跳失败的处置是强杀 —— 那就等于"下断点即插件被杀"。
        client.StartHeartbeat(debug ? TimeSpan.Zero : options.HeartbeatInterval);
    }

    /// <summary>
    /// 隔离插件崩溃处置(蓝图 04):窗口内按退避序列自动重启,超限判 Failed 不再自愈
    /// (管理页标红,等待用户处置)。崩溃只影响该插件自己。
    /// </summary>
    private void OnIsolatedCrashed(PluginRuntime runtime)
    {
        TimeSpan? restartDelay = null;
        lock (_gate)
        {
            if (_disposed || runtime.Descriptor.State != PluginState.Active)
            {
                return;
            }
            long now = Environment.TickCount64;
            runtime.CrashTimes.RemoveAll(t => now - t > options.CrashRestartWindow.TotalMilliseconds);
            runtime.CrashTimes.Add(now);
            int attempt = runtime.CrashTimes.Count;
            if (attempt > options.CrashRestartBackoff.Count)
            {
                runtime.Descriptor.State = PluginState.Failed;
                runtime.Descriptor.Error =
                    $"Plugin process crashed {attempt} times within {options.CrashRestartWindow.TotalMinutes:0} minutes; giving up.";
            }
            else
            {
                restartDelay = options.CrashRestartBackoff[attempt - 1];
                runtime.Descriptor.State = PluginState.Crashed;
                runtime.Descriptor.Error =
                    $"Plugin process exited unexpectedly; restarting in {restartDelay.Value.TotalSeconds:0.#}s (attempt {attempt}/{options.CrashRestartBackoff.Count}).";
            }
        }
        Log($"Plugin '{runtime.Descriptor.Id}' crashed: {runtime.Descriptor.Error}");
        CleanupRuntime(runtime);
        RaiseChanged();
        if (restartDelay is { } delay)
        {
            _ = RestartAfterAsync(runtime, delay);
        }
    }

    private async Task RestartAfterAsync(PluginRuntime runtime, TimeSpan delay)
    {
        if (await DelayObservedAsync(delay).ConfigureAwait(false) || _disposed)
        {
            return;
        }
        Log($"Restarting crashed plugin '{runtime.Descriptor.Id}'.");
        await ActivateAsync(runtime, CancellationToken.None).ConfigureAwait(false);
    }

    private static TracePluginLogger GetOrCreateLogger(PluginRuntime runtime)
        => runtime.Logger ??= new(runtime.Descriptor.Id);

    /// <summary>每插件命令能力(懒建、占位与真实注册共用;停用后置空重建)。</summary>
    private ICommandsApi GetOrCreateCommandsApi(PluginRuntime runtime)
    {
        TracePluginLogger log = GetOrCreateLogger(runtime);
        return runtime.CommandsApi ??= options.CommandsFactory?.Invoke(runtime.Descriptor.Id, log)
                                       ?? new NullCommandsApi(log);
    }

    private PluginContext CreateContext(PluginManifest manifest, PluginRuntime runtime)
    {
        string dataDirectory = Path.Combine(options.DataRootDirectory, manifest.Id);
        Directory.CreateDirectory(dataDirectory);
        TracePluginLogger log = GetOrCreateLogger(runtime);
        return new()
        {
            PluginId = manifest.Id,
            PluginVersion = manifest.Version,
            DataDirectory = dataDirectory,
            Host = new HostInfoCapability(options.HostVersion, options.Theme, options.Localization),
            Log = log,
            // 数据后端优先 SonnetDB(按插件 id 命名空间隔离,卸载可整体清除);
            // 无 DB 的宿主(headless 测试)退回数据目录文件。
            Storage = options.DataStore?.CreateStorage(manifest.Id)
                      ?? new JsonFilePluginStorage(dataDirectory),
            // 时序只有 SonnetDB 后端能提供(文件退化实现没有意义):无 DB 的宿主一律报不可用。
            TimeSeries = options.DataStore?.CreateTimeSeries(manifest.Id) ?? new UnavailableTimeSeries(),
            // 会话能力按插件分实例:开出来的会话记在实例里,CloseAsync 据此判"这条是不是你开的"。
            Sessions = options.Connections is { } connections
                ? new SessionsCapability(manifest.Id, connections, options.SessionProfiles, options.SessionOpener)
                : new EmptySessionsApi(),
            RemoteFs = options is { Sftp: { } sftp, Connections: { } conn }
                ? new RemoteFsCapability(sftp, conn)
                : new UnavailableRemoteFs(),
            RemoteExec = options.Connections is { } execConnections
                ? new RemoteExecCapability(execConnections)
                : new UnavailableRemoteExec(),
            RemoteTunnel = options.Connections is { } tunnelConnections
                ? new RemoteTunnelCapability(tunnelConnections)
                : new UnavailableRemoteTunnel(),
            Commands = GetOrCreateCommandsApi(runtime),
            Events = new PluginEventHub(log, options.Connections, options.Theme, options.Localization),
            // 主题能力挂在共享采集点上(每次换肤只采一份快照,全部插件共用)。
            // 没有主题服务的宿主(headless 测试)退化成一套固定的默认主题。
            Theme = options.Theme is null
                ? new StaticHostTheme()
                : new HostThemeCapability(log, _themeSource.Value),
            Ui = options.UiFactory?.Invoke(manifest.Id, log) ?? new NullUiApi(log),
            Secrets = options.DataStore is { } dataStore
                ? dataStore.CreateSecrets(manifest.Id)
                : options.SecretProtector is { } protector
                    ? new ProtectedSecretsCapability(dataDirectory, protector)
                    : new UnavailableSecrets(),
            Clipboard = options.Clipboard ?? new UnavailableClipboard(),
            Terminal = options.TerminalFactory?.Invoke(manifest.Id, log) ?? new UnavailableTerminal(),
            TerminalView = options.TerminalView ?? new UnavailableTerminalView(),
            Protocols = options.ProtocolRegistry is { } protocols
                ? new ProtocolsCapability(manifest.Id, protocols, log)
                : new UnavailableProtocols(),
            Workspaces = options.ProtocolRegistry is { } workspaces
                ? new WorkspacesCapability(manifest.Id, workspaces, log)
                : new UnavailableWorkspaces(),
            Shutdown = _shutdown.Token
        };
    }

    /// <summary>停用全部活跃插件并卸载其 ALC。退出路径上有严格时限,不被慢插件拖住。</summary>
    public async ValueTask DisposeAsync()
    {
        List<PluginRuntime> active;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            active = [.. _plugins.Where(p => p.Descriptor.State == PluginState.Active)];
        }
        StopDevWatchers();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        // 并发停用:退出耗时 = 最慢一个插件(带上限),而非全体之和。
        await Task.WhenAll(active.Select(DeactivateAsync)).ConfigureAwait(false);
        // 从未激活的惰性插件也要注销其占位命令。
        List<PluginRuntime> leftovers;
        lock (_gate)
        {
            leftovers = [.. _plugins.Where(p => p.CommandsApi is not null)];
        }
        foreach (PluginRuntime runtime in leftovers)
        {
            (runtime.CommandsApi as IDisposable)?.Dispose();
            runtime.CommandsApi = null;
        }
        if (_themeSource.IsValueCreated)
        {
            _themeSource.Value.Dispose();
        }
        _shutdown.Dispose();
    }

    private async Task DeactivateAsync(PluginRuntime runtime)
    {
        try
        {
            if (runtime.Process is { } process)
            {
                await process.DeactivateAsync(options.DeactivationTimeout).ConfigureAwait(false);
            }
            else if (runtime.Instance is { } instance)
            {
                using var cts = new CancellationTokenSource(options.DeactivationTimeout);
                await instance.DeactivateAsync(cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            }
            runtime.Descriptor.State = PluginState.Deactivated;
        }
        catch (Exception ex)
        {
            runtime.Descriptor.State = PluginState.Deactivated;
            Log($"Plugin '{runtime.Descriptor.Id}' deactivation did not finish cleanly: {ex.Message}");
        }
        finally
        {
            WriteDebugPidFile(runtime.Descriptor.Id, null); // 进程没了,pid 文件不该留下误导
            await CleanupRuntimeAsync(runtime).ConfigureAwait(false);
        }
    }

    /// <summary>回收隔离插件进程(IAsyncDisposable):ValueTask 只消费一次,异常不外溢。</summary>
    private static async Task DisposeProcessAsync(PluginProcessClient process)
    {
        try
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 清理失败不阻断。
        }
    }

    /// <summary>
    /// 异步路径上的清理:进程回收与同步清理并发进行,但方法返回时进程确实已被杀掉、
    /// 管道确实已断开——退出流程据此保证不留孤儿子进程。
    /// </summary>
    private static async Task CleanupRuntimeAsync(PluginRuntime runtime)
    {
        Task disposeProcess = Task.CompletedTask;
        if (runtime.Process is { } process)
        {
            runtime.Process = null;
            disposeProcess = DisposeProcessAsync(process); // 与下面的同步清理并行
        }
        CleanupRuntime(runtime);
        await disposeProcess.ConfigureAwait(false);
    }

    /// <summary>
    /// 拆除命令/事件等宿主侧引用并卸载 ALC / 回收进程(不等待 GC 真正回收)。
    /// 同步入口只供 Process.Exited 这类无法 await 的回调使用,进程回收退化为后台尽力而为;
    /// 能 await 的地方一律走 <see cref="CleanupRuntimeAsync" />。
    /// </summary>
    private static void CleanupRuntime(PluginRuntime runtime)
    {
        if (runtime.Process is { } process)
        {
            runtime.Process = null;
            _ = DisposeProcessAsync(process); // 杀进程 + 断管道,后台尽力而为
        }
        try
        {
            runtime.Context?.Dispose();
        }
        catch
        {
            // 清理失败不阻断。
        }
        runtime.Context = null;
        runtime.Instance = null;
        // 命令能力随上下文释放(占位/真实注册全部注销);置空使重启/回挂重建新实例。
        runtime.CommandsApi = null;
        try
        {
            runtime.LoadContext?.Unload();
        }
        catch
        {
            // 已卸载或不可卸载:忽略。
        }
        runtime.LoadContext = null;
    }

    // ---- 开发内环:影子拷贝 / 重新加载 / 自动重载 --------------------------------

    /// <summary>
    /// 决定从哪个目录装载。已安装插件就地装载;开发期插件在配置了
    /// <see cref="PluginManagerOptions.DevShadowRootDirectory" /> 时先整份复制到
    /// <c>&lt;影子根&gt;/&lt;id&gt;/gen-N</c> 再从副本装载。
    /// <para>
    /// 用"新一代目录"而不是"覆盖同一个目录":ALC 的卸载是异步的(要等 GC 真正回收才放句柄),
    /// 刚停用的那一代很可能还锁着,覆盖必然时灵时不灵。换个代号目录则永远成功,
    /// 旧代能删则删、删不掉留着下次再清。
    /// </para>
    /// <para>拷贝失败绝不阻断激活:退回就地装载(等于影子拷贝引入前的行为)并记一行日志。</para>
    /// </summary>
    private string PrepareLoadDirectory(PluginDescriptor descriptor)
    {
        if (!descriptor.IsDevelopment || options.DevShadowRootDirectory is not { } shadowRoot)
        {
            return descriptor.Directory;
        }
        try
        {
            string pluginShadow = Path.Combine(shadowRoot, SanitizeDirectoryName(descriptor.Id));
            Directory.CreateDirectory(pluginShadow);
            int generation = 1;
            foreach (string existing in Directory.EnumerateDirectories(pluginShadow))
            {
                string name = Path.GetFileName(existing);
                if (name.StartsWith("gen-", StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan(4), out int n) && n >= generation)
                {
                    generation = n + 1;
                }
            }
            string target = Path.Combine(pluginShadow, $"gen-{generation}");
            CopyDirectory(descriptor.Directory, target);
            PurgeShadowGenerations(pluginShadow, keep: target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log($"Could not shadow-copy development plugin '{descriptor.Id}' ({ex.Message}); loading in place.");
            return descriptor.Directory;
        }
    }

    /// <summary>把插件目录整份复制到目标(递归)。<c>.disabled</c> 标记不带过去。</summary>
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            if (Path.GetFileName(file) is ".disabled")
            {
                continue;
            }
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    /// <summary>清理某插件影子目录下除 <paramref name="keep" /> 外的旧代(删不掉的留到下次)。</summary>
    private static void PurgeShadowGenerations(string pluginShadowDirectory, string? keep)
    {
        foreach (string directory in Directory.EnumerateDirectories(pluginShadowDirectory))
        {
            if (keep is not null && string.Equals(directory, keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 上一代还被未回收的 ALC 锁着:留着,下次装载时再清。
            }
        }
    }

    /// <summary>清掉已不再挂载的开发期插件留下的影子目录(每次发现后调用)。</summary>
    private void PurgeOrphanShadows()
    {
        if (options.DevShadowRootDirectory is not { } shadowRoot || !Directory.Exists(shadowRoot))
        {
            return;
        }
        HashSet<string> live;
        lock (_gate)
        {
            live = [.. _plugins.Where(p => p.Descriptor.IsDevelopment)
                               .Select(p => SanitizeDirectoryName(p.Descriptor.Id))];
        }
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(shadowRoot))
            {
                if (!live.Contains(Path.GetFileName(directory)))
                {
                    PurgeShadowGenerations(directory, keep: null);
                    try
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 留着,下次再清。
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Could not sweep the development shadow directory: {ex.Message}");
        }
    }

    /// <summary>插件 id 里可能出现的路径非法字符一律换成 <c>_</c>(id 规则本已很窄,这是兜底)。</summary>
    private static string SanitizeDirectoryName(string id)
    {
        Span<char> buffer = stackalloc char[id.Length];
        for (int i = 0; i < id.Length; i++)
        {
            buffer[i] = Array.IndexOf(Path.GetInvalidFileNameChars(), id[i]) >= 0 ? '_' : id[i];
        }
        return new(buffer);
    }

    private static DateTime? TryGetWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 重新加载一个插件:停用 → 卸载 ALC / 回收进程 → **重读清单** → 按激活策略重新装载。
    /// 开发内环的主入口:改完代码 <c>dotnet build</c>,点一下就跑上新代码,不必重启宿主。
    /// <para>
    /// 之所以整条描述重来而不是只换程序集:两次构建之间清单也可能变了(版本、命令、协议页签),
    /// 只换程序集会让管理页与连接页显示的还是上一版的声明。
    /// </para>
    /// <para>
    /// 禁用中的插件不在此列(先启用再说);清单已被删掉的插件重载后标记 Invalid 而非消失,
    /// 否则"我删错了文件"会表现成"插件凭空不见了"。
    /// </para>
    /// </summary>
    /// <param name="pluginId">插件 id。</param>
    /// <returns>重载后是否处于 <see cref="PluginState.Active" /> 或已挂上惰性触发器。</returns>
    public async Task<bool> ReloadAsync(string pluginId)
    {
        PluginRuntime? old;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }
            old = _plugins.FirstOrDefault(p => p.Descriptor.Id == pluginId);
        }
        if (old is null || old.Descriptor.State == PluginState.Disabled)
        {
            return false;
        }
        string directory = old.Descriptor.Directory;
        bool isDevelopment = old.Descriptor.IsDevelopment;

        await old.ActivationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (old.Descriptor.State == PluginState.Active)
            {
                await DeactivateAsync(old).ConfigureAwait(false);
            }
            (old.CommandsApi as IDisposable)?.Dispose();
            old.CommandsApi = null;
            options.ProtocolRegistry?.RemovePlugin(pluginId);
            await CleanupRuntimeAsync(old).ConfigureAwait(false);
        }
        finally
        {
            old.ActivationGate.Release();
        }

        // 重新发现:id 去重要排除自己那条,否则重读出来的清单会被判成"与自己重复"。
        HashSet<string> seenIds;
        lock (_gate)
        {
            seenIds = [.. _plugins.Where(p => !ReferenceEquals(p, old)).Select(p => p.Descriptor.Id)];
        }
        string manifestPath = Path.Combine(directory, PluginManifestReader.FileName);
        bool needsVerification = false;
        PluginDescriptor descriptor = File.Exists(manifestPath)
            ? Describe(directory, manifestPath, seenIds, isDevelopment, out needsVerification)
            : new()
            {
                Directory = directory,
                State = PluginState.Invalid,
                Error = $"{PluginManifestReader.FileName} is no longer present in '{directory}'."
            };
        descriptor.IsDevelopment = isDevelopment;
        // 全新的运行时 = 全新的校验记忆:重载正是"目录内容可能变了"的那个场景,
        // 沿用旧的记忆化结果等于把改动过的插件放行。
        var fresh = new PluginRuntime { Descriptor = descriptor, NeedsVerification = needsVerification };
        lock (_gate)
        {
            int index = _plugins.IndexOf(old);
            if (index >= 0)
            {
                _plugins[index] = fresh;
            }
            else
            {
                _plugins.Add(fresh);
            }
        }
        DeclareProtocols(fresh);
        bool ok = false;
        if (descriptor.State == PluginState.Discovered)
        {
            if (descriptor.Manifest!.ActivatesOnStartup)
            {
                ok = await EnsureActivatedAsync(fresh).ConfigureAwait(false);
            }
            else
            {
                RegisterActivationTriggers(fresh);
                ok = true;
            }
        }
        Log($"Reloaded plugin '{pluginId}': {descriptor.State}{(descriptor.Error is { } e ? $" ({e})" : "")}.");
        RaiseChanged();
        return ok;
    }

    /// <summary>
    /// 开发期自动重载(<c>--dev-watch</c>):监视开发根,构建产物落定后重载受影响的插件。
    /// 只在显式开启时才挂 —— 文件监视器在网络盘/共享盘上会抖,不该是所有人默认承担的成本。
    /// </summary>
    private void StartDevWatchers()
    {
        if (!options.DevAutoReload)
        {
            return;
        }
        // 去抖定时器在挂监视器之前建好:留到事件回调里懒建的话,两个几乎同时到达的
        // 变更事件会各建一个,其中一个从此没人 Dispose。
        _devWatchDebounce = new(_ => _ = ReloadChangedDevPluginsAsync(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        foreach (string root in options.DevPluginRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnDevRootChanged;
                watcher.Created += OnDevRootChanged;
                watcher.Renamed += OnDevRootChanged;
                watcher.Error += (_, e) => Log($"Development watcher on '{root}' failed: {e.GetException().Message}");
                _devWatchers.Add(watcher);
                Log($"Watching development plugin root '{root}' for rebuilds.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log($"Could not watch development plugin root '{root}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 变更去抖:一次 <c>dotnet build</c> 会连着写十几个文件,每个都触发一次重载纯属自找麻烦。
    /// 最后一次变更之后静默 1.5 秒才动手 —— 也给链接器写完 pdb 留出余地。
    /// </summary>
    private void OnDevRootChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed || _shutdown.IsCancellationRequested)
        {
            return;
        }
        string name = Path.GetFileName(e.FullPath);
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && !name.Equals(PluginManifestReader.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            _devWatchDebounce?.Change(TimeSpan.FromMilliseconds(1500), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 正在停机。
        }
    }

    /// <summary>重载那些入口程序集写入时间与装载时不同的开发期插件。</summary>
    private async Task ReloadChangedDevPluginsAsync()
    {
        List<PluginDescriptor> candidates;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            candidates = [.. _plugins.Select(p => p.Descriptor)
                                     .Where(d => d.IsDevelopment && d.Manifest is not null
                                                 && d.State is not PluginState.Disabled)];
        }
        foreach (PluginDescriptor descriptor in candidates)
        {
            string entry = Path.Combine(descriptor.Directory, descriptor.Manifest!.Entry);
            DateTime? stamp = TryGetWriteTimeUtc(entry);
            // 装载失败过的插件没有时间戳:那种情况下只要文件还在就重试一次,
            // 否则"编译错误 → 修好 → 自动重载"这条链在第二步就断了。
            if (stamp is null || (descriptor.EntryTimestampUtc is { } previous && stamp == previous))
            {
                continue;
            }
            Log($"Development plugin '{descriptor.Id}' was rebuilt; reloading.");
            try
            {
                await ReloadAsync(descriptor.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 自动重载是后台的尽力而为:构建到一半被抓到、文件正被写等等都可能失败,
                // 下一次变更还会再来一遍。这里不吞的话就是一条没人观察的后台异常。
                Log($"Auto-reload of '{descriptor.Id}' failed: {ex.Message}");
            }
        }
    }

    private void StopDevWatchers()
    {
        foreach (FileSystemWatcher watcher in _devWatchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 停机路径:尽力而为。
            }
        }
        _devWatchers.Clear();
        _devWatchDebounce?.Dispose();
        _devWatchDebounce = null;
    }

    /// <summary>
    /// 等待调试器的隔离插件把 pid 落一个文件,免得开发者去日志里翻。
    /// <paramref name="processId" /> 为 null 表示插件已停,删掉文件。
    /// </summary>
    private void WriteDebugPidFile(string pluginId, int? processId)
    {
        if (options.DiagnosticsDirectory is not { } directory)
        {
            return;
        }
        string path = Path.Combine(directory, $"plugin-host-{SanitizeDirectoryName(pluginId)}.pid");
        try
        {
            if (processId is { } pid)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Could not write the debug pid file for '{pluginId}': {ex.Message}");
        }
    }

    private static void Log(string message) => Trace.WriteLine($"[PluginManager] {message}");

    // ---- 能力缺席时的退化实现(headless / 单测宿主) ----

    private sealed class EmptySessionsApi : ISessionsApi
    {
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<SessionInfo?>(null);

        public Task<IReadOnlyList<SavedSessionInfo>> ListSavedAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SavedSessionInfo>>([]);

        // 没有连接服务的宿主既问不了用户、也连不了机器。报"拒绝"而不是"能力不可用":
        // 契约里拒绝是插件必须处理的一种正常结局,而 InvalidOperationException 不是。
        public Task<SessionInfo> OpenAsync(string savedSessionId, SessionOpenOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromException<SessionInfo>(
                new PluginPermissionDeniedException("This host cannot open sessions."));

        public Task CloseAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromException(new PluginPermissionDeniedException(
                $"Session '{sessionId}' was not opened by this plugin."));
    }

    private sealed class UnavailableRemoteFs : IRemoteFsApi
    {
        private static InvalidOperationException Unavailable() => new("Remote file capability is unavailable in this host.");

        public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string sessionId, string path, CancellationToken cancellationToken = default) => Task.FromException<IReadOnlyList<RemoteFileEntry>>(Unavailable());
        public Task<RemoteFileEntry?> StatAsync(string sessionId, string path, CancellationToken cancellationToken = default) => Task.FromException<RemoteFileEntry?>(Unavailable());
        public Task<bool> ExistsAsync(string sessionId, string path, CancellationToken cancellationToken = default) => Task.FromException<bool>(Unavailable());
        public Task<string> GetWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken = default) => Task.FromException<string>(Unavailable());
        public Task DownloadFileAsync(string sessionId, string remotePath, string localPath, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
        public Task UploadFileAsync(string sessionId, string localPath, string remotePath, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
        public Task<Stream> OpenReadAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default) => Task.FromException<Stream>(Unavailable());
        public Task<byte[]> ReadAllBytesAsync(string sessionId, string remotePath, int maxBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default) => Task.FromException<byte[]>(Unavailable());
        public Task WriteAllBytesAsync(string sessionId, string remotePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
        public Task DeleteAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
        public Task CreateDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
        public Task EnsureDirectoryAsync(string sessionId, string remotePath, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
        public Task RenameAsync(string sessionId, string oldPath, string newPath, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
    }

    private sealed class UnavailableRemoteExec : IRemoteExecApi
    {
        public Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromException<ExecResult>(new InvalidOperationException("Remote exec capability is unavailable in this host."));
    }

    private sealed class UnavailableRemoteTunnel : IRemoteTunnelApi
    {
        private static InvalidOperationException Unavailable() => new("Remote tunnel capability is unavailable in this host.");

        public int ActiveTunnels => 0;

        public Task<Stream> OpenUnixSocketAsync(string sessionId, string socketPath, TunnelOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromException<Stream>(Unavailable());

        public Task<Stream> OpenTcpAsync(string sessionId, string host, int port, TunnelOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromException<Stream>(Unavailable());
    }

    /// <summary>没有界面层的宿主(headless 测试)上的终端视图能力:明确报不可用。</summary>
    private sealed class UnavailableTerminalView : PluginSdk.TerminalView.ITerminalViewApi
    {
        public bool IsAvailable => false;

        public PluginSdk.TerminalView.IPluginTerminalView Create(
            PluginSdk.TerminalView.TerminalViewOptions? options = null) =>
            throw new InvalidOperationException("Terminal view capability is unavailable in this host.");
    }

    /// <summary>无数据库的宿主上的时序能力:明确报不可用,绝不静默丢数据。</summary>
    private sealed class UnavailableTimeSeries : PluginSdk.TimeSeries.ITimeSeriesApi
    {
        private static InvalidOperationException Unavailable() => new("Time series capability is unavailable in this host.");

        public Task<PluginSdk.TimeSeries.ITimeSeries> OpenAsync(PluginSdk.TimeSeries.TimeSeriesDefinition definition, CancellationToken cancellationToken = default)
            => Task.FromException<PluginSdk.TimeSeries.ITimeSeries>(Unavailable());

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<bool> DropAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
