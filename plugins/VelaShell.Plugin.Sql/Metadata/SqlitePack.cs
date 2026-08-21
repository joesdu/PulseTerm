using System.Data.Common;
using System.Globalization;

namespace VelaShell.Plugin.Sql.Metadata;

/// <summary>
/// SQLite 方言包。
/// <para>
/// <b>五个方言里最该自己动手的一个。</b>调研在真机上把 <c>DbMaintenance</c> 的 SQLite 实现测穿了(§3.4):
/// 23 条里只有 12 条能用,而且能用的那几条还会**返回语法合法、语义错误的值** ——
/// <c>GetIndexList</c> 把索引名报成 <c>"0"</c>、列长度恒 0、<c>AUTOINCREMENT</c> 一个都认不出。
/// 抛异常插件还能接住降级,返回假值则会被界面如实画出来。所以这里一条都不用它,全部直查 SQLite 自己的目录。
/// </para>
/// <para>
/// <b>数据源全是 <c>PRAGMA</c> 的表值函数形态</b>(<c>pragma_table_xinfo(?)</c> 而不是 <c>PRAGMA table_xinfo(x)</c>)。
/// 这不是风格选择:<c>PRAGMA</c> 语句**不接受绑定参数**,要查哪张表只能把表名拼进语句 ——
/// 那正是 §5.4.4 里实测能删表的那条路。表值函数形态可以走参数,于是"永不把用户标识符拼进 SQL"这条纪律
/// 在整个元数据层得以成立;真正需要拼的地方(<c>SELECT * FROM 表</c>)才走 <see cref="DialectPackBase.QuoteIdentifier" />。
/// </para>
/// <para>
/// SQLite 3.16 起所有"有返回值且至多一个参数"的 pragma 都有同名表值函数;本包在 3.50.4 上逐条实测通过。
/// </para>
/// </summary>
internal sealed class SqlitePack : DialectPackBase
{
    /// <summary>对象列表:表与视图一次查完(§7.2 的计数要求),内部对象按 SQLite 的保留前缀<b>标记</b>。</summary>
    /// <remarks>
    /// <c>sqlite_</c> 前缀是 SQLite 保留给自己的(<c>sqlite_sequence</c> / <c>sqlite_stat1</c> / 自动索引),
    /// 建表时用这个前缀会被引擎拒绝 —— 所以这条判据不会误伤用户对象,是精确的而不是启发式的。
    /// 虚表的影子表(fts5 的 <c>x_data</c> 之类)是真表,照常列出:用户看得见它们占的空间。
    /// <para>
    /// <b>从"WHERE 掉"改成"标成系统"</b>:被排掉的那批里有 <c>sqlite_sequence</c> ——
    /// 而"这张表的 AUTOINCREMENT 走到哪一号了"除了看它没有第二个办法。
    /// 现在它归进树上的"系统对象"分组:既不与用户表混排,也不再是一个查不到的东西。
    /// </para>
    /// </remarks>
    private const string RelationsSql = """
        SELECT type, name, (name LIKE 'sqlite\_%' ESCAPE '\')
        FROM sqlite_master
        WHERE type IN ('table', 'view')
        ORDER BY type, name
        """;

    /// <summary>
    /// 列:用 <c>table_xinfo</c> 而不是 <c>table_info</c> —— 只有前者多给一列 <c>hidden</c>,
    /// 而**生成列只能靠它认出来**(实测 <c>VIRTUAL</c> 是 2、<c>STORED</c> 是 3;1 是虚表的隐藏列)。
    /// <c>table_info</c> 会把生成列**整个漏掉**,回写时就会拿一份少了列的表结构去拼 UPDATE。
    /// </summary>
    private const string ColumnsSql = """
        SELECT cid, name, type, "notnull", dflt_value, pk, hidden
        FROM pragma_table_xinfo(@p0)
        ORDER BY cid
        """;

    /// <summary>
    /// 索引:一条 SQL 取齐"哪些索引 + 每个索引的有序列 + 建索引原文"。
    /// <para>
    /// 用 <c>index_xinfo</c> 而不是 <c>index_info</c>,是为了拿到 <c>key</c> 这一列:
    /// <c>index_xinfo</c> 会把索引末尾**自动附带的 rowid / 主键列**也列出来(<c>key = 0</c>),
    /// 那些不是用户声明的索引列,混进去会让"唯一索引的列集合"变大一列,
    /// 而结果网格正是拿这个列集合当行键的(<see cref="SqlTableSchema.TryGetRowKey" />)。
    /// </para>
    /// <para>
    /// <c>ORDER BY il.seq</c> 让索引按 SQLite 自己的顺序出场(最新建的在前),
    /// <c>ix.seqno</c> 保证复合索引的列**按声明顺序**;<c>sqlite_master.sql</c> 是自动索引时为 NULL。
    /// </para>
    /// </summary>
    private const string IndexesSql = """
        SELECT il.name, il."unique", il.origin, il.partial, ix.name, m.sql
        FROM pragma_index_list(@p0) AS il
        JOIN pragma_index_xinfo(il.name) AS ix
        LEFT JOIN sqlite_master AS m ON m.type = 'index' AND m.name = il.name
        WHERE ix."key" = 1
        ORDER BY il.seq, ix.seqno
        """;

    /// <summary>外键:按 <c>id</c> 归并成一条约束,<c>seq</c> 是复合外键内的列序。</summary>
    private const string ForeignKeysSql = """
        SELECT id, seq, "table", "from", "to", on_update, on_delete
        FROM pragma_foreign_key_list(@p0)
        ORDER BY id, seq
        """;

    /// <summary>父表主键(按主键内序号)。外键不写目标列时用它补齐。</summary>
    private const string ParentKeySql = """
        SELECT name
        FROM pragma_table_xinfo(@p0)
        WHERE pk > 0
        ORDER BY pk
        """;

