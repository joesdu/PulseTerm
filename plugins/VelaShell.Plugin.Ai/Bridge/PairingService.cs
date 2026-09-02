using System.Security.Cryptography;

namespace VelaShell.Plugin.Ai.Bridge;

/// <summary>一个敲过门但没被放行的聊天。</summary>
/// <param name="ChannelId">来自哪个渠道实例。</param>
/// <param name="ChatId">聊天 id。</param>
/// <param name="IsGroup">是不是群聊。</param>
/// <param name="UserName">最近一次是谁在说话(给设置页显示"是谁在敲门")。</param>
/// <param name="FirstSeen">第一次敲门的时刻。</param>
public readonly record struct PendingChat(
    string ChannelId,
    string ChatId,
    bool IsGroup,
    string UserName,
    DateTimeOffset FirstSeen)
{
    /// <summary>会话键(与 <see cref="InboundMessage.ChatKey" /> 同构)。</summary>
    public string ChatKey => $"{ChannelId}/{ChatId}";
}

/// <summary>
/// 配对码与待放行聊天:把"授权一个群"这件事从电脑前挪到群里。
/// </summary>
/// <remarks>
/// <b>为什么要有这个东西。</b>白名单本身没问题,难受的是拿到群 id 的过程 ——
/// 群 id 在飞书/钉钉的界面上根本看不到,原来的路子是"加机器人进群 → 发一句 → 看它回的 id
/// → 复制 → 回电脑粘进设置页 → 保存 → 重连"。人在手机上,电脑在工位上,这一趟很蠢。
/// <para>
/// 两条更短的路:① 设置页生成一个配对码,在群里发 <c>/pair 428913</c> 就完事;
/// ② 敲过门的聊天会被记下来,设置页上一行一个「允许」按钮。
/// </para>
/// <para>
/// <b>安全性没有让步。</b>配对码只决定"这个聊天能不能跟机器人说话",能不能动服务器仍旧由
/// 挡位与审批管;码是一次性的、十分钟过期、猜错五次直接作废,而且它只能<b>加</b>白名单,
/// 不能改挡位、不能绕审批。
/// </para>
/// <para>本实例由 <see cref="BridgeService" /> 持有,因此<b>跨渠道重载存活</b> ——
/// 用户在设置页点保存导致桥接重建时,已经敲过的门不该跟着丢掉。</para>
/// </remarks>
public sealed class PairingService
{
    /// <summary>配对码的寿命。够走到手机跟前,又不至于挂一整天。</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>猜错这么多次就作废。六位数字被暴力猜中的概率本就极低,这一条是兜底。</summary>
    private const int MaxAttempts = 5;

    /// <summary>待放行清单的上限:被一个陌生群刷屏时不该把内存吃掉。</summary>
    private const int MaxPending = 50;

    private readonly Dictionary<string, PendingChat> _pending = [];
    private readonly Lock _sync = new();
    private string? _code;
    private DateTimeOffset _expiresAt;
    private int _attempts;

    /// <summary>当前有效的配对码;没有或已过期时为 <see langword="null" />。</summary>
    public string? Code
    {
        get
        {
            lock (_sync)
            {
                return Valid() ? _code : null;
            }
        }
    }

    /// <summary>当前配对码的失效时刻(没有码时为 <see cref="DateTimeOffset.MinValue" />)。</summary>
    public DateTimeOffset ExpiresAt
    {
        get
        {
            lock (_sync)
            {
                return Valid() ? _expiresAt : DateTimeOffset.MinValue;
            }
        }
    }

    /// <summary>发一个新的配对码(旧的立即作废)。</summary>
    public string Issue()
    {
        lock (_sync)
        {
            // 随机数走密码学 RNG:配对码在有效期内是一个能把陌生群放进白名单的凭据,
            // 用 Random 生成的话,知道种子规律的人就能猜。
            _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _expiresAt = DateTimeOffset.UtcNow + Lifetime;
            _attempts = 0;
            return _code;
        }
    }

    /// <summary>作废当前配对码。</summary>
    public void Revoke()
    {
        lock (_sync)
        {
            _code = null;
            _expiresAt = DateTimeOffset.MinValue;
            _attempts = 0;
        }
    }

    /// <summary>
    /// 核对并<b>用掉</b>一个配对码。对了返回 true,并且这个码立刻作废(一次性)。
    /// </summary>
    public bool TryRedeem(string presented)
    {
        lock (_sync)
        {
            if (!Valid())
            {
                return false;
            }
            if (!string.Equals(presented.Trim(), _code, StringComparison.Ordinal))
            {
                if (++_attempts >= MaxAttempts)
                {
                    _code = null;
                    _expiresAt = DateTimeOffset.MinValue;
                }
                return false;
            }
            _code = null;
            _expiresAt = DateTimeOffset.MinValue;
            _attempts = 0;
            return true;
        }
    }

    /// <summary>记下一个敲过门的聊天(同一个聊天只记第一次,但显示名跟着最新一次更新)。</summary>
    public void Remember(PendingChat chat)
    {
        lock (_sync)
        {
            if (_pending.TryGetValue(chat.ChatKey, out PendingChat existing))
            {
                _pending[chat.ChatKey] = existing with { UserName = chat.UserName };
                return;
            }
            if (_pending.Count >= MaxPending)
            {
                // 满了就挤掉最早的那条 —— 用户关心的是刚才敲门的那个群
                string oldest = _pending.OrderBy(kv => kv.Value.FirstSeen).First().Key;
                _pending.Remove(oldest);
            }
            _pending[chat.ChatKey] = chat;
        }
    }

    /// <summary>待放行清单(最近敲门的排在前面)。</summary>
    public IReadOnlyList<PendingChat> Pending()
    {
        lock (_sync)
        {
            return [.. _pending.Values.OrderByDescending(c => c.FirstSeen)];
        }
    }

    /// <summary>把一个聊天从待放行清单里去掉(放行了,或者用户点了忽略)。</summary>
    public void Forget(string channelId, string chatId)
    {
        lock (_sync)
        {
            _pending.Remove($"{channelId}/{chatId}");
        }
    }

    /// <summary>清空待放行清单。</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _pending.Clear();
        }
    }

    private bool Valid() => _code is not null && DateTimeOffset.UtcNow < _expiresAt;
}
