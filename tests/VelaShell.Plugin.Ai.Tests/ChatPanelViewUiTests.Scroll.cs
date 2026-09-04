using Avalonia;
using Avalonia.Controls;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// "跳到末尾"圆钮:滚上去看旧消息时右下角显形,回到底部即收起。
/// </summary>
public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void JumpToBottom_ShowsWhenScrolledUp_HidesAtBottom()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                var scroll = Find<ScrollViewer>(panel, "ChatScroll");
                var messages = (StackPanel)scroll.Content!;
                Border jump = Find<Border>(panel, "JumpToBottomButton");

                // 空对话、内容不超一屏:钮不该出现。
                Assert.IsFalse(jump.IsVisible, "内容没超出一屏时不该显示跳到末尾");

                // 塞进远超一屏(窗口 600px 高)的内容,让消息流可滚动。
                for (int i = 0; i < 60; i++)
                {
                    messages.Children.Add(new TextBlock { Text = $"line {i}", MinHeight = 40 });
                }
                await PumpAsync();

                // 滚到顶(离底很远):钮显形。
                scroll.Offset = new Vector(0, 0);
                await PumpAsync();
                Assert.IsTrue(jump.IsVisible, "滚上去之后应显示跳到末尾");

                // 回到底:钮收起。
                scroll.ScrollToEnd();
                await PumpAsync();
                Assert.IsFalse(jump.IsVisible, "回到底部后跳到末尾应收起");
            }
            finally
            {
                window.Close();
            }
        });
    }
}
