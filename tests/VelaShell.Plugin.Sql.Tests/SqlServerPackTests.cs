using System.Data.Common;
using System.Globalization;
using VelaShell.Plugin.Sql.Metadata;
using MsSqlConnection = Microsoft.Data.SqlClient.SqlConnection;
using MsSqlException = Microsoft.Data.SqlClient.SqlException;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// <see cref="SqlServerPack" /> 的真机测试。
/// <para>
/// <b>为什么不用替身</b>:这一层要验的就是"<c>sys.*</c> 到底长什么样" ——
/// <c>nvarchar</c> 的 <c>max_length</c> 是不是字节数、<c>(max)</c> 在目录里是不是 -1、
/// 计算列在不在 <c>sys.computed_columns</c> 里。用假数据测等于把待验的事实自己编一遍。
/// </para>
/// <para>
/// 真机是 SQL Server 2025 LocalDB 实例 <c>VelaSpike</c>(<c>sqllocaldb start VelaSpike</c>),
/// 测试自己建 <c>port_mssql</c> 库并在类初始化时重建,不依赖任何既有数据。
/// 连不上时逐个测试 <see cref="Assert.Inconclusive(string)" /> 跳过(仓库惯例) ——
/// 装不了 LocalDB 的机器上不该因为缺一台服务器就把构建判红,但也绝不用替身冒充通过。
/// </para>
/// <para>
/// <b>与移植前相比,这里少了几条断言,原因是契约里没有对应的格子</b>(不是实现退化):
/// 列上的 <c>BaseTypeName</c> / <c>IsGeneratedStored</c> / <c>GeneratedExpression</c> /
/// <c>PrimaryKeyOrdinal</c> / <c>Collation</c>、索引列的升降序与序号、外键的本表 schema/表名、
/// 数据库的状态与可访问性。能改用契约里等价格子表达的都改了(主键顺序改看
/// <see cref="SqlTableSchema.PrimaryKey" /> 与主键索引的列序、唯一约束/筛选/禁用改看
/// <see cref="SqlIndex.Kind" /> 上的记号、升降序改看 <see cref="SqlIndex.Definition" />),
/// 真的没处安放的才删。
/// </para>
/// </summary>
[TestClass]
public sealed class SqlServerPackTests
{
    /// <summary>本测试自己的库。别的探针库(<c>spike_*</c> / <c>pack_*</c>)与它无关,不要碰。</summary>
    private const string TestDatabase = "port_mssql";

    /// <summary>
    /// dbo 与 sales 下的<b>同名表</b> —— 本文件最重要的那组断言就架在这上面。
    /// 两张表列数不同、同名列 <c>Sku</c> 的长度与可空性也不同:
    /// 一旦实现退回按名字过滤(SqlSugar 走 <c>sysobjects</c> 就是这样),两边立刻串在一起。
    /// </summary>
    private static readonly string[] FixtureBatches =
    [
        "CREATE SCHEMA sales;",
        """
        CREATE TABLE dbo.Region (
            Country nvarchar(10) NOT NULL,
            City    nvarchar(20) NOT NULL,
            CONSTRAINT PK_Region PRIMARY KEY CLUSTERED (Country, City)
        );
        """,
        """
        CREATE TABLE dbo.Customer (
            CustomerId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY CLUSTERED,
            Code       nvarchar(30) NOT NULL,
            Name       nvarchar(50) NULL
        );
        """,
        // LineNo 是 T-SQL 保留字,DDL 里必须加定界符 —— 顺便让索引/主键那几条路径也吃一次保留字列名。
        """
        CREATE TABLE dbo.OrderDetail (
            OrderId    int              NOT NULL,
            [LineNo]   int              NOT NULL,
            Sku        nvarchar(50)     NOT NULL,
            Qty        int              NOT NULL CONSTRAINT DF_dbo_OrderDetail_Qty DEFAULT ((1)),
            UnitPrice  decimal(12,3)    NOT NULL,
            Amount     AS (Qty * UnitPrice),
            AmountTax  AS (Qty * UnitPrice * 0.13) PERSISTED,
            Note       nvarchar(max)    NULL,
            Status     varchar(20)      NOT NULL CONSTRAINT DF_dbo_OrderDetail_Status DEFAULT ('new'),
            CreatedAt  datetime2(3)     NOT NULL CONSTRAINT DF_dbo_OrderDetail_CreatedAt DEFAULT (sysutcdatetime()),
            RowGuid    uniqueidentifier NOT NULL CONSTRAINT DF_dbo_OrderDetail_RowGuid DEFAULT (newid()),
            Payload    varbinary(max)   NULL,
            IsActive   bit              NOT NULL CONSTRAINT DF_dbo_OrderDetail_IsActive DEFAULT ((1)),
            Doc        xml              NULL,
            CustomerId int              NULL,
            Country    nvarchar(10)     NULL,
            City       nvarchar(20)     NULL,
            CONSTRAINT PK_dbo_OrderDetail PRIMARY KEY CLUSTERED (OrderId, [LineNo])
        );
        """,
        """
        ALTER TABLE dbo.OrderDetail ADD CONSTRAINT FK_OrderDetail_Customer
            FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (CustomerId) ON DELETE SET NULL;
        """,
        """
        ALTER TABLE dbo.OrderDetail ADD CONSTRAINT FK_OrderDetail_Region
            FOREIGN KEY (Country, City) REFERENCES dbo.Region (Country, City) ON UPDATE CASCADE;
        """,
        "CREATE UNIQUE NONCLUSTERED INDEX IX_OrderDetail_Sku ON dbo.OrderDetail (Sku DESC);",
        "CREATE NONCLUSTERED INDEX IX_OrderDetail_Cover ON dbo.OrderDetail (CustomerId) INCLUDE (Qty, UnitPrice);",
        "CREATE NONCLUSTERED INDEX IX_OrderDetail_Active ON dbo.OrderDetail (Status) WHERE (IsActive = 1);",
        // 同名不同构:列数 3 对 17,Sku 是 nvarchar(200) NULL 对 nvarchar(50) NOT NULL。
        """
        CREATE TABLE sales.OrderDetail (
            DetailId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_sales_OrderDetail PRIMARY KEY CLUSTERED,
            Sku      nvarchar(200) NULL,
            Channel  nvarchar(20)  NOT NULL
        );
        """,
        // 名字里带结束定界符:定界符加倍这条规矩不成立的话,这张表根本查不到。
        "CREATE TABLE dbo.[Ta]]ble] ([We]]ird] int NOT NULL CONSTRAINT PK_Weird PRIMARY KEY CLUSTERED);",
        "CREATE VIEW dbo.v_OrderBrief AS SELECT OrderId, [LineNo], Sku, Amount, Note FROM dbo.OrderDetail;",
        """
        EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'订单明细(dbo 版)',
            @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'OrderDetail';
        """,
        """
        EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'商品编码,全局唯一',
            @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'OrderDetail',
            @level2type = N'COLUMN', @level2name = N'Sku';
        """,
        "INSERT INTO dbo.Customer (Code, Name) VALUES (N'C1', N'甲'), (N'C2', N'乙');",
        """
        INSERT INTO dbo.OrderDetail (OrderId, [LineNo], Sku, Qty, UnitPrice)
        VALUES (1, 1, N'A', 2, 3.5), (1, 2, N'B', 1, 9.25), (2, 1, N'C', 4, 1.0);
        """
    ];

