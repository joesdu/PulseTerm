using System.Reflection;
using VelaShell.Plugin.Sql;
using VelaShell.Plugin.Sql.Metadata;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// <see cref="OraclePack" /> 的**离线**用例。
/// <para>
/// <b>这一组里没有一条连真 Oracle 的用例,这是刻意的,不是漏了</b> ——
/// 真机那一半在 <see cref="OracleRealMachineTests" /> 里,两边分工不同,见下。
/// </para>
/// <para>
/// <b>【历史】</b>这份文件写成时,整份 Oracle 方言包还是照官方文档写的离线推断
/// (当时拉 Oracle 官方镜像实测 5 MB/min、剩余 2 GB 要 6.7 小时,放弃了)。
/// <b>那个前提在 2026-08-20 已经不成立</b>:换 <c>gvenzl/oracle-free:23-slim</c> 镜像后
/// 接上了真机(Oracle AI Database 26ai Free 23.26.2.0.0),包里的推断被逐条验过,
/// 并且**当场逮到一个真 bug**:<c>DATA_DEFAULT</c>(<c>LONG</c> 列)读回来是空串,
/// 因为 ODP.NET 的 <c>InitialLONGFetchSize</c> 默认是 0 —— 源码里那条
/// 【未验证】【TODO】注释预言对了,现在它是已修复的实测结论。
/// </para>
/// <para>
/// 在这种前提下,<b>"假装连上了 Oracle"的测试是负资产</b>:它要么去 mock 一个
/// <c>DbDataReader</c> 喂自己编的数据字典行,那验的是"我编的数据能被我自己的映射代码读回来",
/// 与真实的 <c>ALL_*</c> 视图长什么样毫无关系;要么在 CI 上挂一个连不上的连接串,
/// 那验的是异常路径。两种都会给出"Oracle 包有测试"的假信号,而这份包<b>恰恰最需要别人知道它没被验过</b>。
/// </para>
/// <para>
/// 所以这里只验<b>离线就能判对错</b>的那一半 —— 它们不依赖任何服务端行为:
/// ① 标识符转义(含标识符里带定界符的注入载荷);
/// ② <see cref="OraclePack.ApplyPaging" /> 生成的 SQL 文本;
/// ③ <see cref="DialectPackBase.QuoteQualified" /> 的两段转义;
/// ④ 无参数通道的那几条语句里字面量的转义;
/// ⑤ 结构性断言:接口每个成员都真的实现了、纯函数成员都不抛。
/// ⑥ SQL 常量的纪律:绑定变量前缀、数据源前缀、大小写不做规范化。
/// </para>
/// <para>
/// <b>本文件验不了的那一半已经在 <see cref="OracleRealMachineTests" /> 里补上了</b>:
/// 数据字典视图的列名与取值域、<c>LONG</c> 类型的 <c>DATA_DEFAULT</c> 读不读得回来、
/// 视图的列、identity 列、分页窗口、会话与锁视图、两段式执行计划、四条 DDL 生成器。
/// <b>仍然没验的</b>:<c>CANCEL SQL</c> 在目标版本上认不认(它要 <c>ALTER SYSTEM</c> 且要真有一条跑飞的查询)、
/// 降序索引的列名形态。源码里剩下的 <c>【未验证】</c> 就是清单。
/// </para>
/// </summary>
[TestClass]
public sealed class OraclePackTests
{
    /// <summary>被测对象。方言包是无状态的,每个用例新建一个也无所谓。</summary>
    private static OraclePack NewPack() => new();

    /// <summary>
    /// 方言身份与对象树层级形态。
    /// <para>
    /// <c>HasDatabases = false</c> 是一个建模决定(Oracle 一条连接只属于一个库,换库等于重连;
    /// 切 PDB 是 CDB 管理员的动作),"库"那一级由 schema 顶替 —— 详见
    /// <see cref="OraclePack.HasDatabases" /> 上的说明。写成用例是因为把它翻过来,
    /// 对象树会多出一层永远只有一个孩子、点开还不能切的节点。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 方言身份与层级形态()
    {
        OraclePack pack = NewPack();

        Assert.AreEqual(SqlDialect.Oracle, pack.Dialect);
        Assert.IsTrue(pack.HasSchemas, "Oracle 的 schema 就是 user,是实打实的一级。");
        Assert.IsFalse(pack.HasDatabases, "'库'那一级由 schema 顶替;翻过来会画出一层空节点。");
    }

