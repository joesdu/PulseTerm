using System.IO.Compression;
using System.Security.Cryptography;
using VelaShell.Services;
using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

[TestClass]
public class UpdateServiceTests : IDisposable
{
    private readonly string _appDir;
    private readonly FakeUpdateSource _source = new();

    public UpdateServiceTests()
    {
        _appDir = Path.Combine(Path.GetTempPath(), $"velashell_update_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_appDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_appDir))
        {
            Directory.Delete(_appDir, true);
        }
        GC.SuppressFinalize(this);
    }

    private UpdateService CreateService(
        string currentVersion = "1.0.0",
        Func<Task<string>>? channelProvider = null,
        Action? shutdown = null) =>
        new(_source,
            channelProvider,
            applicationDirectory: _appDir,
            currentVersionOverride: currentVersion,
            shutdownForRestart: shutdown ?? (static () => { }));

    /// <summary>为当前平台构造一个指向 <paramref name="archiveBytes" /> 的清单。</summary>
    private static UpdateManifest CreateManifest(string version, byte[] archiveBytes)
    {
        string rid = UpdateManifest.CurrentRid()
            ?? throw new InvalidOperationException("Tests must run on a supported platform.");
        string sha = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
        return UpdateManifest.Parse($$"""
            {
              "version": "{{version}}",
              "tag": "v{{version}}",
              "assets": {
                "{{rid}}": { "name": "VelaShell-{{version}}-{{rid}}.zip", "sha256": "{{sha}}", "size": {{archiveBytes.Length}} }
              }
            }
            """);
    }

    private static byte[] CreateZipBytes(params (string Path, string Content)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, true))
        {
            foreach ((string path, string content) in entries)
            {
                using StreamWriter writer = new(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    [TestMethod]
    [TestCategory("Update")]
    public void CurrentVersion_WithoutOverride_ReturnsAssemblyVersion()
    {
        var service = new UpdateService(_source, applicationDirectory: _appDir);
        Assert.IsFalse(string.IsNullOrEmpty(service.CurrentVersion));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void AvailableVersion_Initially_IsNull()
    {
        Assert.IsNull(CreateService().AvailableVersion);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void CanSelfUpdate_WritableTempDir_IsTrue()
    {
        Assert.IsTrue(CreateService().CanSelfUpdate);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_NewerVersion_ReturnsTrueAndExposesVersion()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        UpdateService service = CreateService("1.0.0");

        Assert.IsTrue(await service.CheckForUpdateAsync());
        Assert.AreEqual("2.0.0", service.AvailableVersion);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("1.0.0")]
    [DataRow("0.9.0")]
    [DataRow("1.0.0-beta")]
    public async Task CheckForUpdateAsync_SameOrOlderVersion_ReturnsFalse(string remote)
    {
        _source.Manifest = CreateManifest(remote, CreateZipBytes(("a", "b")));
        UpdateService service = CreateService("1.0.0");

        Assert.IsFalse(await service.CheckForUpdateAsync());
        Assert.IsNull(service.AvailableVersion);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_NoManifestOnSource_ReturnsFalse()
    {
        _source.Manifest = null;
        Assert.IsFalse(await CreateService().CheckForUpdateAsync());
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_NoAssetForPlatform_ReturnsFalse()
    {
        _source.Manifest = UpdateManifest.Parse("""
            { "version": "9.9.9", "tag": "v9.9.9",
              "assets": { "solaris-sparc": { "name": "n", "sha256": "s", "size": 1 } } }
            """);
        Assert.IsFalse(await CreateService().CheckForUpdateAsync());
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_SourceThrows_ReturnsFalse()
    {
        _source.ThrowOnManifest = true;
        Assert.IsFalse(await CreateService().CheckForUpdateAsync());
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task CheckForUpdateAsync_PreviewChannel_RequestsPreReleases()
    {
        _source.Manifest = null;
        await CreateService(channelProvider: static () => Task.FromResult("preview")).CheckForUpdateAsync();
        Assert.IsTrue(_source.LastIncludePreRelease);

        await CreateService(channelProvider: static () => Task.FromResult("stable")).CheckForUpdateAsync();
        Assert.IsFalse(_source.LastIncludePreRelease);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_WithoutCheck_CompletesWithoutError()
    {
        await CreateService().DownloadUpdateAsync();
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_ValidChecksum_Succeeds()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());

        List<int> reported = [];
        await service.DownloadUpdateAsync(new SynchronousProgress(reported.Add));

        string archive = Path.Combine(_appDir, UpdateApplier.StagingDirectoryName, "VelaShell-2.0.0-" + UpdateManifest.CurrentRid() + ".zip");
        Assert.IsTrue(File.Exists(archive));
        Assert.Contains(100, reported);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_ChecksumMismatch_ThrowsAndDeletesArchive()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = [1, 2, 3]; // 与清单声明的哈希不符
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.DownloadUpdateAsync());

        string staging = Path.Combine(_appDir, UpdateApplier.StagingDirectoryName);
        Assert.IsEmpty(Directory.GetFiles(staging), "校验失败的下载必须删除");
        // 校验失败后不允许进入换版。
        Assert.ThrowsExactly<InvalidOperationException>(() => service.ApplyUpdateAndRestart());
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_ExistingValidArchive_ReusedWithoutRedownload()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        await service.DownloadUpdateAsync();
        Assert.AreEqual(1, _source.DownloadCalls);

        // 再次下载(例如重复点“检查更新”):完整包校验通过,直接复用不再请求网络。
        List<int> reported = [];
        await service.DownloadUpdateAsync(new SynchronousProgress(reported.Add));

        Assert.AreEqual(1, _source.DownloadCalls);
        Assert.Contains(100, reported);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_StaleLeftovers_AreCleaned()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        string staging = Path.Combine(_appDir, UpdateApplier.StagingDirectoryName);
        Directory.CreateDirectory(staging);
        // 旧版本的包与半成品(文件名不同)必须被清掉,不参与本次续传。
        File.WriteAllText(Path.Combine(staging, "VelaShell-1.5.0-old.zip"), "stale");
        File.WriteAllText(Path.Combine(staging, "VelaShell-1.5.0-old.zip.partial"), "stale");

        await service.DownloadUpdateAsync();

        string[] files = Directory.GetFiles(staging);
        Assert.HasCount(1, files);
        Assert.AreEqual("VelaShell-2.0.0-" + UpdateManifest.CurrentRid() + ".zip", Path.GetFileName(files[0]));
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task DownloadUpdateAsync_PendingSwapBookkeeping_IsLeftAlone()
    {
        // 老实现会把暂存目录里除下载产物外的一切删光,包括换版日志 —— 上一轮没收尾的备份
        // 就此失去归属,再没有代码知道它们的存在,残留永久堆在应用目录里。
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        UpdateApplier applier = new(_appDir);
        Directory.CreateDirectory(applier.BackupDirectory);
        File.WriteAllText(Path.Combine(applier.BackupDirectory, "app.txt"), "previous version");
        string journal = Path.Combine(applier.StagingDirectory, UpdateApplier.JournalFileName);
        File.WriteAllText(journal, """{ "Phase": "applying", "Files": [ { "Path": "app.txt", "Existed": true } ] }""");

        await service.DownloadUpdateAsync();

        Assert.IsTrue(File.Exists(journal), "换版日志归 UpdateApplier 管,下载流程不得删除");
        Assert.AreEqual("previous version", File.ReadAllText(Path.Combine(applier.BackupDirectory, "app.txt")));
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task RepairUpdateState_ClearsStagingAndForcesFreshDownload()
    {
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService("1.0.0");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        await service.DownloadUpdateAsync();
        Assert.AreEqual(1, _source.DownloadCalls);

        Assert.IsTrue(service.RepairUpdateState());

        Assert.IsFalse(Directory.Exists(Path.Combine(_appDir, UpdateApplier.StagingDirectoryName)));
        // 修复会连检查结果一起丢掉,必须重新走完整流程(而不是拿着失效状态继续换版)。
        Assert.ThrowsExactly<InvalidOperationException>(service.ApplyUpdateAndRestart);
        Assert.IsTrue(await service.CheckForUpdateAsync());
        await service.DownloadUpdateAsync();
        Assert.AreEqual(2, _source.DownloadCalls);
    }

    [TestMethod]
    [TestCategory("Update")]
    public async Task ApplyUpdateAndRestart_StagesWithoutTouchingApplicationDirectory_OnFailure()
    {
        // 包在下载后被损坏(磁盘错误/被改写):解包阶段就会失败,而解包不动应用目录,
        // 因此应用完好如初,重试也不需要任何人工清理。
        byte[] zip = CreateZipBytes(("app.txt", "new"));
        _source.Manifest = CreateManifest("2.0.0", zip);
        _source.AssetBytes = zip;
        UpdateService service = CreateService("1.0.0");
        File.WriteAllText(Path.Combine(_appDir, "app.txt"), "current");
        Assert.IsTrue(await service.CheckForUpdateAsync());
        await service.DownloadUpdateAsync();
        string archive = Path.Combine(
            _appDir, UpdateApplier.StagingDirectoryName, "VelaShell-2.0.0-" + UpdateManifest.CurrentRid() + ".zip");
        File.WriteAllText(archive, "corrupted after checksum passed");

        Assert.ThrowsExactly<InvalidDataException>(service.ApplyUpdateAndRestart);

        Assert.AreEqual("current", File.ReadAllText(Path.Combine(_appDir, "app.txt")));
        Assert.IsEmpty(Directory.GetFiles(_appDir, "*.old", SearchOption.AllDirectories));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void ApplyUpdateAndRestart_WithoutDownload_ThrowsInvalidOperation()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => CreateService().ApplyUpdateAndRestart());
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Constructor_WithNullSource_ThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => _ = new UpdateService((IUpdateSource)null!));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Constructor_WithInvalidRepositoryUrl_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _ = new UpdateService("https://github.com/only-owner"));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void ImplementsIUpdateService()
    {
        Assert.IsInstanceOfType<IUpdateService>(CreateService());
    }

    /// <summary>同步回调的进度器(Progress&lt;T&gt; 经同步上下文投递,测试里改用直呼)。</summary>
    private sealed class SynchronousProgress(Action<int> handler) : IProgress<int>
    {
        public void Report(int value) => handler(value);
    }

    private sealed class FakeUpdateSource : IUpdateSource
    {
        public UpdateManifest? Manifest { get; set; }
        public byte[]? AssetBytes { get; set; }
        public bool ThrowOnManifest { get; set; }
        public bool LastIncludePreRelease { get; private set; }
        public int DownloadCalls { get; private set; }

        public Task<UpdateManifest?> GetLatestManifestAsync(bool includePreRelease, CancellationToken cancellationToken = default)
        {
            LastIncludePreRelease = includePreRelease;
            return ThrowOnManifest
                ? Task.FromException<UpdateManifest?>(new HttpRequestException("offline"))
                : Task.FromResult(Manifest);
        }

        public async Task<string?> DownloadAssetAsync(
            UpdateManifest manifest,
            UpdateAsset asset,
            string destinationPath,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            await File.WriteAllBytesAsync(destinationPath, AssetBytes ?? [], cancellationToken);
            progress?.Report(100);
            // 不返回流式哈希,覆盖 UpdateService 读文件校验的兜底路径。
            return null;
        }
    }
}
