using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.Infrastructure.Plugins.Isolated;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Hosting;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Manifest;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;
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
    }

    private readonly List<PluginRuntime> _plugins = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
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
            TryWriteDisabledMarker(runtime.Descriptor.Directory, disabled: true);
            if (runtime.Descriptor.State == PluginState.Active)
            {
                await DeactivateAsync(runtime).ConfigureAwait(false);
            }
            (runtime.CommandsApi as IDisposable)?.Dispose();
            runtime.CommandsApi = null;
            runtime.Descriptor.State = PluginState.Disabled;
            runtime.Descriptor.Error = null;
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
            TryWriteDisabledMarker(runtime.Descriptor.Directory, disabled: false);
            runtime.Descriptor.State = PluginState.Discovered;
            runtime.Descriptor.Error = null;
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
        if (options.UserPluginRoot is not { } userRoot)
        {
            return false;
        }
        lock (_gate)
        {
            return _plugins.FirstOrDefault(p => p.Descriptor.Id == pluginId) is { } runtime
                   && IsUnder(runtime.Descriptor.Directory, userRoot);
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
            TryDeleteDirectory(runtime.Descriptor.Directory);
            await PurgePluginDataAsync(pluginId).ConfigureAwait(false);
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
    /// 从 <c>.vpx</c> 包安装插件(zip 容器,含 plugin.json + 入口 dll)。解包到用户插件目录
    /// (zip-slip 防护),校验清单;同 id 已存在则先卸载旧版。安装后按激活策略激活。
    /// 返回安装的插件 id。
    /// </summary>
    /// <exception cref="InvalidOperationException">无用户插件目录、包非法或校验失败。</exception>
    public async Task<string> InstallFromVpxAsync(string vpxPath, CancellationToken cancellationToken = default)
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
        try
        {
            ExtractZipSafely(vpxPath, staging);
            string manifestPath = Path.Combine(staging, PluginManifestReader.FileName);
            if (!File.Exists(manifestPath))
            {
                throw new InvalidOperationException("Package has no plugin.json at its root.");
            }
            manifest = PluginManifestReader.Load(manifestPath); // 坏清单在此抛 PluginManifestException
            string entryPath = Path.Combine(staging, manifest.Entry);
            if (!File.Exists(entryPath))
            {
                throw new InvalidOperationException($"Entry assembly '{manifest.Entry}' is missing from the package.");
            }

            // 同 id 已装 → 先卸载旧版(用户目录的)或拒绝(应用自带的,避免覆盖只读自带件)。
            lock (_gate)
            {
                if (_plugins.FirstOrDefault(p => p.Descriptor.Id == manifest.Id) is { } existing
                    && !IsUnder(existing.Descriptor.Directory, userRoot))
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

            var runtime = new PluginRuntime { Descriptor = Describe(target, Path.Combine(target, PluginManifestReader.FileName), []) };
            lock (_gate)
            {
                _plugins.Add(runtime);
            }
            if (runtime.Descriptor.State == PluginState.Discovered)
            {
                if (manifest.ActivatesOnStartup)
                {
                    await ActivateAsync(runtime, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    RegisterActivationTriggers(runtime);
                }
            }
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
    private static void ExtractZipSafely(string zipPath, string destination)
    {
        string root = Path.GetFullPath(destination + Path.DirectorySeparatorChar);
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
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
            entry.ExtractToFile(targetPath, overwrite: true);
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
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase);
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

    private static void TryWriteDisabledMarker(string directory, bool disabled)
    {
        string marker = Path.Combine(directory, ".disabled");
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
        Discover();
        await PurgeUninstalledDataAsync().ConfigureAwait(false);
        List<PluginRuntime> discovered;
        lock (_gate)
        {
            discovered = [.. _plugins.Where(p => p.Descriptor.State == PluginState.Discovered)];
        }
        foreach (PluginRuntime runtime in discovered)
        {
            if (_shutdown.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            if (runtime.Descriptor.Manifest!.ActivatesOnStartup)
            {
                await ActivateAsync(runtime, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // 惰性激活(蓝图 D7):只注册清单声明的占位命令,不碰程序集/不拉进程。
                RegisterActivationTriggers(runtime);
            }
        }
        _ = IdleMonitorAsync();
    }

    /// <summary>把清单声明的命令注册为占位:首次触发即激活插件并转交真实处理器。</summary>
    private void RegisterActivationTriggers(PluginRuntime runtime)
    {
        PluginManifest manifest = runtime.Descriptor.Manifest!;
        CommandContribution[] contributions = manifest.Contributes?.Commands ?? [];
        if (contributions.Length == 0)
        {
            Log($"Plugin '{manifest.Id}' has no onStartup activation and no contributed commands; it will never activate.");
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
        Log($"Plugin '{manifest.Id}' waiting lazily behind {contributions.Length} command trigger(s).");
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

    /// <summary>串行化的按需激活;返回激活后是否 Active(Failed/Disabled 等不由命令救活)。</summary>
    private async Task<bool> EnsureActivatedAsync(PluginRuntime runtime)
    {
        await runtime.ActivationGate.WaitAsync().ConfigureAwait(false);
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
            await ActivateAsync(runtime, CancellationToken.None).ConfigureAwait(false);
            return runtime.Descriptor.State == PluginState.Active;
        }
        finally
        {
            runtime.ActivationGate.Release();
        }
    }

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
        foreach (string root in options.PluginRoots)
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
                found.Add(new() { Descriptor = Describe(dir, manifestPath, seenIds) });
            }
        }
        lock (_gate)
        {
            _plugins.AddRange(found);
        }
    }

    private PluginDescriptor Describe(string dir, string manifestPath, HashSet<string> seenIds)
    {
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
        if (!seenIds.Add(manifest.Id))
        {
            descriptor.State = PluginState.Invalid;
            descriptor.Error = $"Duplicate plugin id '{manifest.Id}' (already provided by an earlier plugin root).";
        }
        else if (File.Exists(Path.Combine(dir, ".disabled")))
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
        return descriptor;
    }

    /// <summary>宿主版本是否低于要求(数字段比较,忽略预发布后缀;不可解析时不拦)。</summary>
    private bool IsHostOlderThan(string minHostVersion)
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
        return ParseNumeric(options.HostVersion) is { } host && ParseNumeric(minHostVersion) is { } min && host < min;
    }

    private async Task ActivateAsync(PluginRuntime runtime, CancellationToken cancellationToken)
    {
        PluginDescriptor descriptor = runtime.Descriptor;
        PluginManifest manifest = descriptor.Manifest!;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string entryPath = Path.GetFullPath(Path.Combine(descriptor.Directory, manifest.Entry));
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
            CleanupRuntime(runtime);
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
        cts.CancelAfter(options.ActivationTimeout);
        // WaitAsync:即使插件无视取消令牌,宿主也不陪它挂着。
        await instance.ActivateAsync(context, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>隔离模式:拉起 PluginHost 进程并经 RPC 激活(设计稿 02/04/05)。</summary>
    private async Task ActivateIsolatedAsync(PluginRuntime runtime, PluginManifest manifest, string entryPath,
        PluginContext context, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
        PluginProcessClient client = await PluginProcessClient.StartAsync(manifest, entryPath, context,
            options.HostVersion, context.DataDirectory, options.ActivationTimeout, cts.Token,
            options.ThemeTokensProvider, options.EmbedHost).ConfigureAwait(false);
        runtime.Process = client;
        runtime.LastActivityTicks = Environment.TickCount64;
        runtime.OpenSurfaces = 0;
        client.Crashed += () => OnIsolatedCrashed(runtime);
        client.Activity += () => runtime.LastActivityTicks = Environment.TickCount64;
        client.SurfacesChanged += count => runtime.OpenSurfaces = count;
        client.StartHeartbeat(options.HeartbeatInterval);
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

    private TracePluginLogger GetOrCreateLogger(PluginRuntime runtime)
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
            Sessions = options.Connections is { } connections
                ? new SessionsCapability(connections)
                : new EmptySessionsApi(),
            RemoteFs = options is { Sftp: { } sftp, Connections: { } conn }
                ? new RemoteFsCapability(sftp, conn)
                : new UnavailableRemoteFs(),
            RemoteExec = options.Connections is { } execConnections
                ? new RemoteExecCapability(execConnections)
                : new UnavailableRemoteExec(),
            Commands = GetOrCreateCommandsApi(runtime),
            Events = new PluginEventHub(log, options.Connections, options.Theme, options.Localization),
            Ui = options.UiFactory?.Invoke(manifest.Id, log) ?? new NullUiApi(log),
            Secrets = options.DataStore is { } dataStore
                ? dataStore.CreateSecrets(manifest.Id)
                : options.SecretProtector is { } protector
                    ? new ProtectedSecretsCapability(dataDirectory, protector)
                    : new UnavailableSecrets(),
            Clipboard = options.Clipboard ?? new UnavailableClipboard(),
            Terminal = options.TerminalFactory?.Invoke(manifest.Id, log) ?? new UnavailableTerminal(),
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
            CleanupRuntime(runtime);
        }
    }

    /// <summary>拆除命令/事件等宿主侧引用并卸载 ALC / 回收进程(不等待 GC 真正回收)。</summary>
    private static void CleanupRuntime(PluginRuntime runtime)
    {
        if (runtime.Process is { } process)
        {
            runtime.Process = null;
            _ = process.DisposeAsync(); // 杀进程 + 断管道,后台尽力而为
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

    private static void Log(string message) => Trace.WriteLine($"[PluginManager] {message}");

    // ---- 能力缺席时的退化实现(headless / 单测宿主) ----

    private sealed class EmptySessionsApi : ISessionsApi
    {
        public Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SessionInfo>>([]);

        public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<SessionInfo?>(null);
    }

    private sealed class UnavailableRemoteFs : IRemoteFsApi
    {
        private static Exception Unavailable() => new InvalidOperationException("Remote file capability is unavailable in this host.");

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
}
