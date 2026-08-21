using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using VelaShell.Plugin.Sql.Metadata;

namespace VelaShell.Plugin.Sql.Ui;

/// <summary>运维面上的一行(会话或锁)。</summary>
/// <param name="Id">会话 id(杀会话要用它)。</param>
/// <param name="Columns">按方言包约定的列值。</param>
public sealed record SqlOpsRow(string Id, IReadOnlyList<string> Columns);

/// <summary>
/// 运维面:谁在跑、谁锁了我。
/// <para>
/// <b>"谁锁了我"是运维排障里问得最多的一句</b>,而它恰恰是 <c>IDbMaintenance</c> 完全没有的(§2.3)——
/// 会话、锁、阻塞链在每种方言里的系统视图都不一样,只能一份份写进方言包。
/// </para>
/// <para>
/// 这一页与终端并排才是它真正的用法(§8.2):看完 <c>pg_stat_activity</c> 转头就在同一个 dock 里
/// 敲 <c>iostat</c> —— 纯 GUI 工具在这里是断裂的。
/// </para>
/// </summary>
public sealed class SqlOpsTabViewModel : SqlTabViewModel
{
    private readonly SqlSession _session;
    private readonly Loc _loc;
    private string _status = "";
    private string _unsupported = "";

    internal SqlOpsTabViewModel(SqlSession session, Loc loc)
    {
        _session = session;
        _loc = loc;
        Title = loc["Sql_OpsTabTitle"];
        RefreshCommand = new(() => LoadAsync(CancellationToken.None));
        KillCommand = new(KillSelectedAsync, () => Selected is not null);
        // 方言不提供会话视图时**明说**,而不是给一张空表 ——
        // 空表与"现在没人连"长得一模一样(§7.8)。
        _unsupported = session.Pack.SessionListSql is null
            ? loc.Format("Sql_NoOpsForDialect", SqlDialects.Of(session.Dialect).DisplayName)
            : "";
    }

    /// <inheritdoc />
    public override string Title { get; }

    /// <summary>会话列头(按方言包契约固定)。</summary>
    public IReadOnlyList<string> SessionHeaders { get; } = ["id", "user", "host", "db", "state", "seconds", "query"];

    /// <summary>锁列头(按方言包契约固定)。</summary>
    public IReadOnlyList<string> LockHeaders { get; } = ["blocked", "blocking", "object", "mode", "query"];

    /// <summary>会话。</summary>
    public ObservableCollection<SqlOpsRow> Sessions { get; } = [];

    /// <summary>锁与阻塞链。</summary>
    public ObservableCollection<SqlOpsRow> Locks { get; } = [];

    /// <summary>选中的会话。</summary>
    public SqlOpsRow? Selected
    {
        get;
        set
        {
            field = value;
            KillCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>状态。</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>方言不支持时的说明;支持时为空。</summary>
    public string UnsupportedNotice
    {
        get => _unsupported;
        private set => SetProperty(ref _unsupported, value);
    }

    /// <summary>这个方言支持运维面吗。</summary>
    public bool IsSupported => string.IsNullOrEmpty(_unsupported);

    /// <summary>刷新。<b>手动刷新,永不自动轮询</b> —— 线上库的系统视图查询本身就有代价(§7.2)。</summary>
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>杀掉选中的会话。</summary>
    public AsyncRelayCommand KillCommand { get; }

    /// <summary>刷新按钮文案。</summary>
    public string RefreshLabel => _loc["Sql_RefreshLabel"];

    /// <summary>杀会话按钮文案。</summary>
    public string KillLabel => _loc["Sql_KillLabel"];

    /// <summary>装载。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return;
        }
        try
        {
            await FillAsync(Sessions, _session.Pack.SessionListSql, cancellationToken).ConfigureAwait(true);
            await FillAsync(Locks, _session.Pack.LockListSql, cancellationToken).ConfigureAwait(true);
            Status = _loc.Format("Sql_OpsLoaded", Sessions.Count, Locks.Count);
        }
        catch (Exception ex)
        {
            // 权限不足是这一页最常见的失败(会话视图基本都要额外权限)。
            // 如实报出来并说明可能要什么权限,比一张空表有用得多(§7.8)。
            Status = _loc.Format("Sql_OpsFailed", ex.Message);
        }
    }

    private async Task FillAsync(ObservableCollection<SqlOpsRow> into, string? sql, CancellationToken cancellationToken)
    {
        into.Clear();
        if (sql is null)
        {
            return;
        }
        // **必须走 UseAsync 排队**:运维面的两栏是并排刷新的,而对象树的展开又是即发即忘的 ——
        // 直接摸 Raw 就是在同一条元数据连接上并发发命令,Npgsql / MySqlConnector 会直接抛
        // "A command is already in progress"(见 SqlConnection._gate)。
        List<SqlOpsRow> rows = await _session.Metadata.UseAsync(async (raw, token) =>
        {
            List<SqlOpsRow> read = [];
            await using DbCommand command = raw.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _session.Settings.CommandTimeoutSeconds;
            await using DbDataReader reader = await command
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                List<string> values = [];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    values.Add(reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "");
                }
                read.Add(new(values.Count > 0 ? values[0] : "", values));
            }
            return read;
        }, cancellationToken).ConfigureAwait(true);
        foreach (SqlOpsRow row in rows)
        {
            into.Add(row);
        }
    }

    /// <summary>
    /// 杀掉一条会话。
    /// <para>
    /// <b>这是本插件里除写回之外唯一会影响别人的操作</b>,所以它要过确认 ——
    /// 而且文案要说清 <c>KILL</c> 杀的是**整条会话**不是一条语句(实测:被杀方之后不能再用,
    /// 必须重建连接)。
    /// </para>
    /// </summary>
    private async Task KillSelectedAsync()
    {
        if (Selected is not { } row || _session.Pack.CancelSessionSql(row.Id) is not { } sql)
        {
            return;
        }
        // 不杀自己 —— 那会把用户正在看的这条会话干掉,然后整个面板陷入"连接断了"。
        if (string.Equals(row.Id, _session.Metadata.SessionId, StringComparison.Ordinal))
        {
            Status = _loc["Sql_WontKillSelf"];
            return;
        }
        // **只读连接不许杀会话。** 同一个插件里改数据、发 DDL、跑写语句三条路都被只读拦住了
        // (`SqlGuard.Judge` 的 blocked、结构页的 DDL、网格写回),唯独这一条没有 ——
        // 而它是这里唯一会**影响到别人**的操作:SQL Server 上被 KILL 的那条会话,
        // 未提交事务会一并回滚。用户勾了"只读"就是在说"我这次不改任何东西",
        // 掐掉生产库上别人的会话显然不在那句话的范围内。
        if (_session.Settings.ReadOnly)
        {
            Status = _loc["Sql_WontKillReadOnly"];
            return;
        }
        try
        {
            await _session.Metadata.UseAsync(async (raw, token) =>
            {
                await using DbCommand command = raw.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 10;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }).ConfigureAwait(true);
            Status = _loc.Format("Sql_Killed", row.Id);
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = _loc.Format("Sql_KillFailed", row.Id, ex.Message);
        }
    }
}
