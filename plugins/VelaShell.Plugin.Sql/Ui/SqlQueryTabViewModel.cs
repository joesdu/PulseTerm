using System.Collections.ObjectModel;
using System.Data.Common;
using VelaShell.Plugin.Sql.Execution;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.PluginSdk.Logging;

namespace VelaShell.Plugin.Sql.Ui;

/// <summary>确认框要问的一件事。</summary>
/// <param name="Title">标题。</param>
/// <param name="Message">正文。</param>
/// <param name="TypedName">要用户手打的对象名;不需要时为空。</param>
public sealed record SqlConfirmationRequest(string Title, string Message, string TypedName);

/// <summary>
/// 工作区里的一个标签。
/// <para>
/// 三种形态:查询(编辑器 + 结果网格)、结构(表的列/索引/外键/DDL)、运维(会话与锁)。
/// 它们共用同一条标签栏,因为用户在这三件事之间来回切是常态 ——
/// 看着表结构写查询、跑完查询去看谁锁了我。
/// </para>
/// </summary>
public abstract class SqlTabViewModel : ObservableObject
{
    /// <summary>标签标题。</summary>
    public abstract string Title { get; }
}

/// <summary>
/// 一个查询标签:编辑器 + 结果网格 + 一条独占的查询连接。
/// <para>
/// <b>独占连接是取消的前提</b>(§5.2):取消要拿到那个 <c>DbCommand</c>,
/// 共享连接上没法只取消其中一条。
/// </para>
/// </summary>
public sealed class SqlQueryTabViewModel : SqlTabViewModel, IAsyncDisposable
{
    private readonly SqlSession _session;
    private readonly Loc _loc;
    private readonly IPluginLogger _log;
    private SqlConnection? _connection;
    private SqlExecutor? _executor;
    private CancellationTokenSource? _running;

    private string _sql = "";
    private string _status = "";
    private bool _isBusy;
    private bool _isCancelling;
    private string _cancelHint = "";
    private string _errorText = "";
    private int? _errorLine;
    private int? _errorColumn;
    private SqlConfirmationRequest? _confirmation;
    private TaskCompletionSource<bool>? _confirmationAnswer;
    private SqlObject? _editTarget;
    private SqlTableSchema? _editSchema;
    private IReadOnlyList<string> _editKeyColumns = [];

    internal SqlQueryTabViewModel(SqlSession session, Loc loc, IPluginLogger log, string title, string catalog = "")
    {
        _session = session;
        _loc = loc;
        _log = log;
        Title = title;
        Catalog = catalog;
        Grid = new(loc);
        ExecuteCurrentCommand = new(() => RunAsync(currentOnly: true));
        ExecuteAllCommand = new(() => RunAsync(currentOnly: false));
        CancelCommand = new(CancelAsync, () => _isBusy && !_isCancelling);
        CommitEditsCommand = new(CommitEditsAsync, () => Grid.IsEditable && !_isBusy);
        RevertEditsCommand = new(() => Grid.RevertAll());
        ExplainCommand = new(ExplainAsync);
        ExportCsvCommand = new(() => ExportAsync(SqlExportFormat.Csv));
        ExportJsonCommand = new(() => ExportAsync(SqlExportFormat.Json));
        ExportInsertCommand = new(() => ExportAsync(SqlExportFormat.Insert));
        ConfirmCommand = new(() => AnswerConfirmation(true));
        RejectCommand = new(() => AnswerConfirmation(false));
    }

    /// <inheritdoc />
    public override string Title { get; }

    /// <summary>
    /// 这个标签跑在哪个库上;空表示连接串里那个。
    /// <para>
    /// <b>库这一级必须落在连接上,不能落在 SQL 里。</b> 在树上双击
    /// <c>ops_pg</c> → <c>public</c> → <c>orders</c> 生成的是 <c>SELECT * FROM "public"."orders"</c>,
    /// 两段限定名在 PG 上说不出"哪个库" —— 不带这一格,查询会跑在连接串里那个库上,
    /// 回来的是 42P01,而树上那张表明明就在那儿。
    /// </para>
    /// </summary>
    public string Catalog { get; }

