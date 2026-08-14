using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using LiveMarkdown.Avalonia;
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

    /// <summary>
    /// 面板的初始化是 fire-and-forget 的异步链,跑几拍调度器让它落定。
    /// 默认拍数要盖过 <c>@</c> 补全的防抖(180ms),否则弹层还没来得及开就断言了。
    /// </summary>
    private static async Task PumpAsync(int rounds = 60)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Find<T>(ChatPanelView panel, string name) where T : Control
        => panel.GetControl<T>(name);

    /// <summary>
    /// 在 headless UI 线程上跑一段<b>异步</b>测试体,并把里面的异常/断言失败带回本线程。
    /// </summary>
    /// <remarks>
    /// 千万别直接写 <c>_session.Dispatch(async () =&gt; { … }, ct).GetAwaiter().GetResult()</c>:
    /// <see cref="HeadlessUnitTestSession" /> <b>没有 <c>Func&lt;Task&gt;</c> 重载</b>,那样会命中
    /// <c>Dispatch&lt;TResult&gt;(Func&lt;TResult&gt;)</c>,TResult 被推成 <c>Task</c> ——
    /// 拿回来的是一个<b>从没被等待过的 <c>Task&lt;Task&gt;</c></b>:测试体只跑到第一个 await 就返回,
    /// 之后的断言全在后台默默地跑、失败也丢了,测试永远"通过"(本文件此前 6 个用例都是如此,
    /// 2ms 就"跑完"了)。这里让 lambda 带一个返回值,命中 <c>Func&lt;Task&lt;TResult&gt;&gt;</c> 重载,
    /// 才是真的等到底。
    /// </remarks>
    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestMethod]
    public void Panel_Loads_WithoutHostThemeTokens()
    {
        OnUi(async () =>
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
                Assert.IsFalse(Find<DockPanel>(panel, "HistoryHost").IsVisible);
                Assert.IsTrue(Find<ToggleButton>(panel, "HistoryToggle").IsEnabled,
                    "时序能力可用时历史按钮应可点");
                // 裸测试宿主里一个供应商都没配,初始化末尾会自己切到设置页(见 InitAsync 的
                // NoProvider 分支)—— 中间区是设置而不是聊天流,这不是异常,是既定引导路径。
                Assert.IsTrue(Find<ToggleButton>(panel, "SettingsToggle").IsChecked);
                Assert.IsFalse(Find<ScrollViewer>(panel, "ChatScroll").IsVisible,
                    "没有供应商时先请用户去设置页,聊天流让位");
                // 输入框是 AvaloniaEdit(要 Markdown 着色与 @ 芯片),不是 TextBox
                Assert.IsNotNull(Find<TextEditor>(panel, "InputBox"));
                Assert.IsNotNull(Find<TextEditor>(panel, "InputBox").SyntaxHighlighting,
                    "输入框应挂上 Markdown 着色");
                Assert.IsTrue(Find<TextBlock>(panel, "InputPlaceholder").IsVisible,
                    "空输入框要显示占位提示");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void HistoryToggle_SwitchesCentreView_AndListsSavedConversations()
    {
        OnUi(async () =>
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
                    [.. list.Children[0].GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")]);

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
        });
    }

    [TestMethod]
    public void AtSign_OpensRemoteFilePicker_AndEnterInsertsThePath()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            VelaShell.PluginSdk.Sessions.SessionInfo session = context.FakeSessions.AddConnected();
            context.FakeRemoteFs.AddFile(session.SessionId, "/etc/hosts", "127.0.0.1 localhost"u8.ToArray());
            context.FakeRemoteFs.AddFile(session.SessionId, "/etc/hostname", "web-01"u8.ToArray());
            context.FakeRemoteFs.AddDirectory(session.SessionId, "/etc/nginx");

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.TextArea.Focus();
                input.Text = "看看 @/etc/host";
                input.CaretOffset = input.Text.Length;
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
        });
    }

    [TestMethod]
    public void ArrowUp_RecallsPreviouslySentMessages_AndArrowDownRestoresDraft()
    {
        OnUi(async () =>
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
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.TextArea.Focus();
                input.Text = "写到一半的草稿";
                input.CaretOffset = input.Text.Length;
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
        });
    }

    /// <summary>
    /// 同一个目录里继续敲过滤词,不该再发列目录请求 —— 列目录在真机上是一次 SFTP 往返,
    /// "敲一个字符列一次、还要取消上一次"正是输入卡顿与调试输出刷屏的来源。
    /// </summary>
    [TestMethod]
    public void FilePicker_ListsEachDirectoryOnce_WhileTheFilterKeepsChanging()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            VelaShell.PluginSdk.Sessions.SessionInfo session = context.FakeSessions.AddConnected();
            context.FakeRemoteFs.AddFile(session.SessionId, "/etc/hosts", "127.0.0.1"u8.ToArray());
            context.FakeRemoteFs.AddFile(session.SessionId, "/etc/hostname", "web-01"u8.ToArray());
            context.FakeRemoteFs.AddFile(session.SessionId, "/etc/passwd", "root:x:0:0"u8.ToArray());

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.TextArea.Focus();
                Type(input, "看看 @/etc/");
                await PumpAsync();

                Assert.IsTrue(Find<Popup>(panel, "FilePopup").IsOpen);
                int afterFirstListing = context.FakeRemoteFs.ListDirectoryCalls;
                Assert.AreEqual(1, afterFirstListing, "进到一个目录只列一次");

                // 逐字符敲过滤词:目录没变,应当纯本地筛,不再碰网络
                foreach (char c in "hostn")
                {
                    Type(input, input.Text + c);
                    await PumpAsync(3);
                }
                await PumpAsync();

                Assert.AreEqual(afterFirstListing, context.FakeRemoteFs.ListDirectoryCalls,
                    "过滤词变化不该触发新的列目录(同目录结果有缓存)");
                Assert.HasCount(1, Find<StackPanel>(panel, "FileList").Children,
                    "过滤仍然生效:hostn 只剩 hostname");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 补全落定的文件引用是一整块(Claude Code / OpenCode 里那枚芯片):
    /// 一次退格整块删掉,而不是一个字符一个字符地啃回去(啃一次就列一次目录)。
    /// </summary>
    [TestMethod]
    public void Backspace_RemovesTheWholeAcceptedReference_WithoutAnotherListing()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            VelaShell.PluginSdk.Sessions.SessionInfo session = context.FakeSessions.AddConnected();
            context.FakeRemoteFs.AddFile(session.SessionId, "/etc/hostname", "web-01"u8.ToArray());

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.TextArea.Focus();
                Type(input, "看看 @/etc/hostname ");
                await PumpAsync();
                int listings = context.FakeRemoteFs.ListDirectoryCalls;

                window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
                await PumpAsync();

                Assert.AreEqual("看看 ", input.Text, "一次退格删掉整条引用(含补全时补上的尾空格)");
                Assert.AreEqual("看看 ".Length, input.CaretOffset, "光标落在被删块的起点");
                Assert.IsFalse(Find<Popup>(panel, "FilePopup").IsOpen);
                Assert.AreEqual(listings, context.FakeRemoteFs.ListDirectoryCalls,
                    "退格不该再引发列目录:删完已经不在引用里了");

                // 还在敲、没落定的 token 仍按字符退格(用户在改自己刚打错的那几位)
                Type(input, "看看 @/etc/hostn");
                await PumpAsync();
                window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
                await PumpAsync();
                Assert.AreEqual("看看 @/etc/host", input.Text, "未落定的引用照常一个字符一个字符退");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 落定的 <c>@</c> 引用在输入框里显示成一枚只写文件名的芯片(OpenCode 那种观感),
    /// 但文档里存的仍是全路径 —— 发送、附件展开都读文档,不受渲染影响。
    /// </summary>
    [TestMethod]
    public void CompletedReference_RendersAsChipShowingOnlyTheFileName()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            VelaShell.PluginSdk.Sessions.SessionInfo session = context.FakeSessions.AddConnected();
            context.FakeRemoteFs.AddFile(session.SessionId, "/root/abc.txt", "x"u8.ToArray());

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.TextArea.Focus();
                Type(input, "看看 @/root/abc.txt 这个");
                await PumpAsync();
                input.TextArea.TextView.EnsureVisualLines();

                Assert.AreEqual("看看 @/root/abc.txt 这个", input.Text, "文档里仍是全路径");
                List<FormattedTextElement> chips = ChipsOf(input);
                Assert.HasCount(1, chips, "落定的引用应被替换成一枚芯片元素");
                Assert.AreEqual("@/root/abc.txt".Length, chips[0].DocumentLength, "芯片正好覆盖整条引用");
                Assert.AreEqual(1, chips[0].VisualLength,
                    "芯片只占一个视觉列:光标整枚跨过,与整块退格的语义一致");

                // 还在敲、没落定的那条不做芯片:那时用户要看清自己敲的每个字符
                Type(input, "看看 @/root/ab");
                await PumpAsync(5);
                input.TextArea.TextView.EnsureVisualLines();
                Assert.IsEmpty(ChipsOf(input));
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 芯片不能把行撑高:AvaloniaEdit 的光标按所在行的行高画,行一高,光标就变成一根
    /// 突兀的长条(这正是把芯片从"内联控件"改成"替换文本"的原因)。
    /// </summary>
    [TestMethod]
    public void Chip_DoesNotInflateLineHeight_SoTheCaretStaysNormal()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            VelaShell.PluginSdk.Sessions.SessionInfo session = context.FakeSessions.AddConnected();
            context.FakeRemoteFs.AddFile(session.SessionId, "/root/.bashrc", "x"u8.ToArray());

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextEditor input = Find<TextEditor>(panel, "InputBox");
                input.TextArea.Focus();

                Type(input, "为我简单解释下这个文件的内容");
                await PumpAsync(5);
                input.TextArea.TextView.EnsureVisualLines();
                double plain = input.TextArea.TextView.VisualLines[0].Height;

                Type(input, "@/root/.bashrc 为我简单解释下这个文件的内容");
                await PumpAsync(5);
                input.TextArea.TextView.EnsureVisualLines();
                double withChip = input.TextArea.TextView.VisualLines[0].Height;

                Assert.HasCount(1, ChipsOf(input), "前提:这一行确实带一枚芯片");
                Assert.AreEqual(plain, withChip, 0.01, "带芯片的行与纯文字行必须等高");
                Assert.AreEqual(plain, input.TextArea.Caret.CalculateCaretRectangle().Height, 0.01,
                    "光标高度跟着行高走,行不变高光标才正常");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 用户气泡与 VSCode 的 Copilot 对齐:引用的文件以芯片列出(短名 + 全路径提示),
    /// 正文按 Markdown 渲染,且正文里的引用同样收成短名 —— 与输入框所见一致。
    /// </summary>
    [TestMethod]
    public void UserBubble_ShowsFileChips_AndRendersMarkdown()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            VelaShell.PluginSdk.Sessions.SessionInfo session = context.FakeSessions.AddConnected();
            context.FakeRemoteFs.AddFile(session.SessionId, "/root/.bashrc", "export PATH=/usr/bin"u8.ToArray());
            // 没有供应商时 SendAsync 会直接引导到设置页、不发消息,所以先配一个(不会真去连它:
            // 本用例只看用户气泡长什么样,后面那半轮请求失败与否都不影响断言)。
            var provider = new AiProviderConfig { Name = "test", BaseUrl = "http://127.0.0.1:1/v1", Model = "m" };
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveProviderId = provider.Id
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("@/root/.bashrc 为我**简单**解释下这个文件");
                await PumpAsync();

                Border bubble = Find<StackPanel>(panel, "MessagesPanel").Children
                    .OfType<Border>().First(b => b.Classes.Contains("userMsg"));

                List<string> chips = bubble.GetVisualDescendants()
                    .OfType<Border>()
                    .Where(b => b.Classes.Contains("refChip"))
                    .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>())
                    .Select(t => t.Text ?? "")
                    .ToList();
                Assert.AreSequenceEqual([".bashrc"], chips, "引用的文件以短名芯片列出");

                // 正文走 Markdown 渲染器(而不是一块纯文本),且不再出现长路径
                Assert.IsNotEmpty(bubble.GetVisualDescendants().OfType<MarkdownRenderer>().ToList(),
                    "用户正文也该按 Markdown 渲染");
                List<string> texts = [.. bubble.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
                Assert.IsFalse(texts.Any(t => t.Contains("/root/.bashrc", StringComparison.Ordinal)),
                    "气泡里不该再出现长路径(输入框里看到的是短名,发出去也该是短名)");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>当前可视行里的引用芯片元素。</summary>
    private static List<FormattedTextElement> ChipsOf(TextEditor input)
        => [.. input.TextArea.TextView.VisualLines.SelectMany(l => l.Elements).OfType<FormattedTextElement>()];

    /// <summary>照用户打字那样改写输入框:文本 + 光标落到末尾。</summary>
    private static void Type(TextEditor input, string text)
    {
        input.Text = text;
        input.CaretOffset = text.Length;
    }
}
