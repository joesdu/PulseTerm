using System.Data.Common;
using System.Globalization;
using VelaShell.Plugin.Sql.Execution;
using VelaShell.Plugin.Sql.Metadata;
using MsSqlConnection = Microsoft.Data.SqlClient.SqlConnection;
using MsSqlException = Microsoft.Data.SqlClient.SqlException;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// SQL Server 方言包的 <b>M4 资产真机验收</b>:执行计划(能力组 7)、运维面(能力组 8)、表设计器 DDL(能力组 5)。
/// <para>
/// 这一组的断言几乎全是"真发给服务端看它认不认",而不是比字符串 —— 理由与 MySQL / PG 两组同一条:
/// 方言资产最容易出的错是<b>语法合法、语义错误</b>,只比文本的测试对这一类完全无感。
/// 而 T-SQL 这一批里有五条<b>只有真机才证得了</b>,它们也正是本文件存在的理由:
/// ① <c>SET SHOWPLAN_ALL ON; …; OFF;</c> 当成一批发是 <c>Msg 1067</c>,必须靠调用方切句成三批;
/// ② 静态档<b>不执行</b>被解释的 <c>DELETE</c>,而 analyze 档<b>真的删</b>;
/// ③ 基类那两条通行 DDL(<c>ADD COLUMN</c> / 裸名 <c>DROP INDEX</c>)在 T-SQL 上<b>发不出去</b>;
/// ④ 权限不足时会话栏是"报错"而不是"静默只剩一行";
/// ⑤ 类型下拉里的每一项建完读回来是不是逐字相同(括号带不带、rowversion 改名这些只在往返里露馅)。
/// </para>
/// <para>
/// 真机是 SQL Server 2025 LocalDB 实例 <c>VelaSpike</c>(先 <c>sqllocaldb start VelaSpike</c>)。
/// 用本组自己的库 <c>ops_mssql</c>(没有就现建),不去动 <c>SqlServerPackTests</c> 的 <c>port_mssql</c>。
/// 连不上时逐个测试 <see cref="Assert.Inconclusive(string)" /> 跳过(仓库惯例)——
/// 装不了 LocalDB 的机器上不该因为缺一台服务器就把构建判红,但也绝不用替身冒充通过。
/// </para>
/// </summary>
[TestClass]
public sealed class SqlServerOpsTests
{
    /// <summary>本组专用的库。与别的用例分开,免得互相踩。</summary>
    private const string Database = "ops_mssql";

    /// <summary>被测对象。无状态,建一个用到底。</summary>
    private static readonly SqlServerPack Pack = new();

    private static string? _unavailableReason;

    /// <summary>MSTest 注入的上下文(取消令牌从它来)。</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>当前测试的取消令牌(runsettings 里的 60s 超时到点会触发它)。</summary>
    private CancellationToken Token => TestContext.CancellationTokenSource.Token;

