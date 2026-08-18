using System.Diagnostics;
using System.Globalization;
using StackExchange.Redis;

namespace VelaShell.Plugin.Redis;

/// <summary>一条控制台命令的执行结果。</summary>
/// <param name="Lines">已格式化的输出行(redis-cli 口径)。</param>
/// <param name="IsError">是否是错误。</param>
/// <param name="Elapsed">往返耗时。</param>
/// <param name="SelectedDatabase">命令是 <c>SELECT n</c> 时的目标库;否则为 null。</param>
public sealed record RedisConsoleResult(
    IReadOnlyList<RedisReplyLine> Lines,
    bool IsError,
    TimeSpan Elapsed,
    int? SelectedDatabase = null);

/// <summary>一条命令的补全条目。</summary>
/// <param name="Name">命令名(大写)。</param>
/// <param name="Arity">参数个数(负数表示"至少")。</param>
/// <param name="Summary">一句话说明;取不到时为空串。</param>
/// <param name="Syntax">参数形态提示;取不到时为空串。</param>
public sealed record RedisCommandHint(string Name, int Arity, string Summary, string Syntax);

/// <summary>
/// 控制台路径:任意命令执行 + 命令元数据。
/// <para>
/// **补全数据来自服务器**(<c>COMMAND DOCS</c> / <c>COMMAND INFO</c>)。这一步同时解决三件事:
/// 命令表永远匹配这台服务器的版本、自动包含模块命令(<c>JSON.SET</c> / <c>FT.SEARCH</c> / <c>TS.ADD</c>
/// 全都白得)、以及插件里不必维护一张两百多行的表。
/// </para>
/// <para>
/// 已知边界(库的多路复用模型所致):<c>MONITOR</c> 与阻塞类命令在这条通道上跑不了 ——
/// 前者要让连接进入只吐流的状态,后者会卡住整个复用器。**如实拒绝并说明原因**,
/// 而不是让用户敲下去然后卡住或超时。
/// </para>
/// </summary>
internal sealed partial class RedisConnection
{
    /// <summary>
    /// 这条通道跑不了的命令。
    /// <para>
    /// 阻塞类命令库明确不提供(官方文档:多路复用下会卡死复用器);<c>MONITOR</c>/<c>SUBSCRIBE</c>
    /// 族要求连接进入特定状态,而复用连接上没有"这条连接"的概念。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Unsupported =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "MONITOR",
        "SUBSCRIBE", "UNSUBSCRIBE", "PSUBSCRIBE", "PUNSUBSCRIBE", "SSUBSCRIBE", "SUNSUBSCRIBE",
        "BLPOP", "BRPOP", "BRPOPLPUSH", "BLMOVE", "BLMPOP", "BZPOPMIN", "BZPOPMAX", "BZMPOP",
        "WAIT", "WAITAOF",
        "MULTI", "EXEC", "DISCARD", "WATCH", "UNWATCH"
    ];

    private readonly Dictionary<string, RedisCommandHint> _hints = [with(StringComparer.OrdinalIgnoreCase)];

    /// <summary>命令闸门(分级依据 <c>COMMAND INFO</c> 的 flags)。</summary>
    public RedisCommandGuard Guard { get; } = new();

    /// <summary>已知命令的补全条目,按名字排序。</summary>
    public IReadOnlyList<RedisCommandHint> CommandHints => [.. _hints.Values.OrderBy(h => h.Name, StringComparer.Ordinal)];

    /// <summary>某条命令在这条通道上是否跑不了(界面据此在敲之前就说清楚)。</summary>
    /// <param name="command">命令名或整行。</param>
    /// <returns>是否不支持。</returns>
    public static bool IsUnsupportedOnThisTransport(string? command) =>
        Unsupported.Contains(RedisCommandGuard.Normalize(command));

    /// <summary>
    /// 装载命令元数据:先试 <c>COMMAND DOCS</c>(7.0+,带说明与参数形态),
    /// 再用 <c>COMMAND INFO</c> 补 flags(闸门分级的依据)。两者都拿不到时闸门退回内置兜底表。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步操作的任务。</returns>
    public async Task LoadCommandMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IDatabase db = Db();
        var flags = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        RedisResult info = await TryExecuteAsync(db, "COMMAND").ConfigureAwait(false);
        if (!info.IsNull && info.Resp2Type == ResultType.Array)
        {
            foreach (RedisResult entry in (RedisResult[])info!)
            {
                ParseCommandInfoEntry(entry, flags);
            }
        }
        Guard.LoadServerMetadata(flags);

        // COMMAND DOCS 只影响提示文案的丰俭,拿不到不影响可用性。
        RedisResult docs = await TryExecuteAsync(db, "COMMAND", "DOCS").ConfigureAwait(false);
        if (!docs.IsNull && docs.Resp2Type == ResultType.Array)
        {
            ParseCommandDocs(docs);
        }
    }

    /// <summary>
    /// 执行一行控制台命令。
    /// <para>
    /// **闸门不在这里** —— 判定与确认在界面层完成(它才有弹窗),这里只负责跑与格式化。
    /// 把两件事混在一层会让"没有界面的调用方"(未来的自动化)绕过确认。
    /// </para>
    /// </summary>
    /// <param name="line">用户敲的一整行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果。</returns>
    public async Task<RedisConsoleResult> ExecuteConsoleAsync(string line, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        if (!RedisCommandLine.TrySplit(line, out IReadOnlyList<string> args, out string parseError))
        {
            return new([RedisReplyFormatter.Error($"parse: {parseError}")], IsError: true, stopwatch.Elapsed);
        }
        if (args.Count == 0)
        {
            return new([], IsError: false, stopwatch.Elapsed);
        }
        string command = args[0].ToUpperInvariant();
        if (Unsupported.Contains(command))
        {
            return new(
                [RedisReplyFormatter.Note($"'{command}' is not available on a multiplexed connection.")],
                IsError: true,
                stopwatch.Elapsed);
        }

        object[] parameters = [.. args.Skip(1).Select(static arg => (object)arg)];
        try
        {
            RedisResult result = await Db().ExecuteAsync(command, parameters).ConfigureAwait(false);
            stopwatch.Stop();
            int? selected = command is "SELECT" && args.Count > 1
                            && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int target)
                ? target
                : null;
            if (selected is { } database && _settings.SupportsDatabases)
            {
                // 控制台切库要让浏览器跟上:静默分叉(控制台在 db3、浏览器还在 db0)
                // 是比"多刷新一次"糟糕得多的失败模式。
                SelectDatabase(database);
            }
            return new(RedisReplyFormatter.Format(result), IsError: false, stopwatch.Elapsed, selected);
        }
        catch (RedisServerException ex)
        {
            // 服务器说的错要原样呈现:它是排障的第一手信息(NOPERM / WRONGTYPE / MOVED …)。
            stopwatch.Stop();
            return new([RedisReplyFormatter.Error(ex.Message)], IsError: true, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is RedisTimeoutException or RedisConnectionException)
        {
            stopwatch.Stop();
            return new([RedisReplyFormatter.Error(ex.Message)], IsError: true, stopwatch.Elapsed);
        }
    }

    /// <summary>按前缀给补全候选(最多 <paramref name="limit" /> 条)。</summary>
    /// <param name="prefix">已输入的命令前缀。</param>
    /// <param name="limit">最多返回几条。</param>
    /// <returns>候选。</returns>
    public IReadOnlyList<RedisCommandHint> Complete(string? prefix, int limit = 12)
    {
        string text = (prefix ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return [];
        }
        return
        [
            .. _hints.Values
                .Where(hint => hint.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(hint => hint.Name, StringComparer.Ordinal)
                .Take(limit)
        ];
    }

    /// <summary><c>COMMAND</c> 的一条:<c>[name, arity, [flags…], firstKey, lastKey, step, …]</c>。</summary>
    private void ParseCommandInfoEntry(RedisResult entry, Dictionary<string, IReadOnlyList<string>> flags)
    {
        if (entry.IsNull || entry.Resp2Type != ResultType.Array)
        {
            return;
        }
        RedisResult[] parts;
        try
        {
            parts = (RedisResult[])entry!;
        }
        catch (InvalidCastException)
        {
            return;
        }
        if (parts.Length < 3 || (string?)parts[0] is not { Length: > 0 } name)
        {
            return;
        }
        string upper = name.ToUpperInvariant();
        int arity = (int?)(long?)parts[1] ?? 0;
        var commandFlags = new List<string>();
        if (parts[2].Resp2Type == ResultType.Array)
        {
            try
            {
                foreach (RedisResult flag in (RedisResult[])parts[2]!)
                {
                    if ((string?)flag is { Length: > 0 } text)
                    {
                        commandFlags.Add(text);
                    }
                }
            }
            catch (InvalidCastException)
            {
                // flags 形状不对就当没有:闸门会退回"未知即写"的保守判定。
            }
        }
        flags[upper] = commandFlags;
        _hints[upper] = new(upper, arity, string.Empty, string.Empty);
    }

    /// <summary><c>COMMAND DOCS</c> 的扁平映射:<c>[name, [key, value, …], …]</c>。</summary>
    private void ParseCommandDocs(RedisResult docs)
    {
        RedisResult[] flat;
        try
        {
            flat = (RedisResult[])docs!;
        }
        catch (InvalidCastException)
        {
            return;
        }
        for (int i = 0; i + 1 < flat.Length; i += 2)
        {
            if ((string?)flat[i] is not { Length: > 0 } name)
            {
                continue;
            }
            string upper = name.ToUpperInvariant();
            string summary = ReadDocField(flat[i + 1], "summary");
            string syntax = ReadDocField(flat[i + 1], "arguments");
            RedisCommandHint existing = _hints.TryGetValue(upper, out RedisCommandHint? found)
                ? found
                : new(upper, 0, string.Empty, string.Empty);
            _hints[upper] = existing with { Summary = summary, Syntax = syntax };
        }
    }

    /// <summary>从 DOCS 的键值对里取一个字段的文本表示;取不到给空串。</summary>
    private static string ReadDocField(RedisResult map, string field)
    {
        if (map.IsNull || map.Resp2Type != ResultType.Array)
        {
            return string.Empty;
        }
        RedisResult[] pairs;
        try
        {
            pairs = (RedisResult[])map!;
        }
        catch (InvalidCastException)
        {
            return string.Empty;
        }
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            if (!string.Equals((string?)pairs[i], field, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // arguments 是一棵嵌套结构,这里只要"有没有参数"这个信号 ——
            // 把整棵树渲染成一行参数形态是 M4 的事,现在给个占位比给一串乱码好。
            return pairs[i + 1].Resp2Type == ResultType.Array
                ? "…"
                : (string?)pairs[i + 1] ?? string.Empty;
        }
        return string.Empty;
    }
}
