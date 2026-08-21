using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;

namespace VelaShell.ViewModels;

/// <summary>插件管理页里的一行。</summary>
public sealed class PluginRowViewModel(PluginDescriptor descriptor, bool hasTerminalGrant)
{
    /// <summary>插件 id。</summary>
    public string Id => descriptor.Id;

    /// <summary>显示名称(清单缺失时退化为 id)。</summary>
    public string DisplayName => descriptor.Manifest?.DisplayName ?? descriptor.Id;

    /// <summary>版本(清单缺失时空)。</summary>
    public string Version => descriptor.Manifest is { } m ? $"v{m.Version}" : "";

    /// <summary>宿主模式标签。</summary>
    public string HostMode => descriptor.Manifest?.HostMode.ToString() ?? "";

    /// <summary>
    /// 作者展示文案(如 <c>作者:Joe</c>)。清单的 <c>author</c> 缺省时退回 <c>publisher</c> ——
    /// 老插件只填了 publisher,不该因为新增字段就显示成"无作者"。
    /// </summary>
    public string AuthorText => HasAuthor
        ? Strings.Format("PluginManager_Author", descriptor.Manifest?.Author ?? descriptor.Manifest?.Publisher ?? "")
        : "";

    /// <summary>是否有作者可展示(两个字段都缺时整块隐藏)。</summary>
    public bool HasAuthor => !string.IsNullOrWhiteSpace(descriptor.Manifest?.Author)
                             || !string.IsNullOrWhiteSpace(descriptor.Manifest?.Publisher);

    /// <summary>是否为开发期挂载的插件(显示 DEV 角标)。</summary>
    public bool IsDevelopment => descriptor.IsDevelopment;

    /// <summary>已本地化的状态文案。</summary>
    public string StatusText => Strings.Get($"PluginState_{descriptor.State}");

    /// <summary>状态点着色:运行中为绿。</summary>
    public bool IsOk => descriptor.State == PluginState.Active;

    /// <summary>状态点着色:重启中为黄。</summary>
    public bool IsWarn => descriptor.State == PluginState.Crashed;

    /// <summary>状态点着色:失败/无效/不兼容为红。</summary>
    public bool IsErr => descriptor.State is PluginState.Failed or PluginState.Invalid or PluginState.Incompatible;

    /// <summary>状态点着色:其余(待激活/禁用/已停用)为灰。</summary>
    public bool IsIdle => !IsOk && !IsWarn && !IsErr;

    /// <summary>错误/原因(有则展示)。</summary>
    public string? Error => descriptor.Error;

    /// <summary>是否可切换启停(清单有效才行)。</summary>
    public bool CanToggle => descriptor.Manifest is not null
        && descriptor.State is not (PluginState.Invalid or PluginState.Incompatible);

    /// <summary>当前是否已禁用(决定按钮显示"启用"还是"禁用")。</summary>
    public bool IsDisabled => descriptor.State == PluginState.Disabled;

    /// <summary>启用/禁用按钮文案。</summary>
    public string ToggleText => Strings.Get(IsDisabled ? "PluginManager_Enable" : "PluginManager_Disable");

    /// <summary>该插件当前是否持有终端回写授权(展示"撤销"入口)。</summary>
    public bool HasTerminalGrant => hasTerminalGrant;

    /// <summary>撤销终端授权按钮文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string RevokeText => Strings.Get("PluginManager_RevokePermission");

    /// <summary>
    /// 是否可"重新加载"。只对开发期挂载的插件出现:它的意义是"我刚重编了,跑新代码",
    /// 对已安装插件没有对应的用户动作(那条路是重装)。
    /// </summary>
    public bool CanReload => descriptor.IsDevelopment
                             && descriptor.Manifest is not null
                             && descriptor.State is not (PluginState.Invalid or PluginState.Incompatible
                                 or PluginState.Disabled);

    /// <summary>重新加载按钮文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string ReloadText => Strings.Get("PluginManager_Reload");

    /// <summary>是否可卸载(用户安装,非应用自带)。</summary>
    public bool CanUninstall { get; init; }

    /// <summary>卸载按钮文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string UninstallText => Strings.Get("PluginManager_Uninstall");
}

/// <summary>
/// 插件管理页视图模型:列出全部插件、启停、撤销终端授权。
/// 订阅 <see cref="PluginManager.Changed" /> 自动刷新(封送到 UI 线程)。
/// </summary>
public sealed class PluginManagerViewModel : ReactiveObject, IDisposable
{
    private readonly PluginManager _manager;
    private readonly PluginPermissionGate? _gate;
    private readonly Action _onChanged;
    private readonly Action<string, int> _onDebugAttach;

    /// <summary>插件行集合(UI 线程更新)。</summary>
    public ObservableCollection<PluginRowViewModel> Plugins { get; } = [];

    /// <summary>标题文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string Title => Strings.Get("PluginManager_Title");

