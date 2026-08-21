using System.Data.Common;
using System.Reflection;

namespace VelaShell.Plugin.Sql.Execution;

/// <summary>取消走到了哪一档(界面据此换文案)。</summary>
internal enum SqlCancelStage
{
    /// <summary>还没开始。</summary>
    None,

    /// <summary>已调 <c>DbCommand.Cancel()</c>(SQLite 是 <c>sqlite3_interrupt</c>)。</summary>
    DriverCancel,

    /// <summary>已升级到旁路取消(另一根连接发 <c>pg_cancel_backend</c> / <c>KILL</c>)。</summary>
    Bypass,

    /// <summary>已放弃这根连接 —— <b>放弃 = 不再引用,不调 Dispose</b>。</summary>
    Abandoned
}

/// <summary>
/// 取消阶梯。**这是整份调研里最贵的一段结论**(设计文档 §3.10),原样落成代码:
/// <list type="number">
///   <item>先 <c>DbCommand.Cancel()</c>。PG/MSSQL/MySQL 实测都是带外取消,
///         客户端 0~39 ms 返回、服务端 32~233 ms 真停。SQLite 例外:
///         <c>SqliteCommand.Cancel()</c> 是**空方法体**,必须直调 <c>raw.sqlite3_interrupt</c>。</item>
///   <item><b>1.5 秒</b>(≈最坏观测 233 ms 的 6 倍,留足网络 RTT)没回来 → 升级到**旁路取消**:
///         用状态探针那根连接发 <c>pg_cancel_backend(pid)</c> / <c>KILL spid</c> /
///         <c>KILL QUERY id</c>。这是唯一能打断"已经交给同步 API 的查询"的手段。</item>
///   <item>再等 <b>2 秒</b>仍不回来 → **放弃这根连接**。</item>
/// </list>
/// <para>
/// <b>初版设计里那条"取消不成就 Dispose 连接"必须删掉,不是优化问题,是它会挂死界面线程。</b>
/// 实测:Dispose 一条有在途命令的连接,Dispose 自身 10 秒内不返回(栈停在 Winsock <c>recv</c>),
/// 而且服务端**根本没停**,一直跑到自然结束(PG 43.8 秒 / MSSQL 120 秒仍未返回)。
/// 所以"放弃"的定义是:不再引用它,让 GC 回收;池子会在连接对象被回收或服务端断开后自愈
/// (实测池子没被毒化,另取一根连接照常可用,PG 104 ms / MSSQL 62 ms)。
/// </para>
/// </summary>
internal sealed class SqlCancellation(SqlDialect dialect, Metadata.IDialectPack pack)
{
    /// <summary>驱动取消之后等多久升级到旁路取消。</summary>
    public static TimeSpan DriverCancelGrace { get; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>旁路取消之后等多久放弃连接。</summary>
    public static TimeSpan BypassGrace { get; } = TimeSpan.FromSeconds(2);

    /// <summary>当前走到了哪一档。</summary>
    public SqlCancelStage Stage { get; private set; }

    /// <summary>
    /// 走一遍阶梯。调用方在另一处 <c>await</c> 着那条查询;这里只负责"敲门",
    /// 敲到哪一档由 <paramref name="stillRunning" /> 决定。
    /// </summary>
    /// <param name="command">正在跑的命令(**必须是插件自己持有的那个**)。</param>
    /// <param name="probeConnection">状态探针那根独立连接(旁路取消用);没有就传 <see langword="null" />。</param>
    /// <param name="sessionId">被取消会话的 id;拿不到就传空。</param>
    /// <param name="stillRunning">查询是不是还在跑。</param>
    /// <param name="log">诊断输出。</param>
    /// <param name="cancellationToken">整体取消(文档关闭时用)。</param>
    /// <returns>最终走到的档位。</returns>
    public async Task<SqlCancelStage> EscalateAsync(
        DbCommand command,
        DbConnection? probeConnection,
        string sessionId,
        Func<bool> stillRunning,
        Action<string> log,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(stillRunning);
        ArgumentNullException.ThrowIfNull(log);

        // ── 第一档:驱动取消 ──
        Stage = SqlCancelStage.DriverCancel;
        try
        {
            if (dialect == SqlDialect.Sqlite)
            {
                InterruptSqlite(command.Connection, log);
            }
            else
            {
                command.Cancel();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"驱动取消失败:{ex.Message}");
        }

        if (await SettledAsync(stillRunning, DriverCancelGrace, cancellationToken).ConfigureAwait(false))
        {
            return Stage;
        }

        // ── 第二档:旁路取消 ──
        Stage = SqlCancelStage.Bypass;
        if (probeConnection is not null && !string.IsNullOrEmpty(sessionId)
            && pack.CancelSessionSql(sessionId) is { } cancelSql)
        {
            try
            {
                await using DbCommand bypass = probeConnection.CreateCommand();
                bypass.CommandText = cancelSql;
                bypass.CommandTimeout = 5;
                await bypass.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                log($"已发旁路取消:{cancelSql}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // MySQL 的取消要另开一条连接发 KILL QUERY —— 连接开不出来时它会**静默失败**
                // (实测 27~31 ms 返回、不抛异常、查询照跑到底)。所以这里的失败必须说出来。
                log($"旁路取消失败:{ex.Message}");
            }
        }
        else
        {
            log("没有可用的旁路取消通道(缺探针连接或会话 id)。");
        }

        if (await SettledAsync(stillRunning, BypassGrace, cancellationToken).ConfigureAwait(false))
        {
            return Stage;
        }

        // ── 第三档:放弃 ──
        Stage = SqlCancelStage.Abandoned;
        log("服务端未响应取消,已放弃该连接(不调 Dispose —— 那会挂死调用线程)。");
        return Stage;
    }

    /// <summary>
    /// SQLite 的取消:ADO.NET 门面上**根本没有这条路**。
    /// <c>SqliteCommand.Cancel()</c> 是空方法体,异步是同步套壳且令牌只在开跑前检查一次
    /// (实测 <c>Cancel()</c> 之后客户端跑满 144 秒)。真正管用的是直调
    /// <c>raw.sqlite3_interrupt(SqliteConnection.Handle)</c> —— 实测 20 ms 打断跑满 150 秒的递归 CTE,
    /// 而且打断后同一根连接立刻可复用。
    /// <para>用反射调是为了不在连别的库时把 SQLite 的类型拖进来。</para>
    /// </summary>
    private static void InterruptSqlite(DbConnection? connection, Action<string> log)
    {
        if (connection is null)
        {
            return;
        }
        try
        {
            object? handle = connection.GetType()
                .GetProperty("Handle", BindingFlags.Public | BindingFlags.Instance)?.GetValue(connection);
            if (handle is null)
            {
                log("拿不到 SqliteConnection.Handle,无法中断。");
                return;
            }
            Type? raw = handle.GetType().Assembly.GetType("SQLitePCL.raw")
                        ?? AppDomain.CurrentDomain.GetAssemblies()
                            .Select(a => a.GetType("SQLitePCL.raw"))
                            .FirstOrDefault(t => t is not null);
            MethodInfo? interrupt = raw?.GetMethod("sqlite3_interrupt", BindingFlags.Public | BindingFlags.Static);
            if (interrupt is null)
            {
                log("找不到 SQLitePCL.raw.sqlite3_interrupt。");
                return;
            }
            interrupt.Invoke(null, [handle]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            log($"sqlite3_interrupt 调用失败:{ex.Message}");
        }
    }

    /// <summary>在给定时间内轮询"是不是停了"。</summary>
    private static async Task<bool> SettledAsync(Func<bool> stillRunning, TimeSpan grace, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < deadline)
        {
            if (!stillRunning())
            {
                return true;
            }
            try
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return !stillRunning();
            }
        }
        return !stillRunning();
    }
}