    /// <summary>
    /// 方言包登记处认得出这一份。
    /// <para>
    /// <c>DialectPacks</c> 是反射装配的(见 <c>SqlSession.cs</c>),所以"类写好了"与
    /// "会话拿得到它"是两件事:类名拼错、忘了 <c>public</c> 无参构造、
    /// <see cref="IDialectPack.Dialect" /> 返回了别的枚举值,任何一条都会让它静默缺席 ——
    /// 表现是 Oracle 连上了但对象树是空的,不报错。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 方言包登记处认得出_Oracle()
    {
        Assert.IsTrue(DialectPacks.Has(SqlDialect.Oracle), "反射装配没把 OraclePack 收进去。");
        Assert.IsInstanceOfType<OraclePack>(DialectPacks.For(SqlDialect.Oracle));
    }

    /// <summary>
    /// 标识符转义:定界符是双引号,转义是双引号加倍。
    /// <para>
    /// <b>为什么明明 Oracle 禁止标识符里出现双引号,还要测这一条</b>:正因为合法名字里不可能有它,
    /// 那个带双引号的输入就<b>只可能</b>是注入载荷 —— §5.4.4 在 SQL Server 上实测同类载荷
    /// 真的删掉了一张表。这条断言守的是"哪怕这一步在正常输入上从不触发,它也必须在"。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 标识符转义_双引号加倍()
    {
        OraclePack pack = NewPack();

        Assert.AreEqual("\"ORDERS\"", pack.QuoteIdentifier("ORDERS"));
        Assert.AreEqual("\"a\"\"b\"", pack.QuoteIdentifier("a\"b"));
        // 典型载荷:不转义的话闭合引号会提前收尾,后面那段就成了可执行的 SQL。
        Assert.AreEqual(
            "\"ORDERS\"\"; DROP TABLE VICTIM--\"",
            pack.QuoteIdentifier("ORDERS\"; DROP TABLE VICTIM--"));
        // 空标识符也要包起来(而不是返回空串):拼出去至少是个能被服务端拒绝的语法错,
        // 返回空串则会静默拼成 `SELECT * FROM .` 这种更难查的东西。
        Assert.AreEqual("\"\"", pack.QuoteIdentifier(""));
    }

    /// <summary>
    /// **本方言最大的坑**:转义这一步<b>绝不能</b>顺手把标识符折成大写。
    /// <para>
    /// Oracle 的规则是"不加引号的标识符折大写存字典,加引号的原样存",两者可以并存 ——
    /// <c>ORDERS</c> 与 <c>orders</c> 是同一个 schema 里的两张不同的表。
    /// 一旦这里做了大小写规范化,拼出来的 <c>"ORDERS"</c> 查的就是另一张表:
    /// 对象树上画得出来、一点开报 ORA-00942 表不存在(§5.4.5 在 PG 上真机坐实过同型故障)。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 标识符转义_不做大小写规范化()
    {
        OraclePack pack = NewPack();

        Assert.AreEqual("\"orders\"", pack.QuoteIdentifier("orders"));
        Assert.AreEqual("\"OrderDetail\"", pack.QuoteIdentifier("OrderDetail"));
        Assert.AreNotEqual(
            pack.QuoteIdentifier("ORDERS"),
            pack.QuoteIdentifier("orders"),
            "大小写不同的两个名字在 Oracle 上是两个对象,转义之后必须仍然分得开。");
    }

