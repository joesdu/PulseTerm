using System.Security.Cryptography;
using System.Text.Json;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Interop;

/// <summary>
/// 对外 MCP 服务端的设置:让 Claude Code / Codex / Cursor 这类外部 agent
/// 反过来调用 VelaShell 的能力(枚举会话、读终端、跑命令、读写远端文件)。
/// </summary>
/// <remarks>
/// <b>方向要分清。</b>插件本来就是 MCP <i>客户端</i>(见 <c>Agent/McpManager</c>) ——
/// VelaShell 的 agent 去调别人的工具。这里是反过来:VelaShell 当<i>服务端</i>,
/// 别人的 agent 来调 VelaShell 的工具。两套东西没有共用代码,只共用一个名字。
/// </remarks>
public sealed class McpServerSettings
{
    /// <summary>
    /// 总开关。关着时不监听任何端口。<b>默认开</b>。
    /// </summary>
    /// <remarks>
    /// 默认打开一个监听端口需要有理由,这里的理由是三条叠起来之后风险足够低,
    /// 而"默认关"带来的代价(装完还得先去翻一页设置)恰恰是这个功能最没必要的门槛:
    /// <list type="number">
    /// <item>只绑 <c>127.0.0.1</c>,不提供绑其它地址的选项;</item>
    /// <item>每个请求都必须带令牌,而令牌是随机生成、不可关闭的;</item>
    /// <item>默认挡位是 <see cref="ChatMode.Plan" /> —— 外部 agent 开箱只能<b>看</b>,
    /// 改任何东西都要用户显式把挡位或审批方式调开。</item>
    /// </list>
    /// 换句话说:默认开的是一个"只读、要令牌、只在本机"的接口。
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>监听端口(只绑 127.0.0.1)。</summary>
    public int Port { get; set; } = 8391;

    /// <summary>
    /// 暴露哪一档工具。<b>默认计划档 = 只读</b> ——
    /// 外部 agent 的行为不在用户眼皮底下,默认不该能改任何东西。
    /// </summary>
    public ChatMode Mode { get; set; } = ChatMode.Plan;

    /// <summary>
    /// 写操作的审批方式。<see cref="ApprovalMode.Ask" /> 在这条路上<b>等于拒绝</b> ——
    /// 外部 agent 没有可以弹审批卡的界面(详见 <see cref="McpToolHost" />)。
    /// </summary>
    public ApprovalMode Approval { get; set; } = ApprovalMode.ReadOnlyAuto;

    /// <summary>不暴露给外部 agent 的工具名,每行一条。</summary>
    public string DisabledTools { get; set; } = "";

    /// <summary>
    /// 外部 agent 能操作哪些机器。<b><see langword="null" /> = 这份配置还没迁移过</b>
    /// (见 <see cref="NormalizeScope" />)。
    /// </summary>
    /// <remarks>
    /// <b>它作用在每一次工具调用上</b>,不只是 <c>use_session</c> ——
    /// 工具箱里九个工具都收可选的 <c>session_id</c>,只挡"选哪台"等于没挡
    /// (见 <see cref="McpToolHost" />)。
    /// <para>
    /// 与 IM 授权用的是同一个 <see cref="SessionScope" />:范围记的是会话树里那些<b>已保存配置的
    /// id 与分组名</b>,界面上勾的是它们的名字。名字会改、会重名,而 id 不变;活会话身上更是压根
    /// 没有名字(<c>SessionInfo</c> 只有主机、端口、用户名),所以"按名字比对"这件事本身不成立 ——
    /// 名字只是标签,判定仍旧落在<b>把活会话映射回已保存配置</b>那一步(见 <c>SavedSessionScope</c>)。
    /// </para>
    /// <para>
    /// <b>默认仍是 <see cref="ScopeKind.All" /> = 允许全部</b>,与 IM 那边"空 = 一个都不放行"
    /// 的方向刻意相反:这条路的边界是回环地址 + 令牌 + 只读挡位,顺手把用户自己机器上的
    /// Claude Code / Codex 一起收紧,挡不住任何攻击者,只挡得住用户自己。
    /// </para>
    /// </remarks>
    public SessionScope? Scope { get; set; }

    /// <summary>
    /// 旧版那份 <c>user@host:port</c> 清单。<b>已经不再是判定依据</b>,只剩两个用处:
    /// 折算成 <see cref="Scope" />,以及万一用户换回旧版本时清单还在。
    /// </summary>
    /// <remarks>
    /// 与 <c>ChannelConfig.AllowedChats</c> 对 <c>Grants</c> 是同一套关系:折算完之后它变成
    /// <see cref="Scope" /> 的派生镜像,由 <see cref="NormalizeScope" /> 重算,不再单独维护。
    /// </remarks>
    public string AllowedTargets { get; set; } = "";

