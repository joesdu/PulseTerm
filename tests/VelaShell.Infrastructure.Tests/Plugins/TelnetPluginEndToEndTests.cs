using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// Telnet 插件的整链路验证:清单发现(不装载程序集)→ 惰性激活 → 注册成**终端**协议 →
/// 宿主适配成 <see cref="IShellStreamWrapper" /> → 在真实环回 TCP 上收发字节。
/// <para>
/// 单测宿主与真实应用之间最容易断的就是这条链:插件自己的单测全绿、协商状态机也全绿,
/// 但清单少一个字段、协议 id 大小写不一致、或注册走了文件协议那条重载,
/// 用户看到的就是"页签在,点了没反应"。这个测试专门盯这一段。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class TelnetPluginEndToEndTests
{
    private const string ProtocolId = "velashell.telnet";

    private string _root = null!;
    private string _dataRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "velashell-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "plugins");
        _dataRoot = Path.Combine(baseDir, "plugin-data");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch
        {
            // 尽力清理。
        }
    }

    /// <summary>
    /// 把**构建产物里的**真 Telnet 插件目录铺到临时插件根下。
    /// 刻意不在测试里手写一份 plugin.json:那样就测不到真清单里的
    /// contributes/activationEvents 写没写对 —— 而那正是最容易错的地方。
    /// <para>
    /// <b>从插件自己的 bin 取,而不是本测试项目的 bin</b>(见
    /// <see cref="PluginOutputLocator" />):本项目引了三个插件项目,它们都把
    /// <c>plugin.json</c> 复制到测试 bin 的**根**下,谁赢由 MSBuild 的复制顺序决定。
    /// 这条用例曾经绿过很久,只是**恰好**赢了那枚硬币 —— 第三个插件一进来就翻了。
    /// </para>
    /// </summary>
    private void StageTelnetPlugin() =>
        PluginOutputLocator.StageInto("VelaShell.Plugin.Telnet", Path.Combine(_root, "velashell-telnet"));

    private (PluginManager Manager, PluginProtocolRegistry Registry) CreateManager()
    {
        var registry = new PluginProtocolRegistry();
        var manager = new PluginManager(new()
        {
            PluginRoots = [_root],
            DataRootDirectory = _dataRoot,
            HostVersion = "1.0.0",
            ActivationTimeout = TimeSpan.FromSeconds(30),
            DeactivationTimeout = TimeSpan.FromSeconds(10),
            CommandsFactory = (_, _) => new RecordingCommands(),
            ProtocolRegistry = registry
        });
        return (manager, registry);
    }

    [TestMethod]
    public async Task Manifest_DeclaresTheTabWithoutLoadingTheAssembly_AndResolvesToATerminalProtocol()
    {
        StageTelnetPlugin();
        (PluginManager manager, PluginProtocolRegistry registry) = CreateManager();
        await manager.StartAsync();

        // 发现期:页签画得出来,插件仍未装载(onProtocol 惰性激活)。
        PluginProtocolTab tab = registry.Tabs.Single(entry => entry.Id == ProtocolId);
        Assert.AreEqual("Telnet", tab.DisplayName);
        Assert.AreEqual(23, tab.DefaultPort);
        Assert.IsFalse(tab.IsReady, "没人点它之前不该装载程序集。");
        Assert.AreEqual(PluginState.Discovered, manager.Plugins.Single().State);

        // 用户点到页签(或打开一条会话)→ 惰性激活 → 注册成终端协议。
        PluginProtocolRegistration? registration = await registry.ResolveAsync(ProtocolId);
        Assert.IsNotNull(registration);
        Assert.IsNotNull(registration.Terminal, "Telnet 必须注册为终端协议,否则会被当成文件协议开出空的双栏浏览器。");
        Assert.IsNull(registration.FileSystem);
        Assert.IsTrue(registration.Descriptor.Features.HasFlag(ProtocolFeatures.NoCredentials),
            "Telnet 的登录在带内进行,宿主应据此收起用户名/口令两栏。");
        Assert.Contains(field => field.Key == "enterMode", registration.Descriptor.Fields);

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_OpenedThroughTheHostAdapter_ExchangesBytesOverARealSocket()
    {
        StageTelnetPlugin();
        (PluginManager manager, PluginProtocolRegistry registry) = CreateManager();
        await manager.StartAsync();
        PluginProtocolRegistration registration = (await registry.ResolveAsync(ProtocolId))!;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync();

        var profile = new SessionProfile
        {
            Name = "switch-01",
            Host = "127.0.0.1",
            Port = port,
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = ProtocolId
        };
        using IShellStreamWrapper stream = await PluginProtocolTerminalConnector.OpenAsync(
            registration, profile, new("xterm-256color", 100, 40));

        using TcpClient server = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        NetworkStream serverStream = server.GetStream();

        // 服务端先收到客户端主动发起的协商(IAC WILL TERMINAL-TYPE 打头)。
        byte[] hello = new byte[64];
        int helloLength = await serverStream.ReadAsync(hello).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsGreaterThanOrEqualTo(3, helloLength);
        Assert.AreSequenceEqual(new byte[] { 255, 251, 24 }, hello[..3], "连接建立后应立即发出 WILL TERMINAL-TYPE。");

        // 服务端发数据 → 宿主的读循环拿到的是去掉协议字节后的净文本。
        await serverStream.WriteAsync(Encoding.ASCII.GetBytes("login: "));
        await serverStream.FlushAsync();
        byte[] buffer = new byte[64];
        int read = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("login: ", Encoding.ASCII.GetString(buffer, 0, read));

        // 尺寸变化 → NAWS(此处对端未 DO NAWS,因此不该发出任何东西,但也不能抛)。
        stream.Resize(132, 43);

        listener.Stop();
        await manager.DisposeAsync();
    }
}
