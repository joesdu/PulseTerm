using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 侧栏宽度:没有设置项,由用户拖分割条决定 —— 拖完记下来,下次打开还是那个宽度。
/// </summary>
/// <remarks>
/// 让人去设置页填一个百分比,不如直接把他拖出来的结果记住(用户要求)。
/// 宿主在拖动<b>结束</b>时通知一次(<see cref="IPluginPanel.PlacementRatioChanged" />),
/// 所以插件直接落盘,不必防抖。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class PanelWidthMemoryTests
{
    /// <summary>激活插件并跑一次"打开聊天(标签页)"命令,返回那次开出来的假面板。</summary>
    private static async Task<(TestPluginContext Context, FakePanel Panel)> OpenChatAsync()
    {
        var context = new TestPluginContext();
        var plugin = new AiPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        await context.RecordingCommands.RunAsync($"{context.PluginId}.chat");
        return (context, context.FakeUi.LastPanel);
    }

    [TestMethod]
    public async Task DefaultsTo30Percent()
    {
        (TestPluginContext context, FakePanel panel) = await OpenChatAsync();
        using (context)
        {
            Assert.AreEqual(0.30, panel.Options.PlacementRatio, 0.001, "没拖过就是默认的 30%");
        }
    }

    [TestMethod]
    public async Task RemembersTheWidthTheUserDraggedTo()
    {
        (TestPluginContext context, FakePanel panel) = await OpenChatAsync();
        using (context)
        {
            panel.RaisePlacementRatioChanged(0.42);
            await WaitForWidthAsync(context, 42);

            AiSettings saved = await new AiSettingsStore(context).LoadAsync();
            Assert.AreEqual(42, saved.PanelWidthPercent, "拖出来的宽度要记进配置");
        }
    }

    /// <summary>拖到极端位置也得落在宿主认的区间里,否则下次打开会被夹一次、看着像"没记住"。</summary>
    [TestMethod]
    public async Task ClampsToTheRangeTheHostAccepts()
    {
        (TestPluginContext context, FakePanel panel) = await OpenChatAsync();
        using (context)
        {
            panel.RaisePlacementRatioChanged(0.93);
            await WaitForWidthAsync(context, 85);

            AiSettings saved = await new AiSettingsStore(context).LoadAsync();
            Assert.AreEqual(85, saved.PanelWidthPercent);
        }
    }

    /// <summary>下一次打开用的就是记住的那个宽度。</summary>
    [TestMethod]
    public async Task ReopeningUsesTheRememberedWidth()
    {
        using var context = new TestPluginContext();
        await new AiSettingsStore(context).SaveAsync(new AiSettings { PanelWidthPercent = 55 });

        var plugin = new AiPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        await context.RecordingCommands.RunAsync($"{context.PluginId}.chat");

        Assert.AreEqual(0.55, context.FakeUi.LastPanel.Options.PlacementRatio, 0.001);
    }

    /// <summary>落盘是 fire-and-forget 的,等它写完(或超时)。</summary>
    private static async Task WaitForWidthAsync(TestPluginContext context, int expected)
    {
        var store = new AiSettingsStore(context);
        for (int i = 0; i < 100; i++)
        {
            if ((await store.LoadAsync()).PanelWidthPercent == expected)
            {
                return;
            }
            await Task.Delay(10);
        }
    }
}
