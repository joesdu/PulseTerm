using System.IO.Compression;
using System.Security.Cryptography;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk.Packaging;
using VelaShell.PluginSdk.Testing;
using VelaShell.TestPlugin;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>卸载与 .vpx 安装:仅用户目录可卸载,zip-slip 防护,覆盖安装,清数据。</summary>
[TestClass]
[TestCategory("Plugins")]
public class PluginInstallUninstallTests
{
    private string _appRoot = null!;
    private string _userRoot = null!;
    private string _dataRoot = null!;
    private RecordingDataStore _dataStore = null!;

    private sealed class RecordingDataStore : IPluginDataStore
    {
        public List<string> Purged { get; } = [];
        public PluginSdk.Storage.IPluginStorage CreateStorage(string pluginId) => new InMemoryStorage();
        public PluginSdk.Secrets.ISecretsApi CreateSecrets(string pluginId) => new FakeSecrets();
        public PluginSdk.TimeSeries.ITimeSeriesApi CreateTimeSeries(string pluginId) => new InMemoryTimeSeries();
        public Task<IReadOnlyList<string>> ListPluginIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task PurgeAsync(string pluginId, CancellationToken cancellationToken = default)
        {
            Purged.Add(pluginId);
            return Task.CompletedTask;
        }
    }

