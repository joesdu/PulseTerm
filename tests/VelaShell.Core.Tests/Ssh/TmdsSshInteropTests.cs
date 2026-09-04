using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 锁定 <see cref="TmdsSshInterop.Translate" /> 的取消/超时语义:
/// 调用方主动取消必须保留 OperationCanceledException,不得被翻译为超时;
/// 只有调用方未取消时的取消异常(库内部超时)才映射为 <see cref="VelaSshOperationTimeoutException" />。
/// (Tmds.Ssh 自身异常类型的构造函数均为 internal,其映射由 switch 的编译期类型检查保证。)
/// </summary>
[TestClass]
public sealed class TmdsSshInteropTests
{
    [TestMethod]
    public void Translate_CallerCancelled_ReturnsNull_PreservingCancellationSemantics()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Exception? translated = TmdsSshInterop.Translate(new OperationCanceledException(cts.Token), cts.Token);

        Assert.IsNull(translated, "调用方主动取消时应原样上抛 OperationCanceledException,而不是翻译成超时。");
    }

    [TestMethod]
    public void Translate_CallerCancelled_TaskCanceledException_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException 派生自 OperationCanceledException,同样必须保留取消语义
        Exception? translated = TmdsSshInterop.Translate(new TaskCanceledException(), cts.Token);

        Assert.IsNull(translated);
    }

    [TestMethod]
    public void Translate_CancellationWithoutCallerCancel_MapsToTimeout()
    {
        Exception? translated = TmdsSshInterop.Translate(new OperationCanceledException(), CancellationToken.None);

        Assert.IsInstanceOfType<VelaSshOperationTimeoutException>(translated);
    }

    [TestMethod]
    public void Translate_CancellationWithLiveCallerToken_MapsToTimeout()
    {
        using var cts = new CancellationTokenSource();

        // 调用方 token 存在但未取消 → 取消来自库内部超时
        Exception? translated = TmdsSshInterop.Translate(new OperationCanceledException(), cts.Token);

        Assert.IsInstanceOfType<VelaSshOperationTimeoutException>(translated);
    }

    [TestMethod]
    public void Translate_UnknownException_ReturnsNull() => Assert.IsNull(TmdsSshInterop.Translate(new InvalidOperationException("boom")));

    [TestMethod]
    public void Translate_PreservesInnerException()
    {
        var original = new OperationCanceledException("timed out");

        Exception? translated = TmdsSshInterop.Translate(original);

        Assert.IsNotNull(translated);
        Assert.AreSame(original, translated.InnerException);
    }
}
