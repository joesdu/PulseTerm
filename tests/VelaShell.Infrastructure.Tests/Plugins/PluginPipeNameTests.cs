using VelaShell.Infrastructure.Plugins.Isolated;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 隔离插件的管道名长度。
/// </summary>
/// <remarks>
/// Unix 上 .NET 把命名管道实现成 <c>$TMPDIR/CoreFxPipe_&lt;名字&gt;</c> 的 Unix 域套接字,
/// 而 <b>macOS 的 sun_path 上限只有 104 字节</b> —— 它的每用户临时目录光自己就占 48 字符。
/// 名字长一点点,macOS 上隔离插件就<b>一个都起不来</b>,而且报的是
/// "invalid length for use with domain sockets" 这种只在那个平台出现的错。
/// <para>
/// Windows 的命名管道与 Linux 的 108 字节上限都容得下,所以这条越界在 CI 加上 macOS 之前
/// 一直没人发现。这组用例把长度钉住,免得日后有人想给名字加点可读的前缀就又超了。
/// </para>
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class PluginPipeNameTests
{
    /// <summary>macOS 的 <c>sun_path</c> 上限(含结尾的 NUL,故实际可用 103)。</summary>
    private const int MacOsUnixSocketPathLimit = 104;

    /// <summary>.NET 在 Unix 上给管道套接字加的前缀。</summary>
    private const string CoreFxPipePrefix = "CoreFxPipe_";

    /// <summary>一个真实的 macOS 每用户临时目录(取自 CI 上的实际报错信息)。</summary>
    private const string MacOsTempDirectory = "/var/folders/d8/hvxvltxn0fl4rmnd52sncbth0000gn/T/";

    [TestMethod]
    public void ThePipeNameFitsInsideMacOsUnixSocketPathLimit()
    {
        string name = PluginProcessClient.CreatePipeName();
        int socketPathLength = MacOsTempDirectory.Length + CoreFxPipePrefix.Length + name.Length;

        Assert.IsLessThan(
            MacOsUnixSocketPathLimit,
            socketPathLength,
            $"管道套接字路径长 {socketPathLength},超过 macOS 的 {MacOsUnixSocketPathLimit} 上限 —— "
            + $"隔离插件在 macOS 上会一个都起不来。名字是 '{name}'({name.Length} 字符)。");
    }

    [TestMethod]
    public void ThePipeNameStaysWithinItsDeclaredBudget()
    {
        string name = PluginProcessClient.CreatePipeName();

        Assert.IsLessThanOrEqualTo(PluginProcessClient.MaxPipeNameLength, name.Length);
        Assert.IsNotEmpty(name);
    }

    /// <summary>每次都得是新的:同时激活两个插件不能撞到同一条管道上。</summary>
    [TestMethod]
    public void EveryPipeNameIsUnique()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 1_000; i++)
        {
            Assert.IsTrue(names.Add(PluginProcessClient.CreatePipeName()), "管道名重复了。");
        }
    }
}
