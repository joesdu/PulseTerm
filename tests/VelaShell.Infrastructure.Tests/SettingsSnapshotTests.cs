using NSubstitute;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 只读设置快照(<see cref="ISettingsService.CurrentSnapshot" /> +
/// <see cref="SettingsServiceExtensions.GetSnapshotAsync" />)的行为契约。
/// </summary>
[TestClass]
public sealed class SettingsSnapshotTests : IDisposable
{
    private readonly SonnetDbEngine _engine;
    private readonly string _testDirectory;

    public SettingsSnapshotTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_snapshot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _engine = new(Path.Combine(_testDirectory, "sonnetdb"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    public async Task CurrentSnapshot_IsNullBeforeFirstLoad_ThenTracksTheLoadedSettings()
    {
        var service = new SonnetDbSettingsService(_engine);
        Assert.IsNull(service.CurrentSnapshot, "还没读过任何设置时不应该凭空有快照");

        AppSettings loaded = await service.GetSettingsAsync();
        Assert.IsNotNull(service.CurrentSnapshot);
        Assert.AreEqual(loaded.TerminalFontSize, service.CurrentSnapshot!.TerminalFontSize);
    }

    [TestMethod]
    public async Task CurrentSnapshot_IsReplacedOnSave_WithANewInstance()
    {
        var service = new SonnetDbSettingsService(_engine);
        AppSettings settings = await service.GetSettingsAsync();
        AppSettings? before = service.CurrentSnapshot;

        settings.TerminalFontSize = 19;
        await service.SaveSettingsAsync(settings);

        Assert.IsNotNull(service.CurrentSnapshot);
        Assert.AreEqual(19, service.CurrentSnapshot!.TerminalFontSize);
        Assert.IsFalse(ReferenceEquals(before, service.CurrentSnapshot), "保存后应整体换成新快照");
    }

    [TestMethod]
    public async Task SavedInstance_IsNotAliasedByTheSnapshot()
    {
        // 快照从 JSON 重新反序列化,而不是直接引用调用方传进来的对象:
        // 否则调用方保存后继续改自己那份,会把所有只读调用方看到的共享快照一起改掉。
        var service = new SonnetDbSettingsService(_engine);
        AppSettings settings = await service.GetSettingsAsync();
        settings.TerminalFontSize = 14;
        await service.SaveSettingsAsync(settings);

        settings.TerminalFontSize = 99;

        Assert.AreEqual(14, service.CurrentSnapshot!.TerminalFontSize);
    }

    [TestMethod]
    public async Task MutatingTheResultOfGetSettingsAsync_DoesNotTouchTheSnapshot()
    {
        var service = new SonnetDbSettingsService(_engine);
        await service.GetSettingsAsync();
        int original = service.CurrentSnapshot!.TerminalFontSize;

        AppSettings mine = await service.GetSettingsAsync();
        mine.TerminalFontSize = original + 7;

        Assert.AreEqual(original, service.CurrentSnapshot!.TerminalFontSize,
            "GetSettingsAsync 的语义仍然是每次一份独立实例");
    }

    [TestMethod]
    public async Task GetSnapshotAsync_LoadsOnce_ThenStopsHittingTheStore()
    {
        var service = new SonnetDbSettingsService(_engine);

        AppSettings first = await service.GetSnapshotAsync();
        AppSettings second = await service.GetSnapshotAsync();
        AppSettings third = await service.GetSnapshotAsync();

        Assert.IsTrue(ReferenceEquals(first, second));
        Assert.IsTrue(ReferenceEquals(second, third), "后续调用应当直接复用同一份共享快照");
    }

    /// <summary>
    /// 钉住 <see cref="SettingsServiceExtensions.GetSnapshotAsync" /> 必须是**扩展方法**。
    /// </summary>
    /// <remarks>
    /// 曾经的方案是把它写成接口的默认实现。实测(NSubstitute 6.2.0 / Castle DynamicProxy):
    /// 替身会为默认接口成员生成实现并路由给拦截器,于是返回 <c>default</c>,
    /// **不会**回落到被 stub 的 <c>GetSettingsAsync()</c> —— 全仓 23 处
    /// <c>Substitute.For&lt;ISettingsService&gt;()</c> 会拿到 null 设置然后 NRE。
    /// 扩展方法是静态解析的,代理拦不到。谁把它挪回接口默认实现,这条立刻红。
    /// </remarks>
    [TestMethod]
    public async Task GetSnapshotAsync_OnASubstitute_FallsBackToGetSettingsAsync()
    {
        ISettingsService substitute = Substitute.For<ISettingsService>();
        AppSettings stubbed = new() { TerminalFontSize = 21 };
        substitute.GetSettingsAsync().Returns(Task.FromResult(stubbed));

        AppSettings resolved = await substitute.GetSnapshotAsync();

        Assert.IsNotNull(resolved, "替身上的快照读取不能返回 null —— 说明 GetSnapshotAsync 被代理拦截了");
        Assert.AreEqual(21, resolved.TerminalFontSize);
        Assert.IsTrue(ReferenceEquals(stubbed, resolved));
    }

    [TestMethod]
    public void GetSnapshotBlocking_OnANullService_ReturnsDefaults()
    {
        AppSettings settings = ((ISettingsService?)null).GetSnapshotBlocking();

        Assert.IsNotNull(settings);
        Assert.AreEqual(new AppSettings().TerminalFontSize, settings.TerminalFontSize);
    }

    [TestMethod]
    public async Task GetSnapshotBlocking_ReusesTheSnapshotOnceLoaded()
    {
        var service = new SonnetDbSettingsService(_engine);
        await service.GetSettingsAsync();

        AppSettings blocking = service.GetSnapshotBlocking();

        Assert.IsTrue(ReferenceEquals(service.CurrentSnapshot, blocking));
    }
}
