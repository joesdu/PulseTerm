using System.Text.Json;
using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>
/// 代理默认值与它的一次性迁移。
/// </summary>
/// <remarks>
/// 为什么默认必须是 <c>system</c> 而不是 <c>none</c>:<c>VelaWebProxy.Install</c> 把本设置
/// 解析出的路由装成进程级 <c>HttpClient.DefaultProxy</c>,<b>顶掉 .NET 原本的系统代理</b>。
/// 默认 none 的话,"装了 VelaShell 反而把系统代理关掉了" —— 浏览器出得去、本程序出不去,
/// 而且完全无从察觉。真机上就是这么栽的,所以给它上棘轮。
/// </remarks>
[TestClass]
[TestCategory("DataStore")]
public class ProxyDefaultsTests
{
    [TestMethod]
    public void FreshSettings_FollowTheSystemProxy() => Assert.AreEqual("system", new AppSettings().Proxy.Type);

    [TestMethod]
    public void NullType_FallsBackToSystemRatherThanForcingDirect()
    {
        var options = new ProxyOptions { Type = null! };

        Assert.AreEqual("system", options.Type);
    }

    [TestMethod]
    public void LegacySettings_AreLiftedOffTheOldNoneDefaultExactlyOnce()
    {
        // 老配置:落盘的是 none,而且没有迁移标记
        var legacy = new AppSettings();
        legacy.Proxy.Type = "none";
        legacy.Proxy.DefaultsMigrated = false;

        legacy.Normalize();

        Assert.AreEqual("system", legacy.Proxy.Type, "老用户升上来该跟随系统,而不是继续被强制直连");
        Assert.IsTrue(legacy.Proxy.DefaultsMigrated);
    }

    [TestMethod]
    public void ADeliberateNone_SurvivesEveryLaterLoad()
    {
        // 迁移跑过之后,用户主动选的 none 就一直算数 —— 否则"我就是要直连"每次启动都被改回去
        var settings = new AppSettings();
        settings.Normalize();
        settings.Proxy.Type = "none";

        settings.Normalize();
        settings.Normalize();

        Assert.AreEqual("none", settings.Proxy.Type);
    }

    [TestMethod]
    public void MigrationLeavesAnExplicitProxyAlone()
    {
        var settings = new AppSettings();
        settings.Proxy.Type = "socks5";
        settings.Proxy.Host = "127.0.0.1";
        settings.Proxy.Port = 7890;
        settings.Proxy.DefaultsMigrated = false;

        settings.Normalize();

        Assert.AreEqual("socks5", settings.Proxy.Type);
        Assert.AreEqual(7890, settings.Proxy.Port);
        Assert.IsTrue(settings.Proxy.DefaultsMigrated);
    }

    [TestMethod]
    public void MigrationFlagRoundTripsThroughJson()
    {
        // 标记要能落盘,否则每次启动都会把用户选的 none 抬回 system
        var settings = new AppSettings();
        settings.Normalize();
        settings.Proxy.Type = "none";

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        AppSettings reloaded = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, options), options)!;
        reloaded.Normalize();

        Assert.IsTrue(reloaded.Proxy.DefaultsMigrated);
        Assert.AreEqual("none", reloaded.Proxy.Type);
    }
}
