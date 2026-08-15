using Anthropic.Exceptions;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>把接入层异常翻成"看得出哪儿不对"的一行字。</summary>
/// <remarks>
/// 起因:Anthropic SDK 的 <see cref="AnthropicApiException.Message" /> 只有一句
/// <c>Status Code: BadRequest</c> —— 服务端明明在正文里写清了原因(哪个字段不合法、
/// 超了哪个上限),却被 <see cref="AnthropicApiException.ResponseBody" /> 兜着没人看,
/// 于是日志和界面上都只剩一个没头没尾的 400。
/// OpenAI 那边的 <c>ClientResultException</c> 本来就把正文拼进 Message 了,不用管。
/// </remarks>
public static class ApiErrorText
{
    /// <summary>正文截断长度:够看清 Anthropic 的 error.message,又不至于把日志刷爆。</summary>
    private const int MaxBody = 600;

    /// <summary>异常的可读描述(带上服务端正文,如果有的话)。</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is not AnthropicApiException api)
        {
            return exception.Message;
        }
        string body = (api.ResponseBody ?? "").Trim();
        if (body.Length == 0)
        {
            return api.Message;
        }
        if (body.Length > MaxBody)
        {
            body = body[..MaxBody] + "…";
        }
        return $"{api.Message} — {body}";
    }
}
