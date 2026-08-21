using System.IO.Pipelines;
using System.Text;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 把 SSE 响应里<b>不是 JSON 的 <c>data:</c> 行</b>滤掉再交给 SDK 解析。
/// </summary>
/// <remarks>
/// 起因是实测踩到的:某些第三方中转站在 Anthropic 流的末尾补一行 <c>data: DONE</c>
/// (OpenAI 协议的习惯,Anthropic 协议里根本没有这东西 —— 它以 <c>message_stop</c> 收尾)。
/// Anthropic SDK 对每个 <c>data:</c> 行无条件 <c>JsonSerializer.Deserialize&lt;RawMessageStreamEvent&gt;</c>,
/// 于是整轮回复在最后一刻炸成
/// <c>'D' is an invalid start of a value</c> —— 前面已经流出来的内容全白费。
/// SDK 的 SSE 解析在 <c>Anthropic.Core.Sse</c> 里,是 internal,够不着;
/// 但 <c>AnthropicClient.Handlers</c> 允许在 HTTP 层插一手,于是在这儿把流洗一遍。
///
/// <para><b>只丢不改</b>:合法事件(<c>{…}</c>)一个字节不动地转发,连行内空白都保留;
/// 拿不准的行丢掉之外还会<b>报一次日志</b> —— 万一某个中转站在这里塞的是错误信息,
/// 至少排查时看得见,而不是变成一次无声的截断。</para>
///
/// <para><b>必须逐行冲刷</b>:攒着批量写会把流式变成"一次到货",思考与正文的实时观感就没了。</para>
///
/// <para><b>只对流式响应动手,而"是不是流式"看请求体里的 <c>stream:true</c></b> ——
/// 第一版按响应的 <c>Content-Type == text/event-stream</c> 判断,结果在真实中转站上整个没生效:
/// 它给流式响应贴的是 <c>application/json</c>。判据必须取自我们自己发出去的东西。
/// 非流式响应也不能碰:HttpClient 已经把它缓冲成可重复读的了,换成管道就只能读一次。</para>
/// </remarks>
/// <param name="onDropped">
/// 丢掉一行时的回调(记日志用):载荷,以及它是不是<b>公认的收尾哨兵</b>
/// (<c>DONE</c> / <c>[DONE]</c> —— 中转站的习惯,不是故障,调用方据此压低噪音)。
/// </param>
internal sealed class SseRepairHandler(Action<string, bool> onDropped) : DelegatingHandler
{
    private const string DataPrefix = "data:";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 判据取自<b>请求</b>而不是响应的 Content-Type:中转站给流式响应贴 application/json
        // 之类的错标签是常事,而"这一次是不是流式"由我们自己发出去的 stream:true 唯一决定。
        bool streaming = await IsStreamingAsync(request, cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || !streaming)
        {
            // 非流式的响应<b>不要</b>换掉 Content:HttpClient 把它缓冲成了可重复读的,
            // 换成管道就只能读一次,而 SDK 的校验/解析路径可能读两遍。
            return response;
        }

        Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var pipe = new Pipe();
        _ = PumpAsync(source, pipe.Writer, onDropped);
        var repaired = new StreamContent(pipe.Reader.AsStream());
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
        {
            // Content-Length 不能照搬:洗过之后长度变了,而 SSE 本来就是分块的(它一般也不在)
            if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                repaired.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        response.Content = repaired;
        return response;
    }

    /// <summary>
    /// 这一次请求是不是流式 —— 看请求体里的 <c>"stream": true</c>。
    /// </summary>
    /// <remarks>
    /// 先 <see cref="HttpContent.LoadIntoBufferAsync()" /> 再读,读完照样能发出去
    /// (实测 SDK 用的是 <c>StringContent</c>,本就可重复读;缓冲一下是为了不依赖这个细节)。
    /// 任何异常都当作"不是流式"—— 这只是个开关,拿不准就别去动响应。
    /// </remarks>
    private static async Task<bool> IsStreamingAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is null)
        {
            return false;
        }
        try
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            string body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return HasStreamTrue(body);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>JSON 里有没有 <c>"stream": true</c>(容忍冒号两边的空白)。</summary>
    internal static bool HasStreamTrue(string body)
    {
        const string Key = "\"stream\"";
        int at = body.IndexOf(Key, StringComparison.Ordinal);
        while (at >= 0)
        {
            int i = at + Key.Length;
            while (i < body.Length && char.IsWhiteSpace(body[i]))
            {
                i++;
            }
            if (i < body.Length && body[i] == ':')
            {
                i++;
                while (i < body.Length && char.IsWhiteSpace(body[i]))
                {
                    i++;
                }
                if (body.AsSpan(i).StartsWith("true", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            at = body.IndexOf(Key, at + Key.Length, StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>逐行搬运,顺手把不可解析的 <c>data:</c> 行摘掉。</summary>
    private static async Task PumpAsync(Stream source, PipeWriter writer, Action<string, bool> onDropped)
    {
        Exception? failure = null;
        try
        {
            // 不传 cancellationToken:取消由调用方弃读管道 / SDK 释放响应触发,
            // 这里再挂一个只会把正常收尾也变成 OperationCanceledException。
            using var reader = new StreamReader(source, Encoding.UTF8);
            Stream output = writer.AsStream();
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (ShouldDrop(line, out string payload, out bool sentinel))
                {
                    // 哨兵也要往外报一声(它是"清洗确实生效了"的凭据),但标出身份 ——
                    // 中转站每轮都发,调用方不该把它当成每轮一条的警告。
                    onDropped(payload, sentinel);
                    continue;
                }
                await output.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            await writer.CompleteAsync(failure).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 这一行要不要丢。只看 <c>data:</c> 行,且只放行 <c>{</c> 开头的载荷 ——
    /// Anthropic 的流事件全是 JSON 对象,别的形状 SDK 一律解析不了。
    /// </summary>
    /// <param name="line">SSE 里的一行(不含换行符)。</param>
    /// <param name="payload">被丢掉的载荷(过长会截断)。</param>
    /// <param name="sentinel">是不是公认的收尾哨兵(<c>DONE</c> / <c>[DONE]</c>)。</param>
    internal static bool ShouldDrop(string line, out string payload, out bool sentinel)
    {
        payload = "";
        sentinel = false;
        if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
        {
            return false; // event: / id: / retry: / 空行:原样转发
        }
        string body = line[DataPrefix.Length..].Trim();
        if (body.Length == 0 || body.StartsWith('{'))
        {
            return false;
        }
        sentinel = body.Equals("DONE", StringComparison.OrdinalIgnoreCase)
                   || body.Equals("[DONE]", StringComparison.OrdinalIgnoreCase);
        payload = body.Length > 200 ? body[..200] + "…" : body;
        return true;
    }
}
