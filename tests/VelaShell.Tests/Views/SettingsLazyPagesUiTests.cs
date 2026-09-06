using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Data;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Services;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;
using VelaShell.Views.Settings;

namespace VelaShell.Tests.Views;

/// <summary>
/// 设置窗口的分页按需创建。
/// </summary>
/// <remarks>
/// 原先 12 页全部常驻、靠 <c>IsVisible</c> 切换,窗口一打开就把 12 棵控件树全建出来
/// (外观页 9 个 <c>ItemsControl</c>、快捷键页 8 个),而用户多半只看其中一两页。
/// </remarks>
[TestClass]
[TestCategory("SettingsUi")]
public sealed class SettingsLazyPagesUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SettingsLazyPagesUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    private static void OnSettingsWindow(Action<SettingsView, SettingsViewModel> body) =>
        _session.Dispatch(async () =>
        {
            ISettingsService settings = Substitute.For<ISettingsService>();
            IThemeService theme = Substitute.For<IThemeService>();
            settings.GetSettingsAsync().Returns(new AppSettings());
            var viewModel = new SettingsViewModel(settings, theme);
            await viewModel.LoadCommand.Execute().FirstAsync();

            var window = new SettingsView { DataContext = viewModel };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            body(window, viewModel);

            window.Close();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    [TestMethod]
    public void OpeningTheWindow_BuildsOnlyTheFirstPage()
    {
        OnSettingsWindow((window, _) =>
        {
            Assert.AreEqual(1, window.CreatedPageCountForTest,
                "打开设置窗口不该把 12 页全建出来。");
            Assert.HasCount(1, window.GetVisualDescendants().OfType<GeneralSettingsPage>());
            Assert.IsEmpty(window.GetVisualDescendants().OfType<AboutPage>());
        });
    }

    [TestMethod]
    public void SwitchingSection_BuildsThatPageOnly()
    {
        OnSettingsWindow((window, vm) =>
        {
            vm.SelectSection(SettingsSectionKey.About);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.AreEqual(2, window.CreatedPageCountForTest);
            Assert.HasCount(1, window.GetVisualDescendants().OfType<AboutPage>());
            // 切走的页面从视觉树上撤下来,不再参与布局与渲染。
            Assert.IsEmpty(window.GetVisualDescendants().OfType<GeneralSettingsPage>());
        });
    }

    [TestMethod]
    public void SwitchingBackReusesTheSameInstance()
    {
        // 页面上有滚动位置、展开的分组、填了一半的输入框,切回来时都要在。
        OnSettingsWindow((window, vm) =>
        {
            GeneralSettingsPage first = window.GetVisualDescendants().OfType<GeneralSettingsPage>().Single();

            vm.SelectSection(SettingsSectionKey.About);
            Dispatcher.UIThread.RunJobs();
            vm.SelectSection(SettingsSectionKey.General);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            GeneralSettingsPage again = window.GetVisualDescendants().OfType<GeneralSettingsPage>().Single();
            Assert.IsTrue(ReferenceEquals(first, again), "切回来应当复用同一个页面实例,而不是重建。");
            Assert.AreEqual(2, window.CreatedPageCountForTest, "复用就不该再多建一个。");
        });
    }

    [TestMethod]
    public void EverySection_BuildsAPage_AndKeepsTheViewModelAsDataContext()
    {
        // DataContext 是这条路最容易踩的坑:用 ContentControl + IDataTemplate 的话,
        // 页面的 DataContext 会变成分区标识枚举,整窗设置项集体失灵。
        OnSettingsWindow((window, vm) =>
        {
            foreach (SettingsSectionKey key in Enum.GetValues<SettingsSectionKey>())
            {
                vm.SelectSection(key);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Control page = window.GetVisualDescendants()
                    .OfType<Panel>()
                    .First(panel => panel.Name == "PageHost")
                    .Children
                    .Single();
                Assert.IsTrue(ReferenceEquals(vm, page.DataContext),
                    $"{key} 页的 DataContext 不是 SettingsViewModel —— 该页所有绑定都会失灵。");
            }

            Assert.AreEqual(Enum.GetValues<SettingsSectionKey>().Length, window.CreatedPageCountForTest);
        });
    }
}
