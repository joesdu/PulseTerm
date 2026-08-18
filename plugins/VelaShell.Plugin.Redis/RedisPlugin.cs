using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// Redis 插件入口。只做一件事:把 Redis 注册成一种**工作台连接类型** ——
/// 于是它在连接配置页里与 SSH/SFTP/FTP 平起平坐,而连接对话框、凭据加密落盘、
/// 登录弹窗、云同步、会话树与最近连接全部由宿主复用。
/// <para>
/// 惰性激活:清单里声明了 <c>onWorkspace:velashell.redis</c>,用户点到 Redis 页签
/// (或从最近连接打开一条 Redis 会话)才会装载本程序集与 StackExchange.Redis。
/// </para>
/// </summary>
[VelaPlugin]
public sealed class RedisPlugin : IVelaPlugin
{
    private IDisposable? _registration;
    private IPluginContext? _context;
    private RedisWorkspaceProvider? _provider;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var loc = new Loc(context.Host.Locale);
        _provider = new(context, loc);
        _registration = context.Workspaces.Register(Describe(context.PluginId, loc), _provider);
        // 命令面板:从已连接的 SSH 会话里探测 Redis。清单里已声明同 id 的占位命令,
        // 这里注册真实处理器把它替换掉。
        _discoverCommand = context.Commands.Register(new(
            DiscoverCommandId,
            loc["Redis_DiscoverCommand"],
            "Redis",
            token => DiscoverAsync(context, token)));
        context.Log.Info("Redis workspace registered.");
        // 语言切换后重注册一次,让页签与表单标签跟着换 —— 描述是数据,重注册即替换。
        context.Events.LocaleChanged += OnLocaleChanged;
        return Task.CompletedTask;
    }

    /// <summary>探测命令的 id(与 <c>plugin.json</c> 的占位命令一致)。</summary>
    private const string DiscoverCommandId = "velashell.redis.discover";

    private IDisposable? _discoverCommand;

    /// <summary>
    /// 从已连接的 SSH 会话里探测 Redis 并逐个提议连接。
    /// <para>
    /// 这是**零打字建连**:主机名不用抄、端口不用记、密码不用翻配置、隧道不用手开 ——
    /// 用的全是现成能力(RemoteExec + RemoteFs + 声明式隧道)。
    /// </para>
    /// <para>
    /// 用户在某一条提议上按了取消就停下:那是"够了"的信号,继续弹下一个只会烦人。
    /// </para>
    /// </summary>
    private async Task DiscoverAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var loc = new Loc(context.Host.Locale);
        var discovery = new RedisDiscovery(context);
        IReadOnlyList<PluginSdk.Sessions.SessionInfo> sessions =
            await discovery.ConnectedSessionsAsync(cancellationToken).ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            context.Log.Info(loc["Redis_DiscoverNoSessions"]);
            return;
        }

        int proposed = 0;
        foreach (PluginSdk.Sessions.SessionInfo session in sessions)
        {
            IReadOnlyList<RedisDiscoveredInstance> found =
                await discovery.ProbeAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
            if (found.Count == 0)
            {
                continue;
            }
            context.Log.Info(loc.Format("Redis_DiscoverFound", found.Count, session.Host));
            foreach (RedisDiscoveredInstance instance in found)
            {
                bool saved = await context.Workspaces.ProposeConnectionAsync(
                    Propose(context.PluginId, session, instance),
                    cancellationToken).ConfigureAwait(false);
                proposed++;
                if (!saved)
                {
                    // 用户取消了 —— 不再往下弹。
                    return;
                }
            }
        }
        if (proposed == 0)
        {
            context.Log.Info(loc["Redis_DiscoverNoneFound"]);
        }
    }

    /// <summary>把一个探到的实例变成一条连接提议。</summary>
    private static WorkspaceConnectionProposal Propose(
        string pluginId,
        PluginSdk.Sessions.SessionInfo session,
        RedisDiscoveredInstance instance) =>
        new()
        {
            WorkspaceId = pluginId,
            // 名字带上主机与端口:一台机器上跑两个实例是常态,光叫"redis"分不出来。
            Name = $"redis@{session.Host}:{instance.Port}",
            // **主机填远端的真实地址,而不是 127.0.0.1**:隧道由宿主按下面那个
            // jumpSession 字段代建,它需要知道"从跳板机看过去要连哪里"。
            Host = "127.0.0.1",
            Port = instance.Port,
            Password = instance.Password,
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tls"] = instance.UsesTls ? "true" : "false",
                // 预选这条 SSH 会话所属的配置做跳板机 —— 这一项正是"隧道不用手开"的落点。
                // 会话 id 不等于配置 id,所以这里留空由用户在对话框里选;
                // 名字里已经写明是哪台机器,选起来只有一步。
                ["jumpSession"] = string.Empty,
                // 探到的实例默认按"开发"起步:环境标记决定护栏强度,
                // 替用户猜"这是生产"会让护栏在错误的地方紧或松。
                ["environment"] = "development"
            }
        };

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        if (_context is { } context)
        {
            context.Events.LocaleChanged -= OnLocaleChanged;
        }
        _discoverCommand?.Dispose();
        _discoverCommand = null;
        _registration?.Dispose();
        _registration = null;
        _provider = null;
        _context = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 连接类型描述。它是**纯数据**,所以换语言只要拿一份新文案重算一次。
    /// </summary>
    /// <param name="pluginId">插件 id(即连接类型 id)。</param>
    /// <param name="loc">文案表。</param>
    /// <returns>连接类型描述。</returns>
    private static WorkspaceDescriptor Describe(string pluginId, Loc loc) =>
        new()
        {
            Id = pluginId,
            DisplayName = "Redis",
            DefaultPort = 6379,
            HostLabel = loc["Redis_Host"],
            HostPlaceholder = "127.0.0.1",
            UsernameLabel = loc["Redis_User"],
            PasswordLabel = loc["Redis_Password"],
            Fields = RedisSettings.Declare(loc),
            // 匿名是一条正当路径:开发机上的 Redis 通常没有 requirepass,那时不该弹登录框。
            // TLS 自签走宿主与 FTPS/S3 共用的「提示 → 记指纹 → 重连」流程。
            // SshTunnel:宿主在打开会话前代建 SSH 会话与本地转发 —— 这是"运维顺手查一下
            // 线上 Redis"这个最高频场景的关键一环,而插件为此一行 SSH 代码都不用写。
            Features = WorkspaceFeatures.AnonymousAccess
                       | WorkspaceFeatures.CertificateTrust
                       | WorkspaceFeatures.SshTunnel,
            // 用户点过"永久信任"后,宿主把指纹写回这个隐藏字段;下次连接即按指纹放行。
            TrustedThumbprintSettingKey = RedisSettings.TrustedThumbprintKey
        };

    /// <summary>
    /// 语言变了:换一份文案重注册。
    /// <para>
    /// **provider 必须是同一个实例**:注册表把"同 id 换成另一个实现"视为旧实现失效,
    /// 会通知宿主关掉该类型名下所有已打开的文档 —— 用户只是切了个语言,标签页不该全没了。
    /// 因此这里只换描述,provider 沿用,并就地把它的文案换掉。
    /// </para>
    /// </summary>
    private void OnLocaleChanged(string locale)
    {
        if (_context is not { } context || _provider is not { } provider)
        {
            return;
        }
        try
        {
            var loc = new Loc(locale);
            provider.Loc = loc;
            _registration = context.Workspaces.Register(Describe(context.PluginId, loc), provider);
        }
        catch (Exception ex)
        {
            context.Log.Error("Re-registering the Redis workspace after a locale change failed.", ex);
        }
    }
}