    /// <summary>
    /// 这个标签是不是跑在**别的**库上(与连接落脚的那个不同)。
    /// <para>
    /// 相同时不画徽标:每个标签上都挂一个"位于 xxx"是噪声,而<b>不同</b>是必须说出来的事 ——
    /// 编辑器里那条 <c>SELECT * FROM "public"."orders"</c> 完全看不出它跑在哪个库上。
    /// </para>
    /// </summary>
    public bool HasOwnCatalog =>
        Catalog.Length > 0 && !string.Equals(Catalog, _session.DefaultCatalog, StringComparison.OrdinalIgnoreCase);

    /// <summary>库徽标文案。</summary>
    public string CatalogBadge => HasOwnCatalog ? _loc.Format("Sql_CatalogBadge", Catalog) : "";

    /// <summary>结果网格。</summary>
    public SqlGridViewModel Grid { get; }

    /// <summary>编辑器里的 SQL。</summary>
    public string Sql
    {
        get => _sql;
        set => SetProperty(ref _sql, value);
    }

    /// <summary>光标偏移(<c>Ctrl+Enter</c> 取当前语句要用)。</summary>
    public int CaretOffset { get; set; }

    /// <summary>底栏状态。</summary>
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>正在执行。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            SetProperty(ref _isBusy, value);
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 正在取消。**按下取消按钮后立刻变**,并在阶梯升级时更新 <see cref="CancelHint" /> ——
    /// 用户必须看得见"我们在做什么",而不是一个转不停的圈(§3.6)。
    /// </summary>
    public bool IsCancelling
    {
        get => _isCancelling;
        private set
        {
            SetProperty(ref _isCancelling, value);
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>取消进度文案。</summary>
    public string CancelHint
    {
        get => _cancelHint;
        private set => SetProperty(ref _cancelHint, value);
    }

    /// <summary>错误文本(空 = 没有错误)。</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            SetProperty(ref _errorText, value);
            RaisePropertyChanged(nameof(HasError));
        }
    }

    /// <summary>有没有错误。</summary>
    public bool HasError => !string.IsNullOrEmpty(_errorText);

    /// <summary>出错的行(1 起);拿不到时为 <see langword="null" />。</summary>
    public int? ErrorLine
    {
        get => _errorLine;
        private set
        {
            SetProperty(ref _errorLine, value);
            RaisePropertyChanged(nameof(ErrorLocationText));
            RaisePropertyChanged(nameof(HasErrorLocation));
        }
    }

    /// <summary>出错位置的人话形态("第 12 行" / "第 12 行第 8 列");拿不到位置时为空。</summary>
    public string ErrorLocationText => _errorLine is not { } line
        ? ""
        : _errorColumn is { } column
            ? _loc.Format("Sql_ErrorAtLineColumn", line, column)
            : _loc.Format("Sql_ErrorAtLine", line);

    /// <summary>有没有位置信息。SQLite 什么都没有,那时就只高亮整条语句,不瞎指一行。</summary>
    public bool HasErrorLocation => _errorLine is not null;

    /// <summary>确认框「确定」的文案。</summary>
    public string ConfirmLabel => _loc["Sql_ConfirmYes"];

    /// <summary>确认框「取消」的文案。</summary>
    public string CancelLabel => _loc["Sql_ConfirmNo"];

    /// <summary>待回答的确认框;<see langword="null" /> = 没有。</summary>
    public SqlConfirmationRequest? Confirmation
    {
        get => _confirmation;
        private set
        {
            SetProperty(ref _confirmation, value);
            RaisePropertyChanged(nameof(HasConfirmation));
        }
    }

    /// <summary>确认框开着没有。</summary>
    public bool HasConfirmation => _confirmation is not null;

    /// <summary>用户在确认框里手打的对象名。</summary>
    public string TypedConfirmation { get; set; } = "";

    /// <summary>执行光标所在语句。</summary>
    public AsyncRelayCommand ExecuteCurrentCommand { get; }

    /// <summary>执行全部。</summary>
    public AsyncRelayCommand ExecuteAllCommand { get; }

    /// <summary>取消。</summary>
    public AsyncRelayCommand CancelCommand { get; }

