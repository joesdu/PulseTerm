using VelaShell.Infrastructure.Ftp;

namespace VelaShell.Infrastructure.Tests.Ftp;

/// <summary>
/// 上限可收紧的并发闸(FTP 连接池的名额账本)。
/// </summary>
/// <remarks>
/// 存在的理由是 <see cref="SemaphoreSlim" /> 的许可只增不减:服务器只肯给一条连接时,
/// 池必须当场从"最多 4 条"收成"最多 1 条",让后来的传输排队复用那一条 —— 这正是
/// "批量上传只成功第一个"那个问题的修法。
/// </remarks>
[TestClass]
[TestCategory("Ftp")]
public class AdjustableConcurrencyGateTests
{
    [TestMethod]
    public async Task Acquire_UpToLimit_DoesNotBlock()
    {
        var gate = new AdjustableConcurrencyGate(2);

        await gate.AcquireAsync(TestContext.CancellationToken);
        await gate.AcquireAsync(TestContext.CancellationToken);

        Assert.AreEqual(2, gate.InUse);
        Assert.IsFalse(gate.AcquireAsync(TestContext.CancellationToken).IsCompleted, "满员后必须排队。");
    }

    [TestMethod]
    public async Task Release_HandsThePermitToTheFirstWaiter()
    {
        var gate = new AdjustableConcurrencyGate(1);
        await gate.AcquireAsync(TestContext.CancellationToken);
        Task first = gate.AcquireAsync(TestContext.CancellationToken);
        Task second = gate.AcquireAsync(TestContext.CancellationToken);

        gate.Release();

        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.IsFalse(second.IsCompleted, "名额只有一个,第二个还得等。");
        Assert.AreEqual(1, gate.InUse, "移交名额不改变占用数。");
    }

    /// <summary>收紧上限后,归还的名额不再放行 —— 直到占用数降到新上限以下。</summary>
    [TestMethod]
    public async Task LimitTo_ShrinksEffectiveConcurrency()
    {
        var gate = new AdjustableConcurrencyGate(3);
        await gate.AcquireAsync(TestContext.CancellationToken);
        await gate.AcquireAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, gate.LimitTo(1));

        gate.ReleaseWithoutHandoff(); // 占用 2 → 1
        Task queued = gate.AcquireAsync(TestContext.CancellationToken);
        Assert.IsFalse(queued.IsCompleted, "上限已是 1、且已占 1,新的请求必须排队。");

        gate.Release(); // 最后一个占用者归还,名额移交给排队者
        await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
    }

    /// <summary>上限只减不增:一次误报不该把并发又放回去。</summary>
    [TestMethod]
    public void LimitTo_NeverRaisesTheLimit_AndNeverGoesBelowOne()
    {
        var gate = new AdjustableConcurrencyGate(2);

        Assert.AreEqual(1, gate.LimitTo(1));
        Assert.AreEqual(1, gate.LimitTo(8));
        Assert.AreEqual(1, gate.LimitTo(0));
        Assert.AreEqual(1, gate.LimitTo(-5));
    }

    /// <summary>
    /// 失败路径上的归还**不叫醒**排队者:那次失败没有让任何连接空出来,
    /// 叫醒下一个只会让他重复同一次失败(再去开一条注定被服务器顶回来的连接)。
    /// </summary>
    [TestMethod]
    public async Task ReleaseWithoutHandoff_LeavesWaitersQueued()
    {
        var gate = new AdjustableConcurrencyGate(2);
        await gate.AcquireAsync(TestContext.CancellationToken);
        await gate.AcquireAsync(TestContext.CancellationToken);
        Task queued = gate.AcquireAsync(TestContext.CancellationToken);

        gate.ReleaseWithoutHandoff();

        Assert.IsFalse(queued.IsCompleted, "不移交就不该叫醒排队者。");
        Assert.AreEqual(1, gate.InUse);

        gate.Release(); // 真正的归还才叫醒
        await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
    }

    /// <summary>
    /// 收紧那一刻已经发出去的名额可能多于新上限(4 个传输在跑、上限却收成了 1)。
    /// 这时候每回收一个就转手给下一个,占用数永远降不下来 —— 服务器看到的仍是多个并发传输,
    /// 照样报忙。超发期间必须只收不发,直到占用数落回上限。
    /// </summary>
    [TestMethod]
    public async Task Release_WhileOverSubscribed_ShedsPermitsInsteadOfHandingThemOn()
    {
        var gate = new AdjustableConcurrencyGate(3);
        for (int i = 0; i < 3; i++)
        {
            await gate.AcquireAsync(TestContext.CancellationToken);
        }
        gate.LimitTo(1);
        Task queued = gate.AcquireAsync(TestContext.CancellationToken);

        gate.Release(); // 占用 3 → 2:超发,不移交
        Assert.IsFalse(queued.IsCompleted);
        gate.Release(); // 2 → 1:仍是超发的最后一格,不移交
        Assert.IsFalse(queued.IsCompleted);
        Assert.AreEqual(1, gate.InUse);

        gate.Release(); // 占用已回到上限内,这一次才移交
        await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.AreEqual(1, gate.InUse, "移交后占用数仍是 1:排队者接手了那个名额。");
    }

    /// <summary>取消的等待者不占名额:归还时跳过它,交给下一个真在等的人。</summary>
    [TestMethod]
    public async Task CancelledWaiter_DoesNotSwallowThePermit()
    {
        var gate = new AdjustableConcurrencyGate(1);
        await gate.AcquireAsync(TestContext.CancellationToken);
        using var cts = new CancellationTokenSource();
        Task cancelled = gate.AcquireAsync(cts.Token);
        Task waiting = gate.AcquireAsync(TestContext.CancellationToken);

        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled);

        gate.Release();

        await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
    }

    /// <summary>已取消的令牌直接拒绝,不排队。</summary>
    [TestMethod]
    public async Task Acquire_WithAlreadyCancelledToken_Throws()
    {
        var gate = new AdjustableConcurrencyGate(1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => gate.AcquireAsync(cts.Token));
    }

    /// <summary>MSTest 注入的测试上下文(取消令牌)。</summary>
    public TestContext TestContext { get; set; } = null!;
}
