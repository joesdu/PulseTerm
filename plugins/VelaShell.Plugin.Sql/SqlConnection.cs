using System.Data;
using System.Data.Common;
using System.Diagnostics;
using SqlSugar;

namespace VelaShell.Plugin.Sql;

/// <summary>连上之后拿到的服务端信息(标签页与状态栏用)。</summary>
/// <param name="ServerVersion">服务端版本原文。</param>
/// <param name="DatabaseName">当前库名(SQLite 上是文件名)。</param>
/// <param name="HandshakeMs">首次握手耗时(毫秒)。</param>
internal sealed record SqlServerInfo(string ServerVersion, string DatabaseName, long HandshakeMs);

/// <summary>
/// 一条数据库连接。**它显式持有连接对象**,这是三条实测结论的共同要求(§5.2):
/// 取消要拿到那个 <see cref="DbCommand" />;会话级 <c>SET</c> 不能丢;
/// 异常翻译要拿到原始驱动异常。
/// <para>
/// M0 只做"连得上、断得开、看得见状态"。查询执行、元数据、结果网格在 M1/M2 追加。
/// </para>
/// </summary>
internal sealed class SqlConnection : IAsyncDisposable
{
    private readonly SqlSugarClient _client;
    private readonly SqlSettings _settings;
    private readonly string _endpoint;
    private readonly Loc _loc;

    private SqlConnection(SqlSugarClient client, SqlSettings settings, string endpoint, SqlServerInfo info, Loc loc)
    {
        _client = client;
        _settings = settings;
        _endpoint = endpoint;
        _loc = loc;
        Info = info;
    }

    /// <summary>服务端信息。</summary>
    public SqlServerInfo Info { get; private set; }

    /// <summary>用户可见方言。</summary>
    public SqlDialect Dialect => _settings.Dialect;

    /// <summary>本连接的设置。</summary>
    public SqlSettings Settings => _settings;

    /// <summary>端点描述(<c>host:port</c>,SQLite 上是文件路径)。</summary>
    public string Endpoint => _endpoint;

    /// <summary>
    /// 底层连接对象。执行引擎与方言包都直接用它 —— 它们走裸 <c>DbCommand</c>,不经 SqlSugar
    /// (理由见 <see cref="Execution.SqlExecutor" /> 与 <see cref="Metadata.IDialectPack" />)。
    /// </summary>
    public DbConnection Raw => (DbConnection)_client.Ado.Connection;