    /// <summary>限定名两段<b>各自</b>转义,中间一个点。只转一段等于给另一段留了个洞。</summary>
    [TestMethod]
    public void 限定名两段都转义()
    {
        OraclePack pack = NewPack();

        Assert.AreEqual(
            "\"APP\".\"ORDERS\"",
            pack.QuoteQualified(new SqlObject(SqlObjectKind.Table, "ORDERS", "APP")));
        Assert.AreEqual(
            "\"a\"\"b\".\"c\"\"d\"",
            pack.QuoteQualified(new SqlObject(SqlObjectKind.Table, "c\"d", "a\"b")));
        // schema 为空时只出一段,不能拼成 `"".` 这种东西。
        Assert.AreEqual(
            "\"ORDERS\"",
            pack.QuoteQualified(new SqlObject(SqlObjectKind.Table, "ORDERS")));
    }

    /// <summary>
    /// 分页走 12c 的行限定子句 <c>OFFSET … ROWS FETCH NEXT … ROWS ONLY</c>。
    /// <para>
    /// 逐字断言而不是"包含 OFFSET"是有意的:这条子句<b>必须跟在整条语句(含 <c>ORDER BY</c>)之后</b>,
    /// 顺序写反了服务端直接 ORA-00933,而"包含"型断言看不出顺序。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 分页语句_按行限定子句生成()
    {
        OraclePack pack = NewPack();

        Assert.AreEqual(
            "SELECT * FROM \"APP\".\"ORDERS\" ORDER BY \"ID\"\nOFFSET 200 ROWS FETCH NEXT 100 ROWS ONLY",
            pack.ApplyPaging("SELECT * FROM \"APP\".\"ORDERS\" ORDER BY \"ID\"", 200, 100));

        Assert.AreEqual(
            "SELECT 1 FROM DUAL\nOFFSET 0 ROWS FETCH NEXT 200 ROWS ONLY",
            pack.ApplyPaging("SELECT 1 FROM DUAL", 0, 200));
    }

    /// <summary>
    /// 分页要剥掉尾分号。
    /// <para>
    /// <b>Oracle 的 SQL 通道根本不接受语句末尾的分号</b>(那是 SQL*Plus 的行终止符,不是 SQL 的一部分)。
    /// 用户手敲的 SQL 十有八九以分号收尾,不剥掉就是 ORA-00911 —— 而用户看着自己那条
    /// 在别的工具里能跑的 SQL,只会以为是插件坏了。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 分页_剥掉尾分号与尾随空白()
    {
        OraclePack pack = NewPack();

        Assert.AreEqual(
            "SELECT 1 FROM DUAL\nOFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY",
            pack.ApplyPaging("SELECT 1 FROM DUAL;", 0, 10));
        Assert.AreEqual(
            "SELECT 1 FROM DUAL\nOFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY",
            pack.ApplyPaging("SELECT 1 FROM DUAL ;  \r\n", 0, 10));
        Assert.AreEqual(
            "SELECT 1 FROM DUAL\nOFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY",
            pack.ApplyPaging("SELECT 1 FROM DUAL; ;", 0, 10));
    }

    /// <summary>
    /// 负数夹到 0。界面上的页码算错(或者上层减出个负 offset)不该变成一句语法错 ——
    /// <c>OFFSET -1 ROWS</c> 在 Oracle 上会被当成 0 处理还是报错并不确定,与其依赖服务端的宽容,不如自己夹住。
    /// </summary>
    [TestMethod]
    public void 分页_负数夹到零()
    {
        OraclePack pack = NewPack();

        Assert.AreEqual(
            "SELECT 1 FROM DUAL\nOFFSET 0 ROWS FETCH NEXT 0 ROWS ONLY",
            pack.ApplyPaging("SELECT 1 FROM DUAL", -5, -1));
    }

