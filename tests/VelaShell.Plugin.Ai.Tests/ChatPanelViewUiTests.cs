using System.Text.Json;
using Avalonia;
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
public sealed partial class ChatPanelViewUiTests
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
                // 设置与"配置工具"都是独立窗口了,顶栏上是两枚按钮而不是视图开关
                Assert.IsNotNull(Find<Button>(panel, "SettingsButton"));
                Assert.IsNotNull(Find<Button>(panel, "ToolsButton"));
                Assert.IsNotNull(Find<DockPanel>(panel, "HistoryHost"));
                Assert.IsNotNull(Find<Popup>(panel, "FilePopup"));
                Assert.IsFalse(Find<Popup>(panel, "FilePopup").IsOpen, "没输入 @ 时文件选择器不该弹出");
                Assert.IsFalse(Find<DockPanel>(panel, "HistoryHost").IsVisible);
                Assert.IsTrue(Find<ToggleButton>(panel, "HistoryToggle").IsEnabled,
                    "时序能力可用时历史按钮应可点");
                // 裸测试宿主里一个供应商都没配。设置改成独立窗口之后就不再抢版面了:
                // 聊天流照常在,只在状态行留一句"去 ⚙ 配一个"(见 InitAsync 的 NoProvider 分支)。
                // 面板可能是随宿主启动一起开的,冷不丁弹一个窗口在用户脸上不合适。
                Assert.IsTrue(Find<ScrollViewer>(panel, "ChatScroll").IsVisible);
                Assert.Contains("⚙", Find<TextBlock>(panel, "StatusText").Text ?? "",
                    "没配接入时状态行要指路(五种语言的这句文案里都有 ⚙)");
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

                List<string> chips = [.. bubble.GetVisualDescendants()
                    .OfType<Border>()
                    .Where(b => b.Classes.Contains("refChip"))
                    .SelectMany(b => b.GetVisualDescendants().OfType<TextBlock>())
                    .Select(t => t.Text ?? "")];
                Assert.AreSequenceEqual([".bashrc"], chips, "引用的文件以短名芯片列出");

                // 正文走 Markdown 渲染器(而不是一块纯文本),且不再出现长路径
                Assert.IsNotEmpty(bubble.GetVisualDescendants().OfType<MarkdownRenderer>().ToList(),
                    "用户正文也该按 Markdown 渲染");
                List<string> texts = [.. bubble.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
                Assert.DoesNotContain(t => t.Contains("/root/.bashrc", StringComparison.Ordinal), texts,
                    "气泡里不该再出现长路径(输入框里看到的是短名,发出去也该是短名)");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 输入区的版式对齐 GitHub Copilot:模型选择与对话模式跟输入框同处一个描边容器
    /// (而不是散在顶栏),用量与审批模式落在容器正下方那条细行上,编辑区本身默认就有几行高。
    /// </summary>
    [TestMethod]
    public void InputArea_KeepsModelPickerAndUsage_AroundTheEditor()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                Border wrap = Find<Border>(panel, "InputWrap");
                foreach (string name in (string[])["ProviderCombo", "ModeCombo", "SendButton", "InputBox"])
                {
                    Assert.Contains(c => c.Name == name, wrap.GetVisualDescendants().OfType<Control>(),
                        $"{name} 应当在输入容器里(决定这条消息怎么发的东西要挨着输入框)");
                }

                // 用量与审批模式在容器之外的细行上,免得跟模型下拉抢宽度
                Grid statusBar = Find<Grid>(panel, "InputStatusBar");
                Assert.DoesNotContain(c => c.Name == "ProviderCombo", statusBar.GetVisualDescendants().OfType<Control>());
                Assert.IsNotNull(Find<TextBlock>(panel, "UsageText"));
                Assert.IsNotNull(Find<ComboBox>(panel, "ApprovalCombo"));

                TextEditor input = Find<TextEditor>(panel, "InputBox");
                Assert.IsGreaterThanOrEqualTo(66, input.Bounds.Height,
                    "输入框默认要有多行的余量,写长提示词不必先撑开");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>一轮结束后回复气泡底部要挂出"复制整段"与"时间 · 模型"(不显示积分与点赞/差评)。</summary>
    [TestMethod]
    public void AssistantReply_EndsWithCopyButton_AndModelStamp()
    {
        OnUi(async () =>
        {
            using var stub = new SseStub("""
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"好的。"},"finish_reason":"stop"}]}

            data: [DONE]


            """);

            using var context = new TestPluginContext();
            var provider = new AiProviderConfig
            {
                Name = "test",
                BaseUrl = stub.BaseUrl,
                Model = "some-model-id"
            };
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveProviderId = provider.Id,
                SuggestFollowUps = false
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("随便说点什么");
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.IsTrue(await WaitForAsync(() => Footers(messages).Count > 0),
                    "一轮结束后回复气泡应当挂出底部元信息条");

                Border footer = Footers(messages)[^1];
                Assert.IsNotEmpty(footer.GetVisualDescendants().OfType<Button>().ToList(),
                    "底部要有一个复制整段回复的按钮");
                string meta = string.Join("|", footer.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));
                Assert.Contains("some-model-id", meta, "底部要写明这一段是哪个模型答的");
                Assert.MatchesRegex(TimeStamp(), meta, "底部要带上这条回复的时刻");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 处理中输入框边框走流光,答完立刻收回主题色 —— 有没有在跑,扫一眼边框就知道。
    /// </summary>
    [TestMethod]
    public void InputBorder_FlowsWhileBusy_AndReturnsToTheThemeColourWhenDone()
    {
        OnUi(async () =>
        {
            using var stub = new SseStub("""
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

            data: [DONE]


            """, delay: TimeSpan.FromMilliseconds(900));

            using var context = new TestPluginContext();
            var provider = new AiProviderConfig { Name = "stub", BaseUrl = stub.BaseUrl, Model = "m" };
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveProviderId = provider.Id,
                SuggestFollowUps = false // 这个用例只看边框,别再多发一次请求
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                Border wrap = Find<Border>(panel, "InputWrap");
                BorderGlowOverlay glow = panel.GetControl<BorderGlowOverlay>("InputGlow");
                Assert.IsFalse(glow.IsRunning, "空闲时不该有光在跑");
                object? idleBorder = wrap.BorderBrush;

                panel.SendExternal("hi");
                Assert.IsTrue(await WaitForAsync(() => glow.IsRunning, maxRounds: 40),
                    "请求在途时应点亮边框流光");

                // 相位在推进 = 真的在跑,而不是画了一枚不动的光斑
                double first = glow.Phase;
                Assert.IsTrue(await WaitForAsync(() => Math.Abs(glow.Phase - first) > 1e-6, maxRounds: 40),
                    "彗尾要沿着边框跑起来");

                // 光是盖在边框上画的(自带暗色轨道),但不改边框自己的画刷 ——
                // 于是焦点态/悬停态那几条选择器照常生效,熄灭时也不用恢复什么
                Assert.AreSame(idleBorder, wrap.BorderBrush, "流光不该改动边框自身的画刷");

                Assert.IsTrue(await WaitForAsync(() => !glow.IsRunning), "一轮结束后熄灭");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 思考过程要<b>边到边显示</b>,而不是攒到正文一起冒出来 —— 这是各家 AI 工具的通行做法,
    /// 也是"模型正在想什么"这件事唯一有用的呈现方式。
    /// 这里让假端点按真实节奏逐事件下发,断言正文还没来之前思考区就已经在长。
    /// </summary>
    [TestMethod]
    public void Thinking_StreamsWhileItArrives_NotAfterTheAnswer()
    {
        OnUi(async () =>
        {
            using var stub = new SseStub("""
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"先看看磁盘"}}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"reasoning_content":",再看看服务"}}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"结论是没问题。"},"finish_reason":"stop"}]}

            data: [DONE]


            """, chunkDelay: TimeSpan.FromMilliseconds(400));

            using var context = new TestPluginContext();
            var provider = new AiProviderConfig { Name = "stub", BaseUrl = stub.BaseUrl, Model = "m" };
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveProviderId = provider.Id,
                SuggestFollowUps = false,
                AgentMode = true
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                panel.SendExternal("这台机器还好吗");

                // 思考一到就出卡片,但默认是收起的(用户决策)
                Assert.IsTrue(await WaitForAsync(() => ThinkingHeader(messages) is not null),
                    "第一段思考到了就该出思考卡片");
                Assert.IsFalse(ThinkingBody(messages)?.IsVisible ?? false, "默认收起");
                Assert.IsEmpty(AnswerRenderers(messages), "此时正文一个字都还没来");

                // 展开之后就该看到边到边长出来的内容
                ClickHeader(messages);
                Assert.IsTrue(await WaitForAsync(() => ThinkingText(messages).Contains("先看看磁盘")),
                    $"展开后应看到已到达的思考。当前思考区:「{ThinkingText(messages)}」");
                Assert.IsEmpty(AnswerRenderers(messages),
                    "正文还没开始,思考却已经在长 —— 这才叫流式");

                // 第二段追加上去(仍在正文之前)
                Assert.IsTrue(await WaitForAsync(() => ThinkingText(messages).Contains("再看看服务")),
                    "后续思考要继续往上追加");

                Assert.IsTrue(await WaitForAsync(() => AnswerRenderers(messages).Count > 0),
                    "正文随后照常渲染");

                // 用户已经手动点开了,收尾时就不能再替他折起来 —— 别在人正读的时候把内容抽走
                Assert.IsTrue(ThinkingBody(messages)!.IsVisible, "用户展开过就一直开着");
                Assert.Contains("先看看磁盘,再看看服务", ThinkingText(messages), "收尾时思考补齐完整");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>点一下思考折叠区的头部(开/合)。</summary>
    private static void ClickHeader(StackPanel messages)
    {
        Grid header = ThinkingHeader(messages)!;
        header.RaiseEvent(new PointerPressedEventArgs(header, new Pointer(0, PointerType.Mouse, true),
            header, default, 0, new PointerPointProperties(), KeyModifiers.None));
    }

    /// <summary>思考折叠区的可点头部(点它开合)。</summary>
    private static Grid? ThinkingHeader(StackPanel messages)
        => messages.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("toolCard"))
            .SelectMany(b => b.GetVisualDescendants().OfType<Grid>())
            .FirstOrDefault();

    /// <summary>思考折叠区的正文容器(展开与否看它的 IsVisible)。</summary>
    private static ScrollViewer? ThinkingBody(StackPanel messages)
        => messages.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("toolCard"))
            .SelectMany(b => b.GetVisualDescendants().OfType<ScrollViewer>())
            .FirstOrDefault();

    /// <summary>思考折叠区里的正文(没有工具卡时,消息流里唯一的 toolCard 就是它)。</summary>
    private static string ThinkingText(StackPanel messages)
        => string.Concat(messages.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("toolCard"))
            .SelectMany(b => b.GetVisualDescendants().OfType<SelectableTextBlock>())
            .Select(t => t.Text ?? ""));

    /// <summary>助手正文的 Markdown 渲染器(有它就说明正文已经开始渲染)。</summary>
    private static List<MarkdownRenderer> AnswerRenderers(StackPanel messages)
        => [.. messages.Children.OfType<Border>()
            .Where(b => b.Classes.Contains("msg") && !b.Classes.Contains("userMsg"))
            .SelectMany(b => b.GetVisualDescendants().OfType<MarkdownRenderer>())];

    /// <summary>
    /// 空会话在输入框上方给几条起手提示(本地文案,不请求模型),点一下就发出去。
    /// </summary>
    [TestMethod]
    public void Suggestions_OfferStarterPrompts_AndClickingOneSendsIt()
    {
        OnUi(async () =>
        {
            using var stub = new SseStub("""
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

            data: [DONE]


            """);

            using var context = new TestPluginContext();
            var provider = new AiProviderConfig { Name = "stub", BaseUrl = stub.BaseUrl, Model = "m" };
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveProviderId = provider.Id,
                SuggestFollowUps = false
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                WrapPanel bar = Find<WrapPanel>(panel, "SuggestionBar");
                Assert.IsTrue(bar.IsVisible, "空会话该给点起手提示");
                Assert.HasCount(3, bar.Children);

                var chip = (Border)bar.Children[0];
                string prompt = chip.GetVisualDescendants().OfType<TextBlock>().First().Text!;
                chip.RaiseEvent(new PointerPressedEventArgs(chip, new Pointer(0, PointerType.Mouse, true),
                    chip, default, 0, new PointerPointProperties(), KeyModifiers.None));
                await PumpAsync(10);

                Assert.IsFalse(bar.IsVisible, "发出去之后建议就该收起");
                // 气泡正文是 Markdown 渲染的,拿不到平整的 TextBlock.Text —— 直接看发给模型的是什么
                string body = await stub.RequestBodyAsync.WaitAsync(TimeSpan.FromSeconds(10));
                using var request = JsonDocument.Parse(body);
                string sent = request.RootElement.GetProperty("messages")
                    .EnumerateArray().Last().GetProperty("content").GetString() ?? "";
                Assert.AreEqual(prompt, sent, "点中的那条应当原样作为用户消息发出去");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>一轮答完后,模型给的后续提问显示成药丸(开关打开时)。</summary>
    [TestMethod]
    public void Suggestions_ShowFollowUps_AfterAReply()
    {
        OnUi(async () =>
        {
            // 聊天走流式;"给几条后续提问"那一问是非流式的,由 jsonContent 回它
            using var stub = new SseStub("""
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"磁盘看着还行。"},"finish_reason":"stop"}]}

            data: [DONE]


            """, jsonContent: "1. 磁盘还够吗\n- 有哪些服务在跑\n\"要不要看日志\"");

            using var context = new TestPluginContext();
            var provider = new AiProviderConfig { Name = "stub", BaseUrl = stub.BaseUrl, Model = "m" };
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveProviderId = provider.Id,
                SuggestFollowUps = true
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                WrapPanel bar = Find<WrapPanel>(panel, "SuggestionBar");
                panel.SendExternal("看看这台机器");
                Assert.IsTrue(await WaitForAsync(() => bar.IsVisible && bar.Children.Count == 3),
                    "答完之后应当给出三条后续提问");

                List<string> chips = [.. bar.Children.OfType<Border>()
                    .SelectMany(c => c.GetVisualDescendants().OfType<TextBlock>())
                    .Select(t => t.Text ?? "")];
                // 序号、项目符号、引号都该被洗掉 —— 模型很少按格式要求老实输出
                Assert.AreSequenceEqual(["磁盘还够吗", "有哪些服务在跑", "要不要看日志"], chips);
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 一轮真实回复之后,输入框下方那块用量要给出:上下文占比、缓存命中率,
    /// 以及悬停里的完整明细。走的是真 SDK + 真适配器 —— 用量字段的口径差异只有这样才测得出。
    /// </summary>
    [TestMethod]
    public void Usage_ShowsContextRatioAndCacheHitRate_AfterARealResponse()
    {
        OnUi(async () =>
        {
            // prompt_tokens 含 cached_tokens(OpenAI 口径):1000 里命中 800
            using var stub = new SseStub("""
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{"prompt_tokens":1000,"completion_tokens":50,"total_tokens":1050,"prompt_tokens_details":{"cached_tokens":800}}}

            data: [DONE]


            """);

            using var context = new TestPluginContext();
            var provider = new AiProviderConfig
            {
                Name = "stub",
                Protocol = ChatProtocol.OpenAiChatCompletions,
                BaseUrl = stub.BaseUrl,
                Model = "m",
                MaxInputTokens = 10000
            };
            await new AiSettingsStore(context).SaveAsync(new AiSettings
            {
                Providers = [provider],
                ActiveProviderId = provider.Id
            });

            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                TextBlock usage = Find<TextBlock>(panel, "UsageText");
                Assert.IsEmpty(usage.Text ?? "", "还没发过消息时不显示用量");

                panel.SendExternal("hi");
                Assert.IsTrue(await WaitForAsync(() => !string.IsNullOrEmpty(usage.Text)),
                    "一轮结束后应当显示用量");

                // 上下文 1000/10000 = 10%,缓存命中 800/1000 = 80%
                Assert.Contains("10%", usage.Text!, "上下文占比");
                Assert.Contains("80%", usage.Text!, "缓存命中率就显示在占比旁边");

                string tip = ToolTip.GetTip(usage) as string ?? "";
                Assert.Contains("800", tip, "悬停给出命中的具体 token 数");
                Assert.Contains("1,000", tip, "以及这一轮的输入总量");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 输入框下那条细行:高度与内容无关(审批选择器只在带工具的模式下出现,
    /// 行一变高整个输入区就会跳),且它与右侧的用量文字共用同一条中心线。
    /// </summary>
    [TestMethod]
    public void InputStatusBar_KeepsItsHeight_WhenTheApprovalPickerAppears()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                Grid bar = Find<Grid>(panel, "InputStatusBar");
                ComboBox approval = Find<ComboBox>(panel, "ApprovalCombo");
                TextBlock usage = Find<TextBlock>(panel, "UsageText");

                double withoutTools = bar.Bounds.Height;
                approval.IsVisible = true;
                window.Measure(window.ClientSize);
                window.Arrange(new Rect(window.ClientSize));
                await PumpAsync(5);

                Assert.AreEqual(withoutTools, bar.Bounds.Height, 0.01,
                    "审批选择器显隐不该改变这一行的高度,否则输入区会跳一下");
                Assert.AreEqual(CentreY(approval, bar), CentreY(usage, bar), 0.75,
                    "审批选择器与用量文字要在同一条中心线上");
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 对话模式三选一(对齐 Copilot),选中项写回设置;审批方式只在<b>有工具</b>的模式下露出 ——
    /// 纯对话里摆着它会让人以为还有什么能被自动执行。
    /// </summary>
    [TestMethod]
    public void ModeCombo_DrivesTheApprovalPickerVisibility()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                ComboBox mode = Find<ComboBox>(panel, "ModeCombo");
                ComboBox approval = Find<ComboBox>(panel, "ApprovalCombo");

                Assert.HasCount(3, mode.ItemsSource!.Cast<object>().ToList(), "对话 / 计划 / Agent");

                mode.SelectedIndex = (int)ChatMode.Chat;
                await PumpAsync(3);
                Assert.IsFalse(approval.IsVisible, "纯对话模式下没有工具,审批方式无意义");

                mode.SelectedIndex = (int)ChatMode.Plan;
                await PumpAsync(3);
                Assert.IsTrue(approval.IsVisible, "计划模式仍会调只读工具");

                mode.SelectedIndex = (int)ChatMode.Agent;
                await PumpAsync(3);
                Assert.IsTrue(approval.IsVisible);
            }
            finally
            {
                panel.Detach();
                window.Close();
            }
        });
    }

    /// <summary>
    /// 设置与"配置工具"都开成独立窗口(而不是占掉面板中间那块):侧栏往往只有三成宽,
    /// 那些两列三列的表单在里头铺不开,改设置时也不该看不见对话。
    /// 面板关闭时两个窗口要跟着走,别孤零零留在屏幕上。
    /// </summary>
    [TestMethod]
    public void SettingsAndTools_OpenAsSeparateWindows_AndCloseWithThePanel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                Click(panel, "SettingsButton");
                Click(panel, "ToolsButton");
                await PumpAsync(5);

                List<PluginDialog> dialogs = [.. Dialogs(window)];
                Assert.HasCount(2, dialogs, "设置与配置工具各开一个窗口");
                Assert.Contains(d => d.GetVisualDescendants().OfType<SettingsView>().Any(), dialogs,
                    "其中一个装着设置表单");
                Assert.Contains(d => d.GetVisualDescendants().OfType<ToolPickerView>().Any(), dialogs,
                    "另一个装着工具勾选列表");

                // 再点一次不该开出第二个
                Click(panel, "SettingsButton");
                await PumpAsync(3);
                Assert.HasCount(2, Dialogs(window).ToList(), "已经开着的窗口只带到前面,不重复开");

                panel.Detach();
                await PumpAsync(3);
                Assert.IsEmpty(Dialogs(window).ToList(), "面板关了,两个窗口要跟着关");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>挂在宿主窗口下、当前开着的插件对话框。</summary>
    private static IEnumerable<PluginDialog> Dialogs(Window owner) => owner.OwnedWindows.OfType<PluginDialog>();

    private static void Click(ChatPanelView panel, string name)
        => Find<Button>(panel, name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    /// <summary>控件在某个祖先坐标系里的垂直中心。</summary>
    private static double CentreY(Visual visual, Visual relativeTo)
    {
        Matrix transform = visual.TransformToVisual(relativeTo) ?? Matrix.Identity;
        return new Point(0, visual.Bounds.Height / 2).Transform(transform).Y;
    }

    /// <summary><c>HH:mm</c> 形式的时刻。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\d{1,2}:\d{2}")]
    private static partial System.Text.RegularExpressions.Regex TimeStamp();

    /// <summary>消息流里所有已收尾的回复底部条(时间 · 模型那一行)。</summary>
    private static List<Border> Footers(StackPanel messages)
        => [.. messages.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("replyFooter"))];

    /// <summary>边跑调度器边等某个条件成立;超时返回 false(上限约 15 秒)。</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int maxRounds = 600)
    {
        for (int i = 0; i < maxRounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return true;
            }
            await Task.Delay(25);
        }
        return false;
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
