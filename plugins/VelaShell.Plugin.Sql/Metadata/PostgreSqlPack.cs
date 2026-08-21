using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VelaShell.Plugin.Sql.Metadata;

/// <summary>
/// PostgreSQL 的方言资产。**数据源一律是 <c>pg_catalog</c>,一条 <c>IDbMaintenance</c> 都不调。**
/// <para>
/// <b>为什么整块自己查</b>(设计文档 §3.5 的真机结论):PG 上 <c>DbMaintenance</c> 有 13 处
/// "返回了值而值是错的" —— <c>IsIdentity</c> 恒 True 还会永久污染 <c>GetIsIdentities</c> 的缓存;
/// 视图与物化视图的列返回 0 且不抛异常(它的列 SQL 数据源是 <c>pg_tables</c>,里面没有视图);
/// 物化视图既不在 <c>GetTableInfoList</c> 也不在 <c>GetViewInfoList</c> 里,**在对象树上整个消失**;
/// schema 是写死的 <c>nspname='public'</c>,自定义 schema 静默不可达;
/// <c>GetTriggerNames</c> 少了 <c>and not tgisinternal</c>,任何有外键的表都会凭空多两个触发器。
/// 一个会说谎的元数据源比没有更坏 —— 界面会如实地把假数据画出来。
/// </para>
/// <para>
/// <b>为什么不用 <c>information_schema</c></b>:它是标准视图,每一格都要过一层权限函数与类型转换,
/// 而且**它给不出本包必须给的东西** —— 物化视图、生成列的存储/虚拟之分、
/// 表达式索引与部分索引的原文、内部触发器标记,在 <c>information_schema</c> 里根本没有对应列。
/// </para>
/// <para>
/// <b>标识符纪律</b>:所有比对系统表的地方走参数(<c>@p0</c> / <c>@p1</c>),
/// 确实要拼进 SQL 的地方(限定名、字面量)走 <see cref="DialectPackBase.QuoteIdentifier" /> 或
/// <see cref="Literal" />。PG 的定界符是双引号,转义是双引号加倍(基类已实现)。
/// </para>
/// </summary>
internal sealed class PostgreSqlPack : DialectPackBase
{
    /// <summary>
    /// 关系定位谓词。<c>@p0</c> 是 schema、<c>@p1</c> 是对象名。
    /// <para>
    /// schema 传空时回落到 <c>pg_table_is_visible</c>(即按连接的 <c>search_path</c> 解析) ——
    /// 这一格存在的理由是"调用方还没拿到 schema"的中间态,**不是**给 <c>DbMaintenance</c> 那种
    /// "只认 public"的行为留后门:本包的正常路径永远带 schema。
    /// </para>
    /// </summary>
    private const string RelationFilter =
        "((@p0 = '' AND pg_catalog.pg_table_is_visible(c.oid)) OR n.nspname = @p0)";

