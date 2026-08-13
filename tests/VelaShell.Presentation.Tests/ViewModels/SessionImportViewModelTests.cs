using VelaShell.Core.Import;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Presentation.Tests.ViewModels;

/// <summary>
/// 会话导入对话框的「全自动」行为:打开即扫描全部来源、跨来源去重、
/// 自动勾选可直接导入的会话,同时保留用户自行挑选的能力。
/// </summary>
[TestClass]
[TestCategory("SessionImport")]
public class SessionImportViewModelTests
{
    [TestMethod]
    public async Task Initialize_ScansEverySource_AndAutoSelectsImportableSessions()
    {
        var xshell = new FakeImportService("Xshell", Session("web", "10.0.0.1", password: "p1"));
        var winscp = new FakeImportService("WinSCP",
            Session("db", "10.0.0.2"),
            Session("ftp", "10.0.0.3", supported: false));
        var vm = new SessionImportViewModel([xshell, winscp]);

        await vm.InitializeAsync();

        Assert.AreEqual(1, xshell.ScanCount);
        Assert.AreEqual(1, winscp.ScanCount);
        Assert.AreEqual(3, vm.TotalCount);
        Assert.AreEqual(2, vm.SelectedCount);     // 不支持的协议自动排除
        Assert.AreEqual(1, vm.RecoveredCount);
        Assert.AreEqual(1, vm.SkippedCount);
        Assert.IsFalse(vm.IsBusy);
        Assert.IsFalse(vm.IsAdvanced);            // 默认是全自动模式,不需要用户先做选择
    }

    [TestMethod]
    public async Task Initialize_SkipsSessionsThatAlreadyExist()
    {
        var winscp = new FakeImportService("WinSCP",
            Session("new", "10.0.0.1"),
            Session("old", "10.0.0.2", exists: true));
        var vm = new SessionImportViewModel([winscp]);

        await vm.InitializeAsync();

        Assert.AreEqual(1, vm.SelectedCount);
        Assert.IsTrue(vm.Sources[0].Items[1].IsDuplicate);
        Assert.IsFalse(vm.Sources[0].Items[1].IsSelected);
    }

    [TestMethod]
    public async Task Initialize_DeduplicatesAcrossSources_KeepingTheFirstOne()
    {
        var xshell = new FakeImportService("Xshell", Session("web", "10.0.0.1", user: "root"));
        var winscp = new FakeImportService("WinSCP", Session("web-copy", "10.0.0.1", user: "root"));
        var vm = new SessionImportViewModel([xshell, winscp]);

        await vm.InitializeAsync();

        Assert.AreEqual(2, vm.TotalCount);
        Assert.AreEqual(1, vm.SelectedCount);
        Assert.IsTrue(vm.Sources[0].Items[0].IsSelected);
        Assert.IsTrue(vm.Sources[1].Items[0].IsDuplicate);
        Assert.IsFalse(vm.Sources[1].Items[0].IsSelected);
    }

    [TestMethod]
    public async Task ClearingSkipExisting_BringsDuplicatesBack()
    {
        var winscp = new FakeImportService("WinSCP",
            Session("new", "10.0.0.1"),
            Session("old", "10.0.0.2", exists: true));
        var vm = new SessionImportViewModel([winscp]);
        await vm.InitializeAsync();

        vm.SkipExisting = false;

        Assert.AreEqual(2, vm.SelectedCount);
        Assert.IsTrue(vm.Sources[0].Items[1].IsSelected);
    }

    [TestMethod]
    public async Task UserCanStillPickManually_CountsFollowTheCheckboxes()
    {
        var winscp = new FakeImportService("WinSCP",
            Session("a", "10.0.0.1", password: "p"),
            Session("b", "10.0.0.2"));
        var vm = new SessionImportViewModel([winscp]);
        await vm.InitializeAsync();

        vm.IsAdvanced = true;
        Assert.IsTrue(vm.Sources[0].IsExpanded);   // 切到自定义模式即展开逐条会话

        vm.Sources[0].Items[0].IsSelected = false;

        Assert.AreEqual(1, vm.SelectedCount);
        Assert.AreEqual(0, vm.RecoveredCount);
    }

    [TestMethod]
    public async Task Import_WritesSelectedSessionsPerSource_AndAggregatesTheOutcome()
    {
        var xshell = new FakeImportService("Xshell", Session("web", "10.0.0.1", password: "p1"));
        var winscp = new FakeImportService("WinSCP",
            Session("db", "10.0.0.2"),
            Session("dup", "10.0.0.3", exists: true));
        var vm = new SessionImportViewModel([xshell, winscp]);
        await vm.InitializeAsync();

        SessionImportOutcome? outcome = await vm.ImportSelectedAsync();

        Assert.IsNotNull(outcome);
        Assert.AreEqual(2, outcome.Imported);
        Assert.AreEqual(1, outcome.PasswordsRecovered);
        CollectionAssert.AreEqual(new[] { "web" }, xshell.ImportedNames);
        CollectionAssert.AreEqual(new[] { "db" }, winscp.ImportedNames);   // 重复项不写入
        StringAssert.Contains(xshell.ImportedGroup ?? string.Empty, "Xshell");   // 按来源各建一个分组
    }

