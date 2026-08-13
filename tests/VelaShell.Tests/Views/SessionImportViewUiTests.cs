using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Core.Import;
using VelaShell.Presentation.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 「导入会话」对话框的界面契约:打开即自动出结果,默认不要求用户做任何选择;
/// 需要时切到「自己挑选会话」才展开逐条勾选。
/// </summary>
[TestClass]
[TestCategory("SessionImportUi")]
public sealed class SessionImportViewUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(SessionImportViewUiTests).Assembly);

    [TestMethod]
    public void Opening_AutoScansAndPreparesEverything_WithoutAnyUserChoice()
    {
        _session.Dispatch(() =>
        {
            var window = new SessionImportView { DataContext = CreateViewModel(out SessionImportViewModel vm) };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // 打开即扫完:两个来源各一张卡片,勾选与统计都已就绪。
            Assert.HasCount(2, vm.Sources);
            Assert.AreEqual(3, vm.TotalCount);
            Assert.AreEqual(2, vm.SelectedCount);   // 重复项自动跳过
            Assert.IsFalse(vm.IsBusy);

            // 自动模式下不摆出一堆勾选框——用户不必先弄懂怎么选。
            Assert.IsEmpty(VisibleCheckBoxes(window));

            Button import = PrimaryButton(window);
            Assert.IsTrue(import.IsEffectivelyEnabled, "扫描完成后导入按钮应可直接点击。");
            Assert.Contains("2", (string)import.Content!, "导入按钮要写清将导入几个会话。");

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void SwitchingToManualMode_RevealsPerSessionCheckboxes_AndFollowsTheUser()
    {
        _session.Dispatch(() =>
        {
            var window = new SessionImportView { DataContext = CreateViewModel(out SessionImportViewModel vm) };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.IsAdvanced = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // 3 条会话 + 1 个「跳过已存在的会话」开关。
            Assert.HasCount(4, VisibleCheckBoxes(window));

            vm.Sources[0].Items[0].IsSelected = false;
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(1, vm.SelectedCount);
            Assert.Contains("1", (string)PrimaryButton(window).Content!);

            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static List<CheckBox> VisibleCheckBoxes(Window window) =>
        [.. window.GetVisualDescendants().OfType<CheckBox>().Where(static box => box.IsEffectivelyVisible)];

    private static Button PrimaryButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Single(static b => b.Classes.Contains("dlg-primary"));

    private static SessionImportViewModel CreateViewModel(out SessionImportViewModel viewModel)
    {
        viewModel = new SessionImportViewModel(
        [
            new FakeImportService("Xshell", Session("web", "10.0.0.1", password: "p")),
            new FakeImportService("WinSCP",
                Session("db", "10.0.0.2"),
                Session("dup", "10.0.0.3", exists: true))
        ]);
        return viewModel;
    }

    private static ImportedSession Session(string name, string host, string? password = null, bool exists = false) =>
        new()
        {
            Name = name,
            Host = host,
            Port = 22,
            Username = "root",
            Protocol = "SSH",
            IsSupported = true,
            HasEncryptedPassword = password is not null,
            Password = password,
            AlreadyExists = exists
        };

    private sealed class FakeImportService(string key, params ImportedSession[] sessions) : ISessionImportService
    {
        public string SourceKey => key;

        public ImportBrowseKind BrowseKind => ImportBrowseKind.File;

        public string? DetectDefaultSource() => $@"C:\{key}\config.ini";

        public Task<SessionImportScan> ScanAsync(string? source, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionImportScan { Source = source ?? string.Empty, Items = sessions });

        public Task<SessionImportOutcome> ImportAsync(IReadOnlyList<ImportedSession> items, string groupName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionImportOutcome { Imported = items.Count, PasswordsRecovered = 0, GroupId = Guid.NewGuid() });
    }
}
