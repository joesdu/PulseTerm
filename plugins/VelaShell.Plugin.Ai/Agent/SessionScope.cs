using System.Text.Json.Serialization;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Ai.Agent;

/// <summary>范围的形状。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeKind
{
    /// <summary>
    /// 不限范围。<b>这是缺省值,而且是刻意的</b> ——
    /// 反序列化一份没有这个字段的旧配置会落在这里,于是升级不改变任何人当前的行为。
    /// </summary>
    All,

    /// <summary>只限于 <see cref="SessionScope.Groups" /> 与 <see cref="SessionScope.SavedIds" /> 的并集。</summary>
    Limited
}

/// <summary>
/// 一份授权能碰哪些机器。
/// </summary>
/// <remarks>
/// <para>
/// <b>范围是"这个房间"的属性,不是"这个人"的属性。</b>群要收紧是因为群里的人数会在你不知情时
/// 增长;单聊、聊天面板、本机 MCP 都不存在这个问题,所以它们默认 <see cref="ScopeKind.All" />。
/// 一套把作者自己也拦住的权限设计,结局是被整个关掉,回到零防护。
/// </para>
/// <para>
/// <b><see cref="ScopeKind.Limited" /> 且两个列表都空 = 一台都不给。</b>空列表最自然的读法是
/// "什么都没选",而权限的默认值一旦读错方向,错的方向是放开。要不限范围就明写
/// <see cref="ScopeKind.All" /> —— 它不是一个"放行全部"的过滤器,而是<b>压根没有过滤器</b>
/// (见 <see cref="Resolve" />)。
/// </para>
/// </remarks>
public sealed class SessionScope
{
    /// <summary>形状。</summary>
    public ScopeKind Kind { get; set; } = ScopeKind.All;

    /// <summary>允许的分组名(会话树上那些分组的名字,大小写不敏感)。</summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>额外单独放行的已保存会话 id(<c>SavedSessionInfo.SavedSessionId</c>)。</summary>
    public List<string> SavedIds { get; set; } = [];

    /// <summary>不限范围?</summary>
    [JsonIgnore]
    public bool IsUnrestricted => Kind == ScopeKind.All;

    /// <summary>
    /// 把这份配置变成运行期的闸门。<b>不限范围时返回 <see langword="null" /></b> ——
    /// 于是"全部"与"没有范围这回事"走的是同一条代码路径,不存在一个可能写错的放行分支。
    /// </summary>
    public ISessionScope? Resolve(IPluginContext context)
        => IsUnrestricted ? null : new SavedSessionScope(context, this);

    /// <summary>拷一份(设置页编辑时不该改到正在生效的那一份)。</summary>
    public SessionScope Clone() => new() { Kind = Kind, Groups = [.. Groups], SavedIds = [.. SavedIds] };
}

/// <summary>
/// 运行期的会话闸门:一台机器在不在这次授权的范围里。
/// </summary>
/// <remarks>
/// <b>它是工具箱上的一个钩子,不是提示词里的一句话。</b>写进系统提示的"你只能碰生产组"
/// 是一条建议,模型可以忽略、可以被用户的下一句话劝服;写在这里的是每次工具调用都要过的闸。
/// </remarks>
public interface ISessionScope
{
    /// <summary>这条活着的会话在范围内吗?</summary>
    Task<bool> AllowsLiveAsync(SessionInfo session, CancellationToken cancellationToken);

    /// <summary>这条已保存的配置在范围内吗?</summary>
    Task<bool> AllowsSavedAsync(SavedSessionInfo saved, CancellationToken cancellationToken);

    /// <summary>范围的一句人话描述(给 <c>/status</c> 用)。</summary>
    string Describe();
}

/// <summary>
/// 按"已保存的连接配置"判定范围:分组名 + 单独放行的配置 id。
/// </summary>
/// <remarks>
/// <para>
/// <b>活会话怎么判。</b>会话本身不带分组(<c>SessionInfo</c> 只有主机、端口、用户名),
/// 所以要先把它映射回一条已保存的配置,再看那条配置在不在范围里:
/// 对上了且在范围内 → 放行;对上了但不在范围内 → 拒绝;
/// <b>一条都对不上</b>(用户在终端里手敲 <c>ssh</c> 连出去的临时会话)→ <b>也拒绝</b>。
/// </para>
/// <para>
/// 最后那一档是<b>失败关闭</b>,也是这套设计里最容易写反的一处:一条不在会话树里的机器,
/// 恰恰是没人替它定过范围的那种,把它当成"不受管辖所以放行",等于给任何手敲的连接开了后门。
/// 代价是受限的群碰不到临时会话 —— 这是对的,而你自己那条路本来就不受限
/// (见 <see cref="SessionScope" />)。
/// </para>
/// <para>
/// 已保存列表缓存几秒:一轮里工具会被调很多次,每次都去问宿主既慢又没必要;
/// 而用户刚在会话树里挪了一台机器,几秒之内生效也够快了。
/// </para>
/// </remarks>
public sealed class SavedSessionScope(IPluginContext context, SessionScope scope) : ISessionScope
{
    private static readonly TimeSpan CacheLife = TimeSpan.FromSeconds(5);

