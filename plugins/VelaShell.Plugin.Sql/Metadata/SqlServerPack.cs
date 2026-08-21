using System.Data.Common;
using System.Globalization;
using System.Text;

namespace VelaShell.Plugin.Sql.Metadata;

/// <summary>
/// SQL Server 的方言包。**数据源一律是 <c>sys.*</c> 目录视图,一个 <c>IDbMaintenance</c> 方法都不调。**
/// <para>
/// <b>为什么一行 <c>sysobjects</c> 都不能用</b>(§3.6 真机结论):那套兼容视图不带 schema、不区分表与视图,
/// 真机上 <c>dbo.OrderDetail</c>(17 列)会被 <c>sales.OrderDetail</c> 的同名列污染,
/// 而且污染一路传到代码生成。所有对象一律先按 <c>object_id</c> 定位、
/// 再用 <c>sys.schemas</c> + 参数精确匹配 schema。
/// </para>
/// <para>
/// <b>类型也不能走 <c>systypes</c></b>:SQL Server 2025 里 <c>varbinary</c> 与新增的 <c>vector</c>
/// 共用 <c>xtype = 165</c>,按 <c>xtype</c> 内连接会让一列变两行、类型名还可能被报成 <c>vector</c>。
/// 这里 join 的是 <c>sys.types.user_type_id</c>,一对一。
/// </para>
/// <para>
/// <b>目录列之间不做字符串拼接</b>:<c>sys.schemas.name</c> 与 <c>sys.objects.name</c> 在同一个库里
/// 可以是两种排序规则(实测 <c>Latin1_General_CI_AS_KS_WS</c> vs <c>SQL_Latin1_General_CP1_CI_AS</c>),
/// 一 <c>+</c> 就是 Msg 451 排序规则冲突。要拼名字请在 C# 侧拼。
/// </para>
/// <para>
/// <b>标识符纪律</b>:比对目录一律走参数(<c>@p0</c> = schema、<c>@p1</c> = 对象名),
/// 用户给的标识符<b>永不拼进 SQL</b>;确实要拼的地方(<see cref="EstimateRowCountSql" /> 这种
/// 接口不给参数通道的)先过 <see cref="DialectPackBase.QuoteQualified" /> 再过 <see cref="Literal" /> ——
/// 两层各挡一种形态,名字里的 <c>]</c> 与 <c>'</c> 都出不了圈(§5.4.4 实测 SqlSugar 的
/// <c>AS(表名)</c> 那条路不转义、能删表)。
/// </para>
/// </summary>
internal sealed class SqlServerPack : DialectPackBase
{
    /// <summary>
    /// 只认表与视图。<c>type</c> 这一列在 <c>sys.objects</c> 上是定长 <c>char(2)</c>,
    /// 比较时要么用 <c>IN</c> 要么记得右侧补空格 —— 这里用 <c>IN</c>。
    /// </summary>
    private const string RelationTypeFilter = "o.type IN ('U', 'V')";

    /// <summary>
    /// "这个对象是不是服务端/工具自带的"的 SQL 表达式(<c>bit</c>)。
    /// <para>
    /// <c>is_ms_shipped</c> 认微软自带的那批(<c>master</c> 里实测 645 个系统视图,§3.6);
    /// 数据库关系图那张 <c>sysdiagrams</c> 是 <c>is_ms_shipped = 0</c> 的,
    /// 得靠 <c>microsoft_database_tools_support</c> 扩展属性另认一次。
    /// </para>
    /// <para>
    /// <b>从"过滤掉"改成"标出来"</b>:早先这是一条 <c>WHERE</c> 谓词,系统对象在树上整个不存在。
    /// 代价是 <c>msdb.dbo.backupset</c>、<c>msdb.dbo.sysjobs</c> 这些**运维每天都要查的表**
    /// 一张都看不见 —— 而它们全是 <c>is_ms_shipped = 1</c>。
    /// 现在它们照列,只是归进"系统对象"分组:该在的在,该分开的分开。
    /// </para>
    /// </summary>
    private const string SystemObjectExpr = """
        CAST(CASE WHEN o.is_ms_shipped = 1
                     OR EXISTS (
                         SELECT 1 FROM sys.extended_properties tools
                         WHERE tools.class = 1 AND tools.major_id = o.object_id AND tools.minor_id = 0
                           AND tools.name = 'microsoft_database_tools_support')
                  THEN 1 ELSE 0 END AS bit)
        """;

    /// <summary>
    /// schema 参数为空时回落到<b>登录的默认 schema</b>(通常是 <c>dbo</c>)。
    /// <para>
    /// 写成 SQL 片段而不是在 C# 侧判断,是为了让"默认 schema 是哪个"由服务端回答 ——
    /// 它取决于登录的 <c>DEFAULT_SCHEMA</c>,客户端猜不出来。**回落到默认 schema 不等于
    /// "随便哪个同名对象"**:后者正是 <c>sysobjects</c> 那条路上跨 schema 串表的成因。
    /// </para>
    /// </summary>
    private const string SchemaParam = "COALESCE(NULLIF(@p0, ''), SCHEMA_NAME())";

    /// <inheritdoc />
    public override SqlDialect Dialect => SqlDialect.SqlServer;

    /// <inheritdoc />
    public override bool HasSchemas => true;

    /// <inheritdoc />
    public override bool HasDatabases => true;

    /// <summary>
    /// <see langword="false" /> —— <c>sys.objects</c> / <c>sys.schemas</c> 是**每库一份**的目录视图,
    /// 一条连接只看得见 <c>Initial Catalog</c> 指定的那个库。
    /// <para>
    /// SQL Server 确实有三段名(<c>[db].sys.objects</c>),理论上跨得过去。**刻意不走那条路**:
    /// 三段名要求把库名拼进 SQL(参数化不了,那是对象名的位置),于是每一条元数据查询都得
    /// 多一处"用户给的标识符进 SQL"的入口 —— 而本包的纪律是永不拼用户标识符(§5.4.4 那条能删表的教训)。
    /// 按库开连接反倒更省事,而且与 PG 共用同一套机制。
    /// </para>
    /// </summary>
    public override bool MetadataSpansCatalogs => false;

    /// <inheritdoc />
    public override bool HasRoutines => true;

    /// <inheritdoc />
    /// <remarks><c>CREATE SEQUENCE</c> 是 SQL Server 2012 引入的;更老的服务端上这一栏会是空的。</remarks>
    public override bool HasSequences => true;

    /// <summary>方括号。标识符里出现结束定界符时加倍 —— <c>Ta]ble</c> → <c>[Ta]]ble]</c>(基类已实现)。</summary>
    protected override (char Open, char Close) Delimiters => ('[', ']');

    /// <inheritdoc />
    /// <remarks><c>SCHEMA_NAME()</c> 不带参数时给的就是**当前登录的默认 schema**
    /// (通常是 <c>dbo</c>,但它是登录属性,可以被建成别的)。这与
    /// <see cref="SchemaParam" /> 里那个回落用的是同一个函数,两处口径天然一致。</remarks>
    public override string CurrentSchemaSql => "SELECT SCHEMA_NAME()";

    /// <inheritdoc />
    public override string SessionIdSql => "SELECT @@SPID";

