using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Localization;
using VelaShell.Presentation.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>隧道面板与帮助窗口的真实 Avalonia 布局、主题和截图回归测试。</summary>
[TestClass]
[TestCategory("TunnelUI")]
public sealed class TunnelPanelUiTests
{
    private static HeadlessUnitTestSession _session = null!;
    private static LocalizationService _localization = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TunnelPanelUiTests).Assembly);
        _localization = new();
        LocalizedStrings.Instance.Attach(_localization);
    }

    [TestMethod]
    public void Panel_HelpAndFormActions_FollowTheLayoutContract()
    {
        OnUi(() =>
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("zh-CN");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            _localization.SetLanguage("zh-CN");
            try
            {
                var vm = new TunnelPanelViewModel(Substitute.For<ITunnelWorkflowService>());
                vm.Tunnels.Add(new(new TunnelInfo
                {
                    Id = Guid.NewGuid(),
                    Config = new()
                    {
                        Type = TunnelType.LocalForward,
                        Name = string.Empty,
                        LocalHost = "127.0.0.1",
                        LocalPort = 5432,
                        RemoteHost = "127.0.0.1",
                        RemotePort = 5432
                    },
                    Status = TunnelStatus.Active,
                    SessionId = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow
                }));

                var view = new TunnelPanelView { DataContext = vm };
                var window = new Window { Width = 380, Height = 760, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Button help = view.FindControl<Button>("HelpButton")!;
                StackPanel actions = view.FindControl<StackPanel>("FormActions")!;
                Assert.IsNotNull(help);
                Assert.AreEqual(HorizontalAlignment.Right, actions.HorizontalAlignment);
                Assert.IsGreaterThan(0, help.Bounds.Width);

                SaveFrame(window, "tunnel-panel-dark.png");
                window.Close();
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
                _localization.SetLanguage(previousUiCulture.Name);
            }
        });
    }

    [TestMethod]
    public void Panel_EditButtons_ReflectTunnelStatusAndTooltip()
    {
        OnUi(() =>
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            try
            {
                var vm = new TunnelPanelViewModel(Substitute.For<ITunnelWorkflowService>());
                vm.Tunnels.Add(new(new TunnelInfo
                {
                    Id = Guid.NewGuid(),
                    Config = new()
                    {
                        Type = TunnelType.LocalForward,
                        Name = "active",
                        LocalHost = "127.0.0.1",
                        LocalPort = 5432,
                        RemoteHost = "127.0.0.1",
                        RemotePort = 5432
                    },
                    Status = TunnelStatus.Active,
                    SessionId = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow
                }));
                vm.Tunnels.Add(new(new TunnelInfo
                {
                    Id = Guid.NewGuid(),
                    Config = new()
                    {
                        Type = TunnelType.LocalForward,
                        Name = "stopped",
                        LocalHost = "127.0.0.1",
                        LocalPort = 5433,
                        RemoteHost = "127.0.0.1",
                        RemotePort = 5433
                    },
                    Status = TunnelStatus.Stopped,
                    SessionId = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow
                }));

                var view = new TunnelPanelView { DataContext = vm };
                var window = new Window { Width = 380, Height = 760, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Button[] editButtons = [.. view.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(button => ToolTip.GetTip(button) is string tip
                        && (string.Equals(tip, Strings.Get("Tunnel_EditTip"))
                            || string.Equals(tip, Strings.Get("Tunnel_EditDisabledTip"))))];

                Assert.HasCount(2, editButtons);
                Assert.IsFalse(editButtons.Single(button => Equals(ToolTip.GetTip(button), Strings.Get("Tunnel_EditDisabledTip"))).IsEnabled);
                Assert.IsTrue(editButtons.Single(button => Equals(ToolTip.GetTip(button), Strings.Get("Tunnel_EditTip"))).IsEnabled);
                window.Close();
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
            }
        });
    }

    [TestMethod]
    public void HelpDialog_UsesOwnedDialogChrome_AndRendersThemesAndLocales()
    {
        OnUi(() =>
        {
            ThemeVariant original = Application.Current!.RequestedThemeVariant;
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                RenderHelpDialog("en-US", ThemeVariant.Dark, "tunnel-help-en-dark.png");
                RenderHelpDialog("zh-CN", ThemeVariant.Dark, "tunnel-help-zh-Hans-dark.png");
                RenderHelpDialog("zh-CN", ThemeVariant.Light, "tunnel-help-zh-Hans-light.png");
                RenderHelpDialog("zh-TW", ThemeVariant.Dark, "tunnel-help-zh-Hant-dark.png");
                RenderHelpDialog("ja-JP", ThemeVariant.Dark, "tunnel-help-ja-dark.png");
                RenderHelpDialog("ko-KR", ThemeVariant.Dark, "tunnel-help-ko-dark.png");
            }
            finally
            {
                Application.Current.RequestedThemeVariant = original;
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
                _localization.SetLanguage(previousUiCulture.Name);
            }
        });
    }

    private static void RenderHelpDialog(string cultureName, ThemeVariant theme, string fileName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        _localization.SetLanguage(cultureName);
        Application.Current!.RequestedThemeVariant = theme;
        var dialog = new TunnelHelpDialog();
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        dialog.UpdateLayout();

        Assert.AreEqual(WindowDecorations.None, dialog.WindowDecorations);
        Assert.IsFalse(dialog.ShowInTaskbar);
        Assert.IsFalse(dialog.CanResize);
        Assert.AreEqual(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);
        Assert.IsGreaterThanOrEqualTo(9, dialog.GetVisualDescendants().OfType<TextBlock>().Count());

        SaveFrame(dialog, fileName);
        dialog.Close();
    }

    private static void SaveFrame(TopLevel topLevel, string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        using WriteableBitmap? frame = topLevel.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "Skia headless renderer should produce a visual-QA frame.");
        using FileStream output = File.Create(Path.Combine(directory, fileName));
        frame.Save(output, PngBitmapEncoderOptions.Default);
    }

    private static void OnUi(Action action) => _session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}
