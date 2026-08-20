using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Behaviors;
using VelaShell.Core.Models;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
[TestCategory("ConnectionProfileUi")]
public sealed class ConnectionProfileViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ConnectionProfileViewUiTests).Assembly);

    [TestMethod]
    public void ProtocolTabs_ExposeFocusableSftpAndFtp_AndKeepLegacyProtocolsDisabled()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel
            {
                Host = "files.example.com",
                Username = "root",
                Password = SecureStringConvert.FromPlaintext("secret"),
            };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var protocolButtons = window.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("proto-tab"))
                .ToList();
            // SSH / SFTP / FTP 三个内建可点页签;S3 与 Telnet 现在都由插件贡献,
            // 没装插件(单测宿主就是这种)时不出现。只剩串口一个禁用的 Border。
            Assert.HasCount(3, protocolButtons);
            Assert.IsTrue(protocolButtons.All(button => button.IsTabStop));
            AssertProtocolTabMotion(protocolButtons);

            var legacyProtocols = window.GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("proto-tab"))
                .ToList();
            Assert.HasCount(1, legacyProtocols);
            Assert.IsTrue(legacyProtocols.All(border => !border.IsEnabled));

            TextBox passwordBox = window.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(SecurePasswordBox.GetEnabled);
            Assert.IsTrue(EnglishInputLocale.GetEnabled(passwordBox));
            Assert.IsFalse(InputMethod.GetIsInputMethodEnabled(passwordBox));

            vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(protocolButtons.Single(button => button.Classes.Contains("selected")).IsEffectivelyVisible);
            Assert.IsTrue(vm.IsSftpSelected);

            // 切到 FTP:仍然只有一个页签处于选中态,且端口跟着切到 21(原本是 SSH 的 22)。
            vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(vm.IsFtpSelected);
            Assert.HasCount(1, protocolButtons.Where(button => button.Classes.Contains("selected")));
            Assert.AreEqual(21, vm.Port);
            Assert.IsFalse(vm.RequiresSshAuth);

            // 没有插件协议时,页签集合里不该凭空多出什么。
            Assert.IsEmpty(vm.PluginProtocols);
            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void CopyErrorButton_AppearsOnlyWithAnError_AndSwitchesToCopiedState()
    {
        _session.Dispatch(() =>
        {
            IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
            workflow.TestConnectionAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                    .Returns(new ConnectionTestResult(false, "Permission denied (publickey,password)."));
            var vm = new ConnectionProfileViewModel(connectionWorkflowService: workflow)
            {
                Host = "prod.example.com",
                Username = "root",
                Password = SecureStringConvert.FromPlaintext("secret")
            };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Button copyButton = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => button.Name == "CopyErrorButton");
            // 没出错时反馈条整条不占位置,复制按钮自然也不该露出来。
            Assert.IsFalse(copyButton.IsEffectivelyVisible);

            vm.TestConnectionCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(copyButton.IsEffectivelyVisible, "测试失败后错误信息旁必须出现复制按钮。");

            // 视图在 Opened 里把剪贴板回调注入了 VM;headless 下换成探针,
            // 断言点击真的走到剪贴板这一步(而不是命令灰着、按了没反应)。
            string? copied = null;
            vm.CopyToClipboard = text =>
            {
                copied = text;
                return Task.CompletedTask;
            };
            copyButton.Command?.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("Permission denied (publickey,password).", copied);
            Assert.IsTrue(vm.ErrorCopied);

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ProtocolTabIndicator_SlidesToSelectedTab()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel
            {
                Host = "files.example.com",
                Username = "root",
                Password = SecureStringConvert.FromPlaintext("secret"),
            };
            var window = new ConnectionProfileView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Border indicator = window.FindControl<Border>("ProtoTabIndicator")
                ?? throw new AssertFailedException("ProtoTabIndicator not found.");
            Button sshTab = window.FindControl<Button>("SshTab")!;
            Button sftpTab = window.FindControl<Button>("SftpTab")!;

            // 初始:下划线对齐 SSH。
            Assert.IsTrue(indicator.IsVisible);
            AssertIndicatorAligned(indicator, sshTab);

            // 切到 SFTP:下划线经 180ms 过渡滑到 SFTP(断言读基值,与动画时间解耦)。
            vm.SelectConnectionTypeCommand.Execute(ConnectionType.SFTP).Subscribe();
            Dispatcher.UIThread.RunJobs();
            AssertIndicatorAligned(indicator, sftpTab);

            // 切到 FTP:定位逻辑曾是「IsSftpSelected ? SftpTab : SshTab」的二元三目,
            // 加第三个协议后必须按枚举分派,否则下划线会留在 SFTP 上。
            Button ftpTab = window.FindControl<Button>("FtpTab")!;
            vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).Subscribe();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AssertIndicatorAligned(indicator, ftpTab);

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void Form_ScrollsAndKeepsFooterReachable_WhenWindowHeightIsCapped()
    {
        _session.Dispatch(() =>
        {
            var vm = new ConnectionProfileViewModel
            {
                Host = "s3.amazonaws.com",
                Username = "AKIA",
                Password = SecureStringConvert.FromPlaintext("secret"),
            };
            var window = new ConnectionProfileView { DataContext = vm };
            // 构造时就按屏幕工作区钳过高度:等到 Opened 再钳,用户会先看见一个高过屏幕的窗口。
            Assert.IsTrue(double.IsFinite(window.MaxHeight), "窗口高度上限应在显示前就设好。");
            // 屏幕再高也不越过设计上限(与设置窗口 768 对齐)——「能放下」不等于「该放这么高」。
            Assert.IsLessThanOrEqualTo(768d, window.MaxHeight, "大屏上应按设计上限钳住,而不是任其长到屏幕高度。");
            window.Show();
            // 模拟一块矮屏(Opened 里会按真实屏幕重新钳一次,所以只能在显示之后压):
            // 插件协议字段一多,原来的 StackPanel 会一路长到屏幕外,底部的保存/连接按钮
            // 点不到 —— 现在超出的部分必须由表单区自己滚。
            window.MaxHeight = 320;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.IsLessThanOrEqualTo(320.5, window.Bounds.Height, "窗口高度不得越过上限。");

            ScrollViewer form = window.GetVisualDescendants().OfType<ScrollViewer>()
                .First(scroll => scroll.GetVisualParent() is Grid);
            Assert.IsGreaterThan(form.Viewport.Height, form.Extent.Height, "表单放不下时必须可滚动。");

            // 页脚是定高行,不参与滚动:连接按钮永远落在窗口里。
            Button connect = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Classes.Contains("dlg-primary"));
            Point origin = connect.TranslatePoint(default, window) ?? default;
            Assert.IsLessThanOrEqualTo(window.Bounds.Height + 0.5, origin.Y + connect.Bounds.Height,
                "「连接」按钮必须留在窗口可视区域内。");

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void AssertIndicatorAligned(Border indicator, Button tab)
    {
        // 读基值(过渡目标)而非属性现值:现值在 180ms 滑动期间是动画中间值。
        Visual panel = indicator.GetVisualParent()!;
        Point origin = tab.TranslatePoint(default, panel) ?? default;
        double actualX = indicator.GetBaseValue(Visual.RenderTransformProperty).GetValueOrDefault()?.Value.M31 ?? -1;
        double actualWidth = indicator.GetBaseValue(Avalonia.Layout.Layoutable.WidthProperty).GetValueOrDefault(double.NaN);
        Assert.AreEqual(Math.Round(origin.X), actualX, 0.6, "下划线应与选中协议标签左缘对齐。");
        Assert.AreEqual(Math.Round(tab.Bounds.Width), actualWidth, 0.6, "下划线宽度应等于选中协议标签宽度。");
    }

    private static void AssertProtocolTabMotion(IReadOnlyList<Button> protocolButtons)
    {
        foreach (Button button in protocolButtons)
        {
            Assert.IsNotNull(button.Transitions);
            Assert.HasCount(3, button.Transitions);
            Assert.Contains(transition =>
                transition is BrushTransition { Property: var property, Duration: var duration }
                && property == Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty
                && duration == TimeSpan.FromMilliseconds(120), button.Transitions);
            Assert.Contains(transition =>
                transition is BrushTransition { Property: var property, Duration: var duration }
                && property == Border.BorderBrushProperty
                && duration == TimeSpan.FromMilliseconds(120), button.Transitions);
            Assert.Contains(transition =>
                transition is BrushTransition { Property: var property, Duration: var duration }
                && property == Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty
                && duration == TimeSpan.FromMilliseconds(120), button.Transitions);
        }
    }
}