    /// <summary>被测对象。无状态,建一个用到底。</summary>
    private static readonly SqlServerPack Pack = new();

    private static string? _unavailableReason;

    /// <summary>MSTest 注入的上下文。</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>当前测试的取消令牌(runsettings 里的 60s 超时到点会触发它)。</summary>
    private CancellationToken Token => TestContext.CancellationTokenSource.Token;

    /// <summary>重建 <c>port_mssql</c>。连不上就记下原因,让每个测试跳过而不是整片报错。</summary>
    /// <param name="context">测试上下文。</param>
    /// <returns>等待句柄。</returns>
    [ClassInitialize]
    public static async Task ClassInitializeAsync(TestContext context)
    {
        try
        {
            // 上一轮遗留的池化连接会挡住 DROP DATABASE,先清干净。
            MsSqlConnection.ClearAllPools();
            await using (var master = new MsSqlConnection(BuildConnectionString("master")))
            {
                await master.OpenAsync(context.CancellationTokenSource.Token).ConfigureAwait(false);
                await ExecuteAsync(master, $"""
                    IF DB_ID('{TestDatabase}') IS NOT NULL
                    BEGIN
                        ALTER DATABASE {TestDatabase} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                        DROP DATABASE {TestDatabase};
                    END
                    """).ConfigureAwait(false);
                await ExecuteAsync(master, $"CREATE DATABASE {TestDatabase};").ConfigureAwait(false);
            }
            await using var connection = new MsSqlConnection(BuildConnectionString(TestDatabase));
            await connection.OpenAsync().ConfigureAwait(false);
            foreach (string batch in FixtureBatches)
            {
                await ExecuteAsync(connection, batch).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is MsSqlException or InvalidOperationException or PlatformNotSupportedException)
        {
            _unavailableReason = $"连不上 SQL Server 2025 LocalDB 实例 VelaSpike(先跑 `sqllocaldb start VelaSpike`):{ex.Message}";
        }
    }

    /// <summary>
    /// 把 <c>port_mssql</c> 拆掉 —— 测试自建自毁,不给下一个人留一个来历不明的库。
    /// <para>清理失败绝不能让整片测试判红:该验的东西这时候已经验完了。</para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [ClassCleanup]
    public static async Task ClassCleanupAsync()
    {
        if (_unavailableReason is not null)
        {
            return;
        }
        try
        {
            // 池里留着的连接会挡住 DROP DATABASE(Msg 5030),先清干净。
            MsSqlConnection.ClearAllPools();
            await using var master = new MsSqlConnection(BuildConnectionString("master"));
            await master.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(master, $"""
                IF DB_ID('{TestDatabase}') IS NOT NULL
                BEGIN
                    ALTER DATABASE {TestDatabase} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE {TestDatabase};
                END
                """).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MsSqlException or InvalidOperationException)
        {
            // 拆不掉就留着 —— 下一轮的 ClassInitialize 会重建它。
        }
    }

    /// <summary>方言标识与两个能力位。对象树的层级(库 → schema → 对象)按它们搭。</summary>
    [TestMethod]
    public void Dialect_And_Capabilities()
    {
        Assert.AreEqual(SqlDialect.SqlServer, Pack.Dialect);
        Assert.IsTrue(Pack.HasSchemas, "SQL Server 有 schema 这一级。");
        Assert.IsTrue(Pack.HasDatabases, "SQL Server 有多库这一级。");
        // 反射装配的登记处认得出这一份 —— 类写好了与会话拿得到它是两件事。
        Assert.IsTrue(DialectPacks.Has(SqlDialect.SqlServer), "反射装配没把 SqlServerPack 收进去。");
    }

    /// <summary>定界符是方括号,标识符里出现 <c>]</c> 时加倍。</summary>
    [TestMethod]
    public void QuoteIdentifier_UsesBracketsAndDoublesClosingBracket()
    {
        Assert.AreEqual("[dbo]", Pack.QuoteIdentifier("dbo"));
        Assert.AreEqual("[Ta]]ble]", Pack.QuoteIdentifier("Ta]ble"));
        // 起始定界符不需要转义,别顺手一起加倍了。
        Assert.AreEqual("[Ta[ble]", Pack.QuoteIdentifier("Ta[ble"));
        Assert.AreEqual("[sales].[OrderDetail]", Pack.QuoteQualified(new(SqlObjectKind.Table, "OrderDetail", "sales")));
        Assert.AreEqual("[OrderDetail]", Pack.QuoteQualified(new(SqlObjectKind.Table, "OrderDetail")));
        Assert.AreEqual("[dbo].[Ta]]ble]", Pack.QuoteQualified(new(SqlObjectKind.Table, "Ta]ble", "dbo")));
    }

    /// <summary>库清单能列出本测试库,并把系统库标出来。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListDatabases_IncludesTestDatabaseAndFlagsSystemOnes()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        IReadOnlyList<SqlObject> databases = await Pack.ListDatabasesAsync(connection, Token).ConfigureAwait(false);

        SqlObject mine = databases.Single(d => d.Name == TestDatabase);
        Assert.AreEqual(SqlObjectKind.Database, mine.Kind);
        Assert.IsFalse(mine.IsSystem, "用户库不该被标成系统库。");
        // 系统库仍然列出来(用户要去 master 看会话、去 msdb 看作业),
        // 只是带上 IsSystem 让对象树把它们收进"系统对象"分组。
        Assert.IsTrue(databases.Single(d => d.Name == "master").IsSystem);
        Assert.IsTrue(databases.Single(d => d.Name == "tempdb").IsSystem);
        Assert.IsTrue(databases.Single(d => d.Name == "msdb").IsSystem);
        // 契约上没有"状态"与"可访问性"这两格,state_desc 与 HAS_DBACCESS 就此丢掉(见类注释)。
    }