    /// <summary>库不在就现建。连不上就记下原因,让每个测试跳过而不是整片报错。</summary>
    /// <param name="context">测试上下文。</param>
    /// <returns>等待句柄。</returns>
    [ClassInitialize]
    public static async Task ClassInitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await using var master = new MsSqlConnection(BuildConnectionString("master"));
            await master.OpenAsync(context.CancellationTokenSource.Token).ConfigureAwait(false);
            // 库名是本文件里的常量,不是用户输入;仍然走 QuoteIdentifier,免得将来有人改成变量。
            await ExecAsync(
                master,
                $"IF DB_ID('{Database}') IS NULL CREATE DATABASE {Pack.QuoteIdentifier(Database)};").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MsSqlException or InvalidOperationException or PlatformNotSupportedException)
        {
            _unavailableReason = $"连不上 SQL Server 2025 LocalDB 实例 VelaSpike(先跑 `sqllocaldb start VelaSpike`):{ex.Message}";
        }
    }

    // ═══════════════════════════ 执行计划(能力组 7) ═══════════════════════════

    /// <summary>
    /// <b>本文件最要紧的一条</b>:静态档返回的是<b>三条语句</b>,而且它非这样不可 ——
    /// 同一批发出去就是 <c>Msg 1067</c>,切成三批才跑得通。
    /// <para>
    /// 三段断言各对着一个失败模式:① 契约的返回值被谁改成一条了(那时 1067 会当场打回);
    /// ② 切句之后中间那条给不出计划(说明发出去的根本不是计划语句);
    /// ③ <c>OFF</c> 没起作用 —— 那样这条连接会一直只吐计划,后面所有查询都拿不到数据。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 执行计划_静态档必须切成三批_同一批发是Msg1067()
    {
        await using DbConnection connection = await OpenAsync().ConfigureAwait(false);
        await ResetPlanProbeAsync(connection).ConfigureAwait(false);

        const string Query = "select id, name from dbo.plan_probe where tag = N'x3'";
        string? explain = Pack.ExplainSql(Query, analyze: false);
        Assert.IsNotNull(explain, "SQL Server 是有执行计划的,这里不该是 null。");

        IReadOnlyList<SqlStatement> parts = SqlStatementSplitter.Split(explain, SqlDialect.SqlServer);
        Assert.AreEqual(3, parts.Count, $"契约这一格返回的是 ON / 用户语句 / OFF 三条,实际切出 {parts.Count} 条:\n{explain}");
        Assert.AreEqual("SET SHOWPLAN_ALL ON", parts[0].Text);
        Assert.AreEqual(Query, parts[1].Text, "中间那条必须是用户原文,一个字都不许改写。");
        Assert.AreEqual("SET SHOWPLAN_ALL OFF", parts[2].Text);

        // ① 整段当一批发 —— 这正是"为什么不能返回一条语句"的证据。
        MsSqlException batchError = await CaptureAsync(connection, explain).ConfigureAwait(false)
                                    ?? throw new AssertFailedException(
                                        "SET SHOWPLAN 与别的语句同批居然成功了 —— 那 ExplainSql 就该改回单条写法。");
        Assert.AreEqual(1067, batchError.Number,
            $"预期 Msg 1067(SET SHOWPLAN 必须独占一批),实际:{batchError.Number} {batchError.Message}");

        // ② 按调用方的做法逐条发:计划出得来,而且点得出命中的索引。
        List<(List<string> Headers, List<string[]> Rows)> grids =
            await RunStatementsAsync(connection, parts).ConfigureAwait(false);
        Assert.AreEqual(1, grids.Count, "只有中间那条会出结果集,两条 SET 不出。");
        Assert.AreEqual("StmtText", grids[0].Headers[0],
            $"SHOWPLAN_ALL 的第一列恒为 StmtText,实际:{string.Join(", ", grids[0].Headers)}");
        CollectionAssert.Contains(grids[0].Headers, "EstimateRows", "计划里没有估算行数这一列,那这一栏就白开了。");
        string plan = string.Join("\n", grids[0].Rows.Select(static r => r[0]));
        StringAssert.Contains(plan, "ix_plan_probe_tag",
            $"等值条件命中了索引,计划里必须点出索引名。实际:\n{plan}");
        Assert.IsFalse(
            grids[0].Headers.Contains("Rows", StringComparer.Ordinal),
            "静态档不该有 Rows/Executes 这两列 —— 有它们就说明这条真跑过了。");

        // ③ OFF 之后同一条连接立刻恢复取数(不恢复的话这条连接就"中毒"了,见 ExplainSql 注释)。
        Assert.AreEqual(5000L, await CountAsync(connection, "plan_probe").ConfigureAwait(false),
            "OFF 之后普通查询必须拿回数据而不是计划。");

        // 末尾分号要剥掉(编辑器切出来的语句常带终止符),否则会多切出一条空语句。
        string trimmed = Pack.ExplainSql("select 1;  ", analyze: false)!
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.AreEqual("SET SHOWPLAN_ALL ON;\nselect 1;\nSET SHOWPLAN_ALL OFF;", trimmed);
    }

    /// <summary>
    /// <b>静态档不执行被解释的语句 —— 这是"非绿档只给静态计划"那条护栏的地基。</b>
    /// <para>
    /// 这条要是哪天挂了(表里少了行),说明 SQL Server 改了 <c>SET SHOWPLAN_ALL</c> 的语义;
    /// 那时该改的是 <see cref="SqlServerPack.ExplainSql" /> 的档位选择,<b>而不是</b>把护栏往方言包这边挪。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 执行计划_静态档不执行被解释的DELETE()
    {
        await using DbConnection connection = await OpenAsync().ConfigureAwait(false);
        await ResetDangerAsync(connection).ConfigureAwait(false);
        Assert.AreEqual(10L, await CountAsync(connection, "danger").ConfigureAwait(false), "前置条件:10 行。");

        const string Delete = "delete from dbo.danger where id > 5";
        string explain = Pack.ExplainSql(Delete, analyze: false)!;
        List<(List<string> Headers, List<string[]> Rows)> grids = await RunStatementsAsync(
            connection, SqlStatementSplitter.Split(explain, SqlDialect.SqlServer)).ConfigureAwait(false);

        StringAssert.Contains(
            string.Join("\n", grids[0].Rows.Select(static r => r[0])), "Clustered Index Delete",
            "计划里得看得出这是一条删除。");
        Assert.AreEqual(10L, await CountAsync(connection, "danger").ConfigureAwait(false),
            "静态计划**不能**真的删 —— 这是绿档之外唯一能给用户看的那一档。");
    }

    /// <summary>
    /// <b>analyze 档真的把 <c>DELETE</c> 跑完 —— 契约把它标成危险开关不是虚张声势。</b>
    /// <para>
    /// 顺带钉住一条与静态档<b>相反</b>的实测差别:<c>SET STATISTICS PROFILE</c> <b>没有</b>
    /// "必须独占一批"的限制,整段当一批发也跑得通。两档的形状一样、约束不一样,
    /// 这条差别不写进测试的话,将来有人"顺手统一"就会把静态档改成同批而当场炸掉。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 执行计划_analyze档真的把DELETE跑完并给出实际行数()
    {
        await using DbConnection connection = await OpenAsync().ConfigureAwait(false);
        await ResetDangerAsync(connection).ConfigureAwait(false);

        const string Delete = "delete from dbo.danger where id > 5";
        string explain = Pack.ExplainSql(Delete, analyze: true)!;
        IReadOnlyList<SqlStatement> parts = SqlStatementSplitter.Split(explain, SqlDialect.SqlServer);
        Assert.AreEqual("SET STATISTICS PROFILE ON", parts[0].Text,
            "调用方要 analyze,拿到的就必须是 analyze —— 这里绝不静默降级成静态计划。");
        Assert.AreEqual("SET STATISTICS PROFILE OFF", parts[2].Text);

        List<(List<string> Headers, List<string[]> Rows)> grids =
            await RunStatementsAsync(connection, parts).ConfigureAwait(false);
        Assert.AreEqual("Rows", grids[0].Headers[0],
            $"analyze 档的头两列是 Rows / Executes(实际行数),实际:{string.Join(", ", grids[0].Headers)}");
        Assert.AreEqual("Executes", grids[0].Headers[1]);
        Assert.AreEqual("5", grids[0].Rows[0][0], "实际删掉的行数要出现在 Rows 这一列里。");
        Assert.AreEqual(5L, await CountAsync(connection, "danger").ConfigureAwait(false),
            "analyze 档就是真跑 —— 护栏必须留在调用方(§7.6)。");

        // 与静态档相反:这一档整段当一批发也不会撞 Msg 1067。
        await ResetDangerAsync(connection).ConfigureAwait(false);
        MsSqlException? sameBatch = await CaptureAsync(connection, explain).ConfigureAwait(false);
        Assert.IsNull(sameBatch,
            $"SET STATISTICS PROFILE 没有'必须独占一批'的限制,同批发不该报错:{sameBatch?.Number} {sameBatch?.Message}");
        Assert.AreEqual(5L, await CountAsync(connection, "danger").ConfigureAwait(false));
    }

    /// <summary>
    /// <b>把"中间那条失败会让连接停在只出计划"这个状态钉死,并证明补发 <c>OFF</c> 能治好它。</b>
    /// <para>
    /// 这条用例存在的理由是:它是 <see cref="SqlServerPack.ExplainSql" /> 那段注释里最吓人的一句,
    /// 也是调用方 <c>SqlQueryTabViewModel.FinishPlanScriptAsync</c>(脚本没跑完就补发尾巴)存在的全部原因。
    /// 两边各有一半 —— 方言包负责"<c>OFF</c> 是<b>独立可发</b>的一条",调用方负责"真的把它发出去" ——
    /// 少了哪一半这条路都不成立,所以这里连着验:先复现中毒,再按调用方的做法补发,看它恢复。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 执行计划_中间那条失败会让连接只出计划_补发OFF能治好()
    {
        await using DbConnection connection = await OpenAsync().ConfigureAwait(false);
        await ResetDangerAsync(connection).ConfigureAwait(false);

        IReadOnlyList<SqlStatement> parts = SqlStatementSplitter.Split(
            Pack.ExplainSql("select * from dbo.no_such_table", analyze: false)!, SqlDialect.SqlServer);

        await ExecAsync(connection, parts[0].Text).ConfigureAwait(false);
        MsSqlException missing = await CaptureAsync(connection, parts[1].Text).ConfigureAwait(false)
                                 ?? throw new AssertFailedException("表名是编的,这条必须失败。");
        Assert.AreEqual(208, missing.Number, $"预期 Msg 208(Invalid object name),实际:{missing.Number}");
        // 执行器一条失败即停,于是第三条 OFF **没发出去** —— 连接就停在这个状态里。

        (List<string> poisoned, _) = await ReadNamedGridAsync(connection, "SELECT COUNT_BIG(*) FROM dbo.danger")
            .ConfigureAwait(false);
        Assert.AreEqual("StmtText", poisoned[0],
            "中毒的连接会把普通查询也变成计划 —— 这一格要是变了,ExplainSql 那段警告就该重写。");

        // 调用方的收尾:把没跑到的那几条补发一遍。
        await ExecAsync(connection, parts[2].Text).ConfigureAwait(false);
        Assert.AreEqual(10L, await CountAsync(connection, "danger").ConfigureAwait(false),
            "补发 OFF 之后这条连接必须立刻恢复取数。");
    }

    // ═══════════════════════════ 运维面(能力组 8) ═══════════════════════════

    /// <summary>
    /// 会话列表:<b>列名与列序严格按契约</b>,而且<b>看得见自己</b>(<c>@@SPID</c>)。
    /// <para>
    /// 调用方是按序号读的,所以列序错了不会报错、只会把"主机"画到"数据库"那一格里 ——
    /// 这种错只有断言列名才拦得住。看得见自己则是这一栏的最低要求:
    /// 连自己都不在里面的会话表,用户根本没法判断它是"服务器很闲"还是"这条查询坏了"。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 会话列表_列序按契约_而且看得见自己()
    {
        await using DbConnection connection = await OpenAsync().ConfigureAwait(false);
        string spid = Convert.ToString(
            await ScalarAsync(connection, Pack.SessionIdSql!).ConfigureAwait(false),
            CultureInfo.InvariantCulture)!;

        (List<string> headers, List<string[]> rows) =
            await ReadNamedGridAsync(connection, Pack.SessionListSql!).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[] { "id", "user", "host", "db", "state", "seconds", "query" },
            headers,
            $"列名与列序是契约的一部分(调用方按序号读),实际:{string.Join(", ", headers)}");

        string[] mine = rows.Find(r => string.Equals(r[0], spid, StringComparison.Ordinal))
                        ?? throw new AssertFailedException(
                            $"会话列表里没有自己(@@SPID = {spid}),拿到的 id:{string.Join(", ", rows.Select(static r => r[0]))}");
        Assert.AreNotEqual("", mine[1], "登录名不该是空的。");
        Assert.AreEqual(Database, mine[3], "db 这一格该是当前库。");
        StringAssert.Contains(mine[4], "running", $"自己这一条正在跑,state 应当是 running,实际:{mine[4]}");
        Assert.IsTrue(
            double.TryParse(mine[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds >= 0,
            $"seconds 必须是个非负的数 —— 用 UTC 去减服务器本地时间会得到 -28800 那种值。实际:{mine[5]}");
        StringAssert.Contains(mine[6], "dm_exec_sessions",
            "query 这一格给的是**当前正在跑的那一条**,而此刻跑的正是会话列表自己这条 SQL。");
    }

    /// <summary>
    /// 会话栏<b>只列用户会话</b>:一台全空闲的 LocalDB 上系统会话占了绝大多数,
    /// 不过滤的话这一栏开箱就是一屏空格子(与 PG 那边的后台进程是同一个病)。
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 会话列表_系统会话不进来()
    {
        await using DbConnection connection = await OpenAsync().ConfigureAwait(false);
        (_, List<string[]> systemRows) = await ReadNamedGridAsync(
            connection, "SELECT session_id FROM sys.dm_exec_sessions WHERE is_user_process = 0")
            .ConfigureAwait(false);
        HashSet<string> systemIds = [.. systemRows.Select(static r => r[0])];
        Assert.IsTrue(systemIds.Count > 0, "这台实例上一条系统会话都没有?那这条用例证不了任何东西。");

        (_, List<string[]> rows) = await ReadNamedGridAsync(connection, Pack.SessionListSql!).ConfigureAwait(false);
        // 不比行数(两次查询之间随时可能有连接进出),比**集合**:系统会话一条都不许出现在这一栏里。
        string[] leaked = [.. rows.Select(static r => r[0]).Where(systemIds.Contains)];
        Assert.AreEqual(0, leaked.Length,
            $"会话栏里混进了 {leaked.Length} 条系统会话(id:{string.Join(", ", leaked)})—— 那一栏会变成一屏空格子。");
        Assert.IsTrue(rows.Count > 0, "过滤过头了:自己这条(用户会话)必须留下。");
    }

    /// <summary>
    /// <b>权限不足时这一栏必须报错,而不是静默地只剩一行。</b>
    /// <para>
    /// 这条用例把两件事一起钉住:① 光查 <c>sys.dm_exec_sessions</c> 是<b>不报错</b>的 ——
    /// 它悄悄退化成"只看得见你自己",而那会被读成"服务器上就一个人";
    /// ② 本包的会话 SQL 里那个 <c>sys.dm_exec_sql_text</c> 会把权限不足<b>喊出来</b>(Msg 371),
    /// 于是界面显示的是"权限不够"。所以那次 <c>OUTER APPLY</c> 除了取语句原文,还兼着权限探针。
    /// </para>
    /// <para>
    /// 降权用的是 <c>EXECUTE AS USER</c>(本机 LocalDB 只认 Windows 身份,建不了第二个登录)。
    /// 它是近似:服务器级权限检查确实按被模拟的身份走,但模拟本身还会引入"模块不受信任"一类的限制,
    /// 所以这条只断言"报错且提到 VIEW SERVER",不去咬死具体错号之外的措辞。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 会话列表_权限不足时报错而不是静默只剩一行()
    {
        await using DbConnection connection = await OpenAsync().ConfigureAwait(false);
        await ExecAsync(connection, "IF USER_ID('vela_lowpriv') IS NULL CREATE USER vela_lowpriv WITHOUT LOGIN;")
            .ConfigureAwait(false);
        try
        {
            await ExecAsync(connection, "EXECUTE AS USER = 'vela_lowpriv';").ConfigureAwait(false);

            long visible = Convert.ToInt64(
                await ScalarAsync(connection, "SELECT COUNT(*) FROM sys.dm_exec_sessions").ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            Assert.AreEqual(1L, visible,
                "没有 VIEW SERVER STATE 时 dm_exec_sessions **不报错**,只返回自己那一行 —— 这正是不能只靠它的原因。");

            MsSqlException denied = await CaptureAsync(connection, Pack.SessionListSql!).ConfigureAwait(false)
                                   ?? throw new AssertFailedException(
                                       "降权之后会话列表居然跑通了 —— 那这一栏就会把'只有一条会话'当成真相显示出去。");
            StringAssert.Contains(denied.Message, "VIEW SERVER",
                $"报错要说得出缺的是哪一项权限,实际:Msg {denied.Number} {denied.Message}");
        }
        finally
        {
            await ExecAsync(connection, "REVERT;").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 锁与阻塞链:造一次真阻塞,看这一栏认不认得出<b>谁被谁挡住</b>。
    /// <para>
    /// 四格断言各有各的失败模式:两个 id 错了就点不开对应会话(也杀不掉);
    /// <c>object</c> 空着说明 hobt_id 那条解析路断了(行锁的关联实体<b>不是</b> object_id);
    /// <c>mode</c> 只剩一边说明持有方那半条子查询没配上;
    /// <c>query</c> 给成持锁方的语句则会让整行的主语错乱。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 锁列表_真机阻塞链_指得出谁挡着谁()
    {
        await using DbConnection observer = await OpenAsync().ConfigureAwait(false);
        await ExecAsync(observer, "IF OBJECT_ID('dbo.lock_probe') IS NOT NULL DROP TABLE dbo.lock_probe;")
            .ConfigureAwait(false);
        await ExecAsync(observer, "CREATE TABLE dbo.lock_probe(id int NOT NULL PRIMARY KEY, v int NOT NULL);")
            .ConfigureAwait(false);
        await ExecAsync(observer, "INSERT INTO dbo.lock_probe(id, v) VALUES (1, 0);").ConfigureAwait(false);

        (List<string> headers, string[] row, string holderSpid, string waiterSpid) =
            await WithBlockingAsync(observer).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            new[] { "blocked_id", "blocking_id", "object", "mode", "query" },
            headers,
            $"列名与列序是契约的一部分,实际:{string.Join(", ", headers)}");
        Assert.AreEqual(waiterSpid, row[0], "第一列是**被阻塞方** —— 这一行的主语。");
        Assert.AreEqual(holderSpid, row[1], "第二列是挡住它的那条会话,它必须能拿去会话栏里找、拿去 KILL。");
        Assert.AreEqual("dbo.lock_probe", row[2],
            $"行锁的关联实体是 hobt_id,要绕 sys.partitions 才换得到表名。实际:{row[2]}");
        StringAssert.StartsWith(row[3], "KEY ", $"mode 前面要带资源类型(表锁与行锁的排障方向完全不同)。实际:{row[3]}");
        StringAssert.Contains(row[3], "<-", $"只给一边说不清冲突,持有方那一半必须并进来。实际:{row[3]}");
        StringAssert.Contains(row[4], "UPDATE", $"query 给的是**被阻塞方**的语句。实际:{row[4]}");
    }

    /// <summary>没人争锁时锁栏干净地返回 0 行 —— 空表要意味着"真的没有阻塞",而不是"这条 SQL 没跑通"。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 锁列表_没有阻塞时是空表而不是报错()
    {
        await using DbConnection connection = await OpenAsync().ConfigureAwait(false);
        (List<string> headers, List<string[]> rows) =
            await ReadNamedGridAsync(connection, Pack.LockListSql!).ConfigureAwait(false);
        Assert.AreEqual(5, headers.Count, "空表也要有正确的列形状(网格是按它画表头的)。");
        Assert.AreEqual(0, rows.Count,
            $"这一刻没人在争锁,却查出了 {rows.Count} 行 —— 多半是把'在等待'当成了'被阻塞'。");
    }

    // ═══════════════════════════ 表设计器(能力组 5) ═══════════════════════════

    /// <summary>
    /// 加列 / 删列 / 建索引 / 删索引各走一轮真机,<b>并且证明基类那两条通行写法在 T-SQL 上发不出去</b>。
    /// <para>
    /// 场地刻意用<b>自定义 schema + 名字里带结束定界符的表</b>(<c>ops.[Odd]]Table]</c>):
    /// 那是"不转义就必炸"的组合,四条 DDL 里任何一条漏了 <c>QuoteQualified</c> 都会当场语法错。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 表设计器_四条DDL真机各一轮_基类写法在T_SQL上发不出去()
    {
        await using DbConnection connection = await OpenAsync().ConfigureAwait(false);
        await ResetDesignSchemaAsync(connection).ConfigureAwait(false);
        var target = new SqlObject(SqlObjectKind.Table, "Odd]Table", "ops");

        // ── 加列:基类的 ADD COLUMN 是 Msg 156,本包的 ADD 才发得出去 ──
        string generic = $"ALTER TABLE {Pack.QuoteQualified(target)} ADD COLUMN [qty] int NULL";
        MsSqlException genericError = await CaptureAsync(connection, generic).ConfigureAwait(false)
                                     ?? throw new AssertFailedException(
                                         "T-SQL 居然接受了 ADD COLUMN —— 那 AddColumnDdl 的覆盖就该撤掉。");
        Assert.AreEqual(156, genericError.Number,
            $"预期 Msg 156(Incorrect syntax near the keyword 'COLUMN'),实际:{genericError.Number} {genericError.Message}");

        string? add = Pack.AddColumnDdl(target, new("qty", 4, "int", IsNullable: true));
        Assert.AreEqual("ALTER TABLE [ops].[Odd]]Table] ADD [qty] int NULL", add,
            "加列不带 COLUMN 关键字,可空性显式写出来,标识符两段都转义。");
        await ExecAsync(connection, add!).ConfigureAwait(false);

        SqlTableSchema afterAdd = await Pack.DescribeAsync(connection, target, Token).ConfigureAwait(false);
        SqlColumn added = afterAdd.Columns.Single(c => c.Name == "qty");
        Assert.AreEqual("int", added.DataType);
        Assert.IsTrue(added.IsNullable, "显式 NULL 就该落成可空(省略它的话结果由两个看不见的设置决定)。");

        // ── 建索引:基类的通行写法在 T-SQL 上逐字成立;但索引名不能加 schema 限定 ──
        string? create = Pack.CreateIndexDdl(target, "ix_ab", ["a", "b"], unique: false);
        Assert.AreEqual("CREATE INDEX [ix_ab] ON [ops].[Odd]]Table] ([a], [b])", create);
        await ExecAsync(connection, create!).ConfigureAwait(false);
        await ExecAsync(connection, Pack.CreateIndexDdl(target, "ux_a", ["a"], unique: true)!).ConfigureAwait(false);

        MsSqlException qualifiedError = await CaptureAsync(
            connection, $"CREATE INDEX [ops].[ix_bad] ON {Pack.QuoteQualified(target)} ([a])").ConfigureAwait(false)
            ?? throw new AssertFailedException("索引名居然能加 schema 限定 —— 那删索引那条的前提就得重写。");
        Assert.AreEqual(102, qualifiedError.Number,
            $"预期 Msg 102(Incorrect syntax near '.'),实际:{qualifiedError.Number} {qualifiedError.Message}");

        SqlTableSchema afterIndex = await Pack.DescribeAsync(connection, target, Token).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { "a", "b" }, afterIndex.Indexes.Single(i => i.Name == "ix_ab").Columns.ToArray());
        Assert.IsTrue(afterIndex.Indexes.Single(i => i.Name == "ux_a").IsUnique);

        // ── 删索引:基类的裸名写法是 Msg 159,本包的 DROP INDEX ix ON t 才发得出去 ──
        MsSqlException bareError = await CaptureAsync(connection, "DROP INDEX [ix_ab]").ConfigureAwait(false)
                                  ?? throw new AssertFailedException(
                                      "裸名 DROP INDEX 居然成功了 —— 那 DropIndexDdl 的覆盖就该撤掉。");
        Assert.AreEqual(159, bareError.Number,
            $"预期 Msg 159(Must specify the table name and index name),实际:{bareError.Number} {bareError.Message}");

        string? drop = Pack.DropIndexDdl(target, "ix_ab");
        Assert.AreEqual("DROP INDEX [ix_ab] ON [ops].[Odd]]Table]", drop);
        await ExecAsync(connection, drop!).ConfigureAwait(false);
        await ExecAsync(connection, Pack.DropIndexDdl(target, "ux_a")!).ConfigureAwait(false);

        SqlTableSchema afterDropIndex = await Pack.DescribeAsync(connection, target, Token).ConfigureAwait(false);
        Assert.IsFalse(
            afterDropIndex.Indexes.Any(i => i.Name is "ix_ab" or "ux_a"),
            "两个索引都该没了,剩下的只有主键那个。");

        // ── 删列:基类的写法在 T-SQL 上逐字成立(删列反过来必须带 COLUMN) ──
        string? dropColumn = Pack.DropColumnDdl(target, "qty");
        Assert.AreEqual("ALTER TABLE [ops].[Odd]]Table] DROP COLUMN [qty]", dropColumn);
        await ExecAsync(connection, dropColumn!).ConfigureAwait(false);

        SqlTableSchema afterDropColumn = await Pack.DescribeAsync(connection, target, Token).ConfigureAwait(false);
        Assert.IsFalse(afterDropColumn.Columns.Any(c => c.Name == "qty"), "qty 该被删掉了。");
    }

    /// <summary>
    /// <b>"加一列带默认值,再删掉它"在 SQL Server 上走不通</b> —— 而那条默认值约束正是本包自己加的。
    /// <para>
    /// 这条用例不是为了证明 DDL 有 bug,是为了把这个<b>往返缺口</b>钉在测试里:
    /// 契约还没有"删约束"那一格,表设计器现在只能把 Msg 5074 原样报出来。
    /// 等那一格开出来,这条断言要跟着改成"先删约束再删列"。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 表设计器_带默认值的列删不掉_Msg5074()
    {
        await using DbConnection connection = await OpenAsync().ConfigureAwait(false);
        await ResetDesignSchemaAsync(connection).ConfigureAwait(false);
        var target = new SqlObject(SqlObjectKind.Table, "Odd]Table", "ops");

        string? add = Pack.AddColumnDdl(target, new("qty", 4, "int", IsNullable: false, DefaultValue: "0"));
        Assert.AreEqual("ALTER TABLE [ops].[Odd]]Table] ADD [qty] int NOT NULL DEFAULT 0", add);
        await ExecAsync(connection, add!).ConfigureAwait(false);

        // 默认值往返闭合:目录里存的是 ((0)),读回来剥成 0,原样再拼一次仍然成立。
        SqlTableSchema schema = await Pack.DescribeAsync(connection, target, Token).ConfigureAwait(false);
        Assert.AreEqual("0", schema.Columns.Single(c => c.Name == "qty").DefaultValue);

        MsSqlException blocked = await CaptureAsync(connection, Pack.DropColumnDdl(target, "qty")!).ConfigureAwait(false)
                                ?? throw new AssertFailedException(
                                    "带默认值的列居然直接删掉了 —— 那注释里那条'先删约束'的提示就该撤掉。");
        Assert.AreEqual(5074, blocked.Number,
            $"预期 Msg 5074(The object 'DF__…' is dependent on column),实际:{blocked.Number} {blocked.Message}");
        StringAssert.Contains(blocked.Message, "DF__", "报错里会点名那条自动命名的默认值约束,提示文案要能引用它。");
    }

    /// <summary>
    /// 列定义里说了、而这条 DDL 表达不了的四样,一律返回 <see langword="null" />。
    /// <para>静默办成一个普通列比报错坏得多 —— 用户点的"加一个计算列"办成了别的事,而且哪儿都不提示。</para>
    /// </summary>
    [TestMethod]
    public void 表设计器_四样表达不了的列一律返回null()
    {
        var target = new SqlObject(SqlObjectKind.Table, "t", "dbo");
        Assert.IsNull(Pack.AddColumnDdl(target, new("c", 1, "int", true, IsGenerated: true)), "计算列拼不出 AS (表达式)。");
        Assert.IsNull(Pack.AddColumnDdl(target, new("c", 1, "int", false, IsPrimaryKey: true)), "拼不出 PRIMARY KEY。");
        Assert.IsNull(Pack.AddColumnDdl(target, new("c", 1, "int", false, IsAutoIncrement: true)), "拼不出 IDENTITY(1,1)。");
        Assert.IsNull(
            Pack.AddColumnDdl(target, new("c", 1, "int", true, Comment: "说明")),
            "T-SQL 的列注释是另一条 sp_addextendedproperty,而契约这一格只返回一条 DDL。");
        // 说得出口的那一种照常给。
        Assert.AreEqual(
            "ALTER TABLE [dbo].[t] ADD [c] int NULL",
            Pack.AddColumnDdl(target, new("c", 1, "int", true)));
    }

    /// <summary>
    /// <b>类型下拉里的每一项,建出来再读回来必须是逐字相同的那个字符串。</b>
    /// <para>
    /// 这条是整份类型表的立身之本:下拉里挑 <c>datetime2</c>、加完列一刷新变成 <c>datetime2(7)</c>,
    /// 用户会以为插件把他的类型改了。别名、语法糖、默认精度、<c>rowversion</c> 改名成 <c>timestamp</c> ——
    /// 这一类全都只在往返里露馅,离线比字符串是看不出来的。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task 类型下拉_每一项建出来读回来逐字相同()
    {
        await using DbConnection connection = await OpenAsync().ConfigureAwait(false);
        await ExecAsync(connection, "IF OBJECT_ID('dbo.type_probe') IS NOT NULL DROP TABLE dbo.type_probe;")
            .ConfigureAwait(false);
        await ExecAsync(connection, "CREATE TABLE dbo.type_probe(id int NOT NULL PRIMARY KEY);").ConfigureAwait(false);

        var target = new SqlObject(SqlObjectKind.Table, "type_probe", "dbo");
        IReadOnlyList<string> types = Pack.CommonTypes;
        Assert.IsTrue(types.Count > 20, $"类型表只有 {types.Count} 项,不像是一份能用的下拉。");

        Dictionary<string, string> expected = [];
        for (int i = 0; i < types.Count; i++)
        {
            string column = $"c{i.ToString(CultureInfo.InvariantCulture)}";
            expected[column] = types[i];
            // 走 AddColumnDdl 而不是自己拼:要验的是"下拉里的类型能不能被本包的加列 DDL 用掉"。
            string? ddl = Pack.AddColumnDdl(target, new(column, i + 2, types[i], IsNullable: true));
            Assert.IsNotNull(ddl, $"类型 {types[i]} 拼不出加列 DDL。");
            await ExecAsync(connection, ddl).ConfigureAwait(false);
        }

        SqlTableSchema schema = await Pack.DescribeAsync(connection, target, Token).ConfigureAwait(false);
        foreach (SqlColumn column in schema.Columns.Where(c => expected.ContainsKey(c.Name)))
        {
            Assert.AreEqual(
                expected[column.Name], column.DataType,
                $"下拉里挑的是 {expected[column.Name]},读回来却是 {column.DataType} —— 用户会以为插件改了他的类型。");
        }
        Assert.AreEqual(types.Count, schema.Columns.Count(c => expected.ContainsKey(c.Name)), "有类型没落成列。");

        // 反面:rowversion 建得成,但读回来叫 timestamp —— 这正是它不进下拉的原因。
        await ExecAsync(connection, "ALTER TABLE dbo.type_probe ADD rv rowversion;").ConfigureAwait(false);
        SqlTableSchema withRowVersion = await Pack.DescribeAsync(connection, target, Token).ConfigureAwait(false);
        Assert.AreEqual("timestamp", withRowVersion.Columns.Single(c => c.Name == "rv").DataType);
        CollectionAssert.DoesNotContain(types.ToArray(), "rowversion", "往返不闭合的类型不该进下拉。");
    }

    // ═══════════════════════════ 脚手架 ═══════════════════════════

    /// <summary>本组的连接串。与 <c>SqlServerPackTests</c> 同一条口径,只是库不同。</summary>
    /// <param name="database">库名。</param>
    /// <returns>连接串。</returns>
    private static string BuildConnectionString(string database) =>
        $@"Server=(localdb)\VelaSpike;Integrated Security=true;Database={database};TrustServerCertificate=true;ConnectRetryCount=0";

    /// <summary>开一条连到 <see cref="Database" /> 的连接;真机不在就跳过当前测试。</summary>
    /// <returns>已打开的连接。</returns>
    private static async Task<DbConnection> OpenAsync()
    {
        if (_unavailableReason is not null)
        {
            Assert.Inconclusive(_unavailableReason);
        }
        var connection = new MsSqlConnection(BuildConnectionString(Database));
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// 造一次真阻塞,把锁栏里那一行取回来。
    /// <para>
    /// 持锁方开着事务改一行不提交,等待方改同一行 —— 这是最干净的一种行锁冲突(<c>KEY</c> 锁)。
    /// 等待方的语句<b>不能 await</b>:它就是要挂在那儿,await 了这条用例就一起挂了。
    /// </para>
    /// </summary>
    /// <param name="observer">观察用的连接。</param>
    /// <returns>列名、那一行、持锁方与等待方的 spid。</returns>
    private static async Task<(List<string> Headers, string[] Row, string Holder, string Waiter)> WithBlockingAsync(
        DbConnection observer)
    {
        await using DbConnection holder = await OpenAsync().ConfigureAwait(false);
        await using DbConnection waiter = await OpenAsync().ConfigureAwait(false);
        string holderSpid = Convert.ToString(
            await ScalarAsync(holder, "SELECT @@SPID").ConfigureAwait(false), CultureInfo.InvariantCulture)!;
        string waiterSpid = Convert.ToString(
            await ScalarAsync(waiter, "SELECT @@SPID").ConfigureAwait(false), CultureInfo.InvariantCulture)!;

        await ExecAsync(holder, "BEGIN TRAN; UPDATE dbo.lock_probe SET v = v + 1 WHERE id = 1;").ConfigureAwait(false);
        Task blocked = ExecAsync(waiter, "UPDATE dbo.lock_probe SET v = v + 100 WHERE id = 1;");
        try
        {
            (List<string> headers, string[]? row) = await PollForLockAsync(observer, waiterSpid).ConfigureAwait(false);
            return (headers,
                    row ?? throw new AssertFailedException(
                        $"等了几秒也没在锁栏里看见 {waiterSpid} 被挡住 —— 阻塞链那条 SQL 没认出这次冲突。"),
                    holderSpid,
                    waiterSpid);
        }
        finally
        {
            await ExecAsync(holder, "ROLLBACK;").ConfigureAwait(false);
            await blocked.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 反复查锁栏,直到看见 <paramref name="blockedId" /> 那条等待为止。
    /// <para>锁等待是<b>异步</b>出现的:发出 UPDATE 到它真的挂在服务端之间有一小段,查一次十有八九是空的。</para>
    /// </summary>
    /// <param name="connection">观察用的连接。</param>
    /// <param name="blockedId">被阻塞方的 spid。</param>
    /// <returns>列名与那一行(等不到则行为 <see langword="null" />)。</returns>
    private static async Task<(List<string> Headers, string[]? Row)> PollForLockAsync(
        DbConnection connection, string blockedId)
    {
        List<string> headers = [];
        for (int i = 0; i < 100; i++)
        {
            (List<string> current, List<string[]> rows) =
                await ReadNamedGridAsync(connection, Pack.LockListSql!).ConfigureAwait(false);
            headers = current;
            string[]? row = rows.Find(r => string.Equals(r[0], blockedId, StringComparison.Ordinal));
            if (row is not null)
            {
                return (headers, row);
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        return (headers, null);
    }

    /// <summary>重建计划用例的场地(5000 行、一个可命中的索引)。</summary>
    /// <param name="connection">连接。</param>
    /// <returns>等待句柄。</returns>
    private static async Task ResetPlanProbeAsync(DbConnection connection)
    {
        await ExecAsync(connection, "IF OBJECT_ID('dbo.plan_probe') IS NOT NULL DROP TABLE dbo.plan_probe;")
            .ConfigureAwait(false);
        await ExecAsync(connection, """
            CREATE TABLE dbo.plan_probe(
                id   int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                tag  nvarchar(20) NULL,
                name nvarchar(50) NULL);
            """).ConfigureAwait(false);
        // 标签要**足够散**:命中的行多了优化器会改走全表扫描,那条"计划里点得出索引名"的断言
        // 就会被数据分布决定而不是被实现决定。700 个取值 ≈ 每个标签 7 行,索引查找稳赢。
        await ExecAsync(connection, """
            INSERT INTO dbo.plan_probe(tag, name)
            SELECT TOP (5000)
                   CONCAT(N'x', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) % 700),
                   CONCAT(N'n', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)))
              FROM master.dbo.spt_values v CROSS JOIN master.dbo.spt_values v2;
            """).ConfigureAwait(false);
        await ExecAsync(connection, "CREATE INDEX ix_plan_probe_tag ON dbo.plan_probe(tag);").ConfigureAwait(false);
    }

    /// <summary>重建"analyze 会不会真删"用例的场地(10 行)。</summary>
    /// <param name="connection">连接。</param>
    /// <returns>等待句柄。</returns>
    private static async Task ResetDangerAsync(DbConnection connection)
    {
        await ExecAsync(connection, "IF OBJECT_ID('dbo.danger') IS NOT NULL DROP TABLE dbo.danger;")
            .ConfigureAwait(false);
        await ExecAsync(connection, "CREATE TABLE dbo.danger(id int NOT NULL PRIMARY KEY);").ConfigureAwait(false);
        await ExecAsync(connection, """
            INSERT INTO dbo.danger(id)
            SELECT TOP (10) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_objects;
            """).ConfigureAwait(false);
    }

    /// <summary>
    /// 把表设计器用例的场地清干净重建。
    /// <para>
    /// 刻意用<b>自定义 schema</b> + <b>名字里带结束定界符</b>的表:那正是"不转义就必炸"的组合,
    /// 而四条 DDL 里任何一条漏了转义都会在这里当场语法错。
    /// </para>
    /// </summary>
    /// <param name="connection">连接。</param>
    /// <returns>等待句柄。</returns>
    private static async Task ResetDesignSchemaAsync(DbConnection connection)
    {
        await ExecAsync(connection, "IF OBJECT_ID('ops.[Odd]]Table]') IS NOT NULL DROP TABLE ops.[Odd]]Table];")
            .ConfigureAwait(false);
        await ExecAsync(connection, "IF SCHEMA_ID('ops') IS NULL EXEC('CREATE SCHEMA ops');").ConfigureAwait(false);
        await ExecAsync(connection, """
            CREATE TABLE ops.[Odd]]Table](id int NOT NULL PRIMARY KEY, a int NULL, b int NULL);
            """).ConfigureAwait(false);
    }

    /// <summary>逐条发一批语句(调用方对执行计划那一格就是这么做的),把每条产生的结果集收回来。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="statements">切好的语句。</param>
    /// <returns>各条语句的结果集(不出结果集的不占位)。</returns>
    private static async Task<List<(List<string> Headers, List<string[]> Rows)>> RunStatementsAsync(
        DbConnection connection, IReadOnlyList<SqlStatement> statements)
    {
        List<(List<string> Headers, List<string[]> Rows)> grids = [];
        foreach (SqlStatement statement in statements)
        {
            (List<string> headers, List<string[]> rows) =
                await ReadNamedGridAsync(connection, statement.Text).ConfigureAwait(false);
            if (headers.Count > 0)
            {
                grids.Add((headers, rows));
            }
        }
        return grids;
    }

    /// <summary>发一条语句;失败就让测试失败(这一组要的就是"引擎认不认")。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">语句。</param>
    /// <returns>等待句柄。</returns>
    private static async Task ExecAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>发一条<b>预期会失败</b>的语句,把异常带回来(拿不到异常本身就是断言失败的证据)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">语句。</param>
    /// <returns>异常;居然成功了则为 <see langword="null" />。</returns>
    private static async Task<MsSqlException?> CaptureAsync(DbConnection connection, string sql)
    {
        try
        {
            await ExecAsync(connection, sql).ConfigureAwait(false);
            return null;
        }
        catch (MsSqlException ex)
        {
            return ex;
        }
    }

    /// <summary>把一条查询读成"列名 + 行"。列名要断言时用它(运维面那两条的列约定就靠它守)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">查询。</param>
    /// <returns>列名与行。</returns>
    private static async Task<(List<string> Headers, List<string[]> Rows)> ReadNamedGridAsync(
        DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        // 跳到第一个**有列**的结果集:DML 自己不出结果集,而 SET STATISTICS PROFILE 的计划排在它后面 ——
        // 不跳的话"analyze 一条 DELETE"会被读成"什么都没返回"。
        while (reader.FieldCount == 0 && await reader.NextResultAsync().ConfigureAwait(false))
        {
            // 空转:条件本身就是推进。
        }
        List<string> headers = [];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            headers.Add(reader.GetName(i));
        }
        List<string[]> rows = [];
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            string[] cells = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                cells[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";
            }
            rows.Add(cells);
        }
        return (headers, rows);
    }

    /// <summary>取第一行第一列。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">查询。</param>
    /// <returns>值;没有行或值为 NULL 时是 <see langword="null" />。</returns>
    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        object? value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is DBNull ? null : value;
    }

    /// <summary>数一张 dbo 表有多少行(表名是本文件里的常量,走 <c>QuoteIdentifier</c> 是纪律)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="table">表名。</param>
    /// <returns>行数。</returns>
    private static async Task<long> CountAsync(DbConnection connection, string table) =>
        Convert.ToInt64(
            await ScalarAsync(connection, $"SELECT COUNT_BIG(*) FROM [dbo].{Pack.QuoteIdentifier(table)}")
                .ConfigureAwait(false),
            CultureInfo.InvariantCulture);
}
