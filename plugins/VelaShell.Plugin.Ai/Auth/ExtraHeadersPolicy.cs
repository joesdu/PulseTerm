using System.ClientModel.Primitives;

namespace VelaShell.Plugin.Ai.Auth;

/// <summary>
/// 给每条出站请求补上供应商要求的额外头。
/// </summary>
/// <remarks>
/// <para>
/// 订阅型端点常常光有 <c>Authorization</c> 还不够,还要一个"算在哪个账号头上"之类的头
/// (见 <see cref="Configuration.OAuthConfig.ExtraHeaders" />)。OpenAI SDK 走的是
/// <c>System.ClientModel</c> 的管道,插一条 per-call 策略就行。
/// </para>
/// <para>
/// <b>为什么不换 <c>Transport</c></b>:那要自备 <see cref="HttpClient" />,而客户端是
/// <b>每发一条消息现建一个</b>的(每次都取最新凭据)—— 跟着建 HttpClient 会攒下一堆
/// 处于 TIME_WAIT 的连接。策略是无状态的,挂上去不碰连接池。
/// </para>
/// </remarks>
/// <param name="headers">要补的头(值里的占位符调用方已经替换过)。</param>
internal sealed class ExtraHeadersPolicy(IReadOnlyList<KeyValuePair<string, string>> headers) : PipelinePolicy
{
    /// <inheritdoc />
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    /// <inheritdoc />
    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        Apply(message);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Apply(PipelineMessage message)
    {
        if (message?.Request is not { } request)
        {
            return;
        }
        foreach ((string name, string value) in headers)
        {
            // Set 而不是 Add:重试会把同一条 message 再走一遍管道,Add 会把头叠成两份
            request.Headers.Set(name, value);
        }
    }

    /// <summary>
    /// 解析"每行一条 <c>名: 值</c>"的配置,并把 <c>{account_id}</c> 换成登录换回来的账号 id。
    /// </summary>
    /// <remarks>值里没有可替换的占位符、或账号 id 还没拿到时,那一行原样保留 —— 让服务端去报准确的错。</remarks>
    /// <param name="text">配置文本。</param>
    /// <param name="accountId">账号 id(可空)。</param>
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string? text, string? accountId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        var headers = new List<KeyValuePair<string, string>>();
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int split = line.IndexOf(':');
            if (split <= 0)
            {
                continue;
            }
            string name = line[..split].Trim();
            string value = line[(split + 1)..].Trim()
                                              .Replace("{account_id}", accountId ?? "", StringComparison.Ordinal);
            if (name.Length > 0 && value.Length > 0)
            {
                headers.Add(new KeyValuePair<string, string>(name, value));
            }
        }
        return headers;
    }
}

/// <summary>
/// 同一件事的 <see cref="DelegatingHandler" /> 版本 —— Anthropic SDK 走的是 <c>HttpClient</c>
/// 管道,挂不上 <c>System.ClientModel</c> 的策略。
/// </summary>
/// <param name="headers">要补的头(值里的占位符调用方已经替换过)。</param>
internal sealed class ExtraHeadersHandler(IReadOnlyList<KeyValuePair<string, string>> headers) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach ((string name, string value) in headers)
        {
            // 先移除再加:SDK 的重试会拿同一个 request 再来一次,不清掉就叠成两份。
            // 不校验值:这些头是各家自定义的,校验器认不出来会直接拒掉。
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
