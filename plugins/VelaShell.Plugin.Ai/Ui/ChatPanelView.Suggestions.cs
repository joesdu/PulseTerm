using Avalonia.Controls;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 输入框上方的建议药丸(对齐 GitHub Copilot):空会话给几条起手提示,
/// 一轮回答结束后给几条后续提问。点一下直接发出去。
/// </summary>
/// <remarks>
/// 起手提示是写死的本地文案,<b>不花一分钱</b>;后续提问要额外问一次模型,
/// 因此单独有个开关(<see cref="AiSettings.SuggestFollowUps" />),
/// 并且用最小的代价问:不带工具、不进对话历史、输出上限压到几十 token。
/// </remarks>
public partial class ChatPanelView
{
    /// <summary>最多显示几条。再多就把输入框顶上去了。</summary>
    private const int MaxSuggestions = 3;

    /// <summary>单条建议的长度上限(超了截断)——药丸太长就不像药丸了。</summary>
    private const int MaxSuggestionChars = 42;

    private CancellationTokenSource? _suggestCts;

    /// <summary>空会话的起手提示(本地文案,不请求模型)。</summary>
    private void ShowStarterSuggestions()
    {
        if (_history.Count > 0)
        {
            return;
        }
        RenderSuggestions([_loc["Starter1"], _loc["Starter2"], _loc["Starter3"]]);
    }

    private void ClearSuggestions()
    {
        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        _suggestCts = null;
        SuggestionBar.Children.Clear();
        SuggestionBar.IsVisible = false;
    }

    /// <summary>把若干条建议渲染成药丸;一条都没有就整行收起,不留空白。</summary>
    private void RenderSuggestions(IReadOnlyList<string> suggestions)
    {
        SuggestionBar.Children.Clear();
        foreach (string suggestion in suggestions.Take(MaxSuggestions))
        {
            string text = suggestion.Trim();
            if (text.Length == 0)
            {
                continue;
            }
            SuggestionBar.Children.Add(BuildSuggestionChip(text));
        }
        SuggestionBar.IsVisible = SuggestionBar.Children.Count > 0;
    }

    private Border BuildSuggestionChip(string text)
    {
        var chip = new Border
        {
            Classes = { "suggestChip" },
            Child = new TextBlock { Text = Truncate(text, MaxSuggestionChars) }
        };
        ToolTip.SetTip(chip, text);
        chip.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            ClearSuggestions();
            _ = SendAsync(text);
        };
        return chip;
    }

    /// <summary>
    /// 一轮答完后,额外问一次模型要几条后续提问。整条链路失败都只是"不显示建议",
    /// 不打扰用户 —— 这是锦上添花的东西,不该因为它报错。
    /// </summary>
    private async Task SuggestFollowUpsAsync(AiProviderConfig provider, string userText, string replyText)
    {
        if (!_settings.SuggestFollowUps || replyText.Trim().Length == 0)
        {
            return;
        }
        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        _suggestCts = new CancellationTokenSource();
        CancellationToken token = _suggestCts.Token;
        try
        {
            // 裸客户端:不叠 UseFunctionInvocation,也不给 Tools —— 这一问不该触发任何工具
            IChatClient client = await _store.CreateClientAsync(provider, cancellationToken: token);
            var options = new ChatOptions { MaxOutputTokens = 120 };
            // 思考对三行短句毫无意义,而且会把这次"便宜的附带请求"变贵
            if (provider.Reasoning is not ReasoningLevel.Default)
            {
                options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None, Output = ReasoningOutput.None };
            }

            ChatResponse response = await Task.Run(
                () => client.GetResponseAsync(BuildFollowUpPrompt(userText, replyText), options, token), token);
            token.ThrowIfCancellationRequested();

            // 附带请求的用量也算进累计(是真花的钱),但不动"上一轮上下文"那个读数 ——
            // 那个数表示的是对话本身占了多少窗口,掺进这一问会误导。
            if (response.Usage is { } usage)
            {
                _totalInputTokens += usage.InputTokenCount ?? 0;
                _totalOutputTokens += usage.OutputTokenCount ?? 0;
                UpdateUsageText();
            }
            RenderSuggestions(ParseSuggestions(response.Text));
        }
        catch (OperationCanceledException)
        {
            // 用户已经开始下一轮,这批建议作废
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Follow-up suggestions unavailable: {ex.Message}");
        }
    }

    private string BuildFollowUpPrompt(string userText, string replyText)
        => $"""
            Suggest {MaxSuggestions} short follow-up questions the user might ask next, based on the exchange below.
            Rules: one per line, no numbering, no bullets, no quotes, at most 8 words each,
            phrased as the user would type them, written in the user's language (UI locale: {_context.Host.Locale}).

            --- user ---
            {Truncate(userText.Trim(), 400)}

            --- assistant ---
            {Truncate(replyText.Trim(), 1200)}
            """;

    /// <summary>
    /// 把模型吐的几行拆成建议。模型经常不听话(带序号、带项目符号、带引号、多给几条),
    /// 所以这里按最宽松的方式清洗,而不是指望它格式规整。
    /// </summary>
    private static List<string> ParseSuggestions(string text)
    {
        var result = new List<string>(MaxSuggestions);
        foreach (string rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = rawLine.Trim().TrimStart('-', '*', '•', '·', ' ');
            // 去掉 "1." / "2)" 这类序号
            int dot = line.IndexOfAny(['.', ')', '、']);
            if (dot is > 0 and <= 2 && line[..dot].All(char.IsDigit))
            {
                line = line[(dot + 1)..];
            }
            line = line.Trim().Trim('"', '\'', '「', '」', '“', '”').Trim();
            if (line.Length is 0 or > 120)
            {
                continue; // 空行,或者模型开始长篇大论了 —— 那不是一条建议
            }
            result.Add(line);
            if (result.Count == MaxSuggestions)
            {
                break;
            }
        }
        return result;
    }

    /// <summary>面板关闭时掐掉在途的建议请求。</summary>
    private void DisposeSuggestions()
    {
        try
        {
            _suggestCts?.Cancel();
            _suggestCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已释放
        }
        _suggestCts = null;
    }

    /// <summary>语言切换时,若当前显示的是起手提示就换成新语言的。</summary>
    private void RefreshStarterSuggestions()
    {
        if (_history.Count == 0 && SuggestionBar.IsVisible)
        {
            ShowStarterSuggestions();
        }
    }
}
