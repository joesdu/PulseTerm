using Avalonia.Threading;
using VelaShell.Plugin.Redis.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// 一条已打开的 Redis 会话文档。宿主拿它做标签页外壳与状态圆点,内容是本插件自绘的面板。
/// </summary>
internal sealed class RedisWorkspaceDocument : IWorkspaceDocument
{
    private readonly RedisConnection _connection;
    private readonly RedisWorkspaceViewModel _viewModel;
    private readonly Loc _loc;
    private readonly IPluginContext _context;
    private int _disposed;

    /// <summary>构造。</summary>
    /// <param name="connection">已连接的连接。</param>
    /// <param name="request">宿主的连接请求(取展示名与端点)。</param>
    /// <param name="loc">文案表。</param>
    /// <param name="context">插件上下文。</param>
    public RedisWorkspaceDocument(
        RedisConnection connection,
        WorkspaceConnectRequest request,
        Loc loc,
        IPluginContext context)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(request);
        // 走隧道时 request.Host 已是本地转发端点,所以端点文本要显示**真实目标**,
        // 再把来路缀在后面 —— 用户认的是 "10.0.3.12:6379 ↝ bastion-01",不是 127.0.0.1。
        string endpoint = request.Tunnel is { } tunnel
            ? $"{tunnel.TargetHost}:{tunnel.TargetPort}  ↝ {tunnel.JumpDisplayName}"
            : $"{request.Host}:{request.Port}";
        string title = string.IsNullOrWhiteSpace(request.DisplayName) ? endpoint : request.DisplayName;
        // 收藏与控制台历史落插件私有存储:按端点分组,跨会话留住。
        _viewModel = new(connection, title, endpoint, loc, new PluginLoggerFacade(context.Log), new RedisStore(context));
        Status = new(ProtocolSessionState.Connected, Describe());
        _connection.Availability += OnAvailability;
    }

    /// <inheritdoc />
    public WorkspaceStatus Status { get; private set; }

    /// <inheritdoc />
    public event EventHandler<WorkspaceStatus>? StatusChanged;

    /// <inheritdoc />
    public object CreateView()
    {
        var view = new RedisWorkspaceView(_viewModel);
        // 首次加载在控件挂上之后再跑:构造期做 I/O 会让标签页在出现前先卡住一拍。
        Dispatcher.UIThread.Post(() => _ = _viewModel.InitializeAsync());
        return view;
    }

    /// <inheritdoc />
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        // 库本身会在后台自动重连,所以这里做的是"确认它真的通了"并把状态刷新出去:
        // PING 成功即恢复,失败就如实报错,而不是让按钮点了没有任何反馈。
        try
        {
            await _connection.PingAsync().ConfigureAwait(false);
            await _connection.RefreshKeyspaceAsync(cancellationToken).ConfigureAwait(false);
            Publish(new(ProtocolSessionState.Connected, Describe()));
        }
        catch (Exception ex)
        {
            Publish(new(ProtocolSessionState.Faulted, _loc.Format("Redis_Error", ex.Message)));
            throw new ProtocolConnectionException(_loc.Format("Redis_Error", ex.Message), ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _connection.Availability -= OnAvailability;
        _viewModel.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
        _context.Log.Info("Redis session closed.");
        Publish(new(ProtocolSessionState.Closed));
    }

    private void OnAvailability(bool available)
    {
        // 库的连接事件在它自己的线程上触发:视图模型的属性通知已自行封送回 UI 线程
        // (见 Ui/Mvvm.cs 的 RaisePropertyChanged),这里只管把状态转给宿主。
        _viewModel.OnAvailabilityChanged(available);
        Publish(available
            ? new(ProtocolSessionState.Connected, Describe())
            : new WorkspaceStatus(ProtocolSessionState.Faulted, _loc["Redis_Disconnected"]));
    }

    private void Publish(WorkspaceStatus status)
    {
        Status = status;
        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            // 宿主的处理器自爆不该反过来把插件带崩。
            _context.Log.Error("A host status handler threw.", ex);
        }
    }

    private string Describe() =>
        $"{_loc["Redis_Connected"]} · {_connection.Info.Flavor} {_connection.Info.Version} · {_connection.Info.Protocol}";
}
