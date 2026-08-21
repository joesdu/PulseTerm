using System.Collections.Concurrent;
using System.Data.Common;
using VelaShell.Plugin.Sql.Execution;
using VelaShell.Plugin.Sql.Metadata;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql;

/// <summary>
/// 一条数据库会话:持有这条连接用到的**几根物理连接**与方言包。
/// <para>
/// 为什么不是一根(设计文档 §5.2):<c>SqlSugarClient</c> 不是线程安全的,而数据库管理工具天然并发 ——
/// 用户一边跑长查询,一边在对象树里展开另一个库。更要紧的是**旁路取消必须有第二根连接**
/// (§3.10 的第二档),而 MySQL 的 <c>Cancel()</c> 本身就要另开一条连接发 <c>KILL QUERY</c>。
/// </para>
/// <list type="table">
///   <item><term>元数据连接</term><description>对象树、表结构、补全数据源。会话级,串行访问。</description></item>
///   <item><term>探针连接</term><description>状态圆点 + <b>旁路取消通道</b>。会话级,低频。</description></item>
///   <item><term>查询连接</term><description>每个查询标签一根,由标签自己持有与释放。</description></item>
/// </list>
/// </summary>
internal sealed class SqlSession : IAsyncDisposable
{
    private readonly WorkspaceConnectRequest _request;
    private readonly Loc _loc;
    private readonly List<SqlConnection> _queryConnections = [];

    /// <summary>
    /// 按库缓存的元数据连接(PostgreSQL / SQL Server 用)。键是库名,大小写不敏感。
    /// <para>
    /// <b>为什么必须有这个池</b>:PG 与 SQL Server 的目录表是**每库一份**的
    /// (<see cref="Metadata.IDialectPack.MetadataSpansCatalogs" /> 上有真机数据)。
    /// 早先对象树拿会话上那条唯一的元数据连接去查每一个库,于是除了"连接串里那个库"之外
    /// <b>每个库展开都是空的</b> —— 用户看到的就是"pgsql 数据库连表都显示不出来"。
    /// </para>
    /// <para>
    /// <b>为什么是 <see cref="Lazy{T}" /> 而不是直接 <c>GetOrAdd</c> 一个 <see cref="Task" /></b>:
    /// <c>GetOrAdd</c> 的工厂在并发下**可能被调用多次**,输的那次会开出一条没人持有、
    /// 也没人关的连接。对象树的展开是即发即忘的、天然并发,这不是理论风险。
    /// <c>ExecutionAndPublication</c> 保证连接只开一条。
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<SqlConnection>>> _catalogConnections =
        new(StringComparer.OrdinalIgnoreCase);

    private SqlSession(
        SqlSettings settings,
        WorkspaceConnectRequest request,
        SqlConnection metadata,
        SqlConnection? probe,
        IDialectPack pack,
        Loc loc)
    {
        Settings = settings;
        _request = request;
        Metadata = metadata;
        Probe = probe;
        Pack = pack;
        _loc = loc;
    }

    /// <summary>连接设置。</summary>
    public SqlSettings Settings { get; }

    /// <summary>方言包。</summary>
    public IDialectPack Pack { get; }

    /// <summary>元数据连接。</summary>
    public SqlConnection Metadata { get; }

    /// <summary>
    /// 探针连接(兼旁路取消通道)。开不出来时为 <see langword="null" /> ——
    /// 那意味着**取消阶梯的第二档不可用**,界面要如实降级而不是假装能取消。
    /// </summary>
    public SqlConnection? Probe { get; }

    /// <summary>用户可见方言。</summary>
    public SqlDialect Dialect => Settings.Dialect;

    /// <summary>连接的展示名。</summary>
    public string DisplayName => _request.DisplayName;

