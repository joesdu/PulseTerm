using System.Data.Common;
using System.Globalization;
using System.Text;

namespace VelaShell.Plugin.Sql.Metadata;

/// <summary>
/// MySQL / MariaDB 的方言包。**全部元数据直查 <c>information_schema</c>,一个 <c>IDbMaintenance</c> 方法都不调。**
/// <para>
/// 这不是洁癖,是设计文档 §2.3 / §3.7 在这台 8.4.11 真机上逐条量过的账:
/// <c>IsIdentity</c> 恒 <see langword="true" />(一条 SQL 都不发)、
/// <c>GetPrimaries</c> 与 <c>GetIsIdentities</c> 共用一个大小写不敏感的表名缓存
/// (两张只差大小写的表并存时会把 A 表的主键当成 B 表的返回 —— 拿错主键 = UPDATE 打到别的行)、
/// <c>GetIndexList</c> 是 <c>SHOW INDEX</c> 的 <c>Key_name</c> 原样输出不去重(7 个索引报成 11 项、丢唯一性)、
/// <c>Length</c> 是从 <c>COLUMN_TYPE</c> 括号里 <c>SUBSTRING</c> 出来的(<c>datetime(3)</c> 的 3 是秒的小数位、
/// <c>tinyint(1)</c> 的 1 是显示宽度、<c>text</c>/<c>json</c>/<c>enum</c> 一律 0)、
/// 生成列与 <c>ON UPDATE CURRENT_TIMESTAMP</c> 完全不可见、视图的列返回 0 且不抛异常。
/// 一个会说谎的元数据源比没有更坏 —— 界面会如实地把假数据画出来。
/// </para>
/// <para>
/// <b>MySQL 没有 schema 这一级</b>:它的 "schema" 就是 database,<c>information_schema</c> 里两者是同一列
/// (<c>TABLE_SCHEMA</c>)。所以 <see cref="HasSchemas" /> 为 <see langword="false" />、
/// <see cref="ListSchemasAsync" /> 恒返回空,而<b>库名被填进 <see cref="SqlObject.Schema" /></b> ——
/// 这样限定名(<c>`库`.`表`</c>)与估算行数语句都不依赖"连接当前是哪个库",
/// 用户开着 A 库的连接去看 B 库的表是常态。
/// </para>
/// <para>
/// 全部比对都走参数(<c>@p0</c> = 库名、<c>@p1</c> = 表名),用户给的标识符<b>永不拼进 SQL</b>;
/// 确实要拼的地方(<see cref="ShowCreateSql" />)走 <see cref="DialectPackBase.QuoteQualified" />,
/// 反引号加倍(§5.4.4 实测 SqlSugar 的 <c>AS(表名)</c> 那条路不转义、能删表)。
/// </para>
/// </summary>
internal sealed class MySqlPack : DialectPackBase
{
    /// <summary>
    /// MySQL 自带的四个库。它们仍然<b>列出来</b>(用户确实要去 <c>mysql.user</c> 看权限、
    /// 去 <c>performance_schema</c> 看会话),只是收进"系统对象"分组 —— 不列出来是另一种撒谎。
    /// <para>
    /// <b>这个判据以前的下场值得记一笔</b>:它算出来的结果被写进 <c>SqlObject.Comment</c>
    /// 的一个 <c>"@system"</c> 记号里,指望对象树认。而对象树一个字都没读,
    /// 于是 <c>information_schema</c> / <c>mysql</c> / <c>performance_schema</c> / <c>sys</c>
    /// 就按字母序插在业务库中间(真机上是 14 个库里的 4 个)。
    /// 现在它落在 <see cref="SqlObject.IsSystem" /> 上 —— 模型上的一格,树读得到、单测点得着。
    /// </para>
    /// </summary>
    private static readonly string[] SystemDatabases =
        ["information_schema", "mysql", "performance_schema", "sys"];

    /// <summary>
    /// 库名参数为空时回落到连接当前库。写成 SQL 片段而不是在 C# 侧判断,
    /// 是为了让"当前库"这件事由服务端回答 —— 客户端记的那个库名会被用户的 <c>USE</c> 语句改掉。
    /// </summary>
    private const string SchemaParam = "COALESCE(NULLIF(@p0, ''), DATABASE())";

    /// <inheritdoc />
    public override SqlDialect Dialect => SqlDialect.MySql;

    /// <inheritdoc />
    /// <remarks>MySQL 的 schema 就是 database,对象树少一层。</remarks>
    public override bool HasSchemas => false;

    /// <inheritdoc />
    public override bool HasDatabases => true;

    /// <inheritdoc />
    /// <remarks>
    /// <c>information_schema</c> 是**服务端级**的:一条连接就看得见所有库的表与例程
    /// (<c>TABLE_SCHEMA</c> 只是一列过滤条件)。所以对象树不必按库另开连接 —— 与 PG 相反。
    /// </remarks>
    public override bool MetadataSpansCatalogs => true;

    /// <inheritdoc />
    public override bool HasRoutines => true;

    /// <inheritdoc />
    /// <remarks>MySQL 没有序列(自增列是列属性,不是独立对象);MariaDB 有,但那是另一个方言。</remarks>
    public override bool HasSequences => false;

    /// <summary>反引号。<b>注意服务端 <c>sql_mode</c> 开了 <c>ANSI_QUOTES</c> 时双引号才是标识符</b>(§3.7),
    /// 那种配置下这里要换成双引号 —— 探测 <c>sql_mode</c> 是连接层的事,尚未接上(见交付说明)。</summary>
    protected override (char Open, char Close) Delimiters => ('`', '`');

    /// <summary>某个库是不是 MySQL 自带的系统库。</summary>
    /// <param name="name">库名。</param>
    /// <returns>是则 <see langword="true" />。</returns>
    public static bool IsSystemDatabase(string name) =>
        Array.Exists(SystemDatabases, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SqlObject>> ListDatabasesAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        // SHOW DATABASES 也能拿到同一份表,但它的结果列名随版本变(Database / Database (%)),
        // 而 SCHEMATA 是稳定的目录视图 —— 元数据通道一律走目录视图。
        const string Sql = """
            SELECT SCHEMA_NAME
              FROM information_schema.SCHEMATA
             ORDER BY SCHEMA_NAME
            """;
        List<SqlObject> databases = await QueryAsync(
            connection,
            Sql,
            r =>
            {
                string name = Str(r, 0);
                return new SqlObject(SqlObjectKind.Database, name, IsSystem: IsSystemDatabase(name));
            },
            parameters: null,
            cancellationToken).ConfigureAwait(false);
        return databases;
    }

