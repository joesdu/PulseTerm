using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql;

/// <summary>
/// 数据库插件入口。把五种方言各注册成一种**工作台连接类型** ——
/// 于是它们在连接配置页里与 SSH/SFTP/FTP 平起平坐,而连接对话框、凭据加密落盘、
/// 登录弹窗、云同步、会话树与最近连接全部由宿主复用,插件一行都不写。
/// <para>
/// 惰性激活:清单里五个 <c>onWorkspace:</c> 事件,用户点到其中一个页签(或从最近连接
/// 打开一条数据库会话)才会装载本程序集与 SqlSugar —— 不碰数据库的用户,
/// 进程里一个字节的数据库驱动都不会出现。这一点对本插件尤其重要:
/// 它是插件生态里第一个带**原生依赖**的(SQLite 的 e_sqlite3、SqlClient 的 SNI)。
/// </para>
/// </summary>
[VelaPlugin]
public sealed class SqlPlugin : IVelaPlugin
{
    private readonly List<IDisposable> _registrations = [];
    private readonly List<SqlWorkspaceProvider> _providers = [];
    private IPluginContext? _context;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var loc = new Loc(context.Host.Locale);
        RegisterAll(context, loc);
        context.Log.Info($"SQL workspaces registered ({_providers.Count} dialects).");
        // 语言切换后重注册一次,让页签与表单标签跟着换 —— 描述是数据,重注册即替换。
        context.Events.LocaleChanged += OnLocaleChanged;
        return Task.CompletedTask;
    }

    private void RegisterAll(IPluginContext context, Loc loc)
    {
        var provider = new SqlWorkspaceProvider(context, loc);
        _providers.Add(provider);
        _registrations.Add(context.Workspaces.Register(Describe(loc), provider));
    }

    /// <summary>
    /// 描述「数据库」这一个连接类型。
    /// <para>
    /// <b>五个方言是同一个页签的五个<see cref="WorkspaceVariant">变体</see>,不是五个页签。</b>
    /// 初版是五个工作台,更直白,但它在连接类型那一排摆出五个几乎一模一样的页签
    /// (差别只有默认端口)。收成一个之后,原先靠"一个方言一个描述符"承载的三样东西
    /// —— 默认端口、"主机"那一栏的含义、要不要凭据 —— 改由变体承载。
    /// </para>
    /// <para>
    /// SQLite 那一条变体最能说明为什么需要这个机制:它<b>没有端点也没有凭据</b>,
    /// "主机"那一栏装的是文件路径。没有变体就只能让五个方言共用一套标签,
    /// 于是 SQLite 用户对着一个叫"主机"的框填文件路径、对着两个填了没用的凭据框发呆。
    /// </para>
    /// </summary>
    /// <param name="loc">文案表。</param>
    /// <returns>连接类型描述。</returns>
    internal static WorkspaceDescriptor Describe(Loc loc) =>
        new()
        {
            Id = SqlDialects.WorkspaceId,
            DisplayName = loc["Sql_WorkspaceName"],

            // 描述符自身取默认方言那一档;其余四种由变体覆盖。
            DefaultPort = SqlDialects.Default.DefaultPort,
            Fields = SqlSettings.Declare(loc),
            Features = WorkspaceFeatures.CertificateTrust | WorkspaceFeatures.SshTunnel,
            TrustedThumbprintSettingKey = SqlSettings.TrustedThumbprintKey,

            VariantKey = SqlDialects.DialectKey,
            Variants = [.. SqlDialects.All.Select(info => Variant(info, loc))]
        };

    /// <summary>把一种方言描述成一条变体。</summary>
    /// <param name="info">方言元信息。</param>
    /// <param name="loc">文案表。</param>
    /// <returns>变体。</returns>
    private static WorkspaceVariant Variant(SqlDialectInfo info, Loc loc) =>
        new()
        {
            Value = SqlDialects.VariantValue(info),
            DefaultPort = info.DefaultPort,

            // 文件型方言:"主机"那一栏装的是文件路径,而且没有端口、没有凭据。
            HostLabel = info.IsFileBased ? loc["Sql_SqliteFile"] : null,
            HostPlaceholder = info.IsFileBased ? loc["Sql_SqliteFilePlaceholder"] : null,

            // 文件型方言没有网络端点,也就没有隧道与证书信任可言;
            // NoCredentials 让宿主把用户名/口令两栏收起来 —— 填了也没有任何地方会用到。
            // NoEndpoint 同理收起端口那一栏:不收的话,从 PostgreSQL 切到 SQLite 之后
            // 端口框里还摆着 55432(实拍过),而 SqlConnection 拼串时对文件型方言压根不看端口。
            Features = info.IsFileBased
                ? WorkspaceFeatures.NoCredentials | WorkspaceFeatures.NoEndpoint
                : WorkspaceFeatures.CertificateTrust | WorkspaceFeatures.SshTunnel
        };

    private void OnLocaleChanged(string locale)
    {
        if (_context is not { } context)
        {
            return;
        }
        var loc = new Loc(context.Host.Locale);
        foreach (SqlWorkspaceProvider provider in _providers)
        {
            // 已打开的文档继续用它自己那份文案表(换语言不该让正在看的面板半中半英),
            // 新开的会话拿到新的。
            provider.Loc = loc;
        }
        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }
        _registrations.Clear();
        _providers.Clear();
        RegisterAll(context, loc);
    }

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        if (_context is { } context)
        {
            context.Events.LocaleChanged -= OnLocaleChanged;
        }
        foreach (IDisposable registration in _registrations)
        {
            registration.Dispose();
        }
        _registrations.Clear();
        _providers.Clear();
        return Task.CompletedTask;
    }
}
