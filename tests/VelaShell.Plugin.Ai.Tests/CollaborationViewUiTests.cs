using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using QRCoder;
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

                Assert.IsTrue(buttons.Length >= 8, $"expected the whole page, only found {buttons.Length} buttons");
                string[] offenders =
                [
                    .. buttons.Where(b => b.HorizontalContentAlignment != HorizontalAlignment.Center)
                              .Select(b => $"{b.Name ?? "(code-built)"}:{b.Content}")
                ];
                Assert.AreEqual(0, offenders.Length,
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

                StringAssert.Contains(sample, "http://127.0.0.1:9123/mcp");
                StringAssert.Contains(sample, token);
                StringAssert.Contains(sample, "claude mcp add --transport http");
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
                var panel = view.FindControl<StackPanel>("ChannelsPanel")!;
                Assert.AreEqual(1, panel.Children.Count);
                TextBox[] boxes = [.. panel.GetLogicalDescendants().OfType<TextBox>()];
                Assert.IsTrue(boxes.Any(b => b.Text == "cli_abc"), "the App ID should be shown");
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
                CollectionAssert.Contains(labels, "Bot Token");
                CollectionAssert.DoesNotContain(labels, "App ID");
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
                view.FindControl<Button>("IssuePairCodeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();

                string shown = view.FindControl<TextBlock>("PairCodeText")!.Text ?? "";
                Assert.IsNotNull(bridge.Pairing.Code);
                StringAssert.Contains(shown, bridge.Pairing.Code!);
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
                view.FindControl<Button>("IssuePairCodeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await PumpAsync();

                StringAssert.Contains(view.FindControl<TextBlock>("StatusText")!.Text ?? "", "bridge on");
            });
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

                var panel = view.FindControl<StackPanel>("PendingPanel")!;
                Assert.AreEqual(1, panel.Children.Count);
                string[] text = [.. panel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];
                CollectionAssert.Contains(text, "oc_abc");
                Assert.IsTrue(text.Any(t => t.Contains("Ann")), "the row should say who knocked");
                Assert.IsTrue(panel.GetLogicalDescendants().OfType<Button>().Any(b => (string?)b.Content == "Allow"));
            }, bridge);
        });
    }

    /// <summary>
    /// 二维码真的画得出来。
    /// </summary>
    /// <remarks>
    /// QRCoder 是这次新引的依赖,而且要经 PNG 字节转成 Avalonia 的 <see cref="Bitmap" /> ——
    /// 这条路"编译得过、运行才炸"的可能性最高(比如挑了个要 System.Drawing 的重载),
    /// 所以单独钉一条。
    /// </remarks>
    [TestMethod]
    public void QrCode_RendersToABitmap()
    {
        OnUi(() =>
        {
            using var generator = new QRCodeGenerator();
            using QRCodeData data = generator.CreateQrCode("https://t.me/example_bot?startgroup=true",
                QRCodeGenerator.ECCLevel.M);
            byte[] png = new PngByteQRCode(data).GetGraphic(6);

            using var bitmap = new Bitmap(new MemoryStream(png));

            Assert.IsTrue(bitmap.PixelSize.Width > 0 && bitmap.PixelSize.Height > 0);
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
                var channels = view.FindControl<StackPanel>("ChannelsPanel")!;
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
