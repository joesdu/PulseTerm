using System.Text.Json;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.TimeSeries;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>历史会话的一条摘要(会话列表用)。</summary>
/// <param name="Id">会话 id(时序标签值)。</param>
/// <param name="Title">标题(取首条用户消息)。</param>
/// <param name="CreatedAt">创建时刻。</param>
/// <param name="UpdatedAt">最后一条消息的时刻。</param>
/// <param name="MessageCount">消息条数。</param>
public sealed record ChatSessionSummary(string Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount);

/// <summary>历史会话里的一条消息。</summary>
/// <param name="Role">角色:user / assistant。</param>
/// <param name="Text">正文(超长已截断)。</param>
/// <param name="At">时刻。</param>
/// <param name="Meta">assistant 消息的附加信息(思考/工具/模型/耗时);没有则为 null。</param>
public sealed record ChatEntry(string Role, string Text, DateTimeOffset At, ChatTurnMeta? Meta = null);

/// <summary>一次工具调用的存档。</summary>
/// <param name="Name">工具名。</param>
/// <param name="Arguments">参数(JSON 文本)。</param>
/// <param name="Result">返回值(超长已截断)。</param>
public sealed record ChatToolCall(string Name, string Arguments, string Result);

/// <summary>
/// 一条 assistant 回复除正文以外的东西。单独存 —— 翻回旧会话时,
/// 光看到"干净的一问一答"是不够的:Agent 到底动了什么手,恰恰是最该留档的部分。
/// </summary>
/// <param name="Model">回答用的模型。</param>
/// <param name="ElapsedMs">整轮耗时(毫秒);0 表示没记。</param>
/// <param name="Thinking">思考过程原文(超长已截断)。</param>
/// <param name="ThinkingMs">思考耗时(毫秒);0 表示没记。</param>
/// <param name="Tools">这一轮调用过的工具。</param>
public sealed record ChatTurnMeta(
    string Model = "",
    long ElapsedMs = 0,
    string Thinking = "",
    long ThinkingMs = 0,
    IReadOnlyList<ChatToolCall>? Tools = null)
{
    /// <summary>这一轮有没有值得存的东西 —— 全空就别白占一行。</summary>
    public bool HasContent => Model.Length > 0 || ElapsedMs > 0 || Thinking.Length > 0 || Tools is { Count: > 0 };
}

/// <summary>
/// AI 会话的持久化:落在插件私有的时序库里。
/// <list type="bullet">
/// <item><c>chat_messages</c> —— 每条消息一个点(标签 conv = 会话 id,时间即消息时刻)。</item>
/// <item>
/// <c>chat_sessions</c> —— 每个会话<b>一个</b>点:时间戳固定为会话创建时刻,
/// 于是每次更新都命中「同序列同时间戳 = 覆盖」这条时序语义,天然只保留最新一份摘要,
/// 不必先删后写(<c>updated</c> 字段单独记最后更新时刻,排序用它)。
/// </item>
/// </list>
/// 时序能力不可用(headless / 无数据库的宿主)时整体降级:<see cref="IsAvailable" /> 为 false,
/// 所有写入静默跳过,聊天照常工作,只是不留历史。
/// </summary>
public sealed class ChatHistoryStore(IPluginContext context)
{
    /// <summary>单条消息入库的正文上限(字符),超出截断 —— 历史是给人翻的,不是备份原文。</summary>
    public const int MaxMessageChars = 32 * 1024;

    /// <summary>会话标题的长度上限。</summary>
    public const int MaxTitleChars = 60;

    /// <summary>思考过程入库的上限(字符)。它常常比正文还长,不值得原样留档。</summary>
    private const int MaxThinkingChars = 8 * 1024;

    /// <summary>单次工具返回值入库的上限(字符),以及一轮最多存几次调用。</summary>
    private const int MaxToolResultChars = 4 * 1024, MaxToolCalls = 12;

    private const string MessagesName = "chat_messages";
    private const string SessionsName = "chat_sessions";
    private const string MetaName = "chat_meta";
    private const string ConversationTag = "conv";

    private static readonly TimeSeriesDefinition MessagesDefinition = new(MessagesName,
    [
        TimeSeriesColumn.Tag(ConversationTag),
        TimeSeriesColumn.Field("role", TimeSeriesValueKind.Text),
        TimeSeriesColumn.Field("seq", TimeSeriesValueKind.Integer),
        TimeSeriesColumn.Field("text", TimeSeriesValueKind.Text)
    ]);

