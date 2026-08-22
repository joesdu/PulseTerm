using VelaShell.Infrastructure.Startup;

namespace VelaShell.Infrastructure.Tests.Startup;

/// <summary>
/// 插件开发用的启动参数解析。这些参数由 <c>vela-plugin dev init</c> 写进 IDE 启动配置,
/// 所以"值里带空格的路径"与"两种写法(空格 / 等号)"必须都稳。
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public class VelaShellStartupArgumentsTests
{
    [TestMethod]
    public void Parse_Empty_IsAllDefaults()
    {
        var args = VelaShellStartupArguments.Parse([]);
        Assert.IsEmpty(args.DevPluginRoots);
        Assert.IsEmpty(args.DebugPluginIds);
        Assert.IsNull(args.DataRoot);
        Assert.IsFalse(args.DevWatch);
    }

    [TestMethod]
    public void Parse_SpaceAndEqualsForms_BothWork()
    {
        var args = VelaShellStartupArguments.Parse(
            ["--dev-root", @"C:\work\a\bin\Debug", "--data-root=C:\\Users\\joe\\.velashell-dev"]);

        Assert.AreEqual(@"C:\work\a\bin\Debug", args.DevPluginRoots.Single());
        Assert.AreEqual(@"C:\Users\joe\.velashell-dev", args.DataRoot);
    }

    [TestMethod]
    public void Parse_RepeatedDevRoot_Accumulates()
    {
        var args = VelaShellStartupArguments.Parse(
            ["--dev-root", "/a", "--dev-root", "/b"]);
        Assert.HasCount(2, args.DevPluginRoots);
    }

    [TestMethod]
    public void Parse_WaitDebuggerWithoutValue_MeansEveryPlugin()
    {
        // 只挂了一个开发插件时这是最常用的写法,逼人重复一遍 id 纯属添堵。
        Assert.AreEqual("*", VelaShellStartupArguments.Parse(["--wait-debugger"]).DebugPluginIds.Single());
        Assert.AreEqual("*", VelaShellStartupArguments.Parse(["--wait-debugger", "--dev-watch"]).DebugPluginIds.Single());
    }

    [TestMethod]
    public void Parse_WaitDebuggerWithIds_SplitsOnCommaAndSemicolon()
    {
        var args = VelaShellStartupArguments.Parse(["--wait-debugger", "acme.one,acme.two;acme.three"]);
        Assert.HasCount(3, args.DebugPluginIds);
        Assert.Contains("acme.two", args.DebugPluginIds);
    }

    [TestMethod]
    public void Parse_UnknownArguments_AreIgnored()
    {
        // argv 是与 Avalonia 共用的,认不出的参数必须原样放过。
        var args = VelaShellStartupArguments.Parse(
            ["--after-update", "--some-avalonia-flag", "value", "--dev-watch"]);
        Assert.IsTrue(args.DevWatch);
        Assert.IsEmpty(args.DevPluginRoots);
    }

    [TestMethod]
    public void Parse_FlagWithMissingValue_DoesNotSwallowTheNextFlag()
    {
        var args = VelaShellStartupArguments.Parse(["--dev-root", "--data-root", "/tmp/x"]);
        Assert.IsEmpty(args.DevPluginRoots);
        Assert.AreEqual("/tmp/x", args.DataRoot);
    }
}
