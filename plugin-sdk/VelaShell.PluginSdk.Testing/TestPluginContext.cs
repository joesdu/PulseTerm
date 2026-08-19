using VelaShell.PluginSdk.Clipboard;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Events;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Secrets;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Storage;
using VelaShell.PluginSdk.Terminal;
using VelaShell.PluginSdk.TimeSeries;
using VelaShell.PluginSdk.Ui;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.PluginSdk.Testing;

/// <summary><see cref="IHostInfo" /> 的可设置实现。</summary>
public sealed class TestHostInfo : IHostInfo
{
    /// <inheritdoc />
    public string AppVersion { get; set; } = "999.0.0-test";

    /// <inheritdoc />
    public int ApiLevel { get; set; } = VelaPluginApi.Level;

    /// <inheritdoc />
    public string Locale { get; set; } = "en";

    /// <inheritdoc />
    public string Theme { get; set; } = "dark";
}

/// <summary>
/// <see cref="IPluginContext" /> 的测试实现:各能力默认为本包的内存替身,
/// 可按需替换。用法:
/// <code>
/// var ctx = new TestPluginContext();
/// ctx.FakeSessions.AddConnected();
/// await new MyPlugin().ActivateAsync(ctx, CancellationToken.None);
/// </code>
/// </summary>
public sealed class TestPluginContext : IPluginContext, IDisposable
{
    private readonly CancellationTokenSource _shutdownSource = new();
    private string? _dataDirectory;

    /// <summary>默认日志替身(<see cref="Log" /> 未被替换时即此实例)。</summary>
    public CollectingLogger CollectingLog { get; } = new();

    /// <summary>默认存储替身。</summary>
    public InMemoryStorage MemoryStorage { get; } = new();

    /// <summary>默认时序替身。</summary>
    public InMemoryTimeSeries MemoryTimeSeries { get; } = new();

    /// <summary>默认会话替身。</summary>
    public FakeSessions FakeSessions { get; } = new();

    /// <summary>默认远程文件替身。</summary>
    public FakeRemoteFs FakeRemoteFs { get; } = new();

    /// <summary>默认远程执行替身。</summary>
    public FakeRemoteExec FakeRemoteExec { get; } = new();

    /// <summary>默认命令替身。</summary>
    public RecordingCommands RecordingCommands { get; } = new();

    /// <summary>默认事件替身。</summary>
    public TestHostEvents HostEvents { get; } = new();

    /// <summary>默认界面替身。</summary>
    public FakeUi FakeUi { get; } = new();

    /// <summary>默认机密替身。</summary>
    public FakeSecrets FakeSecrets { get; } = new();

    /// <summary>默认剪贴板替身。</summary>
    public FakeClipboard FakeClipboard { get; } = new();

    /// <summary>默认终端替身。</summary>
    public FakeTerminal FakeTerminal { get; } = new();

    /// <summary>默认协议替身(按 <see cref="PluginId" /> 做 id 前缀校验)。</summary>
    public RecordingProtocols RecordingProtocols { get; } = new();

    /// <summary>默认工作台替身(按 <see cref="PluginId" /> 做 id 前缀校验)。</summary>
    public RecordingWorkspaces RecordingWorkspaces { get; } = new();

    /// <summary>默认宿主信息替身。</summary>
    public TestHostInfo HostInfo { get; } = new();

    /// <inheritdoc />
    /// <remarks>协议替身的前缀校验跟着它走,免得测试改了 id 却在注册协议时被自己的替身拒掉。</remarks>
    public string PluginId
    {
        get;
        init
        {
            field = value;
            RecordingProtocols.PluginId = value;
            RecordingWorkspaces.PluginId = value;
        }
    } = "test.plugin";

    /// <inheritdoc />
    public string PluginVersion { get; init; } = "0.0.0";

    /// <summary>数据目录:默认惰性创建一个独立临时目录,<see cref="Dispose" /> 时删除。</summary>
    public string DataDirectory
    {
        get
        {
            if (_dataDirectory is null)
            {
                _dataDirectory = Path.Combine(Path.GetTempPath(), "velashell-plugin-test", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_dataDirectory);
            }
            return _dataDirectory;
        }
        init => _dataDirectory = value;
    }

    /// <inheritdoc />
    public IHostInfo Host => HostInfo;

    /// <inheritdoc cref="IPluginContext.Log" />
    public IPluginLogger Log { get; init; }

    /// <inheritdoc cref="IPluginContext.Storage" />
    public IPluginStorage Storage { get; init; }

    /// <inheritdoc cref="IPluginContext.TimeSeries" />
    public ITimeSeriesApi TimeSeries { get; init; }

    /// <inheritdoc cref="IPluginContext.Sessions" />
    public ISessionsApi Sessions { get; init; }

    /// <inheritdoc cref="IPluginContext.RemoteFs" />
    public IRemoteFsApi RemoteFs { get; init; }

    /// <inheritdoc cref="IPluginContext.RemoteExec" />
    public IRemoteExecApi RemoteExec { get; init; }

    /// <inheritdoc cref="IPluginContext.Commands" />
    public ICommandsApi Commands { get; init; }

    /// <inheritdoc cref="IPluginContext.Events" />
    public IHostEvents Events { get; init; }

    /// <inheritdoc cref="IPluginContext.Ui" />
    public IUiApi Ui { get; init; }

    /// <inheritdoc cref="IPluginContext.Secrets" />
    public ISecretsApi Secrets { get; init; }

    /// <inheritdoc cref="IPluginContext.Clipboard" />
    public IClipboardApi Clipboard { get; init; }

    /// <inheritdoc cref="IPluginContext.Terminal" />
    public ITerminalApi Terminal { get; init; }

    /// <inheritdoc cref="IPluginContext.Protocols" />
    public IProtocolsApi Protocols { get; init; }

    /// <inheritdoc cref="IPluginContext.Workspaces" />
    public IWorkspacesApi Workspaces { get; init; }

    /// <inheritdoc />
    public CancellationToken Shutdown => _shutdownSource.Token;

    /// <summary>构造:各能力默认指向本包替身。</summary>
    public TestPluginContext()
    {
        Log = CollectingLog;
        Storage = MemoryStorage;
        TimeSeries = MemoryTimeSeries;
        Sessions = FakeSessions;
        RemoteFs = FakeRemoteFs;
        RemoteExec = FakeRemoteExec;
        Commands = RecordingCommands;
        Events = HostEvents;
        Ui = FakeUi;
        Secrets = FakeSecrets;
        Clipboard = FakeClipboard;
        Terminal = FakeTerminal;
        Protocols = RecordingProtocols;
        Workspaces = RecordingWorkspaces;
    }

    /// <summary>模拟宿主停机:触发 <see cref="Shutdown" /> 令牌。</summary>
    public void RequestShutdown() => _shutdownSource.Cancel();

    /// <summary>清理临时数据目录并释放停机令牌源。</summary>
    public void Dispose()
    {
        _shutdownSource.Dispose();
        if (_dataDirectory is not null)
        {
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch
            {
                // 临时目录清理尽力而为。
            }
        }
    }
}
