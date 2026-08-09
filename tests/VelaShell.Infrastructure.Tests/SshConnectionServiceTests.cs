using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Tests;

/// <summary>SSH 连接服务的拆除路径。</summary>
[TestClass]
[TestCategory("Ssh")]
public class SshConnectionServiceTests
{
    [TestMethod]
    public async Task DisconnectAsync_UnknownSession_IsANoOp()
    {
        // 关闭应用时标签关闭与会话拆除是两条并发路径,后到的那条必然找不到会话。
        // 这里曾经抛 InvalidOperationException,调用方一律 catch 吞掉,只在调试器里留噪声。
        await using var service = new SshConnectionService(_ => throw new InvalidOperationException("不该建连接"));

        await service.DisconnectAsync(Guid.NewGuid());
        await service.DisconnectAsync(Guid.Empty);

        Assert.IsEmpty(service.Sessions);
    }

    [TestMethod]
    public async Task DisconnectAsync_CalledTwice_DoesNotThrow()
    {
        await using var service = new SshConnectionService(_ => throw new InvalidOperationException("不该建连接"));
        var id = Guid.NewGuid();

        await service.DisconnectAsync(id);
        await service.DisconnectAsync(id);

        Assert.IsNull(service.GetClient(id));
    }
}
