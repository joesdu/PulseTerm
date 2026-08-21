using System.Data.Common;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.Plugin.Sql.Ui;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// **跨库浏览的真机验收** —— 用户报上来的第二号故障:"pgsql 数据库连表都显示不出来"。
/// <para>
/// <b>现象</b>:连上 PostgreSQL 之后对象树的根上列出了十几个库,每一个都点得开,
/// 而每一个点开都是一个空的 <c>public</c> —— 库里明明有表。
/// </para>
/// <para>
/// <b>根因</b>:PG 的目录表(<c>pg_namespace</c> / <c>pg_class</c>)<b>只覆盖当前连接所在的那个库</b>,
/// 而对象树对每一个库节点都拿会话上那条唯一的元数据连接去查。于是无论展开哪个库,
/// 查到的都是连接串里那个库的内容 —— 连到 <c>postgres</c> 时那里恰好一张表都没有,
/// 所以每个库看起来都是空的。真机实测(psql,PostgreSQL 18.1):
/// 同一条 <c>pg_class</c> 查询在 <c>postgres</c> 上回 0 行、在 <c>ops_pg</c> 上回 9 行。
/// </para>
/// <para>
/// <b>修法</b>:<c>IDialectPack.MetadataSpansCatalogs</c> 把"目录表跨不跨库"变成方言的一格,
/// <c>SqlSession.MetadataForAsync</c> 按库懒开并缓存连接。这一组盯的就是这条路 ——
/// <b>连到 A 库,要能看见 B 库的表</b>。
/// </para>
/// <para>
/// SQL Server 同病同治(<c>sys.objects</c> 也是每库一份),所以两个方言各验一遍。
/// 按仓库惯例:服务端不在就 <c>Inconclusive</c>,不是失败。
/// </para>
/// <para>
/// 这一组还捎带验对象树的另外两件同源的事,它们都是"树该给出什么"的一部分:
/// <b>系统对象归组</b>(用户报的第一号故障),以及<b>例程与序列各有分类</b>
/// (早先整个树只有"表"与"视图"两栏,而 <c>SqlObjectKind</c> 上的
/// <c>Procedure</c>/<c>Function</c>/<c>Sequence</c> 三格从来没有人填过)。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class SqlCrossCatalogTests
{
    private static readonly Loc Localization = new("zh-Hans");

    /// <summary>
    /// 本组专用的 PostgreSQL 库。
    /// <para>
    /// <b>刻意不用 <c>ops_pg</c>。</b> 那个库是 <c>PostgreSqlOpsTests</c> 与
    /// <c>PgBinaryFallbackTests</c> 共用的场地,而本组要在里面**反复建删表**才验得到跨库 ——
    /// 建删表会挪动 <c>pg_class</c> 的物理行序,而 <c>PgBinaryFallbackTests</c> 的
    /// <c>select * from pg_class limit 20</c> 恰好靠行序取样。实测:并行跑时它会间歇性地
    /// 取到一批 <c>relacl</c> 全为 NULL 的行,于是根本不触发降级、断言落空。
    /// </para>
    /// <para>各组用各组的库,是这个仓库里已有的惯例(见那两组的类注释),这里照办。</para>
    /// </summary>
    private const string CrossDatabase = "cross_pg";

    /// <summary>MSTest 注入的上下文(取消令牌从它来)。</summary>
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Token => TestContext.CancellationTokenSource.Token;

    // ═══════════════════════════ PostgreSQL ═══════════════════════════

    /// <summary>
    /// 连在 <c>postgres</c> 上,展开 <c>ops_pg</c> 要看得见它 <c>public</c> 下的表。
    /// <para>
    /// <b>这条用例本身就是那个缺陷的复现脚本。</b> 修复前它必然失败在最后一句:
    /// 表清单是空的 —— 因为查的是 <c>postgres</c> 库的 <c>public</c>。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task PostgreSQL_连在一个库上要看得见另一个库的表()
    {
        if (!await EnsureCrossDatabaseAsync().ConfigureAwait(false))
        {
            Assert.Inconclusive("没有可用的 PostgreSQL(127.0.0.1:55432)。");
            return;
        }

        await using (SqlSession session = await OpenPostgresAsync("postgres").ConfigureAwait(false))
        {
            // 先在**另一个库**里备一张一眼认得出的表。
            const string Probe = "cross_catalog_probe";
            await using (SqlSession seed = await OpenPostgresAsync(CrossDatabase).ConfigureAwait(false))
            {
                await ExecAsync(seed, $"drop table if exists public.{Probe}").ConfigureAwait(false);
                await ExecAsync(seed, $"create table public.{Probe}(id int primary key, tag text)").ConfigureAwait(false);
            }

            Assert.AreEqual("postgres", session.DefaultCatalog, "这条会话必须落在 postgres 上,否则验不到跨库。");

            var tree = new SqlTreeViewModel(session, Localization);
            await tree.InitializeAsync(Token).ConfigureAwait(false);

            SqlTreeNode database = tree.Roots.SingleOrDefault(n => n.Title == CrossDatabase)
                ?? throw new AssertFailedException($"根上没有 {CrossDatabase} —— 库清单本身就不对了。");
            await database.LoadAsync(Token).ConfigureAwait(false);

            SqlTreeNode schema = database.Children.SingleOrDefault(n => n.Title == "public")
                ?? throw new AssertFailedException(
                    $"{CrossDatabase} 下没有 public,拿到的是:{string.Join(", ", database.Children.Select(c => c.Title))}");
            await schema.LoadAsync(Token).ConfigureAwait(false);

            SqlTreeNode tables = schema.Children.First(n => n.Kind == SqlNodeKind.Category);
            await tables.LoadAsync(Token).ConfigureAwait(false);

            CollectionAssert.Contains(
                (string[])[.. tables.Children.Select(c => c.Title)],
                Probe,
                $"连在 postgres 上展开 {CrossDatabase} 却看不见它的表 —— 这正是被报上来的那个故障。");
        }
    }

    /// <summary>
    /// 库清单里的系统库(<c>postgres</c>)与用户库分开;<c>pg_catalog</c> 归到系统 schema。
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task PostgreSQL_系统库与系统schema各归各的()
    {
        if (!await EnsureCrossDatabaseAsync().ConfigureAwait(false))
        {
            Assert.Inconclusive("没有可用的 PostgreSQL(127.0.0.1:55432)。");
            return;
        }

        await using (SqlSession session = await OpenPostgresAsync(CrossDatabase).ConfigureAwait(false))
        {
            var tree = new SqlTreeViewModel(session, Localization);
            await tree.InitializeAsync(Token).ConfigureAwait(false);

            string[] top = [.. tree.Roots.Select(n => n.Title)];
            CollectionAssert.Contains(top, CrossDatabase, "用户库要在第一层。");
            CollectionAssert.DoesNotContain(top, "postgres", "postgres 是落脚库,不是业务库,该归进系统分组。");

            SqlTreeNode group = tree.Roots.Single(n => n.Kind == SqlNodeKind.SystemGroup);
            await group.LoadAsync(Token).ConfigureAwait(false);
            CollectionAssert.Contains(
                (string[])[.. group.Children.Select(c => c.Title)],
                "postgres",
                "系统库要**照样列得出来** —— 藏起来是另一种撒谎。");

            // schema 这一层同理:pg_catalog 与 information_schema 现在列得出来,但不与用户 schema 混排。
            SqlTreeNode database = tree.Roots.Single(n => n.Title == CrossDatabase);
            await database.LoadAsync(Token).ConfigureAwait(false);
            string[] schemas = [.. database.Children.Select(n => n.Title)];
            CollectionAssert.Contains(schemas, "public");
            CollectionAssert.DoesNotContain(schemas, "pg_catalog", "目录 schema 不该与用户 schema 并排。");

            SqlTreeNode schemaGroup = database.Children.Single(n => n.Kind == SqlNodeKind.SystemGroup);
            await schemaGroup.LoadAsync(Token).ConfigureAwait(false);
            string[] systemSchemas = [.. schemaGroup.Children.Select(n => n.Title)];
            CollectionAssert.Contains(systemSchemas, "pg_catalog",
                "早先这两个 schema 是被 WHERE 掉的,于是'看看 pg_class 长什么样'根本做不到。");
            CollectionAssert.Contains(systemSchemas, "information_schema");
        }
    }

    /// <summary>
    /// 序列与例程有各自的分类,不混进"表"里。
    /// <para>真机建一个序列与一个函数再读回来 —— 只比 SQL 文本证不了目录里认不认。</para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task PostgreSQL_序列与函数各有分类()
    {
        if (!await EnsureCrossDatabaseAsync().ConfigureAwait(false))
        {
            Assert.Inconclusive("没有可用的 PostgreSQL(127.0.0.1:55432)。");
            return;
        }

        await using (SqlSession session = await OpenPostgresAsync(CrossDatabase).ConfigureAwait(false))
        {
            await ExecAsync(session, "drop sequence if exists public.cross_seq_probe").ConfigureAwait(false);
            await ExecAsync(session, "create sequence public.cross_seq_probe").ConfigureAwait(false);
            await ExecAsync(session, "drop function if exists public.cross_fn_probe(int)").ConfigureAwait(false);
            await ExecAsync(
                session,
                "create function public.cross_fn_probe(n int) returns int language sql as $$ select n + 1 $$")
                .ConfigureAwait(false);

            var pack = new PostgreSqlPack();
            IReadOnlyList<SqlObject> sequences = await session.Metadata
                .UseAsync((c, t) => pack.ListSequencesAsync(c, "public", t), Token).ConfigureAwait(false);
            IReadOnlyList<SqlObject> routines = await session.Metadata
                .UseAsync((c, t) => pack.ListRoutinesAsync(c, "public", t), Token).ConfigureAwait(false);
            IReadOnlyList<SqlObject> relations = await session.Metadata
                .UseAsync((c, t) => pack.ListRelationsAsync(c, "public", t), Token).ConfigureAwait(false);

            Assert.IsTrue(
                sequences.Any(s => s.Name == "cross_seq_probe" && s.Kind == SqlObjectKind.Sequence),
                "序列这一栏必须查得出来。");
            Assert.IsFalse(
                relations.Any(r => r.Name == "cross_seq_probe"),
                "序列不能混进'表'那一栏 —— 它点开是 last_value/log_cnt/is_called,不是数据。");

            // PG 允许重载,所以名字必须带形参签名,否则树上是几行一模一样的名字。
            SqlObject fn = routines.Single(r => r.Name.StartsWith("cross_fn_probe(", StringComparison.Ordinal));
            Assert.AreEqual(SqlObjectKind.Function, fn.Kind);
            StringAssert.Contains(fn.Name, "integer", $"函数名要带上 DROP 认的那份形参签名,实际:{fn.Name}");
        }
    }

    /// <summary>
    /// **查询标签必须绑在它那张表所在的库上。**
    /// <para>
    /// 树上双击 <c>cross_pg.public.orders</c> 生成的是 <c>SELECT * FROM "public"."orders"</c> ——
    /// 两段限定名<b>说不出"哪个库"</b>。所以库这一级只能落在连接上:
    /// 不给查询标签带上它,这条 SQL 会跑在连接串里那个库上,回来的是 42P01,
    /// 而树上那张表明明就在那儿。
    /// </para>
    /// <para>
    /// 这条用**同一条 SQL 在两根连接上一成一败**来证:成的那根是按库开的,
    /// 败的那根是默认的 —— 两边都成或都败都说明机制没在起作用。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task PostgreSQL_查询连接要绑在对象所在的库上()
    {
        if (!await EnsureCrossDatabaseAsync().ConfigureAwait(false))
        {
            Assert.Inconclusive("没有可用的 PostgreSQL(127.0.0.1:55432)。");
            return;
        }

        const string Probe = "cross_qtab_probe";
        await using (SqlSession seed = await OpenPostgresAsync(CrossDatabase).ConfigureAwait(false))
        {
            await ExecAsync(seed, $"drop table if exists public.{Probe}").ConfigureAwait(false);
            await ExecAsync(seed, $"create table public.{Probe}(id int primary key)").ConfigureAwait(false);
        }

        // 会话落在 postgres 上,而目标表在 cross_pg 里。
        await using SqlSession session = await OpenPostgresAsync("postgres").ConfigureAwait(false);
        Assert.AreEqual("postgres", session.DefaultCatalog);

        SqlConnection bound = await session.OpenQueryConnectionAsync(CrossDatabase, Token).ConfigureAwait(false);
        try
        {
            Assert.AreEqual(
                CrossDatabase,
                bound.Info.DatabaseName,
                "按库开的查询连接必须真的落在那个库上,不然徽标写的是一回事、跑的是另一回事。");
            Assert.AreEqual(1, await CountProbeAsync(bound, Probe).ConfigureAwait(false));
        }
        finally
        {
            await session.CloseQueryConnectionAsync(bound).ConfigureAwait(false);
        }

        // 反面:不带库的那根连接上,同一条 SQL 必须查不到 —— 否则这条用例什么都没证。
        SqlConnection plain = await session.OpenQueryConnectionAsync("", Token).ConfigureAwait(false);
        try
        {
            Assert.AreEqual("postgres", plain.Info.DatabaseName);
            await Assert.ThrowsExactlyAsync<Npgsql.PostgresException>(
                () => CountProbeAsync(plain, Probe),
                "默认连接上本来就该 42P01 —— 它看不见 cross_pg 里的表。两边都成说明库根本没切。");
        }
        finally
        {
            await session.CloseQueryConnectionAsync(plain).ConfigureAwait(false);
        }
    }

    /// <summary>在指定连接上数一下探针表在不在(在就是 1)。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="probe">表名。</param>
    /// <returns>行数。</returns>
    private static Task<int> CountProbeAsync(SqlConnection connection, string probe) =>
        connection.UseAsync(async (raw, token) =>
        {
            await using DbCommand command = raw.CreateCommand();
            command.CommandText = $"select count(*) from public.{probe}";
            object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return value is null ? 0 : 1;
        });

    /// <summary>
    /// **在库节点上按 F5,要连它底下各 schema 的清单一起作废。**
    /// <para>
    /// 库节点自己的 <c>Schema</c> 是空的(它下面挂的是 schema,不是对象),
    /// 而缓存键带着各自的 schema 名。只按 (库, "") 精确匹配的话一条都清不掉 ——
    /// 树会重建出一批新的 schema 节点,而它们一展开又原样拿回旧清单。
    /// 表现是"在库上按 F5 没反应,在 schema 上按才有",而这个差别没有任何人能猜到。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task PostgreSQL_在库节点上F5要作废它底下每个schema的清单()
    {
        if (!await EnsureCrossDatabaseAsync().ConfigureAwait(false))
        {
            Assert.Inconclusive("没有可用的 PostgreSQL(127.0.0.1:55432)。");
            return;
        }

        const string Probe = "cross_f5_probe";
        await using SqlSession session = await OpenPostgresAsync(CrossDatabase).ConfigureAwait(false);
        await ExecAsync(session, $"drop table if exists public.{Probe}").ConfigureAwait(false);

        var tree = new SqlTreeViewModel(session, Localization);
        await tree.InitializeAsync(Token).ConfigureAwait(false);

        SqlTreeNode database = tree.Roots.Single(n => n.Title == CrossDatabase);
        await database.LoadAsync(Token).ConfigureAwait(false);
        SqlTreeNode schema = database.Children.Single(n => n.Title == "public");
        await schema.LoadAsync(Token).ConfigureAwait(false);
        SqlTreeNode tables = schema.Children.First(n => n.Kind == SqlNodeKind.Category);
        await tables.LoadAsync(Token).ConfigureAwait(false);
        CollectionAssert.DoesNotContain((string[])[.. tables.Children.Select(c => c.Title)], Probe);

        // 树背后建一张新表,然后**在库节点上**按 F5 —— 不是在 schema 上。
        await ExecAsync(session, $"create table public.{Probe}(id int primary key)").ConfigureAwait(false);
        await tree.RefreshAsync(database, Token).ConfigureAwait(false);

        SqlTreeNode freshSchema = database.Children.Single(n => n.Title == "public");
        await freshSchema.LoadAsync(Token).ConfigureAwait(false);
        SqlTreeNode freshTables = freshSchema.Children.First(n => n.Kind == SqlNodeKind.Category);
        await freshTables.LoadAsync(Token).ConfigureAwait(false);

        CollectionAssert.Contains(
            (string[])[.. freshTables.Children.Select(c => c.Title)],
            Probe,
            "在库节点上 F5 之后新建的表还是看不见 —— 作废范围没盖到底下的 schema。");

        await ExecAsync(session, $"drop table if exists public.{Probe}").ConfigureAwait(false);
    }

    // ═══════════════════════════ SQL Server ═══════════════════════════

    /// <summary>
    /// 连在 <c>master</c> 上,展开用户库要看得见它的表。
    /// <para><c>sys.objects</c> 与 PG 的 <c>pg_class</c> 同病:每库一份。</para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task SQLServer_连在master上要看得见用户库的表()
    {
        SqlSession? session = await TryOpenSqlServerAsync("master").ConfigureAwait(false);
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 SQL Server((localdb)\\VelaSpike)。");
            return;
        }

        await using (session)
        {
            const string Database = "ops_mssql";
            const string Probe = "cross_catalog_probe";
            await ExecAsync(
                session,
                $"if db_id('{Database}') is null exec('create database [{Database}]')").ConfigureAwait(false);

            await using (SqlSession seed = await OpenSqlServerAsync(Database).ConfigureAwait(false))
            {
                await ExecAsync(seed, $"if object_id('dbo.{Probe}') is not null drop table dbo.{Probe}")
                    .ConfigureAwait(false);
                await ExecAsync(seed, $"create table dbo.{Probe}(id int primary key, tag nvarchar(20))")
                    .ConfigureAwait(false);
            }

            var tree = new SqlTreeViewModel(session, Localization);
            await tree.InitializeAsync(Token).ConfigureAwait(false);

            SqlTreeNode database = tree.Roots.SingleOrDefault(n => n.Title == Database)
                ?? throw new AssertFailedException(
                    $"根上没有 {Database},拿到的是:{string.Join(", ", tree.Roots.Select(r => r.Title))}");
            await database.LoadAsync(Token).ConfigureAwait(false);

            SqlTreeNode schema = database.Children.SingleOrDefault(n => n.Title == "dbo")
                ?? throw new AssertFailedException(
                    $"{Database} 下没有 dbo,拿到的是:{string.Join(", ", database.Children.Select(c => c.Title))}");
            await schema.LoadAsync(Token).ConfigureAwait(false);

            SqlTreeNode tables = schema.Children.First(n => n.Kind == SqlNodeKind.Category);
            await tables.LoadAsync(Token).ConfigureAwait(false);

            CollectionAssert.Contains(
                (string[])[.. tables.Children.Select(c => c.Title)],
                Probe,
                "连在 master 上展开用户库却看不见它的表 —— 与 PG 是同一个缺陷。");
        }
    }

    /// <summary>
    /// 四个系统库(<c>master</c>/<c>tempdb</c>/<c>model</c>/<c>msdb</c>)归进系统分组,不顶在用户库前面。
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task SQLServer_系统库不与用户库混排()
    {
        SqlSession? session = await TryOpenSqlServerAsync("master").ConfigureAwait(false);
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 SQL Server((localdb)\\VelaSpike)。");
            return;
        }

        await using (session)
        {
            var tree = new SqlTreeViewModel(session, Localization);
            await tree.InitializeAsync(Token).ConfigureAwait(false);

            string[] top = [.. tree.Roots.Select(n => n.Title)];
            foreach (string system in (string[])["master", "model", "msdb", "tempdb"])
            {
                CollectionAssert.DoesNotContain(top, system, $"{system} 是系统库,不该与用户库并排。");
            }

            SqlTreeNode group = tree.Roots.Single(n => n.Kind == SqlNodeKind.SystemGroup);
            await group.LoadAsync(Token).ConfigureAwait(false);
            string[] inside = [.. group.Children.Select(n => n.Title)];
            CollectionAssert.Contains(inside, "master");
            CollectionAssert.Contains(inside, "msdb", "运维要去 msdb 看作业与备份历史,它必须列得出来。");
        }
    }

    // ═══════════════════════════ MySQL / Oracle 的例程与序列 ═══════════════════════════

    /// <summary>
    /// MySQL:例程查得出来,而且<b>没有序列这一类</b>。
    /// <para>
    /// 后半句同样要验:给 MySQL 画一个恒空的"序列"分类,与"这个库真的没有序列"长得一模一样。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task MySQL_例程查得出来而且没有序列这一类()
    {
        SqlSession? session = await TryOpenAsync(MySqlRequest("ops_mysql"), SqlDialect.MySql).ConfigureAwait(false);
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 MySQL(127.0.0.1:13306)。");
            return;
        }

        await using (session)
        {
            await ExecAsync(session, "drop function if exists vs_probe_fn").ConfigureAwait(false);
            await ExecAsync(
                session,
                "create function vs_probe_fn(n int) returns int deterministic return n + 1").ConfigureAwait(false);

            var pack = new MySqlPack();
            Assert.IsTrue(pack.HasRoutines);
            Assert.IsFalse(pack.HasSequences, "MySQL 的自增是列属性,不是独立对象 —— 不该画一个恒空的序列分类。");

            IReadOnlyList<SqlObject> routines = await session.Metadata
                .UseAsync((c, t) => pack.ListRoutinesAsync(c, "ops_mysql", t), Token).ConfigureAwait(false);
            SqlObject fn = routines.Single(r => r.Name == "vs_probe_fn");
            Assert.AreEqual(SqlObjectKind.Function, fn.Kind);
            Assert.IsFalse(fn.IsSystem, "业务库里的函数不是系统对象。");

            // 系统库里的例程要带上 IsSystem —— sys 库真机上有 48 个。
            IReadOnlyList<SqlObject> systemRoutines = await session.Metadata
                .UseAsync((c, t) => pack.ListRoutinesAsync(c, "sys", t), Token).ConfigureAwait(false);
            Assert.IsTrue(systemRoutines.Count > 0, "sys 库里是有例程的,一个都没有说明查询本身不对。");
            Assert.IsTrue(systemRoutines.All(r => r.IsSystem), "系统库里的例程一个都不许漏标。");
        }
    }

    /// <summary>
    /// Oracle:例程与序列查得出来,而且 identity 列背后那条 <c>ISEQ$$_*</c> <b>不算序列</b>。
    /// <para>
    /// 引擎为 <c>GENERATED ALWAYS AS IDENTITY</c> 自建的序列用户删不掉也改不动,
    /// 画进树与"把物化视图的容器表当成表画出来"是同一类错 —— 那是实现细节,不是用户的对象。
    /// 真机上 <c>velaspike</c> 这个 schema 里的序列有一多半是这种。
    /// </para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task Oracle_例程与序列查得出来且不含identity内部序列()
    {
        SqlSession? session = await TryOpenAsync(OracleRequest(), SqlDialect.Oracle).ConfigureAwait(false);
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle(127.0.0.1:11521/FREEPDB1)。");
            return;
        }

        await using (session)
        {
            await ExecAsync(
                session,
                "create or replace function VS_PROBE_FN(n number) return number is begin return n + 1; end;")
                .ConfigureAwait(false);
            await DropQuietlyAsync(session, "drop sequence VS_PROBE_SEQ").ConfigureAwait(false);
            await ExecAsync(session, "create sequence VS_PROBE_SEQ").ConfigureAwait(false);
            // identity 列会让引擎自建一条 ISEQ$$_* 序列。
            await DropQuietlyAsync(session, "drop table VS_PROBE_IDENT purge").ConfigureAwait(false);
            await ExecAsync(
                session,
                "create table VS_PROBE_IDENT(id number generated always as identity primary key, v varchar2(10))")
                .ConfigureAwait(false);

            var pack = new OraclePack();
            IReadOnlyList<SqlObject> routines = await session.Metadata
                .UseAsync((c, t) => pack.ListRoutinesAsync(c, "VELASPIKE", t), Token).ConfigureAwait(false);
            Assert.IsTrue(
                routines.Any(r => r.Name == "VS_PROBE_FN" && r.Kind == SqlObjectKind.Function),
                $"函数没查出来,拿到的是:{string.Join(", ", routines.Select(r => r.Name))}");

            IReadOnlyList<SqlObject> sequences = await session.Metadata
                .UseAsync((c, t) => pack.ListSequencesAsync(c, "VELASPIKE", t), Token).ConfigureAwait(false);
            CollectionAssert.Contains((string[])[.. sequences.Select(q => q.Name)], "VS_PROBE_SEQ");
            string[] internals = [.. sequences.Where(q => q.Name.StartsWith("ISEQ$$", StringComparison.Ordinal))
                .Select(q => q.Name)];
            Assert.AreEqual(
                0,
                internals.Length,
                $"identity 列的内部序列不该出现在树上:{string.Join(", ", internals)}");
        }
    }

    // ═══════════════════════════ 「当前」标记 ═══════════════════════════

    /// <summary>
    /// **「当前」只能标一个。**
    /// <para>
    /// 树把连接落脚的那个库 / schema 加粗，好让"我现在在哪"一眼可见。
    /// 而 Oracle 那一支曾经拿 <c>s.Name</c> 去和自己比 —— 恒为真，
    /// 于是真机上 30 个 schema <b>全部加粗</b>。全都加粗等于都没加粗，
    /// 而这种错在截图上也看不出来（一屏里本来就没有对照）。
    /// </para>
    /// <para>Oracle 的 schema 就是 user，所以判据是登录名。</para>
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task Oracle_当前schema只能标一个()
    {
        SqlSession? session = await TryOpenAsync(OracleRequest(), SqlDialect.Oracle).ConfigureAwait(false);
        if (session is null)
        {
            Assert.Inconclusive("没有可用的 Oracle(127.0.0.1:11521/FREEPDB1)。");
            return;
        }

        await using (session)
        {
            var tree = new SqlTreeViewModel(session, Localization);
            await tree.InitializeAsync(Token).ConfigureAwait(false);

            // 系统分组里的那批也要一起数 —— 真机上 30 个 schema 里 28 个在分组下面。
            List<SqlTreeNode> all = [];
            foreach (SqlTreeNode root in tree.Roots)
            {
                if (root.Kind == SqlNodeKind.SystemGroup)
                {
                    all.AddRange(root.Children);
                }
                else
                {
                    all.Add(root);
                }
            }

            Assert.IsGreaterThan(2, all.Count, "真机上这个实例有三十来个 schema,数量不对说明清单本身就错了。");
            string[] current = [.. all.Where(n => n.IsCurrent).Select(n => n.Title)];
            Assert.AreEqual(
                1,
                current.Length,
                $"「当前」只能标一个,实际标了 {current.Length} 个:{string.Join(", ", current)}");
            Assert.IsTrue(
                string.Equals(current[0], "VELASPIKE", StringComparison.OrdinalIgnoreCase),
                $"标错了 schema:{current[0]}");
        }
    }

    /// <summary>
    /// PostgreSQL 侧的同一件事：**只有连接落脚的那个库**被标成当前。
    /// </summary>
    /// <returns>等待句柄。</returns>
    [TestMethod]
    public async Task PostgreSQL_当前库只能标一个()
    {
        if (!await EnsureCrossDatabaseAsync().ConfigureAwait(false))
        {
            Assert.Inconclusive("没有可用的 PostgreSQL(127.0.0.1:55432)。");
            return;
        }

        await using SqlSession session = await OpenPostgresAsync(CrossDatabase).ConfigureAwait(false);
        var tree = new SqlTreeViewModel(session, Localization);
        await tree.InitializeAsync(Token).ConfigureAwait(false);

        List<SqlTreeNode> all = [];
        foreach (SqlTreeNode root in tree.Roots)
        {
            if (root.Kind == SqlNodeKind.SystemGroup)
            {
                all.AddRange(root.Children);
            }
            else
            {
                all.Add(root);
            }
        }

        string[] current = [.. all.Where(n => n.IsCurrent).Select(n => n.Title)];
        Assert.AreEqual(
            1,
            current.Length,
            $"「当前」只能标一个,实际标了 {current.Length} 个:{string.Join(", ", current)}");
        Assert.AreEqual(CrossDatabase, current[0]);
    }

    // ═══════════════════════════ 脚手架 ═══════════════════════════

    private static WorkspaceConnectRequest PostgresRequest(string database) => new()
    {
        SessionId = "cross-pg",
        Host = "127.0.0.1",
        Port = 55432,
        Username = "postgres",
        Password = "velaspike",
        DisplayName = "cross-pg",
        Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["database"] = database }
    };

    /// <summary>SQL Server 走 LocalDB 的命名管道:主机以 <c>(local</c> 打头时插件不拼端口,用户名留空走集成认证。</summary>
    /// <param name="database">库名。</param>
    /// <returns>连接请求。</returns>
    private static WorkspaceConnectRequest SqlServerRequest(string database) => new()
    {
        SessionId = "cross-mssql",
        Host = @"(localdb)\VelaSpike",
        Port = 1433,
        Username = "",
        Password = "",
        DisplayName = "cross-mssql",
        Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["database"] = database }
    };

    private static WorkspaceConnectRequest MySqlRequest(string database) => new()
    {
        SessionId = "cross-mysql",
        Host = "127.0.0.1",
        Port = 13306,
        Username = "root",
        Password = "velaspike",
        DisplayName = "cross-mysql",
        Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["database"] = database }
    };

    /// <summary>Oracle 走服务名 <c>FREEPDB1</c>;库这一级由 schema 顶替(见 <c>OraclePack.HasDatabases</c>)。</summary>
    /// <returns>连接请求。</returns>
    private static WorkspaceConnectRequest OracleRequest() => new()
    {
        SessionId = "cross-oracle",
        Host = "127.0.0.1",
        Port = 11521,
        Username = "velaspike",
        Password = "velaspike",
        DisplayName = "cross-oracle",
        Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["oracleServiceName"] = "FREEPDB1" }
    };

    private static async Task<SqlSession?> TryOpenAsync(WorkspaceConnectRequest request, SqlDialect dialect)
    {
        try
        {
            return await SqlSession.OpenAsync(request, dialect, Localization).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Oracle 没有 <c>DROP … IF EXISTS</c>,所以"不存在"这条错要吃掉(ORA-00942 / ORA-02289)。</summary>
    /// <param name="session">会话。</param>
    /// <param name="sql">语句。</param>
    /// <returns>等待句柄。</returns>
    private static async Task DropQuietlyAsync(SqlSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // 本来就不存在。
        }
    }

    /// <summary>
    /// 确保本组专用的库在。没有 PostgreSQL 时返回 <see langword="false" />(调用方 <c>Inconclusive</c>)。
    /// </summary>
    /// <returns>库可用与否。</returns>
    private static async Task<bool> EnsureCrossDatabaseAsync()
    {
        SqlSession? bootstrap = await TryOpenPostgresAsync("postgres").ConfigureAwait(false);
        if (bootstrap is null)
        {
            return false;
        }
        await using (bootstrap)
        {
            object? exists = await bootstrap.Metadata.UseAsync(async (raw, token) =>
            {
                await using DbCommand command = raw.CreateCommand();
                command.CommandText =
                    $"select 1 from pg_catalog.pg_database where datname = '{CrossDatabase}'";
                return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (exists is null)
            {
                // PG 没有 CREATE DATABASE IF NOT EXISTS,而且它不能在事务里跑,只能先查再单独发。
                // 库名是本文件里的常量,不是用户输入;仍然走 QuoteIdentifier,免得将来有人改成变量。
                await ExecAsync(
                    bootstrap,
                    $"create database {new PostgreSqlPack().QuoteIdentifier(CrossDatabase)}").ConfigureAwait(false);
            }
        }
        return true;
    }

    private static Task<SqlSession> OpenPostgresAsync(string database) =>
        SqlSession.OpenAsync(PostgresRequest(database), SqlDialect.PostgreSql, Localization);

    private static Task<SqlSession> OpenSqlServerAsync(string database) =>
        SqlSession.OpenAsync(SqlServerRequest(database), SqlDialect.SqlServer, Localization);

    private static async Task<SqlSession?> TryOpenPostgresAsync(string database)
    {
        try
        {
            return await OpenPostgresAsync(database).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<SqlSession?> TryOpenSqlServerAsync(string database)
    {
        try
        {
            return await OpenSqlServerAsync(database).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>发一条语句(建表 / 建库这类脚手架动作)。走闸门,与产品代码同一条路。</summary>
    /// <param name="session">会话。</param>
    /// <param name="sql">语句。</param>
    /// <returns>等待句柄。</returns>
    private static Task ExecAsync(SqlSession session, string sql) =>
        session.Metadata.UseAsync(async (raw, token) =>
        {
            await using DbCommand command = raw.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        });
}
