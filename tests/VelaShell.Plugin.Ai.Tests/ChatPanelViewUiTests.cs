using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 聊天面板的 headless 装载与交互:XAML 真装载一次(Popup/资源引用等编译期看不出的问题在此暴露),
/// 并验证历史开关、输入框 ↑↓ 回溯这两条新接线。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class ChatPanelViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ChatPanelViewUiTests).Assembly);

    /// <summary>把面板挂进一个窗口(控件要拿到 TopLevel 才算真正装载),并等初始化跑完。</summary>
    private static async Task<(Window Window, ChatPanelView Panel)> ShowAsync(TestPluginContext context)
    {
        var panel = new ChatPanelView(context, new AiSettingsStore(context));
        var window = new Window { Width = 800, Height = 600, Content = panel };
        window.Show();
        await PumpAsync();
        return (window, panel);
    }

    /// <summary>面板的初始化是 fire-and-forget 的异步链,跑几拍调度器让它落定。</summary>
    private static async Task PumpAsync(int rounds = 20)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Find<T>(ChatPanelView panel, string name) where T : Control
        => panel.GetControl<T>(name);

    [TestMethod]
    public void Panel_Loads_WithoutHostThemeTokens()
    {
        _session.Dispatch(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                // 顶栏三件套 + 输入区 + 新增的历史区都应在可视树里
                Assert.IsNotNull(Find<ToggleButton>(panel, "HistoryToggle"));
                Assert.IsNotNull(Find<ToggleButton>(panel, "SettingsToggle"));
                Assert.IsNotNull(Find<DockPanel>(panel, "HistoryHost"));
                Assert.IsNotNull(Find<Popup>(panel, "FilePopup"));
                Assert.IsFalse(Find<Popup>(panel, "FilePopup").IsOpen, "没输入 @ 时文件选择器不该弹出");
                Assert.IsTrue(Find<ScrollViewer>(panel, "ChatScroll").IsVisible);
                Assert.IsFalse(Find<DockPanel>(panel, "HistoryHost").IsVisible);
                Assert.IsTrue(Find<ToggleButton>(panel, "HistoryToggle").IsEnabled,
                    "时序能力可用时历史按钮应可点");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void HistoryToggle_SwitchesCentreView_AndListsSavedConversations()
    {
        _session.Dispatch(async () =>
        {
            using var context = new TestPluginContext();
            var store = new ChatHistoryStore(context);
            await store.InitAsync();
            DateTimeOffset created = DateTimeOffset.UtcNow;
            string id = ChatHistoryStore.NewConversationId();
            await store.AppendAsync(id, created, 0, "user", "磁盘满了怎么查?");
            await store.AppendAsync(id, created, 1, "assistant", "先看 df -h。");

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                Find<ToggleButton>(panel, "HistoryToggle").IsChecked = true;
                await PumpAsync();

                Assert.IsTrue(Find<DockPanel>(panel, "HistoryHost").IsVisible);
                Assert.IsFalse(Find<ScrollViewer>(panel, "ChatScroll").IsVisible, "历史与聊天流是二选一");
                StackPanel list = Find<StackPanel>(panel, "HistoryList");
                Assert.HasCount(1, list.Children);
                Assert.Contains("磁盘满了怎么查?",
                    list.Children[0].GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList());

                // 再点一次回到聊天流
                Find<ToggleButton>(panel, "HistoryToggle").IsChecked = false;
                await PumpAsync(3);
                Assert.IsTrue(Find<ScrollViewer>(panel, "ChatScroll").IsVisible);
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void AtSign_OpensRemoteFilePicker_AndEnterInsertsThePath()
    {
        _session.Dispatch(async () =>
        {
            using var context = new TestPluginContext();
            VelaShell.PluginSdk.Sessions.SessionInfo session = context.FakeSessions.AddConnected();
            context.FakeRemoteFs.AddFile(session.SessionId, "/etc/hosts", "127.0.0.1 localhost"u8.ToArray());
            context.FakeRemoteFs.AddFile(session.SessionId, "/etc/hostname", "web-01"u8.ToArray());
            context.FakeRemoteFs.AddDirectory(session.SessionId, "/etc/nginx");

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextBox input = Find<TextBox>(panel, "InputBox");
                input.Focus();
                input.Text = "看看 @/etc/host";
                input.CaretIndex = input.Text.Length;
                await PumpAsync();

                Popup popup = Find<Popup>(panel, "FilePopup");
                Assert.IsTrue(popup.IsOpen, "@ 后应弹出远端文件选择器");
                StackPanel list = Find<StackPanel>(panel, "FileList");
                Assert.HasCount(2, list.Children, "只应列出匹配 host 前缀的两个文件(nginx 目录被过滤掉)");

                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                await PumpAsync();

                Assert.AreEqual("看看 @/etc/hostname ", input.Text, "回车插入完整路径并补空格收尾");
                Assert.IsFalse(popup.IsOpen, "选中文件后弹层收起");
                Assert.IsEmpty(Find<StackPanel>(panel, "MessagesPanel").Children, "补全不该顺手把消息发出去");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ArrowUp_RecallsPreviouslySentMessages_AndArrowDownRestoresDraft()
    {
        _session.Dispatch(async () =>
        {
            using var context = new TestPluginContext();
            var store = new ChatHistoryStore(context);
            await store.InitAsync();
            DateTimeOffset created = DateTimeOffset.UtcNow;
            string id = ChatHistoryStore.NewConversationId();
            await store.AppendAsync(id, created, 0, "user", "第一条");
            await store.AppendAsync(id, created, 1, "assistant", "回复");
            await store.AppendAsync(id, created, 2, "user", "第二条");

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextBox input = Find<TextBox>(panel, "InputBox");
                input.Focus();
                input.Text = "写到一半的草稿";
                input.CaretIndex = input.Text.Length;
                await PumpAsync(3);

                window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
                await PumpAsync(3);
                Assert.AreEqual("第二条", input.Text, "↑ 先给最近发过的一条");

                window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
                await PumpAsync(3);
                Assert.AreEqual("第一条", input.Text);

                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                await PumpAsync(3);
                Assert.AreEqual("写到一半的草稿", input.Text, "翻回头要还回草稿,别把用户没发的内容吃掉");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