    /// <inheritdoc />
    public override SqlDialect Dialect => SqlDialect.Sqlite;

    /// <inheritdoc />
    /// <remarks>
    /// SQLite 没有 schema 这一级。<c>ATTACH</c> 出来的 <c>db.表</c> 前缀看着像 schema,
    /// 但那是**另一个数据库文件**,归 <see cref="ListDatabasesAsync" /> 管,不该在对象树上多插一层。
    /// </remarks>
    public override bool HasSchemas => false;

    /// <inheritdoc />
    /// <remarks>一个文件就是一个库;连上之后没有"换个库"这回事,对象树直接从对象类别开始。</remarks>
    public override bool HasDatabases => false;

    /// <summary>定界符是双引号,转义是双引号加倍(基类统一处理)。</summary>
    /// <remarks>
    /// SQLite 还认 <c>[]</c> 与反引号,那是为了兼容 SQL Server 与 MySQL 的方言遗产。
    /// 这里只用标准的双引号:三选一的时候选那个**标准里写着的**,免得将来某个兼容开关把另外两个关掉。
    /// </remarks>
    protected override (char Open, char Close) Delimiters => ('"', '"');

    /// <summary>
    /// 会话 id:SQLite 没有,也不需要。
    /// <para>
    /// <b>这里返回 <see langword="null" /> 不是没写完。</b>SQLite 的取消根本不走 SQL:
    /// 它是进程内库,没有"另开一条连接去杀会话"这个模型,取消要直调
    /// <c>raw.sqlite3_interrupt(SqliteConnection.Handle)</c>(§3.10 实测 20 ms 打断了一条跑满 150 秒的递归 CTE)。
    /// 执行层看到这两个属性为 <see langword="null" /> 时应当走 interrupt 那条路,而不是判定"本方言不支持取消"。
    /// </para>
    /// </summary>
    public override string? SessionIdSql => null;

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SqlObject>> ListDatabasesAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        // HasDatabases 是 false,但这个方法照样有用:ATTACH 之后 database_list 会多出几行,
        // 而"我到底连着哪个文件"是排障时第一个要看的东西(file 列给的是绝对路径,内存库为空串)。
        List<SqlObject> databases = await QueryAsync(
            connection,
            "SELECT name, file FROM pragma_database_list ORDER BY seq",
            r => new SqlObject(SqlObjectKind.Database, Str(r, 0), Comment: Str(r, 1)),
            null,
            cancellationToken).ConfigureAwait(false);
        return databases;
    }

    /// <inheritdoc />
    /// <remarks>SQLite 没有 schema,恒空。见 <see cref="HasSchemas" /> 上的说明。</remarks>
    public override Task<IReadOnlyList<SqlObject>> ListSchemasAsync(
        DbConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SqlObject>>([]);

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="schema" /> 在本方言里无意义,刻意忽略而不是抛异常 ——
    /// 调用方按统一签名传空串进来,为此报错只会让上层多写一个方言分支。
    /// <para>
    /// <see cref="SqlObject.EstimatedRows" /> 一律留空:SQLite 没有便宜的行数估算(理由见
    /// <see cref="EstimateRowCountSql" />),而对象树上显示一个编出来的数字比不显示更坏(§7.2)。
    /// </para>
    /// </remarks>
    public override async Task<IReadOnlyList<SqlObject>> ListRelationsAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        List<SqlObject> relations = await QueryAsync(
            connection,
            RelationsSql,
            r => new SqlObject(
                string.Equals(Str(r, 0), "view", StringComparison.Ordinal) ? SqlObjectKind.View : SqlObjectKind.Table,
                Str(r, 1),
                // sqlite_sequence / sqlite_stat1 这些**照列**,只是归进"系统对象"分组 ——
                // 想知道 AUTOINCREMENT 走到哪一号了,除了看 sqlite_sequence 没有别的办法,
                // 而早先这条 SQL 是把它们整个 WHERE 掉的。
                IsSystem: Bool(r, 2)),
            null,
            cancellationToken).ConfigureAwait(false);
        return relations;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>视图走的是同一条路径</b>,不需要任何特判:<c>pragma_table_xinfo</c> 对视图照样给列
    /// (声明类型从底表传播过来),<c>index_list</c> / <c>foreign_key_list</c> 对视图返回 0 行且不报错。
    /// 这正是 <c>GetColumnInfosByTableName</c> 对视图"返回 0 列且不抛异常"的那个洞(§2.3)。
    /// </remarks>
    public override async Task<SqlTableSchema> DescribeAsync(
        DbConnection connection, SqlObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        object?[] parameters = [target.Name];

        List<ColumnRow> columnRows = await QueryAsync(
            connection,
            ColumnsSql,
            r => new ColumnRow(Int(r, 0), Str(r, 1), Str(r, 2), Bool(r, 3), StrOrNull(r, 4), Int(r, 5), Int(r, 6)),
            parameters,
            cancellationToken).ConfigureAwait(false);

        List<IndexRow> indexRows = await QueryAsync(
            connection,
            IndexesSql,
            r => new IndexRow(Str(r, 0), Bool(r, 1), Str(r, 2), Bool(r, 3), StrOrNull(r, 4), Str(r, 5)),
            parameters,
            cancellationToken).ConfigureAwait(false);

        List<ForeignKeyRow> foreignKeyRows = await QueryAsync(
            connection,
            ForeignKeysSql,
            r => new ForeignKeyRow(Int(r, 0), Str(r, 2), Str(r, 3), StrOrNull(r, 4), Str(r, 5), Str(r, 6)),
            parameters,
            cancellationToken).ConfigureAwait(false);

        bool rowIdAlias = IsRowIdAlias(columnRows, indexRows);
        List<SqlColumn> columns =
        [
            .. columnRows.Select(c => new SqlColumn(
                c.Name,
                c.Cid + 1,
                c.DeclaredType,
                !c.NotNull,
                c.KeyOrdinal > 0,
                rowIdAlias && c.KeyOrdinal == 1,
                c.Hidden is GeneratedVirtual or GeneratedStored,
                c.DefaultValue,
                IsExpressionDefault(c.DefaultValue)))
        ];

        List<SqlIndex> indexes = Fold(indexRows, r => r.Name, BuildIndex);
        IReadOnlyList<SqlForeignKey> foreignKeys =
            await BuildForeignKeysAsync(connection, target, foreignKeyRows, cancellationToken).ConfigureAwait(false);

        return new SqlTableSchema(target, columns, indexes, foreignKeys);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>LIMIT/OFFSET</c> 直接追加在用户 SQL 后面,**不套派生表**。这一条是 §7.3 的直接后果:
    /// SqlSugar 的 <c>ToPageList</c> 把原 SQL 塞进派生表,于是用户 SQL 带 <c>ORDER BY</c> 就在
    /// SQL Server 上直接失败;SQLite 上套派生表虽然能跑,但会让列名与重复列的行为跟着变,
    /// 而结果网格拿的就是那些列名。追加式在 SQLite 上对复合查询(<c>UNION</c> 系)同样成立 ——
    /// 语法上 <c>LIMIT</c> 本来就是整条 SELECT 的尾巴。
    /// <para>
    /// 末尾分号要先剥掉:编辑器切出来的语句常常还带着终止符,而 <c>...; LIMIT 100</c> 是语法错误。
    /// 调用方负责不要对**已经带 <c>LIMIT</c>** 的语句再调一次(那会是两个 LIMIT,SQLite 直接报错)。
    /// </para>
    /// </remarks>
    public override string ApplyPaging(string innerSql, int offset, int limit)
    {
        ArgumentNullException.ThrowIfNull(innerSql);
        string body = StripTerminators(innerSql);
        string take = Math.Max(limit, 0).ToString(CultureInfo.InvariantCulture);
        string skip = Math.Max(offset, 0).ToString(CultureInfo.InvariantCulture);
        return $"{body}\nLIMIT {take} OFFSET {skip}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>SQLite 没有便宜的行数估算,所以这里如实返回 <see langword="null" />。</b>
    /// 它不维护任何在线行数统计:<c>sqlite_stat1</c> 只有跑过 <c>ANALYZE</c> 才存在,存的是
    /// 采样出来的索引选择度文本而不是当前行数,过时了也不会有人更新它;<c>count(*)</c> 则是一次
    /// 完整的 b-tree 扫描。给一个陈旧的采样值当"约 N 行",比底栏空着更坏 —— 用户会按它做决策。
    /// 底栏据此显示"未知",点了才做精确 <c>count(*)</c>(§7.3)。
    /// </remarks>
    public override string? EstimateRowCountSql(SqlObject target) => null;

    /// <inheritdoc />
    /// <remarks>取消不走 SQL,见 <see cref="SessionIdSql" />。</remarks>
    public override string? CancelSessionSql(string sessionId) => null;

    /// <inheritdoc />
    /// <remarks>
    /// SQLite 把**建表原文**逐字存在 <c>sqlite_master.sql</c> 里 —— 别的方言要靠
    /// <c>SHOW CREATE TABLE</c> 或一堆目录表拼回去,这里是白得的,而且连注释与排版都在。
    /// <para>
    /// 对象名在这里是**值**不是标识符,所以走单引号字面量(单引号加倍)而不是
    /// <see cref="DialectPackBase.QuoteIdentifier" />。接口没有给参数通道,只能自己转义;
    /// 单引号加倍是 SQLite 字符串字面量的完整转义规则,没有反斜杠这类第二逃逸路径。
    /// </para>
    /// <para><c>sql IS NOT NULL</c> 是为了滤掉自动索引:它们在目录里有行,但没有建表原文。</para>
    /// </remarks>
    public override string? ShowCreateSql(SqlObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return $"SELECT sql FROM sqlite_master WHERE name = {QuoteTextLiteral(target.Name)} AND sql IS NOT NULL";
    }

    // ─────────────────────────── 运维面(M4) ───────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <c>EXPLAIN QUERY PLAN</c> —— 注意是这一条,不是 <c>EXPLAIN</c>:后者吐的是 VDBE 字节码
    /// (几十行 <c>OpenRead</c> / <c>Column</c> / <c>Next</c>),那是调 SQLite 自己用的,
    /// 不是给用户看"走没走索引"的。
    /// <para>
    /// <b><paramref name="analyze" /> 两档返回同一条,而且这不是没做完。</b>
    /// 契约里那个开关之所以危险,是因为别的方言的 analyze 档(<c>EXPLAIN ANALYZE</c> /
    /// <c>SET STATISTICS PROFILE</c>)会**真的把语句跑一遍** —— 对 <c>DELETE</c> 就是真删。
    /// SQLite 这边根本没有那种档位:<c>EXPLAIN QUERY PLAN</c> 只做**准备**(prepare),
    /// 拿到的是优化器选出来的计划,底下那段程序一步都不走。
    /// 于是"危险版本"在本方言里不存在,两档给同一条是**如实**,而不是把 analyze 悄悄降级 ——
    /// 代价是拿不到真实行数与耗时(计划里的行数是估算),这一点要在界面上说清。
    /// </para>
    /// <para>
    /// 末尾分号先剥掉,理由与 <see cref="ApplyPaging" /> 同一条:编辑器切出来的语句常带终止符。
    /// </para>
    /// </remarks>
    public override string? ExplainSql(string innerSql, bool analyze)
    {
        ArgumentNullException.ThrowIfNull(innerSql);
        // analyze 被刻意忽略:见上。签名照契约保留,别在这里按方言少一个参数。
        return $"EXPLAIN QUERY PLAN {StripTerminators(innerSql)}";
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>返回 <see langword="null" />:SQLite 里没有"会话"这个东西。</b>
    /// 它是**进程内的库**而不是服务端 —— 打开一个 <c>.db</c> 文件不产生任何服务端状态,
    /// 也就没有 <c>SHOW PROCESSLIST</c> / <c>pg_stat_activity</c> / <c>sys.dm_exec_sessions</c> 的对应物:
    /// 谁"连着"这个文件只有操作系统知道,而那是文件句柄,不是数据库概念。
    /// <para>
    /// 所以这里的 <see langword="null" /> 与"这条 SQL 还没写"要分得开:界面据此显示
    /// <b>"该数据库不提供会话列表"</b>,而不是空表格、更不是永远转圈(§7.8 —— 方言不支持某项时
    /// 必须说出来,空白会被读成"查出来就是一个会话都没有")。
    /// </para>
    /// <para>
    /// 顺带一句,别拿 <c>pragma_database_list</c> 来凑数:它列的是本连接 <c>ATTACH</c> 了哪些文件,
    /// 跟"谁在跑什么"没有一点关系 —— 那正是"返回一个语法合法、语义错误的值"的老毛病(§3.4)。
    /// </para>
    /// </remarks>
    public override string? SessionListSql => null;

    /// <inheritdoc />
    /// <remarks>
    /// <b>返回 <see langword="null" />:SQLite 有锁,但没有"锁表"可查。</b>
    /// 它的锁是**整库粒度的文件锁**(SHARED / RESERVED / PENDING / EXCLUSIVE,WAL 下还多一层),
    /// 由 <c>flock</c> / <c>LockFileEx</c> 落在文件系统上,不进任何目录表;
    /// 而且既然只有整库一把锁,"谁锁了我"在这里也没有阻塞链可言 ——
    /// 拿不到锁就是 <c>SQLITE_BUSY</c>,对面是哪个进程 SQLite 自己都不知道。
    /// <para>
    /// <c>PRAGMA lock_status</c> 看着像那么回事,但它只在 <c>SQLITE_DEBUG</c> 编译的库里存在
    /// (发行版的原生库里没有,查了就是查不到这个 pragma),而且给的是**本连接自己**的锁级别,
    /// 不是"谁持有"。所以它凑不出契约要的那五列,一列都凑不出。
    /// </para>
    /// <para>同 <see cref="SessionListSql" />:这是"本方言不存在这个概念",界面要如实说出来。</para>
    /// </remarks>
    public override string? LockListSql => null;

    /// <inheritdoc />
    /// <remarks>
    /// <b>SQLite 只有五种类型亲和性,但用户写的是别的东西 —— 这一栏给的是后者。</b>
    /// 引擎把声明类型的**文本**按五条子串规则折成亲和性(含 <c>INT</c> → INTEGER;
    /// 含 <c>CHAR</c> / <c>CLOB</c> / <c>TEXT</c> → TEXT;含 <c>BLOB</c> 或整个为空 → BLOB;
    /// 含 <c>REAL</c> / <c>FLOA</c> / <c>DOUB</c> → REAL;其余 → NUMERIC),
    /// 声明成 <c>VARCHAR(50)</c> 与声明成 <c>TEXT</c> 在存储上没有区别。
    /// 所以下面这份表是**"常用的声明写法"而不是"支持的类型清单"** ——
    /// 五个亲和性名排在最前(它们同时也是合法的声明写法),后面是用户实际会敲的那些。
    /// <para>
    /// 三条要一并显示给用户的陷阱:
    /// ① <b>长度与精度只是装饰</b> —— <c>VARCHAR(50)</c> 照样存得下一万个字,
    ///    <c>DECIMAL(10,2)</c> 也不做四舍五入(它落 NUMERIC 亲和性,存的仍是整数或 IEEE 双精度);
    /// ② <b>没有布尔与日期时间类型</b> —— <c>BOOLEAN</c> 存 0/1,
    ///    <c>DATE</c> / <c>DATETIME</c> / <c>TIMESTAMP</c> 落 NUMERIC 亲和性,
    ///    到底存成文本还是数字由写入方决定;
    /// ③ <b>故意不给 <c>JSON</c></b> —— SQLite 没有 JSON 类型,声明成 <c>JSON</c> 会落到 NUMERIC 亲和性,
    ///    于是形如 <c>123</c> 的 JSON 文档会被**静默转成整数**。要存 JSON 就声明 <c>TEXT</c>,
    ///    <c>json_*()</c> 函数照常能用。把它列进下拉等于把一个静默丢数据的选项摆给用户。
    /// </para>
    /// <para>
    /// 与 <c>GetDbTypes()</c> 的差别正是契约点名的那条:它返回的是"这个库当前用到了哪些类型",
    /// 随建表变多(§2.3)。这里是**静态表**,与库里有什么、有没有表都无关。
    /// </para>
    /// </remarks>
    public override IReadOnlyList<string> CommonTypes => TypeNames;

    // ─────────────────────────── 表设计器(M4) ───────────────────────────
    //
    // CreateIndexDdl / DropIndexDdl **不覆盖**,基类的通行写法在 SQLite 上逐字成立:
    //   CREATE [UNIQUE] INDEX "ix" ON "t" ("a", "b")   /   DROP INDEX "ix"
    // 两条注记(它们不改文本,所以留在这儿,而不是弄两个空覆盖):
    //   ① SQLite 的**索引名与表同一个命名空间**,是库级唯一而不是表级 —— 两张表上都叫 ix_name 会直接冲突;
    //      所以 DROP INDEX 不带表名是对的,而调用方取索引名时最好自己带上表名前缀;
    //   ② HasSchemas 为 false,于是 QuoteQualified 只吐一段名字,ON 后面拿到的正是 SQLite 要的裸表名
    //      (它的限定形态是 CREATE INDEX 库名.索引名 ON 表名 —— 限定的是**索引**不是表,通用写法碰不到这块)。

    /// <inheritdoc />
    /// <remarks>
    /// 文本与基类的通行写法**逐字相同**(<c>ALTER TABLE ... ADD COLUMN</c> 就是 SQLite 的写法),
    /// 覆盖只为一条规矩:**列定义里说了、而这条 DDL 表达不了的,一律不生成。**
    /// <para>
    /// 通用写法只写得出"列名 + 类型 + <c>NOT NULL</c> + <c>DEFAULT</c>"四样;
    /// 列模型上另外三面旗 —— <see cref="SqlColumn.IsGenerated" />、
    /// <see cref="SqlColumn.IsPrimaryKey" />、<see cref="SqlColumn.IsAutoIncrement" /> ——
    /// 它**一声不吭地丢掉**。丢掉的后果不是报错,是 SQLite 照办出一个**普通列**:
    /// 用户点了"加一个生成列 / 主键列 / 自增列",拿到一个什么都不是的列,而且哪儿都不提示。
    /// 静默办成别的事比报错坏得多,所以这三种一律返回 <see langword="null" />,
    /// 让界面显示"该数据库不支持这样加列"(§7.8)。
    /// </para>
    /// <para>
    /// 三面旗里,后两面即使表达得出来 SQLite 也照样拒绝(实测:<c>Cannot add a PRIMARY KEY column</c> /
    /// <c>Cannot add a UNIQUE column</c>,空表有行都拒)—— 主键与自增要重建整张表。
    /// 只有生成列是"SQLite 允许(<c>VIRTUAL</c>)而模型表达不了",
    /// 等表设计器把生成表达式建模之后就能放行。
    /// </para>
    /// <para>
    /// <b>其余几条 SQLite 的 <c>ADD COLUMN</c> 限制一律不在这里拦,因为拦了就是错的</b> ——
    /// 真机(3.50.4)量出来的形态是:<b>它们只在表里已经有行时才生效</b>,空表上全部放行。
    /// 而本方法只有一份表结构、没有连接,**它不知道表里有没有行**;拿不准就拦,等于让表设计器
    /// 在空表上拒绝一件合法的事,那比让引擎报一条清楚的错更坏。逐条实测:
    /// <list type="table">
    ///   <listheader><term>形态</term><description>空表 / 有行</description></listheader>
    ///   <item>
    ///     <term><c>NOT NULL</c> 且无默认值</term>
    ///     <description>放行 / <c>Cannot add a NOT NULL column with default value NULL</c></description>
    ///   </item>
    ///   <item>
    ///     <term>默认值非常量(<c>CURRENT_TIMESTAMP</c>、<c>(1 + 1)</c>)</term>
    ///     <description>放行 / <c>Cannot add a column with non-constant default</c></description>
    ///   </item>
    ///   <item><term><c>STORED</c> 生成列</term><description>放行 / <c>cannot add a STORED column</c></description></item>
    ///   <item><term><c>VIRTUAL</c> 生成列</term><description>放行 / 放行</description></item>
    ///   <item>
    ///     <term><c>UNIQUE</c> / <c>PRIMARY KEY</c> 列</term>
    ///     <description>两种表都拒(<c>Cannot add a UNIQUE column</c> / <c>... a PRIMARY KEY column</c>)</description>
    ///   </item>
    /// </list>
    /// 执行失败时按上表把错误翻成人话(§7.8),别原样丢给用户 ——
    /// "空表能加、加过几行之后同一件事就不行了"是最容易让人以为撞了鬼的一类。
    /// </para>
    /// <para>
    /// <b>默认值是原样拼进去的,所以调用方给的文本必须自己成立。</b>一个实测到的坑:
    /// <c>DEFAULT CURRENT_TIMESTAMP</c> 与 <c>DEFAULT (1 + 1)</c> 语法都对,
    /// 但 <c>DEFAULT datetime('now')</c> 是**语法错误**(<c>near "(": syntax error</c>)——
    /// 函数调用当默认值必须自己带一层括号:<c>DEFAULT (datetime('now'))</c>。
    /// 这跟 <see cref="DescribeAsync" /> 读回来的形态正好对得上:SQLite 存的是剥掉外层括号的原文,
    /// 所以**把读回来的默认值原样拿去加列是不成立的**,函数调用那种要补回括号。
    /// </para>
    /// </remarks>
    public override string? AddColumnDdl(SqlObject target, SqlColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        // 通用写法表达不了这三样,而它丢掉它们时不报错 —— 见上。
        return column.IsGenerated || column.IsPrimaryKey || column.IsAutoIncrement
            ? null
            : base.AddColumnDdl(target, column);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 文本与通用写法一致,覆盖是为了把两条**只有 SQLite 才有的前提**写在离生成处最近的地方 ——
    /// 它们都不改 DDL,但都会让这条 DDL 在真机上失败,而失败时的报错**指不到真正的原因**。
    /// <para>
    /// ① <b>版本:<c>ALTER TABLE ... DROP COLUMN</c> 是 SQLite 3.35.0(2021-03)才有的。</b>
    ///    更早的引擎报的是 <c>near "DROP": syntax error</c> —— 一句语法错,
    ///    排障的人第一反应会去怀疑列名和引号,不会想到是引擎太老。所以这条错误要由插件补一句版本要求,
    ///    不能把原文直接丢给用户。
    ///    (本插件捆绑的原生库是 <c>SQLitePCLRaw.bundle_e_sqlite3</c> 3.0.3 那一支,远在这条线之上;
    ///    但 SQLite 是**进程内**库,谁装载谁的引擎说了算,所以这个前提写在代码里比写在文档里可靠。)
    /// ② <b>SQLite 会拒绝删"还被别的东西用着"的列</b>:主键成员、带 <c>UNIQUE</c> 的列、
    ///    <b>被任何索引引用的列</b>(包括只出现在部分索引 <c>WHERE</c> 里的)、
    ///    被 <c>CHECK</c> / 外键 / 生成列表达式引用的列、被视图或触发器引用的列。
    ///    最常撞上的是索引那一条 —— 表设计器要先 <see cref="DialectPackBase.DropIndexDdl" /> 再删列。
    ///    <b>顺序反了报的错还特别容易误导</b>(3.50.4 实测原文):
    ///    <c>error in index ix_x after drop column: no such column: a</c> ——
    ///    它长得像"索引坏了",实际是"这一列还被那条索引用着"。
    ///    主键那条则直白得多:<c>cannot drop PRIMARY KEY column: "id"</c>。
    ///    这两条都要按 §7.8 翻成人话再给用户。
    /// </para>
    /// </remarks>
    public override string? DropColumnDdl(SqlObject target, string columnName) =>
        base.DropColumnDdl(target, columnName);

    /// <summary>
    /// 常用声明类型(静态表,取舍见 <see cref="CommonTypes" />)。
    /// 提成静态字段是因为类型下拉每次打开都要读它,没必要每次新建一个数组。
    /// </summary>
    private static readonly string[] TypeNames =
    [
        // 五种亲和性本身 —— 直接写它们是最没有歧义的声明方式。
        "INTEGER", "REAL", "TEXT", "BLOB", "NUMERIC",
        // 整数族(全部落 INTEGER 亲和性;BOOLEAN 存 0/1)。
        "INT", "BIGINT", "SMALLINT", "TINYINT", "BOOLEAN",
        // 浮点与"定点"(DECIMAL 的精度不被强制,见上)。
        "DOUBLE", "FLOAT", "DECIMAL(10,2)",
        // 文本(括号里的长度同样不被强制)。
        "VARCHAR(255)", "CHAR(36)", "CLOB",
        // 日期时间:SQLite 没有这三种类型,只是大家都这么声明。
        "DATE", "DATETIME", "TIMESTAMP"
    ];

    /// <summary>
    /// 剥掉末尾的语句终止符(可能有多个,后面也可能还跟着空白)。
    /// <para>
    /// <see cref="ApplyPaging" /> 与 <see cref="ExplainSql" /> 都要它:前者要往后接 <c>LIMIT</c>
    /// (<c>...; LIMIT 100</c> 是语法错误),后者要往前接 <c>EXPLAIN QUERY PLAN</c>。
    /// 两处共用一份,免得将来只改一处。
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

    /// <summary><c>hidden = 2</c>:<c>GENERATED ALWAYS AS (...) VIRTUAL</c>。</summary>
    private const int GeneratedVirtual = 2;

    /// <summary><c>hidden = 3</c>:<c>GENERATED ALWAYS AS (...) STORED</c>。</summary>
    private const int GeneratedStored = 3;

    /// <summary><c>PRAGMA index_list</c> 的 <c>origin</c>:这条索引就是主键。</summary>
    private const string OriginPrimaryKey = "pk";

    /// <summary><c>PRAGMA index_list</c> 的 <c>origin</c>:来自表定义里的 <c>UNIQUE</c> 约束。</summary>
    private const string OriginUnique = "u";

    /// <summary>
    /// 判断这张表的主键是不是 <b>rowid 别名</b> —— 也就是"插入时不给值,引擎会替你填"的那种列。
    /// <para>
    /// <b>判据是 pragma 而不是 DDL 文本,这一条是实测选出来的。</b>SQLite 只把
    /// <i>rowid 表上、单列、声明类型恰好是 <c>INTEGER</c> 的主键</i>当成 rowid 的别名;
    /// 而这三个条件在目录里有一个等价且不用解析 SQL 的信号:<b>这种表的主键不会有索引</b>。
    /// 实测四种反例全部对得上 ——
    /// <c>INT PRIMARY KEY</c>(不是 <c>INTEGER</c>)、<c>WITHOUT ROWID</c>、复合主键、非整型主键,
    /// 四者的 <c>index_list</c> 里都有一条 <c>origin = 'pk'</c> 的自动索引;
    /// 而 <c>INTEGER PRIMARY KEY</c> 的 <c>index_list</c> 是空的。
    /// </para>
    /// <para>
    /// 另外两条看着更直白的判据都不成立,别再换回去:
    /// ① <b><c>sqlite_sequence</c> 里有没有记录</b> —— 实测建完 <c>AUTOINCREMENT</c> 表**还没插过行**时
    ///    那张表里一行都没有,而且整个库没有 <c>AUTOINCREMENT</c> 表时 <c>sqlite_sequence</c> **根本不存在**
    ///    (直查报 <c>no such table</c>);拿它当判据等于"空表的自增列认不出来"。
    /// ② <b>在 <c>sqlite_master.sql</c> 里搜 <c>AUTOINCREMENT</c></b> —— 要正确就得先剥字符串字面量、
    ///    注释和引号标识符,还得同时判 <c>WITHOUT ROWID</c> 与 <c>INT</c>/<c>INTEGER</c> 之差。
    /// </para>
    /// <para>
    /// <b>关于严格意义的 <c>AUTOINCREMENT</c>:</b>它是本判据的**真子集** ——
    /// 带关键字的多一条"绝不重用已删除的 id"的保证(代价是多一次 <c>sqlite_sequence</c> 写),
    /// 不带关键字的 <c>INTEGER PRIMARY KEY</c> 同样会自动填值。
    /// <see cref="SqlColumn.IsAutoIncrement" /> 服务的是"回写时这一列能不能省着不给",
    /// 两者在这件事上行为一致,所以这里取宽的那个;两者之差不影响任何一处回写决策,
    /// 也就没有为它在模型上加一个字段。
    /// </para>
    /// </summary>
    /// <param name="columns">列行。</param>
    /// <param name="indexes">索引行。</param>
    /// <returns>是不是 rowid 别名主键。</returns>
    private static bool IsRowIdAlias(List<ColumnRow> columns, List<IndexRow> indexes) =>
        columns.Count(c => c.KeyOrdinal > 0) == 1
        && !indexes.Exists(i => string.Equals(i.Origin, OriginPrimaryKey, StringComparison.Ordinal));

    /// <summary>把"每列一行"的索引行归并成一条索引。</summary>
    /// <param name="name">索引名。</param>
    /// <param name="rows">该索引的列行(已按 <c>seqno</c> 排好)。</param>
    /// <returns>索引。</returns>
    private static SqlIndex BuildIndex(string name, IReadOnlyList<IndexRow> rows)
    {
        IndexRow head = rows[0];
        // 表达式索引(CREATE INDEX ix ON t(lower(a)))在 index_xinfo 里的列名是 NULL —— 没有列名可给。
        // 这时**整条索引的列集合留空**,而不是编一个占位名:结果网格会拿唯一索引当行键
        // (SqlTableSchema.TryGetRowKey),占位名进去就是一条打错行的 UPDATE。留空则该索引被跳过,
        // 而 Definition 里的建索引原文照样把真相显示给用户。
        bool hasExpression = rows.Any(r => r.ColumnName is null);
        IReadOnlyList<string> columns = hasExpression ? [] : [.. rows.Select(r => r.ColumnName!)];
        bool isPrimaryKey = string.Equals(head.Origin, OriginPrimaryKey, StringComparison.Ordinal);
        return new SqlIndex(name, columns, head.IsUnique, isPrimaryKey, DescribeKind(head.Origin, head.IsPartial), head.Definition);
    }

    /// <summary>
    /// 索引类型。SQLite 只有一种索引结构(b-tree),所以这里给的是**它从哪儿来** ——
    /// 自动建的主键索引、<c>UNIQUE</c> 约束带出来的索引、手工建的索引,三者在界面上要分得开:
    /// 前两种删不掉(得改表定义),第三种能 <c>DROP INDEX</c>。部分索引额外标出来,
    /// 因为"这条索引只覆盖一部分行"会直接影响用户对查询计划的判断。
    /// </summary>
    /// <param name="origin"><c>index_list</c> 的 <c>origin</c>。</param>
    /// <param name="partial">是不是部分索引。</param>
    /// <returns>类型描述。</returns>
    private static string DescribeKind(string origin, bool partial)
    {
        string kind = origin switch
        {
            OriginPrimaryKey => "PRIMARY KEY",
            OriginUnique => "UNIQUE CONSTRAINT",
            _ => "BTREE"
        };
        return partial ? $"{kind} PARTIAL" : kind;
    }

    /// <summary>
    /// 把外键行归并成约束,并在需要时去父表把目标列补齐。
    /// <para>
    /// <c>foreign_key_list</c> 的 <c>to</c> 在**没写目标列**时是 NULL(<c>REFERENCES 父表</c> 这种写法),
    /// 语义是"父表的主键"。契约要求外键必须带目标列,所以这里按父表名去查一次主键补上 ——
    /// 每个不同的父表只查一次。父表连主键都没有时补出来是空表(SQLite 运行期也会拒绝这种外键),
    /// 空表比编一个列名诚实。
    /// </para>
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="target">本表。</param>
    /// <param name="rows">外键行(已按 <c>id</c>、<c>seq</c> 排好)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>外键列表。</returns>
    private static async Task<IReadOnlyList<SqlForeignKey>> BuildForeignKeysAsync(
        DbConnection connection,
        SqlObject target,
        List<ForeignKeyRow> rows,
        CancellationToken cancellationToken)
    {
        Dictionary<string, IReadOnlyList<string>> parentKeys = new(StringComparer.Ordinal);
        foreach (string parent in rows.Where(r => r.ToColumn is null).Select(r => r.ParentTable).Distinct(StringComparer.Ordinal))
        {
            List<string> keys = await QueryAsync(
                connection, ParentKeySql, r => Str(r, 0), [parent], cancellationToken).ConfigureAwait(false);
            parentKeys[parent] = keys;
        }

        return Fold(rows, r => r.Id, (id, group) =>
        {
            ForeignKeyRow head = group[0];
            IReadOnlyList<string> referenced = [];
            if (group.All(r => r.ToColumn is not null))
            {
                referenced = [.. group.Select(r => r.ToColumn!)];
            }
            else if (parentKeys.TryGetValue(head.ParentTable, out IReadOnlyList<string>? inherited))
            {
                referenced = inherited;
            }
            // SQLite **不通过任何 pragma 暴露外键约束名**,哪怕建表时写了 CONSTRAINT fk_xxx ——
            // 名字只活在 sqlite_master.sql 的文本里。这里给的是"表名 + 该表内第几条外键"的合成 id:
            // 它在表结构没变时是稳定的,可以用来定位与比对,但它**不是能拿去 DROP CONSTRAINT 的名字**
            // (SQLite 也没有那条语句 —— 改外键得重建整张表)。
            string name = $"FK_{target.Name}_{id.ToString(CultureInfo.InvariantCulture)}";
            return new SqlForeignKey(
                name,
                [.. group.Select(r => r.FromColumn)],
                "",
                head.ParentTable,
                referenced,
                head.OnDelete,
                head.OnUpdate);
        });
    }

    /// <summary>
    /// 默认值是表达式还是字面量。
    /// <para>
    /// 这一条正是模型里 <c>CURRENT_TIMESTAMP</c> 与字符串 <c>'CURRENT_TIMESTAMP'</c> 要分得开的地方:
    /// SQLite 在 <c>dflt_value</c> 里给的是**声明时的原文**,两者的差别只有那对单引号。
    /// 实测原文形态:<c>'anon'</c> / <c>'it''s'</c> / <c>CURRENT_TIMESTAMP</c> / <c>-1</c> / <c>1.5</c> /
    /// <c>X'AB'</c> / <c>TRUE</c> / <c>NULL</c> / <c>upper('x')</c> ——
    /// 注意 <c>DEFAULT (datetime('now'))</c> 的外层括号被 SQLite 剥掉了,存的是 <c>datetime('now')</c>,
    /// 所以不能靠"有没有括号"来判。
    /// </para>
    /// <para>
    /// 判法是反过来的:**能认出是字面量的才算字面量,其余一律当表达式**。
    /// 宁可把字面量误判成表达式(界面多显示一个"表达式"标记),也不要把表达式误判成字面量 ——
    /// 后者会让"按默认值填入"的路径把一段 SQL 当成字符串写进去。
    /// </para>
    /// </summary>
    /// <param name="raw">原文;无默认值时为 <see langword="null" />。</param>
    /// <returns>是不是表达式。</returns>
    private static bool IsExpressionDefault(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        string text = raw.Trim();
        // 引号字面量。这里必须**扫到字符串真正的结尾**再看是不是最后一个字符,
        // 不能只比首尾两个字符:'a' || 'b' 的首尾也都是单引号,但它是表达式。
        if (IsClosedQuoted(text, '\'') || IsClosedQuoted(text, '"'))
        {
            return false;
        }
        // X'AB' 是 blob 字面量。
        if (text.Length >= 3 && text[0] is 'x' or 'X' && IsClosedQuoted(text[1..], '\''))
        {
            return false;
        }
        // 数字(含符号、小数、科学计数)与 0x 十六进制。
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }
        if (text.Length > 2 && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && text[2..].All(char.IsAsciiHexDigit))
        {
            return false;
        }
        // 关键字字面量。SQLite 把 TRUE/FALSE 直接折成 1/0,和写 1/0 没有区别。
        return !text.Equals("NULL", StringComparison.OrdinalIgnoreCase)
               && !text.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
               && !text.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>整段文本是不是一个完整闭合的引号字面量(内部的引号按加倍转义)。</summary>
    /// <param name="text">文本。</param>
    /// <param name="quote">引号字符。</param>
    /// <returns>是不是。</returns>
    private static bool IsClosedQuoted(string text, char quote)
    {
        if (text.Length < 2 || text[0] != quote)
        {
            return false;
        }
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] != quote)
            {
                continue;
            }
            if (i + 1 < text.Length && text[i + 1] == quote)
            {
                i++;
                continue;
            }
            return i == text.Length - 1;
        }
        return false;
    }

    /// <summary>把一个值转成 SQL 字符串字面量(单引号加倍)。</summary>
    /// <param name="value">值。</param>
    /// <returns>可拼进 SQL 的字面量。</returns>
    private static string QuoteTextLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary><c>pragma_table_xinfo</c> 的一行。</summary>
    /// <param name="Cid">列序号(0 起)。</param>
    /// <param name="Name">列名。</param>
    /// <param name="DeclaredType">声明类型原文(<c>VARCHAR(50)</c> / <c>NUMERIC(12,3)</c>);无类型列为空串。</param>
    /// <param name="NotNull">是否 <c>NOT NULL</c>。</param>
    /// <param name="DefaultValue">默认值原文。</param>
    /// <param name="KeyOrdinal">
    /// <b>主键内的序号(1 起),不是列序号</b> —— 0 表示不是主键成员。
    /// 复合主键的声明顺序就藏在这个值里,而不是列的先后。
    /// </param>
    /// <param name="Hidden">0 普通 / 1 虚表隐藏列 / 2 VIRTUAL 生成列 / 3 STORED 生成列。</param>
    private sealed record ColumnRow(
        int Cid,
        string Name,
        string DeclaredType,
        bool NotNull,
        string? DefaultValue,
        int KeyOrdinal,
        int Hidden);

    /// <summary><c>index_list</c> × <c>index_xinfo</c> 的一行(一个索引的一列)。</summary>
    /// <param name="Name">索引名。</param>
    /// <param name="IsUnique">是否唯一。</param>
    /// <param name="Origin"><c>pk</c> / <c>u</c> / <c>c</c>。</param>
    /// <param name="IsPartial">是否部分索引。</param>
    /// <param name="ColumnName">列名;表达式索引列为 <see langword="null" />。</param>
    /// <param name="Definition">建索引原文;自动索引为空串。</param>
    private sealed record IndexRow(
        string Name,
        bool IsUnique,
        string Origin,
        bool IsPartial,
        string? ColumnName,
        string Definition);

    /// <summary><c>foreign_key_list</c> 的一行。</summary>
    /// <param name="Id">同一张表内的第几条外键(复合外键的多行共用一个 id)。</param>
    /// <param name="ParentTable">父表。</param>
    /// <param name="FromColumn">本表列。</param>
    /// <param name="ToColumn">父表列;没写目标列时为 <see langword="null" />。</param>
    /// <param name="OnUpdate">更新时动作。</param>
    /// <param name="OnDelete">删除时动作。</param>
    private sealed record ForeignKeyRow(
        int Id,
        string ParentTable,
        string FromColumn,
        string? ToColumn,
        string OnUpdate,
        string OnDelete);
}
