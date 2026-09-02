using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using VelaShell.Plugin.Ai.Chat;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 报错文案:分清"根本没连上"与"服务端拒绝了",并把服务端正文带出来。
/// </summary>
/// <remarks>
/// 这两类的下一步动作完全不同 —— 连不上要查网络/代理,被拒才轮到看 Key 和参数。
/// 分错了会把人往完全相反的方向送(真机上就是"以为 Key 填错了,其实是没走代理")。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class ApiErrorTextTests
{
    private const string Hint = "check your proxy";

    [TestMethod]
    public void ConnectionRefused_CountsAsUnreachable()
    {
        // SDK 会裹好几层,整条 InnerException 链都得看
        var exception = new InvalidOperationException("wrapper",
            new HttpRequestException("An error occurred while sending the request.",
                new SocketException((int)SocketError.ConnectionRefused)));

        Assert.IsTrue(ApiErrorText.IsUnreachable(exception));
        string text = ApiErrorText.Describe(exception, Hint);
        Assert.Contains(Hint, text);
    }

    [TestMethod]
    public void DnsFailureAndTlsFailure_CountAsUnreachable()
    {
        Assert.IsTrue(ApiErrorText.IsUnreachable(
            new HttpRequestException("no such host", new SocketException((int)SocketError.HostNotFound))));
        // 代理把 TLS 拦下来时是这一个
        Assert.IsTrue(ApiErrorText.IsUnreachable(
            new HttpRequestException("tls", new AuthenticationException("remote certificate is invalid"))));
    }

    [TestMethod]
    public void HttpClientTimeout_CountsAsUnreachable()
    {
        // HttpClient 超时抛的是 TaskCanceledException(内含 TimeoutException),
        // 只看类型会把它错当成"用户点了停止"
        var timeout = new TaskCanceledException("The request was canceled due to timeout.",
            new TimeoutException());

        Assert.IsTrue(ApiErrorText.IsUnreachable(timeout));
    }

    [TestMethod]
    public void UserCancellation_IsNotUnreachable()
    {
        // 没有 TimeoutException 内因的取消是用户按的停止,不该被说成网络不通
        Assert.IsFalse(ApiErrorText.IsUnreachable(new TaskCanceledException("stopped")));
        Assert.IsFalse(ApiErrorText.IsUnreachable(new OperationCanceledException()));
    }

    [TestMethod]
    public void ServerRejection_IsNotUnreachable()
    {
        // 带状态码的 HttpRequestException 是服务端答复过了 —— 那是 Key/参数的问题
        var rejected = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

        Assert.IsFalse(ApiErrorText.IsUnreachable(rejected));
        Assert.AreEqual("Unauthorized", ApiErrorText.Describe(rejected, Hint), "别给它扣一顶代理的帽子");
    }

    [TestMethod]
    public void PlainApiError_IsPassedThroughUntouched()
    {
        var error = new InvalidOperationException("model not found: gpt-nope");

        Assert.AreEqual("model not found: gpt-nope", ApiErrorText.Describe(error, Hint));
        Assert.AreEqual("model not found: gpt-nope", ApiErrorText.Describe(error));
    }

    [TestMethod]
    public void Unreachable_ReportsTheInnermostMessage()
    {
        // 外层那句 "An error occurred while sending the request." 什么都没说,
        // 说清楚的是最里面那个
        var exception = new HttpRequestException("An error occurred while sending the request.",
            new SocketException((int)SocketError.TimedOut));

        string text = ApiErrorText.Describe(exception, Hint);

        Assert.DoesNotContain("An error occurred while sending", text);
        Assert.Contains(Hint, text);
    }

    [TestMethod]
    public void WithoutAHint_UnreachableStillFallsBackToTheOldBehaviour()
    {
        var exception = new HttpRequestException("boom", new SocketException((int)SocketError.ConnectionRefused));

        Assert.AreEqual("boom", ApiErrorText.Describe(exception));
    }

    [TestMethod]
    public void Null_IsNotUnreachable() => Assert.IsFalse(ApiErrorText.IsUnreachable(null));
}
