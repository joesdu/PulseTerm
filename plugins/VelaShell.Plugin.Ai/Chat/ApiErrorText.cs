using System.ClientModel;
using System.Net.Sockets;
using System.Security.Authentication;
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

    /// <summary>
    /// 这次失败是<b>根本没连上</b>,而不是服务端拒绝了请求。
    /// </summary>
    /// <remarks>
    /// 两者的下一步动作完全不同:连不上要去查网络 / 代理,被拒才轮到看 Key、模型名、参数。
    /// 而它们在界面上长得很像 —— 一个 <c>由于目标计算机积极拒绝,无法连接</c> 混在
    /// 一堆 API 报错里,人的第一反应往往是去翻 Key 有没有填错,方向从一开始就错了。
    /// <para>
    /// 各家 SDK 会把它裹好几层(<c>ClientResultException</c> → <c>HttpRequestException</c> →
    /// <c>SocketException</c>),所以整条 InnerException 链都要看。
    /// 超时单独认:<c>HttpClient</c> 超时抛的是 <see cref="TaskCanceledException" />
    /// 而不是 <see cref="TimeoutException" />,只看类型会把它当成"用户点了停止"。
    /// </para>
    /// </remarks>
    /// <param name="exception">要判断的异常。</param>
    public static bool IsUnreachable(Exception? exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException or AuthenticationException or TimeoutException:
                    return true;
                // HttpRequestException 也用来表示 4xx/5xx,那种带着状态码 —— 不算"没连上"
                case HttpRequestException { StatusCode: null }:
                    return true;
                case TaskCanceledException canceled when canceled.InnerException is TimeoutException:
                    return true;
            }
        }
        return false;
    }

    /// <summary>异常的可读描述(带上服务端正文,如果有的话)。</summary>
    /// <param name="exception">要描述的异常。</param>
    /// <param name="unreachableHint">
    /// 判定为"根本没连上"时补在后面的一句提示(由界面按当前语言给;为空则不补)。
    /// </param>
    public static string Describe(Exception exception, string? unreachableHint = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!string.IsNullOrEmpty(unreachableHint) && IsUnreachable(exception))
        {
            return $"{Innermost(exception).Message} — {unreachableHint}";
        }
        if (exception is ClientResultException openAi)
        {
            // OpenAI 系的异常消息<b>不总是</b>带正文:流式请求失败时经常只剩一句
            // "Service request failed. Status: 400 (Bad Request)" —— 到底哪个字段不合法
            // 全在响应体里,不挖出来就只能靠猜(真机上就卡在这儿过)。
            string detail = Shorten(RawBody(openAi));
            return detail.Length == 0 ? openAi.Message : $"{openAi.Message} — {detail}";
        }
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

    /// <summary>正文截到 <see cref="MaxBody" />:够看清哪个字段不合法,又不至于把日志刷爆。</summary>
    private static string Shorten(string body)
    {
        string text = body.Trim();
        return text.Length <= MaxBody ? text : text[..MaxBody] + "…";
    }

    /// <summary>把 OpenAI 系异常里那份原始响应体挖出来;拿不到就返回空串。</summary>
    private static string RawBody(ClientResultException exception)
    {
        try
        {
            return exception.GetRawResponse()?.Content?.ToString() ?? "";
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            // 流式响应的正文可能已经被消费/释放掉了 —— 拿不到就算了,别在报错路径上再抛一次
            return "";
        }
    }

    /// <summary>
    /// 链条最里面那个异常 —— 说清楚到底怎么了的是它
    /// (外层往往只有一句 <c>An error occurred while sending the request.</c>)。
    /// </summary>
    private static Exception Innermost(Exception exception)
    {
        Exception current = exception;
        while (current.InnerException is { } inner)
        {
            current = inner;
        }
        return current;
    }
}
