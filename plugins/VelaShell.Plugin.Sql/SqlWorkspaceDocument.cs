using VelaShell.Plugin.Sql.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql;

/// <summary>
/// 一条已打开的数据库会话文档。M0 只做外壳:连接、状态圆点、重连、关闭。
/// 对象树(M1)、SQL 编辑器与结果网格(M2)在这之上追加。
/// </summary>
internal sealed class SqlWorkspaceDocument : IWorkspaceDocument
{
    private readonly SqlSession _session;
    private readonly WorkspaceConnectRequest _request;
    private readonly IPluginContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Loc _loc;
    private SqlWorkspaceViewModel? _viewModel;
    private WorkspaceStatus _status;
    private Task? _probe;

    /// <summary>造一个文档。</summary>
    /// <param name="session">已打开的会话。</param>
    /// <param name="request">连接请求。</param>
    /// <param name="loc">文案表。</param>
    /// <param name="context">插件上下文。</param>
    public SqlWorkspaceDocument(
        SqlSession session,
        WorkspaceConnectRequest request,
        Loc loc,
        IPluginContext context)
    {
        _session = session;
        _request = request;
        _loc = loc;
        _context = context;
        _status = new(ProtocolSessionState.Connected, Describe(), (int)session.Metadata.Info.HandshakeMs);
    }

    /// <inheritdoc />
    public WorkspaceStatus Status => _status;

    /// <inheritdoc />
    public event EventHandler<WorkspaceStatus>? StatusChanged;

    /// <inheritdoc />
    public object CreateView()
    {
        if (_viewModel is null)
        {
            _viewModel = new(_session, _request, _loc, _context);
            // 对象树在视图建好之后才装载 —— 它要发好几条系统表查询,
            // 没人看的时候没必要往线上库发(§7.2 的"永不自动轮询"同源)。
            _ = _viewModel.InitializeAsync(_lifetime.Token);
        }
        // 状态探针在视图建好之后才起 —— 它只服务于界面上那个圆点,
        // 没人看的时候没必要往线上库发探活语句(§7.2 的"永不自动轮询"同源)。
        _probe ??= Task.Run(() => ProbeLoopAsync(_lifetime.Token));
        return new SqlWorkspaceView(_viewModel);
    }

    /// <inheritdoc />
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        // 重连期间会话本来就是 Faulted 的(那正是要重连的理由),文案说明正在做什么。
        Publish(new(ProtocolSessionState.Faulted, _loc["Sql_Reconnecting"]));
        try
        {
            await _session.Metadata.ReopenAsync(cancellationToken).ConfigureAwait(false);
            Publish(new(ProtocolSessionState.Connected, Describe(), (int)_session.Metadata.Info.HandshakeMs));
        }
        catch (Exception ex)
        {
            Publish(new(ProtocolSessionState.Faulted, ex.Message));
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_probe is { } probe)
        {
            try
            {
                await probe.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // 探针退出时的异常不该挡住文档关闭。
            }
        }
        _lifetime.Dispose();
        if (_viewModel is not null)
        {
            await _viewModel.DisposeAsync().ConfigureAwait(false);
        }
        await _session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 低频探活。<b>不看 <c>conn.State</c></b> —— 它在两个驱动上都是过期信息(§5.2),
    /// 必须真发一条语句才知道连接还活着。
    /// </summary>
    private async Task ProbeLoopAsync(CancellationToken cancellationToken)
    {
        var period = TimeSpan.FromSeconds(20);
        using var timer = new PeriodicTimer(period);
        while (await SafeWaitAsync(timer, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                int latency = await _session.Metadata.PingAsync(cancellationToken).ConfigureAwait(false);
                Publish(new(ProtocolSessionState.Connected, Describe(), latency));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _context.Log.Debug($"Probe failed for {_session.Metadata.Endpoint}: {ex.Message}");
                Publish(new(ProtocolSessionState.Faulted, ex.Message));
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Publish(WorkspaceStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>状态栏那一行字:版本 + 库 + 环境 —— 环境必须出现在这里,"我在动线上"不能被忽略。</summary>
    private string Describe()
    {
        string version = string.IsNullOrWhiteSpace(_session.Metadata.Info.ServerVersion)
            ? SqlDialects.Of(_session.Dialect).DisplayName
            : $"{SqlDialects.Of(_session.Dialect).DisplayName} {_session.Metadata.Info.ServerVersion}";
        string database = string.IsNullOrWhiteSpace(_session.Metadata.Info.DatabaseName) ? "" : $" · {_session.Metadata.Info.DatabaseName}";
        string environment = _session.Settings.Environment == SqlEnvironment.Development
            ? ""
            : $" · {_loc[$"Sql_Env{_session.Settings.Environment}"]}";
        string readOnly = _session.Settings.ReadOnly ? $" · {_loc["Sql_ReadOnlyBadge"]}" : "";
        return $"{version}{database}{environment}{readOnly}";
    }
}