    [TestMethod]
    public async Task NoSourceDetected_LeavesNothingToImport_ButKeepsManualEntryAvailable()
    {
        var xshell = new FakeImportService("Xshell") { Detected = false };
        var winscp = new FakeImportService("WinSCP") { Detected = false };
        var vm = new SessionImportViewModel([xshell, winscp]);

        await vm.InitializeAsync();

        Assert.AreEqual(0, vm.TotalCount);
        Assert.AreEqual(0, vm.SelectedCount);
        Assert.IsFalse(vm.Sources[0].Detected);
        Assert.IsTrue(vm.Sources[0].ShowSourceActions);   // 自动模式下也给出「手动指定」入口
        Assert.IsTrue(vm.Sources[0].CanBrowse);
    }

    [TestMethod]
    public async Task MasterPasswordSources_AreCalledOutOnce_WithTheirNames()
    {
        var xshell = new FakeImportService("Xshell", Session("web", "10.0.0.1")) { MasterPassword = true };
        var winscp = new FakeImportService("WinSCP", Session("db", "10.0.0.2"));
        var vm = new SessionImportViewModel([xshell, winscp]);

        await vm.InitializeAsync();

        Assert.IsTrue(vm.HasMasterPasswordWarning);
        StringAssert.Contains(vm.MasterPasswordWarning, "Xshell");
        Assert.IsFalse(vm.MasterPasswordWarning.Contains("WinSCP", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ManualSourceRescan_RefreshesThatSourceAndTheSummary()
    {
        var winscp = new FakeImportService("WinSCP");
        var vm = new SessionImportViewModel([winscp]);
        await vm.InitializeAsync();
        Assert.AreEqual(0, vm.TotalCount);

        winscp.SetSessions(Session("db", "10.0.0.2", password: "p"));
        vm.Sources[0].SourceText = @"D:\portable\WinSCP.ini";
        await vm.Sources[0].ScanAsync();

        Assert.AreEqual(@"D:\portable\WinSCP.ini", winscp.LastScannedSource);
        Assert.AreEqual(1, vm.TotalCount);
        Assert.AreEqual(1, vm.SelectedCount);
        Assert.AreEqual(1, vm.RecoveredCount);
    }

    private static ImportedSession Session(
        string name,
        string host,
        string user = "root",
        string? password = null,
        bool supported = true,
        bool exists = false) =>
        new()
        {
            Name = name,
            Host = host,
            Port = 22,
            Username = user,
            Protocol = supported ? "SSH" : "FTP",
            IsSupported = supported,
            HasEncryptedPassword = password is not null,
            Password = password,
            AlreadyExists = exists
        };

    /// <summary>可控的导入来源替身:记录扫描/导入调用,便于验证「打开即自动扫描全部来源」。</summary>
    private sealed class FakeImportService(string key, params ImportedSession[] sessions) : ISessionImportService
    {
        private ImportedSession[] _sessions = sessions;

        public string SourceKey => key;

        public ImportBrowseKind BrowseKind => ImportBrowseKind.File;

        public bool Detected { get; init; } = true;

        public bool MasterPassword { get; init; }

        public int ScanCount { get; private set; }

        public string? LastScannedSource { get; private set; }

        public List<string> ImportedNames { get; } = [];

        public string? ImportedGroup { get; private set; }

        public void SetSessions(params ImportedSession[] sessions) => _sessions = sessions;

        public string? DetectDefaultSource() => Detected ? $@"C:\{key}\config.ini" : null;

        public Task<SessionImportScan> ScanAsync(string? source, CancellationToken cancellationToken = default)
        {
            ScanCount++;
            LastScannedSource = source;
            return Task.FromResult(new SessionImportScan
            {
                Source = source ?? string.Empty,
                Items = _sessions,
                MasterPasswordEnabled = MasterPassword
            });
        }

        public Task<SessionImportOutcome> ImportAsync(IReadOnlyList<ImportedSession> items, string groupName, CancellationToken cancellationToken = default)
        {
            ImportedGroup = groupName;
            ImportedNames.AddRange(items.Select(static i => i.Name));
            return Task.FromResult(new SessionImportOutcome
            {
                Imported = items.Count,
                PasswordsRecovered = items.Count(static i => i.PasswordRecovered),
                GroupId = Guid.NewGuid()
            });
        }
    }
}
