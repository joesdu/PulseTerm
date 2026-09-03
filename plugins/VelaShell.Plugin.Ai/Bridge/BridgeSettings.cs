using System.Text.Json;
using System.Text.Json.Serialization;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>桥接支持的 IM 平台。</summary>
/// <remarks>
/// 枚举值决定的是<b>入站传输</b>怎么走,这是各家真正的差别所在:
/// 前三种都能从内网主动连出去(长连接 / 长轮询),桌面端开箱即用;
/// <see cref="WeCom" /> 只有"平台回调到你的地址"一条路,必须有公网可达的入口。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelKind
{
    /// <summary>飞书 / Lark:官方长连接(WebSocket + pbbp2 帧),不需要公网地址。</summary>
    Feishu,

    /// <summary>钉钉:Stream 模式(WebSocket + JSON 帧),不需要公网地址。</summary>
    DingTalk,

    /// <summary>Telegram:Bot API 长轮询(getUpdates),不需要公网地址。</summary>
    Telegram,

    /// <summary>企业微信:只有回调 URL 一条路,需要公网可达的入口(见 WeCom 渠道的说明)。</summary>
    WeCom
}

/// <summary>
/// 一份给某个聊天的授权:能碰哪些机器、在哪一档挡位上、写操作怎么审批。
/// </summary>
/// <remarks>
/// <para>
/// <b>范围是"这个房间"的属性,不是"这个人"的属性。</b>把机器人拉进一个群,群里的人数
/// 之后还会增长,而你不会每次都收到通知 —— 所以群要收紧。单聊只有一个对端、还是你逐个
/// 放行的,它的默认值是不限范围(<see cref="ScopeKind.All" />);聊天面板压根不经过这里。
/// </para>
/// <para>
/// <b>挡位与审批也下放到这一层</b>,不然"只读群"和"运维群"没法共存 ——
/// 那几乎是提出范围需求之后的下一个需求。<see langword="null" /> = 跟随
/// <see cref="BridgeSettings.Mode" /> / <see cref="BridgeSettings.Approval" /> 的全局值,
/// 也是升级后既有聊天的取值,所以行为逐字不变。
/// </para>
/// </remarks>
public sealed class ChatGrant
{
    /// <summary>被授权的聊天 id(群 id 或单聊会话 id)。</summary>
    public string ChatId { get; set; } = "";

    /// <summary>是群聊(设置页据此提示"群里的人数会变")。</summary>
    public bool IsGroup { get; set; }

    /// <summary>显示名(配对时记下的,纯粹给设置页看;认不出来就留空)。</summary>
    public string Label { get; set; } = "";

    /// <summary>能碰哪些机器。</summary>
    public SessionScope Scope { get; set; } = new();

    /// <summary>这个聊天的挡位(<see langword="null" /> = 跟随全局)。</summary>
    public ChatMode? Mode { get; set; }

    /// <summary>这个聊天的审批方式(<see langword="null" /> = 跟随全局)。</summary>
    public ApprovalMode? Approval { get; set; }

    /// <summary>拷一份(设置页编辑时不该改到正在生效的那一份)。</summary>
    public ChatGrant Clone() => new()
    {
        ChatId = ChatId,
        IsGroup = IsGroup,
        Label = Label,
        Scope = Scope.Clone(),
        Mode = Mode,
        Approval = Approval
    };
}

/// <summary>一个已配置的渠道。</summary>
/// <remarks>
/// <b>机密不在这里。</b>应用密钥(飞书 app_secret / 钉钉 clientSecret / Telegram bot token /
/// 企微 secret 与 EncodingAESKey)一律走 <see cref="VelaShell.PluginSdk.Secrets.ISecretsApi" />,
/// 键名见 <see cref="BridgeSettingsStore.SecretName" /> —— 这份配置是明文 JSON,躺在插件存储里。
/// </remarks>
public sealed class ChannelConfig
{
    /// <summary>渠道实例 id(机密键与会话键的命名空间;生成后不再变)。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>平台。</summary>
    public ChannelKind Kind { get; set; }

    /// <summary>是否启用(关掉即不建连,配置保留)。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>显示名(设置页与日志里用;空则回落成平台名)。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>飞书 app_id / 钉钉 clientId / 企微 corpid。Telegram 不用(令牌本身就是身份)。</summary>
    public string AppId { get; set; } = "";

    /// <summary>企业微信自建应用的 agentid。其它平台留空。</summary>
    public string AgentId { get; set; } = "";

