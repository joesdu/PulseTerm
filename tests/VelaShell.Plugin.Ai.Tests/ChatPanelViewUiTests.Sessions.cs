using Avalonia.Controls;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 顶栏会话下拉的两条新行为:一是显示<b>连接的名字</b>而不是 user@host;
/// 二是每台机器各持一份对话 —— 切下拉即换那份的消息面板,切回来还是原来那一份(状态不丢、不串台)。
/// </summary>
public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void SessionCombo_ShowsConnectionName_NotIp()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            // 已保存配置里给这台机器起了名;在途会话本身只带 主机/端口/用户。
            context.FakeSessions.AddSaved(name: "我的生产机", host: "10.0.0.1", username: "root");
            context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root");

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ComboBox combo = Find<ComboBox>(panel, "SessionCombo");
                List<SessionNavItem> items = [.. combo.ItemsSource!.Cast<SessionNavItem>()];
                Assert.HasCount(1, items);
                Assert.AreEqual("我的生产机", items[0].Text, "下拉该显示连接名,而不是 root@10.0.0.1");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void SessionCombo_FallsBackToUserAtHost_WhenNoSavedName()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            // 没有对得上的已保存配置(临时会话):退回 user@host,总有个能认的落点。
            context.FakeSessions.AddConnected(host: "10.0.0.9", username: "root");

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ComboBox combo = Find<ComboBox>(panel, "SessionCombo");
                List<SessionNavItem> items = [.. combo.ItemsSource!.Cast<SessionNavItem>()];
                Assert.HasCount(1, items);
                Assert.AreEqual("root@10.0.0.9", items[0].Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [TestMethod]
    public void SwitchingSession_SwapsConversationPanel_AndPreservesState()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            context.FakeSessions.AddConnected(host: "10.0.0.1", username: "root"); // A(下拉第 0 项)
            context.FakeSessions.AddConnected(host: "10.0.0.2", username: "root"); // B(下拉第 1 项)

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ScrollViewer scroll = Find<ScrollViewer>(panel, "ChatScroll");
                ComboBox combo = Find<ComboBox>(panel, "SessionCombo");

                // 初始选中第 0 台(A):记下它的消息面板,并在里面放一枚标记,模拟"A 里已经聊了内容"。
                var panelA = (StackPanel)scroll.Content!;
                panelA.Children.Add(new TextBlock { Text = "marker-A" });

                // 切到 B:消息面板应换成另一条(A 的内容不该出现在 B 里 —— 不串台)。
                combo.SelectedIndex = 1;
                await PumpAsync();
                var panelB = (StackPanel)scroll.Content!;
                Assert.AreNotSame(panelA, panelB, "每台机器各有自己的消息面板");
                Assert.DoesNotContain(
                    t => t.Text == "marker-A", panelB.Children.OfType<TextBlock>(),
                    "B 的面板里不该看到 A 的内容");

                // 切回 A:应回到<b>同一条</b>面板,里面的标记还在(状态没丢)。
                combo.SelectedIndex = 0;
                await PumpAsync();
                Assert.AreSame(panelA, scroll.Content, "切回同一台机器要回到同一份对话面板");
                Assert.Contains(
                    t => t.Text == "marker-A", panelA.Children.OfType<TextBlock>(),
                    "切走再切回,A 里的内容应原样还在");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