    /// <summary>打开一条会话。</summary>
    /// <param name="request">连接请求。</param>
    /// <param name="dialect">方言。</param>
    /// <param name="loc">文案表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的会话。</returns>
    public static async Task<SqlSession> OpenAsync(
        WorkspaceConnectRequest request,
        SqlDialect dialect,
        Loc loc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(loc);

        SqlSettings settings = SqlSettings.From(request, dialect);
        SqlConnection metadata = await SqlConnection.ConnectAsync(
            settings, request.Host, request.Port, request.Username, request.Password, loc, null, cancellationToken)
            .ConfigureAwait(false);

        // 探针连接失败不该让整条会话失败 —— 用户要的是能查数据,状态圆点是锦上添花。
        // 但失败要记下来:取消阶梯会因此少一档。
        SqlConnection? probe = null;
        try
        {
            probe = await SqlConnection.ConnectAsync(
                settings, request.Host, request.Port, request.Username, request.Password, loc, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            probe = null;
        }

        return new(settings, request, metadata, probe, DialectPacks.For(dialect), loc);
    }

    /// <summary>连上时那条元数据连接实际落在哪个库(SQLite 上是文件)。</summary>
    public string DefaultCatalog => Metadata.Info.DatabaseName;

    /// <summary>
    /// 登录名。
    /// <para>
    /// 对象树用它来判「我现在在哪个 schema」——<b>Oracle 上 schema 就是 user</b>,
    /// 而 Oracle 没有"库"这一级,所以那棵树上唯一能标出"当前"的就是登录名对应的那个 schema。
    /// </para>
    /// <para>
    /// 只作显示用(加粗)。**真正的默认 schema 由服务端在每条元数据查询里自己回落**
    /// (各方言包的 <c>SchemaParam</c> / <c>OwnerParam</c>) —— 用户一句
    /// <c>ALTER SESSION SET CURRENT_SCHEMA</c> 就能把它改掉,客户端记的这个名字不作数。
    /// </para>
    /// </summary>
    public string LoginName => _request.Username;

    /// <summary>
    /// 取用来查 <paramref name="database" /> 这个库的元数据连接。
    /// <para>
    /// 目录表跨得过库的方言(MySQL / Oracle / SQLite)一律回落到会话那条元数据连接;
    /// 跨不过的(PG / SQL Server)按库懒开一条并缓存,见 <see cref="_catalogConnections" />。
    /// </para>
    /// <para>
    /// <b>连接本身用 <see cref="CancellationToken.None" /> 建立</b>,调用方的取消只作用在等待上:
    /// 缓存里那个 <see cref="Task" /> 是**所有人共享**的,让第一个调用方的取消把它变成
    /// canceled,后面每一个人都会拿到一条永远连不上的连接。取消该做的是"我不等了",
    /// 不是"谁都别想连"。
    /// </para>
    /// </summary>
    /// <param name="database">库名;空表示"用默认那条"。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该库的元数据连接。</returns>
    public async Task<SqlConnection> MetadataForAsync(string database, CancellationToken cancellationToken = default)
    {
        if (Pack.MetadataSpansCatalogs
            || string.IsNullOrEmpty(database)
            || string.Equals(database, DefaultCatalog, StringComparison.OrdinalIgnoreCase))
        {
            return Metadata;
        }

        Lazy<Task<SqlConnection>> entry = _catalogConnections.GetOrAdd(
            database,
            key => new(() => ConnectToAsync(key), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 连不上的库不能留在缓存里:那会让"起服务端之后按 F5 重试"永远失败。
            // 只清失败的那一条,而且要确认清掉的就是自己放进去的那一条(TryRemove 的 KeyValuePair 重载)。
            _catalogConnections.TryRemove(new KeyValuePair<string, Lazy<Task<SqlConnection>>>(database, entry));
            throw;
        }
    }

    /// <summary>
    /// 为一个查询标签开一根**独占**连接。
    /// <para>独占是取消的前提:取消要拿到那个 <c>DbCommand</c>,而共享连接上没法只取消其中一条。</para>
    /// </summary>
    /// <param name="database">
    /// 这个标签跑在哪个库上;空表示连接串里那个。
    /// <para>
    /// <b>这一格不能省。</b> 在树上双击 <c>ops_pg.public.orders</c> 时生成的是
    /// <c>SELECT * FROM "public"."orders"</c> —— 两段限定名在 PG 上无法表达"哪个库",
    /// 所以库这一级必须落在<b>连接</b>上。不传的话,查询会跑在连接串里那个库上,
    /// 结果是 42P01(关系不存在),而树上那张表明明就在那儿。
    /// </para>
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>查询连接。</returns>
    public async Task<SqlConnection> OpenQueryConnectionAsync(
        string database = "", CancellationToken cancellationToken = default)
    {
        SqlSettings settings = ResolveSettings(database);
        SqlConnection connection = await SqlConnection.ConnectAsync(
            settings, _request.Host, _request.Port, _request.Username, _request.Password, _loc, null, cancellationToken)
            .ConfigureAwait(false);
        lock (_queryConnections)
        {
            _queryConnections.Add(connection);
        }
        return connection;
    }

    private Task<SqlConnection> ConnectToAsync(string database) =>
        SqlConnection.ConnectAsync(
            ResolveSettings(database),
            _request.Host,
            _request.Port,
            _request.Username,
            _request.Password,
            _loc,
            null,
            CancellationToken.None);

    /// <summary>
    /// 把设置改写到指定的库上。
    /// <para>
    /// <b>只有"库"是一个可切换概念的方言才改写</b>:SQLite 的 <c>Database</c> 装的是**文件路径**,
    /// Oracle 的装的是服务名回落值 —— 往里塞一个库名会得到一条连不上、
    /// 而且错误信息完全指不到症结的连接串。<see cref="IDialectPack.HasDatabases" /> 正好是这条判据。
    /// </para>
    /// </summary>
    /// <param name="database">库名。</param>
    /// <returns>用于建连的设置。</returns>
    private SqlSettings ResolveSettings(string database) =>
        string.IsNullOrEmpty(database) || !Pack.HasDatabases || Pack.MetadataSpansCatalogs
            ? Settings
            : Settings with { Database = database };

    /// <summary>关掉一根查询连接(标签页关闭时)。</summary>
    /// <param name="connection">连接。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task CloseQueryConnectionAsync(SqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_queryConnections)
        {
            _queryConnections.Remove(connection);
        }
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 探针连接的底层对象(旁路取消要在它上面发语句)。
    /// </summary>
    public DbConnection? ProbeConnection => Probe?.Raw;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        SqlConnection[] queries;
        lock (_queryConnections)
        {
            queries = [.. _queryConnections];
            _queryConnections.Clear();
        }
        foreach (SqlConnection connection in queries)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        // 按库开出来的元数据连接也要关。已经失败的那些任务里没有连接可关,
        // 但**必须把异常吃掉** —— 否则一个连不上的库会让整个文档关不掉。
        foreach (Lazy<Task<SqlConnection>> entry in _catalogConnections.Values)
        {
            if (!entry.IsValueCreated)
            {
                continue;
            }
            try
            {
                SqlConnection catalog = await entry.Value.ConfigureAwait(false);
                await catalog.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // 这条库从来没连上过,或者关的时候出错了 —— 都不该挡住文档关闭。
            }
        }
        _catalogConnections.Clear();

        if (Probe is not null)
        {
            await Probe.DisposeAsync().ConfigureAwait(false);
        }
        await Metadata.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>方言包登记处。</summary>
internal static class DialectPacks
{
    private static readonly Dictionary<SqlDialect, IDialectPack> Packs = Build();

    /// <summary>取某方言的包。</summary>
    /// <param name="dialect">方言。</param>
    /// <returns>方言包。</returns>
    public static IDialectPack For(SqlDialect dialect) =>
        Packs.TryGetValue(dialect, out IDialectPack? pack)
            ? pack
            : throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "这个方言还没有方言包。");

    /// <summary>某方言有没有方言包(界面据此决定要不要画对象树)。</summary>
    /// <param name="dialect">方言。</param>
    /// <returns>有没有。</returns>
    public static bool Has(SqlDialect dialect) => Packs.ContainsKey(dialect);

    private static Dictionary<SqlDialect, IDialectPack> Build()
    {
        Dictionary<SqlDialect, IDialectPack> map = [];
        // 方言包是逐个落地的。这里用反射装配而不是写死 new,是为了让"还没写完的方言"
        // 在界面上如实表现为"没有对象树",而不是构造时就抛异常把整条会话打死。
        //
        // **每个包单独 try**:一个包出问题只该让它自己那个方言没有对象树,
        // 不该把别的方言一起拖下水 —— 这正是 SqlSugar 的 InstanceFactory 犯的错
        // (碰一次未内置方言,之后所有方言都装不出 provider,见设计文档 §3.3)。
        foreach (Type type in typeof(DialectPacks).Assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IDialectPack).IsAssignableFrom(type))
            {
                continue;
            }
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }
            try
            {
                if (Activator.CreateInstance(type) is IDialectPack pack)
                {
                    map[pack.Dialect] = pack;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // 装不出来就当这个方言没有包。
            }
        }
        return map;
    }
}
