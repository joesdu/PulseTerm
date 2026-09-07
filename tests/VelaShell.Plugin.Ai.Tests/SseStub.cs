using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 一次性的本地 SSE 端点:回一段写死的流式报文就收摊。
/// 用它把"报文长这样 → 界面该显示什么"整条链路真跑一遍(真 SDK、真适配器、真解析),
/// 比拿构造好的 <c>ChatResponseUpdate</c> 断言可信得多 —— 各家协议的坑都在解析这一段。
/// </summary>
public sealed class SseStub : IDisposable
{
    private readonly HttpListener _listener;
    private readonly TaskCompletionSource<string> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly List<string> _bodies = [];

    /// <summary>接入配置里填的基地址。</summary>
    public string BaseUrl { get; }

    /// <summary>收到的请求体(等到真有请求打进来为止)—— 用来断言"发出去的到底长什么样"。</summary>
    public Task<string> RequestBodyAsync => _request.Task;

    /// <summary>
    /// 到目前为止收到的<b>每一次</b>请求体(按先后)。一轮里不止一次请求时要看的是这一份 ——
    /// 比如"排队的那句有没有真的进到下一次请求里"。
    /// </summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _bodies];
            }
        }
    }

    /// <param name="sse">流式请求(<c>"stream":true</c>)的回应。</param>
    /// <param name="delay">回应前先等一会儿,用来观察"处理中"的界面状态。</param>
    /// <param name="jsonContent">
    /// 非流式请求的回复正文。插件一轮里不止一种请求 —— 聊天是流式的,
    /// 而"给几条后续提问"那一问是<b>非流式</b>的,拿 SSE 去回它会解析失败。
    /// </param>
    /// <param name="chunkDelay">
    /// 逐事件下发的间隔(&gt;0 时按空行切开、分块发送)。整段一次性写出去的话,
    /// 界面会在同一拍里收到全部增量,"流式渲染到底有没有在流"就测不出来了。
    /// </param>
    /// <param name="hold">
    /// 收下请求后<b>挂住不回</b>,直到测试调用 <see cref="Release" />。要观察"这一轮还在跑"
    /// 时的界面,用它而不是 <paramref name="delay" />:延时是在赌"我这几句断言跑得比它快",
    /// CI 上机器一慢就赌输 —— 轮次早收尾了,排队芯片也就跟着没了(macOS runner 上
    /// <c>ClickingAQueuedChip_TakesTheMessageBack</c> 正是这么挂的)。挂住则与机器快慢无关。
    /// </param>
    public SseStub(
        string sse,
        TimeSpan delay = default,
        string? jsonContent = null,
        TimeSpan chunkDelay = default,
        bool hold = false)
    {
        // 端口交给系统挑,避免并行跑测试时撞车
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{port}";

        string normalised = sse.ReplaceLineEndings("\n");
        byte[] streamed = Encoding.UTF8.GetBytes(normalised);
        byte[] plain = Encoding.UTF8.GetBytes(CompletionJson(jsonContent ?? ""));
        byte[][] chunks =
        [
            .. normalised.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                         .Select(part => Encoding.UTF8.GetBytes(part + "\n\n"))
        ];
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    HttpListenerContext context = await _listener.GetContextAsync();
                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync();
                    }
                    lock (_gate)
                    {
                        _bodies.Add(body);
                    }
                    _request.TrySetResult(body);
                    if (hold)
                    {
                        // 放行是一次性的:放开之后本轮与后续请求都照常走
                        // (排队那句作为下一轮发出去时,不该再被挡一次)。
                        await _released.Task;
                    }
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay);
                    }
                    bool wantsStream = body.Contains("\"stream\":true", StringComparison.Ordinal);
                    if (!wantsStream)
                    {
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = plain.Length;
                        await context.Response.OutputStream.WriteAsync(plain);
                        context.Response.Close();
                        continue;
                    }
                    context.Response.ContentType = "text/event-stream";
                    if (chunkDelay <= TimeSpan.Zero)
                    {
                        context.Response.ContentLength64 = streamed.Length;
                        await context.Response.OutputStream.WriteAsync(streamed);
                        context.Response.Close();
                        continue;
                    }
                    // 逐事件下发:不能给 ContentLength,得走分块传输,而且每块都要 Flush,
                    // 否则全被缓冲到最后一起吐出去,等于没有分块。
                    context.Response.SendChunked = true;
                    foreach (byte[] chunk in chunks)
                    {
                        await context.Response.OutputStream.WriteAsync(chunk);
                        await context.Response.OutputStream.FlushAsync();
                        await Task.Delay(chunkDelay);
                    }
                    context.Response.Close();
                }
            }
            catch (Exception)
            {
                // 监听器被 Dispose 掉即收摊
            }
        });
    }

    /// <summary>
    /// 放行被 <c>hold</c> 挂住的回应 —— 测试把"这一轮还在跑"时要看的都看完了,再让它答完。
    /// 多次调用无害;没挂住时调用也无害。
    /// </summary>
    public void Release() => _released.TrySetResult();

    /// <summary>一份最小的非流式 Chat Completions 回应。</summary>
    private static string CompletionJson(string content)
        => "{\"id\":\"1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"m\","
           + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
           + JsonSerializer.Serialize(content)
           + "},\"finish_reason\":\"stop\"}],"
           + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

    public void Dispose()
    {
        _request.TrySetCanceled();
        // 挂住的那一轮多半没人来放行(用例按了停止,或断言中途就失败了):
        // 取消掉,监听线程从 await 里出来直接收摊,不留一个永远等下去的任务。
        _released.TrySetCanceled();
        _listener.Close();
    }
}