    /// <summary>飞书:用国际版(larksuite.com)而不是国内版(feishu.cn)。</summary>
    public bool International { get; set; }

    /// <summary>企业微信回调:本机监听端口。</summary>
    public int WebhookPort { get; set; } = 8378;

    /// <summary>企业微信回调:本机监听路径。</summary>
    public string WebhookPath { get; set; } = "/wecom";

    /// <summary>
    /// 允许对话的群 / 单聊 id 白名单。<b>空 = 一个都不放行</b> ——
    /// 这是安全默认:一个能在生产机上敲命令的机器人,不该因为被拉进某个群就开始听话。
    /// </summary>
    /// <remarks>
    /// <b>这一份现在是 <see cref="Grants" /> 的派生镜像</b>,由 <see cref="NormalizeGrants" />
    /// 重算,不再单独维护。留着它是为了两件事:把升级前的白名单折算成授权,
    /// 以及万一用户换回旧版本,白名单还在。判断"能不能说话"一律走 <see cref="GrantFor" />。
    /// </remarks>
    public List<string> AllowedChats { get; set; } = [];

    /// <summary>每个聊天各自的授权(范围 / 挡位 / 审批)。</summary>
    public List<ChatGrant> Grants { get; set; } = [];

    /// <summary>允许对话的用户 id 白名单(空 = 白名单群里的任何人都可以)。</summary>
    public List<string> AllowedUsers { get; set; } = [];

    /// <summary>
    /// 能批准危险操作的用户 id(空 = 与 <see cref="AllowedUsers" /> 相同;两者都空 = 群里任何人)。
    /// 单独一份是因为"能指使"和"能放行"本来就该分开 —— 值班的谁都能问,重启服务得管事的点头。
    /// </summary>
    public List<string> Approvers { get; set; } = [];

    /// <summary>该渠道默认绑定的服务器(<c>user@host:port</c>,见 <see cref="BridgeSettings.ChatBindings" />)。</summary>
    public string DefaultTarget { get; set; } = "";

    /// <summary>平台名(显示名为空时的回落)。</summary>
    public string Label => DisplayName.Length > 0 ? DisplayName : Kind.ToString();

    /// <summary>这个聊天的授权;没有就是没被放行。</summary>
    public ChatGrant? GrantFor(string chatId)
        => Grants.FirstOrDefault(g => string.Equals(g.ChatId, chatId, StringComparison.Ordinal));

    /// <summary>
    /// 把升级前的 <see cref="AllowedChats" /> 折算成授权,并让它重新成为 <see cref="Grants" /> 的镜像。
    /// </summary>
    /// <remarks>
    /// 折算出来的授权是 <see cref="ScopeKind.All" /> + 挡位审批跟随全局,
    /// 也就是<b>与升级前逐字相同的行为</b>。收紧是用户自己的决定,不该由一次升级替他做。
    /// <para>
    /// 读写两头都调:读的时候把老配置补上,写的时候把镜像刷新 ——
    /// 一个派生字段只要有一头没算,它就会开始撒谎。
    /// </para>
    /// </remarks>
    public void NormalizeGrants()
    {
        foreach (string chatId in AllowedChats)
        {
            if (chatId.Length > 0 && GrantFor(chatId) is null)
            {
                Grants.Add(new ChatGrant { ChatId = chatId });
            }
        }
        AllowedChats = [.. Grants.Select(g => g.ChatId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal)];
    }
}

/// <summary>IM 桥接的设置。</summary>
/// <remarks>
/// <b>刻意与 <see cref="AiSettings" /> 分开一个存储键。</b>聊天面板每次保存都是整份
/// <c>AiSettings</c> 覆盖写(见 <c>AiPlugin.RememberPanelWidth</c> 的注释),桥接设置若混在里面,
/// 用户在设置页改完渠道、再回聊天面板勾一个工具,就会被面板内存里那份旧值盖回去。
/// </remarks>
public sealed class BridgeSettings
{
    /// <summary>总开关。关着时插件启动后什么都不做(连一条网络连接都不建)。</summary>
    public bool Enabled { get; set; }

    /// <summary>已配置的渠道。</summary>
    public List<ChannelConfig> Channels { get; set; } = [];

