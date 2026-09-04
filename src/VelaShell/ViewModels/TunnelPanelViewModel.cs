using System.Collections.ObjectModel;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Presentation.Services;

namespace VelaShell.ViewModels;

/// <summary>
/// 隧道管理面板(设计 B3Rth),以服务器为中心:从已保存会话中选一台服务器,
/// 创建/启动隧道时后台自动建立专用 SSH 连接(不打开终端标签)。
/// 隧道生命周期独立于终端会话;该服务器最后一条隧道删除后,后台连接自动断开。
/// </summary>
public class TunnelPanelViewModel : ReactiveObject, IDisposable
{
    /// <summary>隧道配置持久化集合:每台服务器一份文档(id = profileId),内容为配置列表。</summary>
    private const string TunnelCollection = "tunnels";

    private readonly Func<SessionProfile, CancellationToken, Task<Guid>>? _backgroundConnector;

    private readonly IAppDataStore? _dataStore;

    /// <summary>已从存储恢复过配置的服务器(每次运行每台只恢复一次)。</summary>
    private readonly HashSet<Guid> _restoredProfiles = [];

    /// <summary>面板持有的后台隧道连接:profileId → sessionId。</summary>
    private readonly Dictionary<Guid, Guid> _hostSessions = [];

    private readonly Func<Guid, bool>? _isSessionAlive;

    /// <summary>每台服务器的隧道条目缓存:切换服务器/后台连接掉线后配置仍在,可一键重启。</summary>
    private readonly Dictionary<Guid, ObservableCollection<TunnelItemViewModel>> _itemsByProfile = [];

    private readonly HashSet<Guid> _deletingTunnelIds = [];

    /// <summary>正在自动恢复的服务器,防止时钟每 5 秒重复排一次同样的恢复。</summary>
    private readonly HashSet<Guid> _autoReconnectingProfiles = [];

    /// <summary>自动恢复的连续失败次数,用于按 <see cref="BackoffFor" /> 拉长重试间隔。</summary>
    private readonly Dictionary<Guid, int> _autoReconnectFailures = [];

    /// <summary>自动恢复的下次可尝试时刻(退避窗口)。</summary>
    private readonly Dictionary<Guid, DateTime> _autoReconnectNotBefore = [];

    private readonly Lock _deletingTunnelIdsGate = new();

    private readonly DispatcherTimer? _liveTimer;
    private readonly Func<Task<IReadOnlyList<SessionProfile>>>? _savedProfilesProvider;
    private readonly Func<Guid, Task>? _sessionDisconnector;
    private readonly ITunnelWorkflowService _workflowService;
    private Guid? _editingTunnelId;
    private bool _isConnectingHost;

    private SessionProfile? _selectedServer;
    private ObservableCollection<TunnelItemViewModel> _tunnels;

