using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using VelaShell.Plugin.Sql.Metadata;

namespace VelaShell.Plugin.Sql.Execution;

/// <summary>
/// 执行一批语句。
/// <para>
/// <b>走裸 <c>DbCommand</c>,不走 SqlSugar</b>。三条理由,每条都是实测出来的:
/// <list type="number">
///   <item>取消要拿到那个 <c>DbCommand</c> 对象;</item>
///   <item>SqlSugar 的 <c>GetDataTableAsync</c> 是 <c>Task.Run</c> 套同步实现,
///         令牌只影响调度、任务一旦开跑就管不了(反编译确认);</item>
///   <item>用户手敲的 SQL 要**原样透传**,任何改写都是背叛。</item>
/// </list>
/// 代价是 AOP 不覆盖裸 <c>DbCommand</c>,所以审计要在这里自己埋点(§8.3)。
/// </para>
/// </summary>
/// <param name="dialect">方言。</param>
/// <param name="pack">方言包(取消语句、会话 id 从它来)。</param>
internal sealed class SqlExecutor(SqlDialect dialect, IDialectPack pack)
{
    private volatile DbCommand? _current;
    private volatile bool _running;

    /// <summary>当前是不是有语句在跑。</summary>
    public bool IsRunning => _running;

    /// <summary>取消阶梯走到了哪一档。</summary>
    public SqlCancelStage CancelStage { get; private set; }

