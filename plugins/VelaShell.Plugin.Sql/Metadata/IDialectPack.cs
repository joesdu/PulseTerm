using System.Data.Common;

namespace VelaShell.Plugin.Sql.Metadata;

/// <summary>
/// 一份方言资产。**这是本插件的第 ③ 层**(设计文档 §1.2):
/// 凡是"每种库长得不一样、而用户就是要看那个不一样"的,全在这里。
/// <para>
/// <b>为什么不用 <c>IDbMaintenance</c></b>:调研在四台真机上把它测了个透,结论是
/// "支持"不等于"说真话" —— <c>IsIdentity</c> 恒 True、视图的列返回 0、自定义 schema 静默不可达、
/// 索引丢唯一性、MySQL 的 <c>Length</c> 是从类型名里 <c>SUBSTRING</c> 出来的、
/// 生成列根本没有这个概念(§2.3 / §3.5–§3.7)。一个会说谎的元数据源比没有更坏,
/// 因为界面会如实地把假数据画出来。所以对象树与表结构**一律走这里**。
/// </para>
/// <para>
/// 实现方一律用**参数化查询**,并且**永不拼接用户给的标识符** ——
/// 表名走参数比对系统表,而不是拼进 SQL(§5.4.4 实测 <c>AS(表名)</c> 那条路能删表)。
/// </para>
/// </summary>
internal interface IDialectPack
{
    /// <summary>本包服务的方言。</summary>
    SqlDialect Dialect { get; }

    /// <summary>这个方言有没有 schema 这一级。没有的话对象树少一层。</summary>
    bool HasSchemas { get; }

    /// <summary>这个方言有没有"多个数据库"的概念(SQLite 没有)。</summary>
    bool HasDatabases { get; }

    /// <summary>
    /// 一条连接的元数据查询**能不能看见别的库**。
    /// <para>
    /// <b>这一格是对象树能不能用的分水岭。</b> 早先没有它,树对所有方言一律
    /// "根上列出全部库 → 展开时拿<b>同一条</b>元数据连接查 schema 与表" ——
    /// 而 PostgreSQL 与 SQL Server 的目录表(<c>pg_class</c> / <c>sys.objects</c>)
    /// <b>只覆盖当前连接所在的那个库</b>。
    /// </para>
    /// <para>
    /// 真机实测(PostgreSQL 18.1):连到 <c>postgres</c> 库、展开树上的 <c>ops_pg</c>,
    /// <c>pg_namespace</c> 只回一个 <c>public</c>、<c>pg_class</c> 回 <b>0 行</b> ——
    /// 而 <c>ops_pg.public</c> 里实实在在有 9 张表。用户看到的就是
    /// "每个库都点得开,每个库都是空的"。连接表单上那句
    /// "留空则列出你能看见的每一个库"把用户直接引到这条死路上。
    /// </para>
    /// <para>
    /// <see langword="true" />(MySQL / Oracle / SQLite):目录视图是**服务端级**或本来就只有一个库,
    /// 一条连接查得全,树共用会话上那条元数据连接。<br />
    /// <see langword="false" />(PostgreSQL / SQL Server):**每个库要一条自己的连接**,
    /// 由 <c>SqlSession.MetadataForAsync</c> 按库懒开并缓存。
    /// </para>
    /// </summary>
    bool MetadataSpansCatalogs { get; }

    /// <summary>这个方言有没有"存储过程 / 函数"这一类对象(SQLite 没有)。</summary>
    bool HasRoutines { get; }

    /// <summary>这个方言有没有序列(MySQL 与 SQLite 没有)。</summary>
    bool HasSequences { get; }

    /// <summary>列出数据库。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>数据库列表(系统库已按方言惯例标出或剔除)。</returns>
    Task<IReadOnlyList<SqlObject>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <summary>列出 schema。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>schema 列表。</returns>
    Task<IReadOnlyList<SqlObject>> ListSchemasAsync(DbConnection connection, CancellationToken cancellationToken);

