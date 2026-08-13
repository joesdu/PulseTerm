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
public sealed record ChatEntry(string Role, string Text, DateTimeOffset At);

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

    private const string MessagesName = "chat_messages";
    private const string SessionsName = "chat_sessions";
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

    private readonly TimeSeriesClock _clock = new();
    private ITimeSeries? _messages;
    private ITimeSeries? _sessions;

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
        }
        catch (Exception ex)
        {
            _messages = null;
            _sessions = null;
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
            return [.. points.Select(p => new ChatEntry(p.Text("role"), p.Text("text"), p.Timestamp))];
        }
        catch (Exception ex)
        {
            context.Log.Warn($"Loading chat history failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>删除一个历史会话(消息与摘要一并清除)。</summary>
    public async Task DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (_messages is not { } messages || _sessions is not { } sessions || string.IsNullOrEmpty(conversationId))
        {
            return;
        }
        var tags = new Dictionary<string, string> { [ConversationTag] = conversationId };
        try
        {
            await messages.DeleteAsync(tags, cancellationToken).ConfigureAwait(false);
            await sessions.DeleteAsync(tags, cancellationToken).ConfigureAwait(false);
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
        try
        {
            await messages.DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await sessions.DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Warn($"Clearing chat history failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 最近发过的用户消息(去重、最新在前),供输入框 ↑↓ 调取。
    /// 只回看最近 <paramref name="days" /> 天,避免历史很长时的大扫描。
    /// </summary>
    public async Task<IReadOnlyList<string>> RecentUserInputsAsync(int limit = 100, int days = 90,
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
                Limit = TimeSeriesLimits.MaxQueryLimit
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

    /// <summary>标题:首条用户消息的首行;后续消息沿用已存标题。</summary>
    private async Task<string> ResolveTitleAsync(string conversationId, int sequence, string role, string text,
        CancellationToken cancellationToken)
    {
        if (sequence == 0 && role == "user")
        {
            return TitleFrom(text);
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
        return stored.Length > 0 ? stored : TitleFrom(text);
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
