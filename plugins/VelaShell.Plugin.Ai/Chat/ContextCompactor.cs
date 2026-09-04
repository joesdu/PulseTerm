using System.Text;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>压缩的产出。</summary>
/// <param name="Summary">新的滚动摘要(已经把旧摘要一并折进去了)。</param>
/// <param name="Through">摘要覆盖到历史的哪个下标为止(不含);这之后的消息仍以原文发送。</param>
/// <param name="FoldedMessages">本次折进摘要的消息条数。</param>
/// <param name="Usage">这次压缩请求自身的用量(它也是真花的钱)。</param>
public readonly record struct CompactionResult(string Summary, int Through, int FoldedMessages, UsageDetails? Usage);

/// <summary>
/// 上下文压缩:快撑满窗口时,把早期对话折成一段结构化摘要,近几轮保持原文。
/// 这是各家 AI 工具的通行做法 —— 直接丢早期消息会让模型"忘掉"排查过程中已经确认的事实,
/// 折成摘要则能用几百 token 把上千 token 的经过留住。
/// </summary>
/// <remarks>
/// <b>滚动</b>:每次压缩都把<i>上一版摘要</i>连同新折进来的消息一起交给模型重写,
/// 于是摘要本身不会越滚越长,也不会丢掉更早的结论。
/// </remarks>
public static class ContextCompactor
{
    /// <summary>用量超过窗口的这个比例就该压缩了。</summary>
    public const double Threshold = 0.75;

    /// <summary>压缩后的目标:把上下文压到窗口的这个比例以内,免得压完一轮又立刻触发。</summary>
    private const double Target = 0.45;

    /// <summary>无论如何都保留原文的末尾条数 —— 刚发生的几轮是模型最需要精确复述的。</summary>
    private const int KeepVerbatim = 6;

    /// <summary>摘要自身的输出上限。压缩的意义就在于短,给太多反而失去意义。</summary>
    private const int SummaryTokens = 700;