    /// <summary>
    /// 桥接这一侧的对话模式。<b>默认是计划模式</b> —— 只读工具。
    /// 面板那边人就坐在屏幕前,点一下就能拦住;IM 这边发起人可能在地铁上,
    /// 所以默认挡位比面板保守一档,要放开得用户自己去设置页点。
    /// </summary>
    public ChatMode Mode { get; set; } = ChatMode.Plan;

    /// <summary>危险操作的审批方式(<see cref="ApprovalMode.Ask" /> 时审批卡片发到 IM 里等回复)。</summary>
    public ApprovalMode Approval { get; set; } = ApprovalMode.Ask;

    /// <summary>
    /// 允许群里用 <c>/mode agent</c> 把挡位往<b>高</b>了调。默认关 ——
    /// 提权该在 VelaShell 的设置页里做,不该由聊天里的一句话完成。
    /// </summary>
    public bool AllowModeEscalation { get; set; }

    /// <summary>桥接用哪个模型(空 = 跟随聊天面板当前选中的那个)。</summary>
    public string? ModelId { get; set; }

    /// <summary>同时最多跑几轮(跨全部渠道;每个会话本身天然串行)。</summary>
    public int MaxConcurrentTurns { get; set; } = 2;

    /// <summary>一轮最长跑多久(秒),到点掐掉并回一句超时。</summary>
    public int TurnTimeoutSeconds { get; set; } = 300;

    /// <summary>等审批回复的时限(秒),超时按拒绝处理。</summary>
    public int ApprovalTimeoutSeconds { get; set; } = 120;

    /// <summary>一个会话闲置多久后丢掉上下文(分钟)。</summary>
    public int ConversationIdleMinutes { get; set; } = 120;

    /// <summary>
    /// 会话 → 服务器的绑定(键 <c>渠道 id/会话 id</c>,值 <c>user@host:port</c>)。
    /// </summary>
    /// <remarks>
    /// <b>不存宿主的 SessionId。</b>那是一次连接的不透明 id,断线重连就换一个;
    /// 存 <c>user@host:port</c> 才能在下一次连接后仍然对得上(解析见 <c>SessionTargets</c>)。
    /// </remarks>
    public Dictionary<string, string> ChatBindings { get; set; } = [];

    /// <summary>额外追加给桥接侧的系统提示词(空 = 只用内置那段)。</summary>
    public string ExtraSystemPrompt { get; set; } = "";
}

/// <summary>桥接设置的读写(配置走 Storage,机密走 Secrets)。</summary>
public sealed class BridgeSettingsStore(IPluginContext context)
{
    private const string SettingsKey = "bridge";

    /// <summary>某渠道某项机密的键名。</summary>
    /// <param name="channelId">渠道实例 id。</param>
    /// <param name="slot">机密槽位(<c>secret</c> / <c>token</c> / <c>aeskey</c>)。</param>
    public static string SecretName(string channelId, string slot) => $"bridge:{channelId}:{slot}";

    /// <summary>读取设置(没有则返回默认值)。</summary>
    public async Task<BridgeSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        JsonElement raw = await context.Storage.GetAsync<JsonElement>(SettingsKey, cancellationToken).ConfigureAwait(false);
        if (raw.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return new BridgeSettings();
        }
        return Normalize(raw.Deserialize<BridgeSettings>() ?? new BridgeSettings());
    }

    /// <summary>持久化设置。</summary>
    public Task SaveAsync(BridgeSettings settings, CancellationToken cancellationToken = default)
        => context.Storage.SetAsync(SettingsKey, Normalize(settings), cancellationToken);

    /// <summary>把每个渠道的授权补齐 / 刷新镜像(见 <see cref="ChannelConfig.NormalizeGrants" />)。</summary>
    private static BridgeSettings Normalize(BridgeSettings settings)
    {
        foreach (ChannelConfig channel in settings.Channels)
        {
            channel.NormalizeGrants();
        }
        return settings;
    }

    /// <summary>读某渠道的机密(未配置返回 null)。</summary>
    public Task<string?> GetSecretAsync(string channelId, string slot, CancellationToken cancellationToken = default)
        => context.Secrets.GetAsync(SecretName(channelId, slot), cancellationToken);

    /// <summary>写某渠道的机密;传空串即删除。</summary>
    public Task SetSecretAsync(string channelId, string slot, string? value, CancellationToken cancellationToken = default)
        => string.IsNullOrEmpty(value)
            ? context.Secrets.DeleteAsync(SecretName(channelId, slot), cancellationToken)
            : context.Secrets.SetAsync(SecretName(channelId, slot), value, cancellationToken);
}