    /// <inheritdoc />
    /// <remarks>
    /// <b>系统库是标记而不是剔除</b>:<c>database_id &lt;= 4</c> 是 master/tempdb/model/msdb。
    /// 不按名字判 —— <c>database_id</c> 判起来不用管排序规则,更准也更省事。
    /// <para>
    /// <b>状态与可访问性在当前契约里表达不了</b>(<see cref="SqlObject" /> 上没有这两格):
    /// <c>state_desc</c>(ONLINE / OFFLINE / RESTORING)与 <c>HAS_DBACCESS</c> 就此丢掉。
    /// 仍然**全部列出来**而不是按可访问性过滤:一个点开会报错的节点,比一个凭空消失的库诚实。
    /// </para>
    /// </remarks>
    public override async Task<IReadOnlyList<SqlObject>> ListDatabasesAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT d.name, d.database_id
            FROM sys.databases d
            ORDER BY d.name
            """;
        return await QueryAsync(
            connection,
            Sql,
            static r =>
            {
                // SequentialAccess:必须按列序读,顺序不能与 SELECT 列表错开。
                string name = Str(r, 0);
                int id = Int(r, 1);
                // database_id 1..4 是 master / tempdb / model / msdb。它们**照样列出来** ——
                // 用户确实要去 msdb 看作业与备份历史、去 master 看会话 —— 只是收进"系统对象"分组。
                return new SqlObject(SqlObjectKind.Database, name, IsSystem: id <= 4);
            },
            null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>schema_id &gt;= 16384</c> 是固定数据库角色自带的那批(<c>db_owner</c>、<c>db_datareader</c>…);
    /// <c>sys</c> / <c>INFORMATION_SCHEMA</c> / <c>guest</c> 是另外三个系统 schema。
    /// <para><b>属主(<c>principal</c>)在当前契约里表达不了</b>,就此丢掉。</para>
    /// </remarks>
    public override async Task<IReadOnlyList<SqlObject>> ListSchemasAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT s.name, s.schema_id
            FROM sys.schemas s
            ORDER BY s.name
            """;
        return await QueryAsync(
            connection,
            Sql,
            static r =>
            {
                string name = Str(r, 0);
                int schemaId = Int(r, 1);
                bool system = schemaId >= 16384
                              || name.Equals("sys", StringComparison.OrdinalIgnoreCase)
                              || name.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)
                              || name.Equals("guest", StringComparison.OrdinalIgnoreCase);
                return new SqlObject(SqlObjectKind.Schema, name, IsSystem: system);
            },
            null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="schema" /> 传空表示"全部用户 schema";给了就必须按它过滤 ——
    /// 不过滤的话 <c>dbo.OrderDetail</c> 与 <c>sales.OrderDetail</c> 在清单里是两行一模一样的名字(§3.6)。
    /// <para>
    /// <b>估算行数这一格留空</b>:SQL Server 的行数估算在 <c>sys.dm_db_partition_stats</c> 上,
    /// 而那张动态管理视图要 <c>VIEW DATABASE STATE</c> 权限 —— 把它 join 进对象清单,
    /// 权限不够的账号会连**整棵对象树都展不开**。所以它留给按需的
    /// <see cref="EstimateRowCountSql" />(底栏"约 N 行"那条路),清单本身只保证列得出来。
    /// </para>
    /// </remarks>
    public override async Task<IReadOnlyList<SqlObject>> ListRelationsAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        const string Sql = $"""
            SELECT s.name AS schema_name, o.name AS object_name, o.type_desc,
                   CONVERT(nvarchar(max), ep.value) AS comment,
                   {SystemObjectExpr} AS is_system
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.extended_properties ep
                   ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0
                  AND ep.name = 'MS_Description'
            WHERE {RelationTypeFilter}
              AND (@p0 = '' OR s.name = @p0)
            ORDER BY s.name, o.name
            """;
        return await QueryAsync(
            connection,
            Sql,
            static r =>
            {
                string owner = Str(r, 0);
                string name = Str(r, 1);
                // type_desc 是 USER_TABLE / VIEW。**SQL Server 没有物化视图这一类** ——
                // 它的等价物是索引视图,而索引视图在目录里仍然是 VIEW(多一个聚集索引而已),
                // 所以不映射到 MaterializedView:那会画出一个别的方言才有的类别。
                SqlObjectKind kind = Str(r, 2) switch
                {
                    "VIEW" => SqlObjectKind.View,
                    _ => SqlObjectKind.Table
                };
                string comment = Str(r, 3);
                return new SqlObject(kind, name, owner, comment, IsSystem: Bool(r, 4));
            },
            [schema ?? ""],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 列、索引、外键三段各一条查询,在这里组装。
    /// <para>
    /// <b>视图不走另一条路</b>:视图在 <c>sys.columns</c> 里有真实的列行(类型、可空性都是引擎推导好的),
    /// 与表共用同一条 SQL —— 而 <c>DbMaintenance</c> 对视图返回 0 列且不抛异常(§2.3)。
    /// 索引那两条也照发:**索引视图是有索引的**(它的聚集索引就在 <c>sys.indexes</c> 里),
    /// 按"视图没有索引"跳过会把索引视图最要紧的那一格藏起来。
    /// </para>
    /// </remarks>
    public override async Task<IReadOnlyList<SqlObject>> ListRoutinesAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        // type 是定长 char(2),一律用 IN 比对(与 RelationTypeFilter 同一条理由)。
        // P/PC = 存储过程(T-SQL / CLR);FN/IF/TF/AF = 标量、内联表值、多语句表值、CLR 聚合函数。
        const string Sql = $"""
            SELECT s.name AS schema_name, o.name AS object_name, o.type,
                   CONVERT(nvarchar(max), ep.value) AS comment,
                   {SystemObjectExpr} AS is_system
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.extended_properties ep
                   ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0
                  AND ep.name = 'MS_Description'
            WHERE o.type IN ('P', 'PC', 'FN', 'IF', 'TF', 'AF')
              AND (@p0 = '' OR s.name = @p0)
            ORDER BY s.name, o.name
            """;
        return await QueryAsync(
            connection,
            Sql,
            static r =>
            {
                string owner = Str(r, 0);
                string name = Str(r, 1);
                string type = Str(r, 2).Trim();
                string comment = Str(r, 3);
                bool system = Bool(r, 4);
                return new SqlObject(
                    type is "P" or "PC" ? SqlObjectKind.Procedure : SqlObjectKind.Function,
                    name,
                    owner,
                    comment,
                    IsSystem: system);
            },
            [schema ?? ""],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SqlObject>> ListSequencesAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        // sys.sequences 是 sys.objects 的派生视图,所以 SystemObjectExpr 里的 o.* 照样成立
        // (别名仍取 o)。2012 以下的服务端上这张视图不存在 —— 那时整栏空着,
        // 而不是让整棵树挂掉:调用方对空表与"不支持"是同一种处理。
        const string Sql = $"""
            SELECT s.name AS schema_name, o.name AS object_name,
                   CONVERT(nvarchar(max), ep.value) AS comment,
                   {SystemObjectExpr} AS is_system
            FROM sys.sequences o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.extended_properties ep
                   ON ep.class = 1 AND ep.major_id = o.object_id AND ep.minor_id = 0
                  AND ep.name = 'MS_Description'
            WHERE (@p0 = '' OR s.name = @p0)
            ORDER BY s.name, o.name
            """;
        try
        {
            return await QueryAsync(
                connection,
                Sql,
                static r => new SqlObject(
                    SqlObjectKind.Sequence, Str(r, 1), Str(r, 0), Str(r, 2), IsSystem: Bool(r, 3)),
                [schema ?? ""],
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // 2008 R2 及以下没有 sys.sequences(Msg 208)。空表 = 这个服务端上没有序列这一类。【未验证】
            return [];
        }
    }

    /// <inheritdoc />
    public override async Task<SqlTableSchema> DescribeAsync(
        DbConnection connection, SqlObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        object?[] key = [target.Schema ?? "", target.Name];

        IReadOnlyList<SqlColumn> columns = await ReadColumnsAsync(connection, key, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SqlIndex> indexes = await ReadIndexesAsync(connection, key, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SqlForeignKey> foreignKeys = await ReadForeignKeysAsync(connection, key, cancellationToken).ConfigureAwait(false);
        return new(target, columns, indexes, foreignKeys);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>OFFSET … ROWS FETCH NEXT … ROWS ONLY</c>。
    /// <para>
    /// 它<b>要求最外层必须有 <c>ORDER BY</c></b>,没有就是 Msg 10741。所以这里先自己看一眼
    /// 用户那条 SQL 的最外层有没有 <c>ORDER BY</c>(<see cref="HasTopLevelOrderBy" />),没有才补
    /// <c>ORDER BY (SELECT NULL)</c>。那是纯占位:<b>它不保证任何行序</b> ——
    /// 同一条查询翻两次页可能拿到重复行也可能漏行。调用方必须把这件事显示给用户,
    /// 并在用户点列头排序时自动追加主键做 tie-breaker(§7.3)。
    /// </para>
    /// <para>
    /// <b>为什么不把原 SQL 包进派生表</b>(<c>SELECT * FROM (原SQL) AS t ORDER BY (SELECT NULL) …</c>):
    /// 那正是 §7.3 实测把 SQL Server 上带 <c>ORDER BY</c> 的查询整片打死的那条路 ——
    /// 派生表里不许出现 <c>ORDER BY</c>(Msg 1033),而"用户 SQL 带 ORDER BY"是常态而非例外;
    /// 它还会额外丢掉重复列名(Msg 8156)与 <c>FOR UPDATE</c> 之类只能出现在最外层的子句。
    /// 用一次文本扫描换掉这些代价是划算的。
    /// </para>
    /// <para>
    /// <b>这次扫描的代价与边界</b>:它认引号(<c>'…'</c>)、定界标识符(<c>[…]</c> / <c>"…"</c>)、
    /// 行注释与嵌套块注释,只把<b>括号深度为 0</b> 的 <c>ORDER BY</c> 算数(所以
    /// <c>OVER (… ORDER BY …)</c> 与子查询里的排序不会被误认)。它<b>不是</b>完整的 T-SQL 语法分析器:
    /// 一条<b>本来就已经带 <c>OFFSET … FETCH</c></b> 的 SQL 再分页一次仍然是语法错
    /// (与 MySQL / PG 包对已带 <c>LIMIT</c> 的 SQL 追加 <c>LIMIT</c> 是同一类边界)。
    /// </para>
    /// <para>
    /// 契约上没有"调用方告诉我有没有 ORDER BY"的那一格,所以这件事只能由本方法自己判 ——
    /// 判错的代价不对称:漏判(该补没补)是 Msg 10741 当场报错,误判(不该补却补了)是
    /// 两个 <c>ORDER BY</c> 的语法错,两种都是<b>响亮的失败</b>,不会静默出错行。
    /// </para>
    /// </remarks>
    public override string ApplyPaging(string innerSql, int offset, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(innerSql);

        var text = new StringBuilder(innerSql);
        // 结尾的分号会把 OFFSET 甩成第二条语句。
        while (text.Length > 0 && (text[^1] == ';' || char.IsWhiteSpace(text[^1])))
        {
            text.Length--;
        }
        if (!HasTopLevelOrderBy(text.ToString()))
        {
            _ = text.Append("\nORDER BY (SELECT NULL)");
        }
        _ = text.Append(CultureInfo.InvariantCulture,
            $"\nOFFSET {Num(Math.Max(0, offset))} ROWS FETCH NEXT {Num(Math.Max(0, limit))} ROWS ONLY");
        return text.ToString();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 估算行数走 <c>sys.dm_db_partition_stats.row_count</c>(堆 <c>index_id = 0</c> 或聚集索引
    /// <c>index_id = 1</c>,二者只会有一个)。
    /// <para>
    /// 实测比精确 <c>count(*)</c> 快约 3.5 倍(大表上;小表上几乎没有优势),
    /// 所以底栏该写"约 N 行"而不是承诺秒回精确值(§7.3)。它是统计值,可能落后于真实行数。
    /// </para>
    /// <para>
    /// <b>视图也照给这条语句</b>:普通视图在这张表里没有行、<c>SUM</c> 返回 NULL(调用方走"拿不到估算"那条分支),
    /// 而<b>索引视图有</b> —— 按"视图一律返回 null"处理会把索引视图的行数白白丢掉。
    /// </para>
    /// <para>
    /// 名字必须以<b>字符串</b>身份进 <c>OBJECT_ID()</c>,所以先过
    /// <see cref="DialectPackBase.QuoteQualified" /> 变成 <c>[dbo].[Ta]]ble]</c>,
    /// 再过 <see cref="Literal" /> 变成 <c>N'[dbo].[Ta]]ble]'</c>。
    /// </para>
    /// </remarks>
    public override string? EstimateRowCountSql(SqlObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string name = Literal(QuoteQualified(target));
        return $"""
            SELECT SUM(ps.row_count)
            FROM sys.dm_db_partition_stats ps
            WHERE ps.object_id = OBJECT_ID({name}) AND ps.index_id IN (0, 1)
            """;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>KILL</c>。
    /// <para>
    /// <b>它杀的是整条会话,不是取消一条语句</b> —— 与 PostgreSQL 的 <c>pg_cancel_backend</c> 不是一回事。
    /// 客户端那边看到的不是"查询被取消",而是连接进入 kill 状态后的错误
    /// (典型是 Msg 596 "Cannot continue the execution because the session is in the kill state"),
    /// 之后这条连接不能再用,必须重建。所以调用方要把它当"最后手段",并且在界面上按
    /// "断开这条会话"来措辞,而不是"取消这条查询"(§3.10)。
    /// </para>
    /// <para>
    /// 会话 id 是本包自己从 <see cref="SessionIdSql" /> 查回来的十进制整数。仍然校验一遍再拼:
    /// <c>KILL</c> 不接受参数,拼接是这里唯一的注入面,而"这个值一定是我自己查的"是一句
    /// 靠调用方守的约定 —— 约定守不住的时候,校验是最后一道。认不出就返回 <see langword="null" />,
    /// 让调用方降级。
    /// </para>
    /// </remarks>
    public override string? CancelSessionSql(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }
        foreach (char c in sessionId)
        {
            if (!char.IsAsciiDigit(c))
            {
                return null;
            }
        }
        return $"KILL {sessionId}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// SQL Server <b>没有</b> <c>SHOW CREATE TABLE</c>。
    /// <para>
    /// <c>sys.sql_modules.definition</c> 只覆盖视图/存储过程/函数这类"有正文的模块",表根本没有正文。
    /// 与其为表拼一份自己造的 DDL 冒充原文(列、约束、索引、文件组、压缩、分区……漏一样就是错的),
    /// 不如如实返回 <see langword="null" />,让界面显示"该方言不提供建表原文"。
    /// </para>
    /// </remarks>
    public override string? ShowCreateSql(SqlObject target)
    {
        _ = target;
        return null;
    }

    // ─────────────────────────── 运维面(M4) ───────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <b>SQL Server 的执行计划是开关式的,不是前缀式的</b> —— 这是本方法与 MySQL / PG 两个包
    /// (那边一个 <c>EXPLAIN</c> 前缀就完事)最要紧的形状差别,也是它返回的是<b>三条语句</b>而不是一条的原因。
    /// <para>
    /// <b>为什么塞不进一条语句</b>(真机 2025 17.0.4025.3 实测):把
    /// <c>SET SHOWPLAN_ALL ON; select …; SET SHOWPLAN_ALL OFF;</c> 当成<b>一批</b>发出去,
    /// 直接是 <c>Msg 1067, Level 15: The SET SHOWPLAN statements must be the only statements in the batch.</c>
    /// (<c>SHOWPLAN_XML</c> / <c>SHOWPLAN_TEXT</c> 一样,官方文档也明写"必须是批里唯一的语句")。
    /// 所以这里返回的是三条语句,靠调用方<b>按分号切开逐条发</b> ——
    /// <c>SqlQueryTabViewModel.ExplainAsync</c> 正是这么做的(它把本方法的返回值交给
    /// <c>SqlStatementSplitter.Split</c> 再逐条执行),而 <c>SET</c> 是<b>连接级</b>设置、跨批次保持,
    /// 于是三批拼起来仍然是一次完整的"看计划"。真机按三批发过:计划出来了,
    /// <c>OFF</c> 之后同一条连接上的普通查询立刻恢复取数(实测 5000 行)。
    /// </para>
    /// <para>
    /// <b>两档用的不是同一个开关,这是刻意的</b>:
    /// <list type="bullet">
    ///   <item>
    ///     静态档 <c>SET SHOWPLAN_ALL</c> —— <b>只编译不执行</b>。实测拿
    ///     <c>delete from danger where id &gt; 5</c> 走一遍:计划出来了,表里 10 行<b>一行没少</b>。
    ///     契约要的正是这个 —— 绿档之外的语句只给静态计划,绝不能真跑。
    ///   </item>
    ///   <item>
    ///     analyze 档 <c>SET STATISTICS PROFILE</c> —— <b>真的执行</b>,换回来的是多出
    ///     <c>Rows</c> / <c>Executes</c> 两列的<b>实际</b>行数(估算列仍在)。同一条 <c>delete</c>
    ///     走这一档,10 行变 5 行(实测)。契约把这个开关标成危险不是虚张声势,护栏必须留在调用方(§7.6)。
    ///   </item>
    /// </list>
    /// 顺带一条实测差别:<c>SET STATISTICS PROFILE</c> <b>没有</b> SHOWPLAN 那条"必须独占一批"的限制
    /// (同一批里连着用户语句发也跑得通;官方文档对它的 XML 孪生兄弟也明写 "need not be the only
    /// statement in a batch"),所以 analyze 档在"调用方不切句"的场合仍然成立,静态档不行。
    /// 两档写成同一个骨架,是为了让预览面板里两段读起来是一件事的两面。
    /// </para>
    /// <para>
    /// <b>为什么是这两个老开关而不是它们的 XML 版</b>:官方对 <c>SET STATISTICS PROFILE</c> 的原话是
    /// "将来新增的计划信息只会出现在 <c>SET STATISTICS XML</c> 里,不会加进 PROFILE 版"。
    /// 但 XML 版返回的是<b>一行一列、一整段 XML 文档</b>,而 M4 这一版计划是"先出原文"(§6.1 能力组 7)——
    /// 结果网格里一格几十 KB 的 XML 没人读得动;老开关给的是一棵带 <c>StmtText</c> 缩进的<b>树</b>,
    /// 外加 <c>EstimateRows</c> / <c>TotalSubtreeCost</c> / <c>PhysicalOp</c> 这些真正要看的列,
    /// 一行一个算子,正好是网格的形状。等"计划可视化"那一格开出来再换 XML —— 那时换的是渲染,不是这里。
    /// </para>
    /// <para>
    /// <b>一个必须写下来的失败形态:中间那条语句失败时,连接会停在"只出计划"的状态。</b>
    /// 实测 <c>SET SHOWPLAN_ALL ON</c> → <c>select * from dbo.no_such_table</c>(Msg 208)→
    /// 再发一条正常查询,拿回来的<b>是计划不是数据</b>。根源是 <c>SqlExecutor</c> 一条失败即停,
    /// 第三条 <c>OFF</c> 就发不出去了,而 <c>SET</c> 是连接级的。
    /// 三件事让它没有坏到要为此放弃这一格:① 它<b>不静默</b> —— 网格列头当场变成
    /// <c>StmtText</c> / <c>EstimateRows</c>,一眼看得出;② 下一次成功的"看计划"会把它带回来
    /// (那一趟的第三条 <c>OFF</c> 会发出去);③ analyze 档卡住的后果更轻(数据照给,只多一个计划结果集)。
    /// <b>真正的修法在调用方</b>,而调用方已经做了:<c>SqlQueryTabViewModel.FinishPlanScriptAsync</c>
    /// 在脚本没跑完时<b>尽力补发尾巴上那几条</b>(失败一律吞掉,免得盖住用户要看的那条错)。
    /// 本方法这一层能做的只有"把 <c>OFF</c> 写进返回值、并且让它是<b>独立的一条</b>" ——
    /// 补发得以成立,正是因为它是可以单独发出去的一条语句。
    /// </para>
    /// <para>
    /// <b>还有两条是 SHOWPLAN 本身的语义,界面提示要用到</b>:① 它对 <c>CREATE TABLE</c> 这类语句
    /// 同样"只编译不执行",于是<b>后面引用这张新表的语句会报"对象不存在"</b>(官方文档点名的例子);
    /// ② 出计划要 <c>SHOWPLAN</c> 权限,而且是<b>查询碰到的每一个库</b>都要 —— 权限不足时按 §7.8
    /// 翻成"这个账号看不了执行计划",别把原文丢给用户。
    /// </para>
    /// <para>末尾分号先剥掉,理由与 <see cref="ApplyPaging" /> 同一条:编辑器切出来的语句常带终止符。</para>
    /// </remarks>
    public override string? ExplainSql(string innerSql, bool analyze)
    {
        ArgumentNullException.ThrowIfNull(innerSql);
        string body = StripTerminators(innerSql);
        // 两档只差一个开关名,ON / 用户语句 / OFF 的骨架完全一样。
        string toggle = analyze ? "STATISTICS PROFILE" : "SHOWPLAN_ALL";
        // 三条之间的分号是**给调用方的切句标记**,不是要它们进同一批 —— 见上面那条 Msg 1067。
        return $"""
            SET {toggle} ON;
            {body};
            SET {toggle} OFF;
            """;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 列严格按契约:<c>id</c>、<c>user</c>、<c>host</c>、<c>db</c>、<c>state</c>、<c>seconds</c>、<c>query</c>
    /// —— 调用方(<c>SqlOpsTabViewModel</c>)是 <c>SequentialAccess</c> <b>按序号</b>读的,列序不能动。
    /// 别名一律加方括号:<c>user</c> 是 T-SQL 保留字,裸写 <c>AS user</c> 直接是语法错。
    /// <para>
    /// <b><c>WHERE s.is_user_process = 1</c> 是这条 SQL 里最该解释的一格。</b>
    /// <c>sys.dm_exec_sessions</c> 把系统会话也列进来,而实测一台<b>全空闲</b>的 2025 LocalDB 上
    /// <b>40 行里有 39 行是系统会话</b>:<c>host_name</c> / <c>program_name</c> 全是 NULL、
    /// 登录名一律 <c>sa</c>、状态一律 <c>sleeping</c> —— 不过滤的话运维面第一栏开箱就是 39 行空格子
    /// 加 1 行真会话(与 PG 那边"9 行里 8 行是后台进程"是同一个病)。
    /// <b>注意这条过滤只在会话栏,锁栏不过滤</b>:系统会话照样可能持锁挡住你,
    /// 那时它必须出现在阻塞链里(见 <see cref="LockListSql" />)。
    /// </para>
    /// <para>
    /// <b><c>seconds</c> 有两种含义,与 MySQL 的 <c>TIME</c> 是同一类陷阱</b>:
    /// 有请求在跑时它是 <c>total_elapsed_time</c>(毫秒,除 1000 得秒)—— 那是<b>当前语句已耗时</b>;
    /// 没有请求时它是 <c>last_request_end_time</c> 到现在的秒数 —— 那是<b>空闲了多久</b>。
    /// 按它排序找慢查询之前必须先看 <c>state</c>,否则会把一个挂了三小时的空闲连接读成"跑了三小时的查询"。
    /// </para>
    /// <para>
    /// <b>算空闲时长必须用 <c>GETDATE()</c> 而不是 <c>SYSUTCDATETIME()</c> —— 实测差出一整个时区。</b>
    /// <c>sys.dm_exec_sessions</c> 的时间列是<b>服务器本地时间</b>,拿 UTC 去减,在东八区上
    /// 实测同一行同时给出 <c>by_local = 0</c> 与 <c>by_utc = -28800</c>:整栏空闲时长变成 -8 小时。
    /// (与 PG 那边"用 <c>now()</c> 算会算出负数"是同一类错,只是成因不同 —— 那边是事务时间戳,这边是时区。)
    /// </para>
    /// <para>
    /// <b><c>state</c> 把等待类型并进来了</b>,与 PG 把 <c>wait_event</c> 并进来同理:
    /// 光一个 <c>suspended</c> 等于什么都没说 —— 它既可能在等 I/O,也可能已经挂在锁上一小时。
    /// 实测形态:<c>running</c>、<c>suspended (LCK_M_X)</c>(挂在行锁上)、<c>suspended (WAITFOR)</c>、
    /// 空闲连接的 <c>sleeping</c>。<b>等待资源(<c>wait_resource</c>)不并进来</b>:
    /// 它形如 <c>KEY: 5:72057594043105280 (61a06abd401c)</c>,一行就把这一栏撑爆,
    /// 而"到底锁的是哪张表"是锁栏那一格的活(那边把它解析成了表名)。
    /// </para>
    /// <para>
    /// <b><c>query</c> 给的是"正在跑的那一条",不是整批。</b> <c>sys.dm_exec_sql_text</c> 拿回来的是
    /// 整个批(存储过程正文,或者用户一次发的一长串),靠 <c>statement_start_offset</c> /
    /// <c>statement_end_offset</c> 才切得出当前语句 —— 实测一条
    /// <c>BEGIN TRAN; UPDATE …; WAITFOR DELAY '00:00:25'; ROLLBACK;</c> 的会话,
    /// 这一格显示的正是 <c>WAITFOR DELAY '00:00:25'</c> 那一句而不是整批。切法见
    /// <see cref="CurrentStatement" />(两条运维 SQL 共用同一份,免得改一边忘另一边)。
    /// </para>
    /// <para>
    /// <b>空闲会话这一格是空的</b>:没有请求就没有 <c>sql_handle</c>,<c>OUTER APPLY</c> 出来是 NULL。
    /// 想让"空闲在事务里"的连接也显示最后一条语句,得再 join 一次
    /// <c>sys.dm_exec_connections.most_recent_sql_handle</c> —— <b>刻意不加</b>:那是第四张 DMV、
    /// 又要一次 <c>dm_exec_sql_text</c> 调用,而这一栏是每次刷新整表重查的。留作待办。
    /// </para>
    /// <para>
    /// <b>权限:这一栏在权限不足时是"响亮地报错",而不是静默空 —— 而且这是刻意换来的。</b>
    /// 实测(用 <c>EXECUTE AS USER</c> 降权到一个只有 CONNECT 的用户):
    /// <c>sys.dm_exec_sessions</c> 与 <c>sys.dm_exec_requests</c> <b>不报错,只返回你自己那一行</b>
    /// (<c>COUNT(*)</c> 从 40 变 1)—— 那正是 §7.8 说的"一行会被读成'服务器上就一个人'";
    /// 而 <c>sys.dm_exec_sql_text</c> 是<b>报错</b>的:
    /// <c>Msg 371, Level 14: The user does not have … permission 'VIEW SERVER PERFORMANCE STATE'
    /// to perform this action.</c>(2022 起 <c>VIEW SERVER STATE</c> 被拆成了更细的几项,这是其中之一;
    /// 老版本上报的是 <c>VIEW SERVER STATE</c>。)于是<b>整条查询失败</b>,
    /// 界面显示的是"权限不够"而不是"只有一条会话"。把 <c>dm_exec_sql_text</c> 放进这条 SQL
    /// 因此有第二重意义:它是这一栏的<b>权限探针</b>。提示文案该说的是"给这个账号 <c>VIEW SERVER STATE</c>"。
    /// </para>
    /// <para>排序:有语句在跑的排前面,同组按已耗时倒序,最后按 <c>session_id</c> 定序(让两次刷新之间行不乱跳)。</para>
    /// </remarks>
    public override string? SessionListSql => SessionListText;

    /// <inheritdoc />
    /// <remarks>
    /// 列严格按契约:<c>blocked_id</c>、<c>blocking_id</c>、<c>object</c>、<c>mode</c>、<c>query</c>。
    /// <para>
    /// <b>阻塞链的来源是 <c>sys.dm_exec_requests.blocking_session_id</c>,不自连 <c>sys.dm_tran_locks</c>。</b>
    /// 它是服务端按真实等待队列算好的"谁挡着我",与 PG 那边用 <c>pg_blocking_pids()</c> 而不是手写自连
    /// 是同一条理由:手写自连要把"同一把锁"的多个定位列两两配对,而配错的表现不是报错,
    /// 是<b>指认出一个无辜的会话</b>。两个 id 都是 <c>session_id</c>,与
    /// <see cref="SessionIdSql" />(<c>@@SPID</c>)、<see cref="CancelSessionSql" />(<c>KILL</c>)认的是同一个号 ——
    /// 锁栏点出来的 id 必须能直接拿去会话栏里找、拿去杀。
    /// </para>
    /// <para>
    /// <b><c>blocking_session_id</c> 的取值不只是"正数或 0"</b>,这一格要照实转出去:
    /// <c>0</c> 是没被挡(过滤掉);<b>负数是哨兵</b> —— <c>-2</c> 是"挡你的是一个孤儿分布式事务"、
    /// <c>-3</c> 是延迟恢复事务、<c>-4</c> 是闩锁的持有者查不出来。它们<b>不过滤</b>:
    /// "有人挡着你、但那不是一条能点开的会话"本身就是一条要看见的结论
    /// (代价是 <c>KILL -2</c> 不成立,那种要 <c>KILL 'UOW'</c>,界面要提示)。
    /// </para>
    /// <para>
    /// <b>排除自己挡自己</b>(<c>blocking_session_id &lt;&gt; session_id</c>):并行查询的工作线程互等时
    /// 服务端会把 <c>blocking_session_id</c> 填成<b>本会话</b>,那不是阻塞链,是 <c>CXPACKET</c> 一类的
    /// 并行同步。不排掉的话,一条正常跑着的并行大查询会在锁栏里显示成"自己把自己锁住了"。
    /// </para>
    /// <para>
    /// <b>等待中的那把锁用 <c>OUTER APPLY</c> 取而不是 <c>JOIN</c>。</b> 与 PG 那条同一个理由:
    /// "谁挡着我"由 <c>blocking_session_id</c> 独立给出,不依赖 <c>dm_tran_locks</c> 里那一行长什么样;
    /// 用 <c>JOIN</c> 的话,某种等待在锁表里没有对应行时<b>整条阻塞关系会消失</b> ——
    /// <b>"没查到"与"没有阻塞"长得一模一样,而这一栏最不能撒的谎正是这个</b>。
    /// 现在最坏也只是 <c>object</c> 空着、<c>mode</c> 退化成 <c>wait_type</c>,两个 id 与 <c>query</c> 照样是对的。
    /// <c>TOP (1)</c> 是因为一个会话同一时刻只等一把锁,但并行工作线程可能各等一把;取一把并按
    /// <c>resource_type</c> 定序,免得两次刷新之间同一行的 <c>object</c> 来回跳。
    /// </para>
    /// <para>
    /// <b><c>object</c> 的解析分三路,每一路对着一种 <c>resource_associated_entity_id</c> 的含义</b>:
    /// <list type="bullet">
    ///   <item><c>OBJECT</c> 锁 —— 那个 id 就是 <c>object_id</c>,<c>OBJECT_NAME(id, dbid)</c> 直接出名字。</item>
    ///   <item>
    ///     <c>KEY</c> / <c>RID</c> / <c>HOBT</c> / <c>PAGE</c> 锁 —— 那个 id 是 <b>hobt_id</b>,
    ///     要绕 <c>sys.partitions</c> 换 <c>object_id</c>。<b><c>PAGE</c> 也是 hobt_id 而不是分配单元 id</b>,
    ///     这一条是实测出来的(拿 <c>WITH (PAGLOCK, HOLDLOCK)</c> 压一把页锁,它的
    ///     <c>resource_associated_entity_id</c> 等于 <c>sys.partitions.hobt_id</c>,
    ///     而不是 <c>sys.allocation_units.allocation_unit_id</c>)—— 文档只说"可能是三种 id 之一",
    ///     猜错的后果是整类页锁解析不出名字。
    ///   </item>
    ///   <item><c>DATABASE</c> 锁 —— 名字就是库名。</item>
    /// </list>
    /// 其余类型(<c>METADATA</c>、<c>APPLICATION</c>、<c>ALLOCATION_UNIT</c>、<c>EXTENT</c>…)
    /// <b>老实写"&lt;类型&gt; &lt;资源描述&gt;",不猜表名</b>:一个错的表名比一句
    /// <c>METADATA data_space_id = 1</c> 坏得多(与 PG 那边不去反推 <c>transactionid</c> 锁的表名同一条纪律)。
    /// 行/页的具体位置也不摆进来:<c>KEY</c> 锁的 <c>resource_description</c> 是一段行哈希
    /// (形如 <c>(61a06abd401c)</c>),没有 <c>%%lockres%%</c> 关联查询根本反查不到是哪一行。
    /// </para>
    /// <para>
    /// <b>跨库的锁只写得出名字的那一半 —— 而且必须先比库。</b> <c>sys.dm_tran_locks</c> 是<b>全实例</b>的,
    /// 别的库里的锁也在里面;而 <c>sys.partitions</c> 只有当前库的分区。拿别的库的 hobt_id 去查它,
    /// 要么查不到(退化成资源描述),<b>要么在当前库里碰巧撞上同一个 hobt_id,给出一个完全无关的表名</b>。
    /// 所以先比 <c>resource_database_id = DB_ID()</c> 再查(<c>OBJECT_NAME</c> 那一路不用比,它自带库参数)。
    /// 这与 PG 那边 <c>relation::regclass</c> 前面先比 <c>w.database</c> 是同一条。
    /// </para>
    /// <para>
    /// <b>拼名字一律加 <c>COLLATE DATABASE_DEFAULT</c>。</b> 类注释里那条"目录列之间不做字符串拼接"
    /// (Msg 451 排序规则冲突)在这里躲不掉 —— 跨库解析时 <c>OBJECT_NAME(id, dbid)</c> 拿回来的名字
    /// 带的是<b>那个库</b>的排序规则,与当前库的 <c>N'.'</c> 一拼就可能冲突。
    /// 强制落到当前库的默认排序规则,是这一格唯一的拼接许可。
    /// </para>
    /// <para>
    /// <b><c>mode</c> 把冲突的两边并进一格</b>:"&lt;资源类型&gt; &lt;要的模式&gt; &lt;- &lt;持有方的模式&gt;",
    /// 实测形如 <c>KEY X &lt;- X</c>(两个事务改同一行)。只给一边说不清冲突(<c>S</c> 撞 <c>X</c> 与
    /// <c>X</c> 撞 <c>X</c> 是两种事),而契约的五列里没有第六格放持有方。资源类型一并放进来同理:
    /// 表锁(<c>OBJECT</c>)与行锁(<c>KEY</c>)的排障方向完全不同。
    /// 持有方那一半是拿"同一把锁"的四个定位列反查持有方<b>已授予</b>的行得来的;
    /// <c>resource_description</c> 可能是 NULL,所以用 <c>a = b OR (a IS NULL AND b IS NULL)</c> 比 ——
    /// <b>不用 2022 才有的 <c>IS NOT DISTINCT FROM</c></b>:那会让这条 SQL 在 2019 及更早的服务端上直接语法错,
    /// 而本包不做版本探测。查不到持有方就整段省掉(<c>COALESCE(N' &lt;- ' + …)</c>),不留一个空箭头。
    /// </para>
    /// <para>
    /// <b><c>query</c> 是被阻塞方的语句</b>,与 MySQL / PG 两边同一条理由:这一行的主语是被阻塞的会话
    /// (<c>blocked_id</c> 排第一),读起来才一致;而持锁方十有八九是"开着事务闲着",
    /// 它的当前语句要么是别的、要么根本没有。想看持锁方在干什么,拿 <c>blocking_id</c> 去会话栏里找 ——
    /// 那一栏就在同一页上。切法与会话栏共用 <see cref="CurrentStatement" />。
    /// </para>
    /// <para>
    /// <b>没人争锁时它干净地返回 0 行</b>(实测),这正是契约要的:空表意味着"真的没有阻塞",
    /// 而不是"这条查询没跑通"。权限那一条与 <see cref="SessionListSql" /> 同 ——
    /// <c>dm_exec_sql_text</c> 会替这条 SQL 把权限不足喊出来(Msg 371),
    /// 而 <c>sys.dm_tran_locks</c> 本身也要 <c>VIEW SERVER STATE</c>。
    /// </para>
    /// </remarks>
    public override string? LockListSql => LockListText;

    /// <inheritdoc />
    /// <remarks>
    /// <b>这张表的每一项都必须"建出来再读回来还是同一个字符串"</b> ——
    /// 下拉里挑一个类型加完列,结构页一刷新显示的是 <see cref="DescribeAsync" /> 从
    /// <c>sys.types</c> + <c>sys.columns</c> 还原出来的形态(见 <see cref="RenderDataType" />)。
    /// 两边对不上,用户会以为插件把他的类型改了。所以这张表是照着 <see cref="RenderDataType" /> 的
    /// 输出形态写的,并且有一条真机用例逐项验往返(<c>SqlServerOpsTests</c>)。
    /// <para>
    /// <b>因此括号该带的必须带、不该带的不能带,这是本表里最容易写错的一格</b>:
    /// <list type="bullet">
    ///   <item>
    ///     <c>datetime2</c> / <c>time</c> / <c>datetimeoffset</c> 括号里是<b>时间精度</b>不是长度,
    ///     而且不写就落成最大精度 7,读回来变成 <c>datetime2(7)</c> —— 与下拉里那个裸 <c>datetime2</c> 对不上。
    ///     所以一律写全。<c>datetime2(3)</c> 单摆一项:毫秒是最常用的那一档(<c>datetime</c> 的替代品)。
    ///   </item>
    ///   <item>
    ///     <c>char</c> / <c>varchar</c> / <c>nchar</c> / <c>nvarchar</c> 不带长度<b>静默变成 (1)</b>
    ///     (实测:<c>ADD c varchar</c> 之后 <c>max_length = 1</c>),不是语法错 ——
    ///     这比 MySQL 的 <c>VARCHAR</c> 不带长度直接报错<b>更坏</b>,因为它悄悄成功。
    ///     同理 <c>decimal</c> 不带精度静默变成 <c>decimal(18,0)</c>(实测),小数位直接没了。
    ///   </item>
    ///   <item>
    ///     <c>float</c> 反过来:声明 <c>float(25..53)</c> 一律落成 <c>float</c>(精度 53),所以摆裸名。
    ///     要单精度请用 <c>real</c>。
    ///   </item>
    /// </list>
    /// </para>
    /// <para><b>几项刻意不给,每一条都有实测或语义上的具体理由</b>:</para>
    /// <list type="bullet">
    ///   <item>
    ///     <c>rowversion</c> —— 建得成,但<b>读回来叫 <c>timestamp</c></b>(实测:
    ///     <c>ADD c28 rowversion</c> 之后 <c>sys.types.name = 'timestamp'</c>)。
    ///     往返不闭合的项一律不摆进下拉,理由见上。何况它一张表只能有一列、还不可写。
    ///   </item>
    ///   <item>
    ///     <c>text</c> / <c>ntext</c> / <c>image</c> —— 官方标了"将来会移除,请改用
    ///     <c>varchar(max)</c> / <c>nvarchar(max)</c> / <c>varbinary(max)</c>",而且它们用不了大多数
    ///     字符串函数与比较。读得出来(<see cref="RenderDataType" /> 认它们),但不该再新建。
    ///   </item>
    ///   <item>
    ///     <c>money</c> / <c>smallmoney</c> —— 它是定标 4 位的定点数,<c>decimal(19,4)</c> 完全等价;
    ///     而它的<b>除法中间结果按 4 位截断</b>,算利率、算占比时会静默丢精度。要存钱用 <c>decimal</c>。
    ///   </item>
    ///   <item>
    ///     <c>smalldatetime</c> —— 精度只到<b>分钟</b>(秒会被四舍五入进分钟)、范围 1900–2079。
    ///     新列没有理由选它;老库里有,照样读得出来。
    ///   </item>
    ///   <item>
    ///     <c>json</c> 与 <c>vector(n)</c> —— <b>2025 才有的新类型</b>,而本包<b>不做服务端版本探测</b>
    ///     (与 MySQL 包那条 <c>data_locks</c> 待办同一笔),摆进下拉就会在 2016–2022 上给用户一条
    ///     "Cannot find data type json"。<see cref="RenderDataType" /> 认得它们(读没问题),
    ///     等版本探测接上再按版本分叉这张表。
    ///   </item>
    ///   <item>
    ///     <c>sysname</c> —— 是 <c>nvarchar(128) NOT NULL</c> 的系统别名,给用户建列没有意义;
    ///     <c>hierarchyid</c> / <c>sql_variant</c> —— 真类型,但取用场景窄、比较语义反直觉,
    ///     不占下拉的位置(要用的人会自己敲)。
    ///   </item>
    /// </list>
    /// <para>
    /// 与 <c>GetDbTypes()</c> 的差别正是契约点名的那条(§2.3):它返回的是"这个库<b>当前用到了</b>哪些类型",
    /// 随建表变多。这里是<b>静态表</b>,与库里有什么无关。
    /// </para>
    /// </remarks>
    public override IReadOnlyList<string> CommonTypes => TypeNames;

    // ─────────────────────────── 表设计器(M4) ───────────────────────────
    //
    // CreateIndexDdl **不覆盖**,基类的通行写法在 T-SQL 上逐字成立(真机逐条发过):
    //   CREATE [UNIQUE] INDEX [ix_ab] ON [dbo].[ddl_probe] ([a], [b])
    // 有一格与 PG 一模一样,值得在这里钉死:**索引名必须是裸名,不能加 schema 限定**。
    // 索引跟着表走(它没有自己的 schema),写成 CREATE INDEX [dbo].[ix_bad] ON … 是语法错 ——
    // 实测 Msg 102, Level 15: Incorrect syntax near '.',插入符就指在那个点上。基类给的正是裸名。
    // 另有两条这条 DDL 说不出口、但表设计器该知道的:
    //   ① 建出来的一律是**非聚集**索引(T-SQL 的默认),契约的参数里只有 unique 一个开关;
    //      聚集索引一张表只能有一个,真要建会撞 Msg 1902,那是另一格的活。
    //   ② INCLUDE 列、筛选条件(WHERE)、升降序在契约的参数里都没有位置 ——
    //      DescribeAsync 读得出它们(见 BuildIndexDefinition),但这条 DDL 造不出来。
    //
    // DropColumnDdl 也**不覆盖**,ALTER TABLE [dbo].[t] DROP COLUMN [c] 在 T-SQL 上逐字成立。
    // 但删列这一路有一条 T-SQL 特有、而且**本包自己会踩上**的形态,必须记在这儿:
    //   **带默认值的列删不掉**(与 MySQL 那种"静默连坐删索引"正相反,SQL Server 是硬拒绝):
    //   实测 ALTER TABLE [dbo].[ddl_probe] DROP COLUMN [qty] →
    //     Msg 5074, Level 16: The object 'DF__ddl_probe__qty__6FE99F9F' is dependent on column 'qty'.
    //     Msg 4922, Level 16: ALTER TABLE DROP COLUMN qty failed because one or more objects access this column.
    //   而那个 DF__… 名字正是 AddColumnDdl 生成的 DEFAULT 子句留下的**自动命名**约束 ——
    //   也就是说"加一列带默认值,再删掉它"这条最自然的往返在 SQL Server 上**走不通**,
    //   要先 ALTER TABLE … DROP CONSTRAINT [DF__…]。契约里还没有"删约束"那一格,
    //   所以表设计器现在只能把这条错原样报出来(§7.8 该把它翻成"这一列上有默认值约束,要先删约束")。
    //   索引、检查约束、外键引用了这一列时是同一条错(Msg 5074),只是宾语不同。

    /// <inheritdoc />
    /// <remarks>
    /// <b>必须覆盖:基类的 <c>ADD COLUMN</c> 在 T-SQL 上是语法错。</b>
    /// 实测 <c>ALTER TABLE [dbo].[ddl_probe] ADD COLUMN [qty] int NOT NULL DEFAULT 0</c> →
    /// <c>Msg 156, Level 15: Incorrect syntax near the keyword 'COLUMN'.</c>
    /// T-SQL 的加列<b>没有 <c>COLUMN</c> 这个关键字</b>(<c>ALTER TABLE t ADD c int</c>),
    /// 这与 MySQL / PG / SQLite 三家都不同,是本包必须自己拼这一句的全部原因。
    /// (删列反过来<b>必须</b>带 <c>COLUMN</c>,所以 <see cref="DialectPackBase.DropColumnDdl" /> 照用不误 ——
    /// 一正一反,是 T-SQL 语法本身的不对称。)
    /// <para>
    /// <b>可空性显式写出来</b>,不像基类那样"可空就什么都不写":T-SQL 里省略可空性时,
    /// 结果由数据库选项 <c>ANSI_NULL_DEFAULT</c> 与连接的 <c>SET ANSI_NULL_DFLT_ON/OFF</c> <b>共同</b>决定 ——
    /// 那是两个界面上看不见的变量。实测本机这几种组合下都落成可空,但那是"这一组配置"的结论,
    /// 不是语法保证;多写一个 <c>NULL</c> 换掉一个"看运气"的默认值,划算。
    /// </para>
    /// <para>
    /// 通用写法只写得出"列名 + 类型 + 可空性 + <c>DEFAULT</c>"四样,列模型上另外四样它
    /// <b>一声不吭地丢掉</b>,而 SQL Server 会照办出一个<b>普通列</b>:
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="SqlColumn.IsGenerated" /> —— 拼不出 <c>AS (表达式) [PERSISTED]</c>,
    ///     因为 <see cref="SqlColumn" /> 上<b>根本没有生成表达式这一格</b>。
    ///     用户点的"加一个计算列"办成了别的事,而且哪儿都不提示。
    ///   </item>
    ///   <item>
    ///     <see cref="SqlColumn.IsPrimaryKey" /> —— 拼不出 <c>PRIMARY KEY</c>;
    ///     何况表已有主键时它必然是 Msg 1779。
    ///   </item>
    ///   <item>
    ///     <see cref="SqlColumn.IsAutoIncrement" /> —— 拼不出 <c>IDENTITY(1,1)</c>
    ///     (种子与步长在契约里也没有位置)。
    ///   </item>
    ///   <item>
    ///     <see cref="SqlColumn.Comment" /> —— T-SQL <b>没有</b>内联的列注释子句,
    ///     它是一条独立的 <c>EXEC sys.sp_addextendedproperty …</c>(本包读的那个
    ///     <c>MS_Description</c> 扩展属性就是它写的)。本契约这一格返回的是<b>一条</b> DDL,
    ///     调用方也是按一条来预览与确认的;塞成两句会让确认框里那段原文与实际发生的事对不上。
    ///     等契约把"一次多条 DDL"那一格开出来再接 —— 与 PG 包那条是同一笔待办。
    ///   </item>
    /// </list>
    /// 静默办成别的事比报错坏得多,所以这四种一律返回 <see langword="null" />,
    /// 让界面显示"该数据库不支持这样加列"(§7.8)。
    /// </para>
    /// <para>
    /// <b>两条运行时形态,DDL 文本改不了,记在这儿给界面提示用</b>:
    /// ① 表里已有行时,<c>NOT NULL</c> 不带 <c>DEFAULT</c> 必然失败 —— 实测
    /// <c>Msg 4901: ALTER TABLE only allows columns to be added that can contain nulls, or have a
    /// DEFAULT definition specified …</c>,所以"非空"与"默认值"这两个输入框在非空表上是绑定关系,
    /// 该在界面上就拦住;
    /// ② <b>可空列上的 <c>DEFAULT</c> 不回填老行</b> —— 加完之后已有行是 NULL 而不是默认值
    /// (要回填得写 <c>WITH VALUES</c>,而 <c>NOT NULL</c> 列是隐含回填的)。
    /// 这一条最容易被读成"默认值没生效",实际是 T-SQL 说明书里的规则。
    /// </para>
    /// <para>
    /// <b>类型与默认值都是原样拼进去的,所以调用方给的文本必须自己成立。</b>
    /// 好消息是这趟来回在 SQL Server 上是通的:<see cref="DescribeAsync" /> 读回来的
    /// <see cref="SqlColumn.DataType" /> 就是 <see cref="RenderDataType" /> 的规范形态
    /// (<c>nvarchar(50)</c> / <c>nvarchar(max)</c> / <c>decimal(12,3)</c> / <c>datetime2(3)</c>),
    /// 原样拿去加列成立;<see cref="CommonTypes" /> 给的也是同一套形态。默认值那一格同理 ——
    /// <see cref="StripOuterParentheses" /> 把目录里的 <c>((1))</c> 剥成 <c>1</c>,
    /// 而 <c>DEFAULT 1</c> 加回去之后目录里又变回 <c>((1))</c>,往返闭合。
    /// </para>
    /// </remarks>
    public override string? AddColumnDdl(SqlObject target, SqlColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        // 通用写法表达不了这四样,而它丢掉它们时不报错 —— 见上。
        if (column.IsGenerated || column.IsPrimaryKey || column.IsAutoIncrement
            || !string.IsNullOrEmpty(column.Comment))
        {
            return null;
        }
        // 不复用基类:那边写死了 ADD COLUMN,在 T-SQL 上是 Msg 156。
        return $"ALTER TABLE {QuoteQualified(target)} ADD {QuoteIdentifier(column.Name)} {column.DataType}"
               + (column.IsNullable ? " NULL" : " NOT NULL")
               + (string.IsNullOrEmpty(column.DefaultValue) ? "" : $" DEFAULT {column.DefaultValue}");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>必须覆盖:基类的 <c>DROP INDEX [ix]</c> 在 T-SQL 上是语法错,不是"删不掉"。</b>
    /// 实测 <c>Msg 159, Level 15: Must specify the table name and index name for the DROP INDEX statement.</c>
    /// —— 这条错至少把话说明白了(比 MySQL 那种"错在句尾的 1064"好认),但它仍然是一条发不出去的语句。
    /// <para>
    /// 根源与 MySQL 同:索引名<b>只在表内唯一</b>(它不是 schema 里的对象,同一个 schema 下两张表
    /// 各有一个 <c>ix_name</c> 完全合法),所以"删哪个 ix"不带表名根本不完整。
    /// 写法取 <c>DROP INDEX ix ON t</c> 而不是老式的 <c>DROP INDEX t.ix</c>:后者是上一个时代的语法,
    /// 官方早已不建议再用;前者还与基类 <see cref="DialectPackBase.CreateIndexDdl" /> 生成的
    /// <c>CREATE INDEX ix ON t</c> 正好成对,预览面板里两条并排读起来是一件事的正反面。
    /// </para>
    /// <para>
    /// 表名走 <see cref="DialectPackBase.QuoteQualified" />(schema 与表名两段都加方括号、<c>]</c> 加倍),
    /// 索引名走 <see cref="DialectPackBase.QuoteIdentifier" /> —— 用户标识符永不裸拼。
    /// </para>
    /// <para>
    /// <b>一条这条 DDL 表达不了、但界面该先拦下的</b>:主键 / 唯一<b>约束</b>背后的索引删不掉,
    /// 报 <c>Msg 3723: An explicit DROP INDEX is not allowed on index '…'. It is being used for
    /// PRIMARY KEY constraint enforcement.</c>,要改用 <c>ALTER TABLE … DROP CONSTRAINT</c>。
    /// 结构页的索引栏会把主键索引一起列出来(<see cref="DescribeAsync" /> 是照实报的,
    /// 而且 <see cref="Marked" /> 已经给唯一约束打了 <c>unique-constraint</c> 记号),
    /// 所以那颗按钮点在主键上必然是这条错 —— 等契约开出"删约束"那一格再接。
    /// 现在<b>不</b>在这里偷偷改写成 <c>DROP CONSTRAINT</c>:用户点的是"删索引",而删约束是另一件事。
    /// </para>
    /// </remarks>
    public override string? DropIndexDdl(SqlObject target, string indexName) =>
        $"DROP INDEX {QuoteIdentifier(indexName)} ON {QuoteQualified(target)}";

    /// <summary>
    /// 把 <c>sys.dm_exec_sql_text</c> 拿回来的<b>整批</b>文本切成"当前正在跑的那一条"。
    /// <para>
    /// 两条运维 SQL(<see cref="SessionListSql" /> / <see cref="LockListSql" />)共用这一份,
    /// 免得改一边忘另一边 —— 它们的 <c>query</c> 列必须是同一个语义,
    /// 不然同一条会话在两栏里长得不一样,而用户会以为那是两条不同的语句。
    /// </para>
    /// <para>
    /// 两个陷阱都挤在这一小段里:① 偏移量是<b>字节</b>数(文本按 <c>nvarchar</c> 算),
    /// 要除以 2 才是字符位置,少除一次就从半截开始切;
    /// ② <c>statement_end_offset</c> 为 <b>-1</b> 表示"到批末尾",直接拿它算长度会得到负数
    /// (<c>SUBSTRING</c> 的第三个参数为负是 <c>Msg 536: Invalid length parameter passed to the
    /// substring function.</c>,实测),所以先换成 <c>DATALENGTH(t.text)</c>。
    /// </para>
    /// <para>写成一行是为了让拼出来的 SQL 仍然齐整:常量里换行会破坏外层那段的缩进。</para>
    /// </summary>
    private const string CurrentStatement =
        "SUBSTRING(t.text, (r.statement_start_offset / 2) + 1, "
        + "((CASE r.statement_end_offset WHEN -1 THEN DATALENGTH(t.text) "
        + "ELSE r.statement_end_offset END - r.statement_start_offset) / 2) + 1)";

    /// <summary>会话列表的 SQL(取舍与陷阱见 <see cref="SessionListSql" />)。</summary>
    private const string SessionListText = $"""
        SELECT s.session_id                                    AS [id],
               s.login_name                                    AS [user],
               COALESCE(NULLIF(s.host_name, N''), N'')         AS [host],
               DB_NAME(COALESCE(r.database_id, s.database_id)) AS [db],
               COALESCE(r.status, s.status)
                 + COALESCE(N' (' + r.wait_type + N')', N'')   AS [state],
               CAST(CASE WHEN r.session_id IS NULL
                         THEN DATEDIFF(second, s.last_request_end_time, GETDATE())
                         ELSE r.total_elapsed_time / 1000.0
                    END AS decimal(18, 3))                     AS [seconds],
               {CurrentStatement} AS [query]
          FROM sys.dm_exec_sessions s
          LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
          OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
         WHERE s.is_user_process = 1
         ORDER BY CASE WHEN r.session_id IS NULL THEN 1 ELSE 0 END, [seconds] DESC, s.session_id
        """;

    /// <summary>锁与阻塞链的 SQL(取舍与陷阱见 <see cref="LockListSql" />)。</summary>
    private const string LockListText = $"""
        SELECT r.session_id          AS [blocked_id],
               r.blocking_session_id AS [blocking_id],
               COALESCE(o.name,
                        NULLIF(w.resource_type
                               + COALESCE(N' ' + NULLIF(w.resource_description, N''), N''), N''),
                        N'')         AS [object],
               COALESCE(w.resource_type + N' ' + w.request_mode, r.wait_type, N'')
                 + COALESCE(N' <- ' + h.modes, N'') AS [mode],
               {CurrentStatement} AS [query]
          FROM sys.dm_exec_requests r
          OUTER APPLY (
              SELECT TOP (1) l.resource_type, l.resource_database_id,
                     l.resource_associated_entity_id, l.resource_description, l.request_mode
                FROM sys.dm_tran_locks l
               WHERE l.request_session_id = r.session_id AND l.request_status = N'WAIT'
               ORDER BY l.resource_type
          ) w
          OUTER APPLY (
              SELECT CASE
                         WHEN w.resource_type = N'OBJECT'
                             THEN OBJECT_SCHEMA_NAME(w.resource_associated_entity_id,
                                                     w.resource_database_id) COLLATE DATABASE_DEFAULT
                                  + N'.'
                                  + OBJECT_NAME(w.resource_associated_entity_id,
                                                w.resource_database_id) COLLATE DATABASE_DEFAULT
                         WHEN w.resource_type = N'DATABASE'
                             THEN DB_NAME(w.resource_database_id) COLLATE DATABASE_DEFAULT
                         WHEN w.resource_type IN (N'KEY', N'RID', N'HOBT', N'PAGE')
                              AND w.resource_database_id = DB_ID()
                             THEN (SELECT TOP (1)
                                          OBJECT_SCHEMA_NAME(p.object_id) COLLATE DATABASE_DEFAULT
                                          + N'.'
                                          + OBJECT_NAME(p.object_id) COLLATE DATABASE_DEFAULT
                                     FROM sys.partitions p
                                    WHERE p.hobt_id = w.resource_associated_entity_id)
                     END AS name
          ) o
          OUTER APPLY (
              SELECT STRING_AGG(m.request_mode, N',') AS modes
                FROM (SELECT DISTINCT g.request_mode
                        FROM sys.dm_tran_locks g
                       WHERE g.request_session_id = r.blocking_session_id
                         AND g.request_status = N'GRANT'
                         AND g.resource_type = w.resource_type
                         AND g.resource_database_id = w.resource_database_id
                         AND g.resource_associated_entity_id = w.resource_associated_entity_id
                         AND (g.resource_description = w.resource_description
                              OR (g.resource_description IS NULL
                                  AND w.resource_description IS NULL))) m
          ) h
          OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
         WHERE r.blocking_session_id <> 0
           AND r.blocking_session_id <> r.session_id
         ORDER BY [blocked_id], [blocking_id]
        """;

    /// <summary>
    /// 常用类型的静态表(取舍与陷阱见 <see cref="CommonTypes" />)。
    /// 提成静态字段是因为类型下拉每次打开都要读它,没必要每次新建一个数组。
    /// </summary>
    private static readonly string[] TypeNames =
    [
        // 整数与布尔:T-SQL 没有 BOOLEAN,bit 就是那一格(0 / 1 / NULL)。
        "bit", "tinyint", "smallint", "int", "bigint",
        // 定点与浮点:钱一律用 decimal(money 的除法会静默丢精度,见 CommonTypes)。
        // numeric 是 decimal 的同义词,只摆一个;float 不带括号(25~53 一律落成 53)。
        "decimal(18,2)", "float", "real",
        // 日期时间:datetime2 排在 datetime 前面 —— 后者 3.33 毫秒的舍入是历史包袱。
        // 括号里是**时间精度**,不写就落成 7,读回来对不上下拉。
        "date", "time(7)", "datetime2(7)", "datetime2(3)", "datetimeoffset(7)", "datetime",
        // 文本:一律优先 n 前缀(Unicode)。不带长度会**静默**变成 (1),所以模板都带长度。
        "char(10)", "varchar(50)", "varchar(max)", "nchar(10)", "nvarchar(50)", "nvarchar(max)",
        // 二进制:大对象用 varbinary(max)(image 已经不该再用了)。
        "binary(16)", "varbinary(50)", "varbinary(max)",
        // 其余常用的独立类型。空间类型是 T-SQL 内置的,不像 PG 那样要装扩展。
        "uniqueidentifier", "xml", "geography", "geometry"
    ];

    /// <summary>
    /// 剥掉末尾的语句终止符(可能有多个,后面还可能跟着空白)。
    /// <para>
    /// <see cref="ExplainSql" /> 要把用户语句夹在两条 <c>SET</c> 中间、自己补分号;
    /// 留着原文尾巴上的分号会切出一条空语句,回显里的语句也难认。
    /// <see cref="ApplyPaging" /> 里有一份等价的内联写法 —— 那是 M2 已定稿的代码,
    /// 这一轮不去动它;两处规则相同,将来要改就一起改。
    /// </para>
    /// </summary>
    /// <param name="sql">SQL 原文。</param>
    /// <returns>去掉尾部分号与空白的 SQL。</returns>
    private static string StripTerminators(string sql)
    {
        string body = sql.TrimEnd();
        while (body.EndsWith(';'))
        {
            body = body[..^1].TrimEnd();
        }
        return body;
    }

    /// <summary>
    /// 读列。
    /// <para>
    /// 视图与表走的是同一条 <c>sys.columns</c> 路径:视图在 <c>sys.columns</c> 里有真实的列行
    /// (类型、可空性都是引擎推导好的)。SqlSugar 对视图返回 0 列且不抛异常,那条路不能复用(§2.3)。
    /// </para>
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="key">schema 与对象名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按列序的列。</returns>
    private static async Task<IReadOnlyList<SqlColumn>> ReadColumnsAsync(
        DbConnection connection, object?[] key, CancellationToken cancellationToken)
    {
        const string Sql = $"""
            SELECT
                c.name                              AS column_name,
                c.column_id                         AS column_ordinal,
                t.name                              AS type_name,
                t.is_user_defined                   AS type_is_user_defined,
                c.max_length                        AS max_length,
                c.precision                         AS numeric_precision,
                c.scale                             AS numeric_scale,
                c.is_nullable                       AS is_nullable,
                c.is_identity                       AS is_identity,
                c.is_computed                       AS is_computed,
                dc.definition                       AS default_definition,
                pk.key_ordinal                      AS pk_ordinal,
                CONVERT(nvarchar(max), ep.value)    AS comment
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.types   t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            LEFT JOIN sys.extended_properties ep
                   ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id
                  AND ep.name = 'MS_Description'
            OUTER APPLY (
                SELECT TOP (1) ic.key_ordinal
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.object_id = c.object_id AND i.is_primary_key = 1
                  AND ic.column_id = c.column_id AND ic.is_included_column = 0
            ) pk
            WHERE {RelationTypeFilter}
              AND s.name = {SchemaParam}
              AND o.name = @p1
            ORDER BY c.column_id
            """;

        return await QueryAsync(
            connection,
            Sql,
            static r =>
            {
                // SequentialAccess:**严格按列序读,一列只读一次**。
                // 先读进局部变量再构造 —— SqlColumn 的构造参数顺序与 SELECT 列序不同,
                // 直接写成构造调用就会倒着读,运行期才炸。
                string name = Str(r, 0);
                int ordinal = Int(r, 1);
                string typeName = Str(r, 2);
                bool isUserDefined = Bool(r, 3);
                int maxLength = Int(r, 4);
                int precision = Int(r, 5);
                int scale = Int(r, 6);
                bool nullable = Bool(r, 7);
                bool identity = Bool(r, 8);
                // 持久化(PERSISTED)与非持久化都是生成列:两者都不可写。
                // 只有"落不落盘"这一点不同,那影响的是空间与索引,不是可写性。
                bool computed = Bool(r, 9);
                string? rawDefault = StrOrNull(r, 10);
                bool primaryKey = LongOrNull(r, 11) is not null;
                string comment = Str(r, 12);

                string? defaultValue = rawDefault is null ? null : StripOuterParentheses(rawDefault);
                return new SqlColumn(
                    name,
                    ordinal,
                    RenderDataType(typeName, isUserDefined, maxLength, precision, scale),
                    nullable,
                    primaryKey,
                    identity,
                    computed,
                    defaultValue,
                    defaultValue is not null && !IsLiteralDefault(defaultValue),
                    comment);
            },
            key,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读索引。两条查询:一条出索引本身,一条出<b>每列一行</b>的列清单,在 C# 侧装配。
    /// <para>
    /// <c>i.type = 0</c> 是堆,不是索引,别当成一个"没有列的索引"画出来。
    /// </para>
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="key">schema 与对象名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按 <c>index_id</c> 的索引清单(主键索引排在最前)。</returns>
    private async Task<IReadOnlyList<SqlIndex>> ReadIndexesAsync(
        DbConnection connection, object?[] key, CancellationToken cancellationToken)
    {
        const string IndexSql = $"""
            SELECT i.index_id, i.name, i.is_unique, i.is_primary_key, i.is_unique_constraint,
                   i.type_desc, i.filter_definition, i.is_disabled
            FROM sys.indexes i
            JOIN sys.objects o ON o.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE s.name = {SchemaParam} AND o.name = @p1 AND i.type <> 0
            ORDER BY i.index_id
            """;

        // 键列与 INCLUDE 列一起取回来,在 C# 侧分开装 —— is_included_column = 1 的
        // 不进 Columns:它们不参与排序也不能用于最左前缀匹配,混进去会让
        // "这个索引撑不撑得住我这条查询"的判断直接失真。
        const string ColumnSql = $"""
            SELECT i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id,
                   ic.is_descending_key, c.name
            FROM sys.indexes i
            JOIN sys.objects o ON o.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE s.name = {SchemaParam} AND o.name = @p1 AND i.type <> 0
            ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal, ic.index_column_id
            """;

        List<IndexColumnRow> columnRows = await QueryAsync(
            connection,
            ColumnSql,
            static r =>
            {
                int indexId = Int(r, 0);
                bool included = Bool(r, 1);
                int keyOrdinal = Int(r, 2);
                bool descending = Bool(r, 4);
                string column = Str(r, 5);
                return new IndexColumnRow(indexId, included, keyOrdinal, descending, column);
            },
            key,
            cancellationToken).ConfigureAwait(false);

        Dictionary<int, List<IndexColumnRow>> keyColumns = [];
        Dictionary<int, List<string>> includedColumns = [];
        foreach (IndexColumnRow row in columnRows)
        {
            if (row.IsIncluded)
            {
                if (!includedColumns.TryGetValue(row.IndexId, out List<string>? included))
                {
                    included = [];
                    includedColumns[row.IndexId] = included;
                }
                included.Add(row.Column);
            }
            else
            {
                if (!keyColumns.TryGetValue(row.IndexId, out List<IndexColumnRow>? keys))
                {
                    keys = [];
                    keyColumns[row.IndexId] = keys;
                }
                keys.Add(row);
            }
        }

        return await QueryAsync(
            connection,
            IndexSql,
            r =>
            {
                int indexId = Int(r, 0);
                string name = StrOrNull(r, 1) ?? "";
                bool unique = Bool(r, 2);
                bool primary = Bool(r, 3);
                bool uniqueConstraint = Bool(r, 4);
                string typeDesc = Str(r, 5);
                string? filter = StrOrNull(r, 6);
                bool disabled = Bool(r, 7);

                IReadOnlyList<IndexColumnRow> keys = keyColumns.TryGetValue(indexId, out List<IndexColumnRow>? k) ? k : [];
                IReadOnlyList<string> included = includedColumns.TryGetValue(indexId, out List<string>? inc) ? inc : [];
                return new SqlIndex(
                    name,
                    [.. keys.Select(static x => x.Column)],
                    unique,
                    primary,
                    // Kind 用逗号拼一串机器可读的标记(与 PG 包同一条约定):契约上没有
                    // "唯一约束 / 筛选索引 / 已禁用"这三格,而它们各自有实际后果 ——
                    // 唯一约束的删除路径是 ALTER TABLE DROP CONSTRAINT 而不是 DROP INDEX;
                    // 筛选索引只覆盖一部分行;已禁用的索引优化器根本不用(用户会盯着一个
                    // "存在但从不生效"的索引查半天慢查询)。丢进 Kind 至少让它们仍然可见。
                    Marked(typeDesc, uniqueConstraint, filter, disabled),
                    BuildIndexDefinition(unique, typeDesc, keys, included, filter, disabled));
            },
            key,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读外键。<c>IDbMaintenance</c> 里**一个外键方法都没有**(§2.3),这条只能自己查。
    /// <para>
    /// 一行一列对,按 <c>constraint_column_id</c> 排序 —— 复合外键的列顺序就是靠它跟目标列对上的,
    /// 顺序错了外键关系图会画出根本不存在的对应。
    /// </para>
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="key">schema 与对象名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>外键清单。</returns>
    private static async Task<IReadOnlyList<SqlForeignKey>> ReadForeignKeysAsync(
        DbConnection connection, object?[] key, CancellationToken cancellationToken)
    {
        const string Sql = $"""
            SELECT fk.name, fk.object_id,
                   rs.name AS referenced_schema, ro.name AS referenced_table,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc,
                   fkc.constraint_column_id, pc.name AS parent_column, rc.name AS referenced_column
            FROM sys.foreign_keys fk
            JOIN sys.objects po ON po.object_id = fk.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = po.schema_id
            JOIN sys.objects ro ON ro.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = ro.schema_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE ps.name = {SchemaParam} AND po.name = @p1
            ORDER BY fk.name, fkc.constraint_column_id
            """;

        List<ForeignKeyRow> rows = await QueryAsync(
            connection,
            Sql,
            static r =>
            {
                string name = Str(r, 0);
                int objectId = Int(r, 1);
                string referencedSchema = Str(r, 2);
                string referencedTable = Str(r, 3);
                string onDelete = Str(r, 4);
                string onUpdate = Str(r, 5);
                int ordinal = Int(r, 6);
                string column = Str(r, 7);
                string referencedColumn = Str(r, 8);
                return new ForeignKeyRow(
                    objectId, name, referencedSchema, referencedTable, onDelete, onUpdate, ordinal, column, referencedColumn);
            },
            key,
            cancellationToken).ConfigureAwait(false);

        // 按 object_id 分组而不是按名字:外键名在库内唯一,但用 id 分组不受排序规则影响。
        return Fold(
            rows,
            static row => row.ObjectId,
            static (_, parts) =>
            {
                ForeignKeyRow[] ordered = [.. parts.OrderBy(static x => x.Ordinal)];
                ForeignKeyRow head = ordered[0];
                return new SqlForeignKey(
                    head.Name,
                    [.. ordered.Select(static x => x.Column)],
                    head.ReferencedSchema,
                    head.ReferencedTable,
                    [.. ordered.Select(static x => x.ReferencedColumn)],
                    head.OnDelete,
                    head.OnUpdate);
            });
    }

    /// <summary>
    /// 把目录里的宽度/精度还原成<b>能直接写进 DDL 的类型原文</b>。
    /// <para>三条真机踩出来的规矩(§3.6):</para>
    /// <list type="number">
    /// <item><c>max_length = -1</c> 是 <c>(max)</c>,不是长度 -1。界面上出现 <c>nvarchar(-1)</c> 就是这里漏了。</item>
    /// <item><c>nchar</c> / <c>nvarchar</c> 的 <c>max_length</c> 是<b>字节数</b>,要除以 2:
    /// <c>nvarchar(50)</c> 在目录里是 100。</item>
    /// <item><c>xml</c> / <c>text</c> / <c>image</c> 这些也可能是 -1 或固定值,但它们<b>不带长度</b>,
    /// 套上 <c>(max)</c> 就成了非法类型。所以是白名单匹配,不是"看到 -1 就加 (max)"。</item>
    /// </list>
    /// </summary>
    /// <param name="typeName">类型名(来自 <c>sys.types.name</c>)。</param>
    /// <param name="isUserDefined">是否用户定义类型。</param>
    /// <param name="maxLength">目录里的 <c>max_length</c>(字节)。</param>
    /// <param name="precision">数值精度。</param>
    /// <param name="scale">小数位 / 时间精度。</param>
    /// <returns>类型原文。</returns>
    private static string RenderDataType(string typeName, bool isUserDefined, int maxLength, int precision, int scale)
    {
        // 别名类型与 CLR 类型:长度精度都在类型定义里,名字本身就是完整形态。
        if (isUserDefined)
        {
            return typeName;
        }
        return typeName.ToLowerInvariant() switch
        {
            "char" or "varchar" or "binary" or "varbinary" =>
                maxLength < 0 ? $"{typeName}(max)" : $"{typeName}({Num(maxLength)})",
            // 双字节字符类型:目录记字节数。
            "nchar" or "nvarchar" =>
                maxLength < 0 ? $"{typeName}(max)" : $"{typeName}({Num(maxLength / 2)})",
            "decimal" or "numeric" => $"{typeName}({Num(precision)},{Num(scale)})",
            // 这三个的括号里是**时间精度**(scale),不是长度。
            "datetime2" or "time" or "datetimeoffset" => $"{typeName}({Num(scale)})",
            // SQL Server 2025 的向量类型:维数没有独立列,藏在 max_length 里
            // (8 字节头 + 每维 4 字节)。算不出正数就退回裸名字,别编一个假维数。
            "vector" => maxLength > 8 ? $"{typeName}({Num((maxLength - 8) / 4)})" : typeName,
            // int/bigint/bit/uniqueidentifier/money/date/xml/text/ntext/image/sysname/
            // float(声明 25~53 一律落成 precision 53)/real/geography/… 都不带括号。
            _ => typeName
        };
    }

    /// <summary>
    /// 拼一行给人看的索引摘要。
    /// <para>
    /// INCLUDE 列、键列的<b>升降序</b>、筛选条件原文、是否禁用 —— 契约的结构化字段里都没有它们的位置,
    /// 于是<b>只出现在这里</b>。INCLUDE 列尤其不能进 <see cref="SqlIndex.Columns" />:
    /// 它们不是键列,混进去会误导"最左前缀"的判断;但完全不显示又会让人以为这是个普通单列索引。
    /// </para>
    /// </summary>
    /// <param name="isUnique">是否唯一。</param>
    /// <param name="typeDesc">种类原文(<c>type_desc</c>)。</param>
    /// <param name="keys">键列。</param>
    /// <param name="included">INCLUDE 列。</param>
    /// <param name="filter">筛选条件原文。</param>
    /// <param name="isDisabled">是否已禁用。</param>
    /// <returns>摘要文本。</returns>
    private string BuildIndexDefinition(
        bool isUnique,
        string typeDesc,
        IReadOnlyList<IndexColumnRow> keys,
        IReadOnlyList<string> included,
        string? filter,
        bool isDisabled)
    {
        var text = new StringBuilder();
        if (isUnique)
        {
            _ = text.Append("UNIQUE ");
        }
        _ = text.Append(typeDesc).Append(" (");
        for (int i = 0; i < keys.Count; i++)
        {
            if (i > 0)
            {
                _ = text.Append(", ");
            }
            _ = text.Append(QuoteIdentifier(keys[i].Column));
            if (keys[i].IsDescending)
            {
                _ = text.Append(" DESC");
            }
        }
        _ = text.Append(')');
        if (included.Count > 0)
        {
            _ = text.Append(" INCLUDE (");
            for (int i = 0; i < included.Count; i++)
            {
                if (i > 0)
                {
                    _ = text.Append(", ");
                }
                _ = text.Append(QuoteIdentifier(included[i]));
            }
            _ = text.Append(')');
        }
        if (!string.IsNullOrEmpty(filter))
        {
            _ = text.Append(" WHERE ").Append(filter);
        }
        if (isDisabled)
        {
            _ = text.Append(" [DISABLED]");
        }
        return text.ToString();
    }

    /// <summary>把契约装不下的三个索引标志拼成机器可读的记号(与 PG 包的 <c>,expression</c> / <c>,partial</c> 同一条约定)。</summary>
    /// <param name="typeDesc">种类原文。</param>
    /// <param name="isUniqueConstraint">是否唯一约束背后的索引。</param>
    /// <param name="filter">筛选条件原文。</param>
    /// <param name="isDisabled">是否已禁用。</param>
    /// <returns>带记号的种类。</returns>
    private static string Marked(string typeDesc, bool isUniqueConstraint, string? filter, bool isDisabled)
    {
        string kind = typeDesc;
        if (isUniqueConstraint)
        {
            kind += ",unique-constraint";
        }
        if (!string.IsNullOrEmpty(filter))
        {
            kind += ",filtered";
        }
        if (isDisabled)
        {
            kind += ",disabled";
        }
        return kind;
    }

    /// <summary>
    /// 剥掉一段表达式最外层<b>配对</b>的圆括号。
    /// <para>
    /// 目录表存默认值时会加括号(SQL Server 的 <c>((1))</c>、<c>(newid())</c>),直接显示很难看。
    /// 但只能剥"整段被一对括号包住"的那种 —— <c>(a)+(b)</c> 首尾也是括号,剥了就成了 <c>a)+(b</c>。
    /// 括号计数时跳过字符串字面量,免得 <c>(')')</c> 把配对算歪。
    /// </para>
    /// </summary>
    /// <param name="text">原文。</param>
    /// <returns>剥完的文本。</returns>
    private static string StripOuterParentheses(string text)
    {
        string current = text.Trim();
        while (current.Length >= 2 && current[0] == '(' && current[^1] == ')')
        {
            int depth = 0;
            bool inString = false;
            bool matchesLast = false;
            for (int i = 0; i < current.Length; i++)
            {
                char c = current[i];
                if (inString)
                {
                    if (c == '\'')
                    {
                        inString = false;
                    }
                    continue;
                }
                switch (c)
                {
                    case '\'':
                        inString = true;
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        if (depth == 0)
                        {
                            matchesLast = i == current.Length - 1;
                            i = current.Length; // 提前收工
                        }
                        break;
                    default:
                        break;
                }
            }
            if (!matchesLast)
            {
                break;
            }
            current = current[1..^1].Trim();
        }
        return current;
    }

    /// <summary>
    /// 判断一段默认值文本是不是<b>字面量</b>(数字 / 字符串 / <c>NULL</c> / 二进制),
    /// 不是就说明它要交给服务端求值。
    /// <para>
    /// 这一格的全部意义在于把 <c>sysutcdatetime()</c>(每行求值一次)与字符串
    /// <c>'sysutcdatetime()'</c>(一个碰巧长这样的常量)分开 —— 复制一行、生成 DDL、
    /// 给新行填默认值时判错一边,就是"默认值变成了固定的那一秒"或者"建表直接语法错"。
    /// </para>
    /// </summary>
    /// <param name="text">已剥掉外层括号的默认值文本。</param>
    /// <returns>是字面量则为真。</returns>
    private static bool IsLiteralDefault(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        string value = text.Trim();
        if (string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // 字符串字面量:允许 N 前缀;内部单引号必须成对,否则它其实是拼接表达式的一部分。
        string body = value;
        if (body.Length > 1 && body[0] is 'N' or 'n' && body[1] == '\'')
        {
            body = body[1..];
        }
        if (body.Length >= 2 && body[0] == '\'' && body[^1] == '\'')
        {
            int quotes = 0;
            for (int i = 1; i < body.Length - 1; i++)
            {
                if (body[i] == '\'')
                {
                    quotes++;
                }
            }
            return quotes % 2 == 0;
        }
        // 二进制字面量。
        if (body.Length > 2 && body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            foreach (char c in body.AsSpan(2))
            {
                if (!char.IsAsciiHexDigit(c))
                {
                    return false;
                }
            }
            return true;
        }
        // 数字字面量(含符号、小数与科学计数)。
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// 一条 SQL 的<b>最外层</b>有没有 <c>ORDER BY</c>。
    /// <para>
    /// 只把括号深度为 0 的算数,所以 <c>ROW_NUMBER() OVER (ORDER BY …)</c>、子查询、CTE 正文里的排序
    /// 都不会被误认;引号、方括号/双引号定界的标识符、行注释与<b>可嵌套</b>的块注释都要跳过 ——
    /// 一个名叫 <c>[order by]</c> 的列或者一句 <c>-- order by</c> 的注释都不该让判断翻车。
    /// </para>
    /// </summary>
    /// <param name="sql">SQL 原文(已剥掉尾分号)。</param>
    /// <returns>最外层有 <c>ORDER BY</c> 则为真。</returns>
    private static bool HasTopLevelOrderBy(string sql)
    {
        int depth = 0;
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            switch (c)
            {
                case '\'':
                    i = SkipDelimited(sql, i, '\'');
                    break;
                case '"':
                    i = SkipDelimited(sql, i, '"');
                    break;
                case '[':
                    i = SkipDelimited(sql, i, ']');
                    break;
                case '-' when i + 1 < sql.Length && sql[i + 1] == '-':
                    int newline = sql.IndexOf('\n', i);
                    i = newline < 0 ? sql.Length : newline;
                    break;
                case '/' when i + 1 < sql.Length && sql[i + 1] == '*':
                    i = SkipBlockComment(sql, i);
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (depth > 0)
                    {
                        depth--;
                    }
                    break;
                default:
                    if (depth == 0 && c is 'o' or 'O' && IsOrderByAt(sql, i))
                    {
                        return true;
                    }
                    break;
            }
        }
        return false;
    }

    /// <summary>某个位置起是不是一个独立的 <c>ORDER BY</c> 词组。</summary>
    /// <param name="sql">SQL 原文。</param>
    /// <param name="start">起始下标(已知是 <c>o</c> / <c>O</c>)。</param>
    /// <returns>是则为真。</returns>
    private static bool IsOrderByAt(string sql, int start)
    {
        if (start > 0 && IsIdentifierChar(sql[start - 1]))
        {
            return false;
        }
        ReadOnlySpan<char> span = sql.AsSpan(start);
        if (!span.StartsWith("ORDER", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        int i = start + 5;
        int before = i;
        while (i < sql.Length && char.IsWhiteSpace(sql[i]))
        {
            i++;
        }
        // ORDER 与 BY 之间至少要有一个空白,否则那是 ORDERBY 这么个标识符。
        if (i == before)
        {
            return false;
        }
        if (!sql.AsSpan(i).StartsWith("BY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        int after = i + 2;
        return after >= sql.Length || !IsIdentifierChar(sql[after]);
    }

    /// <summary>标识符里合法的字符(T-SQL 允许 <c>@</c> / <c>#</c> / <c>$</c> / <c>_</c>)。</summary>
    /// <param name="c">字符。</param>
    /// <returns>是则为真。</returns>
    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';

    /// <summary>跳过一段定界文本(引号 / 方括号 / 双引号),定界符加倍算转义。</summary>
    /// <param name="sql">SQL 原文。</param>
    /// <param name="start">起始定界符的下标。</param>
    /// <param name="close">结束定界符。</param>
    /// <returns>结束定界符的下标;没有闭合时是末尾。</returns>
    private static int SkipDelimited(string sql, int start, char close)
    {
        for (int i = start + 1; i < sql.Length; i++)
        {
            if (sql[i] != close)
            {
                continue;
            }
            if (i + 1 < sql.Length && sql[i + 1] == close)
            {
                i++;   // 加倍的定界符是被转义的一个,不是收尾。
                continue;
            }
            return i;
        }
        return sql.Length;
    }

    /// <summary>跳过一段块注释。T-SQL 的块注释<b>可以嵌套</b>,所以要计数而不是找第一个 <c>*&#47;</c>。</summary>
    /// <param name="sql">SQL 原文。</param>
    /// <param name="start"><c>/</c> 的下标。</param>
    /// <returns>注释结束处的下标;没有闭合时是末尾。</returns>
    private static int SkipBlockComment(string sql, int start)
    {
        int depth = 0;
        for (int i = start; i < sql.Length - 1; i++)
        {
            if (sql[i] == '/' && sql[i + 1] == '*')
            {
                depth++;
                i++;
            }
            else if (sql[i] == '*' && sql[i + 1] == '/')
            {
                depth--;
                i++;
                if (depth == 0)
                {
                    return i;
                }
            }
        }
        return sql.Length;
    }

    /// <summary>
    /// 把一个字符串包成 SQL 的 <b>Unicode 字符串字面量</b>(单引号加倍)。
    /// <para>
    /// 只在"名字必须以字符串身份出现"的地方用(<c>OBJECT_ID(N'…')</c> 这类),
    /// 那时先过 <see cref="DialectPackBase.QuoteIdentifier" /> 再过这里 —— 两层各挡一种注入形态。
    /// 普通取值一律用参数,不要用它拼值。
    /// </para>
    /// <para>
    /// <c>N</c> 前缀不是装饰:目录里有中文名的对象很常见,少个 <c>N</c> 就会在非 Unicode 排序规则的库上
    /// 被降级成 <c>varchar</c> 而匹配不到。
    /// </para>
    /// </summary>
    /// <param name="value">原文。</param>
    /// <returns>SQL 字面量。</returns>
    private static string Literal(string value) =>
        $"N'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>数字转文本(恒用不变文化,避免某些区域设置给整数加千位分隔符)。</summary>
    /// <param name="value">数值。</param>
    /// <returns>文本。</returns>
    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>sys.index_columns</c> 的一行(每个索引每一列一行)。</summary>
    /// <param name="IndexId">索引 id。</param>
    /// <param name="IsIncluded">是不是 INCLUDE 列(不是键列)。</param>
    /// <param name="KeyOrdinal">在键里的位置(从 1 起)。</param>
    /// <param name="IsDescending">该列在索引里是否降序。</param>
    /// <param name="Column">列名。</param>
    private sealed record IndexColumnRow(int IndexId, bool IsIncluded, int KeyOrdinal, bool IsDescending, string Column);

    /// <summary><c>sys.foreign_keys</c> ⨝ <c>sys.foreign_key_columns</c> 的一行(每条外键每一列对一行)。</summary>
    /// <param name="ObjectId">约束的 object_id(分组键)。</param>
    /// <param name="Name">约束名。</param>
    /// <param name="ReferencedSchema">目标 schema。</param>
    /// <param name="ReferencedTable">目标表。</param>
    /// <param name="OnDelete">删除时动作。</param>
    /// <param name="OnUpdate">更新时动作。</param>
    /// <param name="Ordinal">列对在约束里的序号。</param>
    /// <param name="Column">本表列。</param>
    /// <param name="ReferencedColumn">目标列。</param>
    private sealed record ForeignKeyRow(
        int ObjectId,
        string Name,
        string ReferencedSchema,
        string ReferencedTable,
        string OnDelete,
        string OnUpdate,
        int Ordinal,
        string Column,
        string ReferencedColumn);
}
