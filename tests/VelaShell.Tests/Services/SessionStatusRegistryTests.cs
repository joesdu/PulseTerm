using VelaShell.Core.Models;
using VelaShell.Services;

namespace VelaShell.Tests.Services;

/// <summary>
/// 「每条配置名下都有谁活着」这本账,以及树上那个圆点的合并规则。
/// </summary>
/// <remarks>
/// 这里是两个已修过的同形 bug 的原产地(§24 / §39 / #321)。它们同形、症状却毫不相干:
/// 一个是"节点永远停在连接中",一个是"关掉两个 FTP 标签里的一个,圆点就灭了"。
/// 规则本身只有两句话,但每一句都是被现场教出来的,所以逐条钉住。
/// </remarks>
[TestClass]
[TestCategory("SessionStatus")]
public sealed class SessionStatusRegistryTests
{
    private static readonly Guid Profile = Guid.NewGuid();

    [TestMethod]
    public void WithNothingOpenTheProfileIsDisconnected()
    {
        SessionStatusRegistry registry = new();

        Assert.AreEqual(SessionStatus.Disconnected, registry.Merge(Profile, []));
    }

    /// <summary>已连上的标签压得住旁边正在握手的那个。</summary>
    /// <remarks>
    /// 按"最后一次变更"更新会留下一个走不出去的状态:在已连上的会话上再开一个标签、
    /// 趁它还在握手时立刻关掉,节点会永远停在「连接中」(#321)。
    /// </remarks>
    [TestMethod]
    public void ConnectedBeatsConnecting() =>
        Assert.AreEqual(
            SessionStatus.Connected,
            new SessionStatusRegistry().Merge(Profile, [SessionStatus.Connecting, SessionStatus.Connected]));

    [TestMethod]
    public void ConnectingBeatsError() =>
        Assert.AreEqual(
            SessionStatus.Connecting,
            new SessionStatusRegistry().Merge(Profile, [SessionStatus.Error, SessionStatus.Connecting]));

    [TestMethod]
    public void ErrorBeatsDisconnected() =>
        Assert.AreEqual(
            SessionStatus.Error,
            new SessionStatusRegistry().Merge(Profile, [SessionStatus.Disconnected, SessionStatus.Error]));

    [TestMethod]
    public void TheMergeDoesNotDependOnOrder()
    {
        // 事件到达顺序是不可控的;规则若与顺序有关,就等于把 bug 写进了设计。
        SessionStatusRegistry registry = new();
        SessionStatus[] statuses = [SessionStatus.Connected, SessionStatus.Connecting, SessionStatus.Error];

        Assert.AreEqual(registry.Merge(Profile, statuses), registry.Merge(Profile, statuses.Reverse()));
    }

    /// <summary>文档型会话与终端标签一起参与合并。</summary>
    /// <remarks>
    /// 它们原先各自直接往树上写、最后一次说了算 —— 于是"点快了开出两个 FTP 标签,
    /// 关掉一个,圆点就灭了"(明明还有一个活着)。
    /// </remarks>
    [TestMethod]
    public void ADocumentSessionKeepsTheDotLitWithNoTabsAtAll()
    {
        SessionStatusRegistry registry = new();
        registry.Track(Guid.NewGuid(), Profile, SessionStatus.Connected);

        Assert.AreEqual(SessionStatus.Connected, registry.Merge(Profile, []));
    }

    [TestMethod]
    public void ClosingOneOfTwoDocumentsLeavesTheDotLit()
    {
        SessionStatusRegistry registry = new();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        registry.Track(first, Profile, SessionStatus.Connected);
        registry.Track(second, Profile, SessionStatus.Connected);

        registry.Forget(first);

        Assert.AreEqual(SessionStatus.Connected, registry.Merge(Profile, []),
            "还有一个文档活着,圆点不该灭。");
    }

    [TestMethod]
    public void ClosingTheLastOneGoesBackToDisconnected()
    {
        SessionStatusRegistry registry = new();
        Guid only = Guid.NewGuid();
        registry.Track(only, Profile, SessionStatus.Connected);

        registry.Forget(only);

        Assert.AreEqual(SessionStatus.Disconnected, registry.Merge(Profile, []));
    }

    [TestMethod]
    public void OtherProfilesAreNotMixedIn()
    {
        SessionStatusRegistry registry = new();
        registry.Track(Guid.NewGuid(), Guid.NewGuid(), SessionStatus.Connected);

        Assert.AreEqual(SessionStatus.Disconnected, registry.Merge(Profile, []));
    }

    /// <summary>已经摘掉的会话不会被一条迟到的状态事件复活。</summary>
    /// <remarks>
    /// FTP 的失效是在下一次操作时才暴露的,所以文档关掉之后仍可能收到状态事件。
    /// 照单全收会把刚灭掉的圆点重新点亮 —— 而用户明明已经关掉了那个标签。
    /// </remarks>
    [TestMethod]
    public void ALateEventDoesNotResurrectAClosedSession()
    {
        SessionStatusRegistry registry = new();
        Guid session = Guid.NewGuid();
        registry.Track(session, Profile, SessionStatus.Connected);
        registry.Forget(session);

        Guid? affected = registry.Update(session, SessionStatus.Connected);

        Assert.IsNull(affected, "不在册的会话不该触发刷新。");
        Assert.AreEqual(SessionStatus.Disconnected, registry.Merge(Profile, []));
    }

    [TestMethod]
    public void UpdatingAnOpenSessionMovesTheDot()
    {
        SessionStatusRegistry registry = new();
        Guid session = Guid.NewGuid();
        registry.Track(session, Profile, SessionStatus.Connected);

        Guid? affected = registry.Update(session, SessionStatus.Disconnected);

        Assert.AreEqual(Profile, affected);
        Assert.AreEqual(SessionStatus.Disconnected, registry.Merge(Profile, []));
    }

    [TestMethod]
    public void ForgettingSomethingNeverTrackedIsHarmless()
    {
        // 关闭路径与协议自己的 Closed 事件都会走到这里,谁先到都行 —— 必须幂等。
        SessionStatusRegistry registry = new();
        Guid session = Guid.NewGuid();
        registry.Track(session, Profile, SessionStatus.Connected);

        Assert.AreEqual(Profile, registry.Forget(session));
        Assert.IsNull(registry.Forget(session), "第二次摘除是空操作。");
    }

    [TestMethod]
    public void EmptyIdentifiersAreRejected()
    {
        // 没有 profile id 的连接(临时会话)不该在树上占一个位置。
        SessionStatusRegistry registry = new();

        Assert.IsNull(registry.Track(Guid.Empty, Profile, SessionStatus.Connected));
        Assert.IsNull(registry.Track(Guid.NewGuid(), Guid.Empty, SessionStatus.Connected));
        Assert.AreEqual(SessionStatus.Disconnected, registry.Merge(Profile, []));
    }
}