    /// <summary>
    /// 该不该压缩:窗口已知、开关打开、可折的消息够多,且估算用量已越过阈值。
    /// </summary>
    public static bool ShouldCompact(IReadOnlyList<ChatMessage> history, int summarizedThrough, string summary,
        int windowTokens, int reserveTokens)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (windowTokens <= 0 || history.Count - summarizedThrough <= KeepVerbatim + 2)
        {
            return false;
        }
        int used = ContextBuilder.Estimate(history.Skip(summarizedThrough)) + ContextBuilder.Estimate(summary);
        return used > (windowTokens - Math.Max(0, reserveTokens)) * Threshold;
    }

    /// <summary>
    /// 折到哪儿为止。目标是让"摘要 + 保留原文"落到 <see cref="Target" /> 以内,
    /// 同时至少留 <see cref="KeepVerbatim" /> 条原文,并且<b>只在用户消息处切</b> ——
    /// 从 assistant / 工具结果中间切会把工具调用和它的结果拆开。
    /// </summary>
    public static int PlanCutPoint(IReadOnlyList<ChatMessage> history, int summarizedThrough,
        int windowTokens, int reserveTokens)
    {
        ArgumentNullException.ThrowIfNull(history);
        int keepFrom = Math.Max(summarizedThrough, history.Count - KeepVerbatim);
        int budget = (int)((windowTokens - Math.Max(0, reserveTokens)) * Target);

        // 从"只留最后几条"往前放宽,能多留原文就多留 —— 摘要终究是有损的
        int cut = keepFrom;
        int kept = ContextBuilder.Estimate(history.Skip(keepFrom));
        for (int i = keepFrom - 1; i > summarizedThrough; i--)
        {
            int size = ContextBuilder.Estimate(history[i]);
            if (kept + size > budget)
            {
                break;
            }
            kept += size;
            cut = i;
        }
        // 落到用户消息上,别把一轮拦腰截断
        while (cut < history.Count && history[cut].Role != ChatRole.User)
        {
            cut++;
        }
        return Math.Min(cut, Math.Max(summarizedThrough, history.Count - 1));
    }

    /// <summary>
    /// 真正做一次压缩:把 <c>[summarizedThrough, cut)</c> 这段连同旧摘要交给模型重写成新摘要。
    /// </summary>
    /// <param name="client">裸客户端(不带工具 —— 这一问不该触发任何工具调用)。</param>
    /// <param name="history">对话历史;<b>不修改</b>。</param>
    /// <param name="summarizedThrough">上一版摘要覆盖到哪儿。</param>
    /// <param name="previousSummary">上一版摘要(没有则为空串)。</param>
    /// <param name="cut">本次要折到哪儿为止(不含)。</param>
    /// <param name="locale">让摘要用用户的语言写。</param>
    /// <param name="cancellationToken">用户按停止时一并取消。</param>
    /// <param name="tuneOptions">可选:在发压缩请求前调一遍请求选项(用来套用端点脾气,摘掉这一家不收的参数)。</param>
    public static async Task<CompactionResult?> CompactAsync(IChatClient client, IReadOnlyList<ChatMessage> history,
        int summarizedThrough, string previousSummary, int cut, string locale, CancellationToken cancellationToken,
        Action<ChatOptions>? tuneOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(history);
        if (cut <= summarizedThrough)
        {
            return null;
        }
        string prompt = BuildPrompt(history, summarizedThrough, previousSummary, cut, locale);
        // 和正式一轮走同一套"端点脾气"处理:这一家不收的参数在这儿摘掉,免得这条附带请求 400。
        var options = new ChatOptions { MaxOutputTokens = SummaryTokens };
        tuneOptions?.Invoke(options);
        ChatResponse response = await client
            .GetResponseAsync(prompt, options, cancellationToken)
            .ConfigureAwait(false);

        string summary = response.Text.Trim();
        return summary.Length == 0
            ? null
            : new CompactionResult(summary, cut, cut - summarizedThrough, response.Usage);
    }

    /// <summary>
    /// 压缩用的提示词。刻意要求"事实清单"而不是叙述 —— 运维排查里真正需要留住的是
    /// 已确认的结论、改过的东西、还没解决的问题,而不是对话的来龙去脉。
    /// </summary>
    private static string BuildPrompt(IReadOnlyList<ChatMessage> history, int from, string previousSummary,
        int cut, string locale)
    {
        var transcript = new StringBuilder();
        for (int i = from; i < cut; i++)
        {
            ChatMessage message = history[i];
            transcript.Append("### ").AppendLine(RoleLabel(message.Role));
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when text.Text.Length > 0:
                        transcript.AppendLine(Clip(text.Text, 4000));
                        break;
                    case FunctionCallContent call:
                        transcript.Append("[tool] ").AppendLine(call.Name);
                        break;
                    case FunctionResultContent result:
                        transcript.Append("[tool result] ").AppendLine(Clip(result.Result?.ToString() ?? "", 1500));
                        break;
                }
            }
            transcript.AppendLine();
        }

        var prompt = new StringBuilder();
        prompt.AppendLine("You are compacting the earlier part of a long conversation so it can keep going within a limited context window.");
        prompt.AppendLine("Write a dense factual digest that lets the assistant continue without re-reading the original messages.");
        prompt.AppendLine();
        prompt.AppendLine("Cover, in this order, omitting any section that has no content:");
        prompt.AppendLine("1. What the user is ultimately trying to do.");
        prompt.AppendLine("2. Facts established so far (hosts, paths, versions, error messages, command output that mattered).");
        prompt.AppendLine("3. Actions already taken and their result — especially anything that CHANGED state (files written, commands run).");
        prompt.AppendLine("4. Conclusions and decisions already agreed on.");
        prompt.AppendLine("5. What is still open or unresolved.");
        prompt.AppendLine();
        prompt.AppendLine("Rules: keep concrete identifiers verbatim (paths, hostnames, flags, error strings) — they are the whole point.");
        prompt.AppendLine("Do not invent anything. Do not address the user. No preamble, no closing remark.");
        prompt.AppendLine($"Write it in the user's language (UI locale: {locale}).");
        if (previousSummary.Length > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("--- digest so far (fold this in; do not lose what it already established) ---");
            prompt.AppendLine(previousSummary);
        }
        prompt.AppendLine();
        prompt.AppendLine("--- conversation to fold in ---");
        prompt.Append(transcript);
        return prompt.ToString();
    }

    private static string RoleLabel(ChatRole role)
        => role == ChatRole.User ? "user" : role == ChatRole.Assistant ? "assistant" : "tool";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
