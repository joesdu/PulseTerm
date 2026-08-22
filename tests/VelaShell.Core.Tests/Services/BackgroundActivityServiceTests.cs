using VelaShell.Core.Services;

namespace VelaShell.Core.Tests.Services;

/// <summary>
/// 后台活动账本(状态栏右下角圆环的数据来源)。
/// 这些用例守的是同一件事:圆环绝不会因为某条活动没被正确收尾而一直转下去。
/// </summary>
[TestClass]
[TestCategory("BackgroundActivity")]
public sealed class BackgroundActivityServiceTests
{
    [TestMethod]
    public void Begin_PublishesActivity_AndDisposeRemovesIt()
    {
        using var service = new BackgroundActivityService();

        IBackgroundActivityScope scope = service.Begin("正在加载插件", "Redis Client");
        Assert.HasCount(1, service.Activities);
        Assert.AreEqual("正在加载插件", service.Activities[0].Title);
        Assert.AreEqual("Redis Client", service.Activities[0].Detail);
        Assert.IsNull(service.Activities[0].Progress, "未指定进度的活动应报告为不确定。");

        scope.Dispose();
        Assert.IsEmpty(service.Activities);
    }

    [TestMethod]
    public void Dispose_IsIdempotent_AndNeverEvictsAnotherActivity()
    {
        using var service = new BackgroundActivityService();
        IBackgroundActivityScope first = service.Begin("甲");
        using IBackgroundActivityScope second = service.Begin("乙");

        first.Dispose();
        first.Dispose(); // 重复释放不得把"乙"顶掉

        Assert.HasCount(1, service.Activities);
        Assert.AreEqual("乙", service.Activities[0].Title);
    }

    [TestMethod]
    public void Report_UpdatesProgressAndDetail_AndClampsOutOfRangeValues()
    {
        using var service = new BackgroundActivityService();
        using IBackgroundActivityScope scope = service.Begin("正在校验插件", progress: 0);

        scope.Report(0.5, "Redis Client");
        Assert.AreEqual(0.5, service.Activities[0].Progress);
        Assert.AreEqual("Redis Client", service.Activities[0].Detail);

        scope.Report(7.5);
        Assert.AreEqual(1, service.Activities[0].Progress, "越界进度应被夹到 1。");
        Assert.AreEqual("Redis Client", service.Activities[0].Detail, "未传 detail 时不应清空既有副标题。");

        scope.Report(double.NaN);
        Assert.IsNull(service.Activities[0].Progress, "非有限进度应退回不确定。");
    }

    [TestMethod]
    public void Report_AfterDispose_IsIgnored()
    {
        using var service = new BackgroundActivityService();
        IBackgroundActivityScope scope = service.Begin("甲");
        scope.Dispose();

        scope.Report(0.5, "不该出现");

        Assert.IsEmpty(service.Activities, "已结束的活动不得因迟到的上报复活。");
    }

    [TestMethod]
    public void Changed_FiresImmediately_OnStructuralChange()
    {
        using var service = new BackgroundActivityService();
        int notifications = 0;
        service.Changed += () => Interlocked.Increment(ref notifications);

        IBackgroundActivityScope scope = service.Begin("甲");
        Assert.AreEqual(1, notifications, "开始一条活动必须立刻通知 —— 圆环要马上出现。");

        scope.Dispose();
        Assert.AreEqual(2, notifications, "结束一条活动必须立刻通知 —— 圆环要马上消失。");
    }

    [TestMethod]
    public void Changed_SubscriberThrowing_DoesNotPropagateToTheReporter()
    {
        using var service = new BackgroundActivityService();
        service.Changed += () => throw new InvalidOperationException("视图层炸了");

        // 订阅方的异常绝不能回灌到上报活动的后台工作里 —— 那会把插件装载判成失败。
        using IBackgroundActivityScope scope = service.Begin("甲");
        Assert.HasCount(1, service.Activities);
    }

    [TestMethod]
    public void Describe_RewritesTitleAndDetail()
    {
        using var service = new BackgroundActivityService();
        using IBackgroundActivityScope scope = service.Begin("正在校验插件", "甲");

        scope.Describe("正在预热插件", "乙");

        Assert.AreEqual("正在预热插件", service.Activities[0].Title);
        Assert.AreEqual("乙", service.Activities[0].Detail);
    }

    [TestMethod]
    public void ConcurrentScopes_AllSettleToEmpty()
    {
        using var service = new BackgroundActivityService();

        Parallel.For(0, 64, i =>
        {
            using IBackgroundActivityScope scope = service.Begin($"活动 {i}");
            scope.Report(i / 64d, $"细节 {i}");
        });

        Assert.IsEmpty(service.Activities, "并发开始/结束后账本必须归零,否则圆环会一直转。");
    }
}