    /// <summary>
    /// 逐条执行。**任一条失败即停** —— 但要注意失败的后果按方言不同:
    /// PG 把一批当隐式事务(第 2 条失败会把第 1 条一起回滚),MSSQL 不会(前面的已经提交)。
    /// 调用方要把这个差别显示给用户(§5.3)。
    /// </summary>
    /// <param name="connection">已打开的查询连接(**插件显式持有的那根**)。</param>
    /// <param name="statements">语句。</param>
    /// <param name="options">取数选项。</param>
    /// <param name="commandTimeoutSeconds">语句超时。</param>
    /// <param name="onSql">审计钩子:每条真正发出去的 SQL。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>逐条结果。</returns>
    public async Task<IReadOnlyList<SqlStatementResult>> ExecuteAsync(
        DbConnection connection,
        IReadOnlyList<SqlStatement> statements,
        SqlFetchOptions options,
        int commandTimeoutSeconds,
        Action<string>? onSql,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(options);

        // **必须跳到后台线程。**
        // 不是所有驱动的"异步"都是真异步:`Microsoft.Data.Sqlite` 的 ExecuteReaderAsync 是
        // **同步套壳**(实测:按 ADO.NET 门面取消一条递归 CTE 会跑满 144 秒,因为它压根没让出线程)。
        // 于是 `await executor.ExecuteAsync(...)` 在 UI 线程上调用时会**同步跑完整条查询** ——
        // 表现是整个窗口冻住,连"取消"按钮都点不到。
        // 一次线程池跳转换来的是"长查询期间界面还活着",这笔交易在任何驱动上都划算。
        return await Task.Run(async () =>
        {
            List<SqlStatementResult> results = [];
            foreach (SqlStatement statement in statements)
            {
                if (statement.IsEmpty)
                {
                    continue;
                }
                SqlStatementResult result = await ExecuteOneAsync(
                    connection, statement, options, commandTimeoutSeconds, onSql, cancellationToken)
                    .ConfigureAwait(false);
                results.Add(result);
                if (!result.Succeeded)
                {
                    break;
                }
            }
            return (IReadOnlyList<SqlStatementResult>)results;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按方言调整取数选项。
    /// <para>
    /// 目前只有一件事:**SQLite 上关掉分块读**。
    /// <c>Microsoft.Data.Sqlite</c> 的 <c>GetChars</c>/<c>GetBytes</c> 对**表达式列**
    /// (<c>EXPLAIN QUERY PLAN</c> 的每一列、<c>select 1+1</c>、任何函数或聚合结果)
    /// 会走进原生的 <c>sqlite3_table_column_metadata</c> 并触发 <c>0xC0000005</c> ——
    /// <b>访问冲突不是异常,它直接带走进程</b>。详见 <see cref="SqlFetchOptions.AllowChunkedReads" />。
    /// </para>
    /// </summary>
    /// <param name="options">原始选项。</param>
    /// <returns>调整后的选项。</returns>
    private SqlFetchOptions ForDialect(SqlFetchOptions options) =>
        dialect == SqlDialect.Sqlite ? options with { AllowChunkedReads = false } : options;

    private async Task<SqlStatementResult> ExecuteOneAsync(
        DbConnection connection,
        SqlStatement statement,
        SqlFetchOptions options,
        int commandTimeoutSeconds,
        Action<string>? onSql,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        // 审计只记一次:降级重跑发出去的 SQL 与第一次**逐字相同**,差别只在线路格式。
        // 记两次会让历史里出现一条莫名其妙的重复,反而像是我们偷偷执行了两遍。
        onSql?.Invoke(statement.Text);

        Attempt attempt = await RunOnceAsync(
            NewCommand(connection, statement, commandTimeoutSeconds), options, asText: false, cancellationToken)
            .ConfigureAwait(false);

        if (attempt.Error is not null && ShouldRefetchAsText(dialect, attempt.Error))
        {
            DbCommand retryCommand = NewCommand(connection, statement, commandTimeoutSeconds);
            if (TryFetchEverythingAsText(retryCommand))
            {
                // 只有降级**真的把数据取回来了**才采用它;降级本身失败时报第一次的错。
                // 理由:降级失败最常见的形态是 25P02(事务已中止,后续命令被忽略),
                // 那是二次现象 —— 把它报给用户,用户会以为问题出在事务上,而真正的原因是
                // "某一列没有二进制输出函数"。原错误才是问题的原貌。
                Attempt retry = await RunOnceAsync(retryCommand, options, asText: true, cancellationToken)
                    .ConfigureAwait(false);
                if (retry.Error is null)
                {
                    attempt = retry;
                }
            }
            else
            {
                // 驱动给不出这个开关(不是 Npgsql,或者它哪天改了名字)。
                // 再发一遍一模一样的命令毫无意义,只会白等一个超时 —— 直接放弃降级。
                await retryCommand.DisposeAsync().ConfigureAwait(false);
            }
        }

        stopwatch.Stop();
        if (attempt.Error is not null)
        {
            (int? line, int? column) = SqlErrorLocator.Locate(attempt.Error, statement, dialect);
            return new(statement, [], -1, stopwatch.ElapsedMilliseconds, attempt.Error, line, column);
        }
        return new(statement, attempt.Sets, attempt.Affected, stopwatch.ElapsedMilliseconds)
        {
            TextFallback = attempt.TextFallback
        };
    }

    /// <summary>一次执行尝试的产物(成功则 <see cref="Error" /> 为空)。</summary>
    /// <param name="Sets">结果集。</param>
    /// <param name="Affected">影响行数。</param>
    /// <param name="Error">失败原因。</param>
    /// <param name="TextFallback">这一次是不是走的全文本降级。</param>
    private readonly record struct Attempt(
        IReadOnlyList<SqlResultSet> Sets,
        int Affected,
        Exception? Error,
        bool TextFallback);

    /// <summary>
    /// 造一条命令。<b>用户手敲的 SQL 原样透传</b>,这里除了超时什么都不改。
    /// </summary>
    /// <param name="connection">连接。</param>
    /// <param name="statement">语句。</param>
    /// <param name="commandTimeoutSeconds">语句超时。</param>
    /// <returns>命令(调用方负责让 <see cref="RunOnceAsync" /> 或自己把它 Dispose 掉)。</returns>
    private static DbCommand NewCommand(DbConnection connection, SqlStatement statement, int commandTimeoutSeconds)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = statement.Text;
        command.CommandTimeout = commandTimeoutSeconds;
        return command;
    }

    /// <summary>
    /// 真正发一次命令,并<b>负责把它 Dispose 掉</b>。
    /// <para>
    /// <paramref name="asText" /> 只是个"这次是不是降级"的标记 —— 真正的开关由调用方
    /// 在建命令时用 <see cref="TryFetchEverythingAsText" /> 设好。分开是为了让"开关设不上"
    /// 这件事在调用方就看得见,而不是变成一个只能靠返回 <see langword="null" /> 表达的暗号。
    /// </para>
    /// </summary>
    /// <param name="command">已建好的命令。</param>
    /// <param name="options">取数选项。</param>
    /// <param name="asText">这一次是不是全文本降级。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>这一次的产物。</returns>
    private async Task<Attempt> RunOnceAsync(
        DbCommand command,
        SqlFetchOptions options,
        bool asText,
        CancellationToken cancellationToken)
    {
        _current = command;
        _running = true;
        CancelStage = SqlCancelStage.None;

        try
        {
            // SequentialAccess 是超长文本护栏的前提:默认 CommandBehavior 下 GetChars 也救不了,
            // 它照样先把整列缓冲(实测 200 行 × 1MB = +400MB 托管堆)。
            await using DbDataReader reader = await command
                .ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);

            List<SqlResultSet> sets = [];
            do
            {
                if (reader.FieldCount > 0)
                {
                    SqlResultSet set = await SqlResultReader.ReadAsync(
                        reader, ForDialect(options), cancellationToken).ConfigureAwait(false);
                    sets.Add(asText ? set with { TextFallback = true } : set);
                }
            }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

            return new Attempt(sets, reader.RecordsAffected, null, asText);
        }
        catch (Exception ex)
        {
            return new Attempt([], -1, ex, asText);
        }
        finally
        {
            _running = false;
            _current = null;
            // 读完之后 Dispose 命令是安全的 —— 这时已经没有在途操作了。
            // 有在途操作时**绝不能**碰连接的 Dispose(见 SqlCancellation 的说明)。
            await command.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 这条失败该不该**用全文本格式重跑一次**。
    /// <para>
    /// <b>真机现象</b>:PG 上 <c>select * from pg_class limit 20</c> 一行都拿不到,
    /// 报 <c>42883: no binary output function available for type aclitem</c>。
    /// 根因是 <c>pg_class.relacl</c> 的类型是 <c>aclitem[]</c>,驱动默认按**二进制格式**要数据,
    /// 而 <c>aclitem</c> 在服务端<b>只有文本表示、没有二进制输出函数</b>,于是服务端在吐第一行时就 ereport 了。
    /// 这个失败发生在"读某一格"<b>之下的一层</b>,所以 <see cref="SqlCell.FromError" /> 那条
    /// 单元格级容错完全接不住 —— 整个结果集一起没了。
    /// </para>
    /// <para>
    /// <b>判据是三段,缺一不可</b>:
    /// <list type="number">
    ///   <item>方言是 PostgreSQL。别的驱动没有这个开关,而它们的 42883 含义也不一样。</item>
    ///   <item>SQLSTATE 恰好是 <c>42883</c>(<c>undefined_function</c>)。走 <see cref="DbException.SqlState" />
    ///         这个 BCL 门面,不碰 <c>PostgresException</c>(§驱动隔离)。</item>
    ///   <item>再加一个"确实是二进制输出函数缺席"的证据:服务端 <c>Routine</c> 是
    ///         <c>getTypeBinaryOutputInfo</c> 或某个 <c>*_send</c>,<b>或者</b>消息里出现
    ///         <c>binary output function</c>。</item>
    /// </list>
    /// 第 ③ 段是这条判据的全部价值。<c>42883</c> 本身是**用户敲错函数名**的日常错误
    /// (<c>select nosuchfunction(1)</c> 就是它,实测 <c>Routine=ParseFuncOrColumn</c>),
    /// 只看 SQLSTATE 就重试等于把每一条打错字的语句都发两遍。反过来,
    /// <c>Routine</c> 是服务端 C 函数名、<b>永远不本地化</b>,而 <c>MessageText</c> 会被
    /// <c>lc_messages</c> 翻译 —— 两个证据取"或",在任何 <c>lc_messages</c> 下都还认得出来。
    /// </para>
    /// <para>
    /// <b>为什么重跑是安全的(实测,不是推理)</b>:这个错发生在服务端**输出结果行**的时候,
    /// 也就是语句已经开跑之后。所以"会不会把一条 DELETE 跑两遍"是个真问题。答案是不会 ——
    /// PG 在语句失败时把该语句的效果整体回滚:实测 5 行的表上跑
    /// <c>delete from t returning *</c>(带 aclitem 列)报错之后,表里<b>还是 5 行</b>;
    /// 显式事务里则整个事务转入 aborted,重跑会以 <c>25P02</c> 被打回而不是重复执行
    /// (这也正是上面"降级失败时报第一次的错"的由来)。
    /// </para>
    /// </summary>
    /// <param name="dialect">方言。</param>
    /// <param name="error">第一次失败的异常。</param>
    /// <returns>该不该降级重跑。</returns>
    internal static bool ShouldRefetchAsText(SqlDialect dialect, Exception error)
    {
        if (dialect != SqlDialect.PostgreSql || error is not DbException db)
        {
            return false;
        }
        if (!string.Equals(db.SqlState, "42883", StringComparison.Ordinal))
        {
            return false;
        }
        string? routine = ReadRoutine(db);
        return routine is not null
            ? string.Equals(routine, "getTypeBinaryOutputInfo", StringComparison.Ordinal)
              || routine.EndsWith("_send", StringComparison.Ordinal)
            : db.Message.Contains("binary output function", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 反射读服务端报的 <c>Routine</c>(报错那个 C 函数的名字)。
    /// <para>
    /// 它只在 <c>Npgsql.PostgresException</c> 上有,而执行层不许引用驱动类型 ——
    /// 与 <c>OraclePack.EnableLongFetch</c> 同一条路子:拿得到就用,拿不到就当没这回事。
    /// 拿不到时判据回落到消息匹配,而不是"宁可重试" —— 重试一条我们没看懂的失败才是真的危险。
    /// </para>
    /// </summary>
    /// <param name="error">异常。</param>
    /// <returns>函数名;拿不到为 <see langword="null" />。</returns>
    private static string? ReadRoutine(DbException error)
    {
        object? value = error.GetType()
            .GetProperty("Routine", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(error);
        return value is string { Length: > 0 } routine ? routine : null;
    }

    /// <summary>
    /// 让这条命令**整个结果集都按文本格式**取回来。
    /// <para>
    /// Npgsql 的开关是 <c>NpgsqlCommand.AllResultTypesAreUnknown</c>(还有个按列的
    /// <c>UnknownResultTypeList</c>,这里用不上:服务端的报错<b>不带列序号</b> ——
    /// 实测 <c>ColumnName</c> 是空的,我们根本不知道是哪一列惹的祸,只能整条降级)。
    /// 照纪律走反射,拿不到就返回 <see langword="false" />,由调用方放弃降级。
    /// </para>
    /// <para>
    /// <b>代价要说清楚</b>:降级之后驱动对每一列都报 <c>GetFieldType() == typeof(string)</c>,
    /// 于是 <see cref="SqlResultReader" /> 里那条 <c>type == typeof(byte[])</c> 的分支再也不会命中 ——
    /// <c>bytea</c> 列会以 PG 的文本形态 <c>\x0102…</c> 显示成**文本**,而不是网格里那个
    /// <c>0x…(1.2 KB)</c> 的二进制标记。NULL 与空串仍然分得开(<c>IsDBNull</c> 不受影响),
    /// 数字与时间的文本形态也是服务端给的、比 <c>ToString()</c> 更权威。
    /// 换句话说:丢的是"这一列是二进制"这个信息,换回来的是"整张系统表看得见"。
    /// 这笔交易只在**本来一行都拿不到**的时候才做,所以是划算的;
    /// 但用户得知道自己看的是降级结果,于是 <see cref="SqlResultSet.TextFallback" /> 会被打上标记,
    /// 文案见 <c>Sql_TextFallback</c>。
    /// </para>
    /// </summary>
    /// <param name="command">命令。</param>
    /// <returns>开关设上了没有。</returns>
    private static bool TryFetchEverythingAsText(DbCommand command)
    {
        PropertyInfo? property = command.GetType()
            .GetProperty("AllResultTypesAreUnknown", BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(bool) || !property.CanWrite)
        {
            return false;
        }
        property.SetValue(command, true);
        return true;
    }

    /// <summary>
    /// 取消当前正在跑的语句,走完整条阶梯。
    /// </summary>
    /// <param name="probeConnection">状态探针那根独立连接(旁路取消用)。</param>
    /// <param name="sessionId">当前查询连接的会话 id。</param>
    /// <param name="log">诊断输出。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最终档位;当时没有语句在跑则返回 <see cref="SqlCancelStage.None" />。</returns>
    public async Task<SqlCancelStage> CancelAsync(
        DbConnection? probeConnection,
        string sessionId,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        DbCommand? command = _current;
        if (command is null || !_running)
        {
            return SqlCancelStage.None;
        }
        var ladder = new SqlCancellation(dialect, pack);
        CancelStage = await ladder
            .EscalateAsync(command, probeConnection, sessionId, () => _running, log, cancellationToken)
            .ConfigureAwait(false);
        return CancelStage;
    }

    /// <summary>取当前连接的会话 id(旁路取消要用)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>会话 id;方言不支持时为空串。</returns>
    public async Task<string> ReadSessionIdAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (pack.SessionIdSql is not { } sql)
        {
            return "";
        }
        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 5;
            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value?.ToString() ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 拿不到会话 id 只是让旁路取消这一档失效,不该让连接失败。
            return "";
        }
    }
}
