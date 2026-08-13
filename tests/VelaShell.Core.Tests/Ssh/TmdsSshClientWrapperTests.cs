using System.Net;
using System.Net.Sockets;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 锁定 <see cref="TmdsSshClientWrapper" /> 的连接状态语义:
/// 连接失败不得残留半初始化的客户端(IsConnected 必须仍为 false),
/// 调用方主动取消不得被误报为超时异常。
/// </summary>
[TestClass]
public sealed class TmdsSshClientWrapperTests
{
    /// <summary>取一个刚被释放的本机端口:对它发起连接会被快速拒绝。</summary>
    private static int GetClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static TmdsSshClientWrapper CreateWrapper(int port) => new(
        new Tmds.Ssh.SshClientSettings("user@127.0.0.1")
        {
            Port = port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            Credentials = [new Tmds.Ssh.PasswordCredential("unused")],
        });

    [TestMethod]
    public async Task NewWrapper_IsNotConnected_AndOperationsRequireConnection()
    {
        using TmdsSshClientWrapper wrapper = CreateWrapper(1);

        Assert.IsFalse(wrapper.IsConnected);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            wrapper.CreateShellStreamAsync("xterm", 80, 24, 0, 0, 4096));
    }

    [TestMethod]
    public async Task ConnectAsync_Failure_LeavesWrapperDisconnected()
    {
        using TmdsSshClientWrapper wrapper = CreateWrapper(GetClosedLoopbackPort());

        await Assert.ThrowsAsync<VelaSshClientException>(() => wrapper.ConnectAsync(CancellationToken.None));

        // 失败的客户端不得残留:否则 IsConnected 误报、重连被短路
        Assert.IsFalse(wrapper.IsConnected);
    }

    [TestMethod]
    public async Task ConnectAsync_CallerCancelled_IsNotReportedAsTimeout()
    {
        using TmdsSshClientWrapper wrapper = CreateWrapper(GetClosedLoopbackPort());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 断言写在 catch 外:取消语义被保留(抛 OperationCanceledException),
        // 而不是被翻译成超时异常(两者互不继承,拿到实例后直接判类型即可)。
        OperationCanceledException cancelled =
            await Assert.ThrowsAsync<OperationCanceledException>(() => wrapper.ConnectAsync(cts.Token));

        Assert.IsNotInstanceOfType<VelaSshOperationTimeoutException>(cancelled, "主动取消不应被翻译为超时异常。");
        Assert.IsFalse(wrapper.IsConnected);
    }

    [TestMethod]
    public void DisposedWrapper_Throws()
    {
        TmdsSshClientWrapper wrapper = CreateWrapper(1);
        wrapper.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = wrapper.IsConnected);
    }
}