    /// <summary>
    /// 这条连接的"一次只跑一条"闸门。
    /// <para>
    /// <b>不是防御性编程,是驱动的硬性约束。</b> Npgsql 与 MySqlConnector 在一条连接上
    /// 同时发两条命令会直接抛 —— PG 的原话是
    /// <c>A command is already in progress: …</c>;SQL Server 要显式开 MARS 才行。
    /// </para>
    /// <para>
    /// 而元数据连接<b>天然会被并发使用</b>:对象树的展开是即发即忘的
    /// (<c>IsExpanded</c> 的 setter 里 <c>_ = LoadAsync(...)</c>),用户快速点开两个节点、
    /// 或一次展开带出多个子节点,两次查询就落在同一条连接上了。
    /// 真机上表现为树里挂出一行 <c>A command is already in progress</c> ——
    /// 而它看起来像"这个库读不了",完全指不到症结。
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 排队用这条连接跑一段活。
    /// <para>
    /// 元数据查询<b>一律</b>走这里,而不是直接摸 <see cref="Raw" /> ——
    /// 摸 <see cref="Raw" /> 的那一刻就绕过了闸门。见 <see cref="_gate" /> 上的说明。
    /// </para>
    /// </summary>
    /// <typeparam name="T">结果类型。</typeparam>
    /// <param name="work">要跑的活。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结果。</returns>
    public async Task<T> UseAsync<T>(
        Func<DbConnection, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(Raw, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>排队用这条连接跑一段没有返回值的活。</summary>
    /// <param name="work">要跑的活。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public Task UseAsync(Func<DbConnection, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return UseAsync(async (connection, token) =>
        {
            await work(connection, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>这条连接在服务端的会话 id(旁路取消要用);还没取过时为空串。</summary>
    public string SessionId { get; internal set; } = "";

    /// <summary>
    /// 连上一台服务器。
    /// <para>
    /// <b>连接由这里自己 <c>Open()</c>,不交给 SqlSugar</b> —— 它在这一步会把驱动异常整个吞掉
    /// (抛 <c>SqlSugarException</c> 且 <c>InnerException</c> 为 null),认证失败的错误码只剩在
    /// 本地化文案里,MSSQL 侧连 18456 都没了。自己 Open 才拿得到 28P01 / 18456 / 1045 原样(§5.3)。
    /// </para>
    /// </summary>
    /// <param name="settings">设置。</param>
    /// <param name="host">主机(走隧道时已是本地端点);SQLite 上是文件路径。</param>
    /// <param name="port">端口。</param>
    /// <param name="username">用户名。</param>
    /// <param name="password">口令。</param>
    /// <param name="loc">文案表。</param>
    /// <param name="onSql">AOP 回显钩子。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的连接。</returns>
    public static async Task<SqlConnection> ConnectAsync(
        SqlSettings settings,
        string host,
        int port,
        string username,
        string password,
        Loc loc,
        Action<string, SugarParameter[]>? onSql,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(loc);

        string endpoint = settings.Info.IsFileBased ? host : $"{host}:{port}";
        string connectionString = SqlConnectionString.Build(settings, host, port, username, password);

        SqlSugarClient client;
        try
        {
            client = SqlSugarGate.Create(settings.Dialect, connectionString, settings.CommandTimeoutSeconds, onSql);
            // 取一次 DbMaintenance 把 provider 装出来 —— 未内置的方言在这一步就抛,
            // 而不是等到真去连服务器(§3.3 的惰性失败边界)。
            _ = client.DbMaintenance;
        }
        catch (Exception ex)
        {
            throw SqlExceptionTranslator.TranslateProviderMissing(ex, settings.Dialect, loc)
                  ?? SqlExceptionTranslator.Translate(ex, settings.Dialect, endpoint, loc);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var connection = (DbConnection)client.Ado.Connection;
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var info = new SqlServerInfo(
                SafeServerVersion(connection),
                string.IsNullOrWhiteSpace(connection.Database) ? settings.Database : connection.Database,
                stopwatch.ElapsedMilliseconds);
            return new(client, settings, endpoint, info, loc);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw SqlExceptionTranslator.Translate(ex, settings.Dialect, endpoint, loc);
        }
    }

    /// <summary>
    /// 探活。**不能看 <c>conn.State</c>** —— 实测它在 Npgsql 与 SqlClient 上都是过期信息
    /// (PG 被 terminate 之后仍显示 Open),必须真发一条语句(§5.2)。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>往返延迟(毫秒)。</returns>
    public async Task<int> PingAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var connection = (DbConnection)_client.Ado.Connection;
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = Dialect switch
        {
            SqlDialect.Oracle => "select 1 from dual",
            _ => "select 1"
        };
        command.CommandTimeout = _settings.CommandTimeoutSeconds;
        try
        {
            _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw SqlExceptionTranslator.Translate(ex, Dialect, _endpoint, _loc);
        }
        stopwatch.Stop();
        return (int)stopwatch.ElapsedMilliseconds;
    }

    /// <summary>重连(标签页上的"重连"按钮)。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task ReopenAsync(CancellationToken cancellationToken = default)
    {
        var connection = (DbConnection)_client.Ado.Connection;
        try
        {
            if (connection.State != ConnectionState.Closed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
            var stopwatch = Stopwatch.StartNew();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            Info = Info with { HandshakeMs = stopwatch.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            throw SqlExceptionTranslator.Translate(ex, Dialect, _endpoint, _loc);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            // 注意:这里 Dispose 的是一条**空闲**连接。绝不能对"有在途命令"的连接调 Dispose ——
            // 实测那会永久挂死调用线程(栈停在 Winsock recv),而且服务端照跑到底(§3.10)。
            // 取消一条正在跑的查询走的是另一条路:Cancel() → 旁路取消 → 放弃引用但不 Dispose。
            if (_client.Ado.Connection is DbConnection connection && connection.State != ConnectionState.Closed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 关连接失败不该挡住文档关闭。
        }
        _client.Dispose();
    }

    private static string SafeServerVersion(DbConnection connection)
    {
        try
        {
            return connection.ServerVersion ?? "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // SQLite 之外的驱动大多有;拿不到就空着,不要因为一句版本号让连接失败。
            return "";
        }
    }
}
