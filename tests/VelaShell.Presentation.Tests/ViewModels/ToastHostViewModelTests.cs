using VelaShell.Presentation.Tests.Fakes;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.Tests.ViewModels;

/// <summary>
/// 浮层提示的堆叠、分级与自动消失。
/// </summary>
/// <remarks>
/// 时间由 <c>FakeClock</c> 驱动,不是 <c>Task.Delay</c>:自动消失的时机是这个类唯一值得测的行为,
/// 挂在真实时钟上就只能靠睡眠去赌,那种用例迟早会成为下一条偶发失败。
/// </remarks>
[TestClass]
public sealed class ToastHostViewModelTests
{
    [TestMethod]
    public void InfoDisappearsOnItsOwn()
    {
        FakeClock clock = new();
        using ToastHostViewModel host = new(clock.Schedule);
        host.Info("已导出");
        Assert.HasCount(1, host.Toasts);
        Assert.IsTrue(host.HasToasts);

        clock.Advance(ToastHostViewModel.InfoLifetime + TimeSpan.FromMilliseconds(1));

        Assert.IsEmpty(host.Toasts);
        Assert.IsFalse(host.HasToasts);
    }

    [TestMethod]
    public void ErrorOutlivesWarningButStillLeavesOnItsOwn()
    {
        FakeClock clock = new();
        // 一条转瞬即逝的错误与没报错没有区别 —— 所以留得最久;
        // 但要人伸手去点才肯走的浮层,在用户眼里和一个卡住的弹窗没有分别。
        using ToastHostViewModel host = new(clock.Schedule);
        host.Error("连接失败");

        clock.Advance(ToastHostViewModel.WarningLifetime + TimeSpan.FromMilliseconds(1));
        Assert.HasCount(1, host.Toasts, "错误该比警告留得久。");

        clock.Advance(
            ToastHostViewModel.ErrorLifetime - ToastHostViewModel.WarningLifetime);
        Assert.IsEmpty(host.Toasts, "错误留得久,但不该赖着不走。");
    }

    [TestMethod]
    public void ErrorCanStillBeDismissedEarly()
    {
        FakeClock clock = new();
        using ToastHostViewModel host = new(clock.Schedule);
        ToastViewModel toast = host.Error("连接失败");

        host.Dismiss(toast);

        Assert.IsEmpty(host.Toasts);
    }

    [TestMethod]
    public void WarningOutlivesInfo()
    {
        FakeClock clock = new();
        using ToastHostViewModel host = new(clock.Schedule);
        host.Info("信息");
        host.Warning("警告");

        clock.Advance(ToastHostViewModel.InfoLifetime + TimeSpan.FromMilliseconds(1));

        Assert.HasCount(1, host.Toasts);
        Assert.AreEqual("警告", host.Toasts[0].Message);
    }

    [TestMethod]
    public void TheNewestToastComesFirst()
    {
        FakeClock clock = new();
        using ToastHostViewModel host = new(clock.Schedule);
        host.Error("第一条");
        host.Error("第二条");

        Assert.AreEqual("第二条", host.Toasts[0].Message);
        Assert.AreEqual("第一条", host.Toasts[1].Message);
    }

    [TestMethod]
    public void SameKeyUpdatesInPlaceInsteadOfStacking()
    {
        FakeClock clock = new();
        // 自动重连倒计时逐秒刷新:不合并的话十秒之内会堆出十条几乎一样的提示,
        // 把别的消息全挤出屏幕。
        using ToastHostViewModel host = new(clock.Schedule);
        host.Warning("3 秒后重连", mergeKey: "reconnect:a");
        host.Warning("2 秒后重连", mergeKey: "reconnect:a");
        host.Warning("1 秒后重连", mergeKey: "reconnect:a");

        Assert.HasCount(1, host.Toasts);
        Assert.AreEqual("1 秒后重连", host.Toasts[0].Message);
    }

    [TestMethod]
    public void DifferentKeysStackSeparately()
    {
        FakeClock clock = new();
        // 三个标签同时掉线时各占一条,互不覆盖 —— 状态栏那一个字符串做不到这件事。
        using ToastHostViewModel host = new(clock.Schedule);
        host.Warning("标签 A 重连中", mergeKey: "reconnect:a");
        host.Warning("标签 B 重连中", mergeKey: "reconnect:b");

        Assert.HasCount(2, host.Toasts);
    }