    /// <summary>提交网格里的改动(先给 SQL 预览)。</summary>
    public AsyncRelayCommand CommitEditsCommand { get; }

    /// <summary>撤销网格里的改动。</summary>
    public RelayCommand RevertEditsCommand { get; }

    /// <summary>提交按钮文案。</summary>
    public string CommitLabel => _loc["Sql_CommitLabel"];

    /// <summary>提交按钮悬浮提示。</summary>
    public string CommitTooltip => _loc["Sql_CommitTooltip"];

    /// <summary>撤销按钮文案。</summary>
    public string RevertLabel => _loc["Sql_RevertLabel"];

    /// <summary>CSV 导出按钮文案。</summary>
    public string ExportCsvLabel => _loc["Sql_ExportCsv"];

    /// <summary>JSON 导出按钮文案。</summary>
    public string ExportJsonLabel => _loc["Sql_ExportJson"];

    /// <summary>INSERT 导出按钮文案。</summary>
    public string ExportInsertLabel => _loc["Sql_ExportInsert"];

    /// <summary>出执行计划。</summary>
    public AsyncRelayCommand ExplainCommand { get; }

    /// <summary>执行计划按钮文案。</summary>
    public string ExplainLabel => _loc["Sql_ExplainLabel"];

    /// <summary>导出为 CSV。</summary>
    public AsyncRelayCommand ExportCsvCommand { get; }

    /// <summary>导出为 JSON。</summary>
    public AsyncRelayCommand ExportJsonCommand { get; }

    /// <summary>导出为 INSERT 脚本。</summary>
    public AsyncRelayCommand ExportInsertCommand { get; }

    /// <summary>
    /// 由视图注入的"另存为"对话框。视图模型不认识 <c>TopLevel</c>,
    /// 而文件选择器只有控件层拿得到 —— 用一个委托把这件事挡在视图那边。
    /// </summary>
    internal Func<string, string, Task<string?>>? SaveFilePicker { get; set; }

    /// <summary>确认框:确定。</summary>
    public RelayCommand ConfirmCommand { get; }

    /// <summary>确认框:取消。</summary>
    public RelayCommand RejectCommand { get; }

    /// <summary>执行历史(最近在前)。</summary>
    public ObservableCollection<string> History { get; } = [];

    /// <summary>
    /// 声明这个标签是"打开的某一张表",于是网格可以就地编辑。
    /// <para>
    /// **只有从对象树打开的表才可编辑** —— 自由查询的结果我们不知道该往哪张表写,
    /// 猜错的代价是把 UPDATE 打到别的表上。§7.5 说的"结果来自 JOIN/聚合则只读"就是这条的另一面。
    /// </para>
    /// </summary>
    /// <param name="target">目标表。</param>
    /// <param name="schema">表结构。</param>
    internal void BindEditTarget(SqlObject target, SqlTableSchema schema)
    {
        _editTarget = target;
        _editSchema = schema;
    }