    /// <summary>
    /// 把旧清单折算成 <see cref="Scope" />,并把镜像重新算一遍。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>没有这个字段的旧配置落在哪一边,是这次改动唯一会改到别人行为的地方。</b>
    /// 清单空着 → <see cref="ScopeKind.All" />,与升级前逐字相同;清单非空 → 逐行去会话树里找
    /// 对得上的配置,勾出它们。<b>一行都对不上时结果是"受限且一台都没勾"</b>,也就是一台都不给 ——
    /// 而不是回到"允许全部"。权限的默认值一旦读错方向,错的方向是放开;何况这种配置本来就只
    /// 放行了几台不在会话树里的机器,把它读成"全都行"是凭空多给。界面上那句"一个都没勾"会
    /// 直接说出这件事。
    /// </para>
    /// <para>
    /// 代价说清楚:用户在终端里手敲 <c>ssh</c> 连出去、从没存进会话树的那种临时会话,
    /// 从此在受限模式下碰不到 —— 这与 IM 授权那边是同一条失败关闭的规矩(见 <c>SavedSessionScope</c>)。
    /// 不限范围时不受影响。
    /// </para>
    /// <para>
    /// 读和写两头都要调:读的时候把老配置补上,写的时候把镜像刷新 ——
    /// 一个派生字段只要有一头没算,它就会开始撒谎。
    /// </para>
    /// </remarks>
    /// <param name="saved">会话树里的已保存配置。</param>
    public void NormalizeScope(IReadOnlyList<SavedSessionInfo> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        string[] legacy = AllowedTargets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Scope ??= legacy.Length == 0
            ? new SessionScope()
            : new SessionScope
            {
                Kind = ScopeKind.Limited,
                SavedIds =
                [
                    .. saved.Where(s => legacy.Any(line => SessionTargets.Matches(s, line)))
                            .Select(s => s.SavedSessionId)
                            .Distinct(StringComparer.Ordinal)
                ]
            };
        // 镜像:不限范围写成空(旧版本读到空 = 允许全部,行为一致),受限就把勾中的那几台写回去
        AllowedTargets = Scope.IsUnrestricted
            ? ""
            : string.Join('\n', saved.Where(Scope.Allows).Select(s => $"{s.Username}@{s.Host}:{s.Port}"));
    }

    /// <summary>
    /// 把这份设置变成运行期的闸门。
    /// </summary>
    /// <remarks>
    /// <see cref="Scope" /> 还没迁移过(取已保存配置失败之类)时退回旧的
    /// <see cref="TargetListScope" /> —— 那正好是升级前的行为,既不多给也不少给。
    /// 退回成"不限范围"是不行的:一次读取失败不该把用户配好的名单悄悄拆掉。
    /// </remarks>
    /// <param name="context">插件上下文。</param>
    public ISessionScope? ResolveScope(IPluginContext context)
    {
        if (Scope is { } scope)
        {
            return scope.Resolve(context);
        }
        var legacy = new TargetListScope(
            AllowedTargets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return legacy.IsEmpty ? null : legacy;
    }
}

/// <summary>MCP 服务端设置的读写。令牌走机密存储,不进明文配置。</summary>
public sealed class McpServerSettingsStore(IPluginContext context)
{
    private const string SettingsKey = "mcp-server";
    private const string TokenSecret = "mcp-server:token";

    /// <summary>读取设置(没有则返回默认值),顺带把旧的 <c>user@host:port</c> 清单折算成范围。</summary>
    public async Task<McpServerSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        JsonElement raw = await context.Storage.GetAsync<JsonElement>(SettingsKey, cancellationToken).ConfigureAwait(false);
        McpServerSettings settings = raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new McpServerSettings()
            : raw.Deserialize<McpServerSettings>() ?? new McpServerSettings();
        await NormalizeAsync(settings, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    /// <summary>持久化设置(写之前把镜像刷新)。</summary>
    public async Task SaveAsync(McpServerSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await NormalizeAsync(settings, cancellationToken).ConfigureAwait(false);
        await context.Storage.SetAsync(SettingsKey, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 折算 + 重算镜像。<b>取不到会话树就什么都不做</b>,让
    /// <see cref="McpServerSettings.ResolveScope" /> 退回旧清单 —— 一次读取失败不该把
    /// 用户配好的名单悄悄拆掉,更不该把它读成"允许全部"。
    /// </summary>
    private async Task NormalizeAsync(McpServerSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            settings.NormalizeScope(await context.Sessions.ListSavedAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Log.Warn($"The MCP server scope could not be reconciled with the session tree: {ex.Message}");
        }
    }

    /// <summary>
    /// 取访问令牌;<b>没有就现生成一个并存下</b>。
    /// </summary>
    /// <remarks>
    /// 监听在 127.0.0.1 上并不等于安全:同一台机器上任何进程(包括浏览器里的页面)
    /// 都能往本地端口发请求。令牌是这条路上唯一挡住"别的程序顺手调你的服务器"的东西,
    /// 所以它不是可选项,也不该让用户自己想一个。
    /// </remarks>
    public async Task<string> TokenAsync(CancellationToken cancellationToken = default)
    {
        if (await context.Secrets.GetAsync(TokenSecret, cancellationToken).ConfigureAwait(false) is { Length: > 0 } existing)
        {
            return existing;
        }
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await context.Secrets.SetAsync(TokenSecret, token, cancellationToken).ConfigureAwait(false);
        return token;
    }

    /// <summary>换一个新令牌(设置页上的"重新生成")。</summary>
    public async Task<string> RotateTokenAsync(CancellationToken cancellationToken = default)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await context.Secrets.SetAsync(TokenSecret, token, cancellationToken).ConfigureAwait(false);
        return token;
    }
}
