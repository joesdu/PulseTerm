using NSubstitute;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 注入目录上报钩子(OSC 7)之前的 POSIX shell 探测。
/// </summary>
/// <remarks>
/// 用户报的现象:连 Windows 的 OpenSSH,一登录就在 cmd.exe 里看到
/// <c>'test' 不是内部或外部命令</c> —— 那串给 bash 准备的钩子被 cmd 当命令执行了(#305)。
/// 这里的断言全部围绕"什么样的回答才算 POSIX",样例取自各 shell 对探针命令的真实反应。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public class RemoteShellProbeTests
{
    [TestInitialize]
    public void ResetCache() => RemoteShellProbe.ClearCache();

    /// <summary>
    /// 探针必须三样一起考:printf、<c>$((...))</c> 算术展开、<c>${var:-默认值}</c> 默认值展开。
    /// 少一样就会被 cmd.exe(有 printf.exe 时)或 PowerShell(它自己会算 <c>$(...)</c>)蒙混过去。
    /// </summary>
    [TestMethod]
    public void ProbeCommand_ExercisesPrintfAndBothExpansions()
    {
        Assert.Contains("printf", RemoteShellProbe.ProbeCommand);
        Assert.Contains("$((6*7))", RemoteShellProbe.ProbeCommand);
        Assert.Contains("${vela_probe_ok:-ok}", RemoteShellProbe.ProbeCommand);
    }

    /// <summary>bash/zsh/dash/sh:两种展开都做对,退出码 0。</summary>
    [TestMethod]
    public void IsPosixShell_WithExpandedMarker_IsTrue() =>
        Assert.IsTrue(RemoteShellProbe.IsPosixShell(new("vela-posix-42ok\n", "", 0)));

    /// <summary>cmd.exe:没有 printf 这个命令。</summary>
    [TestMethod]
    public void IsPosixShell_WhenCommandNotFound_IsFalse() =>
        Assert.IsFalse(RemoteShellProbe.IsPosixShell(
            new("", "'printf' 不是内部或外部命令,也不是可运行的程序\n", 1)));

    /// <summary>
    /// cmd.exe 而 PATH 上恰好有 MSYS/Git 的 printf.exe:命令跑通了(退出码 0),
    /// 但 cmd 两种展开都不做,打出来的是字面量 —— 只看退出码就会误判成 POSIX。
    /// </summary>
    [TestMethod]
    public void IsPosixShell_WhenNothingExpanded_IsFalse() =>
        Assert.IsFalse(RemoteShellProbe.IsPosixShell(
            new("vela-posix-$((6*7))${vela_probe_ok:-ok}\n", "", 0)));

    /// <summary>
    /// PowerShell 作默认 shell 且 PATH 上有 printf.exe:它自己会把 <c>$((6*7))</c> 算成 42
    /// (实测 <c>"vela-$((6*7))-${vela_probe:-ok}"</c> → <c>vela-42--</c>),但认不得
    /// <c>${var:-默认值}</c>,展开成空。只考算术就会在这里翻车 —— 后半截 ok 就是为它准备的。
    /// </summary>
    [TestMethod]
    public void IsPosixShell_WhenOnlyArithmeticExpanded_IsFalse() =>
        Assert.IsFalse(RemoteShellProbe.IsPosixShell(new("vela-posix-42\n", "", 0)));

    /// <summary>ForceCommand 之类:退出码 0,但回来的是别的东西。</summary>
    [TestMethod]
    public void IsPosixShell_WhenOutputIsUnrelated_IsFalse() =>
        Assert.IsFalse(RemoteShellProbe.IsPosixShell(new("Welcome to the gateway\n", "", 0)));

    /// <summary>底层根本没给结果(通道开不出来的替身默认值)。</summary>
    [TestMethod]
    public void IsPosixShell_WithNullResult_IsFalse() => Assert.IsFalse(RemoteShellProbe.IsPosixShell(null));

    /// <summary>同一台主机只探一次:重连、开新标签都吃缓存,不该反复占对端的 exec 通道。</summary>
    [TestMethod]
    public async Task IsPosixShellAsync_CachesResultPerHost()
    {
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(RemoteShellProbe.ProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("vela-posix-42ok\n", "", 0)));
        string key = RemoteShellProbe.CacheKey("cache.example", 22, "root");

        Assert.IsTrue(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));
        Assert.IsTrue(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));

        await client.Received(1).RunCommandDetailedAsync(
            RemoteShellProbe.ProbeCommand, Arg.Any<CancellationToken>());
    }

    /// <summary>非 POSIX 的结论同样进缓存:Windows 主机不该每次连接都再问一遍。</summary>
    [TestMethod]
    public async Task IsPosixShellAsync_CachesNegativeResult()
    {
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(RemoteShellProbe.ProbeCommand, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new RemoteCommandResult("", "'printf' 不是内部或外部命令", 1)));
        string key = RemoteShellProbe.CacheKey("windows.example", 22, "slime");

        Assert.IsFalse(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));
        Assert.IsFalse(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));

        await client.Received(1).RunCommandDetailedAsync(
            RemoteShellProbe.ProbeCommand, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 探测失败(exec 被禁、通道异常)返回 false,但**不进缓存**:那是环境噪声不是结论,
    /// 下次连接还要再问 —— 否则一次网络抖动就永久关掉了这台主机的目录跟随。
    /// </summary>
    [TestMethod]
    public async Task IsPosixShellAsync_WhenProbeThrows_IsFalseAndNotCached()
    {
        ISshClientWrapper client = Substitute.For<ISshClientWrapper>();
        client.RunCommandDetailedAsync(RemoteShellProbe.ProbeCommand, Arg.Any<CancellationToken>())
            .Returns<Task<RemoteCommandResult>>(_ => throw new InvalidOperationException("exec disabled"));
        string key = RemoteShellProbe.CacheKey("flaky.example", 22, "root");

        Assert.IsFalse(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));
        Assert.IsFalse(await RemoteShellProbe.IsPosixShellAsync(client, key, TestContext.CancellationToken));

        await client.Received(2).RunCommandDetailedAsync(
            RemoteShellProbe.ProbeCommand, Arg.Any<CancellationToken>());
    }

    /// <summary>缓存键要认用户:同一台机器上换个用户就可能换了默认 shell。</summary>
    [TestMethod]
    public void CacheKey_DistinguishesUserHostAndPort()
    {
        Assert.AreNotEqual(
            RemoteShellProbe.CacheKey("host", 22, "root"),
            RemoteShellProbe.CacheKey("host", 22, "slime"));
        Assert.AreNotEqual(
            RemoteShellProbe.CacheKey("host", 22, "root"),
            RemoteShellProbe.CacheKey("host", 2222, "root"));
    }

    /// <summary>MSTest 注入的测试上下文(取消令牌)。</summary>
    public TestContext TestContext { get; set; } = null!;
}
