using VelaShell.Infrastructure.Startup;

namespace VelaShell.Infrastructure.Tests.Startup;

/// <summary>
/// 单实例之间的拉起转发。这条通道是 Xshell 兼容登录能不能用的关键:应用已经开着的时候,
/// 网页上那次点击起的是**第二个进程**,它必须把请求交出去再退出;交不出去就得如实说失败
/// (调用方据此退回"已在运行"提示),绝不能假装成功 —— 那样用户点了半天完全没反应。
/// </summary>
[TestClass]
[TestCategory("Startup")]
public class SingleInstanceLaunchChannelTests
{
    /// <summary>每个用例一个独立数据根,管道名互不相撞(并行跑测试时尤其要紧)。</summary>
    private static string UniqueRoot() => Path.Combine(Path.GetTempPath(), $"vela-ipc-{Guid.NewGuid():N}");

    [TestMethod]
    public void PipeName_FollowsDataRoot_SoDevInstanceNeverStealsRequests()
    {
        // --data-root 起的开发实例与正式实例是两个"单实例",转发不能串门。
        Assert.AreNotEqual(
            SingleInstanceLaunchChannel.PipeNameFor(@"C:\Users\joe\.velashell"),
            SingleInstanceLaunchChannel.PipeNameFor(@"C:\Users\joe\.velashell-dev"));
        // 大小写不同的同一个目录必须是同一条管道(Windows 路径不区分大小写)。
        Assert.AreEqual(
            SingleInstanceLaunchChannel.PipeNameFor(@"C:\Users\Joe\.velashell"),
            SingleInstanceLaunchChannel.PipeNameFor(@"c:\users\joe\.velashell"));
    }

    [TestMethod]
    public void TrySend_WithNobodyListening_ReportsFailure()
    {
        // 没人接就得返回 false:调用方靠它决定退回提示框而不是静默退出。
        bool sent = SingleInstanceLaunchChannel.TrySend(
            UniqueRoot(),
            new ExternalLaunchRequest { Kind = ExternalLaunchKind.Activate },
            TimeSpan.FromMilliseconds(300));

        Assert.IsFalse(sent);
    }

    [TestMethod]
    public void TrySend_DeliversRequestWithCredentialsIntact()
    {
        string root = UniqueRoot();
        ExternalLaunchRequest? received = null;
        using var delivered = new ManualResetEventSlim();
        using var server = SingleInstanceLaunchChannel.StartServer(root, request =>
        {
            received = request;
            delivered.Set();
        });
        Assert.IsNotNull(server);

        bool sent = SendWithRetry(root, new ExternalLaunchRequest
        {
            Host = "10.0.3.21",
            Port = 2222,
            Username = "root",
            Password = "one-time",
            Scheme = "ssh"
        });

        Assert.IsTrue(sent, "对方在听,转发就必须成功。");
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(10)), "请求没有送到处理器。");
        Assert.AreEqual("10.0.3.21", received!.Host);
        Assert.AreEqual(2222, received.Port);
        Assert.AreEqual("root", received.Username);
        Assert.AreEqual("one-time", received.Password, "一次性凭据在管道两端必须原样过去,否则外部登录到了对面就成了缺凭据。");
    }

    [TestMethod]
    public void Server_KeepsListeningAfterTheFirstRequest()
    {
        // 一次外部登录之后通道就哑掉的话,后面每次点击都石沉大海 —— 而且没有任何报错。
        string root = UniqueRoot();
        int count = 0;
        using var second = new ManualResetEventSlim();
        using var server = SingleInstanceLaunchChannel.StartServer(root, _ =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                second.Set();
            }
        });
        Assert.IsNotNull(server);

        Assert.IsTrue(SendWithRetry(root, new ExternalLaunchRequest { Host = "a", Username = "u" }));
        Assert.IsTrue(SendWithRetry(root, new ExternalLaunchRequest { Host = "b", Username = "u" }));

        Assert.IsTrue(second.Wait(TimeSpan.FromSeconds(10)), "第二条请求没被收下,监听在第一条之后就断了。");
    }

    /// <summary>服务端是在后台线程里建管道的,首次投递可能赶在它就位之前,重试几轮再判失败。</summary>
    private static bool SendWithRetry(string root, ExternalLaunchRequest request)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (SingleInstanceLaunchChannel.TrySend(root, request, TimeSpan.FromMilliseconds(500)))
            {
                return true;
            }
            Thread.Sleep(100);
        }
        return false;
    }
}