    private readonly HashSet<string> _groups = new(scope.Groups, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _savedIds = new(scope.SavedIds, StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private IReadOnlyList<SavedSessionInfo>? _cached;
    private DateTimeOffset _cachedAt;

    /// <inheritdoc />
    public async Task<bool> AllowsLiveAsync(SessionInfo session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        IReadOnlyList<SavedSessionInfo> saved = await SavedAsync(cancellationToken).ConfigureAwait(false);
        // 对不上任何一条已保存配置时循环自然走完 → false,即失败关闭(理由见类型注释)
        return saved.Any(candidate => Matches(candidate, session) && Allows(candidate));
    }

    /// <inheritdoc />
    public Task<bool> AllowsSavedAsync(SavedSessionInfo saved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return Task.FromResult(Allows(saved));
    }

    /// <inheritdoc />
    public string Describe()
    {
        List<string> parts = [];
        if (scope.Groups.Count > 0)
        {
            parts.Add(string.Join(", ", scope.Groups));
        }
        if (_savedIds.Count > 0)
        {
            parts.Add($"+{_savedIds.Count}");
        }
        return parts.Count == 0 ? "—" : string.Join(" ", parts);
    }

    /// <summary>一条活会话与一条已保存配置是不是同一台机器。</summary>
    /// <remarks>
    /// 与 <c>AgentToolbox</c> 标注"这条配置已经连着了"用的是同一套规则:主机不分大小写、
    /// 端口必须相等、已保存配置没填用户名(留到连接时再问)就不比用户名。
    /// </remarks>
    internal static bool Matches(SavedSessionInfo saved, SessionInfo session)
        => string.Equals(saved.Host, session.Host, StringComparison.OrdinalIgnoreCase)
           && saved.Port == session.Port
           && (saved.Username.Length == 0 || string.Equals(saved.Username, session.Username, StringComparison.Ordinal));

    private bool Allows(SavedSessionInfo saved)
        => _savedIds.Contains(saved.SavedSessionId)
           || (saved.Group is { Length: > 0 } group && _groups.Contains(group));

    private async Task<IReadOnlyList<SavedSessionInfo>> SavedAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_cached is { } fresh && DateTimeOffset.UtcNow - _cachedAt < CacheLife)
            {
                return fresh;
            }
        }
        IReadOnlyList<SavedSessionInfo> loaded =
            await context.Sessions.ListSavedAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _cached = loaded;
            _cachedAt = DateTimeOffset.UtcNow;
        }
        return loaded;
    }
}

/// <summary>
/// 按 <c>user@host:port</c> 清单判定范围(对外 MCP 设置页上那个"允许操作的服务器")。
/// </summary>
/// <remarks>
/// <b>这一条原来是句空话。</b>它只挡 <c>use_session</c>,而工具箱里九个工具都收可选的
/// <c>session_id</c> —— 外部 agent 只要 <c>list_sessions</c> 拿到 id 直接传,清单形同虚设。
/// 做成 <see cref="ISessionScope" /> 之后它作用在每一次工具调用上,界面上写的那句话才是真的。
/// <para>
/// 清单为空仍然是"允许全部",默认值不动:MCP 的边界是回环地址 + 令牌 + 只读挡位,
/// 把用户自己机器上的 agent 一起收紧挡不住任何攻击者,只挡得住用户自己。
/// </para>
/// </remarks>
public sealed class TargetListScope : ISessionScope
{
    private readonly List<string> _targets;

    /// <param name="targets">每行一条 <c>user@host:port</c>;空行忽略。</param>
    public TargetListScope(IEnumerable<string> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _targets = [.. targets.Select(t => t.Trim()).Where(t => t.Length > 0)];
    }

    /// <summary>清单为空(= 不限范围,此时根本不该建这个对象)。</summary>
    public bool IsEmpty => _targets.Count == 0;

    /// <inheritdoc />
    public Task<bool> AllowsLiveAsync(SessionInfo session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Task.FromResult(Contains($"{session.Username}@{session.Host}:{session.Port}"));
    }

    /// <inheritdoc />
    public Task<bool> AllowsSavedAsync(SavedSessionInfo saved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return Task.FromResult(Contains($"{saved.Username}@{saved.Host}:{saved.Port}"));
    }

    /// <inheritdoc />
    public string Describe() => _targets.Count == 0 ? "—" : string.Join(", ", _targets);

    private bool Contains(string target)
        => _targets.Any(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase));
}