    /// <summary>构造隧道面板视图模型;可注入已保存会话来源、后台连接/断开与存活探测委托及配置持久化存储(均可为空,便于测试)。</summary>
    public TunnelPanelViewModel(
        ITunnelWorkflowService workflowService,
        Func<Task<IReadOnlyList<SessionProfile>>>? savedProfilesProvider = null,
        Func<SessionProfile, CancellationToken, Task<Guid>>? backgroundConnector = null,
        Func<Guid, bool>? isSessionAlive = null,
        Func<Guid, Task>? sessionDisconnector = null,
        IAppDataStore? dataStore = null)
    {
        _workflowService = workflowService ?? throw new ArgumentNullException(nameof(workflowService));
        _savedProfilesProvider = savedProfilesProvider;
        _backgroundConnector = backgroundConnector;
        _isSessionAlive = isSessionAlive;
        _sessionDisconnector = sessionDisconnector;
        _dataStore = dataStore;
        Servers = [];
        _tunnels = [];
        IObservable<bool> canCreate = this.WhenAnyValue(vm => vm.SelectedServer,
            vm => vm.NewLocalHost,
            vm => vm.NewLocalPort,
            vm => vm.NewRemoteHost,
            vm => vm.NewRemotePort,
            vm => vm.NewTunnelTypeIndex,
            vm => vm.ForwardToServerLoopback,
            (server, localHost, localPort, remoteHost, remotePort, typeIndex, _) =>
                server is not null &&
                !string.IsNullOrWhiteSpace(localHost) &&
                localPort is >= 1 and <= 65535 &&
                (typeIndex == 2 ||
                 (!string.IsNullOrWhiteSpace(remoteHost) && remotePort is >= 1 and <= 65535)));
        CreateTunnelCommand = ReactiveCommand.CreateFromTask(SubmitAsync, canCreate);
        StopTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(StopTunnelAsync);
        StartTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(StartTunnelAsync);
        DeleteTunnelCommand = ReactiveCommand.CreateFromTask<Guid>(DeleteTunnelAsync);
        EditTunnelCommand = ReactiveCommand.Create<Guid>(BeginEdit);
        // 取消:编辑模式下退出编辑并清空表单;普通状态表单默认值清空不易察觉,直接收起面板。
        ResetFormCommand = ReactiveCommand.Create(() =>
        {
            ErrorMessage = null;
            bool wasEditing = IsEditing;
            ResetForm();
            if (!wasEditing)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        });
        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));

        // 每 5 秒刷新条目(运行时长/服务侧状态与错误)并核对后台连接是否还活着;
        // 无 Avalonia 应用(单元测试)时跳过。
        if (Application.Current is not null)
        {
            _liveTimer = new()
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _liveTimer.Tick += (_, _) => RefreshLiveState();
            _liveTimer.Start();
        }
    }

    /// <summary>隧道走哪台服务器:全部已保存会话。</summary>
    public ObservableCollection<SessionProfile> Servers { get; }

    /// <summary>当前选中的目标服务器;切换时会加载其隧道条目并刷新连接状态。</summary>
    public SessionProfile? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (ReferenceEquals(_selectedServer, value))
            {
                return;
            }
            this.RaiseAndSetIfChanged(ref _selectedServer, value);
            OnServerChanged();
        }
    }

    /// <summary>当前服务器的隧道条目(按服务器缓存,切换不丢配置)。</summary>
    public ObservableCollection<TunnelItemViewModel> Tunnels
    {
        get => _tunnels;
        private set => this.RaiseAndSetIfChanged(ref _tunnels, value);
    }

    /// <summary>服务器行下方的连接状态说明。</summary>
    public string ServerStatusText
    {
        get
        {
            if (SelectedServer is null)
            {
                return Strings.Get("Msg_SelectServer");
            }
            if (_autoReconnectingProfiles.Contains(SelectedServer.Id))
            {
                return Strings.Get("Msg_TunnelAutoReconnecting");
            }
            if (_isConnectingHost)
            {
                return Strings.Get("Msg_ConnectingInBackground");
            }
            return ResolveLiveSession(SelectedServer.Id) is not null
                       ? Strings.Get("Msg_TunnelConnectedBackground")
                       : Strings.Get("Msg_TunnelNotConnectedAuto");
        }
    }

    /// <summary>当前服务器是否已有活着的后台隧道连接。</summary>
    public bool IsServerConnected => SelectedServer is not null && ResolveLiveSession(SelectedServer.Id) is not null;

    /// <summary>ComboBox 适配器:0 = 本地转发, 1 = 远程转发, 2 = 动态转发 (SOCKS)。</summary>
    public int NewTunnelTypeIndex
    {
        get =>
            NewTunnelType switch
            {
                TunnelType.RemoteForward => 1,
                TunnelType.DynamicForward => 2,
                _ => 0
            };
        set
        {
            NewTunnelType = value switch
            {
                1 => TunnelType.RemoteForward,
                2 => TunnelType.DynamicForward,
                _ => TunnelType.LocalForward
            };
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsRemoteTargetVisible));
        }
    }

    /// <summary>动态转发(SOCKS)没有固定目标,隐藏目标主机/端口输入。</summary>
    public bool IsRemoteTargetVisible => NewTunnelType != TunnelType.DynamicForward;

    /// <summary>
    /// 默认转发到"服务器本机":目标主机锁定为 127.0.0.1(从服务器视角,
    /// 填服务器公网 IP 会被服务器自己拒绝)。取消勾选可填内网第三方主机。
    /// </summary>
    public bool ForwardToServerLoopback
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            if (value)
            {
                NewRemoteHost = "127.0.0.1";
            }
            this.RaisePropertyChanged(nameof(IsRemoteHostEditable));
        }
    } = true;

    /// <summary>目标主机是否可编辑:仅在未勾选"转发到服务器本机"时开放输入。</summary>
    public bool IsRemoteHostEditable => !ForwardToServerLoopback;

    /// <summary>新建/编辑表单选择的隧道类型(本地/远程/动态转发)。</summary>
    public TunnelType NewTunnelType
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>表单中的本地监听主机,默认 127.0.0.1。</summary>
    public string NewLocalHost
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "127.0.0.1";

    /// <summary>表单中的本地监听端口。</summary>
    public int NewLocalPort
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 27017;

    /// <summary>表单中的目标(远端)主机,默认 127.0.0.1。</summary>
    public string NewRemoteHost
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "127.0.0.1";

    /// <summary>表单中的目标(远端)端口。</summary>
    public int NewRemotePort
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 27017;

    /// <summary>
    /// 表单中的「承载会话掉线后自动重连并重建」开关。默认关闭:自动重拨可能弹凭据提示,
    /// 也可能对着一台真的下线了的服务器反复重试,该由用户按隧道自己决定。
    /// </summary>
    public bool NewAutoReconnect
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>表单中的隧道别名(可选,留空时用路由描述兜底)。</summary>
    public string NewTunnelName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>表单区展示的错误提示;无错误时为空。</summary>
    public string? ErrorMessage
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>非空 = 表单处于"编辑既有隧道"模式;提交按钮显示"保存"。</summary>
    public bool IsEditing => _editingTunnelId is not null;

    /// <summary>表单标题:根据是否处于编辑模式显示"编辑隧道"或"新建隧道"。</summary>
    public string FormTitle => IsEditing ? Strings.Get("Msg_EditTunnel") : Strings.Get("NewTunnel");

    /// <summary>提交按钮文案:编辑模式显示"保存",否则显示"创建"。</summary>
    public string SubmitButtonText => IsEditing ? Strings.Get("Save") : Strings.Get("Msg_Create");

    /// <summary>按表单当前配置创建新隧道(或保存正在编辑的隧道)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CreateTunnelCommand { get; }

    /// <summary>停止指定隧道(按隧道 Id)。</summary>
    public ReactiveCommand<Guid, RxVoid> StopTunnelCommand { get; }

    /// <summary>启动一条已停止的隧道(按隧道 Id),必要时先建立后台连接。</summary>
    public ReactiveCommand<Guid, RxVoid> StartTunnelCommand { get; }

    /// <summary>删除指定隧道(按隧道 Id),并在无隧道时释放后台连接。</summary>
    public ReactiveCommand<Guid, RxVoid> DeleteTunnelCommand { get; }

    /// <summary>由视图设置:向用户确认删除某条隧道;返回 false 或未设置则取消。</summary>
    public Func<string, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>把某条隧道的配置填回表单进入编辑模式;保存时按新配置重建。</summary>
    public ReactiveCommand<Guid, RxVoid> EditTunnelCommand { get; }

    /// <summary>取消按钮:清空表单与错误提示,并退出编辑模式。</summary>
    public ReactiveCommand<RxVoid, RxVoid> ResetFormCommand { get; }

    /// <summary>收起面板。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseCommand { get; }

    /// <summary>释放面板资源:停止实时刷新计时器。</summary>
    public void Dispose()
    {
        _liveTimer?.Stop();
        GC.SuppressFinalize(this);
    }

    /// <summary>面板右上角关闭按钮(设计 B3Rth tunCloseBtn),由宿主收起面板。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>打开面板时调用:刷新服务器列表并(可选)预选某台服务器。</summary>
    public async Task OpenAsync(Guid? preferredProfileId = null)
    {
        await LoadServersAsync();
        SessionProfile? preferred = preferredProfileId is { } id
                                        ? Servers.FirstOrDefault(p => p.Id == id)
                                        : null;
        if (preferred is not null)
        {
            SelectedServer = preferred;
        }
        else
        {
            SelectedServer ??= Servers.FirstOrDefault();
        }

        RefreshServerStatus();
    }

    private async Task LoadServersAsync()
    {
        if (_savedProfilesProvider is null)
        {
            return;
        }
        try
        {
            IReadOnlyList<SessionProfile> profiles = await _savedProfilesProvider();
            Guid? selectedId = SelectedServer?.Id;
            Servers.Clear();
            foreach (SessionProfile profile in profiles)
            {
                Servers.Add(profile);
            }
            if (selectedId is { } sid)
            {
                _selectedServer = Servers.FirstOrDefault(p => p.Id == sid);
            }
            this.RaisePropertyChanged(nameof(SelectedServer));
        }
        catch
        {
            // 列表刷新失败保留旧数据。
        }
    }

    // ---- 服务器/会话解析 ----

    /// <summary>面板持有的、仍然活着的后台会话;掉线的顺手清掉映射。</summary>
    private Guid? ResolveLiveSession(Guid profileId)
    {
        if (!_hostSessions.TryGetValue(profileId, out Guid sessionId))
        {
            return null;
        }
        if (_isSessionAlive?.Invoke(sessionId) == false)
        {
            _hostSessions.Remove(profileId);
            return null;
        }
        return sessionId;
    }

    /// <summary>取得(或后台建立)到指定服务器的隧道连接。</summary>
    private async Task<Guid> EnsureSessionAsync(SessionProfile profile, CancellationToken ct)
    {
        if (ResolveLiveSession(profile.Id) is { } alive)
        {
            return alive;
        }
        if (_backgroundConnector is null)
        {
            throw new InvalidOperationException(Strings.Get("Msg_BackgroundConnectorNotConfigured"));
        }
        _isConnectingHost = true;
        RefreshServerStatus();
        try
        {
            Guid sessionId = await _backgroundConnector(profile, ct);
            _hostSessions[profile.Id] = sessionId;
            return sessionId;
        }
        finally
        {
            _isConnectingHost = false;
            RefreshServerStatus();
        }
    }

    /// <summary>该服务器最后一条隧道删除后,后台连接没有存在的必要,自动断开。</summary>
    private async Task ReleaseHostIfUnusedAsync(Guid profileId)
    {
        if (!_hostSessions.TryGetValue(profileId, out Guid sessionId))
        {
            return;
        }
        bool hasTunnels;
        try
        {
            hasTunnels = _workflowService.GetActiveTunnels(sessionId).Count > 0;
        }
        catch
        {
            hasTunnels = false;
        }
        if (hasTunnels)
        {
            return;
        }
        _hostSessions.Remove(profileId);
        if (_sessionDisconnector is not null)
        {
            try
            {
                await _sessionDisconnector(sessionId);
            }
            catch
            {
                // 会话可能已经掉线。
            }
        }
        RefreshServerStatus();
    }

    private void OnServerChanged()
    {
        ErrorMessage = null;
        if (IsEditing)
        {
            ResetForm();
        }
        if (SelectedServer is { } server)
        {
            if (!_itemsByProfile.TryGetValue(server.Id, out ObservableCollection<TunnelItemViewModel>? items))
            {
                items = [];
                _itemsByProfile[server.Id] = items;

                // 找回该服务器后台会话上已建的隧道(面板重开/重建的场景)。
                if (ResolveLiveSession(server.Id) is { } sessionId)
                {
                    try
                    {
                        IReadOnlyList<TunnelInfo> existing = _workflowService.GetActiveTunnels(sessionId);
                        foreach (TunnelInfo info in existing.OrderBy(t => t.CreatedAt))
                        {
                            items.Add(new(info));
                        }
                    }
                    catch
                    {
                        // 找不回历史隧道不影响面板可用性。
                    }
                }

                // 恢复上次运行持久化的隧道配置(以"已停止"状态展示,由用户手动启动)。
                _ = RestorePersistedTunnelsAsync(server.Id, items);
            }
            Tunnels = items;
        }
        else
        {
            Tunnels = [];
        }
        RefreshServerStatus();
    }

    private void RefreshServerStatus()
    {
        this.RaisePropertyChanged(nameof(ServerStatusText));
        this.RaisePropertyChanged(nameof(IsServerConnected));
    }

    /// <summary>
    /// 状态时钟的一拍:同步流量读数、刷新条目运行时长与服务侧状态;后台会话掉线时把条目
    /// 标为已停止,并为勾了「自动重连」的隧道排一次恢复。
    /// <para>
    /// 由面板自己的 5 秒计时器驱动。无 Avalonia 应用(单元测试)时没有计时器,
    /// 测试直接调它来推进一拍。
    /// </para>
    /// </summary>
    internal void RefreshLiveState()
    {
        DetectDroppedSessions();
        ScheduleAutoReconnect();
        try
        {
            _workflowService.RefreshStatistics();
        }
        catch
        {
            // 读数同步失败(服务正在拆除)不该让整个时钟停摆。
        }
        foreach (TunnelItemViewModel tunnel in Tunnels)
        {
            tunnel.RefreshLive();
        }
        RefreshServerStatus();
    }

    /// <summary>
    /// 检出掉线的后台会话并把其隧道条目标为已停止。
    /// <para>
    /// 查的是面板持有的**每一台**服务器,而不只是当前选中的那台:隧道跑在后台,
    /// 用户不会为了让某台的隧道被照看到而一直停在它的页面上。
    /// </para>
    /// </summary>
    private void DetectDroppedSessions()
    {
        if (_isSessionAlive is null)
        {
            return;
        }
        foreach (Guid profileId in _hostSessions.Keys.ToList())
        {
            if (!_hostSessions.TryGetValue(profileId, out Guid sessionId) || _isSessionAlive(sessionId))
            {
                continue;
            }
            _hostSessions.Remove(profileId);
            if (!_itemsByProfile.TryGetValue(profileId, out ObservableCollection<TunnelItemViewModel>? items))
            {
                continue;
            }
            foreach (TunnelItemViewModel tunnel in items)
            {
                tunnel.Status = TunnelStatus.Stopped;
            }
        }
    }

    // ---- 断线自动恢复 ----

    /// <summary>
    /// 为每台「有隧道勾了自动重连、且后台连接已断」的服务器排一次恢复。
    /// 失败按指数退避,避免服务器真的下线时每 5 秒重拨一次、把凭据提示刷满屏幕。
    /// </summary>
    private void ScheduleAutoReconnect()
    {
        if (_backgroundConnector is null)
        {
            return;
        }
        foreach ((Guid profileId, ObservableCollection<TunnelItemViewModel> items) in _itemsByProfile)
        {
            if (_autoReconnectingProfiles.Contains(profileId) || ResolveLiveSession(profileId) is not null)
            {
                continue;
            }
            if (!items.Any(t => t is { AutoReconnect: true, IsActive: false, StoppedByUser: false }))
            {
                continue;
            }
            if (_autoReconnectNotBefore.TryGetValue(profileId, out DateTime notBefore) && DateTime.UtcNow < notBefore)
            {
                continue;
            }
            SessionProfile? profile = Servers.FirstOrDefault(p => p.Id == profileId);
            if (profile is null)
            {
                continue;
            }
            _autoReconnectingProfiles.Add(profileId);
            _ = AutoReconnectAsync(profile, items);
        }
    }

    /// <summary>重连服务器并按原配置重建其勾了自动重连的隧道。</summary>
    private async Task AutoReconnectAsync(SessionProfile profile, ObservableCollection<TunnelItemViewModel> items)
    {
        bool isSelected = SelectedServer?.Id == profile.Id;
        try
        {
            if (isSelected)
            {
                RefreshServerStatus();
            }
            Guid sessionId = await EnsureSessionAsync(profile, CancellationToken.None).ConfigureAwait(true);
            foreach (TunnelItemViewModel tunnel in items.Where(t => t is { AutoReconnect: true, IsActive: false, StoppedByUser: false }).ToList())
            {
                // 服务侧还留着停止状态的旧记录,先清掉再重建,避免列表里越积越多。
                await _workflowService.RemoveTunnelAsync(tunnel.Id, CancellationToken.None).ConfigureAwait(true);
                TunnelInfo result = await _workflowService.CreateTunnelAsync(sessionId, tunnel.Config, CancellationToken.None).ConfigureAwait(true);
                int index = items.IndexOf(tunnel);
                if (index >= 0)
                {
                    items[index] = new(result);
                }
            }
            _autoReconnectNotBefore.Remove(profile.Id);
            _autoReconnectFailures.Remove(profile.Id);
            if (isSelected)
            {
                ErrorMessage = null;
            }
        }
        catch (Exception ex)
        {
            int failures = _autoReconnectFailures.GetValueOrDefault(profile.Id) + 1;
            _autoReconnectFailures[profile.Id] = failures;
            _autoReconnectNotBefore[profile.Id] = DateTime.UtcNow + BackoffFor(failures);
            if (isSelected)
            {
                ErrorMessage = Strings.Format("Msg_TunnelAutoReconnectFailed", FriendlyError(ex));
            }
        }
        finally
        {
            _autoReconnectingProfiles.Remove(profile.Id);
            if (isSelected)
            {
                RefreshServerStatus();
            }
        }
    }

    /// <summary>失败重试的退避间隔:10s → 30s → 1min → 2min → 5min 封顶。</summary>
    private static TimeSpan BackoffFor(int failures) =>
        failures switch
        {
            1 => TimeSpan.FromSeconds(10),
            2 => TimeSpan.FromSeconds(30),
            3 => TimeSpan.FromMinutes(1),
            4 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(5)
        };

    // ---- 配置持久化(仅配置,不含运行状态;重启后恢复为"已停止",由用户手动启动) ----

    /// <summary>配置身份键:恢复时用来跳过面板里已存在的同配置条目。</summary>
    private static string ConfigKey(TunnelConfig config) =>
        $"{config.Type}|{config.LocalHost}|{config.LocalPort}|{config.RemoteHost}|{config.RemotePort}|{config.Name}";

    /// <summary>从存储恢复该服务器的隧道配置(每次运行每台只执行一次);失败不影响面板。</summary>
    private async Task RestorePersistedTunnelsAsync(Guid profileId, ObservableCollection<TunnelItemViewModel> items)
    {
        if (_dataStore is null || !_restoredProfiles.Add(profileId))
        {
            return;
        }
        try
        {
            List<TunnelConfig>? saved = await _dataStore
                                              .GetAsync<List<TunnelConfig>>(TunnelCollection, profileId.ToString("D"))
                                              .ConfigureAwait(true);
            if (saved is not { Count: > 0 })
            {
                return;
            }
            HashSet<string> existing = [.. items.Select(t => ConfigKey(t.Config))];
            foreach (TunnelConfig config in saved)
            {
                if (!existing.Add(ConfigKey(config)))
                {
                    continue;
                }
                items.Add(new(new TunnelInfo
                {
                    Id = Guid.NewGuid(),
                    Config = config,
                    Status = TunnelStatus.Stopped,
                    SessionId = Guid.Empty,
                    CreatedAt = DateTime.UtcNow,
                    BytesTransferred = 0
                }));
            }
        }
        catch
        {
            // 恢复失败(存储损坏/旧格式)不影响面板可用性,后续保存会覆盖为新格式。
        }
    }

    /// <summary>把该服务器当前的隧道配置快照写入存储;在创建/编辑/删除后调用,失败不打断操作。</summary>
    private void PersistTunnels(Guid profileId)
    {
        if (_dataStore is null || !_itemsByProfile.TryGetValue(profileId, out ObservableCollection<TunnelItemViewModel>? items))
        {
            return;
        }
        // 先在调用线程(UI)拍快照再落盘,避免后台线程枚举 ObservableCollection。
        var configs = items.Select(t => t.Config).ToList();
        _ = SaveAsync();

        async Task SaveAsync()
        {
            try
            {
                await _dataStore.UpsertAsync(TunnelCollection, profileId.ToString("D"), configs).ConfigureAwait(false);
            }
            catch
            {
                // 落盘失败不影响运行中的隧道;下次变更会再次尝试。
            }
        }
    }

    // ---- 表单/条目操作 ----

    private void BeginEdit(Guid tunnelId)
    {
        TunnelItemViewModel? item = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
        if (item is null)
        {
            return;
        }
        if (item.IsActive)
        {
            return;
        }
        _editingTunnelId = tunnelId;
        NewTunnelName = item.Config.Name;
        NewLocalHost = item.Config.LocalHost;
        NewLocalPort = (int)item.Config.LocalPort;
        NewTunnelTypeIndex = item.TunnelType switch
        {
            TunnelType.RemoteForward => 1,
            TunnelType.DynamicForward => 2,
            _ => 0
        };
        ForwardToServerLoopback = item.TunnelType != TunnelType.DynamicForward && item.Config.RemoteHost is "" or "127.0.0.1" or "localhost";
        if (!ForwardToServerLoopback)
        {
            NewRemoteHost = item.Config.RemoteHost;
        }
        NewRemotePort = (int)item.Config.RemotePort;
        NewAutoReconnect = item.Config.AutoReconnect;
        ErrorMessage = null;
        RaiseEditingChanged();
    }

    /// <summary>创建新隧道,或保存编辑(旧隧道先停止移除,再按新配置重建)。</summary>
    private async Task SubmitAsync(CancellationToken ct)
    {
        if (SelectedServer is not { } server)
        {
            return;
        }
        try
        {
            ErrorMessage = null;
            TunnelConfig config = BuildConfig();
            if (_editingTunnelId is { } editingId)
            {
                TunnelItemViewModel? existing = Tunnels.FirstOrDefault(t => t.Id == editingId);
                int index = existing is null ? -1 : Tunnels.IndexOf(existing);
                bool wasActive = existing?.IsActive ?? true;
                await _workflowService.RemoveTunnelAsync(editingId, ct).ConfigureAwait(true);
                if (wasActive)
                {
                    Guid sessionId = await EnsureSessionAsync(server, ct).ConfigureAwait(true);
                    TunnelInfo result = await _workflowService.CreateTunnelAsync(sessionId, config, ct).ConfigureAwait(true);
                    ReplaceOrAdd(index, new(result));
                }
                else
                {
                    // 停止状态下编辑:只更新配置,不自动拉起。
                    var stopped = new TunnelInfo
                    {
                        Id = Guid.NewGuid(),
                        Config = config,
                        Status = TunnelStatus.Stopped,
                        SessionId = ResolveLiveSession(server.Id) ?? Guid.Empty,
                        CreatedAt = DateTime.UtcNow,
                        BytesTransferred = 0
                    };
                    ReplaceOrAdd(index, new(stopped));
                }
            }
            else
            {
                Guid sessionId = await EnsureSessionAsync(server, ct).ConfigureAwait(true);
                TunnelInfo result = await _workflowService.CreateTunnelAsync(sessionId, config, ct).ConfigureAwait(true);
                Tunnels.Add(new(result));
            }
            PersistTunnels(server.Id);
            ResetForm();
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex);
        }
    }

    private void ReplaceOrAdd(int index, TunnelItemViewModel item)
    {
        if (index >= 0 && index < Tunnels.Count)
        {
            Tunnels[index] = item;
        }
        else
        {
            Tunnels.Add(item);
        }
    }

    private TunnelConfig BuildConfig()
    {
        bool isDynamic = NewTunnelType == TunnelType.DynamicForward;
        bool loopback = !isDynamic && ForwardToServerLoopback;
        return new()
        {
            Type = NewTunnelType,
            // 别名可选(设计 B3Rth):留空时用路由描述兜底,列表里始终有可读名称。
            Name = NewTunnelName.Trim(),
            LocalHost = NewLocalHost.Trim(),
            LocalPort = (uint)NewLocalPort,
            RemoteHost = isDynamic
                             ? string.Empty
                             : loopback
                                 ? "127.0.0.1"
                                 : NewRemoteHost.Trim(),
            RemotePort = isDynamic ? 0u : (uint)NewRemotePort,
            AutoReconnect = NewAutoReconnect
        };
    }

    private async Task StopTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        try
        {
            ErrorMessage = null;
            await _workflowService.StopTunnelAsync(tunnelId, ct).ConfigureAwait(true);
            TunnelItemViewModel? tunnel = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
            if (tunnel is not null)
            {
                tunnel.Status = TunnelStatus.Stopped;
                // 记下"是用户按停的",免得自动恢复过一会儿把它又拉起来。
                tunnel.StoppedByUser = true;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex);
        }
    }

    /// <summary>启动一条已停止的隧道:必要时先后台连上服务器,再按原配置重建转发。</summary>
    private async Task StartTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        if (SelectedServer is not { } server)
        {
            return;
        }
        try
        {
            ErrorMessage = null;
            TunnelItemViewModel? existing = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
            if (existing == null)
            {
                return;
            }

            // 服务侧还留着停止状态的旧记录,先清掉再重建,避免列表里越积越多。
            await _workflowService.RemoveTunnelAsync(tunnelId, ct).ConfigureAwait(true);
            Guid sessionId = await EnsureSessionAsync(server, ct).ConfigureAwait(true);
            TunnelInfo result = await _workflowService.CreateTunnelAsync(sessionId, existing.Config, ct).ConfigureAwait(true);
            int index = Tunnels.IndexOf(existing);
            ReplaceOrAdd(index, new(result));
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex);
        }
    }

    private async Task DeleteTunnelAsync(Guid tunnelId, CancellationToken ct)
    {
        TunnelItemViewModel? tunnel = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
        if (tunnel is null || ConfirmDelete is null)
        {
            return;
        }
        bool isDeleteInFlight;
        lock (_deletingTunnelIdsGate)
        {
            isDeleteInFlight = !_deletingTunnelIds.Add(tunnelId);
        }
        if (isDeleteInFlight)
        {
            return;
        }
        try
        {
            ErrorMessage = null;
            if (!await ConfirmDelete(Strings.Get("Tunnel_DeleteConfirmationBody")).ConfigureAwait(true))
            {
                return;
            }
            tunnel = Tunnels.FirstOrDefault(t => t.Id == tunnelId);
            if (tunnel is null)
            {
                return;
            }
            await _workflowService.RemoveTunnelAsync(tunnelId, ct).ConfigureAwait(true);
            Tunnels.Remove(tunnel);
            if (_editingTunnelId == tunnelId)
            {
                ResetForm();
            }
            if (SelectedServer is { } server)
            {
                PersistTunnels(server.Id);
                await ReleaseHostIfUnusedAsync(server.Id).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = FriendlyError(ex);
        }
        finally
        {
            lock (_deletingTunnelIdsGate)
            {
                _deletingTunnelIds.Remove(tunnelId);
            }
        }
    }

    /// <summary>把服务层异常翻译成用户能看懂的提示。</summary>
    private string FriendlyError(Exception ex) =>
        ex switch
        {
            OperationCanceledException => Strings.Get("Msg_OperationCancelled"),
            InvalidOperationException when ex.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                => Strings.Get("Msg_ServerConnectionUnavailable"),
            SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } => Strings.Format("Msg_LocalPortInUse", NewLocalPort),
            _ => ex.Message
        };

    private void ResetForm()
    {
        _editingTunnelId = null;
        NewTunnelName = string.Empty;
        NewLocalHost = "127.0.0.1";
        NewLocalPort = 27017;
        NewRemotePort = 27017;
        NewAutoReconnect = false;
        NewTunnelTypeIndex = 0;
        ForwardToServerLoopback = true; // 同时把目标主机复位为 127.0.0.1
        RaiseEditingChanged();
    }

    private void RaiseEditingChanged()
    {
        this.RaisePropertyChanged(nameof(IsEditing));
        this.RaisePropertyChanged(nameof(FormTitle));
        this.RaisePropertyChanged(nameof(SubmitButtonText));
    }
}
