using System.ClientModel;
using System.ClientModel.Primitives;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 聊天工具条上的短格式,以及"这次失败值不值得重来"的判定。
/// </summary>
/// <remarks>
/// 这几件原先住在一个两千八百行、要整套 Avalonia 与插件上下文才构造得起来的代码隐藏里 ——
/// 于是连"1000 该显示成 1k 还是 1.0k"这种一眼能验的事,都一条用例都没有。
/// </remarks>
[TestClass]
[TestCategory("ChatFormatting")]
public sealed class ChatFormattingTests
{
    [TestMethod]
    public void SmallCountsAreShownInFull()
    {
        Assert.AreEqual("0", ChatFormatting.Compact(0));
        Assert.AreEqual("999", ChatFormatting.Compact(999));
    }

    [TestMethod]
    public void ThousandsKeepOneDecimal()
    {
        Assert.AreEqual("1k", ChatFormatting.Compact(1_000));
        Assert.AreEqual("1.2k", ChatFormatting.Compact(1_234));
    }

    /// <summary>一万以上只留整数位。</summary>
    /// <remarks>
    /// 工具条按字符宽度计价:那个量级上的小数位没有信息量,只把旁边的模型名往外挤。
    /// </remarks>
    [TestMethod]
    public void TenThousandsDropTheDecimal()
    {
        Assert.AreEqual("12k", ChatFormatting.Compact(12_345));
        Assert.AreEqual("999k", ChatFormatting.Compact(999_000));
    }

    [TestMethod]
    public void MillionsSwitchUnit()
    {
        Assert.AreEqual("1M", ChatFormatting.Compact(1_000_000));
        Assert.AreEqual("1.2M", ChatFormatting.Compact(1_234_567));
    }

    [TestMethod]
    public void ShortDurationsAreSeconds()
    {
        Assert.AreEqual("0.8s", ChatFormatting.Duration(TimeSpan.FromSeconds(0.8)));
        Assert.AreEqual("12.3s", ChatFormatting.Duration(TimeSpan.FromSeconds(12.34)));
        Assert.AreEqual("59.9s", ChatFormatting.Duration(TimeSpan.FromSeconds(59.9)));
    }

    [TestMethod]
    public void AMinuteSwitchesToMinutesAndSeconds()
    {
        Assert.AreEqual("1m 0s", ChatFormatting.Duration(TimeSpan.FromSeconds(60)));
        Assert.AreEqual("1m 5s", ChatFormatting.Duration(TimeSpan.FromSeconds(65)));
        Assert.AreEqual("2m 3s", ChatFormatting.Duration(TimeSpan.FromSeconds(123)));
    }

    [TestMethod]
    public void OneLineFlattensNewlines()
    {
        // 带换行的文本直接放进标签,高度会突然涨到几行,把整条工具条顶变形。
        Assert.DoesNotContain("\n", ChatFormatting.OneLine("a\nb\nc", 100));
        Assert.AreEqual("a b c", ChatFormatting.OneLine("a\nb\nc", 100));
    }

    [TestMethod]
    public void OneLineTruncatesWithAnEllipsis()
    {
        Assert.AreEqual("abcde", ChatFormatting.OneLine("abcde", 5));
        Assert.AreEqual("abcde…", ChatFormatting.OneLine("abcdefgh", 5));
    }

    [TestMethod]
    public void OneLineHandlesEmptyInput()
    {
        Assert.AreEqual(string.Empty, ChatFormatting.OneLine(null, 10));
        Assert.AreEqual(string.Empty, ChatFormatting.OneLine("", 10));
    }
}

/// <summary>
/// 一次失败值不值得自动重来。
/// </summary>
/// <remarks>
/// 判错了的两种后果都不轻:把参数错当成瞬时故障会白白多打一次(还多花一次钱),
/// 把网络抖动当成永久失败则让用户在明明能成的时候看到一条红字。
/// </remarks>
[TestClass]
[TestCategory("ChatFormatting")]
public sealed class TransientFailureTests
{
    [TestMethod]
    public void NetworkAndTimeoutFailuresAreRetryable()
    {
        Assert.IsTrue(TransientFailure.IsTransient(new HttpRequestException("boom")));
        Assert.IsTrue(TransientFailure.IsTransient(new IOException("reset")));
        Assert.IsTrue(TransientFailure.IsTransient(new TimeoutException()));
    }

    [TestMethod]
    public void ServerSideThrottlingAndOutagesAreRetryable()
    {
        Assert.IsTrue(TransientFailure.IsTransient(new ClientResultException("slow down", Response(429))));
        Assert.IsTrue(TransientFailure.IsTransient(new ClientResultException("timeout", Response(408))));
        Assert.IsTrue(TransientFailure.IsTransient(new ClientResultException("oops", Response(500))));
        Assert.IsTrue(TransientFailure.IsTransient(new ClientResultException("gateway", Response(503))));
    }

    [TestMethod]
    public void ClientMistakesAreNotRetryable()
    {
        // 参数错、鉴权失败重试一万次也一样 —— 只是把钱和时间花掉。
        Assert.IsFalse(TransientFailure.IsTransient(new ClientResultException("bad request", Response(400))));
        Assert.IsFalse(TransientFailure.IsTransient(new ClientResultException("unauthorized", Response(401))));
        Assert.IsFalse(TransientFailure.IsTransient(new ClientResultException("not found", Response(404))));
        Assert.IsFalse(TransientFailure.IsTransient(new InvalidOperationException("bug")));
    }

    /// <summary>包了几层的真实原因照样认得出来。</summary>
    /// <remarks>
    /// HTTP 客户端与 SDK 会把真实原因包上一两层。只看最外层那个的话,
    /// <b>绝大多数可重试的失败都会被判成永久失败</b> —— 自动重试形同虚设。
    /// </remarks>
    [TestMethod]
    public void TheRealCauseIsFoundThroughWrappers()
    {
        Exception wrapped = new InvalidOperationException(
            "streaming failed",
            new AggregateException(new HttpRequestException("connection reset")));

        Assert.IsTrue(TransientFailure.IsTransient(wrapped));
    }

    [TestMethod]
    public void NullIsNotRetryable() => Assert.IsFalse(TransientFailure.IsTransient(null));

    /// <summary>造一个只带状态码的响应。</summary>
    private static StubResponse Response(int status) => new(status);

    private sealed class StubResponse(int status) : PipelineResponse
    {
        public override int Status { get; } = status;

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content => BinaryData.FromString("");

        protected override PipelineResponseHeaders HeadersCore { get; } = new StubHeaders();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Content);

        public override void Dispose() { }

        private sealed class StubHeaders : PipelineResponseHeaders
        {
            public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
                Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();

            public override bool TryGetValue(string name, out string? value)
            {
                value = null;
                return false;
            }

            public override bool TryGetValues(string name, out IEnumerable<string>? values)
            {
                values = null;
                return false;
            }
        }
    }
}
