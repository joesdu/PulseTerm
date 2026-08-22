using NSubstitute;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins.Capabilities;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteTunnel;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 插件远程隧道能力(SDK 1.2)。这一层交出去的是一条**裸字节双工流** ——
/// 它存在的全部理由就是远程执行那套"UTF-8 解码 + 按行切"的模型装不下二进制协议。
/// 因此这里要钉住的不是"能不能连上",而是:配额算不算得准、超时只夹建立阶段、
/// 以及流被释放后配额是否真的回来。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class RemoteTunnelCapabilityTests
{
    private static (RemoteTunnelCapability Capability, ISshClientWrapper Client, string SessionId) NewCapability()
    {
        var sessionId = Guid.NewGuid();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(true);
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        return (new(connections), client, sessionId.ToString());
    }

    private static Stream NewStream() => new MemoryStream();

    [TestMethod]
    public async Task OpenUnixSocketAsync_PassesPathThroughAndReturnsUsableStream()
    {
        (RemoteTunnelCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.OpenUnixConnectionAsync("/var/run/docker.sock", Arg.Any<CancellationToken>())
              .Returns(NewStream());

        await using Stream stream = await capability.OpenUnixSocketAsync(sessionId, "/var/run/docker.sock");

        Assert.IsTrue(stream.CanRead);
        Assert.IsTrue(stream.CanWrite);
        await client.Received(1).OpenUnixConnectionAsync("/var/run/docker.sock", Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task OpenTcpAsync_PassesHostAndPortThrough()
    {
        (RemoteTunnelCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.OpenTcpConnectionAsync("127.0.0.1", 2375, Arg.Any<CancellationToken>()).Returns(NewStream());

        await using Stream stream = await capability.OpenTcpAsync(sessionId, "127.0.0.1", 2375);

        Assert.IsNotNull(stream);
        await client.Received(1).OpenTcpConnectionAsync("127.0.0.1", 2375, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task UnknownSession_ThrowsSessionNotFound()
    {
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        var capability = new RemoteTunnelCapability(connections);

        await Assert.ThrowsExactlyAsync<PluginSessionNotFoundException>(
            () => capability.OpenUnixSocketAsync(Guid.NewGuid().ToString(), "/var/run/docker.sock"));
    }

    [TestMethod]
    public async Task DisconnectedSession_ThrowsSessionNotFound()
    {
        var sessionId = Guid.NewGuid();
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.IsConnected.Returns(false);
        ISshConnectionService connections = Substitute.For<ISshConnectionService>();
        connections.GetClient(sessionId).Returns(client);
        var capability = new RemoteTunnelCapability(connections);

        await Assert.ThrowsExactlyAsync<PluginSessionNotFoundException>(
            () => capability.OpenUnixSocketAsync(sessionId.ToString(), "/var/run/docker.sock"));
    }

    [TestMethod]
    public async Task ActiveTunnels_CountsOpenStreamsAndIsReleasedOnDispose()
    {
        (RemoteTunnelCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.OpenUnixConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(_ => NewStream());

        Assert.AreEqual(0, capability.ActiveTunnels);
        Stream first = await capability.OpenUnixSocketAsync(sessionId, "/var/run/docker.sock");
        Stream second = await capability.OpenUnixSocketAsync(sessionId, "/var/run/docker.sock");
        Assert.AreEqual(2, capability.ActiveTunnels);

        // 配额钉在流的释放上,而不是"调用返回时" —— 否则上限形同虚设。
        await first.DisposeAsync();
        Assert.AreEqual(1, capability.ActiveTunnels);
        await second.DisposeAsync();
        Assert.AreEqual(0, capability.ActiveTunnels);
    }

    [TestMethod]
    public async Task DoubleDispose_DoesNotDoubleCountTheRelease()
    {
        (RemoteTunnelCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.OpenUnixConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(_ => NewStream());

        Stream stream = await capability.OpenUnixSocketAsync(sessionId, "/var/run/docker.sock");
        await stream.DisposeAsync();
        await stream.DisposeAsync();
        stream.Dispose();

        Assert.AreEqual(0, capability.ActiveTunnels);
    }

    [TestMethod]
    public async Task ExceedingMaxConcurrentTunnels_Throws()
    {
        (RemoteTunnelCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.OpenUnixConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(_ => NewStream());

        List<Stream> open = [];
        for (int i = 0; i < IRemoteTunnelApi.MaxConcurrentTunnels; i++)
        {
            open.Add(await capability.OpenUnixSocketAsync(sessionId, "/var/run/docker.sock"));
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => capability.OpenUnixSocketAsync(sessionId, "/var/run/docker.sock"));

        // 被拒绝的那次不能把计数留高,否则关掉一条之后仍然开不出新的。
        Assert.AreEqual(IRemoteTunnelApi.MaxConcurrentTunnels, capability.ActiveTunnels);
        foreach (Stream s in open)
        {
            await s.DisposeAsync();
        }
        Assert.AreEqual(0, capability.ActiveTunnels);
    }

    [TestMethod]
    public async Task FailedOpen_ReleasesTheQuotaItReserved()
    {
        (RemoteTunnelCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.OpenUnixConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns<Stream>(_ => throw new IOException("connect: no such file or directory"));

        await Assert.ThrowsExactlyAsync<IOException>(
            () => capability.OpenUnixSocketAsync(sessionId, "/var/run/docker.sock"));

        Assert.AreEqual(0, capability.ActiveTunnels);
    }

    [TestMethod]
    public async Task ConnectTimeout_SurfacesAsTimeoutNotCancellation()
    {
        (RemoteTunnelCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.OpenUnixConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  CancellationToken ct = call.Arg<CancellationToken>();
                  await Task.Delay(Timeout.Infinite, ct);
                  return NewStream();
              });

        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => capability.OpenUnixSocketAsync(sessionId, "/var/run/docker.sock",
                new TunnelOptions { ConnectTimeout = TimeSpan.FromMilliseconds(50) }));

        Assert.AreEqual(0, capability.ActiveTunnels);
    }

    [TestMethod]
    public async Task CallerCancellation_SurfacesAsCancellationNotTimeout()
    {
        (RemoteTunnelCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.OpenUnixConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  CancellationToken ct = call.Arg<CancellationToken>();
                  await Task.Delay(Timeout.Infinite, ct);
                  return NewStream();
              });

        using var cts = new CancellationTokenSource();
        Task<Stream> pending = capability.OpenUnixSocketAsync(sessionId, "/var/run/docker.sock",
            new TunnelOptions { ConnectTimeout = TimeSpan.FromMinutes(1) }, cts.Token);
        await cts.CancelAsync();

        // 调用方自己取消 ≠ 超时:把它翻译成 TimeoutException 会让上层写出错误的重试策略。
        try
        {
            await pending;
            Assert.Fail("Expected the pending open to be cancelled.");
        }
        catch (OperationCanceledException)
        {
        }
        Assert.AreEqual(0, capability.ActiveTunnels);
    }

    [TestMethod]
    public async Task ReturnedStream_IsNotSeekable()
    {
        (RemoteTunnelCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        client.OpenUnixConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(_ => NewStream());

        await using Stream stream = await capability.OpenUnixSocketAsync(sessionId, "/var/run/docker.sock");

        // 内层可能恰好是个 MemoryStream,但隧道语义上就不是可定位的:
        // 让它假装可以,只会让调用方写出在真通道上必然失败的代码。
        Assert.IsFalse(stream.CanSeek);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => _ = stream.Length);
    }

    [TestMethod]
    public async Task ReturnedStream_ReadsAndWritesReachTheInnerStream()
    {
        (RemoteTunnelCapability capability, ISshClientWrapper client, string sessionId) = NewCapability();
        var inner = new MemoryStream();
        client.OpenUnixConnectionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(inner);

        Stream stream = await capability.OpenUnixSocketAsync(sessionId, "/var/run/docker.sock");
        byte[] payload = [0x00, 0x0A, 0xFF, 0x80, 0x0D];
        await stream.WriteAsync(payload);
        await stream.FlushAsync();

        // 二进制原样到达:这条通道存在的意义就是这些字节不被 UTF-8 与换行动过。
        CollectionAssert.AreEqual(payload, inner.ToArray());
        await stream.DisposeAsync();
    }
}
