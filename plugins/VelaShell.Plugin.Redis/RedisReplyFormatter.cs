using System.Globalization;
using StackExchange.Redis;

namespace VelaShell.Plugin.Redis;

/// <summary>控制台输出的一行。带类型是为了让界面按语义上色,而不是靠正则猜。</summary>
/// <param name="Text">整行文本(已含缩进与序号)。</param>
/// <param name="Kind">语义类别。</param>
public sealed record RedisReplyLine(string Text, RedisReplyLineKind Kind);

/// <summary>控制台输出行的语义类别。</summary>
public enum RedisReplyLineKind
{
    /// <summary>用户敲的那一行(带提示符)。</summary>
    Command,

    /// <summary>简单字符串(<c>OK</c>、<c>PONG</c>)。</summary>
    Status,

    /// <summary>批量字符串。</summary>
    Bulk,

    /// <summary>整数。</summary>
    Integer,

    /// <summary>浮点数(RESP3)。</summary>
    Double,

    /// <summary>空值。</summary>
    Nil,

    /// <summary>错误。</summary>
    Error,

    /// <summary>插件自己的说明(不是服务器回的)。</summary>
    Note
}

/// <summary>
/// 把 <see cref="RedisResult" /> 渲染成 redis-cli 那样的输出。
/// <para>
/// **为什么要跟 redis-cli 一模一样**:重度用户的眼睛是按那个格式训练出来的。
/// <c>(integer) 3</c> 与 <c>"3"</c> 在他那里是两个不同的事实(计数 vs 字符串),
/// 而 <c>(nil)</c> 与 <c>(empty array)</c> 的区别更是排障的关键 ——
/// 把这些糊成同一种显示,控制台就只是个"能敲字的框"。
/// </para>
/// </summary>
public static class RedisReplyFormatter
{
    /// <summary>渲染一条回复。</summary>
    /// <param name="result">库返回的回复。</param>
    /// <returns>输出行。</returns>
    public static IReadOnlyList<RedisReplyLine> Format(RedisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<RedisReplyLine>();
        Render(result, lines, prefix: string.Empty, indent: 0);
        return lines;
    }

    /// <summary>渲染一条错误。</summary>
    /// <param name="message">错误消息(不含 <c>(error)</c> 前缀)。</param>
    /// <returns>输出行。</returns>
    public static RedisReplyLine Error(string message) => new($"(error) {message}", RedisReplyLineKind.Error);

    /// <summary>渲染一条插件自己的说明。</summary>
    /// <param name="message">说明。</param>
    /// <returns>输出行。</returns>
    public static RedisReplyLine Note(string message) => new(message, RedisReplyLineKind.Note);

    private static void Render(RedisResult result, List<RedisReplyLine> sink, string prefix, int indent)
    {
        if (result.IsNull)
        {
            sink.Add(new($"{prefix}(nil)", RedisReplyLineKind.Nil));
            return;
        }
        switch (result.Resp2Type)
        {
            case ResultType.SimpleString:
                sink.Add(new($"{prefix}{(string?)result}", RedisReplyLineKind.Status));
                return;
            case ResultType.Error:
                sink.Add(new($"{prefix}(error) {(string?)result}", RedisReplyLineKind.Error));
                return;
            case ResultType.Integer:
                sink.Add(new(
                    $"{prefix}(integer) {((long?)result ?? 0).ToString(CultureInfo.InvariantCulture)}",
                    RedisReplyLineKind.Integer));
                return;
            case ResultType.BulkString:
                sink.Add(new($"{prefix}{Quote(result)}", ClassifyBulk(result)));
                return;
            case ResultType.Array:
                RenderArray(result, sink, prefix, indent);
                return;
            default:
                sink.Add(new($"{prefix}{(string?)result}", RedisReplyLineKind.Bulk));
                return;
        }
    }

    private static void RenderArray(RedisResult result, List<RedisReplyLine> sink, string prefix, int indent)
    {
        RedisResult[] items;
        try
        {
            items = (RedisResult[])result!;
        }
        catch (InvalidCastException)
        {
            // RESP3 的 map/set 在库里不是数组形状。退化成单行,总比抛异常好。
            sink.Add(new($"{prefix}{(string?)result}", RedisReplyLineKind.Bulk));
            return;
        }
        if (items.Length == 0)
        {
            // (empty array) 与 (nil) 是两件不同的事 —— "查了,没有" vs "键不存在"。
            sink.Add(new($"{prefix}(empty array)", RedisReplyLineKind.Nil));
            return;
        }
        string pad = new(' ', indent * 3);
        for (int i = 0; i < items.Length; i++)
        {
            // redis-cli 的序号右对齐,嵌套时靠缩进区分层级。
            string number = (i + 1).ToString(CultureInfo.InvariantCulture);
            string itemPrefix = i == 0 && prefix.Length > 0
                ? $"{prefix}{number}) "
                : $"{pad}{number}) ";
            Render(items[i], sink, itemPrefix, indent + 1);
        }
    }

    private static RedisReplyLineKind ClassifyBulk(RedisResult result) =>
        double.TryParse((string?)result, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? RedisReplyLineKind.Double
            : RedisReplyLineKind.Bulk;

    /// <summary>
    /// 批量字符串按 redis-cli 的规矩加引号并转义 —— 值可能是二进制,
    /// 直接吐原始字节会把终端(和这里的文本控件)搞乱。
    /// </summary>
    private static string Quote(RedisResult result)
    {
        byte[]? raw = (byte[]?)result;
        return raw is null ? "(nil)" : $"\"{new RedisKeyName(raw).Display}\"";
    }
}