    /// <summary>schema 清单区分用户 schema 与系统 schema。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListSchemas_SeparatesUserSchemasFromSystemOnes()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        IReadOnlyList<SqlObject> schemas = await Pack.ListSchemasAsync(connection, Token).ConfigureAwait(false);

        Assert.IsFalse(schemas.Single(s => s.Name == "dbo").IsSystem);
        Assert.IsFalse(schemas.Single(s => s.Name == "sales").IsSystem);
        Assert.IsTrue(schemas.Single(s => s.Name == "sys").IsSystem);
        Assert.IsTrue(schemas.Single(s => s.Name == "INFORMATION_SCHEMA").IsSystem);
        // 固定数据库角色带的那批(db_owner…)也算系统 schema,不该出现在对象树的用户区。
        Assert.IsTrue(schemas.Single(s => s.Name == "db_owner").IsSystem);
        Assert.AreEqual(SqlObjectKind.Schema, schemas.Single(s => s.Name == "sales").Kind);
        // 属主(sales 的属主是 dbo)契约里没有格子,丢掉。
    }

    /// <summary>
    /// <b>本包最重要的一组断言之一</b>:对象清单按 schema 过滤,两张同名表各归各的。
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListRelations_FiltersBySchema_SameNamedTablesStaySeparate()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        CancellationToken token = Token;

        IReadOnlyList<SqlObject> dbo = await Pack.ListRelationsAsync(connection, "dbo", token).ConfigureAwait(false);
        IReadOnlyList<SqlObject> sales = await Pack.ListRelationsAsync(connection, "sales", token).ConfigureAwait(false);

        Assert.IsTrue(dbo.All(r => r.Schema == "dbo"), "给了 schema 就必须按它过滤。");
        Assert.IsTrue(sales.All(r => r.Schema == "sales"));
        Assert.AreEqual(1, dbo.Count(r => r.Name == "OrderDetail"));
        Assert.AreEqual(1, sales.Count(r => r.Name == "OrderDetail"));
        Assert.AreEqual(1, sales.Count, "sales 下只有一张表。");

        // 不给 schema 时两张都在,而且能靠 Schema 区分开 —— 只给名字的清单是分不出来的。
        IReadOnlyList<SqlObject> all = await Pack.ListRelationsAsync(connection, "", token).ConfigureAwait(false);
        List<SqlObject> orderDetails = [.. all.Where(r => r.Name == "OrderDetail")];
        Assert.AreEqual(2, orderDetails.Count);
        CollectionAssert.AreEquivalent(new[] { "dbo", "sales" }, orderDetails.Select(r => r.Schema).ToArray());

        // 视图与表都在,而且分得清。
        SqlObject view = all.Single(r => r.Name == "v_OrderBrief");
        Assert.AreEqual(SqlObjectKind.View, view.Kind);
        Assert.AreEqual(SqlObjectKind.Table, all.Single(r => r.Name == "Customer").Kind);
        // 扩展属性里的中文表注释拿得到。
        Assert.AreEqual("订单明细(dbo 版)", orderDetails.Single(r => r.Schema == "dbo").Comment);
        Assert.AreEqual("", orderDetails.Single(r => r.Schema == "sales").Comment);
    }

    /// <summary>
    /// <b>本文件最重要的一条</b>:跨 schema 同名表的列绝不串。
    /// <para>
    /// SqlSugar 正是在这里塌的 —— 它按 <c>sysobjects.name</c> 过滤、不带 schema 也不分表/视图,
    /// <c>dbo.OrderDetail</c> 的列被 <c>sales.OrderDetail</c> 污染,
    /// 同名列的类型与可空性被顶掉,污染还一路传到代码生成(§3.6)。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListColumns_SameNamedTablesInDifferentSchemas_DoNotBleed()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        CancellationToken token = Token;

        IReadOnlyList<SqlColumn> dbo = await ColumnsAsync(connection, "dbo", "OrderDetail", token).ConfigureAwait(false);
        IReadOnlyList<SqlColumn> sales = await ColumnsAsync(connection, "sales", "OrderDetail", token).ConfigureAwait(false);

        Assert.AreEqual(17, dbo.Count, "dbo.OrderDetail 就是 17 列,多一列就说明串了 sales 的。");
        Assert.AreEqual(3, sales.Count, "sales.OrderDetail 就是 3 列。");

        // 同名列各是各的:类型、长度、可空性都不同。
        SqlColumn dboSku = Column(dbo, "Sku");
        SqlColumn salesSku = Column(sales, "Sku");
        Assert.AreEqual("nvarchar(50)", dboSku.DataType);
        Assert.IsFalse(dboSku.IsNullable);
        Assert.AreEqual("nvarchar(200)", salesSku.DataType);
        Assert.IsTrue(salesSku.IsNullable);

        // 对方独有的列不该出现在这边。
        Assert.IsFalse(dbo.Any(c => c.Name is "Channel" or "DetailId"), "sales 独有的列漏进 dbo 了。");
        Assert.IsFalse(sales.Any(c => c.Name is "Qty" or "Amount" or "Doc"), "dbo 独有的列漏进 sales 了。");

        // 列注释也是按列定位的,不会被同名列顶掉。
        Assert.AreEqual("商品编码,全局唯一", dboSku.Comment);
        Assert.AreEqual("", salesSku.Comment);
    }

    /// <summary>
    /// 类型原文必须是能直接写进 DDL 的完整形态:<c>(max)</c> 不是 -1、
    /// <c>nvarchar</c> 的长度是字符数不是字节数。
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListColumns_RendersFullNativeDataType()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        IReadOnlyList<SqlColumn> columns = await ColumnsAsync(connection, "dbo", "OrderDetail", Token).ConfigureAwait(false);

        // 目录里 max_length = 100(字节),用户看到的必须是 50。
        Assert.AreEqual("nvarchar(50)", Column(columns, "Sku").DataType);
        // 目录里 max_length = -1;显示 nvarchar(-1) 是 SqlSugar 的表现,不是这里的。
        Assert.AreEqual("nvarchar(max)", Column(columns, "Note").DataType);
        Assert.AreEqual("varbinary(max)", Column(columns, "Payload").DataType);
        // varchar 不除 2。
        Assert.AreEqual("varchar(20)", Column(columns, "Status").DataType);
        Assert.AreEqual("decimal(12,3)", Column(columns, "UnitPrice").DataType);
        // 括号里是时间精度而不是长度。
        Assert.AreEqual("datetime2(3)", Column(columns, "CreatedAt").DataType);
        // 这几个不带括号 —— xml 的 max_length 同样是 -1,套上 (max) 就成了非法类型。
        Assert.AreEqual("uniqueidentifier", Column(columns, "RowGuid").DataType);
        Assert.AreEqual("bit", Column(columns, "IsActive").DataType);
        Assert.AreEqual("xml", Column(columns, "Doc").DataType);
        Assert.AreEqual("int", Column(columns, "OrderId").DataType);
        // 裸类型名(BaseTypeName)契约里没有格子 —— 按类型着色/判二进制列只能从 DataType 前缀取。

        Assert.IsFalse(
            columns.Any(c => c.DataType.Contains("-1", StringComparison.Ordinal)),
            "目录表的内部编码 -1 绝不能漏到界面上。");
    }

    /// <summary>IDENTITY 认得出来,而且只认真正自增的那一列。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListColumns_DetectsIdentity_OnlyOnTheIdentityColumn()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        CancellationToken token = Token;

        IReadOnlyList<SqlColumn> sales = await ColumnsAsync(connection, "sales", "OrderDetail", token).ConfigureAwait(false);
        Assert.IsTrue(Column(sales, "DetailId").IsAutoIncrement);
        Assert.IsFalse(Column(sales, "Sku").IsAutoIncrement);
        Assert.IsFalse(Column(sales, "Channel").IsAutoIncrement);

        // dbo.OrderDetail 一列自增都没有。
        // SqlSugar 的 IsIdentity(表,列) 在这里会对**每一列**返回 True(§3.6),
        // 所以"全是 identity"正是那条不合理特征。
        IReadOnlyList<SqlColumn> dbo = await ColumnsAsync(connection, "dbo", "OrderDetail", token).ConfigureAwait(false);
        Assert.IsFalse(dbo.Any(c => c.IsAutoIncrement), "dbo.OrderDetail 没有自增列。");

        IReadOnlyList<SqlColumn> customer = await ColumnsAsync(connection, "dbo", "Customer", token).ConfigureAwait(false);
        Assert.IsTrue(Column(customer, "CustomerId").IsAutoIncrement);
        Assert.IsFalse(Column(customer, "Code").IsAutoIncrement);
    }

    /// <summary>计算列认得出来,持久化与非持久化都算生成列(两者都不可写)。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListColumns_DetectsComputedColumns_PersistedAndNot()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        SqlTableSchema schema = await DescribeAsync(connection, "dbo", "OrderDetail", Token).ConfigureAwait(false);
        IReadOnlyList<SqlColumn> columns = schema.Columns;

        SqlColumn amount = Column(columns, "Amount");
        Assert.IsTrue(amount.IsGenerated, "非持久化计算列也是生成列 —— 它同样不可写。");
        Assert.IsFalse(amount.IsWritable);

        SqlColumn amountTax = Column(columns, "AmountTax");
        Assert.IsTrue(amountTax.IsGenerated, "PERSISTED 的计算列同样是生成列。");
        Assert.IsFalse(amountTax.IsWritable);
        // 落不落盘(IsGeneratedStored)与生成表达式原文(GeneratedExpression)契约里没有格子,丢掉。

        Assert.IsFalse(
            schema.WritableColumns.Any(c => c.Name is "Amount" or "AmountTax"),
            "生成列必须被排除在可写列之外 —— 带上它回写会报错。");

        // 普通列不能被误判成生成列 —— 误判等于把一列从可写集合里剔掉,用户改不了那一格。
        Assert.IsFalse(Column(columns, "Qty").IsGenerated);
        Assert.IsTrue(Column(columns, "Qty").IsWritable);

        // 引擎推导出来的类型也要是完整形态(实测 decimal(23,3),不是裸 decimal)。
        Assert.AreEqual("decimal(23,3)", amount.DataType);
        Assert.AreEqual("numeric(26,5)", amountTax.DataType);
    }

    /// <summary>复合主键:成员标得对,顺序也对。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListColumns_MarksCompositePrimaryKeyInOrder()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        SqlTableSchema schema = await DescribeAsync(connection, "dbo", "OrderDetail", Token).ConfigureAwait(false);

        Assert.IsTrue(Column(schema.Columns, "OrderId").IsPrimaryKey);
        Assert.IsTrue(Column(schema.Columns, "LineNo").IsPrimaryKey);
        Assert.AreEqual(2, schema.Columns.Count(c => c.IsPrimaryKey), "主键只有这两列。");
        // 唯一索引不是主键,别把 IX_OrderDetail_Sku 的列也标成主键。
        Assert.IsFalse(Column(schema.Columns, "Sku").IsPrimaryKey);

        // 契约里没有 PrimaryKeyOrdinal:主键成员的顺序改由这两处表达 ——
        // ① SqlTableSchema.PrimaryKey(**按列序**,回写定位用);
        CollectionAssert.AreEqual(new[] { "OrderId", "LineNo" }, schema.PrimaryKey.ToArray());
        // ② 主键索引的列序(**按键序**,这才是真正的主键顺序)。
        CollectionAssert.AreEqual(
            new[] { "OrderId", "LineNo" },
            schema.Indexes.Single(i => i.IsPrimaryKey).Columns.ToArray());
    }

    /// <summary>默认值:字面量与表达式必须分得开,而且不带目录表加的那层括号。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListColumns_ReadsDefaults_AndTellsLiteralsFromExpressions()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        IReadOnlyList<SqlColumn> columns = await ColumnsAsync(connection, "dbo", "OrderDetail", Token).ConfigureAwait(false);

        SqlColumn qty = Column(columns, "Qty");
        Assert.AreEqual("1", qty.DefaultValue, "目录里是 ((1)),给用户看的应当是 1。");
        Assert.IsFalse(qty.IsDefaultExpression);

        SqlColumn status = Column(columns, "Status");
        Assert.AreEqual("'new'", status.DefaultValue);
        Assert.IsFalse(status.IsDefaultExpression);

        SqlColumn isActive = Column(columns, "IsActive");
        Assert.AreEqual("1", isActive.DefaultValue);
        Assert.IsFalse(isActive.IsDefaultExpression);

        // 表达式只能交给服务端算:复制一行时照抄字面量就错了。
        SqlColumn createdAt = Column(columns, "CreatedAt");
        Assert.AreEqual("sysutcdatetime()", createdAt.DefaultValue);
        Assert.IsTrue(createdAt.IsDefaultExpression);

        SqlColumn rowGuid = Column(columns, "RowGuid");
        Assert.AreEqual("newid()", rowGuid.DefaultValue);
        Assert.IsTrue(rowGuid.IsDefaultExpression);

        // 没有默认约束的列不能凭空造一个。
        Assert.IsNull(Column(columns, "Note").DefaultValue);
        Assert.IsFalse(Column(columns, "Note").IsDefaultExpression);
    }

    /// <summary>
    /// 视图的列拿得到 —— SqlSugar 对视图返回 0 列且不抛异常,对象树上就是"点开什么都没有"(§2.3)。
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListColumns_WorksForViews()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        SqlTableSchema schema = await DescribeAsync(connection, "dbo", "v_OrderBrief", Token, SqlObjectKind.View).ConfigureAwait(false);
        IReadOnlyList<SqlColumn> columns = schema.Columns;

        Assert.AreEqual(5, columns.Count, "视图有 5 列,0 列说明走错了路径。");
        CollectionAssert.AreEqual(
            new[] { "OrderId", "LineNo", "Sku", "Amount", "Note" },
            columns.Select(c => c.Name).ToArray());
        // 类型是引擎推导好的,一样要给完整形态。
        Assert.AreEqual("nvarchar(50)", Column(columns, "Sku").DataType);
        Assert.AreEqual("nvarchar(max)", Column(columns, "Note").DataType);
        Assert.AreEqual("decimal(23,3)", Column(columns, "Amount").DataType);
        // 视图上没有主键/自增/默认值,别把底表的属性搬过来。
        Assert.IsFalse(columns.Any(c => c.IsPrimaryKey));
        Assert.IsFalse(columns.Any(c => c.IsAutoIncrement));
        Assert.IsFalse(columns.Any(c => c.DefaultValue is not null));
    }

    /// <summary>
    /// 不给 schema 时退回登录的默认 schema(通常是 <c>dbo</c>),而不是"随便哪个同名对象"。
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListColumns_WithoutSchema_FallsBackToTheDefaultSchema()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        CancellationToken token = Token;

        IReadOnlyList<SqlColumn> implicitSchema = await ColumnsAsync(connection, "", "OrderDetail", token).ConfigureAwait(false);
        IReadOnlyList<SqlColumn> explicitSchema = await ColumnsAsync(connection, "dbo", "OrderDetail", token).ConfigureAwait(false);

        CollectionAssert.AreEqual(
            explicitSchema.Select(c => c.Name).ToArray(),
            implicitSchema.Select(c => c.Name).ToArray(),
            "默认 schema 是 dbo,拿到的应当与显式 dbo 完全一致 —— 而不是把 sales 的那张也算进来。");
        Assert.AreEqual(17, implicitSchema.Count);
    }

    /// <summary>
    /// SQL Server 2025 的 <c>vector</c>:维数没有独立目录列,藏在 <c>max_length</c> 里
    /// (8 字节头 + 每维 4 字节)。这条把实现里那句注释钉在真机上。
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListColumns_RendersVectorDimension()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        CancellationToken token = Token;
        try
        {
            try
            {
                await ExecuteAsync(connection, "CREATE TABLE dbo._VectorProbe (Embedding vector(3) NOT NULL);").ConfigureAwait(false);
            }
            catch (MsSqlException ex)
            {
                Assert.Inconclusive($"这台服务器没有 vector 类型(SQL Server 2025 起才有):{ex.Message}");
            }

            IReadOnlyList<SqlColumn> columns = await ColumnsAsync(connection, "dbo", "_VectorProbe", token).ConfigureAwait(false);
            Assert.AreEqual(1, columns.Count);
            // 目录里 max_length = 20;直接当长度画出来就是 vector(20),当 varbinary 处理就是 vector(max)。
            Assert.AreEqual("vector(3)", columns[0].DataType);
        }
        finally
        {
            await ExecuteAsync(connection, "IF OBJECT_ID('dbo._VectorProbe') IS NOT NULL DROP TABLE dbo._VectorProbe;").ConfigureAwait(false);
        }
    }

    /// <summary>索引:唯一性、种类、有序键列,INCLUDE 列不混进键列。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListIndexes_ReportsUniquenessKindOrderAndKeepsIncludedColumnsOutOfKeys()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        CancellationToken token = Token;
        IReadOnlyList<SqlIndex> indexes = (await DescribeAsync(connection, "dbo", "OrderDetail", token).ConfigureAwait(false)).Indexes;

        Assert.AreEqual(4, indexes.Count, "主键 1 个 + 手工建的 3 个;按索引列出行不去重就会变成 7 条(SqlSugar 的表现)。");

        // 聚集主键索引:两个键列有序。
        SqlIndex pk = indexes.Single(i => i.Name == "PK_dbo_OrderDetail");
        Assert.IsTrue(pk.IsPrimaryKey);
        Assert.IsTrue(pk.IsUnique);
        Assert.AreEqual("CLUSTERED", pk.Kind);
        CollectionAssert.AreEqual(new[] { "OrderId", "LineNo" }, pk.Columns.ToArray());
        Assert.IsFalse(pk.Definition.Contains("DESC", StringComparison.Ordinal), "主键这两列都是升序。");

        // 非聚集唯一索引 + 降序键列。
        SqlIndex unique = indexes.Single(i => i.Name == "IX_OrderDetail_Sku");
        Assert.IsTrue(unique.IsUnique);
        Assert.IsFalse(unique.IsPrimaryKey);
        // 契约里没有 IsUniqueConstraint,改看 Kind 上的记号:手工建的唯一索引不是唯一约束,
        // 删除路径不一样(DROP INDEX 对 ALTER TABLE DROP CONSTRAINT)。
        Assert.AreEqual("NONCLUSTERED", unique.Kind);
        Assert.AreEqual(1, unique.Columns.Count);
        Assert.AreEqual("Sku", unique.Columns[0]);
        StringAssert.Contains(unique.Definition, "UNIQUE");
        // 键列的升降序契约里没有格子,只剩定义原文说得清。
        StringAssert.Contains(unique.Definition, "[Sku] DESC");

        // 带 INCLUDE 的覆盖索引:键列只有一个,INCLUDE 列只在 Definition 里。
        SqlIndex cover = indexes.Single(i => i.Name == "IX_OrderDetail_Cover");
        Assert.IsFalse(cover.IsUnique);
        Assert.AreEqual(1, cover.Columns.Count, "INCLUDE 列不是键列,混进来就会让最左前缀判断失真。");
        Assert.AreEqual("CustomerId", cover.Columns[0]);
        Assert.IsFalse(cover.Columns.Any(c => c is "Qty" or "UnitPrice"));
        StringAssert.Contains(cover.Definition, "INCLUDE ([Qty], [UnitPrice])");

        // 筛选索引:条件原文留着(契约里没有 FilterDefinition,改看 Kind 记号 + 定义原文)。
        SqlIndex filtered = indexes.Single(i => i.Name == "IX_OrderDetail_Active");
        StringAssert.Contains(filtered.Kind, "filtered");
        StringAssert.Contains(filtered.Definition, "WHERE");
        StringAssert.Contains(filtered.Definition, "IsActive");
        Assert.IsFalse(filtered.Kind.Contains("disabled", StringComparison.Ordinal));

        // 换一张表:另一个 schema 下的同名表有自己的索引,不该串过来。
        IReadOnlyList<SqlIndex> salesIndexes = (await DescribeAsync(connection, "sales", "OrderDetail", token).ConfigureAwait(false)).Indexes;
        Assert.AreEqual(1, salesIndexes.Count);
        Assert.AreEqual("PK_sales_OrderDetail", salesIndexes[0].Name);
    }

    /// <summary>外键:目标表与目标列齐全,复合外键的列顺序对得上。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListForeignKeys_ReportsTargetTableAndOrderedColumns()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        CancellationToken token = Token;
        IReadOnlyList<SqlForeignKey> foreignKeys =
            (await DescribeAsync(connection, "dbo", "OrderDetail", token).ConfigureAwait(false)).ForeignKeys;

        Assert.AreEqual(2, foreignKeys.Count);

        SqlForeignKey toCustomer = foreignKeys.Single(f => f.Name == "FK_OrderDetail_Customer");
        CollectionAssert.AreEqual(new[] { "CustomerId" }, toCustomer.Columns.ToArray());
        Assert.AreEqual("dbo", toCustomer.ReferencedSchema);
        Assert.AreEqual("Customer", toCustomer.ReferencedTable);
        CollectionAssert.AreEqual(new[] { "CustomerId" }, toCustomer.ReferencedColumns.ToArray());
        Assert.AreEqual("SET_NULL", toCustomer.OnDelete);
        Assert.AreEqual("NO_ACTION", toCustomer.OnUpdate);
        // 本表 schema / 表名契约里没有格子(它们本来就是 DescribeAsync 的入参),丢掉。

        // 复合外键:两侧列一一对应,顺序错了关系图就会画出不存在的对应。
        SqlForeignKey toRegion = foreignKeys.Single(f => f.Name == "FK_OrderDetail_Region");
        CollectionAssert.AreEqual(new[] { "Country", "City" }, toRegion.Columns.ToArray());
        CollectionAssert.AreEqual(new[] { "Country", "City" }, toRegion.ReferencedColumns.ToArray());
        Assert.AreEqual("Region", toRegion.ReferencedTable);
        Assert.AreEqual("CASCADE", toRegion.OnUpdate);

        // 同名表在 sales 下没有外键 —— 按 schema 过滤失效的话这里会捡到 dbo 的两条。
        IReadOnlyList<SqlForeignKey> salesForeignKeys =
            (await DescribeAsync(connection, "sales", "OrderDetail", token).ConfigureAwait(false)).ForeignKeys;
        Assert.AreEqual(0, salesForeignKeys.Count);
    }

    /// <summary>名字里带结束定界符的对象:元数据查得到,拼进 SQL 也不破。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task WeirdIdentifiers_SurviveMetadataAndSqlText()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        CancellationToken token = Token;

        // 元数据比对走参数,名字原样传进去就行。
        IReadOnlyList<SqlColumn> columns = await ColumnsAsync(connection, "dbo", "Ta]ble", token).ConfigureAwait(false);
        Assert.AreEqual(1, columns.Count);
        Assert.AreEqual("We]ird", columns[0].Name);
        Assert.IsTrue(columns[0].IsPrimaryKey);

        // 要拼进 SQL 就得过定界符:先加方括号,再作为字符串字面量进 OBJECT_ID()。
        string? sql = Pack.EstimateRowCountSql(new(SqlObjectKind.Table, "Ta]ble", "dbo"));
        Assert.IsNotNull(sql);
        StringAssert.Contains(sql, "N'[dbo].[Ta]]ble]'");
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? estimate = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        Assert.AreEqual(0L, Convert.ToInt64(estimate, CultureInfo.InvariantCulture));
    }

    /// <summary>用户标识符永远不拼进元数据查询:注入载荷只会查不到东西,不会执行。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListColumns_BindsIdentifiersAsParameters_SoInjectionPayloadsAreInert()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        CancellationToken token = Token;

        const string Payload = "OrderDetail'; DROP TABLE dbo.Customer--";
        IReadOnlyList<SqlColumn> columns = await ColumnsAsync(connection, "dbo", Payload, token).ConfigureAwait(false);
        Assert.AreEqual(0, columns.Count, "没有这个名字的对象,应当是空清单而不是一条 DDL。");

        // 哨兵表还在。
        IReadOnlyList<SqlColumn> customer = await ColumnsAsync(connection, "dbo", "Customer", token).ConfigureAwait(false);
        Assert.AreEqual(3, customer.Count);
    }

    /// <summary>
    /// <c>master</c> 上的系统对象**每一个都要带上 <c>IsSystem</c>**。
    /// <para>
    /// <b>这条的判据变过一次,值得说清为什么。</b> 早先它断言的是"<c>master</c> 上一个对象都列不出来"
    /// —— 那时 <c>is_ms_shipped</c> 是一条 <c>WHERE</c> 谓词,系统对象在树上整个不存在。
    /// 代价是 <c>msdb.dbo.backupset</c>、<c>msdb.dbo.sysjobs</c> 这些**运维每天都要查的表**
    /// 一张都看不见:它们全是 <c>is_ms_shipped = 1</c>。
    /// </para>
    /// <para>
    /// 现在它们照列,只是带上 <see cref="SqlObject.IsSystem" />,由对象树收进"系统对象"分组。
    /// 所以这条要验的从"滤干净"变成了**"一个都不许漏标"** ——
    /// 漏标一个,它就会跑到用户对象里去,而 <c>master</c> 上实测有 645 个系统视图(§3.6)。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ListRelations_OnMaster_FlagsEveryMicrosoftShippedObjectAsSystem()
    {
        await using DbConnection connection = await OpenAsync("master").ConfigureAwait(false);
        IReadOnlyList<SqlObject> relations = await Pack.ListRelationsAsync(connection, "", Token).ConfigureAwait(false);

        Assert.IsTrue(relations.Count > 0, "master 的 dbo 下是有系统对象的(spt_values 那一批),一个都没有说明查询本身不对。");
        string[] leaked = [.. relations.Where(r => !r.IsSystem).Select(r => $"{r.Schema}.{r.Name}")];
        Assert.AreEqual(
            0,
            leaked.Length,
            $"master 里没有用户对象,这些却没被标成系统对象:{string.Join(", ", leaked)}");
    }

    /// <summary>
    /// 分页:有 <c>ORDER BY</c> 时直接接,没有时补一个占位排序。两种形态都得真能跑。
    /// <para>
    /// 契约上没有"调用方告诉我有没有 ORDER BY"的那一格,所以这一条同时也是
    /// <c>SqlServerPack.HasTopLevelOrderBy</c> 那次文本扫描的验收。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task ApplyPaging_EmitsOffsetFetch_AndFallsBackToAPlaceholderOrderBy()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        CancellationToken token = Token;

        string ordered = Pack.ApplyPaging("SELECT OrderId, [LineNo] FROM dbo.OrderDetail ORDER BY OrderId, [LineNo]", 1, 2);
        StringAssert.Contains(ordered, "OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY");
        Assert.IsFalse(ordered.Contains("(SELECT NULL)", StringComparison.Ordinal), "用户已经给了排序键,不该再塞占位排序。");
        Assert.AreEqual(2, await CountRowsAsync(connection, ordered, token).ConfigureAwait(false));

        // OFFSET/FETCH 要求最外层必须有 ORDER BY,没有就是 Msg 10741。
        string unordered = Pack.ApplyPaging("SELECT OrderId FROM dbo.OrderDetail", 0, 2);
        StringAssert.Contains(unordered, "ORDER BY (SELECT NULL)");
        Assert.AreEqual(2, await CountRowsAsync(connection, unordered, token).ConfigureAwait(false));

        // 结尾的分号会把 OFFSET 甩成第二条语句。
        string withSemicolon = Pack.ApplyPaging("SELECT OrderId FROM dbo.OrderDetail;", 0, 1);
        Assert.AreEqual(1, await CountRowsAsync(connection, withSemicolon, token).ConfigureAwait(false));

        // 只有**最外层**的 ORDER BY 算数:窗口函数与子查询里的排序不该骗过扫描,
        // 骗过了就是 Msg 10741(该补的占位排序没补上)。
        string windowed = Pack.ApplyPaging(
            "SELECT ROW_NUMBER() OVER (ORDER BY OrderId) AS rn FROM dbo.OrderDetail", 0, 2);
        StringAssert.Contains(windowed, "ORDER BY (SELECT NULL)");
        Assert.AreEqual(2, await CountRowsAsync(connection, windowed, token).ConfigureAwait(false));

        // 注释与定界标识符里的 "order by" 同样不算数。
        string commented = Pack.ApplyPaging("SELECT OrderId FROM dbo.OrderDetail -- order by OrderId\n", 0, 1);
        StringAssert.Contains(commented, "ORDER BY (SELECT NULL)");
        Assert.AreEqual(1, await CountRowsAsync(connection, commented, token).ConfigureAwait(false));
    }

    /// <summary>估算行数走 <c>sys.dm_db_partition_stats</c>,而且真能跑出数来。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task EstimateRowCountSql_UsesPartitionStats_AndRuns()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        CancellationToken token = Token;

        string? sql = Pack.EstimateRowCountSql(new(SqlObjectKind.Table, "OrderDetail", "dbo"));
        Assert.IsNotNull(sql);
        StringAssert.Contains(sql, "sys.dm_db_partition_stats");
        StringAssert.Contains(sql, "N'[dbo].[OrderDetail]'");

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        Assert.AreEqual(3L, Convert.ToInt64(value, CultureInfo.InvariantCulture));

        // 同名表各估各的 —— 名字不带 schema 的实现在这里会串。
        await using DbCommand salesCommand = connection.CreateCommand();
        salesCommand.CommandText = Pack.EstimateRowCountSql(new(SqlObjectKind.Table, "OrderDetail", "sales"))!;
        Assert.AreEqual(0L, Convert.ToInt64(await salesCommand.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture));
    }

    /// <summary><c>SELECT @@SPID</c> 拿会话号。</summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task SessionIdSql_ReturnsCurrentSpid()
    {
        await using DbConnection connection = await OpenAsync(TestDatabase).ConfigureAwait(false);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = Pack.SessionIdSql!;
        long spid = Convert.ToInt64(await command.ExecuteScalarAsync(Token).ConfigureAwait(false), CultureInfo.InvariantCulture);
        Assert.IsTrue(spid > 0, "SPID 应当是正数。");
    }

    /// <summary>
    /// <c>KILL</c> <b>杀的是整条会话,不是取消一条语句</b>:被杀的一方之后不能再用,必须重建连接。
    /// 这条测试就是把那句注释钉在真机上。
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task CancelSessionSql_KillsTheWholeSession_NotJustTheStatement()
    {
        Assert.AreEqual("KILL 53", Pack.CancelSessionSql("53"));
        // 契约把会话 id 从 long 换成了 string,于是拼接这一步多了一个注入面:非数字一律拒绝。
        Assert.IsNull(Pack.CancelSessionSql("53; DROP TABLE dbo.Customer--"));
        Assert.IsNull(Pack.CancelSessionSql(""));

        CancellationToken token = Token;
        // 被杀的连接不能回池:池里留一条被杀过的连接会污染后面的测试。
        await using var victim = new MsSqlConnection(BuildConnectionString(TestDatabase) + ";Pooling=false");
        await victim.OpenAsync(token).ConfigureAwait(false);
        await using (DbCommand spidCommand = victim.CreateCommand())
        {
            spidCommand.CommandText = Pack.SessionIdSql!;
            long spid = Convert.ToInt64(await spidCommand.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);

            await using DbConnection killer = await OpenAsync(TestDatabase).ConfigureAwait(false);
            await using DbCommand killCommand = killer.CreateCommand();
            killCommand.CommandText = Pack.CancelSessionSql(spid.ToString(CultureInfo.InvariantCulture))!;
            await killCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await using DbCommand afterKill = victim.CreateCommand();
        afterKill.CommandText = "SELECT 1;";
        // 表现不是"查询被取消",而是连接进了 kill 状态后的错误 —— 界面上要按
        // "断开这条会话"措辞,不能说成"取消这条查询"。
        _ = await Assert.ThrowsExactlyAsync<MsSqlException>(
            async () => await afterKill.ExecuteScalarAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>SQL Server 没有建表原文,如实返回 <see langword="null" />,不自己拼一份冒充。</summary>
    [TestMethod]
    public void ShowCreateSql_ReturnsNull()
    {
        Assert.IsNull(Pack.ShowCreateSql(new(SqlObjectKind.Table, "OrderDetail", "dbo")));
        Assert.IsNull(Pack.ShowCreateSql(new(SqlObjectKind.View, "v_OrderBrief", "dbo")));
        Assert.IsNull(Pack.ShowCreateSql(new(SqlObjectKind.Table, "OrderDetail")));
    }

    /// <summary>取一张表/视图的结构。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="schema">schema;传空则走默认 schema。</param>
    /// <param name="name">对象名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="kind">对象类别。</param>
    /// <returns>结构。</returns>
    private static async Task<SqlTableSchema> DescribeAsync(
        DbConnection connection,
        string schema,
        string name,
        CancellationToken cancellationToken,
        SqlObjectKind kind = SqlObjectKind.Table) =>
        await Pack.DescribeAsync(connection, new(kind, name, schema), cancellationToken).ConfigureAwait(false);

    /// <summary>取一张表/视图的列(契约把列/索引/外键合成了一次 <c>DescribeAsync</c>)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="schema">schema;传空则走默认 schema。</param>
    /// <param name="name">对象名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>列。</returns>
    private static async Task<IReadOnlyList<SqlColumn>> ColumnsAsync(
        DbConnection connection, string schema, string name, CancellationToken cancellationToken) =>
        (await DescribeAsync(connection, schema, name, cancellationToken).ConfigureAwait(false)).Columns;

    /// <summary>取一列;取不到就把实际列名列出来,免得只看到一句 InvalidOperationException。</summary>
    /// <param name="columns">列清单。</param>
    /// <param name="name">列名。</param>
    /// <returns>那一列。</returns>
    private static SqlColumn Column(IReadOnlyList<SqlColumn> columns, string name)
    {
        SqlColumn? match = columns.SingleOrDefault(c => c.Name == name);
        Assert.IsNotNull(match, $"没有名为 {name} 的列;实际有:{string.Join(", ", columns.Select(c => c.Name))}");
        return match;
    }

    /// <summary>按任务书给的形态拼连接串。</summary>
    /// <param name="database">库名。</param>
    /// <returns>连接串。</returns>
    private static string BuildConnectionString(string database) =>
        $@"Server=(localdb)\VelaSpike;Integrated Security=true;Database={database};TrustServerCertificate=true;ConnectRetryCount=0";

    /// <summary>开一条连接;真机不在就跳过当前测试。</summary>
    /// <param name="database">库名。</param>
    /// <returns>已打开的连接。</returns>
    private static async Task<DbConnection> OpenAsync(string database)
    {
        if (_unavailableReason is not null)
        {
            Assert.Inconclusive(_unavailableReason);
        }
        var connection = new MsSqlConnection(BuildConnectionString(database));
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    /// <summary>跑一条不返回结果的语句。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">SQL。</param>
    /// <returns>等待句柄。</returns>
    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>数一条 SQL 实际返回多少行。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="sql">SQL。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>行数。</returns>
    private static async Task<int> CountRowsAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        int count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            count++;
        }
        return count;
    }
}