    /// <summary>
    /// 列出某个 schema 下的表与视图(含物化视图)。
    /// <para>一次查完是刻意的:对象树的"表 (37)"要计数,分三次查会让展开抖三下。</para>
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="schema">schema;方言无此概念时传空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>对象列表。</returns>
    Task<IReadOnlyList<SqlObject>> ListRelationsAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken);

    /// <summary>
    /// 列出某个 schema 下的存储过程与函数。
    /// <para>
    /// <b>为什么是一次查完而不是分两个方法</b>:与 <see cref="ListRelationsAsync" /> 同理 ——
    /// 两种例程在每个方言里都住在同一张目录表(<c>pg_proc</c> / <c>ROUTINES</c> /
    /// <c>sys.objects</c> / <c>ALL_PROCEDURES</c>),分两次查是白跑一趟。
    /// </para>
    /// <para><see cref="HasRoutines" /> 为 <see langword="false" /> 时返回空表。</para>
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="schema">schema;方言无此概念时传空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>例程列表(<see cref="SqlObjectKind.Procedure" /> 与 <see cref="SqlObjectKind.Function" />)。</returns>
    Task<IReadOnlyList<SqlObject>> ListRoutinesAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken);

    /// <summary>
    /// 列出某个 schema 下的序列。
    /// <para><see cref="HasSequences" /> 为 <see langword="false" /> 时返回空表。</para>
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="schema">schema;方言无此概念时传空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>序列列表。</returns>
    Task<IReadOnlyList<SqlObject>> ListSequencesAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken);

    /// <summary>
    /// 取一张表/视图的完整结构:列 + 索引 + 外键。
    /// <para><b>视图也必须能拿到列</b> —— 这正是 <c>DbMaintenance</c> 返回 0 列且不抛异常的地方。</para>
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="target">目标对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表结构。</returns>
    Task<SqlTableSchema> DescribeAsync(DbConnection connection, SqlObject target, CancellationToken cancellationToken);

    /// <summary>
    /// 按方言把标识符加定界符并转义。
    /// <para>
    /// 这是**唯一**允许把用户标识符放进 SQL 的入口,而且它必须处理"标识符里含定界符"的情况
    /// (定界符加倍)。SqlSugar 的 <c>AS(表名)</c> 这条路没有任何校验也不转义,实测能删表。
    /// </para>
    /// </summary>
    /// <param name="identifier">标识符。</param>
    /// <returns>可安全拼进 SQL 的形态。</returns>
    string QuoteIdentifier(string identifier);

    /// <summary>
    /// 服务端分页。**不用 SqlSugar 的 <c>ToPageList</c>** —— 实测它在 SQL Server 上
    /// 只要用户 SQL 带 <c>ORDER BY</c> 就报 Msg 1033,而且兜底排序键是 <c>GetDate()</c>(§7.3)。
    /// </summary>
    /// <param name="innerSql">原查询。</param>
    /// <param name="offset">跳过行数。</param>
    /// <param name="limit">取多少行。</param>
    /// <returns>带分页的 SQL。</returns>
    string ApplyPaging(string innerSql, int offset, int limit);

    /// <summary>
    /// 估算行数的语句(底栏先显示"约 N 行",点了才做精确 <c>count(*)</c>)。
    /// <para>依据:精确 <c>count(*)</c> 在百万行表上是百毫秒级,而每翻一页都做一次是不可接受的(§7.3)。</para>
    /// </summary>
    /// <param name="target">目标对象。</param>
    /// <returns>取估算行数的 SQL;拿不到时为 <see langword="null" />。</returns>
    string? EstimateRowCountSql(SqlObject target);

    /// <summary>
    /// 取"当前 schema"的语句;方言没有这个概念(或不必问)时为 <see langword="null" />。
    /// <para>
    /// 只给对象树用来<b>加粗那一行</b>,让"我现在在哪"一眼可见。
    /// </para>
    /// <para>
    /// <b>为什么要问服务端而不是按 <c>public</c> / <c>dbo</c> 猜</b>:PG 上它由连接的
    /// <c>search_path</c> 决定、SQL Server 上由登录的 <c>DEFAULT_SCHEMA</c> 决定,
    /// 两者都可以被配置成别的值。按名字猜在那些库上会**把一个不是当前的 schema 加粗** ——
    /// 而加粗这件事的全部意义就是"这一个和别的不一样",指错了比不指更坏。
    /// </para>
    /// </summary>
    string? CurrentSchemaSql { get; }

    /// <summary>取当前会话 id 的语句 —— 旁路取消要用它(§3.10)。</summary>
    string? SessionIdSql { get; }

    /// <summary>
    /// 旁路取消语句(另一根连接上发)。这是**唯一**能打断"已经交给同步 API 的查询"的手段。
    /// </summary>
    /// <param name="sessionId">要取消的会话 id。</param>
    /// <returns>取消语句;方言不支持时为 <see langword="null" />。</returns>
    string? CancelSessionSql(string sessionId);

    /// <summary>取一张表建表 DDL 原文的语句;方言不支持时为 <see langword="null" />。</summary>
    /// <param name="target">目标对象。</param>
    /// <returns>SQL 或 <see langword="null" />。</returns>
    string? ShowCreateSql(SqlObject target);

    // ─────────────────────────── 运维面(M4) ───────────────────────────

    /// <summary>
    /// 执行计划。<c>IDbMaintenance</c> 里**一个都没有**(§2.3),每种方言的写法也完全不同。
    /// </summary>
    /// <param name="innerSql">用户的查询。</param>
    /// <param name="analyze">
    /// 是否**真的执行**再给计划(<c>EXPLAIN ANALYZE</c> / <c>SET STATISTICS PROFILE</c>)。
    /// <b>这个开关很危险</b>:analyze 会真的跑那条 SQL —— 对 <c>DELETE</c> 就是真删。
    /// 调用方必须先过护栏(§7.6),绿档之外的语句一律不给 analyze。
    /// </param>
    /// <returns>SQL;方言不支持时为 <see langword="null" />。</returns>
    string? ExplainSql(string innerSql, bool analyze);

    /// <summary>
    /// 当前会话/进程列表(运维面第一栏)。列约定:
    /// <c>id</c>、<c>user</c>、<c>host</c>、<c>db</c>、<c>state</c>、<c>seconds</c>、<c>query</c>。
    /// </summary>
    string? SessionListSql { get; }

    /// <summary>
    /// 锁与阻塞链。列约定:<c>blocked_id</c>、<c>blocking_id</c>、<c>object</c>、<c>mode</c>、<c>query</c>。
    /// <para>"谁锁了我"是运维排障里问得最多的一句,而它恰恰是 <c>IDbMaintenance</c> 完全没有的。</para>
    /// </summary>
    string? LockListSql { get; }

    /// <summary>
    /// 该方言的**静态类型表**(表设计器的类型下拉)。
    /// <para>
    /// <b>不能用 <c>GetDbTypes()</c></b>:实测它返回的是"这个库当前用到了哪些类型"
    /// (PG 上还混进 <c>pg_node_tree</c>/<c>USER-DEFINED</c> 这种占位符,而且随建表变多),
    /// 拿它去填新建列的类型下拉必然是错的(§2.3)。
    /// </para>
    /// </summary>
    IReadOnlyList<string> CommonTypes { get; }

    // ─────────────────────────── 表设计器(M4) ───────────────────────────

    /// <summary>
    /// 生成"加一列"的 DDL。
    /// <para>
    /// <b>为什么由方言包生成而不是调 <c>IDbMaintenance</c></b>:实测 MySQL 的
    /// <c>DropConstraint</c> 无视传进去的约束名、一律 <c>DROP PRIMARY KEY</c> 还返回 <c>True</c>;
    /// <c>UpdateColumn</c> 只发类型、把注释与默认值一起抹掉(§3.7)。写侧不能信它。
    /// </para>
    /// </summary>
    /// <param name="target">目标表。</param>
    /// <param name="column">列定义。</param>
    /// <returns>DDL;方言不支持时为 <see langword="null" />。</returns>
    string? AddColumnDdl(SqlObject target, SqlColumn column);

    /// <summary>生成"删一列"的 DDL。</summary>
    /// <param name="target">目标表。</param>
    /// <param name="columnName">列名。</param>
    /// <returns>DDL;方言不支持时为 <see langword="null" />。</returns>
    string? DropColumnDdl(SqlObject target, string columnName);

    /// <summary>生成"建索引"的 DDL。</summary>
    /// <param name="target">目标表。</param>
    /// <param name="indexName">索引名。</param>
    /// <param name="columns">列。</param>
    /// <param name="unique">是否唯一。</param>
    /// <returns>DDL;方言不支持时为 <see langword="null" />。</returns>
    string? CreateIndexDdl(SqlObject target, string indexName, IReadOnlyList<string> columns, bool unique);

    /// <summary>生成"删索引"的 DDL。</summary>
    /// <param name="target">目标表。</param>
    /// <param name="indexName">索引名。</param>
    /// <returns>DDL;方言不支持时为 <see langword="null" />。</returns>
    string? DropIndexDdl(SqlObject target, string indexName);
}
