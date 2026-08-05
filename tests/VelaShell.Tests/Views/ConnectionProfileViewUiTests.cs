using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI.Primitives;
using VelaShell.Behaviors;
using VelaShell.Core.Models;
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
    public void ProtocolTabs_ExposeFocusableSftpAndKeepLegacyProtocolsDisabled()
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
            Assert.HasCount(2, protocolButtons);
            Assert.IsTrue(protocolButtons.All(button => button.IsTabStop));
            AssertProtocolTabMotion(protocolButtons);

            var legacyProtocols = window.GetVisualDescendants()
                .OfType<Border>()
                .Where(border => border.Classes.Contains("proto-tab"))
                .ToList();
            Assert.HasCount(2, legacyProtocols);
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