    /// <summary>空白 SQL 要当场拒绝,而不是拼出一条只有 <c>OFFSET</c> 的残句。</summary>
    [TestMethod]
    public void 分页_空白输入直接拒绝()
    {
        OraclePack pack = NewPack();

        _ = Assert.ThrowsExactly<ArgumentException>(() => pack.ApplyPaging("   ", 0, 10));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => pack.ApplyPaging(null!, 0, 10));
    }

    /// <summary>
    /// 估算行数走 <c>ALL_TABLES.NUM_ROWS</c>,且属主/表名<b>作为字符串字面量</b>拼进去时单引号加倍。
    /// <para>
    /// 这个接口不给参数通道(返回的是一条不带参数的 SQL),所以它是本包里少数几个
    /// 必须拼接的地方之一 —— 那就必须证明拼接是转义过的。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 估算行数_取自_ALL_TABLES_且字面量已转义()
    {
        OraclePack pack = NewPack();

        string? sql = pack.EstimateRowCountSql(new SqlObject(SqlObjectKind.Table, "ORDERS", "APP"));

        Assert.IsNotNull(sql);
        StringAssert.Contains(sql, "ALL_TABLES");
        StringAssert.Contains(sql, "NUM_ROWS");
        StringAssert.Contains(sql, "'APP'");
        StringAssert.Contains(sql, "'ORDERS'");
        Assert.IsFalse(sql.Contains("DBA_", StringComparison.Ordinal), "普通账号读不到 DBA_* 视图。");

        // 注入载荷:闭合引号必须被加倍,后面那段不能变成可执行的 SQL。
        string? injected = pack.EstimateRowCountSql(
            new SqlObject(SqlObjectKind.Table, "X' OR '1'='1", "APP"));
        Assert.IsNotNull(injected);
        StringAssert.Contains(injected, "'X'' OR ''1''=''1'");
    }

    /// <summary>
    /// 估算行数:视图如实返回 <see langword="null" />(视图在 <c>ALL_TABLES</c> 里一行都没有),
    /// 物化视图则要多一跳去 <c>ALL_MVIEWS</c> 拿容器表名 —— 容器表名不保证等于物化视图名。
    /// </summary>
    [TestMethod]
    public void 估算行数_视图为空_物化视图走容器表()
    {
        OraclePack pack = NewPack();

        Assert.IsNull(
            pack.EstimateRowCountSql(new SqlObject(SqlObjectKind.View, "V_ORDERS", "APP")),
            "给一条恒空的语句不如明说拿不到。");

        string? mview = pack.EstimateRowCountSql(
            new SqlObject(SqlObjectKind.MaterializedView, "MV_SALES", "APP"));
        Assert.IsNotNull(mview);
        StringAssert.Contains(mview, "ALL_MVIEWS");
        StringAssert.Contains(mview, "CONTAINER_NAME");
    }

    /// <summary>
    /// 建表 DDL 走 <c>DBMS_METADATA.GET_DDL</c>(Oracle 没有 <c>SHOW CREATE TABLE</c>),
    /// 三种类别各自的对象类型串正确,其余类别如实返回 <see langword="null" />。
    /// </summary>
    [TestMethod]
    public void 建表语句_走_DBMS_METADATA()
    {
        OraclePack pack = NewPack();

        string? table = pack.ShowCreateSql(new SqlObject(SqlObjectKind.Table, "ORDERS", "APP"));
        Assert.IsNotNull(table);
        StringAssert.Contains(table, "DBMS_METADATA.GET_DDL");
        StringAssert.Contains(table, "'TABLE'");
        StringAssert.Contains(table, "'ORDERS'");
        StringAssert.Contains(table, "'APP'");
        StringAssert.Contains(table, "FROM DUAL");

        StringAssert.Contains(
            pack.ShowCreateSql(new SqlObject(SqlObjectKind.View, "V_ORDERS", "APP"))!,
            "'VIEW'");
        StringAssert.Contains(
            pack.ShowCreateSql(new SqlObject(SqlObjectKind.MaterializedView, "MV_SALES", "APP"))!,
            "'MATERIALIZED_VIEW'");

        // 认不出的类别:返回 null,**不拼一段半成品 DDL**——用户复制走会真的建错东西。
        Assert.IsNull(pack.ShowCreateSql(new SqlObject(SqlObjectKind.Sequence, "SEQ_ID", "APP")));
        Assert.IsNull(pack.ShowCreateSql(new SqlObject(SqlObjectKind.Trigger, "TRG_X", "APP")));
    }

    /// <summary>建表 DDL 的字面量同样要转义;schema 为空时回落到服务端的当前 schema 而不是空串。</summary>
    [TestMethod]
    public void 建表语句_字面量已转义_且空_schema_回落服务端()
    {
        OraclePack pack = NewPack();

        string? injected = pack.ShowCreateSql(new SqlObject(SqlObjectKind.Table, "X' OR '1'='1", "APP"));
        Assert.IsNotNull(injected);
        StringAssert.Contains(injected, "'X'' OR ''1''=''1'");

        string? noSchema = pack.ShowCreateSql(new SqlObject(SqlObjectKind.Table, "ORDERS"));
        Assert.IsNotNull(noSchema);
        StringAssert.Contains(noSchema, "SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')");
        Assert.IsFalse(noSchema.Contains(", '', ", StringComparison.Ordinal), "空 schema 不能拼成空字面量。");
    }

    /// <summary>
    /// 会话 id 语句<b>必须同时取回 SID 与 SERIAL#</b>。
    /// <para>
    /// 这是 Oracle 旁路取消的硬要求:SID 是会话槽位号、会被复用,SERIAL# 是这个槽位的第几次使用。
    /// 只拿 SID 去发取消语句,等目标会话结束、槽位被别人占了,掐掉的就是<b>一个无辜的会话</b>。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 会话id语句_同时取_SID_与_SERIAL()
    {
        OraclePack pack = NewPack();
        string? sql = pack.SessionIdSql;

        Assert.IsNotNull(sql, "放弃旁路取消 = Oracle 上跑飞的同步查询再也打不断(§3.10)。");
        StringAssert.Contains(sql, "SID");
        StringAssert.Contains(sql, "SERIAL#");
        StringAssert.Contains(sql, "V$SESSION");
    }

    /// <summary>
    /// 取消语句:只认 <c>"sid,serial#"</c> 两个十进制数,拼出 <c>ALTER SYSTEM CANCEL SQL</c>。
    /// <para>
    /// 选 <c>CANCEL SQL</c> 而不是 <c>KILL SESSION</c>,是为了与 PG 的 <c>pg_cancel_backend</c>、
    /// MySQL 的 <c>KILL QUERY</c> 保持同一条纪律:取消的是一条查询,不是用户的整个会话
    /// (掐会话会把编辑器里未提交的事务一起送走)。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 取消语句_取消查询而不是掐会话()
    {
        OraclePack pack = NewPack();

        Assert.AreEqual("ALTER SYSTEM CANCEL SQL '12, 345'", pack.CancelSessionSql("12,345"));
        Assert.AreEqual("ALTER SYSTEM CANCEL SQL '12, 345'", pack.CancelSessionSql(" 12 , 345 "));
        Assert.IsFalse(
            pack.CancelSessionSql("12,345")!.Contains("KILL", StringComparison.OrdinalIgnoreCase),
            "掐会话会连未提交事务一起送走,三个方言在这一点上必须表现一致。");
    }

    /// <summary>
    /// 取消语句:认不出的会话 id 一律返回 <see langword="null" />,不猜、不拼。
    /// <para>
    /// <c>ALTER SYSTEM</c> 不接受绑定变量,拼接是这里唯一的注入面。
    /// "这个值一定是我自己从 <see cref="OraclePack.SessionIdSql" /> 查回来的"是一句靠调用方守的约定 ——
    /// 约定守不住的时候,校验是最后一道。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 取消语句_拒绝一切不是两个十进制数的输入()
    {
        OraclePack pack = NewPack();

        Assert.IsNull(pack.CancelSessionSql(""), "空输入。");
        Assert.IsNull(pack.CancelSessionSql("12"), "只有 SID 没有 SERIAL#:猜错会掐掉无辜会话。");
        Assert.IsNull(pack.CancelSessionSql("abc,def"), "非数字。");
        Assert.IsNull(pack.CancelSessionSql("12,"), "缺 SERIAL#。");
        Assert.IsNull(pack.CancelSessionSql(",345"), "缺 SID。");
        Assert.IsNull(pack.CancelSessionSql("12,345' ; DROP TABLE VICTIM--"), "注入载荷。");
        Assert.IsNull(pack.CancelSessionSql("12,345,678"), "多余的段:宁可不发也不发一条半懂的。");
        Assert.IsNull(pack.CancelSessionSql("-12,345"), "负数不是会话号。");
    }

    /// <summary>
    /// 列数据库恒空,且<b>不碰连接</b>(所以这里能拿 <see langword="null" /> 当连接传进去)。
    /// <para>
    /// 这一条同时守住两件事:① <c>HasDatabases = false</c> 与这个方法的返回值不打架;
    /// ② 将来有人"顺手"在这里塞一条查 <c>V$PDBS</c> 的 SQL 时,用例会当场炸 ——
    /// 那条 SQL 对普通业务账号是 ORA-00942,会把对象树的第一层打死。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task 列数据库恒空且不发查询()
    {
        OraclePack pack = NewPack();

        IReadOnlyList<SqlObject> databases =
            await pack.ListDatabasesAsync(null!, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(0, databases.Count);
    }

    /// <summary>
    /// <b>结构性断言:<see cref="IDialectPack" /> 的每一个成员都由本包<b>自己</b>实现了</b>,
    /// 没有一格是"忘了写、掉回基类的默认值"。
    /// <para>
    /// 基类 <see cref="DialectPackBase" /> 给了四个 <c>virtual</c> 的空实现
    /// (<c>EstimateRowCountSql</c> / <c>SessionIdSql</c> / <c>CancelSessionSql</c> / <c>ShowCreateSql</c>
    /// 一律返回 <see langword="null" />),这对"这个方言真的没有"是对的,
    /// 对"我还没写"则是一个**静默的洞**:界面只会表现成"这个功能在 Oracle 上没有"。
    /// 所以这些格子必须在本类上真的被覆写过 —— 覆写之后返回 <see langword="null" /> 是决定,
    /// 没覆写返回 <see langword="null" /> 是遗漏,反射分得开而人眼分不开。
    /// </para>
    /// <para>
    /// <see cref="IDialectPack.QuoteIdentifier" /> 是唯一有意留给基类的:定界符加倍是所有方言的通行转义,
    /// 各写一遍只会各错一遍。它由 <see cref="OraclePack.Delimiters" /> 参数化,那一格另有用例守着。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 接口每个成员都由本包实现()
    {
        string[] mustOverride =
        [
            "get_Dialect", "get_HasSchemas", "get_HasDatabases",
            "ListDatabasesAsync", "ListSchemasAsync", "ListRelationsAsync", "DescribeAsync",
            "ApplyPaging", "EstimateRowCountSql",
            "get_SessionIdSql", "CancelSessionSql", "ShowCreateSql"
        ];

        InterfaceMapping map = typeof(OraclePack).GetInterfaceMap(typeof(IDialectPack));
        Dictionary<string, MethodInfo> targets = [];
        for (int i = 0; i < map.InterfaceMethods.Length; i++)
        {
            targets[map.InterfaceMethods[i].Name] = map.TargetMethods[i];
        }

        foreach (string name in mustOverride)
        {
            Assert.IsTrue(targets.ContainsKey(name), $"接口上找不到 {name},契约变了要同步这份清单。");
            Assert.AreEqual(
                typeof(OraclePack),
                targets[name].DeclaringType,
                $"{name} 掉回了基类的默认实现 —— 那是一个静默的洞,不是一个决定。");
            Assert.IsFalse(targets[name].IsAbstract, $"{name} 仍然是抽象的。");
        }

        // QuoteIdentifier 是有意留给基类的那一格。
        Assert.AreEqual(typeof(DialectPackBase), targets["QuoteIdentifier"].DeclaringType);
        // 定界符是本包自己定的(双引号),不是继承来的。
        PropertyInfo? delimiters = typeof(OraclePack).GetProperty(
            "Delimiters", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(delimiters);
        Assert.AreEqual(typeof(OraclePack), delimiters.DeclaringType);
    }

    /// <summary>
    /// <b>结构性断言:纯函数成员一个都不抛。</b>
    /// <para>
    /// 这一条守的是"骨架先提交、方法体回头补"这种半成品 —— <c>throw new NotImplementedException()</c>
    /// 编译得过、也不会被上面那条覆写检查抓到(它确实覆写了)。
    /// 这里把所有不需要连接的成员按<b>每一种对象类别</b>各调一遍:任何一格抛,用例就红。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 纯函数成员在每种对象类别上都不抛()
    {
        OraclePack pack = NewPack();

        foreach (SqlObjectKind kind in Enum.GetValues<SqlObjectKind>())
        {
            var withSchema = new SqlObject(kind, "OBJ", "APP");
            var withoutSchema = new SqlObject(kind, "OBJ");

            // 返回 null 是合法答案(那是"这个方言拿不到"),抛异常不是。
            _ = pack.EstimateRowCountSql(withSchema);
            _ = pack.EstimateRowCountSql(withoutSchema);
            _ = pack.ShowCreateSql(withSchema);
            _ = pack.ShowCreateSql(withoutSchema);
            Assert.IsNotNull(pack.QuoteQualified(withSchema));
            Assert.IsNotNull(pack.QuoteQualified(withoutSchema));
        }

        _ = pack.SessionIdSql;
        _ = pack.CancelSessionSql("1,2");
        _ = pack.CancelSessionSql("");
        Assert.IsNotNull(pack.ApplyPaging("SELECT 1 FROM DUAL", 0, 1));
        Assert.IsNotNull(pack.QuoteIdentifier("X"));
    }

    /// <summary>
    /// <b>SQL 常量的三条纪律</b>,逐条对着一个具体的故障:
    /// <list type="number">
    /// <item><description>
    /// <b>绑定变量前缀必须是 <c>:</c>,不能是 <c>@</c></b>。脚手架把参数命名成 <c>@p0</c> / <c>@p1</c>
    /// (那是 PG / MySQL / SQL Server 的写法),而 <b>Oracle 的 <c>@</c> 是数据库链接(dblink)操作符</b>,
    /// 写进 SQL 文本会变成语法错。ODP.NET 默认 <c>BindByName = false</c>,
    /// 参数是<b>按出现位置</b>对上的,所以名字对不对无所谓、SQL 文本里的前缀对不对才要命。
    /// </description></item>
    /// <item><description>
    /// <b>同一个占位符在一条 SQL 里最多出现一次,且 <c>:p0</c> 必须排在 <c>:p1</c> 前面</b>。
    /// 这也是位置绑定的直接后果:写两次 <c>:p0</c> 就成了"要三个参数",而脚手架只给两个,
    /// 报的是一句与真正原因毫无关系的 ORA-01008。<see cref="OraclePack" /> 的关系列表查询
    /// 用 <c>WITH scope AS (…)</c> 把属主收成一行,正是为了满足这一条。
    /// </description></item>
    /// <item><description>
    /// <b>数据源只用 <c>ALL_*</c></b>:<c>USER_*</c> 看不见别的 schema(而"开着 A 用户的连接看 B schema"
    /// 是 Oracle 上的常态),<c>DBA_*</c> 普通账号一律 ORA-00942、一上来就把整棵树打死。
    /// 顺带钉死"不做 <c>UPPER()</c>" —— 那会把 <c>ORDERS</c> 与 <c>orders</c> 两张真实存在的表混成一张。
    /// </description></item>
    /// </list>
    /// </summary>
    [TestMethod]
    public void SQL常量_绑定变量与数据源纪律()
    {
        FieldInfo[] constants = [.. typeof(OraclePack)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Sql", StringComparison.Ordinal))];

        Assert.IsTrue(
            constants.Length >= 6,
            $"只找到 {constants.Length} 条 SQL 常量 —— 要么 SQL 被内联回方法体里了(那这条纪律就没人守了),要么命名变了。");

        foreach (FieldInfo field in constants)
        {
            string sql = (string)field.GetRawConstantValue()!;
            string where = field.Name;

            Assert.IsFalse(sql.Contains("@p", StringComparison.Ordinal),
                $"{where}:Oracle 的 @ 是 dblink 操作符,绑定变量前缀必须是 ':'。");
            Assert.IsTrue(Occurrences(sql, ":p0") <= 1,
                $"{where}::p0 出现了不止一次;ODP.NET 按位置绑定,会变成'要更多参数'。");
            Assert.IsTrue(Occurrences(sql, ":p1") <= 1,
                $"{where}::p1 出现了不止一次;同上。");
            if (sql.Contains(":p1", StringComparison.Ordinal))
            {
                Assert.IsTrue(sql.Contains(":p0", StringComparison.Ordinal),
                    $"{where}:用了 :p1 却没用 :p0,位置绑定会把属主绑到对象名上。");
                Assert.IsTrue(
                    sql.IndexOf(":p0", StringComparison.Ordinal) < sql.IndexOf(":p1", StringComparison.Ordinal),
                    $"{where}::p0 必须出现在 :p1 之前 —— 位置绑定只认先后。");
            }

            Assert.IsFalse(sql.Contains("DBA_", StringComparison.Ordinal),
                $"{where}:DBA_* 对普通账号是 ORA-00942。");
            Assert.IsFalse(sql.Contains("USER_", StringComparison.Ordinal),
                $"{where}:USER_* 只看得见连接用户自己那一个 schema。");
            Assert.IsFalse(sql.Contains("UPPER(", StringComparison.OrdinalIgnoreCase),
                $"{where}:大小写规范化会把 ORDERS 与 orders 两张真表混成一张。");
            Assert.IsFalse(sql.Contains("LOWER(", StringComparison.OrdinalIgnoreCase),
                $"{where}:同上。");
        }
    }

    /// <summary>
    /// 系统对象的标记<b>不许再写回 <c>SqlObject.Comment</c></b>。
    /// <para>
    /// 这条是一次真实事故的棘轮。早先五个方言包各自往 <c>Comment</c> 里塞一个
    /// <c>"@system"</c> 记号,注释里写着"对象树认它来决定默认折叠" ——
    /// 而对象树<b>一个字都没读</b>。真机上的后果:Oracle 的根上并排躺着 28 个
    /// <c>ORACLE_MAINTAINED</c> schema 与 2 个用户 schema,MySQL 的 14 个库里
    /// 4 个系统库按字母序插在业务库中间。
    /// </para>
    /// <para>
    /// 现在系统性落在 <c>SqlObject.IsSystem</c> 上 —— 模型上的一格,
    /// 树读得到、单测点得着。<b>扫源码</b>是因为这类回退不会以编译错误的形式出现:
    /// 谁都可以再往 <c>Comment</c> 里塞一个字符串,而它照样编得过、跑得动、
    /// 只是界面又混回去了。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 系统对象的标记不再借道注释字段()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "plugins")))
        {
            dir = dir.Parent;
        }
        Assert.IsNotNull(dir, "找不到仓库根 —— 这条守卫要扫方言包源码。");

        string packs = Path.Combine(dir.FullName, "plugins", "VelaShell.Plugin.Sql", "Metadata");
        Assert.IsTrue(Directory.Exists(packs), $"方言包目录不见了:{packs}");

        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(packs, "*.cs"))
        {
            int number = 0;
            foreach (string line in File.ReadLines(file))
            {
                number++;
                // 只看代码,不看注释 —— 上面这段说明里就写着这个字符串。
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                if (line.Contains("\"@system\"", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{number}");
                }
            }
        }

        Assert.AreEqual(
            0,
            offenders.Count,
            $"系统对象要标在 SqlObject.IsSystem 上,不能塞回注释字段:{string.Join(", ", offenders)}");
    }

    /// <summary>MSTest 注入的上下文(取消令牌从这里来)。</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>数一个子串在文本里出现了几次(不重叠)。</summary>
    /// <param name="text">文本。</param>
    /// <param name="value">子串。</param>
    /// <returns>次数。</returns>
    private static int Occurrences(string text, string value)
    {
        int count = 0;
        int index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