    private static readonly TimeSeriesDefinition SessionsDefinition = new(SessionsName,
    [
        TimeSeriesColumn.Tag(ConversationTag),
        TimeSeriesColumn.Field("title", TimeSeriesValueKind.Text),
        TimeSeriesColumn.Field("messages", TimeSeriesValueKind.Integer),
        TimeSeriesColumn.Field("updated", TimeSeriesValueKind.Integer)
    ]);

    /// <summary>
    /// assistant 回复的附加信息,按 <c>seq</c> 与 <c>chat_messages</c> 对应。
    /// </summary>
    /// <remarks>
    /// <b>为什么另开一张表而不是给 chat_messages 加字段</b>:宿主的
    /// <c>EnsureMeasurementAsync</c> 对已存在的 measurement <b>原样沿用、不迁移</b> ——
    /// 给旧表加字段,对老用户是静默失效。新表则新老都能用(老会话查不到就是没有附加信息)。
    /// 内容整体塞进一个 JSON 字段,以后再加东西也不用动 schema。
    /// </remarks>
    private static readonly TimeSeriesDefinition MetaDefinition = new(MetaName,
    [
        TimeSeriesColumn.Tag(ConversationTag),
        TimeSeriesColumn.Field("seq", TimeSeriesValueKind.Integer),
        TimeSeriesColumn.Field("payload", TimeSeriesValueKind.Text)
    ]);

    private readonly TimeSeriesClock _clock = new();

    /// <summary>
    /// 会话 id → 已定标题。首条用户消息定标题,之后每条都照抄 ——
    /// 不缓存的话每追加一条消息都要多查一次库(一轮 N 条 = N 次多余往返)。
    /// </summary>
    private readonly Dictionary<string, string> _titles = [];

    private ITimeSeries? _messages;
    private ITimeSeries? _sessions;
    private ITimeSeries? _meta;

    /// <summary>时序能力是否可用(<see cref="InitAsync" /> 成功后为 true)。</summary>
    public bool IsAvailable => _messages is not null && _sessions is not null;