    /// <inheritdoc />
    /// <remarks>
    /// MySQL 没有 schema 这一级,恒空。**不要在这里把库当 schema 返回** ——
    /// 那会让对象树画出"库 → 同名 schema → 表"的假三层。
    /// </remarks>
    public override Task<IReadOnlyList<SqlObject>> ListSchemasAsync(
        DbConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SqlObject>>([]);

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="schema" /> 在 MySQL 上就是<b>库名</b>(见类注释);传空则用连接当前库。
    /// 表与视图一次查完 —— 分两次查会让对象树展开时抖两下,而"表 (37)"那个计数要等两条都回来。
    /// </remarks>
    public override async Task<IReadOnlyList<SqlObject>> ListRelationsAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        const string Sql = $"""
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE, TABLE_COMMENT, TABLE_ROWS
              FROM information_schema.TABLES
             WHERE TABLE_SCHEMA = {SchemaParam}
             ORDER BY TABLE_TYPE, TABLE_NAME
            """;
        List<SqlObject> relations = await QueryAsync(
            connection,
            Sql,
            r =>
            {
                // SequentialAccess:必须按列序读,顺序不能与 SELECT 列表错开。
                string owner = Str(r, 0);
                string name = Str(r, 1);
                string type = Str(r, 2);
                string comment = Str(r, 3);
                long? rows = LongOrNull(r, 4);
                SqlObjectKind kind = type switch
                {
                    "VIEW" or "SYSTEM VIEW" => SqlObjectKind.View,
                    _ => SqlObjectKind.Table
                };
                return new SqlObject(
                    kind,
                    name,
                    owner,
                    // MySQL 把视图的 TABLE_COMMENT 恒写成字面量 "VIEW" —— 那不是注释,是类型。
                    // 原样带出去的话对象树上每个视图后面都跟一句 "VIEW",这正是 §3.7 说的
                    // "视图的 Description 是假的"。
                    kind == SqlObjectKind.View ? "" : comment,
                    // TABLE_ROWS 对视图恒 NULL;对 InnoDB 表是**采样估算**,只能当"约"用(见 EstimateRowCountSql)。
                    kind == SqlObjectKind.View ? null : rows,
                    // 系统性由**所在库**决定:mysql / performance_schema 里的每一张表都是系统表,
                    // 而业务库里恰好叫 user 的表不是。按库判而不是按表名判,后者会误伤。
                    IsSystemDatabase(owner));
            },
            [schema ?? ""],
            cancellationToken).ConfigureAwait(false);
        return relations;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 视图<b>照样拿得到列</b> —— <c>information_schema.COLUMNS</c> 对视图与表一视同仁,
    /// 而 <c>GetColumnInfosByTableName</c> 对视图返回 0 列且不抛异常(§2.3)。
    /// 视图没有索引也没有外键,那两条查询直接省掉:MySQL 里视图在 <c>STATISTICS</c> 与
    /// <c>KEY_COLUMN_USAGE</c> 里一行都没有,发过去只是白跑两个来回。
    /// </remarks>
    public override async Task<IReadOnlyList<SqlObject>> ListRoutinesAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        // MySQL 不允许同名重载,所以名字本身就是标识 —— 不必像 PG 那样带形参签名。
        const string Sql = $"""
            SELECT ROUTINE_SCHEMA, ROUTINE_NAME, ROUTINE_TYPE, ROUTINE_COMMENT
              FROM information_schema.ROUTINES
             WHERE ROUTINE_SCHEMA = {SchemaParam}
             ORDER BY ROUTINE_NAME
            """;
        return await QueryAsync(
            connection,
            Sql,
            r =>
            {
                // SequentialAccess:必须按列序读。
                string owner = Str(r, 0);
                string name = Str(r, 1);
                bool procedure = Str(r, 2).Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase);
                string comment = Str(r, 3);
                return new SqlObject(
                    procedure ? SqlObjectKind.Procedure : SqlObjectKind.Function,
                    name,
                    owner,
                    comment,
                    IsSystem: IsSystemDatabase(owner));
            },
            [schema ?? ""],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<SqlTableSchema> DescribeAsync(
        DbConnection connection, SqlObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        object?[] parameters = [target.Schema, target.Name];

        List<SqlIndex> indexes = target.Kind == SqlObjectKind.View
            ? []
            : await ReadIndexesAsync(connection, parameters, cancellationToken).ConfigureAwait(false);
        List<SqlForeignKey> foreignKeys = target.Kind == SqlObjectKind.View
            ? []
            : await ReadForeignKeysAsync(connection, parameters, cancellationToken).ConfigureAwait(false);

        // 主键成员从上面那份 PRIMARY 索引里取,而不是看 COLUMNS.COLUMN_KEY。
        // 两条理由:① COLUMN_KEY='PRI' 在**没有主键**的表上会落到第一个唯一索引的首列上,
        //           把一个非主键列画成主键;② 同一份来源保证"列上标的主键"与"索引里列的主键"
        //           不可能互相打架 —— 网格就是拿这个当 UPDATE 的定位依据的。
        HashSet<string> primaryKey = new(
            indexes.FirstOrDefault(i => i.IsPrimaryKey)?.Columns ?? [],
            StringComparer.Ordinal);

        const string Sql = $"""
            SELECT COLUMN_NAME, ORDINAL_POSITION, COLUMN_TYPE, IS_NULLABLE,
                   COLUMN_DEFAULT, EXTRA, COLUMN_COMMENT, GENERATION_EXPRESSION
              FROM information_schema.COLUMNS
             WHERE TABLE_SCHEMA = {SchemaParam} AND TABLE_NAME = @p1
             ORDER BY ORDINAL_POSITION
            """;
        List<SqlColumn> columns = await QueryAsync(
            connection,
            Sql,
            r =>
            {
                string name = Str(r, 0);
                int ordinal = Int(r, 1);
                // COLUMN_TYPE 就是**完整原生形态**:varchar(50) / decimal(12,3) / int unsigned /
                // enum('新建','处理中','已完成') / tinyint(1)。这正是 SqlSugar 拆成 DataType+Length
                // 之后丢掉的那份信息(枚举取值整个没了、unsigned 没了、精度标度只剩精度)。
                string dataType = Str(r, 2);
                bool nullable = string.Equals(Str(r, 3), "YES", StringComparison.OrdinalIgnoreCase);
                string? defaultValue = StrOrNull(r, 4);
                string extra = Str(r, 5);
                string comment = Str(r, 6);
                string generation = Str(r, 7);
                return new SqlColumn(
                    name,
                    ordinal,
                    dataType,
                    nullable,
                    primaryKey.Contains(name),
                    extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase),
                    IsGenerated(extra, generation),
                    defaultValue,
                    // **这一格是本包最容易写错的地方。**
                    // `DEFAULT CURRENT_TIMESTAMP` 与 `DEFAULT 'CURRENT_TIMESTAMP'`(字符串字面量)
                    // 在 COLUMN_DEFAULT 里长得**一模一样**,都是 CURRENT_TIMESTAMP、都不带引号;
                    // 唯一的分水岭是 EXTRA 里的 DEFAULT_GENERATED(真机逐列验过)。
                    // 表设计器拿 DefaultValue 生成 DDL 时若不看这一格,就会把表达式加上引号变成字符串,
                    // 或者把字符串脱引号变成表达式 —— 两边都是静默的数据语义改写。
                    extra.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase),
                    comment);
            },
            parameters,
            cancellationToken).ConfigureAwait(false);