    [TestInitialize]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _appRoot = Path.Combine(baseDir, "app-plugins");
        _userRoot = Path.Combine(baseDir, "user-plugins");
        _dataRoot = Path.Combine(baseDir, "plugin-data");
        Directory.CreateDirectory(_appRoot);
        Directory.CreateDirectory(_userRoot);
        _dataStore = new();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_appRoot)!, recursive: true);
        }
        catch
        {
            // ALC 卸载异步,dll 偶尔还锁着:留给临时目录。
        }
    }

    private PluginManager CreateManager(PluginTrustRepository? trustRepository = null,
        Infrastructure.Plugins.Protocols.PluginProtocolRegistry? protocolRegistry = null,
        Core.Services.IBackgroundActivityService? activity = null) => new(new()
        {
            PluginRoots = [_appRoot, _userRoot],
            DataRootDirectory = _dataRoot,
            UserPluginRoot = _userRoot,
            TrustRepository = trustRepository,
            HostVersion = "1.0.0",
            CommandsFactory = (_, _) => new RecordingCommands(),
            DataStore = _dataStore,
            ProtocolRegistry = protocolRegistry,
            Activity = activity
        });

    /// <summary>把夹具插件摊成一个待打包目录(plugin.json + dll)。</summary>
    private static string StagePlugin(string id, string manifestExtras = "")
    {
        string stage = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        File.Copy(typeof(TestFixturePlugin).Assembly.Location, Path.Combine(stage, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(stage, "plugin.json"), $$"""
            { "id": "{{id}}", "version": "1.0.0", "displayName": "Packaged", "author": "Test Author",
              "entry": "VelaShell.TestPlugin.dll"{{manifestExtras}} }
            """);
        return stage;
    }

    /// <summary>打一个真正的 .vpx(专属容器:魔数 + 摘要 + 掩码)。</summary>
    private static string BuildVpx(string id = "acme.packaged", string manifestExtras = "")
    {
        string stage = StagePlugin(id, manifestExtras);
        string vpx = stage + ".vpx";
        VpxContainer.Pack(stage, vpx);
        return vpx;
    }

    /// <summary>打一个"把 zip 改成 .vpx 后缀"的假包。</summary>
    private static string BuildRenamedZip(string id = "acme.renamed")
    {
        string stage = StagePlugin(id);
        string vpx = stage + ".vpx";
        ZipFile.CreateFromDirectory(stage, vpx);
        return vpx;
    }

    [TestMethod]
    public async Task InstallFromVpx_ExtractsActivatesAndPersistsToUserRoot()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        string vpx = BuildVpx();

        string id = await manager.InstallFromVpxAsync(vpx, allowUntrustedPackage: true);
        Assert.AreEqual("acme.packaged", id);
        PluginDescriptor descriptor = manager.Plugins.Single(p => p.Id == id);
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        Assert.IsTrue(File.Exists(Path.Combine(_userRoot, id, "plugin.json")), "应解包进用户插件目录");
        Assert.IsTrue(manager.IsUninstallable(id));

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_ReportsProgressToTheBackgroundLedger_AndClearsIt()
    {
        // 安装是纯等待的几秒(验签 → 解压 → 全目录哈希落凭据),这段时间状态栏的圆环
        // 必须转起来;更要紧的是**转完必须停**,否则一次安装会让它一直转到关程序。
        using var activity = new Core.Services.BackgroundActivityService();
        var progress = new List<double?>();
        string? title = null;
        activity.Changed += () =>
        {
            foreach (Core.Services.BackgroundActivitySnapshot snapshot in activity.Activities)
            {
                if (snapshot.Title == Strings.Get("Msg_PluginInstalling"))
                {
                    title = snapshot.Title;
                    lock (progress)
                    {
                        progress.Add(snapshot.Progress);
                    }
                }
            }
        };
        PluginManager manager = CreateManager(activity: activity);
        await manager.StartAsync();

        await manager.InstallFromVpxAsync(BuildVpx(), allowUntrustedPackage: true);

        Assert.AreEqual(Strings.Get("Msg_PluginInstalling"), title, "安装过程必须在账本上露过面。");
        Assert.IsGreaterThan(1, progress.Count, "安装应分阶段上报进度,而不是从头到尾一个 0%。");
        Assert.IsEmpty(activity.Activities, "安装结束后账本必须归零。");

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_WhenRejected_StillClearsTheBackgroundLedger()
    {
        // 失败路径同样要收干净:装坏包比装好包常见,而"装失败之后圆环永远在转"
        // 比没有圆环更糟 —— 它会让人以为后台还有什么没做完。
        using var activity = new Core.Services.BackgroundActivityService();
        PluginManager manager = CreateManager(activity: activity);
        await manager.StartAsync();

        await Assert.ThrowsExactlyAsync<VpxFormatException>(
            () => manager.InstallFromVpxAsync(BuildRenamedZip()));

        Assert.IsEmpty(activity.Activities);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Uninstall_RemovesDirectory_AndPurgesData()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        string id = await manager.InstallFromVpxAsync(BuildVpx(), allowUntrustedPackage: true);

        Assert.IsTrue(await manager.UninstallAsync(id));
        Assert.DoesNotContain(p => p.Id == id, manager.Plugins);
        Assert.IsFalse(Directory.Exists(Path.Combine(_userRoot, id)), "插件目录应被删除");
        Assert.Contains(id, _dataStore.Purged);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Uninstall_BuiltInPlugin_IsRejected()
    {
        // 应用自带插件(app-plugins 目录,只读语义)不可卸载。
        string dir = Path.Combine(_appRoot, "builtin");
        Directory.CreateDirectory(dir);
        File.Copy(typeof(TestFixturePlugin).Assembly.Location, Path.Combine(dir, "VelaShell.TestPlugin.dll"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            { "id": "velashell.test-fixture", "version": "0.1.0", "displayName": "Test Fixture",
              "entry": "VelaShell.TestPlugin.dll" }
            """);
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        Assert.IsFalse(manager.IsUninstallable("velashell.test-fixture"));
        Assert.IsFalse(await manager.UninstallAsync("velashell.test-fixture"));
        Assert.IsTrue(Directory.Exists(dir), "自带插件目录不该被删");
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_RenamedZip_IsRejected()
    {
        // "改后缀就能装"正是专属容器要消灭的东西:裸 zip 一律拒,且拒绝原因要告诉人怎么补救。
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        VpxFormatException ex = await Assert.ThrowsExactlyAsync<VpxFormatException>(
            () => manager.InstallFromVpxAsync(BuildRenamedZip()));
        Assert.Contains("vela-plugin pack", ex.Message);
        Assert.IsEmpty(manager.Plugins);

        await manager.DisposeAsync();
    }

    private static string BuildSignedVpx(ECDsa signingKey, string id = "acme.signed")
    {
        string stage = StagePlugin(id);
        string vpx = stage + ".vpx";
        VpxContainer.Pack(stage, vpx, new VpxPackOptions { SigningKey = signingKey });
        return vpx;
    }

    [TestMethod]
    public async Task InstallFromVpx_UnsignedPackage_RequiresExplicitApproval()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        string package = BuildVpx("acme.unsigned");

        InvalidOperationException ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => manager.InstallFromVpxAsync(package));

        Assert.Contains("Explicit approval", ex.Message);
        Assert.AreEqual(VpxSignatureState.Unsigned, manager.InspectPackageSignature(package));
        Assert.DoesNotContain(p => p.Id == "acme.unsigned", manager.Plugins);
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task TrustPublisher_PersistsKey_AndFuturePackagesInstallWithoutBypass()
    {
        using var engine = new SonnetDbEngine(Path.Combine(_dataRoot, "trust-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string first = BuildSignedVpx(publisher, "acme.signed-one");

        PluginManager manager = CreateManager(repository);
        await manager.StartAsync();
        PluginPackageTrustInfo before = manager.InspectPackageTrust(first);
        Assert.AreEqual(VpxSignatureState.Untrusted, before.State);
        Assert.StartsWith("SHA256:", before.PublisherFingerprint);

        string fingerprint = await manager.TrustPackagePublisherAsync(first);
        Assert.AreEqual(before.PublisherFingerprint, fingerprint);
        Assert.AreEqual(VpxSignatureState.Trusted, manager.InspectPackageSignature(first));
        await manager.InstallFromVpxAsync(first);
        await manager.DisposeAsync();

        string second = BuildSignedVpx(publisher, "acme.signed-two");
        PluginManager restarted = CreateManager(repository);
        await restarted.StartAsync();
        Assert.AreEqual(VpxSignatureState.Trusted, restarted.InspectPackageSignature(second));
        await restarted.InstallFromVpxAsync(second);
        Assert.Contains(p => p.Id == "acme.signed-two", restarted.Plugins);
        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task Restart_ModifiedInstalledPlugin_IsRejectedByProtectedReceipt()
    {
        using var engine = new SonnetDbEngine(Path.Combine(_dataRoot, "receipt-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        PluginManager manager = CreateManager(repository);
        await manager.StartAsync();
        const string id = "acme.receipt-tamper";
        await manager.InstallFromVpxAsync(BuildVpx(id), allowUntrustedPackage: true);
        await manager.DisposeAsync();

        await File.AppendAllTextAsync(Path.Combine(_userRoot, id, "plugin.json"), " ");
        PluginManager restarted = CreateManager(repository);
        await restarted.StartAsync();

        PluginDescriptor descriptor = Assert.ContainsSingle(plugin => plugin.Id == id, restarted.Plugins);
        Assert.AreEqual(PluginState.Invalid, descriptor.State);
        Assert.Contains("changed after installation", descriptor.Error);
        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task Restart_ModifiedInstalledPlugin_WithdrawsItsConnectionTabs()
    {
        // 内容校验推迟到发现之后:页签在校验有结论之前就已经挂出去了。
        // 校验判负时必须把它撤下来 —— 留着的话用户点下去只会得到一次静默的无反应。
        using var engine = new SonnetDbEngine(Path.Combine(_dataRoot, "tab-withdraw-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        const string id = "acme.tab-tamper";
        const string workspace = """, "contributes": { "workspaces": [ { "id": "acme.tab-tamper.cache", "displayName": "Cache", "defaultPort": 6379 } ] }, "activationEvents": ["onWorkspace:acme.tab-tamper.cache"]""";

        var firstRegistry = new Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
        PluginManager manager = CreateManager(repository, firstRegistry);
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildVpx(id, workspace), allowUntrustedPackage: true);
        Assert.HasCount(1, firstRegistry.Tabs, "装上之后连接页应出现这个工作台页签。");
        await manager.DisposeAsync();

        await File.AppendAllTextAsync(Path.Combine(_userRoot, id, "plugin.json"), " ");
        var registry = new Infrastructure.Plugins.Protocols.PluginProtocolRegistry();
        PluginManager restarted = CreateManager(repository, registry);
        await restarted.StartAsync();

        Assert.AreEqual(PluginState.Invalid, restarted.Plugins.Single(p => p.Id == id).State);
        Assert.IsEmpty(registry.Tabs, "被改动过的插件不该在连接页上留下页签。");

        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task Restart_DirectlyDroppedPlugin_IsAdoptedAsItsOwnBaseline()
    {
        // 命令行 vela-plugin install 与"直接把目录放进插件根"落到的都是这条路径:
        // 目录先于宿主存在,没有受保护收据。文档写明这两条路都能装,宿主必须收养它而不是拒绝。
        using var engine = new SonnetDbEngine(Path.Combine(_dataRoot, "direct-drop-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        PluginManager firstRun = CreateManager(repository);
        await firstRun.StartAsync(); // 先建一次空的信任状态。
        await firstRun.DisposeAsync();

        const string id = "acme.direct-drop";
        Directory.Move(StagePlugin(id), Path.Combine(_userRoot, id));
        PluginManager restarted = CreateManager(repository);
        await restarted.StartAsync();

        PluginDescriptor descriptor = Assert.ContainsSingle(plugin => plugin.Id == id, restarted.Plugins);
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        await restarted.DisposeAsync();

        // 收养必须真的落进了信任库:下一次启动不该再收养一遍。
        PluginTrustState state = await repository.LoadAsync();
        InstalledPluginReceipt receipt = state.Receipts[id];
        Assert.IsTrue(receipt.LegacyAdopted, "旁装目录建立的是 TOFU 基线,不是管理页那份收据。");
        Assert.IsNull(receipt.PackageSha256, "没有包可以证明它出自哪儿,不该伪造一个包摘要。");
    }

    [TestMethod]
    public async Task Restart_SideLoadedPluginChangedOnDisk_IsRebaselinedNotRejected()
    {
        // 旁装换来的代价就是没有事后防篡改(CLI 手册明写)。这里的内容变化通常是
        // `vela-plugin update` 换了版本 —— 把它判成"被人动过"会让更新完的插件全变红。
        using var engine = new SonnetDbEngine(Path.Combine(_dataRoot, "sideload-update-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        const string id = "acme.sideload-update";
        Directory.Move(StagePlugin(id), Path.Combine(_userRoot, id));

        PluginManager first = CreateManager(repository);
        await first.StartAsync();
        Assert.AreEqual(PluginState.Active, first.Plugins.Single(p => p.Id == id).State);
        await first.DisposeAsync();

        await File.AppendAllTextAsync(Path.Combine(_userRoot, id, "extra.txt"), "updated");
        PluginManager restarted = CreateManager(repository);
        await restarted.StartAsync();

        PluginDescriptor descriptor = restarted.Plugins.Single(p => p.Id == id);
        Assert.AreEqual(PluginState.Active, descriptor.State, descriptor.Error);
        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task Restart_ReceiptWithoutDirectory_IsDroppedSoTheIdCanBeInstalledAgain()
    {
        // vela-plugin uninstall 只删目录,够不着宿主进程里的信任库。留着那份孤儿收据,
        // 同一个 id 再装回来时内容必然与旧收据对不上,插件会以"文件被改过"被拒。
        using var engine = new SonnetDbEngine(Path.Combine(_dataRoot, "orphan-receipt-db"));
        var repository = new PluginTrustRepository(engine, new TestSecretProtector());
        const string id = "acme.reinstalled";
        // 懒激活:入口 dll 始终没被装载,测试才能像命令行那样把目录整个删掉。
        const string lazy = """, "activationEvents": ["onWorkspace:acme.reinstalled.ws"], "contributes": { "workspaces": [ { "id": "acme.reinstalled.ws", "displayName": "WS", "defaultPort": 6379 } ] }""";

        PluginManager manager = CreateManager(repository);
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildVpx(id, lazy), allowUntrustedPackage: true);
        await manager.DisposeAsync();

        // 宿主之外把目录删掉 —— 命令行 uninstall 就是这个形状,它够不着信任库。
        Directory.Delete(Path.Combine(_userRoot, id), recursive: true);
        PluginManager afterUninstall = CreateManager(repository);
        await afterUninstall.StartAsync();
        await afterUninstall.DisposeAsync();
        Assert.IsFalse((await repository.LoadAsync()).Receipts.ContainsKey(id), "目录没了,收据该跟着清掉。");

        // 同一个 id 再装回来(内容与上一次不同)时必须能正常装载。
        string replacement = StagePlugin(id, lazy);
        await File.WriteAllTextAsync(Path.Combine(replacement, "extra.txt"), "second install");
        Directory.Move(replacement, Path.Combine(_userRoot, id));

        PluginManager restarted = CreateManager(repository);
        await restarted.StartAsync();

        PluginDescriptor descriptor = restarted.Plugins.Single(p => p.Id == id);
        Assert.AreEqual(PluginState.Discovered, descriptor.State, descriptor.Error);
        await restarted.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_TamperedPackage_IsRejected()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        string vpx = BuildVpx("acme.tampered");

        // 动载荷里的一个字节:头部摘要立刻对不上。
        byte[] bytes = File.ReadAllBytes(vpx);
        bytes[VpxContainer.HeaderSize + 64] ^= 0xFF;
        File.WriteAllBytes(vpx, bytes);

        await Assert.ThrowsExactlyAsync<VpxFormatException>(() => manager.InstallFromVpxAsync(vpx));
        Assert.DoesNotContain(p => p.Id == "acme.tampered", manager.Plugins);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_ZipSlipInsideContainer_IsRejected()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();

        // 合法容器 + 恶意载荷:zip-slip 防护必须在容器之内继续生效。
        string vpx = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N") + ".vpx");
        Directory.CreateDirectory(Path.GetDirectoryName(vpx)!);
        using (var payload = new MemoryStream())
        {
            using (ZipArchive zip = new(payload, ZipArchiveMode.Create, leaveOpen: true))
            {
                zip.CreateEntry("plugin.json");
                using StreamWriter writer = new(zip.CreateEntry("../escaped.txt").Open());
                writer.Write("pwned");
            }
            payload.Position = 0;
            using FileStream destination = File.Create(vpx);
            VpxContainer.Write(destination, payload);
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => manager.InstallFromVpxAsync(vpx, allowUntrustedPackage: true));
        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task InstallFromVpx_SameId_ReplacesOldVersion()
    {
        PluginManager manager = CreateManager();
        await manager.StartAsync();
        await manager.InstallFromVpxAsync(BuildVpx("acme.dup"), allowUntrustedPackage: true);
        // 覆盖安装同 id:不抛,替换。
        string id = await manager.InstallFromVpxAsync(BuildVpx("acme.dup"), allowUntrustedPackage: true);
        Assert.ContainsSingle(p => p.Id == "acme.dup", manager.Plugins);
        Assert.AreEqual(PluginState.Active, manager.Plugins.Single(p => p.Id == id).State);
        await manager.DisposeAsync();
    }
}
