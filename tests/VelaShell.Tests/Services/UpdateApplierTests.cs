using System.Formats.Tar;
using System.IO.Compression;
using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

[TestClass]
public class UpdateApplierTests : IDisposable
{
    private readonly string _appDir;
    private readonly UpdateApplier _applier;

    public UpdateApplierTests()
    {
        _appDir = Path.Combine(Path.GetTempPath(), $"velashell_applier_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_appDir);
        _applier = new(_appDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_appDir))
        {
            Directory.Delete(_appDir, true);
        }
        GC.SuppressFinalize(this);
    }

    private string CreateZip(params (string Path, string Content)[] entries)
    {
        string zipPath = Path.Combine(_applier.PrepareStagingDirectory(), "package.zip");
        using FileStream stream = File.Create(zipPath);
        using ZipArchive zip = new(stream, ZipArchiveMode.Create);
        foreach ((string path, string content) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(path);
            using StreamWriter writer = new(entry.Open());
            writer.Write(content);
        }
        return zipPath;
    }

    private void WriteAppFile(string relativePath, string content)
    {
        string path = Path.Combine(_appDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ReadAppFile(string relativePath) =>
        File.ReadAllText(Path.Combine(_appDir, relativePath));

    private bool AppFileExists(string relativePath) =>
        File.Exists(Path.Combine(_appDir, relativePath));

    private string BackupPath(string relativePath) =>
        Path.Combine(_applier.BackupDirectory, relativePath);

    private void WriteJournal(string phase, params (string Path, bool Existed)[] files)
    {
        Directory.CreateDirectory(_applier.StagingDirectory);
        string entries = string.Join(",\n", files.Select(f =>
            $$"""{ "Path": "{{f.Path}}", "Existed": {{(f.Existed ? "true" : "false")}} }"""));
        File.WriteAllText(
            Path.Combine(_applier.StagingDirectory, UpdateApplier.JournalFileName),
            $$"""{ "Phase": "{{phase}}", "Files": [ {{entries}} ] }""");
    }

    // ———————————————————— 解包(Stage) ————————————————————

    [TestMethod]
    [TestCategory("Update")]
    public void Stage_LeavesApplicationDirectoryUntouched()
    {
        WriteAppFile("app.exe", "old-exe");
        string zip = CreateZip(("app.exe", "new-exe"), ("lib/helper.dll", "new-dll"));

        IReadOnlyList<string> entries = _applier.Stage(zip);

        // 解包只往 payload 里写,应用目录一个字节都不动 —— 这是"升级失败也能原样重试"的基础。
        Assert.AreEqual("old-exe", ReadAppFile("app.exe"));
        Assert.IsFalse(AppFileExists(Path.Combine("lib", "helper.dll")));
        Assert.HasCount(2, entries);
        Assert.AreEqual("new-exe", File.ReadAllText(Path.Combine(_applier.PayloadDirectory, "app.exe")));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void CopyWithBudget_RejectsActualBytesBeyondLimit()
    {
        using var source = new MemoryStream(new byte[9]);
        using var destination = new MemoryStream();

        InvalidDataException ex = Assert.ThrowsExactly<InvalidDataException>(
            () => UpdateApplier.CopyWithBudget(source, destination, 8, "bomb.bin"));

        Assert.Contains("bomb.bin", ex.Message);
        Assert.IsLessThanOrEqualTo(8, destination.Length);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void CopyWithBudget_AllowsContentAtExactLimit()
    {
        byte[] content = "12345678"u8.ToArray();
        using var source = new MemoryStream(content);
        using var destination = new MemoryStream();

        long written = UpdateApplier.CopyWithBudget(source, destination, content.Length, "app.bin");

        Assert.AreEqual(content.Length, written);
        Assert.AreSequenceEqual(content, destination.ToArray());
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Stage_CorruptPackage_LeavesApplicationIntact_AndNextAttemptStillWorks()
    {
        // 老实现在这里会永久卡死:一次失败留下删不掉的残骸,之后每次升级都在同一处抛异常。
        WriteAppFile("app.exe", "old-exe");
        string corrupt = Path.Combine(_applier.PrepareStagingDirectory(), "package.zip");
        File.WriteAllText(corrupt, "not a zip at all");

        Assert.ThrowsExactly<InvalidDataException>(() => _applier.Stage(corrupt));
        Assert.AreEqual("old-exe", ReadAppFile("app.exe"));

        // 同一个 applier 立刻重试一个好包:必须照常成功,不需要任何人工清理。
        string good = CreateZip(("app.exe", "new-exe"));
        _applier.Apply(good);
        Assert.AreEqual("new-exe", ReadAppFile("app.exe"));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Stage_StalePayloadAndBackup_AreRebuilt()
    {
        WriteAppFile("app.exe", "current");
        string zip = CreateZip(("app.exe", "new-exe"));
        // 上一轮中断留下的解包内容与备份,绝不能混进本轮(否则回滚会拿古董版本覆盖新版)。
        Directory.CreateDirectory(_applier.PayloadDirectory);
        Directory.CreateDirectory(_applier.BackupDirectory);
        File.WriteAllText(Path.Combine(_applier.PayloadDirectory, "ancient.txt"), "ancient");
        File.WriteAllText(BackupPath("app.exe"), "ancient");

        _applier.Apply(zip);

        Assert.AreEqual("new-exe", ReadAppFile("app.exe"));
        Assert.AreEqual("current", File.ReadAllText(BackupPath("app.exe")));
        Assert.IsFalse(AppFileExists("ancient.txt"));
    }

    // ———————————————————— 换版(SwapFromPayload) ————————————————————

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_ReplacesPackagedFiles_AndLeavesUserFilesAlone()
    {
        WriteAppFile("app.exe", "old-exe");
        WriteAppFile("user-notes.txt", "user data");
        string zip = CreateZip(("app.exe", "new-exe"), ("lib/helper.dll", "new-dll"));

        _applier.Apply(zip);

        Assert.AreEqual("new-exe", ReadAppFile("app.exe"));
        Assert.AreEqual("new-dll", ReadAppFile(Path.Combine("lib", "helper.dll")));
        // 包外文件绝不动:既不移动也不删除。
        Assert.AreEqual("user data", ReadAppFile("user-notes.txt"));
        // 旧版文件进备份目录,而不是就地改名成 .old —— 应用目录始终干净。
        Assert.AreEqual("old-exe", File.ReadAllText(BackupPath("app.exe")));
        Assert.IsFalse(AppFileExists("app.exe.old"));
        Assert.IsFalse(File.Exists(BackupPath(Path.Combine("lib", "helper.dll"))), "新增文件没有旧版可备份");
        Assert.IsEmpty(Directory.GetFiles(_appDir, "*.old", SearchOption.AllDirectories));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_TarGzPackage_Works()
    {
        WriteAppFile("app", "old");
        string tarPath = Path.Combine(_applier.PrepareStagingDirectory(), "package.tar.gz");
        using (FileStream stream = File.Create(tarPath))
        using (GZipStream gzip = new(stream, CompressionMode.Compress))
        using (TarWriter tar = new(gzip))
        {
            string payload = Path.Combine(_appDir, "payload.tmp");
            File.WriteAllText(payload, "new");
            tar.WriteEntry(payload, "./app");
            File.Delete(payload);
        }

        _applier.Apply(tarPath);

        Assert.AreEqual("new", ReadAppFile("app"));
        Assert.AreEqual("old", File.ReadAllText(BackupPath("app")));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void SwapFromPayload_StagedFileMissing_RollsBackToPreviousVersion()
    {
        WriteAppFile("a.txt", "a-old");
        WriteAppFile("b.txt", "b-old");
        _applier.Stage(CreateZip(("a.txt", "a-new"), ("b.txt", "b-new")));
        // 抽掉一个解包产物,模拟换版进行到一半才发现内容缺失。
        File.Delete(Path.Combine(_applier.PayloadDirectory, "b.txt"));

        Assert.ThrowsExactly<InvalidDataException>(_applier.SwapFromPayload);

        // 已换入的 a 必须退回旧版,应用整体停在换版前的状态。
        Assert.AreEqual("a-old", ReadAppFile("a.txt"));
        Assert.AreEqual("b-old", ReadAppFile("b.txt"));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void SwapFromPayload_WithoutStage_Throws() => Assert.ThrowsExactly<InvalidOperationException>(_applier.SwapFromPayload);

    [TestMethod]
    [TestCategory("Update")]
    public void SwapFromPayload_LeavesPayloadIntact()
    {
        // 外置换版进程就跑在 payload/ 里(那是一份完整的新版应用),换版期间绝不能把它
        // 脚下的文件搬走 —— 新文件必须是复制进应用目录,不是移动。
        WriteAppFile("app.exe", "old-exe");
        _applier.Stage(CreateZip(("app.exe", "new-exe"), ("lib/helper.dll", "new-dll")));

        _applier.SwapFromPayload();

        Assert.AreEqual("new-exe", ReadAppFile("app.exe"));
        Assert.AreEqual("new-exe", File.ReadAllText(Path.Combine(_applier.PayloadDirectory, "app.exe")));
        Assert.AreEqual("new-dll",
            File.ReadAllText(Path.Combine(_applier.PayloadDirectory, "lib", "helper.dll")));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_ZipSlipEntry_ThrowsWithoutTouchingFiles()
    {
        WriteAppFile("app.exe", "old-exe");
        string zip = CreateZip(("../evil.txt", "evil"), ("app.exe", "new-exe"));

        Assert.ThrowsExactly<InvalidDataException>(() => _applier.Apply(zip));

        Assert.AreEqual("old-exe", ReadAppFile("app.exe"));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(_appDir)!, "evil.txt")));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_AbsoluteEntryPath_Throws()
    {
        string zip = CreateZip((OperatingSystem.IsWindows() ? "C:/evil.txt" : "/evil.txt", "evil"));
        Assert.ThrowsExactly<InvalidDataException>(() => _applier.Apply(zip));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_EmptyPackage_Throws()
    {
        string zip = CreateZip();
        Assert.ThrowsExactly<InvalidDataException>(() => _applier.Apply(zip));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_PackageEntryInsideStagingDirectory_IsIgnored()
    {
        WriteAppFile("app.exe", "old-exe");
        string zip = CreateZip(
            ("app.exe", "new-exe"),
            ($"{UpdateApplier.StagingDirectoryName}/{UpdateApplier.JournalFileName}", "malicious journal"));

        _applier.Apply(zip);

        Assert.AreEqual("new-exe", ReadAppFile("app.exe"));
    }

    // ———————————————————— 启动期收尾 ————————————————————

    [TestMethod]
    [TestCategory("Update")]
    public void Apply_ThenFinalize_ClearsBackupAndPayload()
    {
        WriteAppFile("app.exe", "old-exe");
        string zip = CreateZip(("app.exe", "new-exe"));
        _applier.Apply(zip);

        Assert.IsTrue(_applier.TryFinalizeStartup());

        Assert.AreEqual("new-exe", ReadAppFile("app.exe"));
        Assert.IsFalse(Directory.Exists(_applier.BackupDirectory));
        Assert.IsFalse(Directory.Exists(_applier.PayloadDirectory));
        Assert.IsFalse(File.Exists(Path.Combine(_applier.StagingDirectory, UpdateApplier.JournalFileName)));
        // 已下载的包留着:下次检查更新时校验通过即免下载复用(分段续传同理)。
        Assert.IsTrue(File.Exists(zip));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryFinalizeStartup_CrashDuringSwap_RollsBack()
    {
        // 模拟换版中途崩溃的现场:a 已换入新版(备份在),b(新增文件)已换入,c 尚未换。
        WriteAppFile("a.txt", "a-new");
        WriteAppFile("b.txt", "b-new");
        WriteAppFile("c.txt", "c-old");
        Directory.CreateDirectory(_applier.BackupDirectory);
        File.WriteAllText(BackupPath("a.txt"), "a-old");
        Directory.CreateDirectory(_applier.PayloadDirectory);
        File.WriteAllText(Path.Combine(_applier.PayloadDirectory, "c.txt"), "c-new");
        WriteJournal(UpdateJournal.PhaseApplying, ("a.txt", true), ("b.txt", false), ("c.txt", true));

        Assert.IsTrue(_applier.TryFinalizeStartup());

        Assert.AreEqual("a-old", ReadAppFile("a.txt"));
        Assert.IsFalse(AppFileExists("b.txt"), "新增文件应被回滚删除");
        Assert.AreEqual("c-old", ReadAppFile("c.txt"));
        Assert.IsFalse(Directory.Exists(_applier.StagingDirectory));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryFinalizeStartup_CrashBeforeSwap_DiscardsPayload()
    {
        WriteAppFile("a.txt", "a-old");
        Directory.CreateDirectory(_applier.PayloadDirectory);
        File.WriteAllText(Path.Combine(_applier.PayloadDirectory, "a.txt"), "a-new");
        WriteJournal(UpdateJournal.PhaseStaged, ("a.txt", true));

        Assert.IsTrue(_applier.TryFinalizeStartup());

        Assert.AreEqual("a-old", ReadAppFile("a.txt"));
        Assert.IsFalse(Directory.Exists(_applier.StagingDirectory));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryFinalizeStartup_HandoffNeverRan_KeepsCurrentVersion()
    {
        // 外置更新器被杀/没起来:应用目录从未被触碰,收尾只需丢掉解包内容。
        WriteAppFile("a.txt", "a-old");
        Directory.CreateDirectory(_applier.PayloadDirectory);
        File.WriteAllText(Path.Combine(_applier.PayloadDirectory, "a.txt"), "a-new");
        WriteJournal(UpdateJournal.PhaseHandoff, ("a.txt", true));

        Assert.IsTrue(_applier.TryFinalizeStartup());

        Assert.AreEqual("a-old", ReadAppFile("a.txt"));
        Assert.IsFalse(Directory.Exists(_applier.StagingDirectory));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryFinalizeStartup_PurgesLegacyOldFiles_ButKeepsUnrelatedOnes()
    {
        // 老换版机制遗留的 .old:去掉后缀后同名文件存在的才是残骸,才删。
        WriteAppFile("app.exe", "current");
        WriteAppFile("app.exe.old", "ancient");
        WriteAppFile("lib/helper.dll", "current");
        WriteAppFile("lib/helper.dll.old", "ancient");
        WriteAppFile("backup-of-something.old", "user's own file");

        Assert.IsTrue(_applier.TryFinalizeStartup());

        Assert.IsFalse(AppFileExists("app.exe.old"));
        Assert.IsFalse(AppFileExists(Path.Combine("lib", "helper.dll.old")));
        Assert.AreEqual("current", ReadAppFile("app.exe"));
        // 没有对应正片的 .old 是用户自己的文件,不许碰。
        Assert.AreEqual("user's own file", ReadAppFile("backup-of-something.old"));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryFinalizeStartup_RollbackIncomplete_KeepsBackupAndJournal()
    {
        // 回滚失败(目标位置被占着还不掉)时,backup 是还原旧版的唯一副本,日志是"还有待办"
        // 的唯一凭据 —— 谁都不能删,否则用户的旧版本就此蒸发,下次启动也不知道该做什么。
        Directory.CreateDirectory(_applier.BackupDirectory);
        File.WriteAllText(BackupPath("a.txt"), "a-old");
        Directory.CreateDirectory(Path.Combine(_appDir, "a.txt")); // 目标位置被一个同名目录占住
        WriteJournal(UpdateJournal.PhaseApplying, ("a.txt", true));

        Assert.IsFalse(_applier.TryFinalizeStartup());

        Assert.AreEqual("a-old", File.ReadAllText(BackupPath("a.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(_applier.StagingDirectory, UpdateApplier.JournalFileName)));

        // 障碍排除后,下一次启动收尾必须能把旧版本还原回去。
        Directory.Delete(Path.Combine(_appDir, "a.txt"));
        Assert.IsTrue(_applier.TryFinalizeStartup());
        Assert.AreEqual("a-old", ReadAppFile("a.txt"));
        Assert.IsFalse(Directory.Exists(_applier.StagingDirectory));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryRepair_RollbackIncomplete_DoesNotDestroyBackup()
    {
        Directory.CreateDirectory(_applier.BackupDirectory);
        File.WriteAllText(BackupPath("a.txt"), "a-old");
        Directory.CreateDirectory(Path.Combine(_appDir, "a.txt"));
        WriteJournal(UpdateJournal.PhaseApplying, ("a.txt", true));

        Assert.IsFalse(_applier.TryRepair());

        // 修复是为了恢复可用状态,不是把用户仅剩的退路一并清空。
        Assert.AreEqual("a-old", File.ReadAllText(BackupPath("a.txt")));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryFinalizeStartup_NoJournal_KeepsDownloadsForResume()
    {
        // 无换版日志时暂存目录里只有下载产物,保留给断点续传/免下载复用,
        // 过期残留由下次下载按文件名清理。
        _applier.PrepareStagingDirectory();
        string archive = Path.Combine(_applier.StagingDirectory, "package.zip");
        string partial = Path.Combine(_applier.StagingDirectory, "package.zip.partial");
        File.WriteAllText(archive, "leftover download");
        File.WriteAllText(partial, "half download");

        Assert.IsTrue(_applier.TryFinalizeStartup());

        Assert.AreEqual("leftover download", File.ReadAllText(archive));
        Assert.AreEqual("half download", File.ReadAllText(partial));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryFinalizeStartup_NothingPending_ReturnsTrue() => Assert.IsTrue(_applier.TryFinalizeStartup());

    // ———————————————————— 自愈 ————————————————————

    [TestMethod]
    [TestCategory("Update")]
    public void TryRepair_RollsBackAndClearsEverything()
    {
        WriteAppFile("a.txt", "a-new");
        WriteAppFile("app.exe", "current");
        WriteAppFile("app.exe.old", "ancient");
        Directory.CreateDirectory(_applier.BackupDirectory);
        File.WriteAllText(BackupPath("a.txt"), "a-old");
        File.WriteAllText(Path.Combine(_applier.StagingDirectory, "package.zip"), "downloaded");
        WriteJournal(UpdateJournal.PhaseApplying, ("a.txt", true));
        _applier.WriteLastError("boom");

        Assert.IsTrue(_applier.TryRepair());

        Assert.AreEqual("a-old", ReadAppFile("a.txt"), "未完成的换版必须先回滚");
        Assert.IsFalse(Directory.Exists(_applier.StagingDirectory), "连已下载的包一起清掉,下次重新下载");
        Assert.IsFalse(AppFileExists("app.exe.old"));
        Assert.IsNull(_applier.ReadLastError());
    }

    [TestMethod]
    [TestCategory("Update")]
    public void LastError_RoundTripsAndClears()
    {
        Assert.IsNull(_applier.ReadLastError());

        _applier.WriteLastError("disk full");
        Assert.AreEqual("disk full", _applier.ReadLastError());

        _applier.ClearLastError();
        Assert.IsNull(_applier.ReadLastError());
    }

    [TestMethod]
    [TestCategory("Update")]
    public void IsApplicationDirectoryWritable_TempDir_IsTrue() => Assert.IsTrue(_applier.IsApplicationDirectoryWritable());

    [TestMethod]
    [TestCategory("Update")]
    public void TryHandOffToExternalUpdater_LauncherNotExecutable_FallsBackInProcess()
    {
        // 包里那个"主程序"根本跑不起来时,交接要干净地失败并把日志退回 staged,
        // 否则调用方的原地换版兜底就无从谈起。
        WriteAppFile("a.txt", "a-old");
        string launcher = Path.GetFileName(Environment.ProcessPath)!;
        _applier.Stage(CreateZip((launcher, "not an executable"), ("a.txt", "a-new")));

        Assert.IsFalse(_applier.TryHandOffToExternalUpdater(Environment.ProcessId));

        _applier.SwapFromPayload();
        Assert.AreEqual("a-new", ReadAppFile("a.txt"));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void TryHandOffToExternalUpdater_PackageWithoutLauncher_FallsBackInProcess()
    {
        _applier.Stage(CreateZip(("some-other-file.txt", "content")));

        Assert.IsFalse(_applier.TryHandOffToExternalUpdater(Environment.ProcessId));
    }

    // ———————————————————— 启动路径(HasPendingSwap) ————————————————————

    /// <summary>
    /// 没有换版日志时不算「有待办」—— 也就是绝大多数启动。
    /// </summary>
    /// <remarks>
    /// 这是把更新收尾挪出启动路径的依据:此时留下的活只有清扫陈旧文件,而清扫要
    /// <b>递归枚举整个应用目录</b>找 <c>*.old</c>,通常一个都找不到,却结结实实压在首帧之前。
    /// </remarks>
    [TestMethod]
    [TestCategory("Update")]
    public void HasPendingSwap_IsFalse_OnAnOrdinaryStartup()
    {
        WriteAppFile("app.exe", "current");

        Assert.IsFalse(_applier.HasPendingSwap());
    }

    /// <summary>解包完但还没换版(staged / handoff),必须同步收拾。</summary>
    [TestMethod]
    [TestCategory("Update")]
    public void HasPendingSwap_IsTrue_WhenASwapWasStagedButNotApplied()
    {
        WriteJournal(UpdateJournal.PhaseStaged, ("app.exe", true));

        Assert.IsTrue(_applier.HasPendingSwap());
    }

    /// <summary>换到一半崩了:回滚是还原旧版的唯一依据,更要同步。</summary>
    [TestMethod]
    [TestCategory("Update")]
    public void HasPendingSwap_IsTrue_WhenASwapWasInterruptedMidWay()
    {
        WriteJournal(UpdateJournal.PhaseApplying, ("app.exe", true));

        Assert.IsTrue(_applier.HasPendingSwap());
    }

    /// <summary>
    /// 判定为「无待办」之后,清扫本身仍然照做 —— 只是挪到了后台。
    /// </summary>
    /// <remarks>
    /// 这一条钉住的是「挪到后台」不等于「不做了」:历史版本遗留的 <c>*.old</c> 照样得清掉,
    /// 否则从 1.1.x 升上来的用户,应用目录里会一直留着一堆旧文件。
    /// </remarks>
    [TestMethod]
    [TestCategory("Update")]
    public void TheSweepStillRemovesLegacyOldFiles_EvenWithNothingPending()
    {
        WriteAppFile("app.exe", "current");
        WriteAppFile("app.exe.old", "leftover-from-1.1.x");

        Assert.IsFalse(_applier.HasPendingSwap());
        Assert.IsTrue(_applier.TryFinalizeStartup());

        Assert.IsFalse(AppFileExists("app.exe.old"));
        Assert.AreEqual("current", ReadAppFile("app.exe"));
    }
}
