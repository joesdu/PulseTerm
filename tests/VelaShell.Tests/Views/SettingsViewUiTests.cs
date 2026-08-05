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
using VelaShell.Features.Settings;

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

    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(body, CancellationToken.None).GetAwaiter().GetResult();
}
