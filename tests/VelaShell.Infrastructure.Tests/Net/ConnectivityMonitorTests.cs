using VelaShell.Infrastructure.Net;

namespace VelaShell.Infrastructure.Tests.Net;

/// <summary>
/// 连通性监视器的生命周期契约。
/// </summary>
/// <remarks>
/// 真实的网络事件没法在单测里可靠触发(要真拔网线),所以这里只钉住那些**能**验的:
/// 构造与释放不抛、重复释放安全、释放后不再发信号。去抖与"变可用才发"的判定
/// 由代码本身与实机验证覆盖。
/// </remarks>
[TestClass]
[TestCategory("Net")]
public sealed class ConnectivityMonitorTests
{
    [TestMethod]
    public void Construct_AndDispose_DoNotThrow()
    {
        // 订阅系统事件在受限环境(容器、无网络栈的 CI)里可能失败 ——
        // 那种情况下应当静默退化,而不是让整个应用起不来。
        var monitor = new ConnectivityMonitor();

        monitor.Dispose();
    }

    [TestMethod]
    public void DisposingTwice_IsSafe()
    {
        var monitor = new ConnectivityMonitor();

        monitor.Dispose();
        monitor.Dispose();
    }

    [TestMethod]
    public void NoSubscriber_MeansNoWork()
    {
        // 没人订阅 Resumed 时不该有任何副作用(也不该 NRE)。
        using var monitor = new ConnectivityMonitor();

        Assert.IsNotNull(monitor);
    }

    [TestMethod]
    public void AfterDispose_TheEventIsNotRaised()
    {
        var monitor = new ConnectivityMonitor();
        bool raised = false;
        monitor.Resumed += () => raised = true;

        monitor.Dispose();
        // 释放之后即便底层事件因竞态又打进来一次,也不该再往外发。
        Thread.Sleep(50);

        Assert.IsFalse(raised);
    }

    [TestMethod]
    public void ItIsAnIConnectivityMonitor()
    {
        // DI 按接口注册,VM 按接口注入 —— 别在重构里把它退化成具体类型。
        using IConnectivityMonitor monitor = new ConnectivityMonitor();

        Assert.IsNotNull(monitor);
    }
}
