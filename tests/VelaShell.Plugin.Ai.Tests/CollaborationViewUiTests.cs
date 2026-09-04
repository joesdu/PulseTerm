using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Bridge;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Interop;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// 「协作接入」设置页的 headless 装载:XAML 真的装得起来,渠道卡片按平台长对字段,
/// 保存后配置与机密都落到该去的地方。
/// </summary>
/// <remarks>
/// 这一页有相当一部分控件是<b>代码建的</b>(渠道卡片),而 <c>FindResource</c>、
/// 样式类名这类东西"编译得过、运行才炸" —— 只有真装载一次才拦得住。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class CollaborationViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CollaborationViewUiTests).Assembly);

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task PumpAsync(int rounds = 25)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// 开窗、跑用例、<b>无论断言过不过都把窗关掉</b>。
    /// </summary>
    /// <remarks>
    /// 漏关的话 headless 会话会一直等那个窗,一次断言失败就被报成一分钟的超时 ——
    /// 而"超时"这三个字什么线索都不给。
    /// </remarks>
    private static async Task WithViewAsync(TestPluginContext context, Func<CollaborationView, Task> body,
        BridgeService? bridge = null)
    {
        var view = new CollaborationView(context, new Loc("en"), () => Task.CompletedTask, bridge);
        var window = new Window { Width = 860, Height = 780, Content = view };
        window.Show();
        await PumpAsync();
        try
        {
            await body(view);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// 两条服务的<b>开箱默认值</b>:IM 桥接默认关,对外 MCP 服务端默认开。
    /// </summary>
    /// <remarks>
    /// 不对称是有意的,不是漏改:
    /// <list type="bullet">
    /// <item>桥接要用户去开发者后台拿凭证、还要把机器人拉进群,没配之前开着也没有意义;</item>
    /// <item>MCP 服务端只绑 127.0.0.1、强制带令牌、默认只读挡位,开箱可用的收益明显大于风险 ——
    /// 让人为了给 Claude Code 接一下先去翻一页设置,是没必要的门槛。</item>
    /// </list>
    /// 这条用例把这个决定钉住:哪天有人顺手把默认值改了,得先来改这段注释。
    /// </remarks>
    [TestMethod]
    public void View_DefaultsBridgeOffAndMcpServerOn()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await WithViewAsync(context, view =>
            {
                Assert.IsFalse(view.FindControl<CheckBox>("BridgeEnabledCheck")!.IsChecked);
                Assert.IsFalse(view.FindControl<StackPanel>("BridgeDetailPanel")!.IsVisible);

                Assert.IsTrue(view.FindControl<CheckBox>("McpEnabledCheck")!.IsChecked);
                Assert.IsTrue(view.FindControl<StackPanel>("McpDetailPanel")!.IsVisible);
                return Task.CompletedTask;
            });
        });
    }

    /// <summary>默认开着,但默认只给只读工具 —— "开箱可用"不等于"开箱可写"。</summary>
    [TestMethod]
    public void McpServer_DefaultsToTheReadOnlyMode()
        => Assert.AreEqual(ChatMode.Plan, new McpServerSettings().Mode);

    /// <summary>
    /// 页面上<b>每一个</b>按钮的文字都得是居中的 —— 含代码建的那些。
    /// </summary>
    /// <remarks>
    /// 这条是用户报了两次的那个 bug 的直接复现:第一次我只给 XAML 里的按钮挂了主题,
    /// 而 ① <c>VelaAccentPillButtonTheme</c> 根本没有 <c>HorizontalContentAlignment</c> 这个 setter,
    /// ② 代码建的按钮用 <c>TryFindResource</c> 取主题、在没进树时一律落空。两处都还是左对齐。
    /// <para>
    /// 这条用例在 headless 里<b>成立</b>,因为 <c>DialogStyles</c> 里那个 setter 的值是字面量
    /// <c>Center</c>,不依赖宿主的资源字典 —— 而只检查"XAML 里写没写 Theme="的文本级用例,
    /// 上面两种情况一个都抓不到。
    /// </para>
    /// </remarks>
    [TestMethod]
    public void EveryButtonCentresItsLabel()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var bridge = new BridgeService(context, new AiSettingsStore(context));
            // 三条建按钮的路都要覆盖到:XAML、渠道卡片、待放行卡片
            bridge.Pairing.Remember(new PendingChat("c1", "oc_abc", true, "Ann", DateTimeOffset.UtcNow));
            var store = new BridgeSettingsStore(context);
            await store.SaveAsync(new BridgeSettings
            {
                Enabled = true,
                Channels = [new ChannelConfig { Id = "c1", Kind = ChannelKind.Feishu }]
            });
            await WithViewAsync(context, async view =>
            {
                await PumpAsync();
                // CheckBox 继承自 ToggleButton : Button,但勾选框的文字本来就该左对齐 —— 排掉
                Button[] buttons =
                [
                    .. view.GetLogicalDescendants().OfType<Button>().Where(b => b is not ToggleButton)
                ];

                Assert.IsGreaterThanOrEqualTo(8, buttons.Length, $"expected the whole page, only found {buttons.Length} buttons");
                string[] offenders =
                [
                    .. buttons.Where(b => b.HorizontalContentAlignment != HorizontalAlignment.Center)
                              .Select(b => $"{b.Name ?? "(code-built)"}:{b.Content}")
                ];
                Assert.IsEmpty(offenders,
                    "these buttons render their label off-centre: " + string.Join(", ", offenders));
            }, bridge);
        });
    }

    /// <summary>接入方式那一栏是给人直接复制走的,端口改了它必须跟着变。</summary>
    [TestMethod]
    public void CommandSample_FollowsThePortAndCarriesTheToken()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new McpServerSettingsStore(context);
            await store.SaveAsync(new McpServerSettings { Enabled = true, Port = 9123 });
            string token = await store.TokenAsync();
            await WithViewAsync(context, view =>
            {
                string sample = view.FindControl<TextBox>("McpCommandBox")!.Text ?? "";

                Assert.Contains("http://127.0.0.1:9123/mcp", sample);
                Assert.Contains(token, sample);
                Assert.Contains("claude mcp add --transport http", sample);
                return Task.CompletedTask;
            });
        });
    }

    /// <summary>已配好的渠道要回显出来,而且密钥是遮起来的。</summary>
    [TestMethod]
    public void ExistingChannel_IsRenderedWithItsSecretMasked()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new BridgeSettingsStore(context);
            var channel = new ChannelConfig
            {
                Id = "c1",
                Kind = ChannelKind.Feishu,
                DisplayName = "Ops group",
                AppId = "cli_abc",
                AllowedChats = ["oc_1"]
            };
            await store.SaveAsync(new BridgeSettings { Enabled = true, Channels = [channel] });
            await store.SetSecretAsync("c1", "secret", "s3cret");
            await WithViewAsync(context, view =>
            {
                StackPanel panel = view.FindControl<StackPanel>("ChannelsPanel")!;
                Assert.AreEqual(1, panel.Children.Count);
                TextBox[] boxes = [.. panel.GetLogicalDescendants().OfType<TextBox>()];
                Assert.Contains(b => b.Text == "cli_abc", boxes, "the App ID should be shown");
                TextBox? secret = boxes.FirstOrDefault(b => b.Text == "s3cret");
                Assert.IsNotNull(secret, "the stored secret should be loaded back");
                Assert.AreEqual('●', secret.PasswordChar);
                return Task.CompletedTask;
            });
        });
    }

    /// <summary>Telegram 只有一个令牌,不该给它摆一个 App ID 的框。</summary>
    [TestMethod]
    public void TelegramChannel_HasNoAppIdField()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var store = new BridgeSettingsStore(context);
            await store.SaveAsync(new BridgeSettings
            {
                Enabled = true,
                Channels = [new ChannelConfig { Id = "c1", Kind = ChannelKind.Telegram }]
            });
            await WithViewAsync(context, view =>
            {
                string[] labels =
                [
                    .. view.FindControl<StackPanel>("ChannelsPanel")!
                           .GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")
                ];
                Assert.Contains("Bot Token", labels);
                Assert.DoesNotContain("App ID", labels);
                return Task.CompletedTask;
            });
        });
    }

    /// <summary>点一下生成,配对码要真的显示出来 —— 它是整条"授权一个群"路径的起点。</summary>
    [TestMethod]
    public void PairCode_IsShownAfterGenerating()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var bridge = new BridgeService(context, new AiSettingsStore(context));
            await WithViewAsync(context, async view =>
            {
                view.FindControl<Button>("IssuePairSelfButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();

                string shown = view.FindControl<TextBlock>("PairCodeText")!.Text ?? "";
                Assert.IsNotNull(bridge.Pairing.Code);
                Assert.Contains(bridge.Pairing.Code!, shown);
            }, bridge);
        });
    }

    /// <summary>没开桥接时点生成,要说清楚为什么没用,而不是默默什么都不发生。</summary>
    [TestMethod]
    public void PairCode_WithoutARunningBridge_ExplainsWhy()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await WithViewAsync(context, async view =>
            {
                view.FindControl<Button>("IssuePairSelfButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();

                Assert.Contains("bridge on", view.FindControl<TextBlock>("StatusText")!.Text ?? "");
            });
        });
    }

    /// <summary>
    /// 授权编辑器要能<b>存下来再读回去</b>:聊天 id、范围、挡位三样都不能在往返里丢。
    /// </summary>
    /// <remarks>
    /// 这一条守的是最难查的一类 bug:界面上勾好了、点了保存、看着也没报错,
    /// 而落库的那一份少了范围 —— 于是那个群悄悄拥有全部机器,没有任何提示。
    /// </remarks>
    [TestMethod]
    public void Grants_RoundTripThroughTheSettingsPage()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            context.FakeSessions.AddSaved(name: "prod-1", host: "10.0.0.1", group: "生产");
            var store = new BridgeSettingsStore(context);
            await store.SaveAsync(new BridgeSettings
            {
                Enabled = true,
                Channels = [new ChannelConfig { Id = "c1", Kind = ChannelKind.Feishu, AllowedChats = ["oc_1"] }]
            });
            await WithViewAsync(context, async view =>
            {
                StackPanel channels = view.FindControl<StackPanel>("ChannelsPanel")!;
                // 升级折算出来的那一行:不限范围
                ComboBox scope = channels.GetLogicalDescendants().OfType<ComboBox>()
                    .First(c => c.ItemsSource is string[] items && items.Length == 2);
                Assert.AreEqual(0, scope.SelectedIndex, "老配置折算出来的授权应当不限范围");

                scope.SelectedIndex = 1; // 收紧
                await PumpAsync();
                channels.GetLogicalDescendants().OfType<CheckBox>()
                    .Single(c => (string?)c.Content == "生产").IsChecked = true;
                // 挡位下拉:第 0 项是"跟随全局",选 Plan
                ComboBox mode = channels.GetLogicalDescendants().OfType<ComboBox>()
                    .First(c => c.ItemsSource is string[] items && items.Length == 4 && items[1] == "Chat");
                mode.SelectedIndex = 2;

                view.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync(40);
            });

            ChannelConfig saved = (await store.LoadAsync()).Channels.Single();
            ChatGrant grant = saved.GrantFor("oc_1")!;
            Assert.AreEqual(ScopeKind.Limited, grant.Scope.Kind);
            Assert.Contains("生产", grant.Scope.Groups);
            Assert.AreEqual(ChatMode.Plan, grant.Mode);
            // 派生镜像也要跟着对:降级回旧版本时白名单还得在
            Assert.Contains("oc_1", saved.AllowedChats);
        });
    }

    /// <summary>
    /// 给自己单聊的码<b>不带范围</b>,给群的码带。
    /// </summary>
    /// <remarks>
    /// 这是"不卡自己脖子"那条红线在界面上的落点:你不该为了让机器人认得自己,
    /// 先去填一遍分组选择器 —— 那正是把作者自己拦住的第一步。
    /// </remarks>
    [TestMethod]
    public void PairCode_ForSelfIsUnrestricted_ForAGroupCarriesTheScope()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            context.FakeSessions.AddSaved(name: "prod-1", host: "10.0.0.1", group: "生产");
            await using var bridge = new BridgeService(context, new AiSettingsStore(context));
            await WithViewAsync(context, async view =>
            {
                view.FindControl<Button>("IssuePairSelfButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();
                Assert.IsNull(bridge.Pairing.Template, "给自己的码不该带范围");

                // 勾上"生产"那个分组,再发一个给群的码
                CheckBox group = view.FindControl<StackPanel>("PairScopePanel")!
                    .GetLogicalDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "生产");
                group.IsChecked = true;
                view.FindControl<Button>("IssuePairGroupButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();

                ChatGrant? template = bridge.Pairing.Template;
                Assert.IsNotNull(template);
                Assert.AreEqual(ScopeKind.Limited, template.Scope.Kind);
                Assert.Contains("生产", template.Scope.Groups);
            }, bridge);
        });
    }

    /// <summary>敲过门的聊天要长出一行带「允许」的卡片 —— 这就是那个一键放行。</summary>
    [TestMethod]
    public void PendingChat_RendersARowWithAnAllowButton()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            await using var bridge = new BridgeService(context, new AiSettingsStore(context));
            bridge.Pairing.Remember(new PendingChat("ch1", "oc_abc", true, "Ann", DateTimeOffset.UtcNow));
            await WithViewAsync(context, async view =>
            {
                await PumpAsync(); // 页面加载完就会刷一次,不必等定时器

                StackPanel panel = view.FindControl<StackPanel>("PendingPanel")!;
                Assert.AreEqual(1, panel.Children.Count);
                string[] text = [.. panel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
                Assert.Contains("oc_abc", text);
                Assert.Contains(t => t.Contains("Ann"), text, "the row should say who knocked");
                Assert.Contains(b => (string?)b.Content == "Allow", panel.GetLogicalDescendants().OfType<Button>());
            }, bridge);
        });
    }

    /// <summary>
    /// 二维码真的画得出来,而且画的就是编码器给的那张矩阵。
    /// </summary>
    /// <remarks>
    /// 走的是生产路径本身(反射调 <c>RenderQr</c>),不是在测试里另拼一遍 ——
    /// 这一段是"编译得过、运行才炸"的高发地带:它自己建 <see cref="WriteableBitmap" />、
    /// 锁帧缓冲、按 <c>RowBytes</c> 逐行拷字节,任何一处算错都只有真跑一次才看得见。
    /// 矩阵本身的正确性由 <c>QrCodeTests</c> 把关,这里只管"矩阵 → 像素"这一跳。
    /// </remarks>
    [TestMethod]
    public void QrCode_RendersTheEncodedMatrixToPixels()
    {
        OnUi(() =>
        {
            const string url = "https://t.me/example_bot?startgroup=true";
            const int scale = 6;
            const int quiet = 4;
            var expected = QrCode.Encode(url, QrEcc.Medium);

            MethodInfo render = typeof(CollaborationView)
                                    .GetMethod("RenderQr", BindingFlags.NonPublic | BindingFlags.Static)
                                ?? throw new InvalidOperationException("CollaborationView.RenderQr is gone — update this test.");
            using var bitmap = (WriteableBitmap)render.Invoke(null, [url])!;

            int side = (expected.Size + (quiet * 2)) * scale;
            Assert.AreEqual(new PixelSize(side, side), bitmap.PixelSize);

            using ILockedFramebuffer frame = bitmap.Lock();
            byte[] pixels = new byte[frame.RowBytes * side];
            Marshal.Copy(frame.Address, pixels, 0, pixels.Length);
            bool IsDark(int x, int y) => pixels[(y * frame.RowBytes) + (x * 4)] == 0;

            // 静默区必须是白的,否则识读器框不出符号边界。
            Assert.IsFalse(IsDark(0, 0), "左上角静默区不该是深色");
            Assert.IsFalse(IsDark(side - 1, side - 1), "右下角静默区不该是深色");
            // 每个模块的中心像素都要与矩阵对上(顺带验证 quiet/scale 的偏移没算错)。
            for (int my = 0; my < expected.Size; my++)
            {
                for (int mx = 0; mx < expected.Size; mx++)
                {
                    int px = ((mx + quiet) * scale) + (scale / 2);
                    int py = ((my + quiet) * scale) + (scale / 2);
                    Assert.AreEqual(expected[mx, my], IsDark(px, py), $"模块 ({mx},{my}) 画错了");
                }
            }
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void Save_PersistsBothSettingsAndTheChannelSecret()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            var bridgeStore = new BridgeSettingsStore(context);
            var mcpStore = new McpServerSettingsStore(context);
            await bridgeStore.SaveAsync(new BridgeSettings
            {
                Channels = [new ChannelConfig { Id = "c1", Kind = ChannelKind.Feishu }]
            });
            await WithViewAsync(context, async view =>
            {
                view.FindControl<CheckBox>("BridgeEnabledCheck")!.IsChecked = true;
                view.FindControl<CheckBox>("McpEnabledCheck")!.IsChecked = true;
                view.FindControl<TextBox>("McpPortBox")!.Text = "9401";
                StackPanel channels = view.FindControl<StackPanel>("ChannelsPanel")!;
                TextBox[] boxes = [.. channels.GetLogicalDescendants().OfType<TextBox>()];
                boxes.First(b => b.PasswordChar == '●').Text = "written-secret";
                // 按钮的 Click 是直接挂的委托,所以走真的路由事件而不是反射调私有方法
                view.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync(40);
            });

            BridgeSettings bridge = await bridgeStore.LoadAsync();
            McpServerSettings mcp = await mcpStore.LoadAsync();
            Assert.IsTrue(bridge.Enabled);
            Assert.IsTrue(mcp.Enabled);
            Assert.AreEqual(9401, mcp.Port);
            Assert.AreEqual("written-secret", await bridgeStore.GetSecretAsync("c1", "secret"));
        });
    }
}
