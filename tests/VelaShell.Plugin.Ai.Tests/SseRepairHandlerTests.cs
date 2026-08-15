using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// SSE 清洗:中转站在 Anthropic 流里塞的非 JSON <c>data:</c> 行不能带崩整轮回复。
/// </summary>
/// <remarks>
/// 真实报错(用户环境,Anthropic SDK 12.40.0):
/// <c>AnthropicInvalidDataException: Message must be of type RawMessageStreamEvent</c>
/// ← <c>JsonException: 'D' is an invalid start of a value</c>。
/// SDK 对每个 <c>data:</c> 行无条件反序列化,末尾多出来的一行 <c>data: DONE</c>
/// 会让前面已经流出来的内容全部白费。
/// </remarks>
[TestClass]
public sealed class SseRepairHandlerTests
{
    /// <summary>按给定报文与 Content-Type 回一个响应。</summary>
    private sealed class StubHandler(string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)));
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static async Task<(string Body, List<(string Payload, bool Sentinel)> Dropped)> RunAsync(
        string upstream, bool streaming = true, string mediaType = "text/event-stream")
    {
        var dropped = new List<(string, bool)>();
        var handler = new SseRepairHandler((payload, sentinel) => dropped.Add((payload, sentinel)))
        {
            InnerHandler = new StubHandler(upstream, mediaType)
        };
        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/v1/messages")
        {
            Content = new StringContent($$"""{"model":"m","stream":{{(streaming ? "true" : "false")}}}""")
        };
        HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return (await response.Content.ReadAsStringAsync(), dropped);
    }

    private const string Upstream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"m1"}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"text":"hi"}}

        event: message_stop
        data: {"type":"message_stop"}

        data: DONE

        """;

    [TestMethod]
    public async Task DropsTheTrailingDoneSentinel_AndKeepsEveryRealEvent()
    {
        (string body, List<(string Payload, bool Sentinel)> dropped) = await RunAsync(Upstream);

        Assert.DoesNotContain("DONE", body, "这一行正是 'D' is an invalid start of a value 的元凶");
        Assert.Contains("message_start", body);
        Assert.Contains("\"text\":\"hi\"", body);
        Assert.Contains("message_stop", body);
        // 哨兵也往外报一声(它是"清洗生效了"的凭据),但要标出身份 ——
        // 中转站每轮都发,调用方据此只报头一次而不是每轮一条 Warning
        Assert.AreSequenceEqual([("DONE", true)], dropped);
    }

    /// <summary>
    /// <b>判据取自请求而不是响应的 Content-Type。</b>
    /// 中转站给流式响应贴 <c>application/json</c> 之类的错标签是常事(实测),
    /// 只认 <c>text/event-stream</c> 的话清洗会被整个跳过 —— 那正是第一版没修好的原因。
    /// </summary>
    [TestMethod]
    public async Task RepairsEvenWhenTheRelayMislabelsTheContentType()
    {
        (string body, List<(string Payload, bool Sentinel)> dropped) =
            await RunAsync(Upstream, mediaType: "application/json");

        Assert.DoesNotContain("DONE", body);
        Assert.Contains("message_stop", body);
        Assert.AreSequenceEqual([("DONE", true)], dropped);
    }

    /// <summary>丢掉之外还要报一次 —— 否则中转站塞在这里的错误信息会变成一次无声的截断。</summary>
    [TestMethod]
    public async Task ReportsAnythingElseItHadToDrop()
    {
        const string upstream = """
            data: {"type":"message_start"}

            data: Upstream request failed: gateway timeout

            """;

        (string body, List<(string Payload, bool Sentinel)> dropped) = await RunAsync(upstream);

        Assert.Contains("message_start", body);
        Assert.DoesNotContain("gateway timeout", body);
        Assert.AreSequenceEqual([("Upstream request failed: gateway timeout", false)], dropped,
            "认不出来的载荷不是哨兵,调用方要每次都警告 —— 漏一条就变成无声的截断");
    }

    /// <summary>
    /// 非流式的响应一个字节都不碰。HttpClient 已经把它缓冲成可重复读的了,
    /// 换成管道就只能读一次,而 SDK 的校验/解析路径可能读两遍。
    /// </summary>
    [TestMethod]
    public async Task LeavesNonStreamingResponsesAlone()
    {
        (string body, List<(string Payload, bool Sentinel)> dropped) = await RunAsync(
            """{"data":"DONE"}""", streaming: false, mediaType: "application/json");

        Assert.AreEqual("""{"data":"DONE"}""", body);
        Assert.IsEmpty(dropped);
    }

    [TestMethod]
    [DataRow("""{"model":"m","stream":true}""", true)]
    [DataRow("""{"stream": true}""", true, DisplayName = "冒号后有空格")]
    [DataRow("""{"model":"m","stream":false}""", false)]
    [DataRow("""{"model":"streaming-v2"}""", false, DisplayName = "别被模型名里的 stream 骗了")]
    [DataRow("""{"metadata":{"stream":"true"}}""", false, DisplayName = "字符串 \"true\" 不是 true")]
    [DataRow("{}", false)]
    public void HasStreamTrue_OnlyMatchesTheRealFlag(string body, bool expected)
        => Assert.AreEqual(expected, SseRepairHandler.HasStreamTrue(body));

    [TestMethod]
    [DataRow("data: {\"type\":\"ping\"}", false, "", false)]
    [DataRow("event: message_stop", false, "", false)]
    [DataRow("", false, "", false)]
    [DataRow("data:", false, "", false, DisplayName = "空载荷无害,原样转发")]
    [DataRow("data: DONE", true, "DONE", true)]
    [DataRow("data: [DONE]", true, "[DONE]", true, DisplayName = "OpenAI 习惯的哨兵,Anthropic SDK 一样解析不了")]
    [DataRow("data: boom", true, "boom", false)]
    public void ShouldDrop_OnlyTouchesUnparsableDataLines(string line, bool drop, string payload, bool sentinel)
    {
        Assert.AreEqual(drop, SseRepairHandler.ShouldDrop(line, out string actual, out bool actualSentinel));
        Assert.AreEqual(payload, actual);
        Assert.AreEqual(sentinel, actualSentinel);
    }
}
