using VelaShell.Docking.Model;

namespace VelaShell.Tests.Docking;

/// <summary>
/// 关闭拦截器(<see cref="DockWorkspace.CloseInterceptor" />)——「关闭已连接标签前确认」的落脚点。
/// </summary>
/// <remarks>
/// 关键点是**六个入口一处管住**:标签 ×、Ctrl+W、命令面板,以及右键的
/// 关闭其他 / 关闭全部 / 关闭左侧 / 关闭右侧。后四个是批量的,一次静默关掉半屏已连接会话
/// 正是这道闸要防的事故,所以每一个都单独钉一条。
/// </remarks>
[TestClass]
[TestCategory("Docking")]
public sealed class DockCloseInterceptorTests
{
    private sealed class TestDocument(string title) : DockDocument
    {
        public string Name { get; } = title;

        public override string ToString() => Name;
    }

    private static TestDocument NewDoc(string title) => new(title) { Title = title, CanClose = true };

    /// <summary>建一个装了拦截器的工作区,返回工作区、四个文档、以及拦截器收到的批次。</summary>
    private static (DockWorkspace Workspace, TestDocument[] Docs, List<IReadOnlyList<DockDocument>> Seen)
        Setup(bool allow)
    {
        var ws = new DockWorkspace();
        TestDocument[] docs = [NewDoc("a"), NewDoc("b"), NewDoc("c"), NewDoc("d")];
        foreach (TestDocument doc in docs)
        {
            ws.AddDocument(doc);
        }
        List<IReadOnlyList<DockDocument>> seen = [];
        ws.CloseInterceptor = batch =>
        {
            seen.Add(batch);
            return Task.FromResult(allow);
        };
        return (ws, docs, seen);
    }

    [TestMethod]
    public void WithNoInterceptor_RequestClose_ClosesImmediately()
    {
        var ws = new DockWorkspace();
        TestDocument a = NewDoc("a");
        ws.AddDocument(a);
        DockDocument? closed = null;
        ws.DocumentClosed += d => closed = d;

        ws.RequestClose(a);

        Assert.AreSame(a, closed);
        Assert.IsEmpty(ws.PrimaryGroup.Documents);
    }

    [TestMethod]
    public void Interceptor_ReturningFalse_LeavesTheDocumentOpen()
    {
        (DockWorkspace ws, TestDocument[] docs, _) = Setup(allow: false);
        bool closedRaised = false;
        ws.DocumentClosed += _ => closedRaised = true;

        ws.RequestClose(docs[0]);

        Assert.HasCount(4, ws.PrimaryGroup.Documents, "拦截器拒绝时文档必须原样留着。");
        Assert.IsFalse(closedRaised, "被拦下的关闭不该触发 DocumentClosed —— 下游会据此断 SSH。");
    }

    [TestMethod]
    public void Interceptor_ReturningTrue_ClosesTheDocument()
    {
        (DockWorkspace ws, TestDocument[] docs, _) = Setup(allow: true);

        ws.RequestClose(docs[0]);

        Assert.HasCount(3, ws.PrimaryGroup.Documents);
    }

    [TestMethod]
    public void CloseDocument_BypassesTheInterceptor()
    {
        // 程序性关闭(连接失败撤标签、退出清场)绝不能被确认框挡住。
        (DockWorkspace ws, TestDocument[] docs, List<IReadOnlyList<DockDocument>> seen) = Setup(allow: false);

        ws.CloseDocument(docs[0]);

        Assert.HasCount(3, ws.PrimaryGroup.Documents);
        Assert.IsEmpty(seen, "CloseDocument 是无条件的,不该惊动拦截器。");
    }

    [TestMethod]
    public void CloseOtherDocuments_GoesThroughTheInterceptor_AsOneBatch()
    {
        (DockWorkspace ws, TestDocument[] docs, List<IReadOnlyList<DockDocument>> seen) = Setup(allow: false);

        ws.CloseOtherDocuments(docs[1]);

        Assert.HasCount(4, ws.PrimaryGroup.Documents);
        Assert.HasCount(1, seen, "批量关闭应当只问一次,而不是逐个弹框。");
        Assert.HasCount(3, seen[0]);
    }

    [TestMethod]
    public void CloseAllDocuments_GoesThroughTheInterceptor()
    {
        (DockWorkspace ws, TestDocument[] docs, List<IReadOnlyList<DockDocument>> seen) = Setup(allow: false);

        ws.CloseAllDocuments(docs[0]);

        Assert.HasCount(4, ws.PrimaryGroup.Documents);
        Assert.HasCount(1, seen);
        Assert.HasCount(4, seen[0]);
    }

    [TestMethod]
    public void CloseLeftDocuments_GoesThroughTheInterceptor()
    {
        // 原方案漏掉了「关闭左侧/右侧」,这两条正是为此而立。
        (DockWorkspace ws, TestDocument[] docs, List<IReadOnlyList<DockDocument>> seen) = Setup(allow: false);

        ws.CloseLeftDocuments(docs[2]);

        Assert.HasCount(4, ws.PrimaryGroup.Documents, "「关闭左侧」也必须过确认闸。");
        Assert.HasCount(1, seen);
        Assert.HasCount(2, seen[0]);
    }

    [TestMethod]
    public void CloseRightDocuments_GoesThroughTheInterceptor()
    {
        (DockWorkspace ws, TestDocument[] docs, List<IReadOnlyList<DockDocument>> seen) = Setup(allow: false);

        ws.CloseRightDocuments(docs[1]);

        Assert.HasCount(4, ws.PrimaryGroup.Documents, "「关闭右侧」也必须过确认闸。");
        Assert.HasCount(1, seen);
        Assert.HasCount(2, seen[0]);
    }

    [TestMethod]
    public void AllowedBulkClose_ClosesEveryTarget()
    {
        (DockWorkspace ws, TestDocument[] docs, _) = Setup(allow: true);

        ws.CloseOtherDocuments(docs[1]);

        Assert.AreSequenceEqual([docs[1]], ws.PrimaryGroup.Documents.ToArray());
    }

    [TestMethod]
    public void RequestCloseMany_WithAnEmptyBatch_DoesNotAsk()
    {
        (DockWorkspace ws, _, List<IReadOnlyList<DockDocument>> seen) = Setup(allow: true);

        ws.RequestCloseMany([]);

        Assert.IsEmpty(seen, "没有目标就不该弹确认框。");
    }
}
