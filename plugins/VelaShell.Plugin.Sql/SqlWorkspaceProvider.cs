using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql;

/// <summary>
/// 数据库工作台的提供方,<b>五种方言共用这一个实例</b>。
/// <para>
/// 早先是"一种方言一个提供方",方言记在实例字段上;改成变体机制之后方言成了连接请求里的
/// 一个设置值,所以这里<b>不留任何方言状态</b> —— 每次连接都由 <see cref="DialectOf" /> 从请求现读。
/// 留一份实例状态的话,同一个提供方被两条不同方言的连接先后用到时,后一条会拿到前一条的方言。
/// </para>
/// <para>
/// 方言仍必须由插件自己记住、逐次传下去:SqlSugar 会<b>就地改写</b>
/// <c>ConnectionConfig.DbType</c>,回头再问它"这是什么库"已经不作数了(见 <see cref="SqlDialect" />)。
/// </para>
/// </summary>
/// <param name="context">插件上下文。</param>
/// <param name="loc">文案表(语言切换时由入口就地替换)。</param>
internal sealed class SqlWorkspaceProvider(IPluginContext context, Loc loc) : IWorkspaceProvider
{
    /// <summary>当前文案表。语言切换时由 <see cref="SqlPlugin" /> 就地替换。</summary>
    public Loc Loc { get; set; } = loc;

    /// <summary>
    /// 从连接请求里解析出这一条连接用的是哪个方言。
    /// <para>
    /// <b>方言现在是一个设置值,不再是"哪个工作台"。</b> 五个方言共用一个连接类型,
    /// 具体哪个由表单第一栏的下拉决定(宿主的变体机制)。
    /// </para>
    /// <para>
    /// 认不出就回落到默认方言而不是抛异常:这个值来自用户配置,
    /// 而配置可能是手改的、或来自一个更老/更新的版本。回落之后连接大概率会失败,
    /// 那时报的是一条**说得清的连接错误**,比"插件内部异常"对用户有用得多。
    /// </para>
    /// </summary>
    /// <param name="request">连接请求。</param>
    /// <returns>方言。</returns>
    public static SqlDialect DialectOf(WorkspaceConnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (SqlDialects.ByVariantValue(request.GetString(SqlDialects.DialectKey))
                ?? SqlDialects.Default).Dialect;
    }

    /// <inheritdoc />
    public async Task<IWorkspaceDocument> OpenAsync(
        WorkspaceConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        SqlDialect dialect = DialectOf(request);
        SqlSettings settings = SqlSettings.From(request, dialect);

        // PG 的顺序纪律自检(§3.8):违规不阻断连接 —— 它的后果只在写入路径上暴露,
        // 而"连不上"对用户更没用。但要在日志里说清楚,因为这类问题从现象上完全看不出根因。
        if (dialect == SqlDialect.PostgreSql && SqlSugarGate.CheckPostgresFirstTouch() is { } violation)
        {
            context.Log.Warn(violation);
        }

        SqlSession session = await SqlSession
            .OpenAsync(request, dialect, Loc, cancellationToken).ConfigureAwait(false);

        context.Log.Info(
            $"Connected to {SqlDialects.Of(dialect).DisplayName} at {session.Metadata.Endpoint} " +
            $"({session.Metadata.Info.ServerVersion}, {session.Metadata.Info.HandshakeMs} ms, env={settings.Environment}" +
            $"{(settings.ReadOnly ? ", read-only" : "")}).");

        return new SqlWorkspaceDocument(session, request, Loc, context);
    }
}