    private void RefreshEditability()
    {
        IReadOnlyList<string> columns = [.. Grid.Columns.Select(c => c.Header)];
        SqlEditability editability = SqlWriteBack.Judge(_editSchema, columns);
        _editKeyColumns = editability.KeyColumns;
        Grid.IsEditable = editability.Editable && !_session.Settings.ReadOnly;
        Grid.ReadOnlyReason = editability.Editable
            ? (_session.Settings.ReadOnly ? _loc["Sql_GridReadOnlyConnection"] : "")
            : _loc[editability.ReasonKey];
        CommitEditsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 提交网格改动。**提交前必须给 SQL 预览** —— 这是本设计与"点了保存就发出去"的分界线:
    /// 改错一格的代价可能是一次事故,而看一眼要发的 SQL 只要一秒(§7.5)。
    /// </summary>
    /// <summary>
    /// 导出当前结果集。
    /// <para>
    /// 导出的是**原值**而不是界面上的装饰形态 —— 界面把 NULL 画成 <c>NULL</c>、空串画成 <c>''</c>
    /// 是为了让人分得清,而导出的东西要再被机器读,带着那两个记号就是脏数据(§7.3)。
    /// </para>
    /// </summary>
    /// <summary>
    /// 出当前语句的执行计划。
    /// <para>
    /// <b>只对绿档语句给「真跑」那一版。</b> <c>EXPLAIN ANALYZE</c> 会真的执行那条 SQL ——
    /// 对 <c>DELETE</c> 就是真删。所以这里先过一遍护栏(§7.6):绿档才允许 analyze,
    /// 其余一律只出静态计划。这条判断不能省,它是"看一眼计划"与"手滑删库"之间唯一的一道闸。
    /// </para>
    /// </summary>
    private async Task ExplainAsync()
    {
        ErrorText = "";
        if (SqlStatementSplitter.StatementAt(Sql, _session.Dialect, CaretOffset) is not { } statement)
        {
            Status = _loc["Sql_NothingToRun"];
            return;
        }

        SqlVerdict verdict = SqlGuard.Judge(
            statement.Text, _session.Settings.Environment, _session.Settings.ReadOnly, _session.Dialect);
        bool analyze = verdict.Risk == SqlRisk.Green;

        if (_session.Pack.ExplainSql(statement.Text, analyze) is not { } explain)
        {
            Status = _loc.Format("Sql_ExplainNotSupported", SqlDialects.Of(_session.Dialect).DisplayName);
            return;
        }

        IsBusy = true;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<SqlStatement> plan = SqlStatementSplitter.Split(explain, _session.Dialect);
        try
        {
            await EnsureConnectionAsync(CancellationToken.None).ConfigureAwait(true);
            IReadOnlyList<SqlStatementResult> results = await _executor!.ExecuteAsync(
                _connection!.Raw,
                plan,
                SqlFetchOptions.Default,
                _session.Settings.CommandTimeoutSeconds,
                null,
                CancellationToken.None).ConfigureAwait(true);
            stopwatch.Stop();
            await FinishPlanScriptAsync(plan, results.Count).ConfigureAwait(true);
            Present(results, stopwatch.ElapsedMilliseconds, 1);
            // 计划是只读的产物,不该让网格以为"这是一张能改的表"。
            Grid.IsEditable = false;
            if (!analyze)
            {
                Status = _loc["Sql_ExplainAnalyzeRefused"];
            }
            else if (PlanIsEstimateOnly(statement.Text))
            {
                // 护栏放行了 analyze,但**这个方言根本没有"真跑一遍"那一档** ——
                // 用户看到的每一个行数都是优化器的估算。不说的话,
                // 一个离谱的估算值会被当成"真的扫了这么多行"去做判断(§7.8)。
                Status = _loc["Sql_ExplainEstimateOnly"];
            }
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Status = _loc["Sql_Failed"];
            // **这里刻意不补发收尾语句。** 走到这个 catch 说明是连接级的失败(连接断了、被取消),
            // 而此时无从知道脚本跑到了第几条 —— 从头补发会把**用户那条语句真的执行一遍**,
            // 那正是「只看计划」这条路必须避免的事。收尾只在下面那条已知进度的路径上做。
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 这个方言的计划<b>只有估算值</b>吗。
    /// <para>
    /// 判据是方言无关的:<b>两档 <c>analyze</c> 生成的语句逐字相同</b>,就说明这个方言里
    /// 压根不存在"真跑一遍拿实际行数"那一档 —— SQLite 的 <c>EXPLAIN QUERY PLAN</c>
    /// 与 Oracle 的 <c>EXPLAIN PLAN</c> 都是如此(两家的方言包都验过:拿 <c>DELETE</c> 走一遍,
    /// 表里一行没少)。PG 与 SQL Server 的两档是不同语句,不会命中这里。
    /// </para>
    /// <para>
    /// 这样写而不是列一张"哪些方言只有估算"的表:表会和方言包各自的实现漂移,
    /// 而这个判据直接问方言包本人。
    /// </para>
    /// </summary>
    /// <param name="sql">用户那条语句。</param>
    /// <returns>只有估算值则为 <see langword="true" />。</returns>
    private bool PlanIsEstimateOnly(string sql) =>
        string.Equals(
            _session.Pack.ExplainSql(sql, analyze: true),
            _session.Pack.ExplainSql(sql, analyze: false),
            StringComparison.Ordinal);

    /// <summary>
    /// 把计划脚本没跑到的那几条<b>尽力补发一遍</b>。
    /// <para>
    /// <b>这不是补偿性的洁癖,是一个实测过的、会让后续查询全部返回错东西的状态。</b>
    /// SQL Server 的计划脚本是三条:<c>SET SHOWPLAN_ALL ON</c> / 用户语句 / <c>SET ... OFF</c>。
    /// 中间那条失败时(比如表名打错,Msg 208),执行器一条失败即停,<b>第三条 OFF 就发不出去了</b>——
    /// 而 <c>SET</c> 是<b>连接级</b>的,于是这条连接从此只出计划不出数据:
    /// 再发一条完全正常的 <c>select</c>,拿回来的是 <c>StmtText</c> / <c>EstimateRows</c> 那几列。
    /// </para>
    /// <para>
    /// 补发规则是**方言无关**的:脚本没跑完 ⇒ 尾巴上可能还有必须发出去的收尾语句。
    /// Oracle 的两段式计划里尾巴是一条 <c>SELECT</c>,补发它无害;
    /// PG / MySQL / SQLite 的计划只有一条,压根不会走到这里。
    /// </para>
    /// <para>
    /// <b>失败一律吞掉</b>:这一步是在给一个已经失败的操作收尾,
    /// 它自己再报错只会盖住用户真正需要看的那条错误消息。
    /// </para>
    /// </summary>
    /// <param name="plan">计划脚本切出来的语句。</param>
    /// <param name="completed">已经跑掉的条数。</param>
    /// <returns>表示异步操作的任务。</returns>
    private async Task FinishPlanScriptAsync(IReadOnlyList<SqlStatement> plan, int completed)
    {
        if (completed >= plan.Count || _connection is null)
        {
            return;
        }
        for (int i = completed; i < plan.Count; i++)
        {
            try
            {
                await using DbCommand command = _connection.Raw.CreateCommand();
                command.CommandText = plan[i].Text;
                command.CommandTimeout = 10;
                await command.ExecuteNonQueryAsync().ConfigureAwait(true);
            }
            catch (Exception)
            {
                // 见方法注释:收尾失败不该盖住用户要看的那条错误。
            }
        }
    }

    private async Task ExportAsync(SqlExportFormat format)
    {
        if (Grid.Rows.Count == 0)
        {
            Status = _loc["Sql_NothingToExport"];
            return;
        }
        if (SaveFilePicker is not { } picker)
        {
            Status = _loc["Sql_NoFilePicker"];
            return;
        }
        string suggested = $"{Title}{SqlExport.Extension(format)}";
        string? path = await picker(suggested, SqlExport.Extension(format)).ConfigureAwait(true);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            string content = SqlExport.Render(Grid, format, _session.Pack, _editTarget?.Name ?? "exported");
            await File.WriteAllTextAsync(path, content, SqlExport.EncodingFor(format)).ConfigureAwait(true);
            Status = _loc.Format("Sql_Exported", Grid.Rows.Count, path);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Status = _loc["Sql_Failed"];
        }
    }

    private async Task CommitEditsAsync()
    {
        if (_editTarget is null || _editSchema is null || !Grid.IsEditable)
        {
            return;
        }
        IReadOnlyList<SqlPendingEdit> edits = Grid.CollectEdits();
        if (edits.Count == 0)
        {
            Status = _loc["Sql_NoPendingEdits"];
            return;
        }

        IReadOnlyList<SqlWriteStatement> writes = SqlWriteBack.BuildUpdates(
            _session.Pack, _editTarget, _editSchema, _editKeyColumns, edits, Grid.OriginalValue);
        if (writes.Count == 0)
        {
            Status = _loc["Sql_NoPendingEdits"];
            return;
        }

        // 预览就是**真要发的那几条**(值内联版),不是另拼一份给人看的。
        string preview = string.Join(";" + System.Environment.NewLine, writes.Select(w => w.Preview));
        TypedConfirmation = "";
        _confirmationAnswer = new();
        Confirmation = new(
            _loc["Sql_CommitTitle"],
            _loc.Format("Sql_CommitMessage", writes.Count, preview),
            // 生产环境下改数据要手打表名 —— 与红档语句同一条护栏。
            _session.Settings.Environment == SqlEnvironment.Production ? _editTarget.Name : "");
        bool ok = await _confirmationAnswer.Task.ConfigureAwait(true);
        Confirmation = null;
        if (!ok)
        {
            Status = _loc["Sql_Cancelled"];
            return;
        }

        IsBusy = true;
        try
        {
            await EnsureConnectionAsync(CancellationToken.None).ConfigureAwait(true);
            IReadOnlyList<int> affected = await SqlWriteBack.ApplyAsync(
                _connection!.Raw, writes, _session.Settings.CommandTimeoutSeconds).ConfigureAwait(true);

            int total = affected.Sum();
            int stale = affected.Count(a => a == 0);
            Status = stale > 0
                // 影响 0 行 = 乐观并发拦下了:别人在你打开网格之后改过这一行。
                // 悄悄盖上去才是最坏的处理。
                ? _loc.Format("Sql_CommitStale", total, stale)
                : _loc.Format("Sql_CommitDone", total);
            if (stale == 0)
            {
                Grid.RevertAll();
                await RunAsync(currentOnly: false).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Status = _loc["Sql_Failed"];
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync(bool currentOnly)
    {
        ErrorText = "";
        _errorColumn = null;
        ErrorLine = null;

        IReadOnlyList<SqlStatement> statements = currentOnly
            ? SqlStatementSplitter.StatementAt(Sql, _session.Dialect, CaretOffset) is { } one ? [one] : []
            : SqlStatementSplitter.Split(Sql, _session.Dialect);
        if (statements.Count == 0)
        {
            Status = _loc["Sql_NothingToRun"];
            return;
        }

        // ── 护栏 ──
        (IReadOnlyList<SqlVerdict> each, SqlVerdict overall) = SqlGuard.JudgeBatch(
            statements, _session.Settings.Environment, _session.Settings.ReadOnly, _session.Dialect);

        if (overall.BlockedByReadOnly)
        {
            // 只读连接:在**发出之前**拒。不是靠数据库权限 —— 用户可能就是用 root 连的。
            SqlVerdict blocked = each.First(v => v.BlockedByReadOnly);
            ErrorText = _loc.Format("Sql_BlockedByReadOnly", blocked.Verb);
            Status = _loc["Sql_Blocked"];
            return;
        }
        if (overall.RequiresConfirmation && !await AskAsync(overall, statements.Count).ConfigureAwait(true))
        {
            Status = _loc["Sql_Cancelled"];
            return;
        }

        IsBusy = true;
        CancelHint = "";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _running = new();
        try
        {
            await EnsureConnectionAsync(_running.Token).ConfigureAwait(true);
            IReadOnlyList<SqlStatementResult> results = await _executor!.ExecuteAsync(
                _connection!.Raw,
                statements,
                SqlFetchOptions.Default,
                _session.Settings.CommandTimeoutSeconds,
                sql => History.Insert(0, sql),
                _running.Token).ConfigureAwait(true);

            stopwatch.Stop();
            Present(results, stopwatch.ElapsedMilliseconds, statements.Count);
            RefreshEditability();
        }
        catch (OperationCanceledException)
        {
            Status = _loc["Sql_Cancelled"];
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Status = _loc["Sql_Failed"];
        }
        finally
        {
            IsBusy = false;
            IsCancelling = false;
            _running?.Dispose();
            _running = null;
        }
    }

    private void Present(IReadOnlyList<SqlStatementResult> results, long elapsedMs, int requested)
    {
        SqlStatementResult? failed = results.FirstOrDefault(r => !r.Succeeded);
        SqlResultSet? lastSet = results
            .Where(r => r.Succeeded)
            .SelectMany(r => r.ResultSets)
            .LastOrDefault();

        if (lastSet is not null)
        {
            Grid.Load(lastSet, elapsedMs);
        }
        else if (failed is null)
        {
            int affected = results.Sum(r => Math.Max(r.RecordsAffected, 0));
            Grid.Clear(_loc.Format("Sql_AffectedRows", affected, elapsedMs));
        }

        if (failed is null)
        {
            Status = results.Count == 1
                ? Grid.Status
                : _loc.Format("Sql_BatchDone", results.Count, elapsedMs);
            return;
        }

        ErrorText = failed.Error?.Message ?? "";
        _errorColumn = failed.ErrorColumn;
        ErrorLine = failed.ErrorLine;
        int done = results.Count(r => r.Succeeded);

        // **多语句失败的后果按方言不同**(§5.3 实测):PG 把一批当隐式事务,
        // 第 2 条失败会把第 1 条一起回滚;MSSQL 不会,前面的已经提交。
        // 这个差别必须说给用户,否则他不知道数据现在是什么状态。
        string aftermath = _session.Dialect == SqlDialect.PostgreSql
            ? _loc["Sql_BatchRolledBack"]
            : _loc.Format("Sql_BatchPartiallyCommitted", done);
        Status = requested > 1
            ? _loc.Format("Sql_BatchFailed", done + 1, requested, aftermath)
            : _loc["Sql_Failed"];
    }

    private async Task<bool> AskAsync(SqlVerdict verdict, int statementCount)
    {
        string message = _loc.Format(
            verdict.Risk == SqlRisk.Red ? "Sql_ConfirmRed" : "Sql_ConfirmYellow",
            verdict.Verb,
            string.IsNullOrEmpty(verdict.TargetObject) ? "?" : verdict.TargetObject,
            statementCount,
            _loc[$"Sql_Env{_session.Settings.Environment}"]);

        TypedConfirmation = "";
        _confirmationAnswer = new();
        Confirmation = new(
            _loc[verdict.Risk == SqlRisk.Red ? "Sql_ConfirmTitleRed" : "Sql_ConfirmTitleYellow"],
            message,
            verdict.RequiresTypedName ? verdict.TargetObject : "");
        bool answer = await _confirmationAnswer.Task.ConfigureAwait(true);
        Confirmation = null;
        return answer;
    }

    private void AnswerConfirmation(bool accepted)
    {
        if (_confirmation is { TypedName.Length: > 0 } request
            && accepted
            && !string.Equals(TypedConfirmation.Trim(), request.TypedName, StringComparison.OrdinalIgnoreCase))
        {
            // 名字没打对就不算确认。这一档存在的意义就是"让手滑停下来"。
            return;
        }
        _confirmationAnswer?.TrySetResult(accepted);
    }

    private async Task CancelAsync()
    {
        if (_executor is null || !_executor.IsRunning)
        {
            return;
        }
        IsCancelling = true;
        CancelHint = _loc["Sql_CancelStageDriver"];

        SqlCancelStage stage = await _executor.CancelAsync(
            _session.ProbeConnection,
            _connection?.SessionId ?? "",
            hint =>
            {
                _log.Debug(hint);
                CancelHint = hint;
            }).ConfigureAwait(true);

        CancelHint = stage switch
        {
            SqlCancelStage.Bypass => _loc["Sql_CancelStageBypass"],
            // "已放弃该连接",不是初版那句"已断开该连接" —— 我们**没有**断开它:
            // Dispose 一条有在途命令的连接会挂死调用线程(§3.10),所以只是不再引用。
            SqlCancelStage.Abandoned => _loc["Sql_CancelStageAbandoned"],
            _ => _loc["Sql_CancelStageDone"]
        };

        if (stage == SqlCancelStage.Abandoned)
        {
            // 放弃 = 不再引用。下一次执行会开一根新的。
            _connection = null;
            _executor = null;
            _running?.Cancel();
        }
    }

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null && _executor is not null)
        {
            return;
        }
        _connection = await _session.OpenQueryConnectionAsync(Catalog, cancellationToken).ConfigureAwait(true);
        _executor = new(_session.Dialect, _session.Pack);
        // 会话 id 是旁路取消那一档的前提 —— 现在取,免得真要取消时再去查(那时连接正忙)。
        _connection.SessionId = await _executor
            .ReadSessionIdAsync(_connection.Raw, cancellationToken).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _running?.Cancel();
        _running?.Dispose();
        if (_connection is { } connection)
        {
            await _session.CloseQueryConnectionAsync(connection).ConfigureAwait(false);
            _connection = null;
        }
    }
}
