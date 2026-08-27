using VelaShell.Infrastructure.Plugins;
using VelaShell.PluginSdk.TerminalView;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 插件终端视图能力(SDK 1.3)在 headless 装配下的行为。
/// <para>
/// 真正的渲染归 <c>VelaShell.Terminal</c>,那一层有自己的测试;这里钉的是**契约**:
/// 没有界面层的宿主要明确报不可用(不能给一个静默什么都不做的假终端),
/// 以及能力对象要能一路装配到插件上下文里。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class TerminalViewCapabilityTests
{
    [TestMethod]
    public void HeadlessHost_ReportsUnavailableInsteadOfHandingOutADeadTerminal()
    {
        // 没有 UI 层就没有终端控件。这里必须抛 —— 一个"建出来了但永远不显示任何东西"的
        // 终端会让插件作者查上半天,而问题根本不在插件里。
        var options = new PluginManagerOptions
        {
            PluginRoots = [],
            DataRootDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())
        };

        Assert.IsNull(options.TerminalView);
    }

    [TestMethod]
    public void FakeTerminalView_FeedAccumulatesAndClearResets()
    {
        var view = new FakeTerminalView();

        view.Feed("hello "u8);
        view.Write("world");

        Assert.AreEqual("hello world", view.Fed);
        view.Clear();
        Assert.AreEqual("", view.Fed);
        Assert.AreEqual(1, view.ClearCount);
    }

    [TestMethod]
    public async Task AttachedView_PumpsBothDirections()
    {
        var view = new FakeTerminalView();
        var stream = new MemoryStream();
        stream.Write("motd\n"u8);
        stream.Position = 0;
        using var cts = new CancellationTokenSource();

        await view.AttachAsync(stream, cts.Token);

        // 流里的东西进了屏幕。
        Assert.AreEqual("motd\n", view.Fed);
    }

    [TestMethod]
    public void UserInput_IsObservableByThePlugin()
    {
        var view = new FakeTerminalView();
        List<byte[]> seen = [];
        view.UserInput += bytes => seen.Add(bytes);

        view.SimulateUserInput("ls -l\n");

        Assert.HasCount(1, seen);
        Assert.AreSequenceEqual("ls -l\n"u8.ToArray(), seen[0]);
    }

    [TestMethod]
    public void Resize_RaisesResizedSoThePluginCanTellTheRemote()
    {
        var view = new FakeTerminalView();
        (int Cols, int Rows) reported = default;
        view.Resized += (c, r) => reported = (c, r);

        view.Resize(120, 40);

        // 远端不知道尺寸 —— 不把这个数报过去,vim 会照旧尺寸画。
        Assert.AreEqual((120, 40), reported);
        Assert.AreEqual(120, view.Columns);
        Assert.AreEqual(40, view.Rows);
    }

    [TestMethod]
    public void UnavailableCapability_ThrowsRatherThanReturningNull()
    {
        var api = new FakeTerminalViewApi { IsAvailable = false };

        Assert.ThrowsExactly<NotSupportedException>(() => api.Create());
    }

    [TestMethod]
    public void DefaultOptions_MatchTheDocumentedDefaults()
    {
        var options = new TerminalViewOptions();

        Assert.AreEqual(2000, options.ScrollbackLines);
        Assert.IsTrue(options.FollowHostAppearance);
        // 对面是 PTY 时它自己回显,本地回显默认必须关 —— 否则每个字符两遍。
        Assert.IsFalse(options.LocalEcho);
        Assert.AreEqual("xterm-256color", options.TerminalType);
    }

    [TestMethod]
    public void CreatedViewsAreIndependent()
    {
        var api = new FakeTerminalViewApi();

        var first = (FakeTerminalView)api.Create();
        var second = (FakeTerminalView)api.Create();
        first.Write("a");

        // 每个插件面板一个终端,互不串台。
        Assert.AreEqual("a", first.Fed);
        Assert.AreEqual("", second.Fed);
        Assert.HasCount(2, api.Created);
    }
}
