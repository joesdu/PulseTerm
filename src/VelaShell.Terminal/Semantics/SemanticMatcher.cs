using System.Text.RegularExpressions;

namespace VelaShell.Terminal.Semantics;

/// <summary>一行终端输出中,由 <see cref="SemanticSpan" /> 标记的内容的语义类别。</summary>
public enum SemanticKind
{
    /// <summary>URL,例如 http 或 https 链接。</summary>
    Url,

    /// <summary>错误或否定关键词(error、failed、fatal、panic、no 等)。</summary>
    Error,

    /// <summary>警告相关关键词(warn、deprecated、caution 等)。</summary>
    Warning,

    /// <summary>成功、健康或肯定关键词(ok、done、ready、yes 等)。</summary>
    Success,

    /// <summary>点分 IPv4 地址。</summary>
    IpAddress,

    /// <summary>命令行选项标志,如 -x 或 --color。</summary>
    Option,

    /// <summary>独立的数字、端口、计数或时间戳。</summary>
    Number
}

/// <summary>单行内的一段匹配区域,以字符偏移表示。</summary>
/// <param name="Start">该区域在行中的起始字符偏移。</param>
/// <param name="Length">该区域的长度(字符数)。</param>
/// <param name="Kind">匹配区域的语义类别。</param>
public readonly record struct SemanticSpan(int Start, int Length, SemanticKind Kind)
{
    /// <summary>该区域的独占结束偏移(<see cref="Start" /> + <see cref="Length" />)。</summary>
    public int End => Start + Length;
}

/// <summary>
/// 在已提交的整行文本中查找语义上有意义的区域(URL、错误/警告词、IP 地址),
/// 以便渲染层为其着色、并把链接做成可点击(#9)。它作用于已完成的文本而非 VT 字节流,
/// 所以绝不会干扰仿真。规则刻意保持简单且可被覆盖。
/// </summary>
public sealed partial class SemanticMatcher
{
    [GeneratedRegex(@"\bhttps?://[^\s""'<>()\[\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IpRegex();

    // no 一并算作否定态:大量工具的表格输出(sshd -T、systemctl show、docker inspect)用 yes/no
    // 成对表达开关,把 no 标红后一眼能看出哪一项没开(yes 见 SuccessRegex)。
    [GeneratedRegex(@"\b(?:error|errors|failed|failure|fatal|panic|exception|denied|no|unhealthy|offline|disconnected|disabled)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorRegex();

    [GeneratedRegex(@"\b(?:warn|warning|warnings|deprecated|caution|notice)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningRegex();

    [GeneratedRegex(@"\b(?:success|successful|successfully|succeeded|ok|done|yes|enabled|active|running|started|listening|pass|passed|ready|healthy|online|connected)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SuccessRegex();

    // 命令行选项标志,如 -x、--now、--color=auto。做了锚定,因此绝不会在一个单词或带连字符的词内误触发(如 well-known、re-run)。
    [GeneratedRegex(@"(?<![\w-])--?[A-Za-z][\w-]*")]
    private static partial Regex OptionRegex();

    // 独立的数字,包括点分/冒号分组(端口、计数、时间戳如 22:37:41)。
    // IP 地址因优先级更高而胜出,因此不会被拆成多个数字。
    [GeneratedRegex(@"\b\d+(?:[.:]\d+)*\b")]
    private static partial Regex NumberRegex();

    /// <summary>
    /// 返回该行中互不重叠的区域,按起始偏移排序。当区域重叠时
    /// (例如 IP 内的数字,或 URL 内的 IP),优先级更高的类别胜出:
    /// Url &gt; IpAddress &gt; Error &gt; Warning &gt; Success &gt; Option &gt; Number。
    /// </summary>
    public static IReadOnlyList<SemanticSpan> Match(string? line) => Match(line.AsSpan());

    /// <inheritdoc cref="Match(string?)" />
    /// <remarks>
    /// Span 重载是渲染热路径的入口(<c>VelaTerminalControl.ComputeSemanticColumns</c> 逐行调用),
    /// 让调用方不必为了匹配而先把行文本物化成 string。
    /// </remarks>
    public static IReadOnlyList<SemanticSpan> Match(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty)
        {
            return [];
        }
        var raw = new List<SemanticSpan>();
        Collect(raw, UrlRegex(), line, SemanticKind.Url);
        Collect(raw, IpRegex(), line, SemanticKind.IpAddress);
        Collect(raw, ErrorRegex(), line, SemanticKind.Error);
        Collect(raw, WarningRegex(), line, SemanticKind.Warning);
        Collect(raw, SuccessRegex(), line, SemanticKind.Success);
        Collect(raw, OptionRegex(), line, SemanticKind.Option);
        Collect(raw, NumberRegex(), line, SemanticKind.Number);

        // 优先级高者在前,其次起始更早,便于下方贪心遍历保留最佳匹配。
        raw.Sort((a, b) =>
        {
            int byKind = Priority(a.Kind).CompareTo(Priority(b.Kind));
            return byKind != 0 ? byKind : a.Start.CompareTo(b.Start);
        });
        var chosen = new List<SemanticSpan>();
        // 手写重叠检测:此方法在渲染热路径上按行调用,LINQ Any 的委托 + 枚举器分配
        // 会随行数 × 帧率放大;span 数量很小,平方级比较本身无碍。
        foreach (SemanticSpan span in raw)
        {
            bool overlaps = false;
            for (int i = 0; i < chosen.Count; i++)
            {
                SemanticSpan kept = chosen[i];
                if (span.Start < kept.End && kept.Start < span.End)
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps)
            {
                chosen.Add(span);
            }
        }
        chosen.Sort((a, b) => a.Start.CompareTo(b.Start));
        return chosen;
    }

    /// <summary>返回给定字符偏移处的 URL,若没有则返回 null。</summary>
    public static string? UrlAt(string? line, int offset)
    {
        if (string.IsNullOrEmpty(line) || offset < 0 || offset >= line.Length)
        {
            return null;
        }
        foreach (ValueMatch m in UrlRegex().EnumerateMatches(line))
        {
            if (offset >= m.Index && offset < m.Index + m.Length)
            {
                return line.Substring(m.Index, m.Length);
            }
        }
        return null;
    }

    /// <remarks>
    /// 用 <see cref="Regex.EnumerateMatches(ReadOnlySpan{char})" /> 而非 <c>Matches</c>:
    /// 后者每个命中都要实例化一个 <see cref="System.Text.RegularExpressions.Match" />
    /// (还挂着 Group/Capture 集合),而这里只用
    /// 到 Index/Length —— 正是 <see cref="ValueMatch" /> 结构体给的两个字段。七条正则 × 每行 ×
    /// 帧率下,这是纯白扔的对象。
    /// </remarks>
    private static void Collect(List<SemanticSpan> into, Regex regex, ReadOnlySpan<char> line, SemanticKind kind)
    {
        foreach (ValueMatch m in regex.EnumerateMatches(line))
        {
            if (m.Length > 0)
            {
                into.Add(new(m.Index, m.Length, kind));
            }
        }
    }

    private static int Priority(SemanticKind kind) =>
        kind switch
        {
            SemanticKind.Url => 0,
            SemanticKind.IpAddress => 1,
            SemanticKind.Error => 2,
            SemanticKind.Warning => 3,
            SemanticKind.Success => 4,
            SemanticKind.Option => 5,
            _ => 6 // Number
        };
}