    /// <summary>打开两个 measurement;能力不可用时记一条警告并降级(不抛)。</summary>
    public async Task InitAsync(CancellationToken cancellationToken = default)
    {
        if (IsAvailable)
        {
            return;
        }
        try
        {
            _messages = await context.TimeSeries.OpenAsync(MessagesDefinition, cancellationToken).ConfigureAwait(false);
            _sessions = await context.TimeSeries.OpenAsync(SessionsDefinition, cancellationToken).ConfigureAwait(false);
            _meta = await context.TimeSeries.OpenAsync(MetaDefinition, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _messages = null;
            _sessions = null;
            _meta = null;
            context.Log.Warn($"Chat history is disabled: {ex.Message}");
        }
    }

    /// <summary>新会话 id(时间前缀 + 随机段:标签值有序,便于人工排查)。</summary>
    public static string NewConversationId()
        => $"c{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():x}{Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>
    /// 追加一条消息并刷新会话摘要。<paramref name="sequence" /> 是会话内序号(从 0 起)。
    /// 首条用户消息顺带定标题。
    /// </summary>
    public async Task AppendAsync(string conversationId, DateTimeOffset createdAt, int sequence, string role, string text,
        CancellationToken cancellationToken = default)
    {
        if (_messages is not { } messages || _sessions is not { } sessions)
        {
            return;
        }
        try
        {
            DateTimeOffset at = _clock.Next();
            var tags = new Dictionary<string, string> { [ConversationTag] = conversationId };
            await messages.WriteAsync(new(at, tags, new Dictionary<string, TimeSeriesValue>
            {
                ["role"] = TimeSeriesValue.FromText(role),
                ["seq"] = TimeSeriesValue.FromInteger(sequence),
                ["text"] = TimeSeriesValue.FromText(Truncate(text, MaxMessageChars))
            }), cancellationToken).ConfigureAwait(false);

            // 摘要点的时间戳恒为「会话创建时刻」→ 同序列同时间戳 = 覆盖,一个会话永远只有一个点。
            string title = await ResolveTitleAsync(conversationId, sequence, role, text, cancellationToken).ConfigureAwait(false);
            await sessions.WriteAsync(new(createdAt, tags, new Dictionary<string, TimeSeriesValue>
            {
                ["title"] = TimeSeriesValue.FromText(title),
                ["messages"] = TimeSeriesValue.FromInteger(sequence + 1),
                ["updated"] = TimeSeriesValue.FromInteger(at.ToUnixTimeMilliseconds())
            }), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Warn($"Persisting chat message failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 给某条 assistant 消息补上附加信息(思考/工具/模型/耗时)。
    /// 存不下不算错 —— 正文已经落库了,附加信息丢了只是少看点东西。
    /// </summary>
    public async Task AppendMetaAsync(string conversationId, int sequence, ChatTurnMeta meta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        if (_meta is not { } series || !meta.HasContent)
        {
            return;
        }
        try
        {
            var payload = new StoredMeta(
                meta.Model,
                meta.ElapsedMs,
                Truncate(meta.Thinking, MaxThinkingChars),
                meta.ThinkingMs,
                [
                    .. (meta.Tools ?? []).Take(MaxToolCalls)
                       .Select(t => new StoredTool(t.Name, Truncate(t.Arguments, MaxToolResultChars),
                           Truncate(t.Result, MaxToolResultChars)))
                ]);
            await series.WriteAsync(new(_clock.Next(),
                new Dictionary<string, string> { [ConversationTag] = conversationId },
                new Dictionary<string, TimeSeriesValue>
                {
                    ["seq"] = TimeSeriesValue.FromInteger(sequence),
                    ["payload"] = TimeSeriesValue.FromText(JsonSerializer.Serialize(payload))
                }), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Warn($"Persisting turn metadata failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 把一个会话的消息整体换成 <paramref name="surviving" />(编辑/删除某条之后重写用)。
    /// </summary>
    /// <remarks>
    /// 时序库只能<b>按标签整体删</b>,没有"删掉 seq ≥ N"这种能力,所以只能先清空再写回。
    /// 两个刻意的取舍:
    /// <list type="bullet">
    /// <item>
    /// 写回时<b>沿用每条原来的 seq</b> —— 附加信息(chat_meta)是按 conv+seq 挂的,
    /// 序号不变,幸存消息的思考与工具调用就还在。
    /// </item>
    /// <item>
    /// 不动 chat_meta:被删掉那截的附加信息成了孤儿,但永远不会被查到
    /// (调用方会让新消息从原来的最大序号之后继续,不复用旧号)。
    /// </item>
    /// </list>
    /// </remarks>
    public async Task RewriteAsync(string conversationId, DateTimeOffset createdAt,
        IReadOnlyList<(int Sequence, string Role, string Text)> surviving, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surviving);
        if (_messages is not { } messages || _sessions is not { } sessions)
        {
            return;
        }
        var tags = new Dictionary<string, string> { [ConversationTag] = conversationId };
        try
        {
            await messages.DeleteAsync(tags, cancellationToken).ConfigureAwait(false);
            foreach ((int sequence, string role, string text) in surviving)
            {
                await messages.WriteAsync(new(_clock.Next(), tags, new Dictionary<string, TimeSeriesValue>
                {
                    ["role"] = TimeSeriesValue.FromText(role),
                    ["seq"] = TimeSeriesValue.FromInteger(sequence),
                    ["text"] = TimeSeriesValue.FromText(Truncate(text, MaxMessageChars))
                }), cancellationToken).ConfigureAwait(false);
            }
            string title = surviving.FirstOrDefault(m => m.Role == "user").Text is { Length: > 0 } first
                ? TitleFrom(first)
                : _titles.GetValueOrDefault(conversationId, "");
            _titles[conversationId] = title;
            await sessions.WriteAsync(new(createdAt, tags, new Dictionary<string, TimeSeriesValue>
            {
                ["title"] = TimeSeriesValue.FromText(title),
                ["messages"] = TimeSeriesValue.FromInteger(surviving.Count),
                ["updated"] = TimeSeriesValue.FromInteger(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            }), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Warn($"Rewriting chat history failed: {ex.Message}");
        }
    }

    /// <summary>给一个会话改名(摘要点时间戳恒为创建时刻,写进去即覆盖)。</summary>
    public async Task RenameAsync(string conversationId, string title, DateTimeOffset createdAt, int messageCount,
        CancellationToken cancellationToken = default)
    {
        if (_sessions is not { } sessions || string.IsNullOrEmpty(conversationId))
        {
            return;
        }
        string trimmed = Truncate(title.Trim(), MaxTitleChars);
        try
        {
            await sessions.WriteAsync(new(createdAt,
                new Dictionary<string, string> { [ConversationTag] = conversationId },
                new Dictionary<string, TimeSeriesValue>
                {
                    ["title"] = TimeSeriesValue.FromText(trimmed),
                    ["messages"] = TimeSeriesValue.FromInteger(messageCount),
                    ["updated"] = TimeSeriesValue.FromInteger(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                }), cancellationToken).ConfigureAwait(false);
            _titles[conversationId] = trimmed;
        }
        catch (Exception ex)
        {
            context.Log.Warn($"Renaming conversation failed: {ex.Message}");
        }
    }

    /// <summary>列出历史会话(按最后更新倒序)。</summary>
    public async Task<IReadOnlyList<ChatSessionSummary>> ListSessionsAsync(int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (_sessions is not { } sessions)
        {
            return [];
        }
        try
        {
            // 每个会话只有一个摘要点,扫描量 = 会话数;排序按 updated 字段(点的时间是创建时刻)。
            IReadOnlyList<TimeSeriesPoint> points = await sessions
                .QueryAsync(new() { Limit = Math.Clamp(limit * 4, 1, TimeSeriesLimits.MaxQueryLimit) }, cancellationToken)
                .ConfigureAwait(false);
            return
            [
                .. points.Select(p => new ChatSessionSummary(
                             p.Tag(ConversationTag),
                             p.Text("title"),
                             p.Timestamp,
                             DateTimeOffset.FromUnixTimeMilliseconds(p.Integer("updated", p.Timestamp.ToUnixTimeMilliseconds())),
                             (int)p.Integer("messages")))
                         .Where(s => s.Id.Length > 0 && s.MessageCount > 0)
                         .OrderByDescending(s => s.UpdatedAt)
                         .Take(limit)
            ];
        }
        catch (Exception ex)
        {
            context.Log.Warn($"Listing chat history failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>读取一个历史会话的全部消息(按时间正序)。</summary>
    public async Task<IReadOnlyList<ChatEntry>> LoadAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (_messages is not { } messages || string.IsNullOrEmpty(conversationId))
        {
            return [];
        }
        try
        {
            IReadOnlyList<TimeSeriesPoint> points = await messages.QueryAsync(new()
            {
                Tags = new Dictionary<string, string> { [ConversationTag] = conversationId },
                Descending = false,
                Limit = 2000
            }, cancellationToken).ConfigureAwait(false);
            Dictionary<int, ChatTurnMeta> meta = await LoadMetaAsync(conversationId, cancellationToken).ConfigureAwait(false);
            var entries = new List<ChatEntry>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                TimeSeriesPoint point = points[i];
                // seq 是入库时写进去的会话内序号;缺了就退回下标(老数据也能对上)
                int sequence = (int)point.Integer("seq", i);
                entries.Add(new ChatEntry(point.Text("role"), point.Text("text"), point.Timestamp,
                    meta.GetValueOrDefault(sequence)));
            }
            return entries;
        }
        catch (Exception ex)
        {
            context.Log.Warn($"Loading chat history failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>取一个会话的全部附加信息(按 seq 索引);表不存在或读不出来时返回空表。</summary>
    private async Task<Dictionary<int, ChatTurnMeta>> LoadMetaAsync(string conversationId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, ChatTurnMeta>();
        if (_meta is not { } series)
        {
            return result;
        }
        try
        {
            IReadOnlyList<TimeSeriesPoint> points = await series.QueryAsync(new()
            {
                Tags = new Dictionary<string, string> { [ConversationTag] = conversationId },
                Descending = false,
                Limit = 2000
            }, cancellationToken).ConfigureAwait(false);
            foreach (TimeSeriesPoint point in points)
            {
                string payload = point.Text("payload");
                if (payload.Length == 0 || JsonSerializer.Deserialize<StoredMeta>(payload) is not { } stored)
                {
                    continue;
                }
                result[(int)point.Integer("seq")] = new ChatTurnMeta(
                    stored.Model ?? "", stored.ElapsedMs, stored.Thinking ?? "", stored.ThinkingMs,
                    [.. (stored.Tools ?? []).Select(t => new ChatToolCall(t.Name ?? "", t.Arguments ?? "", t.Result ?? ""))]);
            }
        }
        catch (Exception ex)
        {
            // 附加信息读不出来不该拖垮整段会话的加载
            context.Log.Warn($"Loading turn metadata failed: {ex.Message}");
        }
        return result;
    }

    // 落盘用的形状(与对外的记录分开:对外那套将来可以改,存量 JSON 不受影响)
    private sealed record StoredMeta(string? Model, long ElapsedMs, string? Thinking, long ThinkingMs, StoredTool[]? Tools);

    private sealed record StoredTool(string? Name, string? Arguments, string? Result);

    /// <summary>删除一个历史会话(消息与摘要一并清除)。</summary>
    public async Task DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (_messages is not { } messages || _sessions is not { } sessions || string.IsNullOrEmpty(conversationId))
        {
            return;
        }
        var tags = new Dictionary<string, string> { [ConversationTag] = conversationId };
        _titles.Remove(conversationId);
        try
        {
            await messages.DeleteAsync(tags, cancellationToken).ConfigureAwait(false);
            await sessions.DeleteAsync(tags, cancellationToken).ConfigureAwait(false);
            if (_meta is { } meta)
            {
                await meta.DeleteAsync(tags, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            context.Log.Warn($"Deleting chat history failed: {ex.Message}");
        }
    }

    /// <summary>清空全部历史会话。</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (_messages is not { } messages || _sessions is not { } sessions)
        {
            return;
        }
        _titles.Clear();
        try
        {
            await messages.DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await sessions.DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (_meta is { } meta)
            {
                await meta.DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            context.Log.Warn($"Clearing chat history failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 最近发过的用户消息(去重、最新在前),供输入框 ↑↓ 调取。
    /// </summary>
    /// <remarks>
    /// <b>扫描量是刻意压过的</b>:这里没法在库侧按 role 过滤,取回来的每一行都带着完整正文
    /// (单条上限 32KB)。原先按 <c>MaxQueryLimit</c>(5000)取 90 天,重度用户一开面板就要
    /// 拉回几十 MB 文本只为筛出 100 条输入。现在只回看 <paramref name="days" /> 天、最多
    /// <paramref name="scan" /> 行 —— 够翻出最近用过的那些,代价可控。
    /// </remarks>
    public async Task<IReadOnlyList<string>> RecentUserInputsAsync(int limit = 100, int days = 30, int scan = 600,
        CancellationToken cancellationToken = default)
    {
        if (_messages is not { } messages)
        {
            return [];
        }
        try
        {
            IReadOnlyList<TimeSeriesPoint> points = await messages.QueryAsync(new()
            {
                Since = DateTimeOffset.UtcNow.AddDays(-days),
                Limit = Math.Clamp(scan, 1, TimeSeriesLimits.MaxQueryLimit)
            }, cancellationToken).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var inputs = new List<string>(limit);
            foreach (TimeSeriesPoint point in points) // 已是最新在前
            {
                string text = point.Text("text");
                if (point.Text("role") != "user" || text.Length == 0 || !seen.Add(text))
                {
                    continue;
                }
                inputs.Add(text);
                if (inputs.Count >= limit)
                {
                    break;
                }
            }
            return inputs;
        }
        catch (Exception ex)
        {
            context.Log.Warn($"Loading input history failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 标题:首条用户消息的首行;后续消息沿用已定标题。
    /// 先看内存缓存,缓存没有才回库查一次(换会话/重开面板时才会发生)。
    /// </summary>
    private async Task<string> ResolveTitleAsync(string conversationId, int sequence, string role, string text,
        CancellationToken cancellationToken)
    {
        if (sequence == 0 && role == "user")
        {
            return Remember(conversationId, TitleFrom(text));
        }
        if (_titles.TryGetValue(conversationId, out string? cached))
        {
            return cached;
        }
        if (_sessions is not { } sessions)
        {
            return TitleFrom(text);
        }
        IReadOnlyList<TimeSeriesPoint> existing = await sessions.QueryAsync(new()
        {
            Tags = new Dictionary<string, string> { [ConversationTag] = conversationId },
            Limit = 1
        }, cancellationToken).ConfigureAwait(false);
        string stored = existing.Count > 0 ? existing[0].Text("title") : "";
        return Remember(conversationId, stored.Length > 0 ? stored : TitleFrom(text));
    }

    private string Remember(string conversationId, string title)
    {
        // 缓存只是省往返,不必无限长:会话数上不去,超了整体清掉重来即可
        if (_titles.Count > 200)
        {
            _titles.Clear();
        }
        _titles[conversationId] = title;
        return title;
    }

    private static string TitleFrom(string text)
    {
        string firstLine = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault("")
                               .Trim();
        return Truncate(firstLine.Length > 0 ? firstLine : text.Trim(), MaxTitleChars);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
