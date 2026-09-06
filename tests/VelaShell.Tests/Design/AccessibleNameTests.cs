using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Behaviors;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Localization;
using VelaShell.Terminal.Rendering;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Design;

/// <summary>
/// 图标按钮的无障碍名字。
/// </summary>
/// <remarks>
/// 界面上大量按钮的内容只是一个图标(新建、关闭、刷新、折叠侧栏……),读屏器读到的只有
/// "按钮"二字 —— 对读屏器用户来说整个应用是一排无名按钮。但这些按钮几乎都写过
/// <c>ToolTip.Tip</c>(全仓 116 处),文案本来就在,只是写在了读屏器读不到的地方。
/// </remarks>
[TestClass]
[TestCategory("Design")]
public sealed class AccessibleNameTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AccessibleNameTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestMethod]
    public void AnIconButtonTakesItsNameFromTheTooltip()
    {
        OnUi(() =>
        {
            var button = new Button { Content = new Border() };
            ToolTip.SetTip(button, "新建连接");

            AccessibleName.SetFromToolTip(button, true);

            Assert.AreEqual("新建连接", AutomationProperties.GetName(button));
        });
    }

    [TestMethod]
    public void AnExplicitNameIsNeverOverwritten()
    {
        // 显式写过的名字是作者刻意为读屏器准备的,通常比提示更准确。
        OnUi(() =>
        {
            var button = new Button { Content = new Border() };
            AutomationProperties.SetName(button, "刻意写的名字");
            ToolTip.SetTip(button, "提示文案");

            AccessibleName.SetFromToolTip(button, true);

            Assert.AreEqual("刻意写的名字", AutomationProperties.GetName(button));
        });
    }

    [TestMethod]
    public void ATextButtonIsLeftAlone()
    {
        // 文字按钮的内容本身就是名字,读屏器读得到,不必再套一层。
        OnUi(() =>
        {
            var button = new Button { Content = "保存" };
            ToolTip.SetTip(button, "保存全部设置");

            AccessibleName.SetFromToolTip(button, true);

            Assert.IsTrue(string.IsNullOrEmpty(AutomationProperties.GetName(button)));
        });
    }

    [TestMethod]
    public void AButtonWithNeitherTooltipNorTextGetsNothing()
    {
        OnUi(() =>
        {
            var button = new Button { Content = new Border() };

            AccessibleName.SetFromToolTip(button, true);

            Assert.IsTrue(string.IsNullOrEmpty(AutomationProperties.GetName(button)));
        });
    }

    [TestMethod]
    public void ASettingsToggleTakesItsNameFromTheRowLabel()
    {
        // 设置页的开关自己一个字都没有,说明全在同一行左侧的标签上。
        OnUi(() =>
        {
            var label = new TextBlock { Text = "启动时检查更新" };
            label.Classes.Add("row-label");
            var toggle = new ToggleSwitch();
            _ = new Grid { Children = { label, toggle } };

            AccessibleName.SetFromRowLabel(toggle, true);

            Assert.AreEqual("启动时检查更新", AutomationProperties.GetName(toggle));
        });
    }

    [TestMethod]
    public void ARowLabelNeverReachesIntoTheNeighbouringRow()
    {
        // 往上找得太深会窜到隔壁行的标签,给出一个错的名字 —— 那比没有名字更糟。
        OnUi(() =>
        {
            var neighbour = new TextBlock { Text = "隔壁行的标签" };
            neighbour.Classes.Add("row-label");
            var toggle = new ToggleSwitch();
            _ = new StackPanel
            {
                Children =
                {
                    new Grid { Children = { neighbour } },
                    new Grid { Children = { new StackPanel { Children = { new Grid { Children = { toggle } } } } } },
                },
            };

            AccessibleName.SetFromRowLabel(toggle, true);

            Assert.IsTrue(string.IsNullOrEmpty(AutomationProperties.GetName(toggle)));
        });
    }

    [TestMethod]
    public void TheTerminalIntroducesItselfAsANamedDocument()
    {
        // 终端整片是自绘的,没有 peer 时读屏器只报一个匿名 Control ——
        // 连"这里是哪台机器的终端"都说不出来。
        OnUi(() =>
        {
            var terminal = new VelaTerminalControl { AccessibleName = "root@10.0.0.5" };

            AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(terminal);

            Assert.AreEqual(AutomationControlType.Document, peer.GetAutomationControlType());
            Assert.AreEqual("root@10.0.0.5", peer.GetName());
            Assert.AreEqual("Terminal", peer.GetClassName());
        });
    }

    /// <summary>
    /// 设置窗口里每一个可见按钮都必须有读屏器读得出来的名字。
    /// </summary>
    /// <remarks>
    /// 挑设置窗口,是因为它是图标按钮与文字按钮最密的地方,而且全部按钮都靠
    /// <c>DockStyles.axaml</c> 那条全局样式拿名字 —— 哪天有人给某个页面写了一条更靠后的
    /// <c>Button</c> 样式把它盖掉,这里会立刻红。
    /// </remarks>
    [TestMethod]
    public void EveryVisibleButtonInTheSettingsWindowHasAName()
    {
        OnUi(() =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            var viewModel = new SettingsViewModel(settings, theme);
            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            List<string> nameless = [];
            int checkedCount = 0;
            foreach (SettingsSectionKey key in Enum.GetValues<SettingsSectionKey>())
            {
                viewModel.SelectSection(key);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                foreach (Button button in window.GetVisualDescendants().OfType<Button>()
                             .Where(b => b.IsEffectivelyVisible && !IsTemplatePart(b)))
                {
                    checkedCount++;
                    if (Describes(button))
                    {
                        continue;
                    }
                    nameless.Add($"{key}: {button.Name ?? button.GetType().Name}");
                }
            }

            Assert.IsGreaterThan(10, checkedCount,
                $"只扫到 {checkedCount} 个按钮 —— 扫描八成失效了,别让这条测试变成空壳。");
            Assert.IsEmpty(nameless,
                "以下按钮读屏器读不出名字(既没有 AutomationProperties.Name,也没有文字内容或 ToolTip):"
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, nameless.Distinct())}");

            window.Close();
        });
    }

    /// <summary>
    /// 设置窗之外的几块主界面同样不许有无名按钮。
    /// </summary>
    /// <remarks>
    /// 计划点名的高频操作正落在这几块:文件删除/上传(文件面板)、传输取消(传输浮窗)、
    /// 录制播放与清理(回放中心)。设置窗那条扫描守不到它们 —— 那是另一棵可视树。
    /// </remarks>
    [TestMethod]
    [DynamicData(nameof(NamedSurfaces))]
    public void EveryVisibleButtonOnAMainSurfaceHasAName(string surface, Func<Control> build)
    {
        OnUi(() =>
        {
            Control content = build();
            var window = new Window { Width = 1200, Height = 700, Content = content };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            List<string> nameless = [];
            int checkedCount = 0;
            foreach (Button button in window.GetVisualDescendants().OfType<Button>()
                         .Where(b => b.IsEffectivelyVisible && !IsTemplatePart(b)))
            {
                checkedCount++;
                // 判据用 AutomationPeer 的实际产出,而不是"有没有写 Name / 有没有字符串内容":
                // 列表头那种"StackPanel 里放一个 TextBlock"的按钮,读屏器本来就读得出来,
                // 再给它加一个 Name 只会让人听两遍(计划里明确不要这种机械重复)。
                if (string.IsNullOrWhiteSpace(ControlAutomationPeer.CreatePeerForElement(button).GetName()))
                {
                    nameless.Add(button.Name ?? DescribeShape(button));
                }
            }

            Assert.IsGreaterThan(0, checkedCount, $"{surface}:一个按钮都没扫到,这条测试成了空壳。");
            Assert.IsEmpty(nameless,
                $"{surface} 上以下按钮读屏器读不出名字(既没有 AutomationProperties.Name,也没有文字内容或 ToolTip):"
                + $"{Environment.NewLine}{string.Join(Environment.NewLine, nameless.Distinct())}");

            window.Close();
        });
    }

    /// <summary>被扫描的几块界面。每一项都是"能独立挂进 headless 窗口"的那种视图。</summary>
    public static IEnumerable<object[]> NamedSurfaces =>
    [
        ["远端文件面板", () => (Control)new FileBrowserView
        {
            DataContext = new FileBrowserViewModel(Substitute.For<ISftpService>(), Guid.NewGuid()) { IsVisible = true }
        }],
        ["传输浮窗", () => (Control)new FileTransferView
        {
            DataContext = BuildVisibleTransferPanel()
        }],
        ["本地文件面板", () => (Control)new LocalFilePaneView
        {
            DataContext = new LocalFilePaneViewModel(new TransferOptions())
        }]
    ];

    private static FileTransferViewModel BuildVisibleTransferPanel()
    {
        var viewModel = new FileTransferViewModel(null);
        viewModel.AddTransfer(new TransferTask
        {
            Id = Guid.NewGuid(),
            Type = TransferType.Upload,
            LocalPath = "/tmp/app.conf",
            RemotePath = "/etc/app.conf",
            Status = TransferStatus.InProgress
        });
        viewModel.ShowPanel();
        return viewModel;
    }

    /// <summary>没有 x:Name 的按钮:用它的图标/内容类型描述一下,好让失败信息能定位。</summary>
    private static string DescribeShape(Button button) =>
        $"{button.GetType().Name}(content: {button.Content?.GetType().Name ?? "null"})";

    /// <summary>按钮是不是有个读得出来的名字(自动化名字、文字内容,或提示)。</summary>
    private static bool Describes(Button button) =>
        !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))
        || button.Content is string { Length: > 0 }
        || ToolTip.GetTip(button) is string { Length: > 0 };

    /// <summary>是不是控件模板内部的零件(如 NumericUpDown 的上下箭头)。</summary>
    /// <remarks>
    /// 这些按钮不是本仓写的,是 Fluent 主题模板生成的:读屏器读的是外层
    /// <c>NumericUpDown</c> 的 peer,不会单独去念它的两个箭头。补名字既改不到
    /// (要重写整个模板),也不会让读屏体验更好。这条测试只管应用自己写的按钮。
    /// </remarks>
    private static bool IsTemplatePart(Button button) =>
        button.Name?.StartsWith("PART_", StringComparison.Ordinal) is true;
}
