using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 边跑边补的面板接线:一轮还没答完时按回车,消息排队而不是被"正忙"吞掉,
/// 并且真的会发出去(见 ChatPanelView.Steering.cs)。
/// </summary>
/// <remarks>
/// 这里走的是"一轮结束时队列还没空"的那条路 —— 纯对话模式一轮只发一次请求,
/// 排在流式途中的那句赶不上,于是它作为<b>下一轮</b>整体发出去。
/// 真正的中途插入(函数调用循环每跑一步都送一次)在 <c>SteeringTests</c> 里拿真的循环验。
/// </remarks>
public sealed partial class ChatPanelViewUiTests
{
    /// <summary>
    /// 一段流式回应。本文件的用例都用 <c>hold: true</c> 把它扣在服务端,
    /// 看完"处理中"该看的再 <c>Release()</c> —— 插话的窗口由测试自己开合,不靠延时去赌。
    /// </summary>
    private const string SlowReply = """
    data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"在看了。"},"finish_reason":"stop"}]}

    data: [DONE]


    """;

    private static void PressEnter(TextEditor input) =>
        input.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            Source = input
        });

    private static async Task<(Window Window, ChatPanelView Panel)> ShowWithStubAsync(
        TestPluginContext context, SseStub stub)
    {
        AiProvider provider = StubProvider("stub", stub.BaseUrl, "m");
        await new AiSettingsStore(context).SaveAsync(new AiSettings
        {
            Providers = [provider],
            ActiveModelId = provider.Models[0].Id,
            // 这几个用例只看插话,别让"后续提问"再多发一次请求把断言搅浑
            SuggestFollowUps = false
        });
        return await ShowAsync(context);
    }

    /// <summary>
    /// 处理中按回车 = 排队,不是被吞掉:输入框当场清空、上方多出一枚可撤回的芯片,
    /// 发送键仍在(换成「排队」)而不是被停止键顶掉。等这一轮答完,排队的那句自己发出去。
    /// </summary>
    [TestMethod]
    public void EnterWhileBusy_QueuesTheMessage_AndSendsItWhenTheTurnEnds()
    {
        OnUi(async () =>
        {
            using var stub = new SseStub(SlowReply, hold: true);
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowWithStubAsync(context, stub);
            try
            {
                panel.SendExternal("看看日志");
                Button stop = Find<Button>(panel, "StopButton");
                Assert.IsTrue(await WaitForAsync(() => stop.IsVisible, maxRounds: 60), "这一轮该跑起来了");
                Assert.IsTrue(Find<Button>(panel, "SendButton").IsVisible,
                    "忙的时候发送键也得留着 —— 它此刻是「排队」");

                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = "只看最近一小时的";
                PressEnter(input);
                await PumpAsync(10);

                WrapPanel queued = Find<WrapPanel>(panel, "QueuedBar");
                Assert.IsTrue(queued.IsVisible, "排队的那句要在输入框上方看得见");
                Assert.HasCount(1, queued.Children);
                Assert.IsEmpty(input.Text, "排完队输入框就该空了,否则用户会再敲一次回车");
                Assert.HasCount(1, stub.Requests, "不许打断正在跑的这一次请求");

                // 排队的都看完了,这才让这一轮答完
                stub.Release();
                Assert.IsTrue(await WaitForAsync(() => stub.Requests.Count >= 2, maxRounds: 200),
                    "这一轮答完,排队的那句该自己发出去");
                Assert.Contains("只看最近一小时的", stub.Requests[1],
                    "第二次请求里必须带着那句补充");
                Assert.IsFalse(queued.IsVisible, "送出去之后芯片就该收掉");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 这一轮被按停时,排着的那句原样回到输入框 —— 不替用户自动再发一次:
    /// 按停止本身就是"我要改主意",刚失败的那次多半也还会失败。
    /// </summary>
    [TestMethod]
    public void StoppingTheTurn_PutsTheQueuedMessageBackInTheBox()
    {
        OnUi(async () =>
        {
            // 挂住不回:这一轮要被按停,回应压根不需要来
            using var stub = new SseStub(SlowReply, hold: true);
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowWithStubAsync(context, stub);
            try
            {
                panel.SendExternal("看看日志");
                Button stop = Find<Button>(panel, "StopButton");
                Assert.IsTrue(await WaitForAsync(() => stop.IsVisible, maxRounds: 60), "这一轮该跑起来了");

                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = "算了,先看磁盘";
                PressEnter(input);
                await PumpAsync(10);
                Assert.IsTrue(Find<WrapPanel>(panel, "QueuedBar").IsVisible);

                stop.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                Assert.IsTrue(await WaitForAsync(() => input.Text.Contains("算了,先看磁盘"), maxRounds: 120),
                    "停掉之后那句该回到输入框");
                Assert.IsFalse(Find<WrapPanel>(panel, "QueuedBar").IsVisible, "回到输入框了就不该还排着");
                Assert.HasCount(1, stub.Requests, "停掉之后不许再自动发一次");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>点一枚排队芯片就是撤回:这一轮答完也不该再把它发出去。</summary>
    [TestMethod]
    public void ClickingAQueuedChip_TakesTheMessageBack()
    {
        OnUi(async () =>
        {
            using var stub = new SseStub(SlowReply, hold: true);
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowWithStubAsync(context, stub);
            try
            {
                panel.SendExternal("看看日志");
                Assert.IsTrue(await WaitForAsync(() => Find<Button>(panel, "StopButton").IsVisible, maxRounds: 60));

                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.Text = "说错了,撤回";
                PressEnter(input);
                await PumpAsync(10);

                WrapPanel queued = Find<WrapPanel>(panel, "QueuedBar");
                Assert.HasCount(1, queued.Children, "这一句该排在那儿等着被撤回");
                var chip = (Border)queued.Children[0];
                chip.RaiseEvent(new PointerPressedEventArgs(chip, new Pointer(0, PointerType.Mouse, true),
                    chip, default, 0, new PointerPointProperties(), KeyModifiers.None));
                await PumpAsync(5);

                Assert.IsFalse(queued.IsVisible, "撤回之后这一行就该收掉");
                // 撤回完成,这才让这一轮答完 —— 队里已经没人,不该再发第二次
                stub.Release();
                Assert.IsTrue(await WaitForAsync(() => !Find<Button>(panel, "StopButton").IsVisible, maxRounds: 200),
                    "这一轮该正常答完");
                await PumpAsync(20);
                Assert.HasCount(1, stub.Requests, "撤回了就不该再发出去");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }
}