        return new(target, columns, indexes, foreignKeys);
    }

    /// <inheritdoc />
    public override string ApplyPaging(string innerSql, int offset, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(innerSql);
        // 用户手敲的 SQL 十有八九以分号收尾。不剥掉的话 LIMIT 落在语句结束之后,
        // 报的是一句让人摸不着头脑的语法错 —— 而用户看着自己那条能跑的 SQL 只会以为是插件坏了。
        string body = innerSql.TrimEnd().TrimEnd(';').TrimEnd();
        return $"{body}\nLIMIT {Num(Math.Max(0, limit))} OFFSET {Num(Math.Max(0, offset))}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>这个数是"约"不是"是"。</b> InnoDB 的 <c>TABLE_ROWS</c> 由随机采样几个索引页外推,
    /// 同一张表连查两次都可能不一样,偏差几倍是常态(空表也可能报出几十行)。
    /// 它的用途只有一个:底栏秒回"约 N 行",<b>点了才做精确 <c>count(*)</c></b>(§7.3)。
    /// 拿它做分页总数、做"是否为空"的判断,都会错。
    /// </remarks>
    public override string? EstimateRowCountSql(SqlObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind != SqlObjectKind.Table)
        {
            // 视图在 TABLES 里 TABLE_ROWS 恒 NULL,给个恒 NULL 的语句不如明说拿不到。
            return null;
        }
        string schema = string.IsNullOrEmpty(target.Schema) ? "DATABASE()" : TextLiteral(target.Schema);
        return $"""
            SELECT TABLE_ROWS
              FROM information_schema.TABLES
             WHERE TABLE_SCHEMA = {schema}
               AND TABLE_NAME = {TextLiteral(target.Name)}
               AND TABLE_TYPE = 'BASE TABLE'
            """;
    }

    /// <inheritdoc />
    public override string? SessionIdSql => "SELECT CONNECTION_ID()";

    /// <inheritdoc />
    /// <remarks>
    /// MySQL 上取消**必须另开一条连接**发这条语句(§3.10):正在跑查询的那条连接自己发不出去,
    /// 而 <c>KILL QUERY</c> 只掐当前语句、保住会话(<c>KILL CONNECTION</c> 会把用户的临时表、
    /// 会话变量、未提交事务一起送走)。
    /// </remarks>
    public override string? CancelSessionSql(string sessionId)
    {
        // 会话 id 是本包自己从 SessionIdSql 查回来的十进制整数。仍然校验一遍再拼:
        // KILL 不接受参数,拼接是这里唯一的注入面,而"这个值一定是我自己查的"是一句
        // 靠调用方守的约定 —— 约定守不住的时候,校验是最后一道。
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
        return $"KILL QUERY {sessionId}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>结果不是一列。</b> <c>SHOW CREATE TABLE</c> 返回两列(表名、DDL)、
    /// <c>SHOW CREATE VIEW</c> 返回四列(视图名、DDL、character_set_client、collation_connection)——
    /// 两者都是<b>第 2 列(序号 1)</b>才是 DDL 原文。调用方读 <c>reader.GetString(1)</c>,
    /// 别用 <c>ExecuteScalar</c>(那拿到的是表名)。
    /// </remarks>
    public override string? ShowCreateSql(SqlObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Kind switch
        {
            SqlObjectKind.Table => $"SHOW CREATE TABLE {QuoteQualified(target)}",
            SqlObjectKind.View => $"SHOW CREATE VIEW {QuoteQualified(target)}",
            _ => null
        };
    }

    // ─────────────────────────── 运维面(M4) ───────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <b>两档是两种东西,连结果形状都不一样 —— 调用方渲染前必须先看这一段。</b>
    /// <list type="bullet">
    ///   <item>
    ///     <c>EXPLAIN &lt;sql&gt;</c>(<paramref name="analyze" /> = <see langword="false" />)吐的是
    ///     <b>传统 12 列表格</b>(<c>id</c>、<c>select_type</c>、<c>table</c>、<c>partitions</c>、<c>type</c>、
    ///     <c>possible_keys</c>、<c>key</c>、<c>key_len</c>、<c>ref</c>、<c>rows</c>、<c>filtered</c>、<c>Extra</c>),
    ///     每个执行单元一行;它<b>只做优化,不执行</b>。
    ///   </item>
    ///   <item>
    ///     <c>EXPLAIN ANALYZE &lt;sql&gt;</c>(8.0.18+)吐的是<b>一列</b>(列名就叫 <c>EXPLAIN</c>)的树形文本,
    ///     里面带真换行。拿渲染 12 列的那套去画它,只会得到一格长文本。
    ///   </item>
    /// </list>
    /// <para>
    /// <b>analyze 档会真的把语句跑完</b> —— 这不是文档上的说法,是量出来的:
    /// <c>EXPLAIN ANALYZE SELECT SLEEP(1) FROM &lt;两行的表&gt;</c> 在 8.4.11 上耗时 2.5 秒。
    /// 所以契约里那句"绿档之外不给 analyze"(§7.6)在 MySQL 上是硬要求。
    /// </para>
    /// <para>
    /// <b>一条容易被误当成护栏的实测结果,这里明确写下来:不要靠它。</b>
    /// 8.4.11 上 <c>EXPLAIN ANALYZE</c> 对 <c>UPDATE</c> / <c>DELETE</c> 只返回一行
    /// <c>&lt;not executable by iterator executor&gt;</c>,而且<b>确实没有改数据</b>(逐条回查过行数)。
    /// 但这是"当前版本的迭代器执行器还没接这些语句"的副产物,不是承诺 ——
    /// MySQL 一直在往 <c>EXPLAIN ANALYZE</c> 里加 DML 支持,某个小版本升上去它就会真删。
    /// 护栏必须留在调用方、按语句种类判,而不是指望这条返回值。
    /// </para>
    /// <para>
    /// <b><c>EXPLAIN</c> 只吃查询与 DML。</b> 实测 <c>EXPLAIN SHOW TABLES</c> 与
    /// <c>EXPLAIN CREATE TABLE ...</c> 都是 <c>1064</c> 语法错,而报错位置指在<b>用户那条 SQL 上</b> ——
    /// 用户看到的是"我的 SQL 有语法错误",可他那条单独跑起来好好的。
    /// 这一类要按 §7.8 翻成"这种语句没有执行计划",别把 1064 原样丢出去。
    /// </para>
    /// <para>
    /// 末尾分号先剥掉,理由与 <see cref="ApplyPaging" /> 同一条:编辑器切出来的语句常带终止符。
    /// (那边的剥法是内联的,属于 M2 已定稿的代码,这里不去动它;两处规则相同,改的时候要一起改。)
    /// </para>
    /// </remarks>
    public override string? ExplainSql(string innerSql, bool analyze)
    {
        ArgumentNullException.ThrowIfNull(innerSql);
        string body = StripTerminators(innerSql);
        return analyze ? $"EXPLAIN ANALYZE {body}" : $"EXPLAIN {body}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// 列严格按契约:<c>id</c>、<c>user</c>、<c>host</c>、<c>db</c>、<c>state</c>、<c>seconds</c>、<c>query</c>
    /// —— 调用方(<c>SqlOpsTabViewModel</c>)按<b>序号</b>读,列序不能动。
    /// <para>
    /// <b><c>state</c> 这一格是两列合出来的,这是刻意的。</b> MySQL 把"线程在干什么"拆成两列:
    /// <c>COMMAND</c> 是协议层动作(<c>Query</c> / <c>Sleep</c> / <c>Daemon</c>),
    /// <c>STATE</c> 是语句内部阶段(<c>Sending data</c> / <c>Waiting for table metadata lock</c>…)。
    /// 只取 <c>STATE</c> 的话<b>空闲连接整格是空的</b>,而"一堆连接挂着不干活"恰恰是运维最想一眼看见的;
    /// 只取 <c>COMMAND</c> 的话,正在跑的语句全都只显示 <c>Query</c>,等于什么都没说。
    /// 所以 <c>STATE</c> 有值时用它,空了才回落 <c>COMMAND</c>(至少说得出 <c>Sleep</c>)。
    /// </para>
    /// <para>
    /// <b><c>seconds</c> 不是"这条查询跑了多久"。</b> <c>TIME</c> 是"线程在<b>当前状态</b>里待了多少秒":
    /// <c>Sleep</c> 时它是空闲时长,只有 <c>Query</c> 时才是语句已耗时。按它排序找慢查询之前得先看 <c>state</c>,
    /// 否则会把一个挂了三小时的空闲连接读成"跑了三小时的查询"。
    /// </para>
    /// <para>
    /// <b>用 <c>information_schema.PROCESSLIST</c> 而不是 <c>performance_schema.processlist</c>,是明知它已弃用的选择。</b>
    /// 8.4.11 上查它会附一条警告(实测原文):<c>1287 'INFORMATION_SCHEMA.PROCESSLIST' is deprecated and will be
    /// removed in a future release. Please use performance_schema.processlist instead</c>。
    /// 仍然用它,因为另一条的失败方式更坏:<c>performance_schema.processlist</c> 是 8.0.22 才有的,
    /// 而且 performance_schema 关掉时它<b>返回空表而不是报错</b> ——
    /// 那正是 §7.8 说的"空白会被读成'一个会话都没有'"。会话列表宁可带一条弃用警告,也不能在半数配置上静默变空。
    /// (真被移除的那天这里要换,而且要在那之前先接上服务端版本探测。)
    /// </para>
    /// <para>
    /// <b>权限不足时它同样不报错,只是少给行</b>:没有 <c>PROCESS</c> 权限的账号<b>只看得见自己的线程</b>。
    /// 所以"只有一条会话"有两种读法(真的只有一个人连着 / 权限不够),行数很少时界面该把这一条提示出来。
    /// 另外它只列<b>前台线程</b>;InnoDB 的后台线程要去 <c>performance_schema.threads</c> 看,那不叫"会话"。
    /// </para>
    /// <para>
    /// 排序:<b>有语句在跑的排前面</b>(<c>INFO IS NOT NULL</c>),同组按已耗时倒序。
    /// 不拿 <c>COMMAND &lt;&gt; 'Sleep'</c> 分组,是因为 <c>event_scheduler</c> 那类常驻守护线程也不是 <c>Sleep</c>,
    /// 而它的 <c>TIME</c> 是服务器已运行的秒数(实测 48349),会稳稳占住第一行。
    /// </para>
    /// </remarks>
    public override string? SessionListSql => """
        SELECT ID AS `id`, USER AS `user`, HOST AS `host`, DB AS `db`,
               COALESCE(NULLIF(STATE, ''), COMMAND) AS `state`,
               TIME AS `seconds`, INFO AS `query`
          FROM information_schema.PROCESSLIST
         ORDER BY (INFO IS NOT NULL) DESC, TIME DESC, ID
        """;

    /// <inheritdoc />
    /// <remarks>
    /// 列严格按契约:<c>blocked_id</c>、<c>blocking_id</c>、<c>object</c>、<c>mode</c>、<c>query</c>。
    /// <para>
    /// <b>两个 id 给的是连接 id,不是 <c>THREAD_ID</c>。</b>
    /// <c>data_lock_waits</c> 只给 performance_schema 的 <c>THREAD_ID</c>,那个号<b>与会话列表里的 <c>id</c> 对不上</b>,
    /// 也不是 <c>KILL</c> 认的号。所以两边各 <c>LEFT JOIN</c> 一次 <c>performance_schema.threads</c>
    /// 换成 <c>PROCESSLIST_ID</c> —— 锁那一栏点出来的 id 必须能直接拿去会话栏里找、拿去杀。
    /// 用 <c>LEFT JOIN</c> 而不是 <c>JOIN</c>:持锁方的线程可能已经结束(XA 预备事务就是这样),
    /// 那时 <c>blocking_id</c> 是 <see langword="null" />,这比整行消失诚实 ——
    /// "有人锁着你、但那个连接已经没了"本身就是一条要看见的结论。
    /// </para>
    /// <para>
    /// <b><c>query</c> 是被阻塞方的语句,不是持锁方的。</b> 两条理由:
    /// ① 这一行的主语是被阻塞的会话(<c>blocked_id</c> 排第一),读起来才一致;
    /// ② 持锁方十有八九是"开着事务闲着"(<c>PROCESSLIST_INFO</c> 为 <see langword="null" />),
    /// 一整列几乎全空的格子不值得占契约仅有的五列之一。想看持锁方在干什么,拿 <c>blocking_id</c>
    /// 去会话栏里找 —— 那一栏就在同一页上。<b>这一格最长 1024 字节</b>
    /// (<c>performance_schema_max_sql_text_length</c> 的默认值,实测),长语句会被截断;
    /// 会话栏的 <c>query</c> 走 <c>information_schema</c>,没有这个截断。
    /// </para>
    /// <para>
    /// <b><c>mode</c> 把冲突的两边并在一格里</b>:<c>&lt;锁粒度&gt; &lt;要的模式&gt; &lt;- &lt;持有方的模式&gt;</c>,
    /// 实测形如 <c>RECORD X,REC_NOT_GAP &lt;- X,REC_NOT_GAP</c>。只给一边说不清冲突
    /// (<c>S</c> 撞 <c>X</c> 与 <c>X</c> 撞 <c>X</c> 是两种事,处理办法也不同),而契约的五列里没有第六格放持有方,
    /// 合并是这个列约定下唯一说得全的写法。<c>LOCK_TYPE</c>(<c>RECORD</c> / <c>TABLE</c>)一并放进来同理:
    /// 表锁与行锁的排障方向完全不同。
    /// </para>
    /// <para>
    /// <b>join 必须带上 <c>ENGINE</c>。</b> <c>data_locks</c> 的键是 <c>(ENGINE_LOCK_ID, ENGINE)</c> 两列,
    /// 锁 id 只在同一个引擎内唯一;少带一列,多引擎实例上会把两把不相干的锁配成一对。
    /// </para>
    /// <para>
    /// <b>performance_schema 被关掉时这条照样跑得通,只是空,不会报错</b> —— 契约要的正是这个:
    /// 表定义一直在(编译进服务端了),关掉的是采集,于是查出来 0 行。
    /// 代价是"没有锁"与"没在采集"长得一样,界面要把 <c>@@performance_schema</c> 一并显示出来才说得清。
    /// </para>
    /// <para>
    /// <b>版本前提:<c>data_locks</c> / <c>data_lock_waits</c> 是 MySQL 8.0 起才有的。</b>
    /// 5.7 上对应的是 <c>information_schema.INNODB_LOCKS</c> / <c>INNODB_LOCK_WAITS</c>(8.0 已删),
    /// MariaDB 走的又是另一套。契约这里是个<b>无参属性</b>,拿不到服务端版本,所以只能挑一条 ——
    /// 挑的是本插件 T0 承诺的 8.0+。在 5.7 / MariaDB 上它会以 <c>1109 Unknown table</c> 打回,
    /// 按 §7.8 翻成"该服务端版本不支持锁视图";等连接层把版本探测接上
    /// (与类注释里 <c>sql_mode</c> 那条是同一笔待办),这里再按版本分叉。
    /// </para>
    /// </remarks>
    public override string? LockListSql => """
        SELECT rt.PROCESSLIST_ID AS `blocked_id`,
               bt.PROCESSLIST_ID AS `blocking_id`,
               CONCAT(rl.OBJECT_SCHEMA, '.', rl.OBJECT_NAME,
                      IF(rl.INDEX_NAME IS NULL, '', CONCAT(' (', rl.INDEX_NAME, ')'))) AS `object`,
               CONCAT(rl.LOCK_TYPE, ' ', rl.LOCK_MODE, ' <- ', bl.LOCK_MODE) AS `mode`,
               rt.PROCESSLIST_INFO AS `query`
          FROM performance_schema.data_lock_waits w
          JOIN performance_schema.data_locks rl
            ON rl.ENGINE = w.ENGINE AND rl.ENGINE_LOCK_ID = w.REQUESTING_ENGINE_LOCK_ID
          JOIN performance_schema.data_locks bl
            ON bl.ENGINE = w.ENGINE AND bl.ENGINE_LOCK_ID = w.BLOCKING_ENGINE_LOCK_ID
          LEFT JOIN performance_schema.threads rt ON rt.THREAD_ID = w.REQUESTING_THREAD_ID
          LEFT JOIN performance_schema.threads bt ON bt.THREAD_ID = w.BLOCKING_THREAD_ID
         ORDER BY `blocked_id`, `blocking_id`
        """;

    /// <inheritdoc />
    /// <remarks>
    /// <b>整数一律不带显示宽度,这是这张表里最要紧的一条。</b>
    /// <c>INT(11)</c> 的 11 从来就不是长度(它不限制取值,只影响 <c>ZEROFILL</c> 补几个零),
    /// 而 8.0.17 起它已弃用:实测在 8.4.11 上建 <c>int(11)</c> 会拿到警告
    /// <c>1681 Integer display width is deprecated and will be removed in a future release</c>,
    /// 而且<b>存下来的类型是 <c>int</c>,括号被静默丢掉</b>。把 <c>INT(11)</c> 摆进下拉,
    /// 等于让用户选一个"服务端会当场改写掉"的选项 —— 建完回头一看类型对不上,最像插件出了 bug。
    /// <para>
    /// 同理不给 <c>TINYINT(1)</c> 而给 <c>BOOLEAN</c>:两者建出来<b>是同一个东西</b>
    /// (<c>COLUMN_TYPE</c> 都是 <c>tinyint(1)</c>,实测),但前者照样触发 1681 警告,后者一条警告都没有。
    /// 顺带一提,本插件的连接串把 <c>TreatTinyAsBoolean</c> 固定成 <c>false</c>,
    /// 所以这种列在网格里显示 0/1 而不是 <c>true</c>/<c>false</c> —— 类型下拉与网格显示对得上,是刻意的。
    /// </para>
    /// <para>
    /// 带括号的几项(<c>DECIMAL(10,2)</c>、<c>VARCHAR(255)</c>、<c>ENUM('a','b')</c>…)是<b>模板</b>,
    /// 填的是最常用的一组值,等着用户改。它们不能省成裸类型名:<c>VARCHAR</c> 不带长度是语法错误;
    /// <c>DECIMAL</c> 不带精度会<b>静默</b>变成 <c>DECIMAL(10,0)</c>(小数位直接没了),
    /// <c>CHAR</c> 不带长度静默变成 <c>CHAR(1)</c> —— 后两条都是实测。
    /// </para>
    /// <para>
    /// <c>TIMESTAMP</c> 排在 <c>DATETIME</c> 之后而不是之前,有两个要一并显示给用户的原因:
    /// ① 它只到 2038-01-19;② <c>explicit_defaults_for_timestamp</c> 关掉时(8.0.2 之前的默认,
    /// 以及不少沿用旧配置的实例),<b>本表第一个 <c>TIMESTAMP</c> 列会被服务端自动加上
    /// <c>NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP</c></b> ——
    /// 用户明明加的是一个普通可空列,建出来却自己变了。本机实测该变量为 <c>1</c>(不会自动加),
    /// 但它是<b>服务端变量</b>,方言包判不了,所以只能记在这儿。
    /// </para>
    /// <para>
    /// 与 <c>GetDbTypes()</c> 的差别正是契约点名的那条(§2.3):它返回的是"这个库<b>当前用到了</b>哪些类型",
    /// 随建表变多。这里是<b>静态表</b>,与库里有什么无关。
    /// </para>
    /// </remarks>
    public override IReadOnlyList<string> CommonTypes => TypeNames;

    // ─────────────────────────── 表设计器(M4) ───────────────────────────
    //
    // DropColumnDdl 与 CreateIndexDdl **不覆盖**,基类的通行写法在 MySQL 上逐字成立:
    //   ALTER TABLE `db`.`t` DROP COLUMN `c`   /   CREATE [UNIQUE] INDEX `ix` ON `db`.`t` (`a`, `b`)
    // 但删列这一路有三条 MySQL 特有的实测形态,调用方要知道(它们都不改 DDL 文本,所以记在这儿):
    //   ① **MySQL 不像 SQLite 那样拦你**:删掉某列时,只用到这一列的索引会被**静默一并删掉**;
    //      复合索引则**静默少一列继续存在**(实测 KEY ix_ab(a,b) 删掉 a 之后变成 KEY ix_ab(b))——
    //      后者尤其阴:索引还在、名字没变,查询计划却变了。表设计器删列前应当把受影响的索引先列出来。
    //   ② 被外键引用的列删不掉:1828 Cannot drop column 'pid': needed in a foreign key constraint 'fk_p'(实测原文)。
    //   ③ 删最后一列会被拒:1090 You can't delete all columns with ALTER TABLE; use DROP TABLE instead(实测原文)。
    // 建索引那条另有一条前提:MySQL 的索引名是**表内唯一**(不是库内唯一),同库两张表上各有一个 ix_name
    // 完全合法 —— 这正是 DropIndexDdl 必须带表名的根源,见下。

    /// <inheritdoc />
    /// <remarks>
    /// <b>必须覆盖:基类的 <c>DROP INDEX `ix`</c> 在 MySQL 上是语法错,不是"删不掉"。</b>
    /// 实测 <c>1064 ... right syntax to use near '' at line 1</c> —— 报错位置指在语句<b>末尾</b>,
    /// 因为解析器还在等 <c>ON</c>。这种"错在句尾"的 1064 最难认,原样丢给用户等于让他去数引号。
    /// <para>
    /// 根源是命名空间不同:MySQL 的索引名<b>只在表内唯一</b>,所以"删哪个 ix"这句话不带表名根本不完整
    /// (SQLite 正相反,它的索引名是库级唯一,那边不带表名才对)。
    /// 两种合法写法 <c>ALTER TABLE t DROP INDEX ix</c> 与 <c>DROP INDEX ix ON t</c> 等价,这里取后者:
    /// 它与基类 <see cref="DialectPackBase.CreateIndexDdl" /> 生成的 <c>CREATE INDEX ix ON t</c> 正好成对,
    /// 预览面板里两条并排读起来是一件事的正反面。
    /// </para>
    /// <para>
    /// 表名走 <see cref="DialectPackBase.QuoteQualified" />(库名与表名两段都反引号加倍),
    /// 索引名走 <see cref="DialectPackBase.QuoteIdentifier" /> —— 用户标识符永不裸拼。
    /// </para>
    /// </remarks>
    public override string? DropIndexDdl(SqlObject target, string indexName) =>
        $"DROP INDEX {QuoteIdentifier(indexName)} ON {QuoteQualified(target)}";

    /// <inheritdoc />
    /// <remarks>
    /// 文本沿用基类的通行写法(<c>ALTER TABLE ... ADD COLUMN ...</c>),覆盖只做一件事:
    /// <b>列定义里说了、而这条 DDL 表达不了的,一律不生成。</b>
    /// <para>
    /// <c>COLUMN</c> 这个关键字<b>留着</b>。MySQL 两种写法都合法(实测 <c>ADD `qty` int</c> 与
    /// <c>ADD COLUMN `qty` int</c> 都成功),留着的理由是可读性:预览面板里用户要一眼看出这是加列
    /// 还是加索引/加约束,而 <c>ADD</c> 后面跟的是什么全得往后读才知道。省两个词换一次误读不值。
    /// </para>
    /// <para>
    /// 通用写法只写得出"列名 + 类型 + <c>NOT NULL</c> + <c>DEFAULT</c>"四样,列模型上另外四样它
    /// <b>一声不吭地丢掉</b>,而 MySQL 会照办出一个<b>普通列</b>:
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="SqlColumn.IsGenerated" /> —— 拼不出 <c>GENERATED ALWAYS AS (expr) STORED/VIRTUAL</c>
    ///     (模型里根本没有生成表达式这一格)。用户点的"加一个生成列"办成了别的事,而且哪儿都不提示。
    ///   </item>
    ///   <item><see cref="SqlColumn.IsPrimaryKey" /> —— 拼不出 <c>PRIMARY KEY</c>。</item>
    ///   <item>
    ///     <see cref="SqlColumn.IsAutoIncrement" /> —— 拼不出 <c>AUTO_INCREMENT</c>;
    ///     何况 MySQL 还要求自增列必须是某个键的第一列,单靠这一条 DDL 本来也不成立。
    ///   </item>
    ///   <item><see cref="SqlColumn.Comment" /> —— 见下,这一条是本层的工具限制,不是 MySQL 的限制。</item>
    /// </list>
    /// 静默办成别的事比报错坏得多,所以这四种一律返回 <see langword="null" />,
    /// 让界面显示"该数据库不支持这样加列"(§7.8)。
    /// </para>
    /// <para>
    /// <b>注释那一条要单独说清,因为它是四条里唯一"MySQL 明明支持"的。</b>
    /// <c>COMMENT</c> 只接<b>带引号的字符串字面量</b>:本包在 <see cref="EstimateRowCountSql" /> 里用的那招
    /// 十六进制(<c>X'..'</c>)在这里<b>是语法错</b> —— 实测
    /// <c>ADD COLUMN `c3` int COMMENT X'E4B8ADE69687'</c> 报 <c>1064 ... near 'X'E4B8ADE69687''</c>。
    /// 而要自己加引号,就得知道服务端的 <c>sql_mode</c>:默认模式下反斜杠是转义符、
    /// 开了 <c>NO_BACKSLASH_ESCAPES</c> 又不是,同一段注释在两种模式下得转义成两个样子,
    /// <b>而且转错不报错,只把注释内容悄悄改掉</b>。连接层的 <c>sql_mode</c> 探测还没接上
    /// (与类注释里 <c>ANSI_QUOTES</c> 那条是同一笔待办),在那之前宁可不生成:
    /// 丢掉用户敲的注释是静默的,"这样加不了"是看得见的。这条限制随那笔待办一起解除,
    /// 解除时把 <c>COMMENT</c> 接在 <c>DEFAULT</c> 之后即可。
    /// </para>
    /// <para>
    /// <b>类型与默认值都是原样拼进去的,所以调用方给的文本必须自己成立。</b> 好消息是
    /// <see cref="DescribeAsync" /> 读回来的 <see cref="SqlColumn.DataType" /> 就是 <c>COLUMN_TYPE</c> 原文
    /// (<c>varchar(50)</c> / <c>int unsigned</c> / <c>enum('新建','已完成')</c>),原样拿去加列成立 ——
    /// 这条来回在 MySQL 上是通的,不像 SQLite 的默认值那样还要补一层括号。
    /// </para>
    /// </remarks>
    public override string? AddColumnDdl(SqlObject target, SqlColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        // 通用写法表达不了这四样,而它丢掉它们时不报错 —— 见上。
        return column.IsGenerated || column.IsPrimaryKey || column.IsAutoIncrement
               || !string.IsNullOrEmpty(column.Comment)
            ? null
            : base.AddColumnDdl(target, column);
    }

    /// <summary>
    /// 常用类型的静态表(取舍与陷阱见 <see cref="CommonTypes" />)。
    /// 提成静态字段是因为类型下拉每次打开都要读它,没必要每次新建一个数组。
    /// </summary>
    private static readonly string[] TypeNames =
    [
        // 整数:一律不带显示宽度(8.0.17 起弃用,建出来括号会被静默丢掉)。
        "TINYINT", "SMALLINT", "MEDIUMINT", "INT", "BIGINT",
        "INT UNSIGNED", "BIGINT UNSIGNED",
        // 布尔:写 BOOLEAN 而不是 TINYINT(1) —— 建出来一样,但后者会触发 1681 弃用警告。
        "BOOLEAN",
        // 定点与浮点:钱一律用 DECIMAL,FLOAT/DOUBLE 是二进制浮点,存不下精确小数。
        "DECIMAL(10,2)", "FLOAT", "DOUBLE",
        "BIT(1)",
        // 日期时间:DATETIME 排在 TIMESTAMP 前面,理由见 CommonTypes 注释里的 2038 与隐式默认值两条。
        "DATE", "TIME", "DATETIME", "DATETIME(3)", "TIMESTAMP", "YEAR",
        // 文本:VARCHAR 必须带长度(不带是语法错);单行总长有 65535 字节上限,长文本要走 TEXT 族。
        "CHAR(36)", "VARCHAR(255)", "TINYTEXT", "TEXT", "MEDIUMTEXT", "LONGTEXT",
        // 二进制。
        "BINARY(16)", "VARBINARY(255)", "TINYBLOB", "BLOB", "MEDIUMBLOB", "LONGBLOB",
        // 枚举/集合是模板,取值等用户改;JSON 在 MySQL 上是真类型(与 SQLite 相反,那边故意不给)。
        "ENUM('a','b')", "SET('a','b')", "JSON",
        // 空间类型:要建空间索引的话列必须 NOT NULL 且带 SRID,这一步表设计器目前表达不了。
        "GEOMETRY", "POINT"
    ];

    /// <summary>
    /// 剥掉末尾的语句终止符(可能有多个,后面还可能跟着空白)。
    /// <para>
    /// <see cref="ExplainSql" /> 要往语句<b>前面</b>接 <c>EXPLAIN</c>,留着尾巴上的分号与空白
    /// 会让回显里的语句难认。<see cref="ApplyPaging" /> 里有一份等价的内联写法 ——
    /// 那是 M2 已定稿的代码,这一轮不去动它;两处规则相同,将来要改就一起改。
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
    /// 生成列判定。<b>不能简单地找 "GENERATED" 三个字</b> ——
    /// 表达式默认值的 EXTRA 是 <c>DEFAULT_GENERATED</c>,里面同样有这个词。
    /// 认错的代价很具体:<c>created_at DEFAULT CURRENT_TIMESTAMP</c> 会被判成生成列、
    /// 从可写列集合里被剔掉,于是网格里改这一行永远写不进去,而且不报错。
    /// </summary>
    /// <param name="extra">COLUMNS.EXTRA。</param>
    /// <param name="generationExpression">COLUMNS.GENERATION_EXPRESSION。</param>
    /// <returns>是生成列则 <see langword="true" />。</returns>
    private static bool IsGenerated(string extra, string generationExpression) =>
        !string.IsNullOrEmpty(generationExpression)
        || extra.Contains("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase)
        || extra.Contains("STORED GENERATED", StringComparison.OrdinalIgnoreCase)
        // MariaDB 的用词。它的 GENERATION_EXPRESSION 也有,所以这条只是保险。
        || extra.Contains("PERSISTENT GENERATED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读索引。<c>STATISTICS</c> 是<b>每列一行</b>,按 <c>INDEX_NAME</c> 归并、<c>SEQ_IN_INDEX</c> 排序;
    /// <c>NON_UNIQUE=0</c> 是唯一,<c>INDEX_NAME='PRIMARY'</c> 是主键(MySQL 里主键索引名恒为它)。
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="parameters">库名、表名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>索引列表,主键排在最前。</returns>
    private static async Task<List<SqlIndex>> ReadIndexesAsync(
        DbConnection connection, object?[] parameters, CancellationToken cancellationToken)
    {
        // ORDER BY 里把 PRIMARY 顶到最前:按字母排的话它会掉进中间,而主键是用户第一眼要找的东西。
        // (Fold 走 GroupBy,分组顺序 = 首次出现顺序,所以排序在 SQL 里定就够。)
        const string Rich = $"""
            SELECT INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME, NON_UNIQUE, INDEX_TYPE,
                   SUB_PART, COLLATION, EXPRESSION, IS_VISIBLE
              FROM information_schema.STATISTICS
             WHERE TABLE_SCHEMA = {SchemaParam} AND TABLE_NAME = @p1
             ORDER BY (INDEX_NAME = 'PRIMARY') DESC, INDEX_NAME, SEQ_IN_INDEX
            """;
        // EXPRESSION(函数索引)是 8.0.13 才有的列,IS_VISIBLE 是 8.0.0 才有的列,MariaDB 两个都没有。
        // 少一列的后果不是"少显示一点信息",而是整条查询以 1054 打回 —— 对象树上这张表直接展不开。
        // 所以退化成可移植子集重来一次:多一个来回,换老服务端上不黑屏。
        const string Portable = $"""
            SELECT INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME, NON_UNIQUE, INDEX_TYPE,
                   SUB_PART, COLLATION, NULL AS EXPRESSION, 'YES' AS IS_VISIBLE
              FROM information_schema.STATISTICS
             WHERE TABLE_SCHEMA = {SchemaParam} AND TABLE_NAME = @p1
             ORDER BY (INDEX_NAME = 'PRIMARY') DESC, INDEX_NAME, SEQ_IN_INDEX
            """;

        List<IndexRow> rows;
        try
        {
            rows = await QueryAsync(connection, Rich, MapIndexRow, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException)
        {
            rows = await QueryAsync(connection, Portable, MapIndexRow, parameters, cancellationToken).ConfigureAwait(false);
        }

        return Fold(
            rows,
            row => row.Name,
            (name, parts) =>
            {
                bool primary = string.Equals(name, "PRIMARY", StringComparison.Ordinal);
                bool unique = parts[0].NonUnique == 0;
                string kind = parts[0].IndexType;
                bool functional = parts.Any(p => !string.IsNullOrEmpty(p.Expression));
                return new SqlIndex(
                    name,
                    // **函数索引一律报 0 列,而不是把表达式冒充成列名。**
                    // SqlTableSchema.TryGetRowKey 在没有主键时会拿"第一个有列的唯一索引"当行定位键;
                    // 把 upper(`name`) 当成列名交上去,网格就会拼出 WHERE upper(`name`) = ? 这种
                    // 打不中行(或者打中一片)的 UPDATE。列数为 0 的索引会被 TryGetRowKey 跳过,
                    // 而定义原文照样把表达式写全 —— 用户看得见,回写用不上,这才是对的组合。
                    functional ? [] : [.. parts.Select(p => p.Column)],
                    unique,
                    primary,
                    kind,
                    Definition(name, primary, unique, kind, parts));
            });
    }

    /// <summary>把 <c>STATISTICS</c> 的一行读成中间结构(SequentialAccess:严格按列序)。</summary>
    /// <param name="r">读取器。</param>
    /// <returns>一行。</returns>
    private static IndexRow MapIndexRow(DbDataReader r) => new(
        Str(r, 0),
        Int(r, 1),
        Str(r, 2),
        Int(r, 3),
        Str(r, 4),
        LongOrNull(r, 5),
        Str(r, 6),
        Str(r, 7),
        !string.Equals(Str(r, 8), "NO", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 索引定义原文。<b>前缀长度必须体现出来</b> —— <c>name(10)</c> 与 <c>name</c> 是两个不同的索引
    /// (前者不能用于覆盖扫描、不能保证全值唯一),而列名清单里它们长得一模一样。
    /// 降序、函数表达式、不可见同理:这些差别只有原文说得清。
    /// </summary>
    /// <param name="name">索引名。</param>
    /// <param name="primary">是否主键。</param>
    /// <param name="unique">是否唯一。</param>
    /// <param name="kind">INDEX_TYPE。</param>
    /// <param name="parts">按序的列。</param>
    /// <returns>贴近 <c>SHOW CREATE TABLE</c> 写法的定义原文。</returns>
    private static string Definition(string name, bool primary, bool unique, string kind, IReadOnlyList<IndexRow> parts)
    {
        var text = new StringBuilder();
        if (primary)
        {
            _ = text.Append("PRIMARY KEY ");
        }
        else
        {
            _ = text
                .Append(kind switch
                {
                    "FULLTEXT" => "FULLTEXT ",
                    "SPATIAL" => "SPATIAL ",
                    _ => unique ? "UNIQUE " : ""
                })
                .Append("KEY ")
                .Append(Quote(name))
                .Append(' ');
        }
        _ = text.Append('(');
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                _ = text.Append(", ");
            }
            IndexRow part = parts[i];
            if (!string.IsNullOrEmpty(part.Expression))
            {
                _ = text.Append('(').Append(part.Expression).Append(')');
                continue;
            }
            _ = text.Append(Quote(part.Column));
            if (part.SubPart is > 0)
            {
                _ = text.Append('(').Append(Num(part.SubPart.Value)).Append(')');
            }
            if (string.Equals(part.Collation, "D", StringComparison.Ordinal))
            {
                _ = text.Append(" DESC");
            }
        }
        _ = text.Append(')');
        if (string.Equals(kind, "HASH", StringComparison.OrdinalIgnoreCase))
        {
            _ = text.Append(" USING HASH");
        }
        if (!parts[0].Visible)
        {
            // 不可见索引优化器根本不用。不标出来的话,用户会盯着一个"存在但从不生效"的索引查半天慢查询。
            _ = text.Append(" /* INVISIBLE */");
        }
        return text.ToString();
    }

    /// <summary>
    /// 读外键。<c>IDbMaintenance</c> 里**一个外键方法都没有**(§2.3),这条只能自己查。
    /// <c>KEY_COLUMN_USAGE</c> 出本表列与目标列,<c>REFERENTIAL_CONSTRAINTS</c> 出删除/更新动作。
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="parameters">库名、表名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>外键列表。</returns>
    private static async Task<List<SqlForeignKey>> ReadForeignKeysAsync(
        DbConnection connection, object?[] parameters, CancellationToken cancellationToken)
    {
        // REFERENCED_TABLE_NAME IS NOT NULL 把主键/唯一键那些行滤掉 ——
        // KEY_COLUMN_USAGE 装的是"所有键约束的列",不加这一条会把 PRIMARY 也当成外键。
        // 三段 join(库 + 约束名 + 表名)缺一不可:约束名在 MySQL 里只在**表内**唯一,
        // 同库两张表各有一个叫 fk_x 的外键是合法的。
        const string Sql = $"""
            SELECT k.CONSTRAINT_NAME, k.COLUMN_NAME,
                   k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME,
                   r.DELETE_RULE, r.UPDATE_RULE
              FROM information_schema.KEY_COLUMN_USAGE k
              JOIN information_schema.REFERENTIAL_CONSTRAINTS r
                ON r.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA
               AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
               AND r.TABLE_NAME = k.TABLE_NAME
             WHERE k.TABLE_SCHEMA = {SchemaParam}
               AND k.TABLE_NAME = @p1
               AND k.REFERENCED_TABLE_NAME IS NOT NULL
             ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION
            """;
        List<ForeignKeyRow> rows = await QueryAsync(
            connection,
            Sql,
            r => new ForeignKeyRow(Str(r, 0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), Str(r, 6)),
            parameters,
            cancellationToken).ConfigureAwait(false);

        return Fold(
            rows,
            row => row.Name,
            (name, parts) => new SqlForeignKey(
                name,
                [.. parts.Select(p => p.Column)],
                parts[0].ReferencedSchema,
                parts[0].ReferencedTable,
                [.. parts.Select(p => p.ReferencedColumn)],
                parts[0].OnDelete,
                parts[0].OnUpdate));
    }

    /// <summary>
    /// 把一个标识符变成 SQL 里的<b>字符串字面量</b>(不是标识符!)。
    /// <para>
    /// 只在 <see cref="EstimateRowCountSql" /> 用得着 —— 那个接口返回的是一条不带参数的 SQL,
    /// 而库名/表名要进 <c>WHERE</c> 做值比对。这里走十六进制而不是加引号转义,
    /// 是因为 MySQL 字符串字面量的转义规则<b>随 <c>sql_mode</c> 变</b>:
    /// 默认模式下反斜杠是转义符(名字以 <c>\</c> 结尾就能吃掉闭合引号),
    /// 开了 <c>NO_BACKSLASH_ESCAPES</c> 又不是。<c>X'..'</c> 里没有任何需要转义的字符,
    /// 两种模式下都只有一个意思。<c>CONVERT(... USING utf8mb4)</c> 是为了让它和
    /// <c>information_schema</c> 的字符列比得起来(裸的十六进制串是 binary,会撞上排序规则混用)。
    /// </para>
    /// </summary>
    /// <param name="value">要变成字面量的文本。</param>
    /// <returns>SQL 片段。</returns>
    private static string TextLiteral(string value) =>
        value.Length == 0
            ? "''"
            : $"CONVERT(X'{Convert.ToHexString(Encoding.UTF8.GetBytes(value))}' USING utf8mb4)";

    /// <summary>反引号包一层(供定义原文用;与 <see cref="DialectPackBase.QuoteIdentifier" /> 同规则)。</summary>
    /// <param name="identifier">标识符。</param>
    /// <returns>转义后的形态。</returns>
    private static string Quote(string identifier) =>
        $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

    /// <summary>数字转文本(恒用不变文化,避免某些区域设置给整数加千位分隔符)。</summary>
    /// <param name="value">数值。</param>
    /// <returns>文本。</returns>
    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>STATISTICS</c> 的一行(每列一行)。</summary>
    /// <param name="Name">索引名。</param>
    /// <param name="Seq">列在索引里的序号。</param>
    /// <param name="Column">列名;函数索引上为空。</param>
    /// <param name="NonUnique">0 = 唯一。</param>
    /// <param name="IndexType">BTREE / FULLTEXT / HASH / SPATIAL。</param>
    /// <param name="SubPart">前缀长度;整列索引为 <see langword="null" />。</param>
    /// <param name="Collation">A = 升序,D = 降序。</param>
    /// <param name="Expression">函数索引的表达式。</param>
    /// <param name="Visible">优化器是否可见。</param>
    private sealed record IndexRow(
        string Name,
        int Seq,
        string Column,
        int NonUnique,
        string IndexType,
        long? SubPart,
        string Collation,
        string Expression,
        bool Visible);

    /// <summary><c>KEY_COLUMN_USAGE</c> ⨝ <c>REFERENTIAL_CONSTRAINTS</c> 的一行。</summary>
    /// <param name="Name">约束名。</param>
    /// <param name="Column">本表列。</param>
    /// <param name="ReferencedSchema">目标库。</param>
    /// <param name="ReferencedTable">目标表。</param>
    /// <param name="ReferencedColumn">目标列。</param>
    /// <param name="OnDelete">删除时动作。</param>
    /// <param name="OnUpdate">更新时动作。</param>
    private sealed record ForeignKeyRow(
        string Name,
        string Column,
        string ReferencedSchema,
        string ReferencedTable,
        string ReferencedColumn,
        string OnDelete,
        string OnUpdate);
}