    /// <summary>
    /// 默认值原文尾巴上的类型转换。
    /// <para>
    /// 判"是不是纯字面量"必须先把这条尾巴摘掉:PG 把 <c>DEFAULT 'new'</c> 存成
    /// <c>'new'::character varying</c>、把 <c>DEFAULT -1</c> 存成 <c>'-1'::integer</c>,
    /// 带不带这条尾巴与它是不是字面量无关。要认的形态:多词类型(<c>timestamp with time zone</c>)、
    /// 带修饰(<c>numeric(12,3)</c>)、数组(<c>integer[]</c>)、schema 限定(<c>app.mood</c>)、
    /// 加了引号的类型名(<c>"bit"</c>),以及连续多次转换。
    /// </para>
    /// </summary>
    private static readonly Regex CastTailPattern = new(
        """^(?:\s*::\s*(?:"[^"]*"|[A-Za-z_][A-Za-z0-9_]*)(?:\s*\.\s*(?:"[^"]*"|[A-Za-z_][A-Za-z0-9_]*))?(?:\s+[A-Za-z_]+)*(?:\s*\(\s*-?\d+(?:\s*,\s*-?\d+)?\s*\))?(?:\s*\[\s*\])*)+\s*$""",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>数字字面量前缀。</summary>
    private static readonly Regex NumberHeadPattern = new(
        @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <inheritdoc />
    public override SqlDialect Dialect => SqlDialect.PostgreSql;

    /// <inheritdoc />
    public override bool HasSchemas => true;

    /// <inheritdoc />
    public override bool HasDatabases => true;

    /// <summary>
    /// <see langword="false" /> —— <b>PG 的目录表只覆盖当前连接所在的那个库</b>。
    /// <para>
    /// 真机实测(PostgreSQL 18.1,127.0.0.1:55432):连到 <c>postgres</c> 库之后
    /// <c>pg_namespace</c> 只有一个 <c>public</c>、<c>pg_class</c> 里 <c>public</c> 下 <b>0 行</b>;
    /// 同一条 SQL 连到 <c>ops_pg</c> 则回 9 张表。PG 没有跨库查询
    /// (没有 SQL Server 那种三段名,<c>dblink</c> / <c>postgres_fdw</c> 是扩展且要建对象),
    /// 所以<b>只能一库一条连接</b>。
    /// </para>
    /// </summary>
    public override bool MetadataSpansCatalogs => false;

    /// <inheritdoc />
    public override bool HasRoutines => true;

    /// <inheritdoc />
    public override bool HasSequences => true;

    /// <inheritdoc />
    protected override (char Open, char Close) Delimiters => ('"', '"');

    /// <summary>
    /// "这个 schema 是不是服务端自带的"的 SQL 表达式。
    /// <para>
    /// <c>pg_catalog</c> 与 <c>information_schema</c> 是目录本身;<c>pg_*</c> 是 PG 保留的前缀。
    /// 判据写成 SQL 表达式而不是在 C# 侧按名字猜,是为了让它与
    /// <see cref="ListSchemasAsync" /> / <see cref="ListRelationsAsync" /> 用的是同一条判据 ——
    /// 两处各写一份,迟早会有一处漏掉 <c>information_schema</c>,
    /// 表现是"树上大部分系统 schema 都归好类了,唯独这一个混在用户 schema 里"。
    /// </para>
    /// </summary>
    private const string SystemSchemaExpr =
        "(n.nspname LIKE 'pg\\_%' OR n.nspname = 'information_schema')";

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SqlObject>> ListDatabasesAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        // datallowconn / datistemplate:模板库与禁连库列出来只会在用户点开时报错。
        // CONNECT 权限同理 —— 树上画一个点不开的节点不叫"信息全",叫"骗一次点击"。
        const string sql = """
            SELECT d.datname::text,
                   COALESCE(pg_catalog.shobj_description(d.oid, 'pg_database'), ''),
                   (d.datname = 'postgres')
            FROM pg_catalog.pg_database d
            WHERE d.datallowconn
              AND NOT d.datistemplate
              AND pg_catalog.has_database_privilege(d.oid, 'CONNECT')
            ORDER BY d.datname
            """;
        return await QueryAsync(
            connection,
            sql,
            // postgres 库是 initdb 建出来的落脚点(很多客户端把它当默认连接目标),
            // 它不是用户的业务库 —— 归到系统组里,但**照样列出来**:
            // 它确实可连,而且是"我先连上再说"的那个库。
            r => new SqlObject(SqlObjectKind.Database, Str(r, 0), Comment: Str(r, 1), IsSystem: Bool(r, 2)),
            null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SqlObject>> ListSchemasAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        // pg_catalog 与 information_schema **列出来但标成系统** —— 模型有了
        // SqlObject.IsSystem 这一格之后,树把它们收进"系统对象"分组,既不混排也不藏。
        // 早先这里是整条 WHERE 排掉的:那让"我想看看 pg_class 长什么样"变成做不到的事,
        // 而这恰恰是 §7 待复测第 3 条(select * from pg_class)背后的真实需求。
        //
        // 仍然排掉的只有两类**会话内临时**的 schema:pg_temp_N(本会话临时表)与
        // pg_toast / pg_toast_temp_N(行外存储)。它们不是"用户能操作的对象",
        // 而且个数随会话变 —— 列出来只是噪声,不是信息。
        const string sql = $"""
            SELECT n.nspname::text,
                   COALESCE(pg_catalog.obj_description(n.oid, 'pg_namespace'), ''),
                   {SystemSchemaExpr}
            FROM pg_catalog.pg_namespace n
            WHERE pg_catalog.has_schema_privilege(n.oid, 'USAGE')
              AND n.nspname NOT LIKE 'pg\_toast%'
              AND n.nspname NOT LIKE 'pg\_temp%'
            ORDER BY n.nspname
            """;
        return await QueryAsync(
            connection,
            sql,
            r => new SqlObject(SqlObjectKind.Schema, Str(r, 0), Comment: Str(r, 1), IsSystem: Bool(r, 2)),
            null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SqlObject>> ListRelationsAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        // relkind 是本包最重要的一格:'m'(物化视图)与 'p'(分区表)在 DbMaintenance 里
        // 一个都不出现,而它们就是实实在在的对象。分区子表(relispartition)也照列 ——
        // 树上少一张真实存在的表,比多一行更坏。
        string sql = $"""
            SELECT c.relkind::text,
                   c.relname::text,
                   n.nspname::text,
                   COALESCE(pg_catalog.obj_description(c.oid, 'pg_class'), ''),
                   NULLIF(c.reltuples, -1)::bigint,
                   {SystemSchemaExpr}
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'm')
              AND {RelationFilter}
            ORDER BY c.relname
            """;
        return await QueryAsync(
            connection,
            sql,
            r => new SqlObject(
                KindOf(Str(r, 0)),
                Str(r, 1),
                Str(r, 2),
                Str(r, 3),
                LongOrNull(r, 4),
                // 系统性由**所在 schema** 决定,不由表名。pg_catalog 里的东西一律是系统对象,
                // 用户 schema 里叫 pg_xxx 的表则不是 —— 按名字猜会把后者误伤。
                Bool(r, 5)),
            [schema ?? ""],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SqlObject>> ListRoutinesAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        // 名字带上**形参类型**,因为 PG 允许重载 —— 三个 f(int) / f(text) / f(int,text)
        // 在树上只写 proname 就是三行一模一样的 "f",而它们是三个不同的对象
        // (DROP 也必须带签名)。pg_get_function_identity_arguments 给的正是 DROP 认的那份。
        //
        // prokind 里 'a'(聚合)与 'w'(窗口函数)不列:它们不是用户在"函数"这一栏里找的东西。
        static SqlObject Map(DbDataReader r)
        {
            // SequentialAccess:必须按列序读。
            string name = Str(r, 0);
            string args = Str(r, 1);
            string owner = Str(r, 2);
            string comment = Str(r, 3);
            bool procedure = Str(r, 4) == "p";
            bool system = Bool(r, 5);
            return new(
                procedure ? SqlObjectKind.Procedure : SqlObjectKind.Function,
                $"{name}({args})",
                owner,
                comment,
                IsSystem: system);
        }

        string sql = $"""
            SELECT p.proname::text,
                   pg_catalog.pg_get_function_identity_arguments(p.oid),
                   n.nspname::text,
                   COALESCE(pg_catalog.obj_description(p.oid, 'pg_proc'), ''),
                   p.prokind::text,
                   {SystemSchemaExpr}
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE p.prokind IN ('f', 'p')
              AND ((@p0 = '' AND pg_catalog.pg_function_is_visible(p.oid)) OR n.nspname = @p0)
            ORDER BY p.proname
            """;
        try
        {
            return await QueryAsync(connection, sql, Map, [schema ?? ""], cancellationToken).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // prokind 是 PG 11 才有的列;10 及以下是两个布尔列 proisagg / proiswindow,
            // 且**没有存储过程**这一类(CREATE PROCEDURE 也是 11 引入的)。
            // 多一个来回,换老服务端上"函数"这一栏不是一行红字。【未验证:手上只有 18.1】
            string portable = $"""
                SELECT p.proname::text,
                       pg_catalog.pg_get_function_identity_arguments(p.oid),
                       n.nspname::text,
                       COALESCE(pg_catalog.obj_description(p.oid, 'pg_proc'), ''),
                       'f'::text,
                       {SystemSchemaExpr}
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
                WHERE NOT p.proisagg
                  AND NOT p.proiswindow
                  AND ((@p0 = '' AND pg_catalog.pg_function_is_visible(p.oid)) OR n.nspname = @p0)
                ORDER BY p.proname
                """;
            return await QueryAsync(connection, portable, Map, [schema ?? ""], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SqlObject>> ListSequencesAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        // 序列在 PG 里就是 relkind='S' 的一个 pg_class 行,所以谓词与关系清单完全同构。
        // **不并进 ListRelationsAsync**:那一格是"能 SELECT * 出行的东西",
        // 序列点开是 last_value/log_cnt/is_called 三列,与表放同一栏只会让人点错。
        //
        // 剔掉 deptype='i' 的那批:那是 GENERATED … AS IDENTITY 列背后引擎自建的序列,
        // 用户**删不掉也改不动**(DROP 会报"column … requires it"),画出来就是一个点了没用的节点。
        // deptype='a'(serial 列带出来的)与无依赖的独立序列都留着 —— 前者是可以单独 ALTER 的真对象。
        // 与 Oracle 包剔 ISEQ$$_* 是同一条判据的两种方言写法。
        // 真机(ops_pg)上四条序列里正好两条是 'i'。
        string sql = $"""
            SELECT c.relname::text,
                   n.nspname::text,
                   COALESCE(pg_catalog.obj_description(c.oid, 'pg_class'), ''),
                   {SystemSchemaExpr}
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'S'
              AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_depend d
                     WHERE d.classid = 'pg_class'::regclass
                       AND d.objid = c.oid
                       AND d.deptype = 'i')
              AND {RelationFilter}
            ORDER BY c.relname
            """;
        return await QueryAsync(
            connection,
            sql,
            r => new SqlObject(SqlObjectKind.Sequence, Str(r, 0), Str(r, 1), Str(r, 2), IsSystem: Bool(r, 3)),
            [schema ?? ""],
            cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 列出一张表上的**用户**触发器。
    /// <para>
    /// <b>为什么它不在 <see cref="IDialectPack" /> 上</b>:M1 的契约只到"表/视图 + 列/索引/外键"。
    /// 但 <c>NOT tgisinternal</c> 这一格必须现在就堵住 —— 真机实测(§3.5)不加它,
    /// **任何有外键的表都会凭空多出两个用户没建过的触发器**(外键是靠 <c>RI_ConstraintTrigger_*</c>
    /// 实现的),而用户会去找"这两个触发器是谁加的"。等契约开这一格时,方法体照搬即可。
    /// </para>
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="target">宿主表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>触发器列表。</returns>
    public async Task<IReadOnlyList<SqlObject>> ListTriggersAsync(
        DbConnection connection, SqlObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        string sql = $"""
            SELECT t.tgname::text,
                   n.nspname::text,
                   COALESCE(pg_catalog.obj_description(t.oid, 'pg_trigger'), '')
            FROM pg_catalog.pg_trigger t
            JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE NOT t.tgisinternal
              AND c.relname = @p1
              AND {RelationFilter}
            ORDER BY t.tgname
            """;
        return await QueryAsync(
            connection,
            sql,
            r => new SqlObject(SqlObjectKind.Trigger, Str(r, 0), Str(r, 1), Str(r, 2)),
            [target.Schema ?? "", target.Name],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override string ApplyPaging(string innerSql, int offset, int limit)
    {
        ArgumentNullException.ThrowIfNull(innerSql);
        // 结尾的分号会把 LIMIT 挤到下一条语句里去。切句器给的语句本来就不带分号,
        // 这里只是不让"手工传进来的一条 SQL"变成一个语法错。
        string body = innerSql.TrimEnd();
        if (body.EndsWith(';'))
        {
            body = body[..^1].TrimEnd();
        }
        int take = Math.Max(0, limit);
        int skip = Math.Max(0, offset);
        return $"{body}\nLIMIT {take.ToString(CultureInfo.InvariantCulture)} OFFSET {skip.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <inheritdoc />
    public override string? EstimateRowCountSql(SqlObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        // reltuples 是 0.3~7 ms,精确 count(*) 是 110~143 ms(§7.3) —— 底栏"约 N 行"就靠它。
        // **PG 14 起没 analyze 过的关系 reltuples 是 -1**,直接画出来就是"约 -1 行",
        // 所以在 SQL 里就折成 NULL,让调用方走"拿不到估算"的那条分支。
        string where = string.IsNullOrEmpty(target.Schema)
            ? "pg_catalog.pg_table_is_visible(c.oid)"
            : $"n.nspname = {Literal(target.Schema)}";
        return $"""
            SELECT NULLIF(c.reltuples, -1)::bigint
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relname = {Literal(target.Name)}
              AND {where}
            """;
    }

    /// <inheritdoc />
    /// <remarks><c>current_schema()</c> 给的是 <c>search_path</c> 里第一个真实存在的 schema ——
    /// 那正是不带限定名建表时会落到的地方。</remarks>
    public override string CurrentSchemaSql => "SELECT pg_catalog.current_schema()";

    /// <inheritdoc />
    public override string SessionIdSql => "SELECT pg_catalog.pg_backend_pid()";

    /// <inheritdoc />
    public override string? CancelSessionSql(string sessionId)
    {
        // 会话 id 是要拼进 SQL 的,所以只认整数 —— 认不出就返回 null,让调用方降级,
        // **绝不**把一段来历不明的文本塞进 pg_cancel_backend 的括号里。
        if (!int.TryParse(sessionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
        {
            return null;
        }
        // pg_cancel_backend 而不是 pg_terminate_backend:取消的是一条查询,不是用户的整个会话 ——
        // 掐掉连接会让编辑器里未提交的事务一起没了。
        return $"SELECT pg_catalog.pg_cancel_backend({pid.ToString(CultureInfo.InvariantCulture)})";
    }

    /// <inheritdoc />
    public override string? ShowCreateSql(SqlObject target)
    {
        // PG 没有 SHOW CREATE TABLE,也没有服务端函数能吐出建表 DDL ——
        // pg_dump 是个外部进程,不在 M1 范围。**返回 null 而不是拼一段半成品 DDL**:
        // 一段少了默认值、少了注释、少了分区定义的 DDL,用户复制走会真的建错表。
        _ = target;
        return null;
    }

    // ─────────────────────────── 运维面(M4) ───────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// 两档都走 <c>FORMAT TEXT</c>,吐的都是<b>一列</b>(列名恒为 <c>QUERY PLAN</c>)的树形文本。
    /// 这一点与 MySQL 相反 —— 那边不带 analyze 是 12 列表格、带 analyze 变一列树,渲染代码得认两种形状;
    /// PG 这一栏只有一种,<b>照列数分叉去认形态在这里是错的</b>。
    /// <para>
    /// <b>analyze 档会真的把语句跑完,而且 PG 上这句话比 MySQL 上硬得多。</b>
    /// 实测(18.1):一张 10 行的表上发
    /// <c>EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) delete from danger where id &gt; 5</c>,
    /// 计划正常返回,而随后的 <c>count(*)</c> <b>从 10 变成了 5</b> —— 行是真删的。
    /// <c>CREATE TABLE AS</c> 同理:<c>EXPLAIN (ANALYZE, FORMAT TEXT) create table ctas_probe as select 1</c>
    /// 跑完之后表真的建出来了、真的有一行。
    /// <b>MySQL 那条"当前版本的 EXPLAIN ANALYZE 接不了 DML,于是碰巧删不掉"的意外护栏,PG 一条都没有。</b>
    /// 契约里"绿档之外一律不给 analyze"(§7.6)在 PG 上是**唯一**的一道闸,调用方不能省。
    /// </para>
    /// <para>
    /// <b>为什么显式写 <c>BUFFERS</c></b>:计划里最常被追问的是"这条走了多少物理读",而
    /// <c>shared hit/read</c> 只有开了 <c>BUFFERS</c> 才有。PG 18 起它随 <c>ANALYZE</c> 默认开,
    /// 但 17 及以前默认是关的 —— 写死它,免得同一个界面在两台服务器上给出的信息量不一样。
    /// 它只在真跑过之后才有意义,所以静态档不带。
    /// </para>
    /// <para>
    /// <b><c>EXPLAIN</c> 只吃"可优化语句"</b>(<c>SELECT</c> / <c>INSERT</c> / <c>UPDATE</c> /
    /// <c>DELETE</c> / <c>MERGE</c> / <c>VALUES</c> / <c>EXECUTE</c> / <c>DECLARE</c> /
    /// <c>CREATE TABLE AS</c> / <c>CREATE MATERIALIZED VIEW</c>)。别的一律语法错,实测原文:
    /// <c>EXPLAIN (FORMAT TEXT) create table zzz(i int)</c> → <c>42601 syntax error at or near "int"</c>;
    /// <c>EXPLAIN (FORMAT TEXT) vacuum nn_probe</c> → <c>42601 syntax error at or near "vacuum"</c>。
    /// 报错<b>指在用户那条 SQL 上</b>,而那条单独跑起来好好的 —— 按 §7.8 翻成
    /// "这种语句没有执行计划",别把 42601 原样丢出去。
    /// </para>
    /// <para>
    /// <b>错误位置有个固定偏移,将来接错误定位器时必须减掉。</b> 服务端给的 <c>Position</c> 是相对
    /// <b>整条 EXPLAIN 语句</b>的:实测 <c>EXPLAIN (FORMAT TEXT) select * from no_such_table</c> 的
    /// <c>42P01</c> 把插入符指在 <c>no_such_table</c> 上,而那个偏移里含着本方法加的前缀
    /// (静态档 22 字符、analyze 档 40 字符)。直接拿它去用户编辑器里定位会整体右移。
    /// </para>
    /// <para>
    /// 末尾分号先剥掉,理由与 <see cref="ApplyPaging" /> 同一条:编辑器切出来的语句常带终止符,
    /// 留着它回显里的语句难认。(那边的剥法是内联的,属于 M2 已定稿的代码,这一轮不去动它;
    /// 两处规则相同,将来要改就一起改。)
    /// </para>
    /// </remarks>
    public override string? ExplainSql(string innerSql, bool analyze)
    {
        ArgumentNullException.ThrowIfNull(innerSql);
        string body = StripTerminators(innerSql);
        return analyze
            ? $"EXPLAIN (ANALYZE, BUFFERS, FORMAT TEXT) {body}"
            : $"EXPLAIN (FORMAT TEXT) {body}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// 列严格按契约:<c>id</c>、<c>user</c>、<c>host</c>、<c>db</c>、<c>state</c>、<c>seconds</c>、<c>query</c>
    /// —— 调用方(<c>SqlOpsTabViewModel</c>)是 <c>SequentialAccess</c> <b>按序号</b>读的,列序不能动。
    /// 别名一律带双引号:<c>user</c> 在 PG 里是保留字,裸写 <c>AS user</c> 直接是语法错。
    /// <para>
    /// <b><c>WHERE a.datname IS NOT NULL</c> 是这条 SQL 里最该解释的一格。</b>
    /// PG 10 起 <c>pg_stat_activity</c> 把后台进程也列进来了,实测一台<b>全空闲</b>的 18.1 上
    /// 9 行里有 8 行是 <c>checkpointer</c> / <c>walwriter</c> / <c>background writer</c> /
    /// <c>autovacuum launcher</c> / <c>logical replication launcher</c> 和三个 <c>io worker</c>,
    /// 它们的 <c>usename</c>、<c>client_addr</c>、<c>datname</c>、<c>state</c>、<c>query</c> <b>全是 NULL</b> ——
    /// 不过滤的话运维面第一栏开箱就是 8 行空格子加 1 行真会话。
    /// 用 <c>datname IS NOT NULL</c> 而不是 <c>backend_type = 'client backend'</c>,有两条理由:
    /// ① 并行工作进程(<c>parallel worker</c>)与自动清理工作进程(<c>autovacuum worker</c>)
    /// 都绑在某个库上,它们<b>会跑语句、会持锁</b>,该留下;
    /// ② <c>backend_type</c> 的取值随版本增删(<c>io worker</c> 就是 18 才有的),
    /// 按它写白名单等于每升一版漏一类。
    /// </para>
    /// <para>
    /// <b><c>clock_timestamp()</c> 而不是 <c>now()</c> —— 这不是洁癖,实测会算出负数。</b>
    /// <c>now()</c> 就是 <c>transaction_timestamp()</c>,在<b>事务开始那一刻</b>冻住。
    /// 实测:<c>BEGIN; SELECT pg_sleep(2);</c> 之后在同一个事务里查自己,
    /// <c>EXTRACT(EPOCH FROM now() - query_start)</c> 给的是 <b>-2.019753</b>,
    /// 而 <c>clock_timestamp()</c> 给 0.009673。运维面用的元数据连接完全可能正开着事务,
    /// 那时整栏耗时会变成负数或停在旧值。<c>clock_timestamp()</c> 每次求值都取真实时间,
    /// 在非事务场景下与 <c>now()</c> 无差别 —— 没有代价的那一边。
    /// </para>
    /// <para>
    /// <b><c>seconds</c> 不是"这条查询跑了多久"</b>,与 MySQL 的 <c>TIME</c> 是同一类陷阱:
    /// 它是 <c>clock_timestamp() - query_start</c>,只有 <c>state = 'active'</c> 时才等于"当前语句已耗时";
    /// <c>idle</c> 的行里它是"上一条语句是多久以前开始的"。按它排序找慢查询之前必须先看 <c>state</c>,
    /// 否则会把一个挂了三小时的空闲连接读成"跑了三小时的查询"。
    /// (真要量"空闲了多久"该用 <c>state_change</c>,但契约只有这一格,而"当前语句跑了多久"是更常问的那个。)
    /// </para>
    /// <para>
    /// <b><c>state</c> 把等待事件并进来了</b>,与 MySQL 把 <c>COMMAND</c> / <c>STATE</c> 合成一格同理:
    /// 光一个 <c>active</c> 等于什么都没说 —— 它既可能在算,也可能已经挂在锁上一小时。
    /// 实测形态:<c>active (Lock:transactionid)</c>(挂在行锁上)、<c>active (Timeout:PgSleep)</c>。
    /// <b>只在 <c>active</c> 时追加</b>:<c>idle</c> 的等待事件恒是 <c>Client:ClientRead</c>,
    /// 每行都挂一句"正在等客户端说话"是纯噪声。PG 自己的四个状态
    /// (<c>active</c> / <c>idle</c> / <c>idle in transaction</c> / <c>idle in transaction (aborted)</c>)
    /// 原样留在最前面 —— <c>idle in transaction</c> 是这一栏最要紧的一行,不能被装饰盖住。
    /// </para>
    /// <para>
    /// <b><c>host</c> 的四级回落,每一级都对着一种实测形态</b>:
    /// ① 正常 TCP 连接 → <c>127.0.0.1:4660</c>(带端口,与 MySQL 的 <c>HOST</c> 形态一致);
    /// ② 后台/工作进程没有客户端 → 回落成 <c>backend_type</c>(至少说得出 <c>autovacuum worker</c> 是谁);
    /// ③ Unix 域套接字连上来的客户端 <c>client_addr</c> 为 NULL 而 <c>client_port</c> 恒 <c>-1</c> → 写 <c>local</c>;
    /// ④ 剩下的写空串。<b>第 ④ 级是给权限不足的行留的,不能省成 <c>local</c>。</b>
    /// 实测:非超级用户查这张视图<b>看得见别人的行,但 <c>client_addr</c> / <c>client_port</c> /
    /// <c>backend_type</c> / <c>state</c> / <c>query_start</c> 全被抹成 NULL</b>
    /// (<c>query</c> 则显示 <c>&lt;insufficient privilege&gt;</c>)。
    /// 早先那版把最后一级直接写成 <c>'local'</c>,于是一台远程机器上的会话被显示成本机连接 ——
    /// <b>那是编造,不是回落</b>。看不见就写空。
    /// </para>
    /// <para>
    /// <b>权限:这一栏在非超级用户下是"行数对、内容缺"。</b> 要看全需要
    /// <c>pg_read_all_stats</c>(或 <c>pg_monitor</c>)角色。所以 <c>query</c> 整列都是
    /// <c>&lt;insufficient privilege&gt;</c> 时,界面该提示的是"给这个账号 <c>pg_monitor</c>",
    /// 而不是让用户以为服务端在空转(§7.8)。
    /// </para>
    /// <para>排序:有语句在跑的排前面,同组按已耗时倒序,最后按 pid 定序(让两次刷新之间行不乱跳)。</para>
    /// </remarks>
    public override string? SessionListSql => """
        SELECT a.pid                                                            AS "id",
               a.usename::text                                                  AS "user",
               COALESCE(pg_catalog.host(a.client_addr) || ':' || a.client_port::text,
                        NULLIF(a.backend_type, 'client backend'),
                        CASE WHEN a.client_port = -1 THEN 'local' END,
                        '')                                                     AS "host",
               a.datname::text                                                  AS "db",
               COALESCE(a.state, '')
                 || CASE WHEN a.state = 'active' AND a.wait_event_type IS NOT NULL
                         THEN ' (' || a.wait_event_type || ':' || a.wait_event || ')'
                         ELSE '' END                                            AS "state",
               EXTRACT(EPOCH FROM pg_catalog.clock_timestamp() - a.query_start) AS "seconds",
               a.query                                                          AS "query"
          FROM pg_catalog.pg_stat_activity a
         WHERE a.datname IS NOT NULL
         ORDER BY (a.state = 'active') DESC NULLS LAST, "seconds" DESC NULLS LAST, a.pid
        """;

    /// <inheritdoc />
    /// <remarks>
    /// 列严格按契约:<c>blocked_id</c>、<c>blocking_id</c>、<c>object</c>、<c>mode</c>、<c>query</c>。
    /// 两个 id 都是 <b>pid</b>,与 <see cref="SessionListSql" /> 的 <c>id</c> 是同一个号,
    /// 也正是 <see cref="CancelSessionSql" /> 认的号 —— 锁那一栏点出来的 id 必须能直接拿去会话栏里找、
    /// 拿去取消。(MySQL 那边要额外 join 一次 <c>performance_schema.threads</c> 才换得到能用的号,
    /// PG 天生就是同一个。)
    /// <para>
    /// <b>阻塞链走 <c>pg_blocking_pids()</c>(9.6+),不自连 <c>pg_locks</c>。</b>
    /// 手写自连要把"同一把锁"的 9 个定位列两两配对,还得自己处理快路径锁(<c>fastpath</c>)
    /// 与并行组(工作进程和主进程共享锁)这两种情形 —— 流传最广的那些自连版本恰恰在这两处
    /// 会指认出错误的阻塞方。<c>pg_blocking_pids()</c> 是服务端按真实等待队列算的,并行组也替你折算好了。
    /// <b>代价是它不便宜</b>(官方文档明写"频繁调用会影响性能"),所以下面那条前置过滤不是可有可无的。
    /// </para>
    /// <para>
    /// <b>前置过滤为什么是 <c>wait_event_type IS NOT NULL AND &lt;&gt; 'Client'</c>,而不是 <c>= 'Lock'</c></b>:
    /// 没在等待的后端不可能被阻塞,等在客户端套接字上的(<c>Client:ClientRead</c>,也就是所有空闲连接)
    /// 也不可能被阻塞 —— 这两类剔掉是白赚。但<b>不能收紧成 <c>= 'Lock'</c></b>:
    /// 可串行化事务等安全快照时 <c>pg_blocking_pids()</c> 照样报阻塞方,而那种等待的
    /// <c>wait_event_type</c> 是 <c>IPC</c>,收紧就会把它整类丢掉,而且是静默地丢。
    /// </para>
    /// <para>
    /// <b>为什么从 <c>pg_stat_activity</c> 出发、把未授予的锁 <c>LEFT JOIN</c> 进来,而不是反过来</b>:
    /// "谁被谁挡住"这个结论由 <c>pg_blocking_pids()</c> 独立给出,不依赖 <c>pg_locks</c> 里那一行长什么样。
    /// 反着写(从未授予的锁出发)的话,一旦某种等待在 <c>pg_locks</c> 里没有对应行,整条阻塞关系就消失 ——
    /// <b>"没查到"与"没有阻塞"长得一模一样,而这一栏最不能撒的谎正是这个</b>。
    /// 现在最坏也只是 <c>object</c> / <c>mode</c> 两格空着,两个 id 与 <c>query</c> 照样是对的。
    /// </para>
    /// <para>
    /// <b><c>object</c> 按锁类型分别成文;行锁冲突下它<b>不是表名</b>,这一条要写给界面看。</b>
    /// 实测两种最常见的形态:
    /// <list type="bullet">
    ///   <item>
    ///     两个事务改同一行 → 等的是 <c>transactionid</c> 锁,<c>object</c> 是 <c>transaction 6186</c>。
    ///     <b>表名不在这一格里</b> —— PG 的行级冲突是"等对方那个事务结束",锁挂在事务号上而不是挂在行上。
    ///     想知道是哪张表,看同一行的 <c>query</c>。这里<b>不去猜</b>(比如拿被阻塞方持有的
    ///     <c>RowExclusiveLock</c> 反推表名):一条语句碰多张表时那种反推会给出错的表,
    ///     而运维面上一个错的表名比一句 <c>transaction 6186</c> 坏得多。
    ///   </item>
    ///   <item>
    ///     DDL 撞上读事务 → 等的是 <c>relation</c> 锁,<c>object</c> 就是表名(实测 <c>lock_probe</c>)。
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// <b><c>relation::regclass</c> 只在"这把锁属于当前库或共享目录"时才敢用。</b>
    /// <c>pg_locks</c> 是<b>全实例</b>的,别的库里的关系锁也在里面;而 <c>regclass</c> 只认当前库的
    /// <c>pg_class</c>,拿别的库的 oid 去转,要么转不出(退化成数字),
    /// <b>要么在当前库里碰巧撞上同一个 oid,给出一个完全无关的表名</b>。
    /// 所以先比 <c>w.database</c>:等于 0(共享目录)或等于当前库才转,否则老实写 <c>relation &lt;oid&gt;</c>。
    /// </para>
    /// <para>
    /// <b><c>mode</c> 把冲突的两边并成一格</b>:<c>&lt;要的模式&gt; &lt;- &lt;持有方的模式&gt;</c>,
    /// 实测形如 <c>ShareLock &lt;- ExclusiveLock</c>(行锁冲突)、
    /// <c>AccessExclusiveLock &lt;- AccessShareLock</c>(DDL 撞读)。只给一边说不清冲突,
    /// 而契约的五列里没有第六格放持有方。持有方那一半是拿"同一把锁"的 9 个定位列反查
    /// <c>granted</c> 的行得来的 —— 用 <c>IS NOT DISTINCT FROM</c> 而不是 <c>=</c>,
    /// 因为这 9 列里绝大多数是 NULL,用 <c>=</c> 会整条落空、一个持有方都配不上。
    /// 它可能给出多个模式,所以 <c>string_agg</c> 去重后拼起来;查不到就整段省掉
    /// (<c>COALESCE(' &lt;- ' || …)</c>),而不是留一个空的箭头。
    /// </para>
    /// <para>
    /// <b><c>query</c> 是被阻塞方的语句</b>,与 MySQL 那条同一个理由:这一行的主语是被阻塞的会话
    /// (<c>blocked_id</c> 排第一),读起来才一致;而持锁方十有八九是"开着事务闲着",
    /// 它的 <c>query</c> 是上一条早跑完的语句。想看持锁方在干什么,拿 <c>blocking_id</c>
    /// 去会话栏里找 —— 那一栏就在同一页上。
    /// </para>
    /// <para>
    /// <b>没人争锁时它干净地返回 0 行</b>(实测),这正是契约要的:空表意味着"真的没有阻塞",
    /// 而不是"这条查询没跑通"。
    /// </para>
    /// </remarks>
    public override string? LockListSql => """
        SELECT a.pid                                    AS "blocked_id",
               b.pid                                    AS "blocking_id",
               COALESCE(
                   CASE w.locktype
                       WHEN 'relation'      THEN rel.name
                       WHEN 'tuple'         THEN rel.name || ' tuple (' || w.page::text || ',' || w.tuple::text || ')'
                       WHEN 'page'          THEN rel.name || ' page ' || w.page::text
                       WHEN 'transactionid' THEN 'transaction ' || w.transactionid::text
                       WHEN 'virtualxid'    THEN 'virtual transaction ' || w.virtualxid
                       WHEN 'advisory'      THEN 'advisory (' || w.classid::text || ',' || w.objid::text || ')'
                       ELSE w.locktype
                   END,
                   w.locktype,
                   '')                                  AS "object",
               COALESCE(w.mode, '') || COALESCE(' <- ' || h.modes, '')  AS "mode",
               a.query                                  AS "query"
          FROM pg_catalog.pg_stat_activity a
          CROSS JOIN LATERAL pg_catalog.unnest(pg_catalog.pg_blocking_pids(a.pid)) AS b(pid)
          LEFT JOIN LATERAL (
              SELECT l.*
                FROM pg_catalog.pg_locks l
               WHERE l.pid = a.pid AND NOT l.granted
               ORDER BY l.locktype
               LIMIT 1
          ) w ON true
          LEFT JOIN LATERAL (
              SELECT CASE
                         WHEN w.relation IS NULL THEN NULL
                         WHEN w.database = 0
                           OR w.database = (SELECT d.oid FROM pg_catalog.pg_database d
                                             WHERE d.datname = pg_catalog.current_database())
                         THEN w.relation::regclass::text
                         ELSE 'relation ' || w.relation::text
                     END AS name
          ) rel ON true
          LEFT JOIN LATERAL (
              SELECT pg_catalog.string_agg(DISTINCT g.mode, ',' ORDER BY g.mode) AS modes
                FROM pg_catalog.pg_locks g
               WHERE g.pid = b.pid
                 AND g.granted
                 AND g.locktype      =                    w.locktype
                 AND g.database      IS NOT DISTINCT FROM w.database
                 AND g.relation      IS NOT DISTINCT FROM w.relation
                 AND g.page          IS NOT DISTINCT FROM w.page
                 AND g.tuple         IS NOT DISTINCT FROM w.tuple
                 AND g.virtualxid    IS NOT DISTINCT FROM w.virtualxid
                 AND g.transactionid IS NOT DISTINCT FROM w.transactionid
                 AND g.classid       IS NOT DISTINCT FROM w.classid
                 AND g.objid         IS NOT DISTINCT FROM w.objid
                 AND g.objsubid      IS NOT DISTINCT FROM w.objsubid
          ) h ON true
         WHERE a.wait_event_type IS NOT NULL
           AND a.wait_event_type <> 'Client'
         ORDER BY "blocked_id", "blocking_id"
        """;

    /// <inheritdoc />
    /// <remarks>
    /// <b>给的是 <c>format_type()</c> 的规范形态,不是 <c>varchar</c> / <c>timestamptz</c> 这些别名。</b>
    /// PG 会把别名当场折算掉,实测(18.1)建完再读回来:<c>varchar(50)</c> → <c>character varying(50)</c>、
    /// <c>timestamptz</c> → <c>timestamp with time zone</c>、<c>char(4)</c> → <c>character(4)</c>、
    /// <c>int8</c> → <c>bigint</c>。而本包的 <see cref="DescribeAsync" /> 读的正是 <c>format_type</c>,
    /// 于是下拉里挑 <c>varchar(50)</c>、加完列一刷新变成 <c>character varying(50)</c> ——
    /// 用户会以为插件把他的类型改了。<b>下拉里写规范形态,这一趟来回就是恒等的。</b>
    /// <para>
    /// <b>不给 <c>serial</c> / <c>bigserial</c>,理由与 MySQL 不给 <c>INT(11)</c> 是同一条</b>:
    /// 它们不是真类型,是"整数 + 序列 + 默认值"的语法糖。实测 <c>ADD COLUMN c29 serial</c>
    /// 之后存下来的是 <c>integer</c> 外加一条 <c>nextval('type_probe_c29_seq'::regclass)</c> 默认值 ——
    /// 下拉里摆一个"服务端会当场改写掉"的选项,等于让用户建完回头发现类型对不上,最像插件出了 bug。
    /// 要自增该用 <c>GENERATED BY DEFAULT AS IDENTITY</c>,而那不是类型、是列属性,
    /// 得等 <see cref="AddColumnDdl" /> 那一格开出来(见那里的注释)。
    /// </para>
    /// <para>
    /// <b>不给 <c>money</c></b>:它的输出与服务端的 <c>lc_monetary</c> 绑定,同一份数据在两台不同 locale
    /// 的服务器上显示不同,PG 自己的文档也不推荐用它。要存钱用 <c>numeric</c>。
    /// </para>
    /// <para>
    /// <b>带括号的几项是模板</b>,填的是最常用的一组值等着用户改。这里有一条与 MySQL <b>正相反</b>、
    /// 最容易凭 MySQL 经验想当然的实测差别:PG 上 <c>character varying</c> <b>不带长度是合法的</b>
    /// (等于不限长),<c>numeric</c> 不带精度也是合法的,而且<b>不会</b>像 MySQL 的 <c>DECIMAL</c>
    /// 那样静默变成 <c>(10,0)</c> 把小数位抹掉。所以两种形态都摆进来:带长度的当默认模板,
    /// 不带的留给"就是不想限长"的场合。
    /// </para>
    /// <para>
    /// 数组只给 <c>integer[]</c> / <c>text[]</c> 两个样例:PG 里<b>任何</b>类型都能加 <c>[]</c>,
    /// 穷举没有意义,给两个让用户看出"原来可以这么写"就够了。
    /// 枚举(<c>CREATE TYPE … AS ENUM</c>)是用户自定义类型,静态表里放不了 ——
    /// 这与 MySQL 的 <c>ENUM('a','b')</c> 是<b>列类型</b>不同,是本表里最容易被跨方言想当然的一格。
    /// </para>
    /// <para>
    /// 与 <c>GetDbTypes()</c> 的差别正是契约点名的那条(§2.3):它返回的是"这个库<b>当前用到了</b>哪些类型",
    /// 在 PG 上还会混进 <c>pg_node_tree</c> / <c>USER-DEFINED</c> 这种占位符,而且随建表变多。
    /// 这里是<b>静态表</b>,与库里有什么无关。
    /// </para>
    /// </remarks>
    public override IReadOnlyList<string> CommonTypes => TypeNames;

    // ─────────────────────────── 表设计器(M4) ───────────────────────────
    //
    // DropColumnDdl 与 CreateIndexDdl **不覆盖**,基类的通行写法在 PG 上逐字成立(真机逐条发过):
    //   ALTER TABLE "app"."Odd Table" DROP COLUMN "qty"
    //   CREATE [UNIQUE] INDEX "ix_ab" ON "app"."Odd Table" ("a", "b")
    // 建索引这条有一格特别容易"顺手改错",在这里钉死:
    //   **索引名必须是裸名,不能加 schema 限定。** PG 的索引跟着表走 ——
    //   在 "app"."Odd Table" 上建的索引自动落在 app 里(实测 pg_indexes.schemaname = 'app'),
    //   而写成 CREATE INDEX "app"."ix_bad" ON … 是**语法错**(实测 42601 syntax error at or near "."),
    //   插入符就指在那个点上。基类给的正是裸名,所以这里什么都不用做。
    //   注意这与 DropIndexDdl 恰好相反(那边非加不可),见下 —— 一正一反,是 PG 语法本身的不对称。
    // 删列那一路有两条 PG 实测形态,调用方要知道(它们不改 DDL 文本,所以记在这儿):
    //   ① 只用到该列的索引会被**静默一并删掉**;复合索引则**静默少一列继续存在** —— 与 MySQL 同病。
    //   ② 被视图/物化视图引用的列删不掉:2BP01 cannot drop column … because other objects depend on it,
    //      DETAIL 里会点名是哪个视图。这条比 MySQL 那边的外键限制更常撞上。

    /// <inheritdoc />
    /// <remarks>
    /// <b>必须覆盖:基类的 <c>DROP INDEX "ix"</c> 在 PG 上不是"删不掉",是删不到。</b>
    /// 实测 <c>DROP INDEX "ix_ab"</c>(索引在 app 里、<c>search_path</c> 是默认的
    /// <c>"$user", public</c>)→ <c>42704 index "ix_ab" does not exist</c>。
    /// <para>
    /// 根源是 PG 的索引<b>住在 schema 里</b>(与表、视图同一个命名空间),裸名要靠
    /// <c>search_path</c> 解析。而 <c>search_path</c> 是连接级设置,本包的对象树可以停在任何 schema 上 ——
    /// 靠它解析等于把"删哪个索引"交给一个界面上看不见的变量。<b>更坏的一种失败不报错:</b>
    /// 两个 schema 里都有 <c>ix_name</c> 时,裸名会安静地删掉 <c>search_path</c> 先命中的那一个,
    /// 而用户点的是另一个。
    /// </para>
    /// <para>
    /// 限定用的是<b>表所在的 schema</b>,这一步是对的而不是近似:PG 强制索引与它的表同 schema
    /// (<c>CREATE INDEX</c> 根本不接受给索引单独指定 schema,见上面那段注释),
    /// 所以"表在哪儿,索引就在哪儿"是语法层面的保证,不是惯例。
    /// </para>
    /// <para>
    /// <c>target.Schema</c> 为空时(调用方还没拿到 schema 的中间态)回落到基类的裸名写法 ——
    /// 那时按 <c>search_path</c> 解析是<b>唯一</b>能做的事,而不是一个默认值。
    /// </para>
    /// <para>
    /// <b>还有一条这条 DDL 表达不了、但界面该先拦下的</b>:主键/唯一<b>约束</b>背后的索引删不掉。
    /// 实测 <c>DROP INDEX "app"."Odd Table_pkey"</c> →
    /// <c>2BP01 cannot drop index app."Odd Table_pkey" because constraint … requires it</c>,
    /// 提示里点名要改用 <c>DROP CONSTRAINT</c>。结构页的索引栏会把主键索引一起列出来
    /// (<see cref="DescribeAsync" /> 是照实报的),所以那颗按钮点在主键上必然是这条错 ——
    /// 等契约开出"删约束"那一格再接。现在<b>不</b>在这里偷偷改写成
    /// <c>ALTER TABLE … DROP CONSTRAINT</c>:用户点的是"删索引",而删约束会连带把外键引用一起处理掉,
    /// 那是另一件事。
    /// </para>
    /// </remarks>
    public override string? DropIndexDdl(SqlObject target, string indexName)
    {
        ArgumentNullException.ThrowIfNull(target);
        return string.IsNullOrEmpty(target.Schema)
            ? base.DropIndexDdl(target, indexName)
            : $"DROP INDEX {QuoteIdentifier(target.Schema)}.{QuoteIdentifier(indexName)}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// 文本沿用基类的通行写法(<c>ALTER TABLE … ADD COLUMN …</c>,真机逐条发过),覆盖只做一件事:
    /// <b>列定义里说了、而这条 DDL 表达不了的,一律不生成。</b>
    /// 通用写法只写得出"列名 + 类型 + <c>NOT NULL</c> + <c>DEFAULT</c>"四样,列模型上另外四样它
    /// <b>一声不吭地丢掉</b>,而 PG 会照办出一个<b>普通列</b>:
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="SqlColumn.IsGenerated" /> —— 拼不出 <c>GENERATED ALWAYS AS (expr) STORED</c>
    ///     (PG 18 起还有 <c>VIRTUAL</c>),因为 <see cref="SqlColumn" /> 上<b>根本没有生成表达式这一格</b>。
    ///     用户点的"加一个生成列"办成了别的事,而且哪儿都不提示。
    ///   </item>
    ///   <item>
    ///     <see cref="SqlColumn.IsPrimaryKey" /> —— 拼不出 <c>PRIMARY KEY</c>。
    ///     顺带一提这条在 PG 上多半也建不成:表已有主键时 <c>ADD COLUMN … PRIMARY KEY</c> 报
    ///     <c>42P16 multiple primary keys for table … are not allowed</c>(实测)。
    ///   </item>
    ///   <item>
    ///     <see cref="SqlColumn.IsAutoIncrement" /> —— 拼不出 <c>GENERATED BY DEFAULT AS IDENTITY</c>。
    ///     (它本身完全合法,实测一条 <c>ALTER TABLE … ADD COLUMN "sid" int GENERATED BY DEFAULT AS IDENTITY</c>
    ///     就加上了;只是这条通用 DDL 说不出口。)
    ///   </item>
    ///   <item><see cref="SqlColumn.Comment" /> —— 见下,这一条是 PG 的语法限制,不只是本层的。</item>
    /// </list>
    /// 静默办成别的事比报错坏得多,所以这四种一律返回 <see langword="null" />,
    /// 让界面显示"该数据库不支持这样加列"(§7.8)。
    /// <para>
    /// <b>注释那一条在 PG 上是硬限制,与 MySQL 那边"其实支持、只是我们不敢转义"不同。</b>
    /// PG 没有内联的 <c>COMMENT</c> 子句,列注释只能另发一条 <c>COMMENT ON COLUMN … IS '…'</c>。
    /// 而本契约这一格返回的是<b>一条</b> DDL,调用方
    /// (<c>SqlStructureTabViewModel.ApplyDdlAsync</c>)也是按一条来预览与确认的;
    /// 塞成 <c>ALTER …; COMMENT ON …</c> 两句会让确认框里那段原文与实际发生的事对不上,
    /// 而且第二条失败时第一条已经生效(PG 的 DDL 虽然可回滚,但这里没有包事务)。
    /// 等契约把"一次多条 DDL"那一格开出来再接;在那之前宁可不生成 ——
    /// 丢掉用户敲的注释是静默的,"这样加不了"是看得见的。
    /// </para>
    /// <para>
    /// <b>类型与默认值都是原样拼进去的,所以调用方给的文本必须自己成立。</b>
    /// 好消息是这趟来回在 PG 上是通的:<see cref="DescribeAsync" /> 读回来的
    /// <see cref="SqlColumn.DataType" /> 就是 <c>format_type</c> 的规范形态
    /// (<c>character varying(50)</c> / <c>numeric(12,3)</c> / <c>integer[]</c> / <c>app.mood</c>),
    /// 原样拿去加列成立;<see cref="CommonTypes" /> 给的也是同一套形态。
    /// </para>
    /// <para>
    /// <b>两条 PG 特有的运行时形态,DDL 文本改不了,记在这儿给界面提示用</b>:
    /// ① 表里已有行时 <c>NOT NULL</c> 不带 <c>DEFAULT</c> 必然失败 —— 实测
    /// <c>23502 column "must" of relation "nn_probe" contains null values</c>,
    /// 所以"非空"与"默认值"这两个输入框在非空表上是绑定关系,该在界面上就拦住;
    /// ② PG 11 起 <c>ADD COLUMN … DEFAULT &lt;常量&gt;</c> 只改目录、不重写表(秒级完成),
    /// 但默认值是<b>易变函数</b>(如 <c>random()</c>)时会退回全表重写,并全程持
    /// <c>AccessExclusiveLock</c> —— 大表上那是一次真停机,值得在确认框里点出来。
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
    /// 把 <c>relkind</c> 映射成对象类别。
    /// <para><c>'p'</c>(分区表)与 <c>'r'</c> 同归为表:用户眼里它就是一张表。</para>
    /// </summary>
    /// <param name="relkind">pg_class.relkind。</param>
    /// <returns>对象类别。</returns>
    private static SqlObjectKind KindOf(string relkind) => relkind switch
    {
        "v" => SqlObjectKind.View,
        "m" => SqlObjectKind.MaterializedView,
        _ => SqlObjectKind.Table
    };

    /// <summary>读列。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="key">schema 与对象名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>列。</returns>
    private static async Task<IReadOnlyList<SqlColumn>> ReadColumnsAsync(
        DbConnection connection, object?[] key, CancellationToken cancellationToken)
    {
        // 逐格说明为什么是这么取的:
        //  · format_type(atttypid, atttypmod) —— **完整原生形态**(character varying(50)、
        //    numeric(12,3)、integer[]、app.mood)。把长度拆出来单存是 DbMaintenance 的老路,
        //    结果是 text/jsonb 长度恒 0、datetime(3) 的 3 被当成长度(§3.7)。
        //  · attidentity IN ('a','d') —— GENERATED ALWAYS / BY DEFAULT AS IDENTITY 两种都认。
        //  · pg_get_serial_sequence 非空 —— 老式 serial。它只认"序列 OWNED BY 这一列"的情形,
        //    所以再补一条 nextval 默认值的判断:手工挂上去的序列同样是"不给值服务端也会填"。
        //    只在真表上调它(视图/物化视图上无意义)。
        //  · attgenerated IN ('s','v') —— 生成列。**'v' 是 PG 18 的虚拟生成列**,
        //    它和 STORED 一样不能出现在 INSERT/UPDATE 的列表里,漏掉它回写就会报错。
        //  · 生成列的 pg_attrdef 里放的是**生成表达式**,不是默认值 —— 所以这一格要清成 NULL,
        //    否则表设计器会把 (amount * 2) 当成默认值写回去。
        //  · 主键走 pg_constraint(contype='p'),不走 pg_index:约束才是"用户声明的那个主键"。
        string sql = string.Format(
            CultureInfo.InvariantCulture,
            """
            SELECT a.attnum,
                   a.attname::text,
                   pg_catalog.format_type(a.atttypid, a.atttypmod),
                   NOT a.attnotnull,
                   COALESCE((SELECT true
                             FROM pg_catalog.pg_constraint pk
                             WHERE pk.conrelid = c.oid AND pk.contype = 'p' AND a.attnum = ANY (pk.conkey)), false),
                   (a.attidentity IN ('a', 'd')
                    OR (c.relkind IN ('r', 'p')
                        AND pg_catalog.pg_get_serial_sequence(
                              pg_catalog.quote_ident(n.nspname) || '.' || pg_catalog.quote_ident(c.relname),
                              a.attname) IS NOT NULL)
                    OR pg_catalog.pg_get_expr(ad.adbin, ad.adrelid) LIKE 'nextval(%'),
                   a.attgenerated IN ('s', 'v'),
                   CASE WHEN a.attgenerated IN ('s', 'v') THEN NULL
                        ELSE pg_catalog.pg_get_expr(ad.adbin, ad.adrelid) END,
                   COALESCE(pg_catalog.col_description(c.oid, a.attnum), '')
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid
            LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid = c.oid AND ad.adnum = a.attnum
            WHERE c.relname = @p1
              AND {0}
              AND a.attnum > 0
              AND NOT a.attisdropped
            ORDER BY a.attnum
            """,
            RelationFilter);

        return await QueryAsync(
            connection,
            sql,
            r =>
            {
                int ordinal = Int(r, 0);
                string name = Str(r, 1);
                string type = Str(r, 2);
                bool nullable = Bool(r, 3);
                bool primaryKey = Bool(r, 4);
                bool autoIncrement = Bool(r, 5);
                bool generated = Bool(r, 6);
                string? defaultValue = StrOrNull(r, 7);
                string comment = Str(r, 8);
                return new SqlColumn(
                    name,
                    ordinal,
                    type,
                    nullable,
                    primaryKey,
                    autoIncrement,
                    generated,
                    defaultValue,
                    defaultValue is not null && !IsLiteralDefault(defaultValue),
                    comment);
            },
            key,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读索引。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="key">schema 与对象名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>索引。</returns>
    private static async Task<IReadOnlyList<SqlIndex>> ReadIndexesAsync(
        DbConnection connection, object?[] key, CancellationToken cancellationToken)
    {
        // pg_get_indexdef(oid, 第 n 列, true) 是拿"有序列名"的唯一靠谱办法:
        // indkey 里表达式列的 attnum 是 0,照着 attnum 去 pg_attribute 里找会直接丢掉
        // lower(title) 这一列。只取 indnkeyatts(键列),INCLUDE 出来的列不参与定位。
        // Definition 放 pg_get_indexdef 的原文 —— 部分索引的 WHERE、表达式索引的表达式、
        // 排序方向、opclass,只有原文说得清。
        string sql = string.Format(
            CultureInfo.InvariantCulture,
            """
            SELECT i.relname::text,
                   x.indisunique,
                   x.indisprimary,
                   am.amname::text,
                   x.indexprs IS NOT NULL,
                   x.indpred IS NOT NULL,
                   k.ord,
                   pg_catalog.pg_get_indexdef(x.indexrelid, k.ord::int, true),
                   pg_catalog.pg_get_indexdef(x.indexrelid)
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_index x ON x.indrelid = c.oid
            JOIN pg_catalog.pg_class i ON i.oid = x.indexrelid
            JOIN pg_catalog.pg_am am ON am.oid = i.relam
            CROSS JOIN LATERAL pg_catalog.generate_series(1, x.indnkeyatts) AS k(ord)
            WHERE c.relname = @p1
              AND {0}
            ORDER BY i.relname, k.ord
            """,
            RelationFilter);

        List<IndexRow> rows = await QueryAsync(
            connection,
            sql,
            r => new IndexRow(
                Str(r, 0), Bool(r, 1), Bool(r, 2), Str(r, 3), Bool(r, 4), Bool(r, 5), Int(r, 6), Str(r, 7), Str(r, 8)),
            key,
            cancellationToken).ConfigureAwait(false);

        return Fold(
            rows,
            row => row.Name,
            (name, group) =>
            {
                IndexRow head = group[0];
                // Kind 用逗号拼一串机器可读的标记而不是自然语言:界面要按标记上色/加图标,
                // 而文案要过 Loc —— 把中文烧进数据层,五种语言就只剩一种。
                string kind = head.Method;
                if (head.HasExpressions)
                {
                    kind += ",expression";
                }
                if (head.IsPartial)
                {
                    kind += ",partial";
                }
                return new SqlIndex(
                    name,
                    [.. group.OrderBy(x => x.Ordinal).Select(x => x.ColumnExpression)],
                    head.IsUnique,
                    head.IsPrimary,
                    kind,
                    head.Definition);
            });
    }

    /// <summary>读外键。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="key">schema 与对象名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>外键。</returns>
    private static async Task<IReadOnlyList<SqlForeignKey>> ReadForeignKeysAsync(
        DbConnection connection, object?[] key, CancellationToken cancellationToken)
    {
        // **两个数组必须一起按位置展开**:分别 unnest 再拼,复合外键的列对应关系就会错位 ——
        // 而外键画错的后果是关系图上一条指向错列的线,用户照着它写 JOIN。
        //
        // 写法上有个 PG 特有的陷阱:多参数的 `unnest(a, b)` **只允许直接出现在 FROM 里**,
        // 一旦要配 WITH ORDINALITY 就必须写成 `ROWS FROM (unnest(a), unnest(b)) WITH ORDINALITY`。
        // 直接写 `unnest(a, b) WITH ORDINALITY` 会被服务端以
        // `42883: function pg_catalog.unnest(smallint[], smallint[]) does not exist` 打回
        // —— 报的是"函数不存在",很容易让人以为是版本问题,其实是语法位置不对。
        // ROWS FROM 的语义正是"按位置并排展开",与这里要的一致。
        string sql = string.Format(
            CultureInfo.InvariantCulture,
            """
            SELECT con.conname::text,
                   fn.nspname::text,
                   f.relname::text,
                   con.confupdtype::text,
                   con.confdeltype::text,
                   ck.ord,
                   sa.attname::text,
                   ta.attname::text
            FROM pg_catalog.pg_constraint con
            JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_catalog.pg_class f ON f.oid = con.confrelid
            JOIN pg_catalog.pg_namespace fn ON fn.oid = f.relnamespace
            CROSS JOIN LATERAL ROWS FROM (
                     pg_catalog.unnest(con.conkey), pg_catalog.unnest(con.confkey)
                 ) WITH ORDINALITY AS ck(src, tgt, ord)
            JOIN pg_catalog.pg_attribute sa ON sa.attrelid = con.conrelid AND sa.attnum = ck.src
            JOIN pg_catalog.pg_attribute ta ON ta.attrelid = con.confrelid AND ta.attnum = ck.tgt
            WHERE con.contype = 'f'
              AND c.relname = @p1
              AND {0}
            ORDER BY con.conname, ck.ord
            """,
            RelationFilter);

        List<ForeignKeyRow> rows = await QueryAsync(
            connection,
            sql,
            r => new ForeignKeyRow(
                Str(r, 0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Int(r, 5), Str(r, 6), Str(r, 7)),
            key,
            cancellationToken).ConfigureAwait(false);

        return Fold(
            rows,
            row => row.Name,
            (name, group) =>
            {
                ForeignKeyRow[] ordered = [.. group.OrderBy(x => x.Ordinal)];
                ForeignKeyRow head = ordered[0];
                return new SqlForeignKey(
                    name,
                    [.. ordered.Select(x => x.Column)],
                    head.ReferencedSchema,
                    head.ReferencedTable,
                    [.. ordered.Select(x => x.ReferencedColumn)],
                    ReferentialAction(head.OnDelete),
                    ReferentialAction(head.OnUpdate));
            });
    }

    /// <summary>把 <c>pg_constraint</c> 的单字符动作码翻成 SQL 关键字。</summary>
    /// <param name="code">动作码。</param>
    /// <returns>SQL 关键字。</returns>
    private static string ReferentialAction(string code) => code switch
    {
        "c" => "CASCADE",
        "n" => "SET NULL",
        "d" => "SET DEFAULT",
        "r" => "RESTRICT",
        "a" => "NO ACTION",
        _ => code
    };

    /// <summary>
    /// 判一段默认值原文是不是**纯字面量**。
    /// <para>
    /// 这一格的全部意义在于把 <c>CURRENT_TIMESTAMP</c>(每行求值一次)与字符串
    /// <c>'CURRENT_TIMESTAMP'</c>(一个碰巧长这样的常量)分开 —— 表设计器要靠它决定
    /// 生成 DDL 时加不加引号,加错一边就是"默认值变成了固定的那一秒"或者"建表直接语法错"。
    /// </para>
    /// <para>
    /// <b>为什么不看 <c>pg_attrdef.adbin</c> 的节点类型</b>:真机实测(PG 18.1)这条路是死的 ——
    /// <c>'new'::character varying</c> 的节点是 <c>FUNCEXPR</c> 而不是 <c>CONST</c>
    /// (varchar 的长度强制是一次函数调用),<c>numeric(12,3) DEFAULT 0</c> 同样是 <c>FUNCEXPR</c>。
    /// 照 <c>{CONST</c> 判会把一大批常量默认值误判成表达式。所以只能按 <c>pg_get_expr</c>
    /// 的输出形态判,而那个输出是 PG 自己规范化过的,形态有限、可穷举。
    /// </para>
    /// </summary>
    /// <param name="source">默认值原文。</param>
    /// <returns>是不是纯字面量。</returns>
    private static bool IsLiteralDefault(string source)
    {
        string text = source.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        // 字符串字面量:允许 PG 会吐出来的前缀(B'101'::"bit" 里的 B、转义串的 E、十六进制的 X)。
        int start = text.Length > 1 && text[0] is 'B' or 'b' or 'E' or 'e' or 'X' or 'x' && text[1] == '\'' ? 1 : 0;
        if (text[start] == '\'')
        {
            int i = start + 1;
            while (i < text.Length)
            {
                if (text[i] != '\'')
                {
                    i++;
                }
                else if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i += 2;   // 连续两个单引号是被转义的一个,不是收尾。
                }
                else
                {
                    break;
                }
            }
            // 引号没闭合:与其猜,不如当表达式 —— 判错成表达式只是少加一对引号,
            // 判错成字面量会把一段可执行的东西当常量写回去。
            return i < text.Length && IsCastTailOnly(text[(i + 1)..]);
        }

        Match number = NumberHeadPattern.Match(text);
        if (number.Success)
        {
            return IsCastTailOnly(text[number.Length..]);
        }

        foreach (string keyword in (string[])["true", "false", "null"])
        {
            if (text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
                && IsCastTailOnly(text[keyword.Length..]))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>字面量之后剩下的部分是不是"只有类型转换"。</summary>
    /// <param name="tail">剩余文本。</param>
    /// <returns>是不是只有类型转换。</returns>
    private static bool IsCastTailOnly(string tail) =>
        tail.AsSpan().Trim().IsEmpty || CastTailPattern.IsMatch(tail);

    /// <summary>
    /// 把一个字符串包成 SQL 字面量(单引号加倍)。
    /// <para>
    /// 只给 <see cref="EstimateRowCountSql" /> 这类"接口不给参数通道"的地方用。
    /// PG 从 9.1 起 <c>standard_conforming_strings</c> 默认 on,反斜杠不是转义字符,
    /// 所以加倍单引号就够;**要是哪天遇到把它关掉的服务端,这里必须改成 <c>E''</c> 形态**。
    /// </para>
    /// </summary>
    /// <param name="value">原文。</param>
    /// <returns>SQL 字面量。</returns>
    private static string Literal(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>
    /// 剥掉末尾的语句终止符(可能有多个,后面还可能跟着空白)。
    /// <para>
    /// <see cref="ExplainSql" /> 要往语句<b>前面</b>接 <c>EXPLAIN (…)</c>,留着尾巴上的分号与空白
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
    /// 常用类型的静态表(取舍与陷阱见 <see cref="CommonTypes" />)。
    /// 提成静态字段是因为类型下拉每次打开都要读它,没必要每次新建一个数组。
    /// <para>
    /// 表里每一项都在真机(18.1)上以
    /// <c>ALTER TABLE … ADD COLUMN "cNN" &lt;这一项&gt;</c> 发过一遍,再用
    /// <c>format_type(atttypid, atttypmod)</c> 读回来比对 —— <b>读回来必须与这里写的逐字相同</b>。
    /// 加新项之前请照做:别名(<c>varchar</c>、<c>int8</c>、<c>timestamptz</c>)与语法糖
    /// (<c>serial</c>)都会在这一步露馅。
    /// </para>
    /// </summary>
    private static readonly string[] TypeNames =
    [
        // 整数。PG 没有 unsigned,也没有显示宽度这种东西 —— 整数这一族比 MySQL 干净得多。
        "smallint", "integer", "bigint",
        // 定点与浮点:钱一律用 numeric。不带精度的 numeric 是"任意精度",不是 MySQL 那种静默 (10,0)。
        "numeric(12,2)", "numeric", "real", "double precision",
        "boolean",
        // 文本。PG 上 text 与 character varying 存储与性能一致,长度只是一条约束 ——
        // 所以把 text 排在最前面,它才是 PG 的惯用写法。
        "text", "character varying(255)", "character varying", "character(36)",
        "bytea",
        // 日期时间:timestamp with time zone 排在 without 前面 ——
        // PG 上前者存的是绝对时刻(带时区换算),后者是"一串数字",跨时区必错。
        "date", "time without time zone", "timestamp with time zone", "timestamp without time zone", "interval",
        // uuid 是真类型(不像 MySQL 要拿 CHAR(36)/BINARY(16) 凑);jsonb 排在 json 前面,
        // json 只存原文、每次查询都要重新解析,新表基本没有理由选它。
        "uuid", "jsonb", "json", "xml",
        // 网络地址与全文检索:这几个是 PG 独有、而用户常常不知道自己可以直接用的。
        "inet", "cidr", "macaddr", "tsvector",
        "bit(1)",
        // 数组只给两个样例:PG 里任何类型都能加 [],穷举没有意义。
        "integer[]", "text[]"
    ];

    /// <summary>索引查询的一行(每个索引每一列一行)。</summary>
    /// <param name="Name">索引名。</param>
    /// <param name="IsUnique">是否唯一。</param>
    /// <param name="IsPrimary">是否主键索引。</param>
    /// <param name="Method">访问方法(btree / gin / gist…)。</param>
    /// <param name="HasExpressions">是否表达式索引。</param>
    /// <param name="IsPartial">是否部分索引。</param>
    /// <param name="Ordinal">列序。</param>
    /// <param name="ColumnExpression">该列的列名或表达式原文。</param>
    /// <param name="Definition">整条索引定义原文。</param>
    private sealed record IndexRow(
        string Name,
        bool IsUnique,
        bool IsPrimary,
        string Method,
        bool HasExpressions,
        bool IsPartial,
        int Ordinal,
        string ColumnExpression,
        string Definition);

    /// <summary>外键查询的一行(每条外键每一列一行)。</summary>
    /// <param name="Name">约束名。</param>
    /// <param name="ReferencedSchema">目标 schema。</param>
    /// <param name="ReferencedTable">目标表。</param>
    /// <param name="OnUpdate">更新动作码。</param>
    /// <param name="OnDelete">删除动作码。</param>
    /// <param name="Ordinal">列序。</param>
    /// <param name="Column">本表列。</param>
    /// <param name="ReferencedColumn">目标列。</param>
    private sealed record ForeignKeyRow(
        string Name,
        string ReferencedSchema,
        string ReferencedTable,
        string OnUpdate,
        string OnDelete,
        int Ordinal,
        string Column,
        string ReferencedColumn);
}
