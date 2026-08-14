using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.ViewModels;
using VelaShell.Views;
using VelaShell.Views.Settings;

namespace VelaShell.Tests.Views;

/// <summary>设置窗口键盘交互的 Headless UI 回归测试。</summary>
[TestClass]
[TestCategory("SettingsUi")]
public class SettingsViewUiTests
{
    // 必须与全程序集共用同一个会话(VelaHeadlessApp)。此前这里自起
    // StartNew(私有 HeadlessTestApp) 并在 ClassCleanup 里 Dispose:第二个 Application
    // 与共享会话争用进程级单例,Dispose 还会拆掉共享 Dispatcher —— 这正是
    // "ViewModels+Views 合跑整卷死锁、单类跑全绿"的根因。共享会话建成后活到进程结束,
    // 任何测试类都不得 Dispose 它。
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SettingsViewUiTests).Assembly);

    [TestMethod]
    public void Escape_FromTextBox_ClosesWindowAndRollsBackPreview()
    {
        OnUi(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings { Theme = "dark" });
            var viewModel = new SettingsViewModel(settings, theme);
            await viewModel.LoadCommand.Execute().FirstAsync();
            viewModel.Theme = "light";
            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextBox textBox = window.GetVisualDescendants().OfType<TextBox>().First();
            textBox.Focus();

            textBox.RaiseEvent(
                new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }
            );
            Dispatcher.UIThread.RunJobs();

            Assert.IsFalse(window.IsVisible);
            theme.Received().SetTheme("dark");
        });
    }

    [TestMethod]
    public void NonOpacityAppearanceChange_RemainsTrailingSnapshotDebounced()
    {
        OnUi(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            ISettingsPreviewService preview = new SettingsPreviewService();
            var snapshots = new List<AppSettings>();
            var opacityValues = new List<int>();
            preview.PreviewRequested += snapshot => snapshots.Add(snapshot);
            preview.WindowOpacityPreviewRequested += value => opacityValues.Add(value);
            settings.GetSettingsAsync().Returns(new AppSettings());

            var viewModel = new SettingsViewModel(settings, theme, previewService: preview);
            await viewModel.LoadCommand.Execute().FirstAsync();
            viewModel.Appearance.SidebarPosition = "right";

            Assert.IsEmpty(snapshots);
            Assert.IsEmpty(opacityValues);

            await Task.Delay(75);
            Dispatcher.UIThread.RunJobs();

            Assert.HasCount(1, snapshots);
            Assert.AreEqual("right", snapshots[0].Appearance.SidebarPosition);
            Assert.IsEmpty(opacityValues);
        });
    }

    [TestMethod]
    public void AppearanceOpacitySlider_EmitsEveryValueImmediately()
    {
        OnUi(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            var preview = new SettingsPreviewService();
            settings.GetSettingsAsync().Returns(new AppSettings());

            var viewModel = new SettingsViewModel(settings, theme, previewService: preview);
            await viewModel.LoadCommand.Execute().FirstAsync();
            viewModel.SelectedSectionIndex = 1;

            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Slider opacitySlider = window
                .GetVisualDescendants()
                .OfType<Slider>()
                .Single(slider => slider.Minimum == 10 && slider.Maximum == 100);
            var received = new List<int>();
            preview.WindowOpacityPreviewRequested += value => received.Add(value);
            int[] expected = [20, 30, 40, 50];

            foreach (int value in expected)
            {
                opacitySlider.Value = value;
                Dispatcher.UIThread.RunJobs();
            }

            Assert.AreSequenceEqual(expected, received);
            window.Close();
        });
    }

    [TestMethod]
    public void CancelBeforePendingAppearanceDebounce_DoesNotPreviewEditedStateAfterRollback()
    {
        ISettingsService settings = Substitute.For<ISettingsService>();
        IThemeService theme = Substitute.For<IThemeService>();
        var preview = new SettingsPreviewService();
        var baseline = new AppSettings
        {
            Appearance = new() { WindowOpacityPercent = 80, SidebarPosition = "left" },
        };
        settings.GetSettingsAsync().Returns(baseline);
        var viewModel = new SettingsViewModel(settings, theme, previewService: preview);
        viewModel.LoadCommand.Execute().FirstAsync().GetAwaiter().GetResult();
        var snapshots = new List<AppSettings>();
        OnUi(async () =>
        {
            preview.PreviewRequested += snapshot => snapshots.Add(snapshot);

            viewModel.Appearance.SidebarPosition = "right";
            viewModel.Appearance.WindowOpacityPercent = 40;
            viewModel.NotifyClosed();

            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
        });

        Assert.HasCount(1, snapshots);
        Assert.AreEqual(80, snapshots[0].Appearance.WindowOpacityPercent);
        Assert.AreEqual("left", snapshots[0].Appearance.SidebarPosition);
    }

    /// <summary>
    /// 关于页的开源依赖表必须把 <see cref="SettingsViewModel.AboutDependencies" /> 逐条画出来。
    /// 断言落在**已布局的文本**上而不是集合内容：往 ItemsControl 里加数据是一回事，
    /// 那些行真的被渲染出来是另一回事（不可见期入列的行会占住高度却画不出来）。
    /// 名称与许可证一并核对 —— 许可证写错比不写更糟。
    /// </summary>
    [TestMethod]
    public void AboutPage_RendersEveryOpenSourceDependencyRow()
    {
        OnUi(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            var viewModel = new SettingsViewModel(settings, theme);
            await viewModel.LoadCommand.Execute().FirstAsync();
            var page = new AboutPage { DataContext = viewModel };
            var window = new Window
            {
                Width = 720,
                Height = 1400,
                Content = page,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            string[] painted =
            [
                .. page.GetVisualDescendants()
                       .OfType<TextBlock>()
                       .Where(text => text.Bounds.Width > 0 && text.Text is { Length: > 0 })
                       .Select(text => text.Text!)
            ];
            Assert.IsNotEmpty(viewModel.AboutDependencies);
            foreach (DependencyInfo dependency in viewModel.AboutDependencies)
            {
                Assert.Contains(dependency.Name, painted, $"关于页没画出依赖「{dependency.Name}」");
                Assert.Contains(
                    dependency.License,
                    painted,
                    $"「{dependency.Name}」的许可证没画出来"
                );
                Assert.StartsWith("https://", dependency.Url);
                Assert.StartsWith("https://", dependency.LicenseUrl);
            }
            // FTP 与 IP 归属地是随功能一起引入的两个依赖,曾漏登记在册。
            Assert.Contains("FluentFTP", painted);
            Assert.Contains("MaxMind.Db", painted);
            window.Close();
        });
    }

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();
}