    /// <summary>"安装 .vpx" 按钮文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string InstallText => Strings.Get("PluginManager_Install");

    /// <summary>是否可安装(有可写用户目录)。</summary>
    public bool CanInstall => _manager.IsInstallSupported;

    /// <summary>空态文案。</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML 绑定只解析实例成员。")]
    public string EmptyText => Strings.Get("PluginManager_Empty");

    /// <summary>顶部状态提示(安装成功/失败),null 时不显示。</summary>
    public string? Notice
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否无插件(空态)。</summary>
    public bool IsEmpty
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>构造并加载。</summary>
    public PluginManagerViewModel(PluginManager manager, PluginPermissionGate? gate)
    {
        _manager = manager;
        _gate = gate;
        _onChanged = () => Dispatcher.UIThread.Post(() => _ = ReloadAsync());
        // 等待调试器的隔离插件:把 pid 摆到管理页上。它同时进日志、落 pid 文件,
        // 但开发者此刻多半正开着这个页面,让他去翻日志属于本可以省掉的一步。
        _onDebugAttach = (pluginId, pid) =>
            SetNotice(Strings.Format("PluginManager_WaitingForDebugger", pluginId, pid));
        _manager.Changed += _onChanged;
        _manager.DebugAttachRequested += _onDebugAttach;
        _ = ReloadAsync();
    }

    /// <summary>切换某插件启停。</summary>
    public async Task ToggleAsync(PluginRowViewModel row)
    {
        if (!row.CanToggle)
        {
            return;
        }
        if (row.IsDisabled)
        {
            await _manager.EnableAsync(row.Id).ConfigureAwait(false);
        }
        else
        {
            await _manager.DisableAsync(row.Id).ConfigureAwait(false);
        }
        // Changed 事件会触发刷新;这里不重复。
    }

    /// <summary>
    /// 重新加载某个开发期插件:停用 → 重读清单 → 重新装载。开发内环的一步:
    /// 改完代码 <c>dotnet build</c>,点这里就跑上新代码,不必重启 VelaShell。
    /// </summary>
    public async Task ReloadPluginAsync(PluginRowViewModel row)
    {
        if (!row.CanReload)
        {
            return;
        }
        await _manager.ReloadAsync(row.Id).ConfigureAwait(false);
        // Changed 事件会触发刷新;这里不重复。
    }

    /// <summary>撤销某插件的终端回写授权。</summary>
    public async Task RevokeTerminalAsync(PluginRowViewModel row)
    {
        if (_gate is not null)
        {
            await _gate.RevokeAsync(row.Id).ConfigureAwait(false);
            await ReloadAsync().ConfigureAwait(false);
        }
    }

    /// <summary>卸载某插件(用户安装的)。调用方已确认。</summary>
    public async Task UninstallAsync(PluginRowViewModel row)
    {
        if (row.CanUninstall)
        {
            await _manager.UninstallAsync(row.Id).ConfigureAwait(false);
            // Changed 事件会触发刷新。
        }
    }

    /// <summary>校验插件包并返回发布者信任状态与公钥指纹。</summary>
    public PluginPackageTrustInfo InspectPackageTrust(string vpxPath) =>
        _manager.InspectPackageTrust(vpxPath);

    /// <summary>把已由用户核对的签名发布者加入本机信任库。</summary>
    public Task<string> TrustPackagePublisherAsync(string vpxPath) =>
        _manager.TrustPackagePublisherAsync(vpxPath);

    /// <summary>从 .vpx 文件安装。未知来源只能由界面明确确认后单次放行。</summary>
    public async Task InstallFromVpxAsync(string vpxPath, bool allowUntrustedPackage = false)
    {
        try
        {
            string id = await _manager.InstallFromVpxAsync(vpxPath, allowUntrustedPackage).ConfigureAwait(false);
            SetNotice(Strings.Format("PluginManager_Installed", id));
        }
        catch (Exception ex)
        {
            SetNotice(Strings.Format("PluginManager_InstallFailed", ex.Message));
        }
    }

    private void SetNotice(string text) =>
        Dispatcher.UIThread.Post(() => Notice = text);

    private async Task ReloadAsync()
    {
        IReadOnlyList<PluginDescriptor> descriptors = _manager.Plugins;
        var rows = new List<PluginRowViewModel>(descriptors.Count);
        foreach (PluginDescriptor descriptor in descriptors)
        {
            bool grant = _gate is not null && await _gate.HasGrantAsync(descriptor.Id).ConfigureAwait(false);
            rows.Add(new(descriptor, grant) { CanUninstall = _manager.IsUninstallable(descriptor.Id) });
        }
        void Apply()
        {
            Plugins.Clear();
            foreach (PluginRowViewModel row in rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                Plugins.Add(row);
            }
            IsEmpty = Plugins.Count == 0;
        }
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(Apply);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _manager.Changed -= _onChanged;
        _manager.DebugAttachRequested -= _onDebugAttach;
    }
}
