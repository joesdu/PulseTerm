using System.Net;
using System.Net.Sockets;
using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Plugins;
using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;
using VelaShell.TestPlugin.Terminal;

namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 终端协议插件的整链路验证:清单发现(不装载程序集)→ 惰性激活 → 注册成**终端**协议 →
/// 宿主适配成 <see cref="IShellStreamWrapper" /> → 在真实环回 TCP 上收发字节。
/// <para>
/// 单测宿主与真实应用之间最容易断的就是这条链:插件自己的单测全绿、协商状态机也全绿,
/// 但清单少一个字段、协议 id 大小写不一致、或注册走了文件协议那条重载,
/// 用户看到的就是"页签在,点了没反应"。这个测试专门盯这一段。
/// </para>
/// <para>
/// 用的是 <c>tests/fixtures/VelaShell.TestPlugin.Terminal</c> 夹具(裸 TCP 直通,
/// 连上先发一个固定问候序列)。拆库之前这里驱动的是仓库内的真 Telnet 插件 ——
/// 插件已随工具链搬到 joesdu/velashell-plugin-toolchain,本仓库拿不到它;
/// 而这条链本来就归宿主管,具体协议的状态机(Telnet 的选项协商之类)由插件自己的
/// 单测负责,那些测试跟着插件一起在工具链仓库里。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class PluginTerminalProtocolEndToEndTests
{
    private const string ProtocolId = TestTerminalPlugin.Id;

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
    /// 把**构建产物里的**夹具插件目录铺到临时插件根下。
    /// 刻意不在测试里手写一份 plugin.json:那样就测不到真清单里的
    /// contributes/activationEvents 写没写对 —— 而那正是最容易错的地方。
    /// </summary>
    private void StageTerminalPlugin()
    {
        string source = Path.GetDirectoryName(typeof(TestTerminalPlugin).Assembly.Location)!;
        string manifest = Path.Combine(source, "plugin.json");
        Assert.IsTrue(File.Exists(manifest), $"构建产物里应有 plugin.json:{manifest}");
        string target = Path.Combine(_root, "velashell-test-terminal");
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "VelaShell.TestPlugin.Terminal.*"))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        File.Copy(manifest, Path.Combine(target, "plugin.json"), overwrite: true);
    }

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
        StageTerminalPlugin();
        (PluginManager manager, PluginProtocolRegistry registry) = CreateManager();
        await manager.StartAsync();

        // 发现期:页签画得出来,插件仍未装载(onProtocol 惰性激活)。
        PluginProtocolTab tab = registry.Tabs.Single(entry => entry.Id == ProtocolId);
        Assert.AreEqual("Test Terminal", tab.DisplayName);
        Assert.AreEqual(2323, tab.DefaultPort);
        Assert.IsFalse(tab.IsReady, "没人点它之前不该装载程序集。");
        Assert.AreEqual(PluginState.Discovered, manager.Plugins.Single().State);

        // 用户点到页签(或打开一条会话)→ 惰性激活 → 注册成终端协议。
        PluginProtocolRegistration? registration = await registry.ResolveAsync(ProtocolId);
        Assert.IsNotNull(registration);
        Assert.IsNotNull(registration.Terminal, "必须注册为终端协议,否则会被当成文件协议开出空的双栏浏览器。");
        Assert.IsNull(registration.FileSystem);
        Assert.IsTrue(registration.Descriptor.Features.HasFlag(ProtocolFeatures.NoCredentials),
            "声明了 NoCredentials 的协议,宿主应据此收起用户名/口令两栏。");
        Assert.Contains(field => field.Key == TestTerminalPlugin.GreetingModeField, registration.Descriptor.Fields,
            "描述符里声明的自定义字段要能原样到达宿主(连接表单全靠它渲染)。");

        await manager.DisposeAsync();
    }

    [TestMethod]
    public async Task Session_OpenedThroughTheHostAdapter_ExchangesBytesOverARealSocket()
    {
        StageTerminalPlugin();
        (PluginManager manager, PluginProtocolRegistry registry) = CreateManager();
        await manager.StartAsync();
        PluginProtocolRegistration registration = (await registry.ResolveAsync(ProtocolId))!;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync();

        var profile = new SessionProfile
        {
            Name = "fixture-01",
            Host = "127.0.0.1",
            Port = port,
            ConnectionType = ConnectionType.Plugin,
            PluginProtocolId = ProtocolId
        };
        using IShellStreamWrapper stream = await PluginProtocolTerminalConnector.OpenAsync(
            registration, profile, new("xterm-256color", 100, 40));

        using TcpClient server = await accepted.WaitAsync(TimeSpan.FromSeconds(5));
        NetworkStream serverStream = server.GetStream();

        // 服务端先收到插件在连接建立后主动发出的问候 —— 证明"宿主适配器 → 插件会话"
        // 这一段确实接通了,而不是宿主自己开了个裸 socket。
        byte[] hello = new byte[64];
        int helloLength = await serverStream.ReadAsync(hello).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsGreaterThanOrEqualTo(3, helloLength);
        Assert.AreSequenceEqual(TestTerminalPlugin.Greeting.ToArray(), hello[..3],
            "连接建立后应立即发出夹具的问候序列。");

        // 服务端发数据 → 宿主的读循环拿得到。
        await serverStream.WriteAsync(Encoding.ASCII.GetBytes("login: "));
        await serverStream.FlushAsync();
        byte[] buffer = new byte[64];
        int read = await stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("login: ", Encoding.ASCII.GetString(buffer, 0, read));

        // 尺寸变化转达给插件。本协议没有尺寸上报机制,因此不该往线上发任何东西,但也不能抛。
        stream.Resize(132, 43);

        listener.Stop();
        await manager.DisposeAsync();
    }
}
