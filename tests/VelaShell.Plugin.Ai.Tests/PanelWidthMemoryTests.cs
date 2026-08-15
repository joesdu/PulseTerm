using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 侧栏宽度:没有设置项,由用户拖分割条决定 —— 拖完记下来,下次打开还是那个宽度。
/// </summary>
/// <remarks>
/// 让人去设置页填一个百分比,不如直接把他拖出来的结果记住(用户要求)。
/// 宿主在拖动<b>结束</b>时通知一次(<see cref="IPluginPanel.PlacementRatioChanged" />),
/// 所以不必防抖。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class PanelWidthMemoryTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PanelWidthMemoryTests).Assembly);

    /// <summary>见 <c>ChatPanelViewUiTests.OnUi</c>:必须带返回值,否则测试体不会被等到底。</summary>
    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task PumpAsync(int rounds = 40)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>激活插件、跑"打开聊天(标签页)"命令,并把面板真的挂进窗口。</summary>
    private static async Task<(FakePanel Panel, ChatPanelView View, Window Window)> OpenChatAsync(TestPluginContext context)
    {
        // 和真实宿主一样立刻造内容:插件就是在工厂里拿到那个视图引用的,
        // 惰性工厂会让它一直是 null,测出来的就不是真实路径了。
        context.FakeUi.CreateContentEagerly = true;
        var plugin = new AiPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        await context.RecordingCommands.RunAsync($"{context.PluginId}.chat");
        FakePanel panel = context.FakeUi.LastPanel;
        var view = (ChatPanelView)panel.CreateContent();
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        await PumpAsync();
        return (panel, view, window);
    }

    [TestMethod]
    public void DefaultsTo30Percent()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (FakePanel panel, ChatPanelView view, Window window) = await OpenChatAsync(context);
            try
            {
                Assert.AreEqual(0.30, panel.Options.PlacementRatio, 0.001, "没拖过就是默认的 30%");
            }
            finally
            {
                view.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 拖完记住,而且<b>扛得住面板后续的整体保存</b>。
    /// </summary>
    /// <remarks>
    /// 这是真正踩过的坑:一开始是绕过面板 Load-改-Save 直接写库,面板内存里还揣着旧宽度,
    /// 下一次换模式/勾工具就把它整体覆盖回去了 —— 用户看到的就是"拖了不算数,重开还是老样子"。
    /// 所以这里在"拖动"之后额外触发一次面板自己的保存,再验值还在。
    /// </remarks>
    [TestMethod]
    public void RemembersTheDraggedWidth_EvenAfterThePanelSavesItsOwnSettings()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (FakePanel panel, ChatPanelView view, Window window) = await OpenChatAsync(context);
            try
            {
                panel.RaisePlacementRatioChanged(0.42);
                await PumpAsync();

                // 面板自己的一次整体保存(换对话模式就会走这条路)
                view.GetControl<ComboBox>("ModeCombo").SelectedIndex = (int)ChatMode.Agent;
                await PumpAsync();

                AiSettings saved = await new AiSettingsStore(context).LoadAsync();
                Assert.AreEqual(42, saved.PanelWidthPercent, "拖出来的宽度不能被面板的整体保存盖掉");
                Assert.AreEqual(ChatMode.Agent, saved.Mode, "同一次保存里模式也要落下去");
            }
            finally
            {
                view.Detach();
                window.Close();
            }
        });
    }

    /// <summary>拖到极端位置也得落在宿主认的区间里,否则下次打开会被夹一次、看着像"没记住"。</summary>
    [TestMethod]
    public void ClampsToTheRangeTheHostAccepts()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (FakePanel panel, ChatPanelView view, Window window) = await OpenChatAsync(context);
            try
            {
                panel.RaisePlacementRatioChanged(0.93);
                await PumpAsync();

                AiSettings saved = await new AiSettingsStore(context).LoadAsync();
                Assert.AreEqual(85, saved.PanelWidthPercent);
            }
            finally
            {
                view.Detach();
                window.Close();
            }
        });
    }

    /// <summary>下一次打开用的就是记住的那个宽度。</summary>
    [TestMethod]
    public void ReopeningUsesTheRememberedWidth()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await new AiSettingsStore(context).SaveAsync(new AiSettings { PanelWidthPercent = 55 });
            (FakePanel panel, ChatPanelView view, Window window) = await OpenChatAsync(context);
            try
            {
                Assert.AreEqual(0.55, panel.Options.PlacementRatio, 0.001);
            }
            finally
            {
                view.Detach();
                window.Close();
            }
        });
    }
}