    [TestMethod]
    public void MergingRestartsTheCountdown()
    {
        FakeClock clock = new();
        using ToastHostViewModel host = new(clock.Schedule);
        host.Warning("重连中", mergeKey: "k");
        clock.Advance(ToastHostViewModel.WarningLifetime - TimeSpan.FromMilliseconds(100));

        host.Warning("还在重连", mergeKey: "k");
        clock.Advance(TimeSpan.FromMilliseconds(200));

        Assert.HasCount(1, host.Toasts, "刷新过的消息应当从头计时,而不是沿用旧的到期时刻。");
    }

    [TestMethod]
    public void TheOldestIsPushedOutWhenTheStackIsFull()
    {
        FakeClock clock = new();
        // 一次批量操作失败能刷出几十条,把整个窗口盖住 —— 那比看不见提示更糟。
        using ToastHostViewModel host = new(clock.Schedule);
        for (int i = 0; i < ToastHostViewModel.MaxVisible + 2; i++)
        {
            host.Error($"第 {i} 条");
        }

        Assert.HasCount(ToastHostViewModel.MaxVisible, host.Toasts);
        Assert.AreEqual($"第 {ToastHostViewModel.MaxVisible + 1} 条", host.Toasts[0].Message);
        Assert.IsFalse(host.Toasts.Any(t => t.Message == "第 0 条"), "最老的那条应当被挤掉。");
    }

    [TestMethod]
    public void AnActionRunsAndThenTheToastGoesAway()
    {
        FakeClock clock = new();
        using ToastHostViewModel host = new(clock.Schedule);
        int ran = 0;
        ToastViewModel toast = host.Error("已断开", "立即重连", () => ran++);
        Assert.IsTrue(toast.HasAction);

        ((System.Windows.Input.ICommand)host.InvokeCommand).Execute(toast);

        Assert.AreEqual(1, ran);
        Assert.IsEmpty(host.Toasts, "按钮多半是一次性的,执行完留在屏幕上只会让人怀疑到底点没点上。");
    }

    /// <summary>一个抛异常的操作既不该把提示卡住,也不该把命令本身弄坏。</summary>
    /// <remarks>
    /// <c>ReactiveCommand</c> 遇到未处理异常会打断自己的管道 ——
    /// 那意味着**之后每一条提示**的按钮全部失效,而现场只表现为"按钮点了没反应"。
    /// 所以第二次执行必须照样有效,这条用例的后半段就是为它准备的。
    /// </remarks>
    [TestMethod]
    public void AThrowingActionNeitherStrandsTheToastNorBreaksTheCommand()
    {
        FakeClock clock = new();
        using ToastHostViewModel host = new(clock.Schedule);
        ToastViewModel bad = host.Error("失败", "重试", () => throw new InvalidOperationException("boom"));

        ((System.Windows.Input.ICommand)host.InvokeCommand).Execute(bad);
        Assert.IsEmpty(host.Toasts, "抛异常的操作照样要把提示收掉,否则浮层永远撤不掉。");

        int ran = 0;
        ToastViewModel good = host.Error("再来一条", "重试", () => ran++);
        ((System.Windows.Input.ICommand)host.InvokeCommand).Execute(good);

        Assert.AreEqual(1, ran, "命令的管道被上一次异常打断了 —— 之后所有提示的按钮都会失效。");
    }

    [TestMethod]
    public void AnActionWithoutALabelIsNotOffered()
    {
        // 有回调没文案 = 一个画不出来的按钮。把这种半配置归成"没有操作",
        // 免得视图那边出现一个空白按钮。
        ToastViewModel toast = new(ToastSeverity.Error, "消息", actionLabel: null, action: () => { });

        Assert.IsFalse(toast.HasAction);
    }

    [TestMethod]
    public void DismissingSomethingAlreadyGoneIsHarmless()
    {
        FakeClock clock = new();
        // 自动消失与用户点关闭会撞在一起,这条路径必须是幂等的。
        using ToastHostViewModel host = new(clock.Schedule);
        ToastViewModel toast = host.Info("消息");
        clock.Advance(ToastHostViewModel.InfoLifetime + TimeSpan.FromMilliseconds(1));

        host.Dismiss(toast);
        host.Dismiss(null);

        Assert.IsEmpty(host.Toasts);
    }
}
